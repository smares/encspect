using Encspect.Core;

namespace Encspect.Formats.Dts;

// Where the units that are not one run of the file lie in it: a unit that a container hands over with stripped bytes put back (Matroska's header stripping, whose bytes come from
// the ContentCompSettings) or put together from fragments. Such a unit is at a position of its own in the records, which is where its last piece is less the bytes before it, so
// that it is unique and in the order of the stream; the nodes the syntax makes lie in the unit's bytes at that position, and are placed in the pieces by Place. Units of one run have
// no entry. Written while the stream is read, read by the nodes built after it.
internal sealed class UnitPieces
{
    private readonly ChunkedList<long> _positions = new();
    private readonly ChunkedList<int> _lengths = new();
    private readonly ChunkedList<int> _first = new();
    private readonly ChunkedList<ByteRange> _ranges = new();

    public bool IsEmpty => _positions.Count == 0;

    // The position of the packet's bytes [start, end) in the records, and their pieces where they are not one run of the file; the position is the file offset where they are one.
    public static long PositionOf(MappedBytes packet, int start, int end, out IReadOnlyList<ByteRange>? pieces)
    {
        IReadOnlyList<ByteRange> ranges = packet.IsOpaque ? [] : packet.FileRanges(start, end);
        if (ranges.Count <= 1)
        {
            pieces = null;
            return packet.OffsetOf(start);
        }

        pieces = ranges;
        return Math.Max(0, ranges[^1].Offset - ((end - start) - ranges[^1].Length));
    }

    public void Add(long position, int length, IReadOnlyList<ByteRange> pieces)
    {
        _positions.Add(position);
        _lengths.Add(length);
        _first.Add(_ranges.Count);
        foreach (ByteRange piece in pieces)
        {
            _ranges.Add(piece);
        }
    }

    // The entry whose bytes [position, position + length) hold offset, or -1.
    private int Find(long offset)
    {
        int low = 0;
        int high = _positions.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (_positions[middle] <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return high >= 0 && offset < _positions[high] + Math.Max(1, _lengths[high]) ? high : -1;
    }

    public bool TryGet(long position, out List<ByteRange> pieces)
    {
        pieces = [];
        int index = Find(position);
        if (index < 0 || _positions[index] != position)
        {
            return false;
        }

        pieces = Pieces(index);
        return true;
    }

    private List<ByteRange> Pieces(int index)
    {
        var pieces = new List<ByteRange>();
        int end = index + 1 < _first.Count ? _first[index + 1] : _ranges.Count;
        for (int i = _first[index]; i < end; i++)
        {
            pieces.Add(_ranges[i]);
        }

        return pieces;
    }

    // The file ranges that hold bytes [from, to) of the unit that starts at the entry's position.
    private static List<ByteRange> Within(List<ByteRange> pieces, long from, long to)
    {
        var ranges = new List<ByteRange>();
        long at = 0;
        foreach (ByteRange piece in pieces)
        {
            long start = Math.Max(from, at);
            long end = Math.Min(to, at + piece.Length);
            if (start < end)
            {
                long file = piece.Offset + (start - at);
                if (ranges.Count > 0 && ranges[^1].End == file)
                {
                    ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + (end - start) };
                }
                else
                {
                    ranges.Add(new ByteRange(file, end - start));
                }
            }

            at += piece.Length;
        }

        return ranges;
    }

    // The file offset of the first byte of the frame or unit at a position of the records (the position itself where it is one run of the file).
    public long FirstFile(long position)
    {
        int index = IsEmpty ? -1 : Find(position);
        if (index < 0)
        {
            return position;
        }

        long inside = position - _positions[index];
        foreach (ByteRange piece in Pieces(index))
        {
            if (inside < piece.Length)
            {
                return piece.Offset + inside;
            }

            inside -= piece.Length;
        }

        return position;
    }

    // The node as the file has it, where it lies in a unit in pieces: at the first piece's bytes it touches, to the end of the last, with Ranges where they are several; its children
    // the same, built when they are read where the node's are.
    public Node Place(Node node)
    {
        if (IsEmpty || Find(node.Offset) is not (>= 0 and var index))
        {
            return node;
        }

        List<ByteRange> pieces = Pieces(index);
        long position = _positions[index];
        List<ByteRange> ranges = node.Length == 0 ? [] : Within(pieces, node.Offset - position, node.End - position);
        long offset = ranges.Count > 0 ? ranges[0].Offset : Within(pieces, node.Offset - position, node.Offset - position + 1) is { Count: > 0 } one ? one[0].Offset : pieces[0].Offset;
        long length = ranges.Count > 0 ? ranges[^1].End - ranges[0].Offset : 0;
        Func<IReadOnlyList<Node>>? build = node.AreChildrenBuiltOnDemand ? () => [.. node.Children.Select(Place)] : null;
        Node placed;
        if (node.BitOffset is { } bit && node.BitLength is { } bits)
        {
            // Bits that continue in another piece of the file are placed by the pieces' bytes.
            placed = ranges.Count > 1
                ? Node.FromBits(node.Name, new MappedBytes(new byte[ranges.Sum(r => r.Length)], ranges), bit, bit + bits, node.Value, node.Kind, node.Description, node.Topic, node.BitOrder)
                : Node.FromBits(node.Name, offset, bit, bit + bits, node.Value, node.Kind, node.Description, node.Topic, node.BitOrder);
        }
        else
        {
            IReadOnlyList<ByteRange>? spread = ranges.Count > 1 ? ranges : null;
            placed = build is null
                ? new Node(node.Name, offset, length, node.Value) { Kind = node.Kind, Description = node.Description, Topic = node.Topic, Ranges = spread }
                : new Node(node.Name, offset, length, node.Value, build) { Kind = node.Kind, Description = node.Description, Topic = node.Topic, Ranges = spread };
        }

        if (build is null)
        {
            foreach (Node child in node.Children)
            {
                placed.Add(Place(child));
            }
        }

        return placed;
    }
}
