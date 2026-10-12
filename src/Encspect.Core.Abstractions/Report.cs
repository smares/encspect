namespace Encspect.Core;

public sealed class Report
{
    public Report(string source, Node root, IReadOnlyList<Finding> findings, IReadOnlyList<StreamInfo>? streams = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(findings);

        Source = source;
        Root = root;
        Findings = findings;
        Streams = streams ?? [];
    }

    public string Source { get; }

    public Node Root { get; }

    public IReadOnlyList<Finding> Findings { get; }

    // The audio streams the format parser found, in file order.
    public IReadOnlyList<StreamInfo> Streams { get; }
}
