using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// What the report writers show of the format detection: the alternatives and the forced format.
public class FormatDetectionReportTests
{
    [Fact]
    public void TheWritersShowTheAlternativesAndTheForcedFormat()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-lame-cbr128-joint.mp3");
        Report forced = Analyzer.CreateDefault().AnalyzeFile(path, new AnalysisOptions { ForcedFormat = "aac" });
        Report plain = Analyzer.CreateDefault().AnalyzeFile(path);

        string text = Text(forced);
        Assert.Contains("Format detection:", text);
        Assert.Contains("Analyzed as AAC as requested; detection would take MPEG Audio.", text);
        Assert.Contains("Also matches: MPEG Audio (class 90", text);
        Assert.DoesNotContain("Format detection:", Text(plain));

        using var stream = new MemoryStream();
        ReportJsonWriter.Write([forced, plain], stream);
        using var json = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        System.Text.Json.JsonElement detection = json.RootElement[0].GetProperty("detection");
        Assert.Equal("AAC", detection.GetProperty("format").GetString());
        Assert.True(detection.GetProperty("isForced").GetBoolean());
        Assert.Equal("MPEG Audio", detection.GetProperty("detected").GetString());
        System.Text.Json.JsonElement alternative = detection.GetProperty("alternatives")[0];
        Assert.Equal(("MPEG Audio", 90), (alternative.GetProperty("format").GetString(), alternative.GetProperty("evidence").GetProperty("class").GetInt32()));
        Assert.False(json.RootElement[1].TryGetProperty("detection", out _));
    }

    private static string Text(Report report)
    {
        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder))
        {
            ReportTextWriter.Write(report, writer, showOffsets: false);
        }

        return builder.ToString();
    }
}
