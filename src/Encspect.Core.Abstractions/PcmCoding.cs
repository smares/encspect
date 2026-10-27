namespace Encspect.Core;

public enum PcmEncoding
{
    SignedInteger,

    // 8-bit WAV and AIFF-C "raw ": the decoder subtracts 128.
    UnsignedInteger,

    Float,

    // G.711, expanded to 16-bit integers.
    ALaw,

    MuLaw,
}

// How the samples of a data chunk are laid out, in terms neutral to the container (WAV's WAVEFORMATEX cannot say big-endian and AIFF's Common Chunk is no
// WAVEFORMATEX). The parser of WAV, Wave64, AIFF or AIFF-C states it in DecodeSetup.Pcm when it starts a session with the decoder "pcm"
// (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder"); every sample frame holds Channels samples one after another.
public sealed record PcmCoding
{
    // The codec ID of the decoder, which parsers of different projects start sessions with.
    public const string CodecId = "pcm";

    public required PcmEncoding Encoding { get; init; }

    public required bool IsBigEndian { get; init; }

    // Bits one sample takes in the data, a whole number of bytes: 8, 16, 24 or 32 for integers, 32 or 64 for floats, 8 for A-law and mu-law. Other widths are
    // stated as they are and the decoder refuses them (decode.not-possible).
    public required int ContainerBits { get; init; }

    // The sample rate the container states, or 0 where it states none. A rate over int.MaxValue is stated as int.MaxValue.
    public int SampleRate { get; init; }

    // The speakers of the channels where the container states them.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }

    // The width of a sample of the decoder's output, which DecodeSetup.BitsPerSample states: the container's, except that A-law and mu-law are expanded to 16 bits.
    public int OutputBits => Encoding is PcmEncoding.ALaw or PcmEncoding.MuLaw ? 16 : ContainerBits;

    // Bytes of one sample frame in the data.
    public long FrameBytes(int channels) => (long)(ContainerBits / 8) * channels;
}
