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
        Assert.Equal([32, 40, 48], steps.Steps);
        Assert.Equal(32, steps.Minimum);
        Assert.Equal(48, steps.Maximum);

        ValueRange between = ValueRange.Between(0, 100);
        Assert.Null(between.Steps);
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
}
