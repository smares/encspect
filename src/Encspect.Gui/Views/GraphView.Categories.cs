using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The category charts: the share of each category over time, the strip of one cell per frame, the summary tables and the
// two-part share. Made from the series alone, in the palette of CategoryColors, always with names and shares in text.
public sealed partial class GraphView
{
    private const double TableHeader = 28;
    private const double RowHeightOfTable = 22;
    private const double TableWidth = 470;
    private const double LegendLine = 18;
    private const int MaxLegendLines = 3;
    private const double StripGap = 6;
    private const double CaptionHeight = 20;
    private const double AxisHeight = 24;
    private const double HatchedLastOpacity = 0.6;
    private const double MinSeparated = 3;
    internal const double MinLastWidth = 3;

    // The plot is never lower than this: a view with less room scrolls instead of squeezing the chart.
    private const double MinOverTimeHeight = 110;
    private const double MinStripRow = 8;

    private readonly Dictionary<(int Form, int Width, int Lane), CategoryColumns> _categoryColumns = [];
    private Graph? _categoryColumnsGraph;
    private double _scroll;
    private int _hoverLane = -1;
    private Rect _hoverArea;
    private (Graph Graph, Size Size, ChartLayout Layout)? _layoutCache;

    // The category chart's answer to a pointer position or to a key: the point it stands for (what the graph's FrameAt
    // takes), its hover text, the cell it covers, the strip row, and for a legend entry its category.
    private readonly record struct CategoryHit(int Index, string Text, Rect Area, int Lane = -1, bool IsLegend = false, int Category = -1);

    // Where a chart puts its parts, in content coordinates (scrolled by _scroll when the content is taller than the view).
    private readonly record struct ChartLayout(Rect Plot, double RowHeight, int Rows, IReadOnlyList<(Rect Rect, int Category)> Legend, int LegendEntries, double ContentHeight);

    // The plot's height, the rows of a strip, the height of all the content and whether it is taller than the view, so that tests
    // can hold the layout to its minimums; null for a graph that is no category chart.
    public (double PlotHeight, int Rows, double ContentHeight, bool Scrolls)? ChartMetrics()
    {
        if (Graph is not { } graph || !IsCategory(graph) || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        if (IsSummary(graph))
        {
            double height = ContentHeightOf(graph);
            return (Bounds.Height, 0, height, height > Bounds.Height);
        }

        ChartLayout layout = ChartLayoutFor(graph);
        return (layout.Plot.Height, layout.Rows, layout.ContentHeight, layout.ContentHeight > Bounds.Height);
    }

    // The plot of a category chart in the view's coordinates (not scrolled), for tests; null for anything else.
    public Rect? PlotArea() => Graph is { } graph && IsCategory(graph) && !IsSummary(graph) && Bounds.Width >= 80 && Bounds.Height >= 60 ? ChartLayoutFor(graph).Plot : null;

    // The rows of a strip as the pointer is answered in them (top and height in the view's own coordinates), for tests; null for
    // anything else.
    public IReadOnlyList<(double Top, double Height)>? StripRowBounds()
    {
        if (Graph is not CategoryGraph { View: GraphForm.Frames } strip || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        ChartLayout layout = ChartLayoutFor(strip);
        return [.. Enumerable.Range(0, layout.Rows).Select(r => (RowTop(layout, r) - _scroll, layout.RowHeight))];
    }

    private static bool IsCategory(Graph graph) => graph is CategoryGraph or CodingSummaryGraph or ShareSplitGraph;

    private static bool IsSummary(Graph graph) => graph is CodingSummaryGraph || graph is CategoryGraph { View: GraphForm.Summary };

    private static bool HasHighContrast()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Graph is not { Count: > 0 } graph)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            if (_hover is { } chosen)
            {
                PointClicked?.Invoke(this, chosen);
                e.Handled = true;
            }

            return;
        }

        // Up and Down change the row of a strip of several; in a table, and everywhere else, they step like Left and Right.
        if (e.Key is Key.Up or Key.Down && graph is CategoryGraph { View: GraphForm.Frames } strip && ChartLayoutFor(strip).Rows > 1)
        {
            int rows = ChartLayoutFor(strip).Rows;
            int lane = Math.Clamp((_hoverLane < 0 ? 0 : _hoverLane) + (e.Key == Key.Down ? 1 : -1), 0, rows - 1);
            _hoverLane = lane;
            SetCategoryHit(HitOfIndex(graph, Math.Clamp(_hover ?? MarkedIndex ?? 0, 0, graph.Count - 1)));
            e.Handled = true;
            return;
        }

        int count = graph.Count;
        int current = _hover ?? MarkedIndex ?? -1;
        int? next = e.Key switch
        {
            Key.Left or Key.Up => current < 0 ? count - 1 : current - Step(graph, e),
            Key.Right or Key.Down => current < 0 ? 0 : current + Step(graph, e),
            Key.Home => 0,
            Key.End => count - 1,
            _ => null,
        };
        if (next is { } index)
        {
            index = Math.Clamp(index, 0, count - 1);
            if (IsCategory(graph))
            {
                SetCategoryHit(HitOfIndex(graph, index));
            }
            else
            {
                SetHover(index);
            }

            e.Handled = true;
        }
    }

    // One point, or with Ctrl one pixel column's worth of units.
    private int Step(Graph graph, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || graph is not CategoryGraph { View: GraphForm.Frames } frames)
        {
            return 1;
        }

        return Math.Max(1, frames.Count / Math.Max(1, (int)Bounds.Width));
    }

    private void SetCategoryHit(CategoryHit? hit)
    {
        int? index = hit is { IsLegend: false, Index: >= 0 } ? hit.Value.Index : null;
        string? text = hit?.Text;
        int lane = hit?.Lane ?? -1;
        if (_hover != index || _hoverText != text || _hoverLane != lane)
        {
            _hover = index;
            _hoverText = text;
            _hoverLane = lane;
            _hoverArea = hit?.Area ?? default;
            if (Graph is { } graph)
            {
                UpdateAutomation(graph);
            }

            InvalidateVisual();
        }
    }

    // The graph's name and help text carry what the picture shows in words: the chart, its legend with the shares, and what
    // the cursor is on.
    private void UpdateAutomation(Graph graph)
    {
        string legend = graph switch
        {
            CategoryGraph { View: not GraphForm.Summary } category => "Legend: " + string.Join(", ", LegendEntries(category).Select(e => e.Label)),
            ShareSplitGraph split => $"Legend: first channel, second channel",
            _ => string.Empty,
        };
        AutomationProperties.SetName(this, graph.Title);
        AutomationProperties.SetHelpText(this, string.Join(". ", new[] { legend, _hoverText ?? string.Empty }.Where(t => t.Length > 0)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Graph is { } graph && IsCategory(graph))
        {
            double max = Math.Max(0, ContentHeightOf(graph) - Bounds.Height);
            double next = Math.Clamp(_scroll - (e.Delta.Y * 40), 0, max);
            if (next != _scroll)
            {
                _scroll = next;
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }

    private double ContentHeightOf(Graph graph) => IsSummary(graph)
        ? SummaryRows(graph, Bounds.Width).Height + 30
        : ChartLayoutFor(graph).ContentHeight;

    private CategoryColumns ColumnsFor(CategoryGraph graph, int width, int lane)
    {
        if (!ReferenceEquals(_categoryColumnsGraph, graph))
        {
            _categoryColumns.Clear();
            _categoryColumnsGraph = graph;
        }

        var key = ((int)graph.View, width, lane);
        if (!_categoryColumns.TryGetValue(key, out CategoryColumns? columns))
        {
            if (_categoryColumns.Count > 24)
            {
                _categoryColumns.Clear();
            }

            columns = graph.IsOverTime ? CategoryColumns.OverTime(graph.Series, width, graph.View == GraphForm.Smoothed ? graph.SmoothedCounts : graph.IntervalCounts) : CategoryColumns.Frames(graph.Series, width, lane);
            _categoryColumns[key] = columns;
        }

        return columns;
    }

    private ChartLayout ChartLayoutFor(Graph graph)
    {
        if (_layoutCache is { } cached && ReferenceEquals(cached.Graph, graph) && cached.Size == Bounds.Size)
        {
            return cached.Layout;
        }

        ChartLayout layout = MeasureChart(graph);
        _layoutCache = (graph, Bounds.Size, layout);
        return layout;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // The legend of the over-time chart lists the layers bottom to top, which is largest share first (CategoryGraph.StackOrder):
    // the legend runs left to right and wraps downward, so the baseline layer comes first as the stack's first row would, and the
    // entries then fall in the order of the shares in the file. The strip of frames and the summary tables keep the order of
    // the categories.
    private static IEnumerable<int> LegendOrder(CategoryGraph graph) => graph.IsOverTime
        ? graph.StackOrder
        : Enumerable.Range(0, graph.Series.Categories.Count);

    // The legend's entries as text with their shares, and the category each stands for.
    internal static List<(string Label, int Category)> LegendEntries(Graph graph) => graph switch
    {
        CategoryGraph category => [.. LegendOrder(category).Where(c => category.Series.Counts[c] > 0).Select(c => ($"{category.Series.Categories[c].Name} {CategoryGraph.SharePercent(category.Series.Counts[c], category.Series.Total)}", c))],
        ShareSplitGraph split => [("First channel", 0), ("Second channel", 1)],
        _ => [],
    };

    // Packs the legend entries into lines at most maxLines of them.
    private static (List<(Rect, int)> Rects, int Lines, int Shown) PackLegend(List<(string Label, int Category)> entries, double right, int maxLines)
    {
        var rects = new List<(Rect, int)>();
        double x = 12;
        int line = 0;
        foreach ((string label, int category) in entries)
        {
            double width = 16 + Text(label, TextSize, Brushes.Black).Width + 14;
            if (x + width > right && x > 12)
            {
                line++;
                x = 12;
            }

            if (line >= maxLines)
            {
                break;
            }

            rects.Add((new Rect(x, line * LegendLine, width, LegendLine), category));
            x += width;
        }

        return (rects, rects.Count == 0 ? 1 : line + 1, rects.Count);
    }

    // The chart's parts for the size of the view. The plot is never lower than a minimum (about 110 pixels over time, what
    // its rows need in a strip): the legend folds to one line first, a strip whose rows do not fit becomes one stacked row,
    // and the content that is still taller than the view scrolls.
    private ChartLayout MeasureChart(Graph graph)
    {
        var category = graph as CategoryGraph;
        double right = Bounds.Width - 16;
        int rows = category is { View: GraphForm.Frames } ? category.Rows : 1;
        double labelWidth = category is { View: GraphForm.Frames }
            ? rows > 1 && category.Series.ValueNames.Count > 0 ? Math.Min(150, category.Series.ValueNames.Max(n => Text(n, AxisSize, Brushes.Black).Width)) : 0
            : Text("100%", AxisSize, Brushes.Black).Width;
        double left = 12 + labelWidth + (labelWidth > 0 ? 8 : 0);

        List<(string, int)> entries = LegendEntries(graph);
        (List<(Rect, int)> rects, int lines, int shown) = PackLegend(entries, right, MaxLegendLines);
        double Needed(int r) => (r * MinStripRow) + ((r - 1) * StripGap) + 14;
        double minPlot = category is { View: GraphForm.Frames } ? Math.Max(60, Needed(rows)) : MinOverTimeHeight;

        // Rows that cannot each get their minimum, in the room left with a one-line legend, become one stacked row.
        double roomWithOneLine = Bounds.Height - CaptionHeight - AxisHeight - (LegendLine + 8);
        if (rows > 1 && roomWithOneLine < Needed(rows))
        {
            rows = 1;
            minPlot = 60;
        }

        int chosenLines = 1;
        double plotHeight = minPlot;
        for (int l = lines; l >= 1; l--)
        {
            double room = Bounds.Height - CaptionHeight - AxisHeight - ((l * LegendLine) + 8);
            if (room >= minPlot)
            {
                chosenLines = l;
                plotHeight = room;
                break;
            }
        }

        if (plotHeight == minPlot && Bounds.Height - CaptionHeight - AxisHeight - ((lines * LegendLine) + 8) < minPlot)
        {
            chosenLines = 1;
        }

        if (chosenLines < lines)
        {
            (rects, _, shown) = PackLegend(entries, right, chosenLines);
        }

        double legendTop = CaptionHeight + plotHeight + AxisHeight;
        var placed = rects.Select(r => (new Rect(r.Item1.X, legendTop + r.Item1.Y, r.Item1.Width, r.Item1.Height), r.Item2)).ToList();
        var plot = new Rect(left, CaptionHeight, Math.Max(10, right - left), plotHeight);
        double rowHeight = category is { View: GraphForm.Frames } ? Math.Clamp((plotHeight - ((rows - 1) * StripGap) - 8) / rows, MinStripRow, 64) : plotHeight;
        return new ChartLayout(plot, rowHeight, rows, placed, shown, legendTop + (chosenLines * LegendLine) + 8);
    }

    private static double RowTop(ChartLayout layout, int row) => layout.Plot.Top + 8 + (row * (layout.RowHeight + StripGap));

    private CategoryHit? CategoryHitAt(Point point, bool withText = true, bool stepping = false)
    {
        if (Graph is not { Count: > 0 } graph || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        if (IsSummary(graph))
        {
            SummaryLayout summary = SummaryRows(graph, Bounds.Width);
            Point inContent = new(point.X, point.Y - 24 + _scroll);
            foreach (SummaryRow row in summary.Rows)
            {
                if (row.Rect.Contains(inContent))
                {
                    Rect area = new(row.Rect.X, row.Rect.Y - _scroll + 24, row.Rect.Width, row.Rect.Height);
                    return new CategoryHit(row.Flat, withText ? graph.Describe(row.Flat) : string.Empty, area);
                }
            }

            return null;
        }

        point = new Point(point.X, point.Y + _scroll);
        ChartLayout layout = ChartLayoutFor(graph);
        foreach ((Rect rect, int index) in layout.Legend)
        {
            if (rect.Contains(point))
            {
                return graph is CategoryGraph legendOwner
                    ? new CategoryHit(-1, withText ? CategoryGraph.DescribeCategory(legendOwner.Series, index) : string.Empty, rect, IsLegend: true, Category: index)
                    : null;
            }
        }

        Rect plot = layout.Plot;
        if (point.X < plot.Left || point.X >= plot.Right)
        {
            return null;
        }

        int width = Math.Max(1, (int)plot.Width);
        if (graph is ShareSplitGraph split)
        {
            if (point.Y < plot.Top || point.Y > plot.Bottom)
            {
                return null;
            }

            SplitColumns columns = SplitColumnsOf(split, width);
            return SplitHit(split, columns, SlotsOf(plot, split, columns).At(point.X), plot, withText);
        }

        var category = (CategoryGraph)graph;
        if (category.IsOverTime)
        {
            if (point.Y < plot.Top || point.Y > plot.Bottom)
            {
                return null;
            }

            CategoryColumns columns = ColumnsFor(category, width, -1);
            return OverTimeHit(category, columns, SlotsOf(plot, category, columns).At(point.X), plot, withText);
        }

        for (int row = 0; row < layout.Rows; row++)
        {
            double top = RowTop(layout, row);
            if (point.Y < top - 6 || point.Y > top + layout.RowHeight)
            {
                continue;
            }

            int lane = layout.Rows > 1 ? row : -1;
            CategoryColumns columns = ColumnsFor(category, width, lane);
            int column = Math.Clamp((int)((point.X - plot.Left) * columns.Count / plot.Width), 0, columns.Count - 1);
            return StripHit(category, columns, column, lane, plot, top, layout.RowHeight, withText, stepping ? MarkedIndex : null);
        }

        return null;
    }

    private static CategoryHit OverTimeHit(CategoryGraph graph, CategoryColumns columns, int column, Rect plot, bool withText = true)
    {
        Slots slots = SlotsOf(plot, graph, columns);
        var area = new Rect(slots.X(column), plot.Top, Math.Max(1, slots.W(column)), plot.Height);
        int first = columns.First(column);
        int end = columns.End(column);
        int filled = graph.FirstFilledInterval(first, end);
        return new CategoryHit(filled >= 0 ? filled : first, withText ? (graph.View == GraphForm.Smoothed ? graph.DescribeSmoothed(first, end) : graph.DescribeIntervals(first, end)) : string.Empty, area);
    }

    private static CategoryHit StripHit(CategoryGraph graph, CategoryColumns columns, int column, int lane, Rect plot, double top, double rowHeight, bool withText = true, int? after = null)
    {
        double colW = plot.Width / columns.Count;
        var area = new Rect(plot.Left + (column * colW), top, Math.Max(1, colW), rowHeight);
        int first = columns.First(column);
        int end = columns.End(column);
        return new CategoryHit(graph.UnitOfColumn(first, end, lane, after), withText ? graph.DescribeUnits(first, end, lane) : string.Empty, area, lane);
    }

    private static CategoryHit SplitHit(ShareSplitGraph graph, SplitColumns columns, int column, Rect plot, bool withText = true)
    {
        Slots slots = SlotsOf(plot, graph, columns);
        var area = new Rect(slots.X(column), plot.Top, Math.Max(1, slots.W(column)), plot.Height);
        int first = columns.First(column);
        int end = columns.First(column + 1);
        string text = string.Empty;
        if (withText)
        {
            text = end - first <= 1
                ? graph.Describe(first)
                : $"{Format.Clock(graph.Series.X.Span[first])} to {Format.Clock(graph.Series.X.Span[end - 1])} · first channel {ShareSplitGraph.Percent(columns.Share(column))} · second channel {ShareSplitGraph.Percent(100 - columns.Share(column))} (mean of {end - first} intervals)";
        }

        return new CategoryHit(first, text, area);
    }

    // The hit for a key: the interval, unit or row at the index, in the strip's row that was last pointed at.
    private CategoryHit? HitOfIndex(Graph graph, int index)
    {
        if (IsSummary(graph))
        {
            SummaryLayout summary = SummaryRows(graph, Bounds.Width);
            SummaryRow row = summary.Rows[index];
            _scroll = Math.Clamp(_scroll, Math.Max(0, row.Rect.Bottom - (Bounds.Height - 30)), row.Rect.Y);
            var area = new Rect(row.Rect.X, row.Rect.Y - _scroll + 24, row.Rect.Width, row.Rect.Height);
            return new CategoryHit(index, graph.Describe(index), area);
        }

        ChartLayout layout = ChartLayoutFor(graph);
        int width = Math.Max(1, (int)layout.Plot.Width);
        if (graph is ShareSplitGraph split)
        {
            SplitColumns splitColumns = SplitColumnsOf(split, width);
            CategoryHit hit = SplitHit(split, splitColumns, ColumnOf(splitColumns.Firsts, index), layout.Plot);
            return hit with { Index = index, Text = split.Describe(index) };
        }

        var category = (CategoryGraph)graph;
        if (category.IsOverTime)
        {
            CategoryColumns columns = ColumnsFor(category, width, -1);
            CategoryHit hit = OverTimeHit(category, columns, ColumnOf(columns, index), layout.Plot);
            return hit with { Index = index, Text = category.Describe(index) };
        }

        int rowIndex = Math.Clamp(_hoverLane, 0, layout.Rows - 1);
        int lane = layout.Rows > 1 ? rowIndex : -1;
        CategoryColumns strip = ColumnsFor(category, width, lane);
        CategoryHit stripHit = StripHit(category, strip, ColumnOf(strip, index), lane, layout.Plot, RowTop(layout, layout.Rows > 1 ? rowIndex : 0), layout.RowHeight);
        return stripHit with { Index = index, Text = category.DescribeUnits(index, index + 1, lane) };
    }

    // The column that holds an interval or unit.
    private static int ColumnOf(CategoryColumns columns, int item)
    {
        int low = 0;
        int high = columns.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >>> 1;
            if (columns.First(middle) <= item)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static int ColumnOf(int[] firsts, int item)
    {
        int found = Array.BinarySearch(firsts, 0, firsts.Length - 1, item);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
    }

    // The intervals of a two-part share merged into at most as many columns as pixels: the mean of the first part's share in each.
    private sealed class SplitColumns(int[] firsts, double[] shares)
    {
        public int[] Firsts { get; } = firsts;

        public int Count => shares.Length;

        public int First(int column) => Firsts[column];

        public double Share(int column) => shares[column];
    }

    private SplitColumns SplitColumnsOf(ShareSplitGraph graph, int width)
    {
        int n = graph.Count;
        int used = Math.Max(1, Math.Min(n, width));
        var firsts = new int[used + 1];
        var shares = new double[used];
        for (int column = 0; column < used; column++)
        {
            int from = (int)((long)column * n / used);
            int to = (int)((long)(column + 1) * n / used);
            firsts[column] = from;
            double sum = 0;
            int count = 0;
            for (int i = from; i < to; i++)
            {
                double value = graph.FirstShare(i);
                if (!double.IsNaN(value))
                {
                    sum += value;
                    count++;
                }
            }

            shares[column] = count == 0 ? double.NaN : sum / count;
        }

        firsts[used] = n;
        return new SplitColumns(firsts, shares);
    }

    private void RenderCategory(DrawingContext context, Graph graph)
    {
        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        Color surface = Palette.SurfaceColor(this);
        IBrush secondary = Palette.Secondary(this);
        IBrush foreground = Palette.Foreground(this);
        if (IsSummary(graph))
        {
            DrawSummary(context, (ISummaryGraph)graph, graph, isDark, surface, secondary, foreground);
            if (_hover is not null && _hoverText is { } text)
            {
                DrawRowTooltip(context, text);
            }

            return;
        }

        ChartLayout layout = ChartLayoutFor(graph);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, layout.ContentHeight - Bounds.Height));
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateTranslation(0, -_scroll)))
        {
            if (graph is ShareSplitGraph split)
            {
                DrawSplit(context, split, layout, isDark, surface, secondary, foreground);
            }
            else
            {
                DrawChart(context, (CategoryGraph)graph, layout, isDark, surface, secondary, foreground);
            }
        }
    }

    private void DrawChart(DrawingContext context, CategoryGraph category, ChartLayout layout, bool isDark, Color surface, IBrush secondary, IBrush foreground)
    {
        CategorySeries series = category.Series;
        CategoryStyle[] styles = category.IsOverTime ? CategoryColors.ForStack(series, isDark, surface) : CategoryColors.For(series, isDark, surface);
        IBrush[] brushes = [.. styles.Select(s => (IBrush)new ImmutableSolidColorBrush(s.Color))];
        bool highContrast = HasHighContrast();
        Rect plot = layout.Plot;
        IPen grid = new Pen(Palette.Grid(this), 1);
        int width = Math.Max(1, (int)plot.Width);

        if (category.IsOverTime)
        {
            string caption = category.View == GraphForm.Smoothed
                ? $"Share of the values, each interval averaged with its neighbors over {CategoryGraph.SmoothingWindow} seconds"
                : "Share of the values in each interval";
            FormattedText captionText = Text(caption, TextSize, secondary);
            context.DrawText(captionText, new Point(4, 2));
            DrawPercentTicks(context, plot, grid, secondary);
            CategoryColumns columns = ColumnsFor(category, width, -1);
            Slots slots = SlotsOf(plot, category, columns);
            if (HatchedLastNote(category, columns) is { } note)
            {
                context.DrawText(Text(note, TextSize, secondary), new Point(4 + captionText.Width + 16, 2));
            }

            DrawArea(context, category, columns, slots, plot, styles, brushes, surface, highContrast);
            DrawTimeAxis(context, series.Time!.Starts.Span, slots, columns.First, secondary, plot);
            if (MarkedIndex is { } marked && marked < category.Count)
            {
                double x = Math.Round(slots.Center(ColumnOf(columns, marked))) + 0.5;
                context.DrawLine(new Pen(foreground, 1.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
        else
        {
            var heights = new double[series.Categories.Count];
            string caption = layout.Rows > 1 ? $"One row for each of the {series.ValuesPerUnit} values of a frame" : Capitalize($"{series.ValueLabel} values of each frame");
            context.DrawText(Text(caption, TextSize, secondary), new Point(4, 2));
            for (int row = 0; row < layout.Rows; row++)
            {
                int lane = layout.Rows > 1 ? row : -1;
                double top = RowTop(layout, row);
                CategoryColumns columns = ColumnsFor(category, width, lane);
                double colW = plot.Width / columns.Count;
                for (int column = 0; column < columns.Count; column++)
                {
                    ReadOnlySpan<long> counts = columns.Counts(column);
                    if (columns.Total(column) == 0)
                    {
                        continue;
                    }

                    double x = plot.Left + (column * colW);
                    DrawStack(context, counts, x, colW, top, layout.RowHeight, styles, brushes, heights, surface, highContrast, series);
                    for (int c = 0; c < counts.Length; c++)
                    {
                        if (columns.IsRare(column, c))
                        {
                            context.FillRectangle(brushes[c], new Rect(Math.Round(x + (Math.Max(1, colW) / 2)) - 0.5, top - 5, Math.Max(1, Math.Min(2, colW)), 4));
                        }
                    }
                }

                if (layout.Rows > 1 && row < series.ValueNames.Count)
                {
                    FormattedText name = Fit(series.ValueNames[row], AxisSize, secondary, plot.Left - 20);
                    context.DrawText(name, new Point(plot.Left - 8 - name.Width, top + ((layout.RowHeight - name.Height) / 2)));
                }
            }

            DrawFrameAxis(context, category, plot, RowTop(layout, layout.Rows - 1) + layout.RowHeight + 4, secondary);
            if (MarkedIndex is { } marked && marked < category.Count)
            {
                double x = Math.Round(plot.Left + ((marked + 0.5) * plot.Width / category.Count)) + 0.5;
                context.DrawLine(new Pen(foreground, 1.5), new Point(x, plot.Top), new Point(x, RowTop(layout, layout.Rows - 1) + layout.RowHeight + 2));
            }
        }

        DrawLegend(context, layout, series, brushes, styles, secondary, surface);
        DrawCursor(context, plot, foreground);
    }

    // The hover text of a table row on the row itself, from its middle to the right (over its bar and counts), so that the rows
    // above and below the pointer stay readable.
    private void DrawRowTooltip(DrawingContext context, string text)
    {
        FormattedText label = Text(text, TextSize, Palette.Foreground(this));
        label.MaxTextWidth = Math.Max(160, Bounds.Width - 40);
        var size = new Size(label.Width + 16, label.Height + 8);
        double x = Math.Min(Math.Max(2, _hoverArea.X + (_hoverArea.Width / 2)), Math.Max(2, Bounds.Width - size.Width - 2));
        double y = Math.Clamp(_hoverArea.Y + ((_hoverArea.Height - size.Height) / 2), 2, Math.Max(2, Bounds.Height - size.Height - 2));
        var box = new Rect(x, y, size.Width, size.Height);
        context.DrawRectangle(Palette.TooltipBackground(this), new Pen(Palette.Grid(this, 0.3), 1), box, 4, 4);
        context.DrawText(label, new Point(box.X + 8, box.Y + 4));
    }

    private void DrawCursor(DrawingContext context, Rect plot, IBrush foreground)
    {
        if (_hover is not null && _hoverText is { } text)
        {
            context.DrawRectangle(null, new Pen(foreground, 1), _hoverArea.Inflate(0.5));
            DrawTooltip(context, text, new Point(_hoverArea.Center.X, _hoverArea.Y), plot);
        }
    }

    // Ticks by the room between them: every 25% where each has 22 pixels, else every 50%, else the ends.
    private void DrawPercentTicks(DrawingContext context, Rect plot, IPen grid, IBrush secondary)
    {
        int[] ticks = plot.Height / 4 >= 22 ? [0, 25, 50, 75, 100] : plot.Height / 2 >= 22 ? [0, 50, 100] : [0, 100];
        foreach (int percent in ticks)
        {
            double y = Math.Round(plot.Bottom - (plot.Height * percent / 100.0)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = Text(percent + "%", AxisSize, secondary);
            context.DrawText(label, new Point(plot.Left - 8 - label.Width, Math.Clamp(y - (label.Height / 2), plot.Top - 4, plot.Bottom - label.Height + 4)));
        }
    }

    private void DrawSplit(DrawingContext context, ShareSplitGraph graph, ChartLayout layout, bool isDark, Color surface, IBrush secondary, IBrush foreground)
    {
        IReadOnlyList<Color> hues = isDark ? CategoryColors.Dark : CategoryColors.Light;
        Rect plot = layout.Plot;
        context.DrawText(Text("Share of the audio bits of the two channels in each interval", TextSize, secondary), new Point(4, 2));
        DrawPercentTicks(context, plot, new Pen(Palette.Grid(this), 1), secondary);
        SplitColumns columns = SplitColumnsOf(graph, Math.Max(1, (int)plot.Width));
        Slots slots = SlotsOf(plot, graph, columns);
        IBrush first = new ImmutableSolidColorBrush(hues[0]);
        IBrush second = new ImmutableSolidColorBrush(hues[1]);
        IBrush gap = new ImmutableSolidColorBrush(surface);
        for (int column = 0; column < columns.Count; column++)
        {
            double share = columns.Share(column);
            if (double.IsNaN(share))
            {
                continue;
            }

            double x = slots.X(column);
            double colW = slots.W(column);
            double width = colW >= 4 ? colW - 1 : colW;
            double low = plot.Height * share / 100;
            context.FillRectangle(first, new Rect(x, plot.Bottom - low, width, low));
            context.FillRectangle(second, new Rect(x, plot.Top, width, plot.Height - low));
            if (low >= 4 && plot.Height - low >= 4)
            {
                context.FillRectangle(gap, new Rect(x, plot.Bottom - low - 1, width, 2));
            }
        }

        // The 50% line.
        double mid = Math.Round(plot.Bottom - (plot.Height / 2)) + 0.5;
        context.DrawLine(new Pen(foreground, 1, dashStyle: DashStyle.Dash), new Point(plot.Left, mid), new Point(plot.Right, mid));
        DrawTimeAxis(context, graph.Series.X.Span, slots, columns.First, secondary, plot);
        if (MarkedIndex is { } marked && marked < graph.Count)
        {
            double x = Math.Round(slots.Center(ColumnOf(columns.Firsts, marked))) + 0.5;
            context.DrawLine(new Pen(foreground, 1.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        foreach ((Rect rect, int index) in layout.Legend)
        {
            var swatch = new Rect(rect.X, rect.Y + ((rect.Height - 10) / 2), 10, 10);
            context.FillRectangle(index == 0 ? first : second, swatch, 2f);
            FormattedText text = Text(LegendEntries(graph)[index].Label, TextSize, foreground);
            context.DrawText(text, new Point(rect.X + 16, rect.Y + ((rect.Height - text.Height) / 2)));
        }

        DrawCursor(context, plot, foreground);
    }

    // The edges of every column of an over-time chart: where a column starts and where it ends, which for all but the last
    // is where the next starts, so that neighbors share an edge exactly.
    private static (double[] Start, double[] End) ColumnEdges(Slots slots)
    {
        var start = new double[slots.Count];
        var end = new double[slots.Count];
        for (int column = 0; column < slots.Count; column++)
        {
            start[column] = slots.X(column);
            end[column] = column + 1 < slots.Count ? slots.X(column + 1) : slots.X(column) + slots.W(column);
        }

        return (start, end);
    }

    // The runs of columns that hold values: an interval without any leaves a gap instead of a step.
    private static List<(int From, int To)> FilledRuns(CategoryColumns columns)
    {
        var runs = new List<(int, int)>();
        int from = -1;
        for (int column = 0; column <= columns.Count; column++)
        {
            bool filled = column < columns.Count && columns.Total(column) > 0;
            if (filled && from < 0)
            {
                from = column;
            }
            else if (!filled && from >= 0)
            {
                runs.Add((from, column));
                from = -1;
            }
        }

        return runs;
    }

    // The share of each category over time as a stacked area with stepped edges: each interval is a level that lasts exactly
    // as long as the interval does, with a vertical step at each boundary and no slope between them. The category with the
    // largest share is the bottom layer, the one with a flat baseline, in the muted color (CategoryColors.ForStack). Levels are
    // whole pixels, and a category that occurs is at least a pixel high. A 1-pixel line of the surface separates two layers
    // where both are at least MinSeparated pixels high, as the strip separates its segments, so a rare layer keeps its color and
    // a full switch shows no stripe at its step. An interval of no values is a gap. The layers carry no names: the legend, in the
    // order of the stack, and the hover text name them.
    private void DrawArea(DrawingContext context, CategoryGraph graph, CategoryColumns columns, Slots slots, Rect plot, CategoryStyle[] styles, IBrush[] brushes, Color surface, bool highContrast)
    {
        CategorySeries series = graph.Series;
        int categories = series.Categories.Count;
        int[] order = graph.StackOrder;
        var tops = new double[columns.Count * categories];
        var bottoms = new double[columns.Count * categories];
        var heights = new double[categories];
        for (int column = 0; column < columns.Count; column++)
        {
            if (columns.Total(column) == 0)
            {
                continue;
            }

            CategoryColumns.StackHeights(columns.Counts(column), plot.Height, heights);
            double used = 0;
            foreach (int category in order)
            {
                bottoms[(column * categories) + category] = Math.Round(plot.Bottom - used);
                used += heights[category];
                tops[(column * categories) + category] = Math.Round(plot.Bottom - used);
            }
        }

        (double[] start, double[] end) = ColumnEdges(slots);
        List<(int From, int To)> runs = FilledRuns(columns);
        foreach (int category in order)
        {
            foreach ((int from, int to) in runs)
            {
                var fill = new StreamGeometry();
                bool present = false;
                using (StreamGeometryContext g = fill.Open())
                {
                    g.BeginFigure(new Point(start[from], tops[(from * categories) + category]), true);
                    for (int column = from; column < to; column++)
                    {
                        double top = tops[(column * categories) + category];
                        present |= bottoms[(column * categories) + category] > top;
                        if (column > from)
                        {
                            g.LineTo(new Point(start[column], top));
                        }

                        g.LineTo(new Point(end[column], top));
                    }

                    for (int column = to - 1; column >= from; column--)
                    {
                        double bottom = bottoms[(column * categories) + category];
                        g.LineTo(new Point(end[column], bottom));
                        g.LineTo(new Point(start[column], bottom));
                    }

                    g.EndFigure(true);
                }

                if (!present)
                {
                    continue;
                }

                context.DrawGeometry(brushes[category], null, fill);
                if (styles[category].IsHatched || (highContrast && category % 2 == 1))
                {
                    using (context.PushGeometryClip(fill))
                    {
                        DrawHatch(context, fill.Bounds, surface);
                    }
                }
            }
        }

        DrawSeparators(context, columns, start, end, tops, bottoms, order, surface);

        // A partial last interval of only a few frames keeps its exact shares and is hatched, as the note under the caption and the
        // hover text say (CategoryGraph.LastInterval): in the area, not in the average, whose last level is not a share of those
        // frames, and not in a column that merges intervals. The hatch is the text color at an angle opposite to the reserved
        // categories' hatch, over the layers' own colors, which show between its lines.
        int last = columns.Count - 1;
        if (HatchedLastNote(graph, columns) is not null)
        {
            Color ink = Palette.Foreground(this) is ISolidColorBrush foreground ? foreground.Color : Colors.Gray;
            DrawLastHatch(context, new Rect(start[last], plot.Top, end[last] - start[last], plot.Height), ink);
        }
    }

    // The note under the caption when the chart draws its last interval hatched, else null: the Over Time form, with the last
    // interval as a column of its own that has values and is a partial one of a few frames.
    private static string? HatchedLastNote(CategoryGraph graph, CategoryColumns columns)
    {
        int last = columns.Count - 1;
        return graph.View == GraphForm.OverTime && graph.LastInterval is { IsFew: true } interval && columns.End(last) - columns.First(last) == 1 && columns.Total(last) > 0
            ? CategoryGraph.DescribeHatchedLast(interval)
            : null;
    }

    // The note the chart shows now, for tests.
    internal string? HatchNote => Graph is CategoryGraph { IsOverTime: true } graph && Bounds.Width >= 80 && Bounds.Height >= 60
        ? HatchedLastNote(graph, ColumnsFor(graph, Math.Max(1, (int)ChartLayoutFor(graph).Plot.Width), -1))
        : null;

    // The surface line between a layer and the layer above it, as level segments (a run of columns at one height is one
    // segment) and only where both are at least MinSeparated pixels high.
    private static void DrawSeparators(DrawingContext context, CategoryColumns columns, double[] start, double[] end, double[] tops, double[] bottoms, int[] order, Color surface)
    {
        IBrush brush = new ImmutableSolidColorBrush(surface);
        foreach (int category in order)
        {
            double from = double.NaN;
            double to = 0;
            double y = 0;
            for (int column = 0; column <= columns.Count; column++)
            {
                double separator = column < columns.Count ? SeparatorAt(columns, tops, bottoms, order, category, column) : double.NaN;
                if (!double.IsNaN(from) && (double.IsNaN(separator) || separator != y))
                {
                    context.FillRectangle(brush, new Rect(from, y, to - from, 1));
                    from = double.NaN;
                }

                if (!double.IsNaN(separator))
                {
                    if (double.IsNaN(from))
                    {
                        from = start[column];
                        y = separator;
                    }

                    to = end[column];
                }
            }
        }
    }

    // The row of the line over a layer in a column, or NaN where there is none: the layer and the next one above that has
    // any height both have to be at least MinSeparated pixels.
    private static double SeparatorAt(CategoryColumns columns, double[] tops, double[] bottoms, int[] order, int category, int column)
    {
        int categories = order.Length;
        if (columns.Total(column) == 0 || bottoms[(column * categories) + category] - tops[(column * categories) + category] < MinSeparated)
        {
            return double.NaN;
        }

        for (int rank = Array.IndexOf(order, category) + 1; rank < order.Length; rank++)
        {
            int above = order[rank];
            double height = bottoms[(column * categories) + above] - tops[(column * categories) + above];
            if (height > 0)
            {
                return height >= MinSeparated ? tops[(column * categories) + category] : double.NaN;
            }
        }

        return double.NaN;
    }

    // One stack of segments, the plain category at the bottom, each category present at least a pixel high, two pixels of
    // the surface between segments that are tall enough to spare them.
    private void DrawStack(DrawingContext context, ReadOnlySpan<long> counts, double x, double columnWidth, double top, double height, CategoryStyle[] styles, IBrush[] brushes, double[] heights, Color surface, bool highContrast, CategorySeries series)
    {
        CategoryColumns.StackHeights(counts, height, heights);
        double width = columnWidth >= 4 ? columnWidth - 1 : columnWidth;
        double y = top + height;
        double previous = 0;
        bool label = series.Categories.Count <= 4 && width >= 70;
        for (int category = 0; category < counts.Length; category++)
        {
            double h = heights[category];
            if (h <= 0)
            {
                continue;
            }

            y -= h;
            var rect = new Rect(x, y, width, h);
            context.FillRectangle(brushes[category], rect);
            if (styles[category].IsHatched || (highContrast && category % 2 == 1))
            {
                DrawHatch(context, rect, surface);
            }

            if (previous >= 4 && h >= 4)
            {
                context.FillRectangle(new ImmutableSolidColorBrush(surface), new Rect(x, y + h - 1, width, 2));
            }

            if (label && h >= 14)
            {
                FormattedText name = Fit(series.Categories[category].Name, TextSize, new ImmutableSolidColorBrush(CategoryColors.Readable(styles[category].Color)), width - 8);
                context.DrawText(name, new Point(x + 4, y + ((h - name.Height) / 2)));
            }

            previous = h;
        }
    }

    private static void DrawHatch(DrawingContext context, Rect rect, Color lines)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(lines, 0.85), 1.5);
        using (context.PushClip(rect))
        {
            for (double dx = -rect.Height; dx < rect.Width; dx += 5)
            {
                context.DrawLine(pen, new Point(rect.X + dx, rect.Bottom), new Point(rect.X + dx + rect.Height, rect.Y));
            }
        }
    }

    // The hatch of the last interval: lines falling to the right every 4 pixels, the reserved categories' rising to the right every 5.
    private static void DrawLastHatch(DrawingContext context, Rect rect, Color ink)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(ink, HatchedLastOpacity), 1);
        using (context.PushClip(rect))
        {
            for (double dx = -rect.Height; dx < rect.Width; dx += 4)
            {
                context.DrawLine(pen, new Point(rect.X + dx, rect.Y), new Point(rect.X + dx + rect.Height, rect.Bottom));
            }
        }
    }

    private void DrawLegend(DrawingContext context, ChartLayout layout, CategorySeries series, IBrush[] brushes, CategoryStyle[] styles, IBrush secondary, Color surface)
    {
        IBrush foreground = Palette.Foreground(this);
        foreach ((Rect rect, int category) in layout.Legend)
        {
            var swatch = new Rect(rect.X, rect.Y + ((rect.Height - 10) / 2), 10, 10);
            context.FillRectangle(brushes[category], swatch, 2f);
            if (styles[category].IsHatched)
            {
                DrawHatch(context, swatch, surface);
            }

            string label = $"{series.Categories[category].Name} {CategoryGraph.SharePercent(series.Counts[category], series.Total)}";
            FormattedText text = Text(label, TextSize, series.Counts[category] > 0 ? foreground : secondary);
            context.DrawText(text, new Point(rect.X + 16, rect.Y + ((rect.Height - text.Height) / 2)));
        }

        if (layout.Legend.Count < series.Counts.Count(c => c > 0) && layout.Legend.Count > 0)
        {
            Rect last = layout.Legend[^1].Rect;
            string more = $"+{series.Counts.Count(c => c > 0) - layout.Legend.Count} more in the Summary";
            context.DrawText(Text(more, TextSize, secondary), new Point(last.Right, last.Y + 2));
        }
    }

    // Time labels about every 90 pixels under the columns, from the start of each column's first interval.
    private void DrawTimeAxis(DrawingContext context, ReadOnlySpan<double> starts, Slots slots, Func<int, int> firstOf, IBrush secondary, Rect plot)
    {
        int columns = slots.Count;
        double lastEnd = double.NegativeInfinity;
        int step = Math.Max(1, (int)Math.Ceiling(90 / (plot.Width / columns)));
        for (int column = 0; column < columns; column += step)
        {
            FormattedText label = Text(Format.Clock(starts[firstOf(column)]), AxisSize, secondary);
            double x = slots.X(column);
            double start = Math.Clamp(x - (label.Width / 2), plot.Left - 4, plot.Right - label.Width);
            if (start < lastEnd + 12)
            {
                continue;
            }

            context.DrawText(label, new Point(start, plot.Bottom + 4));
            lastEnd = start + label.Width;
        }
    }

    private void DrawFrameAxis(DrawingContext context, CategoryGraph graph, Rect plot, double y, IBrush secondary)
    {
        int count = graph.Count;
        int step = NiceStep(count * 90 / plot.Width);
        double lastEnd = double.NegativeInfinity;
        FormattedText caption = Text("frames", AxisSize, secondary);
        double limit = plot.Right - (caption.Width + 12);
        for (long position = 0; position <= count; position += step)
        {
            FormattedText label = Text(position.ToString("N0", CultureInfo.InvariantCulture), AxisSize, secondary);
            double x = plot.Left + (position * plot.Width / count);
            double start = Math.Clamp(x - (label.Width / 2), plot.Left - 4, plot.Right - label.Width);
            if (start < lastEnd + 12 || start + label.Width > limit)
            {
                continue;
            }

            context.DrawText(label, new Point(start, y));
            lastEnd = start + label.Width;
        }

        context.DrawText(caption, new Point(plot.Right - caption.Width, y));
    }

    private readonly record struct SummaryRow(Rect Rect, int Flat, int Table, int Category);

    private readonly record struct SummaryTable(Rect Header, int Table);

    private readonly record struct SummaryLayout(IReadOnlyList<SummaryRow> Rows, IReadOnlyList<SummaryTable> Tables, double Height, double TableWidth);

    // The tables in a grid of as many columns as fit, in content coordinates.
    private static SummaryLayout SummaryRows(Graph graph, double width)
    {
        IReadOnlyList<CategorySeries> tables = ((ISummaryGraph)graph).Tables;
        double available = Math.Max(200, width - 28);
        int columns = Math.Max(1, (int)((available + 24) / (TableWidth + 24)));
        double tableWidth = Math.Min(TableWidth, (available - ((columns - 1) * 24)) / columns);
        var rows = new List<SummaryRow>();
        var headers = new List<SummaryTable>();
        double[] columnTops = new double[columns];
        int flat = 0;
        for (int table = 0; table < tables.Count; table++)
        {
            int column = Array.IndexOf(columnTops, columnTops.Min());
            double x = 12 + (column * (tableWidth + 24));
            double y = columnTops[column];
            headers.Add(new SummaryTable(new Rect(x, y, tableWidth, TableHeader), table));
            y += TableHeader + (tables[table].UncodedValues > 0 ? 16 : 0);
            for (int category = 0; category < tables[table].Categories.Count; category++)
            {
                rows.Add(new SummaryRow(new Rect(x, y, tableWidth, RowHeightOfTable), flat++, table, category));
                y += RowHeightOfTable;
            }

            columnTops[column] = y + 16;
        }

        return new SummaryLayout(rows, headers, columnTops.Max(), tableWidth);
    }

    private void DrawSummary(DrawingContext context, ISummaryGraph summary, Graph graph, bool isDark, Color surface, IBrush secondary, IBrush foreground)
    {
        SummaryLayout layout = SummaryRows(graph, Bounds.Width);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, layout.Height - (Bounds.Height - 30)));
        var clip = new Rect(0, 24, Bounds.Width, Math.Max(0, Bounds.Height - 24));
        context.DrawText(Text("Count and share of the values of the whole stream", TextSize, secondary), new Point(4, 2));
        IBrush trackBrush = Palette.Grid(this, 0.1);
        bool highContrast = HasHighContrast();
        using (context.PushClip(clip))
        using (context.PushTransform(Matrix.CreateTranslation(0, 24 - _scroll)))
        {
            CategoryStyle[][] styles = [.. summary.Tables.Select(t => CategoryColors.For(t, isDark, surface))];
            (double, double) columns = SummaryColumns(summary.Tables.SelectMany(t => t.Counts), TextSize);
            foreach (SummaryTable header in layout.Tables)
            {
                CategorySeries series = summary.Tables[header.Table];
                FormattedText title = Text(series.Name, TextSize, foreground);
                title.SetFontWeight(FontWeight.SemiBold);
                context.DrawText(title, new Point(header.Header.X, header.Header.Y + 2));
                string totals = Format.Count(series.Total, "value", "values") + " · " + series.ValueLabel;
                FormattedText sub = Fit(totals, TextSize, secondary, header.Header.Width - title.Width - 12);
                context.DrawText(sub, new Point(header.Header.Right - sub.Width, header.Header.Y + 4));
                context.DrawLine(new Pen(Palette.Grid(this, 0.25), 1), new Point(header.Header.X, header.Header.Bottom - 2), new Point(header.Header.Right, header.Header.Bottom - 2));
                if (series.UncodedValues > 0)
                {
                    string note = $"{Format.Count(series.UncodedValues, "value", "values")} counted without a code, not drawn per frame";
                    context.DrawText(Fit(note, TextSize, secondary, header.Header.Width), new Point(header.Header.X, header.Header.Bottom));
                }
            }

            foreach (SummaryRow row in layout.Rows)
            {
                CategorySeries series = summary.Tables[row.Table];
                Rect rect = row.Rect;
                if (_hover == row.Flat)
                {
                    context.FillRectangle(Palette.Grid(this, 0.08), rect, 3f);
                }

                CategoryStyle style = styles[row.Table][row.Category];
                var swatch = new Rect(rect.X + 2, rect.Y + ((rect.Height - 10) / 2), 10, 10);
                context.FillRectangle(new ImmutableSolidColorBrush(style.Color), swatch, 2f);
                if (style.IsHatched || (highContrast && row.Category % 2 == 1))
                {
                    DrawHatch(context, swatch, surface);
                }

                long count = series.Counts[row.Category];
                double share = series.Total > 0 ? (double)count / series.Total : 0;
                const double barWidth = 96;
                (double shareWidth, double countWidth) = columns;
                double right = rect.Right;
                var track = new Rect(right - barWidth, rect.Y + ((rect.Height - 8) / 2), barWidth, 8);
                context.FillRectangle(trackBrush, track, 2f);
                if (count > 0)
                {
                    context.FillRectangle(new ImmutableSolidColorBrush(style.Color), new Rect(track.X, track.Y, Math.Max(2, barWidth * share), 8), 2f);
                }

                FormattedText percent = Text(CategoryGraph.SharePercent(count, series.Total), TextSize, foreground);
                context.DrawText(percent, new Point(track.X - 8 - percent.Width, rect.Y + ((rect.Height - percent.Height) / 2)));
                FormattedText number = Text(count.ToString("N0", CultureInfo.InvariantCulture), TextSize, count > 0 ? foreground : secondary);
                double countRight = track.X - 8 - shareWidth;
                context.DrawText(number, new Point(countRight - number.Width, rect.Y + ((rect.Height - number.Height) / 2)));
                FormattedText name = Fit(series.Categories[row.Category].Name, TextSize, count > 0 ? foreground : secondary, Math.Max(40, countRight - countWidth - rect.X - 20));
                context.DrawText(name, new Point(rect.X + 18, rect.Y + ((rect.Height - name.Height) / 2)));
            }
        }
    }

    // The widths of the share column and the count column of a summary table, each measured from its widest text plus a gap, so
    // that the two right-aligned numbers never run together: the share column holds "100.0%", the count column the largest count.
    internal const double SummaryGap = 12;

    internal static (double Share, double Count) SummaryColumns(IEnumerable<long> counts, double size)
    {
        double share = Text("100.0%", size, Brushes.Black).Width + SummaryGap;
        double count = Text(counts.DefaultIfEmpty(0).Max().ToString("N0", CultureInfo.InvariantCulture), size, Brushes.Black).Width + SummaryGap;
        return (share, count);
    }

    // The columns of an over-time chart: equal slots, but a last interval that lasts less than the others takes its share of one.
    internal readonly record struct Slots(double Left, double Width, int Count, double LastFraction)
    {
        private double Unit => Width / (Count - 1 + LastFraction);

        public double X(int column) => Left + (column * Unit);

        public double W(int column) => column == Count - 1 ? LastFraction * Unit : Unit;

        public double Center(int column) => X(column) + (W(column) / 2);

        public int At(double x) => Math.Clamp((int)((x - Left) / Unit), 0, Count - 1);
    }

    // Where the columns of an over-time chart are: one column for each interval, the last as wide as its length against the
    // others' when the stream ends inside it; columns that merge intervals are equal.
    internal static Slots SlotsOf(Rect plot, int columns, ReadOnlySpan<double> starts, double duration)
    {
        double fraction = 1;
        if (columns == starts.Length && columns >= 2 && duration > starts[^1] && starts[1] > starts[0])
        {
            fraction = Math.Clamp((duration - starts[^1]) / (starts[1] - starts[0]), 0.02, 1);

            // A last interval of a long stream would be a fraction of a pixel, which can be neither seen nor pointed at: it
            // takes MinLastWidth pixels from the one before it (a few pixels of time on a stream of minutes).
            if (fraction < 1 && plot.Width > 2 * MinLastWidth)
            {
                fraction = Math.Max(fraction, Math.Min(1, MinLastWidth * (columns - 1) / (plot.Width - MinLastWidth)));
            }
        }

        return new Slots(plot.Left, plot.Width, columns, fraction);
    }

    private static Slots SlotsOf(Rect plot, CategoryGraph graph, CategoryColumns columns) => SlotsOf(plot, columns.Count, graph.Series.Time!.Starts.Span, graph.Duration);

    private static Slots SlotsOf(Rect plot, ShareSplitGraph graph, SplitColumns columns) => SlotsOf(plot, columns.Count, graph.Series.X.Span, graph.Duration);

    private static FormattedText Fit(string text, double size, IBrush brush, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }
}
