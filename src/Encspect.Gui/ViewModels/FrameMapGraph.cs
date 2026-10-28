using System.Globalization;
using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The severities whose findings the map rings and marks: the Findings tab's filters.
[Flags]
public enum SeverityMask
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 4,
    All = 7,
}

// A finding that lies in a frame: the frame's offset, found through the finding's path, and what the hover says of it.
public readonly record struct MapFinding(long Offset, Severity Severity, string Message, int Count);

// How a square's number falls on the shades: against the value it has (clamped to the end class, or moved to the nearest
// declared step) or not.
public enum ValueClamp
{
    None,
    Below,
    Above,
    NotAStep,
}

// One swatch of a numeric map's key: the value or range it stands for, its class, and how many squares have it.
public sealed record MapKeyEntry(string Label, int Class, long Count);

// What a Frame Map draws as one square: a unit of the series, a frame, packet or block, in rows like text. A numeric series
// is shaded by its value (one class per value up to 16 distinct values, else 8 classes of equal width, or the series' declared
// range), a category series by its categories, a unit of several values as a split square or a share mix. Squares are the
// stream's frames where the series' units are among them, so that they stay in place from one entry to the next, else the
// series' own units. Made from the series alone and the findings of the report; keeps no array per unit.
public sealed class FrameMapGraph : Graph
{
    // Up to this many distinct values get a color each; more are cut into Classes classes of equal width.
    public const int MaxValueClasses = 16;
    public const int Classes = 8;

    // Up to this many single-hue steps; more values use the multi-hue ramp.
    public const int MaxSingleHue = 8;

    // A fixed layout of up to this many values is drawn as split squares; more, and every variable layout, as a share mix.
    public const int MaxSplit = 4;

    // The values of a unit the hover lists.
    public const int MaxHoverValues = 32;

    private readonly Series? _numbers;
    private readonly CategorySeries? _categories;
    private readonly ReadOnlyMemory<double> _frameOffsets;
    private readonly IReadOnlyList<MapFinding> _findings;
    private ReadOnlyMemory<double> _squares;
    private bool _identity;
    private bool _hasOwnSquares;
    private bool _isResolved;
    private Scale? _scale;
    private ResolvedFinding[]? _resolved;

    public FrameMapGraph(Series series, ReadOnlyMemory<double> frameOffsets, IReadOnlyList<MapFinding>? findings = null)
        : base(series.Name, series.Unit)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Axis != SeriesAxis.FrameOffset || series.Values.Length == 0)
        {
            throw new ArgumentException("A map needs a series over frames with at least one unit.", nameof(series));
        }

        _numbers = series;
        _frameOffsets = frameOffsets;
        _findings = findings ?? [];
    }

    public FrameMapGraph(CategorySeries series, ReadOnlyMemory<double> frameOffsets, IReadOnlyList<MapFinding>? findings = null)
        : base(series.Name, string.Empty)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count == 0)
        {
            throw new ArgumentException("A map needs a series with at least one unit.", nameof(series));
        }

        _categories = series;
        _frameOffsets = frameOffsets;
        _findings = findings ?? [];
    }

    public bool IsCategory => _categories is not null;

    public Series? Numbers => _numbers;

    public CategorySeries? CategorySeries => _categories;

    public override string Heading => _numbers is { } n ? GraphHeadings.Of(n) : GraphHeadings.Of(_categories!.Concept);

    public override string? Description => _numbers?.Description ?? _categories!.Description;

    public override int Count
    {
        get
        {
            Resolve();
            return _squares.Length;
        }
    }

    public override double Maximum => 1;

    // True where the series' units are not among the stream's frames and so have squares of their own.
    public bool HasOwnSquares
    {
        get
        {
            Resolve();
            return _hasOwnSquares;
        }
    }

    // The line above the squares.
    public string Caption => HasOwnSquares ? "One square per point of this series" : "One square per frame";

    // The byte offset of a square.
    public long OffsetOf(int square)
    {
        Resolve();
        return (long)_squares.Span[square];
    }

    // The unit of the series that a square shows, or -1 where the series has none there.
    public int UnitOf(int square)
    {
        Resolve();
        int units = _numbers?.Values.Length ?? _categories!.Count;
        if (_identity)
        {
            return square < units ? square : -1;
        }

        return FrameIndex(UnitOffsets, (long)_squares.Span[square]);
    }

    // The square a unit is drawn on.
    public int SquareOf(int unit)
    {
        Resolve();
        return _identity ? unit : FrameIndex(_squares.Span, (long)UnitOffsets[unit]);
    }

    private ReadOnlySpan<double> UnitOffsets => _numbers is { } n ? n.X.Span : _categories!.X.Span;

    // Decides what the squares are: the stream's frames where every unit's offset is one of them, else the units' own.
    private void Resolve()
    {
        if (_isResolved)
        {
            return;
        }

        ReadOnlyMemory<double> x = _numbers is { } n ? n.X : _categories!.X;
        if (_frameOffsets.IsEmpty || x.Span.SequenceEqual(_frameOffsets.Span))
        {
            (_squares, _identity, _hasOwnSquares) = (x, true, false);
        }
        else
        {
            bool isSubset = true;
            ReadOnlySpan<double> frames = _frameOffsets.Span;
            foreach (double offset in x.Span)
            {
                if (FrameIndex(frames, (long)offset) < 0)
                {
                    isSubset = false;
                    break;
                }
            }

            (_squares, _identity, _hasOwnSquares) = isSubset ? (_frameOffsets, false, false) : (x, true, true);
        }

        _isResolved = true;
    }

    // ---- Numbers

    // The shades of a numeric map: how many classes there are, and whether they use the multi-hue ramp.
    public int ClassCount => EnsureScale().Classes;

    public bool IsMultiHue => EnsureScale() is { IsRange: false, Classes: > MaxSingleHue };

    // True when the series has one value only and no declared range: the key then reads "Every frame 128 kbps".
    public bool IsSingleValue => EnsureScale() is { IsDeclared: false, IsRange: false, Classes: 1 };

    public bool IsDeclared => EnsureScale().IsDeclared;

    // True when the classes are 8 of equal width between two ends (the file's range or a declared minimum and maximum).
    public bool UsesRange => EnsureScale().IsRange;

    // Squares the series has no finite value for, and squares whose value the declared range moved to an end class or to the
    // nearest step.
    public long NoValueCount => EnsureScale().NoValue;

    public long ClampedCount => EnsureScale().Clamped;

    // The smallest and largest value of the key with their unit; the declared ends where the series declares them.
    public string KeyLow => EnsureScale() is { Classes: > 0 } s ? FormatValue(s.IsRange ? s.Min : s.Points[0], Unit) : string.Empty;

    public string KeyHigh => EnsureScale() is { Classes: > 0 } s ? FormatValue(s.IsRange ? s.Max : s.Points[^1], Unit) : string.Empty;

    // One swatch for each class, lowest first, with the squares that have it. A declared step the file does not use has a count of 0.
    public IReadOnlyList<MapKeyEntry> Key
    {
        get
        {
            Scale scale = EnsureScale();
            return [.. Enumerable.Range(0, scale.Classes).Select(c => new MapKeyEntry(ClassLabel(scale, c), c, scale.Counts[c]))];
        }
    }

    // The value of a unit's square in the series' unit, or NaN where the square has none.
    public double NumberAt(int square)
    {
        if (_numbers is null)
        {
            return double.NaN;
        }

        int unit = UnitOf(square);
        return unit < 0 ? double.NaN : _numbers.Values.Span[unit];
    }

    public override double ValueAt(int index)
    {
        if (_numbers is not null)
        {
            return NumberAt(index);
        }

        int unit = UnitOf(index);
        return unit < 0 ? 0 : _categories!.ValueCount(unit);
    }

    // The class of a numeric square, or -1 for a square without a finite value.
    public int ClassAt(int square) => ClassAt(square, out _);

    public int ClassAt(int square, out ValueClamp clamp)
    {
        clamp = ValueClamp.None;
        double value = NumberAt(square);
        return double.IsFinite(value) ? ClassOf(EnsureScale(), value, out clamp) : -1;
    }

    private static string ClassLabel(Scale scale, int index)
    {
        if (!scale.IsRange)
        {
            return string.Empty;
        }

        double width = (scale.Max - scale.Min) / Classes;
        return $"{FormatValue(scale.Min + (index * width), string.Empty)} to {FormatValue(index == Classes - 1 ? scale.Max : scale.Min + ((index + 1) * width), string.Empty)}";
    }

    private string KeyEntryText(int index)
    {
        Scale scale = EnsureScale();
        string label = scale.IsRange ? ClassLabel(scale, index) : FormatValue(scale.Points[index], string.Empty);
        return Unit.Length > 0 ? $"{label} {Unit}" : label;
    }

    private static int ClassOf(Scale scale, double value, out ValueClamp clamp)
    {
        clamp = ValueClamp.None;
        if (scale.IsRange)
        {
            if (scale.IsDeclared && value < scale.Min)
            {
                clamp = ValueClamp.Below;
                return 0;
            }

            if (scale.IsDeclared && value > scale.Max)
            {
                clamp = ValueClamp.Above;
                return Classes - 1;
            }

            double fraction = (value - scale.Min) / (scale.Max - scale.Min);
            return Math.Clamp((int)Math.Floor(fraction * Classes), 0, Classes - 1);
        }

        double[] points = scale.Points;
        if (scale.IsDeclared && value < points[0])
        {
            clamp = ValueClamp.Below;
            return 0;
        }

        if (scale.IsDeclared && value > points[^1])
        {
            clamp = ValueClamp.Above;
            return points.Length - 1;
        }

        int found = Array.BinarySearch(points, value);
        if (found >= 0)
        {
            return found;
        }

        // Between two points: the nearer, the lower on a tie.
        int upper = Math.Min(~found, points.Length - 1);
        int lower = Math.Max(0, upper - 1);
        int nearest = value - points[lower] <= points[upper] - value ? lower : upper;
        if (scale.IsDeclared)
        {
            clamp = ValueClamp.NotAStep;
        }

        return nearest;
    }

    private Scale EnsureScale()
    {
        if (_scale is { } built)
        {
            return built;
        }

        Series series = _numbers ?? throw new InvalidOperationException("A category map has no scale.");
        ReadOnlySpan<double> values = series.Values.Span;
        var scale = new Scale { IsDeclared = series.DeclaredRange is not null };
        if (series.DeclaredRange is { HasSteps: true, Steps.Length: <= MaxValueClasses } declared)
        {
            scale.Points = declared.Steps.ToArray();
        }
        else if (series.DeclaredRange is { } range)
        {
            (scale.IsRange, scale.Min, scale.Max) = (true, range.Minimum, range.Maximum);
        }
        else
        {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            var distinct = new List<double>(MaxValueClasses + 1);
            bool tooMany = false;
            foreach (double value in values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                min = Math.Min(min, value);
                max = Math.Max(max, value);
                if (!tooMany)
                {
                    int at = distinct.BinarySearch(value);
                    if (at < 0)
                    {
                        distinct.Insert(~at, value);
                        tooMany = distinct.Count > MaxValueClasses;
                    }
                }
            }

            if (min > max)
            {
                scale.Points = [];
            }
            else if (!tooMany)
            {
                scale.Points = [.. distinct];
            }
            else
            {
                (scale.IsRange, scale.Min, scale.Max) = (true, min, max);
            }
        }

        scale.Classes = scale.IsRange ? Classes : scale.Points.Length;
        scale.Counts = new long[scale.Classes];
        long valued = 0;
        if (scale.Classes > 0)
        {
            foreach (double value in values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                scale.Counts[ClassOf(scale, value, out ValueClamp clamp)]++;
                valued++;
                if (clamp != ValueClamp.None)
                {
                    scale.Clamped++;
                }
            }
        }

        scale.NoValue = Count - valued;
        _scale = scale;
        return scale;
    }

    private sealed class Scale
    {
        public double[] Points = [];
        public bool IsRange;
        public bool IsDeclared;
        public double Min;
        public double Max;
        public int Classes;
        public long[] Counts = [];
        public long NoValue;
        public long Clamped;
    }

    // The text of the declared range for the hover: "32 to 320 kbps".
    private string DeclaredText()
    {
        ValueRange range = _numbers!.DeclaredRange!;
        string unit = Unit.Length > 0 ? " " + Unit : string.Empty;
        return $"{FormatValue(range.Minimum, string.Empty)} to {FormatValue(range.Maximum, string.Empty)}{unit}";
    }

    // ---- Categories

    // How a category unit is drawn: one fill, a cell for each of 2 to 4 values, or the share of each category.
    public CellLayout Layout => _categories is not { } c
        ? CellLayout.Single
        : c.ValuesPerUnit == 1 ? CellLayout.Single : c.ValuesPerUnit is >= 2 and <= MaxSplit ? CellLayout.Split : CellLayout.Mix;

    public enum CellLayout
    {
        Single,
        Split,
        Mix,
    }

    // Reads the codes of squares one after another, in time proportional to their values.
    public struct CellReader
    {
        private readonly FrameMapGraph _graph;
        private CategorySeries.UnitReader _reader;
        private int _next;

        internal CellReader(FrameMapGraph graph)
        {
            _graph = graph;
            _reader = default;
            _next = -1;
        }

        // The number of coded values of the square (into codes, which must be MaxValuesPerUnit long), or -1 where it has no unit.
        public int Read(int square, Span<byte> codes)
        {
            int unit = _graph.UnitOf(square);
            if (unit < 0)
            {
                return -1;
            }

            if (unit != _next)
            {
                _reader = _graph._categories!.Reader(unit);
            }

            _next = unit + 1;
            return _reader.Next(codes);
        }
    }

    public CellReader Reader() => new(this);

    // ---- Findings

    private readonly record struct ResolvedFinding(int Square, Severity Severity, string Message, int Count);

    private ResolvedFinding[] Resolved
    {
        get
        {
            if (_resolved is null)
            {
                Resolve();
                var list = new List<ResolvedFinding>();
                foreach (MapFinding finding in _findings)
                {
                    int square = FrameIndex(_squares.Span, finding.Offset);
                    if (square >= 0)
                    {
                        list.Add(new ResolvedFinding(square, finding.Severity, finding.Message, finding.Count));
                    }
                }

                _resolved = [.. list.OrderBy(f => f.Square).ThenByDescending(f => f.Severity)];
            }

            return _resolved;
        }
    }

    // The squares that hold a finding of a shown severity, in order.
    public int[] FindingSquares(SeverityMask shown = SeverityMask.All) =>
        [.. Resolved.Where(f => IsShown(f.Severity, shown)).Select(f => f.Square).Distinct()];

    // The most severe finding of a square among the shown, or null.
    public Severity? SeverityAt(int square, SeverityMask shown = SeverityMask.All)
    {
        Severity? worst = null;
        foreach (ResolvedFinding finding in Resolved)
        {
            if (finding.Square == square && IsShown(finding.Severity, shown) && (worst is null || finding.Severity > worst))
            {
                worst = finding.Severity;
            }
        }

        return worst;
    }

    public static bool IsShown(Severity severity, SeverityMask shown) => (shown & MaskOf(severity)) != 0;

    public static SeverityMask MaskOf(Severity severity) => severity switch
    {
        Severity.Error => SeverityMask.Error,
        Severity.Warning => SeverityMask.Warning,
        _ => SeverityMask.Info,
    };

    // ---- Text and frames

    public override string Describe(int index) => Describe(index, SeverityMask.All);

    // "Frame 1,234 · 0x1A2B · 192 kbps", the values of a unit of several, then the findings of the frame.
    public string Describe(int index, SeverityMask shown)
    {
        if ((uint)index >= (uint)Count)
        {
            return string.Empty;
        }

        string head = $"Frame {(index + 1).ToString("N0", CultureInfo.InvariantCulture)} · {Format.Hex(OffsetOf(index))}";
        string body = _numbers is not null ? DescribeNumber(index) : DescribeUnit(index);
        var parts = new List<string> { head, body };
        foreach (ResolvedFinding finding in Resolved.Where(f => f.Square == index && IsShown(f.Severity, shown)))
        {
            string cases = finding.Count > 1 ? $" (first of {finding.Count.ToString("N0", CultureInfo.InvariantCulture)} cases)" : string.Empty;
            parts.Add($"{finding.Severity}: {finding.Message}{cases}");
        }

        return string.Join(" · ", parts);
    }

    private string DescribeNumber(int square)
    {
        double value = NumberAt(square);
        if (!double.IsFinite(value))
        {
            return "no value";
        }

        string text = FormatValue(value, Unit);
        ClassAt(square, out ValueClamp clamp);
        return clamp switch
        {
            ValueClamp.Below => $"{text} (below the declared range of {DeclaredText()})",
            ValueClamp.Above => $"{text} (above the declared range of {DeclaredText()})",
            ValueClamp.NotAStep => $"{text} (not a declared step)",
            _ => text,
        };
    }

    private string DescribeUnit(int square)
    {
        CategorySeries series = _categories!;
        int unit = UnitOf(square);
        if (unit < 0)
        {
            return "no value";
        }

        byte[] codes = new byte[CategorySeries.MaxValuesPerUnit];
        int n = series.Reader(unit).Next(codes);
        if (n <= 0)
        {
            return series.UncodedValues > 0 ? "values past the limits are counted without a category" : "no values";
        }

        string CategoryName(byte code) => code == CategorySeries.None ? "no category" : series.Categories[code].Name;
        if (n == 1 && series.ValuesPerUnit <= 1)
        {
            return CategoryName(codes[0]);
        }

        int shown = Math.Min(n, MaxHoverValues);
        string[] parts = [.. Enumerable.Range(0, shown).Select(i => $"{(i < series.ValueNames.Count ? series.ValueNames[i] : $"{series.ValueLabel} {i + 1}")}: {CategoryName(codes[i])}")];
        string text = string.Join(" · ", parts);
        return n > shown ? $"{text} · {n - shown} more" : text;
    }

    // The frame a click on a square selects: the square's own offset.
    public override long? FrameAt(int index, long? selected) => (uint)index < (uint)Count ? OffsetOf(index) : null;

    public override int? IndexOfFrame(long offset)
    {
        Resolve();
        int found = FrameIndex(_squares.Span, offset);
        return found >= 0 ? found : null;
    }

    // The frame after the selected one (the first when none is selected) whose square has the key's class, or holds the category
    // in any value; wraps around the end; null when none does. A swatch or legend entry steps through its frames as a bar of the
    // distribution does.
    public long? NextFrameOf(int entry, long? selected)
    {
        if (_categories is { } series)
        {
            return CategoryGraph.NextUnit(series, entry, selected);
        }

        int count = Count;
        int start = 0;
        if (selected is { } at)
        {
            int low = 0;
            int high = count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (OffsetOf(middle) <= at)
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

        for (int step = 0; step < count; step++)
        {
            int square = (start + step) % count;
            if (ClassAt(square) == entry)
            {
                return OffsetOf(square);
            }
        }

        return null;
    }

    // What a swatch of the key says on hover: "128 kbps · 1,263 frames".
    public string DescribeKeyEntry(int entry) =>
        $"{KeyEntryText(entry)} · {Format.Count(EnsureScale().Counts[entry], "frame", "frames")}";
}
