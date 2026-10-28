using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

        // An export that failed, and an error of the system in the main window's status bar.
        detail.PickExportTarget = () => Task.FromResult<ExportTarget?>(new ExportTarget(folder["gone/report.txt"], null, ReportFormat.Text));
        Task export = detail.ExportAsync();
        UiPump.Until(() => export.IsCompleted);
        Save(detail, $"detail-export-error-{variant}.png");
        main.Model.ShowError("The path was not copied: the clipboard is not available.");
        Save(main, $"main-error-{variant}.png");

        main.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // The graphs of a VBR stream: the bitrate per frame with a point clicked, which selects its frame
    // in the tree and hex view, and the bitrate distribution with a bar hovered.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersGraphs(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1180, Height = 800 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Stats("vbr-seconds.mp3"));
        UiPump.Until(() => load.IsCompleted);
        window.Model.IsGraphsShown = true;
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

        Point point = PointOver(window, 60);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(Core.NodeKind.Frame, window.Model.SelectedNode!.Kind);
        window.MouseMove(PointOver(window, 90));
        Save(window, $"graphs-{variant}.png");

        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        window.Model.Graphs.SelectForm(GraphForm.Summary);
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        window.MouseMove(PointOver(window, window.Graph.Graph!.Count / 2));
        Save(window, $"distribution-{variant}.png");

        // A file of two streams offers a choice of stream.
        load = window.Model.LoadAsync(Fixtures.Stats("sample-rate-change-statistics.mp3"));
        UiPump.Until(() => load.IsCompleted);
        window.Model.Graphs.SelectedStream = window.Model.Graphs.Streams[1];
        Save(window, $"graphs-streams-{variant}.png");

        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // The over-time forms of a category chart of a stream longer than 30 seconds, scripted: the stepped area and its smoothed
    // variant, a gap of two empty seconds and a last interval of a few frames hatched.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheOverTimeForms(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        IReadOnlyList<Core.Category> categories = [new("Left/right stereo", "a."), new("Mid/side stereo", "b."), new("Intensity stereo", "c.")];
        var builder = new Core.CategorySeriesBuilder("Stereo Coding", categories, "frame", 1);
        const int seconds = 90;
        int[] first = new int[seconds];
        int units = 0;
        for (int second = 0; second < seconds; second++)
        {
            first[second] = units;
            int count = second == seconds - 1 ? 3 : second is 40 or 41 ? 0 : 20;
            for (int unit = 0; unit < count; unit++)
            {
                builder.Add((byte)(second < 25 ? 1 : second < 60 ? (unit < 3 + (second % 10) ? 0 : 1) : (unit < 2 ? 2 : unit < 8 ? 0 : 1)));
                units++;
            }
        }

        Core.CategorySeries series = builder.Build(Enumerable.Range(0, units).Select(i => i * 100.0).ToArray(), null, null, "Scripted.", null, new Core.TimeIndex(Enumerable.Range(0, seconds).Select(s => (double)s).ToArray(), first));
        foreach ((GraphForm form, string name) in new[] { (GraphForm.OverTime, "area"), (GraphForm.Smoothed, "area-smoothed") })
        {
            var view = new GraphView { Graph = new CategoryGraph(series, form) { Duration = seconds - 1 + 0.075 } };
            var window = new Avalonia.Controls.Window { Width = 1180, Height = 420, Content = view };
            window.Show();
            Save(window, $"over-time-{name}-{variant}.png");
            window.Close();
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // The Audio panels: the caption and the command before decoding, then the spectrogram (hovered at the cutoff, with a frame selected), the
    // spectrum with its cutoff, the waveform and the figures, the waveform of six channels and the spectrum of each, and a sweep.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void RendersTheAudioPanels(string variant)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        DetailWindow window = AudioPanelTests.Open(Fixtures.Flac("external-16-stereo-l5.flac"), decode: false);
        Save(window, $"audio-before-decoding-{variant}.png");
        window.Close();

        window = AudioPanelTests.Open(Fixtures.Measurement("lossy-lame-b64.flac"));
        var spectrogram = (SpectrogramGraph)window.Graph.Graph!;
        int cutoffRow = spectrogram.RowOfHertz(spectrogram.Cutoff!.Frequency - 1);
        Point click = window.Graph.TranslatePoint(window.Graph.PointOfCell(spectrogram.Columns / 3, 300)!.Value, window)!.Value;
        window.MouseDown(click, MouseButton.Left);
        window.MouseUp(click, MouseButton.Left);
        window.MouseMove(window.Graph.TranslatePoint(window.Graph.PointOfCell(spectrogram.Columns / 2, cutoffRow)!.Value, window)!.Value);
        Save(window, $"audio-spectrogram-{variant}.png");

        AudioPanelTests.Choose(window, "Spectrum");
        window.MouseMove(window.Graph.TranslatePoint(window.Graph.PointOfCell(cutoffRow)!.Value, window)!.Value);
        Save(window, $"audio-spectrum-{variant}.png");
        AudioPanelTests.Choose(window, "Waveform");
        Save(window, $"audio-waveform-{variant}.png");
        AudioPanelTests.Choose(window, "Figures");
        Save(window, $"audio-figures-{variant}.png");
        window.Close();

        window = AudioPanelTests.Open(Fixtures.Flac("external-16-6ch.flac"));
        AudioPanelTests.Choose(window, "Waveform");
        window.MouseMove(window.Graph.TranslatePoint(new Point(window.Graph.AudioPlotArea()!.Value.Center.X, window.Graph.WaveformLanes()![1].Center.Y), window)!.Value);
        Save(window, $"audio-waveform-channels-{variant}.png");
        AudioPanelTests.Choose(window, "Spectrum", GraphForm.PerChannel);
        window.MouseMove(window.Graph.TranslatePoint(window.Graph.PointOfCell(150)!.Value, window)!.Value);
        Save(window, $"audio-spectrum-channels-{variant}.png");
        window.Close();

        window = AudioPanelTests.Open(Fixtures.Measurement("synthetic-sweep.flac"));
        Save(window, $"audio-spectrogram-sweep-{variant}.png");
        window.Close();

        window = AudioPanelTests.Open(Fixtures.Measurement("synthetic-loudness-steps-8000.flac"));
        AudioPanelTests.Choose(window, "Loudness");
        window.MouseMove(window.Graph.TranslatePoint(window.Graph.LoudnessDotOf(20)!.Value, window)!.Value);
        Save(window, $"audio-loudness-{variant}.png");
        window.Close();

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    // A point in the window over the chart's point or bar at index, at the height of its value.
    internal static Point PointOver(DetailWindow window, int index)
    {
        GraphView view = window.Graph;
        Graph graph = view.Graph!;
        for (double x = 0; x < view.Bounds.Width; x++)
        {
            for (double y = view.Bounds.Height - 1; y > 0; y -= 2)
            {
                if (view.IndexAt(new Point(x, y)) == index)
                {
                    return view.TranslatePoint(new Point(x + 1, y), window)!.Value;
                }
            }
        }

        throw new InvalidOperationException($"No point of {graph.Title} at {index}.");
    }

    internal static void Save(Avalonia.Controls.TopLevel window, string name)
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is { Length: > 0 } folder)
        {
            WritePng(frame, folder, name);
        }
    }

    // Encodes on another thread and waits a bounded time, so that an encoder or a file system that stalls fails the test
    // with the file's name instead of hanging the run. The file appears under its name only once complete, so a stalled
    // write never leaves an empty PNG there.
    internal static void WritePng(WriteableBitmap frame, string folder, string name, int timeoutMilliseconds = 60000)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        string partial = path + ".partial";
        Task write = Task.Run(() =>
        {
            frame.Save(partial, new PngBitmapEncoderOptions());
            File.Move(partial, path, overwrite: true);
        });
        if (!write.Wait(timeoutMilliseconds))
        {
            throw new TimeoutException($"Saving the screenshot {path} did not finish in {timeoutMilliseconds} ms.");
        }
    }
}
