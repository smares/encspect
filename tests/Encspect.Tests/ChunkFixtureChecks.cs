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
        if (manifest.TryGetProperty("iec61937", out JsonElement bursts))
        {
            CheckBursts(bursts, node, report, name);
        }

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

        // A stream whose samples the decoder "pcm" takes has a session in every analysis, which decodes nothing unless measuring or output is asked for.
        if (expected.TryGetProperty("pcm", out _))
        {
            Assert.True(stream.Verification is { Decoding: DecodeStatus.NotRequested, HasChecksums: false, DecodedSamples: 0 }, $"{where}: the verification is {stream.Verification}.");
        }
        else
        {
            Assert.Null(stream.Verification);
        }
    }

    // The data-bursts of IEC 61937 the model found in the samples: how many of the stream's data type, where the first of them start, and the pause and null bursts that are counted and not listed.
    private static void CheckBursts(JsonElement expected, Node container, Report report, string where)
    {
        Node data = container.Children.Single(c => c.Name == "Data Chunk");
        Node bursts = data.Children.Single(c => c.Name == "IEC 61937 Bursts");
        Assert.Equal(expected.GetProperty("bursts").GetInt32(), bursts.Value?.AsInteger());
        long[] listed = [.. expected.GetProperty("listed").EnumerateArray().Select(o => o.GetInt64())];
        long[] found = [.. bursts.Children.Select(c => c.Offset)];
        Assert.True(listed.SequenceEqual(found), $"{where}: the burst nodes are at {string.Join(", ", found.Take(6))}, the manifest {string.Join(", ", listed.Take(6))}.");
        Assert.Equal(expected.GetProperty("first").GetInt64(), bursts.Offset);
        Assert.DoesNotContain(bursts.Children, c => c.Value?.AsText() is "pause" or "null data");
        int pauses = expected.GetProperty("pauses").GetInt32();
        Finding[] counted = [.. report.Findings.Where(f => f.Code == "wav.iec61937-pause-bursts")];
        if (pauses == 0)
        {
            Assert.Empty(counted);
        }
        else
        {
            Assert.Contains($"{pauses} pause or null burst", Assert.Single(counted).Message, StringComparison.Ordinal);
            Assert.Contains($" in {expected.GetProperty("pause_runs").GetInt32()} run", counted[0].Message, StringComparison.Ordinal);
        }
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

    // What decoding adds to the report of a fixture the decoder "pcm" takes: the verification (the status and the samples decoded, no checksum) and the
    // decode.* findings, in <name>.decode.json as for the other decoded formats (rewritten with ENCSPECT_UPDATE_GOLDEN=1). A fixture without a session has none.
    public static void DecodeGoldenMatches(string name)
    {
        JsonElement stream = Fixtures.LoadMpegManifest(name).GetProperty("stream");
        string path = Fixtures.MpegCompanion(name, ".decode.json");
        if (stream.ValueKind != JsonValueKind.Object || !stream.TryGetProperty("pcm", out _))
        {
            Assert.False(File.Exists(path), $"{path} exists for a fixture whose stream the decoder does not take.");
            return;
        }

        var sink = new PcmHashingSink();
        using FileStream file = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), file, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });
        string actual = DecoderFixtureTests.Golden(report);
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // Decodes the file with a hashing sink and holds the decoder "pcm" to the manifest: the samples and the MD5 of the canonical layout that pcm_model.py works out
    // from the specifications, which crosscheck_wav_fixtures.py confirms with FFmpeg; a file whose manifest has no pcm is not decoded.
    public static void DecodedAudioMatchesManifest(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        var sink = new PcmHashingSink();
        using FileStream file = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), file, new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => sink.Declare(stream) } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.no-decoder");
        JsonElement stream = manifest.GetProperty("stream");
        if (stream.ValueKind != JsonValueKind.Object || !stream.TryGetProperty("pcm", out JsonElement pcm))
        {
            Assert.Equal(0, sink.Samples);
            Assert.All(report.Streams, s => Assert.Null(s.Verification));
            return;
        }

        StreamVerification verification = report.Streams[0].Verification!;
        Assert.False(verification.HasChecksums);
        Assert.Null(verification.IsVerified);
        if (pcm.TryGetProperty("not_possible", out _))
        {
            Assert.Equal(DecodeStatus.NotPossible, verification.Decoding);
            Assert.Contains(report.Findings, f => f.Code == "decode.not-possible");
            Assert.Equal(0, sink.Samples);
            return;
        }

        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(pcm.GetProperty("samples").GetInt64(), verification.DecodedSamples);
        Assert.Equal(verification.DecodedSamples, sink.Samples);
        Assert.Equal(pcm.GetProperty("md5").GetString(), sink.Hash());
        if (verification.DecodedSamples == 0)
        {
            // No sample frame: the decoder declares no output.
            Assert.Equal(0, sink.Declared);
            return;
        }

        Assert.Equal(pcm.GetProperty("bits").GetInt32(), sink.Bits);
        Assert.Equal(pcm.GetProperty("format").GetString() == "float" ? (pcm.GetProperty("bits").GetInt32() == 32 ? SampleFormat.Float32 : SampleFormat.Float64) : SampleFormat.Integer, sink.Format);
        Assert.Equal(stream.GetProperty("sample_rate").GetInt32(), sink.SampleRate);
        Assert.Equal(stream.GetProperty("channels").GetInt32(), sink.Channels);
        Assert.Equal(1, sink.Declared);
    }
}
