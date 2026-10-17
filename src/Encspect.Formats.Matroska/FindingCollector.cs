using Encspect.Core;

namespace Encspect.Formats.Matroska;

// Findings of one kind are reported once per stream with a count, pointing at the first case.
internal sealed class FindingCollector
{
    private readonly Dictionary<string, (Severity Severity, Func<Node> Node, int Count, Func<int, string> Message)> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<Finding> _immediate = [];

    public void Add(Severity severity, string code, Node node, Func<int, string> message) => Add(severity, code, () => node, message);

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

    // Findings that stand for one element each (a track, a Segment) are listed up to MaxListed of a kind; the rest are counted and said so in one
    // more, at the last one listed, so that a file of a hundred thousand empty tracks does not raise a million findings.
    internal const int MaxListed = 100;

    private readonly Dictionary<string, int> _listed = [];
    private readonly Dictionary<string, (Finding Last, int Skipped)> _overflow = [];

    public void AddNow(Severity severity, string code, string message, Node node, int count = 1)
    {
        var finding = new Finding(severity, code, message, node) { Count = count };
        int listed = _listed.GetValueOrDefault(code);
        if (listed < MaxListed)
        {
            _listed[code] = listed + 1;
            _immediate.Add(finding);
            if (listed + 1 == MaxListed)
            {
                _overflow[code] = (finding, 0);
            }

            return;
        }

        (Finding last, int skipped) = _overflow[code];
        _overflow[code] = (last, skipped + count);
    }

    public void Flush(AnalysisContext context)
    {
        foreach (Finding finding in _immediate)
        {
            context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node, finding.Count);
        }

        foreach ((string code, (Finding last, int skipped)) in _overflow.Select(p => (p.Key, p.Value)))
        {
            if (skipped > 0)
            {
                context.AddFinding(last.Severity, code, $"{(skipped == 1 ? "1 more finding of this kind is" : $"{skipped} more findings of this kind are")} not listed; the first {MaxListed} are above.", last.Node, skipped);
            }
        }

        foreach (string code in _order)
        {
            var entry = _aggregated[code];
            context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node(), entry.Count);
        }

        _immediate.Clear();
        _listed.Clear();
        _overflow.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
