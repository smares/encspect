using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every FLAC fixture against its manifest (make_external_flac_fixtures.py, make_flac_fixtures.py, both worked out by flac_model.py, a
// reading of RFC 9639 apart from the analyzer, and confirmed by crosscheck_flac_fixtures.py): the metadata blocks, every frame with its
// header fields, subframes and checksums, the stretches of skipped bytes, the stream info, the verification and exactly the findings listed.
public class FlacFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "flac");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("flac/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    private static readonly string[] AssignmentNames = ["mono", "stereo", "3 channels", "4 channels", "5 channels", "6 channels", "7 channels", "8 channels", "left/side stereo", "side/right stereo", "mid/side stereo"];

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Assert.Equal(
            manifest.GetProperty("regions").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"),
            report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}").Take(manifest.GetProperty("regions").GetArrayLength()));
        bool isOgg = manifest.TryGetProperty("ogg", out JsonElement ogg);
        Node container = report.Root.Children.First(n => n.Kind == NodeKind.Stream || n.Name == "Ogg");
        Node stream = isOgg ? Resolve(container, "Logical Stream") : container;
        List<Node> metadata = stream.Children.Where(c => c.Name == "Metadata Block").ToList();
        // The report lists the first 1,024 blocks and counts the rest.
        JsonElement[] expectedMetadata = [.. manifest.GetProperty("metadata").EnumerateArray().Take(1024)];
        Assert.Equal(expectedMetadata.Length, metadata.Count);
        for (int i = 0; i < expectedMetadata.Length; i++)
        {
            CheckMetadata(expectedMetadata[i], metadata[i], $"{name}, metadata block {i}");
        }

        Node framesNode = isOgg ? stream.Children.Single(c => c.Name == "Frames") : stream;
        List<Node> frames = framesNode.Children.Where(c => c.Name == "Frame").ToList();
        JsonElement[] expectedFrames = manifest.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(expectedFrames.Length, frames.Count);
        for (int i = 0; i < expectedFrames.Length; i++)
        {
            CheckFrame(expectedFrames[i], frames[i], i, $"{name}, frame {i}");
        }

        Assert.Equal(
            manifest.GetProperty("skipped").EnumerateArray().Select(r => $"{r[0].GetInt64()}+{r[1].GetInt64()}"),
            stream.Children.Where(c => c.Name == "Skipped Data").Select(c => $"{c.Offset}+{c.Length}"));
        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        if (report.Streams.Count > 0)
        {
            CheckVerification(manifest.GetProperty("verification").GetProperty("default"), report.Streams.Single().Verification, DecodeStatus.NotRequested);
        }
        CheckFindings(manifest.GetProperty("findings"), report.Findings);
        if (isOgg)
        {
            Assert.Equal(ogg.GetProperty("packets").GetInt32(), stream.Children.Single(c => c.Name == "Packet Count").Value?.AsInteger());
            Assert.Equal(ogg.GetProperty("last_granule").GetInt64(), stream.Children.Single(c => c.Name == "Last Granule Position").Value?.AsInteger());
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
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

    private static Node Resolve(Node start, string name) => start.Children.First(c => c.Name == name);

    private static T Single<T>(IEnumerable<T> items, string what) => items.Single();

    private static void CheckMetadata(JsonElement expected, Node block, string where)
    {
        Assert.True(expected.GetProperty("offset").GetInt64() == block.Offset && expected.GetProperty("length").GetInt64() == block.Length,
            $"{where} is at {block.Offset}+{block.Length}, expected {expected.GetProperty("offset").GetInt64()}+{expected.GetProperty("length").GetInt64()}");
        Assert.Equal(expected.GetProperty("type").GetString(), block.Value?.ToString());
        Node header = block.Children.First(c => c.Name == "Block Header");
        Assert.Equal(expected.GetProperty("is_last").GetBoolean(), header.Children.Single(c => c.Name == "IsLastBlock").Value?.AsBoolean());
        Assert.Equal(expected.GetProperty("body_length").GetInt64(), header.Children.Single(c => c.Name == "Block Length").Value?.AsInteger());
    }

    private static void CheckFrame(JsonElement expected, Node frame, int index, string where)
    {
        Assert.True(expected.GetProperty("offset").GetInt64() == frame.Offset && expected.GetProperty("length").GetInt64() == frame.Length,
            $"{where} is at {frame.Offset}+{frame.Length}, expected {expected.GetProperty("offset").GetInt64()}+{expected.GetProperty("length").GetInt64()}");
        Node header = frame.Children.First(c => c.Name == "Frame Header");
        Node Field(string field) => header.Children.First(c => c.Name == field);
        Assert.Equal(expected.GetProperty("variable").GetBoolean() ? "variable" : "fixed", Field("Blocking Strategy").Value?.ToString());
        Assert.Equal(expected.GetProperty("block_size").GetInt64(), Field("Block Size").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("sample_rate").GetInt64(), Field("Sample Rate").Value?.AsInteger());
        Assert.Equal(AssignmentNames[expected.GetProperty("channel_code").GetInt32()], Field("Channel Assignment").Value?.ToString());
        Assert.Equal(expected.GetProperty("bits").GetInt64(), Field("Sample Size").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("number").GetInt64(), header.Children.First(c => c.Name is "Frame Number" or "Sample Number").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("crc8").GetString(), Hex(Field("CRC-8").Value!, 2));
        string[] status = [.. expected.GetProperty("status").EnumerateArray().Select(s => s.GetString()!)];
        Node? crc16 = frame.Children.FirstOrDefault(c => c.Name == "CRC-16");
        JsonElement stored = expected.GetProperty("crc16");
        if (stored.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(crc16);
        }
        else
        {
            Assert.Equal(stored.GetString(), Hex(crc16!.Value!, 4));
            Assert.Equal(!status.Contains("crc16"), frame.Children.Single(c => c.Name == "IsCrc16Valid").Value?.AsBoolean());
        }

        if (expected.TryGetProperty("subframes", out JsonElement subframes) && index < 4096)
        {
            Node[] nodes = [.. frame.Children.Where(c => c.Name == "Subframe")];
            Assert.Equal(subframes.GetArrayLength(), nodes.Length);
            for (int c = 0; c < nodes.Length; c++)
            {
                JsonElement[] values = [.. subframes[c].EnumerateArray()];
                string type = values[0].GetString()!;
                string text = nodes[c].Value!.ToString();
                Assert.StartsWith(type switch { "constant" => "constant", "verbatim" => "verbatim", "fixed" => "fixed predictor", _ => "linear predictor" }, text, StringComparison.Ordinal);
                Assert.Equal(values[1].GetInt64(), type is "fixed" or "lpc" ? nodes[c].Children.Single(n => n.Name == "Predictor Order").Value?.AsInteger() : 0);
                Assert.Equal(values[2].GetInt64(), nodes[c].Children.Single(n => n.Name == "Wasted Bits").Value?.AsInteger());
                if (values.Length > 3)
                {
                    Assert.Equal(values[3].GetInt64(), nodes[c].Children.Single(n => n.Name == "Residual").Children.Single(n => n.Name == "Partition Order").Value?.AsInteger());
                }
            }
        }
    }

    private static string Hex(NodeValue value, int digits) => value.AsInteger()?.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ?? value.ToString();

    private static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
    {
        if (expected.GetArrayLength() == 0)
        {
            Assert.Empty(streams);
            return;
        }

        JsonElement info = expected.EnumerateArray().Single();
        StreamInfo stream = streams.Single();
        Assert.Equal(info.GetProperty("codec").GetString(), stream.Codec);
        Assert.Equal(info.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
        Assert.Equal(info.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
        Assert.Equal(info.GetProperty("bits_per_sample").GetInt32(), stream.BitsPerSample);
        if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 6);
        }

        Assert.Equal(info.GetProperty("layout").EnumerateArray().Select(s => s.GetString()), stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
    }

    internal static void CheckVerification(JsonElement expected, StreamVerification? actual, DecodeStatus status)
    {
        Assert.NotNull(actual);
        Assert.Equal(status, actual.Decoding);
        JsonElement verdict = expected.GetProperty("isVerified");
        Assert.Equal(verdict.ValueKind == JsonValueKind.Null ? null : verdict.GetBoolean(), actual.IsVerified);
        JsonElement[] tallies = [.. expected.GetProperty("checksums").EnumerateArray()];
        Assert.Equal(tallies.Length, actual.Checksums.Count);
        for (int i = 0; i < tallies.Length; i++)
        {
            ChecksumTally tally = actual.Checksums[i];
            Assert.Equal(tallies[i].GetProperty("name").GetString(), tally.Name);
            Assert.Equal(tallies[i].GetProperty("coversAllAudio").GetBoolean(), tally.CoversAllAudio);
            Assert.Equal(tallies[i].GetProperty("stored").GetInt64(), tally.Stored);
            Assert.Equal(tallies[i].GetProperty("checked").GetInt64(), tally.Checked);
            Assert.Equal(tallies[i].GetProperty("failed").GetInt64(), tally.Failed);
        }
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            wanted[code] = wanted.GetValueOrDefault(code) + (item.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 1);
        }

        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in findings)
        {
            found[finding.Code] = found.GetValueOrDefault(finding.Code) + finding.Count;
        }

        Assert.True(wanted.SequenceEqual(found), $"Expected findings {string.Join(", ", wanted.Select(w => $"{w.Key} x{w.Value}"))}; found {string.Join("; ", findings.Select(f => $"{f.Code} x{f.Count}: {f.Message}"))}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }
}
