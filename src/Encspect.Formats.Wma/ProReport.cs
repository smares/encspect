using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The figures over a Professional stream (docs/design/wma-pro-format.md, "Statistics"): the Statistics node and the series.
internal static class ProReport
{
    public static Node Statistics(ProParameters p, WmaPacketReader reader, ProTally tally, Node parent, double duration)
    {
        long frames = reader.Data.Frames.Count;
        string value = string.Create(CultureInfo.InvariantCulture, $"{Counts.Of(frames, "frame", "frames")}, {duration:0.000} s");
        var statistics = new Node("Statistics", parent.Offset, parent.Length, NodeValue.Text(value))
        {
            Kind = NodeKind.Statistics,
            Description = ProExplain.Statistics,
            Topic = Topics.WmaPro,
        };

        Node Add(string name, NodeValue figure, string description, string? topic = null) =>
            statistics.Add(name, parent.Offset, parent.Length, figure, description, topic);

        Add("Packets", NodeValue.Integer(reader.Packets), ProExplain.StatPackets, Topics.WmaPackets);
        Add("Frames", NodeValue.Integer(frames), ProExplain.StatFrames);
        if (reader.InvalidFrames > 0)
        {
            Add("Invalid Frames", NodeValue.Integer(reader.InvalidFrames), ProExplain.StatInvalidFrames);
        }

        Add("Frame Length", NodeValue.Integer(p.FrameLength, "samples"), ProExplain.StatFrameLength);
        Add("Samples", NodeValue.Integer(reader.Samples, "samples", "sample"), ProExplain.StatSamples);
        if (reader.TrimmedSamples > 0)
        {
            Add("Trimmed Samples", NodeValue.Integer(reader.TrimmedSamples, "samples", "sample"), ProExplain.StatTrimmedSamples);
        }

        Add("Duration", NodeValue.Decimal(duration, 3, "s"), ProExplain.StatDuration);
        Add("Block Alignment", NodeValue.Integer(p.BlockAlignment, "bytes"), ProExplain.StatBlockAlignment);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(p.BlockAlignment * 8.0 * reader.FullPackets / duration / 1000, 1, "kbps"), ProExplain.StatAverageBitrate, Topics.WmaPackets);
        }

        if (reader.ValidFrames > 0)
        {
            Add("Smallest Frame", NodeValue.Decimal(reader.SmallestBits / 8.0, 1, "bytes"), ProExplain.StatSmallestFrame);
            Add("Largest Frame", NodeValue.Decimal(reader.LargestBits / 8.0, 1, "bytes"), ProExplain.StatLargestFrame);
            Add("Minimum Frame Bitrate", NodeValue.Decimal(WmaPacketReader.RateOf(reader.SmallestBits, p), 1, "kbps"), ProExplain.StatMinimumFrameBitrate);
            Add("Maximum Frame Bitrate", NodeValue.Decimal(WmaPacketReader.RateOf(reader.LargestBits, p), 1, "kbps"), ProExplain.StatMaximumFrameBitrate);
        }

        Add("Frames Spanning Packets", NodeValue.Integer(reader.SpanningFrames), ProExplain.StatFramesSpanningPackets, Topics.WmaPackets);
        Add("Most Frames in a Packet", NodeValue.Integer(reader.MostFramesInPacket), ProExplain.StatMostFramesInPacket, Topics.WmaPackets);
        Add("Has Length Prefix", NodeValue.Boolean(p.HasLengthPrefix), ProExplain.StatHasLengthPrefix, Topics.WmaPackets);
        Add("Most Subframes in a Frame", NodeValue.Integer(p.MaxSubframes), ProExplain.StatMostSubframes, Topics.WmaSubframes);
        Add("Subframes", NodeValue.Integer(tally.Subframes), ProExplain.StatSubframes, Topics.WmaSubframes);
        int sizes = tally.Sizes.Count(n => n > 0);
        Node sizeNode = Add("Subframe Sizes", NodeValue.Integer(sizes, "sizes", "size"), ProExplain.StatSubframeSizes, Topics.WmaSubframes);
        for (int k = 0; k < tally.Sizes.Length; k++)
        {
            if (tally.Sizes[k] > 0)
            {
                sizeNode.Add($"{p.FrameLength >> k} samples", parent.Offset, parent.Length,
                    NodeValue.Text(WmaText.Share(tally.Sizes[k], tally.Subframes, "subframe", "subframes")), ProExplain.StatSubframeSize);
            }
        }

        Add("Channels per Subframe", NodeValue.Decimal(tally.Subframes == 0 ? 0 : tally.ChannelSubframes / (double)tally.Subframes, 2), ProExplain.StatChannelsPerSubframe, Topics.WmaSubframes);
        if (p.Channels > 1)
        {
            Add("Channel Groups", NodeValue.Text(WmaText.Share(tally.GroupsWithTransform, tally.Groups, "group with a transform", "groups with a transform")), ProExplain.StatChannelGroups, Topics.WmaChannelTransforms);
        }

        if (tally.GroupsWithTransform > 0)
        {
            Add("Bands Transformed", NodeValue.Text(WmaText.Share(tally.BandsTransformed, tally.BandsOfTransformedGroups, "band", "bands")), ProExplain.StatBandsTransformed, Topics.WmaChannelTransforms);
        }

        if (p.Channels > 1)
        {
            Add("Frames with a Postprocessing Transform", NodeValue.Integer(reader.PostprocessingFrames), ProExplain.StatPostprocessingFrames);
        }

        if (p.HasDynamicRange)
        {
            Add("Frames with a Dynamic Range Gain", NodeValue.Integer(reader.DynamicRangeFrames), ProExplain.StatDynamicRangeFrames);
        }

        Add("Trimmed Frames", NodeValue.Integer(reader.TrimmedFrames), ProExplain.StatTrimmedFrames);
        Add("Subframes with an Extended Header", NodeValue.Integer(tally.ExtendedHeaders), ProExplain.StatExtendedHeaders, Topics.WmaSubframes);
        Add("Subframes with Vector Counts", NodeValue.Integer(tally.VectorCounted), ProExplain.StatVectorCounts, Topics.WmaCoefficients);
        Add("Coded Channel Subframes", NodeValue.Text(WmaText.Share(tally.CodedChannels, tally.ChannelSubframes, "channel subframe", "channel subframes")), ProExplain.StatCodedChannels, Topics.WmaCoefficients);
        Add("Scale Factors Sent", NodeValue.Text(WmaText.Share(tally.ScaleFactorsSent, tally.CodedChannels, "channel subframe", "channel subframes")), ProExplain.StatScaleFactorsSent, Topics.WmaScaleFactors);
        Add("Scale Factors as Differences", NodeValue.Text(WmaText.Share(tally.ScaleFactorDifferences, tally.ScaleFactorsSent, "channel subframe", "channel subframes")), ProExplain.StatScaleFactorDifferences, Topics.WmaScaleFactors);
        Add("Second Coefficient Codebook", NodeValue.Text(WmaText.Share(tally.SecondCodebook, tally.CodedChannels, "channel subframe", "channel subframes")), ProExplain.StatSecondCodebook, Topics.WmaCoefficients);
        if (tally.MinStep <= tally.MaxStep)
        {
            Add("Smallest Quantization Step", NodeValue.Integer(tally.MinStep), ProExplain.StatSmallestStep, Topics.WmaScaleFactors);
            Add("Largest Quantization Step", NodeValue.Integer(tally.MaxStep), ProExplain.StatLargestStep, Topics.WmaScaleFactors);
        }

        Add("Non-Zero Coefficients", NodeValue.Integer(tally.NonZero), ProExplain.StatNonZero, Topics.WmaCoefficients);
        Add("Escape-Coded Coefficients", NodeValue.Integer(tally.Escapes), ProExplain.StatEscapes, Topics.WmaCoefficients);
        if (tally.Damaged > 0)
        {
            Add("Damaged Coefficient Codes", NodeValue.Integer(tally.Damaged), ProExplain.StatDamaged, Topics.WmaCoefficients);
        }

        if (p.LowFrequencyChannel >= 0)
        {
            Add("Low-Frequency Channel", NodeValue.Integer(p.LowFrequencyChannel + 1), ProExplain.StatLowFrequencyChannel, Topics.ChannelLayouts);
        }

        return statistics;
    }

    public static List<Series> Series(ProParameters p, WmaPacketReader reader, Node anchor)
    {
        var series = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, reader.BitrateX.ToArray(), reader.BitrateY.ToArray()) { Description = ProExplain.BitrateSeries },
        };

        if (reader.LastValidSecond >= 0)
        {
            int seconds = reader.LastValidSecond + 1;
            var starts = new double[seconds];
            var rates = new double[seconds];
            for (int s = 0; s < seconds; s++)
            {
                starts[s] = s;
                int count = reader.SecondFrames[s];
                if (count > 0)
                {
                    double rate = reader.SecondBits[s] * (double)p.SampleRate / ((double)count * p.FrameLength) / 1000;
                    rates[s] = Math.Round(rate, 1, MidpointRounding.ToEven);
                }
            }

            series.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = ProExplain.BitrateOverTimeSeries });
        }

        return series;
    }
}
