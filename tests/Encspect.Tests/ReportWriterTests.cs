using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

public class ReportWriterTests
{
    private static Report CreateReport()
    {
        var root = new Node("File", 0, 10, "Test");
        Node header = root.Add("Header", 0, 4);
        var findings = new List<Finding>
        {
            new(Severity.Warning, "test.code", "Something odd", header),
        };

        return new Report("test.bin", root, findings);
    }

    [Fact]
    public void TextOutputShowsTreeFindingsAndOffsets()
    {
        var writer = new StringWriter();

        ReportTextWriter.Write(CreateReport(), writer, showOffsets: true);

        string text = writer.ToString();
        Assert.Contains("File: Test [0x0, 10 bytes]", text);
        Assert.Contains("    Header [0x0, 4 bytes]", text);
        Assert.Contains("[Warning] test.code: Something odd", text);
    }

    [Fact]
    public void TextOutputOmitsOffsetsUnlessRequested()
    {
        var writer = new StringWriter();

        ReportTextWriter.Write(CreateReport(), writer, showOffsets: false);

        Assert.DoesNotContain("0x", writer.ToString());
    }

    [Fact]
    public void JsonOutputIsAnArrayOfReports()
    {
        using var output = new MemoryStream();

        ReportJsonWriter.Write([CreateReport()], output);

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement report = document.RootElement[0];
        Assert.Equal("test.bin", report.GetProperty("source").GetString());

        JsonElement root = report.GetProperty("root");
        Assert.Equal("File", root.GetProperty("name").GetString());
        Assert.Equal(10L, root.GetProperty("length").GetInt64());

        JsonElement header = root.GetProperty("children")[0];
        Assert.Equal("Header", header.GetProperty("name").GetString());
        Assert.False(header.TryGetProperty("children", out _));

        JsonElement finding = report.GetProperty("findings")[0];
        Assert.Equal("Warning", finding.GetProperty("severity").GetString());
        Assert.Equal("test.code", finding.GetProperty("code").GetString());
        Assert.Equal(4L, finding.GetProperty("length").GetInt64());
    }

    [Fact]
    public void TextOutputEscapesControlCharactersInValues()
    {
        var root = new Node("File", 0, 10);
        root.Add("Lyrics", 0, 10, "one\r\ntwo\u0001");
        var writer = new StringWriter();

        ReportTextWriter.Write(new Report("test.bin", root, []), writer, showOffsets: false);

        Assert.Contains("Lyrics: one\\r\\ntwo\\x01", writer.ToString());
    }
}
