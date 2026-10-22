using Avalonia.Media;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The category charts' view model: aggregation per pixel column, the rule that keeps rare categories visible, the frame
// each click selects, the headings, and the palette.
public class CategoryGraphTests
{
    private static CategorySeries Strip(int units, Func<int, byte[]> codes, int valuesPerUnit, IReadOnlyList<Category>? categories = null, TimeIndex? time = null, bool weighted = false)
    {
        var builder = new CategorySeriesBuilder("Test", categories ?? CategoryTestData.BlockTypes, "value", valuesPerUnit);
        for (int unit = 0; unit < units; unit++)
        {
            byte[] row = codes(unit);
            if (weighted)
            {
                builder.Add(row, [.. row.Select(_ => (byte)(1 + (unit % 3)))]);
            }
            else
            {
                builder.Add(row);
            }
        }

        return builder.Build(Enumerable.Range(0, units).Select(i => i * 100.0).ToArray(), null, null, "Test series.", null, time);
    }

    [Fact]
    public void Columns_count_each_category_as_a_brute_force_count_does()
    {
        var random = new Random(3);
        byte[][] rows = [.. Enumerable.Range(0, 5000).Select(_ => new[] { (byte)random.Next(6), (byte)random.Next(6), random.Next(9) == 0 ? CategorySeries.None : (byte)random.Next(6) })];
        CategorySeries series = Strip(5000, i => rows[i], 3, weighted: true);

        foreach (int lane in new[] { -1, 0, 2 })
        {
            CategoryColumns columns = CategoryColumns.Frames(series, 700, lane);

            Assert.Equal(700, columns.Count);
            Assert.Equal(0, columns.First(0));
            Assert.Equal(5000, columns.End(699));
            for (int column = 0; column < columns.Count; column++)
            {
                long[] expected = new long[6];
                for (int unit = columns.First(column); unit < columns.End(column); unit++)
                {
                    for (int value = 0; value < 3; value++)
                    {
                        if ((lane < 0 || lane == value) && rows[unit][value] != CategorySeries.None)
                        {
                            expected[rows[unit][value]] += 1 + (unit % 3);
                        }
                    }
                }

                Assert.Equal(expected, columns.Counts(column).ToArray());
            }
        }

        // Fewer units than columns give a column each.
        Assert.Equal(5000, CategoryColumns.Frames(series, 9000, -1).Count);
    }

    [Fact]
    public void Intervals_merge_into_columns_weighted_by_their_values_and_an_empty_one_stays_empty()
    {
        // 60 units, 12 intervals of 5, interval 4 empty (no unit starts in it).
        int[] first = [0, 5, 10, 15, 20, 20, 25, 30, 35, 40, 50, 55];
        CategorySeries series = Strip(60, i => [(byte)(i / 30)], 1, time: new TimeIndex(Enumerable.Range(0, 12).Select(i => (double)i).ToArray(), first));

        CategoryColumns each = CategoryColumns.OverTime(series, 100);
        CategoryColumns merged = CategoryColumns.OverTime(series, 4);

        Assert.Equal(12, each.Count);
        Assert.Equal(0, each.Total(4));
        Assert.Equal(5, each.Total(3));
        Assert.Equal(4, merged.Count);
        Assert.Equal([3, 3, 3, 3], Enumerable.Range(0, 4).Select(c => merged.End(c) - merged.First(c)));
        Assert.Equal([15L, 0, 0, 0, 0, 0], merged.Counts(0).ToArray());
        Assert.Equal(60, Enumerable.Range(0, 4).Sum(c => merged.Total(c)));
    }

    [Fact]
    public void A_rare_category_keeps_a_pixel_taken_from_the_largest_segment()
    {
        // A single short granule among 400 values of a 100-pixel stack is 0.25 pixels.
        double[] heights = new double[6];
        long[] counts = [399, 0, 1, 0, 0, 0];

        CategoryColumns.StackHeights(counts, 100, heights);

        Assert.Equal(1, heights[2]);
        Assert.Equal(99, heights[0]);
        Assert.Equal(100, heights.Sum(), 6);
        Assert.Equal(0, heights[1]);

        // Several rare categories each keep a pixel; with no room the stack is shared equally.
        CategoryColumns.StackHeights([1000, 1, 1, 1, 0, 0], 40, heights);
        Assert.All(heights.Take(4), h => Assert.True(h >= 1));
        Assert.Equal(40, heights.Sum(), 6);
        CategoryColumns.StackHeights([1, 1, 1, 1, 1, 1], 4, heights);
        Assert.All(heights, h => Assert.Equal(4.0 / 6, h, 6));
        CategoryColumns.StackHeights([0, 0, 0, 0, 0, 0], 40, heights);
        Assert.Equal(0, heights.Sum());
    }

    [Fact]
    public void A_lone_short_granule_among_100000_frames_is_marked_rare_in_its_column()
    {
        const int units = 100_000;
        CategorySeries series = Strip(units, i => i == 54_321 ? [0, 0, 2, 0] : [0, 0, 0, 0], 4);
        CategorySeries weighted = Strip(units, i => i == 54_321 ? [2] : [0], 1, weighted: true);

        foreach (CategorySeries s in new[] { series, weighted })
        {
            foreach (int lane in s.ValuesPerUnit == 4 ? new[] { -1, 2 } : new[] { -1 })
            {
                CategoryColumns columns = CategoryColumns.Frames(s, 1000, lane);
                int column = Enumerable.Range(0, columns.Count).Single(c => columns.First(c) <= 54_321 && 54_321 < columns.End(c));

                Assert.True(columns.IsRare(column, 2), $"lane {lane}");
                Assert.False(columns.IsRare(column, 0));
                Assert.Equal(1, Enumerable.Range(0, columns.Count).Count(c => columns.IsRare(c, 2)));
            }
        }

        // A category in two units of a column is not rare, whatever its count.
        CategorySeries two = Strip(100, i => i is 10 or 11 ? [2] : [0], 1);
        CategoryColumns coarse = CategoryColumns.Frames(two, 10, -1);
        Assert.False(coarse.IsRare(1, 2));
    }

    [Fact]
    public void A_click_selects_the_first_unit_of_an_interval_or_the_unit_of_a_cell()
    {
        int[] first = [0, 4, 4, 8];
        CategorySeries series = Strip(10, i => [(byte)(i % 3)], 1, time: new TimeIndex(new double[] { 0, 1, 2, 3 }, first));
        var graph = new CategoryGraph(series);

        Assert.Equal(CategoryView.OverTime, graph.View);
        Assert.Equal(4, graph.Count);
        Assert.Equal(0, graph.FrameAt(0, null));
        Assert.Null(graph.FrameAt(1, null));
        Assert.Equal(400, graph.FrameAt(2, null));
        Assert.Equal(800, graph.FrameAt(3, null));
        Assert.Equal(2, graph.IndexOfFrame(500));
        Assert.Equal(3, graph.IndexOfFrame(900));
        Assert.Null(graph.IndexOfFrame(450));

        graph.View = CategoryView.Frames;
        Assert.Equal(10, graph.Count);
        Assert.Equal(700, graph.FrameAt(7, null));
        Assert.Equal(7, graph.IndexOfFrame(700));
        Assert.StartsWith("Frame 8 · 0x2BC · ", graph.Describe(7), StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_leads_to_the_next_unit_that_holds_it_after_the_selected_one()
    {
        CategorySeries series = Strip(10, i => i is 2 or 5 or 9 ? [1, 0] : [0, 0], 2);

        Assert.Equal(200, CategoryGraph.NextUnit(series, 1, null));
        Assert.Equal(500, CategoryGraph.NextUnit(series, 1, 200));
        Assert.Equal(900, CategoryGraph.NextUnit(series, 1, 500));
        Assert.Equal(200, CategoryGraph.NextUnit(series, 1, 900));
        Assert.Equal(500, CategoryGraph.NextUnit(series, 1, 300));
        Assert.Null(CategoryGraph.NextUnit(series, 2, null));
        Assert.Equal(300, CategoryGraph.NextUnit(series, 0, 200));

        var graph = new CategoryGraph(series);
        Assert.Equal(CategoryView.Frames, graph.View);
        graph.View = CategoryView.Summary;
        Assert.Equal(500, graph.FrameAt(1, 200));
        Assert.Equal("Start · 3 values · 15.0%", graph.Describe(1));
    }

    [Fact]
    public void The_strip_column_of_many_units_selects_the_unit_of_its_rarest_category()
    {
        CategorySeries series = Strip(1000, i => i == 123 ? [0, 0, 2, 0] : [0, 0, 0, 0], 4);
        var graph = new CategoryGraph(series);

        Assert.Equal(123, graph.UnitOfColumn(100, 200, -1));
        Assert.Equal(123, graph.UnitOfColumn(100, 200, 2));
        Assert.Equal(100, graph.UnitOfColumn(100, 200, 1));
        Assert.Equal(100, graph.UnitOfColumn(100, 101, -1));
        Assert.Contains("rare categories enlarged", graph.DescribeUnits(100, 200, -1), StringComparison.Ordinal);
        Assert.Equal("Frame 124 · 0x300C · granule 2, channel 1: Short", NamedSeries(series).DescribeUnits(123, 124, 2));
    }

    private static CategoryGraph NamedSeries(CategorySeries series)
    {
        var builder = new CategorySeriesBuilder("Test", series.Categories, "value", 4, ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"]);
        for (int unit = 0; unit < series.Count; unit++)
        {
            builder.Add([series[unit, 0], series[unit, 1], series[unit, 2], series[unit, 3]]);
        }

        return new CategoryGraph(builder.Build(series.X, null, null, "Named."));
    }

    [Fact]
    public void Graphs_are_listed_under_the_headings_of_their_concepts_whatever_the_format()
    {
        Report report = CategoryTestData.Analyzer().AnalyzeFile(Fixtures.Stats("vbr-seconds.mp3"));
        var graphs = new GraphsViewModel();

        graphs.Load(report);

        Assert.Equal(
            [
                ("Summary", "Coding Summary"),
                ("Stereo coding", "Stereo Coding"),
                ("Block switching", "Block Usage"),
                ("Block switching", "Block Sizes"),
                ("Block switching", "Window Sequences"),
                ("Bit distribution", "Bitrate"),
                ("Bit distribution", "Bitrate Distribution"),
                ("Bit distribution", "Bitrate Over Time"),
                ("Bit distribution", "Reservoir Fill"),
                ("Bit distribution", "Reservoir Fill Over Time"),
                ("Other", "Submodes"),
            ],
            graphs.Choices.Select(c => (c.Graph.Heading, c.Title)));
        Assert.Equal(["Summary", "Stereo coding", "Block switching", "Bit distribution", "Other"], graphs.ChoiceGroups.Select(g => g.Heading));

        // A file without category series has the bitrate headings only, and no heading without a graph.
        graphs.Load(Analyzer.CreateDefault().AnalyzeFile(Fixtures.Stats("vbr-seconds.mp3")));
        Assert.Equal(["Bit distribution"], graphs.ChoiceGroups.Select(g => g.Heading));
    }

    [Fact]
    public void A_numeric_series_with_a_concept_sits_under_it_and_the_bitrate_series_by_their_unit()
    {
        var offsets = new double[] { 0, 10 };
        Series Make(string name, string unit, GraphConcept concept = GraphConcept.None) => new(name, unit, SeriesAxis.FrameOffset, offsets, new double[] { 1, 2 }) { Concept = concept };
        var stream = new StreamInfo
        {
            Nodes = [],
            Codec = "Test",
            Duration = 1,
            Series = [Make("Bitrate", "kbps"), Make("Reservoir Fill", "bytes"), Make("Coded Bandwidth", "Hz", GraphConcept.CodedBandwidth), Make("Channel Bit Share", "%", GraphConcept.BitDistribution), Make("Odd", "things")],
        };

        IReadOnlyList<Graph> graphs = GraphsViewModel.For(stream);

        Assert.Equal(
            [("Coded bandwidth", "Coded Bandwidth"), ("Bit distribution", "Bitrate"), ("Bit distribution", "Bitrate Distribution"), ("Bit distribution", "Reservoir Fill"), ("Bit distribution", "Channel Bit Share"), ("Other", "Odd")],
            graphs.Select(g => (g.Heading, g.Title)));
    }

    [Fact]
    public void A_view_choice_holds_for_the_next_category_series_and_marks_the_frame_in_it()
    {
        Report report = CategoryTestData.Analyzer().AnalyzeFile(Fixtures.Stats("vbr-seconds.mp3"));
        var graphs = new GraphsViewModel();
        graphs.Load(report);
        graphs.Selected = graphs.Choices.Single(c => c.Title == "Stereo Coding");
        Assert.Equal(CategoryView.OverTime, graphs.View);
        Assert.Equal(["Over time", "Frames", "Summary"], graphs.Views.Select(v => v.Title));
        Assert.True(graphs.HasViews);

        long offset = (long)report.Streams[0].Series[0].X.Span[77];
        graphs.MarkFrame(offset);
        int interval = graphs.MarkedIndex!.Value;
        graphs.SelectView(CategoryView.Frames);
        Assert.Equal(77, graphs.MarkedIndex);
        Assert.Equal([false, true, false], graphs.Views.Select(v => v.IsSelected));

        graphs.Selected = graphs.Choices.Single(c => c.Title == "Window Sequences");
        Assert.Equal(CategoryView.Frames, graphs.View);
        Assert.Equal(77, graphs.MarkedIndex);
        graphs.SelectView(CategoryView.OverTime);
        Assert.Equal(interval, graphs.MarkedIndex);

        // A line graph has no views.
        graphs.Selected = graphs.Choices.First(c => c.Graph is LineGraph);
        Assert.False(graphs.HasViews);
        Assert.Null(graphs.View);
    }

    [Fact]
    public void The_coding_summary_lists_every_series_and_reads_the_counts_from_them()
    {
        Report report = CategoryTestData.Analyzer().AnalyzeFile(Fixtures.Stats("vbr-seconds.mp3"));
        StreamInfo stream = report.Streams[0];
        var summary = (CodingSummaryGraph)GraphsViewModel.For(stream).OfType<CodingSummaryGraph>().Single();

        Assert.Equal(stream.CategorySeries.Select(s => s.Name), summary.Tables.Select(t => t.Name));
        Assert.Equal(stream.CategorySeries.Sum(s => s.Categories.Count), summary.Count);
        (int series, int category) = summary.RowAt(2);
        Assert.Equal(stream.CategorySeries[series].Counts[category], summary.ValueAt(2));
        Assert.StartsWith("Block Usage · Short · ", summary.Describe(2), StringComparison.Ordinal);
        Assert.NotNull(summary.FrameAt(2, null));
    }

    [Fact]
    public void The_palette_has_six_hues_ramps_for_ordered_series_gray_other_and_hatched_reserved()
    {
        Color surface = Colors.White;
        CategorySeries plain = Strip(3, i => [(byte)i], 1, CategoryTestData.Numbered("Mode", 8));
        CategorySeries ordered = Strip(3, i => [(byte)i], 1, CategoryTestData.Numbered("Size", 10));
        var orderedBuilder = new CategorySeriesBuilder("Size", CategoryTestData.Numbered("Size", 10), "frame", 1, isOrdered: true);
        orderedBuilder.Add(0);
        CategorySeries ramp = orderedBuilder.Build(new double[1], null, null, "d");
        CategorySeries blocks = Strip(3, i => [(byte)i], 1);

        CategoryStyle[] light = CategoryColors.For(plain, false, surface);
        CategoryStyle[] dark = CategoryColors.For(plain, true, Colors.Black);

        // The first six categories take the six hues in order; the plain one is muted. Past the sixth, one flat gray.
        Assert.Equal(CategoryColors.Light.Skip(1).Take(5), light.Skip(1).Take(5).Select(s => s.Color));
        Assert.Equal(CategoryColors.Dark.Skip(1).Take(5), dark.Skip(1).Take(5).Select(s => s.Color));
        Assert.NotEqual(CategoryColors.Light[0], light[0].Color);
        Assert.All(light.Skip(6), s => Assert.True(s.IsOther));
        Assert.Equal(light[6].Color, light[7].Color);
        Assert.DoesNotContain(CategoryColors.Light, c => c == light[6].Color);

        // An ordered series is one hue light to dark for eight steps, then gray.
        CategoryStyle[] steps = CategoryColors.For(ramp, false, surface);
        double[] lightness = [.. steps.Take(8).Select(s => s.Color.R + s.Color.G + s.Color.B)];
        Assert.Equal(lightness.OrderByDescending(l => l), lightness);
        Assert.Equal(8, lightness.Distinct().Count());
        Assert.All(steps.Skip(8), s => Assert.True(s.IsOther));
        Assert.DoesNotContain(steps.Take(8), s => s.IsOther || s.IsHatched);

        // Reserved is hatched gray and takes no hue; the categories after it keep their place in the hues.
        CategoryStyle[] types = CategoryColors.For(blocks, false, surface);
        Assert.Equal(6, CategoryTestData.BlockTypes.Count);
        var withReserved = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "block", 1);
        withReserved.Add(0);
        CategoryStyle[] reserved = CategoryColors.For(withReserved.Build(new double[1], null, null, "d"), false, surface);
        Assert.True(reserved[5].IsHatched);
        Assert.False(reserved[5].IsOther);
        Assert.DoesNotContain(CategoryColors.Light, c => c == reserved[5].Color);
        Assert.Equal(CategoryColors.Light[4], reserved[4].Color);
        Assert.NotNull(types);
        Assert.NotNull(ordered);
    }
}
