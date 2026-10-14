using Encspect.Core;

namespace Encspect.Formats.Dts;

// Finds the frames of a stream by their sync words and lengths, as the MPEG, ADTS and AC-3 parsers do: a sync word starts a
// stream only when a chain of frames follows it, at the lengths their headers give, or frames that end exactly at the end of the data.
internal static class Framing
{
    public const int ChainLength = 3;

    // The first bytes an item needs: a core header in a 14-bit form takes 14 bytes.
    private const int HeaderBytes = 16;

    // The item that starts at position, or null. An extension substream counts only with the right CRC16 where strict, which
    // chains start with, since its sync word has nothing else to check it against; budget limits the CRC work that a
    // search spends on candidates (see CrcBudget).
    public static Item? ItemAt(ISource source, long position, long end, bool strict, CrcBudget? budget = null)
    {
        if (position >= end)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[HeaderBytes];
        int read = source.Read(position, bytes[..(int)Math.Min(HeaderBytes, end - position)]);
        if (read < ExssHeader.Bytes)
        {
            return null;
        }

        ReadOnlySpan<byte> file = bytes[..read];
        if (CoreHeader.Read(file) is { } core)
        {
            int length = core.Header.FileLength(core.Form);
            return new Item(ItemKind.Core, core.Form, length, core.Header, default);
        }

        // An extension substream's sync word is 0x64582025; a little endian stream swaps each pair of bytes, so it reads 0x58642520.
        StreamForm? form = file[0] == 0x64 && file[1] == 0x58 ? StreamForm.BigEndian16 : file[0] == 0x58 && file[1] == 0x64 ? StreamForm.LittleEndian16 : null;
        if (form is not { } found)
        {
            return null;
        }

        Span<byte> packed = stackalloc byte[HeaderBytes];
        Forms.Unpack(file[..ExssHeader.Bytes], found, packed);
        if (ExssHeader.Read(packed[..ExssHeader.Bytes]) is not { } exss)
        {
            return null;
        }

        if (strict && !HasValidCrc(source, position, end, found, exss, budget))
        {
            return null;
        }

        return new Item(ItemKind.Exss, found, exss.TotalSize, default, exss);
    }

    // 7.5.2: the CRC16 over the header from byte 5 to its end, the CRC included, comes to zero.
    public static bool HasValidCrc(ISource source, long position, long end, StreamForm form, ExssHeader header, CrcBudget? budget = null)
    {
        int size = header.HeaderSize;
        if (size > ExssHeader.MaximumHeaderSize || position + size > end || (budget is not null && !budget.TryCharge(size)))
        {
            return false;
        }

        Span<byte> file = stackalloc byte[ExssHeader.MaximumHeaderSize + 2];
        file = file[..(size + (size & 1))];
        file.Clear();
        if (source.Read(position, file[..size]) < size)
        {
            return false;
        }

        Span<byte> packed = stackalloc byte[ExssHeader.MaximumHeaderSize + 2];
        packed = packed[..file.Length];
        Forms.Unpack(file, form, packed);
        return Crc16.Compute(packed.Slice(5, size - 5)) == 0;
    }

    // A unit starting with first: its core frame, if it is one, and the extension substreams that follow, each with a higher index
    // than the one before, at the end of the core frame or of the previous substream, or at the next 32-bit boundary after it (7.5.1
    // adds one to three null bytes to align them). A substream right after the end, off the boundary, is kept in the unit but flagged
    // (ExssRef.IsMisaligned): FFmpeg looks for it at the boundary only (dcadec.c, FFALIGN(frame_size, 4)) and so does not read it.
    // A substream after a core frame needs the right CRC16, or to be whole and followed by a unit that passes the same test or the end of the data, so a
    // damaged header is still reported and a sync word in junk is not taken for one. Null bytes that align the next unit's sync word
    // belong to the unit.
    public static UnitInfo? UnitAt(ISource source, long position, long end, bool strict, CrcBudget? budget = null)
    {
        if (ItemAt(source, position, end, strict, budget) is not { } first)
        {
            return null;
        }

        var unit = new UnitInfo(first, first.Length);
        if (first.Form.Is14Bit())
        {
            return Padded(source, position, end, unit);
        }

        int lastIndex = first.IsCore ? -1 : first.Exss.Index;
        while (unit.Count < UnitLayout.MaxExtensionSubstreams)
        {
            long next = position + unit.Length;
            long aligned = position + ((unit.Length + 3L) & ~3L);
            Item? exss = null;
            long at = 0;
            foreach (long candidate in aligned == next ? [next] : new[] { next, aligned })
            {
                if (candidate < end && ItemAt(source, candidate, end, false) is { IsCore: false } item && item.Exss.Index > lastIndex && item.Form == first.Form
                    && IsSubstream(source, candidate, end, item, budget))
                {
                    exss = item;
                    at = candidate;
                    break;
                }
            }

            if (exss is not { } found)
            {
                break;
            }

            unit = unit.With(new ExssRef((int)(at - position), found.Length, found.Exss.Index, found.Length, at != aligned), (int)(at + found.Length - position));
            lastIndex = found.Exss.Index;
        }

        return Padded(source, position, end, unit);
    }

    private static bool IsSubstream(ISource source, long position, long end, Item item, CrcBudget? budget)
    {
        if (HasValidCrc(source, position, end, item.Form, item.Exss, budget))
        {
            return true;
        }

        long after = position + item.Length;
        return after <= end && (after == end || ItemAt(source, after, end, true, budget) is not null);
    }

    // Adds the one to three null bytes that bring the next unit's sync word to a 32-bit boundary (7.5.1), where one follows or the data ends.
    private static UnitInfo Padded(ISource source, long position, long end, UnitInfo unit)
    {
        int pad = (4 - (unit.Length & 3)) & 3;
        long at = position + unit.Length;
        if (pad == 0 || at + pad > end)
        {
            return unit;
        }

        Span<byte> bytes = stackalloc byte[3];
        if (source.Read(at, bytes[..pad]) < pad || bytes[..pad].IndexOfAnyExcept((byte)0) >= 0 || (at + pad < end && ItemAt(source, at + pad, end, false) is null))
        {
            return unit;
        }

        return unit.WithLength(unit.Length + pad);
    }

    // A chain of units from position: each one at the end of the one before, in the same stream.
    public static bool Chains(ISource source, long position, UnitInfo first, long end, CrcBudget? budget = null)
    {
        long next = position + first.Length;
        UnitInfo previous = first;
        for (int count = 1; count < ChainLength; count++)
        {
            if (next == end)
            {
                return true;
            }

            if (next > end)
            {
                return false;
            }

            if (UnitAt(source, next, end, true, budget) is not { } following || !following.First.Continues(previous.First))
            {
                return false;
            }

            next += following.Length;
            previous = following;
            if (next > end)
            {
                return count >= 2;
            }
        }

        return true;
    }
}

// The CRC work a search for a chain may spend on extension substream candidates: a header of up to 4 KiB is read and checked for each
// sync word, and a file of sync words 16 bytes apart would otherwise cost 256 times its size. A search earns some bytes of work for
// every byte it scans, after a start allowance that covers the headers of a real stream.
internal sealed class CrcBudget
{
    private const long StartAllowance = 1 << 20;
    private const int PerScannedByte = 4;

    private long _allowance = StartAllowance;
    private long _scanned;

    public void Scanned(long position)
    {
        if (_scanned == 0)
        {
            _scanned = position;
        }

        _allowance += Math.Max(0, position - _scanned) * PerScannedByte;
        _scanned = position;
    }

    public bool TryCharge(int bytes)
    {
        if (_allowance < bytes)
        {
            return false;
        }

        _allowance -= bytes;
        return true;
    }
}

// A unit's items as the chain sees them: its first item, its length in bytes and the extension substreams after the core frame.
internal readonly struct UnitInfo
{
    private readonly ExssRef _a;
    private readonly ExssRef _b;
    private readonly ExssRef _c;
    private readonly ExssRef _d;

    public UnitInfo(Item first, int length)
    {
        First = first;
        Length = length;
        Count = 0;
        _a = _b = _c = _d = default;
    }

    private UnitInfo(Item first, int length, int count, ExssRef a, ExssRef b, ExssRef c, ExssRef d)
    {
        First = first;
        Length = length;
        Count = count;
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    public Item First { get; }

    // The bytes from the unit's start to the end of its last item.
    public int Length { get; }

    // The extension substreams after the core frame (or after the first substream, where it is the first item).
    public int Count { get; }

    public ExssRef this[int index] => index switch { 0 => _a, 1 => _b, 2 => _c, _ => _d };

    public UnitInfo WithLength(int length) => new(First, length, Count, _a, _b, _c, _d);

    public UnitInfo With(ExssRef substream, int length) => Count switch
    {
        0 => new UnitInfo(First, length, 1, substream, _b, _c, _d),
        1 => new UnitInfo(First, length, 2, _a, substream, _c, _d),
        2 => new UnitInfo(First, length, 3, _a, _b, substream, _d),
        _ => new UnitInfo(First, length, 4, _a, _b, _c, substream),
    };
}

// A buffer that a container handed over, read as a source: offsets are file offsets, Start the file offset of its first byte.
internal sealed class BufferSource(byte[] data, long start) : ISource
{
    public long Start => start;

    public long End => start + data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        long from = offset - start;
        if (from < 0 || from >= data.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, data.Length - from);
        data.AsSpan((int)from, count).CopyTo(buffer);
        return count;
    }
}
