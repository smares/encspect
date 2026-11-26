using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.OptimFrog;
using Encspect.Host;

namespace Encspect.Tests.Formats.OptimFrog;

// Checks every OptimFROG fixture against its manifest (import_optimfrog_fixtures.py, worked out by optimfrog_model.py, a reading of the author's
// specification apart from the analyzer, and confirmed by crosscheck_optimfrog_fixtures.py): the blocks, the main block's fields, every data block with its
// CRC-32, the MD5, the stored header and footer, the stream info, the verification and exactly the findings listed.
public class OptimFrogFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "optimfrog");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => p.EndsWith(".ofr", StringComparison.Ordinal) || p.EndsWith(".ofs", StringComparison.Ordinal) || p.EndsWith(".ofc", StringComparison.Ordinal));
                data.Add("optimfrog/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    [Fact]
    public void TheFixturesAreThere() => Assert.True(Names.Count > 50, $"Found only {Names.Count} fixtures.");

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        if (manifest.TryGetProperty("corr", out JsonElement corr))
        {
            CheckCorrection(manifest, corr, report);
            return;
        }

        Node stream = Assert.Single(report.Root.Children, n => n.Name == "OptimFROG");
        Assert.Equal(manifest.GetProperty("end").GetInt64(), stream.End);
        JsonElement[] blocks = [.. manifest.GetProperty("blocks").EnumerateArray()];
        Dictionary<long, Node> nodes = stream.Children.Where(c => c.Name.EndsWith("Block", StringComparison.Ordinal)).ToDictionary(c => c.Offset);
        Assert.Equal(blocks.Length, nodes.Count);
        foreach (JsonElement block in blocks)
        {
            long offset = block.GetProperty("offset").GetInt64();
            Node node = nodes[offset];
            Assert.Equal(block.GetProperty("end").GetInt64() - offset, node.Length);
            Assert.Equal(block.GetProperty("id").GetString(), node.Children.Single(c => c.Name == "Block ID").Value?.ToString());
            Assert.Equal(block.GetProperty("length").GetInt64(), node.Children.Single(c => c.Name == "Block Length").Value?.AsInteger());
        }

        CheckMain(manifest.GetProperty("main"), nodes[0]);
        JsonElement[] comps = [.. manifest.GetProperty("comp").EnumerateArray()];
        Node[] dataBlocks = [.. stream.Children.Where(c => c.Name == "Data Block")];
        Assert.Equal(comps.Length, dataBlocks.Length);
        for (int i = 0; i < comps.Length; i++)
        {
            CheckComp(comps[i], dataBlocks[i], $"{name}, data block {i}");
        }

        CheckMd5(manifest.GetProperty("md5"), stream);
        CheckStored(manifest.GetProperty("head"), stream, "Header Block", "WAV Header Data");
        CheckStored(manifest.GetProperty("tail"), stream, "Footer Block", "WAV Terminating Data");
        CheckStreamInfo(manifest.GetProperty("stream_info"), Assert.Single(report.Streams));
        CheckVerification(manifest.GetProperty("verification"), report.Streams.Single().Verification!);
        bool isLossy = manifest.GetProperty("lossy").GetBoolean();
        Node? lossy = stream.Children.FirstOrDefault(c => c.Name == "Lossy Part");
        Assert.Equal(isLossy, lossy is not null);
        Assert.False(report.Streams.Single().Verification!.IsLossy);
        Assert.Equal(isLossy, report.Streams.Single().Claims.Count > 0);
        if (lossy is not null)
        {
            // A consistent correction file beside the file raises the claim to High (decision 7 of docs/design/correction-files.md).
            string confidence = manifest.TryGetProperty("lossy_confidence", out JsonElement raised) ? raised.GetString()!
                : name.EndsWith(".ofs", StringComparison.Ordinal) ? "Medium" : "Low";
            Assert.Equal(confidence, lossy.Children.Single(c => c.Name == "Confidence").Value?.ToString());
        }

        CheckPairing(manifest, name, report);

        string[] expected = [.. manifest.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, report.Findings.Select(f => f.Code).Order(StringComparer.Ordinal));
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    // The pairing the manifest works out from both files (optimfrog_model.pairing), the companion's report and its tree, which equals the tree of the file analyzed alone.
    private static void CheckPairing(JsonElement manifest, string name, Report report)
    {
        StreamInfo stream = report.Streams.Single();
        if (!manifest.TryGetProperty("correction", out JsonElement expected))
        {
            Assert.Null(stream.Correction);
            Assert.Empty(report.Companions);
            return;
        }

        CorrectionPairing pairing = stream.Correction!;
        Assert.Equal(expected.GetProperty("status").GetString(), CorrectionText.Word(pairing.Status));
        Assert.Equal((expected.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("kind").GetString()!, p.GetProperty("count").GetInt64()))),
            pairing.Problems.Select(p => (CorrectionText.Word(p.Kind), p.Count)));
        Assert.Equal(CorrectionBasis.Structure, pairing.Basis);
        bool isConsistent = pairing.Status == CorrectionStatus.Consistent;
        Assert.Equal(isConsistent, manifest.GetProperty("verification").TryGetProperty("with_correction", out JsonElement with) && with.GetBoolean());
        Assert.Equal(isConsistent, stream.Verification!.IsCorrected);
        if (!manifest.TryGetProperty("companion", out JsonElement companion))
        {
            Assert.Equal(CorrectionStatus.Unchecked, pairing.Status);
            Assert.Empty(report.Companions);
            return;
        }

        Assert.Equal(Path.GetFileNameWithoutExtension(name) + ".ofc", pairing.Source);
        CompanionReport pair = Assert.Single(report.Companions);
        Assert.Equal(CompanionReport.Correction, pair.Role);
        Assert.Equal("Correction File", pair.Report.Root.Name);
        Assert.Equal(Path.GetFileNameWithoutExtension(name) + ".ofc", pair.Report.Source);
        bool hasData = pairing.Problems.All(p => p.Kind != CorrectionProblemKind.NotCorrectionData);
        Assert.Equal(hasData, pair.Report.Streams.Count == 1);
        Assert.All(pair.Report.Streams, s => Assert.True(s.IsCorrectionFile));
        Assert.DoesNotContain(pair.Report.Findings, f => f.Code == "optimfrog.correction-file");
        Assert.All(pair.Report.Findings, f => Assert.NotNull(f.Path));
        if (hasData)
        {
            using FileStream alone = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "optimfrog", companion.GetString()!));
            Report reference = Analyzer.CreateDefault().Analyze(companion.GetString()!, alone);
            Assert.Equal(Dump(reference.Root.Children.Single(c => c.Kind == NodeKind.Stream)), Dump(pair.Report.Root.Children.Single(c => c.Kind == NodeKind.Stream)));
        }
    }

    private static string Dump(Node node) =>
        $"{node.Name}={node.Value}@{node.Offset}+{node.Length}:{node.Description}[{string.Join(",", node.Children.Select(Dump))}]";

    private static void CheckCorrection(JsonElement manifest, JsonElement corr, Report report)
    {
        Node stream = Assert.Single(report.Root.Children, n => n.Name == "OptimFROG correction data");
        Node[] blocks = [.. stream.Children.Where(c => c.Name == "Correction Block")];
        JsonElement[] expected = [.. corr.EnumerateArray()];
        Assert.Equal(expected.Length, blocks.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("offset").GetInt64(), blocks[i].Offset);
            Assert.Equal(expected[i].GetProperty("size").GetInt64(), blocks[i].Length);
            Assert.Equal(expected[i].GetProperty("crc").GetInt64().ToString("x8", CultureInfo.InvariantCulture), Hex(blocks[i].Children.Single(c => c.Name == "CRC-32").Value, 8));
            Assert.Equal(expected[i].GetProperty("crc_ok").GetBoolean(), blocks[i].Children.Single(c => c.Name == "IsCrc32Valid").Value?.AsBoolean());
        }

        StreamInfo info = Assert.Single(report.Streams);
        Assert.Equal(manifest.GetProperty("stream_info").GetProperty("codec").GetString(), info.Codec);
        CheckVerification(manifest.GetProperty("verification"), info.Verification!);
        Assert.Equal(manifest.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).Order(StringComparer.Ordinal), report.Findings.Select(f => f.Code).Order(StringComparer.Ordinal));
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

    private static string Hex(NodeValue? value, int digits) => ((long)value!.AsInteger()!).ToString("x" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static void CheckMain(JsonElement main, Node block)
    {
        Node Field(string field) => block.Children.Single(c => c.Name == field);
        Assert.Equal(main.GetProperty("samples").GetInt64(), Field("Total Samples").Value?.AsInteger());
        Assert.Equal(Layout.SampleTypes[main.GetProperty("sample_type").GetInt32()], Field("Sample Type").Value?.ToString());
        Assert.Equal(Layout.ChannelConfigurations[main.GetProperty("channel_config").GetInt32()], Field("Channel Configuration").Value?.ToString());
        Assert.Equal(main.GetProperty("sample_rate").GetInt64(), Field("Sample Rate").Value?.AsInteger());
        Node encoder = Field("Encoder ID");
        Assert.Equal(main.GetProperty("encoder_id").GetInt64().ToString("x4", CultureInfo.InvariantCulture), Hex(encoder.Value, 4));
        Assert.Equal(Layout.VersionText(main.GetProperty("version").GetInt32()), encoder.Children.Single(c => c.Name == "Encoder Version").Value?.ToString());
        Assert.Equal(Layout.BuildSystems[main.GetProperty("system").GetInt32()], encoder.Children.Single(c => c.Name == "Build System").Value?.ToString());
        Node compression = Field("Compression");
        Assert.Equal(Layout.Modes[main.GetProperty("mode").GetInt32()], compression.Children.Single(c => c.Name == "Mode").Value?.ToString());
        Assert.Equal(Layout.Speedups[main.GetProperty("speedup").GetInt32()], compression.Children.Single(c => c.Name == "Speedup").Value?.ToString());
        if (main.TryGetProperty("min_decoder_version", out JsonElement minimum))
        {
            Assert.Equal(Layout.VersionText(minimum.GetInt32()), Field("Minimum Decoder Version").Value?.ToString());
        }
        else
        {
            Assert.DoesNotContain(block.Children, c => c.Name == "Minimum Decoder Version");
        }
    }

    private static void CheckComp(JsonElement comp, Node block, string where)
    {
        Node Field(string field) => block.Children.Single(c => c.Name == field);
        Assert.True(comp.GetProperty("offset").GetInt64() == block.Offset, $"{where} is at {block.Offset}");
        Assert.Equal(comp.GetProperty("size").GetInt64(), block.Length);
        Assert.Equal(comp.GetProperty("crc").GetInt64().ToString("x8", CultureInfo.InvariantCulture), Hex(Field("CRC-32").Value, 8));
        Assert.Equal(comp.GetProperty("crc_ok").GetBoolean(), Field("IsCrc32Valid").Value?.AsBoolean());
        Assert.Equal(comp.GetProperty("samples").GetInt64(), Field("Samples").Value?.AsInteger());
        Assert.Equal(comp.GetProperty("reserved").GetInt64(), Field("Reserved").Value?.AsInteger());
        Assert.Equal(comp.GetProperty("encoder_id").GetInt64(), Field("Encoder ID").Value?.AsInteger());
    }

    private static void CheckMd5(JsonElement md5, Node stream)
    {
        Node? block = stream.Children.FirstOrDefault(c => c.Name == "MD5 Block");
        if (md5.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(block);
        }
        else
        {
            Assert.Equal(md5.GetString(), block!.Children.Single(c => c.Name == "MD5").Value?.ToString());
        }
    }

    private static void CheckStored(JsonElement expected, Node stream, string blockName, string dataName)
    {
        Node? block = stream.Children.FirstOrDefault(c => c.Name == blockName);
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(block);
            return;
        }

        Assert.Equal(expected.GetProperty("length").GetInt64(), block!.Children.Single(c => c.Name == "Block Length").Value?.AsInteger());
        if (expected.GetProperty("length").GetInt64() == 0)
        {
            Assert.DoesNotContain(block.Children, c => c.Name == dataName);
            return;
        }

        Node data = block.Children.Single(c => c.Name == dataName);
        Assert.Equal(expected.GetProperty("offset").GetInt64(), data.Offset);
        string[] chunks = [.. expected.GetProperty("chunks").EnumerateArray().Select(c => c.GetString()!)];
        Node? list = data.Children.FirstOrDefault(c => c.Name == "Chunks");
        Assert.Equal(chunks, list is null ? [] : list.Children.Select(c => c.Children[0].Value!.ToString()!));
        if (expected.TryGetProperty("fmt", out JsonElement fmt) && fmt.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(fmt.GetProperty("channels").GetInt64(), data.Children.Single(c => c.Name == "Channels").Value?.AsInteger());
        }
    }

    private static void CheckStreamInfo(JsonElement expected, StreamInfo info)
    {
        Assert.Equal(expected.GetProperty("codec").GetString(), info.Codec);
        Assert.Equal(expected.GetProperty("sample_rate").GetInt32(), info.SampleRate);
        Assert.Equal(expected.GetProperty("channels").GetInt32(), info.Channels);
        Assert.Equal(expected.GetProperty("bits_per_sample").GetInt32(), info.BitsPerSample);
        Assert.Equal(expected.GetProperty("samples").GetInt64(), info.Samples);
        Assert.Equal(expected.GetProperty("encoder").GetString(), info.Encoder);
    }

    private static void CheckVerification(JsonElement expected, StreamVerification verification)
    {
        Assert.Equal(DecodeStatus.NoDecoder, verification.Decoding);
        foreach (JsonElement checksum in expected.GetProperty("checksums").EnumerateArray())
        {
            ChecksumTally tally = verification.Checksums.Single(c => c.Name == checksum.GetProperty("name").GetString());
            Assert.Equal(checksum.GetProperty("stored").GetInt64(), tally.Stored);
            Assert.Equal(checksum.GetProperty("checked").GetInt64(), tally.Checked);
            Assert.Equal(checksum.GetProperty("failed").GetInt64(), tally.Failed);
            if (checksum.TryGetProperty("covers_all", out JsonElement coversAll))
            {
                Assert.Equal(coversAll.GetBoolean(), tally.CoversAllAudio);
            }
        }
    }
}
