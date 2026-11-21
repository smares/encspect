using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The figures over a Lossless stream: the Statistics node and the series.
internal static class LosslessReport
{
    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", LosslessExplain.StereoLeftRight) { Topic = Topics.MidSide },
        new("Mid/side stereo", LosslessExplain.StereoMidSide) { Topic = Topics.MidSide },
    ];

    public static IReadOnlyList<CategorySeries> Categories(WmaPacketReader reader, IReadOnlyList<Series> series, Node stream, CategorySeriesBuilder sizes)
    {
        var categories = new List<CategorySeries>();
        if (series.Count == 0 || series[0].Name != "Bitrate")
        {
            return categories;
        }

        Node? statistics = stream.Children.FirstOrDefault(c => c.Kind == NodeKind.Statistics);
        if (reader.Stereo is { } stereo && FindStereo(stream) is { } node && stereo.Build(series[0], node, LosslessExplain.StereoCodingSeries, Topics.MidSide, null) is { } built)
        {
            categories.Add(built);
        }

        if (statistics?.Children.FirstOrDefault(c => c.Name == "Subframe Sizes") is { } sizeNode && sizes.Count == series[0].X.Length)
        {
            categories.Add(sizes.Build(series[0].X, series[0], sizeNode, ProExplain.SubframeSizesSeries, Topics.WmaSubframes));
        }

        return categories;
    }

    private static Node? FindStereo(Node stream) =>
        stream.Children.FirstOrDefault(c => c.Kind == NodeKind.Statistics)?.Children.FirstOrDefault(c => c.Name == "Stereo Coding");

    public static Node Statistics(LosslessParameters p, WmaPacketReader reader, LosslessTally tally, Node parent, double duration, CodecConfiguration? configuration)
    {
        long frames = reader.FramesRead;
        string value = string.Create(CultureInfo.InvariantCulture, $"{Counts.Of(frames, "frame", "frames")}, {duration:0.000} s");
        var statistics = new Node("Statistics", parent.Offset, parent.Length, NodeValue.Text(value))
        {
            Kind = NodeKind.Statistics,
            Description = ProExplain.Statistics,
            Topic = Topics.WmaLossless,
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
        Add("Sample Size", NodeValue.Integer(p.SampleSize, "bits"), LosslessExplain.StatSampleSize);
        Add("Samples", NodeValue.Integer(reader.Samples, "samples", "sample"), LosslessExplain.StatSamples);
        if (reader.LostSamples > 0)
        {
            Add("Lost Samples", NodeValue.Integer(reader.LostSamples, "samples", "sample"), StreamInfo.LostSamplesDescription, StreamInfo.LostSamplesTopic);
        }

        if (reader.CodedSamples != reader.Samples)
        {
            Add("Coded Samples", NodeValue.Integer(reader.CodedSamples, "samples", "sample"), LosslessExplain.StatCodedSamples);
        }

        if (reader.TrimmedSamples > 0)
        {
            Add("Trimmed Samples", NodeValue.Integer(reader.TrimmedSamples, "samples", "sample"), LosslessExplain.StatTrimmedSamples);
        }

        Add("Duration", NodeValue.Decimal(duration, 3, "s"), ProExplain.StatDuration);
        ContainerFigures.AddLength(statistics, parent, configuration);
        Add("Block Alignment", NodeValue.Integer(p.BlockAlignment, "bytes"), ProExplain.StatBlockAlignment);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(p.BlockAlignment * 8.0 * reader.FullPackets / duration / 1000, 1, "kbps"), ProExplain.StatAverageBitrate, Topics.WmaPackets);
        }

        if (reader.ValidFrames > 0)
        {
            Add("Smallest Frame", NodeValue.Decimal(reader.SmallestBits / 8.0, 1, "bytes"), ProExplain.StatSmallestFrame);
            Add("Largest Frame", NodeValue.Decimal(reader.LargestBits / 8.0, 1, "bytes"), ProExplain.StatLargestFrame);
            Add("Minimum Bitrate", NodeValue.Decimal(WmaPacketReader.RateOf(reader.SmallestBits, p), 1, "kbps"), ProExplain.StatMinimumFrameBitrate);
            Add("Maximum Bitrate", NodeValue.Decimal(WmaPacketReader.RateOf(reader.LargestBits, p), 1, "kbps"), ProExplain.StatMaximumFrameBitrate);
            reader.Distribution.AddTo(statistics, (name, value, description) => new Node(name, parent.Offset, parent.Length, value) { Description = description }, "Frame", "Frames", configuration?.IsVbr);
            if (duration > 0)
            {
                CompressionRatio.AddTo(statistics, (name, figure, description) => new Node(name, parent.Offset, parent.Length, figure) { Description = description }, PcmBitsPerSecond(p),
                    p.BlockAlignment * 8.0 * reader.FullPackets / duration / 1000, WmaPacketReader.RateOf(reader.SmallestBits, p), WmaPacketReader.RateOf(reader.LargestBits, p));
            }
        }

        Add("Frames Spanning Packets", NodeValue.Integer(reader.SpanningFrames), ProExplain.StatFramesSpanningPackets, Topics.WmaPackets);
        Add("Most Frames in a Packet", NodeValue.Integer(reader.MostFramesInPacket), ProExplain.StatMostFramesInPacket, Topics.WmaPackets);
        Add("Has Length Prefix", NodeValue.Boolean(p.HasLengthPrefix), ProExplain.StatHasLengthPrefix, Topics.WmaPackets);
        Add("Most Subframes in a Frame", NodeValue.Integer(p.MaxSubframes), ProExplain.StatMostSubframes, Topics.WmaSubframes);
        Add("Subframes", NodeValue.Integer(tally.Subframes), ProExplain.StatSubframes, Topics.WmaSubframes);
        Node sizeNode = Add("Subframe Sizes", NodeValue.Integer(tally.Sizes.Count(n => n > 0), "sizes", "size"), ProExplain.StatSubframeSizes, Topics.WmaSubframes);
        for (int k = 0; k < tally.Sizes.Length; k++)
        {
            if (tally.Sizes[k] > 0)
            {
                sizeNode.Add($"{p.FrameLength >> k} samples", parent.Offset, parent.Length,
                    NodeValue.Text(WmaText.Share(tally.Sizes[k], tally.Subframes, "subframe", "subframes")), ProExplain.StatSubframeSize);
            }
        }

        Add("Seekable Subframes", NodeValue.Text(WmaText.Share(tally.Seekable, tally.Subframes, "subframe", "subframes")), LosslessExplain.StatSeekable, Topics.WmaLosslessSubframes);
        Add("Raw PCM Subframes", NodeValue.Text(WmaText.Share(tally.RawPcm, tally.Subframes, "subframe", "subframes")), LosslessExplain.StatRawPcm, Topics.WmaLosslessSubframes);
        Add("Coded Channel Subframes", NodeValue.Text(WmaText.Share(tally.CodedChannels, tally.CodableChannelSubframes, "channel subframe", "channel subframes")), LosslessExplain.StatCodedChannels, Topics.WmaLosslessSubframes);
        Add("Escape-Coded Residuals", NodeValue.Integer(tally.Escapes), LosslessExplain.StatEscapes, Topics.WmaLosslessSubframes);
        Add("Transient Channel Subframes", NodeValue.Integer(tally.Transients), LosslessExplain.StatTransients, Topics.WmaLosslessSubframes);
        Add("Subframes with Zero Low Bits", NodeValue.Integer(tally.ZeroLowBitSubframes), LosslessExplain.StatZeroLowBits, Topics.WastedBits);
        reader.Stereo?.AddTo(statistics, (name, value, description, topic) => new Node(name, parent.Offset, parent.Length, value) { Description = description, Topic = topic }, LosslessExplain.StatStereoCoding, Topics.MidSide, "Frame", "Frames");

        Add("Seekable Subframes with a Recursive Filter", NodeValue.Integer(tally.RecursiveFilters), LosslessExplain.StatRecursive, Topics.WmaLosslessSubframes);
        Add("Seekable Subframes with a Multichannel Filter", NodeValue.Integer(tally.MultichannelFilters), LosslessExplain.StatMultichannel, Topics.WmaLosslessSubframes);
        if (p.HasLpcFlags)
        {
            Add("Subframes with an LPC Filter", NodeValue.Integer(tally.LpcFilters), LosslessExplain.StatLpc, Topics.WmaLosslessSubframes);
        }

        if (tally.Seekable > 0 && tally.MinFilters <= tally.MaxFilters)
        {
            string text = tally.MinFilters == tally.MaxFilters ? $"{tally.MinFilters}" : $"{tally.MinFilters} to {tally.MaxFilters}";
            Add("Cascaded Filters per Channel", NodeValue.Text(text), LosslessExplain.StatCascaded, Topics.WmaLosslessSubframes);
        }

        return statistics;
    }

    public static List<Series> Series(LosslessParameters p, WmaPacketReader reader, Node anchor)
    {
        var series = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, reader.BitrateX.ToArray(), reader.BitrateY.ToArray()) { Description = ProExplain.BitrateSeries, Concept = GraphConcept.BitDistribution, Quantity = "Bitrate" },
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

            series.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = ProExplain.BitrateOverTimeSeries, Concept = GraphConcept.BitDistribution, Quantity = "Bitrate" });
        }

        Node? ratio = anchor.Children.FirstOrDefault(c => c.Kind == NodeKind.Statistics)?.Children.FirstOrDefault(c => c.Name == CompressionRatio.Name);
        CompressionRatio.AddTo(series, PcmBitsPerSecond(p), ratio);
        return series;
    }

    private static long PcmBitsPerSecond(LosslessParameters p) => CompressionRatio.PcmBitsPerSecond(p.SampleRate, p.Channels, p.SampleSize);
}
