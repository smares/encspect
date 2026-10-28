using Avalonia.Media;
using Encspect.Gui.Views;

namespace Encspect.Gui.Tests;

// The Frame Map's ramps against the `dataviz` skill's checks, computed here as its validator does (docs/gui.md, "Frame Map"): the
// ordinal checks of the single-hue ramps, and for the multi-hue ramps the lightness, the contrast against the surface, and the
// smallest difference between neighbors under normal, protan, deutan and tritan vision for the numbers of classes a map uses.
public class MapColorsTests
{
    private static readonly double[][] Machado =
    [
        [0.152286, 1.052583, -0.204868, 0.114503, 0.786281, 0.099216, -0.003882, -0.048116, 1.051998],
        [0.367322, 0.860646, -0.227968, 0.280085, 0.672501, 0.047413, -0.011820, 0.042940, 0.968881],
        [1.255528, -0.076749, -0.178779, -0.078411, 0.930809, 0.147602, 0.004733, 0.691367, 0.303900],
    ];

    private static double[] Linear(Color c) => [Lin(c.R), Lin(c.G), Lin(c.B)];

    private static double Lin(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double[] OkLab(double[] rgb)
    {
        double l = Math.Cbrt((0.4122214708 * rgb[0]) + (0.5363325363 * rgb[1]) + (0.0514459929 * rgb[2]));
        double m = Math.Cbrt((0.2119034982 * rgb[0]) + (0.6806995451 * rgb[1]) + (0.1073969566 * rgb[2]));
        double s = Math.Cbrt((0.0883024619 * rgb[0]) + (0.2817188376 * rgb[1]) + (0.6299787005 * rgb[2]));
        return
        [
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s),
        ];
    }

    private static double Lightness(Color c) => OkLab(Linear(c))[0];

    private static double Hue(Color c)
    {
        double[] lab = OkLab(Linear(c));
        return ((Math.Atan2(lab[2], lab[1]) * 180 / Math.PI % 360) + 360) % 360;
    }

    // The OKLab difference times 100 of two colors; vision is -1 for normal vision, else 0 protan, 1 deutan, 2 tritan (Machado
    // and others, 2009, severity 1.0).
    private static double Delta(Color a, Color b, int vision)
    {
        double[] Simulated(Color c)
        {
            double[] rgb = Linear(c);
            if (vision < 0)
            {
                return OkLab(rgb);
            }

            double[] m = Machado[vision];
            return OkLab([.. Enumerable.Range(0, 3).Select(r => Math.Clamp((m[r * 3] * rgb[0]) + (m[(r * 3) + 1] * rgb[1]) + (m[(r * 3) + 2] * rgb[2]), 0, 1))]);
        }

        double[] x = Simulated(a);
        double[] y = Simulated(b);
        return 100 * Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (x[i] - y[i]) * (x[i] - y[i])));
    }

    private static double Worst(IReadOnlyList<Color> ramp) =>
        Enumerable.Range(0, ramp.Count - 1).SelectMany(i => new[] { -1, 0, 1, 2 }.Select(v => Delta(ramp[i], ramp[i + 1], v))).Min();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_single_hue_ramp_passes_the_ordinal_checks_in_its_mode(bool isDark)
    {
        Color surface = isDark ? Colors.Black : Colors.White;
        Color[] ramp = MapColors.Classes(8, false, isDark);

        double[] lightness = [.. ramp.Select(Lightness)];
        Assert.Equal(isDark ? lightness.Order() : lightness.OrderDescending(), lightness);
        for (int i = 1; i < 8; i++)
        {
            Assert.True(Math.Abs(lightness[i] - lightness[i - 1]) >= 0.06, $"Steps {i - 1} and {i} are {Math.Abs(lightness[i] - lightness[i - 1]):F3} apart.");
        }

        Assert.True(CategoryColors.ContrastRatio(ramp[0], surface) >= 2.0, $"{CategoryColors.ContrastRatio(ramp[0], surface):F2}:1 for the lowest value.");
        double[] hues = [.. ramp.Select(Hue)];
        Assert.True(hues.Max() - hues.Min() <= 40, $"A hue spread of {hues.Max() - hues.Min():F0} degrees.");

        // Fewer classes take steps of the same ramp, so their lowest value clears the surface just as well.
        for (int classes = 1; classes <= 8; classes++)
        {
            Color[] some = MapColors.Classes(classes, false, isDark);
            double[] contrast = [.. some.Select(c => CategoryColors.ContrastRatio(c, surface))];
            Assert.Equal(contrast.Order(), contrast);
            Assert.True(contrast[0] >= 2.0, $"{classes} classes: {contrast[0]:F2}:1.");
            Assert.All(some, c => Assert.Contains(c, ramp));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_multi_hue_ramp_has_steps_of_its_own_per_mode_with_more_contrast_for_higher_values_and_neighbors_apart_in_every_vision(bool isDark)
    {
        Color surface = isDark ? Colors.Black : Colors.White;
        IReadOnlyList<Color> ramp = isDark ? MapColors.MultiHueDark : MapColors.MultiHueLight;
        Assert.Equal(MapColors.MultiHueSteps, ramp.Count);

        double[] lightness = [.. ramp.Select(Lightness)];
        Assert.Equal(isDark ? lightness.Order() : lightness.OrderDescending(), lightness);
        Assert.InRange(lightness[0], isDark ? 0.38 : 0.76, isDark ? 0.40 : 0.78);
        Assert.InRange(lightness[^1], isDark ? 0.89 : 0.24, isDark ? 0.91 : 0.26);

        double[] contrast = [.. ramp.Select(c => CategoryColors.ContrastRatio(c, surface))];
        Assert.Equal(contrast.Order(), contrast);
        Assert.True(contrast[0] >= 2.0, $"{contrast[0]:F2}:1 for the lowest value.");

        foreach (int classes in new[] { 9, 12, 14, 16 })
        {
            Color[] some = MapColors.Classes(classes, true, isDark);
            Assert.True(Worst(some) >= 4.1, $"{classes} classes: the closest neighbors are {Worst(some):F2} apart.");
            double[] steps = [.. some.Select(c => CategoryColors.ContrastRatio(c, surface))];
            Assert.Equal(steps.Order(), steps);
        }

        // Dark mode is not the light list reversed.
        Assert.NotEqual(MapColors.MultiHueLight.Reverse(), MapColors.MultiHueDark);
    }
}
