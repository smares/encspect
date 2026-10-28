using Avalonia;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// The Loudness panel under the heading Audio (docs/design/spectral-analysis.md, step 5): the short-term loudness as a line over time with the
// integrated loudness as a reference line, and the loudness figures in its header, hover and automation text.
public class LoudnessPanelTests
{
    private const string Steps = "synthetic-loudness-steps-8000.flac";

    [AvaloniaFact]
    public void After_decoding_a_stream_of_three_seconds_or_more_the_loudness_panel_is_listed_between_the_spectrum_and_the_figures()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));

        GraphChoiceGroup audio = window.Model.Graphs.ChoiceGroups.Single(g => g.Heading == "Audio");
        Assert.Equal(["Waveform", "Spectrogram", "Spectrum", "Loudness", "Figures"], audio.Choices.Select(c => c.Title));
        AudioPanelTests.Choose(window, "Loudness");
        var graph = (LoudnessGraph)window.Graph.Graph!;
        Assert.Equal(51, graph.Count);
        Assert.Equal(3.0, graph.StartSeconds, 3);
        Assert.Equal(8.1, graph.EndSeconds, 3);
        Assert.NotNull(graph.IntegratedTenths);
        Assert.Contains(graph.Figures, f => f.Name == "Integrated Loudness");
        Assert.Contains(graph.Figures, f => f.Name == "Maximum True Peak Level");
        window.Close();
    }

    [AvaloniaFact]
    public void A_stream_shorter_than_three_seconds_has_no_loudness_panel_but_its_figures_are_listed()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement("lossy-lame-v2.flac"));

        Assert.DoesNotContain(window.Model.Graphs.Choices, c => c.Title == "Loudness");
        AudioPanelTests.Choose(window, "Figures");
        var figures = (FiguresGraph)window.Graph.Graph!;
        Assert.Contains(figures.Lines, l => l.Name == "Integrated Loudness" && l.Text.EndsWith("LUFS", StringComparison.Ordinal));
        Assert.Contains(figures.Lines, l => l.Name == "Loudness Range" && l.Text.StartsWith("none (", StringComparison.Ordinal));
        Assert.Contains(figures.Lines, l => l.Name == "Maximum True Peak Level" && l.Text.Contains("dBTP", StringComparison.Ordinal));
        Assert.All(figures.Lines, l => Assert.False(string.IsNullOrEmpty(l.Description)));
        window.Close();
    }

    [AvaloniaFact]
    public void Hovering_a_point_says_the_time_and_the_loudness_and_the_keys_move_along_the_line()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Loudness");
        var graph = (LoudnessGraph)window.Graph.Graph!;

        Point point = window.Graph.TranslatePoint(window.Graph.LoudnessDotOf(10)!.Value, window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        AudioPanelTests.Settle();
        Assert.Equal(10, window.Graph.HoverIndex);
        Assert.Equal(graph.Describe(10), window.Graph.HoverText ?? graph.Describe(10));
        Assert.Matches(@"^\d:\d\d\.\d · short-term loudness -\d+\.\d LUFS$", graph.Describe(10));

        // The line reads the two levels of the signal, 10 LU apart (125 Hz is a little under 1 kHz in the K-weighting): about -24.2 LUFS, then -34.2.
        Assert.InRange(graph.ValueAt(0), -24.8, -23.6);
        Assert.InRange(graph.ValueAt(graph.Count - 1), -34.8, -33.6);
        Assert.True(window.Graph.LoudnessDotOf(0)!.Value.Y < window.Graph.LoudnessDotOf(graph.Count - 1)!.Value.Y);

        window.Graph.Focus();
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(0, window.Graph.HoverIndex);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, window.Graph.HoverIndex);
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(graph.Count - 1, window.Graph.HoverIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void The_automation_text_names_the_axis_and_the_figures()
    {
        DetailWindow window = AudioPanelTests.Open(Fixtures.Measurement(Steps));
        AudioPanelTests.Choose(window, "Loudness");

        string help = AutomationProperties.GetHelpText(window.Graph) ?? string.Empty;
        Assert.Contains("Short-term loudness in LUFS from 0:03.0 to 0:08.1", help, StringComparison.Ordinal);
        Assert.Contains("Integrated Loudness", help, StringComparison.Ordinal);
        Assert.Contains("Loudness Range", help, StringComparison.Ordinal);
        Assert.Equal("Loudness", AutomationProperties.GetName(window.Graph));
        window.Close();
    }

    [Fact]
    public void The_axis_spans_at_least_twenty_LU_in_multiples_of_five_and_a_merged_point_says_how_many_values_it_averages()
    {
        var series = new Series("Short-Term Loudness", "LUFS", SeriesAxis.Seconds, new double[] { 3.0, 3.8 }, new double[] { -23.0, -23.1 });
        var measurements = new StreamMeasurements
        {
            Status = MeasurementStatus.Complete,
            SampleRate = 48000,
            Samples = 480_000,
            Loudness = new LoudnessFigures { ShortTerm = series, StepsPerPoint = 8, IntegratedTenths = -230, ChannelWeights = [100] },
        };
        var graph = new LoudnessGraph(new StreamInfo { Nodes = [], Codec = "FLAC", SampleRate = 48000 }, measurements);

        Assert.Equal(-20, graph.AxisTop);
        Assert.Equal(-40, graph.AxisBottom);
        Assert.Equal(0, graph.AxisTop % 5);
        Assert.Contains("(average of 8 values)", graph.Describe(1), StringComparison.Ordinal);
        Assert.Equal(3.8 + 0.8, graph.EndSeconds, 3);
    }

    private static LoudnessGraph GraphWithAGap()
    {
        // Three values 0.1 s apart, a gap of 4.7 s, then two more.
        var series = new Series("Short-Term Loudness", "LUFS", SeriesAxis.Seconds, new double[] { 3.0, 3.1, 3.2, 7.9, 8.0 }, new double[] { -23.0, -23.1, -23.2, -30.0, -30.1 });
        var measurements = new StreamMeasurements
        {
            Status = MeasurementStatus.Complete,
            SampleRate = 48000,
            Samples = 480_000,
            Loudness = new LoudnessFigures { ShortTerm = series, StepsPerPoint = 1, IntegratedTenths = -260, ChannelWeights = [100] },
        };
        return new LoudnessGraph(new StreamInfo { Nodes = [], Codec = "FLAC", SampleRate = 48000 }, measurements);
    }

    [Fact]
    public void The_points_sit_at_their_times_so_a_gap_is_a_longer_step_and_the_nearest_point_is_found_by_time()
    {
        LoudnessGraph graph = GraphWithAGap();
        double span = graph.EndSeconds - graph.StartSeconds;
        Assert.Equal(5.1, span, 6);
        for (int i = 0; i < graph.Count; i++)
        {
            Assert.Equal((graph.TimeAt(i) + 0.05 - graph.StartSeconds) / span, graph.FractionAt(i), 9);
            Assert.Equal(i, graph.IndexAtFraction(graph.FractionAt(i)));
        }

        Assert.True(graph.FractionAt(3) - graph.FractionAt(2) > 40 * (graph.FractionAt(1) - graph.FractionAt(0)));

        // In the middle of the gap the nearer side wins; at either end of the axis the end points do.
        Assert.Equal(2, graph.IndexAtFraction(graph.FractionAt(2) + 0.1));
        Assert.Equal(3, graph.IndexAtFraction(graph.FractionAt(3) - 0.1));
        Assert.Equal(0, graph.IndexAtFraction(0));
        Assert.Equal(4, graph.IndexAtFraction(1));
    }

    [AvaloniaFact]
    public void The_drawn_points_and_the_hover_follow_the_time_axis_over_a_gap()
    {
        LoudnessGraph graph = GraphWithAGap();
        var view = new GraphView { Graph = graph };
        var window = new Avalonia.Controls.Window { Width = 900, Height = 400, Content = view };
        window.Show();
        AudioPanelTests.Settle();

        Point[] dots = [.. Enumerable.Range(0, graph.Count).Select(i => view.LoudnessDotOf(i)!.Value)];
        double step = dots[1].X - dots[0].X;
        double gap = dots[3].X - dots[2].X;
        Assert.True(step > 0);
        Assert.Equal(50 * step, dots[4].X - dots[0].X, 1.0);
        Assert.InRange(gap / step, 46.0, 48.0);

        window.MouseMove(view.TranslatePoint(new Point(dots[3].X + 3, dots[3].Y), window)!.Value, RawInputModifiers.None);
        AudioPanelTests.Settle();
        Assert.Equal(3, view.HoverIndex);
        window.Close();
    }
}
