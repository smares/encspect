using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Findings of one kind are reported once with a count, pointing at the first case; the first
// case's node is only made when it occurs.
internal sealed class Findings
{
    private readonly Dictionary<string, Entry> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<Finding> _immediate = [];

    public void Add(Severity severity, string code, Func<Node> node, Func<int, string> message)
    {
        if (_aggregated.TryGetValue(code, out Entry? existing))
        {
            existing.Count++;
            return;
        }

        _aggregated[code] = new Entry(severity, node(), message);
        _order.Add(code);
    }

    public void AddNow(Severity severity, string code, string message, Node node) => _immediate.Add(new Finding(severity, code, message, node));

    public void Flush(AnalysisContext context)
    {
        foreach (Finding finding in _immediate)
        {
            context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node);
        }

        foreach (string code in _order)
        {
            Entry entry = _aggregated[code];
            context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node, entry.Count);
        }

        _immediate.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    private sealed class Entry(Severity severity, Node node, Func<int, string> message)
    {
        public Severity Severity => severity;

        public Node Node => node;

        public Func<int, string> Message => message;

        public int Count { get; set; } = 1;
    }
}
