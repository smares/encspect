using System.Collections.Concurrent;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// One analyzer, and so one instance of every parser, serves all threads (DefaultParsers), so
// parsers keep no per-call state; reports read on several threads at once build the same nodes.
public class ParallelAnalysisTests
{
    private static List<string> AudioFixtures(Func<string, bool> include) =>
        Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories)
            .Where(include)
            .Order(StringComparer.Ordinal)
            .ToList();

    [Theory]
    [InlineData("mpeg", 300)]
    [InlineData("ac3", 30)]
    public void ParallelAnalysisMatchesSequential(string kind, int least)
    {
        List<string> files = AudioFixtures(p => kind == "mpeg"
            ? Path.GetExtension(p) is ".mp1" or ".mp2" or ".mp3"
            : Path.GetFileName(Path.GetDirectoryName(p)) == "ac3" && Path.GetExtension(p) is ".ac3" or ".eac3" or ".mp4");
        var analyzer = Analyzer.CreateDefault();
        Dictionary<string, string> sequential = files.ToDictionary(f => f, f => Fixtures.ToJson(analyzer.AnalyzeFile(f)));

        var parallel = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, f => parallel[f] = Fixtures.ToJson(analyzer.AnalyzeFile(f)));

        Assert.True(files.Count > least, $"{files.Count} fixtures");
        Assert.DoesNotContain(files, f => parallel[f] != sequential[f]);
    }

    [Fact]
    public void OneReportWrittenOnSeveralThreadsAtOnce()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-blade-flags.mp3"));
        string expected = Fixtures.ToJson(report);

        string[] written = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ => Fixtures.ToJson(report)).ToArray();

        Assert.All(written, json => Assert.Equal(expected, json));
    }
}
