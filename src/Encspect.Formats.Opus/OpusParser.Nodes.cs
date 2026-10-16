using Encspect.Core;

namespace Encspect.Formats.Opus;

public sealed partial class OpusParser
{
    // An audio packet's node, whose fields are built from its records when first read.
    private static Node PacketNode(Records records, int index)
    {
        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        int bytes = records.LengthOf(index);
        long length = ranges is null ? bytes : ranges[^1].End - ranges[0].Offset;
        return new Node("Packet", records.OffsetOf(index), length, Summary(records, index, bytes), () => PacketFields(records, index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = Explain.Packet,
            Topic = Topics.OpusToc,
        };
    }

    private static string Summary(Records records, int index, int bytes)
    {
        PartRecord first = records.Part(index, 0);
        string text = bytes == 1 ? "1 byte" : $"{bytes} bytes";
        if (!first.HasToc)
        {
            return text + ", empty";
        }

        string frames = first.Status == PartStatus.Valid ? $", {first.Frames} {(first.Frames == 1 ? "frame" : "frames")}" : ", invalid";
        return $"{text}, {Toc.Describe(first.TocByte)}{(Toc.IsStereo(first.TocByte) ? ", stereo" : ", mono")}{frames}";
    }

    private static List<Node> PacketFields(Records records, int index)
    {
        records.Ranges.TryGetValue(index, out ByteRange[]? ranges);
        int bytes = records.LengthOf(index);
        var at = new Place(records.OffsetOf(index), ranges is null ? null : records.Opaque.Contains(index) ? MappedBytes.Opaque(new byte[bytes], ranges[0].Offset, ranges[^1].End) : new MappedBytes(new byte[bytes], ranges));
        if (records.StreamCount == 1)
        {
            return PartFields(records, records.Part(index, 0), at, 0);
        }

        var nodes = new List<Node>();
        int start = 0;
        for (int s = 0; s < records.StreamCount; s++)
        {
            PartRecord part = records.Part(index, s);
            if (part.Status == PartStatus.Missing)
            {
                continue;
            }

            Node stream = at.Bytes($"Stream {s}", start, start + part.Length, part.HasToc ? Toc.Describe(part.TocByte) : "empty",
                s < records.StreamCount - 1 ? Explain.StreamSelfDelimited : Explain.StreamLast);
            foreach (Node child in PartFields(records, part, at, start))
            {
                stream.Add(child);
            }

            nodes.Add(stream);
            start += part.Length;
        }

        return nodes;
    }

    // One stream's part as RFC 6716 lays it out (sections 3.1 and 3.2): the TOC byte, for code 3
    // the frame count byte and the padding length, the frame lengths, the frames, the padding.
    private static List<Node> PartFields(Records records, PartRecord part, Place at, int start)
    {
        var nodes = new List<Node>();
        if (!part.HasToc)
        {
            return nodes;
        }

        byte toc = part.TocByte;
        Node tocNode = at.Bytes("TOC", start, start + 1, $"{Toc.Describe(toc)}, {(Toc.IsStereo(toc) ? "stereo" : "mono")}, code {part.Code}", Explain.TocByte);
        long bit = start * 8L;
        tocNode.Add(at.Bits("Config", bit, bit + 5, $"{Toc.Config(toc)}: {Toc.Describe(toc)}", Explain.Config));
        tocNode.Add(at.Bits("IsStereo", bit + 5, bit + 6, NodeValue.Boolean(Toc.IsStereo(toc)), Explain.IsStereo));
        tocNode.Add(at.Bits("Frame Count Code", bit + 6, bit + 8, CodeText(part.Code), Explain.FrameCountCode));
        nodes.Add(tocNode);

        int position = start + 1;
        if (part.HasCountByte)
        {
            byte count = part.CountByte;
            Node countNode = at.Bytes("Frame Count Byte", position, position + 1, $"{count & 0x3F} frames, {(part.IsVbr ? "VBR" : "CBR")}{(part.HasPadding ? ", padded" : string.Empty)}",
                Explain.FrameCountByte);
            long b = position * 8L;
            countNode.Add(at.Bits("IsVbr", b, b + 1, NodeValue.Boolean(part.IsVbr), Explain.CountIsVbr));
            countNode.Add(at.Bits("HasPadding", b + 1, b + 2, NodeValue.Boolean(part.HasPadding), Explain.CountHasPadding));
            countNode.Add(at.Bits("Frame Count", b + 2, b + 8, NodeValue.Integer(count & 0x3F), Explain.CountFrames));
            nodes.Add(countNode);
            position++;
        }

        if (part.PaddingLengthBytes > 0)
        {
            nodes.Add(at.Bytes("Padding Length", position, position + part.PaddingLengthBytes, NodeValue.Integer(part.PaddingLengthBytes, "bytes", "byte"), Explain.PaddingLength));
            position += part.PaddingLengthBytes;
        }

        int end = start + part.Length;
        if (part.Status != PartStatus.Valid)
        {
            if (position < end)
            {
                nodes.Add(at.Bytes("Unparsed", position, end, NodeValue.Integer(end - position, "bytes", "byte"), Explain.Unparsed));
            }

            return nodes;
        }

        int header = start + part.Header;
        if (header > position)
        {
            var lengths = new List<int>();
            for (int f = 0; f < part.Frames; f++)
            {
                lengths.Add(records.FrameLengths[part.FirstFrame + f]);
            }

            nodes.Add(at.Bytes("Frame Lengths", position, header, string.Join(", ", lengths), Explain.FrameLengths));
        }

        position = header;
        for (int f = 0; f < part.Frames; f++)
        {
            int length = records.FrameLengths[part.FirstFrame + f];
            nodes.Add(at.Bytes("Frame", position, position + length, length == 0 ? "0 bytes (no data)" : NodeValue.Integer(length, "bytes", "byte"),
                length == 0 ? Explain.EmptyFrame : Explain.Frame));
            position += length;
        }

        if (position < end)
        {
            nodes.Add(at.Bytes("Padding", position, end, NodeValue.Integer(end - position, "bytes", "byte"), Explain.Padding));
        }

        return nodes;
    }

    private static string CodeText(int code) => code switch
    {
        0 => "0 (one frame)",
        1 => "1 (two frames of equal size)",
        2 => "2 (two frames of different sizes)",
        _ => "3 (any number of frames)",
    };

    // Places fields of a packet in the file: at its offset when its bytes are contiguous, or
    // through its ranges when it spans pages.
    private sealed class Place(long offset, MappedBytes? mapped)
    {
        public Node Bytes(string name, int start, int end, NodeValue? value, string description) => mapped is null
            ? new Node(name, offset + start, end - start, value) { Description = description }
            : Node.FromMapped(name, mapped, start, end, value, description: description);

        public Node Bits(string name, long start, long end, NodeValue? value, string description) => mapped is null
            ? Node.FromBits(name, offset, start, end, value, description: description)
            : Node.FromBits(name, mapped, start, end, value, description: description);
    }
}
