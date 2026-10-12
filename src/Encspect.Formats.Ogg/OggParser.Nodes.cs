using Encspect.Core;

namespace Encspect.Formats.Ogg;

public sealed partial class OggParser
{
    // A page's node, whose fields are built from its record and segment table when first read.
    private static Node PageNode(Region region, int index)
    {
        PageRecord page = region.Pages[index];
        if (page.IsGap)
        {
            return new Node("Gap", page.Offset, page.Length, NodeValue.Integer(page.Length, "bytes", "byte"));
        }

        return new Node("Page", page.Offset, page.Length, Summary(page), () => PageFields(region, index)) { Kind = NodeKind.Frame };
    }

    private static string Summary(in PageRecord page)
    {
        if ((page.Status & PageStatus.HeaderCut) != 0)
        {
            return "header cut short";
        }

        string text = $"serial 0x{page.Serial:X8}, sequence {page.Sequence}, granule {page.Granule}";
        if ((page.Flags & Pages.Beginning) != 0)
        {
            text += ", beginning of stream";
        }

        if ((page.Flags & Pages.Continued) != 0)
        {
            text += ", continued packet";
        }

        if ((page.Flags & Pages.End) != 0)
        {
            text += ", end of stream";
        }

        return (page.Status & PageStatus.Truncated) != 0 ? text + ", cut short"
            : (page.Status & PageStatus.CrcMismatch) != 0 ? text + ", CRC mismatch"
            : text;
    }

    // RFC 3533, section 6: the header fields at their offsets, the segment table, then the body as
    // the packets' bytes on this page.
    private static List<Node> PageFields(Region region, int index)
    {
        PageRecord page = region.Pages[index];
        long offset = page.Offset;
        var fields = new List<Node> { new("Capture Pattern", offset, Math.Min(4L, page.Length), "OggS") };
        if ((page.Status & PageStatus.HeaderCut) != 0)
        {
            if (page.Length > 4)
            {
                fields.Add(new Node("Unparsed", offset + 4, page.Length - 4, NodeValue.Integer(page.Length - 4, "bytes", "byte")));
            }

            return fields;
        }

        fields.Add(new Node("Version", offset + 4, 1, NodeValue.Integer(0)));
        Node flags = new("Header Type", offset + 5, 1, NodeValue.Hex(page.Flags, 2));
        flags.Add(Node.FromBits("IsContinued", offset + 5, 7, 8, NodeValue.Boolean((page.Flags & Pages.Continued) != 0)));
        flags.Add(Node.FromBits("IsBeginningOfStream", offset + 5, 6, 7, NodeValue.Boolean((page.Flags & Pages.Beginning) != 0)));
        flags.Add(Node.FromBits("IsEndOfStream", offset + 5, 5, 6, NodeValue.Boolean((page.Flags & Pages.End) != 0)));
        if ((page.Flags & 0xF8) != 0)
        {
            flags.Add(Node.FromBits("Undefined Flags", offset + 5, 0, 5, NodeValue.Hex(page.Flags >> 3, 2)));
        }

        fields.Add(flags);
        fields.Add(new Node("Granule Position", offset + 6, 8, NodeValue.Integer(page.Granule)));
        fields.Add(new Node("Serial Number", offset + 14, 4, NodeValue.Hex(page.Serial, 8)));
        fields.Add(new Node("Sequence Number", offset + 18, 4, NodeValue.Integer(page.Sequence)));
        Node crc = new("CRC", offset + 22, 4, NodeValue.Hex(page.Crc, 8));
        if ((page.Status & PageStatus.Truncated) == 0)
        {
            crc.Add("Computed", offset + 22, 4, NodeValue.Hex(page.ComputedCrc, 8));
        }

        fields.Add(crc);
        fields.Add(new Node("Segments", offset + 26, 1, NodeValue.Integer(page.Segments)));

        int present = page.LacingPresent;
        if (present > 0)
        {
            long lacing = page.Lacing;
            fields.Add(new Node("Segment Table", offset + 27, present, NodeValue.Integer(page.Segments, "lacing values", "lacing value"), () =>
            {
                ReadOnlySpan<byte> values = region.Lacing.Read(lacing, present);
                var nodes = new Node[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    nodes[i] = new Node("Lacing Value", offset + 27 + i, 1, NodeValue.Integer(values[i], "bytes", "byte"));
                }

                return nodes;
            }));
        }

        long bodyOffset = page.BodyOffset;
        long bodyPresent = page.Offset + page.Length - bodyOffset;
        if (bodyPresent <= 0)
        {
            return fields;
        }

        if (page.IsLost)
        {
            string why = (page.Status & PageStatus.Truncated) != 0 ? "the page is cut short" : "the page fails its CRC check";
            fields.Add(new Node("Body", bodyOffset, bodyPresent, $"{bodyPresent} bytes, not read as packets: {why}"));
            return fields;
        }

        fields.AddRange(BodyRuns(region, page));
        return fields;
    }

    // The packets' bytes on the page, as the scan saw them.
    private static IEnumerable<Node> BodyRuns(Region region, PageRecord page)
    {
        var assembler = new PacketAssembler();
        assembler.Restore((page.Status & PageStatus.PendingBefore) != 0 && (page.Status & PageStatus.DropsPending) == 0, page.CompletedBefore);
        var runs = new List<Run>();
        assembler.Feed((page.Flags & Pages.Continued) != 0, region.Lacing.Read(page.Lacing, page.Segments), runs);
        foreach (Run run in runs)
        {
            string what = run.Kind switch
            {
                RunKind.Orphan => "the rest of a packet whose start is missing",
                RunKind.TooLarge => "part of a packet too large to read",
                _ when run.Continues && run.Ends => $"the end of packet {run.Packet}",
                _ when run.Continues => $"more of packet {run.Packet}, which continues on the next page",
                _ when run.Ends => $"packet {run.Packet}",
                _ => $"the start of packet {run.Packet}, which continues on the next page",
            };
            yield return new Node("Packet Data", page.BodyOffset + run.Start, run.Length, $"{run.Length} bytes, {what}");
        }
    }

    // The pages and gaps of a region, built from the records in blocks when first read and held
    // weakly, so writing a region of a hundred thousand pages never keeps all their nodes, and
    // readers on several threads get the same nodes while a block lives.
    private sealed class PageList(Region region) : IReadOnlyList<Node>
    {
        private const int BlockSize = 1024;

        private readonly WeakReference<Node[]>?[] _blocks = new WeakReference<Node[]>?[(region.Pages.Count + BlockSize - 1) / BlockSize];

        // The block read last, so reading the nodes in order does not look up a weak reference each
        // time.
        private Block? _last;

        public int Count => region.Pages.Count;

        public Node this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
                return BlockOf(index / BlockSize)[index % BlockSize];
            }
        }

        public IEnumerator<Node> GetEnumerator()
        {
            for (int block = 0; block < _blocks.Length; block++)
            {
                foreach (Node node in BlockOf(block))
                {
                    yield return node;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private Node[] BlockOf(int block)
        {
            if (Volatile.Read(ref _last) is { } last && last.Index == block)
            {
                return last.Nodes;
            }

            Node[]? built = null;
            while (true)
            {
                WeakReference<Node[]>? current = Volatile.Read(ref _blocks[block]);
                if (current is not null && current.TryGetTarget(out Node[]? existing))
                {
                    Volatile.Write(ref _last, new Block(block, existing));
                    return existing;
                }

                built ??= Build(block);
                if (Interlocked.CompareExchange(ref _blocks[block], new WeakReference<Node[]>(built), current) == current)
                {
                    Volatile.Write(ref _last, new Block(block, built));
                    return built;
                }
            }
        }

        private Node[] Build(int block)
        {
            int start = block * BlockSize;
            var nodes = new Node[Math.Min(BlockSize, Count - start)];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = PageNode(region, start + i);
            }

            return nodes;
        }

        private sealed record Block(int Index, Node[] Nodes);
    }
}
