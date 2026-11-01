using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Encspect.Gui;
using Encspect.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Encspect.Gui.Tests;

// The application on Avalonia's headless platform, drawing with Skia so tests can capture frames. The default font family
// is the Inter that Avalonia.Fonts.Inter embeds: without this, "$Default" is whatever Skia's font manager names on the
// machine (DirectWrite on Windows), and a lookup of it that failed once on windows-11-arm threw "Could not create
// glyphTypeface" (docs/development.md, "Fonts of the GUI tests"). Text that names another family keeps it: on macOS App
// sets the content font to .AppleSystemUIFont, and the monospaced lists use the system's monospaced fonts everywhere.
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
