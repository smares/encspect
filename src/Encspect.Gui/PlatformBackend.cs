using Avalonia;

namespace Encspect.Gui;

internal enum BackendChoice
{
    Default,
    X11,
    Wayland,
}

// Linux has two windowing backends: X11 (also under Wayland, through XWayland) and the native
// Wayland one, which Avalonia calls experimental. X11 is the default and ENCSPECT_GUI_BACKEND=wayland
// opts in to Wayland; making Wayland the default is the one constant below.
internal static class PlatformBackend
{
    public const string Variable = "ENCSPECT_GUI_BACKEND";

    // When true, an unset or unrecognized choice means Wayland where a compositor is reachable and X11
    // otherwise. The self-check, docs and CI assertions name the default; update them with this.
    private const bool WaylandIsDefault = false;

    // The backend the descriptor of a screen's handle names; asserted against the real backends in
    // CI, which fails if Avalonia renames them.
    private const string WaylandOutput = "WaylandOutput";
    private const string X11Monitor = "XRandRMonitorName";

    private static string? s_descriptor;
    private static bool s_mayBeWayland;

    public static BackendChoice Choose(string? value, out string? warning)
    {
        warning = null;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                return BackendChoice.Default;
            case "x11":
                return BackendChoice.X11;
            case "wayland":
                return BackendChoice.Wayland;
            default:
                warning = $"{Variable}={value} is not recognized; use x11 or wayland. Choosing the windowing backend automatically.";
                return BackendChoice.Default;
        }
    }

    public static AppBuilder Configure(AppBuilder builder, string? value)
    {
        builder = builder.UsePlatformDetect();
        if (!OperatingSystem.IsLinux())
        {
            return builder;
        }

        BackendChoice choice = Choose(value, out string? warning);
        if (warning is not null)
        {
            Console.Error.WriteLine($"encspect-gui: {warning}");
        }

        s_mayBeWayland = choice == BackendChoice.Wayland || (choice == BackendChoice.Default && WaylandIsDefault);
        return choice switch
        {
            BackendChoice.X11 => builder,
            BackendChoice.Wayland => builder.UseWayland(),
            _ => WaylandIsDefault ? builder.UseWaylandWithFallback() : builder,
        };
    }

    // The choice the environment makes, for messages and the self-check.
    public static BackendChoice Requested()
    {
        BackendChoice choice = Choose(Environment.GetEnvironmentVariable(Variable), out _);
        return choice == BackendChoice.Default ? (WaylandIsDefault ? BackendChoice.Wayland : BackendChoice.X11) : choice;
    }

    // libwayland connects to $WAYLAND_DISPLAY and, when that is unset, to wayland-0 in
    // $XDG_RUNTIME_DIR, so an unset variable does not mean there is no compositor.
    public static bool IsWaylandDisplayReachable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return true;
        }

        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, "wayland-0"));
    }

    // Wayland lets a client choose a window's size but not its position, which the compositor
    // decides: a client cannot set a position. The
    // backend is told once, by the first screen that appears, and kept. Before any output is known
    // a session that may be on Wayland is not placeable, so that nothing is saved over the
    // position another backend stored.
    public static bool CanPlaceWindows(Avalonia.Controls.Window window)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        return Descriptor(window) switch
        {
            WaylandOutput => false,
            null => !s_mayBeWayland,
            _ => true,
        };
    }

    private static string? Descriptor(Avalonia.Controls.Window window)
    {
        if (Volatile.Read(ref s_descriptor) is { } known)
        {
            return known;
        }

        string? descriptor = window.Screens?.All.FirstOrDefault()?.TryGetPlatformHandle()?.HandleDescriptor;
        if (descriptor is not null)
        {
            Volatile.Write(ref s_descriptor, descriptor);
        }

        return descriptor;
    }

    // The windowing backend that is running, told by what its first screen's handle describes,
    // which is public API and needs no window on screen; Wayland announces its outputs
    // asynchronously, so the first screen may take a moment. "unknown" when none appears.
    public static string Name()
    {
        var window = new Avalonia.Controls.Window();
        try
        {
            string? descriptor = null;
            for (int attempt = 0; attempt < 50 && descriptor is null; attempt++)
            {
                descriptor = Descriptor(window);
                if (descriptor is null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(50);
                }
            }

            return descriptor switch
            {
                WaylandOutput => "Wayland",
                X11Monitor => "X11",
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
