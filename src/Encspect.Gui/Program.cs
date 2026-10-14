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
        bool isSetUp = false;
        try
        {
            return BuildAvaloniaApp().AfterSetup(_ => isSetUp = true).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (!isSetUp)
        {
            // The backend could not connect to its display; the runtime's own report of it is a
            // stack trace and an abort.
            Console.Error.WriteLine($"encspect-gui: the {PlatformBackend.Requested()} windowing backend could not start ({ex.Message}); check {PlatformBackend.Variable} (x11 or wayland), WAYLAND_DISPLAY and DISPLAY.");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => PlatformBackend.Configure(AppBuilder.Configure<App>(), Environment.GetEnvironmentVariable(PlatformBackend.Variable)).LogToTrace();
}
