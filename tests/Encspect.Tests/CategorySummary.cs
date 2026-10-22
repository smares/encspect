using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Tests;

// Holds every category series of a report equal to the tally node it is anchored to, by the table in
// category-summary.json (docs/design/coding-graphs.md, "The summary over the stream"). The tally covers a region and the
// series a stream, so the comparison is made per connected group: the streams that share an anchor or a node form one
// group, and it sums the series over its streams against the tallies over its distinct anchors.
internal sealed partial class CategorySummary
{
    private readonly Dictionary<string, Entry> _entries;

    private CategorySummary(Dictionary<string, Entry> entries) => _entries = entries;

    public static string TablePath { get; } = Path.Combine(Fixtures.FixtureDirectory, "..", "Encspect.Tests", "category-summary.json");

    public IReadOnlyCollection<string> Names => _entries.Keys;

    public static CategorySummary Load() => Parse(File.ReadAllText(TablePath));

    public static CategorySummary Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("series").EnumerateObject())
        {
            JsonElement value = property.Value;
            string form = value.GetProperty("form").GetString()!;
            if (form is not ("entries" or "flag"))
            {
                throw new InvalidDataException($"{property.Name}: form \"{form}\" is neither entries nor flag.");
            }

            entries.Add(property.Name, new Entry(
                form,
                value.GetProperty("anchor").GetString()!,
                value.GetProperty("denominator").GetString()!,
                value.TryGetProperty("withCategory", out JsonElement with) ? with.GetInt32() : 0,
                value.TryGetProperty("findings", out JsonElement findings) ? [.. findings.EnumerateArray().Select(f => f.GetString()!)] : []));
        }

        return new CategorySummary(entries);
    }

    public List<string> Check(Report report)
    {
        var problems = new List<string>();
        IReadOnlyList<StreamInfo> streams = report.Streams;
        var parent = Enumerable.Range(0, streams.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var owner = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < streams.Count; i++)
        {
            foreach (Node node in streams[i].Nodes.Concat(streams[i].CategorySeries.Select(s => s.Anchor).OfType<Node>()))
            {
                if (owner.TryGetValue(node, out int other))
                {
                    parent[Find(i)] = Find(other);
                }
                else
                {
                    owner[node] = i;
                }
            }
        }

        foreach (IGrouping<int, int> group in Enumerable.Range(0, streams.Count).GroupBy(Find))
        {
            foreach (IGrouping<string, CategorySeries> named in group.SelectMany(i => streams[i].CategorySeries).GroupBy(s => s.Name))
            {
                CheckGroup(report, named.Key, [.. named], [.. group.SelectMany(i => streams[i].Nodes).Distinct(ReferenceEqualityComparer.Instance).Cast<Node>()], problems);
            }
        }

        return problems;
    }

    private void CheckGroup(Report report, string name, List<CategorySeries> series, List<Node> regions, List<string> problems)
    {
        if (!_entries.TryGetValue(name, out Entry? entry))
        {
            problems.Add($"{name}: the series is not in category-summary.json");
            return;
        }

        if (series.Any(s => s.Anchor is null || s.Anchor.Name != entry.Anchor))
        {
            problems.Add($"{name}: the series is anchored to {string.Join(", ", series.Select(s => s.Anchor?.Name ?? "no node").Distinct())}, not to {entry.Anchor}");
            return;
        }

        // The tally covers a region and the series a stream: the tallies of the group's regions are summed, and the series of its
        // streams. A series' own anchor counts too where it lies outside the streams' nodes.
        var anchors = new List<Node>();
        foreach (Node region in regions)
        {
            if (FindByName(region, entry.Anchor, 0) is { } found && !anchors.Contains(found, ReferenceEqualityComparer.Instance))
            {
                anchors.Add(found);
            }
        }

        foreach (Node own in series.Select(s => s.Anchor!))
        {
            if (!anchors.Contains(own, ReferenceEqualityComparer.Instance))
            {
                anchors.Add(own);
            }
        }

        bool mayBeSmaller = report.Findings.Any(f => entry.Findings.Contains(f.Code));
        long seriesTotal = series.Sum(s => s.Total);
        long denominator = 0;
        foreach (Node anchor in anchors)
        {
            if (Denominator(report.Root, anchor, entry.Denominator) is not { } value)
            {
                problems.Add($"{name}: no node {entry.Denominator} with a whole number beside {anchor.Name}");
                return;
            }

            denominator += value;
        }

        Compare(name, "total", seriesTotal, denominator, mayBeSmaller, problems);
        if (entry.Form == "flag")
        {
            if (series.Any(s => s.Categories.Count != 2))
            {
                problems.Add($"{name}: a flag series has two categories");
                return;
            }

            long tally = anchors.Sum(a => Count(a.Value) ?? 0);
            long with = series.Sum(s => s.Counts[entry.WithCategory]);
            Compare(name, series[0].Categories[entry.WithCategory].Name, with, tally, mayBeSmaller, problems);
            return;
        }

        for (int category = 0; category < series[0].Categories.Count; category++)
        {
            string entryName = series[0].Categories[category].Name;

            // A tally lists the entries that occur: a missing one is 0.
            long tally = anchors.Sum(a => a.Children.FirstOrDefault(c => c.Name == entryName) is { } child ? Count(child.Value) ?? -1_000_000_000 : 0);
            Compare(name, entryName, series.Sum(s => s.Counts[category]), tally, mayBeSmaller, problems);
        }
    }

    // The count of a tally entry: a typed integer, or the text the parsers write ("4 granules (1.7%)", "1,234 frames (50.0%)"),
    // whose leading number is the count. The tallies keep the share in the text, and retyping them would change every golden,
    // so the harness, which is test code, reads the text.
    public static long? Count(NodeValue? value)
    {
        if (value?.AsInteger() is { } integer)
        {
            return integer;
        }

        if (value?.AsText() is { } text && LeadingCount().Match(text) is { Success: true } match
            && long.TryParse(match.Groups[1].ValueSpan, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long count))
        {
            return count;
        }

        return null;
    }

    [GeneratedRegex(@"^\s*([0-9][0-9,]*)(?![0-9])")]
    private static partial Regex LeadingCount();

    private static Node? FindByName(Node node, string name, int depth)
    {
        if (node.Name == name)
        {
            return node;
        }

        if (depth >= 6 || !node.HasChildren)
        {
            return null;
        }

        foreach (Node child in node.Children)
        {
            if (child.Kind != NodeKind.Frame && FindByName(child, name, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Compare(string name, string what, long actual, long expected, bool mayBeSmaller, List<string> problems)
    {
        if (actual == expected || (mayBeSmaller && actual < expected))
        {
            return;
        }

        problems.Add($"{name}: {what} is {actual} in the series and {expected} in the tally");
    }

    // The value of the node with the name that lies beside the anchor, else the first below its grandparent.
    private static long? Denominator(Node root, Node anchor, string name)
    {
        var path = new List<Node>();
        if (!PathTo(root, anchor, path))
        {
            return null;
        }

        for (int up = 2; up <= 3; up++)
        {
            Node scope = path[Math.Max(0, path.Count - up)];
            if (Search(scope, name, 0) is { } found)
            {
                return Count(found.Value);
            }
        }

        return null;
    }

    private static bool PathTo(Node node, Node target, List<Node> path)
    {
        path.Add(node);
        if (ReferenceEquals(node, target))
        {
            return true;
        }

        if (!node.HasChildren)
        {
            path.RemoveAt(path.Count - 1);
            return false;
        }

        foreach (Node child in node.Children)
        {
            if (child.Offset <= target.Offset && target.Offset < child.Offset + Math.Max(1, child.Length) && PathTo(child, target, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static Node? Search(Node node, string name, int depth)
    {
        if (node.Name == name && Count(node.Value) is not null)
        {
            return node;
        }

        if (depth >= 6 || !node.HasChildren)
        {
            return null;
        }

        foreach (Node child in node.Children)
        {
            if (child.Kind != NodeKind.Frame && Search(child, name, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed record Entry(string Form, string Anchor, string Denominator, int WithCategory, IReadOnlyList<string> Findings);
}
