using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Lossless (the format tag 0x0163): the packets of the stream, the frames in them and the layout of each frame down to the
// residuals of its subframes (docs/design/wma-lossless-format.md). Nothing is reconstructed: the filters that turn residuals into samples are
// not run, and the samples are counted from the frames.
public sealed class WmaLosslessParser : ICodecParser
{
    public const string CodecId = "wma-lossless";

    private const string Name = "Windows Media Audio Lossless";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
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

        CodecConfiguration configuration = context.Configuration!;
        var findings = new FindingCollector();
        ReadOnlySpan<byte> data = configuration.Data.Span;
        if (data.Length >= LosslessParameters.DataSize)
        {
            CodecData.Add(configuration.Parent, configuration.Data, Topics.WmaLossless, isLossless: true);
        }
        else
        {
            findings.AddNow(Severity.Warning, "wma.codec-data-size",
                $"The codec-specific data of {Name} is {Counts.Bytes(data.Length)}; FFmpeg's decoder needs 18 and refuses fewer.", configuration.Parent);
        }

        if (configuration.AreSamplesEncrypted)
        {
            WmaStreams.AddEncrypted(context, Name, format, configuration, Topics.WmaLossless, LosslessExplain.EncryptedStream);
            findings.Flush(context);
            return;
        }

        LosslessParameters p = LosslessParameters.Create(format, data);
        if (p.Unsupported is { } reason)
        {
            WmaStreams.AddNotAnalyzed(context, Name, format, Topics.WmaLossless, LosslessExplain.NotAnalyzedStream);
            findings.AddNow(Severity.Warning, "wma.format-unsupported", $"The stream is not analyzed: {reason}.", configuration.Parent);
            findings.Flush(context);
            return;
        }

        if (p.ChannelMask != 0 && p.Channels != p.FormatChannels)
        {
            findings.AddNow(Severity.Info, "wma.lossless-channel-count",
                $"The channel mask names {Counts.Of(p.Channels, "channel", "channels")} and the WAVEFORMATEX {p.FormatChannels}; the stream is read with the mask's count.", configuration.Parent);
        }

        Node parent = context.Parent;
        var stream = parent.Add(new Node(Name, parent.Offset, parent.Length)
        {
            Kind = NodeKind.Stream,
            Description = LosslessExplain.Stream,
            Topic = Topics.WmaLossless,
        });

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, stream, findings);
        var tally = new LosslessTally();
        var reader = new WmaPacketReader(p, new LosslessFrameReader(p, tally), true, p.Channels, fault => LosslessExplain.Reason((LosslessFault)fault), context, findings);
        reader.Run(packets);

        var nodes = new LosslessNodes(reader.Data, p);
        var packetNodes = new PacketNodes(reader.Data, p, true);
        long frameCount = reader.Data.Frames.Count;
        double duration = reader.Samples / (double)p.SampleRate;
        if (frameCount > 0)
        {
            stream.Add(LosslessReport.Statistics(p, reader, tally, parent, duration));
        }

        Node packetsNode = stream.Add(packetNodes.PacketList(parent.Offset, parent.Length));
        Node framesNode = stream.Add(packetNodes.FrameList(parent.Offset, parent.Length, nodes.Frame, Topics.WmaLossless));
        stream.Value = NodeValue.Text(Counts.Of(reader.ValidFrames, "frame", "frames"));
        reader.PacketNode = i => packetsNode.Children[i];
        reader.FrameNode = i => framesNode.Children[i];

        if (reader.Skipped > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {WmaPacketReader.MaxPackets} media objects; the {reader.Skipped} after them are not read, so the statistics and the series cover the first {WmaPacketReader.MaxPackets}.", parent);
        }

        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = Name,
            SampleRate = p.SampleRate,
            Channels = p.Channels,
            ChannelLayout = WmaStreams.Layout(p.ChannelMask, p.Channels),
            Samples = reader.Samples,
            Duration = duration,
            AverageBitrate = duration > 0 ? p.BlockAlignment * 8.0 * reader.FullPackets / duration / 1000 : null,
            IsVbr = configuration.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = frameCount > 0 ? LosslessReport.Series(p, reader, stream) : [],
        });

        findings.Flush(context);
    }
}
