namespace Encspect.Core;

public enum Severity
{
    Info,
    Warning,
    Error,
}

// Node is a node of the report's tree, or one equal to it in Name, Offset and Length: frames are
// built on demand, so a parser points at a field it has not built. The host finds it after parsing
// and sets Path. Where several nodes are equal, the node itself is the one the path leads to if the tree holds it.
public sealed record Finding(Severity Severity, string Code, string Message, Node? Node = null)
{
    // How many cases the finding stands for: findings of one kind are reported once per stream,
    // pointing at the first case.
    public int Count { get; init; } = 1;

    // The child indexes that lead from the report's root to Node; empty for the root itself, null
    // when the finding has no node or the node is not in the tree.
    public IReadOnlyList<int>? Path { get; init; }
}
