using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Findings of one kind are reported once per stream with a count, pointing at the first case.
internal sealed class FindingCollector
{
    private readonly Dictionary<string, (Severity Severity, Node Node, int Count, Func<int, string> Message)> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<Finding> _immediate = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Node, object> _detached = new();

    // The "Boxes Not Shown" node of the parent where the box being read was hidden (the tree is over its node budget): findings made while such a box
    // is read, and findings about a node it held, point to it, since those nodes are in no tree.
    public Node? CurrentNote { get; set; }

    public bool IsDetaching { get; set; }

    public bool IsNodeLimitReported { get; set; }

    public void MarkDetached(Node node, Node note)
    {
        _detached.AddOrUpdate(node, note);
        foreach (Node child in node.Children)
        {
            MarkDetached(child, note);
        }
    }

    public bool IsDetached(Node node) => _detached.TryGetValue(node, out _);

    // The node itself, or where it is in no tree the node that stands for the box it lay in.
    public Node Resolve(Node node) =>
        IsDetaching && CurrentNote is not null ? CurrentNote : _detached.TryGetValue(node, out object? note) ? (Node)note : node;

    public void Add(Severity severity, string code, Node node, Func<int, string> message)
    {
        if (_aggregated.TryGetValue(code, out var existing))
        {
            _aggregated[code] = existing with { Count = existing.Count + 1 };
            return;
        }

        _aggregated[code] = (severity, Resolve(node), 1, message);
        _order.Add(code);
    }

    public void AddNow(Severity severity, string code, string message, Node node) => _immediate.Add(new Finding(severity, code, message, Resolve(node)));

    public void Flush(AnalysisContext context)
    {
        foreach (Finding finding in _immediate)
        {
            context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node);
        }

        foreach (string code in _order)
        {
            var entry = _aggregated[code];
            context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node, entry.Count);
        }

        _immediate.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
