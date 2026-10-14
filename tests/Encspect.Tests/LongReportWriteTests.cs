using Encspect.Core;

namespace Encspect.Tests;

// Long reports written on several threads, and written again after a collection (see ThreadSafetyTests).
// Its own class, so that xUnit runs it beside the others.
public class LongReportWriteTests
{
    // One report written on eight threads at once by the three writers, with collections going on, writes
    // what a single thread writes.
    [Theory]
    [MemberData(nameof(ThreadSafetyTests.LongInputNames), MemberType = typeof(ThreadSafetyTests))]
    public void OneLongReportWrittenOnSeveralThreadsAtOnce(string name)
    {
        Report report = ThreadSafetyTests.AnalyzeLong(name);
        string[] expected = [.. Enumerable.Range(0, 3).Select(w => ReportRendering.Write(report, w))];

        using var collector = new ReportRendering.Collector(maximumPauseMilliseconds: 10);
        string[] written = [.. Enumerable.Range(0, 4).AsParallel().AsOrdered().WithDegreeOfParallelism(4).Select(i => ReportRendering.Write(report, i))];

        for (int i = 0; i < written.Length; i++)
        {
            Assert.True(written[i] == expected[i % 3], $"{name}: writer {i % 3} on thread {i} wrote something else.");
        }
    }

    [Theory]
    [MemberData(nameof(ThreadSafetyTests.LongInputNames), MemberType = typeof(ThreadSafetyTests))]
    public void ALongReportBuiltAgainAfterCollectionMatches(string name)
    {
        Report report = ThreadSafetyTests.AnalyzeLong(name);
        string first = ReportRendering.Json(report);
        ReportRendering.Collect();

        Assert.True(ReportRendering.Json(report) == first, $"{name} differs after a collection.");
    }
}
