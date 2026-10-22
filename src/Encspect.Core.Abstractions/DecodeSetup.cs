namespace Encspect.Core;

// What the parser states when it starts a session (AnalysisContext.StartDecoding).
public sealed record DecodeSetup
{
    // The parser's node of the stream, the first of StreamInfo.Nodes: findings about the whole stream point to it.
    public required Node Stream { get; init; }

    public required int Channels { get; init; }

    // The width of a sample in the canonical layout's terms: ceil(BitsPerSample / 8) bytes count toward the budget. For
    // DSD, 8, with one byte of DSD per channel as one sample.
    public required int BitsPerSample { get; init; }

    // The samples per channel the parser counts in the stream, or null where it cannot tell.
    public long? Samples { get; init; }

    // The configuration as the parser read it (FLAC's STREAMINFO block, ALAC's magic cookie, a TTA header), which
    // the decoder reads with the parser's code; null for a coding whose frames carry it (TrueHD's major sync).
    public MappedBytes? Configuration { get; init; }

    // The WAVEFORMATEX a container states (ASF's WMA Lossless, WAV's PCM), or null.
    public AudioFormat? Format { get; init; }

    // True where the stream's decoded audio is a lossy coding's output (a WavPack hybrid file without its correction
    // file); the host copies it into StreamVerification.IsLossy.
    public bool IsLossy { get; init; }
}

// The unit the coding decodes one at a time, as the parser read it: a frame, a block or an access unit; a packet where
// the coding's frames are not byte-aligned (WMA Lossless), whose decoder then finds the frames with the parser's code.
//
// Bytes: the unit's bytes, mapped to the file. NodeName, with the file range of Bytes, names the parser's node of the
// unit (equal in name, offset and length), which the host builds for a finding only when one is reported.
// FirstSample: the unit's position in the stream in samples per channel. Samples: how many the parser says it codes.
public readonly record struct CodedFrame(MappedBytes Bytes, string NodeName, long FirstSample, int Samples)
{
    // The parser found the frame damaged: a checksum over its coded data fails, or the data ends inside it. The decoder
    // still decodes what it can, so a whole-stream checksum then says the audio differs.
    public bool IsDamaged { get; init; }

    // The parser's walk stored the frame's warm-up samples and residuals in the buffers the session lent (Lend), so the
    // decoder does not read them again: it takes them from DecodeContext.LentBuffers.
    public bool IsWalked { get; init; }

    // The parser could not read the unit at all (its syntax breaks) and reported that itself. The host counts it as not decoded, so the
    // stream is incomplete, and reports nothing more; the decoder is not called for it.
    public bool IsUnreadable { get; init; }

    // Frames the parser counted in the stream were lost before this one (a gap, a resync), so the decoder resets
    // what it carries from frame to frame and the whole-stream checksums cannot be compared.
    public bool FollowsGap { get; init; }
}
