using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Where the packets of a stream lie in the file: the first block alignment's bytes of each media object, which may be spread over several
// ASF payloads. Written while the packets are read; read by node builders afterwards.
internal sealed class PacketTable
{
    private readonly ChunkedList<ByteRange> _ranges = new();
    private readonly ChunkedList<(int First, int Count)> _packets = new();

    public int Count => _packets.Count;

    public void Add(IReadOnlyList<ByteRange> ranges)
    {
        _packets.Add((_ranges.Count, ranges.Count));
        foreach (ByteRange range in ranges)
        {
            _ranges.Add(range);
        }
    }

    // The file offset of byte index of a packet.
    public long OffsetOf(int packet, int index)
    {
        (int first, int count) = _packets[packet];
        long remaining = index;
        for (int i = 0; i < count; i++)
        {
            ByteRange range = _ranges[first + i];
            if (remaining < range.Length || i == count - 1)
            {
                return range.Offset + remaining;
            }

            remaining -= range.Length;
        }

        return 0;
    }

    // The file ranges that hold bytes [start, end) of a packet.
    public List<ByteRange> RangesOf(int packet, int start, int end)
    {
        var result = new List<ByteRange>(1);
        (int first, int count) = _packets[packet];
        long position = 0;
        for (int i = 0; i < count && position < end; i++)
        {
            ByteRange range = _ranges[first + i];
            long from = Math.Max(start, position);
            long to = Math.Min(end, position + range.Length);
            if (to > from)
            {
                result.Add(new ByteRange(range.Offset + (from - position), to - from));
            }

            position += range.Length;
        }

        return result;
    }

    public ByteRange Whole(int packet)
    {
        (int first, int count) = _packets[packet];
        ByteRange start = _ranges[first];
        return new ByteRange(start.Offset, _ranges[first + count - 1].End - start.Offset);
    }

    public IReadOnlyList<ByteRange> WholeRanges(int packet)
    {
        (int first, int count) = _packets[packet];
        var result = new ByteRange[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = _ranges[first + i];
        }

        return result;
    }
}

// A piece of a frame: bits of one packet (counted from the packet's first bit, header included).
internal readonly record struct FramePart(int Packet, int StartBit, int Bits);

// The bits of frames and fields placed in the file.
internal static class BitNodes
{
    // A node over bits [start, end) of a frame whose bits lie in parts, one after another. Within one part and one file range it carries its
    // bit position when isPrecise and the bits are not whole bytes; across parts it carries the ranges of each and no bit position.
    public static Node Make(PacketTable table, ReadOnlySpan<FramePart> parts, long start, long end, string name, NodeValue? value, string description,
        string? topic = null, NodeKind kind = NodeKind.Detail, bool isPrecise = true)
    {
        var ranges = new List<ByteRange>(2);
        long position = 0;
        int touched = 0;
        long firstBit = 0;
        long lastBit = 0;
        int firstPacket = -1;
        if (end <= start)
        {
            end = start;
        }

        foreach (FramePart part in parts)
        {
            long partStart = position;
            long partEnd = position + part.Bits;
            position = partEnd;
            long from = Math.Max(start, partStart);
            long to = Math.Min(end, partEnd);
            if (to < from || (to == from && !(from == start && start >= partStart && start < partEnd)))
            {
                continue;
            }

            long bitFrom = part.StartBit + (from - partStart);
            long bitTo = part.StartBit + (to - partStart);
            int byteFrom = (int)(bitFrom / 8);
            int byteTo = to == from ? byteFrom + 1 : (int)((bitTo + 7) / 8);
            if (touched == 0)
            {
                firstBit = bitFrom;
                firstPacket = part.Packet;
            }

            lastBit = bitTo;
            touched++;
            foreach (ByteRange range in table.RangesOf(part.Packet, byteFrom, byteTo))
            {
                if (ranges.Count > 0 && range.Offset < ranges[^1].End)
                {
                    continue;
                }

                if (ranges.Count > 0 && range.Offset == ranges[^1].End)
                {
                    ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + range.Length };
                }
                else
                {
                    ranges.Add(range);
                }
            }
        }

        if (ranges.Count == 0)
        {
            ByteRange whole = table.Count > 0 && parts.Length > 0 ? table.Whole(parts[0].Packet) : new ByteRange(0, 0);
            return new Node(name, whole.Offset, 0, value) { Kind = kind, Description = description, Topic = topic };
        }

        if (ranges.Count == 1)
        {
            long offset = ranges[0].Offset;
            if (isPrecise && touched == 1)
            {
                int bitInByte = (int)(firstBit % 8);
                return Node.FromBits(name, offset, bitInByte, bitInByte + (lastBit - firstBit), value, kind, description, topic);
            }

            return new Node(name, offset, ranges[0].Length, value) { Kind = kind, Description = description, Topic = topic };
        }

        return new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value) { Kind = kind, Description = description, Topic = topic, Ranges = ranges };
    }

    // The file offset of the byte that holds bit position start of a frame.
    public static long OffsetOf(PacketTable table, ReadOnlySpan<FramePart> parts, long start)
    {
        long position = 0;
        foreach (FramePart part in parts)
        {
            if (start < position + part.Bits || part.Equals(parts[^1]))
            {
                long bit = part.StartBit + Math.Max(0, start - position);
                return table.OffsetOf(part.Packet, (int)(bit / 8));
            }

            position += part.Bits;
        }

        return 0;
    }
}

// Joins the bits of the parts of a frame into one buffer that starts at bit 0, where the frame's bits are read.
internal static class BitCopy
{
    // Copies count bits from source (from bit sourceBit) to target (to bit targetBit); target holds at least 8 bytes of slack after them.
    public static void Copy(ReadOnlySpan<byte> source, int sourceBit, Span<byte> target, int targetBit, int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, 32);
            uint value = Get(source, sourceBit, chunk);
            int index = targetBit >> 3;
            int shift = 64 - (targetBit & 7) - chunk;
            ulong window = BinaryPrimitives.ReadUInt64BigEndian(target[index..]);
            ulong mask = (chunk == 64 ? ulong.MaxValue : (1UL << chunk) - 1) << shift;
            window = (window & ~mask) | ((ulong)value << shift);
            BinaryPrimitives.WriteUInt64BigEndian(target[index..], window);
            sourceBit += chunk;
            targetBit += chunk;
            count -= chunk;
        }
    }

    // The count bits (1 to 32) at bit position, as the low bits of the result; bits past the data are zero.
    public static uint Get(ReadOnlySpan<byte> data, int position, int count)
    {
        int index = position >> 3;
        ulong window = 0;
        for (int i = 0; i < 8; i++)
        {
            window = (window << 8) | (index + i < data.Length ? data[index + i] : (uint)0);
        }

        return (uint)((window << (position & 7)) >> (64 - count));
    }
}

// Counts and shares as the statistics write them.
internal static class WmaText
{
    public static string Count(long count, string singular, string plural) => Counts.Of(count, singular, plural);

    // "K things (x.x%)" for part of a whole, with one decimal rounded to even and 0.0% for 0 of 0.
    public static string Share(long part, long whole, string singular, string plural)
    {
        double percent = whole == 0 ? 0 : part * 100.0 / whole;
        return string.Create(CultureInfo.InvariantCulture, $"{Counts.Of(part, singular, plural)} ({Math.Round(percent, 1, MidpointRounding.ToEven):0.0}%)");
    }

    public static string Hex(long offset) => offset.ToString("X", CultureInfo.InvariantCulture);
}
