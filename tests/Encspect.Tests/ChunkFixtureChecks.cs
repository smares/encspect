using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// What the WAV and AIFF fixture tests check alike against a manifest (make_wav_fixtures.py, make_external_wav_fixtures.py, make_aiff_fixtures.py,
// make_external_aiff_fixtures.py, worked out by wav_model.py and aiff_model.py from the specifications apart from the analyzer): the length, the
// chunks with their places, the stream and exactly the findings listed.
internal static class ChunkFixtureChecks
{
    // The most chunks a level lists as nodes (Walker.MaxListed).
    private const int MaxListed = 2048;

    private static readonly HashSet<string> HeaderNodes = ["Chunk ID", "Chunk Size", "Form Type", "List Type"];

    public static TheoryData<string> Names(string directory)
    {
        var data = new TheoryData<string>();
        string path = Path.Combine(Fixtures.FixtureDirectory, directory);
        foreach (string manifest in Directory.EnumerateFiles(path, "*.expected.json").Order(StringComparer.Ordinal))
        {
            string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
            string file = Directory.EnumerateFiles(path, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
            data.Add($"{directory}/{Path.GetFileName(file)}");
        }

        return data;
    }

    public static void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        JsonElement container = manifest.GetProperty("container");
        if (container.ValueKind == JsonValueKind.Null)
        {
            CheckFindings(manifest.GetProperty("findings"), report, false);
            return;
        }

        Node node = report.Root.Children.Single(c => c.Name is "RIFF" or "RIFX" or "RF64" or "BW64" or "Wave64" or "FORM");
        Assert.Equal(container.GetProperty("id").GetString(), node.Name);
        Assert.Equal(container.GetProperty("end").GetInt64(), node.End);
        CheckChunks(manifest.GetProperty("chunks"), node, name);

        JsonElement stream = manifest.GetProperty("stream");
        bool isRouted = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("routed", out _);
        CheckStream(stream, report.Streams, name);
        CheckFindings(manifest.GetProperty("findings"), report, isRouted);
    }

    public static void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToGoldenJson(name, Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // The chunk nodes below a container or a LIST chunk: its children but the fields of its header and the nodes that are no chunk.
    private static List<Node> ChunkNodes(Node parent) =>
        [.. parent.Children.Where(c => !HeaderNodes.Contains(c.Name) && c.Name is not ("Unread Data" or "Extra Data" or "Further Chunks" or "Statistics" or "Writer" or "Pad Byte" or "Chunks"))];

    private static void CheckChunks(JsonElement expected, Node parent, string where)
    {
        List<Node> nodes = ChunkNodes(parent);
        JsonElement[] chunks = [.. expected.EnumerateArray().Take(MaxListed)];
        Assert.True(nodes.Count == chunks.Length, $"{where}: {parent.Name} holds {nodes.Count} chunk nodes, the manifest {chunks.Length}.");
        for (int i = 0; i < chunks.Length; i++)
        {
            string id = chunks[i].GetProperty("id").GetString()!;
            Node node = nodes[i];
            long offset = chunks[i].GetProperty("offset").GetInt64();
            long end = chunks[i].GetProperty("end").GetInt64();
            Assert.True(offset == node.Offset && end == node.End, $"{where}: chunk {i} ({id}) is at {node.Offset}..{node.End}, the manifest {offset}..{end}.");
            Assert.Equal(id, node.Children.First(c => c.Name == "Chunk ID").Value?.AsText());
            if (chunks[i].TryGetProperty("children", out JsonElement children))
            {
                Assert.Equal(chunks[i].GetProperty("list_type").GetString(), node.Children.First(c => c.Name == "List Type").Value?.AsText());
                CheckChunks(children, node, $"{where}/{id}");
            }
        }
    }

    private static void CheckStream(JsonElement expected, IReadOnlyList<StreamInfo> streams, string where)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Empty(streams);
            return;
        }

        Assert.True(streams.Count >= 1, $"{where}: the report holds no stream.");
        StreamInfo stream = streams[0];
        Assert.Equal(expected.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
        Assert.Equal(expected.GetProperty("channels").GetInt32(), stream.Channels);
        if (expected.TryGetProperty("routed", out _))
        {
            Assert.NotEqual("Other", stream.Codec);
            return;
        }

        Assert.Single(streams);
        Assert.Equal(expected.GetProperty("codec").GetString(), stream.Codec);
        Assert.Equal(expected.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(expected.GetProperty("duration").GetDouble(), stream.Duration, 9);
        JsonElement bits = expected.GetProperty("bits");
        Assert.Equal(bits.ValueKind == JsonValueKind.Null ? null : bits.GetInt32(), stream.BitsPerSample);
        JsonElement bitrate = expected.GetProperty("average_bitrate");
        if (bitrate.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(bitrate.GetDouble(), stream.AverageBitrate!.Value, 6);
        }

        Assert.Equal(expected.GetProperty("layout").EnumerateArray().Select(s => s.GetString()), stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
        Assert.Null(stream.Verification);
    }

    // The findings of the analyzer's own codes and of the host's routes must be exactly the manifest's. A codec parser reached through a route adds
    // findings of its own, which its fixtures check in detail; here every one of them that is a Warning or an Error must be listed too, so that a
    // route that makes the codec parser complain (a configuration it should not have been given) cannot pass as a clean file.
    private static void CheckFindings(JsonElement expected, Report report, bool isRouted)
    {
        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            wanted[code] = wanted.GetValueOrDefault(code) + (item.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 1);
        }

        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in report.Findings.Where(f => !isRouted || f.Severity >= Severity.Warning || f.Code.StartsWith("wav.", StringComparison.Ordinal) || f.Code.StartsWith("parse.", StringComparison.Ordinal) || f.Code.StartsWith("tag.", StringComparison.Ordinal)))
        {
            found[finding.Code] = found.GetValueOrDefault(finding.Code) + finding.Count;
        }

        Assert.True(wanted.SequenceEqual(found), $"Expected findings {string.Join(", ", wanted.Select(w => $"{w.Key} x{w.Value}"))}; found {string.Join("; ", report.Findings.Select(f => $"{f.Code} x{f.Count}: {f.Message}"))}");
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        Assert.All(report.Findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }
}
