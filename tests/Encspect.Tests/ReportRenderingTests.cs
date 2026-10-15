using Encspect.Core;

namespace Encspect.Tests;

// The helper that explains a mismatch between two hashed renderings must itself be right.
public class ReportRenderingTests
{
    [Fact]
    public void EqualTextsHaveNoDifference()
    {
        Assert.Equal(string.Empty, ReportRendering.FirstDifference("a\nb\n", "a\nb\n"));
    }

    [Fact]
    public void TheFirstDifferingLineIsNamed()
    {
        string difference = ReportRendering.FirstDifference("one\ntwo\nthree\n", "one\ntwx\nthree\n");

        Assert.Equal("line 2, offset 6: expected \"two\", got \"twx\".", difference);
    }

    [Fact]
    public void ATextThatEndsEarlierDiffersAtItsEnd()
    {
        Assert.Equal("line 2, offset 4: expected \"two\", got \"\".", ReportRendering.FirstDifference("one\ntwo\n", "one\n"));
    }

    [Fact]
    public void ARenderingOfOneReportEqualsAnother()
    {
        var root = new Node("File", 0, 4);
        root.Add("Field", 0, 2, NodeValue.Integer(7), "A field.");
        var report = new Report("x.bin", root, []);

        Assert.Equal(string.Empty, ReportRendering.FirstDifference(ReportRendering.Render(report, 1), ReportRendering.Render(report, 1)));
        Assert.Contains("Field", ReportRendering.Render(report, 0));
        Assert.Contains("x.bin", ReportRendering.Render(report, 2));
        Assert.StartsWith("Written again now, the report equals", ReportRendering.Differences(report, report));
    }
}
