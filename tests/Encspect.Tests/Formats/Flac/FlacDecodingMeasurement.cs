using System.Diagnostics;
using Encspect.Core;
using Encspect.Formats.Flac;
using Encspect.Formats.Flac.Decoding;
using Encspect.Host;
using Xunit.Abstractions;

namespace Encspect.Tests;

// The measurement of docs/design/decoders.md ("The second entropy pass, measured again on the built decoder"). It runs only where
// ENCSPECT_FLAC_MEASURE names a folder of .flac files, and then prints for each: A, the analysis without decoding; B, with decoding as built
// (the walk stores into the buffers the session lends); C, with decoding and a walk that stores nothing (FlacParser.LendsBuffers = false), so
// the decoder reads every frame again. D = C - A is the decoder as the first draft of the contract had it, S = C - B the second read net of
// what the stores add. Each figure is the best of 5 runs after a warm-up. Run it alone, in Release, on a quiet machine:
//   ENCSPECT_FLAC_MEASURE=/path/to/flac dotnet test tests/Encspect.Tests -c Release --filter FlacDecodingMeasurement --logger "console;verbosity=detailed"
public class FlacDecodingMeasurement(ITestOutputHelper output)
{
    [Fact]
    public void MeasuresTheSecondEntropyPass()
    {
        string? folder = Environment.GetEnvironmentVariable("ENCSPECT_FLAC_MEASURE");
        if (folder is null)
        {
            return;
        }

        foreach (string path in Directory.GetFiles(folder, "*.flac").Order())
        {
            double a = Best(path, null, true), b = Best(path, new DecodeOptions(), true), c = Best(path, new DecodeOptions(), false);
            Report r = Run(path, new DecodeOptions(), true);
            StreamInfo s = r.Streams[0];
            double seconds = s.Duration;
            output.WriteLine($"RESULT {Path.GetFileName(path)} seconds={seconds:F0} A={a:F3} B={b:F3} C={c:F3} D(C-A)={c - a:F3} S(C-B)={c - b:F3} S/D={(c - b) / (c - a):P1} decode(B-A)={b - a:F3} xRT={seconds / (b - a):F0} saved={(c - b) / c:P1} verified={s.Verification!.IsVerified}");
        }
    }

    private static Report Run(string path, DecodeOptions? decoding, bool lends)
    {
        var analyzer = new Analyzer(DefaultParsers.Tags, [new FlacParser { LendsBuffers = lends }], DefaultParsers.Codecs, [new FlacDecoder()]);
        return analyzer.AnalyzeFile(path, new AnalysisOptions { Decoding = decoding });
    }

    private static double Best(string path, DecodeOptions? decoding, bool lends)
    {
        Run(path, decoding, lends);
        double best = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Run(path, decoding, lends);
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        return best;
    }
}
