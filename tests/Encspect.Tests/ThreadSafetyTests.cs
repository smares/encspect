using System.Collections.Concurrent;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Reports build the nodes of repeated parts when they are read, in blocks, from compact records, hold
// them weakly and build them again after a collection; two threads reading one block may both build
// it. These tests read reports the ways that could go wrong: on several threads, with collections in
// between, and past more than one block, which no committed fixture is. They force no collections from
// other threads, so they run beside the other tests (see ThreadTestScope for what a pull request samples).
public class ThreadSafetyTests
{
    internal const int BlockSize = 1024;

    private static readonly string[] SkippedExtensions = [".json", ".md", ".txt", ".sh", ".py"];

    public static TheoryData<string> FixtureDirectories()
    {
        var data = new TheoryData<string>();
        foreach (string directory in Directory.EnumerateDirectories(Fixtures.FixtureDirectory).Order(StringComparer.Ordinal))
        {
            if (FixturesIn(Path.GetFileName(directory)).Count > 0)
            {
                data.Add(Path.GetFileName(directory));
            }
        }

        return data;
    }

    public static TheoryData<string> LongInputNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in LongInputs.Names)
        {
            data.Add(name);
        }

        return data;
    }

    internal static List<string> FixturesIn(string directory) =>
        Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, directory), "*", SearchOption.AllDirectories)
            .Where(p => !SkippedExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static Report AnalyzeLong(string name) => Analyzer.CreateDefault().Analyze(name, new MemoryStream(LongInputs.Build(name)));

    // Nodes built again after a collection are the nodes built first. Every report of the directory is
    // written, then one collection drops the nodes none of them holds (their children are held weakly), then
    // each is written again.
    [Theory]
    [MemberData(nameof(FixtureDirectories))]
    public void RebuiltAfterCollectionMatches(string directory)
    {
        var analyzer = Analyzer.CreateDefault();
        List<string> files = ThreadTestScope.Sample(FixturesIn(directory));
        var reports = new List<Report>();
        var first = new List<string>();
        foreach (string file in files)
        {
            Report report = analyzer.AnalyzeFile(file);
            reports.Add(report);
            first.Add(ReportRendering.All(report));
        }

        ReportRendering.Collect();

        var differing = new List<string>();
        string detail = string.Empty;
        for (int i = 0; i < files.Count; i++)
        {
            if (ReportRendering.All(reports[i]) != first[i])
            {
                differing.Add(files[i]);
                detail = detail.Length > 0 ? detail : ReportRendering.Differences(reports[i], analyzer.AnalyzeFile(files[i]));
            }
        }

        Assert.True(differing.Count == 0, $"Written again after a collection, these differ: {string.Join(", ", differing)}. First: {detail}");
    }

    [Fact]
    public void EveryLongInputHasAListPastTwoBlocks()
    {
        var lacking = new List<string>();
        foreach (string name in LongInputs.Names)
        {
            Report report = AnalyzeLong(name);
            if (!LongLists(report.Root).Any(p => p.Count > 2 * BlockSize))
            {
                lacking.Add(name);
            }
        }

        Assert.True(lacking.Count == 0, $"No list of children longer than {2 * BlockSize} in: {string.Join(", ", lacking)}.");
    }

    // The paths (child indexes) of the lists of more than one block of children, looked for in the first
    // children of every node down to a depth of 6.
    internal static List<(int[] Path, int Count)> LongLists(Node root)
    {
        var found = new List<(int[], int)>();
        void Find(Node node, List<int> path, int depth)
        {
            IReadOnlyList<Node> children = node.Children;
            if (children.Count > BlockSize)
            {
                found.Add(([.. path], children.Count));
            }

            if (depth >= 6)
            {
                return;
            }

            for (int i = 0; i < Math.Min(children.Count, 64); i++)
            {
                path.Add(i);
                Find(children[i], path, depth + 1);
                path.RemoveAt(path.Count - 1);
            }
        }

        Find(root, [], 0);
        return found;
    }

    internal static Node Follow(Node root, int[] path)
    {
        Node node = root;
        foreach (int i in path)
        {
            node = node.Children[i];
        }

        return node;
    }
}
