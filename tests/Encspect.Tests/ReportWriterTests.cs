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
    public void JsonOutputWritesEachReportBeforeTheNextIsRequested()
    {
        using var output = new MemoryStream();
        var lengthsWhenRequested = new List<long>();

        IEnumerable<Report> Reports()
        {
            for (int i = 0; i < 3; i++)
            {
                lengthsWhenRequested.Add(output.Length);
                yield return CreateReport();
            }
        }

        ReportJsonWriter.Write(Reports(), output);

        Assert.Equal(0, lengthsWhenRequested[0]);
        Assert.True(lengthsWhenRequested[1] > lengthsWhenRequested[0]);
        Assert.True(lengthsWhenRequested[2] > lengthsWhenRequested[1]);
        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(3, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void JsonOutputIsFlushedWhileALargeReportIsWritten()
    {
        var root = new Node("File", 0, 100_000);
        for (int i = 0; i < 10_000; i++)
        {
            root.Add("Frame", i * 10, 10, "MPEG-1 Layer III, 128 kbps, 44100 Hz, Joint stereo");
        }

        using var output = new WriteCountingStream();

        ReportJsonWriter.Write([new Report("large.bin", root, [])], output);

        Assert.True(output.Writes > 2, $"{output.Writes} writes");
        Assert.True(output.LargestWrite < 1 << 20, $"largest write {output.LargestWrite} bytes");
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

    private sealed class WriteCountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public int LargestWrite { get; private set; }

        // MemoryStream.Write(ReadOnlySpan) on a derived type goes through Write(byte[], int, int),
        // so both are counted here and neither calls the other.
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            LargestWrite = Math.Max(LargestWrite, count);
            base.Write(buffer, offset, count);
        }
    }
}
