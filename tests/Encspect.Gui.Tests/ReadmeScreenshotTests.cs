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

    // Sixty seconds of the block switching of a joint stereo file, which is the same six seconds ten times.
    private static void FrameMap(string variant)
    {
        using var folder = new TempFolder();
        byte[] one = File.ReadAllBytes(Fixtures.Mpeg("external-lame-joint-music.mp3"));
        using (FileStream output = File.Create(folder["minute.mp3"]))
        {
            for (int copy = 0; copy < 10; copy++)
            {
                output.Write(one);
            }
        }

        DetailWindow window = Open(folder["minute.mp3"], decode: false, 760);
        AudioPanelTests.Choose(window, "Block Usage", GraphForm.Map);
        var map = (FrameMapGraph)window.Graph.Graph!;
        Assert.True(map.Count > 2000);
        int shortFrame = Enumerable.Range(map.Count / 2, map.Count / 2).First(s => Enumerable.Range(0, 4).Any(v => map.CategorySeries![map.UnitOf(s), v] == 2));
        FitTree(window, () => window.Model.SelectFrameAt(map.OffsetOf(shortFrame)));
        Save(window, $"readme-frame-map-{variant}.png");
        window.Close();
    }

    // The tree pane is half the window's body; a height that holds a whole number of rows keeps a row from being cut at the pane's edge.
    private static void FitTree(DetailWindow window, Action select)
    {
        var list = window.FindControl<ListBox>("TreeList")!;
        for (int round = 0; round < 4; round++)
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

            window.Height -= 2 * extra;
        }

        throw new InvalidOperationException("The tree pane did not settle on a whole number of rows.");
    }

    private static void Audio(string variant)
    {
        using var folder = new TempFolder();
        // A stream of more than 47 seconds has merged columns, so its spectrogram has no strip of columns without data at the end.
        File.WriteAllBytes(folder["music.wav"], MusicWav.Build(Environment.GetEnvironmentVariable("ENCSPECT_SCREENSHOTS") is { Length: > 0 } ? 50 : 6));
        DetailWindow window = Open(folder["music.wav"], decode: true, 760);
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

// Stereo 16-bit music at 44,100 Hz, synthesized the same way on every run: four chords of pads and plucked notes, a kick,
// a snare and hi-hats, the noise through a lowpass at 15.5 kHz so the spectrogram shows a cutoff.
internal static class MusicWav
{
    private const int Rate = 44100;

    public static byte[] Build(int seconds)
    {
        int count = Rate * seconds;
        double[] tonalLeft = new double[count], tonalRight = new double[count], noiseLeft = new double[count], noiseRight = new double[count];
        int[][] chords = [[57, 60, 64, 69], [53, 57, 60, 65], [48, 55, 60, 64], [55, 59, 62, 67]];
        double chordLength = 2.5, beat = 0.625;
        for (int chord = 0; chord * chordLength < seconds; chord++)
        {
            int[] notes = chords[chord % chords.Length];
            for (int voice = 0; voice < notes.Length; voice++)
            {
                double pan = (voice - 1.5) / 3.0 * 0.8;
                Add(tonalLeft, tonalRight, chord * chordLength, chordLength + 0.4, Frequency(notes[voice] - 12), pan, 0.5, pad: true, voice);
                for (int step = 0; step < 8; step++)
                {
                    if ((step + voice) % 4 == 0 || step % 2 == voice % 2 && voice > 1)
                    {
                        Add(tonalLeft, tonalRight, chord * chordLength + step * beat / 2, 0.7, Frequency(notes[voice] + 12), -pan, 0.35, pad: false, voice);
                    }
                }
            }
        }

        uint seed = 12345;
        for (int bar = 0; bar * beat * 4 < seconds; bar++)
        {
            for (int b = 0; b < 4; b++)
            {
                double start = (bar * 4 + b) * beat;
                Kick(tonalLeft, tonalRight, start);
                if (b % 2 == 1)
                {
                    Noise(noiseLeft, noiseRight, start, 0.18, 14, 0.5, ref seed);
                }
            }

            for (int h = 0; h < 8; h++)
            {
                Noise(noiseLeft, noiseRight, (bar * 4 * beat) + h * beat / 2 + beat / 4, 0.06, 70, h % 2 == 0 ? 0.28 : 0.18, ref seed);
            }
        }

        double[] kernel = Lowpass(127, 15500.0 / Rate);
        double[] left = Mix(tonalLeft, Filter(noiseLeft, kernel));
        double[] right = Mix(tonalRight, Filter(noiseRight, kernel));
        double peak = Math.Max(left.Max(Math.Abs), right.Max(Math.Abs));
        double gain = 0.8 / peak;

        var bytes = new byte[44 + count * 4];
        void Put(int offset, string text) => System.Text.Encoding.ASCII.GetBytes(text, bytes.AsSpan(offset));
        Put(0, "RIFF");
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 36 + count * 4);
        Put(8, "WAVEfmt ");
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), Rate);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), Rate * 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)4);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        Put(36, "data");
        BitConverter.TryWriteBytes(bytes.AsSpan(40), count * 4);
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(44 + i * 4), (short)Math.Round(left[i] * gain * 32767));
            BitConverter.TryWriteBytes(bytes.AsSpan(46 + i * 4), (short)Math.Round(right[i] * gain * 32767));
        }

        return bytes;
    }

    private static double Frequency(int note) => 440 * Math.Pow(2, (note - 69) / 12.0);

    private static void Add(double[] left, double[] right, double start, double length, double frequency, double pan, double level, bool pad, int voice)
    {
        int first = (int)(start * Rate), last = Math.Min(left.Length, (int)((start + length) * Rate));
        double phase0 = voice * 0.7;
        for (int i = first; i < last; i++)
        {
            double t = (double)(i - first) / Rate;
            double envelope = pad
                ? Math.Min(1, t / 0.25) * Math.Min(1, (length - t) / 0.4)
                : Math.Min(1, t / 0.004) * Math.Exp(-t * 5);
            double vibrato = pad ? 1 + 0.002 * Math.Sin(2 * Math.PI * 5 * t) : 1;
            double sample = 0;
            for (int k = 1; k * frequency < 12000 && k <= 16; k++)
            {
                sample += Math.Sin(2 * Math.PI * frequency * vibrato * k * t + phase0 * k) / Math.Pow(k, pad ? 1.6 : 1.1);
            }

            sample *= envelope * level * 0.25;
            left[i] += sample * (1 - pan) / 2 * 1.4;
            right[i] += sample * (1 + pan) / 2 * 1.4;
        }
    }

    private static void Kick(double[] left, double[] right, double start)
    {
        int first = (int)(start * Rate), last = Math.Min(left.Length, first + (int)(0.35 * Rate));
        double phase = 0;
        for (int i = first; i < last; i++)
        {
            double t = (double)(i - first) / Rate;
            phase += 2 * Math.PI * (45 + 110 * Math.Exp(-t * 28)) / Rate;
            double sample = Math.Sin(phase) * Math.Exp(-t * 9) * 0.9;
            left[i] += sample;
            right[i] += sample;
        }
    }

    private static void Noise(double[] left, double[] right, double start, double length, double decay, double level, ref uint seed)
    {
        int first = (int)(start * Rate), last = Math.Min(left.Length, first + (int)(length * Rate));
        for (int i = first; i < last; i++)
        {
            double t = (double)(i - first) / Rate;
            double envelope = Math.Exp(-t * decay) * level;
            seed = seed * 1664525 + 1013904223;
            left[i] += ((seed >> 8) / 8388608.0 - 1) * envelope;
            seed = seed * 1664525 + 1013904223;
            right[i] += ((seed >> 8) / 8388608.0 - 1) * envelope;
        }
    }

    private static double[] Lowpass(int taps, double cutoff)
    {
        var kernel = new double[taps];
        int middle = taps / 2;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            int n = i - middle;
            double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            kernel[i] = sinc * window;
            sum += kernel[i];
        }

        for (int i = 0; i < taps; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    private static double[] Filter(double[] input, double[] kernel)
    {
        var output = new double[input.Length];
        int middle = kernel.Length / 2;
        for (int i = 0; i < input.Length; i++)
        {
            double sum = 0;
            for (int k = 0; k < kernel.Length; k++)
            {
                int j = i + k - middle;
                if (j >= 0 && j < input.Length)
                {
                    sum += input[j] * kernel[k];
                }
            }

            output[i] = sum;
        }

        return output;
    }

    private static double[] Mix(double[] a, double[] b) => [.. a.Zip(b, (x, y) => x + y)];
}
