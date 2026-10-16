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

    private static byte[] TwoTrackSegment() => MatroskaBuilder.TwoOpusTracksSegment(blocks: 50);
}
