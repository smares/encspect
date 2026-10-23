using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// The Graphs tab of real AC-3 and E-AC-3 files, with the series the AC-3 parser itself derives (docs/design/coding-graphs.md, rollout step 3),
// not the test-only ones of CategoryTestData: the entries under their headings, and, with ENCSPECT_SCREENSHOTS set, the summaries and the
// per-frame strips of files that code coupling, rematrixing, block switching and spectral extension, in light and dark mode.
public class Ac3GraphsTests
{
    [AvaloniaFact]
    public void A_stereo_file_lists_its_graphs_under_the_shared_headings()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Ac3("external-ffmpeg-stereo-192k.ac3"));

        Assert.Equal(
            [
                ("Summary", "Coding Summary"),
                ("Stereo Coding", "Blocks With Coupling"),
                ("Stereo Coding", "Blocks With Rematrixing"),
                ("Block Switching", "Blocks With Block Switching"),
                ("Coded Bandwidth", "Coded Bandwidth"),
                ("Bit Distribution", "Bitrate"),
            ],
            window.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        Assert.Equal(["Over Time", "Frames", "Summary"], window.Model.Graphs.Choices.Single(c => c.Title == "Blocks With Coupling").Forms.Select(f => f.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void An_eac3_file_adds_spectral_extension_under_coded_bandwidth()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Ac3("coding-eac3-blocks.eac3"));

        Assert.Contains(("Coded Bandwidth", "Blocks With Spectral Extension"), window.Model.Graphs.Choices.Select(c => (c.Heading, c.Title)));
        window.Close();
    }

    [AvaloniaFact]
    public void A_mono_file_has_block_switching_and_coded_bandwidth_only()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Ac3("external-ffmpeg-mono-96k.ac3"));

        Assert.Equal(["Coding Summary", "Blocks With Block Switching", "Coded Bandwidth", "Bitrate"], window.Model.Graphs.Choices.Select(c => c.Title));
        window.Close();
    }

    // Six blocks of a frame are six rows of the strip; the program frames of an E-AC-3 file with dependent frames are one row of cells that
    // count their blocks.
    [AvaloniaFact]
    public void A_fixed_layout_shows_a_row_for_each_block_and_a_variable_one_a_single_row()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Ac3("external-ffmpeg-stereo-192k.ac3"));
        AacGraphsTests.Choose(window, "Blocks With Rematrixing", GraphForm.Frames);
        Assert.Equal(6, ((CategoryGraph)window.Graph.Graph!).Rows);

        DetailWindow eac3 = AacGraphsTests.Open(Fixtures.Ac3("coding-eac3-dependent.eac3"));
        AacGraphsTests.Choose(eac3, "Blocks With Rematrixing", GraphForm.Frames);
        Assert.Equal(1, ((CategoryGraph)eac3.Graph.Graph!).Rows);
        window.Close();
        eac3.Close();
    }

    // The legend of the over time chart lists the categories in the order of the stack, the largest share first; the frames chart's in the
    // order of the categories.
    [AvaloniaFact]
    public void The_legend_follows_the_stack_over_time_and_the_categories_in_frames()
    {
        DetailWindow window = AacGraphsTests.Open(Fixtures.Ac3("external-ffmpeg-stereo-192k.ac3"));

        AacGraphsTests.Choose(window, "Blocks With Rematrixing", GraphForm.OverTime);
        Assert.Equal([1, 0], GraphView.LegendEntries(window.Model.Graphs.Graph!).Select(e => e.Category));
        AacGraphsTests.Choose(window, "Blocks With Rematrixing", GraphForm.Frames);
        Assert.Equal([0, 1], GraphView.LegendEntries(window.Model.Graphs.Graph!).Select(e => e.Category));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheGraphsOfRealAc3Files(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var folder = new TempFolder();

        // A stereo stream of 10 seconds, the frames of one encoder's file repeated, so the over time views have columns to show.
        string repeated = folder.Write("repeated.ac3", Repeat(File.ReadAllBytes(Fixtures.Ac3("external-ffmpeg-stereo-192k.ac3")), 20));
        DetailWindow stereo = AacGraphsTests.Open(repeated);
        AacGraphsTests.Choose(stereo, "Coding Summary");
        ScreenshotTests.Save(stereo, $"ac3-coding-summary-{variant}.png");
        AacGraphsTests.Choose(stereo, "Blocks With Rematrixing", GraphForm.OverTime);
        ScreenshotTests.Save(stereo, $"ac3-rematrixing-over-time-{variant}.png");
        AacGraphsTests.Choose(stereo, "Blocks With Rematrixing", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"ac3-rematrixing-frames-{variant}.png");
        AacGraphsTests.Choose(stereo, "Blocks With Rematrixing", GraphForm.Summary);
        ScreenshotTests.Save(stereo, $"ac3-rematrixing-summary-{variant}.png");
        AacGraphsTests.Choose(stereo, "Blocks With Coupling", GraphForm.Frames);
        ScreenshotTests.Save(stereo, $"ac3-coupling-frames-{variant}.png");
        AacGraphsTests.Choose(stereo, "Coded Bandwidth", GraphForm.OverTime);
        ScreenshotTests.Save(stereo, $"ac3-coded-bandwidth-over-time-{variant}.png");
        stereo.Close();

        // Aften's file with a block that switches.
        string aften = folder.Write("aften.ac3", Repeat(File.ReadAllBytes(Fixtures.Ac3("external-aften-stereo-192k-blocksw.ac3")), 20));
        DetailWindow switched = AacGraphsTests.Open(aften);
        AacGraphsTests.Choose(switched, "Blocks With Block Switching", GraphForm.Frames);
        ScreenshotTests.Save(switched, $"ac3-block-switching-frames-{variant}.png");
        AacGraphsTests.Choose(switched, "Blocks With Block Switching", GraphForm.Summary);
        ScreenshotTests.Save(switched, $"ac3-block-switching-summary-{variant}.png");
        switched.Close();

        // Hand-built files: coupling that starts and stops, and E-AC-3 blocks of different counts with spectral extension.
        DetailWindow coupling = AacGraphsTests.Open(Fixtures.Ac3("coding-ac3-coupling.ac3"));
        AacGraphsTests.Choose(coupling, "Blocks With Coupling", GraphForm.Frames);
        ScreenshotTests.Save(coupling, $"ac3-hand-built-coupling-frames-{variant}.png");
        AacGraphsTests.Choose(coupling, "Blocks With Coupling", GraphForm.Summary);
        ScreenshotTests.Save(coupling, $"ac3-hand-built-coupling-summary-{variant}.png");
        coupling.Close();
        DetailWindow blocks = AacGraphsTests.Open(Fixtures.Ac3("coding-eac3-blocks.eac3"));
        AacGraphsTests.Choose(blocks, "Blocks With Spectral Extension", GraphForm.Frames);
        ScreenshotTests.Save(blocks, $"ac3-hand-built-eac3-spectral-extension-frames-{variant}.png");
        AacGraphsTests.Choose(blocks, "Coding Summary");
        ScreenshotTests.Save(blocks, $"ac3-hand-built-eac3-coding-summary-{variant}.png");
        blocks.Close();
        DetailWindow dependent = AacGraphsTests.Open(Fixtures.Ac3("coding-eac3-dependent.eac3"));
        AacGraphsTests.Choose(dependent, "Blocks With Rematrixing", GraphForm.Frames);
        ScreenshotTests.Save(dependent, $"ac3-hand-built-eac3-dependent-rematrixing-frames-{variant}.png");
        dependent.Close();

        // Six channels with coupling in every block.
        DetailWindow surround = AacGraphsTests.Open(Fixtures.Ac3("external-ffmpeg-5.1-448k.ac3"));
        AacGraphsTests.Choose(surround, "Coding Summary");
        ScreenshotTests.Save(surround, $"ac3-51-coding-summary-{variant}.png");
        surround.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // A raw AC-3 file repeated: its frames follow one another, so the copies are one stream.
    private static byte[] Repeat(byte[] source, int copies)
    {
        byte[] data = new byte[source.Length * copies];
        for (int at = 0; at < data.Length; at += source.Length)
        {
            source.CopyTo(data, at);
        }

        return data;
    }
}
