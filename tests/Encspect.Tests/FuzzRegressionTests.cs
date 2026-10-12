using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Inputs the fuzzer once failed on (tests/Encspect.Fuzz), kept so the fixes stay: each must yield a
// report that the fuzzer would accept.
public class FuzzRegressionTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "fuzz"), "*.bin").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void FormerFailuresYieldCleanReports(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name));

        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

        Assert.Equal(data.Length, report.Root.Length);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        Assert.All(report.Streams, s => Assert.All(s.NodePaths, p => Assert.NotNull(p)));
        TestFiles.CheckInsideParents(report.Root);
    }
}
