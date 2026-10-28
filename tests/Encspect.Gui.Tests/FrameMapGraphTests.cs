using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The view model of the Frame Map: the classes of a numeric series, the squares of a series, the squares of findings, the
// declared ranges and their clamp, the units of several values, and the entries that offer the form.
public class FrameMapGraphTests
{
    // The 14 bitrate steps of MPEG-1 Layer III, as the MPEG parser declares them.
    private static readonly double[] Mpeg1Layer3 = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static double[] Offsets(int count, double step = 50) => [.. Enumerable.Range(0, count).Select(i => 100 + (i * step))];

    private static Series Numbers(double[] values, string unit = "kbps", ValueRange? range = null, double[]? x = null) =>
        new("Bitrate", unit, SeriesAxis.FrameOffset, x ?? Offsets(values.Length), values) { DeclaredRange = range };

    private static FrameMapGraph Map(Series series, double[]? frames = null, IReadOnlyList<MapFinding>? findings = null) =>
        new(series, frames ?? series.X.ToArray(), findings);

    [Theory]
    [InlineData(1, 1, false, false)]
    [InlineData(8, 8, false, false)]
    [InlineData(9, 9, true, false)]
    [InlineData(16, 16, true, false)]
    [InlineData(17, 8, false, true)]
    public void A_series_has_one_class_for_each_value_up_to_16_distinct_values_and_8_classes_of_equal_width_beyond(int distinct, int classes, bool isMultiHue, bool usesRange)
    {
        double[] values = [.. Enumerable.Range(0, 340).Select(i => 10.0 * (i % distinct))];
        FrameMapGraph map = Map(Numbers(values));

        Assert.Equal(classes, map.ClassCount);
        Assert.Equal(isMultiHue, map.IsMultiHue);
        Assert.Equal(usesRange, map.UsesRange);
        Assert.Equal(distinct == 1, map.IsSingleValue);
        int[] seen = [.. Enumerable.Range(0, 340).Select(map.ClassAt).Distinct().Order()];
        Assert.Equal(Enumerable.Range(0, classes), seen);
        if (!usesRange)
        {
            // One class each, ascending with the value.
            Assert.Equal(Enumerable.Range(0, distinct), Enumerable.Range(0, distinct).Select(d => map.ClassAt(d)));
        }
        else
        {
            // 160 / 8 = 20 wide: the value 0 is in the first class, 160 in the last, and 30 in the second.
            Assert.Equal(0, map.ClassAt(0));
            Assert.Equal(7, map.ClassAt(16));
            Assert.Equal(1, map.ClassAt(3));
            Assert.Equal("0 kbps", map.KeyLow);
            Assert.Equal("160 kbps", map.KeyHigh);
        }

        Assert.Equal(classes, map.Key.Count);
        Assert.Equal(340, map.Key.Sum(k => k.Count));
    }

    [Fact]
    public void A_series_of_one_value_is_one_even_color_and_a_key_of_one_line()
    {
        FrameMapGraph map = Map(Numbers([.. Enumerable.Repeat(128.0, 50)]));

        Assert.True(map.IsSingleValue);
        Assert.Equal(1, map.ClassCount);
        Assert.Equal("128 kbps", map.KeyLow);
        Assert.Equal(50, map.Key.Single().Count);
        Assert.Equal("Frame 1 · 0x64 · 128 kbps", map.Describe(0));
    }

    [Fact]
    public void Values_that_are_not_finite_have_no_class_and_are_counted_apart()
    {
        double[] values = [128, double.NaN, 192, double.PositiveInfinity, double.NegativeInfinity, 128];
        FrameMapGraph map = Map(Numbers(values));

        Assert.Equal(2, map.ClassCount);
        Assert.Equal([0, -1, 1, -1, -1, 0], Enumerable.Range(0, 6).Select(map.ClassAt));
        Assert.Equal(3, map.NoValueCount);
        Assert.EndsWith("no value", map.Describe(1), StringComparison.Ordinal);
        Assert.Equal(6, map.Count);
    }

    [Fact]
    public void A_series_whose_offsets_are_among_the_frames_keeps_the_streams_squares_with_an_empty_one_where_it_has_no_value()
    {
        double[] frames = Offsets(10);
        Series everyOther = Numbers([.. Enumerable.Range(0, 5).Select(i => 100.0 + (i * 10))], x: [.. frames.Where((_, i) => i % 2 == 0)]);
        FrameMapGraph map = Map(everyOther, frames);

        Assert.Equal(10, map.Count);
        Assert.False(map.HasOwnSquares);
        Assert.Equal("One square per frame", map.Caption);
        Assert.Equal([0, -1, 1, -1, 2, -1, 3, -1, 4, -1], Enumerable.Range(0, 10).Select(map.ClassAt));
        Assert.Equal(5, map.NoValueCount);

        // The unit at a square and the square of a unit.
        Assert.Equal([0, -1, 1, -1, 2], Enumerable.Range(0, 5).Select(s => map.UnitOf(s)));
        Assert.Equal([0, 2, 4, 6, 8], Enumerable.Range(0, 5).Select(map.SquareOf));
        Assert.Equal((long)frames[4], map.OffsetOf(4));
        Assert.Equal((long)frames[4], map.FrameAt(4, null));
        Assert.Equal(4, map.IndexOfFrame((long)frames[4]));
        Assert.Null(map.IndexOfFrame(3));
        Assert.Null(map.FrameAt(10, null));
    }

    [Fact]
    public void A_series_with_a_square_per_frame_of_its_own_says_so_in_the_caption()
    {
        double[] frames = Offsets(10);
        Series reservoir = Numbers([1, 2, 3], "bytes", x: [120, 330, 480]);
        FrameMapGraph own = Map(reservoir, frames);

        Assert.True(own.HasOwnSquares);
        Assert.Equal(3, own.Count);
        Assert.Equal("One square per point of this series", own.Caption);
        Assert.Equal(330, own.OffsetOf(1));
        Assert.Equal(1, own.IndexOfFrame(330));

        // A stream with no frames of its own series is the units alone.
        Assert.False(Map(reservoir, []).HasOwnSquares);
    }

    [Fact]
    public void A_findings_square_is_the_first_frame_along_its_path_and_a_finding_on_a_gap_the_stream_or_the_root_has_none()
    {
        var root = new Node("File", 0, 1000);
        var stream = new Node("MPEG Audio", 0, 1000) { Kind = NodeKind.Stream };
        root.Add(stream);
        stream.Add(new Node("Statistics", 0, 1000) { Kind = NodeKind.Statistics });
        var frame1 = new Node("Frame", 100, 50) { Kind = NodeKind.Frame };
        stream.Add(frame1);
        frame1.Add(new Node("Header", 100, 4) { Kind = NodeKind.Detail });
        stream.Add(new Node("Gap", 150, 50) { Kind = NodeKind.Detail });
        stream.Add(new Node("Frame", 200, 50) { Kind = NodeKind.Frame });
        Finding[] findings =
        [
            new(Severity.Warning, "a", "In a field of the second frame.") { Path = [0, 1, 0], Count = 50 },
            new(Severity.Error, "b", "On the frame.") { Path = [0, 3] },
            new(Severity.Info, "c", "On a gap.") { Path = [0, 2] },
            new(Severity.Info, "d", "On the stream.") { Path = [0] },
            new(Severity.Info, "e", "On the root.") { Path = [] },
            new(Severity.Info, "f", "Without a node."),
        ];
        var report = new Report("x", root, findings);

        IReadOnlyList<MapFinding> mapped = GraphsViewModel.MapFindings(report);

        Assert.Equal([100L, 200L], mapped.Select(f => f.Offset));
        double[] offsets = [100, 150, 200];
        FrameMapGraph map = Map(Numbers([128, 128, 192], x: offsets), offsets, mapped);
        Assert.Equal([0, 2], map.FindingSquares());
        Assert.Equal(Severity.Warning, map.SeverityAt(0));
        Assert.Equal(Severity.Error, map.SeverityAt(2));
        Assert.Null(map.SeverityAt(1));
        Assert.EndsWith("Warning: In a field of the second frame. (first of 50 cases)", map.Describe(0), StringComparison.Ordinal);
        Assert.EndsWith("Error: On the frame.", map.Describe(2), StringComparison.Ordinal);

        // A severity filtered out leaves no ring, mark or text.
        Assert.Equal([2], map.FindingSquares(SeverityMask.Error | SeverityMask.Info));
        Assert.DoesNotContain("Warning", map.Describe(0, SeverityMask.Error), StringComparison.Ordinal);
        Assert.Empty(map.FindingSquares(SeverityMask.None));

        // A finding in a frame the map has no square for is not on it.
        FrameMapGraph other = Map(Numbers([128], x: [150]), [150], mapped);
        Assert.Empty(other.FindingSquares());
    }

    [Fact]
    public void A_key_entry_steps_through_its_frames_and_wraps_around()
    {
        double[] values = [32, 64, 32, 128, 32, 64];
        FrameMapGraph map = Map(Numbers(values));
        int lowest = map.ClassAt(0);

        Assert.Equal(100, map.NextFrameOf(lowest, null));
        Assert.Equal(200, map.NextFrameOf(lowest, 100));
        Assert.Equal(300, map.NextFrameOf(lowest, 200));
        Assert.Equal(100, map.NextFrameOf(lowest, 300));
        Assert.Equal(100 + (3 * 50), map.NextFrameOf(map.ClassAt(3), 100 + (3 * 50)));
        Assert.Null(map.NextFrameOf(99, null));
        Assert.Equal("32 kbps · 3 frames", map.DescribeKeyEntry(lowest));
    }

    [Fact]
    public void The_same_bitrate_has_the_same_class_in_two_files_with_different_ranges_and_a_step_a_file_does_not_use_is_dimmed()
    {
        ValueRange steps = ValueRange.OfSteps(Mpeg1Layer3);
        FrameMapGraph cbr = Map(Numbers([.. Enumerable.Repeat(128.0, 30)], range: steps));
        FrameMapGraph vbr = Map(Numbers([32, 128, 320, 128, 192, 40], range: steps));

        Assert.Equal(14, cbr.ClassCount);
        Assert.Equal(14, vbr.ClassCount);
        Assert.True(cbr.IsMultiHue);
        Assert.Equal(8, cbr.ClassAt(0));
        Assert.Equal(cbr.ClassAt(0), vbr.ClassAt(1));
        Assert.Equal(0, vbr.ClassAt(0));
        Assert.Equal(13, vbr.ClassAt(2));

        // The key shows every declared step, with the declared ends, whatever the file uses; unused steps count 0.
        Assert.False(cbr.IsSingleValue);
        Assert.Equal("32 kbps", cbr.KeyLow);
        Assert.Equal("320 kbps", cbr.KeyHigh);
        Assert.Equal(14, cbr.Key.Count);
        Assert.Equal([0L, 0, 0, 0, 0, 0, 0, 0, 30, 0, 0, 0, 0, 0], cbr.Key.Select(k => k.Count));
        Assert.Equal(5, vbr.Key.Count(k => k.Count > 0));
        Assert.Equal("56 kbps · 0 frames", cbr.DescribeKeyEntry(3));
        Assert.DoesNotContain(3, Enumerable.Range(0, 30).Select(cbr.ClassAt));
    }

    [Fact]
    public void A_fixed_range_spans_the_declared_ends_where_the_file_has_few_values()
    {
        FrameMapGraph map = Map(Numbers([10, 12, 10, 12], "%", ValueRange.Between(0, 100)));

        Assert.Equal(8, map.ClassCount);
        Assert.True(map.UsesRange);
        Assert.False(map.IsMultiHue);
        Assert.Equal("0 %", map.KeyLow);
        Assert.Equal("100 %", map.KeyHigh);
        Assert.Equal([0, 0, 0, 0], Enumerable.Range(0, 4).Select(map.ClassAt));
        Assert.Equal(8, map.Key.Count);
        Assert.Equal("0 to 12.5 % · 4 frames", map.DescribeKeyEntry(0));

        FrameMapGraph spread = Map(Numbers([0, 50, 100, 99.9], "%", ValueRange.Between(0, 100)));
        Assert.Equal([0, 4, 7, 7], Enumerable.Range(0, 4).Select(spread.ClassAt));
    }

    [Fact]
    public void A_value_outside_the_declared_range_clamps_to_an_end_class_and_one_between_steps_goes_to_the_nearest_and_the_hover_says_so()
    {
        FrameMapGraph map = Map(Numbers([16, 32, 130, 320, 400, 144], range: ValueRange.OfSteps(Mpeg1Layer3)));

        Assert.Equal([0, 0, 8, 13, 13, 8], Enumerable.Range(0, 6).Select(s => map.ClassAt(s)));
        Assert.Equal(ValueClamp.Below, Clamp(map, 0));
        Assert.Equal(ValueClamp.None, Clamp(map, 1));
        Assert.Equal(ValueClamp.NotAStep, Clamp(map, 2));
        Assert.Equal(ValueClamp.None, Clamp(map, 3));
        Assert.Equal(ValueClamp.Above, Clamp(map, 4));
        Assert.Equal(ValueClamp.NotAStep, Clamp(map, 5));
        Assert.Equal(4, map.ClampedCount);
        Assert.EndsWith("16 kbps (below the declared range of 32 to 320 kbps)", map.Describe(0), StringComparison.Ordinal);
        Assert.EndsWith("400 kbps (above the declared range of 32 to 320 kbps)", map.Describe(4), StringComparison.Ordinal);
        Assert.EndsWith("130 kbps (not a declared step)", map.Describe(2), StringComparison.Ordinal);
        Assert.EndsWith("320 kbps", map.Describe(3), StringComparison.Ordinal);

        // Breaking the format draws without a ring: only a finding rings a square.
        Assert.Empty(map.FindingSquares());

        FrameMapGraph fixedRange = Map(Numbers([-5, 50, 120], "%", ValueRange.Between(0, 100)));
        Assert.Equal([0, 4, 7], Enumerable.Range(0, 3).Select(fixedRange.ClassAt));
        Assert.Equal(ValueClamp.Below, Clamp(fixedRange, 0));
        Assert.Equal(ValueClamp.Above, Clamp(fixedRange, 2));
        Assert.EndsWith("120 % (above the declared range of 0 to 100 %)", fixedRange.Describe(2), StringComparison.Ordinal);
    }

    private static ValueClamp Clamp(FrameMapGraph map, int square)
    {
        map.ClassAt(square, out ValueClamp clamp);
        return clamp;
    }

    [Fact]
    public void A_unit_of_several_values_is_a_split_square_for_two_to_four_and_a_share_mix_otherwise()
    {
        Assert.Equal(FrameMapGraph.CellLayout.Single, Categories(1).Layout);
        Assert.Equal(FrameMapGraph.CellLayout.Split, Categories(2).Layout);
        Assert.Equal(FrameMapGraph.CellLayout.Split, Categories(4).Layout);
        Assert.Equal(FrameMapGraph.CellLayout.Mix, Categories(5).Layout);
        Assert.Equal(FrameMapGraph.CellLayout.Mix, Categories(6).Layout);
        Assert.Equal(FrameMapGraph.CellLayout.Mix, Categories(0).Layout);
    }

    private static FrameMapGraph Categories(int valuesPerUnit)
    {
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", valuesPerUnit);
        for (int unit = 0; unit < 3; unit++)
        {
            builder.Add(valuesPerUnit == 0 ? [.. Enumerable.Repeat((byte)0, unit + 1)] : [.. Enumerable.Repeat((byte)(unit % 3), valuesPerUnit)]);
        }

        CategorySeries series = builder.Build(Offsets(3), null, null, "d");
        return new FrameMapGraph(series, series.X);
    }

    [Fact]
    public void The_hover_of_a_category_square_lists_every_value_in_the_parsers_order()
    {
        string[] names = ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"];
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4, names);
        builder.Add([0, 0, 2, 2]);
        builder.Add([0, 0, 0, CategorySeries.None]);
        CategorySeries series = builder.Build(Offsets(2), null, null, "d");
        var map = new FrameMapGraph(series, series.X);

        Assert.Equal("Frame 1 · 0x64 · granule 1, channel 1: Long · granule 1, channel 2: Long · granule 2, channel 1: Short · granule 2, channel 2: Short", map.Describe(0));
        Assert.EndsWith("granule 2, channel 2: no category", map.Describe(1), StringComparison.Ordinal);
        Assert.Equal(100, map.NextFrameOf(2, 100));
        Assert.Equal(100, map.NextFrameOf(2, null));
    }

    [Fact]
    public void Entries_with_a_series_per_frame_offer_the_map_and_the_others_do_not()
    {
        double[] offsets = Offsets(40);
        double[] values = [.. Enumerable.Range(0, 40).Select(i => 128.0 + (i % 3 * 32))];
        double[] starts = [0, 1];
        var builder = new CategorySeriesBuilder("Stereo Coding", CategoryTestData.StereoCodings, "frame", 1);
        for (int i = 0; i < 40; i++)
        {
            builder.Add((byte)(i % 2));
        }

        StreamInfo stream = new()
        {
            Nodes = [new Node("Zz", 0, 3000)],
            Codec = "Test",
            Duration = 2,
            Series =
            [
                new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, values) { Quantity = "Bitrate" },
                new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, starts, new double[] { 128, 160 }) { Quantity = "Bitrate" },
                new Series("Seconds Only", "%", SeriesAxis.Seconds, starts, new double[] { 10, 20 }),
            ],
            CategorySeries = [builder.Build(offsets, null, null, "d")],
        };

        IReadOnlyList<GraphChoice> entries = GraphsViewModel.For(stream);

        Assert.Equal(["Per Frame", "Over Time", "Distribution", "Frame Map"], entries.Single(e => e.Title == "Bitrate").Forms.Select(f => f.Title));
        Assert.Equal(["Frames", "Summary", "Frame Map"], entries.Single(e => e.Title == "Stereo Coding").Forms.Select(f => f.Title));
        Assert.DoesNotContain(entries.Single(e => e.Title == "Seconds Only").Forms, f => f.Kind == GraphForm.Map);
        Assert.DoesNotContain(entries.Single(e => e.Title == CodingSummaryGraph.TitleText).Forms, f => f.Kind == GraphForm.Map);
        Assert.IsType<FrameMapGraph>(entries.Single(e => e.Title == "Bitrate").Forms.Single(f => f.Kind == GraphForm.Map).Graph);
        Assert.Equal("Every frame as a square, in rows like text", entries.Single(e => e.Title == "Bitrate").Forms[^1].Description);
    }

    [Fact]
    public void A_real_files_findings_ring_the_squares_of_the_frames_they_point_into()
    {
        Report report = Analyzer.CreateDefault().AnalyzeFile(Fixtures.Mpeg("external-blade080-stereo.mp3"));
        IReadOnlyList<MapFinding> findings = GraphsViewModel.MapFindings(report);
        GraphChoice bitrate = GraphsViewModel.For(report.Streams[0], findings).Single(e => e.Title == "Bitrate");
        var map = (FrameMapGraph)bitrate.Forms.Single(f => f.Kind == GraphForm.Map).Graph;

        Assert.NotEmpty(findings);
        Assert.NotEmpty(map.FindingSquares());
        Assert.All(map.FindingSquares(), s => Assert.Contains(map.OffsetOf(s), findings.Select(f => f.Offset)));
        Assert.All(map.FindingSquares(), s => Assert.Matches(" · (Warning|Error|Info): ", map.Describe(s)));
    }

    [Fact]
    public void The_key_range_names_the_unit_once()
    {
        FrameMapGraph steps = Map(Numbers([32, 320, 128], range: ValueRange.OfSteps(Mpeg1Layer3)));
        Assert.Equal("32 to 320 kbps", steps.KeyRange);
        Assert.Equal("32 kbps", steps.KeyLow);
        Assert.Equal("0 to 100 %", Map(Numbers([5, 50], "%", ValueRange.Between(0, 100))).KeyRange);
    }

    [Fact]
    public void A_click_on_the_finding_entry_steps_through_the_ringed_frames_of_the_shown_severities_and_wraps_around()
    {
        long[] offsets = [.. Offsets(10).Select(o => (long)o)];
        MapFinding[] findings =
        [
            new(offsets[7], Severity.Warning, "Odd.", 3),
            new(offsets[2], Severity.Error, "Bad.", 1),
            new(offsets[5], Severity.Info, "Note.", 1),
        ];
        FrameMapGraph map = Map(Numbers([.. Enumerable.Repeat(128.0, 10)]), findings: findings);

        Assert.Equal(offsets[2], map.NextFrameOf(FrameMapGraph.FindingEntry, null));
        Assert.Equal(offsets[5], map.NextFrameOf(FrameMapGraph.FindingEntry, offsets[2]));
        Assert.Equal(offsets[7], map.NextFrameOf(FrameMapGraph.FindingEntry, offsets[5]));
        Assert.Equal(offsets[2], map.NextFrameOf(FrameMapGraph.FindingEntry, offsets[7]));
        Assert.Equal(offsets[7], map.NextFrameOf(FrameMapGraph.FindingEntry, offsets[2], SeverityMask.Warning));
        Assert.Equal(offsets[7], map.NextFrameOf(FrameMapGraph.FindingEntry, offsets[7], SeverityMask.Warning));
        Assert.Null(map.NextFrameOf(FrameMapGraph.FindingEntry, null, SeverityMask.None));
    }

    [Fact]
    public void The_no_value_entry_of_a_category_map_counts_the_squares_drawn_empty()
    {
        // Frames 0 to 5: a coded unit, a value without a category, a unit of two values with none coded, a unit of two values with one
        // coded, a frame the series skips, a coded unit, and a last frame it skips.
        double[] frames = Offsets(6);
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule", 2);
        builder.Add([0, 0]);
        builder.Add([CategorySeries.None, CategorySeries.None]);
        builder.Add([CategorySeries.None, 1]);
        builder.Add([2, 2]);
        CategorySeries series = builder.Build(new[] { frames[0], frames[1], frames[2], frames[4] }, null, null, "d");
        var map = new FrameMapGraph(series, frames);

        Assert.Equal(6, map.Count);
        Assert.Equal(3, map.EmptyCount);

        var single = new CategorySeriesBuilder("Modes", CategoryTestData.StereoCodings, "frame", 1);
        single.Add(0);
        single.Add(CategorySeries.None);
        single.Add(1);
        CategorySeries ones = single.Build(new[] { frames[0], frames[1], frames[3] }, null, null, "d");
        var skipping = new FrameMapGraph(ones, frames);
        Assert.Equal(1 + 3, skipping.EmptyCount);
        Assert.Equal("no category", skipping.Describe(1).Split(" · ")[^1]);
        Assert.Equal("no value", skipping.Describe(2).Split(" · ")[^1]);
    }
}
