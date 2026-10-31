using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The declared range of a series (docs/design/frame-map.md, "Declared ranges"): the model's validation and the JSON writer.
public class SeriesRangeTests
{
    [Fact]
    public void Steps_and_a_fixed_range_keep_their_members()
    {
        ValueRange steps = ValueRange.OfSteps([32, 40, 48]);
        Assert.True(steps.HasSteps);
        Assert.Equal([32, 40, 48], steps.Steps.ToArray());
        Assert.Equal(32, steps.Minimum);
        Assert.Equal(48, steps.Maximum);

        ValueRange between = ValueRange.Between(0, 100);
        Assert.False(between.HasSteps);
        Assert.True(between.Steps.IsEmpty);
        Assert.Equal(0, between.Minimum);
        Assert.Equal(100, between.Maximum);
    }

    [Theory]
    [InlineData(new double[] { })]
    [InlineData(new double[] { 32 })]
    [InlineData(new double[] { 48, 32 })]
    [InlineData(new double[] { 32, 32, 48 })]
    [InlineData(new double[] { 32, double.NaN, 48 })]
    [InlineData(new double[] { 32, double.PositiveInfinity })]
    [InlineData(new double[] { double.NegativeInfinity, 32 })]
    public void Steps_that_are_too_few_unsorted_repeated_or_not_finite_are_rejected(double[] steps) =>
        Assert.Throws<ArgumentException>(() => ValueRange.OfSteps(steps));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0)]
    public void A_fixed_range_needs_finite_ends_with_the_minimum_below_the_maximum(double minimum, double maximum) =>
        Assert.Throws<ArgumentException>(() => ValueRange.Between(minimum, maximum));

    [Fact]
    public void A_series_declares_no_range_unless_it_is_set() =>
        Assert.Null(new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0 }, new double[] { 128 }).DeclaredRange);

    [Fact]
    public void The_json_writes_steps_or_a_minimum_and_maximum_after_the_quantity_and_nothing_when_unset()
    {
        double[] x = [0, 10];
        double[] values = [128, 160];
        Series[] series =
        [
            new("Bitrate", "kbps", SeriesAxis.FrameOffset, x, values) { Quantity = "Bitrate", DeclaredRange = ValueRange.OfSteps([32, 40, 48.5]) },
            new("Share", "%", SeriesAxis.FrameOffset, x, values) { Quantity = "Share", DeclaredRange = ValueRange.Between(0, 100) },
            new("Plain", "Hz", SeriesAxis.FrameOffset, x, values) { Quantity = "Plain" },
        ];
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = series }]);

        using var output = new MemoryStream();
        ReportJsonWriter.Write([report], output);
        string text = Encoding.UTF8.GetString(output.ToArray());
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement[] written = document.RootElement[0].GetProperty("streams")[0].GetProperty("series").EnumerateArray().ToArray();

        Assert.Equal([32, 40, 48.5], written[0].GetProperty("declaredRange").GetProperty("steps").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Single(written[0].GetProperty("declaredRange").EnumerateObject());
        JsonElement fixedRange = written[1].GetProperty("declaredRange");
        Assert.Equal(0, fixedRange.GetProperty("minimum").GetDouble());
        Assert.Equal(100, fixedRange.GetProperty("maximum").GetDouble());
        Assert.Equal(2, fixedRange.EnumerateObject().Count());
        Assert.False(written[2].TryGetProperty("declaredRange", out _));
        Assert.Equal(["name", "unit", "axis", "quantity", "declaredRange", "x", "values"], written[0].EnumerateObject().Select(p => p.Name));

        using var explained = new MemoryStream();
        ReportJsonWriter.Write([report], explained, ReportFilter.All, explain: true);
        using JsonDocument explainedDocument = JsonDocument.Parse(explained.ToArray());
        string[] names = explainedDocument.RootElement[0].GetProperty("streams")[0].GetProperty("series")[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(Array.IndexOf(names, "declaredRange") > Array.IndexOf(names, "quantity"));
        Assert.True(Array.IndexOf(names, "declaredRange") < Array.IndexOf(names, "x"));
        Assert.Contains("\"schemaVersion\": 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_tree_does_not_print_the_range()
    {
        double[] x = [0, 10];
        double[] values = [128, 160];
        string Text(ValueRange? range)
        {
            var series = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, x, values) { DeclaredRange = range };
            Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = [series] }]);
            using var writer = new StringWriter();
            ReportTextWriter.Write(report, writer, showOffsets: false);
            return writer.ToString();
        }

        Assert.Equal(Text(null), Text(ValueRange.OfSteps([32, 128, 160])));
    }

    [Fact]
    public void The_json_writes_the_first_frame_of_each_run_only_for_a_series_of_runs()
    {
        double[] x = [0, 10, 20];
        double[] values = [128, 160, 140];
        Series[] series =
        [
            new("Runs", "kbps", SeriesAxis.FrameOffset, x, values) { Runs = new FrameRuns(new long[] { 1, 17, 44 }, 50, new long[] { 640, 1080, 240 }) },
            new("Frames", "kbps", SeriesAxis.FrameOffset, x, values),
        ];
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = series }]);
        using var output = new MemoryStream();
        ReportJsonWriter.Write([report], output);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement[] written = document.RootElement[0].GetProperty("streams")[0].GetProperty("series").EnumerateArray().ToArray();

        JsonElement runs = written[0].GetProperty("frameRuns");
        Assert.Equal([1L, 17L, 44L], runs.GetProperty("firstFrames").EnumerateArray().Select(v => v.GetInt64()));
        Assert.Equal(50, runs.GetProperty("frameCount").GetInt64());
        Assert.Equal([640L, 1080L, 240L], runs.GetProperty("samples").EnumerateArray().Select(v => v.GetInt64()));
        Assert.False(written[1].TryGetProperty("frameRuns", out _));
        Assert.Equal(27, series[0].Runs!.FramesIn(1));
        Assert.Equal(1, series[0].Runs!.RunOf(17));
        Assert.Equal(-1, new FrameRuns(new long[] { 3 }, 5).RunOf(2));
    }

    [Fact]
    public void Runs_need_ascending_first_frames_inside_the_stream_and_a_first_frame_for_each_point()
    {
        Assert.Throws<ArgumentException>(() => new FrameRuns(new long[] { 1, 1 }, 5));
        Assert.Throws<ArgumentException>(() => new FrameRuns(new long[] { 0, 2 }, 5));
        Assert.Throws<ArgumentException>(() => new FrameRuns(ReadOnlyMemory<long>.Empty, 5));
        Assert.Throws<ArgumentException>(() => new FrameRuns(new long[] { 1, 2 }, 5, new long[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRuns(new long[] { 1, 2 }, 5, new long[] { 1, -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRuns(new long[] { 1, 9 }, 5));
        Assert.Throws<ArgumentException>(() => new Series("Runs", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 10 }, new double[] { 1, 2 }) { Runs = new FrameRuns(new long[] { 1 }, 5) });
    }
}
