using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

public class NodeValueTests
{
    [Theory]
    [InlineData(128L, "kbps", "128 kbps")]
    [InlineData(0L, null, "0")]
    [InlineData(-5L, "dB", "-5 dB")]
    [InlineData(1L, "bytes", "1 bytes")]
    public void IntegersShowTheirUnit(long value, string? unit, string expected)
    {
        Assert.Equal(expected, NodeValue.Integer(value, unit).ToString());
    }

    [Fact]
    public void CountsReadInTheSingularForOne()
    {
        Assert.Equal("1 frame", NodeValue.Integer(1, "frames", "frame").ToString());
        Assert.Equal("2 frames", NodeValue.Integer(2, "frames", "frame").ToString());
        Assert.Equal("frames", NodeValue.Integer(1, "frames", "frame").Unit);
    }

    [Theory]
    [InlineData(0.261, 3, "s", false, "0.261 s")]
    [InlineData(128, 1, "kbps", false, "128.0 kbps")]
    [InlineData(0.98765432, 6, null, false, "0.987654")]
    [InlineData(1.5, 1, "dB", true, "+1.5 dB")]
    [InlineData(-6.2, 1, "dB", true, "-6.2 dB")]
    [InlineData(0.0, 1, "dB", true, "+0.0 dB")]
    [InlineData(-0.0, 1, "dB", false, "0.0 dB")]
    [InlineData(44.1, 1, "kHz", false, "44.1 kHz")]
    public void DecimalsShowTheirPrecision(double value, int decimals, string? unit, bool showsSign, string expected)
    {
        Assert.Equal(expected, NodeValue.Decimal(value, decimals, unit, showsSign).ToString());
    }

    [Fact]
    public void HexValuesArePaddedToTheirDigits()
    {
        Assert.Equal("0x00A1", NodeValue.Hex(0xA1, 4).ToString());
        Assert.Equal("0x0000001F", NodeValue.Hex(0x1F, 8).ToString());
        Assert.Equal("0x1F", NodeValue.Hex(0x1F, 0).ToString());
        Assert.Equal(0x1F, NodeValue.Hex(0x1F, 4).AsInteger());
    }

    [Fact]
    public void HexValuesCanUseAll64Bits()
    {
        NodeValue mask = NodeValue.Hex(ulong.MaxValue, 16);
        Assert.Equal("0xFFFFFFFFFFFFFFFF", mask.ToString());
        Assert.Equal(ulong.MaxValue, mask.AsUnsigned());
        Assert.Null(mask.AsInteger());
        Span<char> text = stackalloc char[32];
        Assert.True(mask.TryFormat(text, out int written));
        Assert.Equal("0xFFFFFFFFFFFFFFFF", text[..written].ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeValue.Hex(-1L, 16));
    }

    [Fact]
    public void BooleansAndTextShowAsBefore()
    {
        Assert.Equal("true", NodeValue.Boolean(true).ToString());
        Assert.Equal("false", NodeValue.Boolean(false).ToString());
        Assert.Equal(true, NodeValue.Boolean(true).AsBoolean());
        Assert.Equal("Joint stereo", NodeValue.Text("Joint stereo").ToString());
    }

    [Fact]
    public void StringsConvertToTextImplicitly()
    {
        NodeValue? value = "Joint stereo";
        NodeValue? none = (string?)null;

        Assert.Equal(NodeValueKind.Text, value!.Kind);
        Assert.Equal("Joint stereo", value.AsText());
        Assert.Null(none);
        Assert.Null(new Node("Field", 0, 1).Value);
    }

    [Fact]
    public void ValuesCompareByKindAndContent()
    {
        Assert.Equal(NodeValue.Integer(128, "kbps"), NodeValue.Integer(128, "kbps"));
        Assert.NotEqual(NodeValue.Integer(128, "kbps"), NodeValue.Integer(128));
        Assert.True(NodeValue.Text("128 kbps") != NodeValue.Integer(128, "kbps"));
        Assert.True(new Node("Mode", 0, 1, "Mono").Value == "Mono");
    }

    [Fact]
    public void AccessorsAnswerOnlyForTheirKind()
    {
        NodeValue decimalValue = NodeValue.Decimal(0.5, 1);

        Assert.Null(decimalValue.AsInteger());
        Assert.Null(decimalValue.AsText());
        Assert.Equal(0.5, decimalValue.AsDouble());
        Assert.Equal(7, NodeValue.Integer(7).AsDouble());
        Assert.Null(NodeValue.Text("7").AsDouble());
    }

    [Fact]
    public void InvalidValuesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeValue.Decimal(double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeValue.Hex(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeValue.Decimal(1, -1));
    }
}

public class TypedJsonTests
{
    private static JsonElement WriteOne(Report report, ReportFilter? filter = null)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream, filter ?? ReportFilter.All);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement[0].Clone();
    }

    private static Report CreateReport()
    {
        var root = new Node("File", 0, 100, "Test");
        Node stream = root.Add(new Node("Stream", 0, 100) { Kind = NodeKind.Stream });
        Node frame = stream.Add(new Node("Frame", 0, 50, "summary") { Kind = NodeKind.Frame });
        frame.Add("Bitrate", 1, 1, NodeValue.Integer(128, "kbps"));
        frame.Add("IsVbr", 1, 1, NodeValue.Boolean(false));
        frame.Add("CRC", 4, 2, NodeValue.Hex(0x1A2B, 4));
        frame.Add("Duration", 0, 50, NodeValue.Decimal(0.261, 3, "s"));
        frame.Add(Node.FromBits("Emphasis", 0, 30, 32, "None"));
        var info = new StreamInfo
        {
            Nodes = [stream],
            NodePaths = [[0]],
            Codec = "Test Codec",
            SampleRate = 44100,
            Channels = 2,
            ChannelMode = "Stereo",
            Samples = 1152,
            Duration = 0.026,
            AverageBitrate = 128,
            IsVbr = false,
            Encoder = "Encoder",
            EncoderConfidence = Confidence.Medium,
            Series = [new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 50 }, new double[] { 128, 160 })],
        };
        var findings = new List<Finding>
        {
            new(Severity.Warning, "test.crc", "2 frames fail", new Node("CRC", 4, 2)) { Count = 2, Path = [0, 0, 2] },
            new(Severity.Info, "test.none", "No node"),
        };
        return new Report("test.bin", root, findings, [info]);
    }

    [Fact]
    public void ReportsCarrySchemaAndToolVersions()
    {
        JsonElement report = WriteOne(CreateReport());

        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("encspect", report.GetProperty("tool").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(report.GetProperty("tool").GetProperty("version").GetString()));
        Assert.Equal(
            ["schemaVersion", "tool", "source", "root", "streams", "findings"],
            report.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ValuesAreTypedWithUnits()
    {
        JsonElement frame = WriteOne(CreateReport()).GetProperty("root").GetProperty("children")[0].GetProperty("children")[0];
        JsonElement[] fields = [.. frame.GetProperty("children").EnumerateArray()];

        Assert.Equal("Frame", frame.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Number, fields[0].GetProperty("value").ValueKind);
        Assert.Equal(128, fields[0].GetProperty("value").GetInt32());
        Assert.Equal("kbps", fields[0].GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.False, fields[1].GetProperty("value").ValueKind);
        Assert.False(fields[1].TryGetProperty("unit", out _));
        Assert.Equal(0x1A2B, fields[2].GetProperty("value").GetInt32());
        Assert.Equal(0.261, fields[3].GetProperty("value").GetDouble());
        Assert.Equal("s", fields[3].GetProperty("unit").GetString());
        Assert.Equal("None", fields[4].GetProperty("value").GetString());
        Assert.Equal(6, fields[4].GetProperty("bitOffset").GetInt32());
        Assert.False(fields[0].TryGetProperty("kind", out _));
    }

    [Fact]
    public void StreamsAndSeriesAreWritten()
    {
        JsonElement stream = WriteOne(CreateReport()).GetProperty("streams")[0];

        Assert.Equal("[[0]]", stream.GetProperty("nodes").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).ReplaceLineEndings(string.Empty));
        Assert.Equal("Test Codec", stream.GetProperty("codec").GetString());
        Assert.Equal(44100, stream.GetProperty("sampleRate").GetInt32());
        Assert.Equal(2, stream.GetProperty("channels").GetInt32());
        Assert.False(stream.TryGetProperty("bitsPerSample", out _));
        Assert.Equal(1152, stream.GetProperty("samples").GetInt64());
        Assert.False(stream.GetProperty("isVbr").GetBoolean());
        Assert.Equal("Medium", stream.GetProperty("encoderConfidence").GetString());
        JsonElement series = stream.GetProperty("series")[0];
        Assert.Equal("frameOffset", series.GetProperty("axis").GetString());
        Assert.Equal("[0,50]", series.GetProperty("x").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).ReplaceLineEndings(string.Empty));
        Assert.Equal("[128,160]", series.GetProperty("values").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).ReplaceLineEndings(string.Empty));
    }

    [Fact]
    public void FindingsCarryPathAndCount()
    {
        JsonElement[] findings = [.. WriteOne(CreateReport()).GetProperty("findings").EnumerateArray()];

        Assert.Equal("[0,0,2]", findings[0].GetProperty("path").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).ReplaceLineEndings(string.Empty));
        Assert.Equal(2, findings[0].GetProperty("count").GetInt32());
        Assert.False(findings[1].TryGetProperty("path", out _));
        Assert.Equal(1, findings[1].GetProperty("count").GetInt32());
    }

    [Fact]
    public void KindsHideSectionsInJson()
    {
        JsonElement root = WriteOne(CreateReport(), new ReportFilter(ReportSections.Frames)).GetProperty("root");

        Assert.False(root.GetProperty("children")[0].TryGetProperty("children", out _));
    }

    [Fact]
    public void FixtureValuesAreTypedInJson()
    {
        string json = Fixtures.ToJson(Fixtures.AnalyzeMpeg("mpeg/external-lame-tagged.mp3"));

        Assert.Contains("\"name\": \"Sample Rate\",\n", json);
        Assert.Contains("\"value\": 44100,\n", json);
        Assert.Contains("\"unit\": \"Hz\",\n", json);
        Assert.Contains("\"name\": \"IsVbr\",\n", json);
        Assert.Contains("\"value\": false,\n", json);
        Assert.Contains("\"kind\": \"Tag\",\n", json);
        Assert.DoesNotContain("\"value\": \"true\"", json);
        Assert.DoesNotContain("\"value\": \"false\"", json);
        Assert.DoesNotContain("Bitrate Over Time\",\n            \"value\"", json);
    }
}

public class StreamTests
{
    // An embedded ID3v2 tag splits one stream of 10 frames into two regions of 5 (B7).
    [Fact]
    public void StreamContinuesAcrossASplitRegion()
    {
        Report report = Fixtures.AnalyzeMpeg("stats/split-by-tag.mp3");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(2, report.Root.Children.Count(c => c.Kind == NodeKind.Stream));
        Assert.Equal(2, stream.Nodes.Count);
        Assert.All(stream.Nodes, n => Assert.Equal(NodeKind.Stream, n.Kind));
        Assert.Equal(stream.Nodes.Select(n => (IReadOnlyList<int>?)[report.Root.Children.ToList().IndexOf(n)]), stream.NodePaths);
        Assert.Equal("MPEG-1 Layer III", stream.Codec);
        Assert.Equal(0.261, Math.Round(stream.Duration, 3));
        Assert.Equal(11520, stream.Samples);
        Assert.Equal(139.0, Math.Round(stream.AverageBitrate!.Value, 1));
        Assert.True(stream.IsVbr);
        Assert.Equal(10, stream.Series.Single(s => s.Name == "Bitrate").Values.Length);
    }

    [Fact]
    public void StreamEndsWhereTheSampleRateChanges()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/sample-rate-change.mp3");

        Assert.Equal(2, report.Streams.Count);
        Assert.NotEqual(report.Streams[0].SampleRate, report.Streams[1].SampleRate);
        Assert.Same(report.Streams[0].Nodes.Single(), report.Streams[1].Nodes.Single());
    }

    [Fact]
    public void PerSecondSeriesAreShownAfterTheirStatistics()
    {
        Report report = Fixtures.AnalyzeMpeg("stats/vbr-seconds.mp3");
        Series bitrate = report.Streams.Single().Series.Single(s => s.Name == "Bitrate Over Time");
        var writer = new StringWriter();

        ReportTextWriter.Write(report, writer, showOffsets: false);

        Assert.Equal(SeriesAxis.Seconds, bitrate.Axis);
        Assert.Equal("Bitrate Distribution", bitrate.Anchor!.Name);
        Assert.Equal(Enumerable.Range(0, bitrate.Values.Length).Select(i => (double)i), bitrate.X.ToArray());
        Assert.Contains("Bitrate Over Time: " + string.Join(',', bitrate.Values.ToArray()), writer.ToString());
        Assert.DoesNotContain(report.Root.Children.SelectMany(c => c.Children).Where(c => c.Kind == NodeKind.Statistics).SelectMany(c => c.Children),
            c => c.Name.EndsWith("Over Time", StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryReadsStreamsNotRegions()
    {
        ReportSummary split = ReportSummary.From(Fixtures.AnalyzeMpeg("stats/split-by-tag.mp3"));
        Report changed = Fixtures.AnalyzeMpeg("stats/sample-rate-change-statistics.mp3");
        ReportSummary summary = ReportSummary.From(changed);

        Assert.Equal(1, split.Streams);
        Assert.Equal(0.261, Math.Round(split.Duration!.Value, 3));
        Assert.Equal(11520, split.Samples);
        Assert.Equal(2, summary.Streams);
        Assert.Equal(changed.Streams.Sum(s => s.Duration), summary.Duration);
        Assert.Equal(changed.Streams.Sum(s => s.Samples), summary.Samples);
        double weighted = changed.Streams.Sum(s => s.AverageBitrate!.Value * s.Duration) / changed.Streams.Sum(s => s.Duration);
        Assert.Equal(weighted, summary.AverageBitrate!.Value, 9);
        Assert.Equal(changed.Streams[0].SampleRate, summary.SampleRate);
    }

    [Fact]
    public void CsvReadsStreams()
    {
        var writer = new StringWriter();

        ReportCsvWriter.Write([Fixtures.AnalyzeMpeg("stats/split-by-tag.mp3")], writer);

        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("split-by-tag.mp3,MPEG Audio,1,MPEG-1 Layer III,0.261,11520,139.0,44100,Joint stereo,\"stereo (front left, front right)\",,,,,\"ID3v2, ID3v2\",0,0,0,false,", lines[1]);
    }

    // The tags of a summary are the nodes of kind Tag, whatever they are named.
    [Fact]
    public void SummaryFindsTagsByKind()
    {
        var root = new Node("File", 0, 10);
        root.Add(new Node("Custom Tag", 0, 5) { Kind = NodeKind.Tag });
        root.Add(new Node("ID3v2", 5, 5));

        Assert.Equal(["Custom Tag"], ReportSummary.From(new Report("x", root, [])).Tags);
    }
}

public class FindingPathTests
{
    public static TheoryData<string> Mpeg => Fixtures.MpegFixtures();

    public static TheoryData<string> Tags => Fixtures.TagFixtures();

    // A finding's node is a node of the tree, equal in name, offset, length and bit position.
    [Theory]
    [MemberData(nameof(Mpeg))]
    public void MpegFindingsPointAtTreeNodes(string fileName) => CheckPaths(Fixtures.AnalyzeMpeg(fileName));

    [Theory]
    [MemberData(nameof(Tags))]
    public void TagFindingsPointAtTreeNodes(string name) => CheckPaths(Fixtures.Analyze(name));

    private static void CheckPaths(Report report)
    {
        foreach (Finding finding in report.Findings)
        {
            Assert.NotNull(finding.Node);
            Assert.True(finding.Path is not null, $"{finding.Code} at 0x{finding.Node.Offset:X}: {finding.Node.Name} is not in the tree");
            Node node = report.Root;
            foreach (int index in finding.Path)
            {
                node = node.Children[index];
            }

            Assert.Equal(
                (finding.Node.Name, finding.Node.Offset, finding.Node.Length, finding.Node.BitOffset, finding.Node.BitLength),
                (node.Name, node.Offset, node.Length, node.BitOffset, node.BitLength));
            Assert.True(finding.Count >= 1);
        }

        foreach (StreamInfo stream in report.Streams)
        {
            Assert.Equal(stream.Nodes.Count, stream.NodePaths.Count);
            Assert.All(stream.NodePaths, Assert.NotNull);
        }
    }

    [Fact]
    public void FindingsPointAtTheFieldThatCausedThem()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/mpeg1-layer3-reserved-emphasis.mp3");

        Finding emphasis = report.Findings.Single(f => f.Code == "mpeg.emphasis-reserved");
        Assert.Equal("Emphasis", emphasis.Node!.Name);
        Assert.Equal(["MPEG Audio", "Frame", "Header", "Emphasis"], Names(report.Root, emphasis.Path!));
    }

    [Fact]
    public void AggregatedFindingsCarryTheirCount()
    {
        Report report = Fixtures.AnalyzeMpeg("layer3/side-info-invalid.mp3");

        Finding huffman = report.Findings.Single(f => f.Code == "mpeg.huffman-table-undefined");
        Assert.Equal(2, huffman.Count);
        Assert.Contains("2 frames", huffman.Message, StringComparison.Ordinal);
        Assert.Equal("Table Select", Names(report.Root, huffman.Path!)[^1]);
    }

    [Fact]
    public void ANodeNotInTheTreeHasNoPath()
    {
        var parser = new FixedParser(context => context.AddFinding(Severity.Info, "test.lost", "lost", new Node("Nowhere", 2, 1)));

        Report report = new Analyzer([parser]).Analyze("x.bin", new MemoryStream(new byte[16]));

        Assert.Null(report.Findings.Single(f => f.Code == "test.lost").Path);
    }

    // Resolving a finding builds only the children on its path, not every frame's.
    [Fact]
    public void ResolvingAPathBuildsOnlyTheFramesOnIt()
    {
        const int Frames = 10_000;
        int built = 0;
        var parser = new FixedParser(context =>
        {
            Node root = context.Root;
            root.Add(new Node("Stream", 0, Frames * 4, null, () => Enumerable.Range(0, Frames).Select(i => new Node("Frame", i * 4, 4, null, () =>
            {
                Interlocked.Increment(ref built);
                return [new Node("Header", i * 4, 2), new Node("Field", (i * 4) + 2, 1)];
            })
            { Kind = NodeKind.Frame }).ToList())
            { Kind = NodeKind.Stream });
            context.AddFinding(Severity.Warning, "test.field", "1 frame has it", new Node("Field", (5000 * 4) + 2, 1), 7);
        });

        Report report = new Analyzer([parser]).Analyze("frames.bin", new MemoryStream(new byte[Frames * 4]));

        Finding finding = report.Findings.Single(f => f.Code == "test.field");
        Assert.Equal([0, 5000, 1], finding.Path);
        Assert.Equal(7, finding.Count);
        Assert.InRange(built, 1, 2);
    }

    // Children out of file order, or whose ends go back, are searched through the sorted index;
    // children in order through bisection, also where several contain the node.
    [Fact]
    public void ResolvingAPathAmongUnorderedAndNestedChildren()
    {
        var parser = new FixedParser(context =>
        {
            Node unordered = context.Root.Add(new Node("Unordered", 0, 160));
            for (int i = 0; i < 40; i++)
            {
                long offset = (39 - i) * 4;
                unordered.Add(new Node("Item", offset, 4)).Add("Field", offset + 2, 1);
            }

            unordered.Add(new Node("Span", 0, 160)).Add("Inner", 50, 1);

            Node ordered = context.Root.Add(new Node("Ordered", 200, 100));
            for (int i = 0; i < 40; i++)
            {
                ordered.Add(new Node("Whole", 200, 100));
            }

            ordered.Children[^1].Add("Deep", 250, 1);
            context.AddFinding(Severity.Info, "test.unordered", "found", new Node("Field", (20 * 4) + 2, 1));
            context.AddFinding(Severity.Info, "test.span", "found", new Node("Inner", 50, 1));
            context.AddFinding(Severity.Info, "test.ordered", "found", new Node("Deep", 250, 1));
        });

        Report report = new Analyzer([parser]).Analyze("nodes.bin", new MemoryStream(new byte[300]));

        Assert.Equal([0, 19, 0], report.Findings.Single(f => f.Code == "test.unordered").Path);
        Assert.Equal([0, 40, 0], report.Findings.Single(f => f.Code == "test.span").Path);
        Assert.Equal([1, 39, 0], report.Findings.Single(f => f.Code == "test.ordered").Path);
    }

    // A long list in file order, such as a stream's packets that are built when read, is read once
    // in order and then bisected, not sorted through its indexer, which rebuilt the nodes of blocks
    // the collector had taken over and over.
    [Fact]
    public void ResolvingAPathReadsAnOrderedListOnce()
    {
        const int Frames = 100_000;
        var frames = new CountingList(Enumerable.Range(0, Frames).Select(i => new Node("Frame", i * 4L, 4)).ToArray());
        frames.Items[70_000].Add("Field", (70_000 * 4) + 1, 1);
        var parser = new FixedParser(context =>
        {
            context.Root.Add(new Node("Stream", 0, Frames * 4, null, () => frames) { Kind = NodeKind.Stream });
            context.AddFinding(Severity.Warning, "test.field", "found", new Node("Field", (70_000 * 4) + 1, 1));
        });

        Report report = new Analyzer([parser]).Analyze("frames.bin", new MemoryStream(new byte[Frames * 4]));

        Assert.Equal([0, 70_000, 0], report.Findings.Single(f => f.Code == "test.field").Path);
        Assert.InRange(frames.Reads, Frames, Frames + 200);
    }

    private sealed class CountingList(Node[] items) : IReadOnlyList<Node>
    {
        private int _reads;

        public Node[] Items => items;

        public int Reads => _reads;

        public int Count => items.Length;

        public Node this[int index]
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return items[index];
            }
        }

        public IEnumerator<Node> GetEnumerator()
        {
            foreach (Node item in items)
            {
                Interlocked.Increment(ref _reads);
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static List<string> Names(Node root, IReadOnlyList<int> path)
    {
        var names = new List<string>();
        Node node = root;
        foreach (int index in path)
        {
            node = node.Children[index];
            names.Add(node.Name);
        }

        return names;
    }

    private sealed class FixedParser(Action<ParseContext> parse) : IFormatParser
    {
        public string Name => "Fixed";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context) => parse(context);
    }
}

// --hide matches node kinds, never names (B8).
public class KindFilterTests
{
    [Fact]
    public void ApeItemsNamedLikeSectionsStay()
    {
        byte[] audio = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg", "mpeg1-layer3-mono.mp3"));
        byte[] data = [.. audio, .. ApeTag(("Frame", "one"), ("Statistics", "two"), ("Title", "three"))];
        Report report = Analyzer.CreateDefault().Analyze("ape-names.mp3", new MemoryStream(data));
        Assert.Contains(report.Root.Children, c => c is { Name: "APE", Kind: NodeKind.Tag });

        foreach (ReportSections hidden in new[] { ReportSections.Frames, ReportSections.Statistics, ReportSections.SideInfo })
        {
            var writer = new StringWriter();
            ReportTextWriter.Write(report, writer, showOffsets: false, new ReportFilter(hidden));
            string text = writer.ToString();
            Assert.Contains("Frame: one", text, StringComparison.Ordinal);
            Assert.Contains("Statistics: two", text, StringComparison.Ordinal);
        }

        var framesHidden = new StringWriter();
        ReportTextWriter.Write(report, framesHidden, showOffsets: false, new ReportFilter(ReportSections.Frames | ReportSections.Statistics));
        Assert.DoesNotContain("Frame: MPEG-1", framesHidden.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Statistics: ", framesHidden.ToString().Replace("Statistics: two", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var tagsHidden = new StringWriter();
        ReportTextWriter.Write(report, tagsHidden, showOffsets: false, new ReportFilter(ReportSections.Tags));
        Assert.DoesNotContain("Frame: one", tagsHidden.ToString(), StringComparison.Ordinal);
        Assert.Contains("Frame: MPEG-1", tagsHidden.ToString(), StringComparison.Ordinal);
    }

    // An APEv2 tag with a footer only (APEv2 specification: 32-byte footer, flags 0 for a footer
    // without a header), items "size, flags, key, $00, value".
    private static byte[] ApeTag(params (string Key, string Value)[] items)
    {
        var body = new List<byte>();
        foreach ((string key, string value) in items.OrderBy(i => i.Key.Length + i.Value.Length))
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            body.AddRange(BitConverter.GetBytes(text.Length));
            body.AddRange(BitConverter.GetBytes(0));
            body.AddRange(Encoding.ASCII.GetBytes(key));
            body.Add(0);
            body.AddRange(text);
        }

        var footer = new List<byte>(Encoding.ASCII.GetBytes("APETAGEX"));
        footer.AddRange(BitConverter.GetBytes(2000));
        footer.AddRange(BitConverter.GetBytes(body.Count + 32));
        footer.AddRange(BitConverter.GetBytes(items.Length));
        footer.AddRange(BitConverter.GetBytes(0));
        footer.AddRange(new byte[8]);
        return [.. body, .. footer];
    }
}
