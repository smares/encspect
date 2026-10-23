using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The forms a graph entry is offered in, which the view switcher lists and the window remembers by kind: for a category
// series the share over time (a stepped stacked area, or the same smoothed), the strip of one cell per frame and the table of
// counts and shares; for a numeric quantity
// its value per frame, per second of audio and its distribution.
public enum GraphForm
{
    // One point or cell per frame.
    Frames,

    // One point or stepped-area level per interval of audio time.
    OverTime,

    // Over the whole stream: a table of counts and shares, or the distribution of the values.
    Summary,

    // The first and second part of a quantity that divides in two, per interval of audio time.
    Split,

    // A category series' share over time as a stepped stacked area (OverTime) with each interval averaged with its neighbors.
    Smoothed,
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

    // The counts of each interval of the series' time index, the categories of an interval in a row: one pass over the codes,
    // which a graph keeps, so that a new width merges these and does not scan the values again.
    public static long[] IntervalCounts(CategorySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        ReadOnlySpan<int> firstUnits = series.Time!.FirstUnits.Span;
        int intervals = firstUnits.Length;
        int categories = series.Categories.Count;
        var counts = new long[intervals * categories];
        for (int interval = 0; interval < intervals; interval++)
        {
            int endUnit = interval + 1 < intervals ? firstUnits[interval + 1] : series.Count;
            if (endUnit > firstUnits[interval])
            {
                series.CountRange(firstUnits[interval], endUnit, counts.AsSpan(interval * categories, categories));
            }
        }

        return counts;
    }

    // The intervals of the series' time index, merged into at most the given number of columns; perInterval is the counts
    // of each interval (IntervalCounts, or the smoothed ones), made here when not given.
    public static CategoryColumns OverTime(CategorySeries series, int columns, long[]? perInterval = null)
    {
        perInterval ??= IntervalCounts(series);
        int intervals = series.Time!.FirstUnits.Length;
        int used = Math.Max(1, Math.Min(intervals, columns));
        int categories = series.Categories.Count;
        var first = new int[used + 1];
        var counts = new long[used * categories];
        for (int column = 0; column < used; column++)
        {
            int from = (int)((long)column * intervals / used);
            int to = (int)((long)(column + 1) * intervals / used);
            first[column] = from;
            for (int interval = from; interval < to; interval++)
            {
                for (int category = 0; category < categories; category++)
                {
                    counts[(column * categories) + category] += perInterval[(interval * categories) + category];
                }
            }
        }

        first[used] = intervals;
        return new CategoryColumns(first, counts, new bool[used * categories], categories);
    }

    // The counts of each interval summed with those of the intervals within half the window on either side, the window
    // shrinking at the ends: a centered moving average that weighs every value alike. An interval with no values of its own
    // stays empty, so that a gap is not filled from its neighbors. A running sum, so that it holds one row besides the result.
    public static long[] SmoothedCounts(CategorySeries series, int window) => SmoothedCounts(IntervalCounts(series), series.Categories.Count, window);

    public static long[] SmoothedCounts(long[] perInterval, int categories, int window)
    {
        int intervals = perInterval.Length / categories;
        int half = window / 2;
        var result = new long[perInterval.Length];
        var sum = new long[categories];
        int from = 0;
        int to = 0;
        for (int interval = 0; interval < intervals; interval++)
        {
            for (; to < Math.Min(intervals, interval + half + 1); to++)
            {
                for (int category = 0; category < categories; category++)
                {
                    sum[category] += perInterval[(to * categories) + category];
                }
            }

            for (; from < interval - half; from++)
            {
                for (int category = 0; category < categories; category++)
                {
                    sum[category] -= perInterval[(from * categories) + category];
                }
            }

            if (!IsEmpty(perInterval, categories, interval))
            {
                Array.Copy(sum, 0, result, interval * categories, categories);
            }
        }

        return result;
    }

    // Whether an interval of per-interval counts holds no values.
    public static bool IsEmpty(long[] perInterval, int categories, int interval)
    {
        for (int category = 0; category < categories; category++)
        {
            if (perInterval[(interval * categories) + category] != 0)
            {
                return false;
            }
        }

        return true;
    }

    // The categories of a series from the bottom of a stack up: the one with the largest share first (the first of equals),
    // because only the bottom layer has a flat baseline to read.
    public static int[] StackOrder(CategorySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return [.. Enumerable.Range(0, series.Categories.Count).OrderByDescending(c => series.Counts[c]).ThenBy(c => c)];
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

// One category series as a chart in one of four forms: the share of each category over time as a stepped stacked area, the
// same smoothed, one cell per unit, and the summary table. Made from the series alone. Over time, a point is an interval of the time index; in the strip, a unit;
// in the summary, a category.
public sealed class CategoryGraph : Graph, ISummaryGraph
{
    // A strip of a fixed layout draws a row for each of this many values at most; more values share one row.
    public const int MaxRows = 8;

    private long[]? _intervalCounts;
    private long[]? _smoothed;
    private int[]? _stackOrder;

    public CategoryGraph(CategorySeries series, GraphForm view)
        : base(series.Name, string.Empty)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (view is GraphForm.Split || (IsOverTimeForm(view) && series.Time is not { FirstUnits.Length: > 0 }) || (view == GraphForm.Frames && series.Count == 0))
        {
            throw new ArgumentException($"A series of {series.Count} units cannot be drawn as {view}.", nameof(view));
        }

        Series = series;
        View = view;
    }

    public CategorySeries Series { get; }

    // The counts of each interval (CategoryColumns.IntervalCounts), made once: a new width of the chart merges them, and the
    // hover text of the smoothed form asks for them at every pointer move.
    public long[] IntervalCounts => _intervalCounts ??= CategoryColumns.IntervalCounts(Series);

    // The counts of every interval averaged over SmoothingWindow intervals (CategoryColumns.SmoothedCounts), made once.
    public long[] SmoothedCounts => _smoothed ??= CategoryColumns.SmoothedCounts(IntervalCounts, Series.Categories.Count, SmoothingWindow);

    // The categories of the series from the bottom layer of the stack up, in the over-time forms and their legend and hover text.
    public int[] StackOrder => _stackOrder ??= CategoryColumns.StackOrder(Series);

    // The last interval of the time index where it is a partial one (the stream ends inside it, shorter than the interval
    // before) of one frame or more but fewer than FewFrames, which the chart draws hatched and the hover text names: how many
    // frames it holds and how many seconds it lasts. IsFew is false for any other last interval, and the property null where the
    // series has one interval or the duration is not known.
    public (bool IsFew, int Frames, double Seconds)? LastInterval
    {
        get
        {
            if (Series.Time is not { FirstUnits.Length: > 1 } time || Duration <= time.Starts.Span[^1])
            {
                return null;
            }

            int frames = Series.Count - time.FirstUnits.Span[^1];
            double seconds = Duration - time.Starts.Span[^1];
            bool isPartial = seconds < time.Starts.Span[^1] - time.Starts.Span[^2];
            return (isPartial && frames is > 0 and < FewFrames, frames, seconds);
        }
    }

    // The stream's duration in seconds, which gives the last interval its length (0 where unknown): a stream that ends a few
    // frames into a second has a last interval that is drawn as long as it lasts.
    public double Duration { get; init; }

    // The form this graph draws.
    public GraphForm View { get; }

    // The number of intervals of a smoothed form's centered moving average, and the number of intervals above which
    // a series is offered in it: a short file is not smoothed, it has few steps to begin with.
    public const int SmoothingWindow = 5;

    public const int SmoothingThreshold = 30;

    public static bool IsOverTimeForm(GraphForm form) => form is GraphForm.OverTime or GraphForm.Smoothed;

    // A partial last interval (LastInterval) is drawn hatched and named in its hover text when it holds fewer frames than this:
    // a stream rarely ends on a second boundary, and a few frames say little about a share. The figure is a count of frames,
    // since the frames of a second differ by format; for MPEG audio it is about a fifth of a second.
    public const int FewFrames = 8;

    // Whether this graph is a chart of the share over time, in whichever of its forms.
    public bool IsOverTime => IsOverTimeForm(View);

    // The forms a series can be drawn in, in the order they are offered.
    public static IReadOnlyList<GraphForm> FormsOf(CategorySeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var forms = new List<GraphForm>();
        if (series.Time is { FirstUnits.Length: > 0 })
        {
            forms.Add(GraphForm.OverTime);
            if (series.Time.FirstUnits.Length > SmoothingThreshold)
            {
                forms.Add(GraphForm.Smoothed);
            }
        }

        if (series.Count > 0)
        {
            forms.Add(GraphForm.Frames);
        }

        forms.Add(GraphForm.Summary);
        return forms;
    }

    public IReadOnlyList<CategorySeries> Tables => [Series];

    public override string Heading => GraphHeadings.Of(Series.Concept);

    public override string? Description => Series.Description;

    public override int Count => View switch
    {
        GraphForm.Frames => Series.Count,
        GraphForm.Summary => Series.Categories.Count,
        _ => Series.Time!.FirstUnits.Length,
    };

    public override double Maximum => IsOverTime ? 100 : 1;

    // The rows of the strip: one for each value of a fixed layout of up to MaxRows values, else one for all values.
    public int Rows => Series.ValuesPerUnit is > 1 and <= MaxRows ? Series.ValuesPerUnit : 1;

    // The values of an interval, a unit or a category: the weighted count of a category, the values of an interval.
    public override double ValueAt(int index)
    {
        switch (View)
        {
            case GraphForm.Summary:
                return Series.Counts[index];
            case GraphForm.Frames:
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
        switch (View)
        {
            case GraphForm.Summary:
                return DescribeCategory(Series, index);
            case GraphForm.Frames:
                return DescribeUnits(index, index + 1, -1);
            case GraphForm.Smoothed:
                return DescribeSmoothed(index, index + 1);
            default:
                return DescribeIntervals(index, index + 1);
        }
    }

    // Hover text of the intervals [first, end) of the time index: "0:12 to 0:13 · Long 87.5% (168 values) · Short 12.5% (24 values)",
    // the categories in the order of the stack, largest first, as the legend lists them. A last interval of few frames leads with
    // its name, "Last 0.03 s, 1 frame · 0:09 to the end · …".
    public string DescribeIntervals(int first, int end) => DescribeIntervals(first, end, true);

    private string DescribeIntervals(int first, int end, bool nameLast)
    {
        ReadOnlySpan<int> units = Series.Time!.FirstUnits.Span;
        ReadOnlySpan<double> starts = Series.Time.Starts.Span;
        var counts = new long[Series.Categories.Count];
        Series.CountRange(units[first], end < units.Length ? units[end] : Series.Count, counts);
        string from = Format.Clock(starts[first]);
        string range = end < starts.Length ? $"{from} to {Format.Clock(starts[end])}" : $"{from} to the end";
        string text = $"{range} · {Shares(Series, counts, StackOrder)}";
        return nameLast && end == units.Length && end - first == 1 && LastInterval is { IsFew: true } last ? $"{DescribeLast(last)} · {text}" : text;
    }

    // "Last 0.03 s, 1 frame".
    private static string DescribeLast((bool IsFew, int Frames, double Seconds) last) => $"Last {LastSeconds(last.Seconds)} s, {Format.Count(last.Frames, "frame", "frames")}";

    // The note under the caption of a chart that draws the last interval hatched: "Last 0.03 s: 1 frame, hatched".
    public static string DescribeHatchedLast((bool IsFew, int Frames, double Seconds) last) => $"Last {LastSeconds(last.Seconds)} s: {Format.Count(last.Frames, "frame", "frames")}, hatched";

    private static string LastSeconds(double seconds) => seconds < 0.01 ? "under 0.01" : seconds.ToString("0.00", CultureInfo.InvariantCulture);

    // Hover text of the smoothed form: the interval's own shares and the average the chart draws, in the same order:
    // "0:12 to 0:13 · Long 87.5% (168 values) · Short 12.5% (24 values) · average of 5 seconds: Long 86.0% · Short 14.0%". The
    // average says how many seconds it holds (three at the first and last intervals, fewer around a gap), and a column of
    // several intervals says "up to". A last interval is not named: its drawn level is an average, not a share of a few frames.
    public string DescribeSmoothed(int first, int end)
    {
        long[] smoothed = SmoothedCounts;
        int categories = Series.Categories.Count;
        var counts = new long[categories];
        for (int interval = first; interval < end; interval++)
        {
            for (int category = 0; category < categories; category++)
            {
                counts[category] += smoothed[(interval * categories) + category];
            }
        }

        if (counts.Sum() == 0)
        {
            return DescribeIntervals(first, end, false);
        }

        string average = Shares(Series, counts, StackOrder, withCounts: false);
        string seconds = Format.Count(end - first == 1 ? WindowSeconds(first) : SmoothingWindow, "second", "seconds");
        return $"{DescribeIntervals(first, end, false)} · average of {(end - first == 1 ? string.Empty : "up to ")}{seconds}: {average}";
    }

    // The intervals with values in the smoothing window of an interval.
    private int WindowSeconds(int interval)
    {
        int categories = Series.Categories.Count;
        int intervals = IntervalCounts.Length / categories;
        int seconds = 0;
        for (int i = Math.Max(0, interval - (SmoothingWindow / 2)); i < Math.Min(intervals, interval + (SmoothingWindow / 2) + 1); i++)
        {
            seconds += CategoryColumns.IsEmpty(IntervalCounts, categories, i) ? 0 : 1;
        }

        return seconds;
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
                byte code = Series[first, lane];
                return $"{frame} · {name}: {(code == CategorySeries.None ? "no category" : Series.Categories[code].Name)}";
            }

            var own = new long[Series.Categories.Count];
            Series.CountRange(first, first + 1, lane, own);
            return own.Sum() == 0 ? $"{frame} · no values" : $"{frame} · {Shares(Series, own)}";
        }

        var counts = new long[Series.Categories.Count];
        Series.CountRange(first, end, lane, counts);
        string range = $"Frames {(first + 1).ToString("N0", CultureInfo.InvariantCulture)} to {end.ToString("N0", CultureInfo.InvariantCulture)}";
        int marked = RareUnits(first, end, lane).Count;
        string stepping = marked > 1 ? $" · {marked} marked frames · click again for the next" : string.Empty;
        return $"{range} · {Shares(Series, counts)} · drawn with its rare categories enlarged{stepping}";
    }

    public override long? FrameAt(int index, long? selected)
    {
        if ((uint)index >= (uint)Count)
        {
            return null;
        }

        switch (View)
        {
            case GraphForm.Summary:
                return NextUnit(Series, index, selected);
            case GraphForm.Frames:
                return (long)Series.X.Span[index];
            default:
                int unit = FirstUnitOfInterval(index);
                return unit < 0 ? null : (long)Series.X.Span[unit];
        }
    }

    public override int? IndexOfFrame(long offset)
    {
        int unit = FrameIndex(Series.X.Span, offset);
        if (unit < 0 || View == GraphForm.Summary)
        {
            return null;
        }

        if (View == GraphForm.Frames)
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

    // The units of a strip column that hold a category no other unit of the column holds: the ones its ticks mark, in unit
    // order. A column may have several, one for each rare category, and they share the column's one pixel.
    public IReadOnlyList<int> RareUnits(int first, int end, int lane)
    {
        if (end - first <= 1)
        {
            return [];
        }

        Span<byte> codes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
        Span<int> units = stackalloc int[256];
        Span<int> stamp = stackalloc int[256];
        CategorySeries.UnitReader reader = Series.Reader(first);
        for (int unit = first; unit < end; unit++)
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

        var rare = new List<int>();
        reader = Series.Reader(first);
        for (int unit = first; unit < end; unit++)
        {
            int n = reader.Next(codes);
            for (int i = lane < 0 ? 0 : lane; i < (lane < 0 ? n : Math.Min(n, lane + 1)); i++)
            {
                if (codes[i] != CategorySeries.None && units[codes[i]] == 1)
                {
                    rare.Add(unit);
                    break;
                }
            }
        }

        return rare;
    }

    // The unit a strip column selects: its only unit, else a unit that holds a category of its ticks (RareUnits): the first,
    // or after a unit (the one selected now) the next, wrapping around, so that choosing the column again steps through the
    // units its ticks mark, which share its pixel. A column without a tick selects the first unit of its rarest category.
    public int UnitOfColumn(int first, int end, int lane, int? after = null)
    {
        if (end - first <= 1)
        {
            return first;
        }

        IReadOnlyList<int> rare = RareUnits(first, end, lane);
        if (rare.Count > 0)
        {
            foreach (int unit in rare)
            {
                if (after is not { } selected || unit > selected)
                {
                    return unit;
                }
            }

            return rare[0];
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

    // "Short 12.5% (24 values) · Long 87.5% (168 values)": the categories that occur, up to eight, the rarest first or, given
    // the stack's order, the bottom layer first; "· 3 more" past eight; without the counts of values where asked.
    public static string Shares(CategorySeries series, ReadOnlySpan<long> counts, int[]? stackOrder = null, bool withCounts = true)
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
        List<int> order = stackOrder is null
            ? [.. Enumerable.Range(0, own.Length).Where(c => own[c] > 0).OrderBy(c => own[c]).ThenBy(c => c)]
            : [.. stackOrder.Where(c => own[c] > 0)];
        string[] parts = [.. order.Take(8).Select(c => $"{series.Categories[c].Name} {SharePercent(own[c], total)}{(withCounts ? $" ({Format.Count(own[c], "value", "values")})" : string.Empty)}")];
        string text = string.Join(" · ", parts);
        return order.Count > 8 ? $"{text} · {order.Count - 8} more" : text;
    }

    // A share in percent to one decimal, rounded half to even as the tallies and the text tree round it, in integers.
    public static string SharePercent(long count, long total)
    {
        if (total <= 0)
        {
            return "0.0%";
        }

        Int128 scaled = (Int128)count * 1000;
        Int128 tenths = scaled / total;
        Int128 twice = (scaled % total) * 2;
        if (twice > total || (twice == total && !Int128.IsEvenInteger(tenths)))
        {
            tenths++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10}%");
    }

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

// A quantity that divides in two, such as the first coded channel's share of a frame's audio bits, in percent, per second of
// audio: each interval as a 100% column of two segments, the first part at the bottom, with the 50% line marked. Made from the
// per-second series; a click selects the interval's first frame, as for the other per-second graphs.
public sealed class ShareSplitGraph : Graph
{
    private readonly LineGraph _line;

    public ShareSplitGraph(Series series, ReadOnlyMemory<double> frameOffsets, double duration)
        : base(series.Name, series.Unit)
    {
        ArgumentNullException.ThrowIfNull(series);

        Series = series;
        Duration = duration;
        _line = new LineGraph(series, frameOffsets, duration);
    }

    public double Duration { get; }

    public Series Series { get; }

    public override string Heading => GraphHeadings.Of(Series);

    public override string? Description => Series.Description;

    public override int Count => _line.Count;

    public override double Maximum => 100;

    // The first part's share of an interval in percent, or NaN where it has none.
    public double FirstShare(int index)
    {
        double value = Series.Values.Span[index];
        return double.IsFinite(value) ? Math.Clamp(value, 0, 100) : double.NaN;
    }

    public override double ValueAt(int index) => FirstShare(index);

    public override string Describe(int index)
    {
        string time = Format.Clock(Series.X.Span[index]);
        double first = FirstShare(index);
        return double.IsNaN(first)
            ? $"{time} · no share"
            : $"{time} · first channel {Percent(first)} · second channel {Percent(100 - first)}";
    }

    // Percent to one decimal, rounded half to even.
    public static string Percent(double value) => Math.Round(value, 1, MidpointRounding.ToEven).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public override long? FrameAt(int index, long? selected) => _line.FrameAt(index, selected);

    public override int? IndexOfFrame(long offset) => _line.IndexOfFrame(offset);
}
