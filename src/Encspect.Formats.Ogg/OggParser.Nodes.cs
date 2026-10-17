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
            return new Node("Gap", page.Offset, page.Length, NodeValue.Integer(page.Length, "bytes", "byte")) { Description = Explain.Gap };
        }

        return new Node("Page", page.Offset, page.Length, Summary(page), () => PageFields(region, index)) { Kind = NodeKind.Frame, Description = Explain.Page };
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

        if ((page.Status & PageStatus.Untracked) != 0)
        {
            text += ", stream not followed";
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
        var fields = new List<Node> { new("Capture Pattern", offset, Math.Min(4L, page.Length), "OggS") { Description = Explain.CapturePattern } };
        if ((page.Status & PageStatus.HeaderCut) != 0)
        {
            if (page.Length > 4)
            {
                fields.Add(new Node("Unparsed", offset + 4, page.Length - 4, NodeValue.Integer(page.Length - 4, "bytes", "byte")) { Description = Explain.UnparsedHeader });
            }

            return fields;
        }

        fields.Add(new Node("Version", offset + 4, 1, NodeValue.Integer(0)) { Description = Explain.Version });
        Node flags = new("Header Type", offset + 5, 1, NodeValue.Hex(page.Flags, 2)) { Description = Explain.HeaderType };
        flags.Add(Node.FromBits("IsContinued", offset + 5, 7, 8, NodeValue.Boolean((page.Flags & Pages.Continued) != 0), description: Explain.IsContinued));
        flags.Add(Node.FromBits("IsBeginningOfStream", offset + 5, 6, 7, NodeValue.Boolean((page.Flags & Pages.Beginning) != 0), description: Explain.IsBeginningOfStream));
        flags.Add(Node.FromBits("IsEndOfStream", offset + 5, 5, 6, NodeValue.Boolean((page.Flags & Pages.End) != 0), description: Explain.IsEndOfStream));
        if ((page.Flags & 0xF8) != 0)
        {
            flags.Add(Node.FromBits("Undefined Flags", offset + 5, 0, 5, NodeValue.Hex(page.Flags >> 3, 2), description: Explain.UndefinedFlags));
        }

        fields.Add(flags);
        fields.Add(new Node("Granule Position", offset + 6, 8, NodeValue.Integer(page.Granule))
        {
            Description = page.Granule == -1 ? Explain.GranuleNone : Explain.GranulePosition,
            Topic = Topics.GranulePosition,
        });
        fields.Add(new Node("Serial Number", offset + 14, 4, NodeValue.Hex(page.Serial, 8)) { Description = Explain.SerialNumber });
        fields.Add(new Node("Sequence Number", offset + 18, 4, NodeValue.Integer(page.Sequence)) { Description = Explain.SequenceNumber });
        Node crc = new("CRC", offset + 22, 4, NodeValue.Hex(page.Crc, 8)) { Description = Explain.Crc };
        if ((page.Status & PageStatus.Truncated) == 0)
        {
            crc.Add("Computed", offset + 22, 4, NodeValue.Hex(page.ComputedCrc, 8), Explain.ComputedCrc);
        }

        fields.Add(crc);
        fields.Add(new Node("Segments", offset + 26, 1, NodeValue.Integer(page.Segments)) { Description = Explain.Segments });

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
                    nodes[i] = new Node("Lacing Value", offset + 27 + i, 1, NodeValue.Integer(values[i], "bytes", "byte")) { Description = Explain.Lacing(values[i]) };
                }

                return nodes;
            })
            {
                Description = Explain.SegmentTable,
            });
        }

        long bodyOffset = page.BodyOffset;
        long bodyPresent = page.Offset + page.Length - bodyOffset;
        if (bodyPresent <= 0)
        {
            return fields;
        }

        if (page.IsLost || (page.Status & PageStatus.Untracked) != 0)
        {
            string why = (page.Status & PageStatus.Untracked) != 0 ? "the stream is beyond the limit of followed streams"
                : (page.Status & PageStatus.Truncated) != 0 ? "the page is cut short" : "the page fails its CRC check";
            fields.Add(new Node("Body", bodyOffset, bodyPresent, $"{Bytes(bodyPresent)}, not read as packets: {why}") { Description = Explain.LostBody });
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
            string description = run.Kind switch
            {
                RunKind.Orphan => Explain.OrphanData,
                RunKind.TooLarge => Explain.TooLargeData,
                _ => Explain.PacketData,
            };
            yield return new Node("Packet Data", page.BodyOffset + run.Start, run.Length, $"{Bytes(run.Length)}, {what}") { Description = description };
        }
    }
}
