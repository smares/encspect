using Encspect.Core;

namespace Encspect.Tests;

// The category series of docs/design/coding-graphs.md: units with a fixed or variable number of values, the caps that
// count values without coding them, weights, groupings, counts over ranges and the time index.
public class CategorySeriesTests
{
    internal static readonly IReadOnlyList<Category> BlockTypes =
    [
        new("Long", "Long block."),
        new("Start", "Start block."),
        new("Short", "Short block."),
        new("Mixed", "Mixed block."),
        new("Stop", "Stop block."),
        new("Reserved", "Reserved block type."),
    ];

    internal static ReadOnlyMemory<double> Offsets(int units) => Enumerable.Range(0, units).Select(i => i * 417.0).ToArray();

    [Fact]
    public void A_fixed_layout_keeps_one_code_per_value_and_counts_them()
    {
        var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "granule and channel", 4, ["g1c1", "g1c2", "g2c1", "g2c2"]);
        builder.Add([0, 0, 0, 0]);
        builder.Add([1, 1, 2, 2]);
        builder.Add([4, CategorySeries.None, 0, 0]);

        CategorySeries series = builder.Build(Offsets(3), null, null, "Block types.");

        Assert.Equal(3, series.Count);
        Assert.Equal(4, series.ValuesPerUnit);
        Assert.Equal(4, series.ValueCount(2));
        Assert.Equal(2, series[1, 2]);
        Assert.Equal(CategorySeries.None, series[2, 1]);
        Assert.Equal(CategorySeries.None, series[2, 4]);
        Assert.Equal([6L, 2, 2, 0, 1, 0], series.Counts);
        Assert.Equal(11, series.Total);
        Assert.Equal(0, series.UncodedValues);
        Assert.Equal(12, series.StorageBytes);
        Assert.False(series.HasWeights);
        Assert.Equal(1, series.WeightAt(0, 0));
        Assert.Equal(["g1c1", "g1c2", "g2c1", "g2c2"], series.ValueNames);
    }

    [Fact]
    public void A_fixed_layout_refuses_a_unit_of_another_length()
    {
        var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "granule and channel", 4);

        Assert.Throws<ArgumentException>(() => builder.Add([0, 0]));
        Assert.Throws<ArgumentException>(() => builder.Add(0));
        Assert.Throws<ArgumentException>(() => new CategorySeriesBuilder("X", BlockTypes, "v", 2, ["only one"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CategorySeriesBuilder("X", BlockTypes, "v", 255));
    }

    [Fact]
    public void A_variable_layout_finds_every_unit_through_the_sparse_index()
    {
        var random = new Random(11);
        const int units = 5000;
        var expected = new List<byte[]>();
        var builder = new CategorySeriesBuilder("Window Sequences", BlockTypes, "channel stream");
        for (int i = 0; i < units; i++)
        {
            byte[] codes = [.. Enumerable.Range(0, random.Next(0, 9)).Select(_ => (byte)random.Next(6))];
            expected.Add(codes);
            builder.Add(codes);
        }

        CategorySeries series = builder.Build(Offsets(units), null, null, "Window sequences.");

        Assert.Equal(0, series.ValuesPerUnit);
        for (int unit = 0; unit < units; unit++)
        {
            Assert.Equal(expected[unit].Length, series.ValueCount(unit));
            for (int value = 0; value < expected[unit].Length; value++)
            {
                Assert.Equal(expected[unit][value], series[unit, value]);
            }

            Assert.Equal(CategorySeries.None, series[unit, expected[unit].Length]);
        }

        long values = expected.Sum(e => e.Length);
        Assert.Equal(values, series.Total);
        Assert.Equal(values + units + (4 * ((units + 1023) / 1024)), series.StorageBytes);

        // Reading from the middle and from the start gives the same units.
        CategorySeries.UnitReader reader = series.Reader(3000);
        Span<byte> codesRead = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        for (int unit = 3000; unit < units; unit++)
        {
            Assert.Equal(unit, reader.Unit);
            int n = reader.Next(codesRead);
            Assert.Equal(expected[unit], codesRead[..n].ToArray());
        }

        Assert.Equal(-1, reader.Next(codesRead));
    }

    [Fact]
    public void Weights_count_a_value_as_many_times_and_cost_a_byte_only_where_used()
    {
        var builder = new CategorySeriesBuilder("Modes", [new("SILK", "s"), new("CELT", "c")], "stream packet", 1);
        builder.Add(0);
        builder.Add(1, 3);
        builder.Add(0, 2);

        CategorySeries series = builder.Build(Offsets(3), null, null, "Modes.");

        Assert.Equal([3L, 3], series.Counts);
        Assert.True(series.HasWeights);
        Assert.Equal(1, series.WeightAt(0, 0));
        Assert.Equal(3, series.WeightAt(1, 0));
        Assert.Equal(2, series.WeightAt(2, 0));
        Assert.Equal(6, series.StorageBytes);

        var counts = new long[2];
        series.CountRange(1, 3, counts);
        Assert.Equal([2L, 3], counts);

        var builderWithZero = new CategorySeriesBuilder("Modes", [new("SILK", "s")], "stream packet", 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => builderWithZero.Add(0, 0));
    }

    [Fact]
    public void Values_past_254_of_a_unit_are_counted_without_a_code()
    {
        var builder = new CategorySeriesBuilder("Block Sizes", BlockTypes, "block");
        byte[] big = [.. Enumerable.Range(0, 300).Select(i => (byte)(i % 2))];
        builder.Add(big);
        builder.Add([2]);

        CategorySeries series = builder.Build(Offsets(2), null, null, "Block sizes.");

        Assert.Equal(CategorySeries.MaxValuesPerUnit, series.ValueCount(0));
        Assert.Equal(1, series.ValueCount(1));
        Assert.Equal(46, series.UncodedValues);
        Assert.Equal([150L, 150, 1, 0, 0, 0], series.Counts);
        Assert.Equal(301, series.Total);
        var drawn = new long[6];
        series.CountRange(0, 2, drawn);
        Assert.Equal(255, drawn.Sum());
    }

    [Fact]
    public void Past_two_to_the_24_values_a_series_stops_coding_and_keeps_counting()
    {
        const int units = 66_052 + 5;
        byte[] unit = new byte[CategorySeries.MaxValuesPerUnit];
        Array.Fill(unit, (byte)1);

        CategorySeries variable = Fill(new CategorySeriesBuilder("V", BlockTypes, "block"), units, unit);
        CategorySeries fixedLayout = Fill(new CategorySeriesBuilder("F", BlockTypes, "block", CategorySeries.MaxValuesPerUnit), units, unit);

        foreach (CategorySeries series in new[] { variable, fixedLayout })
        {
            Assert.Equal(units, series.Count);
            Assert.Equal((long)units * unit.Length, series.Total);
            Assert.Equal(series.Total, series.Counts[1]);
            Assert.Equal(CategorySeries.MaxValuesPerUnit, series.ValueCount(66_051));
            Assert.Equal(0, series.ValueCount(units - 1));
            Assert.Equal(CategorySeries.None, series[units - 1, 0]);
            var drawn = new long[6];
            series.CountRange(0, units, drawn);
            Assert.InRange(drawn[1], CategorySeries.MaxCodedValues - unit.Length, CategorySeries.MaxCodedValues);
            Assert.Equal(series.Total - drawn[1], series.UncodedValues);
            Assert.InRange(series.StorageBytes, CategorySeries.MaxCodedValues - unit.Length, CategorySeries.MaxCodedValues + 100_000);
        }

        // A variable layout fills the last bytes with part of a unit; a fixed one takes whole units only.
        Assert.Equal(CategorySeries.MaxCodedValues, variable.Total - variable.UncodedValues);
        Assert.True(fixedLayout.UncodedValues > variable.UncodedValues);

        static CategorySeries Fill(CategorySeriesBuilder builder, int units, byte[] codes)
        {
            for (int i = 0; i < units; i++)
            {
                builder.Add(codes);
            }

            return builder.Build(new double[units], null, null, "Caps.");
        }
    }

    [Fact]
    public void A_grouping_costs_no_byte_per_value_and_sums_its_sources_counts()
    {
        var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "granule and channel", 2, concept: GraphConcept.BlockSwitching);
        for (int i = 0; i < 1000; i++)
        {
            builder.Add(i % 50 == 0 ? [2, 4] : [0, 1]);
        }

        CategorySeries usage = builder.Build(Offsets(1000), null, null, "Block usage.");
        Category[] sizes = [new("Long, start and stop", "l"), new("Short", "s")];
        CategorySeries grouped = usage.Group("Block Sizes", sizes, [0, 0, 1, CategorySeries.None, 0, 1], null, "Sizes.");

        Assert.Equal(GraphConcept.BlockSwitching, grouped.Concept);
        Assert.Same(usage, grouped.Source);
        Assert.Equal(2000L, usage.StorageBytes);
        Assert.Equal(6L, grouped.StorageBytes);
        Assert.Equal([1980L, 20], grouped.Counts);
        Assert.Equal(2000, grouped.Total);
        Assert.Equal(usage.Count, grouped.Count);
        Assert.Equal(1, grouped[0, 0]);
        Assert.Equal(0, grouped[0, 1]);
        Assert.Equal(0, grouped[1, 0]);
        Assert.Equal(CategorySeries.None, grouped[1, 2]);

        // Ranges and the reader read through the map, and a grouping of a grouping through both.
        var counts = new long[2];
        grouped.CountRange(0, 100, counts);
        Assert.Equal([198L, 2], counts);
        Array.Clear(counts);
        grouped.CountRange(0, 100, 1, counts);
        Assert.Equal([100L, 0], counts);
        CategorySeries again = grouped.Group("Short", [new("Not short", "n"), new("Short", "s")], [0, 1], null, "Again.");
        Assert.Equal(grouped.Counts, again.Counts);
        Span<byte> codes = stackalloc byte[2];
        CategorySeries.UnitReader reader = again.Reader(50);
        Assert.Equal(2, reader.Next(codes));
        Assert.Equal([1, 0], codes.ToArray());

        Assert.Throws<ArgumentException>(() => usage.Group("X", sizes, [0, 0, 1], null, "x"));
        Assert.Throws<ArgumentException>(() => usage.Group("X", sizes, [0, 0, 1, 0, 0, 2], null, "x"));
    }

    [Fact]
    public void Count_range_matches_a_brute_force_count_in_both_forms()
    {
        var random = new Random(5);
        const int units = 3000;
        var builder = new CategorySeriesBuilder("Fixed", BlockTypes, "value", 3);
        var variable = new CategorySeriesBuilder("Variable", BlockTypes, "value");
        byte[][] rows = new byte[units][];
        byte[][] lengths = new byte[units][];
        for (int i = 0; i < units; i++)
        {
            rows[i] = [(byte)random.Next(6), (byte)random.Next(6), random.Next(10) == 0 ? CategorySeries.None : (byte)random.Next(6)];
            builder.Add(rows[i]);
            lengths[i] = rows[i][..random.Next(0, 4)];
            variable.Add(lengths[i]);
        }

        CategorySeries fixedSeries = builder.Build(Offsets(units), null, null, "f");
        CategorySeries variableSeries = variable.Build(Offsets(units), null, null, "v");

        foreach ((int first, int end) in new[] { (0, units), (0, 0), (17, 17), (1000, 2050), (2999, 3000), (5, 1030) })
        {
            AssertRange(fixedSeries, rows, first, end);
            AssertRange(variableSeries, lengths, first, end);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => fixedSeries.CountRange(0, units + 1, new long[6]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixedSeries.CountRange(5, 4, new long[6]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixedSeries.CountRange(0, 4, new long[5]));

        static void AssertRange(CategorySeries series, byte[][] rows, int first, int end)
        {
            var all = new long[6];
            series.CountRange(first, end, all);
            var expected = new long[6];
            foreach (byte code in rows[first..end].SelectMany(r => r).Where(c => c != CategorySeries.None))
            {
                expected[code]++;
            }

            Assert.Equal(expected, all);
            for (int lane = 0; lane < 3; lane++)
            {
                var one = new long[6];
                series.CountRange(first, end, lane, one);
                var expectedLane = new long[6];
                foreach (byte[] row in rows[first..end])
                {
                    if (lane < row.Length && row[lane] != CategorySeries.None)
                    {
                        expectedLane[row[lane]]++;
                    }
                }

                Assert.Equal(expectedLane, one);
            }
        }
    }

    [Fact]
    public void A_time_index_that_does_not_fit_is_a_parser_bug()
    {
        CategorySeriesBuilder Filled()
        {
            var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "block", 1);
            for (int i = 0; i < 10; i++)
            {
                builder.Add(0);
            }

            return builder;
        }

        CategorySeries ok = Filled().Build(Offsets(10), null, null, "d", null, new TimeIndex(new double[] { 0, 1, 2, 3 }, new[] { 0, 4, 4, 10 }));
        Assert.Equal(4, ok.Time!.FirstUnits.Length);

        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(10), null, null, "d", null, new TimeIndex(new double[] { 0, 1 }, new[] { 0, 4, 5 })));
        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(10), null, null, "d", null, new TimeIndex(new double[] { 0, 1 }, new[] { 5, 4 })));
        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(10), null, null, "d", null, new TimeIndex(new double[] { 0, 1 }, new[] { 0, 11 })));
        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(10), null, null, "d", null, new TimeIndex(new double[] { 0, 1 }, new[] { -1, 0 })));
    }

    [Fact]
    public void Build_refuses_what_a_parser_must_not_hand_it()
    {
        var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "block", 1);
        builder.Add(0);
        builder.Add(6);

        Assert.Throws<ArgumentException>(() => builder.Build(Offsets(2), null, null, "d"));

        builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "block", 1);
        builder.Add(0);
        Assert.Throws<ArgumentException>(() => builder.Build(Offsets(2), null, null, "d"));

        var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 10 }, new double[] { 1, 2 });
        builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "block", 1);
        builder.Add(0);
        builder.Add(1);
        Assert.Throws<ArgumentException>(() => builder.Build(new double[] { 0, 11 }, bitrate, null, "d"));
        CategorySeries series = builder.Build(bitrate.X, bitrate, null, "d");
        Assert.Same(bitrate, series.XSeries);
        Assert.Throws<InvalidOperationException>(() => builder.Add(0));
        Assert.Throws<InvalidOperationException>(() => builder.Build(bitrate.X, bitrate, null, "d"));
    }

    [Fact]
    public void A_series_has_one_to_255_categories()
    {
        Category[] many = [.. Enumerable.Range(0, 255).Select(i => new Category($"C{i}", "d"))];
        var builder = new CategorySeriesBuilder("Many", many, "value", 1);
        builder.Add(254);
        builder.Add(CategorySeries.None);

        CategorySeries series = builder.Build(Offsets(2), null, null, "d");

        Assert.Equal(255, series.Counts.Count);
        Assert.Equal(1, series.Counts[254]);
        Assert.Equal(1, series.Total);
        Assert.Throws<ArgumentException>(() => new CategorySeriesBuilder("Many", [.. many, new("C255", "d")], "value"));
        Assert.Throws<ArgumentException>(() => new CategorySeriesBuilder("None", [], "value"));
    }

    [Fact]
    public void Values_without_a_category_past_a_cap_are_not_counted_as_uncoded()
    {
        var builder = new CategorySeriesBuilder("Block Sizes", BlockTypes, "block");
        builder.Add([.. Enumerable.Repeat(CategorySeries.None, 300)]);
        builder.Add([.. Enumerable.Repeat((byte)0, 250), .. Enumerable.Repeat(CategorySeries.None, 10), 2, 2]);

        CategorySeries series = builder.Build(Offsets(2), null, null, "d");

        Assert.Equal(252, series.Total);
        Assert.Equal(2, series.UncodedValues);
    }

    [Fact]
    public void A_unit_with_a_weight_of_zero_is_refused_before_a_count_changes()
    {
        var builder = new CategorySeriesBuilder("Modes", [new("A", "a"), new("B", "b")], "value");
        builder.Add([0, 1], [2, 2]);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add([1, 1, 0], [3, 0, 1]));
        CategorySeries series = builder.Build(Offsets(1), null, null, "d");

        Assert.Equal([2L, 2], series.Counts);
        Assert.Equal(1, series.Count);
    }

    [Fact]
    public void The_starts_of_a_time_index_are_finite_and_nondecreasing()
    {
        CategorySeriesBuilder Filled()
        {
            var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "block", 1);
            builder.Add(0);
            builder.Add(0);
            return builder;
        }

        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(2), null, null, "d", null, new TimeIndex(new[] { 0, double.NaN }, new[] { 0, 1 })));
        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(2), null, null, "d", null, new TimeIndex(new[] { 0, double.PositiveInfinity }, new[] { 0, 1 })));
        Assert.Throws<ArgumentException>(() => Filled().Build(Offsets(2), null, null, "d", null, new TimeIndex(new double[] { 2, 1 }, new[] { 0, 1 })));
        Assert.NotNull(Filled().Build(Offsets(2), null, null, "d", null, new TimeIndex(new double[] { 1, 1 }, new[] { 0, 1 })));
    }
}
