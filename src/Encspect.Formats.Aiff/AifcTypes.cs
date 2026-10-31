using Encspect.Core;

namespace Encspect.Formats.Aiff;

// What the compression type of the Common Chunk says about the sound data: the name of the coding, and for the codings that store samples one
// after another, how a sample is laid out. Sources of the types:
// - NONE, ACE2, ACE8, MAC3 and MAC6: the AIFF-C draft of 8/26/91, section 4.0, with the sample frames of the compressed codings in its appendix C
//   (a frame of 2 bytes for 6 samples of MACE 3:1, 1 byte for 6 of MACE 6:1, 1 byte for 2 of ACE 2:1, 3 bytes for 8 of ACE 8:3, for each channel);
// - raw, twos, sowt, in24, in32, fl32, fl64, ulaw, alaw and ima4: Apple's QuickTime File Format Specification (2001-03-01), "Sound Codec Formats
//   Supported" (Table 3-3), whose sound sample description codes AIFF-C files use; ima4's packet of 64 samples in 34 bytes for each channel:
//   Apple's Core Audio Format Specification, "Audio Description Chunk";
// - GSM : no Apple document read lists it; the analyzer names it as its own choice, and the size of its blocks is not stated by a source read.
internal sealed record AifcType(string Id, string Codec, bool IsLinear = false, int? FixedBits = null, int? BytesPerSample = null, bool IsFloat = false, bool IsLittleEndian = false, bool IsUnsigned = false,
    int PacketBytesPerChannel = 0, int PacketSamples = 0)
{
    private static readonly AifcType[] All =
    [
        new("NONE", CodecNames.Pcm, true),
        new("twos", CodecNames.Pcm, true),
        new("sowt", CodecNames.Pcm, true, IsLittleEndian: true),
        new("in24", CodecNames.Pcm, true, 24, 3),
        new("in32", CodecNames.Pcm, true, 32, 4),
        new("raw ", CodecNames.Pcm, true, 8, 1, IsUnsigned: true),
        new("fl32", CodecNames.PcmFloat, true, 32, 4, IsFloat: true),
        new("fl64", CodecNames.PcmFloat, true, 64, 8, IsFloat: true),
        new("alaw", CodecNames.ALaw, BytesPerSample: 1),
        new("ulaw", CodecNames.MuLaw, BytesPerSample: 1),
        new("ima4", "IMA ADPCM", PacketBytesPerChannel: 34, PacketSamples: 64),
        new("GSM ", "GSM 6.10"),
        new("ACE2", "ACE 2-to-1", PacketBytesPerChannel: 1, PacketSamples: 2),
        new("ACE8", "ACE 8-to-3", PacketBytesPerChannel: 3, PacketSamples: 8),
        new("MAC3", "MACE 3-to-1", PacketBytesPerChannel: 2, PacketSamples: 6),
        new("MAC6", "MACE 6-to-1", PacketBytesPerChannel: 1, PacketSamples: 6),
    ];

    private static readonly Dictionary<string, AifcType> ById = All.ToDictionary(t => t.Id, StringComparer.Ordinal);

    public static AifcType? Of(string id) => ById.GetValueOrDefault(id);

    // The bytes of one sample frame of a coding that stores samples one after another, or null for a coding that packs them in blocks.
    public int? FrameBytes(int channels, int sampleSize) => BytesPerSample is { } bytes ? bytes * channels : IsLinear ? ((sampleSize + 7) / 8) * channels : null;
}

// How AIFF and AIFF-C lay out the channels, where the specification says so (section 4.0, "Common Chunk"): stereo is left and right, three
// channels are left, right and center. It lists two conventions for four channels, which disagree on what the channels are, and one for six
// that places a "surround" channel nowhere, so those give no layout.
internal static class AiffLayouts
{
    public static IReadOnlyList<Speaker>? Of(int channels) => channels switch
    {
        1 => ChannelLayouts.Mono,
        2 => ChannelLayouts.Stereo,
        3 => [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter],
        _ => null,
    };
}
