using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// A user can narrow the pane to a sliver: labels wider than the plot made Math.Clamp bounds cross, and Math.Clamp throws for that. Every graph
// form of a few streams (MPEG with the Frame Map, AAC, FLAC decoded with the audio panels) is drawn, pointed at and walked by key at widths
// and heights down to 1 px.
public class NarrowPaneTests
{
    public static TheoryData<string, bool> Streams() => new()
    {
        { Fixtures.Mpeg("external-lame-vbr-v2.mp3"), false },
        { Fixtures.Aac("ffmpeg-adts.aac"), false },
        { Fixtures.Measurement("synthetic-loudness-steps-8000.flac"), true },
    };

    [AvaloniaTheory(Timeout = 240000)]
    [MemberData(nameof(Streams))]
    public void Every_graph_form_draws_and_takes_the_pointer_and_keys_in_a_sliver_of_a_pane(string fixture, bool decode)
    {
        DetailWindow detail = AudioPanelTests.Open(fixture, decode);
        Graph[] graphs = [.. detail.Model.Graphs.Choices.SelectMany(c => c.Forms).Select(f => f.Graph)];
        detail.Close();
        Assert.NotEmpty(graphs);

        foreach (Graph graph in graphs)
        {
            foreach (double width in new double[] { 1, 20, 50, 79 })
            {
                foreach (double height in new double[] { 1, 30 })
                {
                    var view = new GraphView { Graph = graph };
                    var window = new Avalonia.Controls.Window { Width = width, Height = height, Content = view };
                    try
                    {
                        window.Show();
                        AudioPanelTests.Settle();
                        window.CaptureRenderedFrame();
                        window.MouseMove(new Point(width / 2, height / 2), RawInputModifiers.None);
                        AudioPanelTests.Settle();
                        view.Focus();
                        foreach (Key key in new[] { Key.Home, Key.Right, Key.Down, Key.End, Key.Left, Key.Up })
                        {
                            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                        }

                        AudioPanelTests.Settle();
                        window.CaptureRenderedFrame();
                    }
                    catch (Exception e)
                    {
                        throw new Xunit.Sdk.XunitException($"{graph.GetType().Name} at {width}x{height}: {e}");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        }
    }

    [Fact]
    public void The_clock_and_the_timestamp_format_any_number_of_seconds()
    {
        Assert.Equal("0:00", Format.Clock(double.NaN));
        Assert.Equal("0:00", Format.Clock(-5));
        Assert.Equal("1:02:03", Format.Clock(3723.9));
        Assert.Equal("99999:59:59", Format.Clock(3e15));
        Assert.Equal("99999:59:59", Format.Clock(double.PositiveInfinity));
        Assert.Equal("0:00.0", Format.Timestamp(double.NaN));
        Assert.Equal("1:02:03.9", Format.Timestamp(3723.95));
        Assert.Equal("99999:59:59.0", Format.Timestamp(3e15));
        Assert.Equal("99999:59:59.0", Format.Timestamp(double.PositiveInfinity));
    }
}
