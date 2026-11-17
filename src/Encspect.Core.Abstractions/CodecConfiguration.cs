namespace Encspect.Core;

// What a container stores about a coding apart from its data, handed to the codec parser along
// with the data: MP4 keeps AAC's AudioSpecificConfig in the esds box inside moov, far from the
// samples in mdat. Data maps to the file, so the codec parser builds its nodes for it at the
// container's bytes, under Parent, the container's node that holds them; its other nodes go under
// the node the data is handed with.
public sealed record CodecConfiguration
{
    public CodecConfiguration(MappedBytes data, Node parent)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(parent);

        Data = data;
        Parent = parent;
    }

    // The configuration as the container stores it, such as MP4's DecoderSpecificInfo.
    public MappedBytes Data { get; }

    public Node Parent { get; }

    // What the container says about the bitrate: true for a variable bitrate, false for a constant
    // one, null when it says nothing (MP4's DecoderConfigDescriptor sets avgBitrate to 0 for a
    // variable bitrate, ISO/IEC 14496-1 7.2.6.6.2).
    public bool? IsVbr { get; init; }

    // What the container read about this stream's coding that says something about the encoder (MP4's layout of one track), in addition to the claims
    // of the whole file. The codec parser sees them with the file's, after them, and the caller's claims do not gain them; they take effect
    // only through the host's router. What the container states about the stream itself, not the coding, goes in the route's claims. Record equality compares this list by reference.
    public IReadOnlyList<EncoderClaim> Claims { get; init; } = [];

    // True where the stream has no comment header of its own to name a writer, and the container's claims about the file name it (MP4's ©too atom for
    // Opus, as for AAC and AC-3). Matroska resolves the writer of such a stream itself, so it leaves this false.
    public bool IsWriterFromContainer { get; init; }

    // The container's reader of the whole file, for a codec parser that reads its packets again after a first pass (the MPEG audio parser keeps their file ranges, not their
    // bytes): it reads the packets' file ranges on demand. A parser that reads from it puts the position back where it was, so the container's own reads go on undisturbed;
    // null where the container hands over none.
    public ByteReader? Source { get; init; }

    // Whether the bytes of every packet are the file's bytes of its ranges with each pair of bytes swapped (the 16-bit words of IEC 61937 payloads, which the file holds as
    // samples); false for packets that are the file's bytes as they are.
    public bool IsWordSwapped { get; init; }

    // The WAVEFORMATEX the container states for the stream (WAV, AVI, ASF), or null: its figures
    // come with the configuration, whose Data then holds the bytes after the structure.
    public AudioFormat? Format { get; init; }

    // How the container says the samples of a PCM stream are laid out (MP4's pcmC box and QuickTime's sample entries), for the codec parser "pcm"; null for every other
    // coding. The figures that a WAVEFORMATEX would hold (channels, rate) come in Format.
    public PcmCoding? Pcm { get; init; }

    // The bytes of the stream's data that the container holds, for a codec parser that counts samples from them without reading the packets (the codec
    // parser "pcm", whose packets are read only where the decoder runs); null where the container does not state it.
    public long? DataBytes { get; init; }

    // The length of the stream in seconds as the container states it (ASF's play duration less the
    // preroll), for codec parsers that do not read the data far enough to count samples; null when
    // it states none.
    public double? Duration { get; init; }

    // The same length in samples per channel at the sample rate of the container's format for the stream, rounded to the nearest sample with halves up; null
    // where Duration is null, the format states no rate or the figure does not fit a long.
    public long? DurationSamples { get; init; }

    // True where the container knows the stream's samples are encrypted: it hands over no packets,
    // since ciphertext is no valid coding, and the codec parser describes the stream from the
    // configuration alone, without findings about frames it did not see.
    public bool AreSamplesEncrypted { get; init; }

    // The built-in delay of the coding the container states, in seconds (Matroska's CodecDelay, RFC 9559 section 5.1.4.1.25): what a
    // decoder drops from the start of the decoded audio. Null when it states none.
    public double? Delay { get; init; }

    // The padding at the end of the last block that the container states, in seconds (Matroska's DiscardPadding, RFC 9559 section 5.1.3.5.7): what a
    // decoder drops from the end of the decoded audio. Null when it states none or a negative one.
    public double? DiscardPadding { get; init; }

    // The frames (packets) of the block that carries the DiscardPadding: the most samples the padding can take, with the frames before it of the same block (RFC 9559
    // section 5.1.3.5.7 gives it per Block, and a laced Block holds several frames). Null when it is not known.
    public int? DiscardPaddingFrames { get; init; }

    // Whether the DiscardPadding is what an edit list leaves unpresented at the end of the track (MP4), not a Matroska block's element; only the finding's wording differs.
    public bool IsDiscardPaddingFromEditList { get; init; }
}
