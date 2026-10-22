using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
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
    private static DetailWindow OpenWithCategories(string fixture)
    {
        var window = new DetailWindow(new DetailViewModel(CategoryTestData.Analyzer())) { Width = 1180, Height = 1000 };
        window.Show();
        Task load = window.Model.LoadAsync(fixture);
        UiPump.Until(() => load.IsCompleted);
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    private static void Choose(DetailWindow window, string title, CategoryView? view = null)
    {
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == title);
        if (view is { } v)
        {
            window.Model.Graphs.SelectView(v);
        }

        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    [AvaloniaFact]
    public void The_graphs_tab_offers_the_headings_the_chips_and_the_views()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));

        Assert.Equal(["Summary", "Stereo coding", "Block switching", "Bit distribution", "Other"], window.Model.Graphs.ChoiceGroups.Select(g => g.Heading));
        Assert.True(window.GraphChoices.IsEffectivelyVisible);
        Assert.False(window.GraphViewChoices.IsEffectivelyVisible);
        Choose(window, "Block Usage");
        Assert.True(window.GraphViewChoices.IsEffectivelyVisible);
        Assert.Equal(CategoryView.OverTime, window.Graph.View);
        window.Model.Graphs.SelectView(CategoryView.Frames);
        Assert.Equal(CategoryView.Frames, window.Graph.View);
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_the_over_time_view_says_the_shares_and_clicking_selects_the_first_frame_of_the_interval()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Stereo Coding", CategoryView.OverTime);
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
        Choose(window, "Block Usage", CategoryView.Frames);
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
        Choose(window, "Block Usage", CategoryView.Frames);
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
        Choose(window, "Block Usage", CategoryView.Summary);
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
        Assert.False(window.GraphViewChoices.IsEffectivelyVisible);
        window.MouseMove(ScreenshotTests.PointOver(window, 0));
        Assert.StartsWith("Block Usage · Long · ", window.Graph.HoverText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void The_keys_move_a_cursor_over_the_cells_and_enter_selects_them()
    {
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));
        Choose(window, "Block Usage", CategoryView.Frames);
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
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.First(c => c.Graph is LineGraph);
        UiPump.Until(() => true);
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
        var graph = new CategoryGraph(series);
        var view = new GraphView { Graph = graph, View = CategoryView.Frames };
        var window = new Window { Width = 1000, Height = 360, Content = view };
        window.Show();
        UiPump.Until(() => true);

        foreach (CategoryView mode in new[] { CategoryView.Frames, CategoryView.OverTime, CategoryView.Summary })
        {
            graph.View = mode;
            view.View = mode;
            view.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 6; i++)
            {
                window.MouseMove(new Point(300 + i, 100 + i));
                view.InvalidateVisual();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.NotNull(window.CaptureRenderedFrame());
            }

            double perFrame = clock.Elapsed.TotalMilliseconds / 6;
            Assert.True(perFrame < 500, $"{mode}: {perFrame} ms a frame");
        }

        // The lone short granule is in a column of its own tick: pointing along the strip finds the unit.
        graph.View = CategoryView.Frames;
        view.View = CategoryView.Frames;
        view.InvalidateVisual();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        int? found = null;
        for (double x = 0; x < view.Bounds.Width && found != 123_456; x++)
        {
            for (double y = 20; y < view.Bounds.Height - 40 && found != 123_456; y += 3)
            {
                found = view.IndexAt(new Point(x, y));
                if (found is { } at && Math.Abs(at - 123_456) > 400)
                {
                    break;
                }
            }
        }

        Assert.Equal(123_456, found);
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
        var view = new GraphView { Graph = new CategoryGraph(series), View = CategoryView.Frames };
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

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersCategoryGraphs(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        DetailWindow window = OpenWithCategories(Fixtures.Stats("vbr-seconds.mp3"));

        Choose(window, "Stereo Coding", CategoryView.OverTime);
        window.MouseMove(ScreenshotTests.PointOver(window, 2));
        ScreenshotTests.Save(window, $"category-over-time-{variant}.png");

        Choose(window, "Block Usage", CategoryView.Frames);
        window.MouseMove(ScreenshotTests.PointOver(window, 100));
        ScreenshotTests.Save(window, $"category-frames-{variant}.png");

        Choose(window, "Block Usage", CategoryView.OverTime);
        window.Model.SelectFrameAt((long)window.Model.Report!.Streams[0].Series[0].X.Span[45]);
        ScreenshotTests.Save(window, $"category-block-usage-over-time-{variant}.png");

        Choose(window, "Block Usage", CategoryView.Summary);
        window.MouseMove(ScreenshotTests.PointOver(window, 2));
        ScreenshotTests.Save(window, $"category-summary-{variant}.png");

        Choose(window, "Coding Summary");
        window.MouseMove(ScreenshotTests.PointOver(window, 7));
        ScreenshotTests.Save(window, $"coding-summary-{variant}.png");

        Choose(window, "Window Sequences", CategoryView.Frames);
        ScreenshotTests.Save(window, $"category-variable-layout-{variant}.png");

        Choose(window, "Submodes", CategoryView.OverTime);
        ScreenshotTests.Save(window, $"category-ordered-other-{variant}.png");

        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
