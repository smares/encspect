using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The stream node of a Professional stream, its statistics, series and StreamInfo.
internal static class ProReport
{
    public const string CodecName = "Windows Media Audio Professional";

    public static void Finish(CodecContext context, Node parent, FindingCollector findings, ProStream stream, ProNodes nodes, AudioFormat format)
    {
        ProParameters p = stream.Parameters;
        ProTotals totals = stream.Totals;
        int packetCount = stream.Packets.Count;
        int frameCount = stream.Frames.Count;
        long goodFrames = totals.Frames - totals.FailedFrames;
        // What a decoder outputs: the frames' samples less those the frames cut. The first frame cuts its whole length,
        // since it only fills the overlap of the transform (FFmpeg's decoder drops it: wmaprodec.c, skip_frame).
        long samples = Math.Max(0, (goodFrames * p.SamplesPerFrame) - totals.TrimmedSamples);
        double duration = p.SampleRate > 0 ? samples / (double)p.SampleRate : 0;

        Node streamNode = parent.Add(new Node(CodecName, parent.Offset, parent.Length, ProNodes.Count(goodFrames, "frame", "frames"))
        {
            Kind = NodeKind.Stream,
            Description = ProExplain.Stream,
            Topic = Topics.WmaPro,
        });

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, streamNode, findings, []);
        Node? statistics = frameCount > 0 ? AddStatistics(streamNode, stream, samples, duration) : null;
        streamNode.Add(new Node("Packets", parent.Offset, parent.Length, ProNodes.Count(packetCount, "packet", "packets"),
            () => new BlockedChildren([], packetCount, nodes.PacketNode))
        {
            Description = ProExplain.Packets,
            Topic = Topics.WmaPro,
        });
        streamNode.Add(new Node("Frames", parent.Offset, parent.Length, ProNodes.Count(frameCount, "frame", "frames"),
            () => new BlockedChildren([], frameCount, nodes.FrameNode))
        {
            Description = ProExplain.Frames,
            Topic = Topics.WmaPro,
        });

        if (totals.TailBitsAtEnd > 0 && goodFrames > 0)
        {
            findings.AddNow(Severity.Info, "wma.frame-unfinished",
                $"The last packet ends with {totals.TailBitsAtEnd} bits after a frame whose trailer bit says another frame follows, and no packet follows to finish one; a decoder drops them.", streamNode.Children.Last());
        }

        var series = new List<Series>();
        if (frameCount > 0 && p.SampleRate > 0)
        {
            series.AddRange(MakeSeries(stream, nodes, statistics));
        }

        IReadOnlyList<Speaker>? layout = LayoutOf(p);
        context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = CodecName,
            SampleRate = format.SampleRate,
            Channels = p.Channels,
            ChannelMode = null,
            ChannelLayout = layout,
            Samples = samples,
            Duration = duration,
            AverageBitrate = duration > 0 ? totals.UsedBytes * 8.0 / duration / 1000 : null,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = series,
        });
    }

    // The speakers of the channel mask in the order of its bits, or the common layouts of one and two channels.
    internal static IReadOnlyList<Speaker>? LayoutOf(ProParameters p)
    {
        if (p.ChannelMask != 0 && System.Numerics.BitOperations.PopCount(p.ChannelMask) == p.Channels && (p.ChannelMask >> 18) == 0)
        {
            var speakers = new List<Speaker>();
            for (int bit = 0; bit < 18; bit++)
            {
                if ((p.ChannelMask & (1u << bit)) != 0)
                {
                    speakers.Add((Speaker)(bit + 1));
                }
            }

            return speakers;
        }

        return p.Channels == 1 ? ChannelLayouts.Mono : p.Channels == 2 ? ChannelLayouts.Stereo : null;
    }

    private static Node AddStatistics(Node streamNode, ProStream stream, long samples, double duration)
    {
        ProParameters p = stream.Parameters;
        ProTotals t = stream.Totals;
        long start = streamNode.Offset;
        long length = streamNode.Length;
        Node node = streamNode.Add(new Node("Statistics", start, length, $"{ProNodes.Count(t.Frames, "frame", "frames")}, {duration.ToString("F3", CultureInfo.InvariantCulture)} s")
        {
            Kind = NodeKind.Statistics,
            Description = ProExplain.Statistics,
            Topic = Topics.WmaPro,
        });

        void Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

        Add("Packets", NodeValue.Integer(t.Packets), ProExplain.StatPackets, Topics.WmaPro);
        Add("Frames", NodeValue.Integer(t.Frames), ProExplain.StatFrames, Topics.WmaPro);
        if (t.FailedFrames > 0)
        {
            Add("Invalid Frames", NodeValue.Integer(t.FailedFrames), ProExplain.StatInvalidFrames);
        }

        Add("Frame Length", NodeValue.Integer(p.SamplesPerFrame, "samples"), ProExplain.StatFrameLength, Topics.WmaPro);
        Add("Samples", NodeValue.Integer(samples, "samples", "sample"), ProExplain.StatSamples);
        if (t.TrimmedSamples > 0)
        {
            Add("Trimmed Samples", NodeValue.Integer(t.TrimmedSamples, "samples", "sample"), ProExplain.StatTrimmedSamples, Topics.WmaPro);
        }
        Add("Duration", NodeValue.Decimal(duration, 3, "s"), ProExplain.StatDuration);
        Add("Block Alignment", NodeValue.Integer(p.BlockAlign, "bytes"), ProExplain.StatBlockAlignment);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(t.UsedBytes * 8.0 / duration / 1000, 1, "kbps"), ProExplain.StatAverageBitrate);
        }

        if (t.FrameBits > 0)
        {
            double perFrame = p.SampleRate / (double)p.SamplesPerFrame / 1000;
            Add("Smallest Frame", NodeValue.Decimal(t.MinFrameBits / 8.0, 1, "bytes"), ProExplain.StatSmallestFrame);
            Add("Largest Frame", NodeValue.Decimal(t.MaxFrameBits / 8.0, 1, "bytes"), ProExplain.StatLargestFrame);
            Add("Minimum Frame Bitrate", NodeValue.Decimal(t.MinFrameBits * perFrame, 1, "kbps"), ProExplain.StatMinimumFrameBitrate);
            Add("Maximum Frame Bitrate", NodeValue.Decimal(t.MaxFrameBits * perFrame, 1, "kbps"), ProExplain.StatMaximumFrameBitrate);
        }

        Add("Frames Spanning Packets", NodeValue.Integer(t.StraddlingFrames), ProExplain.StatStraddlingFrames, Topics.WmaPro);
        Add("Most Frames in a Packet", NodeValue.Integer(t.MaxFramesInPacket), ProExplain.StatMaxFramesPerPacket);
        Add("Has Length Prefix", NodeValue.Boolean(p.HasLengthPrefix), ProExplain.StatLengthPrefix, Topics.WmaPro);
        Add("Most Subframes in a Frame", NodeValue.Integer(p.MaxSubframeCount), ProExplain.StatMaxSubframes, Topics.WmaPro);
        Add("Subframes", NodeValue.Integer(t.Subframes), ProExplain.StatSubframes, Topics.WmaPro);
        Node sizes = node.Add("Subframe Sizes", start, length, NodeValue.Integer(t.SubframesBySize.Count(c => c > 0), "sizes", "size"), ProExplain.StatSubframeSizes, Topics.WmaPro);
        for (int i = 0; i < t.SubframesBySize.Length; i++)
        {
            if (t.SubframesBySize[i] > 0)
            {
                sizes.Add($"{p.SamplesPerFrame >> i} samples", start, length, Share(t.SubframesBySize[i], t.Subframes, "subframe", "subframes"), ProExplain.StatSubframeShare);
            }
        }

        if (t.Subframes > 0)
        {
            Add("Channels per Subframe", NodeValue.Decimal(t.ChannelSubframes / (double)t.Subframes, 2), ProExplain.StatChannelsPerSubframe);
        }

        if (p.Channels > 1)
        {
            Add("Channel Groups", Share(t.TransformGroups, t.Groups, "group with a transform", "groups with a transform"), ProExplain.StatGroups, Topics.MidSideStereo);
            if (t.TransformGroups > 0)
            {
                Add("Bands Transformed", Share(t.BandsTransformed, t.BandsInTransformGroups, "band", "bands"), ProExplain.StatTransformBands, Topics.MidSideStereo);
            }

            Add("Frames with a Postprocessing Transform", NodeValue.Integer(t.PostprocessFrames), ProExplain.StatPostprocess);
        }

        if (p.HasDynamicRangeCompression)
        {
            Add("Frames with a Dynamic Range Gain", NodeValue.Integer(t.DrcFrames), ProExplain.StatDynamicRange);
        }

        Add("Trimmed Frames", NodeValue.Integer(t.TrimmedFrames), ProExplain.StatTrimmed);
        Add("Subframes with an Extended Header", NodeValue.Integer(t.ExtendedHeaders), ProExplain.StatExtendedHeaders);
        Add("Subframes with Vector Counts", NodeValue.Integer(t.VectorCountSubframes), ProExplain.StatVectorCounts);
        Add("Coded Channel Subframes", Share(t.CodedChannelSubframes, t.ChannelSubframes, "channel subframe", "channel subframes"), ProExplain.StatCodedChannels);
        Add("Scale Factors Sent", Share(t.ScalesSent, t.CodedChannelSubframes, "channel subframe", "channel subframes"), ProExplain.StatScalesSent, Topics.WmaPro);
        Add("Scale Factors as Differences", Share(t.ScalesDifferential, t.ScalesSent, "channel subframe", "channel subframes"), ProExplain.StatScalesDifferential, Topics.WmaPro);
        Add("Second Coefficient Codebook", Share(t.SecondCodebook, t.CodedChannelSubframes, "channel subframe", "channel subframes"), ProExplain.StatSecondCodebook, Topics.WmaPro);
        if (t.MinQuantStep != int.MaxValue)
        {
            Add("Smallest Quantization Step", NodeValue.Integer(t.MinQuantStep), ProExplain.StatQuantStep);
            Add("Largest Quantization Step", NodeValue.Integer(t.MaxQuantStep), ProExplain.StatQuantStep);
        }

        Add("Non-Zero Coefficients", NodeValue.Integer(t.NonZero), ProExplain.StatNonZero);
        Add("Escape-Coded Coefficients", NodeValue.Integer(t.Escapes), ProExplain.StatEscapes);
        if (t.CoefficientErrors > 0)
        {
            Add("Damaged Coefficient Codes", NodeValue.Integer(t.CoefficientErrors), ProExplain.StatCoefficientErrors);
        }

        if (p.LfeChannel >= 0)
        {
            Add("Low-Frequency Channel", NodeValue.Integer(p.LfeChannel + 1), ProExplain.StatLfe, Topics.ChannelLayouts);
        }

        return node;
    }

    internal static string Share(long count, long total, string singular, string plural) =>
        $"{ProNodes.Count(count, singular, plural)} ({Math.Round(total == 0 ? 0 : count * 100.0 / total, 1, MidpointRounding.ToEven).ToString("F1", CultureInfo.InvariantCulture)}%)";

    private static List<Series> MakeSeries(ProStream stream, ProNodes nodes, Node? anchor)
    {
        ProParameters p = stream.Parameters;
        int frameCount = stream.Frames.Count;
        var offsets = new double[frameCount];
        var bitrates = new double[frameCount];
        var seconds = new List<(long Bits, long Frames)>();
        double factor = p.SampleRate / (double)p.SamplesPerFrame / 1000;
        for (int i = 0; i < frameCount; i++)
        {
            ProFrameRecord frame = stream.Frames[i];
            long bits = frame.EndBit - frame.StartBit;
            offsets[i] = nodes.FrameOffset(i);
            bitrates[i] = frame.Status == 0 ? bits * factor : 0;
            if (frame.Status != 0)
            {
                continue;
            }

            int second = (int)(i * (long)p.SamplesPerFrame / p.SampleRate);
            while (seconds.Count <= second)
            {
                seconds.Add(default);
            }

            seconds[second] = (seconds[second].Bits + bits, seconds[second].Frames + 1);
        }

        var result = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = ProSeriesText.Bitrate },
        };

        if (seconds.Count > 0)
        {
            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Frames > 0 ? Math.Round(s.Bits * (double)p.SampleRate / (s.Frames * (double)p.SamplesPerFrame) / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            result.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = ProSeriesText.BitrateOverTime });
        }

        return result;
    }
}
