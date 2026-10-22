using Encspect.Core;

namespace Encspect.Tests;

// The heap is the whole process's, so the series' memory is measured alone, in the collection that MemoryTests runs in, and as
// MemoryTests measures (docs/development.md, "Memory tests"): the live heap after full compacting collections, read from the
// collector's record of the last one, with the series alive and without it. GC.GetTotalMemory is the collector's running
// estimate, not a measurement of what survived: the first version of this test used it and read 8.09 MB for a 4 MB series on
// macOS (about one 4 MB region of free space counted as in use) and 23,000 bytes under the budget on Windows.
[Collection(nameof(MemoryTests))]
public class CategorySeriesMemoryTests
{
    // The budget of the design: a byte per value, a count byte per unit of a variable layout, a weight byte per value where
    // weights are used and four bytes per 1,024 units. What the series keeps is that, and the arrays' headers.
    [Fact]
    public void A_series_keeps_what_the_budget_says_and_the_heap_agrees()
    {
        const int units = 1_000_000;
        Category[] categories = [.. CategorySeriesTests.BlockTypes];
        byte[] four = [0, 1, 2, 4];
        double[] x = new double[units];

        long fixedKept = Kept(() =>
        {
            var builder = new CategorySeriesBuilder("Fixed", categories, "value", 4);
            for (int i = 0; i < units; i++)
            {
                builder.Add(four);
            }

            return builder.Build(x, null, null, "d");
        }, out CategorySeries fixedSeries);
        long variableKept = Kept(() =>
        {
            var builder = new CategorySeriesBuilder("Variable", categories, "value");
            for (int i = 0; i < units; i++)
            {
                builder.Add(four.AsSpan(0, 1 + (i % 4)));
            }

            return builder.Build(x, null, null, "d");
        }, out CategorySeries variableSeries);

        Assert.Equal(4L * units, fixedSeries.StorageBytes);
        Assert.Equal((2_500_000L) + units + (4 * ((units + 1023) / 1024)), variableSeries.StorageBytes);

        // Both bounds come from the budget. The readings are of live objects, so a series keeps at least its budget (less the 64 KB
        // within which two readings of Live agree), and at most the budget and the headers of its 4 KB blocks, about one percent,
        // so five percent. A series that kept its builder's growth, or a copy of its codes, doubles the reading and fails the upper
        // bound; one the reading does not see misses the lower by millions of bytes.
        const long Agreement = 64 * 1024;
        Assert.InRange(fixedKept, fixedSeries.StorageBytes - Agreement, (long)(fixedSeries.StorageBytes * 1.05));
        Assert.InRange(variableKept, variableSeries.StorageBytes - Agreement, (long)(variableSeries.StorageBytes * 1.05));
        GC.KeepAlive(x);
        GC.KeepAlive(fixedSeries);
        GC.KeepAlive(variableSeries);
    }

    // What building the series adds to the live heap: the reading with it alive less the one before, taken twice and the smaller
    // counted (a reading that is off inflates one pair, a series that keeps too much inflates both). Everything else the build
    // allocates is dead by the second reading.
    private static long Kept(Func<CategorySeries> build, out CategorySeries series)
    {
        long smallest = long.MaxValue;
        series = null!;
        for (int run = 0; run < 2; run++)
        {
            long before = MemoryTests.Live();
            series = build();
            long with = MemoryTests.Live();
            smallest = Math.Min(smallest, with - before);
            if (run == 0)
            {
                series = null!;
            }
        }

        return smallest;
    }
}
