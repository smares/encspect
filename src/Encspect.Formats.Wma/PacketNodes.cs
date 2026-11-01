using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the packets of a stream from the records the packet reader kept, for either codec. They are built in blocks when
// first read.
internal sealed class PacketNodes(StreamData data, IStreamParameters parameters, bool isLossless)
{
    private readonly IStreamParameters _p = parameters;
    private readonly PacketTable _table = data.Table;

    public Node PacketList(long offset, long length)
    {
        int count = data.Packets.Count;
        return new Node("Packets", offset, length, NodeValue.Text(Counts.Of(count, "packet", "packets")), () => new BlockedChildren([], count, Packet))
        {
            Description = ProExplain.Packets,
            Topic = Topics.WmaPackets,
        };
    }

    public Node FrameList(long offset, long length, Func<int, Node> frame, string topic)
    {
        int count = (int)data.Frames.Count;
        return new Node("Frames", offset, length, NodeValue.Text(Counts.Of(count, "frame", "frames")), () => new BlockedChildren([], count, frame))
        {
            Description = ProExplain.Frames,
            Topic = topic,
        };
    }

    public Node Packet(int index)
    {
        PacketRecord record = data.Packets[index];
        ByteRange whole = _table.Whole(index);
        IReadOnlyList<ByteRange> ranges = _table.WholeRanges(index);
        string value = record.Kind == PacketKind.Read ? Counts.Of(record.FrameCount, "frame", "frames") : "not read";
        var node = new Node("Packet", whole.Offset, whole.Length, NodeValue.Text(value))
        {
            Kind = NodeKind.Frame,
            Description = ProExplain.Packet,
            Topic = Topics.WmaPackets,
            Ranges = ranges.Count > 1 ? ranges : null,
        };

        if (!record.HasHeader)
        {
            return node;
        }

        int bits = (int)Math.Min(_p.PacketBits, whole.Length * 8);
        FramePart[] packet = [new FramePart(index, 0, bits)];
        Node At(string name, long start, long end, NodeValue value, string description, string? topic = null) =>
            BitNodes.Make(_table, packet, start, end, name, value, description, topic);

        int header = _p.HeaderBits;
        node.Add(At("Packet Sequence Number", 0, 4, NodeValue.Integer(record.Sequence), ProExplain.SequenceNumber, Topics.WmaPackets));
        if (isLossless)
        {
            node.Add(At("Has Seekable Frame in Packet", 4, 5, NodeValue.Boolean(record.IsSeekable), LosslessExplain.SeekableFrameInPacket, Topics.WmaLosslessSubframes));
            node.Add(At("Is Spliced", 5, 6, NodeValue.Boolean(record.IsSpliced), LosslessExplain.Spliced));
        }
        else
        {
            node.Add(At("Unused", 4, 6, NodeValue.Integer(0), ProExplain.UnusedBits));
        }

        node.Add(At("Bits of Previous Frame", 6, header, NodeValue.Integer(record.Count, "bits"), ProExplain.BitsOfPreviousFrame, Topics.WmaPackets));
        if (record.Kind != PacketKind.Read)
        {
            return node;
        }

        if (record.Count != 0)
        {
            node.Add(At("Frame End", header, header + record.Count, NodeValue.Integer(record.Count, "bits"), ProExplain.FrameEnd, Topics.WmaPackets));
        }

        for (int i = 0; i < record.FrameCount && record.FirstFrame + i < data.Frames.Count; i++)
        {
            FrameRecord frame = data.Frames[record.FirstFrame + i];
            FramePart first = data.Parts[frame.FirstPart];
            if (first.Packet != index)
            {
                continue;
            }

            bool isWhole = frame.PartCount == 1;
            string text = frame.Fault == 0 ? Counts.Of(frame.Subframes, "subframe", "subframes") : "invalid";
            node.Add(At(isWhole ? "Frame Data" : "Frame Start", first.StartBit, first.StartBit + first.Bits, NodeValue.Text(text),
                isWhole ? ProExplain.FrameData : ProExplain.FrameStart, Topics.WmaPackets));
        }

        if (record.TailBits > 0)
        {
            node.Add(record.IsTailUnfinished
                ? At("Unfinished Frame", record.TailStart, record.TailStart + record.TailBits, NodeValue.Integer(record.TailBits, "bits"), ProExplain.UnfinishedFrame, Topics.WmaPackets)
                : At("Padding", record.TailStart, record.TailStart + record.TailBits, NodeValue.Integer(record.TailBits, "bits"), ProExplain.Padding));
        }

        return node;
    }
}
