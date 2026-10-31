using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Flac;

// The nodes of the report built from the entries when something reads them: a frame's header, its subframes and its CRC-16. Functions only of
// the records, so the findings can build the node they point to.
internal static class Nodes
{
    public static Node Entry(Records r, int index) => r.Entries[index].Kind == EntryKind.Skipped ? Skipped(r, index) : Frame(r, index);

    public static Node Skipped(Records r, int index)
    {
        Entry e = r.Entries[index];
        return new Node("Skipped Data", e.Offset, e.Length, NodeValue.Integer(e.Length, "bytes")) { Description = Explain.SkippedData, Topic = Topics.Frames };
    }

    public static Node Frame(Records r, int index)
    {
        Entry e = r.Entries[index];
        return new Node("Frame", e.Offset, e.Length, NodeValue.Integer(e.BlockSize, "samples"), () => FrameChildren(r, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.Frames,
            Ranges = r.Ranges.GetValueOrDefault(index),
        };
    }

    // The MD5 field of a STREAMINFO block, equal to the node the parser builds, for a finding of the decoder.
    public static Node Md5Field(MappedBytes configuration) =>
        Node.FromMapped("MD5 Signature", configuration, StreamInfoBlock.Md5Offset, StreamInfoBlock.Md5Offset + StreamInfoBlock.Md5Length, null, description: Explain.Md5, topic: Topics.Verification);

    // The frame's node, or the field of its header the name gives.
    public static Node HeaderField(Records r, int index, string name) => Frame(r, index).Children.First(c => c.Name == "Frame Header").Children.First(c => c.Name == name);

    private static int HeaderLength(in Entry e) => 4 + e.NumberLength + (e.BlockSizeCode == 6 ? 1 : e.BlockSizeCode == 7 ? 2 : 0) + (e.RateCode == 12 ? 1 : e.RateCode is 13 or 14 ? 2 : 0) + 1;

    private static IReadOnlyList<Node> FrameChildren(Records r, int index)
    {
        Entry e = r.Entries[index];
        bool hasRanges = (e.Status & EntryStatus.HasRanges) != 0;
        int size = hasRanges ? (int)r.Ranges[index].Sum(x => x.Length) : (int)e.Length;
        FieldMap map = hasRanges ? FieldMap.Of(r.Ranges[index], size) : FieldMap.At(e.Offset);
        int headerLength = Math.Min(HeaderLength(e), size);
        var children = new List<Node> { Header(r, e, map, headerLength) };
        bool isCut = (e.Status & EntryStatus.Cut) != 0;
        int footer = isCut ? 0 : 2;
        int end = size - footer;
        if (e.Detail >= 0 && (e.Status & (EntryStatus.Unreadable | EntryStatus.ExtentFound)) == 0)
        {
            FrameDetail detail = r.Details[e.Detail];
            for (int c = 0; c < detail.Subframes.Length; c++)
            {
                children.Add(Subframe(r, e, map, detail.Subframes[c], c));
            }
        }
        else if (end > headerLength)
        {
            bool isWhole = !isCut && (e.Status & (EntryStatus.Unreadable | EntryStatus.ExtentFound)) == 0;
            children.Add(map.Bytes(isWhole ? "Subframes" : "Frame Data", headerLength, end - headerLength, NodeValue.Integer(end - headerLength, "bytes"),
                isWhole ? Explain.UndetailedSubframes : isCut ? Explain.CutFrameData : Explain.UnreadFrameData, Topics.Subframes));
        }

        if (!isCut && size >= headerLength + 2)
        {
            children.Add(map.Bytes("CRC-16", size - 2, 2, NodeValue.Hex(e.Crc16, 4), Explain.Crc16, Topics.Crc));
            children.Add(map.Bytes("Computed CRC-16", size - 2, 2, NodeValue.Hex(e.ComputedCrc16, 4), Explain.ComputedCrc16, Topics.Crc));
            children.Add(map.Bytes("IsCrc16Valid", size - 2, 2, NodeValue.Boolean(e.Crc16 == e.ComputedCrc16), Explain.IsCrc16Valid, Topics.Crc));
        }

        return children;
    }

    private static Node Header(Records r, in Entry e, FieldMap map, int length)
    {
        Node header = map.Bytes("Frame Header", 0, length, null, Explain.FrameHeader, Topics.Frames);
        header.Add(map.Bits("Sync Code", 0, 15, NodeValue.Hex(0x7FFC, 4), Explain.SyncCode, Topics.Frames));
        header.Add(map.Bits("Blocking Strategy", 15, 16, e.IsVariable ? "variable" : "fixed", e.IsVariable ? Explain.StrategyVariable : Explain.StrategyFixed, Topics.BlockingStrategy));
        header.Add(map.Bits("Block Size", 16, 20, NodeValue.Integer(e.BlockSize, "samples"), BlockSizeDescription(e.BlockSizeCode), Topics.Frames));
        int rate = r.RateOf(e);
        header.Add(map.Bits("Sample Rate", 20, 24, rate == 0 ? "unknown" : NodeValue.Integer(rate, "Hz"), e.RateCode switch { 0 => Explain.SampleRateStreamInfo, 12 or 13 or 14 => Explain.SampleRateStated, _ => Explain.SampleRateCoded }, Topics.Frames));
        header.Add(map.Bits("Channel Assignment", 24, 28, ChannelName(e.ChannelCode), e.ChannelCode < 8 ? Explain.AssignmentIndependent : Explain.AssignmentDecorrelated, Topics.ChannelAssignment));
        int bits = r.BitsOf(e);
        header.Add(map.Bits("Sample Size", 28, 31, bits == 0 ? "unknown" : NodeValue.Integer(bits, "bits"), e.SizeCode == 0 ? Explain.SampleSizeStreamInfo : Explain.SampleSizeCoded, Topics.Frames));
        header.Add(map.Bits("Reserved Bit", 31, 32, NodeValue.Integer(0), Explain.ReservedBit, Topics.Frames));
        header.Add(map.Bytes(e.IsVariable ? "Sample Number" : "Frame Number", 4, e.NumberLength, NodeValue.Integer(e.Number), e.IsVariable ? Explain.SampleNumber : Explain.FrameNumber, Topics.BlockingStrategy));
        int at = 4 + e.NumberLength;
        if (e.BlockSizeCode is 6 or 7)
        {
            int count = e.BlockSizeCode == 6 ? 1 : 2;
            header.Add(map.Bytes("Uncommon Block Size", at, count, NodeValue.Integer(e.BlockSize, "samples"), Explain.UncommonBlockSize, Topics.Frames));
            at += count;
        }

        if (e.RateCode is 12 or 13 or 14)
        {
            int count = e.RateCode == 12 ? 1 : 2;
            header.Add(map.Bytes("Uncommon Sample Rate", at, count, NodeValue.Integer(e.StatedRate, "Hz"), Explain.UncommonSampleRate, Topics.Frames));
            at += count;
        }

        if (at < length)
        {
            header.Add(map.Bytes("CRC-8", at, 1, NodeValue.Hex(e.Crc8, 2), e.Crc8 == e.ComputedCrc8 ? Explain.Crc8 : Explain.Crc8Differs, Topics.Crc));
        }

        return header;
    }

    private static string BlockSizeDescription(int code) => code switch
    {
        1 => Explain.BlockSize192,
        6 => Explain.BlockSizeStated8,
        7 => Explain.BlockSizeStated16,
        >= 2 and <= 5 => Explain.BlockSizeLong,
        _ => Explain.BlockSizePowerOfTwo,
    };

    public static string ChannelName(int code) => code switch
    {
        0 => "mono",
        1 => "stereo",
        < 8 => $"{code + 1} channels",
        8 => "left/side stereo",
        9 => "side/right stereo",
        10 => "mid/side stereo",
        _ => "reserved",
    };

    private static string TypeName(SubframeType type) => type switch
    {
        SubframeType.Constant => "constant",
        SubframeType.Verbatim => "verbatim",
        SubframeType.Fixed => "fixed predictor",
        _ => "linear predictor",
    };

    private static Node Subframe(Records r, in Entry e, FieldMap map, in SubframeRecord s, int channel)
    {
        string value = s.Type switch
        {
            SubframeType.Fixed => $"fixed predictor, order {s.Order}",
            SubframeType.Lpc => $"linear predictor, order {s.Order}",
            _ => TypeName(s.Type),
        };
        int start = s.StartBit;
        Node node = map.Bits("Subframe", start, s.EndBit, value, Explain.Subframe, Topics.Subframes);
        node.Add(map.Bits("Reserved Bit", start, start + 1, NodeValue.Integer(0), Explain.ReservedBit, Topics.Subframes));
        node.Add(map.Bits("Subframe Type", start + 1, start + 7, TypeName(s.Type), Explain.SubframeType, Topics.Subframes));
        if (s.Type is SubframeType.Fixed or SubframeType.Lpc)
        {
            node.Add(map.Bits("Predictor Order", start + 1, start + 7, NodeValue.Integer(s.Order), Explain.PredictorOrder, Topics.LinearPrediction));
        }

        int header = start + 8 + s.WastedBits;
        node.Add(map.Bits("Wasted Bits", start + 7, header, NodeValue.Integer(s.WastedBits, "bits"), s.WastedBits > 0 ? Explain.WastedBits : Explain.NoWastedBits, Topics.WastedBits));
        int at = header;
        switch (s.Type)
        {
            case SubframeType.Constant:
                node.Add(map.Bits("Constant Sample", at, at + s.SampleBits, NodeValue.Integer(s.Constant), Explain.ConstantSample, Topics.Subframes));
                return node;
            case SubframeType.Verbatim:
                node.Add(map.Bits("Samples", at, s.EndBit, NodeValue.Integer(e.BlockSize, "samples"), Explain.VerbatimSamples, Topics.Subframes));
                return node;
        }

        if (s.Order > 0)
        {
            node.Add(map.Bits("Warm-up Samples", at, at + (s.Order * s.SampleBits), NodeValue.Integer(s.Order, "samples"), Explain.WarmUpSamples, Topics.LinearPrediction));
        }

        at += s.Order * s.SampleBits;
        if (s.Type == SubframeType.Lpc)
        {
            node.Add(map.Bits("Coefficient Precision", at, at + 4, NodeValue.Integer(s.Precision, "bits"), Explain.CoefficientPrecision, Topics.LinearPrediction));
            node.Add(map.Bits("Coefficient Shift", at + 4, at + 9, NodeValue.Integer(s.Shift, "bits"), Explain.CoefficientShift, Topics.LinearPrediction));
            ReadOnlySpan<byte> stored = r.Store.Span(s.StoredAt, s.Order * 2);
            var list = new List<string>(s.Order);
            for (int i = 0; i < s.Order; i++)
            {
                list.Add(((short)BinaryPrimitives.ReadUInt16BigEndian(stored[(i * 2)..])).ToString(CultureInfo.InvariantCulture));
            }

            node.Add(map.Bits("Coefficients", at + 9, at + 9 + (s.Order * s.Precision), string.Join(", ", list), Explain.Coefficients, Topics.LinearPrediction));
        }

        if (s.ResidualMethod >= 0)
        {
            node.Add(Residual(r, map, s));
        }

        return node;
    }

    private static Node Residual(Records r, FieldMap map, in SubframeRecord s)
    {
        int start = s.ResidualStartBit;
        Node residual = map.Bits("Residual", start, s.EndBit, null, Explain.Residual, Topics.RiceCoding);
        residual.Add(map.Bits("Residual Coding Method", start, start + 2, s.ResidualMethod == 0 ? "Rice" : "Rice with 5-bit parameters", s.ResidualMethod == 0 ? Explain.MethodRice : Explain.MethodRice2, Topics.RiceCoding));
        residual.Add(map.Bits("Partition Order", start + 2, start + 6, NodeValue.Integer(s.PartitionOrder), Explain.PartitionOrder, Topics.RiceCoding));
        long partitions = 1L << s.PartitionOrder;
        Node list = map.Bits("Partitions", start + 6, s.EndBit, NodeValue.Integer(partitions, "partitions", "partition"), Explain.Partitions, Topics.RiceCoding);
        int parameterBits = s.ResidualMethod == 0 ? 4 : 5;
        int coefficientBytes = s.Type == SubframeType.Lpc ? s.Order * 2 : 0;
        bool hasMore = partitions > s.Partitions;
        ReadOnlySpan<byte> stored = s.Partitions > 0 ? r.Store.Span(s.StoredAt, coefficientBytes + (s.Partitions * 6) + (hasMore ? 4 : 0)) : default;
        for (int i = 0; i < s.Partitions; i++)
        {
            ReadOnlySpan<byte> p = stored[(coefficientBytes + (i * 6))..];
            int partitionStart = BinaryPrimitives.ReadInt32BigEndian(p);
            int parameter = p[4];
            bool isEscaped = p[5] != 0;
            int partitionEnd = i + 1 < s.Partitions || hasMore ? BinaryPrimitives.ReadInt32BigEndian(p[6..]) : s.EndBit;
            Node partition = map.Bits("Partition", partitionStart, Math.Max(partitionStart, partitionEnd), isEscaped ? $"escaped, {parameter}-bit samples" : $"Rice parameter {parameter}",
                isEscaped ? Explain.PartitionEscaped : Explain.PartitionRice, Topics.RiceCoding);
            partition.Add(map.Bits(isEscaped ? "Escape Code" : "Rice Parameter", partitionStart, partitionStart + parameterBits, NodeValue.Integer(isEscaped ? (1 << parameterBits) - 1 : parameter),
                isEscaped ? Explain.EscapeCode : Explain.RiceParameter, Topics.RiceCoding));
            if (isEscaped)
            {
                partition.Add(map.Bits("Escaped Sample Bits", partitionStart + parameterBits, partitionStart + parameterBits + 5, NodeValue.Integer(parameter, "bits"), Explain.EscapedSampleBits, Topics.RiceCoding));
            }

            list.Add(partition);
        }

        if (partitions > s.Partitions)
        {
            int from = s.Partitions > 0 ? BinaryPrimitives.ReadInt32BigEndian(stored[(coefficientBytes + (s.Partitions * 6))..]) : start + 6;
            list.Add(map.Bits("Further Partitions", from, s.EndBit, NodeValue.Integer(partitions - s.Partitions, "partitions", "partition"), Explain.FurtherPartitions, Topics.RiceCoding));
        }

        residual.Add(list);
        if (s.MaxParameter >= 0)
        {
            residual.Add(map.Bits("Rice Parameter Range", start + 6, s.EndBit, s.MinParameter == s.MaxParameter ? $"{s.MinParameter}" : $"{s.MinParameter} to {s.MaxParameter}", Explain.ParameterRange, Topics.RiceCoding));
        }

        if (s.EscapedPartitions > 0)
        {
            residual.Add(map.Bits("Escaped Partitions", start + 6, s.EndBit, NodeValue.Integer(s.EscapedPartitions, "partitions", "partition"), Explain.EscapedPartitions, Topics.RiceCoding));
        }

        return residual;
    }
}
