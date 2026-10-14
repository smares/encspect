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
    [InlineData("dts", 30)]
    public void ParallelAnalysisMatchesSequential(string kind, int least)
    {
        List<string> files = AudioFixtures(p => kind == "mpeg"
            ? Path.GetExtension(p) is ".mp1" or ".mp2" or ".mp3"
            : Path.GetFileName(Path.GetDirectoryName(p)) == kind && Path.GetExtension(p) is ".ac3" or ".eac3" or ".dts" or ".mp4");
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

    // A node's children are held weakly and built again when read after a collection, or by a second thread that
    // lost the race to publish them: building must give the same nodes every time (the DTS frames' CRC verdicts
    // were consumed by the first build, so a second one read "not checked").
    [Theory]
    [InlineData("xxch-bad-crc.dts")]
    [InlineData("xbr-bad-crc.dts")]
    [InlineData("xxch.dts")]
    public void DtsFrameNodesBuiltAgainMatchTheFirst(string name)
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "dts", name));
        string expected = Fixtures.ToJson(report);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(expected, Fixtures.ToJson(report));
        }

        string[] written = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8).Select(_ =>
        {
            GC.Collect();
            return Fixtures.ToJson(report);
        }).ToArray();
        Assert.All(written, json => Assert.Equal(expected, json));
    }
}
