using Avalonia;
using Avalonia.Headless;
using Encspect.Gui;
using Encspect.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Encspect.Gui.Tests;

// The application on Avalonia's headless platform, drawing with Skia so tests can capture frames.
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
