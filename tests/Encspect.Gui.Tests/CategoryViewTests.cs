using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The category charts in a window: each view draws, answers the pointer and the keys, a click selects the first matching
// frame and a frame selected in the tree is marked; 100,000 frames draw as quickly as 200,000 points of a line; the pixels
// are the palette's; and, with ENCSPECT_SCREENSHOTS set, each view is saved in light and dark mode.
public class CategoryViewTests
{
    private static DetailWindow OpenWithCategories(string fixture, double width = 1120, double height = 780, Analyzer? analyzer = null)
    {
        var window = new DetailWindow(new DetailViewModel(analyzer ?? CategoryTestData.Analyzer())) { Width = width, Height = height };
        window.Show();
        Task load = window.Model.LoadAsync(fixture);
        UiPump.Until(() => load.IsCompleted);
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    private static void Choose(DetailWindow window, string title, GraphForm? form = null)
    {
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == title);
        if (form is { } v)
        {
            window.Model.Graphs.SelectForm(v);
        }

        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    [AvaloniaFact]
    public void The_graphs_tab_offers_the_headings_the_chips_and_the_views()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));

        Assert.Equal(["Summary", "Stereo Coding", "Block Switching", "Bit Distribution", "Other"], window.Model.Graphs.ChoiceGroups.Select(g => g.Heading));
        Assert.True(window.GraphChoices.IsEffectivelyVisible);
        Assert.False(window.GraphFormChoices.IsEffectivelyVisible);
        Choose(window, "Block Usage");
        Assert.True(window.GraphFormChoices.IsEffectivelyVisible);
        Assert.Equal(GraphForm.OverTime, ((CategoryGraph)window.Graph.Graph!).View);
        window.Model.Graphs.SelectForm(GraphForm.Frames);
        Assert.Equal(GraphForm.Frames, ((CategoryGraph)window.Graph.Graph!).View);
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_the_over_time_view_says_the_shares_and_clicking_selects_the_first_frame_of_the_interval()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Stereo Coding", GraphForm.OverTime);
        var graph = (CategoryGraph)window.Graph.Graph!;
        Assert.True(graph.Count > 3);

        Point point = ScreenshotTests.PointOver(window, 2);
        window.MouseMove(point);
        Assert.Equal(2, window.Graph.HoverIndex);
        Assert.Contains("0:02 to 0:03", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.Contains("Mid/side stereo", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.Contains("%", window.Graph.HoverText, StringComparison.Ordinal);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        long expected = (long)graph.Series.X.Span[graph.Series.Time!.FirstUnits.Span[2]];
        Assert.Equal(expected, window.Model.SelectedNode.Node.Offset);
        Assert.Equal(2, window.Graph.MarkedIndex);

        // A frame selected in the tree marks its interval.
        window.Model.SelectFrameAt((long)graph.Series.X.Span[graph.Series.Time.FirstUnits.Span[3] + 1]);
        Assert.Equal(3, window.Graph.MarkedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_strip_cell_selects_its_frame_and_a_legend_entry_steps_through_its_frames()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Block Usage", GraphForm.Frames);
        var graph = (CategoryGraph)window.Graph.Graph!;
        Series bitrate = window.Model.Report!.Streams[0].Series.First(s => s.Name == "Bitrate");
        Assert.True(graph.Count <= (int)window.Graph.Bounds.Width, "Each frame has a cell of its own.");

        Point point = ScreenshotTests.PointOver(window, 40);
        window.MouseMove(point);
        Assert.StartsWith("Frame 41 · 0x", window.Graph.HoverText, StringComparison.Ordinal);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal((long)bitrate.X.Span[40], window.Model.SelectedNode!.Node.Offset);
        Assert.Equal(40, window.Graph.MarkedIndex);

        // The legend entry of the short blocks selects the next frame that has one, and again the one after.
        int[] withShort = [.. Enumerable.Range(0, graph.Series.Count).Where(u => Enumerable.Range(0, 4).Any(v => graph.Series[u, v] == 2))];
        Assert.True(withShort.Length >= 2);
        window.Model.Graphs.ActivateCategory(2);
        long first = window.Model.SelectedNode.Node.Offset;
        Assert.Equal((long)bitrate.X.Span[withShort.First(u => u > 40)], first);
        window.Model.Graphs.ActivateCategory(2);
        Assert.Equal((long)bitrate.X.Span[withShort.First(u => bitrate.X.Span[u] > first)], window.Model.SelectedNode.Node.Offset);

        // The same through the legend's rectangle in the window.
        Choose(window, "Block Usage", GraphForm.Frames);
        Point legend = FindLegendEntry(window, 2);
        window.MouseDown(legend, MouseButton.Left);
        window.MouseUp(legend, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode.Kind);
        Assert.Contains(graph.Series.X.Span.IndexOf(window.Model.SelectedNode.Node.Offset), withShort);
        window.Close();
    }

    private static Point FindLegendEntry(DetailWindow window, int category)
    {
        GraphView view = window.Graph;
        for (double y = view.Bounds.Height - 1; y > view.Bounds.Height - 90; y -= 2)
        {
            for (double x = 0; x < view.Bounds.Width; x += 4)
            {
                window.MouseMove(view.TranslatePoint(new Point(x, y), window)!.Value);
                if (view.HoverText?.StartsWith(((CategoryGraph)view.Graph!).Series.Categories[category].Name + " ·", StringComparison.Ordinal) == true)
                {
                    return view.TranslatePoint(new Point(x, y), window)!.Value;
                }
            }
        }

        throw new InvalidOperationException("No legend entry found.");
    }

    [AvaloniaFact]
    public void The_summary_view_is_a_table_whose_rows_answer_the_pointer_and_select_the_next_frame()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Block Usage", GraphForm.Summary);
        var graph = (CategoryGraph)window.Graph.Graph!;
        Assert.Equal(6, graph.Count);

        Point point = ScreenshotTests.PointOver(window, 2);
        window.MouseMove(point);
        long shorts = graph.Series.Counts[2];
        Assert.Equal($"Short · {Format.Count(shorts, "value", "values")} · {shorts * 100.0 / graph.Series.Total:0.0}%", window.Graph.HoverText);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        long first = window.Model.SelectedNode!.Node.Offset;
        Assert.Contains(Enumerable.Range(0, 4), v => graph.Series[graph.Series.X.Span.IndexOf(first), v] == 2);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.True(window.Model.SelectedNode.Node.Offset != first);

        Choose(window, "Coding Summary");
        var summary = (CodingSummaryGraph)window.Graph.Graph!;
        Assert.Equal(window.Model.Report!.Streams[0].CategorySeries.Sum(s => s.Categories.Count), summary.Count);
        Assert.False(window.GraphFormChoices.IsEffectivelyVisible);
        window.MouseMove(ScreenshotTests.PointOver(window, 0));
        Assert.StartsWith("Block Usage · Long · ", window.Graph.HoverText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void The_keys_move_a_cursor_over_the_cells_and_enter_selects_them()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Block Usage", GraphForm.Frames);
        window.Graph.Focus();

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        for (int i = 0; i < 4; i++)
        {
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        }

        Assert.Equal(4, window.Graph.HoverIndex);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(3, window.Graph.HoverIndex);
        Assert.StartsWith("Frame 4 · ", window.Graph.HoverText, StringComparison.Ordinal);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        var graph = (CategoryGraph)window.Graph.Graph!;
        Assert.Equal((long)graph.Series.X.Span[3], window.Model.SelectedNode!.Node.Offset);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(graph.Count - 1, window.Graph.HoverIndex);

        // The line graph answers the same keys.
        Choose(window, "Bitrate", GraphForm.Frames);
        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, window.Graph.HoverIndex);
        window.Close();
    }

    // 100,000 frames of 4 values draw and answer the pointer as quickly as the 200,000 points of a line (500 ms a frame).
    [AvaloniaFact]
    public void A_series_of_200000_units_in_4_values_draws_and_answers_the_pointer_quickly()
    {
        const int units = 200_000;
        var random = new Random(9);
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4,
            ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"], concept: GraphConcept.BlockSwitching);
        for (int unit = 0; unit < units; unit++)
        {
            builder.Add(unit == 123_456 ? [0, 0, 2, 0] : random.Next(50) == 0 ? [0, 0, 1, 1] : [0, 0, 0, 0]);
        }

        double[] offsets = Enumerable.Range(0, units).Select(i => i * 417.0).ToArray();
        int seconds = units / 40;
        var time = new TimeIndex(Enumerable.Range(0, seconds).Select(s => (double)s).ToArray(), Enumerable.Range(0, seconds).Select(s => s * 40).ToArray());
        CategorySeries series = builder.Build(offsets, null, null, "Block types.", null, time);
        var strip = new CategoryGraph(series, GraphForm.Frames);
        var graphs = new Dictionary<GraphForm, CategoryGraph>
        {
            [GraphForm.Frames] = strip,
            [GraphForm.OverTime] = new CategoryGraph(series, GraphForm.OverTime),
            [GraphForm.Summary] = new CategoryGraph(series, GraphForm.Summary),
        };
        var view = new GraphView { Graph = strip };
        var window = new Window { Width = 1000, Height = 360, Content = view };
        window.Show();
        UiPump.Until(() => true);

        // Each frame builds the columns of its width afresh (the window's width changes), so the aggregation over the codes is timed
        // with the drawing, not only the redraws and hovers over cached columns.
        foreach ((GraphForm mode, CategoryGraph graph) in graphs)
        {
            view.Graph = graph;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 6; i++)
            {
                window.Width = 1000 + (7 * (i + 1));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.MouseMove(new Point(300 + i, 100 + i));
                view.InvalidateVisual();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.NotNull(window.CaptureRenderedFrame());
            }

            double perFrame = clock.Elapsed.TotalMilliseconds / 6;
            Assert.True(perFrame < 500, $"{mode}: {perFrame} ms a frame");
        }

        // A lone category's column can be found by pointing, at any width, and a column with several lone categories (they share its
        // pixel) is stepped through by clicking it again. Every point of the strip is probed, with no early stop, and the points
        // that answer near the unit are clicked until the unit is the answer.
        view.Graph = strip;
        int? marked = null;
        view.PointClicked += (_, index) =>
        {
            marked = index;
            view.MarkedIndex = index;
        };
        foreach (double width in new[] { 420.0, 700, 1042 })
        {
            window.Width = width;

            // The resize is laid out on the dispatcher, on every platform's own schedule: wait for the view to have the new size.
            UiPump.Until(() => view.Bounds.Width > 0 && view.Bounds.Width <= width && view.Bounds.Height > 0);
            view.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Assert.True(view.ChartMetrics() is not null, $"The strip has no layout at {view.Bounds}.");
            // The row of granule 2, channel 1, the third.
            (double top, double rowHeight) = view.StripRowBounds()![2];
            double rowY = top + (rowHeight / 2);
            int near = 0;
            bool reached = false;
            for (double x = 0; x < view.Bounds.Width && !reached; x++)
            {
                if (view.IndexAt(new Point(x, rowY)) is not { } answer || Math.Abs(answer - 123_456) > 2_000)
                {
                    continue;
                }

                near++;
                view.MarkedIndex = null;
                for (int click = 0; click < 8 && !reached; click++)
                {
                    Point inWindow = view.TranslatePoint(new Point(x, rowY), window)!.Value;
                    window.MouseDown(inWindow, MouseButton.Left);
                    window.MouseUp(inWindow, MouseButton.Left);
                    reached = marked == 123_456;
                }
            }

            Assert.True(reached, $"The lone short granule is not reachable: view {view.Bounds}, metrics {view.ChartMetrics()}, {near} columns answer near it, last answer {marked}.");
        }

        window.Close();
    }

    // Colors that need no image comparison: a scripted series of one category per cell, rendered, sampled at the middle
    // of each cell, must give the palette's colors.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_drawn_cells_are_the_colors_of_the_palette(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        IReadOnlyList<Category> categories = CategoryTestData.Numbered("Mode", 8);
        var builder = new CategorySeriesBuilder("Modes", categories, "frame", 1);
        for (int unit = 0; unit < 8; unit++)
        {
            builder.Add((byte)unit);
        }

        CategorySeries series = builder.Build(Enumerable.Range(0, 8).Select(i => i * 100.0).ToArray(), null, null, "Modes.");
        var view = new GraphView { Graph = new CategoryGraph(series, GraphForm.Frames) };
        var window = new Window { Width = 800, Height = 300, Content = view };
        window.Show();
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        // The graph surfaces the palette was validated against.
        Assert.True(view.TryFindResource("SystemRegionBrush", view.ActualThemeVariant, out object? region));
        Color surface = ((ISolidColorBrush)region!).Color;
        Assert.Equal(variant == "dark" ? Colors.Black : Colors.White, surface);

        CategoryStyle[] expected = CategoryColors.For(series, variant == "dark", surface);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        for (int category = 0; category < 8; category++)
        {
            Point inView = CellCenter(view, category);
            Color actual = Pixel(frame, view.TranslatePoint(inView, window)!.Value);
            Assert.Equal(expected[category].Color, actual);
        }

        Assert.Equal(expected[1].Color, CategoryColors.For(series, variant == "dark", surface)[1].Color);
        Assert.Equal((variant == "dark" ? CategoryColors.Dark : CategoryColors.Light)[1], expected[1].Color);
        Assert.True(expected[6].IsOther && expected[7].IsOther);
        Assert.Equal(expected[6].Color, expected[7].Color);
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static Point CellCenter(GraphView view, int index)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (double x = 0; x < view.Bounds.Width; x++)
        {
            for (double y = 5; y < view.Bounds.Height - 60; y += 2)
            {
                if (view.IndexAt(new Point(x, y)) == index)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        return new Point((xs.Min() + xs.Max()) / 2, (ys.Min() + ys.Max()) / 2);
    }

    private static Color Pixel(WriteableBitmap bitmap, Point at)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        int x = (int)at.X;
        int y = (int)at.Y;
        unsafe
        {
            byte* row = (byte*)buffer.Address + (y * buffer.RowBytes);
            byte* pixel = row + (x * 4);
            return buffer.Format == PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
        }
    }

    [AvaloniaFact]
    public void The_graphs_are_a_list_down_the_left_under_headings_in_one_font_size()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Bitrate");

        var entries = window.GraphChoices.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("entry")).ToList();
        var headings = window.GraphChoices.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading")).ToList();
        Assert.Equal(window.Model.Graphs.Choices.Select(c => c.Title), entries.Select(b => (string)b.Content!));
        Assert.Equal(window.Model.Graphs.ChoiceGroups.Select(g => g.Heading), headings.Select(h => h.Text));
        Assert.True(window.GraphList.Bounds.Width <= 240);
        Assert.True(window.Graph.Bounds.Width > window.GraphList.Bounds.Width * 2);
        Assert.True(entries.All(b => b.TranslatePoint(new Point(), window)!.Value.X < window.Graph.TranslatePoint(new Point(), window)!.Value.X));
        Assert.Equal(entries.Count, entries.Select(b => b.TranslatePoint(new Point(), window)!.Value.Y).Distinct().Count());

        // Headings, entries and the view switcher are one size; a heading differs by weight.
        var forms = window.GraphFormChoices.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(3, forms.Count);
        double size = entries[0].FontSize;
        Assert.All(entries.Concat(forms), b => Assert.Equal(size, b.FontSize));
        Assert.All(headings, h => Assert.Equal(size, h.FontSize));
        Assert.All(headings, h => Assert.Equal(FontWeight.SemiBold, h.FontWeight));
        Assert.All(entries, b => Assert.NotEqual(FontWeight.SemiBold, b.FontWeight));
        window.Close();
    }

    [AvaloniaFact]
    public void A_numeric_quantity_is_one_entry_with_forms_for_per_frame_over_time_and_distribution()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Bitrate", GraphForm.Frames);

        Assert.Equal(["Per Frame", "Over Time", "Distribution"], window.Model.Graphs.Forms.Select(f => f.Title));
        Assert.Equal(SeriesAxis.FrameOffset, ((LineGraph)window.Graph.Graph!).Axis);
        Choose(window, "Bitrate", GraphForm.OverTime);
        Assert.Equal(SeriesAxis.Seconds, ((LineGraph)window.Graph.Graph!).Axis);
        Choose(window, "Bitrate", GraphForm.Summary);
        Assert.IsType<DistributionGraph>(window.Graph.Graph);
        window.MouseMove(ScreenshotTests.PointOver(window, 1));
        Assert.Contains("frames", window.Graph.HoverText, StringComparison.Ordinal);

        // The form is remembered for the next entry that has one: the reservoir has no distribution, so it keeps its own.
        Choose(window, "Reservoir Fill");
        Assert.Equal(GraphForm.Frames, window.Model.Graphs.Form);
        window.Close();
    }

    // The second value of a frame without a category: the strip's hover text and the keys reach it without an exception.
    [AvaloniaFact]
    public void A_value_without_a_category_can_be_pointed_at_and_reached_by_the_keys_and_rows_change_with_up_and_down()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Channel Streams", GraphForm.Frames);
        var graph = (CategoryGraph)window.Graph.Graph!;
        Assert.Equal(2, graph.Rows);
        Assert.Equal(CategorySeries.None, graph.Series[0, 1]);
        Assert.Equal(2, window.Graph.ChartMetrics()!.Value.Rows);

        // By the pointer: every cell of the second row, the empty ones included.
        for (double x = window.Graph.Bounds.Width * 0.1; x < window.Graph.Bounds.Width * 0.9; x += 3)
        {
            for (double y = 20; y < window.Graph.Bounds.Height - 30; y += 4)
            {
                window.MouseMove(window.Graph.TranslatePoint(new Point(x, y), window)!.Value);
            }
        }

        window.MouseMove(ScreenshotTests.PointOver(window, 0));
        Assert.Contains("no category", window.Graph.HoverText ?? "no category", StringComparison.Ordinal);

        // By the keys: Down moves to the second row, whose value of frame 1 has no category.
        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        Assert.Contains("channel 1: Short", window.Graph.HoverText, StringComparison.Ordinal);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        Assert.Contains("channel 2: no category", window.Graph.HoverText, StringComparison.Ordinal);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, window.Graph.HoverIndex);
        Assert.Contains("channel 2: Long", window.Graph.HoverText, StringComparison.Ordinal);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Contains("channel 1: Long", window.Graph.HoverText, StringComparison.Ordinal);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(1, window.Graph.HoverIndex);

        // Every other layout and form answers the keys through its whole length.
        foreach (string title in new[] { "Window Sequences", "Block Usage", "Submodes" })
        {
            foreach (GraphForm form in new[] { GraphForm.Frames, GraphForm.OverTime, GraphForm.Summary })
            {
                Choose(window, title, form);
                window.Graph.Focus();
                window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
                window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
                Assert.NotNull(window.Graph.HoverText);
            }
        }

        window.Close();
    }

    [AvaloniaFact]
    public void The_graph_names_its_legend_and_the_cursor_in_its_automation_text()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Block Usage", GraphForm.Frames);
        Assert.Equal("Block Usage", AutomationProperties.GetName(window.Graph));
        string help = AutomationProperties.GetHelpText(window.Graph)!;
        Assert.StartsWith("Legend: Long ", help, StringComparison.Ordinal);
        Assert.Contains("Reserved 0.2%", help, StringComparison.Ordinal);

        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Contains("Frame 1 · ", AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);
        window.Close();
    }

    // Whatever the pane's height, the plot keeps its minimum: the legend folds first, a strip that cannot give each row eight pixels
    // becomes one stacked row, and what still does not fit scrolls.
    [AvaloniaTheory]
    [InlineData(700, false, 4)]
    [InlineData(260, false, 4)]
    [InlineData(200, false, 4)]
    [InlineData(150, true, 4)]
    [InlineData(120, true, 1)]
    public void A_short_pane_never_squeezes_the_plot_below_its_minimum(double height, bool overTimeScrolls, int stripRows)
    {
        const int units = 400;
        var overTime = new CategorySeriesBuilder("Stereo", CategoryTestData.Numbered("Coding", 6), "frame", 1);
        var strip = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4, ["a", "b", "c", "d"]);
        for (int unit = 0; unit < units; unit++)
        {
            overTime.Add((byte)((unit / 9) % 6));
            strip.Add([0, 0, (byte)(unit % 37 == 0 ? 2 : 0), 0]);
        }

        double[] x = Enumerable.Range(0, units).Select(i => i * 100.0).ToArray();
        var time = new TimeIndex(Enumerable.Range(0, 10).Select(s => (double)s).ToArray(), Enumerable.Range(0, 10).Select(s => s * 40).ToArray());
        CategorySeries over = overTime.Build(x, null, null, "d", null, time);
        CategorySeries stripSeries = strip.Build(x, null, null, "d", null, time);
        var view = new GraphView { Graph = new CategoryGraph(over, GraphForm.OverTime) };
        var window = new Window { Width = 900, Height = height, Content = view };
        window.Show();
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        (double plot, int rows, double content, bool overflows) = view.ChartMetrics()!.Value;
        Assert.True(plot >= 110, $"{plot} px over time in {height}");
        Assert.Equal(overTimeScrolls, overflows);
        Assert.Equal(overflows, content > view.Bounds.Height);
        Assert.NotNull(window.CaptureRenderedFrame());

        view.Graph = new CategoryGraph(stripSeries, GraphForm.Frames);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        (plot, rows, content, overflows) = view.ChartMetrics()!.Value;
        Assert.True(plot >= 60);
        Assert.Equal(stripRows, rows);
        Assert.NotNull(window.CaptureRenderedFrame());

        // A wheel turn scrolls what does not fit, and the content ends where the legend ends.
        if (overflows)
        {
            window.MouseWheel(new Point(300, 100), new Vector(0, -10));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Assert.NotNull(window.CaptureRenderedFrame());
        }

        window.Close();
    }

    [AvaloniaFact]
    public void The_coding_summary_shows_all_its_tables_by_scrolling_when_they_do_not_fit()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"), 1120, 780, CategoryTestData.Analyzer(CategoryTestData.ManyCategorySeries));
        Choose(window, "Coding Summary");
        var summary = (CodingSummaryGraph)window.Graph.Graph!;
        var (_, _, content, scrolls) = window.Graph.ChartMetrics()!.Value;

        Assert.Equal(14, summary.Tables.Count);
        Assert.True(scrolls, $"{content} px of tables in {window.Graph.Bounds.Height}");
        Assert.True(content > window.Graph.Bounds.Height);

        // Every row can be reached: the keys scroll the cursor into view, and the last row answers.
        window.Graph.Focus();
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(summary.Count - 1, window.Graph.HoverIndex);
        Assert.StartsWith(summary.Tables[^1].Name, window.Graph.HoverText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void The_two_part_share_has_its_own_form_and_hover_text_and_a_click_selects_the_first_frame_of_the_interval()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Channel Bit Share", GraphForm.Split);
        var graph = (ShareSplitGraph)window.Graph.Graph!;
        Assert.Equal(["Per Frame", "Over Time", "First and Second"], window.Model.Graphs.Forms.Select(f => f.Title));
        Assert.Equal("Bit Distribution", graph.Heading);

        Point point = ScreenshotTests.PointOver(window, 2);
        window.MouseMove(point);
        double first = graph.FirstShare(2);
        Assert.Equal($"0:02 · first channel {ShareSplitGraph.Percent(first)} · second channel {ShareSplitGraph.Percent(100 - first)}", window.Graph.HoverText);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        Assert.Equal(graph.FrameAt(2, null), window.Model.SelectedNode.Node.Offset);
        Assert.Equal(2, window.Graph.MarkedIndex);

        window.Graph.Focus();
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(graph.Count - 1, window.Graph.HoverIndex);
        Assert.Contains("second channel", AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersCategoryGraphs(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));

        Choose(window, "Stereo Coding", GraphForm.OverTime);
        window.MouseMove(ScreenshotTests.PointOver(window, 2));
        ScreenshotTests.Save(window, $"list-over-time-{variant}.png");

        Choose(window, "Block Usage", GraphForm.Frames);
        window.MouseMove(ScreenshotTests.PointOver(window, 100));
        ScreenshotTests.Save(window, $"frames-strip-{variant}.png");

        Choose(window, "Block Usage", GraphForm.OverTime);
        window.Model.SelectFrameAt((long)window.Model.Report!.Streams[0].Series[0].X.Span[45]);
        ScreenshotTests.Save(window, $"block-usage-over-time-marked-{variant}.png");

        Choose(window, "Block Usage", GraphForm.Summary);
        window.MouseMove(ScreenshotTests.PointOver(window, 2));
        ScreenshotTests.Save(window, $"summary-{variant}.png");

        Choose(window, "Coding Summary");
        window.MouseMove(ScreenshotTests.PointOver(window, 3));
        ScreenshotTests.Save(window, $"coding-summary-{variant}.png");

        Choose(window, "Channel Streams", GraphForm.Frames);
        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        ScreenshotTests.Save(window, $"value-without-category-{variant}.png");

        Choose(window, "Window Sequences", GraphForm.Frames);
        ScreenshotTests.Save(window, $"variable-layout-{variant}.png");

        Choose(window, "Submodes", GraphForm.OverTime);
        ScreenshotTests.Save(window, $"ordered-ramp-other-{variant}.png");

        Choose(window, "Bitrate", GraphForm.Frames);
        window.MouseMove(ScreenshotTests.PointOver(window, 60));
        ScreenshotTests.Save(window, $"numeric-bitrate-{variant}.png");
        Choose(window, "Bitrate", GraphForm.Summary);
        ScreenshotTests.Save(window, $"numeric-bitrate-distribution-{variant}.png");

        Choose(window, "Channel Bit Share", GraphForm.Split);
        window.MouseMove(ScreenshotTests.PointOver(window, 1));
        ScreenshotTests.Save(window, $"channel-bit-share-{variant}.png");
        window.Close();

        // A narrow window.
        DetailWindow narrow = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"), 900, 780);
        Choose(narrow, "Block Usage", GraphForm.OverTime);
        ScreenshotTests.Save(narrow, $"narrow-900-{variant}.png");
        Choose(narrow, "Block Usage", GraphForm.Frames);
        ScreenshotTests.Save(narrow, $"narrow-900-frames-{variant}.png");
        narrow.Close();

        // A stream with many entries.
        DetailWindow many = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"), 1120, 780, CategoryTestData.Analyzer(CategoryTestData.ManyCategorySeries));
        Choose(many, "Coding Tool 3", GraphForm.OverTime);
        ScreenshotTests.Save(many, $"many-entries-{variant}.png");
        Choose(many, "Coding Summary");
        ScreenshotTests.Save(many, $"many-entries-coding-summary-{variant}.png");
        many.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // The drawn columns and rows and the hit-tested ones agree, with no font in the numbers: each unit has a cell of its own, and
    // along a row the answers are the units in order, each over one run of points, in every row of the strip.
    [AvaloniaFact]
    public void Every_cell_of_the_strip_answers_with_its_own_unit_in_every_row()
    {
        const int units = 300;
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4, ["a", "b", "c", "d"]);
        for (int unit = 0; unit < units; unit++)
        {
            builder.Add([0, 0, 0, 0]);
        }

        CategorySeries series = builder.Build(Enumerable.Range(0, units).Select(i => i * 10.0).ToArray(), null, null, "d");
        var view = new GraphView { Graph = new CategoryGraph(series, GraphForm.Frames) };
        var window = new Window { Width = 1000, Height = 360, Content = view };
        window.Show();
        UiPump.Until(() => view.Bounds.Width > 0);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(4, view.ChartMetrics()!.Value.Rows);

        // The rows, four of them, each answering every unit along x.
        IReadOnlyList<(double Top, double Height)> rows = view.StripRowBounds()!;
        Assert.Equal(4, rows.Count);
        for (int row = 0; row < rows.Count; row++)
        {
            double? y = rows[row].Top + (rows[row].Height / 2) - 2;
            var answers = new List<int>();
            for (double x = 0; x < view.Bounds.Width; x++)
            {
                if (view.IndexAt(new Point(x, y.Value + 2)) is { } unit && (answers.Count == 0 || answers[^1] != unit))
                {
                    answers.Add(unit);
                }
            }

            Assert.Equal(Enumerable.Range(0, units), answers);
        }

        window.Close();
    }
}
