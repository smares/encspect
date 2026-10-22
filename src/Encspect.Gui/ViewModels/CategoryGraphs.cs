using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The three views of a category series.
public enum CategoryView
{
    // The share of each category in each interval of audio time, stacked to 100%.
    OverTime,

    // One cell per unit, the categories of its values.
    Frames,

    // The count and share of each category over the whole stream, as EncSpot's window shows them.
    Summary,
}

// A graph that draws tables of categories with counts and shares.
public interface ISummaryGraph
{
    IReadOnlyList<CategorySeries> Tables { get; }
}

// The counts of the categories of consecutive intervals or units, merged into at most as many columns as the chart has
// pixels across: one pass over the codes, whatever the width. A column that holds a category in one unit only is
// marked rare, for the strip's ticks.
public sealed class CategoryColumns
{
    private readonly int[] _first;
    private readonly long[] _counts;
    private readonly bool[] _rare;

    private CategoryColumns(int[] first, long[] counts, bool[] rare, int categories)
    {
        _first = first;
        _counts = counts;
        _rare = rare;
        Categories = categories;
    }

    public int Count => _first.Length - 1;

    public int Categories { get; }

    // The first item (interval or unit) of a column, and the one after its last.
    public int First(int column) => _first[column];

    public int End(int column) => _first[column + 1];

    public ReadOnlySpan<long> Counts(int column) => _counts.AsSpan(column * Categories, Categories);

    public long Total(int column)
    {
        long total = 0;
        foreach (long count in Counts(column))
        {
            total += count;
        }

        return total;
    }

    // Whether the category is in one unit only of a column of several.
    public bool IsRare(int column, int category) => _rare[(column * Categories) + category];

    // The intervals of the series' time index, merged into at most the given number of columns.
    public static CategoryColumns OverTime(CategorySeries series, int columns)
    {
        ReadOnlySpan<int> firstUnits = series.Time!.FirstUnits.Span;
        int intervals = firstUnits.Length;
        int used = Math.Max(1, Math.Min(intervals, columns));
        int categories = series.Categories.Count;
        var first = new int[used + 1];
        var counts = new long[used * categories];
        for (int column = 0; column < used; column++)
        {
            int from = (int)((long)column * intervals / used);
            int to = (int)((long)(column + 1) * intervals / used);
            first[column] = from;
            if (to > from)
            {
                int firstUnit = firstUnits[from];
                int endUnit = to < intervals ? firstUnits[to] : series.Count;
                series.CountRange(firstUnit, endUnit, counts.AsSpan(column * categories, categories));
            }
        }

        first[used] = intervals;
        return new CategoryColumns(first, counts, new bool[used * categories], categories);
    }

    // The units, merged into at most the given number of columns; lane is the value of each unit to count, or -1 for all.
    public static CategoryColumns Frames(CategorySeries series, int columns, int lane)
    {
        int units = series.Count;
        int used = Math.Max(1, Math.Min(units, columns));
        int categories = series.Categories.Count;
        var first = new int[used + 1];
        var counts = new long[used * categories];
        var rare = new bool[used * categories];
        bool needsPresence = lane < 0 || series.HasWeights;
        for (int column = 0; column < used; column++)
        {
            int from = (int)((long)column * units / used);
            int to = (int)((long)(column + 1) * units / used);
            first[column] = from;
            Span<long> own = counts.AsSpan(column * categories, categories);
            if (lane < 0)
            {
                series.CountRange(from, to, own);
            }
            else
            {
                series.CountRange(from, to, lane, own);
            }

            if (to - from > 1)
            {
                if (needsPresence)
                {
                    MarkRare(series, from, to, lane, own, rare.AsSpan(column * categories, categories));
                }
                else
                {
                    for (int category = 0; category < categories; category++)
                    {
                        rare[(column * categories) + category] = own[category] == 1;
                    }
                }
            }
        }

        first[used] = units;
        return new CategoryColumns(first, counts, rare, categories);
    }

    // The heights of the segments of a stack of the given height whose segments are as the counts say, each category
    // present at least one pixel high, taken from the largest segments. Heights sum to the height.
    public static void StackHeights(ReadOnlySpan<long> counts, double height, Span<double> heights, double minimum = 1)
    {
        long total = 0;
        int present = 0;
        foreach (long count in counts)
        {
            total += count;
            present += count > 0 ? 1 : 0;
        }

        heights.Clear();
        if (total == 0 || height <= 0)
        {
            return;
        }

        if (present * minimum >= height)
        {
            // No room for the minimum of each: the stack is shared equally.
            for (int i = 0; i < counts.Length; i++)
            {
                heights[i] = counts[i] > 0 ? height / present : 0;
            }

            return;
        }

        double deficit = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            double ideal = height * counts[i] / total;
            heights[i] = counts[i] > 0 && ideal < minimum ? minimum : ideal;
            deficit += heights[i] - ideal;
        }

        // The pixels given to the rare come from the segments that can spare them, the largest first.
        while (deficit > 1e-9)
        {
            int largest = 0;
            for (int i = 1; i < heights.Length; i++)
            {
                if (heights[i] > heights[largest])
                {
                    largest = i;
                }
            }

            double spare = Math.Min(deficit, heights[largest] - minimum);
            if (spare <= 1e-12)
            {
                break;
            }

            heights[largest] -= spare;
            deficit -= spare;
        }
    }

    private static void MarkRare(CategorySeries series, int from, int to, int lane, ReadOnlySpan<long> counts, Span<bool> rare)
    {
        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        Span<int> units = stackalloc int[256];
        Span<int> stamp = stackalloc int[256];
        CategorySeries.UnitReader reader = series.Reader(from);
        for (int unit = from; unit < to; unit++)
        {
            int n = reader.Next(codes);
            for (int i = lane < 0 ? 0 : lane; i < (lane < 0 ? n : Math.Min(n, lane + 1)); i++)
            {
                byte code = codes[i];
                if (code != CategorySeries.None && stamp[code] != unit + 1)
                {
                    stamp[code] = unit + 1;
                    units[code]++;
                }
            }
        }

        for (int category = 0; category < rare.Length; category++)
        {
            rare[category] = counts[category] > 0 && units[category] == 1;
        }
    }
}

// One category series as a chart with three views: the share of each category over time, one cell per unit, and the
// summary table. Made from the series alone. Over time, a point is an interval of the time index; in the strip, a unit;
// in the summary, a category.
public sealed class CategoryGraph : Graph, ISummaryGraph
{
    // A strip of a fixed layout draws a row for each of this many values at most; more values share one row.
    public const int MaxRows = 8;

    private CategoryView _view;

    public CategoryGraph(CategorySeries series)
        : base(series.Name, string.Empty)
    {
        ArgumentNullException.ThrowIfNull(series);

        Series = series;
        var views = new List<CategoryView>();
        if (series.Time is { FirstUnits.Length: > 0 })
        {
            views.Add(CategoryView.OverTime);
        }

        if (series.Count > 0)
        {
            views.Add(CategoryView.Frames);
        }

        views.Add(CategoryView.Summary);
        Views = views;
        _view = views[0];
    }

    public CategorySeries Series { get; }

    public IReadOnlyList<CategoryView> Views { get; }

    public CategoryView View
    {
        get => _view;
        set
        {
            if (Views.Contains(value))
            {
                _view = value;
            }
        }
    }

    public IReadOnlyList<CategorySeries> Tables => [Series];

    public override string Heading => GraphHeadings.Of(Series.Concept);

    public override string? Description => Series.Description;

    public override int Count => _view switch
    {
        CategoryView.OverTime => Series.Time!.FirstUnits.Length,
        CategoryView.Frames => Series.Count,
        _ => Series.Categories.Count,
    };

    public override double Maximum => _view == CategoryView.OverTime ? 100 : 1;

    // The rows of the strip: one for each value of a fixed layout of up to MaxRows values, else one for all values.
    public int Rows => Series.ValuesPerUnit is > 1 and <= MaxRows ? Series.ValuesPerUnit : 1;

    // The values of an interval, a unit or a category: the weighted count of a category, the values of an interval.
    public override double ValueAt(int index)
    {
        switch (_view)
        {
            case CategoryView.Summary:
                return Series.Counts[index];
            case CategoryView.Frames:
                return Series.ValueCount(index);
            default:
                var counts = new long[Series.Categories.Count];
                ReadOnlySpan<int> first = Series.Time!.FirstUnits.Span;
                Series.CountRange(first[index], index + 1 < first.Length ? first[index + 1] : Series.Count, counts);
                return counts.Sum();
        }
    }

    public override string Describe(int index)
    {
        switch (_view)
        {
            case CategoryView.Summary:
                return DescribeCategory(Series, index);
            case CategoryView.Frames:
                return DescribeUnits(index, index + 1, -1);
            default:
                return DescribeIntervals(index, index + 1);
        }
    }

    // Hover text of the intervals [first, end) of the time index: "0:12 to 0:13 · Short 12.5% (24 values) · Long 87.5% (168 values)".
    public string DescribeIntervals(int first, int end)
    {
        ReadOnlySpan<int> units = Series.Time!.FirstUnits.Span;
        ReadOnlySpan<double> starts = Series.Time.Starts.Span;
        var counts = new long[Series.Categories.Count];
        Series.CountRange(units[first], end < units.Length ? units[end] : Series.Count, counts);
        string from = Format.Clock(starts[first]);
        string range = end < starts.Length ? $"{from} to {Format.Clock(starts[end])}" : $"{from} to the end";
        return $"{range} · {Shares(Series, counts)}";
    }

    // Hover text of a strip cell of units [first, end) in a row (lane, or -1 for all values): one unit says which
    // frame it is and the category of its value; a column of several says that rare categories are drawn enlarged and
    // gives the true counts.
    public string DescribeUnits(int first, int end, int lane)
    {
        if (end - first <= 1)
        {
            string frame = FrameText(first);
            if (lane >= 0 && Series.ValueCount(first) > lane)
            {
                string name = lane < Series.ValueNames.Count ? Series.ValueNames[lane] : $"{Series.ValueLabel} {lane + 1}";
                return $"{frame} · {name}: {Series.Categories[Series[first, lane]].Name}";
            }

            var own = new long[Series.Categories.Count];
            Series.CountRange(first, first + 1, lane, own);
            return own.Sum() == 0 ? $"{frame} · no values" : $"{frame} · {Shares(Series, own)}";
        }

        var counts = new long[Series.Categories.Count];
        Series.CountRange(first, end, lane, counts);
        string range = $"Frames {(first + 1).ToString("N0", CultureInfo.InvariantCulture)} to {end.ToString("N0", CultureInfo.InvariantCulture)}";
        return $"{range} · {Shares(Series, counts)} · drawn with its rare categories enlarged";
    }

    public override long? FrameAt(int index, long? selected)
    {
        if ((uint)index >= (uint)Count)
        {
            return null;
        }

        switch (_view)
        {
            case CategoryView.Summary:
                return NextUnit(Series, index, selected);
            case CategoryView.Frames:
                return (long)Series.X.Span[index];
            default:
                int unit = FirstUnitOfInterval(index);
                return unit < 0 ? null : (long)Series.X.Span[unit];
        }
    }

    public override int? IndexOfFrame(long offset)
    {
        int unit = FrameIndex(Series.X.Span, offset);
        if (unit < 0 || _view == CategoryView.Summary)
        {
            return null;
        }

        if (_view == CategoryView.Frames)
        {
            return unit;
        }

        // The last interval that starts at or before the unit.
        ReadOnlySpan<int> first = Series.Time!.FirstUnits.Span;
        int low = 0;
        int high = first.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (first[middle] <= unit)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found >= 0 ? found : null;
    }

    // The first unit of an interval of the time index, or -1 for an interval no unit starts in.
    public int FirstUnitOfInterval(int interval)
    {
        ReadOnlySpan<int> first = Series.Time!.FirstUnits.Span;
        int end = interval + 1 < first.Length ? first[interval + 1] : Series.Count;
        return first[interval] < end ? first[interval] : -1;
    }

    // The first interval from the given one up to the end that has a unit, or -1.
    public int FirstFilledInterval(int from, int end)
    {
        for (int interval = from; interval < end; interval++)
        {
            if (FirstUnitOfInterval(interval) >= 0)
            {
                return interval;
            }
        }

        return -1;
    }

    // The unit a strip column selects: its only unit, else the first unit that holds the column's rarest category, so
    // that choosing a tick leads to the frame it marks.
    public int UnitOfColumn(int first, int end, int lane)
    {
        if (end - first <= 1)
        {
            return first;
        }

        var counts = new long[Series.Categories.Count];
        Series.CountRange(first, end, lane, counts);
        int rarest = -1;
        for (int category = 0; category < counts.Length; category++)
        {
            if (counts[category] > 0 && (rarest < 0 || counts[category] < counts[rarest]))
            {
                rarest = category;
            }
        }

        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        CategorySeries.UnitReader reader = Series.Reader(first);
        for (int unit = first; unit < end; unit++)
        {
            int n = reader.Next(codes);
            for (int i = lane < 0 ? 0 : lane; i < (lane < 0 ? n : Math.Min(n, lane + 1)); i++)
            {
                if (codes[i] == rarest)
                {
                    return unit;
                }
            }
        }

        return first;
    }

    // The unit after the one at the offset (the first when none is selected) that holds the category in any of its
    // values, wrapping around the end, as a bar of the bitrate distribution steps through its frames; null when no unit does.
    public static long? NextUnit(CategorySeries series, int category, long? selected)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.Count == 0 || (uint)category >= (uint)series.Categories.Count || series.Counts[category] == 0)
        {
            return null;
        }

        ReadOnlySpan<double> x = series.X.Span;
        int start = 0;
        if (selected is { } at)
        {
            int low = 0;
            int high = x.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (x[middle] <= at)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            start = low;
        }

        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        for (int pass = 0; pass < 2; pass++)
        {
            int from = pass == 0 ? start : 0;
            int to = pass == 0 ? series.Count : Math.Min(start, series.Count);
            if (from >= to)
            {
                continue;
            }

            CategorySeries.UnitReader reader = series.Reader(from);
            for (int unit = from; unit < to; unit++)
            {
                int n = reader.Next(codes);
                for (int i = 0; i < n; i++)
                {
                    if (codes[i] == category)
                    {
                        return (long)x[unit];
                    }
                }
            }
        }

        return null;
    }

    // "Short · 24 values · 5.0%".
    public static string DescribeCategory(CategorySeries series, int category)
    {
        long count = series.Counts[category];
        return $"{series.Categories[category].Name} · {Format.Count(count, "value", "values")} · {SharePercent(count, series.Total)}";
    }

    // "Short 12.5% (24 values) · Long 87.5% (168 values)": the categories that occur, the rarest first, up to eight.
    public static string Shares(CategorySeries series, ReadOnlySpan<long> counts)
    {
        long total = 0;
        foreach (long count in counts)
        {
            total += count;
        }

        if (total == 0)
        {
            return "no values";
        }

        long[] own = counts.ToArray();
        var order = Enumerable.Range(0, own.Length).Where(c => own[c] > 0).OrderBy(c => own[c]).ThenBy(c => c).ToList();
        string[] parts = [.. order.Take(8).Select(c => $"{series.Categories[c].Name} {SharePercent(own[c], total)} ({Format.Count(own[c], "value", "values")})")];
        string text = string.Join(" · ", parts);
        return order.Count > 8 ? $"{text} · {order.Count - 8} more" : text;
    }

    public static string SharePercent(long count, long total) =>
        total <= 0 ? "0.0%" : (100.0 * count / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private string FrameText(int unit) =>
        $"Frame {(unit + 1).ToString("N0", CultureInfo.InvariantCulture)} · {Format.Hex((long)Series.X.Span[unit])}";
}

// The tables of every category series of a stream in one chart, as EncSpot's panel shows them. A point is a row of a
// table: a category of one series.
public sealed class CodingSummaryGraph : Graph, ISummaryGraph
{
    public const string TitleText = "Coding Summary";

    private readonly (int Series, int Category)[] _rows;

    public CodingSummaryGraph(IReadOnlyList<CategorySeries> tables)
        : base(TitleText, string.Empty)
    {
        ArgumentNullException.ThrowIfNull(tables);

        Tables = tables;
        _rows = [.. tables.SelectMany((t, s) => Enumerable.Range(0, t.Categories.Count).Select(c => (s, c)))];
    }

    public IReadOnlyList<CategorySeries> Tables { get; }

    public override string Heading => GraphHeadings.Summary;

    public override string? Description => "The categories each coding choice of the stream takes, with their counts and shares over the whole stream.";

    public override int Count => _rows.Length;

    public override double Maximum => 1;

    public (int Series, int Category) RowAt(int index) => _rows[index];

    public override double ValueAt(int index) => Tables[_rows[index].Series].Counts[_rows[index].Category];

    public override string Describe(int index)
    {
        (int series, int category) = _rows[index];
        return $"{Tables[series].Name} · {CategoryGraph.DescribeCategory(Tables[series], category)}";
    }

    public override long? FrameAt(int index, long? selected)
    {
        if ((uint)index >= (uint)_rows.Length)
        {
            return null;
        }

        (int series, int category) = _rows[index];
        return CategoryGraph.NextUnit(Tables[series], category, selected);
    }

    public override int? IndexOfFrame(long offset) => null;
}
