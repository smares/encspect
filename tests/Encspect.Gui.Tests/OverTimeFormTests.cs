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

// The over-time forms of a category chart: the stepped stacked area (the default) and its smoothed variant. Each interval keeps
// its own shares, with a vertical step at its boundary and no gap between intervals; a stream that ends inside an interval gives
// the last one only the width it lasts; the bottom layer is the largest category, in the muted color. The pixel tests look at what is drawn, with positions worked out from the plot and the number of intervals
// alone and colors from the palette.
public class OverTimeFormTests
{
    private const int UnitsPerSecond = 40;

    private static readonly IReadOnlyList<Category> Names =
    [
        new Category("Left/right stereo", "a."),
        new Category("Mid/side stereo", "b."),
        new Category("Intensity stereo", "c."),
    ];

    // A series with a time index of one interval a second, the units in each as the first function says and their category as the
    // second: code(interval, unit within the interval).
    private static CategorySeries Make(int categories, int seconds, Func<int, int> unitsIn, Func<int, int, int> code)
    {
        var builder = new CategorySeriesBuilder("Stereo Coding", [.. Names.Take(categories)], "frame", 1);
        int[] first = new int[seconds];
        int units = 0;
        for (int second = 0; second < seconds; second++)
        {
            first[second] = units;
            for (int unit = 0; unit < unitsIn(second); unit++)
            {
                builder.Add((byte)code(second, unit));
                units++;
            }
        }

        var time = new TimeIndex(Enumerable.Range(0, seconds).Select(s => (double)s).ToArray(), first);
        return builder.Build(Enumerable.Range(0, units).Select(i => i * 100.0).ToArray(), null, null, "d.", null, time);
    }

    // Two categories: the second holds secondShare(second) of 40 units of the second category and the rest of the first; the last
    // interval holds lastUnits units.
    private static CategorySeries Series(int seconds, Func<int, int> secondShare, int lastUnits = UnitsPerSecond) =>
        Make(2, seconds, s => s == seconds - 1 ? lastUnits : UnitsPerSecond, (s, u) => u < secondShare(s) ? 1 : 0);

    private static (Window Window, GraphView View) Show(CategoryGraph graph, string variant = "light", double width = 900)
    {
        Application.Current!.RequestedThemeVariant = variant == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var view = new GraphView { Graph = graph };
        var window = new Window { Width = width, Height = 420, Content = view };
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

    private static Color SurfaceOf(string variant) => variant == "dark" ? Colors.Black : Colors.White;

    private static void Reset() => Application.Current!.RequestedThemeVariant = ThemeVariant.Default;

    // --- the model

    [Fact]
    public void The_area_is_the_default_and_the_smoothed_form_is_offered_only_above_30_seconds()
    {
        Assert.Equal([GraphForm.OverTime, GraphForm.Frames, GraphForm.Summary], CategoryGraph.FormsOf(Series(30, s => 0)));
        Assert.Equal([GraphForm.OverTime, GraphForm.Smoothed, GraphForm.Frames, GraphForm.Summary], CategoryGraph.FormsOf(Series(31, s => 0)));
    }

    [Fact]
    public void The_category_with_the_largest_share_is_the_bottom_layer()
    {
        Assert.Equal([0, 1], CategoryColumns.StackOrder(Series(10, s => s < 2 ? 36 : 4)));
        Assert.Equal([1, 0], CategoryColumns.StackOrder(Series(10, s => 30)));
        Assert.Equal([0, 1], CategoryColumns.StackOrder(Series(10, s => 20)));
        Assert.Equal([2, 0, 1], CategoryColumns.StackOrder(Make(3, 4, s => 10, (s, u) => u < 6 ? 2 : u < 9 ? 0 : 1)));
    }

    [Fact]
    public void The_smoothing_averages_each_second_with_two_on_either_side_and_fewer_at_the_ends()
    {
        // One second of the second category, in the middle and at the start, the rest of the first.
        long[] smoothed = CategoryColumns.SmoothedCounts(Series(10, s => s == 5 ? 40 : 0), CategoryGraph.SmoothingWindow);
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
    public void The_smoothing_equals_a_brute_force_average_and_leaves_an_empty_second_empty()
    {
        var random = new Random(5);
        int[] units = [.. Enumerable.Range(0, 60).Select(s => s is 20 or 21 or 40 ? 0 : 1 + random.Next(30))];
        byte[][] codes = [.. units.Select(n => Enumerable.Range(0, n).Select(_ => (byte)random.Next(3)).ToArray())];
        CategorySeries series = Make(3, 60, s => units[s], (s, u) => codes[s][u]);

        long[] smoothed = CategoryColumns.SmoothedCounts(series, 5);

        for (int second = 0; second < 60; second++)
        {
            long[] expected = new long[3];
            if (units[second] > 0)
            {
                for (int near = Math.Max(0, second - 2); near <= Math.Min(59, second + 2); near++)
                {
                    foreach (byte code in codes[near])
                    {
                        expected[code]++;
                    }
                }
            }

            Assert.Equal(expected, smoothed.AsSpan(second * 3, 3).ToArray());
        }
    }

    [Fact]
    public void The_counts_of_the_intervals_are_made_once_and_the_columns_merge_them()
    {
        CategorySeries series = Series(40, s => s % 7);
        var graph = new CategoryGraph(series, GraphForm.Smoothed);

        Assert.Same(graph.IntervalCounts, graph.IntervalCounts);
        Assert.Same(graph.SmoothedCounts, graph.SmoothedCounts);
        Assert.Equal(CategoryColumns.SmoothedCounts(series, CategoryGraph.SmoothingWindow), graph.SmoothedCounts);
        Assert.Same(graph.StackOrder, graph.StackOrder);

        // Intervals that outnumber the columns merge into columns that hold the sums of their intervals.
        CategoryColumns merged = CategoryColumns.OverTime(series, 7, graph.IntervalCounts);
        Assert.Equal(7, merged.Count);
        Assert.Equal(series.Count, Enumerable.Range(0, 7).Sum(c => merged.Total(c)));
        Assert.Equal(Enumerable.Range(0, 7).SelectMany(c => merged.Counts(c).ToArray()), CategoryColumns.OverTime(series, 7).Counts(0).ToArray().Concat(Enumerable.Range(1, 6).SelectMany(c => CategoryColumns.OverTime(series, 7).Counts(c).ToArray())));
    }

    [Fact]
    public void The_last_interval_is_few_only_where_it_is_partial_and_has_fewer_than_eight_frames_but_one()
    {
        (bool isFew, int frames, double seconds) = new CategoryGraph(Series(10, s => 0, 3), GraphForm.OverTime) { Duration = 9.075 }.LastInterval!.Value;
        Assert.True(isFew);
        Assert.Equal(3, frames);
        Assert.Equal(0.075, seconds, 1e-9);
        Assert.True(new CategoryGraph(Series(10, s => 0, CategoryGraph.FewFrames - 1), GraphForm.OverTime) { Duration = 9.2 }.LastInterval!.Value.IsFew);
        Assert.False(new CategoryGraph(Series(10, s => 0, CategoryGraph.FewFrames), GraphForm.OverTime) { Duration = 9.2 }.LastInterval!.Value.IsFew);
        Assert.False(new CategoryGraph(Series(10, s => 0), GraphForm.OverTime) { Duration = 9.5 }.LastInterval!.Value.IsFew);

        // A last interval as long as the others is no partial one, however few frames it holds (frames longer than an eighth of a
        // second); one without frames is not few either; and without a duration nothing says it is partial.
        Assert.False(new CategoryGraph(Series(10, s => 0, 5), GraphForm.OverTime) { Duration = 10 }.LastInterval!.Value.IsFew);
        Assert.False(new CategoryGraph(Series(10, s => 0, 0), GraphForm.OverTime) { Duration = 9.5 }.LastInterval!.Value.IsFew);
        Assert.Null(new CategoryGraph(Series(10, s => 0, 3), GraphForm.OverTime).LastInterval);
        Assert.Null(new CategoryGraph(Series(1, s => 0), GraphForm.OverTime) { Duration = 0.5 }.LastInterval);
    }

    [Fact]
    public void The_hover_text_names_a_partial_last_interval_of_few_frames_in_the_area_and_nothing_else()
    {
        var few = new CategoryGraph(Series(10, s => 0, 1), GraphForm.OverTime) { Duration = 9.03 };

        Assert.StartsWith("Last 0.03 s, 1 frame · 0:09 to the end · ", few.Describe(9), StringComparison.Ordinal);
        Assert.DoesNotContain("Last", few.Describe(8), StringComparison.Ordinal);
        Assert.DoesNotContain("Last", new CategoryGraph(Series(10, s => 0, 20), GraphForm.OverTime) { Duration = 9.5 }.Describe(9), StringComparison.Ordinal);
        Assert.DoesNotContain("Last", new CategoryGraph(Series(10, s => 0, 5), GraphForm.OverTime) { Duration = 10 }.Describe(9), StringComparison.Ordinal);
        Assert.StartsWith("Last under 0.01 s, 1 frame · ", new CategoryGraph(Series(10, s => 0, 1), GraphForm.OverTime) { Duration = 9.004 }.Describe(9), StringComparison.Ordinal);

        // The average the smoothed form draws is not a share of those frames, so it does not name them.
        Assert.DoesNotContain("Last", new CategoryGraph(Series(10, s => 0, 1), GraphForm.Smoothed) { Duration = 9.03 }.Describe(9), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hover_text_lists_the_shares_in_the_order_of_the_stack_and_the_average_says_how_many_seconds_it_holds()
    {
        var area = new CategoryGraph(Series(10, s => s == 0 ? 40 : 10), GraphForm.OverTime);
        Assert.Equal("0:00 to 0:01 · Mid/side stereo 100.0% (40 values)", area.Describe(0));
        Assert.Equal("0:03 to 0:04 · Left/right stereo 75.0% (30 values) · Mid/side stereo 25.0% (10 values)", area.Describe(3));

        var smoothed = new CategoryGraph(Series(10, s => s == 0 ? 40 : 0), GraphForm.Smoothed);

        // The first second averages three, the next four, the rest five.
        Assert.EndsWith(" · average of 3 seconds: Left/right stereo 66.7% · Mid/side stereo 33.3%", smoothed.Describe(0), StringComparison.Ordinal);
        Assert.EndsWith(" · average of 4 seconds: Left/right stereo 75.0% · Mid/side stereo 25.0%", smoothed.Describe(1), StringComparison.Ordinal);
        Assert.EndsWith(" · average of 5 seconds: Left/right stereo 100.0%", smoothed.Describe(4), StringComparison.Ordinal);
        Assert.StartsWith("0:01 to 0:02 · Left/right stereo 100.0% (40 values) · average of ", smoothed.Describe(1), StringComparison.Ordinal);
        Assert.Contains("average of up to 5 seconds:", smoothed.DescribeSmoothed(0, 3), StringComparison.Ordinal);

        // A gap is a gap in the average as well: nothing is averaged into it.
        var gap = new CategoryGraph(Make(2, 10, s => s == 4 ? 0 : 40, (s, u) => 0), GraphForm.Smoothed);
        Assert.Equal("0:04 to 0:05 · no values", gap.Describe(4));
        Assert.Contains("average of 4 seconds", gap.Describe(3), StringComparison.Ordinal);

        // Past eight categories the hover text says how many more.
        IReadOnlyList<Category> many = CategoryTestData.Numbered("Mode", 10);
        var builder = new CategorySeriesBuilder("Many", many, "frame", 1);
        for (int unit = 0; unit < 100; unit++)
        {
            builder.Add((byte)(unit % 10));
        }

        CategorySeries ten = builder.Build(Enumerable.Range(0, 100).Select(i => i * 100.0).ToArray(), null, null, "d.", null, new TimeIndex(new[] { 0.0, 1.0 }, new[] { 0, 50 }));
        Assert.EndsWith(" · 2 more", new CategoryGraph(ten, GraphForm.OverTime).Describe(0), StringComparison.Ordinal);
        Assert.EndsWith(" · 2 more", new CategoryGraph(ten, GraphForm.Smoothed).Describe(0), StringComparison.Ordinal);
    }

    [Fact]
    public void The_colors_of_the_stack_give_the_muted_one_to_the_bottom_layer_and_full_ones_to_the_layers_above()
    {
        // The second category is the largest: it is the bottom layer and has the muted first hue, and the first, the rarer layer
        // above it, the second hue in full.
        CategorySeries series = Series(10, s => 30);
        foreach (bool dark in new[] { false, true })
        {
            Color surface = dark ? Colors.Black : Colors.White;
            IReadOnlyList<Color> hues = dark ? CategoryColors.Dark : CategoryColors.Light;
            CategoryStyle[] stack = CategoryColors.ForStack(series, dark, surface);
            CategoryStyle[] plain = CategoryColors.For(series, dark, surface);

            Assert.Equal(CategoryColors.Mix(hues[0], surface, 0.7), stack[1].Color);
            Assert.Equal(hues[1], stack[0].Color);
            Assert.Equal(CategoryColors.Mix(hues[0], surface, 0.7), plain[0].Color);
            Assert.Equal(hues[1], plain[1].Color);
        }

        // Where the first category is the largest the two agree.
        Assert.Equal(CategoryColors.For(Series(10, s => 10), false, Colors.White).Select(s => s.Color), CategoryColors.ForStack(Series(10, s => 10), false, Colors.White).Select(s => s.Color));
    }

    [Fact]
    public void A_ramp_keeps_its_steps_by_category_and_a_reserved_category_stays_hatched_in_the_stack()
    {
        var ordered = new CategorySeriesBuilder("Steps", CategoryTestData.Numbered("Step", 4), "frame", 1, isOrdered: true);
        for (int unit = 0; unit < 10; unit++)
        {
            ordered.Add((byte)(unit < 7 ? 3 : 0));
        }

        CategorySeries ramp = ordered.Build(new double[10], null, null, "d.");
        Assert.Equal(CategoryColors.For(ramp, false, Colors.White).Select(s => s.Color), CategoryColors.ForStack(ramp, false, Colors.White).Select(s => s.Color));

        IReadOnlyList<Category> categories = [new Category("Real", "a."), new Category("Reserved", "b.") { IsReserved = true }, new Category("Other real", "c.")];
        var builder = new CategorySeriesBuilder("R", categories, "frame", 1);
        for (int unit = 0; unit < 10; unit++)
        {
            builder.Add((byte)(unit < 6 ? 1 : unit < 9 ? 2 : 0));
        }

        CategoryStyle[] styles = CategoryColors.ForStack(builder.Build(new double[10], null, null, "d."), false, Colors.White);
        Assert.True(styles[1].IsHatched);
        Assert.Equal(CategoryColors.Mix(CategoryColors.Light[0], Colors.White, 0.7), styles[2].Color);
        Assert.Equal(CategoryColors.Light[1], styles[0].Color);
    }

    [Fact]
    public void Text_on_any_color_of_the_palette_has_a_contrast_of_at_least_4_5()
    {
        foreach (bool dark in new[] { false, true })
        {
            Color surface = dark ? Colors.Black : Colors.White;
            var colors = new List<Color>();
            foreach (CategorySeries series in new[] { Series(10, s => 30), Make(3, 4, s => 10, (s, u) => u % 3), Make(3, 4, s => 10, (s, u) => 2 - (u % 3)) })
            {
                colors.AddRange(CategoryColors.ForStack(series, dark, surface).Select(s => s.Color));
                colors.AddRange(CategoryColors.For(series, dark, surface).Select(s => s.Color));
            }

            colors.AddRange(dark ? CategoryColors.Dark : CategoryColors.Light);
            colors.Add(CategoryColors.Mix(colors[0], surface, 0.7));
            foreach (Color color in colors)
            {
                Assert.True(CategoryColors.ContrastRatio(CategoryColors.Readable(color), color) >= 4.5, $"{color} has {CategoryColors.ContrastRatio(CategoryColors.Readable(color), color)}");
            }
        }

        Assert.Equal(21, CategoryColors.ContrastRatio(Colors.Black, Colors.White), 0.01);
    }

    [Fact]
    public void The_legend_of_the_strip_keeps_the_order_of_the_categories_and_that_of_the_area_follows_the_stack()
    {
        CategorySeries series = Series(10, s => 30);

        Assert.Equal([1, 0], GraphView.LegendEntries(new CategoryGraph(series, GraphForm.OverTime)).Select(e => e.Category));
        Assert.Equal([1, 0], GraphView.LegendEntries(new CategoryGraph(series, GraphForm.Smoothed)).Select(e => e.Category));
        Assert.Equal([0, 1], GraphView.LegendEntries(new CategoryGraph(series, GraphForm.Frames)).Select(e => e.Category));
        Assert.Equal([0, 1], GraphView.LegendEntries(new CategoryGraph(series, GraphForm.Summary)).Select(e => e.Category));
    }

    // --- what is drawn

    // The steps of the area are where the intervals are: with nine intervals of a second and one of half a second, the plot is
    // 9.5 units wide, and a row through it changes color at k units, with the last color to the right edge, and holds no pixel of
    // the surface in between.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_area_steps_at_the_second_boundaries_without_gaps_and_the_last_second_is_as_wide_as_it_lasts(string variant)
    {
        CategorySeries series = Series(10, s => s % 2 == 0 ? 0 : 40, 20);
        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime) { Duration = 9.5 }, variant);
        Rect plot = view.PlotArea()!.Value;
        CategoryStyle[] styles = CategoryColors.ForStack(series, variant == "dark", SurfaceOf(variant));
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double y = plot.Top + (plot.Height * 0.6);
        double unit = plot.Width / 9.5;

        for (int second = 0; second < 10; second++)
        {
            // Pure left of the boundary at k units and pure right of it: pixels whole on either side of the step.
            double x = plot.Left + (second * unit);
            Color expected = styles[second % 2].Color;
            if (second > 0)
            {
                Assert.True(Near(styles[(second - 1) % 2].Color, Pixel(frame, Math.Floor(x) - 1, y)), $"Left of the step at {second}: {Pixel(frame, Math.Floor(x) - 1, y)}.");
                Assert.True(Near(expected, Pixel(frame, Math.Ceiling(x), y)), $"Right of the step at {second}: {Pixel(frame, Math.Ceiling(x), y)}.");
            }

            Assert.True(Near(expected, Pixel(frame, x + 3, y)));
        }

        // No pixel across the plot is the surface: no gap between intervals, none at the steps (a pixel on a step blends the two).
        for (int x = (int)Math.Ceiling(plot.Left); x < (int)plot.Right; x++)
        {
            Color color = Pixel(frame, x, y);
            Assert.False(Near(SurfaceOf(variant), color, 6), $"The surface shows at {x}.");
        }

        // The last interval of 0.5 s ends at the plot's right edge, which is half a unit from its start.
        Assert.True(Near(styles[1].Color, Pixel(frame, plot.Right - 1, y)) || Near(CategoryColors.Mix(styles[1].Color, SurfaceOf(variant), 0.4), Pixel(frame, plot.Right - 1, y), 12));
        Assert.True(Near(SurfaceOf(variant), Pixel(frame, plot.Right + 3, y), 4));
        window.Close();
        Reset();
    }

    // A layer one value in 4,000 would be 0.06 of a pixel; the area gives every category that occurs a pixel, of its own color,
    // not a tint of it: the line between layers is not drawn over a layer that small.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void A_rare_layer_between_two_others_keeps_its_color(string variant)
    {
        // 3,997 values of the first category, 2 of the second and 1 of the third: the second is in the middle of the stack, at
        // 0.1 pixel; the third is on top of it at 0.05.
        CategorySeries series = Make(3, 10, s => 4000, (s, u) => u < 3997 ? 0 : u < 3999 ? 1 : 2);
        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime), variant);
        Rect plot = view.PlotArea()!.Value;
        CategoryStyle[] styles = CategoryColors.ForStack(series, variant == "dark", SurfaceOf(variant));
        WriteableBitmap frame = window.CaptureRenderedFrame()!;

        foreach (int category in new[] { 1, 2 })
        {
            int rows = 0;
            for (double y = plot.Top; y < plot.Bottom; y++)
            {
                rows += Near(styles[category].Color, Pixel(frame, plot.Left + 100, y), 6) ? 1 : 0;
            }

            Assert.Equal(1, rows);
        }

        window.Close();
        Reset();
    }

    // Two tall layers have a line of the surface between them, one pixel, and at a full switch from one category to the other
    // there is none: no stripe at the step.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Tall_layers_are_separated_by_a_line_of_the_surface_and_a_full_switch_has_no_stripe(string variant)
    {
        CategorySeries series = Series(10, s => s < 5 ? 20 : s < 8 ? 0 : 40);
        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime), variant);
        Rect plot = view.PlotArea()!.Value;
        Color surface = SurfaceOf(variant);
        CategoryStyle[] styles = CategoryColors.ForStack(series, variant == "dark", surface);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double unit = plot.Width / 10;

        // Half and half: one row of the surface in the middle of the column.
        double x = plot.Left + (0.4 * unit);
        int lines = Enumerable.Range((int)plot.Top, (int)plot.Height).Count(y => Near(surface, Pixel(frame, x, y), 4));
        Assert.Equal(1, lines);

        // No surface anywhere in a column of one category, nor in the steps between full ones (7 to 8 switches fully).
        foreach (double at in new[] { plot.Left + (5.3 * unit), plot.Left + (8.15 * unit) })
        {
            Assert.DoesNotContain(Enumerable.Range((int)plot.Top, (int)plot.Height), y => Near(surface, Pixel(frame, at, y), 4));
        }

        for (int row = (int)plot.Top + 2; row < (int)plot.Bottom - 2; row++)
        {
            for (int dx = -2; dx <= 1; dx++)
            {
                double edge = plot.Left + (8 * unit);
                Assert.False(Near(surface, Pixel(frame, Math.Floor(edge) + dx, row), 4), $"A stripe at the step to the last interval, row {row}.");
            }
        }

        window.Close();
        Reset();
    }

    // An interval without values is a gap in both forms; the average does not fill it from its neighbors.
    [AvaloniaTheory]
    [InlineData(GraphForm.OverTime)]
    [InlineData(GraphForm.Smoothed)]
    public void An_interval_without_values_is_a_gap_in_both_forms(GraphForm form)
    {
        CategorySeries series = Make(2, 12, s => s is 5 or 6 ? 0 : 20, (s, u) => u < 5 ? 1 : 0);
        (Window window, GraphView view) = Show(new CategoryGraph(series, form));
        Rect plot = view.PlotArea()!.Value;
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double unit = plot.Width / 12;
        double y = plot.Top + (plot.Height * 0.7);

        foreach (int second in new[] { 4, 7 })
        {
            Assert.False(Near(Colors.White, Pixel(frame, plot.Left + ((second + 0.5) * unit), y), 4), $"Second {second} has values.");
        }

        foreach (int second in new[] { 5, 6 })
        {
            foreach (double fraction in new[] { 0.2, 0.5, 0.8 })
            {
                Assert.True(Near(Colors.White, Pixel(frame, plot.Left + ((second + fraction) * unit), y), 4), $"Second {second} is filled.");
                Assert.True(Near(Colors.White, Pixel(frame, plot.Left + ((second + fraction) * unit), plot.Top + 4), 4));
            }
        }

        window.Close();
        Reset();
    }

    [AvaloniaTheory]
    [InlineData(GraphForm.OverTime)]
    [InlineData(GraphForm.Smoothed)]
    public void The_pointer_answers_with_the_second_it_is_over_and_a_last_second_of_one_frame_is_still_reachable(GraphForm form)
    {
        // 219 seconds and the 0.03 s that follow: a last interval of a fraction of a pixel in 830.
        CategorySeries series = Make(2, 220, s => s == 219 ? 1 : 40, (s, u) => s % 3 == 0 ? 0 : u < 10 ? 1 : 0);
        (Window window, GraphView view) = Show(new CategoryGraph(series, form) { Duration = 219.03 });
        Rect plot = view.PlotArea()!.Value;
        double y = plot.Top + (plot.Height / 2);

        // The step to second k is at k/219 of the plot less the 3 pixels the last interval is given, which a fraction of a pixel
        // of time could not be pointed at.
        for (int second = 0; second < 219; second += 17)
        {
            double x = plot.Left + ((plot.Width - 3) * second / 219);
            Assert.Equal(second, view.IndexAt(new Point(x + 1.5, y)));
        }

        Assert.Equal(219, view.IndexAt(new Point(plot.Right - 1, y)));
        Assert.Equal(219, view.IndexAt(new Point(plot.Right - 2.5, y)));
        Assert.Equal(218, view.IndexAt(new Point(plot.Right - 4, y)));
        window.MouseMove(view.TranslatePoint(new Point(plot.Right - 1, y), window)!.Value);
        UiPump.Until(() => true);
        string? help = AutomationProperties.GetHelpText(view);
        Assert.Contains("3:39 to the end", help, StringComparison.Ordinal);
        Assert.Equal(form == GraphForm.OverTime, help!.Contains("Last 0.03 s, 1 frame", StringComparison.Ordinal));
        window.MouseMove(view.TranslatePoint(new Point(plot.Left + ((plot.Width - 3) * 7.5 / 219), y), window)!.Value);
        UiPump.Until(() => true);
        string? interior = AutomationProperties.GetHelpText(view);
        Assert.Contains("0:07 to 0:08", interior, StringComparison.Ordinal);
        Assert.Equal(form == GraphForm.Smoothed, interior!.Contains("average of 5 seconds", StringComparison.Ordinal));
        window.Close();
        Reset();
    }

    // The bottom layer is the largest category, whichever its number, and the legend lists it first, in the colors drawn: the
    // swatches of the legend are those of the layers.
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_area_puts_the_dominant_category_at_the_bottom_in_the_muted_color_and_the_legend_shows_the_colors_drawn(string variant)
    {
        CategorySeries series = Make(3, 10, s => 40, (s, u) => s == 0 ? (u < 10 ? 0 : u < 38 ? 1 : 2) : (u < 4 ? 0 : u < 38 ? 1 : 2));
        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime), variant);
        Rect plot = view.PlotArea()!.Value;
        Color surface = SurfaceOf(variant);
        CategoryStyle[] styles = CategoryColors.ForStack(series, variant == "dark", surface);
        IReadOnlyList<Color> hues = variant == "dark" ? CategoryColors.Dark : CategoryColors.Light;
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double x = plot.Left + 6;

        // The second category (34 values of 40) is at the bottom, muted; the first above it and the third on top, in full colors.
        Assert.Equal([1, 0, 2], series.Categories.Select((_, i) => i).OrderBy(i => Array.IndexOf(CategoryColumns.StackOrder(series), i)));
        Assert.True(Near(CategoryColors.Mix(hues[0], surface, 0.7), Pixel(frame, x, plot.Bottom - 3)));
        Assert.True(Near(hues[1], Pixel(frame, x, plot.Top + (plot.Height * 0.12))));
        Assert.True(Near(hues[2], Pixel(frame, x, plot.Top + 1)));
        Assert.Equal(CategoryColors.Mix(hues[0], surface, 0.7), styles[1].Color);

        // The legend, under the axis: the first swatch is the bottom layer's color, and each color of a layer is in the row.
        double legend = plot.Bottom + 24 + 9;
        Assert.True(Near(styles[1].Color, Pixel(frame, 17, legend)), $"The first swatch is {Pixel(frame, 17, legend)}.");
        foreach (CategoryStyle style in styles)
        {
            Assert.Contains(Enumerable.Range(12, 800), px => Near(style.Color, Pixel(frame, px, legend), 4));
        }

        window.Close();
        Reset();
    }

    // The last interval of a partial one of a few frames is dimmed in the area: the colors blended with the surface, so wide that
    // it can be seen even where it lasts a fraction of a pixel; a longer one, the average and a column that merges intervals are not.
    [AvaloniaFact]
    public void A_partial_last_interval_of_few_frames_is_dimmed_in_the_area_only()
    {
        // 9 seconds, then 3 frames of a tenth of a second.
        CategorySeries series = Series(10, s => 0, 3);
        Color surface = Colors.White;
        Color color = CategoryColors.ForStack(series, false, surface)[0].Color;
        Color dimmed = CategoryColors.Mix(color, surface, 0.4);

        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime) { Duration = 9.075 });
        Rect plot = view.PlotArea()!.Value;
        double y = plot.Top + (plot.Height * 0.6);
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        Assert.True(Near(color, Pixel(frame, plot.Left + (plot.Width * 8.5 / 9.075), y)));
        Assert.True(Near(dimmed, Pixel(frame, plot.Right - 3, y), 6), $"The last interval is {Pixel(frame, plot.Right - 3, y)}, not {dimmed}.");
        window.Close();

        foreach ((CategorySeries other, GraphForm form, double duration) in new[] { (series, GraphForm.Smoothed, 9.075), (Series(10, s => 0, 20), GraphForm.OverTime, 9.5), (Series(10, s => 0, 5), GraphForm.OverTime, 10) })
        {
            (window, view) = Show(new CategoryGraph(other, form) { Duration = duration });
            plot = view.PlotArea()!.Value;
            frame = window.CaptureRenderedFrame()!;
            Assert.True(Near(color, Pixel(frame, plot.Right - 3, plot.Top + (plot.Height * 0.6))), $"{form} of {duration} is dimmed.");
            window.Close();
        }

        // 3,000 intervals in some 830 pixels: the last column merges four of them, and is not dimmed, nor named in the hover text.
        CategorySeries merged = Make(2, 3000, s => s == 2999 ? 3 : 4, (s, u) => 0);
        (window, view) = Show(new CategoryGraph(merged, GraphForm.OverTime) { Duration = 2999.5 });
        plot = view.PlotArea()!.Value;
        frame = window.CaptureRenderedFrame()!;
        Assert.True(Near(CategoryColors.ForStack(merged, false, surface)[0].Color, Pixel(frame, plot.Right - 1, plot.Top + (plot.Height * 0.6))));
        window.MouseMove(view.TranslatePoint(new Point(plot.Right - 1, plot.Top + 20), window)!.Value);
        UiPump.Until(() => true);
        Assert.DoesNotContain("Last", AutomationProperties.GetHelpText(view), StringComparison.Ordinal);
        window.Close();
        Reset();
    }

    [AvaloniaFact]
    public void The_smoothed_form_draws_the_averages_not_the_seconds_own_shares()
    {
        // A single second of the second category in the middle of 40: raw, only second 20 shows it; smoothed, the five seconds around it.
        CategorySeries series = Series(40, s => s == 20 ? 40 : 0);
        CategoryStyle[] styles = CategoryColors.ForStack(series, false, Colors.White);

        (Window window, GraphView view) = Show(new CategoryGraph(series, GraphForm.OverTime));
        Rect plot = view.PlotArea()!.Value;
        double unit = plot.Width / 40;
        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        double top = plot.Top + 3;
        Assert.True(Near(styles[1].Color, Pixel(frame, plot.Left + (20.5 * unit), top)));
        Assert.False(Near(styles[1].Color, Pixel(frame, plot.Left + (19.5 * unit), top)));
        window.Close();

        (window, view) = Show(new CategoryGraph(series, GraphForm.Smoothed));
        frame = window.CaptureRenderedFrame()!;
        foreach (int second in new[] { 18, 19, 21, 22 })
        {
            Assert.True(Near(styles[1].Color, Pixel(frame, plot.Left + ((second + 0.5) * unit), top)), $"Second {second} shows no share of the spike.");
        }

        Assert.False(Near(styles[1].Color, Pixel(frame, plot.Left + (17.5 * unit), top)));
        Assert.False(Near(styles[1].Color, Pixel(frame, plot.Left + (23.5 * unit), top)));
        window.Close();
        Reset();
    }
}
