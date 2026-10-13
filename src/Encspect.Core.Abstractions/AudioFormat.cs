namespace Encspect.Core;

// What a container states about audio in the shape of Microsoft's WAVEFORMATEX, which WAV, AVI
// and ASF share (and Matroska's A_MS/ACM): the format tag of the coding and the figures a decoder
// is configured with. The codec-specific bytes after the structure are the configuration's Data.
public sealed record AudioFormat(int FormatTag, int Channels, int SampleRate, int BytesPerSecond, int BlockAlign, int BitsPerSample)
{
    // WAVEFORMATEXTENSIBLE's channel mask and valid bits per sample, where the container states them.
    public uint? ChannelMask { get; init; }

    public int? ValidBitsPerSample { get; init; }

    // The container's number for the stream, for the codec's node to name.
    public int? StreamNumber { get; init; }
}
