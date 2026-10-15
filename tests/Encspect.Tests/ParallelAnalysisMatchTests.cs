using System.Collections.Concurrent;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Not in the "Thread hammers" collection: it forces no collections and needs no idle machine, so it
// runs beside the other tests.
public class ParallelAnalysisMatchTests
{
    // Analyses of the files of a directory on several threads write the same reports as one thread does,
    // with their descriptions and topics.
    [Theory]
    [MemberData(nameof(ThreadSafetyTests.FixtureDirectories), MemberType = typeof(ThreadSafetyTests))]
    public void ParallelAnalysisMatchesSequential(string directory)
    {
        List<string> files = ThreadSafetyTests.FixturesIn(directory);
        var analyzer = Analyzer.CreateDefault();
        Dictionary<string, string> sequential = files.ToDictionary(f => f, f => ReportRendering.All(analyzer.AnalyzeFile(f)));

        var parallel = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            f => parallel[f] = ReportRendering.All(analyzer.AnalyzeFile(f)));

        Assert.Equal(files.Count, parallel.Count);
        string? differing = files.FirstOrDefault(f => parallel[f] != sequential[f]);
        Assert.True(differing is null, differing is null ? string.Empty
            : $"{differing} differs between the parallel and the sequential analysis. {ReportRendering.Differences(analyzer.AnalyzeFile(differing), analyzer.AnalyzeFile(differing))}");
    }
}
