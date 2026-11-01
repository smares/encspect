using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Encspect.Gui.Tests;

public class FontTests
{
    // The tests set their default text in the embedded Inter, not in whatever the operating system's font manager names as
    // its default. The source is asserted as well as the name: a machine that has Inter installed answers "Inter" either way.
    [AvaloniaFact]
    public void The_default_family_of_the_tests_is_the_embedded_Inter()
    {
        Assert.Equal("fonts:Inter", FontManager.Current.DefaultFontFamily.Key?.ToString());
        Assert.Equal("Inter", Typeface.Default.GlyphTypeface.FamilyName);
        Assert.Equal("Inter", new Typeface(new FontFamily("$Default"), FontStyle.Normal, FontWeight.Bold).GlyphTypeface.FamilyName);
    }
}
