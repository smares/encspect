using Encspect.Gui.Cache;
using Encspect.Host;
using HarfBuzzSharp;
using SkiaSharp;

namespace Encspect.Gui;

// encspect-gui --self-check [file]: what CI runs on a published build, which has no display to
// open a window on. It loads the native drawing (Skia) and text shaping (HarfBuzz) libraries the
// way the window would, analyzes the file through the host and reads the file's ID as the summary
// cache does, through the operating system's functions, then prints a line for each and exits.
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
        Analyzer analyzer = Analyzer.CreateDefault();
        foreach (string file in files)
        {
            ReportSummary summary = ReportSummary.From(analyzer.AnalyzeFile(file));
            Console.Out.WriteLine($"{file}: {summary.Format}, {summary.Codec}, {summary.Encoder} ({summary.EncoderConfidence})");
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
}
