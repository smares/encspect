using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Ogg;

// Random access to a ByteReader through one cached window that holds the largest page, so pages are
// read and checked with one stream read per window and large files are never loaded whole.
internal sealed class PageWindow(ByteReader reader)
{
    private const int WindowSize = 1 << 18;

    private byte[] _window = [];
    private long _start = -1;

    public ByteReader Reader => reader;

    // The bytes [offset, offset + count), which must lie inside the reader; count is at most a page.
    public ReadOnlySpan<byte> Get(long offset, int count)
    {
        if (offset < _start || offset + count > _start + _window.Length)
        {
            _start = offset;
            _window = reader.ReadBytesAt(offset, (int)Math.Min(WindowSize, reader.End - offset));
        }

        return _window.AsSpan((int)(offset - _start), count);
    }

    // The first "OggS" in [from, limit), or -1.
    public long Find(long from, long limit)
    {
        long position = from;
        while (position <= limit - 4)
        {
            int count = (int)Math.Min(WindowSize - 3, limit - position);
            ReadOnlySpan<byte> span = Get(position, count);
            int index = span.IndexOf(Pages.Capture);
            if (index >= 0)
            {
                return position + index;
            }

            position += count - 3;
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
    public static bool StartsChain(PageWindow window, long offset, long end)
    {
        if (Probe(window, offset, end, out PageHeader header) != PageProbe.Fits || header.Length > end - offset
            || ComputeCrc(window, header) != header.Crc)
        {
            return false;
        }

        long next = offset + header.Length;
        return next == end || Probe(window, next, end, out _) == PageProbe.Fits;
    }

    // The first offset in [from, limit) where a chain of pages starts, or -1.
    public static long FindChain(PageWindow window, long from, long limit, long end, CancellationToken cancellationToken)
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

            if (StartsChain(window, found, end))
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
