using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The Graphs tab of real MPEG audio files, with the series the MPEG parser itself derives (docs/design/coding-graphs.md, rollout step 2),
// not the test-only ones of CategoryTestData: the entries under their headings, and, with ENCSPECT_SCREENSHOTS set, the views of
// files that have short blocks, joint stereo, dual channel and Layer II audio, in light and dark mode.
public class MpegGraphsTests
{
    private static DetailWindow Open(string fixture, double width = 1180, double height = 800)
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
    public void A_layer_III_joint_stereo_file_lists_its_graphs_under_the_shared_headings()
    {
        DetailWindow window = Open(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"));

        Assert.Equal(
            [
                ("Summary", "Coding Summary"),
                ("Stereo Coding", "Stereo Coding"),
                ("Stereo Coding", "Joint Stereo Modes"),
                ("Block Switching", "Block Usage"),
                ("Block Switching", "Block Sizes"),
                ("Bit Distribution", "Bitrate"),
                ("Bit Distribution", "Reservoir Fill"),
                ("Bit Distribution", "Channel Bit Share"),
                ("Other", "Channel Modes"),
            ],
            window.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        Assert.Equal(["Per Frame", "Over Time", "First and Second"], window.Model.Graphs.Choices.Single(c => c.Title == "Channel Bit Share").Forms.Select(f => f.Title));
        Assert.Equal(["Over Time", "Lines", "Columns", "Frames", "Summary"], window.Model.Graphs.Choices.Single(c => c.Title == "Block Usage").Forms.Select(f => f.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void A_layer_II_file_lists_its_coded_bandwidth_and_a_mono_file_has_no_channel_bit_share()
    {
        DetailWindow layer2 = Open(Fixtures.Mpeg("external-ffmpeg-mp2.mp2"));
        Assert.Contains(("Coded Bandwidth", "Coded Bandwidth"), layer2.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        Assert.DoesNotContain(layer2.Model.Graphs.Choices, c => c.Title == "Block Usage");
        layer2.Close();

        DetailWindow mono = Open(Fixtures.Mpeg("external-ffmpeg-mono.mp3"));
        Assert.DoesNotContain(mono.Model.Graphs.Choices, c => c.Title == "Channel Bit Share");
        Assert.Contains(mono.Model.Graphs.Choices, c => c.Title == "Block Usage");
        mono.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheGraphsOfRealMpegFiles(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Short blocks, mid/side stereo and the bit share of a joint stereo file.
        DetailWindow joint = Open(Fixtures.Mpeg("external-lame-joint-music.mp3"));
        Choose(joint, "Coding Summary");
        ScreenshotTests.Save(joint, $"mpeg-coding-summary-{variant}.png");
        Choose(joint, "Stereo Coding", GraphForm.OverTime);
        ScreenshotTests.Save(joint, $"mpeg-stereo-coding-over-time-{variant}.png");
        Choose(joint, "Stereo Coding", GraphForm.Frames);
        ScreenshotTests.Save(joint, $"mpeg-stereo-coding-frames-{variant}.png");
        Choose(joint, "Block Usage", GraphForm.OverTime);
        ScreenshotTests.Save(joint, $"mpeg-block-usage-over-time-{variant}.png");
        Choose(joint, "Block Usage", GraphForm.Frames);
        ScreenshotTests.Save(joint, $"mpeg-block-usage-frames-{variant}.png");
        Choose(joint, "Block Sizes", GraphForm.Summary);
        ScreenshotTests.Save(joint, $"mpeg-block-sizes-summary-{variant}.png");
        Choose(joint, "Channel Bit Share", GraphForm.Split);
        ScreenshotTests.Save(joint, $"mpeg-channel-bit-share-split-{variant}.png");
        Choose(joint, "Bitrate", GraphForm.Frames);
        ScreenshotTests.Save(joint, $"mpeg-bitrate-{variant}.png");
        joint.Close();

        // The same content as dual channel: the share of each channel's own bits.
        DetailWindow dual = Open(Fixtures.Mpeg("external-lame-dual-music.mp3"));
        Choose(dual, "Channel Bit Share", GraphForm.Split);
        ScreenshotTests.Save(dual, $"mpeg-dual-channel-bit-share-{variant}.png");
        Choose(dual, "Channel Bit Share", GraphForm.Frames);
        ScreenshotTests.Save(dual, $"mpeg-dual-channel-bit-share-per-frame-{variant}.png");
        Choose(dual, "Stereo Coding", GraphForm.Summary);
        ScreenshotTests.Save(dual, $"mpeg-dual-stereo-coding-summary-{variant}.png");
        dual.Close();

        // Layer II: the coded bandwidth and the bit share of a dual channel stream.
        DetailWindow layer2 = Open(Fixtures.Mpeg("external-twolame-dual-music.mp2"));
        Choose(layer2, "Coded Bandwidth", GraphForm.OverTime);
        ScreenshotTests.Save(layer2, $"mpeg-layer2-coded-bandwidth-{variant}.png");
        Choose(layer2, "Channel Bit Share", GraphForm.Split);
        ScreenshotTests.Save(layer2, $"mpeg-layer2-channel-bit-share-{variant}.png");
        Choose(layer2, "Coding Summary");
        ScreenshotTests.Save(layer2, $"mpeg-layer2-coding-summary-{variant}.png");
        layer2.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}

// The layout rules the review of the MPEG step found wanting: the summary table's columns, the legends, the list and the last interval.
public class MpegGraphLayoutTests
{
    [AvaloniaFact]
    public void The_count_and_the_share_of_a_summary_row_are_at_least_12_pixels_apart_whatever_the_count()
    {
        (double share, double count) = GraphView.SummaryColumns([231, 12_345, 3], 14);
        double widest = new FormattedText("100.0%", System.Globalization.CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight, Avalonia.Media.Typeface.Default, 14, Avalonia.Media.Brushes.Black).Width;
        double digits = new FormattedText("12,345", System.Globalization.CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight, Avalonia.Media.Typeface.Default, 14, Avalonia.Media.Brushes.Black).Width;

        // The share is right-aligned in its column and the count ends where the column starts, so the gap is the column less the text.
        Assert.True(share - widest >= 12, $"The gap after the count is {share - widest}.");
        Assert.True(count - digits >= 12, $"The count column leaves {count - digits} for the name.");
    }

    [AvaloniaFact]
    public void A_legend_of_a_chart_leaves_out_the_categories_that_do_not_occur()
    {
        DetailWindow window = Open(Fixtures.Mpeg("external-lame-joint-music.mp3"));
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Stereo Coding");
        window.Model.Graphs.SelectForm(GraphForm.OverTime);

        List<(string Label, int Category)> entries = GraphView.LegendEntries(window.Model.Graphs.Graph!);

        Assert.Equal([0, 1], entries.Select(e => e.Category));
        Assert.All(entries, e => Assert.DoesNotContain("0.0%", e.Label, StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void The_selected_entry_of_the_list_is_scrolled_into_view()
    {
        DetailWindow window = Open(Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), 1180, 700);

        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Channel Modes");
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        Button button = window.GraphChoices.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, window.Model.Graphs.Selected));
        double top = button.TranslatePoint(new Point(0, 0), window.GraphList)!.Value.Y;
        double bottom = top + button.Bounds.Height;
        Assert.True(top >= -1 && bottom <= window.GraphList.Bounds.Height + 1, $"The entry is at {top} to {bottom} of a list {window.GraphList.Bounds.Height} high.");
        window.Close();
    }

    [Fact]
    public void A_last_interval_that_lasts_a_quarter_of_a_second_takes_a_quarter_of_the_width()
    {
        GraphView.Slots slots = GraphView.SlotsOf(new Rect(10, 0, 225, 100), 3, [0, 1, 2], 2.25);

        Assert.Equal(100, slots.W(0), 6);
        Assert.Equal(25, slots.W(2), 6);
        Assert.Equal(210, slots.X(2), 6);
        Assert.Equal(2, slots.At(230));
        Assert.Equal(1, slots.At(205));

        // A stream that ends on a whole second, or columns that merge intervals, are equal.
        Assert.Equal(75, GraphView.SlotsOf(new Rect(0, 0, 225, 100), 3, [0, 1, 2], 3).W(2), 6);
        Assert.Equal(75, GraphView.SlotsOf(new Rect(0, 0, 225, 100), 3, [0, 1, 2, 3, 4], 4.25).W(2), 6);
    }

    private static DetailWindow Open(string fixture, double width = 1180, double height = 800)
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
}
