using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the packets, frames and subframes of a Professional stream from its records, when they are read.
internal sealed class ProNodes
{
    private readonly ProStream _stream;
    private readonly PartsMap _map;

    public ProNodes(ProStream stream)
    {
        _stream = stream;
        _map = new PartsMap(Packet);
    }

    private ProParameters P => _stream.Parameters;

    private MappedBytes Packet(int index)
    {
        ProPacketRecord record = _stream.Packets[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _stream.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        return PartsMap.Mapped(record.Length, ranges, record.Offset);
    }

    // The whole packet: its header, the end of the frame that began before it, the frames that lie in
    // it and the start of the one that goes on.
    public Node PacketNode(int index)
    {
        ProPacketRecord record = _stream.Packets[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _stream.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        long end = ranges.Count > 0 ? ranges[^1].End : record.Offset + record.Length;
        int started = FramesStartedIn(index).Count;
        string value = record.Has(ProPacketRecord.Skipped) ? "not read" : Count(started, "frame", "frames");
        return new Node("Packet", record.Offset, end - record.Offset, value, () => PacketChildren(index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = ProExplain.Packet,
            Topic = Topics.WmaPro,
        };
    }

    // The frames that begin in a packet: those it finishes, apart from the one that began before it, and the
    // first of the next packet's if that began here.
    private List<int> FramesStartedIn(int packet)
    {
        ProPacketRecord record = _stream.Packets[packet];
        var frames = new List<int>();
        for (int i = record.FirstFrame; i < record.FirstFrame + record.FrameCount; i++)
        {
            if (_stream.StartPackets[i] == packet)
            {
                frames.Add(i);
            }
        }

        // The frame that goes on in the next packet is finished there, and counted there.
        for (int next = packet + 1; next < _stream.Packets.Count && next <= packet + 1; next++)
        {
            ProPacketRecord following = _stream.Packets[next];
            if (following.FrameCount > 0 && _stream.StartPackets[following.FirstFrame] == packet)
            {
                frames.Add(following.FirstFrame);
            }
        }

        return frames;
    }

    private List<Node> PacketChildren(int index)
    {
        ProPacketRecord record = _stream.Packets[index];
        var nodes = new List<Node>();
        int headerBits = 6 + P.Log2FrameSize;
        FramePart[] whole = [new FramePart(index, 0, record.Length * 8)];
        Node Field(int start, int end, string name, NodeValue? value, string description, string? topic = null) =>
            _map.Field(whole, start, Math.Min(end, record.Length * 8), name, value, description, topic);

        if (record.Has(ProPacketRecord.Skipped) && record.Length * 8 < headerBits)
        {
            return nodes;
        }

        nodes.Add(Field(0, 4, "Packet Sequence Number", NodeValue.Integer(record.Sequence), ProExplain.PacketSequence, Topics.WmaPro));
        nodes.Add(Field(4, 6, "Unused", NodeValue.Integer(0), ProExplain.PacketUnused));
        nodes.Add(Field(6, headerBits, "Bits of Previous Frame", NodeValue.Integer(record.PreviousBits, "bits"), ProExplain.PacketPreviousBits, Topics.WmaPro));
        if (record.Has(ProPacketRecord.Skipped))
        {
            return nodes;
        }

        if (record.PreviousBits > 0)
        {
            nodes.Add(Field(headerBits, headerBits + record.PreviousBits, "Frame End", NodeValue.Integer(record.PreviousBits, "bits"), ProExplain.PacketFrameEnd, Topics.WmaPro));
        }

        foreach (int frame in FramesStartedIn(index))
        {
            ProFrameRecord frameRecord = _stream.Frames[frame];
            FramePart first = _stream.Parts[frameRecord.FirstPart];
            int start = first.StartBit + frameRecord.StartBit;
            int end = frameRecord.PartCount == 1 ? first.StartBit + frameRecord.EndBit : record.Length * 8;
            string value = frameRecord.Status == 0 ? Count(frameRecord.SubframeCount, "subframe", "subframes") : "invalid";
            string name = frameRecord.PartCount > 1 && _stream.StartPackets[frame] == index ? "Frame Start" : "Frame Data";
            nodes.Add(Field(start, end, name, value, name == "Frame Start" ? ProExplain.PacketFrameStart : ProExplain.PacketFrameData, Topics.WmaPro));
        }

        bool tailIsFrame = FramesStartedIn(index).Any(f => _stream.Frames[f].PartCount > 1 && _stream.StartPackets[f] == index && record.TailBits > 0);
        if (record.TailBits > 0 && !tailIsFrame)
        {
            bool begins = record.Has(ProPacketRecord.TailBeginsFrame);
            nodes.Add(Field(record.TailStart, record.Length * 8, begins ? "Unfinished Frame" : "Padding", NodeValue.Integer(record.TailBits, "bits"), begins ? ProExplain.PacketUnfinished : ProExplain.PacketPadding, Topics.WmaPro));
        }

        return nodes;
    }

    // A frame, with its fields and subframes. It may begin in one packet and end in the next, and then lies
    // in the ranges of both.
    public Node FrameNode(int index)
    {
        ProFrameRecord frame = _stream.Frames[index];
        List<FramePart> parts = PartsOf(frame);
        (long offset, long length, IReadOnlyList<ByteRange>? ranges) = _map.Cover(parts, frame.StartBit, frame.EndBit);
        string value = frame.Status == 0
            ? $"{Count(frame.SubframeCount, "subframe", "subframes")}, {frame.EndBit - frame.StartBit} bits"
            : $"invalid: {ProStream.StatusText((ProStatus)frame.Status)}";
        return new Node("Frame", offset, length, value, () => FrameChildren(index, parts))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = ProExplain.Frame,
            Topic = Topics.WmaPro,
        };
    }

    // The file offset of the first byte of a frame.
    public long FrameOffset(int index)
    {
        ProFrameRecord frame = _stream.Frames[index];
        return _map.Cover(PartsOf(frame), frame.StartBit, Math.Max(frame.StartBit + 1, frame.EndBit)).Offset;
    }

    private List<FramePart> PartsOf(ProFrameRecord frame)
    {
        var parts = new List<FramePart>(frame.PartCount);
        for (int i = 0; i < frame.PartCount; i++)
        {
            parts.Add(_stream.Parts[frame.FirstPart + i]);
        }

        return parts;
    }

    private List<Node> FrameChildren(int index, List<FramePart> parts)
    {
        ProFrameRecord frame = _stream.Frames[index];
        var nodes = new List<Node>();
        ProParameters p = P;
        Node Field(int start, int end, string name, NodeValue? value, string description, string? topic = null) =>
            _map.Field(parts, start, end, name, value, description, topic);

        if (p.HasLengthPrefix)
        {
            nodes.Add(Field(frame.StartBit, frame.LengthEnd, "Frame Length", NodeValue.Integer(frame.Length, "bits"), ProExplain.FrameLength, Topics.WmaPro));
        }

        Node tile = Field(frame.LengthEnd, frame.TileEnd, "Tile Header", NodeValue.Text(Count(frame.SubframeCount, "subframe", "subframes")), ProExplain.TileHeader, Topics.WmaPro);
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var lengths = new List<string>();
            for (int s = 0; s < frame.SubframeCount; s++)
            {
                ProSubframeRecord subframe = _stream.Subframes[frame.FirstSubframe + s];
                if ((subframe.ChannelMask & (1 << channel)) != 0)
                {
                    lengths.Add(subframe.Length.ToString(CultureInfo.InvariantCulture));
                }
            }

            tile.Add(new Node($"Channel {channel + 1} Subframes", tile.Offset, tile.Length, NodeValue.Text(string.Join(", ", lengths))) { Ranges = tile.Ranges, Description = ProExplain.ChannelLayout, Topic = Topics.WmaPro });
        }

        nodes.Add(tile);
        if (frame.Has(ProFrameRecord.HasPostprocess) || frame.PostEnd > frame.TileEnd)
        {
            nodes.Add(Field(frame.TileEnd, frame.PostEnd, "Postprocessing Transform", NodeValue.Boolean(frame.Has(ProFrameRecord.HasPostprocess)), ProExplain.Postprocess, Topics.WmaPro));
        }

        if (frame.Has(ProFrameRecord.HasDrc))
        {
            nodes.Add(Field(frame.PostEnd, frame.PostEnd + 8, "Dynamic Range Gain", NodeValue.Integer(frame.DrcGain), ProExplain.DynamicRange, Topics.WmaPro));
        }

        int trimStart = frame.PostEnd + (frame.Has(ProFrameRecord.HasDrc) ? 8 : 0);
        if (frame.HeaderEnd > trimStart)
        {
            nodes.Add(Field(trimStart, frame.HeaderEnd, "Trim", NodeValue.Text(frame.Has(ProFrameRecord.HasTrimInfo) ? $"{frame.TrimStart} samples at the start, {frame.TrimEnd} at the end" : "none"), ProExplain.Trim, Topics.WmaPro));
        }

        for (int s = 0; s < frame.SubframeCount; s++)
        {
            nodes.Add(SubframeNode(parts, _stream.Subframes[frame.FirstSubframe + s], frame.Status != 0 && s == frame.SubframeCount - 1));
        }

        if (frame.Status == 0)
        {
            if (frame.EndBit - 1 > frame.SubframesEnd)
            {
                nodes.Add(Field(frame.SubframesEnd, frame.EndBit - 1, "Frame Padding", NodeValue.Integer(frame.EndBit - 1 - frame.SubframesEnd, "bits"), ProExplain.FramePadding));
            }

            nodes.Add(Field(frame.EndBit - 1, frame.EndBit, "More Frames", NodeValue.Boolean(frame.Has(ProFrameRecord.More)), ProExplain.MoreFrames, Topics.WmaPro));
        }

        return nodes;
    }

    private Node SubframeNode(List<FramePart> parts, ProSubframeRecord subframe, bool failed)
    {
        ProParameters p = P;
        (long offset, long length, IReadOnlyList<ByteRange>? ranges) = _map.Cover(parts, subframe.StartBit, subframe.EndBit);
        var channelList = new List<int>();
        for (int c = 0; c < p.Channels; c++)
        {
            if ((subframe.ChannelMask & (1 << c)) != 0)
            {
                channelList.Add(c + 1);
            }
        }

        string value = $"{subframe.Length} samples at {subframe.Offset}, channel{(channelList.Count == 1 ? string.Empty : "s")} {string.Join(" ", channelList)}{(failed ? ", invalid" : string.Empty)}";
        var node = new Node("Subframe", offset, length, value) { Ranges = ranges, Description = ProExplain.Subframe, Topic = Topics.WmaPro };
        Node Field(int start, int end, string name, NodeValue? v, string description, string? topic = null) =>
            _map.Field(parts, start, end, name, v, description, topic);

        node.Add(Field(subframe.StartBit, subframe.FillEnd, "Extended Header", NodeValue.Text(subframe.Has(ProSubframeRecord.HasExtendedHeader) ? $"{subframe.FillBits} fill bits" : "none"), ProExplain.ExtendedHeader, Topics.WmaPro));
        if (subframe.TransformEnd == 0 && subframe.FlagsEnd == 0)
        {
            return node;
        }

        node.Add(Field(subframe.FillEnd, subframe.FillEnd + 1, "Reserved Bit", NodeValue.Boolean(failed && subframe.TransformEnd == subframe.FillEnd + 1 && subframe.FlagsEnd == subframe.TransformEnd), ProExplain.ReservedBit));
        if (subframe.TransformEnd > subframe.FillEnd + 1)
        {
            Node transform = Field(subframe.FillEnd + 1, subframe.TransformEnd, "Channel Transform", NodeValue.Text(Count(subframe.GroupCount, "group", "groups")), ProExplain.ChannelTransform, Topics.WmaPro);
            for (int g = 0; g < subframe.GroupCount; g++)
            {
                transform.Add(GroupNode(parts, _stream.Groups[subframe.FirstGroup + g]));
            }

            node.Add(transform);
        }

        if (subframe.FlagsEnd > subframe.TransformEnd)
        {
            var flags = new List<string>();
            for (int i = 0; i < subframe.ChannelCount; i++)
            {
                flags.Add(_stream.Channels[subframe.FirstChannel + i].Has(ProChannelRecord.Transmits) ? "yes" : "no");
            }

            node.Add(Field(subframe.TransformEnd, subframe.FlagsEnd, "Coefficients Sent", NodeValue.Text(string.Join(", ", flags)), ProExplain.CoefficientsSent, Topics.WmaPro));
        }

        if (!subframe.Has(ProSubframeRecord.HasCoefficients))
        {
            return node;
        }

        if (subframe.VectorEnd > subframe.FlagsEnd)
        {
            node.Add(Field(subframe.FlagsEnd, subframe.VectorEnd, "Vector Coefficients", subframe.Has(ProSubframeRecord.SendsVectorCounts) ? NodeValue.Text(string.Join(", ", Enumerable.Range(0, subframe.ChannelCount).Select(i => _stream.Channels[subframe.FirstChannel + i].VectorCoefficients))) : NodeValue.Text("all"), ProExplain.VectorCoefficients, Topics.WmaPro));
        }

        if (subframe.StepEnd > subframe.VectorEnd)
        {
            node.Add(Field(subframe.VectorEnd, subframe.StepEnd, "Quantization Step", NodeValue.Integer(subframe.QuantStep), ProExplain.QuantizationStep, Topics.WmaPro));
        }

        for (int i = 0; i < subframe.ChannelCount; i++)
        {
            ProChannelRecord channel = _stream.Channels[subframe.FirstChannel + i];
            if (!channel.Has(ProChannelRecord.Transmits))
            {
                continue;
            }

            string scales = channel.Has(ProChannelRecord.ScalesSent)
                ? (channel.Has(ProChannelRecord.ScalesDifferential) ? "differences from the last set" : $"a new set, step {channel.ScaleFactorStep}")
                : "as before";
            if (channel.ScaleEnd > channel.ScaleStart)
            {
                node.Add(Field(channel.ScaleStart, channel.ScaleEnd, $"Scale Factors (channel {channel.Channel + 1})", NodeValue.Text(scales), ProExplain.ScaleFactors, Topics.WmaPro));
            }

            if (channel.CoefficientEnd > channel.CoefficientStart)
            {
                string text = $"{channel.NonZero} non-zero, {channel.Escapes} escapes, codebook {(channel.Has(ProChannelRecord.SecondCodebook) ? 1 : 0)}{(channel.Has(ProChannelRecord.CoefficientError) ? ", damaged" : string.Empty)}";
                node.Add(Field(channel.CoefficientStart, channel.CoefficientEnd, $"Coefficients (channel {channel.Channel + 1})", NodeValue.Text(text), ProExplain.Coefficients, Topics.WmaPro));
            }
        }

        return node;
    }

    private Node GroupNode(List<FramePart> parts, ProGroupRecord group)
    {
        var members = new List<int>();
        for (int c = 0; c < ProParameters.MaxChannels; c++)
        {
            if ((group.ChannelMask & (1 << c)) != 0)
            {
                members.Add(c + 1);
            }
        }

        string value = $"channels {string.Join(" ", members)}, " + (group.Transform != 0 ? $"transform in {group.BandsOn} bands" : "no transform");
        return _map.Field(parts, group.Start, group.End, "Channel Group", value, ProExplain.ChannelGroup, Topics.MidSideStereo);
    }

    internal static string Count(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
