using Encspect.Core;

namespace Encspect.Tests;

// Long reports written on several threads while a thread forces collections (see ThreadSafetyTests). In
// the collection that runs alone: a thread that forces collections stalls every other test of the
// process, and CorruptTagFileTests allows each variant 10 seconds.
[Collection("Thread hammers")]
public class LongReportWriteTests
{
    // One report written on several threads at once, with collections going on, writes what a single thread
    // writes: on four threads by the three writers in the nightly run, on three threads by the JSON writer
    // with the explanations (the one that builds the most) on a pull request.
    [Theory]
    [MemberData(nameof(ThreadTestScope.LongInputNamesToWrite), MemberType = typeof(ThreadTestScope))]
    public void OneLongReportWrittenOnSeveralThreadsAtOnce(string name)
    {
        int writers = ThreadTestScope.Full ? 3 : 1;
        int threads = ThreadTestScope.Full ? 4 : 3;
        Report report = ThreadSafetyTests.AnalyzeLong(name);
        string[] expected = [.. Enumerable.Range(0, writers).Select(w => ReportRendering.Write(report, w))];

        using var collector = new ReportRendering.Collector(maximumPauseMilliseconds: 10);
        string[] written = [.. Enumerable.Range(0, threads).AsParallel().AsOrdered().WithDegreeOfParallelism(threads).Select(i => ReportRendering.Write(report, i % writers))];

        for (int i = 0; i < written.Length; i++)
        {
            Assert.True(written[i] == expected[i % writers], $"{name}: writer {i % writers} on thread {i} wrote something else. {ReportRendering.Differences(report, ThreadSafetyTests.AnalyzeLong(name))}");
        }
    }
}
