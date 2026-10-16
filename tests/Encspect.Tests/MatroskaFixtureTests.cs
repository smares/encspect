using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every Matroska and WebM fixture against its manifest (make_matroska_fixtures.py and make_external_matroska_fixtures.py, worked out
// by matroska_model.py from the bytes alone apart from the analyzer, and confirmed by crosscheck_matroska_fixtures.py): the tree of
// elements with their offsets and sizes, every block's header fields and frames, the tracks, the streams the codec parsers report and
// exactly the findings listed.
public class MatroskaFixtureTests
{
    private static readonly string[] Extensions = [".mka", ".mkv", ".webm"];

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "matroska");
            foreach (string path in Directory.EnumerateFiles(directory).Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            {
                data.Add($"matroska/{Path.GetFileName(path)}");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        JsonElement manifest = Manifest(name);
        Report report = Fixtures.AnalyzeMpeg(name);
        string format = manifest.GetProperty("format").GetString()!;
        Assert.Equal(format, report.Root.Value?.ToString());
        Node file = report.Root.Children.Single(n => n.Name == format);
        Assert.Equal(manifest.GetProperty("length").GetInt64(), file.Length);

        Node[] shown = [.. file.Children.Where(c => !c.Name.EndsWith(" Frames", StringComparison.Ordinal))];
        CompareTree(manifest.GetProperty("tree"), shown, "file");
        CompareBlocks(manifest.GetProperty("blocks"), file);
        CompareTracks(manifest.GetProperty("tracks"), file);
        CompareStreams(manifest, report);
        CheckFindings(manifest.GetProperty("findings"), report.Findings);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        TestFiles.CheckInsideParents(report.Root);
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

    [Theory]
    [MemberData(nameof(Names))]
    public void DetectionIsTheSameInEveryOrder(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        JsonElement manifest = Manifest(name);
        string expected = manifest.TryGetProperty("detected", out JsonElement detected) ? detected.GetString()! : "Matroska";
        Assert.Equal(expected, DetectionOrderTests.Detect(DefaultParsers.Formats, data, Path.GetExtension(name)));
        Assert.Equal(expected, DetectionOrderTests.Detect([.. DefaultParsers.Formats.Reverse()], data, string.Empty));
    }

    // The manifest of a file with deeply nested elements nests deeper than the reader's default allows.
    private static JsonElement Manifest(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Fixtures.MpegCompanion(name, ".expected.json")), new JsonDocumentOptions { MaxDepth = 512 });
        return document.RootElement.Clone();
    }

    private static void CompareTree(JsonElement expected, IReadOnlyList<Node> actual, string where)
    {
        JsonElement[] entries = [.. expected.EnumerateArray()];
        Assert.True(entries.Length == actual.Count, $"{where}: {entries.Length} nodes expected, {actual.Count} found ({string.Join(", ", actual.Select(n => n.Name))}).");
        for (int i = 0; i < entries.Length; i++)
        {
            string name = entries[i][0].GetString()!;
            Node node = actual[i];
            Assert.Equal($"{name}@{entries[i][1].GetInt64()}+{entries[i][2].GetInt64()}", $"{node.Name}@{node.Offset}+{node.Length}");
            bool isBlock = node.Name is "SimpleBlock" or "Block";

            // The codec parser adds the configuration it reads, such as the AudioSpecificConfig, to the CodecPrivate node.
            if (entries[i].GetArrayLength() > 3)
            {
                CompareTree(entries[i][3], node.Children, $"{where}/{name}");
            }
            else if (node.Name == "CodecID" && node.Value?.ToString()?.StartsWith("A_AAC/MPEG", StringComparison.Ordinal) == true)
            {
                // The AAC parser puts the AudioSpecificConfig worked out from a legacy ID under the CodecID that gives it.
                Assert.Equal(["AudioSpecificConfig"], node.Children.Select(c => c.Name));
            }
            else if (!isBlock && node.Name != "CodecPrivate")
            {
                Assert.True(node.Children.Count == 0, $"{where}/{name} has children {string.Join(", ", node.Children.Select(c => c.Name))}.");
            }
        }
    }

    // Every block of the Segment's Clusters, in file order, with the fields of its header and the offset and size of each frame.
    private static List<Node> BlocksOf(Node file)
    {
        var blocks = new List<Node>();
        foreach (Node segment in file.Children.Where(c => c.Name == "Segment"))
        {
            foreach (Node cluster in segment.Children.Where(c => c.Name == "Cluster"))
            {
                foreach (Node item in cluster.Children)
                {
                    if (item.Name == "SimpleBlock")
                    {
                        blocks.Add(item);
                    }
                    else if (item.Name == "BlockGroup")
                    {
                        blocks.AddRange(item.Children.Where(c => c.Name == "Block"));
                    }
                }
            }
        }

        return blocks;
    }

    private static void CompareBlocks(JsonElement expected, Node file)
    {
        JsonElement[] blocks = [.. expected.EnumerateArray()];
        List<Node> actual = BlocksOf(file);
        Assert.Equal(blocks.Length, actual.Count);
        for (int i = 0; i < blocks.Length; i++)
        {
            JsonElement block = blocks[i];
            Node node = actual[i];
            string where = $"block {i} ({block.GetProperty("name").GetString()} at {block.GetProperty("offset").GetInt64()})";
            Assert.True(block.GetProperty("name").GetString() == node.Name && block.GetProperty("offset").GetInt64() == node.Offset && block.GetProperty("length").GetInt64() == node.Length, where);
            string? problem = block.TryGetProperty("problem", out JsonElement p) ? p.GetString() : null;
            if (problem is "short" or "too-large")
            {
                Assert.DoesNotContain(node.Children, c => c.Name == "Frame");
                continue;
            }

            Assert.Equal(block.GetProperty("track").GetInt64(), node.Children.Single(c => c.Name == "Track Number").Value!.AsInteger());
            Assert.Equal(block.GetProperty("timestamp").GetInt64(), node.Children.Single(c => c.Name == "Timestamp").Value!.AsInteger());
            Node flags = node.Children.Single(c => c.Name == "Flags");
            Assert.Equal((ulong)block.GetProperty("flags").GetInt64(), flags.Value!.AsUnsigned());
            string lacing = flags.Children.Single(c => c.Name == "Lacing").Value!.ToString();
            Assert.Contains(block.GetProperty("lacing").GetString() switch { "none" => "(none)", "xiph" => "(Xiph)", "ebml" => "(EBML)", _ => "(fixed-size)" }, lacing, StringComparison.Ordinal);
            Assert.Equal(block.GetProperty("lacing").GetString() != "none", node.Children.Any(c => c.Name == "Lacing"));
            IEnumerable<string> frames = block.GetProperty("frames").EnumerateArray().Select(f => $"{f[0].GetInt64()}+{f[1].GetInt64()}");
            Assert.Equal(frames, node.Children.Where(c => c.Name == "Frame").Select(c => $"{c.Offset}+{c.Length}"));
        }
    }

    private static void CompareTracks(JsonElement expected, Node file)
    {
        List<Node> entries = [.. file.Children.Where(c => c.Name == "Segment").SelectMany(s => s.Children.Where(c => c.Name == "Tracks")).SelectMany(t => t.Children.Where(c => c.Name == "TrackEntry"))];
        JsonElement[] tracks = [.. expected.EnumerateArray()];
        Assert.Equal(tracks.Length, entries.Count);
        for (int i = 0; i < tracks.Length; i++)
        {
            JsonElement track = tracks[i];
            Node entry = entries[i];
            Assert.Equal(Number(track, "number"), entry.Children.FirstOrDefault(c => c.Name == "TrackNumber")?.Value?.AsInteger());
            Assert.Equal(Number(track, "type"), entry.Children.FirstOrDefault(c => c.Name == "TrackType")?.Value?.ToString().Split(' ')[0] is { } t ? long.Parse(t, CultureInfo.InvariantCulture) : null);
            Assert.Equal(track.GetProperty("codec").ValueKind == JsonValueKind.Null ? null : track.GetProperty("codec").GetString(), entry.Children.FirstOrDefault(c => c.Name == "CodecID")?.Value?.ToString());
            if (track.GetProperty("private").ValueKind == JsonValueKind.Array)
            {
                Node priv = entry.Children.Single(c => c.Name == "CodecPrivate");
                Assert.Equal(track.GetProperty("private")[1].GetInt64(), priv.End - track.GetProperty("private")[0].GetInt64());
            }
        }
    }

    private static long? Number(JsonElement track, string property) => track.GetProperty(property).ValueKind == JsonValueKind.Null ? null : track.GetProperty(property).GetInt64();

    private static void CompareStreams(JsonElement manifest, Report report)
    {
        if (manifest.GetProperty("streams").ValueKind != JsonValueKind.Array)
        {
            return;
        }

        JsonElement[] expected = [.. manifest.GetProperty("streams").EnumerateArray()];
        Assert.Equal(expected.Select(s => $"{s.GetProperty("codec").GetString()}, {s.GetProperty("sample_rate").GetInt32()} Hz, {s.GetProperty("channels").GetInt32()} channels"),
            report.Streams.Select(s => $"{(s.Codec.StartsWith("HE-AAC", StringComparison.Ordinal) || s.Codec.StartsWith("AAC", StringComparison.Ordinal) ? "AAC" : s.Codec)}, {s.SampleRate} Hz, {s.Channels} channels"));
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
}
