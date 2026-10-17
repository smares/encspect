using System.Globalization;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Streams of one playback group play at the same time, so the summary and the CSV count the longest once; streams of different
// groups and streams without one follow one another and add up.
public class PlaybackGroupTests
{
    private static StreamInfo Stream(double seconds, long samples, int? group) =>
        new() { Nodes = [], Codec = "Test", Duration = seconds, Samples = samples, PlaybackGroup = group };

    [Fact]
    public void TheLongestStreamOfAGroupCountsOnceAndTheOthersAddUp()
    {
        var report = new Report("x.test", new Node("File", 0, 0), [],
            [Stream(2, 100, 1), Stream(3, 150, 1), Stream(1, 50, 2), Stream(4, 200, null), Stream(0.5, 25, null)]);

        ReportSummary summary = ReportSummary.From(report);

        Assert.Equal(3 + 1 + 4 + 0.5, summary.Duration);
        Assert.Equal(150 + 50 + 200 + 25, summary.Samples);
        Assert.Equal(5, summary.Streams);
    }

    [Fact]
    public void WithoutGroupsEveryStreamAddsUp()
    {
        var report = new Report("x.test", new Node("File", 0, 0), [], [Stream(2, 100, null), Stream(3, 150, null)]);

        Assert.Equal(5, ReportSummary.From(report).Duration);
    }

    [Fact]
    public void TheAudioTracksOfOneSegmentPlayTogetherAndTwoSegmentsFollowEachOther()
    {
        Report together = Fixtures.AnalyzeMpeg("matroska/two-audio-tracks.mka");
        Assert.Equal(2, together.Streams.Count);
        Assert.All(together.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        Assert.Equal(together.Streams.Max(s => s.Duration), ReportSummary.From(together).Duration);
        Assert.Equal(together.Streams.Max(s => s.Samples), ReportSummary.From(together).Samples);

        Report apart = Fixtures.AnalyzeMpeg("matroska/two-segments.mka");
        Assert.Equal([1, 2], apart.Streams.Select(s => s.PlaybackGroup));
        Assert.Equal(apart.Streams.Sum(s => s.Duration), ReportSummary.From(apart).Duration);
    }

    [Fact]
    public void TwoSegmentsOfTwoTracksEachAddUpAsTwoPresentations()
    {
        byte[] data = MatroskaBuilder.Concat(MatroskaBuilder.Header(), TwoTrackSegment(), TwoTrackSegment());

        Report report = Analyzer.CreateDefault().Analyze("segments.mka", new MemoryStream(data));

        Assert.Equal([1, 1, 2, 2], report.Streams.Select(s => s.PlaybackGroup));
        double one = report.Streams.Take(2).Max(s => s.Duration);
        Assert.True(one > 0.5, $"{one} s");
        Assert.Equal(2 * one, ReportSummary.From(report).Duration!.Value, 6);
        Assert.Equal(2 * report.Streams.Take(2).Max(s => s.Samples), ReportSummary.From(report).Samples);
    }

    [Fact]
    public void CsvRowsAndJsonShowTheGroup()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/two-audio-tracks.mka");
        ReportSummary summary = ReportSummary.From(report);
        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        Assert.Contains(Math.Round(summary.Duration!.Value, 3, MidpointRounding.ToEven).ToString("F3", CultureInfo.InvariantCulture), csv.ToString(), StringComparison.Ordinal);

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json.ToArray());
        System.Text.Json.JsonElement streams = document.RootElement[0].GetProperty("streams");
        Assert.All(streams.EnumerateArray(), stream => Assert.Equal(1, stream.GetProperty("playbackGroup").GetInt32()));
    }

    [Fact]
    public void TheTracksOfAnMp4MovieShareAGroup()
    {
        Report report = Fixtures.AnalyzeMpeg("aac/ffmpeg-lc.m4a");

        Assert.NotEmpty(report.Streams);
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
    }

    [Fact]
    public void TheStreamsOfOneAsfFilePlayTogether()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/header-objects.asf");

        Assert.True(report.Streams.Count > 1, $"{report.Streams.Count} streams");
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        Assert.Equal(report.Streams.Max(s => s.Duration), ReportSummary.From(report).Duration);
    }

    [Fact]
    public void MultiplexedOggStreamsPlayTogetherAndChainedOnesFollowEachOther()
    {
        Report multiplexed = Fixtures.AnalyzeMpeg("ogg/multiplexed.ogg");
        Assert.True(multiplexed.Streams.Count > 1);
        Assert.Single(multiplexed.Streams.Select(s => s.PlaybackGroup).Distinct());
        Assert.NotNull(multiplexed.Streams[0].PlaybackGroup);
        Assert.Equal(multiplexed.Streams.Max(s => s.Duration), ReportSummary.From(multiplexed).Duration);

        Report chained = Fixtures.AnalyzeMpeg("ogg/chained.ogg");
        Assert.Equal(chained.Streams.Count, chained.Streams.Select(s => s.PlaybackGroup).Distinct().Count());
        Assert.Equal(chained.Streams.Sum(s => s.Duration), ReportSummary.From(chained).Duration!.Value, 6);
    }

    [Fact]
    public void TheIndependentSubstreamsOfARawEac3FileShareAGroup()
    {
        Report report = Fixtures.AnalyzeMpeg("ac3/substreams.eac3");

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        Assert.Equal(report.Streams.Max(s => s.Duration), ReportSummary.From(report).Duration);
    }

    [Fact]
    public void TheSubstreamsOfAnEac3TrackInMp4ShareTheMovieGroup()
    {
        byte[][] units = Eac3AccessUnits();
        byte[] entry = Mp4Builder.Box("ec-3", [new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xBB, 0x80, 0, 0]]);
        byte[] data = Mp4Builder.FileWithMoov(
            at => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(units.Length, 1536), Mp4Builder.Stsc(units.Length), Mp4Builder.Sizes(units.Select(u => u.Length)), Mp4Builder.ChunkOffsets([at])),
            Mp4Builder.Join(units));

        Report report = Analyzer.CreateDefault().Analyze("substreams.m4a", new MemoryStream(data));

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        Assert.Equal(report.Streams.Max(s => s.Duration), ReportSummary.From(report).Duration);
    }

    [Fact]
    public void TheSubstreamsOfAnEac3TrackInMatroskaShareTheSegmentGroup()
    {
        byte[][] units = Eac3AccessUnits();
        byte[] rate = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(rate, 48000);
        byte[] track = MatroskaBuilder.Element(0xAE, MatroskaBuilder.Unsigned(0xD7, 1), MatroskaBuilder.Unsigned(0x73C5, 1), MatroskaBuilder.Unsigned(0x83, 2),
            MatroskaBuilder.Text(0x86, "A_EAC3"), MatroskaBuilder.Element(0xE1, MatroskaBuilder.Element(0xB5, rate), MatroskaBuilder.Unsigned(0x9F, 6)));
        byte[] blocks = MatroskaBuilder.Concat([.. units.Select((u, i) => MatroskaBuilder.SimpleBlock(1, i * 32, u))]);
        byte[] data = MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, track), MatroskaBuilder.Cluster(0, blocks));

        Report report = Analyzer.CreateDefault().Analyze("substreams.mka", new MemoryStream(data));

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, s => Assert.Equal(1, s.PlaybackGroup));
        Assert.Equal(report.Streams.Max(s => s.Duration), ReportSummary.From(report).Duration);
    }

    // The access units of ac3/substreams.eac3: each starts with a frame of independent substream 0 and holds the frames up to the next one.
    private static byte[][] Eac3AccessUnits()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3/substreams.eac3"));
        var units = new List<byte[]>();
        int start = 0;
        int at = 0;
        while (at + Formats.Ac3.SyncFrame.Length <= source.Length)
        {
            var frame = Formats.Ac3.SyncFrame.Read(source.AsSpan(at));
            int length = frame.FrameLength;
            if (frame.SubstreamId == 0 && frame.StreamType == 0 && at > start)
            {
                units.Add(source[start..at]);
                start = at;
            }

            at += length;
        }

        units.Add(source[start..at]);
        return [.. units];
    }

    private static byte[] TwoTrackSegment() => MatroskaBuilder.TwoOpusTracksSegment(blocks: 50);
}
