using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The category charts: the share of each category over time, the strip of one cell per frame and the summary tables. Made
// from the series alone, in the palette of CategoryColors, always with names and shares in text.
public sealed partial class GraphView
{
    private const double RowHeightOfTable = 22;
    private const double TableHeader = 28;
    private const double TableWidth = 470;
    private const double LegendLine = 18;
    private const int MaxLegendLines = 3;
    private const double StripGap = 6;

    private readonly Dictionary<(int View, int Width, int Lane), CategoryColumns> _categoryColumns = [];
    private Graph? _categoryColumnsGraph;
    private double _scroll;
    private int _hoverLane = -1;
    private Rect _hoverArea;

    // The category chart's answer to a pointer position or to a key: the point it stands for (what the graph's FrameAt
    // takes), its hover text, the cell it covers, the strip row, and for a legend entry its category.
    private readonly record struct CategoryHit(int Index, string Text, Rect Area, int Lane = -1, bool IsLegend = false, int Category = -1);

    private readonly record struct CategoryLayout(Rect Plot, double RowHeight, int Rows, IReadOnlyList<(Rect Rect, int Category)> Legend, double LegendBottom);

    private static bool IsCategory(Graph graph) => graph is CategoryGraph or CodingSummaryGraph;

    private static bool IsSummary(Graph graph) => graph is CodingSummaryGraph || graph is CategoryGraph { View: CategoryView.Summary };

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
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || graph is not CategoryGraph { View: CategoryView.Frames } frames)
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
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Graph is { } graph && IsCategory(graph) && IsSummary(graph))
        {
            double max = Math.Max(0, SummaryRows(graph, Bounds.Width).Height - (Bounds.Height - 30));
            double next = Math.Clamp(_scroll - (e.Delta.Y * 40), 0, max);
            if (next != _scroll)
            {
                _scroll = next;
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }

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

            columns = graph.View == CategoryView.OverTime ? CategoryColumns.OverTime(graph.Series, width) : CategoryColumns.Frames(graph.Series, width, lane);
            _categoryColumns[key] = columns;
        }

        return columns;
    }

    private (CategoryGraph Graph, CategoryView View, Size Size, CategoryLayout Layout)? _layoutCache;

    private CategoryLayout LayoutOfCategory(CategoryGraph graph)
    {
        if (_layoutCache is { } cached && ReferenceEquals(cached.Graph, graph) && cached.View == graph.View && cached.Size == Bounds.Size)
        {
            return cached.Layout;
        }

        CategoryLayout layout = MeasureCategoryLayout(graph);
        _layoutCache = (graph, graph.View, Bounds.Size, layout);
        return layout;
    }

    private CategoryLayout MeasureCategoryLayout(CategoryGraph graph)
    {
        CategorySeries series = graph.Series;
        double labelWidth = 0;
        if (graph.View == CategoryView.OverTime)
        {
            labelWidth = Text("100%", FontSize, Brushes.Black).Width;
        }
        else if (graph.Rows > 1 && series.ValueNames.Count > 0)
        {
            labelWidth = Math.Min(150, series.ValueNames.Max(n => Text(n, FontSize, Brushes.Black).Width));
        }

        double left = 12 + labelWidth + (labelWidth > 0 ? 8 : 0);
        double right = Bounds.Width - 16;
        var legend = new List<(Rect, int)>();
        double x = 12;
        double y = 0;
        int line = 0;
        int shown = 0;
        double legendTop = Bounds.Height - 6 - (MaxLegendLines * LegendLine);
        for (int category = 0; category < series.Categories.Count; category++)
        {
            string label = $"{series.Categories[category].Name} {CategoryGraph.SharePercent(series.Counts[category], series.Total)}";
            double width = 16 + Text(label, FontSize, Brushes.Black).Width + 14;
            if (x + width > right && x > 12)
            {
                line++;
                x = 12;
            }

            if (line >= MaxLegendLines)
            {
                break;
            }

            legend.Add((new Rect(x, y + (line * LegendLine), width, LegendLine), category));
            x += width;
            shown = line + 1;
        }

        double legendHeight = (Math.Max(1, shown) * LegendLine) + 8;
        double top = 22;
        double bottom = Math.Max(top + 20, Bounds.Height - legendHeight - 24);
        double legendOrigin = bottom + 24;
        var placed = legend.Select(l => (new Rect(l.Item1.X, legendOrigin + l.Item1.Y, l.Item1.Width, l.Item1.Height), l.Item2)).ToList();
        var plot = new Rect(left, top, Math.Max(10, right - left), Math.Max(10, bottom - top));
        double rowHeight = graph.View == CategoryView.Frames ? Math.Clamp((plot.Height - ((graph.Rows - 1) * StripGap) - 8) / graph.Rows, 6, 64) : plot.Height;
        _ = legendTop;
        return new CategoryLayout(plot, rowHeight, graph.View == CategoryView.Frames ? graph.Rows : 1, placed, legendOrigin + legendHeight);
    }

    private double RowTop(CategoryLayout layout, int row) => layout.Plot.Top + 8 + (row * (layout.RowHeight + StripGap));

    private CategoryHit? CategoryHitAt(Point point, bool withText = true)
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

        var category = (CategoryGraph)graph;
        CategoryLayout layout = LayoutOfCategory(category);
        foreach ((Rect rect, int index) in layout.Legend)
        {
            if (rect.Contains(point))
            {
                return new CategoryHit(-1, withText ? CategoryGraph.DescribeCategory(category.Series, index) : string.Empty, rect, IsLegend: true, Category: index);
            }
        }

        Rect plot = layout.Plot;
        if (point.X < plot.Left || point.X >= plot.Right)
        {
            return null;
        }

        int width = Math.Max(1, (int)plot.Width);
        if (category.View == CategoryView.OverTime)
        {
            if (point.Y < plot.Top || point.Y > plot.Bottom)
            {
                return null;
            }

            CategoryColumns columns = ColumnsFor(category, width, -1);
            int column = Math.Clamp((int)((point.X - plot.Left) * columns.Count / plot.Width), 0, columns.Count - 1);
            return OverTimeHit(category, columns, column, plot, withText);
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
            return StripHit(category, columns, column, lane, plot, top, layout.RowHeight, withText);
        }

        return null;
    }

    private CategoryHit OverTimeHit(CategoryGraph graph, CategoryColumns columns, int column, Rect plot, bool withText = true)
    {
        double colW = plot.Width / columns.Count;
        var area = new Rect(plot.Left + (column * colW), plot.Top, Math.Max(1, colW), plot.Height);
        int first = columns.First(column);
        int end = columns.End(column);
        int filled = graph.FirstFilledInterval(first, end);
        return new CategoryHit(filled >= 0 ? filled : first, withText ? graph.DescribeIntervals(first, end) : string.Empty, area);
    }

    private CategoryHit StripHit(CategoryGraph graph, CategoryColumns columns, int column, int lane, Rect plot, double top, double rowHeight, bool withText = true)
    {
        double colW = plot.Width / columns.Count;
        var area = new Rect(plot.Left + (column * colW), top, Math.Max(1, colW), rowHeight);
        int first = columns.First(column);
        int end = columns.End(column);
        return new CategoryHit(graph.UnitOfColumn(first, end, lane), withText ? graph.DescribeUnits(first, end, lane) : string.Empty, area, lane);
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

        var category = (CategoryGraph)graph;
        CategoryLayout layout = LayoutOfCategory(category);
        int width = Math.Max(1, (int)layout.Plot.Width);
        if (category.View == CategoryView.OverTime)
        {
            CategoryColumns columns = ColumnsFor(category, width, -1);
            int column = ColumnOf(columns, index);
            CategoryHit hit = OverTimeHit(category, columns, column, layout.Plot);
            return hit with { Index = index, Text = category.Describe(index) };
        }

        int row2 = Math.Clamp(_hoverLane, 0, layout.Rows - 1);
        int lane = layout.Rows > 1 ? row2 : -1;
        CategoryColumns strip = ColumnsFor(category, width, lane);
        int cell = ColumnOf(strip, index);
        CategoryHit stripHit = StripHit(category, strip, cell, lane, layout.Plot, RowTop(layout, layout.Rows > 1 ? row2 : 0), layout.RowHeight);
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

    private void RenderCategory(DrawingContext context, Graph graph)
    {
        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        Color surface = Palette.SurfaceColor(this);
        IBrush secondary = Palette.Secondary(this);
        IBrush foreground = Palette.Foreground(this);
        if (IsSummary(graph))
        {
            DrawSummary(context, (ISummaryGraph)graph, graph, isDark, surface, secondary, foreground);
            if (_hover is { } row && _hoverText is { } text)
            {
                DrawTooltip(context, text, new Point(_hoverArea.Center.X, _hoverArea.Y), new Rect(0, 0, Bounds.Width, Bounds.Height));
            }

            return;
        }

        var category = (CategoryGraph)graph;
        CategorySeries series = category.Series;
        CategoryStyle[] styles = CategoryColors.For(series, isDark, surface);
        IBrush[] brushes = [.. styles.Select(s => (IBrush)new ImmutableSolidColorBrush(s.Color))];
        bool highContrast = HasHighContrast();
        CategoryLayout layout = LayoutOfCategory(category);
        Rect plot = layout.Plot;
        IPen grid = new Pen(Palette.Grid(this), 1);
        int width = Math.Max(1, (int)plot.Width);
        var heights = new double[series.Categories.Count];

        if (category.View == CategoryView.OverTime)
        {
            context.DrawText(Text("share of values", FontSize, secondary), new Point(4, 2));
            foreach (int percent in new[] { 0, 25, 50, 75, 100 })
            {
                double y = Math.Round(plot.Bottom - (plot.Height * percent / 100.0)) + 0.5;
                context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
                FormattedText label = Text(percent + "%", FontSize, secondary);
                context.DrawText(label, new Point(plot.Left - 8 - label.Width, y - (label.Height / 2)));
            }

            CategoryColumns columns = ColumnsFor(category, width, -1);
            double colW = plot.Width / columns.Count;
            for (int column = 0; column < columns.Count; column++)
            {
                if (columns.Total(column) == 0)
                {
                    continue;
                }

                DrawStack(context, columns.Counts(column), plot.Left + (column * colW), colW, plot.Top, plot.Height, styles, brushes, heights, surface, highContrast, series);
            }

            DrawTimeAxis(context, category, columns, plot, secondary);
            if (MarkedIndex is { } marked && marked < category.Count)
            {
                double x = Math.Round(plot.Left + ((ColumnOf(columns, marked) + 0.5) * colW)) + 0.5;
                context.DrawLine(new Pen(foreground, 1.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
        else
        {
            context.DrawText(Text(series.ValueLabel + (layout.Rows > 1 ? "s per frame" : " of each frame"), FontSize, secondary), new Point(4, 2));
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
                    FormattedText name = Fit(series.ValueNames[row], FontSize, secondary, plot.Left - 20);
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

        DrawLegend(context, category, layout, brushes, styles, secondary, surface);

        if (_hover is { } hover && _hoverText is { } hoverText)
        {
            context.DrawRectangle(null, new Pen(foreground, 1), _hoverArea.Inflate(0.5));
            DrawTooltip(context, hoverText, new Point(_hoverArea.Center.X, _hoverArea.Y), plot);
            _ = hover;
        }
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
                FormattedText name = Fit(series.Categories[category].Name, FontSize, new ImmutableSolidColorBrush(Readable(styles[category].Color)), width - 8);
                context.DrawText(name, new Point(x + 4, y + ((h - name.Height) / 2)));
            }

            previous = h;
        }
    }

    private static Color Readable(Color background)
    {
        double luminance = ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255;
        return luminance > 0.6 ? Colors.Black : Colors.White;
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

    private void DrawLegend(DrawingContext context, CategoryGraph graph, CategoryLayout layout, IBrush[] brushes, CategoryStyle[] styles, IBrush secondary, Color surface)
    {
        CategorySeries series = graph.Series;
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
            FormattedText text = Text(label, FontSize, series.Counts[category] > 0 ? foreground : secondary);
            context.DrawText(text, new Point(rect.X + 16, rect.Y + ((rect.Height - text.Height) / 2)));
        }

        if (layout.Legend.Count < series.Categories.Count)
        {
            Rect last = layout.Legend[^1].Rect;
            string more = $"+{series.Categories.Count - layout.Legend.Count} more in the Summary";
            context.DrawText(Text(more, FontSize, secondary), new Point(last.Right, last.Y + 2));
        }
    }

    private void DrawTimeAxis(DrawingContext context, CategoryGraph graph, CategoryColumns columns, Rect plot, IBrush secondary)
    {
        ReadOnlySpan<double> starts = graph.Series.Time!.Starts.Span;
        double colW = plot.Width / columns.Count;
        double lastEnd = double.NegativeInfinity;
        int step = Math.Max(1, (int)Math.Ceiling(90 / colW));
        for (int column = 0; column < columns.Count; column += step)
        {
            FormattedText label = Text(Format.Clock(starts[columns.First(column)]), FontSize, secondary);
            double x = plot.Left + (column * colW);
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
        FormattedText caption = Text("frames", FontSize, secondary);
        double limit = plot.Right - (caption.Width + 12);
        for (long position = 0; position <= count; position += step)
        {
            FormattedText label = Text(position.ToString("N0", CultureInfo.InvariantCulture), FontSize, secondary);
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
        context.DrawText(Text("count and share of the values of the whole stream", FontSize, secondary), new Point(4, 2));
        IBrush trackBrush = Palette.Grid(this, 0.1);
        bool highContrast = HasHighContrast();
        using (context.PushClip(clip))
        using (context.PushTransform(Matrix.CreateTranslation(0, 24 - _scroll)))
        {
            CategoryStyle[][] styles = [.. summary.Tables.Select(t => CategoryColors.For(t, isDark, surface))];
            foreach (SummaryTable header in layout.Tables)
            {
                CategorySeries series = summary.Tables[header.Table];
                FormattedText title = Text(series.Name, 13, foreground);
                title.SetFontWeight(FontWeight.SemiBold);
                context.DrawText(title, new Point(header.Header.X, header.Header.Y + 2));
                string totals = Format.Count(series.Total, "value", "values") + " · " + series.ValueLabel;
                FormattedText sub = Fit(totals, FontSize, secondary, header.Header.Width - title.Width - 12);
                context.DrawText(sub, new Point(header.Header.Right - sub.Width, header.Header.Y + 4));
                context.DrawLine(new Pen(Palette.Grid(this, 0.25), 1), new Point(header.Header.X, header.Header.Bottom - 2), new Point(header.Header.Right, header.Header.Bottom - 2));
                if (series.UncodedValues > 0)
                {
                    string note = $"{Format.Count(series.UncodedValues, "value", "values")} counted without a code, not drawn per frame";
                    context.DrawText(Fit(note, FontSize, secondary, header.Header.Width), new Point(header.Header.X, header.Header.Bottom));
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
                const double shareWidth = 52;
                const double countWidth = 88;
                double right = rect.Right;
                var track = new Rect(right - barWidth, rect.Y + ((rect.Height - 8) / 2), barWidth, 8);
                context.FillRectangle(trackBrush, track, 2f);
                if (count > 0)
                {
                    context.FillRectangle(new ImmutableSolidColorBrush(style.Color), new Rect(track.X, track.Y, Math.Max(2, barWidth * share), 8), 2f);
                }

                FormattedText percent = Text(CategoryGraph.SharePercent(count, series.Total), FontSize, foreground);
                context.DrawText(percent, new Point(track.X - 8 - percent.Width, rect.Y + ((rect.Height - percent.Height) / 2)));
                FormattedText number = Text(count.ToString("N0", CultureInfo.InvariantCulture), FontSize, count > 0 ? foreground : secondary);
                double countRight = track.X - 8 - shareWidth;
                context.DrawText(number, new Point(countRight - number.Width, rect.Y + ((rect.Height - number.Height) / 2)));
                FormattedText name = Fit(series.Categories[row.Category].Name, FontSize, count > 0 ? foreground : secondary, Math.Max(40, countRight - countWidth - rect.X - 20));
                context.DrawText(name, new Point(rect.X + 18, rect.Y + ((rect.Height - name.Height) / 2)));
            }
        }
    }

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
