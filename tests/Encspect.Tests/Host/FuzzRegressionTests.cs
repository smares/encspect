using Encspect.Core;
using Encspect.Host;
using Encspect.Tests.Decoding;

namespace Encspect.Tests.Host;

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

        // As the fuzzer's second pass: decoding with its work limit and measuring, which a decoder's find needs to show again.
        Report decoded = await Task.Run(() => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data),
            new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = 1 << 24, Output = _ => new DecoderWorkTests.NullSink(), Measure = new MeasureOptions { MaxWork = 1 << 24 } } })).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(decoded.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
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
