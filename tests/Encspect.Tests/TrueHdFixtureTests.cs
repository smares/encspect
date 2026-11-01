using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every Dolby TrueHD and MLP fixture against its manifest (make_external_truehd_fixtures.py and make_truehd_fixtures.py, worked out by
// truehd_model.py apart from the analyzer and confirmed by crosscheck_truehd_fixtures.py): the stream's units and skipped stretches with their
// places, the major syncs, restart headers and substream checks of the units that were read, the stream info, the verification without decoding and
// exactly the findings listed.
public class TrueHdFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "truehd");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("truehd/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    [Fact]
    public void TheFixturesCoverBothFormatsAndEveryRate()
    {
        string[] names = [.. Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "truehd")).Where(p => p.EndsWith(".thd", StringComparison.Ordinal) || p.EndsWith(".mlp", StringComparison.Ordinal)).Select(Path.GetFileName)!];
        Assert.True(names.Count(n => n.EndsWith(".thd", StringComparison.Ordinal)) >= 20);
        Assert.True(names.Count(n => n.EndsWith(".mlp", StringComparison.Ordinal)) >= 15);
        foreach (string rate in new[] { "44k", "48k", "88k", "96k", "176k", "192k" })
        {
            Assert.Contains(names, n => n.Contains($"-{rate}-", StringComparison.Ordinal));
        }
    }

    // The runs of the bitrate series state what each codes, so a front end can place a time in the stream: together they code the stream's
    // samples, and a run holds no unit that codes nothing but its last.
    [Theory]
    [MemberData(nameof(Names))]
    public void TheRunsCodeTheSamplesOfTheStream(string name)
    {
        StreamInfo stream = Fixtures.AnalyzeMpeg(name).Streams[0];
        Series? bitrate = stream.Series.FirstOrDefault(s => s.Name == "Bitrate");
        if (bitrate is null)
        {
            return;
        }

        FrameRuns runs = bitrate.Runs!;
        Assert.Equal(stream.Samples, runs.Samples.ToArray().Sum());
        Assert.Equal(bitrate.X.Length, runs.FirstFrames.Length);
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
        Assert.Equal("TrueHD", report.Root.Value?.ToString());

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        Assert.Equal(manifest.GetProperty("format").GetString(), stream.Name);
        List<Node> entries = [.. stream.Children.Where(c => c.Name is "Access Unit" or "Skipped Data")];
        JsonElement[] expected = [.. manifest.GetProperty("entries").EnumerateArray()];
        Assert.Equal(expected.Length, entries.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            CheckEntry(expected[i], entries[i], i, name);
        }

        CheckSyncs(manifest.GetProperty("syncs"), entries, expected, name);
        CheckRestarts(manifest.GetProperty("restarts"), entries, name);
        CheckSubstreamChecks(manifest, entries, expected, name);
        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        if (report.Streams.Count > 0)
        {
            FlacFixtureTests.CheckVerification(manifest.GetProperty("verification").GetProperty("default"), report.Streams.Single().Verification, DecodeStatus.NotRequested);
        }

        CheckFindings(manifest.GetProperty("findings"), report.Findings);
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

    private static void CheckEntry(JsonElement expected, Node node, int index, string name)
    {
        string where = $"{name}, entry {index}";
        string kind = expected[0].GetString()!;
        Assert.True(expected[1].GetInt64() == node.Offset && expected[2].GetInt64() == node.Length,
            $"{where} is at {node.Offset}+{node.Length}, expected {expected[1].GetInt64()}+{expected[2].GetInt64()}");
        Assert.Equal(kind == "skipped" ? "Skipped Data" : "Access Unit", node.Name);
        if (kind == "unit")
        {
            Assert.Equal(expected[3].GetInt32() == 1, node.Children.Any(c => c.Name == "Major Sync"));
            string description = expected[4].GetString() switch
            {
                "leading" or "after-damage" or "after-unsupported" => Formats.TrueHd.Explain.UnitBeforeSync,
                "cut" => Formats.TrueHd.Explain.CutUnit,
                _ => Formats.TrueHd.Explain.AccessUnit,
            };
            Assert.True(description == node.Description, $"{where} is {expected[4].GetString()}, but its description reads \"{node.Description}\"");
        }
    }

    private static bool? Boolean(Node node, string name) => node.Children.SingleOrDefault(c => c.Name == name)?.Value?.AsBoolean();

    private static void CheckSyncs(JsonElement syncs, List<Node> entries, JsonElement[] expected, string name)
    {
        foreach (JsonElement sync in syncs.EnumerateArray())
        {
            int unit = sync.GetProperty("unit").GetInt32();
            Node? node = entries[unit].Children.FirstOrDefault(c => c.Name == "Major Sync");
            Assert.NotNull(node);
            if (!node.HasChildren)
            {
                continue;
            }

            string where = $"{name}, the major sync of unit {unit}";
            Node crc = node.Children.Single(c => c.Name == "CRC");
            Assert.True(sync.GetProperty("crc_ok").GetBoolean() == Boolean(crc, "IsValid"), $"{where}: the CRC verdict differs");
            if (sync.GetProperty("usable").GetBoolean())
            {
                Node info = node.Children.Single(c => c.Name == "Format Info");
                Assert.Equal(sync.GetProperty("rate").GetInt32(), info.Children.First(c => c.Name is "Sampling Rate" or "Group 1 Sampling Rate").Value?.AsInteger());
                Assert.Equal(sync.GetProperty("substreams").GetInt32(), node.Children.Single(c => c.Name == "Substream Count").Value?.AsInteger());
                Assert.Equal(sync.GetProperty("signature").GetInt32(), (int)node.Children.Single(c => c.Name == "Signature").Value!.AsUnsigned()!.Value);
                Assert.Equal(sync.GetProperty("info").GetInt32(), (int)node.Children.Single(c => c.Name == "Substream Info").Value!.AsUnsigned()!.Value);
                if (sync.GetProperty("mlp").GetBoolean())
                {
                    Assert.Equal(sync.GetProperty("arrangement").GetInt32(), info.Children.Single(c => c.Name == "Channel Arrangement").Value?.AsInteger());
                }
                else
                {
                    Assert.Equal(sync.GetProperty("assign6").GetInt32(), (int)info.Children.Single(c => c.Name == "6-Channel Assignment").Value!.AsUnsigned()!.Value);
                    Assert.Equal(sync.GetProperty("assign8").GetInt32(), (int)info.Children.Single(c => c.Name == "8-Channel Assignment").Value!.AsUnsigned()!.Value);
                }
            }
        }
    }

    private static Node? Substream(Node unit, int k) => unit.Children.FirstOrDefault(c => c.Name == $"Substream {k}");

    private static void CheckRestarts(JsonElement restarts, List<Node> entries, string name)
    {
        foreach (JsonElement restart in restarts.EnumerateArray())
        {
            int unit = restart.GetProperty("unit").GetInt32();
            int k = restart.GetProperty("sub").GetInt32();
            string where = $"{name}, the restart header of substream {k} in unit {unit}";
            Node? substream = Substream(entries[unit], k);
            Assert.NotNull(substream);
            Node header = substream.Children.First(c => c.Name == "Block 0").Children.Single(c => c.Name == "Restart Header");
            Assert.True(restart.GetProperty("crc_ok").GetBoolean() == Boolean(header.Children.Single(c => c.Name == "CRC"), "IsValid"), $"{where}: the CRC verdict differs");
            Assert.Equal(restart.GetProperty("lossless").GetInt32(), (int)header.Children.Single(c => c.Name == "Lossless Check").Value!.AsUnsigned()!.Value);
            Assert.Equal(restart.GetProperty("bit").GetInt32(), header.BitOffset is { } bit ? bit + (int)(header.Offset - substream.Offset) * 8 : (int)(header.Offset - substream.Offset) * 8);
        }
    }

    private static void CheckSubstreamChecks(JsonElement manifest, List<Node> entries, JsonElement[] expected, string name)
    {
        var failing = new HashSet<(int, int)>();
        foreach (JsonElement failure in manifest.GetProperty("failures").EnumerateArray())
        {
            int unit = failure.GetProperty("unit").GetInt32();
            int k = failure.GetProperty("sub").GetInt32();
            failing.Add((unit, k));
            Node substream = Substream(entries[unit], k)!;
            Assert.NotNull(substream);
            Assert.Equal(failure.GetProperty("parity_ok").ValueKind == JsonValueKind.Null ? null : failure.GetProperty("parity_ok").GetBoolean(), Boolean(substream.Children.Single(c => c.Name == "Parity Byte"), "IsValid"));
            Assert.Equal(failure.GetProperty("crc_ok").ValueKind == JsonValueKind.Null ? null : failure.GetProperty("crc_ok").GetBoolean(), Boolean(substream.Children.Single(c => c.Name == "CRC-8"), "IsValid"));
        }

        // Every other substream of a unit that was read, with check data, has checks that hold.
        var withoutCheck = manifest.GetProperty("no_check_data").EnumerateArray().Select(e => (e[0].GetInt32(), e[1].GetInt32())).ToHashSet();
        for (int unit = 0; unit < entries.Count; unit++)
        {
            if (expected[unit][0].GetString() != "unit" || expected[unit][4].GetString() != "read")
            {
                continue;
            }

            foreach (Node substream in entries[unit].Children.Where(c => c.Name.StartsWith("Substream ", StringComparison.Ordinal) && c.Name.Length == "Substream 0".Length))
            {
                int k = substream.Name[^1] - '0';
                if (k >= 3 || failing.Contains((unit, k)))
                {
                    continue;
                }

                Node? parity = substream.Children.FirstOrDefault(c => c.Name == "Parity Byte");
                Assert.Equal(!withoutCheck.Contains((unit, k)), parity is not null);
                if (parity is not null)
                {
                    Assert.True(Boolean(parity, "IsValid"), $"{name}: the parity byte of substream {k} in unit {unit} should hold");
                    Assert.True(Boolean(substream.Children.Single(c => c.Name == "CRC-8"), "IsValid"), $"{name}: the CRC-8 of substream {k} in unit {unit} should hold");
                }
            }
        }
    }

    internal static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
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
        if (info.TryGetProperty("average_bitrate", out JsonElement bitrate))
        {
            Assert.Equal(bitrate.GetDouble(), stream.AverageBitrate!.Value, 6);
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
