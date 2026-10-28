using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Encspect.Core;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The Audio panels, drawn from a stream's measurements: the waveform in a lane for each channel, the spectrogram as the grid's bytes
// through the magma ramp, and the average spectrum as a line over frequency. The spectral cutoff is a line on the spectrogram and the
// spectrum; every figure is in the hover text too, and the automation text says what the picture shows.
public sealed partial class GraphView
{
    // The displayed range of the spectrogram's levels: 0 dB at the top of the ramp, -160 dB at the bottom. A byte of the grid is the level in
    // dB plus 200 (1 to 220), so this is bytes 40 to 200; below the range is the ramp's darkest entry, and 0 is no data.
    internal const int RampTopDb = 0;
    internal const int RampBottomDb = -160;

    private const double AxisLabelGap = 8;
    private const double ColorBarWidth = 10;
    internal const double GuideHaloWidth = 5;
    private const double ColorBarLabels = 40;

    // The spectrogram's bytes to colors, as premultiplied BGRA: one entry for each byte of the grid, the fixed ramp indexed by level.
    private static readonly int[] RampColors = BuildRampColors();

    private int _audioRow = -1;

    // Fewer columns than pixels are drawn as blocks: smoothing would fade a column that holds data into one that holds none. The mode is the
    // view's (a mode pushed on the drawing context for one image is not honored by the renderer), so it follows the graph and the size.
    private void UpdateInterpolation()
    {
        bool isBlocks = Graph is SpectrogramGraph graph && Bounds.Width >= 80 && Bounds.Height >= 60 && graph.Columns < AudioLayoutFor(graph).Plot.Width;
        RenderOptions.SetBitmapInterpolationMode(this, isBlocks ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
    }
    private WriteableBitmap? _spectrogramBitmap;
    private SpectrogramGraph? _spectrogramBitmapGraph;

    // The color of a grid byte as the ramp gives it (0 is transparent); exposed for tests.
    internal static Color RampColor(byte cell)
    {
        int argb = RampColors[cell];
        return Color.FromArgb((byte)(argb >>> 24), (byte)(argb >>> 16), (byte)(argb >>> 8), (byte)argb);
    }

    // The ramp entry (0 to 255) a level in dB takes.
    internal static int RampIndex(int decibels) => (int)Math.Round(Math.Clamp((decibels - RampBottomDb) / (double)(RampTopDb - RampBottomDb), 0, 1) * (MagmaRamp.Entries - 1));

    private static int[] BuildRampColors()
    {
        ReadOnlySpan<byte> rgb = MagmaRamp.Rgb;
        var colors = new int[256];
        for (int cell = 1; cell < colors.Length; cell++)
        {
            int index = RampIndex(cell - MeasurementLimits.GridOffset);
            colors[cell] = unchecked((int)0xFF000000u | (rgb[index * 3] << 16) | (rgb[(index * 3) + 1] << 8) | rgb[(index * 3) + 2]);
        }

        return colors;
    }

    private static bool IsAudio(Graph graph) => graph is AudioGraph and not FiguresGraph;

    // The row of the cell the pointer or the cursor is on in the spectrogram, or -1; exposed for tests.
    public int HoverRow => _audioRow;

    // Where the audio panel puts its plot, in the view's coordinates, for tests and the pointer; null for any other graph.
    public Rect? AudioPlotArea() => Graph is { } graph && IsAudio(graph) && Bounds.Width >= 80 && Bounds.Height >= 60 ? AudioLayoutFor(graph).Plot : null;

    // The lanes of a waveform, top to bottom, in the view's coordinates (one for any other audio panel); for tests.
    public IReadOnlyList<Rect>? WaveformLanes() => Graph is WaveformGraph waveform && Bounds.Width >= 80 && Bounds.Height >= 60 ? Lanes(waveform, AudioLayoutFor(waveform).Plot) : null;

    // The cell of the spectrogram under a position (column, row), or null outside the plot.
    public (int Column, int Row)? CellAt(Point point)
    {
        if (Graph is not SpectrogramGraph graph || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        Rect plot = AudioLayoutFor(graph).Plot;
        if (!plot.Contains(point))
        {
            return null;
        }

        int column = Math.Clamp((int)((point.X - plot.Left) / plot.Width * graph.Columns), 0, graph.Columns - 1);
        int row = Math.Clamp((int)((plot.Bottom - point.Y) / plot.Height * graph.Rows), 0, graph.Rows - 1);
        return (column, row);
    }

    // The point in the view where a cell of the spectrogram, a column of the waveform or a row of the spectrum is drawn, for tests.
    public Point? PointOfCell(int index, int row = -1)
    {
        if (Graph is not AudioGraph graph || !IsAudio(graph))
        {
            return null;
        }

        Rect plot = AudioLayoutFor(graph).Plot;
        return graph switch
        {
            SpectrogramGraph spectrogram => new Point(plot.Left + ((index + 0.5) * plot.Width / spectrogram.Columns), plot.Bottom - ((row + 0.5) * plot.Height / spectrogram.Rows)),
            WaveformGraph waveform => new Point(plot.Left + ((index + 0.5) * plot.Width / waveform.Columns), plot.Top + (plot.Height / 2)),
            SpectrumGraph spectrum => new Point(plot.Left + ((index + 0.5) * plot.Width / spectrum.Count), plot.Bottom - (plot.Height / 2)),
            _ => null,
        };
    }

    private int? AudioIndexAt(Point point)
    {
        if (Graph is not AudioGraph graph || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return null;
        }

        Rect plot = AudioLayoutFor(graph).Plot;
        if (point.X < plot.Left || point.X >= plot.Right || point.Y < plot.Top || point.Y > plot.Bottom)
        {
            return null;
        }

        return Math.Clamp((int)((point.X - plot.Left) / plot.Width * graph.Count), 0, graph.Count - 1);
    }

    // Hover for the audio panels: the column (waveform), the cell (spectrogram) or the row (spectrum) under the pointer.
    private void AudioPointerMoved(AudioGraph graph, Point point)
    {
        if (graph is SpectrogramGraph && CellAt(point) is { } cell)
        {
            SetCell(cell.Column, cell.Row);
            return;
        }

        SetAudioHover(AudioIndexAt(point), -1);
    }

    private void SetAudioHover(int? index, int row)
    {
        string? text = null;
        if (index is { } at && Graph is SpectrogramGraph spectrogram && row >= 0)
        {
            text = spectrogram.DescribeCell(at, row);
        }

        if (_hover != index || _audioRow != row || _hoverText != text)
        {
            _hover = index;
            _audioRow = row;
            _hoverText = text;
            if (Graph is { } graph)
            {
                UpdateAutomation(graph);
            }

            InvalidateVisual();
        }
    }

    private void SetCell(int column, int row) => SetAudioHover(column, row);

    // The keys of the audio panels beyond the other graphs': Up and Down move the cursor of the spectrogram from row to row (Ctrl by eight),
    // Left and Right from column to column (Ctrl by a pixel column's worth); Home and End go to the first and last column, Enter to the frame.
    private bool AudioKey(AudioGraph graph, KeyEventArgs e)
    {
        if (graph is not SpectrogramGraph spectrogram || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
        {
            return false;
        }

        int column = _hover ?? MarkedIndex ?? 0;
        int row = _audioRow >= 0 ? _audioRow : Math.Clamp(spectrogram.Cutoff is { } cutoff ? spectrogram.RowOfHertz(Math.Max(0, cutoff.Frequency - 1)) : spectrogram.Rows / 2, 0, spectrogram.Rows - 1);
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        int jump = Math.Max(1, spectrogram.Columns / Math.Max(1, (int)Bounds.Width));
        if (_hover is null && e.Key is Key.Left or Key.Right && MarkedIndex is null)
        {
            column = e.Key == Key.Left ? spectrogram.Columns - 1 : 0;
        }
        else
        {
            column = e.Key switch
            {
                Key.Left => column - (control ? jump : 1),
                Key.Right => column + (control ? jump : 1),
                Key.Home => 0,
                Key.End => spectrogram.Columns - 1,
                _ => column,
            };
            row += e.Key switch
            {
                Key.Up => control ? 8 : 1,
                Key.Down => control ? -8 : -1,
                _ => 0,
            };
        }

        SetCell(Math.Clamp(column, 0, spectrogram.Columns - 1), Math.Clamp(row, 0, spectrogram.Rows - 1));
        return true;
    }

    // The graph's name and what it shows in words, for the automation tree: the axes, the cutoff, and the cell or column the cursor is on.
    private string AudioHelp(AudioGraph graph)
    {
        string what = graph switch
        {
            WaveformGraph waveform => $"{Format.Count(waveform.Channels, "channel", "channels")} over {Format.Timestamp(waveform.EndSeconds - waveform.StartSeconds)}, in 16-bit sample codes",
            SpectrogramGraph spectrogram => $"Time from {Format.Timestamp(spectrogram.StartSeconds)} to {Format.Timestamp(spectrogram.EndSeconds)}, frequency from 0 Hz to {AudioGraph.Hertz(spectrogram.NyquistHertz)}, level from {RampBottomDb} to {RampTopDb} dB",
            SpectrumGraph spectrum => $"Level in dB from 0 Hz to {AudioGraph.Hertz(spectrum.NyquistHertz)}" + (spectrum.View == GraphForm.PerChannel ? "; Legend: " + string.Join(", ", Enumerable.Range(0, spectrum.Lines).Select(spectrum.LineName)) : string.Empty),
            _ => string.Empty,
        };
        return string.Join(". ", new[] { what, graph is WaveformGraph or SpectrogramGraph or SpectrumGraph ? graph.CutoffText ?? string.Empty : string.Empty, HoverText ?? string.Empty }.Where(t => t.Length > 0));
    }

    private void ResetAudio()
    {
        _audioRow = -1;
        _audioLayout = null;
        _spectrogramBitmap?.Dispose();
        _spectrogramBitmap = null;
        _spectrogramBitmapGraph = null;
    }

    // ---- Layout

    private readonly record struct AudioLayout(Rect Plot, IReadOnlyList<double> Ticks);

    private (Graph Graph, Size Size, AudioLayout Layout)? _audioLayout;

    private AudioLayout AudioLayoutFor(Graph graph)
    {
        if (_audioLayout is { } cached && ReferenceEquals(cached.Graph, graph) && cached.Size == Bounds.Size)
        {
            return cached.Layout;
        }

        const double top = 22;
        double bottom = Bounds.Height - 24;
        double left;
        double right = 16;
        List<double> ticks = [];
        switch (graph)
        {
            case SpectrumGraph spectrum:
                ticks = [.. DbTicks(spectrum.AxisBottom, spectrum.AxisTop, bottom - top)];
                left = 12 + ticks.Max(t => Text(DbLabel(t), AxisSize, Brushes.Black).Width) + AxisLabelGap;
                break;
            case SpectrogramGraph spectrogram:
                ticks = [.. HertzTicks(spectrogram.NyquistHertz, bottom - top)];
                left = 12 + ticks.Max(t => Text(KiloHertzLabel(t), AxisSize, Brushes.Black).Width) + AxisLabelGap;
                right = 16 + ColorBarWidth + ColorBarLabels;
                break;
            case WaveformGraph waveform:
                left = 12 + waveform.Names.Max(n => Text(n, AxisSize, Brushes.Black).Width) + AxisLabelGap;
                break;
            default:
                left = 12;
                break;
        }

        var plot = new Rect(left, top, Math.Max(10, Bounds.Width - left - right), Math.Max(10, bottom - top));
        AudioLayout layout = new(plot, ticks);
        _audioLayout = (graph, Bounds.Size, layout);
        return layout;
    }

    private static string DbLabel(double decibels) => decibels.ToString("0", CultureInfo.InvariantCulture);

    private static string KiloHertzLabel(double hertz) => (hertz / 1000).ToString("0.##", CultureInfo.InvariantCulture);

    // Ticks at round values from the bottom to the top of the axis, about one every 40 pixels.
    private static IEnumerable<double> DbTicks(int bottom, int top, double height)
    {
        int[] steps = [10, 20, 40, 50, 100];
        double wanted = Math.Max(2, height / 40);
        int step = steps.FirstOrDefault(s => (top - bottom) / (double)s <= wanted, steps[^1]);
        for (int value = (int)Math.Ceiling(bottom / (double)step) * step; value <= top; value += step)
        {
            yield return value;
        }
    }

    // Ticks in hertz at round multiples of a kilohertz (or a half), about one every 34 pixels at least.
    private static IEnumerable<double> HertzTicks(int nyquist, double height)
    {
        int[] steps = [250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 50000];
        double wanted = Math.Max(2, height / 34);
        int step = steps.FirstOrDefault(s => nyquist / (double)s <= wanted, steps[^1]);
        for (int hertz = 0; hertz <= nyquist; hertz += step)
        {
            yield return hertz;
        }
    }

    // Seconds between time labels: tenths of a second for a short stream, whole minutes and hours for a long one.
    private static readonly double[] AudioTimeSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 18000, 36000];

    // ---- Drawing

    private void RenderAudio(DrawingContext context, AudioGraph graph)
    {
        AudioLayout layout = AudioLayoutFor(graph);
        Rect plot = layout.Plot;
        IBrush secondary = Palette.Secondary(this);
        IPen grid = new Pen(Palette.Grid(this), 1);
        Color accent = Palette.Accent(this);
        switch (graph)
        {
            case WaveformGraph waveform:
                DrawWaveform(context, waveform, plot, secondary, grid, accent);
                DrawTimeAxis(context, waveform, plot, secondary);
                break;
            case SpectrogramGraph spectrogram:
                DrawSpectrogram(context, spectrogram, layout, secondary, grid);
                DrawTimeAxis(context, spectrogram, plot, secondary);
                break;
            case SpectrumGraph spectrum:
                DrawSpectrum(context, spectrum, layout, secondary, grid, accent);
                break;
        }

        if (_hover is { } hover && hover < graph.Count)
        {
            DrawAudioHover(context, graph, plot, hover);
        }
    }

    private void DrawTimeAxis(DrawingContext context, TimeGraph graph, Rect plot, IBrush secondary)
    {
        double span = graph.EndSeconds - graph.StartSeconds;
        if (span <= 0)
        {
            return;
        }

        double step = AudioTimeSteps.FirstOrDefault(s => span / s <= plot.Width / 90, AudioTimeSteps[^1] * Math.Ceiling(span / AudioTimeSteps[^1] / Math.Max(1, plot.Width / 90)));
        double y = plot.Bottom + 4;
        double lastEnd = double.NegativeInfinity;
        for (double time = Math.Ceiling(graph.StartSeconds / step) * step; time <= graph.EndSeconds + 1e-9; time += step)
        {
            FormattedText label = Text(step < 1 ? Format.Timestamp(time) : Format.Clock(time), AxisSize, secondary);
            double x = plot.Left + ((time - graph.StartSeconds) / span * plot.Width);
            double start = Math.Clamp(x - (label.Width / 2), plot.Left - 4, plot.Right - label.Width);
            if (start < lastEnd + 12)
            {
                continue;
            }

            context.DrawText(label, new Point(start, y));
            lastEnd = start + label.Width;
        }
    }

    private IReadOnlyList<Rect> Lanes(WaveformGraph graph, Rect plot)
    {
        const double gap = 2;
        double height = (plot.Height - ((graph.Channels - 1) * gap)) / graph.Channels;
        return [.. Enumerable.Range(0, graph.Channels).Select(c => new Rect(plot.Left, plot.Top + (c * (height + gap)), plot.Width, Math.Max(1, height)))];
    }

    private void DrawWaveform(DrawingContext context, WaveformGraph graph, Rect plot, IBrush secondary, IPen grid, Color accent)
    {
        context.DrawText(Text("16-bit sample codes", TextSize, secondary), new Point(4, 2));
        IReadOnlyList<Rect> lanes = Lanes(graph, plot);
        int columns = graph.Columns;
        int bins = Math.Max(1, Math.Min(columns, (int)plot.Width));
        var fill = new ImmutableSolidColorBrush(accent, 0.55);
        var stroke = new ImmutablePen(new ImmutableSolidColorBrush(accent), 1);
        for (int channel = 0; channel < lanes.Count; channel++)
        {
            Rect lane = lanes[channel];
            double middle = lane.Top + (lane.Height / 2);
            context.DrawLine(grid, new Point(lane.Left, Math.Round(middle) + 0.5), new Point(lane.Right, Math.Round(middle) + 0.5));
            if (lane.Height >= 16)
            {
                FormattedText name = Text(graph.Names[channel], AxisSize, secondary);
                context.DrawText(name, new Point(plot.Left - AxisLabelGap - name.Width, middle - (name.Height / 2)));
            }

            // Each bin is a step from its left edge to its right edge, so a stream of few columns fills the plot as a long one does.
            var top = new List<Point>(bins * 2);
            var bottom = new List<Point>(bins * 2);
            for (int bin = 0; bin < bins; bin++)
            {
                int first = (int)((long)bin * columns / bins);
                int end = Math.Max(first + 1, (int)((long)(bin + 1) * columns / bins));
                int low = short.MaxValue;
                int high = short.MinValue;
                for (int c = first; c < end; c++)
                {
                    low = Math.Min(low, graph.Minimum(channel, c));
                    high = Math.Max(high, graph.Peak(channel, c));
                }

                double yHigh = middle - (Math.Clamp(high, -32768, 32767) / 32768.0 * lane.Height / 2);
                double yLow = middle - (Math.Clamp(low, -32768, 32767) / 32768.0 * lane.Height / 2);
                double left = plot.Left + (bin * plot.Width / bins);
                double right = plot.Left + ((bin + 1) * plot.Width / bins);
                top.Add(new Point(left, yHigh));
                top.Add(new Point(right, yHigh));
                bottom.Add(new Point(left, yLow));
                bottom.Add(new Point(right, yLow));
            }

            var area = new StreamGeometry();
            using (StreamGeometryContext geometry = area.Open())
            {
                geometry.BeginFigure(top[0], true);
                foreach (Point point in top.Skip(1))
                {
                    geometry.LineTo(point);
                }

                for (int i = bottom.Count - 1; i >= 0; i--)
                {
                    geometry.LineTo(bottom[i]);
                }

                geometry.EndFigure(true);
            }

            context.DrawGeometry(fill, stroke, area);
            if (MarkedIndex is { } marked && marked < columns)
            {
                double x = Math.Round(plot.Left + ((marked + 0.5) * plot.Width / columns)) + 0.5;
                context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Palette.ForegroundColor(this), 0.7), 1.5), new Point(x, lane.Top), new Point(x, lane.Bottom));
            }
        }
    }

    private void DrawSpectrogram(DrawingContext context, SpectrogramGraph graph, AudioLayout layout, IBrush secondary, IPen grid)
    {
        Rect plot = layout.Plot;
        context.DrawText(Text("kHz", TextSize, secondary), new Point(4, 2));
        foreach (double hertz in layout.Ticks)
        {
            double y = Math.Round(plot.Bottom - (hertz / Math.Max(1, graph.NyquistHertz) * plot.Height)) + 0.5;
            FormattedText label = Text(KiloHertzLabel(hertz), AxisSize, secondary);
            context.DrawText(label, new Point(plot.Left - AxisLabelGap - label.Width, y - (label.Height / 2)));
        }

        context.FillRectangle(Brushes.Black, plot);
        if (!ReferenceEquals(_spectrogramBitmapGraph, graph))
        {
            _spectrogramBitmap?.Dispose();
            _spectrogramBitmap = CreateBitmap(graph);
            _spectrogramBitmapGraph = graph;
        }

        context.DrawImage(_spectrogramBitmap!, new Rect(0, 0, graph.Columns, graph.Rows), plot);
        context.DrawRectangle(null, grid, plot.Inflate(0.5));
        DrawColorBar(context, plot, secondary);

        if (graph.Cutoff is { } cutoff)
        {
            double y = Math.Round(plot.Bottom - (cutoff.Frequency / (double)Math.Max(1, graph.NyquistHertz) * plot.Height)) + 0.5;
            DrawGuide(context, new Point(plot.Left, y), new Point(plot.Right, y), dashed: true);
            DrawChipLabel(context, AudioGraph.Hertz(cutoff.Frequency), new Point(plot.Right - 4, y), true);
        }

        if (MarkedIndex is { } marked && marked < graph.Columns)
        {
            double x = Math.Round(plot.Left + ((marked + 0.5) * plot.Width / graph.Columns)) + 0.5;
            DrawGuide(context, new Point(x, plot.Top), new Point(x, plot.Bottom), dashed: false);
        }
    }

    private static WriteableBitmap CreateBitmap(SpectrogramGraph graph)
    {
        int columns = graph.Columns;
        int rows = graph.Rows;
        var bitmap = new WriteableBitmap(new PixelSize(columns, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer buffer = bitmap.Lock();
        ReadOnlySpan<byte> power = graph.Grid.Power.Span;
        int[] line = new int[columns];
        for (int y = 0; y < rows; y++)
        {
            int row = rows - 1 - y;
            for (int c = 0; c < columns; c++)
            {
                line[c] = RampColors[power[(c * rows) + row]];
            }

            Marshal.Copy(line, 0, buffer.Address + (y * buffer.RowBytes), columns);
        }

        return bitmap;
    }

    // The ramp as a bar beside the plot, with the levels every 40 dB.
    private void DrawColorBar(DrawingContext context, Rect plot, IBrush secondary)
    {
        var bar = new Rect(plot.Right + 12, plot.Top, ColorBarWidth, plot.Height);
        ReadOnlySpan<byte> rgb = MagmaRamp.Rgb;
        var stops = new GradientStops();
        const int Stops = 17;
        for (int i = 0; i < Stops; i++)
        {
            int entry = i * (MagmaRamp.Entries - 1) / (Stops - 1);
            stops.Add(new GradientStop(Color.FromRgb(rgb[entry * 3], rgb[(entry * 3) + 1], rgb[(entry * 3) + 2]), 1 - ((double)i / (Stops - 1))));
        }

        context.FillRectangle(new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = stops }, bar);
        context.DrawText(Text("dB", TextSize, secondary), new Point(bar.Left, 2));
        for (int decibels = RampTopDb; decibels >= RampBottomDb; decibels -= 40)
        {
            double y = bar.Top + ((RampTopDb - decibels) / (double)(RampTopDb - RampBottomDb) * bar.Height);
            FormattedText label = Text(decibels.ToString("0", CultureInfo.InvariantCulture), AxisSize, secondary);
            context.DrawText(label, new Point(bar.Right + 4, Math.Clamp(y - (label.Height / 2), plot.Top - 4, plot.Bottom - label.Height + 4)));
        }
    }

    // A line over a picture of any brightness: white on a dark halo, so it needs no color of its own. The halo is 5 px wide, 1.75 px
    // beyond the line on each side, so that it shows at 1x scaling; against the ramp's palest entry (#FCFDBF) the white line alone is 1.05:1
    // and the halo's black (85% over that entry) is 5.4:1.
    private static void DrawGuide(DrawingContext context, Point from, Point to, bool dashed)
    {
        context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Colors.Black, 0.85), GuideHaloWidth), from, to);
        context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(Colors.White, 0.95), 1.5, dashed ? new ImmutableDashStyle([4, 3], 0) : null), from, to);
    }

    // A small label on the picture: the text on the tooltip's background, right-aligned at a point, above the line it names.
    private void DrawChipLabel(DrawingContext context, string text, Point at, bool alignRight)
    {
        FormattedText label = Text(text, AxisSize, Palette.Foreground(this));
        var box = new Rect(alignRight ? at.X - label.Width - 8 : at.X, Math.Max(2, at.Y - label.Height - 5), label.Width + 8, label.Height + 2);
        context.DrawRectangle(Palette.TooltipBackground(this), null, box, 3, 3);
        context.DrawText(label, new Point(box.X + 4, box.Y + 1));
    }

    private void DrawSpectrum(DrawingContext context, SpectrumGraph graph, AudioLayout layout, IBrush secondary, IPen grid, Color accent)
    {
        Rect plot = layout.Plot;
        context.DrawText(Text("dB", TextSize, secondary), new Point(4, 2));
        double range = graph.AxisTop - graph.AxisBottom;
        double Y(double decibels) => plot.Bottom - (Math.Clamp(decibels - graph.AxisBottom, 0, range) / range * plot.Height);
        foreach (double tick in layout.Ticks)
        {
            double y = Math.Round(Y(tick)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = Text(DbLabel(tick), AxisSize, secondary);
            context.DrawText(label, new Point(plot.Left - AxisLabelGap - label.Width, y - (label.Height / 2)));
        }

        foreach (double hertz in HertzTicks(graph.NyquistHertz, plot.Width * 34 / 90))
        {
            double x = plot.Left + (hertz / Math.Max(1, graph.NyquistHertz) * plot.Width);
            FormattedText label = Text(KiloHertzLabel(hertz) + (hertz == 0 ? string.Empty : " kHz"), AxisSize, secondary);
            context.DrawText(label, new Point(Math.Clamp(x - (label.Width / 2), plot.Left - 4, plot.Right - label.Width), plot.Bottom + 4));
        }

        bool isPerChannel = graph.View == GraphForm.PerChannel;
        IReadOnlyList<Color> hues = IsDark ? CategoryColors.Dark : CategoryColors.Light;
        for (int line = 0; line < graph.Lines; line++)
        {
            Color color = isPerChannel ? (line < CategoryColors.Hues ? hues[line] : IsDark ? Color.Parse("#9a9a9a") : Color.Parse("#8a8a8a")) : accent;
            ReadOnlySpan<short> levels = graph.Line(line).Span;
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(color), 1.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
            var path = new StreamGeometry();
            using (StreamGeometryContext geometry = path.Open())
            {
                bool open = false;
                for (int row = 0; row < levels.Length; row++)
                {
                    if (levels[row] == MeasurementLimits.NoPower)
                    {
                        if (open)
                        {
                            geometry.EndFigure(false);
                            open = false;
                        }

                        continue;
                    }

                    var point = new Point(plot.Left + ((row + 0.5) * plot.Width / levels.Length), Y(levels[row] / 10.0));
                    if (open)
                    {
                        geometry.LineTo(point);
                    }
                    else
                    {
                        geometry.BeginFigure(point, false);
                        open = true;
                    }
                }

                if (open)
                {
                    geometry.EndFigure(false);
                }
            }

            if (!isPerChannel)
            {
                // The area under the single line, as the other graphs' lines have.
                var area = new StreamGeometry();
                using (StreamGeometryContext geometry = area.Open())
                {
                    bool open = false;
                    double lastX = 0;
                    for (int row = 0; row < levels.Length; row++)
                    {
                        if (levels[row] == MeasurementLimits.NoPower)
                        {
                            if (open)
                            {
                                geometry.LineTo(new Point(lastX, plot.Bottom));
                                geometry.EndFigure(true);
                                open = false;
                            }

                            continue;
                        }

                        double x = plot.Left + ((row + 0.5) * plot.Width / levels.Length);
                        if (!open)
                        {
                            geometry.BeginFigure(new Point(x, plot.Bottom), true);
                            open = true;
                        }

                        geometry.LineTo(new Point(x, Y(levels[row] / 10.0)));
                        lastX = x;
                    }

                    if (open)
                    {
                        geometry.LineTo(new Point(lastX, plot.Bottom));
                        geometry.EndFigure(true);
                    }
                }

                context.DrawGeometry(new ImmutableSolidColorBrush(accent, 0.12), null, area);
            }

            context.DrawGeometry(null, pen, path);
        }

        if (graph.Cutoff is { } cutoff)
        {
            double x = Math.Round(plot.Left + (cutoff.Frequency / (double)Math.Max(1, graph.NyquistHertz) * plot.Width)) + 0.5;
            context.DrawLine(new Pen(Palette.Foreground(this), 1.25, new DashStyle([4, 3], 0)), new Point(x, plot.Top), new Point(x, plot.Bottom));
            FormattedText label = Text(AudioGraph.Hertz(cutoff.Frequency), AxisSize, Palette.Foreground(this));
            double start = x + 6 + label.Width <= plot.Right ? x + 6 : x - 6 - label.Width;
            context.DrawText(label, new Point(start, plot.Top + 2));
        }

        if (isPerChannel)
        {
            DrawSpectrumLegend(context, graph, plot, hues);
        }
    }

    private void DrawSpectrumLegend(DrawingContext context, SpectrumGraph graph, Rect plot, IReadOnlyList<Color> hues)
    {
        double x = plot.Left + 8;
        double y = plot.Bottom - 20;
        for (int line = 0; line < graph.Lines; line++)
        {
            Color color = line < CategoryColors.Hues ? hues[line] : IsDark ? Color.Parse("#9a9a9a") : Color.Parse("#8a8a8a");
            FormattedText label = Text(graph.LineName(line), AxisSize, Palette.Foreground(this));
            if (x + 22 + label.Width > plot.Right)
            {
                break;
            }

            context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(color), 2), new Point(x, y + 8), new Point(x + 14, y + 8));
            context.DrawText(label, new Point(x + 18, y));
            x += 28 + label.Width;
        }
    }

    // The point of each line of the spectrum at a row, in the view's coordinates; a row with no power sits on the axis.
    private static List<Point> SpectrumDots(SpectrumGraph graph, Rect plot, int row)
    {
        double range = graph.AxisTop - graph.AxisBottom;
        var dots = new List<Point>(graph.Lines);
        for (int line = 0; line < graph.Lines; line++)
        {
            double value = graph.ValueAt(line, row);
            double y = double.IsNaN(value) ? plot.Bottom : plot.Bottom - (Math.Clamp(value - graph.AxisBottom, 0, range) / range * plot.Height);
            dots.Add(new Point(plot.Left + ((row + 0.5) * plot.Width / graph.Count), y));
        }

        return dots;
    }

    // Where the dots of a spectrum row are drawn, for tests; null for any other graph.
    public IReadOnlyList<Point>? SpectrumDotsOf(int row) => Graph is SpectrumGraph graph && Bounds.Width >= 80 && Bounds.Height >= 60 ? SpectrumDots(graph, AudioLayoutFor(graph).Plot, row) : null;

    private Color LineColor(int line) => line < CategoryColors.Hues ? (IsDark ? CategoryColors.Dark : CategoryColors.Light)[line] : IsDark ? Color.Parse("#9a9a9a") : Color.Parse("#8a8a8a");

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    private void DrawAudioHover(DrawingContext context, AudioGraph graph, Rect plot, int hover)
    {
        Point at;
        switch (graph)
        {
            case SpectrogramGraph spectrogram when _audioRow >= 0:
                at = new Point(plot.Left + ((hover + 0.5) * plot.Width / spectrogram.Columns), plot.Bottom - ((_audioRow + 0.5) * plot.Height / spectrogram.Rows));
                DrawGuide(context, new Point(at.X, plot.Top), new Point(at.X, plot.Bottom), dashed: false);
                context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), at, 3.5, 3.5);
                break;
            case TimeGraph time:
                at = new Point(plot.Left + ((hover + 0.5) * plot.Width / time.Columns), plot.Top + (plot.Height / 2));
                context.DrawLine(new Pen(Palette.Grid(this, 0.45), 1), new Point(Math.Round(at.X) + 0.5, plot.Top), new Point(Math.Round(at.X) + 0.5, plot.Bottom));
                break;
            case SpectrumGraph spectrum:
                {
                    // The text names the level of every line, so every line has its dot.
                    IReadOnlyList<Point> dots = SpectrumDots(spectrum, plot, hover);
                    at = new Point(plot.Left + ((hover + 0.5) * plot.Width / spectrum.Count), dots.Count > 0 ? dots.Min(d => d.Y) : plot.Bottom);
                    context.DrawLine(new Pen(Palette.Grid(this, 0.45), 1), new Point(Math.Round(at.X) + 0.5, plot.Top), new Point(Math.Round(at.X) + 0.5, plot.Bottom));
                    for (int line = 0; line < dots.Count; line++)
                    {
                        Color color = spectrum.View == GraphForm.PerChannel ? LineColor(line) : Palette.Accent(this);
                        context.DrawEllipse(new ImmutableSolidColorBrush(color), new Pen(Palette.Surface(this), 2), dots[line], 4.5, 4.5);
                    }

                    break;
                }

            default:
                return;
        }

        DrawTooltip(context, HoverText ?? graph.Describe(hover), at, plot);
    }
}
