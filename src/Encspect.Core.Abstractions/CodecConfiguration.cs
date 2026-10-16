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

    // The WAVEFORMATEX the container states for the stream (WAV, AVI, ASF), or null: its figures
    // come with the configuration, whose Data then holds the bytes after the structure.
    public AudioFormat? Format { get; init; }

    // The length of the stream in seconds as the container states it (ASF's play duration less the
    // preroll), for codec parsers that do not read the data far enough to count samples; null when
    // it states none.
    public double? Duration { get; init; }

    // The built-in delay of the coding the container states, in seconds (Matroska's CodecDelay, RFC 9559 section 5.1.4.1.25): what a
    // decoder drops from the start of the decoded audio. Null when it states none.
    public double? Delay { get; init; }
}
