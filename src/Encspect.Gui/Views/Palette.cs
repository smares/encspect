using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Encspect.Gui.Views;

// Brushes and fonts for the controls that draw themselves, taken from the theme so they follow
// light and dark mode and the system's accent color.
internal static class Palette
{
    public static IBrush Foreground(Control control) => control.GetValue(TextElement.ForegroundProperty) ?? Brushes.Black;

    public static IBrush Secondary(Control control) => WithOpacity(Foreground(control), 0.55);

    // Hairlines such as a chart's grid: the text color, faint.
    public static IBrush Grid(Control control, double opacity = 0.14) => WithOpacity(Foreground(control), opacity);

    // The window's background, for gaps and rings that separate marks.
    public static IBrush Surface(Control control) =>
        control.TryFindResource("SystemRegionBrush", control.ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.White;

    public static Color SurfaceColor(Control control) => Surface(control) is ISolidColorBrush solid ? solid.Color : Colors.White;

    public static IBrush TooltipBackground(Control control) =>
        control.TryFindResource("ToolTipBackground", control.ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Surface(control);

    public static Color Accent(Control control) =>
        control.TryFindResource("SystemAccentColor", control.ActualThemeVariant, out object? value) && value is Color color ? color : Color.FromRgb(0, 120, 212);

    public static IBrush AccentFill(Control control, double opacity) => new ImmutableSolidColorBrush(Accent(control), opacity);

    public static Typeface Monospace(Control control) =>
        new(control.TryFindResource("MonospaceFontFamily", control.ActualThemeVariant, out object? value) && value is FontFamily family ? family : FontFamily.Default);

    private static IBrush WithOpacity(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * opacity) : brush;
}
