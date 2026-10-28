using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The Audio panels under the graphs (docs/design/spectral-analysis.md, "GUI"): the captions before decoding, the panels after the Decode
// Audio command, the hover, the click to a frame, the keys, and the automation text.
public class AudioPanelTests
{
    private const string CutoffFile = "lossy-lame-v2.flac";

    internal static DetailWindow Open(string fixture, bool decode = true, double width = 1180, double height = 800)
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = width, Height = height };
        window.Show();
        Task load = window.Model.LoadAsync(fixture);
        UiPump.Until(() => load.IsCompleted);
        if (decode)
        {
            Task task = window.Model.DecodeAudioAsync();
            UiPump.Until(() => task.IsCompleted);
        }

        window.Model.IsGraphsShown = true;
        Settle();
        return window;
    }

    internal static void Settle()
    {
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    internal static void Choose(DetailWindow window, string title, GraphForm? form = null)
    {
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == title);
        if (form is { } kind)
        {
            window.Model.Graphs.SelectForm(kind);
        }

        Settle();
    }

    private static Point Window(DetailWindow window, Point? inView) => window.Graph.TranslatePoint(inView!.Value, window)!.Value;

    [AvaloniaFact]
    public void Before_decoding_a_stream_with_a_decoder_offers_the_command_under_the_heading_audio()
    {
        DetailWindow window = Open(Fixtures.Flac("external-16-stereo-l5.flac"), decode: false);

        GraphChoiceGroup audio = window.Model.Graphs.ChoiceGroups.Single(g => g.Heading == "Audio");
        Assert.Equal("Decode the audio to see its waveform and spectrogram.", audio.Caption);
        Assert.True(audio.OffersDecode);
        Assert.Empty(audio.Choices);
        Assert.True(window.Model.Graphs.CanDecode);
        Button command = window.GraphChoices.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("decode") && b.IsEffectivelyVisible);
        Assert.Equal("Decode Audio", command.Content);
        Assert.True(command.IsEffectivelyVisible);
        Assert.True(command.IsEnabled);

        // The command in the list runs the same decode as the one in the header.
        command.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Model.IsDecoding);
        UiPump.Until(() => !window.Model.IsDecoding);
        Settle();
        Assert.Equal(["Waveform", "Spectrogram", "Spectrum", "Figures"], window.Model.Graphs.Choices.Where(c => c.Heading == "Audio").Select(c => c.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void A_pcm_stream_is_listed_with_the_caption_and_the_command_in_place_of_the_chart()
    {
        DetailWindow window = Open(Fixtures.Wav("external-pcm-s16-stereo-44100.wav"), decode: false);

        Assert.True(window.Model.Graphs.HasGraphs);
        Assert.Null(window.Model.Graphs.Selected);
        Assert.Equal("Decode the audio to see its waveform and spectrogram.", window.Model.Graphs.EmptyNote);
        Assert.Equal(window.Model.Graphs.EmptyNote, window.FindControl<TextBlock>("EmptyNote")!.Text);
        Assert.True(window.FindControl<TextBlock>("EmptyNote")!.IsEffectivelyVisible);
        Assert.False(window.Graph.IsEffectivelyVisible);

        Task decode = window.Model.DecodeAudioAsync();
        UiPump.Until(() => decode.IsCompleted);
        Settle();
        Assert.Equal("Spectrogram", window.Model.Graphs.Selected!.Title);
        Assert.False(window.FindControl<TextBlock>("EmptyNote")!.IsEffectivelyVisible);
        Assert.True(window.Graph.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_stream_the_analysis_does_not_decode_says_so_with_the_codec_and_never_that_no_decoder_exists()
    {
        // MPEG audio has no decoder; FLAC in Matroska has one, but the container does not hand the stream to its codec parser.
        foreach ((string path, string codec) in new[] { (Fixtures.Mpeg("external-lame-cbr128-joint.mp3"), "MPEG-1 Layer III"), (Fixtures.Matroska("external-ffmpeg-flac.mka"), "FLAC") })
        {
            DetailWindow window = Open(path, decode: false);
            AudioCaption caption = window.Model.Graphs.SelectedStream!.Caption!;
            Assert.Equal($"The audio of this {codec} stream is not decoded here.", caption.Text);
            Assert.False(caption.OffersDecode);
            Assert.DoesNotContain("no decoder", caption.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(window.GraphChoices.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("decode") && b.IsEffectivelyVisible);
            window.Close();
        }
    }

    [Fact]
    public void The_captions_of_a_decoded_stream_whose_measurements_are_incomplete_name_the_status()
    {
        StreamInfo Stream(StreamMeasurements? measurements, DecodeStatus? decoding) => new()
        {
            Nodes = [],
            Codec = "FLAC",
            SampleRate = 44100,
            Verification = decoding is { } status ? new StreamVerification { Decoding = status } : null,
            Measurements = measurements,
        };

        string? Caption(StreamInfo stream) => AudioPanels.CaptionFor(stream)?.Text;

        Assert.Equal("The audio cannot be decoded: the stream decodes to DSD, which is not measured.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.NotPossible, Reason = "the stream decodes to DSD, which is not measured" }, DecodeStatus.Decoded)));
        Assert.Equal("The audio cannot be decoded: no decoder for TTA.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.NotPossible, Reason = "no decoder for TTA" }, DecodeStatus.NoDecoder)));
        Assert.Equal("The audio cannot be decoded: the stream cannot be decoded.", Caption(Stream(null, DecodeStatus.NotPossible)));
        Assert.Equal("Decoding stopped after 1.5 s of audio; the figures cover what was measured.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.Stopped, Samples = 66150, SampleRate = 44100 }, DecodeStatus.Stopped)));
        Assert.Equal("Decoding stopped before any audio was measured.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.Stopped }, DecodeStatus.Stopped)));
        Assert.Equal("Measuring stopped where the format of the decoded blocks changed; the figures cover what came before.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.FormatChanged, Samples = 1 }, DecodeStatus.Decoded)));
        Assert.Equal("The measuring failed, so the audio has no figures.", Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.Failed }, DecodeStatus.Decoded)));
        Assert.Equal("The audio was decoded, but not measured.", Caption(Stream(null, DecodeStatus.Decoded)));
        Assert.Null(Caption(Stream(new StreamMeasurements { Status = MeasurementStatus.Complete }, DecodeStatus.Decoded)));
        Assert.Null(AudioPanels.CaptionFor(Stream(null, null) with { Kind = StreamKind.Video }));
        Assert.Equal("The audio of this stream is not decoded here.", AudioPanels.NotDecodedHere(string.Empty));

        // A caption that comes with panels is listed with them, under the heading.
        var stopped = new StreamMeasurements
        {
            Status = MeasurementStatus.Stopped,
            Samples = 100,
            SampleRate = 8000,
            Waveform = new WaveformGrid { Columns = 1, Channels = 1, Minimum = new short[] { -5 }, Maximum = new short[] { 5 } },
        };
        var graphs = new GraphsViewModel();
        graphs.Load(new Report("test", new Node("Root", 0, 0), [], [Stream(stopped, DecodeStatus.Stopped)]));
        GraphChoiceGroup audio = Assert.Single(graphs.ChoiceGroups);
        Assert.Equal("Audio", audio.Heading);
        Assert.StartsWith("Decoding stopped after 0.0 s", audio.Caption, StringComparison.Ordinal);
        Assert.Equal(["Waveform"], audio.Choices.Select(c => c.Title));
    }

    [AvaloniaFact]
    public void After_decoding_the_panels_are_listed_and_the_spectrogram_is_chosen()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));

        GraphChoiceGroup audio = window.Model.Graphs.ChoiceGroups.Single(g => g.Heading == "Audio");
        Assert.Equal(["Waveform", "Spectrogram", "Spectrum", "Figures"], audio.Choices.Select(c => c.Title));
        Assert.Null(audio.Caption);
        Assert.Equal("Spectrogram", window.Model.Graphs.Selected!.Title);
        Assert.IsType<SpectrogramGraph>(window.Graph.Graph);
        Assert.Equal("Audio", window.Model.Graphs.Graph!.Heading);
        Assert.All(audio.Choices, c => Assert.False(string.IsNullOrEmpty(c.Description)));
        Assert.Equal(["Bit Distribution", "Audio"], window.Model.Graphs.ChoiceGroups.Select(g => g.Heading));

        // The Spectrum of one channel has one line; the figures are text, not a chart.
        Choose(window, "Spectrum");
        Assert.False(window.Model.Graphs.HasForms);
        Choose(window, "Figures");
        Assert.True(window.FindControl<ScrollViewer>("FiguresScroll")!.IsEffectivelyVisible);
        Assert.False(window.Graph.IsEffectivelyVisible);
        string[] names = [.. window.Model.Graphs.Figures!.Select(l => l.Name)];
        Assert.Contains("Spectral Cutoff", names);
        Assert.Contains("Sample Peak", names);
        Assert.All(window.Model.Graphs.Figures!, l => Assert.False(string.IsNullOrEmpty(l.Description)));
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_the_spectrogram_shows_time_frequency_and_level_and_the_cutoff_on_its_line()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        var graph = (SpectrogramGraph)window.Graph.Graph!;
        Assert.Equal(512, graph.Rows);

        int column = graph.Columns / 2;
        int row = 100;
        window.MouseMove(Window(window, window.Graph.PointOfCell(column, row)));
        Assert.Equal(column, window.Graph.HoverIndex);
        Assert.Equal(row, window.Graph.HoverRow);
        string text = window.Graph.HoverText!;
        Assert.Equal(graph.DescribeCell(column, row), text);
        Assert.Matches(new Regex(@"^\d:\d\d\.\d · [\d,]+ Hz · -?\d+ dB$"), text);
        Assert.DoesNotContain("Spectral cutoff", text, StringComparison.Ordinal);

        // The row of a frequency is 43 Hz wide at 44.1 kHz: the text names the middle of it.
        Assert.Contains($" · {AudioGraph.Hertz(graph.RowCenterHertz(row))} · ", text, StringComparison.Ordinal);

        // On the cutoff's line the text carries its figures, as the CLI words them.
        int cutoffRow = graph.RowOfHertz(graph.Cutoff!.Frequency - 1);
        window.MouseMove(Window(window, window.Graph.PointOfCell(column, cutoffRow)));
        Assert.EndsWith(" · Spectral cutoff: 18,906 Hz (a step of 61.6 dB, transition from 18,648 Hz, in 20 of 20 frames with signal)", window.Graph.HoverText, StringComparison.Ordinal);

        window.MouseMove(new Point(2, 2));
        Assert.Null(window.Graph.HoverText);
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_the_spectrum_and_the_waveform_name_their_point()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        Choose(window, "Spectrum");
        var spectrum = (SpectrumGraph)window.Graph.Graph!;
        window.MouseMove(Window(window, window.Graph.PointOfCell(200)));
        Assert.Equal(200, window.Graph.HoverIndex);
        Assert.Matches(new Regex(@"^[\d,]+ Hz · -?\d+\.\d dB$"), window.Graph.HoverText!);
        Assert.Equal(spectrum.Describe(200), window.Graph.HoverText);

        int cutoffRow = spectrum.RowOfHertz(spectrum.Cutoff!.Frequency - 1);
        window.MouseMove(Window(window, window.Graph.PointOfCell(cutoffRow)));
        Assert.Contains("Spectral cutoff: 18,906 Hz", window.Graph.HoverText, StringComparison.Ordinal);

        Choose(window, "Waveform");
        var waveform = (WaveformGraph)window.Graph.Graph!;
        window.MouseMove(Window(window, window.Graph.PointOfCell(waveform.Columns / 2)));
        Assert.Matches(new Regex(@"^\d:\d\d\.\d · front center -?[\d,]+ to -?[\d,]+ \(16-bit sample codes\)$"), window.Graph.HoverText!);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_the_spectrogram_selects_the_frame_near_that_time()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        var graph = (SpectrogramGraph)window.Graph.Graph!;
        SampleLocator locator = graph.Locator!;
        Assert.Equal(44100, locator.Total);

        long previous = -1;
        foreach (int column in new[] { 1, graph.Columns / 3, graph.Columns - 2 })
        {
            Point point = Window(window, window.Graph.PointOfCell(column, 200));
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
            long offset = window.Model.SelectedNode.Node.Offset;
            Assert.Equal(locator.FrameOffsetAt(graph.PositionOfColumn(column)), offset);
            Assert.True(offset > previous);
            previous = offset;

            // The frame is marked on the picture, in the column its first sample falls in (a frame is two or three hops long here).
            int marked = window.Graph.MarkedIndex!.Value;
            Assert.InRange(column - marked, 0, 3);
        }

        // Selecting a frame in the tree marks its column.
        long second = (long)window.Model.Report!.Streams[0].Series[0].X.Span[2];
        window.Model.SelectFrameAt(second);
        Assert.Equal(graph.IndexOfFrame(second), window.Graph.MarkedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_the_waveform_selects_a_frame_and_the_spectrum_selects_none()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        Choose(window, "Waveform");
        var waveform = (WaveformGraph)window.Graph.Graph!;
        Point point = Window(window, window.Graph.PointOfCell(waveform.Columns - 2));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        TreeRow selected = window.Model.SelectedNode;

        Choose(window, "Spectrum");
        point = Window(window, window.Graph.PointOfCell(10));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Same(selected, window.Model.SelectedNode);
        Assert.Null(window.Graph.MarkedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void A_pcm_file_has_panels_and_a_click_selects_nothing()
    {
        DetailWindow window = Open(Fixtures.Wav("external-pcm-s16-stereo-44100.wav"));
        var graph = (SpectrogramGraph)window.Graph.Graph!;
        Assert.Null(graph.Locator);
        TreeRow? before = window.Model.SelectedNode;
        Point point = Window(window, window.Graph.PointOfCell(graph.Columns / 2, 100));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Same(before, window.Model.SelectedNode);
        Assert.Matches(new Regex(@"^\d:\d\d\.\d · [\d,]+ Hz · "), window.Graph.HoverText!);
        window.Close();
    }

    [AvaloniaFact]
    public void The_keys_move_the_cursor_over_the_spectrogram_and_enter_selects_the_frame()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        var graph = (SpectrogramGraph)window.Graph.Graph!;
        window.Graph.Focus();

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        int row = window.Graph.HoverRow;
        Assert.Equal(graph.RowOfHertz(graph.Cutoff!.Frequency - 1), row);
        for (int i = 0; i < 4; i++)
        {
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        }

        Assert.Equal(4, window.Graph.HoverIndex);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(3, window.Graph.HoverIndex);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(row + 1, window.Graph.HoverRow);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(row - 1, window.Graph.HoverRow);
        Assert.Equal(graph.DescribeCell(3, row - 1), window.Graph.HoverText);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(graph.Locator!.FrameOffsetAt(graph.PositionOfColumn(3)), window.Model.SelectedNode!.Node.Offset);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(graph.Columns - 1, window.Graph.HoverIndex);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(0, window.Graph.HoverIndex);

        // The spectrum and the waveform answer Left and Right, Home and End as the lines do.
        Choose(window, "Spectrum");
        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, window.Graph.HoverIndex);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(511, window.Graph.HoverIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void The_graphs_name_and_help_text_say_what_the_picture_shows()
    {
        DetailWindow window = Open(Fixtures.Measurement(CutoffFile));
        Assert.Equal("Spectrogram", AutomationProperties.GetName(window.Graph));
        string help = AutomationProperties.GetHelpText(window.Graph)!;
        Assert.Contains("frequency from 0 Hz to 22,050 Hz", help, StringComparison.Ordinal);
        Assert.Contains("level from -160 to 0 dB", help, StringComparison.Ordinal);
        Assert.Contains("Spectral cutoff: 18,906 Hz", help, StringComparison.Ordinal);

        window.MouseMove(Window(window, window.Graph.PointOfCell(5, 50)));
        Assert.EndsWith(window.Graph.HoverText!, AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);

        Choose(window, "Waveform");
        Assert.Equal("Waveform", AutomationProperties.GetName(window.Graph));
        Assert.Contains("1 channel over 0:01.0, in 16-bit sample codes", AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);
        Choose(window, "Spectrum");
        Assert.Equal("Spectrum", AutomationProperties.GetName(window.Graph));
        Assert.Contains("Level in dB from 0 Hz to 22,050 Hz", AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);

        // The entries and the command have names.
        Assert.Contains(window.GraphChoices.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Spectrogram");
        window.Close();
    }

    [AvaloniaFact]
    public void A_stream_of_several_channels_has_a_lane_for_each_and_a_spectrum_for_each()
    {
        DetailWindow window = Open(Fixtures.Flac("external-16-6ch.flac"));
        Choose(window, "Waveform");
        var waveform = (WaveformGraph)window.Graph.Graph!;
        Assert.Equal(6, waveform.Channels);
        IReadOnlyList<Rect> lanes = window.Graph.WaveformLanes()!;
        Assert.Equal(6, lanes.Count);
        Assert.All(lanes.Zip(lanes.Skip(1)), p => Assert.True(p.First.Bottom < p.Second.Top));

        window.MouseMove(Window(window, new Point(window.Graph.AudioPlotArea()!.Value.Center.X, lanes[2].Center.Y)));
        Assert.Contains("front left", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.Contains("LFE", window.Graph.HoverText, StringComparison.Ordinal);

        Choose(window, "Spectrum");
        Assert.True(window.Model.Graphs.HasForms);
        Assert.Equal(["Combined", "Per Channel"], window.Model.Graphs.Forms.Select(f => f.Title));
        Choose(window, "Spectrum", GraphForm.PerChannel);
        var spectrum = (SpectrumGraph)window.Graph.Graph!;
        Assert.Equal(6, spectrum.Lines);
        window.MouseMove(Window(window, window.Graph.PointOfCell(40)));
        Assert.Contains("front left", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.Contains("front right", window.Graph.HoverText, StringComparison.Ordinal);
        Assert.Contains("Legend: front left, front right", AutomationProperties.GetHelpText(window.Graph), StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void The_ramp_is_the_magma_table_through_a_fixed_range()
    {
        Assert.Equal(256, MagmaRamp.Entries);
        Assert.Equal(256 * 3, MagmaRamp.Rgb.Length);

        // The first and last entries of matplotlib's _magma_data, [0.001462, 0.000466, 0.013866] and [0.987053, 0.991438, 0.749504], times 255.
        Assert.Equal([0, 0, 4], MagmaRamp.Rgb[..3].ToArray());
        Assert.Equal([252, 253, 191], MagmaRamp.Rgb[^3..].ToArray());

        // Brightness rises through the table, so a louder cell is a lighter one: by steps of sixteen entries it never falls.
        for (int i = 16; i < MagmaRamp.Entries; i++)
        {
            Assert.True(Luminance(i) > Luminance(i - 16), $"entry {i}");
        }

        // The grid's byte is the level plus 200: 0 is no data, -160 dB and below the first entry, 0 dB and above the last.
        Assert.Equal(0, GraphView.RampColor(0).A);
        Assert.Equal(0, GraphView.RampIndex(-199));
        Assert.Equal(0, GraphView.RampIndex(-160));
        Assert.Equal(255, GraphView.RampIndex(0));
        Assert.Equal(255, GraphView.RampIndex(20));
        Assert.Equal(128, GraphView.RampIndex(-80));
        Assert.Equal(Color.FromRgb(0, 0, 4), GraphView.RampColor(1));
        Assert.Equal(Color.FromRgb(252, 253, 191), GraphView.RampColor(220));
        Assert.Equal(Color.FromRgb(MagmaRamp.Rgb[128 * 3], MagmaRamp.Rgb[(128 * 3) + 1], MagmaRamp.Rgb[(128 * 3) + 2]), GraphView.RampColor(120));

        static double Luminance(int i) => (0.2126 * MagmaRamp.Rgb[i * 3]) + (0.7152 * MagmaRamp.Rgb[(i * 3) + 1]) + (0.0722 * MagmaRamp.Rgb[(i * 3) + 2]);
    }

    [AvaloniaFact]
    public void The_spectrogram_draws_the_grid_through_the_ramp_in_light_and_dark_mode()
    {
        foreach (Avalonia.Styling.ThemeVariant variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = variant;
            DetailWindow window = Open(Fixtures.Measurement("synthetic-sweep.flac"));
            Rect plot = window.Graph.AudioPlotArea()!.Value;
            var graph = (SpectrogramGraph)window.Graph.Graph!;
            Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame()!;

            // The picture holds the ramp's colors: a pixel inside the plot is one of its entries (scaled cells may blend two neighbors).
            Point inView = window.Graph.TranslatePoint(new Point(plot.Left + (plot.Width / 2), plot.Bottom - 2), window)!.Value;
            Assert.NotEqual(0, frame.PixelSize.Width);
            Assert.True(PixelAt(frame, inView, window) is { A: 255 });

            // The sweep rises from low to high frequencies: the loudest row of the first columns is below that of the last.
            int Loudest(int column) => Enumerable.Range(0, graph.Rows).MaxBy(r => graph.Cell(column, r));
            Assert.True(Loudest(1) < Loudest(graph.Columns - 6), $"{Loudest(1)} against {Loudest(graph.Columns - 6)}");
            window.Close();
        }

        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static Color PixelAt(Avalonia.Media.Imaging.WriteableBitmap frame, Point point, TopLevel window)
    {
        double scale = window.RenderScaling;
        using Avalonia.Platform.ILockedFramebuffer buffer = frame.Lock();
        int x = (int)(point.X * scale);
        int y = (int)(point.Y * scale);
        byte[] pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes) + (x * 4), pixel, 0, 4);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    [AvaloniaFact]
    public void Large_grids_draw_and_answer_the_pointer_within_the_bound_of_the_other_graphs()
    {
        const int columns = MeasurementLimits.MaxColumns;
        const int channels = 8;
        var random = new Random(7);
        byte[] power = new byte[columns * MeasurementLimits.Rows];
        for (int c = 0; c < columns; c++)
        {
            for (int r = 0; r < MeasurementLimits.Rows; r++)
            {
                power[(c * MeasurementLimits.Rows) + r] = (byte)Math.Clamp(200 - (r / 4) - random.Next(30), 1, 220);
            }
        }

        short[] minimum = [.. Enumerable.Range(0, channels * columns).Select(i => (short)-random.Next(32768))];
        short[] maximum = [.. Enumerable.Range(0, channels * columns).Select(i => (short)random.Next(32768))];
        short[] row = [.. Enumerable.Range(0, MeasurementLimits.Rows).Select(r => (short)(-200 - (r * 3)))];
        var measurements = new StreamMeasurements
        {
            Status = MeasurementStatus.Complete,
            Samples = 2048L * columns * 8,
            SampleRate = 44100,
            Channels = channels,
            ChannelsMeasured = channels,
            Spectrogram = new SpectrogramGrid { Columns = columns, HopsPerColumn = 8, HopSamples = 2048, Power = power },
            Waveform = new WaveformGrid { Columns = columns, Channels = channels, Minimum = minimum, Maximum = maximum },
            Spectrum = new SpectrumFigures
            {
                FftSize = 4096,
                Combined = row,
                PerChannel = [.. Enumerable.Range(0, channels).Select(_ => (ReadOnlyMemory<short>)row)],
                Cutoff = new SpectralCutoff { IsFound = true, Frequency = 16000, TransitionStart = 15800, StepFrequency = 15900, StepTenthsOfDb = 600, FramesShowing = 9, FramesWithSignal = 10 },
            },
        };
        var stream = new StreamInfo { Nodes = [], Codec = "FLAC", SampleRate = 44100, Samples = measurements.Samples, Measurements = measurements };

        foreach (AudioGraph graph in new AudioGraph[] { new SpectrogramGraph(stream, measurements), new WaveformGraph(stream, measurements), new SpectrumGraph(stream, measurements, GraphForm.PerChannel) })
        {
            var view = new GraphView { Graph = graph };
            var window = new Window { Width = 1000, Height = 500, Content = view };
            window.Show();
            UiPump.Until(() => true);

            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                view.InvalidateVisual();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.NotNull(window.CaptureRenderedFrame());
            }

            double perFrame = clock.Elapsed.TotalMilliseconds / 10;
            Assert.True(perFrame < 500, $"{graph.Title}: {perFrame} ms a frame");

            clock.Restart();
            for (int i = 0; i < 200; i++)
            {
                window.MouseMove(view.TranslatePoint(view.PointOfCell((i * 5) % columns % graph.Count, i % 512)!.Value, window)!.Value);
            }

            Assert.True(clock.Elapsed.TotalMilliseconds / 200 < 50, $"{graph.Title}: {clock.Elapsed.TotalMilliseconds / 200} ms a move");
            window.Close();
        }
    }
}
