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
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The Frame Map in a window: a click selects a frame, a frame selected in the tree is ringed and scrolled into view, the keys, the
// hover text, the lane of marks, the help text, the form remembered across entries; 2,000,000 units draw as quickly as a screenful;
// the pixels are the palette's and the ramps'; and, with ENCSPECT_SCREENSHOTS set, the maps of real files are saved.
public class FrameMapViewTests
{
    // Files found by script from the goldens (see docs/development.md, "Frame Map fixtures"): short blocks in a Layer III stream with
    // 12 of 77 frames short, a VBR stream of 11 distinct bitrates, an AC-3 stream whose blocks couple, and a damaged stream whose three
    // findings point into frames.
    internal static string ShortBlocks => Fixtures.Mpeg("external-blade-stereo.mp3");

    internal static string Vbr => Fixtures.Mpeg("external-lame3101-vbr.mp3");

    internal static string Coupling => Fixtures.Ac3("coding-ac3-coupling.ac3");

    internal static string Damaged => Fixtures.Mpeg("external-blade080-stereo.mp3");

    internal static DetailWindow Open(string fixture, double width = 1180, double height = 800)
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = width, Height = height };
        window.Show();
        Task load = window.Model.LoadAsync(fixture);
        UiPump.Until(() => load.IsCompleted);
        window.Model.IsGraphsShown = true;
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    internal static void Choose(DetailWindow window, string title, GraphForm? form = null)
    {
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == title);
        if (form is { } v)
        {
            window.Model.Graphs.SelectForm(v);
        }

        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static (GraphView View, Window Window) Show(Graph graph, double width = 900, double height = 480)
    {
        var view = new GraphView { Graph = graph };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return (view, window);
    }

    private static Point Center(GraphView view, TopLevel window, int square) =>
        view.TranslatePoint(view.MapSquareRect(square)?.Center ?? throw new InvalidOperationException($"Square {square} is not on screen."), window)!.Value;

    private static FrameMapGraph Numbers(int count, Func<int, double> value, string unit = "kbps", ValueRange? range = null, IReadOnlyList<MapFinding>? findings = null)
    {
        double[] offsets = [.. Enumerable.Range(0, count).Select(i => i * 200.0)];
        double[] values = [.. Enumerable.Range(0, count).Select(value)];
        return new FrameMapGraph(new Series("Bitrate", unit, SeriesAxis.FrameOffset, offsets, values) { DeclaredRange = range }, offsets, findings);
    }

    private static void Press(TopLevel window, Key key) => window.KeyPress(key, RawInputModifiers.None, key switch
    {
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Down => PhysicalKey.ArrowDown,
        Key.PageDown => PhysicalKey.PageDown,
        Key.PageUp => PhysicalKey.PageUp,
        Key.Home => PhysicalKey.Home,
        Key.End => PhysicalKey.End,
        Key.Enter => PhysicalKey.Enter,
        _ => PhysicalKey.None,
    }, null);

    [AvaloniaFact]
    public void The_form_is_offered_on_entries_with_units_and_remembered_for_the_next_entry()
    {
        DetailWindow window = Open(Fixtures.Stats("vbr-seconds.mp3"));

        Choose(window, "Bitrate");
        Assert.Contains(window.Model.Graphs.Forms, f => f.Title == "Frame Map");
        Assert.Equal("Every frame as a square, in rows like text", window.Model.Graphs.Forms.Single(f => f.Kind == GraphForm.Map).Description);
        window.Model.Graphs.SelectForm(GraphForm.Map);
        Assert.IsType<FrameMapGraph>(window.Graph.Graph);

        // The next entry is a map again, a category series and a numeric quantity alike.
        Choose(window, "Stereo Coding");
        Assert.True(Assert.IsType<FrameMapGraph>(window.Graph.Graph).IsCategory);
        Choose(window, "Reservoir Fill");
        Assert.False(Assert.IsType<FrameMapGraph>(window.Graph.Graph).IsCategory);

        // An entry without units has no map, and keeps its first form; the form is still remembered after it.
        GraphChoice summary = window.Model.Graphs.Choices.Single(c => c.Title == CodingSummaryGraph.TitleText);
        Assert.DoesNotContain(summary.Forms, f => f.Kind == GraphForm.Map);
        Choose(window, CodingSummaryGraph.TitleText);
        Assert.IsType<CodingSummaryGraph>(window.Graph.Graph);
        Choose(window, "Block Usage");
        Assert.IsType<FrameMapGraph>(window.Graph.Graph);

        // A file with no units offers none.
        Task load = window.Model.LoadAsync(Fixtures.Wav("external-pcm-s16-stereo-44100.wav"));
        UiPump.Until(() => load.IsCompleted);
        Assert.DoesNotContain(window.Model.Graphs.Choices.SelectMany(c => c.Forms), f => f.Kind == GraphForm.Map);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_square_selects_its_frame_and_hovering_says_what_it_shows()
    {
        DetailWindow window = Open(Vbr);
        Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        GraphView view = window.Graph;

        Point point = Center(view, window, 9);
        window.MouseMove(point);
        Assert.Equal(9, view.HoverIndex);
        Assert.Equal(map.Describe(9), view.HoverText);
        Assert.StartsWith("Frame 10 · 0x", view.HoverText, StringComparison.Ordinal);
        Assert.Contains(" kbps", view.HoverText, StringComparison.Ordinal);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        Assert.Equal(map.OffsetOf(9), window.Model.SelectedNode.Node.Offset);
        Assert.Equal(9, view.MarkedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void A_frame_selected_in_the_tree_is_ringed_and_scrolled_into_view_only_when_it_is_off_screen()
    {
        DetailWindow window = Open(Fixtures.Tta("built-many-small-frames.tta"), 640, 460);
        Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        GraphView view = window.Graph;
        (double Offset, double Maximum, double ViewHeight) scroll = view.MapScroll!.Value;
        Assert.True(scroll.Maximum > 0, $"The map must scroll for this test: {scroll}.");
        Assert.Equal(0, scroll.Offset);
        int last = map.Count - 1;
        Assert.Null(view.MapSquareRect(last));

        window.Model.SelectFrameAt(map.OffsetOf(last));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(last, view.MarkedIndex);
        Assert.NotNull(view.MapSquareRect(last));
        double scrolled = view.MapScroll!.Value.Offset;
        Assert.True(scrolled > 0);

        // Another frame on screen leaves the scroll where it is.
        int onScreen = Enumerable.Range(0, map.Count).First(s => view.MapSquareRect(s) is { Y: > 40 } && s != last);
        window.Model.SelectFrameAt(map.OffsetOf(onScreen));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(onScreen, view.MarkedIndex);
        Assert.Equal(scrolled, view.MapScroll!.Value.Offset);

        // A frame above the screen scrolls back to it.
        window.Model.SelectFrameAt(map.OffsetOf(0));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.NotNull(view.MapSquareRect(0));
        Assert.Equal(0, view.MapScroll!.Value.Offset);
        window.Close();
    }

    [AvaloniaFact]
    public void The_keys_move_the_cursor_by_a_frame_a_row_or_a_screen_and_enter_selects_what_it_shows()
    {
        FrameMapGraph map = Numbers(3000, i => 32 + (i % 9 * 32));
        (GraphView view, Window window) = Show(map, 900, 400);
        var clicked = new List<int>();
        view.PointClicked += (_, index) => clicked.Add(index);
        view.Focus();
        int columns = view.MapColumns!.Value;
        Assert.True(columns >= 10 && columns % 10 == 0);

        Press(window, Key.Right);
        Assert.Equal(0, view.HoverIndex);
        Press(window, Key.Right);
        Press(window, Key.Right);
        Assert.Equal(2, view.HoverIndex);
        Press(window, Key.Left);
        Assert.Equal(1, view.HoverIndex);
        Press(window, Key.Down);
        Assert.Equal(1 + columns, view.HoverIndex);
        Press(window, Key.Up);
        Assert.Equal(1, view.HoverIndex);
        Assert.Equal(map.Describe(1), view.HoverText);

        Press(window, Key.PageDown);
        int rowsOnScreen = (int)(view.MapScroll!.Value.ViewHeight / 12);
        Assert.Equal(1 + (rowsOnScreen * columns), view.HoverIndex);
        Assert.NotNull(view.MapSquareRect(view.HoverIndex!.Value));

        Press(window, Key.End);
        Assert.Equal(2999, view.HoverIndex);
        Assert.NotNull(view.MapSquareRect(2999));
        Assert.True(view.MapScroll!.Value.Offset > 0);
        Press(window, Key.Enter);
        Assert.Equal([2999], clicked);

        Press(window, Key.Home);
        Assert.Equal(0, view.HoverIndex);
        Assert.Equal(0, view.MapScroll!.Value.Offset);
        Press(window, Key.Left);
        Assert.Equal(0, view.HoverIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void The_columns_are_the_largest_multiple_of_10_that_fit_and_a_resize_reflows_in_steps_of_10()
    {
        FrameMapGraph map = Numbers(5000, i => i % 7);
        (GraphView view, Window window) = Show(map, 900, 400);
        var seen = new List<int>();
        for (int width = 200; width <= 1500; width += 37)
        {
            window.Width = width;
            UiPump.Until(() => true);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            int columns = view.MapColumns!.Value;
            seen.Add(columns);
            Assert.True(columns >= 10 && columns % 10 == 0, $"{columns} columns at {width}.");
            MapGeometry grid = MapGeometry.Of(view, columns);
            if (columns > 10)
            {
                Assert.True(grid.Right <= grid.Limit + 0.01, $"{columns} columns reach {grid.Right} past {grid.Limit} at {width}.");
                Assert.True(grid.Right + (10 * 12) > grid.Limit, $"{columns} columns leave room for 10 more at {width}.");
            }
        }

        Assert.Contains(10, seen);
        Assert.True(seen.Max() >= 100);
        Assert.Equal(seen.Order(), seen);
        window.Close();
    }

    // The right edge of the last column and the limit the columns had to fit in.
    private readonly record struct MapGeometry(double Right, double Limit)
    {
        public static MapGeometry Of(GraphView view, int columns)
        {
            Rect first = view.MapSquareRect(0)!.Value;
            Rect lane = view.MapLane!.Value;
            return new MapGeometry(first.X + (columns * 12) - 2, lane.X - 4 - 4);
        }
    }

    [AvaloniaFact]
    public void The_lane_beside_the_scrollbar_marks_the_findings_and_the_selected_frame_and_a_click_on_it_scrolls_there()
    {
        MapFinding[] findings = [new(100 * 200, Severity.Error, "Bad.", 1), new(2500 * 200, Severity.Warning, "Odd.", 40)];
        FrameMapGraph map = Numbers(3000, i => i % 5 * 10, findings: findings);
        (GraphView view, Window window) = Show(map, 900, 400);
        view.MarkedIndex = 1000;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var clicked = new List<int>();
        view.PointClicked += (_, index) => clicked.Add(index);

        IReadOnlyList<(double Y, Severity? Severity)> marks = view.MapLaneMarks()!;
        Assert.Equal([Severity.Error, Severity.Warning, null], marks.Select(m => m.Severity));
        Assert.True(marks[0].Y < marks[2].Y && marks[2].Y < marks[1].Y);
        Rect lane = view.MapLane!.Value;
        Assert.Equal(8, lane.Width);
        Assert.InRange(marks[1].Y / lane.Height, 2500.0 / 3000 - 0.02, 2500.0 / 3000 + 0.02);

        Point at = view.TranslatePoint(new Point(lane.Center.X, lane.Y + (lane.Height * 0.5)), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        (double Offset, double Maximum, double ViewHeight) scroll = view.MapScroll!.Value;
        Assert.Empty(clicked);
        int columns = view.MapColumns!.Value;
        double content = scroll.Maximum + scroll.ViewHeight;
        Assert.InRange(scroll.Offset + (scroll.ViewHeight / 2), (content * 0.5) - 24, (content * 0.5) + 24);

        // The filters of the Findings tab apply to the marks.
        view.ShownSeverities = SeverityMask.Warning;
        Assert.Equal([Severity.Warning, null], view.MapLaneMarks()!.Select(m => m.Severity));
        view.ShownSeverities = SeverityMask.None;
        Assert.Equal([null], view.MapLaneMarks()!.Select(m => m.Severity));
        Assert.True(columns > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void The_severity_filters_of_the_findings_tab_apply_to_the_rings_and_marks()
    {
        DetailWindow window = Open(Damaged);
        Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        GraphView view = window.Graph;
        int[] squares = map.FindingSquares();
        Assert.NotEmpty(squares);
        Assert.Equal(squares.Length, view.MapLaneMarks()!.Count(m => m.Severity is not null));

        window.Model.ShowErrors = false;
        window.Model.ShowWarnings = false;
        window.Model.ShowInfos = false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(SeverityMask.None, view.ShownSeverities);
        Assert.DoesNotContain(view.MapLaneMarks()!, m => m.Severity is not null);
        Assert.DoesNotContain("Warning:", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);
        Assert.DoesNotContain("with findings", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);

        window.Model.ShowErrors = true;
        window.Model.ShowWarnings = true;
        window.Model.ShowInfos = true;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Assert.Equal(squares.Length, view.MapLaneMarks()!.Count(m => m.Severity is not null));
        window.Close();
    }

    [AvaloniaFact]
    public void The_help_text_says_what_the_picture_shows_in_words()
    {
        DetailWindow window = Open(Damaged);
        Choose(window, "Bitrate", GraphForm.Map);
        GraphView view = window.Graph;
        var map = (FrameMapGraph)window.Graph.Graph!;
        string help = AutomationProperties.GetHelpText(view)!;

        Assert.Equal("Bitrate", AutomationProperties.GetName(view));
        Assert.StartsWith($"Frame map of {map.Count} frames, {view.MapColumns} to a row. ", help, StringComparison.Ordinal);
        Assert.Contains($"{map.KeyLow} to {map.KeyHigh}, darker is higher, {map.ClassCount} values", help, StringComparison.Ordinal);
        Assert.Contains($"{map.FindingSquares().Length} frame{(map.FindingSquares().Length == 1 ? string.Empty : "s")} with findings", help, StringComparison.Ordinal);

        window.MouseMove(Center(view, window, 3));
        Assert.EndsWith("Cursor: " + map.Describe(3), AutomationProperties.GetHelpText(view), StringComparison.Ordinal);

        Choose(window, "Block Usage");
        string legend = AutomationProperties.GetHelpText(window.Graph)!;
        Assert.Contains("Legend: ", legend, StringComparison.Ordinal);
        Assert.Contains("Long ", legend, StringComparison.Ordinal);
        Assert.Equal("Block Usage", AutomationProperties.GetName(window.Graph));
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_swatch_of_the_key_or_an_entry_of_the_legend_steps_through_its_frames()
    {
        DetailWindow window = Open(Vbr);
        Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        GraphView view = window.Graph;
        var swatches = view.MapItems()!.Where(i => i.IsSwatch).ToList();
        Assert.Equal(map.Key.Count, swatches.Count);

        var used = map.Key.First(k => k.Count > 1);
        Point at = view.TranslatePoint(swatches[used.Class].Rect.Center, window)!.Value;
        window.MouseMove(at);
        Assert.Equal(map.DescribeKeyEntry(used.Class), view.HoverText);
        Assert.Null(view.HoverIndex);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        long first = window.Model.SelectedNode!.Node.Offset;
        Assert.Equal(used.Class, map.ClassAt(map.IndexOfFrame(first)!.Value));
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        long second = window.Model.SelectedNode.Node.Offset;
        Assert.True(second > first);
        Assert.Equal(used.Class, map.ClassAt(map.IndexOfFrame(second)!.Value));

        Task load = window.Model.LoadAsync(ShortBlocks);
        UiPump.Until(() => load.IsCompleted);
        Choose(window, "Block Usage");
        var legend = window.Graph.MapItems()!.Where(i => i.IsSwatch).ToList();
        int category = legend.First(l => l.Label.StartsWith("Short ", StringComparison.Ordinal)).Entry;
        Point entry = window.Graph.TranslatePoint(legend.Single(l => l.Entry == category).Rect.Center, window)!.Value;
        window.MouseDown(entry, MouseButton.Left);
        window.MouseUp(entry, MouseButton.Left);
        var units = (FrameMapGraph)window.Graph.Graph!;
        Assert.Contains(Enumerable.Range(0, units.Count).Where(s => Enumerable.Range(0, 4).Any(v => units.CategorySeries![units.UnitOf(s), v] == category)).Select(units.OffsetOf), o => o == window.Model.SelectedNode!.Node.Offset);
        window.Close();
    }

    // The maps of real files, light and dark, saved with ENCSPECT_SCREENSHOTS: the block usage of a stream with short blocks (split squares),
    // the bitrate of a VBR stream (the declared steps, key and hover), the blocks of an AC-3 stream that couple (a share mix) and a damaged
    // stream whose findings ring frames.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheMaps(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        DetailWindow window = Open(ShortBlocks, 1180, 640);
        Choose(window, "Block Usage", GraphForm.Map);
        var blocks = (FrameMapGraph)window.Graph.Graph!;
        int shortFrame = Enumerable.Range(0, blocks.Count).First(s => Enumerable.Range(0, 4).Any(v => blocks.CategorySeries![blocks.UnitOf(s), v] == 2));
        window.Model.SelectFrameAt(blocks.OffsetOf(shortFrame));
        window.MouseMove(Center(window.Graph, window, shortFrame));
        ScreenshotTests.Save(window, $"map-block-usage-{variant}.png");
        window.Close();

        window = Open(Vbr, 1180, 640);
        Choose(window, "Bitrate", GraphForm.Map);
        var bitrate = (FrameMapGraph)window.Graph.Graph!;
        window.MouseMove(Center(window.Graph, window, 30));
        ScreenshotTests.Save(window, $"map-bitrate-{variant}.png");
        window.Close();

        window = Open(Coupling, 1180, 640);
        Choose(window, "Blocks With Coupling", GraphForm.Map);
        ScreenshotTests.Save(window, $"map-ac3-coupling-{variant}.png");
        window.Close();

        window = Open(Damaged, 1180, 640);
        Choose(window, "Bitrate", GraphForm.Map);
        var damaged = (FrameMapGraph)window.Graph.Graph!;
        window.MouseMove(Center(window.Graph, window, damaged.FindingSquares()[0]));
        ScreenshotTests.Save(window, $"map-findings-{variant}.png");
        window.Close();

        Assert.True(bitrate.Count > 30);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // ---- Speed and memory

    // What a map of many units may allocate beyond a map of few, while it is shown, drawn at 12 scroll positions and resized. The retained heap is
    // no measure: it moved by several MB between runs with the same map, from the platform's own surfaces. The bytes allocated on this thread
    // are exact, and a per-unit array would add 1 byte, 4 or 8 for each of the 1,970,000 units more (above all of the bound).
    private const long AllocationBound = 512L << 10;

    [AvaloniaFact]
    public void Two_million_numeric_units_draw_while_scrolling_and_resizing_in_the_time_of_a_screenful_and_without_a_per_unit_array()
    {
        FrameMapGraph few = Numbers(30_000, i => 32 + (i * 7 % 23 * 16));
        FrameMapGraph many = Numbers(2_000_000, i => 32 + (i * 7 % 23 * 16));
        _ = Measure(few);
        Measured baseline = Measure(few);
        Measured run = Measure(many);

        long extra = run.Allocated - baseline.Allocated;
        Assert.Equal(8, many.ClassCount);
        Assert.True(run.Times.Average() < 500, $"{run.Times.Average()} ms a frame.");
        Assert.True(run.MostRectangles <= run.SquaresOnScreen, $"{run.MostRectangles} rectangles for {run.SquaresOnScreen} squares on screen.");
        Assert.True(run.MostRectangles > 0);
        Assert.True(extra < AllocationBound, $"2,000,000 units allocated {extra} bytes more than 30,000.");
        Record($"FRAMEMAP numeric: {run.Times.Average():F1} ms a frame (max {run.Times.Max():F1}), {run.MostRectangles} rectangles of {run.SquaresOnScreen} squares on screen, allocated {run.Allocated} against {baseline.Allocated} for 30,000 units (+{extra}).");
    }

    [AvaloniaFact]
    public void Two_hundred_thousand_units_of_four_values_draw_as_split_squares_in_the_same_bounds()
    {
        FrameMapGraph few = Split(20_000);
        FrameMapGraph many = Split(200_000);
        _ = Measure(few);
        Measured baseline = Measure(few);
        Measured run = Measure(many);

        long extra = run.Allocated - baseline.Allocated;
        Assert.True(run.Times.Average() < 500, $"{run.Times.Average()} ms a frame.");
        Assert.True(run.MostRectangles <= run.SquaresOnScreen * 4, $"{run.MostRectangles} rectangles for {run.SquaresOnScreen} squares of 4 cells.");
        Assert.True(run.MostRectangles > run.SquaresOnScreen, "The split squares were not drawn as cells.");
        Assert.True(extra < AllocationBound, $"200,000 units allocated {extra} bytes more than 20,000.");
        Record($"FRAMEMAP split: {run.Times.Average():F1} ms a frame (max {run.Times.Max():F1}), {run.MostRectangles} rectangles of {run.SquaresOnScreen} squares on screen, allocated {run.Allocated} against {baseline.Allocated} for 20,000 units (+{extra}).");
    }

    private static FrameMapGraph Split(int units)
    {
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4);
        var random = new Random(5);
        for (int unit = 0; unit < units; unit++)
        {
            builder.Add(random.Next(40) == 0 ? [0, 0, 2, 2] : [0, 0, 0, 0]);
        }

        CategorySeries series = builder.Build(Enumerable.Range(0, units).Select(i => i * 200.0).ToArray(), null, null, "d");
        return new FrameMapGraph(series, series.X);
    }

    private readonly record struct Measured(long Allocated, double[] Times, int MostRectangles, int SquaresOnScreen);

    private static Measured Measure(FrameMapGraph map)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        if (!map.IsCategory)
        {
            _ = map.ClassCount;
        }

        (GraphView view, Window window) = Show(map, 1000, 600);
        var times = new List<double>();
        int mostRectangles = 0;
        int squaresOnScreen = 0;
        double maximum = view.MapScroll!.Value.Maximum;
        Assert.True(maximum > 2000, $"The map is {maximum} high.");
        Point at = new(view.Bounds.Width / 2, view.Bounds.Height / 2);
        for (int step = 0; step < 12; step++)
        {
            window.Width = 1000 + (step % 3 * 60);
            double target = view.MapScroll!.Value.Maximum * step / 11;
            window.MouseWheel(at, new Vector(0, -(target - view.MapScroll!.Value.Offset) / (12 * 3)));
            UiPump.Until(() => true);
            var clock = Stopwatch.StartNew();
            view.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.NotNull(window.CaptureRenderedFrame());
            times.Add(clock.Elapsed.TotalMilliseconds);

            int columns = view.MapColumns!.Value;
            int rows = (int)Math.Ceiling(view.MapScroll!.Value.ViewHeight / 12) + 1;
            squaresOnScreen = Math.Max(squaresOnScreen, rows * columns);
            mostRectangles = Math.Max(mostRectangles, view.MapRectanglesDrawn);
        }

        Assert.True(view.MapScroll!.Value.Offset > view.MapScroll!.Value.Maximum * 0.5, "The map did not scroll to far rows.");
        window.Close();
        return new Measured(GC.GetAllocatedBytesForCurrentThread() - start, [.. times], mostRectangles, squaresOnScreen);
    }

    // ---- Colors

    // Sampled at the middle of each square: a numeric map gives the ramps' steps and a category map the palette's colors, in both modes.
    [AvaloniaTheory]
    [InlineData("light", 5)]
    [InlineData("dark", 5)]
    [InlineData("light", 12)]
    [InlineData("dark", 12)]
    [InlineData("light", 30)]
    [InlineData("dark", 30)]
    public void The_drawn_squares_are_the_steps_of_the_ramps(string variant, int distinct)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        bool isDark = variant == "dark";
        FrameMapGraph map = Numbers(distinct * 2, i => 10 + (i % distinct * 10));
        (GraphView view, Window window) = Show(map, 900, 300);
        Assert.True(view.TryFindResource("SystemRegionBrush", view.ActualThemeVariant, out object? region));
        Color surface = ((ISolidColorBrush)region!).Color;
        Assert.Equal(isDark ? Colors.Black : Colors.White, surface);

        Color[] expected = MapColors.Classes(map.ClassCount, map.IsMultiHue, isDark);
        Assert.Equal(distinct > 16 ? 8 : distinct, expected.Length);
        Assert.Equal(distinct is >= 9 and <= 16, map.IsMultiHue);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        for (int square = 0; square < map.Count; square++)
        {
            Assert.Equal(expected[map.ClassAt(square)], Pixel(frame, Center(view, window, square)));
        }

        // A higher value has more contrast against the surface, in both modes.
        double[] contrast = [.. expected.Select(c => CategoryColors.ContrastRatio(c, surface))];
        Assert.Equal(contrast.Order(), contrast);
        Assert.True(contrast[0] >= 1.5, $"The lowest class has {contrast[0]}:1.");
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_drawn_squares_of_a_category_series_are_the_colors_of_the_palette(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        bool isDark = variant == "dark";
        var builder = new CategorySeriesBuilder("Modes", CategoryTestData.Numbered("Mode", 8), "frame", 1);
        for (int unit = 0; unit < 16; unit++)
        {
            builder.Add((byte)(unit % 8));
        }

        CategorySeries series = builder.Build(Enumerable.Range(0, 16).Select(i => i * 100.0).ToArray(), null, null, "Modes.");
        (GraphView view, Window window) = Show(new FrameMapGraph(series, series.X), 900, 300);
        CategoryStyle[] expected = CategoryColors.For(series, isDark, isDark ? Colors.Black : Colors.White);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        for (int square = 0; square < 16; square++)
        {
            Assert.Equal(expected[square % 8].Color, Pixel(frame, Center(view, window, square)));
        }

        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void A_split_square_is_four_cells_in_reading_order_and_a_share_mix_stacks_the_categories(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        bool isDark = variant == "dark";
        var four = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule and channel", 4);
        four.Add([0, 1, 2, 4]);
        CategorySeries split = four.Build(new double[] { 100 }, null, null, "d");
        var mix = new CategorySeriesBuilder("Blocks", CategoryTestData.BlockTypes, "block", 6);
        mix.Add([0, 0, 0, 0, 2, 2]);
        CategorySeries mixed = mix.Build(new double[] { 100 }, null, null, "d");

        (GraphView view, Window window) = Show(new FrameMapGraph(split, split.X), 600, 300);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        Color surface = isDark ? Colors.Black : Colors.White;
        CategoryStyle[] styles = CategoryColors.For(split, isDark, surface);
        Rect rect = view.MapSquareRect(0)!.Value;
        Assert.Equal(10, rect.Width);
        Point origin = view.TranslatePoint(rect.TopLeft, window)!.Value;
        Assert.Equal(styles[0].Color, Pixel(frame, origin + new Point(2, 2)));
        Assert.Equal(styles[1].Color, Pixel(frame, origin + new Point(8, 2)));
        Assert.Equal(styles[2].Color, Pixel(frame, origin + new Point(2, 8)));
        Assert.Equal(styles[4].Color, Pixel(frame, origin + new Point(8, 8)));
        window.Close();

        (view, window) = Show(new FrameMapGraph(mixed, mixed.X), 600, 300);
        frame = window.CaptureRenderedFrame()!;
        styles = CategoryColors.For(mixed, isDark, surface);
        origin = view.TranslatePoint(view.MapSquareRect(0)!.Value.TopLeft, window)!.Value;
        Assert.Equal(styles[2].Color, Pixel(frame, origin + new Point(5, 1)));
        Assert.Equal(styles[0].Color, Pixel(frame, origin + new Point(5, 8)));
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void A_ringed_square_shows_a_ring_in_the_text_color_and_a_surface_line_inside_it_and_the_selected_one_an_accent_ring_outside(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        bool isDark = variant == "dark";
        FrameMapGraph map = Numbers(30, i => 32 + (i % 3 * 32), findings: [new(200 * 4, Severity.Warning, "Odd.", 1)]);
        (GraphView view, Window window) = Show(map, 900, 300);
        view.MarkedIndex = 10;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        Color text = isDark ? Colors.White : Colors.Black;
        Color surface = isDark ? Colors.Black : Colors.White;

        Point ringed = view.TranslatePoint(view.MapSquareRect(4)!.Value.TopLeft, window)!.Value;
        Assert.True(Near(text, Pixel(frame, ringed + new Point(0.5, 5))), "The ring is not in the text color.");
        Color fill = Pixel(frame, ringed + new Point(5, 5));
        Assert.True(Distance(surface, Pixel(frame, ringed + new Point(2.5, 5))) < Distance(surface, fill), "The line inside the ring is not the surface.");
        Point selected = view.TranslatePoint(view.MapSquareRect(10)!.Value.TopLeft, window)!.Value;
        Color accent = Pixel(frame, selected + new Point(-1, 5));
        Assert.NotEqual(surface, accent);
        Assert.NotEqual(text, accent);
        Assert.Equal(accent, Pixel(frame, selected + new Point(5, -1)));
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static bool Near(Color expected, Color actual) => Distance(expected, actual) < 30;

    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    // With ENCSPECT_MAPBENCH set to a file, the measured figures are appended to it.
    private static void Record(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ENCSPECT_MAPBENCH") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private static Color Pixel(WriteableBitmap bitmap, Point at)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        int x = (int)at.X;
        int y = (int)at.Y;
        unsafe
        {
            byte* pixel = (byte*)buffer.Address + (y * buffer.RowBytes) + (x * 4);
            return buffer.Format == PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
        }
    }
}
