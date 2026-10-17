using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Lossless (the format tag 0x0163). Read at header level: the 18 bytes of codec-specific data
// as FFmpeg's wmalosslessdec.c reads them (the sample size, the channel mask and the decode flags, laid out as
// those of Professional), and the packets counted. The frames are not read yet.
public sealed class WmaLosslessParser : ICodecParser
{
    public const string CodecId = "wma-lossless";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WmaHeaderOnly.Parse(context, "Windows Media Audio Lossless", Topics.WmaLossless, "wma.lossless-not-read",
            "The frames of Windows Media Audio Lossless are not read yet: the stream is described by what the container and the codec-specific data state, and its packets are counted.", AddConfiguration);
    }

    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        if (bytes.Length < ProParameters.ExtraDataSize)
        {
            findings.AddNow(Severity.Warning, "wma.codec-data-size",
                $"The codec-specific data of Windows Media Audio Lossless is {Counts.Of(bytes.Length, "byte", "bytes")}; FFmpeg's decoder needs 18 and refuses fewer.", holder);
            return;
        }

        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        holder.Add("Bits Per Sample", offset, 2, NodeValue.Integer(bits, "bits"), LosslessExplain.BitsPerSample, Topics.WmaLossless);
        if (bits is not (16 or 24))
        {
            findings.AddNow(Severity.Warning, "wma.lossless-bit-depth", $"The sample size is {Counts.Of(bits, "bit", "bits")}; FFmpeg's decoder handles 16 and 24.", holder.Children[^1]);
        }

        holder.Add("Channel Mask", offset + 2, 4, NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]), 8), ProExplain.ChannelMask, Topics.ChannelLayouts);
        holder.Add("Unknown 1", offset + 6, 8, NodeValue.Hex(BinaryPrimitives.ReadUInt64LittleEndian(bytes[6..]), 16), ProExplain.Unknown);
        int value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        Node flags = holder.Add("Decode Flags", offset + 14, 2, NodeValue.Hex((uint)value, 4), LosslessExplain.DecodeFlags, Topics.WmaLossless);
        holder.Add("Unknown 2", offset + 16, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]), 4), ProExplain.Unknown);
        if (bytes.Length > ProParameters.ExtraDataSize)
        {
            holder.Add("Extra Data", offset + ProParameters.ExtraDataSize, bytes.Length - ProParameters.ExtraDataSize, NodeValue.Integer(bytes.Length - ProParameters.ExtraDataSize, "bytes", "byte"), ProExplain.ExtraData);
        }

        long at = flags.Offset;
        Bit(flags, at, 0, 1, NodeValue.Integer(value & 1), "Unread Flag", ProExplain.FlagOthers);
        Bit(flags, at, 1, 2, NodeValue.Integer((value >> 1) & 3), "Frame Length Adjustment", ProExplain.FlagFrameLength);
        Bit(flags, at, 3, 3, NodeValue.Integer((value >> 3) & 7), "Subframe Count Code", ProExplain.FlagSubframes);
        Bit(flags, at, 6, 1, NodeValue.Boolean((value & 0x40) != 0), "Has Length Prefix", ProExplain.FlagLengthPrefix);
        Bit(flags, at, 7, 1, NodeValue.Boolean((value & 0x80) != 0), "Uses Dynamic Range Compression", ProExplain.FlagDynamicRange);
        Bit(flags, at, 8, 1, NodeValue.Boolean((value & 0x100) != 0), "Is Version 3 RTM", LosslessExplain.FlagV3Rtm);
        Bit(flags, at, 9, 7, NodeValue.Hex((uint)(value >> 9), 2), "Other Flags", ProExplain.FlagOthers);
    }

    private static void Bit(Node flags, long offset, int first, int width, NodeValue value, string name, string description) =>
        flags.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: Topics.WmaLossless, order: BitOrder.LeastSignificantFirst));
}

internal static class LosslessExplain
{
    public const string BitsPerSample = "The sample size of the audio, 16 or 24 bits (the first two bytes of the codec-specific data); FFmpeg's decoder takes it to decode to 16-bit or 24-bit samples (wmalosslessdec.c).";
    public const string DecodeFlags = "The decode flags (bytes 14 and 15 of the codec-specific data), laid out as Professional's: the frame length adjustment (bits 1 and 2), the number of subframes (bits 3 to 5), the length prefix of frames (bit 6), dynamic range compression (bit 7) and bit 8, which FFmpeg's decoder calls bV3RTM (wmalosslessdec.c).";
    public const string FlagV3Rtm = "A flag that FFmpeg's decoder reads as bV3RTM: it changes where the adaptive filters keep their updates and adds a flag for an LPC filter to each subframe, which FFmpeg's decoder does not support (wmalosslessdec.c).";
}
