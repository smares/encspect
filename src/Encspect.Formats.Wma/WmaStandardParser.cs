using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio versions 1 and 2 (the format tags 0x0160 and 0x0161): the codec-specific data, the superframes with their
// bit reservoir, and the syntax of every frame down to the end of its coefficients. No audio is rebuilt. Written clean-room from
// docs/design/wma-standard-format.md.
public sealed class WmaStandardParser : ICodecParser
{
    public const string CodecId = "wma";

    internal const int MaxPackets = 1 << 18;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wma.payload-not-analyzed", StdText.PayloadNotAnalyzed, context.Parent);
            return;
        }

        if (context.Configuration?.Format is not { } format)
        {
            context.AddFinding(Severity.Info, "wma.no-format", StdText.NoFormat, context.Parent);
            return;
        }

        CodecConfiguration configuration = context.Configuration;
        int version = format.FormatTag == 0x0160 ? 1 : 2;
        string codec = version == 1 ? "Windows Media Audio 1" : "Windows Media Audio 2";
        var findings = new FindingCollector();
        ReadOnlySpan<byte> data = configuration.Data.Span;
        AddCodecData(configuration, version, data, findings);

        if (configuration.AreSamplesEncrypted)
        {
            AddBasicStream(context, context.Parent, codec, format, version, configuration);
            findings.Flush(context);
            return;
        }

        StdParameters parameters = StdParameters.Create(format, data);
        if (parameters.Unsupported is { } reason)
        {
            findings.AddNow(Severity.Warning, "wma.format-unsupported", StdText.FormatUnsupported(reason), configuration.Parent);
            AddBasicStream(context, context.Parent, codec, format, version, configuration);
            findings.Flush(context);
            return;
        }

        Node parent = context.Parent;
        var stream = new StdStream(parameters);
        var nodes = new StdNodes(parameters, stream);
        stream.Read(packets, MaxPackets, nodes, findings, context.CancellationToken);

        long samples = Math.Max(0, stream.ValidFrames - 1) * parameters.FrameLength;
        double duration = (double)samples / parameters.SampleRate;
        var streamNode = new Node(codec, parent.Offset, parent.Length, Count(stream.ValidFrames, "frame", "frames"))
        {
            Kind = NodeKind.Stream,
            Description = StdText.Stream,
            Topic = Topics.WmaStandard,
        };

        parent.Add(streamNode);
        WmaEncoder.Detected detected = WmaEncoder.Apply(context, streamNode, findings, Traits(parameters, data, stream));
        Node? statistics = null;
        if (stream.FramesRead > 0)
        {
            statistics = streamNode.Add(new Node("Statistics", parent.Offset, parent.Length, $"{Count(stream.FramesRead, "frame", "frames")}, {duration.ToString("F3", CultureInfo.InvariantCulture)} s")
            {
                Kind = NodeKind.Statistics,
                Description = StdText.Statistics,
                Topic = Topics.WmaStandard,
            });
        }

        streamNode.Add(nodes.Superframes(parent, stream.Superframes.Count));
        Node frames = streamNode.Add(nodes.Frames(parent, stream.Frames.Count));
        double? averageBitrate = duration > 0 ? 8.0 * parameters.BlockAlign * stream.NotShortSuperframes / duration / 1000 : null;
        if (statistics is not null)
        {
            StdReport.AddStatistics(statistics, parent, parameters, stream, samples, duration, averageBitrate);
        }

        if (stream.Skipped > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit", StdText.RecordsLimit(stream.Skipped, MaxPackets), parent);
        }

        if (stream.ValidFrames > 0 && parameters.UsesBitReservoir)
        {
            long bits = (8L * stream.FinalReservoirBytes) - stream.FinalReservoirStartBit;
            if (stream.FinalReservoirBytes > 0 && bits > 0)
            {
                findings.AddNow(Severity.Info, "wma.reservoir-at-end", StdText.ReservoirAtEnd(bits), frames);
            }
        }

        context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = codec,
            SampleRate = parameters.SampleRate,
            Channels = parameters.Channels,
            ChannelMode = parameters.Channels == 1 ? "Mono" : "Stereo",
            ChannelLayout = parameters.Channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo,
            Samples = samples,
            Duration = duration,
            AverageBitrate = averageBitrate,
            IsVbr = configuration.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = statistics is not null ? StdReport.Series(parameters, stream, statistics) : [],
        });

        findings.Flush(context);
    }

    // The stream of a configuration whose frames are not read: one that is encrypted, or one the format's decoders do not open.
    // The container's duration and the nominal bitrate stand in for the figures the frames would give.
    internal static void AddBasicStream(CodecContext context, Node parent, string codec, AudioFormat format, int version, CodecConfiguration? configuration = null,
        string? description = null, string? topic = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(format);

        bool isEncrypted = configuration?.AreSamplesEncrypted ?? false;
        var node = parent.Add(new Node(codec, parent.Offset, parent.Length, isEncrypted ? "frames not read: encrypted" : "not analyzed")
        {
            Kind = NodeKind.Stream,
            Description = description ?? (isEncrypted ? Explain.EncryptedHeaderOnlyStream : StdText.StreamNotAnalyzed),
            Topic = topic ?? (version == 3 ? Topics.WmaPro : Topics.WmaStandard),
        });
        double duration = isEncrypted ? configuration?.Duration ?? 0 : 0;
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = format.Channels == 1 ? "Mono" : format.Channels == 2 ? "Stereo" : null,
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
            Samples = isEncrypted ? (long)Math.Round(duration * format.SampleRate) : 0,
            Duration = duration,
            AverageBitrate = isEncrypted && format.BytesPerSecond > 0 ? format.BytesPerSecond * 8.0 / 1000 : null,
        });
    }

    internal static string Count(long count, string singular, string plural) => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    private static List<WmaEncoder.Trait> Traits(StdParameters parameters, ReadOnlySpan<byte> data, StdStream stream)
    {
        var traits = new List<WmaEncoder.Trait>();
        if (parameters.Version == 2 && data.Length >= 10 && parameters.UsesBitReservoir && parameters.UsesVariableBlocks)
        {
            uint samplesPerBlock = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (samplesPerBlock == 17u * (uint)parameters.FrameLength)
            {
                traits.Add(new WmaEncoder.Trait("Microsoft's Windows Media Audio encoder", "Microsoft", Confidence.Medium, StdText.MicrosoftEvidence(samplesPerBlock)));
            }
        }

        if (!parameters.UsesBitReservoir && !parameters.UsesVariableBlocks && stream.PaddedSuperframes > 0 && stream.IsPaddingAllN)
        {
            traits.Add(new WmaEncoder.Trait($"FFmpeg (wmav{parameters.Version} encoder)", "FFmpeg", stream.PaddedSuperframes >= 2 ? Confidence.High : Confidence.Medium,
                StdText.FFmpegEvidence(parameters.Version, stream.PaddedSuperframes)));
        }

        return traits;
    }

    // The fields of the codec-specific data under the container's node for it, and the findings about their size.
    private static void AddCodecData(CodecConfiguration configuration, int version, ReadOnlySpan<byte> data, FindingCollector findings)
    {
        MappedBytes mapped = configuration.Data;
        Node holder = configuration.Parent;
        int expected = version == 1 ? 4 : 10;
        int optionsAt = version == 1 ? 2 : 4;
        bool hasOptions = data.Length >= optionsAt + 2;
        if (version == 1 && data.Length >= 2)
        {
            holder.Add(Node.FromMapped("Unused", mapped, 0, 2, NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(data)), NodeKind.Detail, StdText.Unused, Topics.WmaStandard));
        }

        if (version == 2 && data.Length >= 4)
        {
            holder.Add(Node.FromMapped("Samples Per Block", mapped, 0, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(data), "samples"), NodeKind.Detail, StdText.SamplesPerBlock, Topics.WmaStandard));
        }

        Node? optionsNode = null;
        if (hasOptions)
        {
            int options = BinaryPrimitives.ReadUInt16LittleEndian(data[optionsAt..]);
            optionsNode = holder.Add(Node.FromMapped("Encode Options", mapped, optionsAt, optionsAt + 2, NodeValue.Integer(options), NodeKind.Detail, StdText.EncodeOptions, Topics.WmaStandard));
            int bit0 = optionsAt * 8;

            Node Bits(string name, int from, int to, NodeValue value, string description) =>
                Node.FromBits(name, mapped, bit0 + from, bit0 + to, value, NodeKind.Detail, description, Topics.WmaStandard, BitOrder.LeastSignificantFirst);

            optionsNode.Add(Bits("Uses Exponent VLC", 0, 1, NodeValue.Boolean((options & 1) != 0), StdText.UsesExponentVlc));
            optionsNode.Add(Bits("Uses Bit Reservoir", 1, 2, NodeValue.Boolean((options & 2) != 0), StdText.UsesBitReservoir));
            optionsNode.Add(Bits("Uses Variable Block Length", 2, 3, NodeValue.Boolean((options & 4) != 0), StdText.UsesVariableBlockLength));
            optionsNode.Add(Bits("Block Size Count Code", 3, 5, NodeValue.Integer((options >> 3) & 3), StdText.BlockSizeCountCode));
            optionsNode.Add(Bits("Other Options", 5, 16, NodeValue.Integer(options >> 5), StdText.OtherOptions));
            if (version == 2 && data.Length >= 8 && options == 0x000D)
            {
                Node disabled = optionsNode;
                findings.AddNow(Severity.Info, "wma.variable-blocks-disabled", StdText.VariableBlocksDisabled, disabled);
            }
        }

        if (version == 2 && data.Length >= 10)
        {
            holder.Add(Node.FromMapped("Super Block Align", mapped, 6, 10, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(data[6..]), "bytes"), NodeKind.Detail, StdText.SuperBlockAlign, Topics.WmaStandard));
        }

        if (data.Length > expected)
        {
            holder.Add(Node.FromMapped("Extra Data", mapped, expected, data.Length, NodeValue.Integer(data.Length - expected, "bytes"), NodeKind.Detail, StdText.ExtraData, Topics.WmaStandard));
        }

        if (data.Length != expected)
        {
            findings.AddNow(hasOptions ? Severity.Info : Severity.Warning, "wma.codec-data-size", StdText.CodecDataSize(version, data.Length, !hasOptions), holder);
        }
    }
}
