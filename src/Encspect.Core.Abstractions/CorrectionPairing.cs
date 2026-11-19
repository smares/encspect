namespace Encspect.Core;

public enum CorrectionStatus
{
    // The correction file belongs to the stream and holds together with it, as far as Basis checks.
    Consistent,

    // The correction file was found and does not hold with the stream (Problems says how).
    Inconsistent,

    // The host looked for the correction file and found none.
    Missing,

    // The stream's own header says the encoder wrote no correction file, and none was found.
    NotWritten,

    // The host did not look, or could not read the file (Reason says why).
    Unchecked,
}

// What a consistent pairing rests on.
public enum CorrectionBasis
{
    // The structure and the checksums of the coded data of both files.
    Structure,

    // Decoding both files together (WavPack 3, whose correction file has no structure).
    DecodedAudio,
}

public enum CorrectionProblemKind
{
    // The companion holds no correction data of this format; an empty file too.
    NotCorrectionData,

    // The companion states another stream.
    StreamDiffers,

    // The companion lacks correction blocks that the main blocks call for.
    BlocksMissing,

    // The companion has blocks that no main block matches.
    BlocksUnmatched,

    // Correction blocks whose checksums over their coded data fail.
    ChecksumsFail,

    // The companion is cut, or bytes follow its last block.
    Cut,
}

// One kind of problem between the files, with how many cases and the main tree's node of the first.
public sealed record CorrectionProblem(CorrectionProblemKind Kind, long Count, Node Node);

// The state of a stream's correction file, which the parser sets (StreamInfo.Correction) and the host words as findings and lines, so that every format
// reads the same.
public sealed record CorrectionPairing
{
    public required CorrectionStatus Status { get; init; }

    // The companion's name, where the host looked for one.
    public string? Source { get; init; }

    public CorrectionBasis Basis { get; init; } = CorrectionBasis.Structure;

    public IReadOnlyList<CorrectionProblem> Problems { get; init; } = [];

    // Why the pair is unchecked.
    public string? Reason { get; init; }

    // True where the file exists and could not be read (Status is Unchecked).
    public bool IsUnreadable { get; init; }

    // Whether the pair is lossless with its correction file: only a consistent pair is.
    public bool IsLossless => Status == CorrectionStatus.Consistent;
}
