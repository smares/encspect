using Encspect.Core;

namespace Encspect.Formats.Dts;

// Findings of one kind are reported once with a count, pointing at the first case; the first
// case's node is only made when it occurs.
internal sealed class Findings
{
    private readonly Dictionary<string, Entry> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<(Finding Finding, Counted? Counted)> _immediate = [];

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

    // Counts one more case of a kind already reported; false where none is, so the caller builds
    // the first case's finding.
    public bool CountIfKnown(string code)
    {
        if (_aggregated.TryGetValue(code, out Entry? existing))
        {
            existing.Count++;
            return true;
        }

        return false;
    }

    public void AddNow(Severity severity, string code, string message, Node node) => _immediate.Add((new Finding(severity, code, message, node), null));

    // A finding that keeps its place among the immediate ones and is counted: the caller raises Count for each further case, and the
    // message is made when the findings are flushed.
    public Counted AddNowCounted(Severity severity, string code, Node node, Func<int, string> message)
    {
        var counted = new Counted(severity, code, node, message);
        _immediate.Add((new Finding(severity, code, string.Empty, node), counted));
        return counted;
    }

    public void Flush(AnalysisContext context)
    {
        foreach ((Finding finding, Counted? counted) in _immediate)
        {
            if (counted is not null)
            {
                context.AddFinding(counted.Severity, counted.Code, counted.Message(counted.Count), counted.Node, counted.Count);
                continue;
            }

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

    internal sealed class Counted(Severity severity, string code, Node node, Func<int, string> message)
    {
        public Severity Severity { get; set; } = severity;

        public string Code => code;

        public Node Node => node;

        public Func<int, string> Message => message;

        public int Count { get; set; } = 1;
    }

    private sealed class Entry(Severity severity, Node node, Func<int, string> message)
    {
        public Severity Severity => severity;

        public Node Node => node;

        public Func<int, string> Message => message;

        public int Count { get; set; } = 1;
    }
}
