using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Cancellation and progress through the default parsers, on a file of a few MiB of MPEG frames.
public class CancellationTests
{
    private static readonly byte[] Frames = MakeFrames();

    private static byte[] MakeFrames()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-blade-flags.mp3"));
        return [.. Enumerable.Repeat(file, 50).SelectMany(b => b)];
    }

    [Fact]
    public void ProgressGrowsAndEndsAtTheFileLength()
    {
        var reports = new List<long>();

        Analyzer.CreateDefault().Analyze("frames.mp3", new MemoryStream(Frames), new AnalysisOptions { BytesRead = new SyncProgress(reports.Add) });

        Assert.True(reports.Count > 4, $"{reports.Count} reports");
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.First < pair.Second, $"{pair.First} then {pair.Second}"));
        Assert.Equal(Frames.Length, reports[^1]);
    }

    [Fact]
    public void CancelingMidFileStopsTheAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        var reports = new List<long>();
        var progress = new SyncProgress(value =>
        {
            reports.Add(value);
            if (value >= Frames.Length / 2)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().Analyze(
            "frames.mp3",
            new MemoryStream(Frames),
            new AnalysisOptions { CancellationToken = cancellation.Token, BytesRead = progress }));

        Assert.True(reports[^1] < Frames.Length, $"progress reached {reports[^1]} of {Frames.Length}");
    }

    [Fact]
    public void CancelingDetectionStopsIt()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["frames.mp3"], Frames);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().DetectFormat(folder["frames.mp3"], new AnalysisOptions { CancellationToken = cancellation.Token }));
        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().AnalyzeFile(folder["frames.mp3"], new AnalysisOptions { CancellationToken = cancellation.Token }));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
