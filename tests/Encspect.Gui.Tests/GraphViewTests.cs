using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;
using Encspect.Host;

namespace Encspect.Gui.Tests;

public class GraphViewTests
{
    [AvaloniaFact]
    public void Hovering_shows_a_points_value_and_clicking_selects_its_frame()
    {
        var window = new DetailWindow(new DetailViewModel(Analyzer.CreateDefault())) { Width = 1100, Height = 760 };
        window.Show();
        Task load = window.Model.LoadAsync(Fixtures.Stats("vbr-seconds.mp3"));
        UiPump.Until(() => load.IsCompleted);
        window.GraphsTab.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.True(window.Graph.IsEffectivelyVisible);
        Assert.False(window.FindingList.IsEffectivelyVisible);
        Assert.Equal("The audio of this MPEG-1 Layer III stream is not decoded here.", window.Model.Graphs.SelectedStream!.Caption!.Text);
        window.Model.Graphs.Selected = window.Model.Graphs.Choices.Single(c => c.Title == "Bitrate");
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var line = (LineGraph)window.Graph.Graph!;

        window.MouseMove(ScreenshotTests.PointOver(window, 40));
        Assert.Equal(40, window.Graph.HoverIndex);
        Assert.Equal(line.Describe(40), window.Graph.HoverText);
        Assert.StartsWith("Frame 41 · 0x", window.Graph.HoverText, StringComparison.Ordinal);

        Point point = ScreenshotTests.PointOver(window, 100);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(NodeKind.Frame, window.Model.SelectedNode!.Kind);
        Assert.Equal((long)line.Series.X.Span[100], window.Model.SelectedNode.Node.Offset);
        Assert.Equal(100, window.Graph.MarkedIndex);
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.Contains(window.TreeList.GetRealizedContainers(), c => c.DataContext == window.Model.SelectedNode);

        // Selecting a frame in the tree marks it on the chart.
        TreeRow other = window.Model.SelectFrameAt((long)line.Series.X.Span[7])!;
        Assert.Same(other, window.Model.SelectedNode);
        Assert.Equal(7, window.Graph.MarkedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void A_line_of_200000_points_draws_and_answers_the_pointer_quickly()
    {
        const int count = 200_000;
        double[] values = Enumerable.Range(0, count).Select(i => 128 + (64 * Math.Sin(i / 500.0))).ToArray();
        values[123_456] = 320;
        double[] offsets = Enumerable.Range(0, count).Select(i => i * 417.0).ToArray();
        var graph = new LineGraph(new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, offsets, values), offsets, count * 0.026);
        var view = new GraphView { Graph = graph };
        var window = new Window { Width = 1000, Height = 300, Content = view };
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
        Assert.True(perFrame < 500, $"{perFrame} ms a frame");

        // The peak is one point in its pixel column; pointing at its height finds it.
        int? found = null;
        for (double px = 0; px < view.Bounds.Width && found != 123_456; px++)
        {
            if (view.IndexAt(new Point(px, view.Bounds.Height / 2)) is not { } near || Math.Abs(near - 123_456) > 1000)
            {
                continue;
            }

            for (double y = 0; y < view.Bounds.Height && found != 123_456; y++)
            {
                found = view.IndexAt(new Point(px, y));
            }
        }

        Assert.Equal(123_456, found);
        window.Close();
    }
}
