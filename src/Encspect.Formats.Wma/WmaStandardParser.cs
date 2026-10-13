using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio versions 1 and 2 (the format tags 0x0160 and 0x0161), handed over by a container
// as the media objects of one stream (ASF: ParsePackets) with the WAVEFORMATEX and its codec-specific
// data. Read as FFmpeg's decoder reads them (wmadec.c and wma.c, FFmpeg 6.1.1): a media object is a
// superframe of the block alignment's size; with the bit reservoir it starts with a header and holds
// the end of the frame before it and the frames that begin in it, the last of which may go on in the
// next superframe. Every frame's blocks are read down to their gains, noise substitution, exponents
// and coefficient codes, without building the audio, so that the report knows where each frame
// ends and what it holds.
public sealed class WmaStandardParser : ICodecParser
{
    // Superframes (packets) are described in records up to this count, so a crafted file of millions of tiny ones
    // stays bounded; the rest are counted but not read.
    internal const int MaxPackets = 1 << 18;

    public const string CodecId = "wma";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wma.payload-not-analyzed", "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are.", context.Parent);
            return;
        }

        CodecConfiguration? configuration = context.Configuration;
        if (configuration?.Format is not { } format)
        {
            context.AddFinding(Severity.Info, "wma.no-format", "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed.", context.Parent);
            return;
        }

        int version = format.FormatTag == 0x0160 ? 1 : 2;
        ReadOnlySpan<byte> data = configuration.Data.Span;
        int options = version == 1
            ? (data.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) : 0)
            : (data.Length >= 6 ? BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) : 0);
        StandardParameters parameters = StandardParameters.Create(version, format.Channels, format.SampleRate, format.BytesPerSecond, format.BlockAlign, options, data.Length);
        var findings = new FindingCollector();
        Node parent = context.Parent;

        AddConfiguration(context, configuration, findings, version, parameters);
        string name = version == 1 ? "Windows Media Audio 1" : "Windows Media Audio 2";

        if (parameters.Error is { } error)
        {
            findings.AddNow(Severity.Warning, "wma.format-unsupported", $"The stream is not analyzed: {error}.", configuration.Parent);
            AddBasicStream(context, parent, name, format, version);
            findings.Flush(context);
            return;
        }

        StandardStream stream = null!;
        StandardNodes nodes = null!;
        stream = new StandardStream(parameters, findings, i => nodes.SuperframeNode(i), i => nodes.FrameNode(i));
        nodes = new StandardNodes(stream);

        int index = 0;
        long unread = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (index > MaxPackets)
            {
                unread++;
                continue;
            }

            stream.Add(packet);
        }

        if (unread > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {MaxPackets} media objects; the {unread} after them are not read, so the statistics and the series cover the first {MaxPackets}.", parent);
        }

        stream.Finish();
        StandardReport.Finish(context, parent, findings, stream, nodes, name, format, version, ConfigurationTraits(version, data, parameters));
        findings.Flush(context);
    }

    // Microsoft's encoders write a samples-per-block of 17 frames, the bit reservoir, variable block lengths and a
    // block size count in the codec data of version 2 (the Windows Media Audio 9.2 files of the project's
    // fixtures, 44.1 kHz and 22.05 kHz); ffmpeg's encoder writes 0 and neither tool.
    private static List<WmaEncoder.Trait> ConfigurationTraits(int version, ReadOnlySpan<byte> data, StandardParameters p)
    {
        var traits = new List<WmaEncoder.Trait>();
        if (version == 2 && data.Length >= 10 && p.UsesBitReservoir && p.UsesVariableBlockLength
            && BinaryPrimitives.ReadUInt32LittleEndian(data) == 17u * (uint)p.FrameLength)
        {
            traits.Add(new WmaEncoder.Trait("Microsoft's Windows Media Audio encoder", "Microsoft", Confidence.Medium,
                $"The codec data states {17 * p.FrameLength} samples per block (17 frames) with the bit reservoir and variable block lengths, as the encoders of the Windows Media Format SDK and of Media Foundation write them"));
        }

        return traits;
    }

    // The nodes of the codec-specific data under the container's node for it.
    private static void AddConfiguration(CodecContext context, CodecConfiguration configuration, FindingCollector findings, int version, StandardParameters parameters)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        int expected = version == 1 ? 4 : 10;
        if (bytes.Length != expected)
        {
            findings.AddNow(bytes.Length < (version == 1 ? 4 : 6) ? Severity.Warning : Severity.Info, "wma.codec-data-size",
                $"The codec-specific data of Windows Media Audio {version} is {bytes.Length} bytes; {(version == 1 ? "FFmpeg's encoder writes 4 (wmaenc.c), the specification defines no version 1 layout" : "the ASF specification, section 11.1.1, has 10")}, and FFmpeg's decoder reads the options from {(version == 1 ? "4" : "6")} bytes on{(bytes.Length < (version == 1 ? 4 : 6) ? ", so it takes none here" : string.Empty)}.", holder);
        }

        Node? optionsNode = null;
        if (version == 2)
        {
            if (bytes.Length >= 4)
            {
                holder.Add("Samples Per Block", offset, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(bytes), "samples"), Explain.SamplesPerBlock, Topics.WmaStandard);
            }

            if (bytes.Length >= 6)
            {
                optionsNode = holder.Add("Encode Options", offset + 4, 2, NodeValue.Hex(parameters.Options, 4), Explain.EncodeOptions, Topics.WmaStandard);
            }

            if (bytes.Length >= 10)
            {
                holder.Add("Super Block Align", offset + 6, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]), "bytes"), Explain.SuperBlockAlign, Topics.WmaStandard);
            }
        }
        else
        {
            if (bytes.Length >= 2)
            {
                holder.Add("Unused", offset, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes), 4), Explain.Version1Unused, Topics.WmaStandard);
            }

            if (bytes.Length >= 4)
            {
                optionsNode = holder.Add("Encode Options", offset + 2, 2, NodeValue.Hex(parameters.Options, 4), Explain.EncodeOptions, Topics.WmaStandard);
            }
        }

        if (bytes.Length > expected)
        {
            holder.Add("Extra Data", offset + expected, bytes.Length - expected, NodeValue.Integer(bytes.Length - expected, "bytes", "byte"), Explain.ExtraData);
        }

        if (optionsNode is not null)
        {
            long at = optionsNode.Offset;
            int options = parameters.Options;
            OptionBits(optionsNode, at, 0, 1, NodeValue.Boolean((options & 1) != 0), "Exponent VLC", Explain.OptionExponentVlc);
            OptionBits(optionsNode, at, 1, 1, NodeValue.Boolean((options & 2) != 0), "Bit Reservoir", Explain.OptionBitReservoir, Topics.WmaBitReservoir);
            OptionBits(optionsNode, at, 2, 1, NodeValue.Boolean((options & 4) != 0), "Variable Block Length", Explain.OptionVariableBlockLength);
            OptionBits(optionsNode, at, 3, 2, NodeValue.Integer((options >> 3) & 3), "Block Size Count Code", Explain.OptionBlockSizeCount);
            OptionBits(optionsNode, at, 5, 11, NodeValue.Hex(options >> 5, 4), "Other Options", Explain.OptionOthers);
            if (parameters.Version == 2 && options == 0xD && (options & 4) != 0 && bytes.Length >= 8)
            {
                findings.AddNow(Severity.Info, "wma.variable-blocks-disabled",
                    "The encode options are 0x000D with variable block length. FFmpeg's decoder disables variable block length for them (wmadec.c, its issue 1503), so frames are read with one block size.", optionsNode);
            }
        }

        _ = context;
    }

    private static void OptionBits(Node options, long offset, int first, int width, NodeValue value, string name, string description, string? topic = null) =>
        options.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: topic, order: BitOrder.LeastSignificantFirst));

    // A stream that is not read: what the container states of it.
    internal static void AddBasicStream(CodecContext context, Node parent, string codec, AudioFormat format, int version)
    {
        var node = parent.Add(new Node(codec, parent.Offset, parent.Length, NodeValue.Text("not analyzed"))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Stream,
            Topic = Topics.WmaStandard,
        });
        context.AddStream(new StreamInfo
        {
            Nodes = [node],
            Codec = codec,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = format.Channels == 1 ? "Mono" : format.Channels == 2 ? "Stereo" : null,
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
        });
    }

    internal static string Count(long count, string singular, string plural) => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
