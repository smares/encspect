using Encspect.Core;

namespace Encspect.Formats.Aac;

// The 56 bits of adts_fixed_header and adts_variable_header (ISO/IEC 14496-3 1.A.2.2.1, Tables
// 1.A.6 and 1.A.7; ISO/IEC 13818-7 6.2), in the low bits of Raw.
internal readonly record struct AdtsHeader(ulong Raw)
{
    public const int Length = 7;

    // syncword 0xFFF and layer 00 (1.A.3.2.1): an MPEG audio frame header has a layer other than 00,
    // so the two never take each other's headers.
    public bool IsValid => Sync == 0xFFF && Layer == 0 && SampleRateIndex < 13 && FrameLength >= HeaderLength;

    public int Sync => (int)(Raw >> 44) & 0xFFF;

    // 0 for MPEG-4, 1 for MPEG-2.
    public int Id => (int)(Raw >> 43) & 1;

    public int Layer => (int)(Raw >> 41) & 3;

    public bool IsProtected => ((Raw >> 40) & 1) == 0;

    public int Profile => (int)(Raw >> 38) & 3;

    public int SampleRateIndex => (int)(Raw >> 34) & 0xF;

    public bool IsPrivate => ((Raw >> 33) & 1) != 0;

    public int ChannelConfiguration => (int)(Raw >> 30) & 7;

    public bool IsOriginal => ((Raw >> 29) & 1) != 0;

    public bool IsHome => ((Raw >> 28) & 1) != 0;

    public bool CopyrightIdBit => ((Raw >> 27) & 1) != 0;

    public bool IsCopyrightIdStart => ((Raw >> 26) & 1) != 0;

    public int FrameLength => (int)(Raw >> 13) & 0x1FFF;

    public int BufferFullness => (int)(Raw >> 2) & 0x7FF;

    public int RawDataBlocks => (int)(Raw & 3) + 1;

    // 0x7FF signals a variable bitrate (1.A.3.2.1).
    public bool IsVbr => BufferFullness == 0x7FF;

    // The header with its raw_data_block_position fields and CRC, where it is protected.
    public int HeaderLength => Length + (IsProtected ? 2 + (2 * (RawDataBlocks - 1)) : 0);

    public int SampleRate => AacTables.SampleRate(SampleRateIndex) ?? 0;

    // The audio object type: profile_ObjectType plus one (1.A.3.2.1); in MPEG-2 ADTS the profiles
    // Main, LC and SSR have the same numbers, and 3 is reserved.
    public int ObjectType => Profile + 1;

    // Whether the fields that adts_fixed_header fixes for the whole stream agree, from the sync word
    // to the channel configuration, but for the private bit, which only the application uses.
    public bool IsSameStream(AdtsHeader other) => ((Raw >> 30) & ~(1UL << 3)) == ((other.Raw >> 30) & ~(1UL << 3));

    public static AdtsHeader Read(ReadOnlySpan<byte> bytes)
    {
        ulong raw = 0;
        for (int i = 0; i < Length; i++)
        {
            raw = (raw << 8) | bytes[i];
        }

        return new AdtsHeader(raw);
    }

    public string ProfileName => Id == 1
        ? Profile switch { 0 => "Main", 1 => "LC", 2 => "SSR", _ => "reserved" }
        : AacTables.ObjectTypeName(ObjectType);

    public AacConfig Config => new()
    {
        ObjectType = ObjectType,
        CoreSampleRate = SampleRate,
        ChannelConfiguration = ChannelConfiguration,
        Channels = ChannelConfiguration == 0 ? null : AacTables.Channels(ChannelConfiguration),
        CoreFrameLength = 1024,
    };

    public NodeValue Summary(int length) =>
        $"{length} bytes, {(RawDataBlocks == 1 ? "1 raw data block" : $"{RawDataBlocks} raw data blocks")}";

    // The header's fields at their bits, from the frame's offset (1.A.2.2.1).
    public IEnumerable<Node> Nodes(long offset)
    {
        yield return Node.FromBits("Sync Word", offset, 0, 12, NodeValue.Hex(Sync, 3));
        yield return Node.FromBits("Version", offset, 12, 13, Id == 0 ? "MPEG-4" : "MPEG-2");
        yield return Node.FromBits("Layer", offset, 13, 15, NodeValue.Integer(Layer));
        yield return Node.FromBits("IsProtected", offset, 15, 16, NodeValue.Boolean(IsProtected));
        yield return Node.FromBits("Profile", offset, 16, 18, $"{Profile} ({ProfileName})");
        yield return Node.FromBits("Sample Rate", offset, 18, 22, AacTables.SampleRate(SampleRateIndex) is { } rate ? NodeValue.Integer(rate, "Hz") : $"{SampleRateIndex} (reserved)");
        yield return Node.FromBits("IsPrivate", offset, 22, 23, NodeValue.Boolean(IsPrivate));
        yield return Node.FromBits("Channel Configuration", offset, 23, 26, $"{ChannelConfiguration} ({AacTables.ChannelLayout(ChannelConfiguration)})");
        yield return Node.FromBits("IsOriginal", offset, 26, 27, NodeValue.Boolean(IsOriginal));
        yield return Node.FromBits("IsHome", offset, 27, 28, NodeValue.Boolean(IsHome));
        yield return Node.FromBits("Copyright ID Bit", offset, 28, 29, NodeValue.Integer(CopyrightIdBit ? 1 : 0));
        yield return Node.FromBits("IsCopyrightIdStart", offset, 29, 30, NodeValue.Boolean(IsCopyrightIdStart));
        yield return Node.FromBits("Frame Length", offset, 30, 43, NodeValue.Integer(FrameLength, "bytes"));
        yield return Node.FromBits("Buffer Fullness", offset, 43, 54, IsVbr ? "0x7FF (variable bitrate)" : NodeValue.Integer(BufferFullness));
        yield return Node.FromBits("Raw Data Blocks", offset, 54, 56, NodeValue.Integer(RawDataBlocks));
    }
}
