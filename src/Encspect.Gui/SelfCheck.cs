using Encspect.Host;
using HarfBuzzSharp;
using SkiaSharp;

namespace Encspect.Gui;

// encspect-gui --self-check [file]: what CI runs on a published build, which has no display to
// open a window on. It loads the native drawing (Skia) and text shaping (HarfBuzz) libraries the
// way the window would, and analyzes the file through the host, then prints one line and exits.
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
        }

        return 0;
    }
}
