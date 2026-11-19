using Encspect.Core;

namespace Encspect.Host;

// The words the host puts on a stream's correction pairing (StreamInfo.Correction), the same for every format: the findings, which point to the
// main file's nodes, and the claim a consistent pair confirms. The parser states the status and the problems; it never words them.
internal static class CorrectionFindings
{
    public const string MissingCode = "correction.missing";
    public const string InconsistentCode = "correction.inconsistent";
    public const string UncheckedCode = "correction.unchecked";
    public const string UnreadableCode = "correction.unreadable";

    public static void Apply(ParseContext context)
    {
        foreach (StreamInfo stream in context.Streams)
        {
            if (stream.Correction is not { } pairing || stream.Nodes.Count == 0)
            {
                continue;
            }

            Node node = stream.Nodes[0];
            string name = pairing.Source ?? "the correction file";
            switch (pairing.Status)
            {
                case CorrectionStatus.Missing:
                    context.AddFinding(Severity.Info, MissingCode,
                        $"No correction file {name} was found beside the file, so the stream is reported as its lossy part alone.", node);
                    break;
                case CorrectionStatus.Inconsistent:
                    foreach (CorrectionProblem problem in pairing.Problems)
                    {
                        context.AddFinding(Severity.Warning, InconsistentCode, $"{Problem(name, problem)}; it is not counted as the lossless pair of the file.", problem.Node,
                            (int)Math.Clamp(problem.Count, 1, int.MaxValue));
                    }

                    break;
                case CorrectionStatus.Unchecked when pairing.IsUnreadable:
                    context.AddFinding(Severity.Warning, UnreadableCode, $"The correction file {name} exists and could not be read: {pairing.Reason}.", node);
                    break;
                case CorrectionStatus.Unchecked:
                    context.AddFinding(Severity.Info, UncheckedCode, $"The correction file {name} was not checked: {pairing.Reason}.", node);
                    break;
            }

            if (pairing.Status == CorrectionStatus.Consistent)
            {
                foreach (StreamClaim claim in stream.Claims.Where(c => c.Kind == StreamClaim.LossyPart))
                {
                    claim.Raise(Confidence.High, $"the correction file {name} beside it belongs with it: {BasisText(pairing)}");
                }
            }
        }
    }

    private static string BasisText(CorrectionPairing pairing) => pairing.Basis == CorrectionBasis.DecodedAudio
        ? "decoding the pair reproduces the checksum of the lossless audio"
        : "its blocks match the file's data blocks and their checksums hold";

    private static string Problem(string name, CorrectionProblem problem) => problem.Kind switch
    {
        CorrectionProblemKind.NotCorrectionData => $"{name} holds no correction data of this format",
        CorrectionProblemKind.StreamDiffers => $"{name} states another stream than the file's (its length, rate, channels or sample format differ)",
        CorrectionProblemKind.BlocksMissing => $"{name} lacks {Counts.Of(problem.Count, "correction block", "correction blocks")} that the file's blocks call for",
        CorrectionProblemKind.BlocksUnmatched => $"{name} holds {Counts.Of(problem.Count, "correction block", "correction blocks")} that no block of the file matches",
        CorrectionProblemKind.ChecksumsFail => $"{Counts.Of(problem.Count, "correction block", "correction blocks")} of {name} {Counts.Agree(problem.Count, "fails its", "fail their")} checksum",
        _ => $"{name} is cut, or holds bytes after its last block",
    };
}

// How a pairing reads in the text output and the front ends, and its words in the JSON and CSV output.
public static class CorrectionText
{
    // The text after "Correction file: ": "consistent (x.wvc)", "inconsistent (x.wvc: 3 correction blocks missing)", "missing (x.wvc)", "none written",
    // "unchecked (no folder to look in: the input is a stream)", then what the status means for the stream: "lossless with its correction file" for a
    // consistent pair, "lossy without its correction file" for the others.
    public static string Describe(CorrectionPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        string status = pairing.Status switch
        {
            CorrectionStatus.Consistent => WithSource("consistent", pairing.Source),
            CorrectionStatus.Inconsistent => $"inconsistent ({(pairing.Source is { } s ? s + ": " : string.Empty)}{string.Join("; ", pairing.Problems.Select(Short))})",
            CorrectionStatus.Missing => WithSource("missing", pairing.Source),
            CorrectionStatus.NotWritten => "none written",
            _ => $"unchecked ({(pairing.IsUnreadable && pairing.Source is { } unreadable ? unreadable + " cannot be read: " : string.Empty)}{pairing.Reason})",
        };
        return pairing.Status switch
        {
            CorrectionStatus.Consistent => $"{status}, lossless with its correction file",
            CorrectionStatus.NotWritten => status,
            _ => $"{status}, lossy without its correction file",
        };
    }

    private static string WithSource(string status, string? source) => source is null ? status : $"{status} ({source})";

    private static string Short(CorrectionProblem problem) => problem.Kind switch
    {
        CorrectionProblemKind.NotCorrectionData => "holds no correction data",
        CorrectionProblemKind.StreamDiffers => "states another stream",
        CorrectionProblemKind.BlocksMissing => $"{Counts.Of(problem.Count, "correction block", "correction blocks")} missing",
        CorrectionProblemKind.BlocksUnmatched => $"{Counts.Of(problem.Count, "correction block", "correction blocks")} match no block",
        CorrectionProblemKind.ChecksumsFail => $"{Counts.Of(problem.Count, "correction block checksum", "correction block checksums")} {Counts.Agree(problem.Count, "fails", "fail")}",
        _ => "cut",
    };

    // The status as JSON and CSV write it.
    public static string Word(CorrectionStatus status) => status switch
    {
        CorrectionStatus.Consistent => "consistent",
        CorrectionStatus.Inconsistent => "inconsistent",
        CorrectionStatus.Missing => "missing",
        CorrectionStatus.NotWritten => "notWritten",
        _ => "unchecked",
    };

    public static string Word(CorrectionBasis basis) => basis == CorrectionBasis.DecodedAudio ? "decodedAudio" : "structure";

    public static string Word(CorrectionProblemKind kind) => kind switch
    {
        CorrectionProblemKind.NotCorrectionData => "notCorrectionData",
        CorrectionProblemKind.StreamDiffers => "streamDiffers",
        CorrectionProblemKind.BlocksMissing => "blocksMissing",
        CorrectionProblemKind.BlocksUnmatched => "blocksUnmatched",
        CorrectionProblemKind.ChecksumsFail => "checksumsFail",
        _ => "cut",
    };
}
