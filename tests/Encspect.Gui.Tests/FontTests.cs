using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Encspect.Gui.Tests;

public class FontTests
{
    // The tests set their text in the embedded Inter, not in whatever the operating system's font manager names as its default.
    [AvaloniaFact]
    public void The_default_typeface_of_the_tests_is_the_embedded_Inter()
    {
        Assert.Equal("Inter", Typeface.Default.GlyphTypeface.FamilyName);
        Assert.Equal("Inter", new Typeface(new FontFamily("$Default"), FontStyle.Normal, FontWeight.Bold).GlyphTypeface.FamilyName);
    }
}
