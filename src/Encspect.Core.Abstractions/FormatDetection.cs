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
// Which candidates are alternatives is the host's policy (plan, "Format detection").
public sealed record FormatDetection(string? Format, SniffResult Evidence, IReadOnlyList<FormatCandidate> Alternatives, bool IsForced = false, string? Detected = null);
