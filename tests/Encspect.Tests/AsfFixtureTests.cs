using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every hand-built ASF fixture against its manifest (make_asf_fixtures.py, worked out by
// asf_model.py apart from the analyzer and confirmed by crosscheck_asf_fixtures.py): the objects at the
// top level, in the header and in the Header Extension Object with their offsets and sizes, the audio
// streams' WAVEFORMATEX, every packet and payload, the media objects put together from the payloads,
// and exactly the findings listed.
public class AsfFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string manifest in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                if (!stem.StartsWith("external-", StringComparison.Ordinal))
                {
                    data.Add($"asf/{stem}.asf");
                }
            }

            return data;
        }
    }

    // PCM, MPEG audio and WMA streams go to a codec that collects the media objects, so the second pass
    // over the packets is checked on fixtures whose payloads are no codec's frames.
    private static readonly Dictionary<int, string> TestRoutes = new() { [0x0001] = "wma", [0x0055] = "wma", [0x0161] = "wma" };

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("ASF", report.Root.Value?.ToString());
        Node file = report.Root.Children.Single(n => n.Name == "ASF");
        Assert.Equal(manifest.GetProperty("length").GetInt64(), file.Length);

        Assert.Equal(Listed(manifest, "objects"), Objects(file));
        Node? header = file.Children.FirstOrDefault(c => c.Name == "Header Object");
        if (header is not null)
        {
            Assert.Equal(Listed(manifest, "header_objects"), Objects(header));
            Node? extension = header.Children.FirstOrDefault(c => c.Name == "Header Extension Object");
            Assert.Equal(Listed(manifest, "extension_objects"), extension is null ? [] : Objects(extension));
            CheckStreams(manifest.GetProperty("streams"), header);
        }

        Node? data = file.Children.FirstOrDefault(c => c.Name == "Data Object");
        if (data is not null && data.Children.FirstOrDefault(c => c.Name == "Data Packets") is { } packets)
        {
            CheckPackets(manifest, packets);
        }

        CheckFindings(manifest.GetProperty("findings"), report.Findings);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        TestFiles.CheckInsideParents(report.Root);
    }

    // The media objects the second pass puts together from the payloads, handed to a codec parser.
    [Theory]
    [MemberData(nameof(Names))]
    public void MediaObjectsMatchManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        var collected = new List<(int Stream, MappedBytes Object)>();
        var analyzer = new Analyzer(DefaultParsers.Tags, [new AsfParser(TestRoutes)], [new CollectingCodec("wma", collected)]);
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = analyzer.Analyze(Path.GetFileName(name), stream);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        CheckMediaObjects(manifest.GetProperty("media_objects"), collected);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void RejectedId3AttributePointsAtANodeOfTheTree()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf/metadata.asf"));
        int at = data.AsSpan().IndexOf("ID3\u0003"u8);
        Assert.True(at > 0);
        data[at + 6] = 0xA1;
        Report report = Analyzer.CreateDefault().Analyze("metadata.asf", new MemoryStream(data));
        Finding rejected = Assert.Single(report.Findings, f => f.Code == "tag.rejected");
        Assert.NotNull(rejected.Path);
    }

    // WM/ToolName holds ffmpeg's encoder ID: it is encoder evidence, weighed because the stream shows ffmpeg's superframe padding, and no writer.
    [Fact]
    public void ALibavcodecToolNameIsEncoderEvidenceAndNoWriter()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "asf/tool-name-lavc.asf");
        Report report = Analyzer.CreateDefault().Analyze(path, new MemoryStream(File.ReadAllBytes(path)));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("FFmpeg (wmav2 encoder)", Confidence.High), (stream.Encoder, stream.EncoderConfidence));
        Assert.Null(stream.Writer);
        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);
    }

    private static List<string> Listed(JsonElement manifest, string property) =>
        [.. manifest.GetProperty(property).EnumerateArray().Select(e => $"{(e[0].GetString()!.Contains('-', StringComparison.Ordinal) ? "Unknown Object" : e[0].GetString())}@{e[1].GetInt64()}+{e[2].GetInt64()}")];

    private static List<string> Objects(Node parent) =>
        [.. parent.Children.Where(c => c.Name.EndsWith(" Object", StringComparison.Ordinal)).Select(c => $"{c.Name}@{c.Offset}+{c.Length}")];

    private static void CheckStreams(JsonElement expected, Node header)
    {
        List<Node> properties = [.. header.Children.Where(c => c.Name == "Stream Properties Object")];
        // A stream inside an Extended Stream Properties Object has no Stream Properties Object of its own in the header.
        JsonElement[] streams = [.. expected.EnumerateArray().Where(s => !s.TryGetProperty("embedded", out _))];
        Assert.True(properties.Count >= streams.Length - 3 && properties.Count <= streams.Length, $"{properties.Count} Stream Properties Objects, {streams.Length} streams");
        foreach (JsonElement entry in streams.Where(s => s.TryGetProperty("tag", out _)))
        {
            Node node = properties.First(p => p.Children.Single(c => c.Name == "Flags").Children.Single(c => c.Name == "Stream Number").Value?.AsInteger() == entry.GetProperty("number").GetInt32());
            Node format = node.Children.Single(c => c.Name == "Type-Specific Data");
            Assert.Equal(entry.GetProperty("channels").GetInt32(), (int)format.Children.Single(c => c.Name == "Channels").Value!.AsInteger()!);
            Assert.Equal(entry.GetProperty("rate").GetInt32(), (int)format.Children.Single(c => c.Name == "Samples Per Second").Value!.AsInteger()!);
            Assert.Equal(entry.GetProperty("avg_bytes").GetInt32(), (int)format.Children.Single(c => c.Name == "Average Bytes Per Second").Value!.AsInteger()!);
            Assert.Equal(entry.GetProperty("block_align").GetInt32(), (int)format.Children.Single(c => c.Name == "Block Alignment").Value!.AsInteger()!);
            Assert.Equal(entry.GetProperty("bits").GetInt32(), (int)format.Children.Single(c => c.Name == "Bits Per Sample").Value!.AsInteger()!);
            Assert.StartsWith($"0x{entry.GetProperty("tag").GetInt32():X4}", format.Children.Single(c => c.Name == "Format Tag").Value!.ToString(), StringComparison.Ordinal);
            if (entry.GetProperty("tag").GetInt32() != 0xFFFE && entry.TryGetProperty("codec_data", out JsonElement codec) && codec.GetString()!.Length > 0)
            {
                Assert.Equal(codec.GetString()!.Length / 2, (int)format.Children.Single(c => c.Name == "Codec Specific Data").Length);
            }
        }
    }

    private static void CheckPackets(JsonElement manifest, Node packets)
    {
        JsonElement[] expectedPackets = [.. manifest.GetProperty("packets").EnumerateArray()];
        Assert.Equal(expectedPackets.Select(p => $"{p[0].GetInt64()}+{p[1].GetInt64()}:{p[2].GetInt32()}"),
            packets.Children.Select(p => $"{p.Offset}+{p.Length}:{p.Children.Count(c => c.Name == "Payload")}"));
        Assert.All(packets.Children, p => Assert.Equal(NodeKind.Frame, p.Kind));

        List<string> actual = [];
        for (int i = 0; i < packets.Children.Count; i++)
        {
            foreach (Node payload in packets.Children[i].Children.Where(c => c.Name == "Payload"))
            {
                Node stream = payload.Children.Single(c => c.Name == "Stream Number");
                long streamNumber = stream.Children.Single(c => c.Name == "Stream Number").Value!.AsInteger()!.Value;
                bool key = stream.Children.Single(c => c.Name == "Key Frame Bit").Value!.AsBoolean()!.Value;
                long number = payload.Children.FirstOrDefault(c => c.Name == "Media Object Number")?.Value?.AsInteger() ?? 0;
                long offset = (payload.Children.FirstOrDefault(c => c.Name is "Offset Into Media Object" or "Presentation Time"))?.Value?.AsInteger() ?? 0;
                long replicated = payload.Children.FirstOrDefault(c => c.Name == "Replicated Data Length")?.Value?.AsInteger() ?? 0;
                Node dataNode = payload.Children.Single(c => c.Name == "Payload Data");
                actual.Add($"{i},{streamNumber},{number},{offset},{replicated},{dataNode.Length},{replicated == 1},{key}");
            }
        }

        Assert.Equal(manifest.GetProperty("payloads").EnumerateArray().Select(p =>
            $"{p[0].GetInt32()},{p[1].GetInt32()},{p[2].GetInt64()},{p[3].GetInt64()},{p[4].GetInt64()},{p[5].GetInt64()},{p[6].GetBoolean()},{p[7].GetBoolean()}"), actual);
    }

    private static void CheckMediaObjects(JsonElement expected, List<(int Stream, MappedBytes Object)> collected)
    {
        // The streams that were routed to a codec: the others' media objects are not put together.
        HashSet<int> routed = [.. collected.Select(c => c.Stream)];
        Assert.Equal(
            expected.EnumerateArray().Where(o => routed.Contains(o[0].GetInt32())).OrderBy(o => o[0].GetInt32())
                .Select(o => $"{o[0].GetInt32()},{o[2].GetInt64()},{string.Join(';', o[4].EnumerateArray().Select(r => $"{r[0].GetInt64()}+{r[1].GetInt64()}"))}"),
            collected.Select(c => $"{c.Stream},{c.Object.Length},{string.Join(';', c.Object.FileRanges().Select(r => $"{r.Offset}+{r.Length}"))}"));
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var remaining = findings.ToList();
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"))}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {string.Join("; ", remaining.Select(f => $"{f.Code}: {f.Message}"))}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }

    // A codec parser that keeps the media objects it is handed.
    private sealed class CollectingCodec(string id, List<(int Stream, MappedBytes Object)> collected) : ICodecParser
    {
        public string Id => id;

        public void Parse(CodecContext context)
        {
            int stream = context.Configuration?.Format?.StreamNumber ?? -1;
            foreach (MappedBytes packet in context.Packets!)
            {
                collected.Add((stream, packet));
            }
        }
    }
}
