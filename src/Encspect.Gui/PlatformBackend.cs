using Avalonia;

namespace Encspect.Gui;

// Linux has two windowing backends: the native Wayland one, which Avalonia calls experimental,
// and X11 (also under Wayland, through XWayland). Wayland is used when a compositor is
// reachable and X11 otherwise; ENCSPECT_GUI_BACKEND chooses instead.
internal static class PlatformBackend
{
    public const string Variable = "ENCSPECT_GUI_BACKEND";

    public static AppBuilder Configure(AppBuilder builder, string? choice)
    {
        builder = builder.UsePlatformDetect();
        if (!OperatingSystem.IsLinux())
        {
            return builder;
        }

        return choice?.Trim().ToLowerInvariant() switch
        {
            "x11" => builder,
            "wayland" => builder.UseWayland(),
            _ => builder.UseWaylandWithFallback(),
        };
    }

    // Wayland lets a client choose a window's size but not its position, which the compositor
    // decides: Position stays 0, 0 and setting it does nothing (checked against Weston 13). The
    // outputs are known once the main window exists.
    public static bool IsWayland(Avalonia.Controls.Window window) => OperatingSystem.IsLinux() && ScreenDescriptor(window) == WaylandOutput;

    private const string WaylandOutput = "WaylandOutput";

    private static string? ScreenDescriptor(Avalonia.Controls.Window window) =>
        window.Screens?.All.FirstOrDefault()?.TryGetPlatformHandle()?.HandleDescriptor;

    // The windowing backend that is running, told by what its first screen's handle describes,
    // which is public API and needs no window on screen; Wayland announces its outputs
    // asynchronously, so the first screen may take a moment.
    public static string Name()
    {
        var window = new Avalonia.Controls.Window();
        try
        {
            string? descriptor = null;
            for (int attempt = 0; attempt < 50 && descriptor is null; attempt++)
            {
                descriptor = ScreenDescriptor(window);
                if (descriptor is null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(50);
                }
            }

            return descriptor switch
            {
                WaylandOutput => "Wayland",
                "XRandRMonitorName" => "X11",
                { } other => other,
                null => "unknown",
            };
        }
        finally
        {
            window.Close();
        }
    }
}
