namespace Encspect.Host;

public sealed class AnalysisOptions
{
    public static AnalysisOptions Default { get; } = new();

    // Canceling makes Analyze throw OperationCanceledException.
    public CancellationToken CancellationToken { get; init; }

    // Skips format detection and runs the format parser with this name instead: the name of a
    // format (Analyzer.FormatNames) or the start of it, ignoring case ("mpeg" for MPEG Audio). For
    // a file whose format detection does not find, or finds wrongly. The report says it was forced
    // and what detection would have chosen. An unknown or ambiguous name makes Analyze throw
    // ArgumentException (Analyzer.MatchFormats checks one beforehand). Tags are found as always.
    public string? ForcedFormat { get; init; }

    // How far into the file the format parser has read, as a file offset that only grows, in
    // steps of a few hundred KiB; the file length once the analysis is done. Reported on the
    // analyzing thread.
    public IProgress<long>? BytesRead { get; init; }
}
