using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// How the JSON and text writers show category series: the example of docs/design/coding-graphs.md, a stream of three
// seconds of 40 frames with four values each, and what the other forms of a series write.
public class CategorySeriesWriterTests
{
    private const int Frames = 120;

    // Switches start at these frames: a frame with start blocks in granule 2, a frame of short blocks and a frame with
    // stop blocks in granule 1.
    private static readonly int[] Switches = [49, 70, 85, 93, 101, 109];

    [Fact]
    public void A_stream_without_category_series_writes_neither_array_nor_concept()
    {
        var series = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 10 }, new double[] { 128, 160 });
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = [series] }]);

        string json = Json(report);

        Assert.DoesNotContain("categorySeries", json, StringComparison.Ordinal);
        Assert.DoesNotContain("concept", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_series_with_a_concept_writes_it()
    {
        var series = new Series("Coded Bandwidth", "Hz", SeriesAxis.FrameOffset, new double[] { 0 }, new double[] { 16000 }) { Concept = GraphConcept.CodedBandwidth };
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = [series] }]);

        using JsonDocument document = JsonDocument.Parse(Json(report));

        Assert.Equal("codedBandwidth", document.RootElement[0].GetProperty("streams")[0].GetProperty("series")[0].GetProperty("concept").GetString());
    }

    [Fact]
    public void The_json_has_counts_slot_names_the_time_index_and_run_length_codes()
    {
        (Report report, _) = Example();

        using JsonDocument document = JsonDocument.Parse(Json(report));
        JsonElement stream = document.RootElement[0].GetProperty("streams")[0];
        JsonElement usage = stream.GetProperty("categorySeries")[0];

        Assert.Equal("Block Usage", usage.GetProperty("name").GetString());
        Assert.Equal("blockSwitching", usage.GetProperty("concept").GetString());
        Assert.Equal("granule and channel", usage.GetProperty("valueLabel").GetString());
        Assert.Equal(4, usage.GetProperty("valuesPerUnit").GetInt32());
        Assert.Equal(["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"], usage.GetProperty("valueNames").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(
            [("Long", 432L), ("Start", 12L), ("Short", 24L), ("Mixed", 0L), ("Stop", 12L), ("Reserved", 0L)],
            usage.GetProperty("categories").EnumerateArray().Select(c => (c.GetProperty("name").GetString()!, c.GetProperty("count").GetInt64())));
        Assert.Equal(480, usage.GetProperty("total").GetInt64());
        Assert.Equal("Bitrate", usage.GetProperty("xSeries").GetString());
        Assert.False(usage.TryGetProperty("x", out _));
        Assert.False(usage.TryGetProperty("uncodedValues", out _));
        Assert.False(usage.TryGetProperty("weights", out _));
        Assert.False(usage.TryGetProperty("valueCounts", out _));
        Assert.False(usage.TryGetProperty("description", out _));
        Assert.Equal([0, 40, 80], usage.GetProperty("seconds").EnumerateArray().Select(v => v.GetInt32()));

        int[][] expected =
        [
            [0, 198], [1, 2], [2, 4], [4, 2], [0, 76], [1, 2], [2, 4], [4, 2], [0, 52], [1, 2], [2, 4], [4, 2],
            [0, 24], [1, 2], [2, 4], [4, 2], [0, 24], [1, 2], [2, 4], [4, 2], [0, 24], [1, 2], [2, 4], [4, 2], [0, 34],
        ];
        Assert.Equal(expected, usage.GetProperty("runs").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()));

        // The grouping names its source and maps its codes, with no runs of its own.
        JsonElement sizes = stream.GetProperty("categorySeries")[1];
        Assert.Equal("Block Sizes", sizes.GetProperty("name").GetString());
        Assert.Equal("blockSwitching", sizes.GetProperty("concept").GetString());
        Assert.Equal("Block Usage", sizes.GetProperty("groupOf").GetString());
        Assert.Equal([0, 0, 1, 2, 0, 3], sizes.GetProperty("map").EnumerateArray().Select(v => v.GetInt32()));
        Assert.False(sizes.TryGetProperty("runs", out _));
        Assert.Equal(456, sizes.GetProperty("categories")[0].GetProperty("count").GetInt64());
    }

    [Fact]
    public void A_reserved_category_a_quantity_and_a_groupings_missing_x_are_written()
    {
        var builder = new CategorySeriesBuilder("Block Usage", [new("Long", "l"), new("Reserved", "r") { IsReserved = true }], "block", 1);
        builder.Add(0);
        builder.Add(1);
        CategorySeries usage = builder.Build(new double[] { 0, 10 }, null, null, "Blocks.");
        CategorySeries grouped = usage.Group("Block Sizes", [new("Long", "l"), new("Reserved", "r")], [0, 1], null, "Sizes.");
        var share = new Series("Channel Bit Share", "%", SeriesAxis.FrameOffset, new double[] { 0 }, new double[] { 48 }) { Quantity = "Channel Bit Share", Concept = GraphConcept.BitDistribution };
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", Series = [share], CategorySeries = [usage, grouped] }]);

        using JsonDocument document = JsonDocument.Parse(Json(report));
        JsonElement stream = document.RootElement[0].GetProperty("streams")[0];

        Assert.Equal("Channel Bit Share", stream.GetProperty("series")[0].GetProperty("quantity").GetString());
        JsonElement categories = stream.GetProperty("categorySeries")[0].GetProperty("categories");
        Assert.False(categories[0].TryGetProperty("isReserved", out _));
        Assert.True(categories[1].GetProperty("isReserved").GetBoolean());
        Assert.True(stream.GetProperty("categorySeries")[0].TryGetProperty("x", out _));
        Assert.False(stream.GetProperty("categorySeries")[1].TryGetProperty("x", out _));
    }

    [Fact]
    public void Explain_adds_the_descriptions_of_the_series_and_its_categories()
    {
        (Report report, _) = Example();
        using var output = new MemoryStream();

        ReportJsonWriter.Write([report], output, ReportFilter.All, explain: true);

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement usage = document.RootElement[0].GetProperty("streams")[0].GetProperty("categorySeries")[0];
        Assert.Equal("The block type of each granule and channel.", usage.GetProperty("description").GetString());
        Assert.Equal("Short block.", usage.GetProperty("categories")[2].GetProperty("description").GetString());
        Assert.Equal("block-switching", usage.GetProperty("topic").GetString());
    }

    [Fact]
    public void Variable_layouts_weights_uncoded_values_and_values_without_a_category_are_written()
    {
        var builder = new CategorySeriesBuilder("Modes", [new("SILK", "s"), new("CELT", "c")], "stream packet");
        builder.Add([0]);
        builder.Add([1, CategorySeries.None, 1], [2, 1, 1]);
        builder.Add([]);
        builder.Add([.. Enumerable.Repeat((byte)0, 300)]);
        double[] x = [0, 10, 20, 30];
        CategorySeries series = builder.Build(x, null, null, "Modes.");
        Report report = new("a.bin", new Node("File", 0, 20), [], [new StreamInfo { Nodes = [], Codec = "Test", CategorySeries = [series] }]);

        using JsonDocument document = JsonDocument.Parse(Json(report));
        JsonElement modes = document.RootElement[0].GetProperty("streams")[0].GetProperty("categorySeries")[0];

        Assert.Equal(46, modes.GetProperty("uncodedValues").GetInt32());
        Assert.Equal([0.0, 10, 20, 30], modes.GetProperty("x").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal([[1, 1], [3, 1], [0, 1], [254, 1]], Pairs(modes.GetProperty("valueCounts")));
        JsonElement[] runs = [.. modes.GetProperty("runs").EnumerateArray()];
        Assert.Equal(5, runs.Length);
        Assert.Equal(JsonValueKind.Null, runs[2][0].ValueKind);
        Assert.Equal([0, 254], [runs[4][0].GetInt32(), runs[4][1].GetInt32()]);
        Assert.Equal([[1, 1], [2, 1], [1, 256]], Pairs(modes.GetProperty("weights")));
        Assert.Equal([301L, 3], modes.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("count").GetInt64()));
        Assert.Equal(304, modes.GetProperty("total").GetInt64());
    }

    private static int[][] Pairs(JsonElement runs) => [.. runs.EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray())];

    [Fact]
    public void The_text_tree_prints_each_categorys_share_per_second_after_its_anchor()
    {
        (Report report, _) = Example();
        var writer = new StringWriter { NewLine = "\n" };

        ReportTextWriter.Write(report, writer, showOffsets: false);

        string[] lines = writer.ToString().Split('\n');
        int anchor = Array.IndexOf(lines, "      Block Usage: 480 granules");
        Assert.True(anchor >= 0, writer.ToString());
        string[] expected =
        [
            "      Block Usage: 480 granules",
            "      Long Share Over Time: 100,90,80",
            "      Start Share Over Time: 0,2.5,5",
            "      Short Share Over Time: 0,5,10",
            "      Stop Share Over Time: 0,2.5,5",
        ];
        Assert.Equal(expected, lines[anchor..(anchor + expected.Length)]);
        Assert.Equal("      Long, start and stop Share Over Time: 100,95,90", lines.Single(l => l.StartsWith("      Long, start and stop Share", StringComparison.Ordinal)));
        Assert.Equal("      Short Share Over Time: 0,5,10", lines.Last(l => l.StartsWith("      Short Share", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("      Mixed Share", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_tree_shows_offsets_explanations_and_numbers_the_streams_that_share_an_anchor()
    {
        (Report report, Node anchor) = Example();
        var builder = new CategorySeriesBuilder("Block Usage", CategorySeriesTests.BlockTypes, "granule and channel", 4);
        for (int i = 0; i < 2; i++)
        {
            builder.Add([0, 0, 0, 0]);
        }

        CategorySeries second = builder.Build(new double[] { 0, 5 }, null, anchor, "Again.", null, new TimeIndex(new double[] { 0 }, new[] { 0 }));
        Report two = new(report.Source, report.Root, [], [report.Streams[0], new StreamInfo { Nodes = [], Codec = "Test", CategorySeries = [second] }]);
        var writer = new StringWriter { NewLine = "\n" };

        ReportTextWriter.Write(two, writer, showOffsets: true, ReportFilter.All, explain: true);

        string text = writer.ToString();
        Assert.Contains("      Long Share Over Time (stream 1): 100,90,80 [0x10, 8 bytes]\n", text, StringComparison.Ordinal);
        Assert.Contains("      Long Share Over Time (stream 2): 100 [0x10, 8 bytes]\n", text, StringComparison.Ordinal);
        Assert.Contains("Long block.", text, StringComparison.Ordinal);

        // Without a time index nothing is printed, and with the statistics hidden the lines go with their anchor.
        var plain = new StringWriter { NewLine = "\n" };
        ReportTextWriter.Write(two, plain, showOffsets: false, new ReportFilter(ReportSections.Statistics));
        Assert.DoesNotContain("Share Over Time", plain.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_interval_without_units_prints_a_dash()
    {
        var builder = new CategorySeriesBuilder("Modes", [new("A", "a"), new("B", "b")], "frame", 1);
        for (int i = 0; i < 4; i++)
        {
            builder.Add((byte)(i % 2));
        }

        var anchor = new Node("Modes", 0, 4) { Kind = NodeKind.Statistics };
        CategorySeries series = builder.Build(new double[] { 0, 1, 2, 3 }, null, anchor, "Modes.", null, new TimeIndex(new double[] { 0, 1, 2 }, new[] { 0, 4, 4 }));
        var root = new Node("File", 0, 4);
        root.Add(anchor);
        var writer = new StringWriter { NewLine = "\n" };

        ReportTextWriter.Write(new Report("a.bin", root, [], [new StreamInfo { Nodes = [], Codec = "Test", CategorySeries = [series] }]), writer, showOffsets: false);

        Assert.Contains("  A Share Over Time: 50,-,-\n", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("  B Share Over Time: 50,-,-\n", writer.ToString(), StringComparison.Ordinal);
    }

    private static string Json(Report report)
    {
        using var output = new MemoryStream();
        ReportJsonWriter.Write([report], output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    // 3 seconds of 40 frames of Layer III with four values each, as the design's example; "Block Usage" and its grouping.
    private static (Report Report, Node Anchor) Example()
    {
        var builder = new CategorySeriesBuilder("Block Usage", CategorySeriesTests.BlockTypes, "granule and channel", 4,
            ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"], concept: GraphConcept.BlockSwitching);
        for (int frame = 0; frame < Frames; frame++)
        {
            int at = Array.IndexOf(Switches, frame);
            int previous = Array.IndexOf(Switches, frame - 2);
            if (at >= 0)
            {
                builder.Add([0, 0, 1, 1]);
            }
            else if (Array.IndexOf(Switches, frame - 1) >= 0)
            {
                builder.Add([2, 2, 2, 2]);
            }
            else if (previous >= 0)
            {
                builder.Add([4, 4, 0, 0]);
            }
            else
            {
                builder.Add([0, 0, 0, 0]);
            }
        }

        var root = new Node("File", 0, 50_000);
        var stream = new Node("Stream", 0, 50_000) { Kind = NodeKind.Stream };
        var anchor = new Node("Block Usage", 16, 8, NodeValue.Integer(480, "granules", "granule")) { Kind = NodeKind.Statistics };
        root.Add(stream);
        stream.Add(anchor);
        double[] x = Enumerable.Range(0, Frames).Select(i => i * 417.0).ToArray();
        var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, x, new double[Frames]);
        var time = new TimeIndex(new double[] { 0, 1, 2 }, new[] { 0, 40, 80 });
        CategorySeries usage = builder.Build(bitrate.X, bitrate, anchor, "The block type of each granule and channel.", "block-switching", time);
        var sizesAnchor = new Node("Block Sizes", 16, 8) { Kind = NodeKind.Statistics };
        stream.Add(sizesAnchor);
        Category[] sizes = [new("Long, start and stop", "l"), new("Short", "s"), new("Mixed", "m"), new("Reserved", "r")];
        CategorySeries grouped = usage.Group("Block Sizes", sizes, [0, 0, 1, 2, 0, 3], sizesAnchor, "Block sizes.");
        CategorySeries[] all = [usage, grouped];
        var info = new StreamInfo { Nodes = [stream], Codec = "Test", Series = [bitrate], CategorySeries = all };
        return (new Report("test.mp3", root, [], [info]), anchor);
    }
}
