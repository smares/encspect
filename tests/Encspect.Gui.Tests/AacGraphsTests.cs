using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The Graphs tab of real AAC files, with the series the AAC parser itself derives (docs/design/coding-graphs.md, rollout step 3), not the
// test-only ones of CategoryTestData: the entries under their headings, a variable layout of a file with several channel elements, and,
// with ENCSPECT_SCREENSHOTS set, the views of files that have short windows, mid/side and intensity stereo and six channels, in light and
// dark mode.
public class AacGraphsTests
{
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

    [AvaloniaFact]
    public void A_stereo_file_lists_its_graphs_under_the_same_headings_as_an_mpeg_audio_file()
    {
        DetailWindow window = Open(Fixtures.Aac("ffmpeg-adts.aac"));

        Assert.Equal(
            [
                ("Summary", "Coding Summary"),
                ("Stereo Coding", "Stereo Coding"),
                ("Block Switching", "Window Sequences"),
                ("Block Switching", "Block Sizes"),
                ("Coded Bandwidth", "Coded Bandwidth"),
                ("Bit Distribution", "Bitrate"),
            ],
            window.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        Assert.Equal(["Over Time", "Frames", "Summary"], window.Model.Graphs.Choices.Single(c => c.Title == "Window Sequences").Forms.Select(f => f.Title));
        Assert.Equal(["Per Frame", "Over Time"], window.Model.Graphs.Choices.Single(c => c.Title == "Coded Bandwidth").Forms.Select(f => f.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void A_mono_file_has_no_stereo_coding()
    {
        DetailWindow window = Open(Fixtures.Aac("ffmpeg-mono-faststart.m4a"));

        Assert.DoesNotContain(window.Model.Graphs.Choices, c => c.Title == "Stereo Coding");
        Assert.Contains(("Block Switching", "Window Sequences"), window.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_of_six_channels_stacks_the_channel_streams_of_each_unit_in_one_row()
    {
        DetailWindow window = Open(Fixtures.Aac("faac-2.2-he-51-adts.aac"));
        Choose(window, "Window Sequences", GraphForm.Frames);
        var graph = (CategoryGraph)window.Graph.Graph!;

        Assert.Equal(0, graph.Series.ValuesPerUnit);
        Assert.Equal(6, graph.Series.ValueCount(0));
        Assert.Equal(1, graph.Rows);

        // A variable layout is one row; the cell of a unit counts its channel streams, and a click on it selects its frame in the tree.
        Point point = ScreenshotTests.PointOver(window, 3);
        window.MouseMove(point);
        Assert.Equal(3, window.Graph.HoverIndex);
        Assert.StartsWith("Frame 4 · 0x", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.EndsWith("(6 values)", window.Graph.HoverText, StringComparison.Ordinal);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        Assert.Equal((long)graph.Series.X.Span[3], window.Model.SelectedNode.Node.Offset);

        // The pair classification of the two channel pair elements of 5.1 (a center, two pairs and an LFE channel).
        Choose(window, "Stereo Coding", GraphForm.Frames);
        Assert.Equal(1, ((CategoryGraph)window.Graph.Graph!).Rows);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheGraphsOfRealAacFiles(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var folder = new TempFolder();

        // A stereo stream of 10 seconds, the frames of one encoder's file repeated, so the over time views have columns to show.
        byte[] source = File.ReadAllBytes(Fixtures.Aac("ffmpeg-adts.aac"));
        string repeated = folder.Write("repeated.aac", RepeatFrames(source, 10 * 44100 / 1024));
        DetailWindow stereo = Open(repeated);
        Choose(stereo, "Coding Summary");
        ScreenshotTests.Save(stereo, $"aac-coding-summary-{variant}.png");
        Choose(stereo, "Stereo Coding", GraphForm.OverTime);
        ScreenshotTests.Save(stereo, $"aac-stereo-coding-over-time-{variant}.png");
        Choose(stereo, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"aac-stereo-coding-frames-{variant}.png");
        Choose(stereo, "Stereo Coding", GraphForm.Summary);
        ScreenshotTests.Save(stereo, $"aac-stereo-coding-summary-{variant}.png");
        Choose(stereo, "Window Sequences", GraphForm.OverTime);
        ScreenshotTests.Save(stereo, $"aac-window-sequences-over-time-{variant}.png");
        Choose(stereo, "Window Sequences", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"aac-window-sequences-frames-{variant}.png");
        Choose(stereo, "Block Sizes", GraphForm.Summary);
        ScreenshotTests.Save(stereo, $"aac-block-sizes-summary-{variant}.png");
        Choose(stereo, "Coded Bandwidth", GraphForm.OverTime);
        ScreenshotTests.Save(stereo, $"aac-coded-bandwidth-over-time-{variant}.png");
        Choose(stereo, "Coded Bandwidth", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"aac-coded-bandwidth-per-frame-{variant}.png");
        Choose(stereo, "Bitrate", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"aac-bitrate-{variant}.png");
        stereo.Close();

        // Apple's encoder: short windows in more than a third of its channel streams.
        DetailWindow apple = Open(Fixtures.Aac("apple-lc-adts-cbr-st44.aac"));
        Choose(apple, "Window Sequences", GraphForm.Frames);
        ScreenshotTests.Save(apple, $"aac-apple-window-sequences-frames-{variant}.png");
        Choose(apple, "Block Sizes", GraphForm.Summary);
        ScreenshotTests.Save(apple, $"aac-apple-block-sizes-summary-{variant}.png");
        apple.Close();

        // Six channels in an access unit: a row for each channel stream.
        DetailWindow surround = Open(Fixtures.Aac("faac-2.2-he-51-adts.aac"));
        Choose(surround, "Window Sequences", GraphForm.Frames);
        ScreenshotTests.Save(surround, $"aac-51-window-sequences-frames-{variant}.png");
        Choose(surround, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(surround, $"aac-51-stereo-coding-frames-{variant}.png");
        Choose(surround, "Coding Summary");
        ScreenshotTests.Save(surround, $"aac-51-coding-summary-{variant}.png");
        surround.Close();

        // The hand-built pairs of every stereo coding, with the reserved one.
        DetailWindow pairs = Open(Path.Combine(Fixtures.Directory, "adts", "stereo-coding.aac"));
        Choose(pairs, "Stereo Coding", GraphForm.Summary);
        ScreenshotTests.Save(pairs, $"aac-hand-built-stereo-coding-summary-{variant}.png");
        pairs.Close();
        DetailWindow reserved = Open(Path.Combine(Fixtures.Directory, "adts", "stereo-coding-reserved.aac"));
        Choose(reserved, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(reserved, $"aac-hand-built-reserved-frames-{variant}.png");
        reserved.Close();
        DetailWindow elements = Open(Path.Combine(Fixtures.Directory, "adts", "stereo-coding-elements.aac"));
        Choose(elements, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(elements, $"aac-hand-built-elements-stereo-coding-frames-{variant}.png");
        elements.Close();

        // FAAC's HE-AAC file, where ms_used sits on noise substitution bands, which the mid/side count leaves out.
        DetailWindow faac = Open(Fixtures.Aac("faac-2.0-he-24k.m4a"));
        Choose(faac, "Stereo Coding", GraphForm.Summary);
        ScreenshotTests.Save(faac, $"aac-faac-stereo-coding-summary-{variant}.png");
        Choose(faac, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(faac, $"aac-faac-stereo-coding-frames-{variant}.png");
        faac.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // The frames of an ADTS file, repeated until it has count of them.
    private static byte[] RepeatFrames(byte[] source, int count)
    {
        var stream = new MemoryStream();
        int frames = 0;
        while (frames < count)
        {
            for (int at = 0; at + 7 <= source.Length && frames < count; frames++)
            {
                int length = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
                stream.Write(source, at, length);
                at += length;
            }
        }

        return stream.ToArray();
    }
}

// The layout rules of the MPEG step (MpegGraphLayoutTests) on an AAC file: a legend of the categories that occur, the selected entry of the
// list in view, and a last interval as wide as it lasts.
public class AacGraphLayoutTests
{
    [AvaloniaFact]
    public void A_legend_of_a_chart_leaves_out_the_categories_that_do_not_occur()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Aac("ffmpeg-adts.aac"));
        AacGraphsTests.Choose(window, "Stereo Coding", GraphForm.OverTime);

        List<(string Label, int Category)> entries = GraphView.LegendEntries(window.Model.Graphs.Graph!);

        // The pairs of this file code mid/side stereo, or mid/side and intensity stereo: two of the five categories.
        // In the order of the stack, the largest share first.
        Assert.Equal([3, 1], entries.Select(e => e.Category));
        Assert.All(entries, e => Assert.DoesNotContain("0.0%", e.Label, StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void The_selected_entry_of_the_list_is_scrolled_into_view_in_a_short_window()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Aac("ffmpeg-adts.aac"), 1180, 560);

        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Last();
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        Button button = window.GraphChoices.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, window.Model.Graphs.Selected));
        double top = button.TranslatePoint(new Point(0, 0), window.GraphList)!.Value.Y;
        Assert.True(top >= -1 && top + button.Bounds.Height <= window.GraphList.Bounds.Height + 1, $"The entry is at {top} of a list {window.GraphList.Bounds.Height} high.");
        window.Close();
    }

    [AvaloniaFact]
    public void The_over_time_view_of_a_stream_of_two_seconds_has_two_columns_and_the_last_lasts_as_long_as_it_does()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Aac("ffmpeg-adts.aac"));
        AacGraphsTests.Choose(window, "Window Sequences", GraphForm.OverTime);
        var graph = (CategoryGraph)window.Graph.Graph!;

        Assert.Equal(2, graph.Series.Time!.Starts.Length);
        double duration = window.Model.Report!.Streams[0].Duration;
        GraphView.Slots slots = GraphView.SlotsOf(new Rect(0, 0, duration * 100, 100), 2, graph.Series.Time.Starts.Span, duration);
        Assert.Equal(100, slots.W(0), 6);
        Assert.Equal((duration - 1) * 100, slots.W(1), 6);
        window.Close();
    }
}
