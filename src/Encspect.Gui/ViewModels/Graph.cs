using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// One chart of a stream, made from its series alone (StreamInfo.Series), never from the tree: a
// series over the stream's frames or seconds as a line, or the distribution of a per-frame series'
// values as bars. Each point can say what it shows and which frame it leads to.
public abstract class Graph
{
    private protected Graph(string title, string unit)
    {
        Title = title;
        Unit = unit;
    }

    public string Title { get; }

    public string Unit { get; }

    // What the chart shows, for its chip's tooltip; from the series, as its parser describes it.
    public abstract string? Description { get; }

    public abstract int Count { get; }

    public abstract double Maximum { get; }

    public abstract double ValueAt(int index);

    // What the point shows, for its tooltip: "Frame 1,234 · 0x1A2B · 192 kbps".
    public abstract string Describe(int index);

    // The offset of the frame that choosing the point selects, or null when it leads to none. A bar
    // stands for many frames: the first of them after the frame selected now, so choosing it again
    // steps through them.
    public abstract long? FrameAt(int index, long? selected);

    // The point that stands for the frame at an offset, to mark it on the chart, or null.
    public abstract int? IndexOfFrame(long offset);

    // Values shown with their unit: whole numbers without decimals, others with one.
    public static string FormatValue(double value, string unit)
    {
        string number = value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("N1", CultureInfo.InvariantCulture);
        return unit.Length > 0 ? number + " " + unit : number;
    }

    // The frame index of an offset among sorted frame offsets, or -1.
    private protected static int FrameIndex(ReadOnlySpan<double> offsets, long offset)
    {
        int low = 0;
        int high = offsets.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            double at = offsets[middle];
            if (at == offset)
            {
                return middle;
            }

            (low, high) = at < offset ? (middle + 1, high) : (low, middle - 1);
        }

        return -1;
    }
}

// A series as a line over its x axis: frames (SeriesAxis.FrameOffset), one point per frame, or
// seconds, one point per interval.
public sealed class LineGraph : Graph
{
    private readonly ReadOnlyMemory<double> _frameOffsets;
    private readonly double _duration;
    private readonly double _maximum;

    // frameOffsets are the stream's frames, from any of its per-frame series, which lets a point of
    // a per-second series lead to the first frame that starts in its second.
    public LineGraph(Series series, ReadOnlyMemory<double> frameOffsets, double duration)
        : base(series.Name, series.Unit)
    {
        ArgumentNullException.ThrowIfNull(series);

        Series = series;
        _frameOffsets = frameOffsets;
        _duration = duration;
        _maximum = Max(series.Values.Span);
    }

    public Series Series { get; }

    public SeriesAxis Axis => Series.Axis;

    public override string? Description => Series.Description;

    public override int Count => Series.Values.Length;

    public override double Maximum => _maximum;

    public override double ValueAt(int index) => Series.Values.Span[index];

    public override string Describe(int index)
    {
        double x = Series.X.Span[index];
        string value = FormatValue(ValueAt(index), Unit);
        return Axis == SeriesAxis.FrameOffset
            ? $"Frame {(index + 1).ToString("N0", CultureInfo.InvariantCulture)} · {Format.Hex((long)x)} · {value}"
            : $"{Format.Clock(x)} · {value}";
    }

    public override long? FrameAt(int index, long? selected)
    {
        if ((uint)index >= (uint)Count)
        {
            return null;
        }

        if (Axis == SeriesAxis.FrameOffset)
        {
            return (long)Series.X.Span[index];
        }

        // The frames of a stream last equally long (a stream ends where the sample rate or the
        // coding changes), so the first frame that starts in a second follows from the duration.
        int frames = _frameOffsets.Length;
        if (frames == 0 || _duration <= 0)
        {
            return null;
        }

        double first = Math.Ceiling((Series.X.Span[index] * frames / _duration) - 1e-9);
        return (long)_frameOffsets.Span[(int)Math.Clamp(first, 0, frames - 1)];
    }

    public override int? IndexOfFrame(long offset)
    {
        if (Axis == SeriesAxis.FrameOffset)
        {
            int found = FrameIndex(Series.X.Span, offset);
            return found >= 0 ? found : null;
        }

        int frame = FrameIndex(_frameOffsets.Span, offset);
        if (frame < 0 || _duration <= 0 || Count == 0)
        {
            return null;
        }

        double second = frame * _duration / _frameOffsets.Length;
        ReadOnlySpan<double> starts = Series.X.Span;
        int index = 0;
        while (index + 1 < starts.Length && starts[index + 1] <= second)
        {
            index++;
        }

        return index;
    }

    // The text under a position on the x axis, counted in points from the start: frames, or the
    // time of the second.
    public string AxisLabel(double position)
    {
        if (Axis == SeriesAxis.FrameOffset)
        {
            return position.ToString("N0", CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<double> x = Series.X.Span;
        double step = x.Length > 1 ? (x[^1] - x[0]) / (x.Length - 1) : 1;
        return Format.Clock(x[0] + (position * step));
    }

    private static double Max(ReadOnlySpan<double> values)
    {
        double max = 0;
        foreach (double value in values)
        {
            if (double.IsFinite(value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }
}

// How many frames have each value of a per-frame series, as bars in ascending order. Values that
// repeat, as bitrates do, get a bar each; a series of more distinct values than fit is cut into
// equal ranges.
public sealed class DistributionGraph : Graph
{
    public const int MaxExactBars = 48;
    public const int RangeBars = 32;

    private readonly Series _series;
    private readonly Bar[] _bars;
    private readonly int _total;

    public override string? Description { get; }

    public DistributionGraph(Series series)
        : base(series.Name + " Distribution", series.Unit)
    {
        ArgumentNullException.ThrowIfNull(series);

        _series = series;
        Description = series.Description is { } description ? "How many frames have each value. " + description : "How many frames have each value.";
        ReadOnlySpan<double> values = series.Values.Span;
        var counts = new SortedDictionary<double, int>();
        foreach (double value in values)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            _total++;
            counts[value] = counts.GetValueOrDefault(value) + 1;
            if (counts.Count > MaxExactBars)
            {
                break;
            }
        }

        _bars = counts.Count <= MaxExactBars ? [.. counts.Select(c => new Bar(c.Key, c.Key, c.Value))] : Ranges(values, out _total);
    }

    public IReadOnlyList<Bar> Bars => _bars;

    public override int Count => _bars.Length;

    public override double Maximum => _bars.Length == 0 ? 0 : _bars.Max(b => b.Count);

    public override double ValueAt(int index) => _bars[index].Count;

    // The bar's value without the unit, for the axis.
    public string Label(int index) => _bars[index].IsRange
        ? $"{FormatValue(_bars[index].Low, string.Empty)}–{FormatValue(_bars[index].High, string.Empty)}"
        : FormatValue(_bars[index].Low, string.Empty);

    public override string Describe(int index)
    {
        Bar bar = _bars[index];
        string value = bar.IsRange ? $"{Label(index)} {Unit}".TrimEnd() : FormatValue(bar.Low, Unit);
        double share = _total > 0 ? 100.0 * bar.Count / _total : 0;
        return $"{value} · {Format.Count(bar.Count, "frame", "frames")} · {share.ToString("0.0", CultureInfo.InvariantCulture)}%";
    }

    public override long? FrameAt(int index, long? selected)
    {
        if ((uint)index >= (uint)_bars.Length)
        {
            return null;
        }

        ReadOnlySpan<double> values = _series.Values.Span;
        ReadOnlySpan<double> offsets = _series.X.Span;
        long? first = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (BarOf(values[i]) != index)
            {
                continue;
            }

            long offset = (long)offsets[i];
            if (selected is null || offset > selected)
            {
                return offset;
            }

            first ??= offset;
        }

        return first;
    }

    public override int? IndexOfFrame(long offset)
    {
        int frame = FrameIndex(_series.X.Span, offset);
        return frame >= 0 && BarOf(_series.Values.Span[frame]) is var bar and >= 0 ? bar : null;
    }

    private int BarOf(double value)
    {
        if (!double.IsFinite(value))
        {
            return -1;
        }

        int low = 0;
        int high = _bars.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            Bar bar = _bars[middle];
            if (value < bar.Low)
            {
                high = middle - 1;
            }
            else if (value > bar.High || (bar.IsRange && value == bar.High && middle < _bars.Length - 1))
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    // Equal ranges from the smallest value to the largest, each including its low end and the last
    // also its high end.
    private static Bar[] Ranges(ReadOnlySpan<double> values, out int total)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        total = 0;
        foreach (double value in values)
        {
            if (double.IsFinite(value))
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                total++;
            }
        }

        double width = (max - min) / RangeBars;
        var counts = new int[RangeBars];
        foreach (double value in values)
        {
            if (double.IsFinite(value))
            {
                counts[Math.Min(RangeBars - 1, (int)((value - min) / width))]++;
            }
        }

        return [.. Enumerable.Range(0, RangeBars).Select(i => new Bar(min + (i * width), i == RangeBars - 1 ? max : min + ((i + 1) * width), counts[i]) { IsRange = true })];
    }

    public sealed record Bar(double Low, double High, int Count)
    {
        public bool IsRange { get; init; }
    }
}

// The points of a line that fall in one pixel column of a chart: indexes [First, End), and the
// smallest and largest value among them with where they are.
public readonly record struct GraphColumn(int First, int End, double Min, double Max, int MinIndex, int MaxIndex);

public static class GraphColumns
{
    // Cuts the values into at most the given number of columns of consecutive points, so a line of
    // any length draws in as many segments as it has pixels across: each column keeps its smallest
    // and largest value, so no peak or dip goes missing. Fewer values than columns get a column each.
    public static GraphColumn[] Decimate(ReadOnlySpan<double> values, int columns)
    {
        int count = values.Length;
        if (count == 0 || columns <= 0)
        {
            return [];
        }

        int used = Math.Min(count, columns);
        var result = new GraphColumn[used];
        for (int column = 0; column < used; column++)
        {
            int first = (int)((long)column * count / used);
            int end = (int)((long)(column + 1) * count / used);
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            int minIndex = first;
            int maxIndex = first;
            for (int i = first; i < end; i++)
            {
                double value = values[i];
                if (value < min)
                {
                    min = value;
                    minIndex = i;
                }

                if (value > max)
                {
                    max = value;
                    maxIndex = i;
                }
            }

            result[column] = new GraphColumn(first, end, min, max, minIndex, maxIndex);
        }

        return result;
    }
}
