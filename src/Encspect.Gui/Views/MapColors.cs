using Avalonia.Media;

namespace Encspect.Gui.Views;

// The shades of the Frame Map's numeric squares: the single-hue ramp of the ordered category series for up to 8 classes, and a
// multi-hue sequential ramp for 9 to 16. A higher value has more contrast against the surface: the ramps run light to dark in
// light mode and are reversed in dark mode (the map only; the ordered category series keep their direction). Figures and the
// validator's output are in docs/gui.md ("Frame Map").
public static class MapColors
{
    // From a light olive to a dark indigo through green, teal and blue, 16 steps with an even fall in lightness (OKLab L from
    // 0.77 to 0.39), so that 14 to 16 neighboring steps differ by an OKLab delta E of 3.2 or more. Fewer classes take evenly
    // spaced steps of it.
    public static IReadOnlyList<Color> MultiHue { get; } =
    [
        Color.Parse("#c8b44a"), Color.Parse("#aeb355"), Color.Parse("#95b35f"), Color.Parse("#7bb26a"),
        Color.Parse("#67ac76"), Color.Parse("#52a683"), Color.Parse("#3ea08f"), Color.Parse("#379699"),
        Color.Parse("#318da4"), Color.Parse("#2a83ae"), Color.Parse("#3076af"), Color.Parse("#356ab1"),
        Color.Parse("#3b5db2"), Color.Parse("#3551a8"), Color.Parse("#30469f"), Color.Parse("#2a3a95"),
    ];

    // The color of each class, lowest value first.
    public static Color[] Classes(int classes, bool isMultiHue, bool isDark)
    {
        var colors = new Color[classes];
        Color hue = (isDark ? CategoryColors.Dark : CategoryColors.Light)[0];
        for (int i = 0; i < classes; i++)
        {
            if (isMultiHue)
            {
                int step = classes <= 1 ? 0 : ((i * (MultiHue.Count - 1) * 2) + (classes - 1)) / (2 * (classes - 1));
                colors[i] = MultiHue[isDark ? MultiHue.Count - 1 - step : step];
            }
            else
            {
                colors[i] = CategoryColors.Ramp(hue, isDark ? classes - 1 - i : i, classes);
            }
        }

        return colors;
    }
}
