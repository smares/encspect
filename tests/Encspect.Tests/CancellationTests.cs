using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Cancellation and progress through the default parsers, on a file of a few MiB of MPEG or AC-3
// frames.
public class CancellationTests
{
    public static TheoryData<string> Sources => ["mpeg/external-blade-flags.mp3", "ac3/external-ffmpeg-5.1-448k.ac3", "wavpack/external-lossless-16-stereo-small-blocks.wv", "dts/external-ffmpeg-5.1-48k-768k.dts", "flac/external-16-stereo-l5.flac", "truehd/external-truehd-51-48k-24.thd"];

    private static byte[] MakeFrames(string source)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, source));
        return [.. Enumerable.Repeat(file, 50).SelectMany(b => b)];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void ProgressGrowsAndEndsAtTheFileLength(string source)
    {
        byte[] frames = MakeFrames(source);
        var reports = new List<long>();

        Analyzer.CreateDefault().Analyze("frames" + Path.GetExtension(source), new MemoryStream(frames), new AnalysisOptions { BytesRead = new SyncProgress(reports.Add) });

        Assert.True(reports.Count > 4, $"{reports.Count} reports");
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.First < pair.Second, $"{pair.First} then {pair.Second}"));
        Assert.Equal(frames.Length, reports[^1]);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void CancelingMidFileStopsTheAnalysis(string source)
    {
        byte[] frames = MakeFrames(source);
        using var cancellation = new CancellationTokenSource();
        var reports = new List<long>();
        var progress = new SyncProgress(value =>
        {
            reports.Add(value);
            if (value >= frames.Length / 2)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().Analyze(
            "frames" + Path.GetExtension(source),
            new MemoryStream(frames),
            new AnalysisOptions { CancellationToken = cancellation.Token, BytesRead = progress }));

        Assert.True(reports[^1] < frames.Length, $"progress reached {reports[^1]} of {frames.Length}");
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void CancelingDetectionStopsIt(string source)
    {
        using var folder = new TempFolder();
        string name = "frames" + Path.GetExtension(source);
        File.WriteAllBytes(folder[name], MakeFrames(source));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().DetectFormat(folder[name], new AnalysisOptions { CancellationToken = cancellation.Token }));
        Assert.Throws<OperationCanceledException>(() => Analyzer.CreateDefault().AnalyzeFile(folder[name], new AnalysisOptions { CancellationToken = cancellation.Token }));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
