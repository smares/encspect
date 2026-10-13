using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Professional (the format tag 0x0162), handed over by a container as the media
// objects of one stream (ASF: ParsePackets) with the WAVEFORMATEX and its 18 bytes of codec-specific
// data. Read as FFmpeg's wmaprodec.c reads it: a media object is a packet of the block alignment's size,
// which holds the end of the frame before and then frames, each prefixed with its length; a frame is
// divided into subframes per channel. The subframes are read down to their scale factors and
// coefficient codes, without building the audio, so that the report knows where each frame ends and what
// it holds.
public sealed class WmaProParser : ICodecParser
{
    public const string CodecId = "wma-pro";

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

        ReadOnlySpan<byte> data = configuration.Data.Span;
        ProParameters parameters = ProParameters.Create(format, data);
        var findings = new FindingCollector();
        Node parent = context.Parent;

        AddConfiguration(configuration, findings, parameters);

        if (parameters.Error is { } error)
        {
            findings.AddNow(Severity.Warning, "wma.format-unsupported", $"The stream is not analyzed: {error}.", configuration.Parent);
            WmaStandardParser.AddBasicStream(context, parent, ProReport.CodecName, format, 3);
            findings.Flush(context);
            return;
        }

        if (!parameters.HasLengthPrefix)
        {
            findings.AddNow(Severity.Info, "wma.no-length-prefix",
                "The decode flags do not set the length prefix of frames, so the frames of the packets cannot be told apart without reading them all across packets, which this parser does not do; the packets are listed and their frames are not read.", configuration.Parent);
        }

        ProStream stream = null!;
        ProNodes nodes = null!;
        stream = new ProStream(parameters, findings, i => nodes.PacketNode(i), i => nodes.FrameNode(i));
        nodes = new ProNodes(stream);

        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            stream.Add(packet);
        }

        stream.Finish();
        ProReport.Finish(context, parent, findings, stream, nodes, format);
        findings.Flush(context);
    }

    // The nodes of the codec-specific data under the container's node for it.
    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings, ProParameters parameters)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        if (bytes.Length < ProParameters.ExtraDataSize)
        {
            findings.AddNow(Severity.Warning, "wma.codec-data-size",
                $"The codec-specific data of Windows Media Audio Professional is {bytes.Length} bytes; FFmpeg's decoder needs 18 and refuses fewer.", holder);
            return;
        }

        holder.Add("Bits Per Sample", offset, 2, NodeValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(bytes), "bits"), ProExplain.BitsPerSample, Topics.WmaPro);
        holder.Add("Channel Mask", offset + 2, 4, NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]), 8), ProExplain.ChannelMask, Topics.ChannelLayouts);
        holder.Add("Unknown 1", offset + 6, 8, NodeValue.Hex(BinaryPrimitives.ReadUInt64LittleEndian(bytes[6..]), 16), ProExplain.Unknown);
        Node flags = holder.Add("Decode Flags", offset + 14, 2, NodeValue.Hex((uint)parameters.DecodeFlags, 4), ProExplain.DecodeFlags, Topics.WmaPro);
        {
            holder.Add("Unknown 2", offset + 16, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]), 4), ProExplain.Unknown);
        }

        if (bytes.Length > ProParameters.ExtraDataSize)
        {
            holder.Add("Extra Data", offset + ProParameters.ExtraDataSize, bytes.Length - ProParameters.ExtraDataSize, NodeValue.Integer(bytes.Length - ProParameters.ExtraDataSize, "bytes", "byte"), ProExplain.ExtraData);
        }

        long at = flags.Offset;
        int value = parameters.DecodeFlags;
        FlagBits(flags, at, 0, 1, NodeValue.Integer(value & 1), "Unread Flag", ProExplain.FlagOthers);
        FlagBits(flags, at, 1, 2, NodeValue.Integer((value >> 1) & 3), "Frame Length Adjustment", ProExplain.FlagFrameLength);
        FlagBits(flags, at, 3, 3, NodeValue.Integer((value >> 3) & 7), "Subframe Count Code", ProExplain.FlagSubframes);
        FlagBits(flags, at, 6, 1, NodeValue.Boolean((value & 0x40) != 0), "Length Prefix", ProExplain.FlagLengthPrefix);
        FlagBits(flags, at, 7, 1, NodeValue.Boolean((value & 0x80) != 0), "Dynamic Range Compression", ProExplain.FlagDynamicRange);
        FlagBits(flags, at, 8, 8, NodeValue.Hex((uint)(value >> 8), 2), "Other Flags", ProExplain.FlagOthers);
    }

    private static void FlagBits(Node flags, long offset, int first, int width, NodeValue value, string name, string description) =>
        flags.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: Topics.WmaPro, order: BitOrder.LeastSignificantFirst));
}
