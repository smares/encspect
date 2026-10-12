using Avalonia;

namespace Encspect.Gui;

internal static class Program
{
    // Files and folders named on the command line are opened as if dropped onto the window.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [SelfCheck.Argument, .. var files])
        {
            return SelfCheck.Run(files);
        }

        Views.UnhandledErrors.InstallForProcess();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
