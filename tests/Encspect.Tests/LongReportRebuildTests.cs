using Encspect.Core;

namespace Encspect.Tests;

// Not in the "Thread hammers" collection: it forces one collection from its own thread, no collector thread.
public class LongReportRebuildTests
{
    [Theory]
    [MemberData(nameof(ThreadTestScope.LongInputNames), MemberType = typeof(ThreadTestScope))]
    public void ALongReportBuiltAgainAfterCollectionMatches(string name)
    {
        Report report = ThreadSafetyTests.AnalyzeLong(name);
        string first = ReportRendering.Json(report);
        ReportRendering.Collect();

        Assert.True(ReportRendering.Json(report) == first, $"{name} differs after a collection. {ReportRendering.Differences(report, ThreadSafetyTests.AnalyzeLong(name))}");
    }
}
