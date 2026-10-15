using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Encspect.Gui.Cache;
using Encspect.Host;
using HarfBuzzSharp;
using SkiaSharp;

namespace Encspect.Gui;

// encspect-gui --self-check [file]: what CI runs on a published build, which has no display to
// open a window on. It loads the native drawing (Skia) and text shaping (HarfBuzz) libraries the
// way the window would, and the platform backend's own native library where it has one, analyzes
// the file through the host and reads the file's ID as the summary cache does, through the
// operating system's functions, then prints a line for each and exits.
internal static class SelfCheck
{
    public const string Argument = "--self-check";

    public static int Run(IReadOnlyList<string> files)
    {
        using (SKSurface surface = SKSurface.Create(new SKImageInfo(16, 16)))
        {
            surface.Canvas.Clear(SKColors.White);
        }

        int glyphs;
        using (var buffer = new HarfBuzzSharp.Buffer())
        {
            buffer.AddUtf8("Encspect");
            buffer.GuessSegmentProperties();
            glyphs = buffer.Length;
        }

        Console.Out.WriteLine($"Skia and HarfBuzz loaded ({glyphs} characters shaped).");
        if (!LoadPlatformBackend())
        {
            return 1;
        }

        Analyzer analyzer = Analyzer.CreateDefault();
        foreach (string file in files)
        {
            ReportSummary summary = ReportSummary.From(analyzer.AnalyzeFile(file));
            Console.Out.WriteLine($"{file}: {summary.Format}, {summary.Codec}, {summary.Encoder} ({summary.EncoderConfidence}), written by {summary.Writer} ({summary.WriterConfidence})");
            if (summary.Errors > 0)
            {
                return 1;
            }

            // Windows, Linux and macOS give every file an ID; elsewhere the cache does without.
            FileId? id = FileFingerprint.Read(file)?.Id;
            Console.Out.WriteLine(id is { } found ? $"File ID {found.Volume:X}:{found.File:X}." : "No file ID.");
            if (id is null && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                return 1;
            }
        }

        return 0;
    }

    // The backends load their libraries by DllImport from their own assemblies, which a
    // single-file build resolves to the folder it extracts its native libraries to; loading
    // through the same assembly takes the same path without starting the backend or opening a
    // window. Windows draws through ANGLE (av_libGLESv2) and falls back to software drawing
    // without it, so its absence is reported but is no failure; macOS has no window at all without
    // Avalonia.Native. Linux's backends (Wayland and X11) are managed code over the system's own
    // libraries. Where the display the chosen backend needs is reachable, the backend is started
    // without showing a window and named, so CI can assert which one was chosen.
    private static bool LoadPlatformBackend()
    {
        (string? library, Assembly? assembly, bool isRequired) = OperatingSystem.IsWindows() ? ("av_libGLESv2.dll", typeof(Win32PlatformOptions).Assembly, false)
            : OperatingSystem.IsMacOS() ? ("libAvaloniaNative", typeof(AvaloniaNativePlatformOptions).Assembly, true)
            : (null, null, false);
        if (library is null || assembly is null)
        {
            Console.Out.WriteLine("Platform backend: no native library beyond Skia and HarfBuzz.");
            return ReportWindowingBackend();
        }

        try
        {
            NativeLibrary.Load(library, assembly, null);
            Console.Out.WriteLine($"Platform backend: {library} loaded.");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            Console.Out.WriteLine($"Platform backend: {library} not loaded: {ex.Message}");
            return !isRequired;
        }
    }

    private static bool ReportWindowingBackend()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        // Only the chosen backend's display counts: a set WAYLAND_DISPLAY does not start Wayland
        // unless ENCSPECT_GUI_BACKEND asks for it, and a set DISPLAY does not start X11 when it does.
        if (PlatformBackend.Requested() == BackendChoice.Wayland)
        {
            if (!PlatformBackend.IsWaylandDisplayReachable())
            {
                Console.Out.WriteLine("Windowing backend: not started, Wayland is chosen but there is no WAYLAND_DISPLAY and no wayland-0 in XDG_RUNTIME_DIR.");
                return true;
            }
        }
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Out.WriteLine("Windowing backend: not started, X11 is chosen but there is no DISPLAY.");
            return true;
        }

        try
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            Console.Out.WriteLine($"Windowing backend: {PlatformBackend.Name()}.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"Windowing backend: failed to start: {ex.Message}");
            return false;
        }
    }
}
