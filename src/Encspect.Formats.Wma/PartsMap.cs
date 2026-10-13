using Encspect.Core;

namespace Encspect.Formats.Wma;

// Where bits of a frame lie in the file. A frame's bits are numbered in its buffer, the parts' bits
// one after another (usually one superframe's, for a frame that spans superframes the end of one and
// the start of the next); a part is whole bytes only at its start, so a field maps byte by byte, and a
// field across two parts gets ranges, like an Ogg packet across pages.
internal sealed class PartsMap(Func<int, MappedBytes> superframe)
{
    private static readonly byte[] Zeros = new byte[1 << 17];

    // The bytes of a superframe as mapped bytes with no content: only the map back to the file is used.
    public static MappedBytes Mapped(int length, IReadOnlyList<ByteRange> ranges, long offset)
    {
        ReadOnlyMemory<byte> data = length <= Zeros.Length ? Zeros.AsMemory(0, length) : new byte[length];
        try
        {
            return new MappedBytes(data, ranges);
        }
        catch (ArgumentException)
        {
            return MappedBytes.Opaque(data, offset, offset + length);
        }
    }

    public readonly record struct Placed(long Offset, long Length, IReadOnlyList<ByteRange>? Ranges, MappedBytes? Map, int StartBit, int EndBit);

    // The place of bits [start, end) of a buffer made of parts.
    public Placed Place(IReadOnlyList<FramePart> parts, int start, int end)
    {
        var pieces = new List<(MappedBytes Map, int Start, int End)>();
        int origin = 0;
        foreach (FramePart part in parts)
        {
            int partStart = origin;
            int partEnd = origin + part.BitCount;
            origin = partEnd;
            int low = Math.Max(start, partStart);
            int high = Math.Min(end, partEnd);
            if (low < high || (start == end && start >= partStart && start <= partEnd && pieces.Count == 0))
            {
                MappedBytes map = superframe(part.Superframe);
                int x = Math.Min(part.StartBit + (low - partStart), map.Length * 8);
                int y = Math.Min(part.StartBit + (Math.Max(low, high) - partStart), map.Length * 8);
                pieces.Add((map, x, y));
            }
        }

        if (pieces.Count == 0 && parts.Count > 0)
        {
            // Bits past the end of the data, which a damaged frame can run to: an empty place at the end of the last part.
            FramePart last = parts[^1];
            MappedBytes map = superframe(last.Superframe);
            int at = Math.Min(map.Length * 8, last.StartBit + last.BitCount);
            pieces.Add((map, at, at));
        }

        if (pieces.Count == 0)
        {
            return new Placed(0, 0, null, null, 0, 0);
        }

        if (pieces.Count == 1)
        {
            (MappedBytes map, int x, int y) = pieces[0];
            return new Placed(0, 0, null, map, x, y);
        }

        var ranges = new List<ByteRange>();
        foreach ((MappedBytes map, int x, int y) in pieces)
        {
            foreach (ByteRange range in map.FileRanges(x / 8, Math.Min(map.Length, (y + 7) / 8)))
            {
                if (ranges.Count > 0 && range.Offset <= ranges[^1].End && range.End >= ranges[^1].Offset)
                {
                    long from = Math.Min(range.Offset, ranges[^1].Offset);
                    long to = Math.Max(range.End, ranges[^1].End);
                    ranges[^1] = new ByteRange(from, to - from);
                }
                else
                {
                    ranges.Add(range);
                }
            }
        }

        if (ranges.Count == 0)
        {
            return new Placed(0, 0, null, null, 0, 0);
        }

        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new Placed(ranges[0].Offset, ranges[^1].End - ranges[0].Offset, ranges.Count > 1 ? ranges : null, null, 0, 0);
    }

    // The bytes the bits touch, as an offset, a length and the ranges where those are not one.
    public (long Offset, long Length, IReadOnlyList<ByteRange>? Ranges) Cover(IReadOnlyList<FramePart> parts, int start, int end)
    {
        Placed place = Place(parts, start, end);
        if (place.Map is { } map)
        {
            IReadOnlyList<ByteRange> ranges = map.FileRanges(place.StartBit / 8, Math.Min(map.Length, (place.EndBit + 7) / 8));
            if (ranges.Count == 0)
            {
                return (map.OffsetOf(Math.Min(map.Length, place.StartBit / 8)), 0, null);
            }

            return (ranges[0].Offset, ranges[^1].End - ranges[0].Offset, ranges.Count > 1 ? ranges : null);
        }

        return (place.Offset, place.Length, place.Ranges);
    }

    // A node for the bits, with the bit position where they lie in one range.
    public Node Field(IReadOnlyList<FramePart> parts, int start, int end, string name, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        Placed place = Place(parts, start, end);
        if (place.Map is { } map)
        {
            return Node.FromBits(name, map, place.StartBit, place.EndBit, value, kind, description, topic);
        }

        return new Node(name, place.Offset, place.Length, value) { Kind = kind, Ranges = place.Ranges, Description = description, Topic = topic };
    }
}
