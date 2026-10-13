using Encspect.Core;

namespace Encspect.Formats.Wma;

// What the codec parsers of Windows Media Audio Lossless and Voice share: the stream is described by what the
// container and the codec-specific data say (and its packets counted); the frames are not read.
internal static class WmaHeaderOnly
{
    public static void Parse(CodecContext context, string codecName, string topic, string notReadCode, string notReadMessage, Action<CodecConfiguration, FindingCollector>? configuration)
    {
        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wma.payload-not-analyzed", "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are.", context.Parent);
            return;
        }

        if (context.Configuration?.Format is not { } format)
        {
            context.AddFinding(Severity.Info, "wma.no-format", "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed.", context.Parent);
            return;
        }

        CodecConfiguration config = context.Configuration!;
        var findings = new FindingCollector();
        configuration?.Invoke(config, findings);

        long count = 0;
        long bytes = 0;
        long smallest = long.MaxValue;
        long largest = 0;
        foreach (MappedBytes packet in packets)
        {
            if (count++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            bytes += packet.Length;
            smallest = Math.Min(smallest, packet.Length);
            largest = Math.Max(largest, packet.Length);
        }

        Node parent = context.Parent;
        var stream = parent.Add(new Node(codecName, parent.Offset, parent.Length, NodeValue.Text("frames not read"))
        {
            Kind = NodeKind.Stream,
            Description = ProExplain.HeaderOnlyStream,
            Topic = topic,
        });

        (string? encoder, Confidence? confidence) = WmaEncoder.Apply(context, stream, findings, []);
        double? duration = config.Duration;
        Node statistics = stream.Add(new Node("Statistics", parent.Offset, parent.Length, WmaStandardParser.Count(count, "packet", "packets"))
        {
            Kind = NodeKind.Statistics,
            Description = ProExplain.Statistics,
            Topic = topic,
        });
        statistics.Add("Packets", parent.Offset, parent.Length, NodeValue.Integer(count), ProExplain.StatPackets);
        statistics.Add("Packet Bytes", parent.Offset, parent.Length, NodeValue.Integer(bytes, "bytes"), ProExplain.StatPacketBytes);
        statistics.Add("Block Alignment", parent.Offset, parent.Length, NodeValue.Integer(format.BlockAlign, "bytes"), ProExplain.StatBlockAlignment);
        if (count > 0)
        {
            statistics.Add("Smallest Packet", parent.Offset, parent.Length, NodeValue.Integer(smallest, "bytes"), ProExplain.StatSmallestPacket);
            statistics.Add("Largest Packet", parent.Offset, parent.Length, NodeValue.Integer(largest, "bytes"), ProExplain.StatLargestPacket);
        }

        if (duration is > 0)
        {
            statistics.Add("Duration", parent.Offset, parent.Length, NodeValue.Decimal(duration.Value, 3, "s"), ProExplain.StatContainerDuration);
        }

        double nominal = format.BytesPerSecond * 8.0 / 1000;
        if (nominal > 0)
        {
            statistics.Add("Nominal Bitrate", parent.Offset, parent.Length, NodeValue.Decimal(nominal, 1, "kbps"), ProExplain.StatNominalBitrate);
        }

        findings.AddNow(Severity.Info, notReadCode, notReadMessage, stream);

        IReadOnlyList<Speaker>? layout = null;
        int channels = format.Channels;
        if (format.ChannelMask is { } mask && mask != 0 && System.Numerics.BitOperations.PopCount(mask) == channels && (mask >> 18) == 0)
        {
            layout = [.. Enumerable.Range(0, 18).Where(bit => (mask & (1u << bit)) != 0).Select(bit => (Speaker)(bit + 1))];
        }
        else if (channels is 1 or 2)
        {
            layout = channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo;
        }

        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = codecName,
            SampleRate = format.SampleRate,
            Channels = channels,
            ChannelLayout = layout,
            BitsPerSample = null,
            Samples = duration is > 0 ? (long)Math.Round(duration.Value * format.SampleRate) : 0,
            Duration = duration ?? 0,
            AverageBitrate = nominal > 0 ? nominal : null,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = encoder,
            EncoderConfidence = confidence,
        });

        findings.Flush(context);
    }
}
