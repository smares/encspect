using Avalonia.Media;
using Encspect.Core;

namespace Encspect.Gui.Views;

// How a category is drawn: a color, and whether it is hatched (a value the specification does not allow).
public readonly record struct CategoryStyle(Color Color, bool IsHatched, bool IsOther);

// The fixed categorical palette of the category charts, not the system accent, so a category has the same hue in every
// file and in both modes (the two modes use different steps of one hue). The six slots are blue, orange, aqua, yellow,
// magenta and green; the values are those of the dataviz skill's default palette, validated against the graph surfaces
// (#ffffff in light mode, #000000 in dark; docs/gui.md has the validator's output, and a test holds the surfaces).
// A series uses them for its first six categories in order, an ordered series a ramp of the first hue for its first
// eight, any category past that one flat gray, and a category marked reserved (`Category.IsReserved`) a hatched gray.
public static class CategoryColors
{
    public const int Hues = 6;
    public const int RampSteps = 8;

    public static IReadOnlyList<Color> Light { get; } =
        [Color.Parse("#2a78d6"), Color.Parse("#eb6834"), Color.Parse("#1baf7a"), Color.Parse("#eda100"), Color.Parse("#e87ba4"), Color.Parse("#008300")];

    public static IReadOnlyList<Color> Dark { get; } =
        [Color.Parse("#3987e5"), Color.Parse("#d95926"), Color.Parse("#199e70"), Color.Parse("#c98500"), Color.Parse("#d55181"), Color.Parse("#008300")];

    // Index 0, the plain coding, takes the first hue in a muted step.
    // The muted step is a mix of 0.7 of the hue with the surface, which passes the validator in both modes (docs/gui.md).
    private const double MutedShare = 0.7;

    public static CategoryStyle[] For(CategorySeries series, bool isDark, Color surface)
    {
        ArgumentNullException.ThrowIfNull(series);

        IReadOnlyList<Color> hues = isDark ? Dark : Light;
        Color other = isDark ? Color.Parse("#9a9a9a") : Color.Parse("#8a8a8a");
        Color reserved = isDark ? Color.Parse("#bdbdbd") : Color.Parse("#5f5f5f");
        var styles = new CategoryStyle[series.Categories.Count];
        int slot = 0;
        int rampSize = Math.Min(RampSteps, series.Categories.Count(c => !c.IsReserved));
        for (int i = 0; i < styles.Length; i++)
        {
            if (series.Categories[i].IsReserved)
            {
                styles[i] = new CategoryStyle(reserved, true, false);
                continue;
            }

            if (series.IsOrdered)
            {
                styles[i] = slot < RampSteps ? new CategoryStyle(Ramp(hues[0], slot, rampSize), false, false) : new CategoryStyle(other, false, true);
            }
            else if (slot >= Hues)
            {
                styles[i] = new CategoryStyle(other, false, true);
            }
            else
            {
                styles[i] = new CategoryStyle(slot == 0 ? Mix(hues[0], surface, MutedShare) : hues[slot], false, false);
            }

            slot++;
        }

        return styles;
    }

    // One hue from light to dark in the given number of steps.
    public static Color Ramp(Color hue, int step, int steps)
    {
        double t = steps <= 1 ? 0.5 : (double)step / (steps - 1);
        return t < 0.5
            ? Mix(hue, Colors.White, 0.4 + (0.6 * (t / 0.5)))
            : Mix(hue, Colors.Black, 1 - (0.45 * ((t - 0.5) / 0.5)));
    }

    // amount of the color, the rest of the background.
    public static Color Mix(Color color, Color background, double amount) => Color.FromRgb(
        (byte)Math.Round((color.R * amount) + (background.R * (1 - amount))),
        (byte)Math.Round((color.G * amount) + (background.G * (1 - amount))),
        (byte)Math.Round((color.B * amount) + (background.B * (1 - amount))));
}
