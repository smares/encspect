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
    public void TextOutputLocatesFindingsWithOffsets()
    {
        var writer = new StringWriter();

        ReportTextWriter.Write(CreateReport(), writer, showOffsets: true);

        Assert.Contains("  [Warning] test.code: Something odd [0x0, 4 bytes]" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void TextOutputEscapesTheSource()
    {
        var writer = new StringWriter();

        ReportTextWriter.Write(new Report("new\nline\t.mp3", new Node("File", 0, 0), []), writer, showOffsets: false);

        Assert.StartsWith("new\\nline\\t.mp3" + Environment.NewLine + "  File", writer.ToString());
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
    public void NdjsonOutputHasOneCompactReportPerLine()
    {
        var root = new Node("File", 0, 10);
        root.Add("Lyrics", 0, 10, "one\r\ntwo");
        Report[] reports = [CreateReport(), new Report("second.bin", root, [])];
        using var output = new MemoryStream();

        ReportJsonWriter.WriteLines(reports, output);

        string text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.EndsWith("\n", text);
        string[] lines = text[..^1].Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.DoesNotContain('\r', line));

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("test.bin", first.RootElement.GetProperty("source").GetString());
        Assert.Equal("test.code", first.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());

        using JsonDocument second = JsonDocument.Parse(lines[1]);
        JsonElement lyrics = second.RootElement.GetProperty("root").GetProperty("children")[0];
        Assert.Equal("one\r\ntwo", lyrics.GetProperty("value").GetString());
    }

    [Fact]
    public void NdjsonLinesMatchTheJsonArrayEntries()
    {
        Report[] reports = [CreateReport(), CreateReport()];
        using var array = new MemoryStream();
        using var lines = new MemoryStream();

        ReportJsonWriter.Write(reports, array);
        ReportJsonWriter.WriteLines(reports, lines);

        using JsonDocument document = JsonDocument.Parse(array.ToArray());
        string[] expected = [.. document.RootElement.EnumerateArray().Select(r => JsonSerializer.Serialize(r))];
        string[] actual = System.Text.Encoding.UTF8.GetString(lines.ToArray()).TrimEnd('\n').Split('\n');
        Assert.Equal(expected, actual);
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

    private static Report BitFieldReport()
    {
        var root = new Node("File", 0, 4);
        Node header = root.Add("Header", 0, 4);
        header.Add(Node.FromBits("Layer", 0, 13, 15, "III"));
        header.Add(Node.FromBits("IsOriginal", 0, 29, 30, "true"));
        return new Report("bits.mp3", root, []);
    }

    [Fact]
    public void JsonOutputHasBitPositionsWhereANodeHasThem()
    {
        using var output = new MemoryStream();

        ReportJsonWriter.Write([BitFieldReport()], output);

        using JsonDocument document = JsonDocument.Parse(output.ToArray());
        JsonElement header = document.RootElement[0].GetProperty("root").GetProperty("children")[0];
        Assert.False(header.TryGetProperty("bitOffset", out _));
        Assert.False(header.TryGetProperty("bitLength", out _));
        JsonElement layer = header.GetProperty("children")[0];
        Assert.Equal((1L, 1L, 5, 2), (layer.GetProperty("offset").GetInt64(), layer.GetProperty("length").GetInt64(),
            layer.GetProperty("bitOffset").GetInt32(), layer.GetProperty("bitLength").GetInt32()));
        Assert.Equal(["name", "value", "offset", "length", "bitOffset", "bitLength"], layer.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TextOutputShowsBitPositionsWithOffsets()
    {
        var writer = new StringWriter();

        ReportTextWriter.Write(BitFieldReport(), writer, showOffsets: true);

        string text = writer.ToString();
        Assert.Contains("    Header [0x0, 4 bytes]" + Environment.NewLine, text);
        Assert.Contains("      Layer: III [0x1+5, 2 bits]" + Environment.NewLine, text);
        Assert.Contains("      IsOriginal: true [0x3+5, 1 bit]" + Environment.NewLine, text);
    }

    // A damaged container can nest boxes thousands deep. Writers stop at Node.MaxDepth instead of
    // failing (JSON) or overflowing the stack (text), which ends the process.
    [Theory]
    [InlineData(Node.MaxDepth)]
    [InlineData(Node.MaxDepth + 1)]
    [InlineData(999)]
    [InlineData(200_000)]
    public void DeepTreesAreCutAtTheDepthLimit(int depth)
    {
        var root = new Node("File", 0, depth * 8L);
        Node current = root;
        for (int i = 0; i < depth; i++)
        {
            current = current.Add("Box", i * 8L, (depth - i) * 8L);
        }

        var report = new Report("deep.mp4", root, []);
        using var json = new MemoryStream();
        using var lines = new MemoryStream();
        var text = new StringWriter();

        ReportJsonWriter.Write([report], json);
        ReportJsonWriter.WriteLines([report], lines);
        ReportTextWriter.Write(report, text, showOffsets: true);

        using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 1000 });
        JsonElement node = document.RootElement[0].GetProperty("root");
        int levels = 0;
        while (node.TryGetProperty("children", out JsonElement children))
        {
            node = children[0];
            levels++;
        }

        Assert.Equal(Math.Min(depth, Node.MaxDepth), levels);
        Assert.Equal(depth > Node.MaxDepth, node.TryGetProperty("hasOmittedChildren", out JsonElement omitted) && omitted.GetBoolean());

        string[] textLines = text.ToString().Split(Environment.NewLine);
        Assert.Equal(Math.Min(depth, Node.MaxDepth) + 1, textLines.Count(l => l.TrimStart().StartsWith("Box", StringComparison.Ordinal) || l.TrimStart().StartsWith("File", StringComparison.Ordinal)));
        Assert.Equal(depth > Node.MaxDepth, text.ToString().Contains($"(children nested deeper than {Node.MaxDepth} levels are not shown)", StringComparison.Ordinal));
    }

    [Fact]
    public void ChildrenThatFailToBuildAreWrittenAsAnError()
    {
        var root = new Node("File", 0, 10);
        root.Add(new Node("Packets", 0, 10, null, () => throw new IndexOutOfRangeException("bug in a deferred builder")));
        var report = new Report("broken.bin", root, []);
        using var json = new MemoryStream();
        var text = new StringWriter();

        ReportJsonWriter.Write([report], json);
        ReportTextWriter.Write(report, text, showOffsets: false);

        Assert.Contains("\"name\": \"Error\"", System.Text.Encoding.UTF8.GetString(json.ToArray()));
        Assert.Contains("Error: The children could not be built: IndexOutOfRangeException: bug in a deferred builder", text.ToString());
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
