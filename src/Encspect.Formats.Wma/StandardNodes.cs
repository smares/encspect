using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Builds the nodes of the superframes and frames from the stream's records, when they are read.
internal sealed class StandardNodes
{
    private readonly StandardStream _stream;
    private readonly PartsMap _map;

    public StandardNodes(StandardStream stream)
    {
        _stream = stream;
        _map = new PartsMap(Superframe);
    }

    private StandardParameters P => _stream.Parameters;

    private MappedBytes Superframe(int index)
    {
        SuperframeRecord record = _stream.Superframes[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _stream.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        return PartsMap.Mapped(record.Length, ranges, record.Offset);
    }

    // The whole superframe: its header, the end of the frame that began before it, the frames that lie in
    // it and the start of the one that goes on.
    public Node SuperframeNode(int index)
    {
        SuperframeRecord record = _stream.Superframes[index];
        IReadOnlyList<ByteRange> ranges = record.RangeIndex >= 0 ? _stream.Ranges[record.RangeIndex] : [new ByteRange(record.Offset, record.Length)];
        long end = ranges.Count > 0 ? ranges[^1].End : record.Offset + record.Length;
        string value = record.Has(SuperframeRecord.Skipped) ? "not read"
            : record.Has(SuperframeRecord.Continues) ? "middle of a frame"
            : $"{Count(record.FrameCount, "frame", "frames")}";
        return new Node("Superframe", record.Offset, end - record.Offset, value, () => SuperframeChildren(index))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges.Count > 1 ? ranges : null,
            Description = Explain.Superframe,
            Topic = Topics.WmaSuperframes,
        };
    }

    private List<Node> SuperframeChildren(int index)
    {
        SuperframeRecord record = _stream.Superframes[index];
        var nodes = new List<Node>();
        FramePart[] whole = [new FramePart(index, 0, record.Length * 8)];
        Node Field(int start, int end, string name, NodeValue? value, string description, string? topic = null) =>
            _map.Field(whole, start, Math.Min(end, record.Length * 8), name, value, description, topic);

        if (record.Has(SuperframeRecord.Skipped) && !record.Has(SuperframeRecord.HasHeader))
        {
            return nodes;
        }

        if (record.Has(SuperframeRecord.HasHeader))
        {
            nodes.Add(Field(0, 4, "Superframe Index", NodeValue.Integer(record.Index), Explain.SuperframeIndex, Topics.WmaSuperframes));
            nodes.Add(Field(4, 8, "Frames in Superframe", NodeValue.Integer(record.RawFrames), Explain.FramesInSuperframe, Topics.WmaSuperframes));
            if (!record.Has(SuperframeRecord.Continues) && !record.Has(SuperframeRecord.Skipped))
            {
                int offsetBits = P.ByteOffsetBits + 3;
                nodes.Add(Field(8, 8 + offsetBits, "Last Frame Bit Offset", NodeValue.Integer(record.BitOffset, "bits"), Explain.LastFrameBitOffset, Topics.WmaSuperframes));
                int header = 8 + offsetBits;
                if (record.BitOffset > 0 && header + record.BitOffset <= record.Length * 8)
                {
                    nodes.Add(Field(header, header + record.BitOffset, "Frame End", NodeValue.Integer(record.BitOffset, "bits"), Explain.FrameEnd, Topics.WmaSuperframes));
                }
            }
            else if (record.Has(SuperframeRecord.Continues))
            {
                nodes.Add(Field(8, record.Length * 8, "Frame Middle", NodeValue.Integer((record.Length - 1) * 8L, "bits"), Explain.FrameMiddle, Topics.WmaSuperframes));
                return nodes;
            }
            else
            {
                return nodes;
            }
        }

        // The frames that start in this superframe, from where the first begins.
        int first = record.FirstFrame + (record.Has(SuperframeRecord.Straddles) ? 1 : 0);
        for (int i = first; i < record.FirstFrame + record.FrameCount; i++)
        {
            FrameRecord frame = _stream.Frames[i];
            string value = frame.Status == 0 ? Count(frame.BlockCount, "block", "blocks") : "invalid";
            nodes.Add(Field(frame.StartBit, frame.EndBit, "Frame Data", value, Explain.FrameData, Topics.WmaSuperframes));
        }

        if (record.Has(SuperframeRecord.HasHeader))
        {
            int start = record.FrameEnd & ~7;
            int bits = record.Length * 8 - start;
            if (record.ReservoirOut > 0 && bits > 0)
            {
                nodes.Add(Field(start, record.Length * 8, "Bit Reservoir", NodeValue.Integer(record.ReservoirOut, "bytes", "byte"), Explain.BitReservoir, Topics.WmaBitReservoir));
            }
        }
        else if (record.FrameEnd < record.Length * 8)
        {
            int start = (record.FrameEnd + 7) & ~7;
            if (start < record.Length * 8)
            {
                nodes.Add(Field(start, record.Length * 8, "Padding", NodeValue.Integer(record.Length - (start / 8), "bytes", "byte"), Explain.SuperframePadding));
            }
        }

        return nodes;
    }

    // A frame, with its blocks. It may begin in one superframe and end in the next, and then lies in the
    // ranges of both.
    public Node FrameNode(int index)
    {
        FrameRecord frame = _stream.Frames[index];
        List<FramePart> parts = PartsOf(frame);
        (long offset, long length, IReadOnlyList<ByteRange>? ranges) = _map.Cover(parts, frame.StartBit, frame.EndBit);
        string value = frame.Status == 0
            ? $"{Count(frame.BlockCount, "block", "blocks")}, {frame.EndBit - frame.StartBit} bits"
            : $"invalid: {StandardStream.StatusText((FrameStatus)frame.Status)}";
        return new Node("Frame", offset, length, value, () => FrameChildren(index, parts))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = Explain.Frame,
            Topic = Topics.WmaFrames,
        };
    }

    // The file offset of the first byte of a frame.
    public long FrameOffset(int index)
    {
        FrameRecord frame = _stream.Frames[index];
        SuperframeRecord record = _stream.Superframes[frame.Superframe];
        if (record.RangeIndex < 0 && frame.PartCount == 1)
        {
            return record.Offset + (frame.StartBit / 8);
        }

        return _map.Cover(PartsOf(frame), frame.StartBit, Math.Max(frame.StartBit + 1, frame.EndBit)).Offset;
    }

    private List<FramePart> PartsOf(FrameRecord frame)
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
        FrameRecord frame = _stream.Frames[index];
        var nodes = new List<Node>();
        for (int i = 0; i < frame.BlockCount; i++)
        {
            nodes.Add(BlockNode(parts, _stream.Blocks[frame.FirstBlock + i], frame.Status != 0 && i == frame.BlockCount - 1));
        }

        return nodes;
    }

    private Node BlockNode(List<FramePart> parts, BlockRecord block, bool failed)
    {
        (long offset, long length, IReadOnlyList<ByteRange>? ranges) = _map.Cover(parts, block.Start, block.End);
        string value = $"{1 << block.BlockBits} samples{(block.IsMidSide ? ", mid/side" : string.Empty)}{(block.CodedChannels == 0 ? ", not coded" : string.Empty)}{(failed ? ", invalid" : string.Empty)}";
        var node = new Node("Block", offset, length, value) { Ranges = ranges, Description = Explain.Block, Topic = Topics.WmaFrames };
        StandardParameters p = P;
        Node Field(int start, int end, string name, NodeValue? v, string description, string? topic = null) =>
            _map.Field(parts, start, end, name, v, description, topic);

        if (p.UsesVariableBlockLength)
        {
            int bits = StandardParameters.Log2(p.BlockSizeCount - 1) + 1;
            int at = block.Start;
            if ((block.Flags & BlockRecord.SentPreviousAndCurrent) != 0)
            {
                node.Add(Field(at, at + bits, "Previous Block Size", NodeValue.Integer(1 << block.PreviousBits, "samples"), Explain.PreviousBlockSize, Topics.WmaFrames));
                node.Add(Field(at + bits, at + (2 * bits), "Block Size", NodeValue.Integer(1 << block.BlockBits, "samples"), Explain.BlockSize, Topics.WmaFrames));
                at += 2 * bits;
            }

            node.Add(Field(at, at + bits, "Next Block Size", NodeValue.Integer(1 << block.NextBits, "samples"), Explain.NextBlockSize, Topics.WmaFrames));
        }

        if ((block.Flags & BlockRecord.HasStereoBit) != 0)
        {
            node.Add(Field(block.LengthFieldsEnd, block.StereoEnd, "Mid/Side Stereo", NodeValue.Boolean(block.IsMidSide), Explain.MidSideStereo, Topics.MidSideStereo));
        }

        for (int ch = 0; ch < p.Channels; ch++)
        {
            int at = block.StereoEnd + ch;
            node.Add(Field(at, at + 1, $"Channel {ch + 1} Coded", NodeValue.Boolean(block.IsCoded(ch)), Explain.ChannelCoded, Topics.WmaFrames));
        }

        if ((block.Flags & BlockRecord.HasGain) == 0)
        {
            return node;
        }

        node.Add(Field(block.CodedEnd, block.GainEnd, "Total Gain", NodeValue.Integer(block.TotalGain), Explain.TotalGain, Topics.WmaFrames));
        if (p.UsesNoiseCoding)
        {
            int noise = block.NoiseBands0 + block.NoiseBands1;
            int bands = p.ExponentHighSizes[p.FrameLengthBits - block.BlockBits] * block.CodedChannels;
            node.Add(Field(block.GainEnd, block.NoiseFlagsEnd, "Noise Substitution", NodeValue.Text($"{noise} of {bands} high bands"), Explain.NoiseSubstitution, Topics.NoiseSubstitution));
            if (block.NoiseGainsEnd > block.NoiseFlagsEnd)
            {
                node.Add(Field(block.NoiseFlagsEnd, block.NoiseGainsEnd, "Noise Levels", NodeValue.Integer(noise, "bands", "band"), Explain.NoiseLevels, Topics.NoiseSubstitution));
            }
        }

        if ((block.Flags & BlockRecord.ExponentsFlagRead) != 0)
        {
            node.Add(Field(block.NoiseGainsEnd, block.ReuseEnd, "Exponents Sent", NodeValue.Boolean((block.Flags & BlockRecord.ExponentsSent) != 0), Explain.ExponentsSent, Topics.WmaFrames));
        }

        int previous = block.ReuseEnd;
        for (int ch = 0; ch < p.Channels; ch++)
        {
            int end = ch == 0 ? block.Exponents0End : block.Exponents1End;
            if (block.IsCoded(ch) && end > previous)
            {
                string text = p.UsesExponentVlc ? "scale factor codes" : "line spectral pairs";
                node.Add(Field(previous, end, $"Exponents (channel {ch + 1})", text, p.UsesExponentVlc ? Explain.ExponentsVlc : Explain.ExponentsLsp, Topics.WmaFrames));
            }

            previous = Math.Max(previous, end);
        }

        for (int ch = 0; ch < p.Channels; ch++)
        {
            int end = ch == 0 ? block.Coefficients0End : block.Coefficients1End;
            if (block.IsCoded(ch) && end > previous)
            {
                int nonZero = ch == 0 ? block.NonZero0 : block.NonZero1;
                int last = ch == 0 ? block.Last0 : block.Last1;
                node.Add(Field(previous, end, $"Coefficients (channel {ch + 1})", NodeValue.Text($"{nonZero} non-zero, last at {last}"), Explain.Coefficients, Topics.WmaFrames));
            }

            previous = Math.Max(previous, end);
        }

        return node;
    }

    internal static string Count(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
