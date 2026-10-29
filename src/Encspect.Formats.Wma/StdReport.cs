using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The statistics and the series over everything the stream's reader counted.
internal static class StdReport
{
    public static void AddStatistics(Node statistics, Node parent, StdParameters p, StdStream stream, long samples, double duration, double? averageBitrate)
    {
        StdTotals totals = stream.Totals;

        Node Add(string name, NodeValue value, string description, Node? to = null) =>
            (to ?? statistics).Add(new Node(name, parent.Offset, parent.Length, value) { Description = description, Topic = Topics.WmaStandard });

        Add("Superframes", NodeValue.Integer(stream.Superframes.Count), StdText.StatSuperframes);
        Add("Frames", NodeValue.Integer(stream.FramesRead), StdText.StatFrames);
        if (stream.InvalidFrames > 0)
        {
            Add("Invalid Frames", NodeValue.Integer(stream.InvalidFrames), StdText.StatInvalidFrames);
        }

        Add("Frame Length", NodeValue.Integer(p.FrameLength, "samples"), StdText.StatFrameLength);
        Add("Samples", NodeValue.Integer(samples, "samples"), StdText.StatSamples);
        Add("Duration", NodeValue.Decimal(duration, 3, "s"), StdText.StatDuration);
        Add("Block Alignment", NodeValue.Integer(p.BlockAlign, "bytes"), StdText.StatBlockAlignment);
        if (averageBitrate is { } average)
        {
            Add("Average Bitrate", NodeValue.Decimal(average, 1, "kbps"), StdText.StatAverageBitrate);
        }

        if (stream.ValidFrames > 0)
        {
            Add("Smallest Frame", NodeValue.Decimal(stream.SmallestFrameBits / 8.0, 3, "bytes"), StdText.StatSmallestFrame);
            Add("Largest Frame", NodeValue.Decimal(stream.LargestFrameBits / 8.0, 3, "bytes"), StdText.StatLargestFrame);
            Add("Minimum Frame Bitrate", NodeValue.Decimal(FrameBitrate(stream.SmallestFrameBits, p), 1, "kbps"), StdText.StatMinimumFrameBitrate);
            Add("Maximum Frame Bitrate", NodeValue.Decimal(FrameBitrate(stream.LargestFrameBits, p), 1, "kbps"), StdText.StatMaximumFrameBitrate);
        }

        Add("Uses Bit Reservoir", NodeValue.Boolean(p.UsesBitReservoir), StdText.StatUsesBitReservoir);
        if (p.UsesBitReservoir)
        {
            Add("Frames per Superframe", NodeValue.Integer(stream.MaxFramesPerSuperframe), StdText.StatFramesPerSuperframe);
            Add("Frames Spanning Superframes", NodeValue.Integer(stream.SpanningFrames), StdText.StatFramesSpanning);
            Add("Largest Bit Reservoir", NodeValue.Integer(stream.LargestReservoir, "bytes"), StdText.StatLargestReservoir);
            Add("Average Bit Reservoir", NodeValue.Decimal((double)stream.ReservoirSum / stream.Superframes.Count, 1, "bytes"), StdText.StatAverageReservoir);
            if (stream.FillerFrames > 0)
            {
                Add("Frames Followed by Filler", NodeValue.Integer(stream.FillerFrames), StdText.StatFramesFollowedByFiller);
                Add("Filler Bits", NodeValue.Integer(stream.FillerBits, "bits"), StdText.StatFillerBits);
            }

            if (stream.InsideFrame > 0)
            {
                Add("Superframes Inside a Frame", NodeValue.Integer(stream.InsideFrame), StdText.StatSuperframesInsideFrame);
            }
        }

        Add("Exponent Coding", p.UsesScaleFactorExponents ? "scale factor codes" : "line spectral pairs", StdText.StatExponentCoding);
        Add("Uses Variable Block Length", NodeValue.Boolean(p.UsesVariableBlocks), StdText.StatUsesVariableBlockLength);
        Add("Uses Noise Substitution", NodeValue.Boolean(p.UsesNoiseSubstitution), StdText.StatUsesNoiseSubstitution);
        Add("Blocks", NodeValue.Integer(totals.Blocks), StdText.StatBlocks);

        int seen = totals.BySize.Count(c => c > 0);
        Node sizes = Add("Block Sizes", NodeValue.Integer(seen, "sizes"), StdText.StatBlockSizes);
        for (int k = 0; k < p.SizeCount; k++)
        {
            if (totals.BySize[k] > 0)
            {
                Add($"{p.BlockLength(k).ToString(CultureInfo.InvariantCulture)} samples", Share(totals.BySize[k], totals.Blocks), StdText.StatBlockSizes, sizes);
            }
        }

        if (p.Channels == 2 && totals.KnownSize > 0)
        {
            Add("Mid/Side Blocks", Share(totals.MidSide, totals.KnownSize), StdText.StatMidSideBlocks);
        }

        if (p.UsesNoiseSubstitution)
        {
            Add("Noise-Substituted Blocks", Share(totals.NoiseSubstituted, totals.Blocks), StdText.StatNoiseSubstitutedBlocks);
        }

        Add("Blocks without Coded Channels", NodeValue.Integer(totals.WithoutCodedChannels), StdText.StatBlocksWithoutCoded);
        Add("Blocks Reusing Exponents", NodeValue.Integer(totals.ReusingExponents), StdText.StatBlocksReusingExponents);
        if (totals.HasGain)
        {
            Add("Smallest Total Gain", NodeValue.Integer(totals.SmallestGain), StdText.StatSmallestGain);
            Add("Largest Total Gain", NodeValue.Integer(totals.LargestGain), StdText.StatLargestGain);
        }

        Add("Non-Zero Coefficients", NodeValue.Integer(totals.NonZero), StdText.StatNonZero);
        Add("Escape-Coded Coefficients", NodeValue.Integer(totals.Escapes), StdText.StatEscapes);
    }

    private static NodeValue Share(long count, long total)
    {
        double percent = Math.Round(count * 100.0 / total, 1, MidpointRounding.ToEven);
        return $"{WmaStandardParser.Count(count, "block", "blocks")} ({percent.ToString("F1", CultureInfo.InvariantCulture)}%)";
    }

    private static double FrameBitrate(long bits, StdParameters p) => bits * (p.SampleRate / 1000.0) / p.FrameLength;

    public static IReadOnlyList<Series> Series(StdParameters p, StdStream stream, Node statistics)
    {
        int frames = stream.Frames.Count;
        var offsets = new double[frames];
        var bitrates = new double[frames];
        var seconds = new List<(long Bits, long Count)>();
        ByteMap? map = null;
        int mapped = -1;
        for (int i = 0; i < frames; i++)
        {
            FrameRecord frame = stream.Frames[i];
            if (mapped != frame.StartSuperframe)
            {
                SuperframeRecord record = stream.Superframes[frame.StartSuperframe];
                map = record.RangesRef == uint.MaxValue ? null : new ByteMap(record, stream.Store);
                mapped = frame.StartSuperframe;
            }

            int first = frame.StartBit >> 3;
            offsets[i] = map is null ? stream.Superframes[frame.StartSuperframe].Offset + first : map.OffsetOf(first);
            bitrates[i] = frame.IsValid ? FrameBitrate(frame.Bits, p) : 0;
            if (frame.IsValid)
            {
                int second = (int)((long)i * p.FrameLength / p.SampleRate);
                while (seconds.Count <= second)
                {
                    seconds.Add((0, 0));
                }

                (long bits, long count) = seconds[second];
                seconds[second] = (bits + frame.Bits, count + 1);
            }
        }

        var series = new List<Series> { new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = StdText.BitrateSeries } };
        if (seconds.Count > 0)
        {
            var starts = new double[seconds.Count];
            var values = new double[seconds.Count];
            for (int s = 0; s < seconds.Count; s++)
            {
                starts[s] = s;
                (long bits, long count) = seconds[s];
                values[s] = count == 0 ? 0 : Math.Round((double)bits * p.SampleRate / ((double)count * p.FrameLength) / 1000, 1, MidpointRounding.ToEven);
            }

            series.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, values, statistics) { Description = StdText.BitrateOverTimeSeries });
        }

        if (p.UsesBitReservoir)
        {
            int count = stream.Superframes.Count;
            var x = new double[count];
            var kept = new double[count];
            for (int i = 0; i < count; i++)
            {
                SuperframeRecord record = stream.Superframes[i];
                x[i] = record.Offset;
                kept[i] = record.Kept;
            }

            series.Add(new Series("Bit Reservoir", "bytes", SeriesAxis.FrameOffset, x, kept) { Description = StdText.BitReservoirSeries });
        }

        return series;
    }
}
