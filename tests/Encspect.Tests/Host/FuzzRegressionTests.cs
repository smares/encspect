using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Inputs the fuzzer once failed on (tests/Encspect.Fuzz), kept so the fixes stay: each must yield a
// report that the fuzzer would accept.
public class FuzzRegressionTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "fuzz"), "*.bin").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task FormerFailuresYieldCleanReports(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name));

        // Some of these once hung, as the fuzzer's 20 s limit found; this fails instead of waiting.
        // The limit counts from the start of the analysis, as in CorruptFiles.
        var started = new TaskCompletionSource();
        Task<Report> work = Task.Run(() =>
        {
            started.SetResult();
            return Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));
        });
        await started.Task;
        Report report = await work.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(data.Length, report.Root.Length);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.All(report.Streams, s => Assert.All(s.NodePaths, p => Assert.NotNull(p)));
        TestFiles.CheckInsideParents(report.Root);

        // As the fuzzer checks: top-level nodes own their bytes, a node with ranges only those.
        long previousEnd = 0;
        foreach (ByteRange range in report.Root.Children.SelectMany(TestFiles.RangesOf).OrderBy(r => r.Offset))
        {
            Assert.True(range.Offset >= previousEnd, $"A region at {range.Offset} overlaps the one before, which ends at {previousEnd}.");
            previousEnd = Math.Max(previousEnd, range.End);
        }
    }
}
