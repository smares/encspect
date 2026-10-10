namespace Encspect.Core;

public sealed class Report
{
    public Report(string source, Node root, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(findings);

        Source = source;
        Root = root;
        Findings = findings;
    }

    public string Source { get; }

    public Node Root { get; }

    public IReadOnlyList<Finding> Findings { get; }
}
