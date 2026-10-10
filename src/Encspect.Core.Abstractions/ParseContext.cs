namespace Encspect.Core;

public sealed class ParseContext
{
    private readonly List<Finding> _findings = [];

    public ParseContext(ByteReader reader, Node root)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(root);

        Reader = reader;
        Root = root;
    }

    public ByteReader Reader { get; }

    public Node Root { get; }

    public IReadOnlyList<Finding> Findings => _findings;

    public void AddFinding(Severity severity, string code, string message, Node? node = null)
    {
        _findings.Add(new Finding(severity, code, message, node));
    }
}
