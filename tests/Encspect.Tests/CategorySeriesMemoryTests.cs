using Encspect.Core;

namespace Encspect.Tests;

// The heap is the whole process's, so the series' memory is measured alone, in the collection that MemoryTests runs in, and as
// MemoryTests measures (docs/development.md, "Memory tests"): the live heap after full compacting collections, read from the
// collector's record of the last one, with the series alive and without it. GC.GetTotalMemory is the collector's running
// estimate, not a measurement of what survived: the first version of this test used it and read 8.09 MB for a 4 MB series on
// macOS (about one 4 MB region of free space counted as in use) and 23,000 bytes under the budget on Windows.
[Collection(nameof(MemoryTests))]
public sealed class CategorySeriesMemoryTests : IDisposable
{
    private readonly SharedPoolGuard _pools = new();

    public void Dispose()
    {
        _pools.Check();
    }

    // The budget of the design: a byte per value, a count byte per unit of a variable layout, a weight byte per value where
    // weights are used and four bytes per 1,024 units. What the series keeps is that, and the arrays' headers.
    [Fact]
    public void A_series_keeps_what_the_budget_says_and_the_heap_agrees()
    {
        const int units = 1_000_000;
        Category[] categories = [.. CategorySeriesTests.BlockTypes];
        byte[] four = [0, 1, 2, 4];
        double[] x = new double[units];

        (long fixedSmallest, long fixedLargest) = Kept(() =>
        {
            var builder = new CategorySeriesBuilder("Fixed", categories, "value", 4);
            for (int i = 0; i < units; i++)
            {
                builder.Add(four);
            }

            return builder.Build(x, null, null, "d");
        }, out CategorySeries fixedSeries);
        (long variableSmallest, long variableLargest) = Kept(() =>
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

        // Both bounds come from the budget, and each is checked against the reading that cannot be too extreme for it. A series keeps
        // at most the budget and the headers of its 4 KB blocks, about one percent, so five percent: a series that kept its builder's
        // growth or a copy of its codes doubles every reading, so the smallest must pass the upper bound. A series keeps at least its
        // budget, less what two readings of Live can differ by: each stops when two collections in a row agree within 64 KB, so it
        // can be that far off, and their difference twice that. A reading the series does not show up in is off by millions of bytes,
        // so the largest must pass the lower bound. The smallest cannot: once on ubuntu-26.04, at the end of the corrupt-b shard, the
        // smaller of two pairs read 3,909,728 bytes for a budget of 4,000,000, 124 KB under the 4,033,856 that 120 pairs read on
        // linux-x64 in a quiet process (33,688 to 34,024 bytes over the budget, 27,440 and 29,736 twice, and 122,696 for the very
        // first build, which allocates what the runtime needs to start).
        const long Agreement = 2 * 64 * 1024;
        Assert.InRange(fixedLargest, fixedSeries.StorageBytes - Agreement, long.MaxValue);
        Assert.InRange(fixedSmallest, 0, (long)(fixedSeries.StorageBytes * 1.05));
        Assert.InRange(variableLargest, variableSeries.StorageBytes - Agreement, long.MaxValue);
        Assert.InRange(variableSmallest, 0, (long)(variableSeries.StorageBytes * 1.05));
        GC.KeepAlive(x);
        GC.KeepAlive(fixedSeries);
        GC.KeepAlive(variableSeries);
    }

    // What building the series adds to the live heap: the reading with it alive less the one before, taken in four pairs, and the
    // smallest and the largest difference given. A reading that is off makes one pair's difference too small or too large; a series
    // that keeps too much makes every difference large, and one the readings do not see makes every difference small. Everything
    // else the build allocates is dead by the second reading of a pair.
    private static (long Smallest, long Largest) Kept(Func<CategorySeries> build, out CategorySeries series)
    {
        const int pairs = 4;
        long smallest = long.MaxValue;
        long largest = long.MinValue;
        series = null!;
        for (int run = 0; run < pairs; run++)
        {
            long before = MemoryTests.Live();
            series = build();
            long with = MemoryTests.Live();
            smallest = Math.Min(smallest, with - before);
            largest = Math.Max(largest, with - before);
            if (run < pairs - 1)
            {
                series = null!;
            }
        }

        return (smallest, largest);
    }
}
