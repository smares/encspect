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
        Assert.Contains($"{map.KeyRange}, darker is higher, {map.ClassCount} values", help, StringComparison.Ordinal);
        Assert.Matches(@"\d+ to \d+ kbps, darker", help);
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

    // What one frame of the map may allocate on this thread (an invalidation, a render and a capture of the frame): the figures measured on
    // this machine were 94 to 95 KB for the numeric map and 145 to 149 KB for the split squares over four runs, whatever the number of
    // units, and the bounds are about a third above them. The comparison above cancels what is allocated for each square drawn, since both
    // maps draw the same squares, so this bound is what catches it: a double[4] for each square drawn adds about 215 KB to a frame.
    private const long NumericBytesPerFrame = 128L << 10;
    private const long SplitBytesPerFrame = 192L << 10;

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
        Record($"FRAMEMAP numeric: {run.Times.Average():F1} ms a frame (max {run.Times.Max():F1}), {run.MostRectangles} rectangles of {run.SquaresOnScreen} squares on screen, allocated {run.Allocated} against {baseline.Allocated} for 30,000 units (+{extra}), {run.BytesPerFrame} bytes a frame.");
        Assert.True(run.BytesPerFrame < NumericBytesPerFrame, $"A frame allocated {run.BytesPerFrame} bytes for {run.MostRectangles} rectangles.");
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
        Record($"FRAMEMAP split: {run.Times.Average():F1} ms a frame (max {run.Times.Max():F1}), {run.MostRectangles} rectangles of {run.SquaresOnScreen} squares on screen, allocated {run.Allocated} against {baseline.Allocated} for 20,000 units (+{extra}), {run.BytesPerFrame} bytes a frame.");
        Assert.True(run.BytesPerFrame < SplitBytesPerFrame, $"A frame allocated {run.BytesPerFrame} bytes for {run.MostRectangles} rectangles.");
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

    private readonly record struct Measured(long Allocated, double[] Times, int MostRectangles, int SquaresOnScreen, long BytesPerFrame);

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
        long drawn = 0;
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
            long before = GC.GetAllocatedBytesForCurrentThread();
            view.InvalidateVisual();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.NotNull(window.CaptureRenderedFrame());
            drawn += GC.GetAllocatedBytesForCurrentThread() - before;
            times.Add(clock.Elapsed.TotalMilliseconds);

            int columns = view.MapColumns!.Value;
            int rows = (int)Math.Ceiling(view.MapScroll!.Value.ViewHeight / 12) + 1;
            squaresOnScreen = Math.Max(squaresOnScreen, rows * columns);
            mostRectangles = Math.Max(mostRectangles, view.MapRectanglesDrawn);
        }

        Assert.True(view.MapScroll!.Value.Offset > view.MapScroll!.Value.Maximum * 0.5, "The map did not scroll to far rows.");
        window.Close();
        return new Measured(GC.GetAllocatedBytesForCurrentThread() - start, [.. times], mostRectangles, squaresOnScreen, drawn / 12);
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
        Assert.True(contrast[0] >= 2.0, $"The lowest class has {contrast[0]}:1, below the validator's 2:1 floor.");
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

    // ---- Layout, hover and keys

    private static void Settle()
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    [AvaloniaFact]
    public void The_tooltip_never_covers_the_square_it_describes_in_the_first_row_the_last_row_or_at_the_edges()
    {
        MapFinding[] findings = [new(0, Severity.Warning, "A finding whose message is long enough to need a line of its own in the box.", 50)];
        FrameMapGraph map = Numbers(3000, i => 32 + (i % 9 * 32), findings: findings);
        (GraphView view, Window window) = Show(map, 900, 400);
        int columns = view.MapColumns!.Value;

        void Check(int square, bool? isAbove = null)
        {
            window.MouseMove(Center(view, window, square));
            Assert.Equal(square, view.HoverIndex);
            Rect box = view.MapTooltipRect() ?? throw new InvalidOperationException("No tooltip.");
            Rect rect = view.MapSquareRect(square)!.Value;
            Assert.False(box.Intersects(rect), $"The tooltip {box} covers square {square} at {rect}.");
            Assert.True(new Rect(view.Bounds.Size).Contains(box), $"The tooltip {box} leaves the view {view.Bounds}.");
            if (isAbove is { } above)
            {
                Assert.Equal(above, box.Bottom <= rect.Y);
            }
        }

        // The first row has no room above: the box goes below, with the finding's second line in it.
        Check(0, isAbove: false);
        Check(columns / 2, isAbove: false);
        Check(columns - 1, isAbove: false);
        Check(5 * columns, isAbove: true);
        Check((5 * columns) + columns - 1, isAbove: true);

        window.MouseWheel(new Point(300, 200), new Vector(0, -1000));
        Settle();
        int last = map.Count - 1;
        Check(last);
        Check(last / columns * columns);
        Check(last - columns);

        // The key's swatches have a box too, clear of the swatch.
        MapFinding[] none = [];
        (view, window) = Show(Numbers(60, i => 32 + (i % 9 * 32), findings: none), 900, 400);
        foreach (var swatch in view.MapItems()!.Where(i => i.IsSwatch))
        {
            window.MouseMove(view.TranslatePoint(swatch.Rect.Center, window)!.Value);
            Rect box = view.MapTooltipRect()!.Value;
            Assert.False(box.Intersects(swatch.Rect), $"The tooltip {box} covers the swatch {swatch.Rect}.");
        }

        window.Close();
    }

    [Fact]
    public void A_tooltip_is_placed_above_then_below_then_beside_and_stays_inside_the_view()
    {
        Size size = new(200, 40);
        Size bounds = new(600, 300);
        foreach (double y in new[] { 0.0, 10, 100, 280 })
        {
            foreach (double x in new[] { 0.0, 100, 395, 590 })
            {
                Rect target = new(x, y, 10, 10);
                Rect box = GraphView.PlaceTooltip(size, target, bounds);
                Assert.False(box.Intersects(target), $"{box} covers {target}.");
                Assert.True(box.X >= 2 && box.Right <= bounds.Width - 2, $"{box} leaves the width.");
            }
        }

        Assert.True(GraphView.PlaceTooltip(size, new Rect(100, 100, 10, 10), bounds).Bottom <= 100);
        Assert.True(GraphView.PlaceTooltip(size, new Rect(100, 10, 10, 10), bounds).Y >= 20);
    }

    [AvaloniaFact]
    public void The_lane_marks_a_finding_where_its_row_is_when_the_file_fits_on_screen()
    {
        FrameMapGraph probe = Numbers(10, i => i);
        (GraphView view, Window window) = Show(probe, 420, 400);
        int columns = view.MapColumns!.Value;
        window.Close();

        int square = (2 * columns) + 3;
        FrameMapGraph map = Numbers(5 * columns, i => i % 4, findings: [new(square * 200, Severity.Error, "Bad.", 1)]);
        (view, window) = Show(map, 420, 400);
        Assert.Equal(0, view.MapScroll!.Value.Maximum);
        Rect lane = view.MapLane!.Value;
        double y = view.MapLaneMarks()!.Single(m => m.Severity == Severity.Error).Y;
        Assert.Equal(view.MapSquareRect(square)!.Value.Y - lane.Y, y, 0.01);
        Assert.InRange(y, 20, 40);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_half_visible_square_in_the_bottom_row_scrolls_the_least_and_does_not_recenter()
    {
        FrameMapGraph map = Numbers(6000, i => i % 7);
        (GraphView view, Window window) = Show(map, 900, 400);
        view.PointClicked += (_, index) => view.MarkedIndex = index;
        int columns = view.MapColumns!.Value;
        Rect lane = view.MapLane!.Value;
        Point at = new(300, 200);
        window.MouseWheel(at, new Vector(0, -10));
        Settle();

        int partial = -1;
        for (int step = 0; step < 12 && partial < 0; step++)
        {
            for (int row = 0; row < 60 && partial < 0; row++)
            {
                int s = (((int)(view.MapScroll!.Value.Offset / 12) + row) * columns) + 1;
                if (view.MapSquareRect(s) is { } rect && rect.Bottom > lane.Bottom + 2 && rect.Y < lane.Bottom - 4)
                {
                    partial = s;
                }
            }

            if (partial < 0)
            {
                window.MouseWheel(at, new Vector(0, -0.1));
                Settle();
            }
        }

        Assert.True(partial >= 0, "No square was half visible.");
        double before = view.MapScroll!.Value.Offset;
        Rect square = view.MapSquareRect(partial)!.Value;
        Point click = view.TranslatePoint(new Point(square.Center.X, (square.Y + lane.Bottom) / 2), window)!.Value;
        window.MouseDown(click, MouseButton.Left);
        window.MouseUp(click, MouseButton.Left);
        Settle();

        Assert.Equal(partial, view.MarkedIndex);
        double moved = view.MapScroll!.Value.Offset - before;
        Assert.InRange(moved, 0, 16);
        Assert.True(view.MapSquareRect(partial)!.Value.Bottom <= lane.Bottom + 0.01, "The square is not shown whole.");
        window.Close();
    }

    [AvaloniaFact]
    public void A_resize_keeps_the_top_frame_at_the_top_and_a_selected_frame_on_screen()
    {
        FrameMapGraph map = Numbers(8000, i => i % 7);
        (GraphView view, Window window) = Show(map, 900, 400);
        Point at = new(300, 200);
        window.MouseWheel(at, new Vector(0, -20));
        Settle();
        int columns = view.MapColumns!.Value;
        Rect lane = view.MapLane!.Value;
        int topFrame = (int)Math.Round(view.MapScroll!.Value.Offset / 12) * columns;
        Assert.True(topFrame > 0);

        window.Width = 520;
        Settle();
        int narrower = view.MapColumns!.Value;
        Assert.True(narrower < columns, $"{narrower} columns after {columns}.");
        Assert.Equal(lane.Y + 4, view.MapSquareRect(topFrame)!.Value.Y, 0.01);

        window.Width = 1100;
        Settle();
        Assert.True(view.MapColumns!.Value > narrower);
        Assert.Equal(lane.Y + 4, view.MapSquareRect(topFrame)!.Value.Y, 0.01);

        // A selected frame far down the screen stays on it when the rows reflow.
        int selected = topFrame + (20 * view.MapColumns!.Value) + 2;
        view.MarkedIndex = selected;
        Settle();
        Rect shown = view.MapSquareRect(selected)!.Value;
        window.Width = 420;
        Settle();
        Rect after = view.MapSquareRect(selected) ?? throw new InvalidOperationException("The selected frame left the screen.");
        Assert.True(after.Y >= lane.Y && after.Bottom <= lane.Bottom + 0.01, $"{after} is not inside the lane {lane}; it was {shown}.");
        window.Close();
    }

    [AvaloniaFact]
    public void A_pane_too_narrow_for_ten_columns_clips_the_squares_before_the_lane_and_says_so()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        FrameMapGraph map = Numbers(5000, i => 32 + (i % 9 * 32));
        (GraphView view, Window window) = Show(map, 190, 400);
        Assert.Equal(10, view.MapColumns);
        Assert.Equal("Widen the pane to see 10 columns", view.MapCaption);
        Assert.Contains("Widen the pane to see 10 columns", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);

        Rect lane = view.MapLane!.Value;
        Rect first = view.MapSquareRect(9)!.Value;
        Assert.True(first.Right > lane.X, "The test needs squares that reach the lane.");
        Color surface = Colors.White;
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        for (double x = lane.X - 5; x < lane.X; x += 1)
        {
            Assert.Equal(surface, Pixel(frame, view.TranslatePoint(new Point(x, first.Center.Y), window)!.Value));
        }

        window.MouseMove(view.TranslatePoint(new Point(lane.X - 3, first.Center.Y), window)!.Value);
        Assert.Null(view.HoverIndex);

        // Wide enough, the caption is the ordinary one.
        window.Width = 900;
        Settle();
        Assert.Equal("One square per frame", view.MapCaption);
        Assert.DoesNotContain("Widen the pane", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [Fact]
    public void The_cells_of_a_split_square_differ_by_at_most_one_physical_pixel_and_fill_it_exactly()
    {
        for (int size = 5; size <= 60; size++)
        {
            foreach (int cells in new[] { 2, 3 })
            {
                foreach (int gap in new[] { 0, 1 })
                {
                    int end = 0;
                    int[] widths = new int[cells];
                    for (int i = 0; i < cells; i++)
                    {
                        (int start, int width) = GraphView.SplitCell(size, cells, i, gap);
                        widths[i] = width;
                        Assert.Equal(i == 0 ? 0 : end + gap, start);

                        end = start + width;
                    }

                    Assert.Equal(size, end);
                    Assert.True(widths.Max() - widths.Min() <= 1, $"{string.Join(",", widths)} in {size} px.");
                }
            }
        }

        // The cases of the review: 4 and 5 px at 100 % and three values in 10 px.
        Assert.Equal([(0, 4), (5, 5)], Enumerable.Range(0, 2).Select(i => GraphView.SplitCell(10, 2, i, 1)));
        Assert.Equal([(0, 2), (3, 3), (7, 3)], Enumerable.Range(0, 3).Select(i => GraphView.SplitCell(10, 3, i, 1)));
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void At_any_scaling_every_square_has_the_same_whole_pixel_size_and_pitch_and_three_values_make_cells_within_a_pixel(double scaling)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var builder = new CategorySeriesBuilder("Block Usage", CategoryTestData.BlockTypes, "granule", 3);
        for (int unit = 0; unit < 30; unit++)
        {
            builder.Add([0, 1, 2]);
        }

        CategorySeries series = builder.Build(Enumerable.Range(0, 30).Select(i => i * 100.0).ToArray(), null, null, "d");
        (GraphView view, Window window) = Show(new FrameMapGraph(series, series.X), 900, 300);
        window.SetRenderScaling(scaling);
        Settle();
        Assert.Equal(scaling, window.RenderScaling);

        int pitchPx = (int)Math.Round(12 * scaling, MidpointRounding.AwayFromZero);
        int gapPx = (int)Math.Round(2 * scaling, MidpointRounding.AwayFromZero);
        int sizePx = pitchPx - gapPx;
        Rect first = view.MapSquareRect(0)!.Value;
        for (int square = 0; square < 10; square++)
        {
            Rect rect = view.MapSquareRect(square)!.Value;
            Assert.Equal(sizePx, rect.Width * scaling, 1e-6);
            Assert.Equal(sizePx, rect.Height * scaling, 1e-6);
            Assert.Equal(Math.Round(rect.X * scaling), rect.X * scaling, 1e-6);
            Assert.Equal(Math.Round(rect.Y * scaling), rect.Y * scaling, 1e-6);
            Assert.Equal(square * pitchPx, (rect.X - first.X) * scaling, 1e-6);
        }

        // The pixels of a row through the middle of the first square: three runs of its three colors, a surface pixel between them.
        CategoryStyle[] styles = CategoryColors.For(series, false, Colors.White);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        Point origin = view.TranslatePoint(first.TopLeft, window)!.Value;
        int x0 = (int)Math.Round(origin.X * scaling);
        int y = (int)Math.Round(origin.Y * scaling) + (sizePx / 2);
        var runs = new List<int>();
        int current = -1;
        for (int x = x0; x < x0 + sizePx; x++)
        {
            Color color = Pixel(frame, new Point(x, y));
            int category = Array.FindIndex(styles, 0, 3, st => st.Color == color);
            if (category >= 0 && category == current)
            {
                runs[^1]++;
            }
            else if (category >= 0)
            {
                runs.Add(1);
            }

            current = category;
        }

        Assert.Equal(3, runs.Count);
        Assert.Equal(sizePx - 2, runs.Sum());
        Assert.True(runs.Max() - runs.Min() <= 1, $"Cells of {string.Join(", ", runs)} px at {scaling}.");
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public void The_hover_follows_the_rows_that_move_under_a_pointer_that_does_not()
    {
        FrameMapGraph map = Numbers(3000, i => 32 + (i % 9 * 32));
        (GraphView view, Window window) = Show(map, 900, 400);
        int columns = view.MapColumns!.Value;
        Point at = Center(view, window, (4 * columns) + 6);
        window.MouseMove(at);
        Assert.Equal((4 * columns) + 6, view.HoverIndex);

        window.MouseWheel(at, new Vector(0, -1));
        Settle();
        int expected = (7 * columns) + 6;
        Assert.Equal(expected, view.HoverIndex);
        Assert.Equal(map.Describe(expected), view.HoverText);
        Assert.EndsWith("Cursor: " + map.Describe(expected), AutomationProperties.GetHelpText(view), StringComparison.Ordinal);

        window.MouseWheel(at, new Vector(0, 1));
        Settle();
        Assert.Equal((4 * columns) + 6, view.HoverIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_the_finding_entry_of_the_key_steps_through_the_ringed_frames_and_wraps_around()
    {
        DetailWindow window = Open(Damaged);
        Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        long[] ringed = [.. map.FindingSquares().Select(map.OffsetOf)];
        Assert.True(ringed.Length >= 2);
        var entry = window.Graph.MapItems()!.Single(i => i.Label == "Finding (first case)");
        Point at = window.Graph.TranslatePoint(entry.Rect.Center, window)!.Value;

        var visited = new List<long>();
        for (int click = 0; click < ringed.Length + 2; click++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            visited.Add(window.Model.SelectedNode!.Node.Offset);
        }

        Assert.Equal(Enumerable.Range(0, ringed.Length + 2).Select(i => ringed[i % ringed.Length]), visited);

        // A severity hidden in the Findings tab is skipped.
        window.Model.ShowErrors = false;
        window.Model.ShowInfos = false;
        Settle();
        long[] warned = [.. map.FindingSquares(SeverityMask.Warning).Select(map.OffsetOf)];
        if (warned.Length > 0)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Assert.Contains(window.Model.SelectedNode!.Node.Offset, warned);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void The_no_value_entry_of_a_category_map_has_its_count_and_those_squares_are_empty()
    {
        double[] frames = [.. Enumerable.Range(0, 6).Select(i => i * 200.0)];
        var builder = new CategorySeriesBuilder("Modes", CategoryTestData.StereoCodings, "frame", 1);
        builder.Add(0);
        builder.Add(CategorySeries.None);
        builder.Add(1);
        builder.Add(CategorySeries.None);
        CategorySeries series = builder.Build(new[] { frames[0], frames[1], frames[2], frames[4] }, null, null, "d");
        var map = new FrameMapGraph(series, frames);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        (GraphView view, Window window) = Show(map, 900, 300);

        var labels = view.MapItems()!.Select(i => i.Label).ToList();
        Assert.Contains("No value · 4 frames", labels);

        Color surface = Colors.White;
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        foreach (int empty in new[] { 1, 3, 4, 5 })
        {
            Assert.Equal(surface, Pixel(frame, Center(view, window, empty)));
        }

        Assert.NotEqual(surface, Pixel(frame, Center(view, window, 0)));
        Assert.EndsWith("no category", map.Describe(1), StringComparison.Ordinal);
        Assert.EndsWith("no value", map.Describe(3), StringComparison.Ordinal);
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void A_declared_step_the_file_does_not_use_is_an_outline_of_its_color_with_nothing_inside_in_both_modes(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        bool isDark = variant == "dark";
        FrameMapGraph map = Numbers(40, i => i % 2 == 0 ? 64 : 128, range: ValueRange.OfSteps([32, 64, 96, 128, 160]));
        (GraphView view, Window window) = Show(map, 900, 300);
        Color surface = isDark ? Colors.Black : Colors.White;
        Color[] colors = MapColors.Classes(5, false, isDark);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        var swatches = view.MapItems()!.Where(i => i.IsSwatch).ToList();
        Assert.Equal(5, swatches.Count);

        for (int step = 0; step < 5; step++)
        {
            Rect rect = swatches[step].Rect;
            Point origin = view.TranslatePoint(rect.TopLeft, window)!.Value;
            Point middle = view.TranslatePoint(rect.Center, window)!.Value;
            if (step is 1 or 3)
            {
                Assert.Equal(colors[step], Pixel(frame, middle));
                continue;
            }

            Assert.Equal(surface, Pixel(frame, middle));
            // The outline is 1.5 pixels wide at a fractional position, so the best pixel of its edge is covered by 3/4 or more.
            int closest = Enumerable.Range(-1, 5).Min(dx => Distance(colors[step], Pixel(frame, new Point(Math.Floor(origin.X) + dx, middle.Y))));
            Assert.True(closest <= Distance(colors[step], surface) / 3, $"The unused step {step} has no outline in its color ({closest}).");
            Assert.Contains("0 frames", map.DescribeKeyEntry(step), StringComparison.Ordinal);
        }

        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
