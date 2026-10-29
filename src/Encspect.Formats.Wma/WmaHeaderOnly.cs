using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What the codec parsers of Windows Media Audio versions 1 and 2, Professional, Lossless and Voice share: the stream is described by what the
// container and the codec-specific data say (and its packets counted); the frames are not read.
internal static class WmaHeaderOnly
{
    public static void Parse(CodecContext context, string codecName, string topic, string notReadCode, string notReadMessage, Action<CodecConfiguration, FindingCollector>? configuration, bool channelMaskInData = false)
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

        if (config.AreSamplesEncrypted)
        {
            AddEncryptedStream(context, codecName, format, config, topic);
            findings.Flush(context);
            return;
        }

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
            Description = Explain.HeaderOnlyStream,
            Topic = topic,
        });

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, stream, findings);
        double? duration = config.Duration;
        Node statistics = stream.Add(new Node("Statistics", parent.Offset, parent.Length, Count(count, "packet", "packets"))
        {
            Kind = NodeKind.Statistics,
            Description = Explain.Statistics,
            Topic = topic,
        });
        statistics.Add("Packets", parent.Offset, parent.Length, NodeValue.Integer(count), Explain.StatPackets);
        statistics.Add("Packet Bytes", parent.Offset, parent.Length, NodeValue.Integer(bytes, "bytes"), Explain.StatPacketBytes);
        statistics.Add("Block Alignment", parent.Offset, parent.Length, NodeValue.Integer(format.BlockAlign, "bytes"), Explain.StatBlockAlignment);
        if (count > 0)
        {
            statistics.Add("Smallest Packet", parent.Offset, parent.Length, NodeValue.Integer(smallest, "bytes"), Explain.StatSmallestPacket);
            statistics.Add("Largest Packet", parent.Offset, parent.Length, NodeValue.Integer(largest, "bytes"), Explain.StatLargestPacket);
        }

        if (duration is > 0)
        {
            statistics.Add("Duration", parent.Offset, parent.Length, NodeValue.Decimal(duration.Value, 3, "s"), Explain.StatContainerDuration);
        }

        double nominal = format.BytesPerSecond * 8.0 / 1000;
        if (nominal > 0)
        {
            statistics.Add("Nominal Bitrate", parent.Offset, parent.Length, NodeValue.Decimal(nominal, 1, "kbps"), Explain.StatNominalBitrate);
        }

        double? average = duration is > 0 && count > 0 ? bytes * 8.0 / duration.Value / 1000 : null;
        if (average is { } measured)
        {
            statistics.Add("Average Bitrate", parent.Offset, parent.Length, NodeValue.Decimal(measured, 1, "kbps"), Explain.StatAverageBitrate);
        }

        findings.AddNow(Severity.Info, notReadCode, notReadMessage, stream);

        IReadOnlyList<Speaker>? layout = null;
        int channels = format.Channels;
        // The mask is in the format's extensible header or, for the codecs that opt in (Professional, Lossless, Voice), at byte 2 of their codec data.
        // The type-specific data of versions 1 and 2 has other fields there (ASF specification, section 11.1.1).
        uint? stated = format.ChannelMask ?? (channelMaskInData && config.Data.Length >= 18 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(config.Data.Span[2..]) : null);
        if (stated is { } mask && mask != 0 && System.Numerics.BitOperations.PopCount(mask) == channels && (mask >> 18) == 0)
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
            ChannelMode = channels == 1 ? "Mono" : channels == 2 ? "Stereo" : null,
            ChannelLayout = layout,
            BitsPerSample = null,
            Samples = duration is > 0 ? (long)Math.Round(duration.Value * format.SampleRate) : 0,
            Duration = duration ?? 0,
            AverageBitrate = average,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
        });

        findings.Flush(context);
    }

    // A stream whose samples are encrypted takes the container's duration and the nominal bitrate of the format; its packets are not counted.
    private static void AddEncryptedStream(CodecContext context, string codec, AudioFormat format, CodecConfiguration configuration, string topic)
    {
        Node parent = context.Parent;
        var node = parent.Add(new Node(codec, parent.Offset, parent.Length, NodeValue.Text("frames not read: encrypted"))
        {
            Kind = NodeKind.Stream,
            Description = Explain.EncryptedHeaderOnlyStream,
            Topic = topic,
        });
        double duration = configuration.Duration ?? 0;
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = format.Channels == 1 ? "Mono" : format.Channels == 2 ? "Stereo" : null,
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
            Samples = (long)Math.Round(duration * format.SampleRate),
            Duration = duration,
            AverageBitrate = format.BytesPerSecond > 0 ? format.BytesPerSecond * 8.0 / 1000 : null,
        });
    }

    private static string Count(long count, string singular, string plural) => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
