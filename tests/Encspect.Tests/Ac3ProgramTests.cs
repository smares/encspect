using System.Diagnostics;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The programs of an AC-3 or E-AC-3 stream (an independent substream with one configuration, and the dependent
// substreams after its frames): how many there are, what their figures and nodes cover, and how a summary adds
// them up. The inputs that once took half an hour, or lost the whole analysis, are built here; they run beside
// the complexity tests, which are bound by wall-clock time.
[Collection(nameof(ComplexityTests))]
public class Ac3ProgramTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", name));

    private static Report Analyze(byte[] data, string name)
    {
        var watch = Stopwatch.StartNew();
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        return report;
    }

    // Frames that alternate between two configurations: 2/0 and mono. A program per change gave a stream, a
    // statistics node that covered every frame and a finding for each of them (4,000 frames took 21 s, 40,000 half an
    // hour); a configuration that comes back takes its program up again.
    [Fact]
    public void FramesThatAlternateBetweenTwoConfigurationsMakeTwoStreams()
    {
        byte[] source = Fixture("stream-changes.ac3");
        byte[] stereo = source[..768];
        byte[] mono = source[1536..2304];
        const int frames = 12_000;
        var data = new byte[frames * 768];
        for (int i = 0; i < frames; i++)
        {
            (i % 2 == 0 ? stereo : mono).CopyTo(data, i * 768);
        }

        Report report = Analyze(data, "alternate.ac3");

        Assert.Equal(2, report.Streams.Count);
        Assert.Equal([frames / 2 * 1536L, frames / 2 * 1536L], report.Streams.Select(s => s.Samples));
        Finding changed = Assert.Single(report.Findings, f => f.Code == "ac3.stream-changed");
        Assert.Equal(frames - 1, changed.Count);
        Assert.Equal(2, CountNodes(report.Root, NodeKind.Statistics));
    }

    // A file with more programs than the analyzer keeps: independent substreams 0 to 7 in every coding mode and
    // with and without the LFE channel, one frame each, make 128 configurations.
    [Fact]
    public void ProgramsAreCapped()
    {
        byte[] source = Fixture("dependent-repeated.eac3");
        byte[] template = source[..400];
        var data = new List<byte>();
        for (int round = 0; round < 3; round++)
        {
            for (int id = 0; id < 8; id++)
            {
                for (int acmod = 0; acmod < 8; acmod++)
                {
                    for (int lfe = 0; lfe < 2; lfe++)
                    {
                        byte[] frame = [.. template];
                        frame[2] = (byte)((frame[2] & 0xC7) | (id << 3));
                        frame[4] = (byte)((frame[4] & 0xF0) | (acmod << 1) | lfe);
                        data.AddRange(frame);
                    }
                }
            }
        }

        Report report = Analyze([.. data], "many-programs.eac3");

        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, report.Streams.Count);
        Finding capped = Assert.Single(report.Findings, f => f.Code == "ac3.too-many-programs");
        Assert.Equal((128 - Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms) * 3, capped.Count);
        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, CountNodes(report.Root, NodeKind.Statistics));
    }

    // The statistics of a program cover its own frames, from its first to its last, not the whole stream.
    [Fact]
    public void StatisticsCoverTheirProgramsFrames()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/stream-changes.ac3");

        List<Node> statistics = [.. Descendants(report.Root).Where(n => n.Kind == NodeKind.Statistics)];
        Assert.Equal(2, statistics.Count);
        Assert.Equal([(0L, 6 * 768L), (1536L, 2 * 768L)], statistics.Select(s => (s.Offset, s.Length)).Order());
    }

    // 40 dependent frames of 4,096 bytes after one independent frame (160 KiB), which once overflowed a frame's length in
    // the statistics and lost the whole analysis.
    [Fact]
    public void ManyDependentFramesDoNotLoseTheAnalysis()
    {
        byte[] source = Fixture("dependent-repeated.eac3");
        byte[] independent = source[..400];
        byte[] dependent = new byte[4096];
        source.AsSpan(400, 320).CopyTo(dependent);
        dependent[2] = (byte)((dependent[2] & 0xF8) | 0x07);
        dependent[3] = 0xFF;
        byte[] data = [.. independent, .. Enumerable.Range(0, 40).SelectMany(_ => dependent), .. independent, .. independent, .. independent];

        Report report = Analyze(data, "many-dependents.eac3");

        Assert.Single(report.Streams);
        Assert.Equal(4 * 1536, report.Streams[0].Samples);
        Finding excess = Assert.Single(report.Findings, f => f.Code == "ac3.dependent-excess");
        Assert.Equal(39, excess.Count);
    }

    // The independent substreams of one E-AC-3 stream play at the same time: the summary counts the longest once.
    [Fact]
    public void SubstreamsOfOneStreamAreOnePlaybackGroup()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/substreams.eac3");

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal(0.128, summary.Duration!.Value, 6);
        Assert.Equal(4 * 1536, summary.Samples);
    }

    // A stream that changes its configuration is a sequence, not substreams: its programs follow one another and add up.
    [Fact]
    public void ConfigurationChangesAreNotPlayedTogether()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/stream-changes.ac3");

        Assert.All(report.Streams, s => Assert.Null(s.PlaybackGroup));
        Assert.Equal(6 * 1536, ReportSummary.From(report).Samples);
    }

    private static int CountNodes(Node root, NodeKind kind) => Descendants(root).Count(n => n.Kind == kind);

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.Children)
        {
            yield return child;
            if (child.Kind != NodeKind.Frame)
            {
                foreach (Node inner in Descendants(child))
                {
                    yield return inner;
                }
            }
        }
    }
}
