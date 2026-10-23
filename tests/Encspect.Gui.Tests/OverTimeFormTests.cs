using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Encspect.Core;
using Encspect.Gui.ViewModels;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// The over-time forms of a category chart: the stepped stacked area (the default) and its smoothed variant. Each second keeps its own shares, with a vertical step at its boundary and no gap between
// seconds; a stream that ends inside a second gives the last one only the width it lasts.
public class OverTimeFormTests
{
    private const int UnitsPerSecond = 40;

    // A series of two categories whose seconds hold the share of the second category the function gives, in 40ths.
    private static CategorySeries Series(int seconds, Func<int, int> secondShare, int lastUnits = UnitsPerSecond)
    {
        IReadOnlyList<Category> categories = [new Category("Left/right stereo", "a."), new Category("Mid/side stereo", "b.")];
        var builder = new CategorySeriesBuilder("Stereo Coding", categories, "frame", 1);
        for (int second = 0; second < seconds; second++)
        {
            for (int unit = 0; unit < (second == seconds - 1 ? lastUnits : UnitsPerSecond); unit++)
            {
                builder.Add((byte)(unit < secondShare(second) ? 1 : 0));
            }
        }

        var time = new TimeIndex(Enumerable.Range(0, seconds).Select(s => (double)s).ToArray(), Enumerable.Range(0, seconds).Select(s => s * UnitsPerSecond).ToArray());
        return builder.Build(Enumerable.Range(0, ((seconds - 1) * UnitsPerSecond) + lastUnits).Select(i => i * 100.0).ToArray(), null, null, "d.", null, time);
    }

    private static (Window Window, GraphView View) Show(CategoryGraph graph, string variant = "light")
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var view = new GraphView { Graph = graph };
        var window = new Window { Width = 900, Height = 420, Content = view };
        window.Show();
        UiPump.Until(() => true);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return (window, view);
    }

    private static Color Pixel(WriteableBitmap bitmap, double x, double y)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        unsafe
        {
            byte* pixel = (byte*)buffer.Address + ((int)y * buffer.RowBytes) + ((int)x * 4);
            return buffer.Format == PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
        }
    }

    private static bool Near(Color a, Color b, int tolerance = 10) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static void Reset() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default;

    [Fact]
    public void The_area_is_the_default_and_the_smoothed_form_is_offered_only_above_30_seconds()
    {
        Assert.Equal([GraphForm.OverTime, GraphForm.Frames, GraphForm.Summary], CategoryGraph.FormsOf(Series(30, s => 0)));
        Assert.Equal([GraphForm.OverTime, GraphForm.Smoothed, GraphForm.Frames, GraphForm.Summary], CategoryGraph.FormsOf(Series(31, s => 0)));
    }

    [Fact]
    public void The_category_with_the_largest_share_is_the_bottom_layer()
    {
        CategorySeries series = Series(10, s => s < 2 ? 36 : 4);

        Assert.Equal([0, 1], CategoryColumns.StackOrder(series));
        Assert.Equal([1, 0], CategoryColumns.StackOrder(Series(10, s => 30)));
        Assert.Equal([0, 1], CategoryColumns.StackOrder(Series(10, s => 20)));
    }

    [Fact]
    public void The_smoothing_averages_each_second_with_two_on_either_side_and_fewer_at_the_ends()
    {
        // One second of the second category, in the middle and at the start, the rest of the first.
        CategorySeries middle = Series(10, s => s == 5 ? 40 : 0);
        long[] smoothed = CategoryColumns.SmoothedCounts(middle, CategoryGraph.SmoothingWindow);
        long[] second = [.. Enumerable.Range(0, 10).Select(i => smoothed[(i * 2) + 1])];
        long[] total = [.. Enumerable.Range(0, 10).Select(i => smoothed[i * 2] + smoothed[(i * 2) + 1])];

        // Seconds 3 to 7 have the spike in their window of five: a fifth of it.
        Assert.Equal([0, 0, 0, 40, 40, 40, 40, 40, 0, 0], second);
        Assert.All(Enumerable.Range(2, 6), i => Assert.Equal(200, total[i]));

        CategorySeries edge = Series(10, s => s == 0 ? 40 : 0);
        smoothed = CategoryColumns.SmoothedCounts(edge, CategoryGraph.SmoothingWindow);

        // The first second has only itself and two after it: a third of its window is the spike.
        Assert.Equal(40, smoothed[1]);
        Assert.Equal(120, smoothed[0] + smoothed[1]);
        Assert.Equal(40, smoothed[3]);
        Assert.Equal(160, smoothed[2] + smoothed[3]);
        Assert.Equal(40, smoothed[5]);
        Assert.Equal(0, smoothed[7]);

        // The columns of the smoothed form hold these counts, and the unsmoothed ones the seconds' own.
        Assert.Equal(40, CategoryColumns.OverTime(edge, 10, smoothed).Counts(1)[1]);
        Assert.Equal(0, CategoryColumns.OverTime(edge, 10).Counts(1)[1]);
        Assert.Equal(40, CategoryColumns.OverTime(edge, 10).Counts(0)[1]);
    }

    [Fact]
    public void The_hover_text_of_the_smoothed_form_gives_the_seconds_own_shares_and_the_average()
    {
        var graph = new CategoryGraph(Series(10, s => s == 0 ? 40 : 0), GraphForm.Smoothed);

        string text = graph.Describe(1);

        Assert.StartsWith("0:01 to 0:02 · Left/right stereo 100.0% (40 values) · average of 5 seconds: ", text, StringComparison.Ordinal);
        Assert.EndsWith("Mid/side stereo 25.0% · Left/right stereo 75.0%", text, StringComparison.Ordinal);
    }

    // The steps of the area are where the seconds are: a pixel row through the plot changes color at the slot of every second
    // and holds no pixel of the surface in between, and the last, shorter second takes only the width it lasts.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_area_steps_at_the_second_boundaries_without_gaps_and_the_last_second_is_as_wide_as_it_lasts(string variant)
    {
        Func<int, int> share = s => s % 2 == 0 ? 0 : 40;
        (Window window, GraphView view) = Show(new CategoryGraph(Series(10, share), GraphForm.OverTime) { Duration = 9.5 }, variant);
        Rect plot = view.PlotArea()!.Value;
        CategoryStyle[] styles = CategoryColors.For(Series(10, share), variant == "dark", variant == "dark" ? Colors.Black : Colors.White);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double y = plot.Top + (plot.Height * 0.6);
        Color surface = variant == "dark" ? Colors.Black : Colors.White;
        GraphView.Slots slots = GraphView.SlotsOf(plot, 10, Enumerable.Range(0, 10).Select(s => (double)s).ToArray(), 9.5);

        for (int second = 0; second < 10; second++)
        {
            Color expected = styles[second % 2].Color;
            Assert.True(Near(expected, Pixel(frame, slots.X(second) + 2, y)), $"The start of second {second} is {Pixel(frame, slots.X(second) + 2, y)}.");
            Assert.True(Near(expected, Pixel(frame, slots.X(second) + slots.W(second) - 3, y)), $"The end of second {second} is {Pixel(frame, slots.X(second) + slots.W(second) - 3, y)}.");
        }

        // Every pixel of the row across the plot is a category's color, never the surface: no gap between seconds.
        for (int x = (int)plot.Left + 1; x < (int)plot.Right - 1; x++)
        {
            Color color = Pixel(frame, x, y);
            Assert.False(Near(surface, color, 4), $"The surface shows at {x}.");
        }

        // The last second lasts half a second and is half as wide as the others.
        Assert.Equal(slots.W(0) / 2, slots.W(9), 0.01);
        Assert.Equal(plot.Right, slots.X(9) + slots.W(9), 0.01);
        Assert.True(Near(surface, Pixel(frame, plot.Right + 3, y), 4));
        window.Close();
        Reset();
    }

    // The bottom layer of the area is the category with the largest share, whichever its number: the first second is mostly the
    // first category, the stream mostly the second.
    [AvaloniaFact]
    public void The_area_puts_the_dominant_category_at_the_bottom_and_the_legend_lists_it_first()
    {
        CategorySeries series = Series(10, s => s == 0 ? 10 : 36);
        CategoryStyle[] styles = CategoryColors.For(series, false, Colors.White);

        (Window area, GraphView areaView) = Show(new CategoryGraph(series, GraphForm.OverTime));
        Rect plot = areaView.PlotArea()!.Value;
        WriteableBitmap frame = area.CaptureRenderedFrame()!;
        double x = plot.Left + 6;
        Assert.True(Near(styles[1].Color, Pixel(frame, x, plot.Bottom - 3)), "The bottom of the area is the dominant category.");
        Assert.True(Near(styles[0].Color, Pixel(frame, x, plot.Top + 3)));
        area.Close();

        Reset();
    }

    [AvaloniaTheory]
    [InlineData(GraphForm.OverTime)]
    [InlineData(GraphForm.Smoothed)]
    public void The_pointer_answers_with_the_second_it_is_over_and_the_last_second_is_narrow(GraphForm form)
    {
        CategorySeries series = Series(40, s => s % 3 * 10);
        (Window window, GraphView view) = Show(new CategoryGraph(series, form) { Duration = 39.5 });
        Rect plot = view.PlotArea()!.Value;
        GraphView.Slots slots = GraphView.SlotsOf(plot, 40, Enumerable.Range(0, 40).Select(s => (double)s).ToArray(), 39.5);
        double y = plot.Top + (plot.Height / 2);

        for (int second = 0; second < 40; second++)
        {
            Assert.Equal(second, view.IndexAt(new Point(slots.X(second) + 1, y)));
            Assert.Equal(second, view.IndexAt(new Point(slots.X(second) + slots.W(second) - 1.5, y)));
        }

        // The step is exactly where the second ends: half a second wide at the end.
        Assert.Equal(slots.W(0) / 2, slots.W(39), 0.01);
        window.MouseMove(view.TranslatePoint(new Point(slots.X(7) + 2, y), window)!.Value);
        UiPump.Until(() => true);
        string? help = AutomationProperties.GetHelpText(view);
        Assert.Contains("0:07 to 0:08", help, StringComparison.Ordinal);
        Assert.Contains(form == GraphForm.Smoothed ? "average of 5 seconds" : "Mid/side stereo 25.0% (10 values)", help, StringComparison.Ordinal);
        window.MouseMove(view.TranslatePoint(new Point(plot.Right - 2, y), window)!.Value);
        Assert.Contains("0:39 to the end", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);
        window.Close();
        Reset();
    }

    [AvaloniaFact]
    public void The_smoothed_form_draws_the_averages_not_the_seconds_own_shares()
    {
        // A single second of the second category, 40 in all: raw, only second 5 shows it; smoothed, the five seconds around it.
        CategorySeries series = Series(40, s => s == 20 ? 40 : 0);
        CategoryStyle[] styles = CategoryColors.For(series, false, Colors.White);

        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime));
        Rect plot = view.PlotArea()!.Value;
        GraphView.Slots slots = GraphView.SlotsOf(plot, 40, Enumerable.Range(0, 40).Select(s => (double)s).ToArray(), 0);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double top = plot.Top + 3;
        Assert.True(Near(styles[1].Color, Pixel(frame, slots.Center(20), top)) || Near(styles[1].Color, Pixel(frame, slots.Center(20), plot.Bottom - 3)));
        Assert.False(Near(styles[1].Color, Pixel(frame, slots.Center(19), plot.Bottom - 3)) || Near(styles[1].Color, Pixel(frame, slots.Center(19), top)));
        window.Close();

        (window, view) = Show(new CategoryGraph(series, GraphForm.Smoothed));
        frame = window.CaptureRenderedFrame()!;
        foreach (int second in new[] { 18, 19, 21, 22 })
        {
            Assert.True(Near(styles[1].Color, Pixel(frame, slots.Center(second), plot.Bottom - 3)) || Near(styles[1].Color, Pixel(frame, slots.Center(second), top)), $"Second {second} shows no share of the spike.");
        }

        Assert.False(Near(styles[1].Color, Pixel(frame, slots.Center(17), plot.Bottom - 3)) || Near(styles[1].Color, Pixel(frame, slots.Center(17), top)));
        window.Close();
        Reset();
    }

    [Fact]
    public void A_last_second_of_fewer_than_eight_frames_is_few_and_one_of_eight_is_not()
    {
        (bool isFew, int frames, double seconds) = new CategoryGraph(Series(10, s => 0, 3), GraphForm.OverTime) { Duration = 9.075 }.LastInterval!.Value;
        Assert.True(isFew);
        Assert.Equal(3, frames);
        Assert.Equal(0.075, seconds, 1e-9);
        Assert.Equal((true, 7, 0), new CategoryGraph(Series(10, s => 0, 7), GraphForm.OverTime).LastInterval);
        Assert.False(new CategoryGraph(Series(10, s => 0, CategoryGraph.FewFrames), GraphForm.OverTime).LastInterval!.Value.IsFew);
        Assert.False(new CategoryGraph(Series(10, s => 0), GraphForm.OverTime).LastInterval!.Value.IsFew);
        Assert.Null(new CategoryGraph(Series(1, s => 0), GraphForm.OverTime).LastInterval);
    }

    [Fact]
    public void The_hover_text_names_a_last_second_of_few_frames_and_only_that()
    {
        var few = new CategoryGraph(Series(10, s => 0, 1), GraphForm.OverTime) { Duration = 9.03 };

        Assert.StartsWith("Last 0.03 s, 1 frame · 0:09 to the end · ", few.Describe(9), StringComparison.Ordinal);
        Assert.DoesNotContain("Last", few.Describe(8), StringComparison.Ordinal);
        Assert.StartsWith("Last 1 frame · ", new CategoryGraph(Series(10, s => 0, 1), GraphForm.OverTime).Describe(9), StringComparison.Ordinal);
        Assert.StartsWith("Last 0.03 s, 1 frame · 0:09 to the end · ", new CategoryGraph(Series(10, s => 0, 1), GraphForm.Smoothed) { Duration = 9.03 }.Describe(9), StringComparison.Ordinal);
        Assert.DoesNotContain("Last", new CategoryGraph(Series(10, s => 0, 20), GraphForm.OverTime) { Duration = 9.5 }.Describe(9), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_last_second_of_few_frames_is_drawn_dimmed_and_a_longer_one_is_not()
    {
        foreach ((int lastUnits, bool dimmed) in new[] { (3, true), (20, false) })
        {
            CategorySeries series = Series(10, s => 0, lastUnits);
            (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime) { Duration = 9 + (lastUnits / 40.0) });
            Rect plot = view.PlotArea()!.Value;
            GraphView.Slots slots = GraphView.SlotsOf(plot, 10, Enumerable.Range(0, 10).Select(s => (double)s).ToArray(), 9 + (lastUnits / 40.0));
            WriteableBitmap frame = window.CaptureRenderedFrame()!;
            Color color = CategoryColors.For(series, false, Colors.White)[0].Color;
            double y = plot.Top + (plot.Height * 0.6);

            Assert.True(Near(color, Pixel(frame, slots.Center(8), y)));
            Assert.Equal(!dimmed, Near(color, Pixel(frame, slots.Center(9), y)));
            window.Close();
        }

        Reset();
    }

    [Fact]
    public void The_smoothed_counts_are_made_once_for_the_hover_text()
    {
        var graph = new CategoryGraph(Series(40, s => s % 7), GraphForm.Smoothed);

        Assert.Same(graph.SmoothedCounts, graph.SmoothedCounts);
        Assert.Equal(CategoryColumns.SmoothedCounts(graph.Series, CategoryGraph.SmoothingWindow), graph.SmoothedCounts);
    }
}
