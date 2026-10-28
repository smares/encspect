using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The Loudness panel: the short-term loudness as a line over time (the points placed by their time, so a gap in the stream shows as a gap), the integrated loudness as a
// dashed line, and the loudness figures in a header. Kept apart from the other audio panels' drawing.
public sealed partial class GraphView
{
    // The height of the header line above the plot.
    private const double LoudnessHeaderHeight = 16;

    private static Point LoudnessDot(LoudnessGraph graph, Rect plot, int index)
    {
        double range = graph.AxisTop - graph.AxisBottom;
        double value = graph.ValueAt(index);
        double y = plot.Bottom - (double.IsNaN(value) ? 0 : Math.Clamp(value - graph.AxisBottom, 0, range) / range * plot.Height);
        double fraction = graph.FractionAt(index);
        return new Point(plot.Left + ((double.IsNaN(fraction) ? 0 : fraction) * plot.Width), y);
    }

    // Where the line of the loudness is drawn at a point, in the view's coordinates, for tests; null for any other graph.
    public Point? LoudnessDotOf(int index) => Graph is LoudnessGraph graph && index >= 0 && index < graph.Count && Bounds.Width >= 80 && Bounds.Height >= 60 ? LoudnessDot(graph, AudioLayoutFor(graph).Plot, index) : null;

    private void DrawLoudness(DrawingContext context, LoudnessGraph graph, AudioLayout layout, IBrush secondary, IPen grid, Color accent)
    {
        Rect plot = layout.Plot;
        context.DrawText(Text("LUFS", TextSize, secondary), new Point(4, 2));
        double range = graph.AxisTop - graph.AxisBottom;
        double Y(double lufs) => plot.Bottom - (Math.Clamp(lufs - graph.AxisBottom, 0, range) / range * plot.Height);
        foreach (double tick in layout.Ticks)
        {
            double y = Math.Round(Y(tick)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = Text(DbLabel(tick), AxisSize, secondary);
            context.DrawText(label, new Point(plot.Left - AxisLabelGap - label.Width, y - (label.Height / 2)));
        }

        DrawTimeAxis(context, graph.StartSeconds, graph.EndSeconds, plot, secondary);

        // The header: every figure when the width allows, else the integrated loudness and the range.
        FormattedText header = Text(graph.Header(isShort: false), AxisSize, Palette.Foreground(this));
        if (header.Width > Bounds.Width - 24)
        {
            header = Text(graph.Header(isShort: true), AxisSize, Palette.Foreground(this));
        }

        context.DrawText(header, new Point(plot.Left, 22));

        var pen = new ImmutablePen(new ImmutableSolidColorBrush(accent), 1.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        var path = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext geometry = path.Open())
        using (StreamGeometryContext fill = area.Open())
        {
            for (int i = 0; i < graph.Count; i++)
            {
                Point point = LoudnessDot(graph, plot, i);
                if (i == 0)
                {
                    geometry.BeginFigure(point, false);
                    fill.BeginFigure(new Point(point.X, plot.Bottom), true);
                }
                else
                {
                    geometry.LineTo(point);
                }

                fill.LineTo(point);
            }

            geometry.EndFigure(false);
            fill.LineTo(new Point(LoudnessDot(graph, plot, graph.Count - 1).X, plot.Bottom));
            fill.EndFigure(true);
        }

        context.DrawGeometry(new ImmutableSolidColorBrush(accent, 0.12), null, area);
        context.DrawGeometry(null, pen, path);

        if (graph.IntegratedTenths is { } integrated)
        {
            double y = Math.Round(Y(integrated / 10.0)) + 0.5;
            context.DrawLine(new Pen(Palette.Foreground(this), 1.25, new DashStyle([4, 3], 0)), new Point(plot.Left, y), new Point(plot.Right, y));
            DrawChipLabel(context, "Integrated " + Encspect.Host.MeasurementText.Tenths(integrated) + " LUFS", new Point(plot.Right - 4, y), true);
        }
    }
}
