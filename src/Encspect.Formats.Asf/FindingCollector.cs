using Encspect.Core;

namespace Encspect.Formats.Asf;

// Findings of one kind are reported once per stream with a count, pointing at the first case.
internal sealed class FindingCollector
{
    private readonly Dictionary<string, (Severity Severity, string Code, Func<Node> Node, int Count, Func<int, string> Message)> _aggregated = [];
    private readonly List<string> _order = [];
    private readonly List<Finding> _immediate = [];

    public void Add(Severity severity, string code, Node node, Func<int, string> message) => Add(severity, code, () => node, message);

    // node is built only for the first case, and only when the findings are reported, so a finding
    // repeated over many units builds one node, and a node that grows while the units are read (a run
    // of packets) is built as it ends.
    public void Add(Severity severity, string code, Func<Node> node, Func<int, string> message) => Add(severity, code, null, node, message);

    // A finding that is reported once per stream: cases are counted per code and stream, and each stream's
    // report names its own stream number.
    public void Add(Severity severity, string code, int streamNumber, Node node, Func<int, string> message) => Add(severity, code, streamNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), () => node, message);

    private void Add(Severity severity, string code, string? scope, Func<Node> node, Func<int, string> message)
    {
        string key = scope is null ? code : $"{code}#{scope}";
        if (_aggregated.TryGetValue(key, out var existing))
        {
            _aggregated[key] = existing with { Count = existing.Count + 1 };
            return;
        }

        _aggregated[key] = (severity, code, node, 1, message);
        _order.Add(key);
    }

    public void AddNow(Severity severity, string code, string message, Node node, int count = 1) => _immediate.Add(new Finding(severity, code, message, node) { Count = count });

    public void Flush(AnalysisContext context)
    {
        foreach (Finding finding in _immediate)
        {
            context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node, finding.Count);
        }

        foreach (string key in _order)
        {
            var entry = _aggregated[key];
            context.AddFinding(entry.Severity, entry.Code, entry.Message(entry.Count), entry.Node(), entry.Count);
        }

        _immediate.Clear();
        _aggregated.Clear();
        _order.Clear();
    }

    public static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
