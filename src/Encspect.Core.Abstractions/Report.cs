namespace Encspect.Core;

public sealed class Report
{
    public Report(string source, Node root, IReadOnlyList<Finding> findings, IReadOnlyList<StreamInfo>? streams = null, FormatDetection? detection = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(findings);

        Source = source;
        Root = root;
        Findings = findings;
        Streams = streams ?? [];
        Detection = detection;
    }

    public string Source { get; }

    public Node Root { get; }

    public IReadOnlyList<Finding> Findings { get; }

    // The audio streams the format parser found, in file order.
    public IReadOnlyList<StreamInfo> Streams { get; }

    // Set when other formats also matched or the analysis was forced to a format; null for the
    // plain case of one format detected.
    public FormatDetection? Detection { get; }
}
