using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// Draws one graph: a line, decimated to the smallest and largest value of each pixel column so a
// stream of any length draws in as many segments as the chart is wide, or bars. Hovering a point
// shows its value; clicking it raises PointClicked, which selects its frame.
public sealed partial class GraphView : Control
{
    public static readonly StyledProperty<Graph?> GraphProperty =
        AvaloniaProperty.Register<GraphView, Graph?>(nameof(Graph));

    public static readonly StyledProperty<int?> MarkedIndexProperty =
        AvaloniaProperty.Register<GraphView, int?>(nameof(MarkedIndex));

    // The view of a category series, bound so that choosing another one draws again.
    public static readonly StyledProperty<CategoryView?> ViewProperty =
        AvaloniaProperty.Register<GraphView, CategoryView?>(nameof(View));

    private const double FontSize = 11.5;
    private const double TooltipSize = 12.5;
    private const double MaxBarWidth = 24;

    // Seconds between time labels: whole minutes and hours once they get long.
    private static readonly double[] TimeSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 18000, 36000];

    private int? _hover;
    private string? _hoverText;
    private GraphColumn[]? _columns;
    private Graph? _columnsGraph;
    private int _columnsWidth;

    static GraphView()
    {
        AffectsRender<GraphView>(GraphProperty, MarkedIndexProperty, ViewProperty);
        FocusableProperty.OverrideDefaultValue<GraphView>(true);
    }

    public GraphView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public event EventHandler<int>? PointClicked;

    // A click on a legend entry of a category chart, with the category.
    public event EventHandler<int>? CategoryClicked;

    public Graph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public CategoryView? View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public int? MarkedIndex
    {
        get => GetValue(MarkedIndexProperty);
        set => SetValue(MarkedIndexProperty, value);
    }

    // The point under the pointer and what it shows; exposed for tests.
    public int? HoverIndex => _hover;

    public string? HoverText => _hoverText ?? (_hover is { } index && Graph is { } graph && index < graph.Count ? graph.Describe(index) : null);

    // The point under a position in the control, or null outside the plot. On a line of more points
    // than pixels, the one in that pixel column whose value is nearest the position's height.
    public int? IndexAt(Point point)
    {
        if (Graph is not { Count: > 0 } graph)
        {
            return null;
        }

        if (IsCategory(graph))
        {
            return CategoryHitAt(point, withText: false) is { Index: >= 0 } hit ? hit.Index : null;
        }

        Layout layout = LayoutFor(graph);
        Rect plot = layout.Plot;
        if (point.X < plot.Left || point.X >= plot.Right || point.Y < plot.Top - 8 || point.Y > plot.Bottom + 8)
        {
            return null;
        }

        int count = graph.Count;
        double fraction = (point.X - plot.Left) / plot.Width;
        if (graph is not LineGraph line || count <= (int)plot.Width)
        {
            return Math.Clamp((int)(fraction * count), 0, count - 1);
        }

        GraphColumn column = ColumnsFor(line, plot)[Math.Clamp((int)(point.X - plot.Left), 0, (int)plot.Width - 1)];
        double target = layout.ValueAt(point.Y);
        ReadOnlySpan<double> values = line.Series.Values.Span;
        int best = column.First;
        for (int i = column.First; i < column.End; i++)
        {
            if (Math.Abs(values[i] - target) < Math.Abs(values[best] - target))
            {
                best = i;
            }
        }

        return best;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Graph is not { Count: > 0 } graph || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return;
        }

        if (IsCategory(graph))
        {
            RenderCategory(context, graph);
            return;
        }

        Layout layout = LayoutFor(graph);
        Rect plot = layout.Plot;
        IBrush secondary = Palette.Secondary(this);
        IPen grid = new Pen(Palette.Grid(this), 1);
        Color accent = Palette.Accent(this);

        // The y axis: hairline grid at each tick, the values to the left and the unit on top.
        foreach (double tick in layout.Ticks)
        {
            double y = Math.Round(layout.Y(tick)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = Text(Graph.FormatValue(tick, string.Empty), FontSize, secondary);
            context.DrawText(label, new Point(plot.Left - 8 - label.Width, y - (label.Height / 2)));
        }

        // A distribution counts frames up and its values run along the bottom.
        string yUnit = graph is DistributionGraph ? "frames" : graph.Unit;
        if (yUnit.Length > 0)
        {
            context.DrawText(Text(yUnit, FontSize, secondary), new Point(4, 2));
        }

        if (graph is LineGraph line)
        {
            DrawLine(context, line, layout, accent);
            DrawLineAxis(context, line, plot, secondary);
        }
        else if (graph is DistributionGraph bars)
        {
            DrawBars(context, bars, layout, accent, secondary);
        }

        if (_hover is { } hover && hover < graph.Count)
        {
            Point at = PointOf(graph, layout, hover);
            if (graph is LineGraph)
            {
                context.DrawLine(new Pen(Palette.Grid(this, 0.45), 1), new Point(Math.Round(at.X) + 0.5, plot.Top), new Point(Math.Round(at.X) + 0.5, plot.Bottom));
                context.DrawEllipse(new ImmutableSolidColorBrush(accent), new Pen(Palette.Surface(this), 2), at, 4.5, 4.5);
            }

            DrawTooltip(context, graph.Describe(hover), at, plot);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty || change.Property == ViewProperty)
        {
            _hover = null;
            _hoverText = null;
            _columns = null;
            _scroll = 0;
            _categoryColumns.Clear();
            AutomationProperties.SetName(this, Graph is { } graph ? graph.Title : null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Graph is { Count: > 0 } graph && IsCategory(graph))
        {
            SetCategoryHit(CategoryHitAt(e.GetPosition(this)));
            return;
        }

        SetHover(IndexAt(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
        _hoverText = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        if (Graph is { Count: > 0 } graph && IsCategory(graph))
        {
            CategoryHit? hit = CategoryHitAt(e.GetPosition(this));
            SetCategoryHit(hit);
            if (hit is { IsLegend: true } legend)
            {
                CategoryClicked?.Invoke(this, legend.Category);
                e.Handled = true;
            }
            else if (hit is { Index: >= 0 } point)
            {
                PointClicked?.Invoke(this, point.Index);
                e.Handled = true;
            }

            return;
        }

        if (IndexAt(e.GetPosition(this)) is { } index)
        {
            SetHover(index);
            PointClicked?.Invoke(this, index);
            e.Handled = true;
        }
    }

    private static int NiceStep(double rough)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(rough, 1))));
        double fraction = rough / magnitude;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return (int)Math.Max(1, nice * magnitude);
    }

    private static int NiceTimeStep(double rough)
    {
        foreach (double step in TimeSteps)
        {
            if (step >= rough)
            {
                return (int)step;
            }
        }

        return (int)(Math.Ceiling(rough / TimeSteps[^1]) * TimeSteps[^1]);
    }

    private void SetHover(int? index)
    {
        if (_hover != index || _hoverText is not null)
        {
            _hover = index;
            _hoverText = null;
            InvalidateVisual();
        }
    }

    private Layout LayoutFor(Graph graph)
    {
        // Ticks at round values from 0 past the largest value, about one every 40 pixels.
        double maximum = graph.Maximum > 0 ? graph.Maximum : 1;
        double top = 22;
        double bottom = Bounds.Height - 22;
        int wanted = Math.Clamp((int)((bottom - top) / 40), 2, 6);
        double step = NiceDecimalStep(maximum / wanted);
        double axisMax = Math.Ceiling(maximum / step) * step;
        var ticks = new List<double>();
        for (double tick = 0; tick <= axisMax + (step / 2); tick += step)
        {
            ticks.Add(tick);
        }

        double labelWidth = ticks.Max(t => Text(Graph.FormatValue(t, string.Empty), FontSize, Brushes.Black).Width);
        double left = 12 + labelWidth + 8;
        var plot = new Rect(left, top, Math.Max(10, Bounds.Width - left - 16), Math.Max(10, bottom - top));
        return new Layout(plot, axisMax, ticks);
    }

    private static double NiceDecimalStep(double rough)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double fraction = rough / magnitude;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private GraphColumn[] ColumnsFor(LineGraph line, Rect plot)
    {
        int width = Math.Max(1, (int)plot.Width);
        if (_columns is null || _columnsGraph != line || _columnsWidth != width)
        {
            _columns = GraphColumns.Decimate(line.Series.Values.Span, width);
            _columnsGraph = line;
            _columnsWidth = width;
        }

        return _columns;
    }

    private Point PointOf(Graph graph, Layout layout, int index)
    {
        Rect plot = layout.Plot;
        double x = plot.Left + ((index + 0.5) * plot.Width / graph.Count);
        double value = graph.ValueAt(index);
        return new Point(x, layout.Y(double.IsFinite(value) ? value : 0));
    }

    private void DrawLine(DrawingContext context, LineGraph line, Layout layout, Color accent)
    {
        Rect plot = layout.Plot;
        int count = line.Count;
        var points = new List<Point>();
        if (count <= (int)plot.Width)
        {
            // Steps: each value holds for its whole frame or second.
            for (int i = 0; i < count; i++)
            {
                double value = line.ValueAt(i);
                double y = layout.Y(double.IsFinite(value) ? value : 0);
                points.Add(new Point(plot.Left + (i * plot.Width / count), y));
                points.Add(new Point(plot.Left + ((i + 1) * plot.Width / count), y));
            }
        }
        else
        {
            // Each column from its smallest to its largest value, in the order they come, so the
            // line runs on from one column to the next as the values do.
            foreach ((GraphColumn column, int c) in ColumnsFor(line, plot).Select((column, c) => (column, c)))
            {
                double x = plot.Left + c + 0.5;
                (double first, double second) = column.MinIndex <= column.MaxIndex ? (column.Min, column.Max) : (column.Max, column.Min);
                points.Add(new Point(x, layout.Y(first)));
                points.Add(new Point(x, layout.Y(second)));
            }
        }

        if (MarkedIndex is { } marked && marked < count)
        {
            double x = Math.Round(plot.Left + ((marked + 0.5) * plot.Width / count)) + 0.5;
            context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(accent, 0.7), 1.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        var area = new StreamGeometry();
        using (StreamGeometryContext geometry = area.Open())
        {
            geometry.BeginFigure(new Point(points[0].X, plot.Bottom), true);
            foreach (Point point in points)
            {
                geometry.LineTo(point);
            }

            geometry.LineTo(new Point(points[^1].X, plot.Bottom));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(new ImmutableSolidColorBrush(accent, 0.12), null, area);

        var stroke = new StreamGeometry();
        using (StreamGeometryContext geometry = stroke.Open())
        {
            geometry.BeginFigure(points[0], false);
            foreach (Point point in points.Skip(1))
            {
                geometry.LineTo(point);
            }

            geometry.EndFigure(false);
        }

        context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 1.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), stroke);
    }

    // Labels under the x axis about every 90 pixels: frame counts, or times for a series over
    // seconds, with "frames" at the end of a frame axis.
    private void DrawLineAxis(DrawingContext context, LineGraph line, Rect plot, IBrush secondary)
    {
        int count = line.Count;
        double perLabel = count * 90 / plot.Width;
        int step = line.Axis == Core.SeriesAxis.Seconds ? NiceTimeStep(perLabel) : NiceStep(perLabel);
        double y = plot.Bottom + 4;
        double lastEnd = double.NegativeInfinity;
        FormattedText? caption = line.Axis == Core.SeriesAxis.FrameOffset ? Text("frames", FontSize, secondary) : null;
        double limit = plot.Right - (caption is null ? 0 : caption.Width + 12);
        for (long position = 0; position <= count; position += step)
        {
            FormattedText label = Text(line.AxisLabel(position), FontSize, secondary);
            double x = plot.Left + (position * plot.Width / count);
            double start = Math.Clamp(x - (label.Width / 2), plot.Left - 4, plot.Right - label.Width);
            if (start < lastEnd + 12 || start + label.Width > limit)
            {
                continue;
            }

            context.DrawText(label, new Point(start, y));
            lastEnd = start + label.Width;
        }

        if (caption is not null)
        {
            context.DrawText(caption, new Point(plot.Right - caption.Width, y));
        }
    }

    private void DrawBars(DrawingContext context, DistributionGraph bars, Layout layout, Color accent, IBrush secondary)
    {
        Rect plot = layout.Plot;
        int count = bars.Count;
        double slot = plot.Width / count;
        double width = Math.Clamp(slot - 2, 1, MaxBarWidth);
        // Opaque, so the rounded top and the square body of a bar do not show where they overlap.
        var fill = new ImmutableSolidColorBrush(Mix(accent, Palette.SurfaceColor(this), 0.82));
        var hovered = new ImmutableSolidColorBrush(accent);
        double lastEnd = double.NegativeInfinity;
        FormattedText? caption = bars.Unit.Length > 0 ? Text(bars.Unit, FontSize, secondary) : null;
        double limit = plot.Right - (caption is null ? 0 : caption.Width + 12);
        for (int i = 0; i < count; i++)
        {
            double center = plot.Left + ((i + 0.5) * slot);
            double top = layout.Y(bars.ValueAt(i));
            double height = plot.Bottom - top;
            if (height > 0)
            {
                // Rounded at the top, square on the baseline.
                double radius = Math.Min(4, Math.Min(width / 2, height));
                IBrush brush = _hover == i ? hovered : fill;
                context.DrawRectangle(brush, null, new Rect(center - (width / 2), top, width, height), radius, radius);
                context.FillRectangle(brush, new Rect(center - (width / 2), top + radius, width, height - radius));
            }

            if (MarkedIndex == i)
            {
                context.FillRectangle(hovered, new Rect(center - (width / 2), plot.Bottom + 2, width, 3), 1.5f);
            }

            FormattedText label = Text(bars.Label(i), FontSize, secondary);
            double start = center - (label.Width / 2);
            if (start >= lastEnd + 6 && start + label.Width <= limit)
            {
                context.DrawText(label, new Point(start, plot.Bottom + 6));
                lastEnd = start + label.Width;
            }
        }

        if (caption is not null)
        {
            context.DrawText(caption, new Point(plot.Right - caption.Width, plot.Bottom + 6));
        }
    }

    // The value in a box beside the point, on whichever side has room.
    private void DrawTooltip(DrawingContext context, string text, Point at, Rect plot)
    {
        FormattedText label = Text(text, TooltipSize, Palette.Foreground(this));
        label.MaxTextWidth = Math.Max(160, Bounds.Width - 40);
        var size = new Size(label.Width + 16, label.Height + 8);
        double x = at.X + 12 + size.Width <= Bounds.Width - 2 ? at.X + 12 : at.X - 12 - size.Width;
        double y = Math.Clamp(at.Y - size.Height - 8, 2, Math.Max(2, plot.Bottom - size.Height));
        var box = new Rect(Math.Max(2, x), y, size.Width, size.Height);
        context.DrawRectangle(Palette.TooltipBackground(this), new Pen(Palette.Grid(this, 0.3), 1), box, 4, 4);
        context.DrawText(label, new Point(box.X + 8, box.Y + 4));
    }

    private static Color Mix(Color color, Color background, double amount) => Color.FromRgb(
        (byte)Math.Round((color.R * amount) + (background.R * (1 - amount))),
        (byte)Math.Round((color.G * amount) + (background.G * (1 - amount))),
        (byte)Math.Round((color.B * amount) + (background.B * (1 - amount))));

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private readonly record struct Layout(Rect Plot, double AxisMax, IReadOnlyList<double> Ticks)
    {
        public double Y(double value) => Plot.Bottom - (Math.Clamp(value, 0, AxisMax) / AxisMax * Plot.Height);

        public double ValueAt(double y) => (Plot.Bottom - y) / Plot.Height * AxisMax;
    }
}
