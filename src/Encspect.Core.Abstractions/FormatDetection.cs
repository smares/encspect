namespace Encspect.Core;

// A format another parser also found evidence for, with that evidence.
public sealed record FormatCandidate(string Format, SniffResult Evidence);

// How the host chose the format parser, for a report whose choice is not the whole story: other
// formats with real evidence (Alternatives), or an analysis that was forced to a format.
//
// Format is the parser that ran and Evidence its own sniff, which is none for a forced format that
// does not match. When the analysis is forced, Detected is the format detection would have chosen,
// or null if it would have found none, and Alternatives are the others that matched.
//
// A candidate is an alternative when its evidence is real, not a stray header-like match: it has
// the same class as the chosen format, or, above SniffResult.WeakClass, at least one frame that
// passes a checksum, or a chain of at least AlternativeFrames consistent frames. A candidate that
// starts inside the data a container declares for itself (IFormatParser.DeclaredEnd) is that
// container's payload and not listed.
public sealed record FormatDetection(string? Format, SniffResult Evidence, IReadOnlyList<FormatCandidate> Alternatives, bool IsForced = false, string? Detected = null)
{
    public const int AlternativeFrames = 8;
}
