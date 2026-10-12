using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The bytes of a node, and for a field that does not cover whole bytes the bits within them: its
// ranges when its bytes are not contiguous (Node.Ranges), and its bit offset and width
// (Node.BitOffset, Node.BitLength), whose bits run on across the ranges in order.
public sealed class HexHighlight
{
    private readonly long[] _starts;

    // Bytes of the node before each range, for finding where its bits are.
    private readonly long[] _before;

    private HexHighlight(IReadOnlyList<ByteRange> ranges, int? bitOffset, int? bitLength)
    {
        Ranges = ranges;
        BitOffset = bitOffset;
        BitLength = bitLength;
        _starts = ranges.Select(r => r.Offset).ToArray();
        _before = new long[ranges.Count];
        for (int i = 1; i < ranges.Count; i++)
        {
            _before[i] = _before[i - 1] + ranges[i - 1].Length;
        }
    }

    public IReadOnlyList<ByteRange> Ranges { get; }

    public int? BitOffset { get; }

    public int? BitLength { get; }

    public long Start => Ranges[0].Offset;

    public long End => Ranges[^1].End;

    // Null for a node of no bytes.
    public static HexHighlight? For(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        IReadOnlyList<ByteRange> ranges = node.Ranges ?? [new ByteRange(node.Offset, node.Length)];
        return node.Length == 0 ? null : new HexHighlight(ranges, node.BitOffset, node.BitLength);
    }

    public bool Contains(long offset) => RangeOf(offset) >= 0;

    // The bits of the byte at offset that belong to the node, from the most significant bit:
    // [Start, End) within 0 to 8, or null for a byte outside it. A node of whole bytes covers
    // every bit of its bytes.
    public (int Start, int End)? BitsOf(long offset)
    {
        int range = RangeOf(offset);
        if (range < 0)
        {
            return null;
        }

        if (BitOffset is not { } bitOffset || BitLength is not { } bitLength)
        {
            return (0, 8);
        }

        long relative = ((_before[range] + offset - Ranges[range].Offset) * 8) - bitOffset;
        int start = (int)Math.Max(0, -relative);
        int end = (int)Math.Min(8, bitLength - relative);
        return end > start ? (start, end) : null;
    }

    private int RangeOf(long offset)
    {
        int index = Array.BinarySearch(_starts, offset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && offset < Ranges[index].End ? index : -1;
    }
}
