using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The stream node of a Windows Media Audio version 1 or 2 stream, its statistics, series and StreamInfo.
internal static class StandardReport
{
    public static void Finish(CodecContext context, Node parent, FindingCollector findings, StandardStream stream, StandardNodes nodes, string codec, AudioFormat format, int version, IReadOnlyList<WmaEncoder.Trait> configurationTraits)
    {
        StandardParameters p = stream.Parameters;
        StandardTotals totals = stream.Totals;
        int superframeCount = stream.Superframes.Count;
        int frameCount = stream.Frames.Count;
        long goodFrames = totals.Frames - totals.FailedFrames;
        // What a decoder outputs: the first frame only fills the overlap of the transform (FFmpeg's output is one frame
        // shorter than the frames it decodes).
        long samples = Math.Max(0, goodFrames - 1) * p.FrameLength;
        double duration = p.SampleRate > 0 ? samples / (double)p.SampleRate : 0;

        Node streamNode = parent.Add(new Node(codec, parent.Offset, parent.Length, WmaStandardParser.Count(goodFrames, "frame", "frames"))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.WmaStandard,
        });

        var traits = new List<WmaEncoder.Trait>(configurationTraits);
        if (stream.FFmpegTrait(version) is { } trait)
        {
            traits.Add(trait);
        }

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, streamNode, findings, traits);
        Node? statistics = frameCount > 0 ? AddStatistics(streamNode, stream, samples, duration) : null;
        streamNode.Add(new Node("Superframes", parent.Offset, parent.Length, WmaStandardParser.Count(superframeCount, "superframe", "superframes"),
            () => new BlockedChildren([], superframeCount, nodes.SuperframeNode))
        {
            Description = Explain.Superframes,
            Topic = Topics.WmaSuperframes,
        });
        streamNode.Add(new Node("Frames", parent.Offset, parent.Length, WmaStandardParser.Count(frameCount, "frame", "frames"),
            () => new BlockedChildren([], frameCount, nodes.FrameNode))
        {
            Description = Explain.Frames,
            Topic = Topics.WmaFrames,
        });

        if (stream.Totals.TailBitsAtEnd > 0 && goodFrames > 0)
        {
            findings.AddNow(Severity.Info, "wma.reservoir-at-end",
                $"The last superframe ends with {stream.Totals.TailBitsAtEnd} bits after its last frame that no frame uses, since no superframe follows to finish a frame begun in them; a decoder drops them.", streamNode.Children.Last());
        }

        var series = new List<Series>();
        if (frameCount > 0 && p.SampleRate > 0)
        {
            series.AddRange(MakeSeries(stream, nodes, statistics));
        }

        double? bitrate = duration > 0 ? totals.UsedBytes * 8.0 / duration / 1000 : null;
        context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = format.Channels == 1 ? "Mono" : "Stereo",
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo,
            Samples = samples,
            Duration = duration,
            AverageBitrate = bitrate,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = series,
        });

        _ = version;
    }

    private static Node AddStatistics(Node streamNode, StandardStream stream, long samples, double duration)
    {
        StandardParameters p = stream.Parameters;
        StandardTotals t = stream.Totals;
        long start = streamNode.Offset;
        long length = streamNode.Length;
        Node node = streamNode.Add(new Node("Statistics", start, length, $"{WmaStandardParser.Count(t.Frames, "frame", "frames")}, {duration.ToString("F3", CultureInfo.InvariantCulture)} s")
        {
            Kind = NodeKind.Statistics,
            Description = Explain.Statistics,
            Topic = Topics.WmaStandard,
        });

        void Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

        Add("Superframes", NodeValue.Integer(t.Superframes), Explain.StatSuperframes, Topics.WmaSuperframes);
        Add("Frames", NodeValue.Integer(t.Frames), Explain.StatFrames, Topics.WmaFrames);
        if (t.FailedFrames > 0)
        {
            Add("Invalid Frames", NodeValue.Integer(t.FailedFrames), Explain.StatInvalidFrames);
        }

        Add("Frame Length", NodeValue.Integer(p.FrameLength, "samples"), Explain.StatFrameLength, Topics.WmaFrames);
        Add("Samples", NodeValue.Integer(samples, "samples", "sample"), Explain.StatSamples);
        Add("Duration", NodeValue.Decimal(duration, 3, "s"), Explain.StatDuration);
        Add("Block Alignment", NodeValue.Integer(p.BlockAlign, "bytes"), Explain.StatBlockAlignment, Topics.WmaSuperframes);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(t.UsedBytes * 8.0 / duration / 1000, 1, "kbps"), Explain.StatAverageBitrate);
        }

        if (t.FrameBits > 0)
        {
            double perFrame = p.SampleRate / (double)p.FrameLength / 1000;
            Add("Smallest Frame", NodeValue.Decimal(t.MinFrameBits / 8.0, 1, "bytes"), Explain.StatSmallestFrame);
            Add("Largest Frame", NodeValue.Decimal(t.MaxFrameBits / 8.0, 1, "bytes"), Explain.StatLargestFrame);
            Add("Minimum Frame Bitrate", NodeValue.Decimal(t.MinFrameBits * perFrame, 1, "kbps"), Explain.StatMinimumFrameBitrate);
            Add("Maximum Frame Bitrate", NodeValue.Decimal(t.MaxFrameBits * perFrame, 1, "kbps"), Explain.StatMaximumFrameBitrate);
        }

        Add("Uses Bit Reservoir", NodeValue.Boolean(p.UsesBitReservoir), Explain.StatUsesBitReservoir, Topics.WmaBitReservoir);
        if (p.UsesBitReservoir)
        {
            Add("Frames per Superframe", NodeValue.Integer(t.MaxFramesInSuperframe), Explain.StatMaxFramesPerSuperframe, Topics.WmaSuperframes);
            Add("Frames Spanning Superframes", NodeValue.Integer(t.StraddlingFrames), Explain.StatStraddlingFrames, Topics.WmaBitReservoir);
            Add("Largest Bit Reservoir", NodeValue.Integer(t.MaxReservoir, "bytes"), Explain.StatLargestReservoir, Topics.WmaBitReservoir);
            if (t.Superframes > 0)
            {
                Add("Average Bit Reservoir", NodeValue.Decimal(t.ReservoirBytes / (double)t.Superframes, 1, "bytes"), Explain.StatAverageReservoir, Topics.WmaBitReservoir);
            }

            if (t.FillerFrames > 0)
            {
                Add("Frames Followed by Filler", NodeValue.Integer(t.FillerFrames), Explain.StatFillerFrames, Topics.WmaBitReservoir);
                Add("Filler Bits", NodeValue.Integer(t.FillerBits, "bits", "bit"), Explain.StatFillerBits, Topics.WmaBitReservoir);
            }

            if (t.ContinuationSuperframes > 0)
            {
                Add("Superframes Inside a Frame", NodeValue.Integer(t.ContinuationSuperframes), Explain.StatContinuations, Topics.WmaBitReservoir);
            }
        }

        Add("Exponent Coding", p.UsesExponentVlc ? "scale factor codes" : "line spectral pairs", Explain.StatExponentCoding, Topics.WmaFrames);
        Add("Uses Variable Block Length", NodeValue.Boolean(p.UsesVariableBlockLength), Explain.StatVariableBlocks, Topics.WmaFrames);
        Add("Uses Noise Substitution", NodeValue.Boolean(p.UsesNoiseCoding), Explain.StatNoiseCoding, Topics.NoiseSubstitution);
        Add("Blocks", NodeValue.Integer(t.Blocks), Explain.StatBlocks, Topics.WmaFrames);
        Node sizes = node.Add("Block Sizes", start, length, NodeValue.Integer(t.BlocksBySize.Count(c => c > 0), "sizes", "size"), Explain.StatBlockSizes, Topics.WmaFrames);
        for (int i = 0; i < t.BlocksBySize.Length; i++)
        {
            if (t.BlocksBySize[i] > 0)
            {
                sizes.Add($"{p.FrameLength >> i} samples", start, length, Share(t.BlocksBySize[i], t.Blocks, "block", "blocks"), Explain.StatBlockSizeShare);
            }
        }

        if (p.Channels == 2 && t.StereoBlocks > 0)
        {
            Add("Mid/Side Blocks", Share(t.MidSideBlocks, t.StereoBlocks, "block", "blocks"), Explain.StatMidSideBlocks, Topics.MidSideStereo);
        }

        if (p.UsesNoiseCoding)
        {
            Add("Noise-Substituted Blocks", Share(t.NoiseBlocks, t.Blocks, "block", "blocks"), Explain.StatNoiseBlocks, Topics.NoiseSubstitution);
        }

        Add("Blocks without Coded Channels", NodeValue.Integer(t.UncodedBlocks), Explain.StatUncodedBlocks);
        Add("Blocks Reusing Exponents", NodeValue.Integer(t.ReusedExponentBlocks), Explain.StatReusedExponents);
        if (t.MinGain != int.MaxValue)
        {
            Add("Smallest Total Gain", NodeValue.Integer(t.MinGain), Explain.StatMinimumGain);
            Add("Largest Total Gain", NodeValue.Integer(t.MaxGain), Explain.StatMaximumGain);
        }

        Add("Non-Zero Coefficients", NodeValue.Integer(t.NonZero), Explain.StatNonZero);
        Add("Escape-Coded Coefficients", NodeValue.Integer(t.Escapes), Explain.StatEscapes);
        return node;
    }

    internal static string Share(long count, long total, string singular, string plural) =>
        $"{WmaStandardParser.Count(count, singular, plural)} ({Math.Round(total == 0 ? 0 : count * 100.0 / total, 1, MidpointRounding.ToEven).ToString("F1", CultureInfo.InvariantCulture)}%)";

    private static List<Series> MakeSeries(StandardStream stream, StandardNodes nodes, Node? anchor)
    {
        StandardParameters p = stream.Parameters;
        int frameCount = stream.Frames.Count;
        var offsets = new double[frameCount];
        var bitrates = new double[frameCount];
        var seconds = new List<(long Bits, long Frames)>();
        double factor = p.SampleRate / (double)p.FrameLength / 1000;
        for (int i = 0; i < frameCount; i++)
        {
            FrameRecord frame = stream.Frames[i];
            long bits = frame.EndBit - frame.StartBit;
            offsets[i] = nodes.FrameOffset(i);
            bitrates[i] = frame.Status == 0 ? bits * factor : 0;
            if (frame.Status != 0)
            {
                continue;
            }

            int second = (int)(i * (long)p.FrameLength / p.SampleRate);
            while (seconds.Count <= second)
            {
                seconds.Add(default);
            }

            seconds[second] = (seconds[second].Bits + bits, seconds[second].Frames + 1);
        }

        var result = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = SeriesText.Bitrate },
        };

        if (seconds.Count > 0)
        {
            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Frames > 0 ? Math.Round(s.Bits * (double)p.SampleRate / (s.Frames * (double)p.FrameLength) / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            result.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = SeriesText.BitrateOverTime });
        }

        if (p.UsesBitReservoir)
        {
            int count = stream.Superframes.Count;
            var x = new double[count];
            var y = new double[count];
            for (int i = 0; i < count; i++)
            {
                SuperframeRecord record = stream.Superframes[i];
                x[i] = record.Offset;
                y[i] = record.ReservoirOut;
            }

            result.Add(new Series("Bit Reservoir", "bytes", SeriesAxis.FrameOffset, x, y) { Description = SeriesText.BitReservoir });
        }

        return result;
    }
}
