using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Where the frames that are not one run of the file lie in it: a frame that a container hands over put together from fragments (an ASF media object fragmented across packets), or
// with stripped bytes put back (Matroska's header stripping, whose bytes come from the ContentCompSettings). Such a frame is at a position of its own in the records, which is
// where its last piece is less the bytes before it, so that it is unique and in the order of the stream; the pieces are what its nodes lie in. Frames of one run have no entry,
// so a stream of them costs nothing. Written while the stream is read, read by the nodes built after it: no writer at the same time as a reader.
internal sealed class FramePieces
{
    private readonly ChunkedList<long> _positions = new();
    private readonly ChunkedList<int> _first = new();
    private readonly ChunkedList<ByteRange> _ranges = new();

    public bool IsEmpty => _positions.Count == 0;

    // The position a frame of the packet's bytes [start, end) has in the records, and its pieces where it is not one run of the file; the position is the file offset of a frame that is.
    public static long PositionOf(MappedBytes packet, int start, int end, out IReadOnlyList<ByteRange>? pieces)
    {
        IReadOnlyList<ByteRange> ranges = packet.IsOpaque ? [] : packet.FileRanges(start, end);
        if (ranges.Count <= 1)
        {
            pieces = null;
            return packet.OffsetOf(start);
        }

        pieces = ranges;
        long before = (end - start) - ranges[^1].Length;
        return Math.Max(0, ranges[^1].Offset - before);
    }

    public void Add(long position, IReadOnlyList<ByteRange> pieces)
    {
        _positions.Add(position);
        _first.Add(_ranges.Count);
        foreach (ByteRange piece in pieces)
        {
            _ranges.Add(piece);
        }
    }

    public bool TryGet(long position, out List<ByteRange> pieces)
    {
        pieces = [];
        int low = 0;
        int high = _positions.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            long at = _positions[middle];
            if (at == position)
            {
                int end = middle + 1 < _first.Count ? _first[middle + 1] : _ranges.Count;
                for (int i = _first[middle]; i < end; i++)
                {
                    pieces.Add(_ranges[i]);
                }

                return true;
            }

            if (at < position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    // The file offset of the first byte of the frame or unit at a position of the records (the position itself where it is one run of the file).
    public long FirstFile(long position) => FileOf(position);

    // The file offset of the byte at a position of the records: inside a frame in pieces, the byte of the piece it is in (frames of one run are at their file offsets).
    public long FileOf(long position)
    {
        if (IsEmpty)
        {
            return position;
        }

        int low = 0;
        int high = _positions.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (_positions[middle] <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (high < 0 || !TryGet(_positions[high], out List<ByteRange> pieces))
        {
            return position;
        }

        long inside = position - _positions[high];
        foreach (ByteRange piece in pieces)
        {
            if (inside < piece.Length)
            {
                return piece.Offset + inside;
            }

            inside -= piece.Length;
        }

        return position;
    }

    // The node as the file has it, where it lies in a frame in pieces: a frame or gap at the first piece, to the end of the last, with Ranges; a field of such a frame as the
    // frame's tree places it (Tree.Bits), in the pieces where it ends inside the frame's bytes, else at its position as made.
    public Node Place(Node node)
    {
        if (IsEmpty || Containing(node.Offset) is not (>= 0 and var index) || !TryGet(_positions[index], out List<ByteRange> pieces))
        {
            return node;
        }

        long position = _positions[index];
        if (node.Name is "Frame" or "Gap" && node.Offset == position)
        {
            return new Node(node.Name, pieces[0].Offset, pieces[^1].End - pieces[0].Offset, node.Value)
            {
                Kind = node.Kind,
                Description = node.Description,
                Topic = node.Topic,
                Ranges = pieces,
            };
        }

        long total = pieces.Sum(p => (long)p.Length);
        long start = (node.Offset - position) * 8 + (node.BitOffset ?? 0);
        long end = node.BitLength is { } bits ? start + bits : (node.End - position) * 8;
        if (end > total * 8)
        {
            return node;
        }

        return Node.FromBits(node.Name, new MappedBytes(new byte[total], pieces), start, end, node.Value, node.Kind, node.Description, node.Topic, node.BitOrder);
    }

    // The index of the frame in pieces whose bytes hold the position, or -1.
    private int Containing(long position)
    {
        int low = 0;
        int high = _positions.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (_positions[middle] <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (high < 0 || !TryGet(_positions[high], out List<ByteRange> pieces) || position >= _positions[high] + pieces.Sum(p => (long)p.Length))
        {
            return -1;
        }

        return high;
    }
}
