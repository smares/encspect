namespace Encspect.Core;

public enum ChecksumCoverage
{
    // A header only: it vouches for no audio.
    Header,

    // Coded bytes that hold audio.
    CodedData,

    // The samples: only a decoder can compare it.
    DecodedAudio,
}

// One kind of checksum a stream stores, declared by the parser, and how its stored values compared.
public sealed record ChecksumTally
{
    // The format's name for it, as its field is named: "frame CRC-16", "STREAMINFO MD5", "lossless check, 6-channel
    // presentation". The decoder's results find their tally by it.
    public required string Name { get; init; }

    public required ChecksumCoverage Covers { get; init; }

    // Whether the stored values together cover all of the stream's audio: a checksum of the whole stream, or one that
    // every frame of the stream carries where no frame is missing (no gap, and the samples equal the count the stream
    // states, where it states one: STREAMINFO, the TTA or Monkey's Audio header, WavPack's total samples). The parser
    // sets it. Never for a header checksum, and never for TrueHD's lossless check, which leaves the last interval of
    // each segment unchecked.
    public bool CoversAllAudio { get; init; }

    // Stored values, those compared, and those of them that differ.
    public long Stored { get; init; }

    public long Checked { get; init; }

    public long Failed { get; init; }

    // Not compared: decoding off or stopped, a frame not decoded, a frame the data cuts short.
    public long NotChecked => Stored - Checked;
}

public enum DecodeStatus
{
    // A decoder exists, but the front end did not ask for decoding.
    NotRequested,

    NoDecoder,

    // The stream cannot be decoded as it is (TTA's encrypted format, a configuration the decoder refuses, a WavPack
    // correction file alone).
    NotPossible,

    // Every frame handed was decoded, or reported where it could not be.
    Decoded,

    // The front end stopped decoding or the budget ran out. (A sink that throws leaves no report: Analyze rethrows.)
    Stopped,

    // The decoder failed (decode.failed), a bug.
    Failed,
}

public sealed record StreamVerification
{
    public required DecodeStatus Decoding { get; init; }

    // The samples per channel the decoder decoded, the sum of what IFrameDecoder.Decode returned.
    public long DecodedSamples { get; init; }

    // True where the audio the checksums vouch for is a lossy coding's output: a WavPack hybrid file decoded without its
    // correction file. The host copies it from DecodeSetup.IsLossy, which the parser sets. Where IsVerified is true, the
    // text output and the GUI say "verified (lossy stream)"; JSON writes isLossy where it is true.
    public bool IsLossy { get; init; }

    public IReadOnlyList<ChecksumTally> Checksums { get; init; } = [];

    // False for a stream of a coding that stores no checksum at all (PCM), whose verification only says whether the audio was decoded: the text
    // and JSON reports and the front ends' "Verified" line leave such a verification out.
    public bool HasChecksums => Checksums.Count > 0;

    // False where a stored checksum differs. True where every stored checksum was compared and holds, and at least one
    // that was compared covers all of the stream's audio. Null otherwise.
    public bool? IsVerified =>
        Checksums.Any(c => c.Failed > 0) ? false
        : Checksums.Any(c => c.NotChecked > 0) ? null
        : Checksums.Any(c => c.CoversAllAudio && c.Checked > 0) ? true
        : null;
}
