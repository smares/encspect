using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The figures of a Windows Media Audio Voice stream: the statistics, from what the walk counted, and the bitrate series.
internal static class VoiceReport
{
    public const string CodecName = "Windows Media Audio Voice";

    public const string BitrateSeries = "The bitrate of each superframe: its bits over the time its samples last.";
    public const string BitrateOverTimeSeries = "The bitrate in each second of audio: the bits of the superframes that start in it over their duration.";

    // "N things", with the noun in the singular for one.
    public static string Count(long count, string singular, string plural) => Counts.Of(count, singular, plural);

    // "N things (P.P%)": the share of the total with one place, rounded to even on ties, 0 for a total of 0.
    public static string Share(long count, long total, string singular, string plural)
    {
        double percent = total == 0 ? 0 : Math.Round(count * 100.0 / total, 1, MidpointRounding.ToEven);
        return $"{Count(count, singular, plural)} ({percent.ToString("F1", CultureInfo.InvariantCulture)}%)";
    }

    public static Node Statistics(VoiceStream stream, AudioFormat format, double? containerDuration, Node container, out double duration)
    {
        VoiceParameters p = stream.Parameters;
        VoiceTotals t = stream.Totals;
        int rate = format.SampleRate;
        duration = rate > 0 ? t.Samples / (double)rate : 0;
        var node = new Node("Statistics", container.Offset, container.Length, Count(t.Superframes, "superframe", "superframes") + ", " + duration.ToString("F3", CultureInfo.InvariantCulture) + " s")
        {
            Kind = NodeKind.Statistics,
            Description = VoiceExplain.Statistics,
            Topic = Topics.WmaVoice,
        };

        void Add(string name, NodeValue value, string description) => node.Add(name, container.Offset, container.Length, value, description);

        Add("Packets", NodeValue.Integer(t.Packets), VoiceExplain.StatPackets);
        Add("Superframes", NodeValue.Integer(t.Superframes), VoiceExplain.StatSuperframes);
        if (t.Invalid > 0)
        {
            Add("Invalid Superframes", NodeValue.Integer(t.Invalid), VoiceExplain.StatInvalidSuperframes);
        }

        long frames = t.Superframes * VoiceParameters.FramesPerSuperframe;
        Add("Frames", NodeValue.Integer(frames), VoiceExplain.StatFrames);
        Add("Frame Length", NodeValue.Integer(VoiceParameters.FrameSamples, "samples"), VoiceExplain.StatFrameLength);
        Add("Samples", NodeValue.Integer(t.Samples, "samples"), VoiceExplain.StatSamples);
        Add("Duration", NodeValue.Decimal(duration, 3, "s"), VoiceExplain.StatDuration);
        if (containerDuration is > 0)
        {
            Add("Container Duration", NodeValue.Decimal(containerDuration.Value, 3, "s"), VoiceExplain.StatContainerDuration);
        }

        Add("Block Alignment", NodeValue.Integer(p.BlockAlign, "bytes"), VoiceExplain.StatBlockAlignment);
        Add("Packet Bytes", NodeValue.Integer(t.PacketBytes, "bytes"), VoiceExplain.StatPacketBytes);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(t.PacketBytes * 8.0 / duration / 1000, 1, "kbps"), VoiceExplain.StatAverageBitrate);
            Add("Average Superframe Bitrate", NodeValue.Decimal(t.SuperframeBits / duration / 1000, 1, "kbps"), VoiceExplain.StatAverageSuperframeBitrate);
        }

        double nominal = format.BytesPerSecond * 8.0 / 1000;
        if (nominal > 0)
        {
            Add("Nominal Bitrate", NodeValue.Decimal(nominal, 1, "kbps"), VoiceExplain.StatNominalBitrate);
        }

        if (t.Superframes > 0)
        {
            Add("Smallest Superframe", NodeValue.Decimal(t.SmallestBits / 8.0, 1, "bytes"), VoiceExplain.StatSmallestSuperframe);
            Add("Largest Superframe", NodeValue.Decimal(t.LargestBits / 8.0, 1, "bytes"), VoiceExplain.StatLargestSuperframe);
            Add("Minimum Superframe Bitrate", NodeValue.Decimal(t.SmallestBits * (rate / (double)VoiceParameters.SuperframeSamples / 1000), 1, "kbps"), VoiceExplain.StatMinimumBitrate);
            Add("Maximum Superframe Bitrate", NodeValue.Decimal(t.LargestBits * (rate / (double)VoiceParameters.SuperframeSamples / 1000), 1, "kbps"), VoiceExplain.StatMaximumBitrate);
        }

        Add("Superframes Spanning Packets", NodeValue.Integer(t.SpanningPackets), VoiceExplain.StatSpanning);
        Add("Most Superframes in a Packet", NodeValue.Integer(t.MostInPacket), VoiceExplain.StatMostInPacket);
        Add("Spillover Field", NodeValue.Integer(p.SpilloverBits, "bits"), VoiceExplain.StatSpilloverField);
        Add("Superframes with a Sample Count", NodeValue.Integer(t.WithSampleCount), VoiceExplain.StatSampleCount);
        Add("LSPs per Frame", NodeValue.Integer(p.LspCount), VoiceExplain.StatLspsPerFrame);
        Add("Superframes with Residual LSPs", Share(t.WithResidualLsps, t.Superframes, "superframe", "superframes"), VoiceExplain.StatResidualLsps);
        Add("Superframes with Additional Data", NodeValue.Integer(t.WithAdditionalData), VoiceExplain.StatAdditionalData);
        Add("Uses Postfilter", NodeValue.Boolean(p.HasPostfilter), VoiceExplain.StatPostfilter);
        Add("Pitch Range", $"{p.MinPitch} to {p.MaxPitch - 1} samples", VoiceExplain.StatPitchRange);

        if (t.IgnoredBits > 0)
        {
            Add("Ignored Bits", NodeValue.Integer(t.IgnoredBits, "bits"), VoiceExplain.StatIgnoredBits);
        }

        if (t.BitsAfterLast > 0)
        {
            Add("Bits After the Last Superframe", NodeValue.Integer(t.BitsAfterLast, "bits"), VoiceExplain.StatBitsAfterLast);
        }

        if (t.SkippedSpilloverBits > 0)
        {
            Add("Skipped Spillover Bits", NodeValue.Integer(t.SkippedSpilloverBits, "bits"), VoiceExplain.StatSkippedSpillover);
        }

        if (frames > 0)
        {
            Group("Frame Types", VoiceExplain.StatFrameTypes, "types", "type", t.FrameTypes, i => $"Type {i} ({VoiceNames.Frame(i)})", VoiceExplain.StatFrameTypeShare);
            Group("Fixed Codebooks", VoiceExplain.StatFixedCodebooks, "kinds", "kind", t.FixedCodebooks, i => VoiceNames.Capitalize(VoiceNames.FixedCodebooks[i]), VoiceExplain.StatFixedShare);
            Group("Adaptive Codebooks", VoiceExplain.StatAdaptiveCodebooks, "kinds", "kind", t.AdaptiveCodebooks, i => VoiceNames.Capitalize(VoiceNames.AdaptiveCodebooks[i]), VoiceExplain.StatAdaptiveShare);
            Group("Blocks per Frame", VoiceExplain.StatBlocksPerFrame, "sizes", "size", t.BlockCounts, i => Counts.Of(1 << i, "block", "blocks"), VoiceExplain.StatBlocksShare);
        }

        return node;

        void Group(string name, string description, string unit, string singular, long[] counts, Func<int, string> label, string shareDescription)
        {
            int kinds = counts.Count(c => c > 0);
            Node group = node.Add(name, container.Offset, container.Length, NodeValue.Integer(kinds, unit, singular), description, Topics.WmaVoiceFrames);
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0)
                {
                    group.Add(label(i), container.Offset, container.Length, Share(counts[i], frames, "frame", "frames"), shareDescription, Topics.WmaVoiceFrames);
                }
            }
        }
    }

    // The bitrate of each superframe at its first byte, and of each second of audio.
    public static IReadOnlyList<Series> SeriesOf(VoiceStream stream, VoiceNodes nodes, int rate, Node statistics)
    {
        int count = stream.Superframes;
        if (count == 0 || rate <= 0)
        {
            return [];
        }

        double[] offsets = new double[count];
        double[] bitrates = new double[count];
        var seconds = new List<(long Bits, long Samples)>();
        long before = 0;
        for (int i = 0; i < count; i++)
        {
            VoiceSuperframe sf = stream.SuperframeRecords[i];
            offsets[i] = nodes.SuperframeOffset(i);
            if (sf.Outcome != VoiceOutcome.Read)
            {
                continue;
            }

            if (sf.Samples > 0)
            {
                bitrates[i] = sf.Needed * (double)rate / sf.Samples / 1000;
            }

            long second = before / rate;
            while (seconds.Count <= second)
            {
                seconds.Add((0, 0));
            }

            (long bits, long samples) = seconds[(int)second];
            seconds[(int)second] = (bits + sf.Needed, samples + sf.Samples);
            before += sf.Samples;
        }

        var x = new List<double>();
        var y = new List<double>();
        for (int s = 0; s < seconds.Count; s++)
        {
            (long bits, long samples) = seconds[s];
            if (samples == 0)
            {
                continue;
            }

            x.Add(s);
            y.Add(Math.Round(bits * (double)rate / samples / 1000, 1, MidpointRounding.ToEven));
        }

        var perSuperframe = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates)
        {
            Description = BitrateSeries,
        };
        var perSecond = new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, x.ToArray(), y.ToArray(), statistics)
        {
            Description = BitrateOverTimeSeries,
        };
        return [perSuperframe, perSecond];
    }
}
