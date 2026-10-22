using Encspect.Core;

namespace Encspect.Tests;

// The heap is the whole process's, so the series' memory is measured alone, in the collection that MemoryTests runs in.
[Collection(nameof(MemoryTests))]
public class CategorySeriesMemoryTests
{
    // The budget of the design: a byte per value, a count byte per unit of a variable layout, a weight byte per value where
    // weights are used and four bytes per 1,024 units. The heap must hold little more than that.
    [Fact]
    public void A_series_keeps_what_the_budget_says_and_the_heap_agrees()
    {
        const int units = 1_000_000;
        Category[] categories = [.. CategorySeriesTests.BlockTypes];
        byte[] four = [0, 1, 2, 4];

        (long fixedHeap, CategorySeries fixedSeries) = Retained(() =>
        {
            var builder = new CategorySeriesBuilder("Fixed", categories, "value", 4);
            for (int i = 0; i < units; i++)
            {
                builder.Add(four);
            }

            return builder.Build(new double[units], null, null, "d");
        });
        (long variableHeap, CategorySeries variableSeries) = Retained(() =>
        {
            var builder = new CategorySeriesBuilder("Variable", categories, "value");
            for (int i = 0; i < units; i++)
            {
                builder.Add(four.AsSpan(0, 1 + (i % 4)));
            }

            return builder.Build(new double[units], null, null, "d");
        });

        // The x values are the stream's, 8 bytes per unit, and not counted.
        long xBytes = units * 8L;
        Assert.Equal(4L * units, fixedSeries.StorageBytes);
        Assert.Equal((2_500_000L) + units + (4 * ((units + 1023) / 1024)), variableSeries.StorageBytes);
        Assert.InRange(fixedHeap - xBytes, fixedSeries.StorageBytes, (long)(fixedSeries.StorageBytes * 1.05));
        Assert.InRange(variableHeap - xBytes, variableSeries.StorageBytes, (long)(variableSeries.StorageBytes * 1.05));
        GC.KeepAlive(fixedSeries);
        GC.KeepAlive(variableSeries);
    }

    private static (long Bytes, CategorySeries Series) Retained(Func<CategorySeries> build)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        CategorySeries series = build();
        long after = GC.GetTotalMemory(true);
        return (after - before, series);
    }
}
