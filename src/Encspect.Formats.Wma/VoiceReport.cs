using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// The stream node of a Voice stream, its statistics, series and StreamInfo.
internal static class VoiceReport
{
    public const string CodecName = "Windows Media Audio Voice";

    public static void Finish(CodecContext context, Node parent, FindingCollector findings, VoiceStream stream, VoiceNodes nodes, AudioFormat format)
    {
        VoiceParameters p = stream.Parameters;
        VoiceTotals totals = stream.Totals;
        int packetCount = stream.Packets.Count;
        int superframeCount = stream.Superframes.Count;
        long samples = totals.Samples;
        double duration = p.SampleRate > 0 ? samples / (double)p.SampleRate : 0;

        Node streamNode = parent.Add(new Node(CodecName, parent.Offset, parent.Length, ProNodes.Count(totals.Superframes, "superframe", "superframes"))
        {
            Kind = NodeKind.Stream,
            Description = VoiceExplain.Stream,
            Topic = Topics.WmaVoice,
        });

        (string? encoder, Confidence? confidence) = WmaEncoder.Apply(context, streamNode, findings, []);
        Node? statistics = packetCount > 0 ? AddStatistics(streamNode, stream, samples, duration, context.Configuration?.Duration, format.BytesPerSecond * 8.0 / 1000) : null;
        streamNode.Add(new Node("Packets", parent.Offset, parent.Length, ProNodes.Count(packetCount, "packet", "packets"),
            () => new BlockedChildren([], packetCount, nodes.PacketNode))
        {
            Description = VoiceExplain.Packets,
            Topic = Topics.WmaVoiceSuperframes,
        });
        streamNode.Add(new Node("Superframes", parent.Offset, parent.Length, ProNodes.Count(superframeCount, "superframe", "superframes"),
            () => new BlockedChildren([], superframeCount, nodes.SuperframeNode))
        {
            Description = VoiceExplain.Superframes,
            Topic = Topics.WmaVoiceSuperframes,
        });

        var series = new List<Series>();
        if (superframeCount > 0 && p.SampleRate > 0)
        {
            series.AddRange(MakeSeries(stream, nodes, statistics));
        }

        int channels = format.Channels;
        context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = CodecName,
            SampleRate = format.SampleRate,
            Channels = channels,
            ChannelMode = null,
            ChannelLayout = channels == 1 ? ChannelLayouts.Mono : channels == 2 ? ChannelLayouts.Stereo : null,
            Samples = samples,
            Duration = duration,
            AverageBitrate = duration > 0 ? totals.SuperframeBits / duration / 1000 : null,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = encoder,
            EncoderConfidence = confidence,
            Series = series,
        });
    }

    private static Node AddStatistics(Node streamNode, VoiceStream stream, long samples, double duration, double? containerDuration, double nominal)
    {
        VoiceParameters p = stream.Parameters;
        VoiceTotals t = stream.Totals;
        long start = streamNode.Offset;
        long length = streamNode.Length;
        Node node = streamNode.Add(new Node("Statistics", start, length, $"{ProNodes.Count(t.Superframes, "superframe", "superframes")}, {duration.ToString("F3", CultureInfo.InvariantCulture)} s")
        {
            Kind = NodeKind.Statistics,
            Description = VoiceExplain.Statistics,
            Topic = Topics.WmaVoice,
        });

        void Add(string name, NodeValue value, string description, string? topic = null) => node.Add(name, start, length, value, description, topic);

        Add("Packets", NodeValue.Integer(t.Packets), VoiceExplain.StatPackets, Topics.WmaVoiceSuperframes);
        Add("Superframes", NodeValue.Integer(t.Superframes), VoiceExplain.StatSuperframes, Topics.WmaVoiceSuperframes);
        if (t.FailedSuperframes > 0)
        {
            Add("Invalid Superframes", NodeValue.Integer(t.FailedSuperframes), VoiceExplain.StatInvalidSuperframes);
        }

        Add("Frames", NodeValue.Integer(t.Frames), VoiceExplain.StatFrames, Topics.WmaVoiceFrames);
        Add("Frame Length", NodeValue.Integer(VoiceParameters.FrameSamples, "samples"), VoiceExplain.StatFrameLength);
        Add("Samples", NodeValue.Integer(samples, "samples", "sample"), VoiceExplain.StatSamples);
        Add("Duration", NodeValue.Decimal(duration, 3, "s"), VoiceExplain.StatDuration);
        if (containerDuration is > 0)
        {
            Add("Container Duration", NodeValue.Decimal(containerDuration.Value, 3, "s"), ProExplain.StatContainerDuration);
        }

        Add("Block Alignment", NodeValue.Integer(p.BlockAlign, "bytes"), ProExplain.StatBlockAlignment);
        Add("Packet Bytes", NodeValue.Integer(t.UsedBytes, "bytes"), ProExplain.StatPacketBytes);
        if (duration > 0)
        {
            Add("Average Bitrate", NodeValue.Decimal(t.SuperframeBits / duration / 1000, 1, "kbps"), VoiceExplain.StatAverageBitrate);
        }

        if (nominal > 0)
        {
            Add("Nominal Bitrate", NodeValue.Decimal(nominal, 1, "kbps"), ProExplain.StatNominalBitrate);
        }

        if (t.Superframes > 0)
        {
            double perSuperframe = p.SampleRate / (double)VoiceParameters.SuperframeSamples / 1000;
            Add("Smallest Superframe", NodeValue.Decimal(t.MinSuperframeBits / 8.0, 1, "bytes"), VoiceExplain.StatSmallestSuperframe);
            Add("Largest Superframe", NodeValue.Decimal(t.MaxSuperframeBits / 8.0, 1, "bytes"), VoiceExplain.StatLargestSuperframe);
            Add("Minimum Superframe Bitrate", NodeValue.Decimal(t.MinSuperframeBits * perSuperframe, 1, "kbps"), VoiceExplain.StatMinimumSuperframeBitrate);
            Add("Maximum Superframe Bitrate", NodeValue.Decimal(t.MaxSuperframeBits * perSuperframe, 1, "kbps"), VoiceExplain.StatMaximumSuperframeBitrate);
        }

        Add("Superframes Spanning Packets", NodeValue.Integer(t.StraddlingSuperframes), VoiceExplain.StatStraddling, Topics.WmaVoiceSuperframes);
        Add("Most Superframes in a Packet", NodeValue.Integer(t.MaxSuperframesInPacket), VoiceExplain.StatMaxPerPacket);
        Add("Spillover Field", NodeValue.Integer(p.SpilloverBits, "bits"), VoiceExplain.StatSpilloverBits, Topics.WmaVoiceSuperframes);
        Add("Superframes with a Sample Count", NodeValue.Integer(t.SuperframesWithSampleCount), VoiceExplain.StatSampleCount);
        Add("LSPs per Frame", NodeValue.Integer(p.LspCount), VoiceExplain.StatLspCount, Topics.WmaVoiceFrames);
        Add("Superframes with Residual LSPs", ProReport.Share(t.ResidualSuperframes, t.Superframes, "superframe", "superframes"), VoiceExplain.StatResidual, Topics.WmaVoiceFrames);
        Add("Superframes with Additional Data", NodeValue.Integer(t.AdditionalDataSuperframes), VoiceExplain.StatAdditionalData);
        Add("Uses Postfilter", NodeValue.Boolean(p.HasPostfilter), VoiceExplain.StatPostfilter, Topics.WmaVoice);
        Add("Pitch Range", NodeValue.Text($"{p.MinPitch} to {p.MaxPitch - 1} samples"), VoiceExplain.StatPitchRange, Topics.WmaVoiceFrames);
        if (t.IgnoredBits > 0)
        {
            Add("Ignored Bits", NodeValue.Integer(t.IgnoredBits, "bits"), VoiceExplain.StatIgnoredBits);
        }

        if (t.TailBitsAtEnd > 0)
        {
            Add("Bits After the Last Superframe", NodeValue.Integer(t.TailBitsAtEnd, "bits"), VoiceExplain.StatEndPadding);
        }

        if (t.SkippedSpilloverBits > 0)
        {
            Add("Skipped Spillover Bits", NodeValue.Integer(t.SkippedSpilloverBits, "bits"), VoiceExplain.StatSkippedSpillover);
        }

        if (t.Frames > 0)
        {
            AddDistributions(node, t, start, length);
        }

        return node;
    }

    // The frames by type, and by the kinds of codebook and the number of blocks their types stand for.
    private static void AddDistributions(Node statistics, VoiceTotals t, long start, long length)
    {
        Node types = statistics.Add("Frame Types", start, length, NodeValue.Integer(t.FramesByType.Count(c => c > 0), "types", "type"), VoiceExplain.StatFrameTypes, Topics.WmaVoiceFrames);
        var fixedCodebooks = new long[VoiceNames.FixedCodebooks.Length];
        var adaptiveCodebooks = new long[VoiceNames.AdaptiveCodebooks.Length];
        var blocks = new SortedDictionary<int, long>();
        for (int type = 0; type < t.FramesByType.Length; type++)
        {
            long count = t.FramesByType[type];
            if (count == 0)
            {
                continue;
            }

            byte[] d = VoiceTables.FrameDescriptors[type];
            fixedCodebooks[d[3]] += count;
            adaptiveCodebooks[d[2]] += count;
            blocks[d[0]] = blocks.GetValueOrDefault(d[0]) + count;
            types.Add($"Type {type} ({VoiceNames.Frame(type)})", start, length, ProReport.Share(count, t.Frames, "frame", "frames"), VoiceExplain.StatFrameType);
        }

        Group(statistics, "Fixed Codebooks", VoiceExplain.StatFixedCodebooks, VoiceNames.FixedCodebooks, fixedCodebooks, t.Frames, start, length);
        Group(statistics, "Adaptive Codebooks", VoiceExplain.StatAdaptiveCodebooks, VoiceNames.AdaptiveCodebooks, adaptiveCodebooks, t.Frames, start, length);
        Node perFrame = statistics.Add("Blocks per Frame", start, length, NodeValue.Integer(blocks.Count, "sizes", "size"), VoiceExplain.StatBlocksPerFrame, Topics.WmaVoiceFrames);
        foreach ((int count, long frames) in blocks)
        {
            perFrame.Add(count == 1 ? "1 block" : $"{count} blocks", start, length, ProReport.Share(frames, t.Frames, "frame", "frames"), VoiceExplain.StatCodebookShare);
        }
    }

    private static void Group(Node statistics, string name, string description, string[] names, long[] counts, long total, long start, long length)
    {
        Node group = statistics.Add(name, start, length, NodeValue.Integer(counts.Count(c => c > 0), "kinds", "kind"), description, Topics.WmaVoiceFrames);
        for (int i = 0; i < names.Length; i++)
        {
            if (counts[i] > 0)
            {
                group.Add(char.ToUpperInvariant(names[i][0]) + names[i][1..], start, length, ProReport.Share(counts[i], total, "frame", "frames"), VoiceExplain.StatCodebookShare);
            }
        }
    }

    private static List<Series> MakeSeries(VoiceStream stream, VoiceNodes nodes, Node? anchor)
    {
        VoiceParameters p = stream.Parameters;
        int count = stream.Superframes.Count;
        var offsets = new double[count];
        var bitrates = new double[count];
        var seconds = new List<(long Bits, long Samples)>();
        long elapsed = 0;
        for (int i = 0; i < count; i++)
        {
            VoiceSuperframeRecord record = stream.Superframes[i];
            offsets[i] = nodes.SuperframeOffset(i);
            if (record.Status != 0 || record.Samples == 0)
            {
                continue;
            }

            bitrates[i] = record.Bits * (double)p.SampleRate / record.Samples / 1000;
            int second = (int)(elapsed / p.SampleRate);
            while (seconds.Count <= second)
            {
                seconds.Add(default);
            }

            seconds[second] = (seconds[second].Bits + record.Bits, seconds[second].Samples + record.Samples);
            elapsed += record.Samples;
        }

        var result = new List<Series>
        {
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, bitrates) { Description = VoiceSeriesText.Bitrate },
        };

        if (seconds.Count > 0)
        {
            double[] starts = Enumerable.Range(0, seconds.Count).Select(s => (double)s).ToArray();
            double[] rates = seconds
                .Select(s => s.Samples > 0 ? Math.Round(s.Bits * (double)p.SampleRate / s.Samples / 1000, 1, MidpointRounding.ToEven) : 0)
                .ToArray();
            result.Add(new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, rates, anchor) { Description = VoiceSeriesText.BitrateOverTime });
        }

        return result;
    }
}
