namespace Encspect.Gui.Tests;

public sealed class PlatformBackendTests
{
    [Theory]
    [InlineData(null, "Default", false)]
    [InlineData("", "Default", false)]
    [InlineData("x11", "X11", false)]
    [InlineData(" Wayland ", "Wayland", false)]
    [InlineData("wayland-ish", "Default", true)]
    public void The_variable_is_read_and_unknown_values_warn(string? value, string expected, bool warns)
    {
        Assert.Equal(expected, PlatformBackend.Choose(value, out string? warning).ToString());
        Assert.Equal(warns, warning is not null);
        if (warning is not null)
        {
            Assert.Contains("x11 or wayland", warning, StringComparison.Ordinal);
        }
    }
}
