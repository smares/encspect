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
