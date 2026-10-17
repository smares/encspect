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

    // Frames that alternate between two configurations: 2/0 and mono. A program per change gave a stream, a statistics node that
    // covered every frame and a finding for each of them (4,000 frames took 21 s, 40,000 half an hour); now the programs are
    // capped, the later frames stay in the last one, and the changes are one counted finding.
    [Fact]
    public void FramesThatAlternateBetweenTwoConfigurationsMakeAtMostTheCappedPrograms()
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

        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, report.Streams.Count);
        Assert.Equal(frames * 1536L, report.Streams.Sum(s => s.Samples));
        Assert.DoesNotContain(report.Findings, f => f.Code == "ac3.programs-limited");
        Finding changed = Assert.Single(report.Findings, f => f.Code == "ac3.stream-changed");
        Assert.Equal(frames - 1, changed.Count);
        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, CountNodes(report.Root, NodeKind.Statistics));
    }

    // Independent substreams 0 to 7 in every coding mode and with and without the LFE channel: 128 configurations, each frame
    // a change for its substream.
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
        // The 64 configurations of substreams 0 to 3 come first and come back in the next rounds, which join their programs; the
        // frames of substreams 4 to 7 have no program and are in no stream.
        Assert.Equal(3 * 64 * 1536L, report.Streams.Sum(s => s.Samples));
        Assert.All(report.Streams, s => Assert.Equal(3 * 1536L, s.Samples));
        Finding left = Assert.Single(report.Findings, f => f.Code == "ac3.programs-limited");
        Assert.Equal(Severity.Warning, left.Severity);
        Assert.Equal(3 * 64, left.Count);
        Assert.Single(report.Findings, f => f.Code == "ac3.stream-changed");
        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, CountNodes(report.Root, NodeKind.Statistics));
    }

    // A sample rate that is new past the cap: the frames are in no program, and the programs keep their own rate and layout.
    [Fact]
    public void ASampleRateChangePastTheCapIsLeftOut()
    {
        byte[] stereo = Fixture("stream-changes.ac3")[..768];
        byte[] mono = Fixture("stream-changes.ac3")[1536..2304];
        byte[] rate32 = Fixture("external-ffmpeg-stereo-32k-320k.ac3");
        rate32 = rate32[..SyncLength(rate32)];
        var data = new List<byte>();
        for (int i = 0; i < 40; i++)
        {
            data.AddRange(stereo);
            data.AddRange(mono);
        }

        for (int i = 0; i < 200; i++)
        {
            data.AddRange(rate32);
        }

        Report report = Analyze([.. data], "rate-cap.ac3");

        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(48000, s.SampleRate));
        Assert.Equal(80 * 1536L, report.Streams.Sum(s => s.Samples));
        Finding left = Assert.Single(report.Findings, f => f.Code == "ac3.programs-limited");
        Assert.Equal(Severity.Warning, left.Severity);
        Assert.Equal(200, left.Count);
    }

    // A substream ID that is new past the cap: its frames are in no program, never in another substream's.
    [Fact]
    public void ANewSubstreamPastTheCapIsLeftOut()
    {
        byte[] template = Fixture("dependent-repeated.eac3")[..400];
        byte[] Frame(int id, int acmod)
        {
            byte[] frame = [.. template];
            frame[2] = (byte)((frame[2] & 0xC7) | (id << 3));
            frame[4] = (byte)((frame[4] & 0xF0) | (acmod << 1));
            return frame;
        }

        var data = new List<byte>();
        for (int i = 0; i < 40; i++)
        {
            data.AddRange(Frame(0, 2));
            data.AddRange(Frame(0, 1));
        }

        for (int i = 0; i < 100; i++)
        {
            data.AddRange(Frame(0, 2));
            data.AddRange(Frame(1, 7));
        }

        Report report = Analyze([.. data], "late-substream.eac3");

        Assert.Equal(Encspect.Formats.Ac3.Ac3Analysis.MaxPrograms, report.Streams.Count);
        Assert.Equal((80 + 100) * 1536L, report.Streams.Sum(s => s.Samples));
        Assert.All(report.Streams, s => Assert.True(s.Channels is 1 or 2));
        Finding left = Assert.Single(report.Findings, f => f.Code == "ac3.programs-limited");
        Assert.Equal(100, left.Count);
    }

    private static int SyncLength(byte[] data) => Encspect.Formats.Ac3.SyncFrame.Read(data).FrameLength;

    // The statistics of a program cover its own frames, not the whole stream.
    [Fact]
    public void StatisticsCoverTheirProgramsFrames()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/stream-changes.ac3");

        List<Node> statistics = [.. Descendants(report.Root).Where(n => n.Kind == NodeKind.Statistics)];
        Assert.Equal(3, statistics.Count);
        Assert.Equal([(0L, 2 * 768L), (1536L, 2 * 768L), (3072L, 2 * 768L)], statistics.Select(s => (s.Offset, s.Length)).Order());
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
        Node statistics = Descendants(report.Root).Single(n => n.Kind == NodeKind.Statistics);
        Assert.Equal(NodeValue.Integer(1, "frames", "frame"), statistics.Children.Single(c => c.Name == "Dependent Frames").Value);
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

    // Substream 0 changes its configuration while substream 1 does not: the summary's groups cannot say that substream 0's
    // runs follow one another and run beside substream 1 (a group counts only its longest stream), so no group is given
    // and the summary adds every stream up.
    [Fact]
    public void SubstreamsWithAChangingSubstreamAreNotGrouped()
    {
        byte[] template = Fixture("dependent-repeated.eac3")[..400];
        byte[] Frame(int id, int acmod)
        {
            byte[] frame = [.. template];
            frame[2] = (byte)((frame[2] & 0xC7) | (id << 3));
            frame[4] = (byte)((frame[4] & 0xF0) | (acmod << 1));
            return frame;
        }

        var data = new List<byte>();
        foreach (int acmod in new[] { 2, 1, 2 })
        {
            for (int i = 0; i < 2; i++)
            {
                data.AddRange(Frame(0, acmod));
                data.AddRange(Frame(1, 2));
            }
        }

        Report report = Analyze([.. data], "mixed.eac3");

        Assert.Equal(4, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Null(s.PlaybackGroup));
        Assert.Equal(report.Streams.Sum(s => s.Samples), ReportSummary.From(report).Samples);
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
