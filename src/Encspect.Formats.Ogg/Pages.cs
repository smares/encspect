using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Ogg;

// Random access to a ByteReader through two cached windows that each hold the largest page, so pages are
// read and checked with one stream read per window and large files are never loaded whole. A search
// that probes the page a candidate claims follows it works in one place of the data and probes in another,
// so with one window every probe would read the window again; the window used least recently is the one
// replaced, so a scan that moves forward reads each stretch once.
internal sealed class PageWindow(ByteReader reader)
{
    private const int WindowSize = 1 << 18;

    private readonly long[] _starts = [-1, -1];
    private readonly byte[][] _windows = [[], []];
    private int _recent;

    public ByteReader Reader => reader;

    // The bytes [offset, offset + count), which must lie inside the reader; count is at most a page.
    public ReadOnlySpan<byte> Get(long offset, int count)
    {
        for (int i = 0; i < 2; i++)
        {
            if (offset >= _starts[i] && offset + count <= _starts[i] + _windows[i].Length)
            {
                _recent = i;
                return _windows[i].AsSpan((int)(offset - _starts[i]), count);
            }
        }

        int slot = 1 - _recent;
        _starts[slot] = offset;
        _windows[slot] = reader.ReadBytesAt(offset, (int)Math.Min(WindowSize, reader.End - offset));
        _recent = slot;
        return _windows[slot].AsSpan(0, count);
    }

    // The first "OggS" in [from, limit), or -1. Searches what a window holds before reading another.
    public long Find(long from, long limit)
    {
        long position = from;
        while (position <= limit - 4)
        {
            long available = limit - position;
            ReadOnlySpan<byte> span = default;
            for (int i = 0; i < 2 && span.IsEmpty; i++)
            {
                if (position >= _starts[i] && position + 4 <= _starts[i] + _windows[i].Length)
                {
                    _recent = i;
                    span = _windows[i].AsSpan((int)(position - _starts[i]), (int)Math.Min(available, _starts[i] + _windows[i].Length - position));
                }
            }

            if (span.IsEmpty)
            {
                span = Get(position, (int)Math.Min(WindowSize, available));
            }

            int index = span.IndexOf(Pages.Capture);
            if (index >= 0)
            {
                return position + index;
            }

            position += span.Length - 3;
        }

        return -1;
    }
}

// A page header (RFC 3533, section 6, "The Ogg page format"): "OggS", the stream structure version
// (0), the header type flags, the granule position, the bitstream serial number, the page sequence
// number, the CRC, the number of segments and the segment table of lacing values, all integers
// little-endian.
internal readonly record struct PageHeader(long Offset, byte Version, byte Flags, long Granule, uint Serial, uint Sequence, uint Crc, int Segments, int BodyLength)
{
    public const int FixedLength = 27;

    // 27 header bytes, 255 lacing values of 255 bytes each: RFC 3533's maximum of 65307 bytes.
    public const int MaxLength = FixedLength + 255 + (255 * 255);

    public int HeaderLength => FixedLength + Segments;

    public int Length => HeaderLength + BodyLength;

    public long BodyOffset => Offset + HeaderLength;

    public bool IsContinued => (Flags & Pages.Continued) != 0;

    public bool IsBeginning => (Flags & Pages.Beginning) != 0;

    public bool IsEnd => (Flags & Pages.End) != 0;
}

internal enum PageProbe
{
    // No capture pattern, or a version other than 0.
    NotAPage,

    // The capture pattern and version 0, but the data ends inside the 27-byte header or the segment
    // table.
    HeaderCut,

    // The header and segment table fit; the body may still run past the end.
    Fits,
}

// How many bytes of CRC a search for the next page may compute over all its candidates in one region of data. A page that is
// found costs one CRC here and one in the scan, so a budget of a few times the data's size leaves any real file's damage
// within it, where junk made of candidates that each look like a page of 64 KiB would cost up to 64,000 operations per byte.
internal sealed class ResyncBudget(long bytes)
{
    private long _left = bytes;

    public bool IsExhausted { get; private set; }

    public static ResyncBudget For(long dataLength) => new((4 * dataLength) + (1 << 20));

    public bool TrySpend(int count)
    {
        if (count > _left)
        {
            IsExhausted = true;
            return false;
        }

        _left -= count;
        return true;
    }
}

internal static class Pages
{
    public const byte Continued = 0x01;
    public const byte Beginning = 0x02;
    public const byte End = 0x04;

    public static ReadOnlySpan<byte> Capture => "OggS"u8;

    public static PageProbe Probe(PageWindow window, long offset, long end, out PageHeader header)
    {
        header = default;
        if (end - offset < 5)
        {
            return end - offset >= 4 && window.Get(offset, 4).SequenceEqual(Capture) ? PageProbe.HeaderCut : PageProbe.NotAPage;
        }

        ReadOnlySpan<byte> start = window.Get(offset, 5);
        if (!start[..4].SequenceEqual(Capture) || start[4] != 0)
        {
            return PageProbe.NotAPage;
        }

        if (end - offset < PageHeader.FixedLength)
        {
            return PageProbe.HeaderCut;
        }

        ReadOnlySpan<byte> fixedPart = window.Get(offset, PageHeader.FixedLength);
        int segments = fixedPart[26];
        if (end - offset < PageHeader.FixedLength + segments)
        {
            header = Parse(offset, fixedPart, 0);
            return PageProbe.HeaderCut;
        }

        int body = 0;
        foreach (byte lacing in window.Get(offset + PageHeader.FixedLength, segments))
        {
            body += lacing;
        }

        header = Parse(offset, window.Get(offset, PageHeader.FixedLength), body);
        return PageProbe.Fits;
    }

    // The page's CRC computed over its bytes, which must all lie in the data.
    public static uint ComputeCrc(PageWindow window, in PageHeader header) => OggCrc.OfPage(window.Get(header.Offset, header.Length));

    // Whether a page that passes its CRC starts at offset and is followed by the start of another
    // page or by the end of the data: a chain of consistent pages, as MPEG sync needs a chain of
    // frames, so "OggS" inside other data is not taken for a page.
    // The follow-on page is checked before the CRC, which costs up to 64 KiB, and a search spends
    // a ResyncBudget on its CRCs, so junk full of capture patterns costs time in proportion to its size.
    public static bool StartsChain(PageWindow window, long offset, long end, ResyncBudget? budget = null)
    {
        if (Probe(window, offset, end, out PageHeader header) != PageProbe.Fits || header.Length > end - offset)
        {
            return false;
        }

        long next = offset + header.Length;
        if (next != end && Probe(window, next, end, out _) != PageProbe.Fits)
        {
            return false;
        }

        if (budget is not null && !budget.TrySpend(header.Length))
        {
            return false;
        }

        return ComputeCrc(window, header) == header.Crc;
    }

    // The first offset in [from, limit) where a chain of pages starts, or -1.
    public static long FindChain(PageWindow window, long from, long limit, long end, ResyncBudget budget, CancellationToken cancellationToken)
    {
        long position = from;
        int tries = 0;
        while (true)
        {
            long found = window.Find(position, Math.Min(limit + 3, end));
            if (found < 0 || found >= limit)
            {
                return -1;
            }

            if (StartsChain(window, found, end, budget))
            {
                return found;
            }

            if (++tries % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            position = found + 1;
        }
    }

    private static PageHeader Parse(long offset, ReadOnlySpan<byte> bytes, int body) => new(
        offset,
        bytes[4],
        bytes[5],
        BinaryPrimitives.ReadInt64LittleEndian(bytes[6..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[22..]),
        bytes[26],
        body);
}
