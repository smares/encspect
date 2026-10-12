using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// Renders the main window with a folder of fixtures and the detail window with a frame field
// selected, in light and dark mode, so drawing is exercised with Skia. With ENCSPECT_SCREENSHOTS
// set to a folder, the frames are saved there as PNG files for review.
public class ScreenshotTests
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersMainAndDetailWindows(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var folder = new TempFolder();
        foreach (string name in new[] { "external-lame-cbr128-joint.mp3", "external-lame-vbr-v2.mp3", "external-fhg-stereo-vbr.mp3", "external-helix-cbr.mp3", "external-blade-stereo.mp3", "external-ffmpeg-shine.mp3", "external-gogo-vbr.mp3", "external-ffmpeg-mp2.mp2", "damaged-bitrate.mp3", "concatenated-files.mp3" })
        {
            if (File.Exists(Fixtures.Mpeg(name)))
            {
                folder.Copy(Fixtures.Mpeg(name), name);
            }
        }

        folder.Write("cover.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 16]);

        var main = new MainWindow(Analyzer.CreateDefault(), new Settings());
        main.Show();
        main.Model.Open(FileSource.ForFolder(folder.Path));
        UiPump.Until(() => !main.Model.IsRunning && main.Model.Rows.Count > 0);
        main.Model.SelectedRow = main.Model.Rows.First(r => r.Name == "external-lame-vbr-v2.mp3");
        Save(main, $"main-{variant}.png");

        DetailWindow detail = main.DetailWindows.Open(main.Model.SelectedRow);
        detail.Width = 1180;
        detail.Height = 800;
        UiPump.Until(() => detail.Model.Tree is not null);
        StructureTree tree = detail.Model.Tree!;
        TreeRow stream = Enumerable.Range(0, tree.Count).Select(i => tree[i]).First(r => r.Kind == Core.NodeKind.Stream);
        tree.Expand(stream);
        TreeRow frame = Enumerable.Range(tree.IndexOf(stream) + 1, stream.VisibleCount).Select(i => tree[i]).Where(r => r.Kind == Core.NodeKind.Frame).Skip(1).First();
        tree.Expand(frame);
        TreeRow header = tree[tree.IndexOf(frame) + 1];
        tree.Expand(header);
        TreeRow field = Enumerable.Range(tree.IndexOf(header) + 1, header.VisibleCount).Select(i => tree[i]).First(r => r.Node.BitLength is > 1);
        detail.Model.SelectedNode = field;
        Save(detail, $"detail-{variant}.png");

        main.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    private static void Save(Avalonia.Controls.TopLevel window, string name)
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name), new PngBitmapEncoderOptions());
        }
    }
}
