using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every WavPack fixture against its manifest (make_external_wavpack_fixtures.py,
// make_wavpack_fixtures.py, both worked out by wavpack_model.py and confirmed by
// crosscheck_wavpack_fixtures.py): the top-level regions, every block with its place, header
// fields, checksum verdict and sub-blocks, the stretches of skipped bytes, the stream info and
// exactly the findings listed.
public class WavPackFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "wavpack");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("wavpack/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Assert.Equal(
            manifest.GetProperty("regions").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"),
            report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));
        Assert.Equal("WavPack", report.Root.Value?.ToString());

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        List<Node> blocks = stream.Children.Where(c => c.Name is "Block" or "Metadata Block").ToList();
        JsonElement[] expected = manifest.GetProperty("blocks").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, blocks.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            CheckBlock(expected[i], blocks[i], i, name);
        }

        Assert.Equal(
            manifest.GetProperty("skipped").EnumerateArray().Select(r => $"{r[0].GetInt64()}+{r[1].GetInt64()}"),
            stream.Children.Where(c => c.Name == "Skipped Data").Select(c => $"{c.Offset}+{c.Length}"));
        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        if (report.Streams.Count > 0)
        {
            WavPackDecoderFixtureTests.CheckVerification(manifest.GetProperty("verification").GetProperty("default"), report.Streams.Single().Verification, DecodeStatus.NotRequested);
        }

        CheckFindings(manifest.GetProperty("findings"), report.Findings);
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

    private static void CheckBlock(JsonElement expected, Node block, int index, string name)
    {
        string where = $"{name}, block {index}";
        Assert.True(expected.GetProperty("offset").GetInt64() == block.Offset && expected.GetProperty("length").GetInt64() == block.Length,
            $"{where} is at {block.Offset}+{block.Length}, expected {expected.GetProperty("offset").GetInt64()}+{expected.GetProperty("length").GetInt64()}");
        Node header = block.Children.First(c => c.Name == "Block Header");
        Assert.Equal(expected.GetProperty("index").GetInt64(), header.Children.Single(c => c.Name == "Block Index").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("samples").GetInt64(), header.Children.Single(c => c.Name == "Block Samples").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("flags").GetString(), Hex(header.Children.Single(c => c.Name == "Flags").Value!, 8));
        Assert.Equal(expected.GetProperty("version").GetString(), Hex(header.Children.Single(c => c.Name == "Stream Version").Value!, 3));
        Assert.Equal(expected.GetProperty("crc").GetString(), Hex(header.Children.Single(c => c.Name == "CRC").Value!, 8));
        JsonElement total = expected.GetProperty("total");
        Assert.Equal(total.ValueKind == JsonValueKind.Null ? "unknown" : null, total.ValueKind == JsonValueKind.Null ? header.Children.Single(c => c.Name == "Total Samples").Value?.AsText() : null);
        if (total.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(total.GetInt64(), header.Children.Single(c => c.Name == "Total Samples").Value?.AsInteger());
        }

        // Blocks past the first 4,096 are listed without their sub-blocks.
        List<Node> subBlocks = block.Children.Where(c => c.Children.Any(k => k.Name == "ID")).ToList();
        if (index < 4096)
        {
            JsonElement[] listed = expected.GetProperty("sub_blocks").EnumerateArray().ToArray();
            Assert.Equal(
                listed.Take(1024).Select(s => $"{s[0].GetInt64()}:{s[1].GetInt32():X2}:{s[2].GetInt32()}"),
                subBlocks.Select(s => $"{s.Offset}:{Convert.ToInt32(s.Children.Single(c => c.Name == "ID").Value!.ToString()[2..], 16):X2}:{s.Children.Single(c => c.Name == "Size").Value!.AsInteger() * 2}"));
        }

        JsonElement checksum = expected.GetProperty("checksum");
        Node? stored = subBlocks.FirstOrDefault(s => s.Name == "Block Checksum");
        string? verdict = stored?.Children.SingleOrDefault(c => c.Name == "Data")?.Children.SingleOrDefault(c => c.Name == "IsValid")?.Value?.AsBoolean() switch { true => "valid", false => "invalid", null => null };
        if (checksum.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(verdict);
        }
        else if (stored?.Children.Any(c => c.Name == "Data" && c.Children.Count > 0) == true)
        {
            Assert.Equal(checksum.GetString(), verdict);
        }
    }

    private static string Hex(NodeValue value, int digits) => value.AsInteger()?.ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        ?? value.ToString().Replace("0x", string.Empty, StringComparison.Ordinal).ToLowerInvariant().TrimStart('0').PadLeft(digits, '0');

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
        Assert.Equal(info.GetProperty("bits_per_sample").ValueKind == JsonValueKind.Null ? null : info.GetProperty("bits_per_sample").GetInt32(), stream.BitsPerSample);
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
