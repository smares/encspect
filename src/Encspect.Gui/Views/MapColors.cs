using Avalonia.Media;

namespace Encspect.Gui.Views;

// The shades of the Frame Map's numeric squares: a single-hue ramp of 8 steps for up to 8 classes, and a multi-hue ramp of 16
// steps for 9 to 16. A higher value has more contrast against the surface, so each mode has steps of its own: light mode runs
// from pale to dark on white, dark mode from dim to light on black (not the same list reversed, since one list cannot clear both
// surfaces with an even step). Figures and the validator's output are in docs/gui.md ("Frame Map").
public static class MapColors
{
    public const int SingleHueSteps = 8;
    public const int MultiHueSteps = 16;

    // The hue of the first category color (blue), OKLab L from 0.76 down to 0.30 on white: every step 0.06 or more from the next,
    // the lightest 2.14:1 against white.
    public static IReadOnlyList<Color> SingleHueLight { get; } = Parse("#80b3fe", "#5f9df8", "#4b88e2", "#3774cc", "#2360b6", "#0b4ca0", "#013a83", "#012a63");

    // The same hue from L 0.42 up to 0.90 on black, the dimmest 2.41:1 against black.
    public static IReadOnlyList<Color> SingleHueDark { get; } = Parse("#06489c", "#205db3", "#3572ca", "#4a87e1", "#5f9df8", "#80b3ff", "#a6caff", "#cbe0fe");

    // Lime through green, teal and blue to a dark violet, OKLab L from 0.77 down to 0.25 on white, found by a search that
    // maximizes the smallest adjacent difference under normal, protan, deutan and tritan vision (4.2 or more in OKLab delta E).
    public static IReadOnlyList<Color> MultiHueLight { get; } = Parse(
        "#b8bd35", "#a3b452", "#8dab64", "#78a171", "#62977c", "#069292", "#298294", "#39709e",
        "#3e609c", "#425782", "#414d6a", "#31426f", "#37306f", "#372956", "#361753", "#33103a");

    // From a violet through blue, teal and green to yellow, OKLab L from 0.39 up to 0.90 on black, found the same way.
    public static IReadOnlyList<Color> MultiHueDark { get; } = Parse(
        "#672170", "#5f358a", "#613ea4", "#5f52a6", "#5a65a5", "#5277a2", "#67829b", "#608db7",
        "#7498ae", "#67abac", "#7fb5a0", "#94c096", "#aaca86", "#c1d373", "#d7dc5b", "#eee533");

    // The color of each class, lowest value first.
    public static Color[] Classes(int classes, bool isMultiHue, bool isDark)
    {
        IReadOnlyList<Color> steps = isMultiHue
            ? (isDark ? MultiHueDark : MultiHueLight)
            : (isDark ? SingleHueDark : SingleHueLight);
        var colors = new Color[classes];
        for (int i = 0; i < classes; i++)
        {
            // A single class takes a middle step; otherwise the steps nearest to evenly spaced ones, the ends included.
            int step = classes <= 1 ? steps.Count / 2 : ((i * (steps.Count - 1) * 2) + (classes - 1)) / (2 * (classes - 1));
            colors[i] = steps[step];
        }

        return colors;
    }

    private static Color[] Parse(params string[] hex) => [.. hex.Select(Color.Parse)];
}
