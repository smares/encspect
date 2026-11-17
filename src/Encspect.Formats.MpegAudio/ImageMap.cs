using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// Where the bytes of a packet image lie in the file. The image is the stream as the parser reads it: a packet put together from pieces of the file (an ASF media
// object fragmented across packets) has its pieces one after another, and a packet with stripped bytes put back (Matroska's header stripping) has them in front,
// read again from the ContentCompSettings element, which is the file's. Each segment is a run of image bytes that is one run of file bytes. For packets of one piece
// the image is the file itself (the segments are the stretches), which IsIdentity tells, and nothing is mapped.
internal sealed class ImageMap
{
    private readonly List<Segment> _segments = [];

    public bool IsIdentity { get; private set; } = true;

    public IReadOnlyList<Segment> Segments => _segments;

    public readonly record struct Segment(long Virtual, long Length, long File)
    {
        public long VirtualEnd => Virtual + Length;
    }

    // Adds a run after the ones before it, merged with the last where both the image and the file continue.
    public void Add(long virtualStart, long length, long file)
    {
        if (length <= 0)
        {
            return;
        }

        if (virtualStart != file)
        {
            IsIdentity = false;
        }

        if (_segments.Count > 0)
        {
            Segment last = _segments[^1];
            if (last.VirtualEnd == virtualStart && last.File + last.Length == file)
            {
                _segments[^1] = last with { Length = last.Length + length };
                return;
            }
        }

        _segments.Add(new Segment(virtualStart, length, file));
    }

    // The index of the first segment that ends after the image position, or the count where none does.
    public int FirstEndingAfter(long position)
    {
        int low = 0;
        int high = _segments.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (_segments[middle].VirtualEnd <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    // The ranges of the file that hold the image bytes [start, end), in the image's order, those that touch merged: what is between the segments is no part of the stream.
    public List<ByteRange> ToFile(long start, long end)
    {
        var ranges = new List<ByteRange>();
        for (int i = FirstEndingAfter(start); i < _segments.Count && _segments[i].Virtual < end; i++)
        {
            Segment segment = _segments[i];
            long from = Math.Max(segment.Virtual, start);
            long to = Math.Min(segment.VirtualEnd, end);
            long file = segment.File + (from - segment.Virtual);
            if (ranges.Count > 0 && ranges[^1].End == file)
            {
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + (to - from) };
            }
            else
            {
                ranges.Add(new ByteRange(file, to - from));
            }
        }

        // The stripped bytes put back are the same ones of the file in every packet: a node that spans packets lists each range of the file once, in file order.
        bool isOrdered = true;
        for (int i = 1; i < ranges.Count && isOrdered; i++)
        {
            isOrdered = ranges[i].Offset >= ranges[i - 1].End;
        }

        if (!isOrdered)
        {
            ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var union = new List<ByteRange>();
            foreach (ByteRange range in ranges)
            {
                if (union.Count > 0 && range.Offset <= union[^1].End)
                {
                    union[^1] = union[^1] with { Length = Math.Max(union[^1].End, range.End) - union[^1].Offset };
                }
                else
                {
                    union.Add(range);
                }
            }

            return union;
        }

        return ranges;
    }

    // The file offset of the image byte at position, or of the next byte of the stream where it lies between segments; the position itself past the last.
    public long FileOf(long position)
    {
        int index = FirstEndingAfter(position);
        if (index >= _segments.Count)
        {
            return _segments.Count == 0 ? position : _segments[^1].File + _segments[^1].Length;
        }

        Segment segment = _segments[index];
        return segment.File + Math.Max(0, position - segment.Virtual);
    }

    // The node as the file has it: where the image has it at offsets of its own, at the file's, with Ranges where its bytes are in several runs, and its children the same (built
    // when they are read, as the node's are). A node of no byte is where its position is.
    public Node Place(Node node)
    {
        List<ByteRange> ranges = node.Length == 0 ? [] : ToFile(node.Offset, node.End);
        long offset = ranges.Count > 0 ? ranges[0].Offset : FileOf(node.Offset);
        long length = ranges.Count > 0 ? ranges[^1].End - ranges[0].Offset : 0;
        Func<IReadOnlyList<Node>>? build = node.AreChildrenBuiltOnDemand ? () => [.. node.Children.Select(Place)] : null;
        Node placed;
        if (node.BitOffset is { } bit && node.BitLength is { } bits)
        {
            placed = Node.FromBits(node.Name, offset, bit, bit + bits, node.Value, node.Kind, node.Description, node.Topic, node.BitOrder);
            if (ranges.Count > 1)
            {
                placed.Ranges = ranges;
            }
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
