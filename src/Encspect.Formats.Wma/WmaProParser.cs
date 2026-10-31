using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Professional (the format tag 0x0162): the packets of the stream, the frames in them and the layout of each frame down to
// the coefficient codes (docs/design/wma-pro-format.md). Nothing is decoded.
public sealed class WmaProParser : ICodecParser
{
    public const string CodecId = "wma-pro";

    private const string Name = "Windows Media Audio Professional";

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
        if (data.Length >= ProParameters.DataSize)
        {
            CodecData.Add(configuration.Parent, configuration.Data, Topics.WmaPro, isLossless: false);
        }
        else
        {
            findings.AddNow(Severity.Warning, "wma.codec-data-size",
                $"The codec-specific data of {Name} is {Counts.Bytes(data.Length)}; FFmpeg's decoder needs 18 and refuses fewer.", configuration.Parent);
        }

        if (configuration.AreSamplesEncrypted)
        {
            WmaStreams.AddEncrypted(context, Name, format, configuration, Topics.WmaPro, ProExplain.EncryptedStream);
            findings.Flush(context);
            return;
        }

        ProParameters p = ProParameters.Create(format, data);
        if (p.Unsupported is { } reason)
        {
            WmaStreams.AddNotAnalyzed(context, Name, format, Topics.WmaPro, ProExplain.NotAnalyzedStream);
            findings.AddNow(Severity.Warning, "wma.format-unsupported", $"The stream is not analyzed: {reason}.", configuration.Parent);
            findings.Flush(context);
            return;
        }

        Node parent = context.Parent;
        var stream = parent.Add(new Node(Name, parent.Offset, parent.Length)
        {
            Kind = NodeKind.Stream,
            Description = ProExplain.Stream,
            Topic = Topics.WmaPro,
        });

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, stream, findings);
        var tally = new ProTally();
        var reader = new WmaPacketReader(p, new ProFrameReader(p, tally), false, p.Channels, fault => ProExplain.Reason((ProFault)fault), context, findings);
        reader.Run(packets);

        var nodes = new ProNodes(reader.Data, p);
        var packetNodes = new PacketNodes(reader.Data, p, false);
        long frameCount = reader.Data.Frames.Count;
        double duration = reader.Samples / (double)p.SampleRate;
        Node? statistics = frameCount > 0 ? ProReport.Statistics(p, reader, tally, parent, duration) : null;
        if (statistics is not null)
        {
            stream.Add(statistics);
        }

        Node packetsNode = stream.Add(packetNodes.PacketList(parent.Offset, parent.Length));
        Node framesNode = stream.Add(packetNodes.FrameList(parent.Offset, parent.Length, nodes.Frame, Topics.WmaPro));
        stream.Value = NodeValue.Text(Counts.Of(reader.ValidFrames, "frame", "frames"));
        reader.PacketNode = i => packetsNode.Children[i];
        reader.FrameNode = i => framesNode.Children[i];

        if (reader.Skipped > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {WmaPacketReader.MaxPackets} media objects; the {reader.Skipped} after them are not read, so the statistics and the series cover the first {WmaPacketReader.MaxPackets}.", parent);
        }

        if (reader.UnfinishedBits > 0 && reader.ValidFrames > 0)
        {
            int left = reader.UnfinishedBits;
            findings.AddNow(Severity.Info, "wma.frame-unfinished",
                $"The last packet ends with {Counts.Bits(left)} after a frame whose trailer bit says another frame follows, and no packet follows to finish one; a decoder drops them.", framesNode);
        }

        IReadOnlyList<Speaker>? layout = WmaStreams.Layout(p.ChannelMask, p.Channels);
        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = Name,
            SampleRate = p.SampleRate,
            Channels = p.Channels,
            ChannelLayout = layout,
            Samples = reader.Samples,
            Duration = duration,
            AverageBitrate = duration > 0 ? p.BlockAlignment * 8.0 * reader.FullPackets / duration / 1000 : null,
            IsVbr = configuration.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = frameCount > 0 ? ProReport.Series(p, reader, stream) : [],
        });

        findings.Flush(context);
    }
}

// The streams that are described from the container and not read: an encrypted one, and one whose codec data fails a check.
internal static class WmaStreams
{
    public static void AddEncrypted(CodecContext context, string codec, AudioFormat format, CodecConfiguration configuration, string topic, string description)
    {
        Node parent = context.Parent;
        var node = parent.Add(new Node(codec, parent.Offset, parent.Length, NodeValue.Text("frames not read: encrypted"))
        {
            Kind = NodeKind.Stream,
            Description = description,
            Topic = topic,
        });
        double duration = configuration.Duration ?? 0;
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = ModeOf(format.Channels),
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
            Samples = (long)Math.Round(duration * format.SampleRate),
            Duration = duration,
            AverageBitrate = format.BytesPerSecond > 0 ? format.BytesPerSecond * 8.0 / 1000 : null,
        });
    }

    public static void AddNotAnalyzed(CodecContext context, string codec, AudioFormat format, string topic, string description)
    {
        Node parent = context.Parent;
        var node = parent.Add(new Node(codec, parent.Offset, parent.Length, NodeValue.Text("not analyzed"))
        {
            Kind = NodeKind.Stream,
            Description = description,
            Topic = topic,
        });
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = ModeOf(format.Channels),
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
        });
    }

    private static string? ModeOf(int channels) => channels == 1 ? "Mono" : channels == 2 ? "Stereo" : null;

    // The speakers of the channel mask in the order of its bits, where its set bits number the channels and no bit lies above 17; else mono
    // or stereo by the count, else none.
    public static IReadOnlyList<Speaker>? Layout(uint mask, int channels)
    {
        if (mask != 0 && BitOperations.PopCount(mask) == channels && (mask >> 18) == 0)
        {
            return [.. Enumerable.Range(0, 18).Where(bit => (mask & (1u << bit)) != 0).Select(bit => (Speaker)(bit + 1))];
        }

        return channels == 1 ? ChannelLayouts.Mono : channels == 2 ? ChannelLayouts.Stereo : null;
    }
}

// The 18 bytes of codec-specific data of Professional and Lossless: the sample size, the channel mask and the decode flags, shown as nodes.
internal static class CodecData
{
    public static void Add(Node holder, MappedBytes data, string topic, bool isLossless)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        // Lossless uses the low byte of the field as its sample size; the high byte is not read.
        int bits = isLossless ? bytes[0] : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        holder.Add("Bits Per Sample", offset, 2, NodeValue.Integer(bits, "bits"), isLossless ? LosslessExplain.SampleSize : ProExplain.SampleSize, topic);
        holder.Add("Channel Mask", offset + 2, 4, NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]), 8), ProExplain.ChannelMask, Topics.ChannelLayouts);
        holder.Add("Unknown 1", offset + 6, 8, NodeValue.Hex(BinaryPrimitives.ReadUInt64LittleEndian(bytes[6..]), 16), ProExplain.Unknown);
        int value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        Node flags = holder.Add("Decode Flags", offset + 14, 2, NodeValue.Hex((uint)value, 4), ProExplain.DecodeFlags, topic);
        holder.Add("Unknown 2", offset + 16, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]), 4), ProExplain.Unknown);
        if (bytes.Length > ProParameters.DataSize)
        {
            holder.Add("Extra Data", offset + ProParameters.DataSize, bytes.Length - ProParameters.DataSize, NodeValue.Integer(bytes.Length - ProParameters.DataSize, "bytes", "byte"), ProExplain.ExtraData);
        }

        long at = flags.Offset;
        Bit(flags, at, 0, 1, NodeValue.Integer(value & 1), "Unread Flag", ProExplain.UnreadFlag, topic);
        Bit(flags, at, 1, 2, NodeValue.Integer((value >> 1) & 3), "Frame Length Adjustment", ProExplain.FrameLengthAdjustment, topic);
        Bit(flags, at, 3, 3, NodeValue.Integer((value >> 3) & 7), "Subframe Count Code", ProExplain.SubframeCountCode, topic);
        Bit(flags, at, 6, 1, NodeValue.Boolean((value & 0x40) != 0), "Has Length Prefix", ProExplain.HasLengthPrefix, topic);
        Bit(flags, at, 7, 1, NodeValue.Boolean((value & 0x80) != 0), "Uses Dynamic Range Compression", ProExplain.UsesDynamicRange, topic);
        if (isLossless)
        {
            Bit(flags, at, 8, 1, NodeValue.Boolean((value & 0x100) != 0), "Has LPC Flags", LosslessExplain.HasLpcFlags, topic);
            Bit(flags, at, 9, 7, NodeValue.Hex((uint)(value >> 9), 2), "Other Flags", ProExplain.OtherFlags, topic);
        }
        else
        {
            Bit(flags, at, 8, 8, NodeValue.Hex((uint)(value >> 8), 2), "Other Flags", ProExplain.OtherFlags, topic);
        }
    }

    private static void Bit(Node flags, long offset, int first, int width, NodeValue value, string name, string description, string topic) =>
        flags.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: topic, order: BitOrder.LeastSignificantFirst));
}
