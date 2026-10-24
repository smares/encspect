using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every Monkey's Audio fixture against its manifest (make_external_monkeys_audio_fixtures.py and make_monkeys_audio_fixtures.py, both worked
// out by monkeys_audio_model.py and confirmed by crosscheck_monkeys_audio_fixtures.py): the top-level regions, the descriptor and header fields, every
// frame with its place and stored CRC, the stream info, the verification by the rule of docs/design/decoders.md and exactly the findings listed.
public class MonkeysAudioFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "monkeys-audio");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("monkeys-audio/" + Path.GetFileName(file));
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
        Assert.Equal("Monkey's Audio", report.Root.Value?.ToString());

        if (manifest.GetProperty("regions").GetArrayLength() == 0)
        {
            // Cut inside the descriptor or the header: no stream node at all.
            Assert.Empty(report.Streams);
            CheckFindings(manifest.GetProperty("findings"), report.Findings);
            return;
        }

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        CheckStructure(manifest, stream, name);
        List<Node> frames = [.. stream.Children.Where(c => c.Name == "Frame")];
        JsonElement[] expected = [.. manifest.GetProperty("frames").EnumerateArray()];
        Assert.True(frames.Count >= expected.Length, $"{name} lists {frames.Count} frames, expected at least {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            CheckFrame(expected[i], frames[i], name);
        }

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

    private static void CheckStructure(JsonElement manifest, Node stream, string name)
    {
        if (!manifest.TryGetProperty("descriptor", out JsonElement descriptor))
        {
            // Files whose structure the model does not work out: cut inside the descriptor or header, and the format before version 3.98.
            Assert.DoesNotContain(stream.Children, c => c.Name == "Descriptor");
            return;
        }

        JsonElement header = manifest.GetProperty("header");
        Node? node = stream.Children.FirstOrDefault(c => c.Name == "Descriptor");
        if (node is not null)
        {
            string Text(string field) => node.Children.Single(c => c.Name == field).Value?.ToString() ?? string.Empty;
            long Integer(string field) => node.Children.Single(c => c.Name == field).Value!.AsInteger()!.Value;
            Assert.Equal(Version(descriptor.GetProperty("version").GetInt32()), Text("File Version"));
            Assert.Equal(descriptor.GetProperty("descriptor_bytes").GetInt64(), Integer("Descriptor Bytes"));
            Assert.Equal(descriptor.GetProperty("header_bytes").GetInt64(), Integer("Header Bytes"));
            Assert.Equal(descriptor.GetProperty("seek_table_bytes").GetInt64(), Integer("Seek Table Bytes"));
            Assert.Equal(descriptor.GetProperty("header_data_bytes").GetInt64(), Integer("WAV Header Data Bytes"));
            Assert.Equal(descriptor.GetProperty("frame_data_bytes").GetInt64(), Integer("Frame Data Bytes"));
            Assert.Equal(descriptor.GetProperty("terminating_bytes").GetInt64(), Integer("WAV Terminating Data Bytes"));
            Assert.Equal(descriptor.GetProperty("md5").GetString(), Text("MD5"));
            JsonElement computed = descriptor.GetProperty("md5_computed");
            if (computed.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(computed.GetString(), Text("Computed MD5"));
                Assert.Equal(computed.GetString() == Text("MD5"), node.Children.Single(c => c.Name == "IsMd5Valid").Value!.AsBoolean());
            }
            else
            {
                Assert.DoesNotContain(node.Children, c => c.Name == "Computed MD5");
            }
        }

        Node? headerNode = stream.Children.FirstOrDefault(c => c.Name == "Header");
        if (headerNode is not null)
        {
            long Integer(string field) => headerNode.Children.Single(c => c.Name == field).Value!.AsInteger()!.Value;
            Assert.Equal(header.GetProperty("level").GetInt64(), Convert.ToInt64(headerNode.Children.Single(c => c.Name == "Compression Level").Value!.ToString().Split('(', ')')[1], CultureInfo.InvariantCulture));
            Assert.Equal(header.GetProperty("flags").GetInt64(), headerNode.Children.Single(c => c.Name == "Format Flags").Value!.AsInteger());
            Assert.Equal(header.GetProperty("blocks_per_frame").GetInt64(), Integer("Blocks Per Frame"));
            Assert.Equal(header.GetProperty("final_frame_blocks").GetInt64(), Integer("Final Frame Blocks"));
            Assert.Equal(header.GetProperty("total_frames").GetInt64(), Integer("Total Frames"));
            Assert.Equal(header.GetProperty("bits_per_sample").GetInt64(), Integer("Bits Per Sample"));
            Assert.Equal(header.GetProperty("channels").GetInt64(), Integer("Channels"));
            Assert.Equal(header.GetProperty("sample_rate").GetInt64(), Integer("Sample Rate"));
        }

        Node? seek = stream.Children.FirstOrDefault(c => c.Name == "Seek Table");
        Assert.Equal(manifest.GetProperty("seek_entries").GetInt64(), seek?.Children.Count ?? 0);
        Assert.True(node is not null || headerNode is not null || manifest.GetProperty("frames").GetArrayLength() == 0, $"{name} shows neither descriptor nor header");
    }

    private static string Version(int version) => $"{version / 1000}.{version % 1000 / 10:D2}";

    private static void CheckFrame(JsonElement expected, Node frame, string name)
    {
        string where = $"{name}, frame {expected.GetProperty("index").GetInt32()}";
        Assert.True(expected.GetProperty("offset").GetInt64() == frame.Offset && expected.GetProperty("length").GetInt64() == frame.Length,
            $"{where} is at {frame.Offset}+{frame.Length}, expected {expected.GetProperty("offset").GetInt64()}+{expected.GetProperty("length").GetInt64()}");
        Assert.Equal(expected.GetProperty("samples").GetInt64(), frame.Value?.AsInteger());
        Node? crc = frame.Children.FirstOrDefault(c => c.Name == "Frame CRC");
        if (crc is not null && expected.GetProperty("crc").ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expected.GetProperty("crc").GetString(), "0x" + crc.Value!.AsInteger()!.Value.ToString("X8", CultureInfo.InvariantCulture));
        }

        Node? special = frame.Children.FirstOrDefault(c => c.Name == "Special Frame Codes");
        JsonElement codes = expected.GetProperty("special");
        Assert.Equal(codes.ValueKind == JsonValueKind.Null, special is null);
    }

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
        Assert.Equal(info.GetProperty("channel_mode").GetString(), stream.ChannelMode);
        Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
        Assert.Equal(info.GetProperty("bits_per_sample").ValueKind == JsonValueKind.Null ? null : info.GetProperty("bits_per_sample").GetInt32(), stream.BitsPerSample);
        Assert.Equal(info.GetProperty("encoder").GetString(), stream.Encoder);
        if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 6);
        }

        JsonElement layout = info.GetProperty("layout");
        Assert.Equal(layout.ValueKind == JsonValueKind.Null ? [] : layout.EnumerateArray().Select(s => s.GetString()), stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var remaining = findings.ToList();
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.TryGetProperty("contains", out JsonElement text) ? text.GetString()! : string.Empty;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"))}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {string.Join("; ", remaining.Select(f => $"{f.Code}: {f.Message}"))}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }
}
