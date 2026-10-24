using System.Runtime.CompilerServices;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A report outlives the stream it was read from (a caller may analyze many files and keep the reports),
// so nothing its nodes are built from may hold the input: a node factory that captures a parse's state,
// its reader or its decoder keeps the whole stream alive. Each file is analyzed from a MemoryStream held
// only through a weak reference, the stream is dropped, and a collection must free it while the report
// is alive.
public class InputReleaseTests
{
    public static TheoryData<string> FixtureDirectories() => ThreadSafetyTests.FixtureDirectories();

    public static TheoryData<string> LongInputNames() => ThreadSafetyTests.LongInputNames();

    [Theory]
    [MemberData(nameof(FixtureDirectories))]
    public void TheInputIsFreedWhileTheReportLives(string directory) => AssertFreed(directory, expand: false);

    // Written first, so that the nodes of the first reading have run their factories too.
    [Theory]
    [MemberData(nameof(FixtureDirectories))]
    public void TheInputIsFreedAfterTheReportIsWritten(string directory) => AssertFreed(directory, expand: true);

    private static void AssertFreed(string directory, bool expand)
    {
        var inputs = new List<(string File, WeakReference Input)>();
        var reports = new List<Report>();
        foreach (string file in ThreadSafetyTests.FixturesIn(directory))
        {
            (WeakReference input, Report report) = Analyze(Path.GetFileName(file), File.ReadAllBytes(file), expand);
            inputs.Add((Path.GetRelativePath(Fixtures.FixtureDirectory, file), input));
            reports.Add(report);
        }

        ReportRendering.Collect();

        string[] held = [.. inputs.Where(i => i.Input.IsAlive).Select(i => i.File)];
        Assert.True(held.Length == 0, $"The report keeps the input alive for: {string.Join(", ", held)}.");
        GC.KeepAlive(reports);
    }

    [Theory]
    [MemberData(nameof(LongInputNames))]
    public void TheInputOfALongFileIsFreedWhileTheReportLives(string name)
    {
        (WeakReference input, Report report) = Analyze(name, LongInputs.Build(name), expand: true);

        ReportRendering.Collect();

        Assert.False(input.IsAlive, $"The report of {name} keeps the input alive.");
        GC.KeepAlive(report);
    }

    // The token a decoder gets is linked to the analysis's and the front end's stop; a caller that reuses one token for many
    // analyses must not keep a linked source registered for each, so the host disposes it when the analysis ends, also when it throws.
    [Fact]
    public void TheDecodingTokenOfEveryAnalysisIsDisposedWhenItEnds()
    {
        using var long_lived = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var decoder = new DecodeRouteTests.ScriptedDecoder { OnOpened = c => tokens.Add(c.CancellationToken) };
        var analyzer = new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [decoder]);
        var options = new AnalysisOptions { CancellationToken = long_lived.Token, Decoding = new DecodeOptions { Stop = stop.Token } };

        for (int i = 0; i < 50; i++)
        {
            analyzer.Analyze("sample.bin", new MemoryStream(new byte[64]), options);
        }

        // An analysis that throws (the front end's sink failed) disposes it too.
        var failing = new AnalysisOptions { CancellationToken = long_lived.Token, Decoding = new DecodeOptions { Output = _ => throw new InvalidOperationException("sink") } };
        var withSink = new DecodeRouteTests.ScriptedDecoder { OnOpened = c => { tokens.Add(c.CancellationToken); c.DeclareOutput(new PcmFormat(44100, 2, 16, SampleFormat.Integer)); } };
        Assert.Throws<InvalidOperationException>(() => new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [withSink]).Analyze("sample.bin", new MemoryStream(new byte[64]), failing));

        Assert.Equal(51, tokens.Count);
        Assert.All(tokens, token => Assert.Throws<ObjectDisposedException>(() => token.WaitHandle));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Input, Report Report) Analyze(string name, byte[] data, bool expand)
    {
        var stream = new MemoryStream(data);
        Report report = Analyzer.CreateDefault().Analyze(name, stream);
        if (expand)
        {
            ReportRendering.Json(report);
        }

        return (new WeakReference(stream), report);
    }
}
