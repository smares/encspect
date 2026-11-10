using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
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

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheReadmeImages(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            MainAndDetail(variant);
            FrameMap(variant);
            Spectrogram(variant);
            CodingSummary(variant);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static void MainAndDetail(string variant)
    {
        string root = Path.Combine(Path.GetTempPath(), "encspect-readme");
        string music = Path.Combine(root, "Music");
        Directory.CreateDirectory(music);
        try
        {
            foreach ((string source, string target) in new[]
            {
                (Fixtures.Mpeg("external-lame-joint-music.mp3"), "lame-joint.mp3"),
                (Fixtures.Mpeg("external-lame-dual-music.mp3"), "lame-dual.mp3"),
                (Fixtures.Mpeg("external-lame3101-vbr.mp3"), "lame-vbr.mp3"),
                (Fixtures.Mpeg("external-blade-stereo.mp3"), "blade.mp3"),
                (Fixtures.Mpeg("external-twolame-dual-music.mp2"), "twolame.mp2"),
                (Fixtures.Mpeg("damaged-bitrate.mp3"), "damaged.mp3"),
                (Fixtures.Aac("ffmpeg-adts.aac"), "ffmpeg.aac"),
                (Fixtures.Aac("ffmpeg-mono-faststart.m4a"), "ffmpeg.m4a"),
                (Fixtures.Flac("external-16-stereo-l5.flac"), "stereo.flac"),
                (Fixtures.Flac("external-16-6ch.flac"), "surround.flac"),
                (Fixtures.Measurement("lossy-lame-b64.flac"), "lowpass.flac"),
                (Fixtures.Ac3("coding-ac3-coupling.ac3"), "coupling.ac3"),
            })
            {
                File.Copy(source, Path.Combine(music, target), overwrite: true);
            }

            var main = new MainWindow(Analyzer.CreateDefault(), new Settings()) { Width = 1400, Height = 640 };
            main.Show();
            main.SetRenderScaling(Scaling);
            main.Model.Open(FileSource.ForFolder(music));
            UiPump.Until(() => !main.Model.IsRunning && main.Model.Rows.Count > 0);
            main.Model.SelectedRow = main.Model.Rows.First(r => r.Name == "lame-joint.mp3");
            Save(main, $"readme-main-{variant}.png");

            DetailWindow detail = main.DetailWindows.Open(main.Model.SelectedRow);
            detail.Width = Width;
            detail.Height = 660;
            detail.SetRenderScaling(Scaling);
            UiPump.Until(() => detail.Model.Tree is not null);
            StructureTree tree = detail.Model.Tree!;
            TreeRow stream = Enumerable.Range(0, tree.Count).Select(i => tree[i]).First(r => r.Kind == Core.NodeKind.Stream);
            tree.Expand(stream);
            TreeRow frame = Enumerable.Range(tree.IndexOf(stream) + 1, stream.VisibleCount).Select(i => tree[i]).Where(r => r.Kind == Core.NodeKind.Frame).Skip(2).First();
            tree.Expand(frame);
            TreeRow header = tree[tree.IndexOf(frame) + 1];
            tree.Expand(header);
            detail.Model.SelectedNode = Enumerable.Range(tree.IndexOf(header) + 1, header.VisibleCount).Select(i => tree[i]).First(r => r.Node.BitLength is > 1);
            Save(detail, $"readme-detail-{variant}.png");
            main.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void FrameMap(string variant)
    {
        DetailWindow window = Open(Fixtures.Mpeg("external-lame-joint-music.mp3"), decode: false, 680);
        AudioPanelTests.Choose(window, "Block Usage", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        int shortFrame = Enumerable.Range(0, map.Count).First(s => Enumerable.Range(0, 4).Any(v => map.CategorySeries![map.UnitOf(s), v] == 2));
        window.Model.SelectFrameAt(map.OffsetOf(shortFrame));
        window.MouseMove(window.Graph.TranslatePoint(window.Graph.MapSquareRect(shortFrame)!.Value.Center, window)!.Value);
        Save(window, $"readme-frame-map-{variant}.png");
        window.Close();
    }

    private static void Spectrogram(string variant)
    {
        DetailWindow window = Open(Fixtures.Measurement("lossy-lame-b64.flac"), decode: true, 680);
        var spectrogram = (SpectrogramGraph)window.Graph.Graph!;
        Assert.NotNull(spectrogram.Cutoff);
        Save(window, $"readme-spectrogram-{variant}.png");
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
