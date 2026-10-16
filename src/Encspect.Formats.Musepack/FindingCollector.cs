using Encspect.Core;

namespace Encspect.Formats.Musepack;

// Findings of one kind are reported once per stream with a count, pointing at the first case.
internal sealed class FindingCollector
{
    private readonly Dictionary<string, (Severity Severity, Func<Node> Node, int Count, Func<int, string> Message)> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<Finding> _immediate = [];

    public void Add(Severity severity, string code, Node node, Func<int, string> message) => Add(severity, code, () => node, message);

    // True, and counted, when a finding of this code is already aggregated, so its caller builds no
    // delegates for the cases after the first.
    public bool Repeat(string code)
    {
        if (!_aggregated.TryGetValue(code, out var existing))
        {
            return false;
        }

        _aggregated[code] = existing with { Count = existing.Count + 1 };
        return true;
    }

    // node is built only for the first case, and only when the findings are reported, so a finding
    // repeated over many units builds one node, and a node that grows while the units are read (a run
    // of packets) is built as it ends.
    public void Add(Severity severity, string code, Func<Node> node, Func<int, string> message)
    {
        if (_aggregated.TryGetValue(code, out var existing))
        {
            _aggregated[code] = existing with { Count = existing.Count + 1 };
            return;
        }

        _aggregated[code] = (severity, node, 1, message);
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
            var entry = _aggregated[code];
            context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node(), entry.Count);
        }

        _immediate.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
