namespace Encspect.Host;

public sealed class AnalysisOptions
{
    public static AnalysisOptions Default { get; } = new();

    // Canceling makes Analyze throw OperationCanceledException.
    public CancellationToken CancellationToken { get; init; }

    // How far into the file the format parser has read, as a file offset that only grows, in
    // steps of a few hundred KiB; the file length once the analysis is done. Reported on the
    // analyzing thread.
    public IProgress<long>? BytesRead { get; init; }
}
