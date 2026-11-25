using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The images of the README (docs/images/readme-*.png), light and dark, at a scale of 2 in windows 1280 logical pixels wide. With
// ENCSPECT_SCREENSHOTS set to a folder they are saved there; without it the views are only opened and checked.
public class ReadmeScreenshotTests
{
    private const double Width = 1280;
    private const double Scaling = 2;
    private const double DetailHeight = 620;

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheReadmeImages(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            MainWindowImage(variant);
            DetailWindowImage(variant);
            FrameMap(variant);
            Audio(variant);
            CodingSummary(variant);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    // The tree shows a staged home folder, not this machine's folders (FolderRoots has no other hook than the roots), and the
    // files are listed as items, whose title is their count and not a path of the machine.
    private static void MainWindowImage(string variant)
    {
        using var folder = new TempFolder();
        string music = folder["Home/Music"];
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(folder["Home/Documents"]);
        Directory.CreateDirectory(folder["Home/Downloads"]);
        var files = new List<string>();
        foreach ((string source, string target) in new[]
        {
            (Fixtures.Mpeg("external-lame-joint-music.mp3"), "Joint Stereo.mp3"),
            (Fixtures.Mpeg("external-lame-dual-music.mp3"), "Dual Channel.mp3"),
            (Fixtures.Mpeg("external-lame3101-vbr.mp3"), "Variable Bitrate.mp3"),
            (Fixtures.Mpeg("external-blade-stereo.mp3"), "Short Blocks.mp3"),
            (Fixtures.Mpeg("external-lame-abr160.mp3"), "Average Bitrate.mp3"),
            (Fixtures.Mpeg("external-twolame-dual-music.mp2"), "Layer II.mp2"),
            (Fixtures.Mpeg("external-blade080-stereo.mp3"), "Damaged.mp3"),
            (Fixtures.Aac("ffmpeg-adts.aac"), "Stream.aac"),
            (Fixtures.Aac("ffmpeg-mono-faststart.m4a"), "Mono.m4a"),
            (Fixtures.Flac("external-16-stereo-l5.flac"), "Lossless.flac"),
            (Fixtures.Flac("external-16-6ch.flac"), "Surround.flac"),
            (Fixtures.Measurement("lossy-lame-b64.flac"), "Lowpass.flac"),
            (Fixtures.Ac3("coding-ac3-coupling.ac3"), "Dolby Digital.ac3"),
            (Fixtures.Directory + "/alac/external-ffmpeg-16-stereo.m4a", "Apple Lossless.m4a"),
            (Fixtures.Directory + "/alac/external-mkvmerge-16-stereo.mka", "Matroska.mka"),
            (Fixtures.Directory + "/ogg/external-ffmpeg-libopus-bitexact.opus", "Opus.opus"),
        })
        {
            files.Add(Path.Combine(music, target));
            File.Copy(source, files[^1]);
        }

        var home = new FolderItem("Home", folder["Home"], FolderKind.Home, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        var main = new MainWindow(Analyzer.CreateDefault(), new Settings(), new MainViewModel(Analyzer.CreateDefault(), roots: [home])) { Width = 1440, Height = 620 };
        main.Show();
        main.SetRenderScaling(Scaling);
        home.IsExpanded = true;
        UiPump.Until(() => home.IsLoaded);
        FolderItem musicItem = home.Children.Single(c => c.Name == "Music");
        musicItem.IsExpanded = true;
        main.Model.Open(FileSource.ForItems(files));
        UiPump.Until(() => !main.Model.IsRunning && main.Model.Rows.Count == files.Count);
        foreach (DataGridColumn column in main.Files.Columns.Where(c => c.Header is "Channel Layout" or "Written by" or "Tags" or "Protection"))
        {
            column.IsVisible = false;
        }

        main.Model.SelectedRow = main.Model.Rows.First(r => r.Name == "Joint Stereo.mp3");
        Save(main, $"readme-main-{variant}.png");
        main.Close();
    }

    // A file with findings: the findings pane lists them and the first is selected, which opens its bytes in the tree and hex view.
    private static void DetailWindowImage(string variant)
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = Width, Height = DetailHeight };
        window.Show();
        window.SetRenderScaling(Scaling);
        Task load = window.Model.LoadAsync(FrameMapViewTests.Damaged);
        UiPump.Until(() => load.IsCompleted);
        Assert.True(window.Model.Findings.Count >= 2);
        FitTree(window, () =>
        {
            window.Model.SelectedFinding = null;
            window.Model.SelectedFinding = window.Model.Findings[0];
        });
        Save(window, $"readme-detail-{variant}.png");
        window.Close();
    }

    // The bitrate of a whole recording, Mozart's Magic Flute overture (the music folder's fixture), at one square per frame. Its block types hardly change (99.8% long blocks), its bitrate follows the music.
    private static void FrameMap(string variant)
    {
        DetailWindow window = Open(MagicFlute, decode: false, 760);
        AudioPanelTests.Choose(window, "Bitrate", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        Assert.True(map.Count > 10000);
        FitTree(window, () => window.Model.SelectFrameAt(map.OffsetOf(map.Count * 3 / 5)));
        Save(window, $"readme-frame-map-{variant}.png");
        window.Close();
    }

    private static string MagicFlute => Fixtures.Mpeg("music/external-lame-magic-flute-v4.mp3");

    // The tree pane is half the window's body; a height that holds a whole number of rows keeps a row from being cut at the pane's edge.
    private static void FitTree(DetailWindow window, Action select)
    {
        var list = window.FindControl<ListBox>("TreeList")!;
        for (int round = 0; round < 12; round++)
        {
            select();
            AudioPanelTests.Settle();
            ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            ListBoxItem[] items = [.. list.GetVisualDescendants().OfType<ListBoxItem>().OrderBy(i => i.TranslatePoint(default, list)!.Value.Y)];
            double pitch = items[1].TranslatePoint(default, list)!.Value.Y - items[0].TranslatePoint(default, list)!.Value.Y;
            double extra = scroller.Viewport.Height % pitch;
            if (extra < 0.5 || pitch - extra < 0.5)
            {
                list.ScrollIntoView(list.SelectedItem!);
                AudioPanelTests.Settle();
                return;
            }

            window.Height -= extra;
        }

        throw new InvalidOperationException("The tree pane did not settle on a whole number of rows.");
    }

    private static void Audio(string variant)
    {
        DetailWindow window = Open(MagicFlute, decode: true, 760);
        var spectrogram = (SpectrogramGraph)window.Graph.Graph!;
        Assert.NotNull(spectrogram.Cutoff);
        Save(window, $"readme-spectrogram-{variant}.png");
        AudioPanelTests.Choose(window, "Waveform");
        Save(window, $"readme-waveform-{variant}.png");
        window.Close();
    }

    private static void CodingSummary(string variant)
    {
        DetailWindow window = Open(Fixtures.Mpeg("external-lame-joint-music.mp3"), decode: false, 980);
        AudioPanelTests.Choose(window, "Coding Summary");
        Save(window, $"readme-coding-summary-{variant}.png");
        window.Close();
    }

    private static DetailWindow Open(string fixture, bool decode, double height)
    {
        DetailWindow window = AudioPanelTests.Open(fixture, decode, Width, height);
        window.SetRenderScaling(Scaling);
        AudioPanelTests.Settle();
        return window;
    }

    private static void Save(TopLevel window, string name)
    {
        Assert.Equal(Scaling, window.RenderScaling);
        ScreenshotTests.Save(window, name);
    }
}
