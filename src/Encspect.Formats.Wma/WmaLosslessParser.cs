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
            "The frames of Windows Media Audio Lossless are not read yet: the stream is described by what the container and the codec-specific data state, and its packets are counted.", AddConfiguration, channelMaskInData: true);
    }

    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < Layout.Size)
        {
            findings.AddNow(Severity.Warning, "wma.codec-data-size",
                $"The codec-specific data of Windows Media Audio Lossless is {Counts.Of(bytes.Length, "byte", "bytes")}; FFmpeg's decoder needs 18 and refuses fewer.", holder);
            return;
        }

        Node bitsNode = Layout.Add(holder, data, Topics.WmaLossless, lossless: true);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (bits is not (16 or 24))
        {
            findings.AddNow(Severity.Warning, "wma.lossless-bit-depth", $"The sample size is {Counts.Of(bits, "bit", "bits")}; FFmpeg's decoder handles 16 and 24.", bitsNode);
        }
    }
}

// The 18 bytes of codec-specific data that Professional and Lossless share (sample size, channel mask, decode flags), shown as nodes.
internal static class Layout
{
    public const int Size = 18;

    // Returns the Bits Per Sample node. The data must hold at least Size bytes.
    public static Node Add(Node holder, MappedBytes data, string topic, bool lossless)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        Node bitsNode = holder.Add("Bits Per Sample", offset, 2, NodeValue.Integer(bits, "bits"), lossless ? LosslessExplain.BitsPerSample : LosslessExplain.ProBitsPerSample, topic);
        holder.Add("Channel Mask", offset + 2, 4, NodeValue.Hex(BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]), 8), LosslessExplain.ChannelMask, Topics.ChannelLayouts);
        string unknown = lossless ? LosslessExplain.Unknown : LosslessExplain.ProUnknown;
        holder.Add("Unknown 1", offset + 6, 8, NodeValue.Hex(BinaryPrimitives.ReadUInt64LittleEndian(bytes[6..]), 16), unknown);
        int value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        Node flags = holder.Add("Decode Flags", offset + 14, 2, NodeValue.Hex((uint)value, 4), lossless ? LosslessExplain.DecodeFlags : LosslessExplain.ProDecodeFlags, topic);
        holder.Add("Unknown 2", offset + 16, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]), 4), unknown);
        if (bytes.Length > Size)
        {
            holder.Add("Extra Data", offset + Size, bytes.Length - Size, NodeValue.Integer(bytes.Length - Size, "bytes", "byte"), LosslessExplain.ExtraData);
        }

        long at = flags.Offset;
        Bit(flags, at, 0, 1, NodeValue.Integer(value & 1), "Unread Flag", LosslessExplain.FlagOthers, topic);
        Bit(flags, at, 1, 2, NodeValue.Integer((value >> 1) & 3), "Frame Length Adjustment", LosslessExplain.FlagFrameLength, topic);
        Bit(flags, at, 3, 3, NodeValue.Integer((value >> 3) & 7), "Subframe Count Code", LosslessExplain.FlagSubframes, topic);
        Bit(flags, at, 6, 1, NodeValue.Boolean((value & 0x40) != 0), "Has Length Prefix", LosslessExplain.FlagLengthPrefix, topic);
        Bit(flags, at, 7, 1, NodeValue.Boolean((value & 0x80) != 0), "Uses Dynamic Range Compression", LosslessExplain.FlagDynamicRange, topic);
        if (lossless)
        {
            Bit(flags, at, 8, 1, NodeValue.Boolean((value & 0x100) != 0), "Is Version 3 RTM", LosslessExplain.FlagV3Rtm, topic);
            Bit(flags, at, 9, 7, NodeValue.Hex((uint)(value >> 9), 2), "Other Flags", LosslessExplain.FlagOthers, topic);
        }
        else
        {
            Bit(flags, at, 8, 8, NodeValue.Hex((uint)(value >> 8), 2), "Other Flags", LosslessExplain.FlagOthers, topic);
        }

        return bitsNode;
    }

    private static void Bit(Node flags, long offset, int first, int width, NodeValue value, string name, string description, string topic) =>
        flags.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: topic, order: BitOrder.LeastSignificantFirst));
}

internal static class LosslessExplain
{
    public const string ExtraData = "Bytes after the fields the format defines, which decoders ignore.";
    public const string ChannelMask = "Which speakers the channels feed, as a WAVE channel mask (bytes 2 to 5 of the codec-specific data). FFmpeg's decoder takes the channel count from it, and the position of the low-frequency channel (wmaprodec.c).";
    public const string Unknown = "Bytes of the codec-specific data that FFmpeg's decoder does not read.";
    public const string FlagFrameLength = "Raises (2) or lowers (4, 6) the frame length from the one the sample rate gives: 2 doubles it, 4 halves it, 6 quarters it (wma_common.c: ff_wma_get_frame_len_bits).";
    public const string FlagSubframes = "The base-2 logarithm of the most subframes a frame is divided into (wmaprodec.c: max_num_subframes).";
    public const string FlagLengthPrefix = "Set if every frame starts with its length in bits, which lets a decoder find the frames of a packet without reading them (wmaprodec.c: len_prefix).";
    public const string FlagDynamicRange = "Set if every frame carries a gain for dynamic range compression (wmaprodec.c: dynamic_range_compression).";
    public const string FlagOthers = "Bits of the decode flags that FFmpeg's decoder does not read.";
    public const string ProBitsPerSample = "The sample size of the audio in bits (the first two bytes of the codec-specific data).";
    public const string ProUnknown = "Bytes of the codec-specific data whose meaning Encspect does not read.";
    public const string ProDecodeFlags = "The decode flags (bytes 14 and 15 of the codec-specific data): the frame length adjustment (bits 1 and 2), the number of subframes (bits 3 to 5), the length prefix of frames (bit 6) and dynamic range compression (bit 7), as FFmpeg's decoder reads them (wmaprodec.c).";
    public const string BitsPerSample = "The sample size of the audio, 16 or 24 bits (the first two bytes of the codec-specific data); FFmpeg's decoder takes it to decode to 16-bit or 24-bit samples (wmalosslessdec.c).";
    public const string DecodeFlags = "The decode flags (bytes 14 and 15 of the codec-specific data), laid out as Professional's: the frame length adjustment (bits 1 and 2), the number of subframes (bits 3 to 5), the length prefix of frames (bit 6), dynamic range compression (bit 7) and bit 8, which FFmpeg's decoder calls bV3RTM (wmalosslessdec.c).";
    public const string FlagV3Rtm = "A flag that FFmpeg's decoder reads as bV3RTM: it changes where the adaptive filters keep their updates and adds a flag for an LPC filter to each subframe, which FFmpeg's decoder does not support (wmalosslessdec.c).";
}
