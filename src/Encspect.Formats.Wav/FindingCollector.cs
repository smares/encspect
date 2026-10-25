using Encspect.Core;

namespace Encspect.Formats.Wav;

// Findings of one code and one case (the same problem, as the optional case argument tells) are
// reported once per stream with a count, pointing at the first case. A code that stands for several
// problems gets a case for each, so the message of the first never names another's.
internal sealed class FindingCollector
{
    private readonly Dictionary<(string Code, string Case), (Severity Severity, Func<Node> Node, int Count, Func<int, string> Message)> _aggregated = [];
    private readonly List<(string Code, string Case)> _order = [];
    private readonly List<Finding> _immediate = [];

    // While set, findings added with a node point to the node it returns instead: the findings of blocks that the tree does not list (their node
    // is not in the tree) point to the node that stands for them. It is read when the findings are reported.
    public Func<Node>? Redirect { get; set; }

    public void Add(Severity severity, string code, Node node, Func<int, string> message, string problem = "")
    {
        Func<Node>? redirect = Redirect;
        Add(severity, code, redirect ?? (() => node), message, problem);
    }

    // node is built only for the first case, and only when the findings are reported, so a finding
    // repeated over many units builds one node, and a node that grows while the units are read (a run
    // of packets) is built as it ends.
    public void Add(Severity severity, string code, Func<Node> node, Func<int, string> message, string problem = "")
    {
        (string, string) key = (code, problem);
        if (_aggregated.TryGetValue(key, out var existing))
        {
            _aggregated[key] = existing with { Count = existing.Count + 1 };
            return;
        }

        _aggregated[key] = (severity, node, 1, message);
        _order.Add(key);
    }

    public void AddNow(Severity severity, string code, string message, Node node) => _immediate.Add(new Finding(severity, code, message, node));

    public void Flush(AnalysisContext context)
    {
        foreach (Finding finding in _immediate)
        {
            context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node);
        }

        foreach ((string code, string problem) in _order)
        {
            var entry = _aggregated[(code, problem)];
            context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node(), entry.Count);
        }

        _immediate.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
