using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every Musepack fixture against its manifest (make_musepack_fixtures.py,
// make_external_musepack_fixtures.py, both worked out by musepack_model.py and confirmed by
// crosscheck_musepack_fixtures.py): the top-level regions, the header fields, every SV4, SV5 and SV7
// frame with its length, used subbands, mid/side subbands and noise channels, every SV8 packet with
// its key and bytes, the stream info and exactly the findings listed.
public class MusepackFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string manifest in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "musepack"), "*.expected.json").Order(StringComparer.Ordinal))
            {
                data.Add("musepack/" + Path.GetFileName(manifest)[..^".expected.json".Length] + ".mpc");
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
        Assert.Equal("Musepack", report.Root.Value?.ToString());

        Node stream = report.Root.Children.Single(n => n.Kind == NodeKind.Stream);
        if (manifest.TryGetProperty("header", out JsonElement header))
        {
            Node fields = stream.Children.FirstOrDefault(c => c.Name == "Header") ?? stream.Children.Single(c => c.Name == "Stream Header");
            foreach (JsonProperty field in header.EnumerateObject())
            {
                Assert.True(field.Value.GetString() == fields.Children.Single(c => c.Name == field.Name).Value?.ToString(),
                    $"Header field {field.Name} is \"{fields.Children.Single(c => c.Name == field.Name).Value}\", expected \"{field.Value.GetString()}\"");
            }
        }

        int version = manifest.GetProperty("version").GetInt32();
        if (version is 4 or 5 or 7 && stream.Children.Any(c => c.Kind == NodeKind.Frame))
        {
            List<Node> frames = stream.Children.Where(c => c.Kind == NodeKind.Frame && c.Value?.ToString() is not "cut short" and not "not decoded").ToList();
            JsonElement[] expected = manifest.GetProperty("frames").EnumerateArray().ToArray();
            int whole = frames.Count(f => f.Children.Any(c => c.Name == "Frame Data"));
            Assert.True(expected.Length <= frames.Count, $"{expected.Length} frames expected, {frames.Count} found");
            for (int i = 0; i < expected.Length; i++)
            {
                JsonElement[] item = expected[i].EnumerateArray().ToArray();
                Node frame = frames[i];
                Node data = frame.Children.Single(c => c.Name == "Frame Data");
                string actual = $"{frame.Children.Single(c => c.Name == "Frame Length").Value?.AsInteger()} {data.Children.Single(c => c.Name == "Used Subbands").Value} "
                    + $"{Mask(data.Children.Single(c => c.Name == "Mid/Side Subbands").Value!.ToString())} {data.Children.SingleOrDefault(c => c.Name == "Noise-Substituted Channels")?.Value?.ToString() ?? "0"}";
                Assert.Equal($"{item[0].GetInt64()} {item[1].GetInt32()} {item[2].GetString()} {item[3].GetInt32()}", actual);
            }

            Assert.True(whole >= expected.Length);
        }

        if (version == 8)
        {
            List<Node> packets = stream.Children.Where(c => c.Children.Any(k => k.Name == "Key")).ToList();
            Assert.Equal(
                manifest.GetProperty("packets").EnumerateArray().Select(p => $"{p[2].GetString()}@{p[0].GetInt64()}+{p[1].GetInt64()}"),
                packets.Select(p => $"{p.Children.Single(k => k.Name == "Key").Value}@{p.Offset}+{p.Length}"));
            Assert.All(packets.Where(p => p.Name == "Audio Packet"), p => Assert.Equal(NodeKind.Frame, p.Kind));
        }

        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
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

    // A stream of version 4 to 6 has no magic number; no fixture of another format passes for one.
    [Fact]
    public void OldStreamsAreNotFoundInOtherFormats()
    {
        var parser = new Encspect.Formats.Musepack.MusepackParser();
        string musepack = Path.Combine(Fixtures.FixtureDirectory, "musepack");
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(musepack, StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            var reader = new ByteReader(stream);
            if (reader.Length >= 4 && !reader.MatchesAt(0, "MP+"u8) && !reader.MatchesAt(0, "MPCK"u8))
            {
                Assert.True(parser.Sniff(new SniffContext(reader)) == 0, path);
            }
        }
    }

    // The SV7 encoder table against what each encoder at hand wrote (sv7-encoders.json, written by
    // make_external_musepack_fixtures.py): the fields the table reads, written into an SV7 stream,
    // name that encoder with high confidence.
    [Fact]
    public void Sv7EncoderTableMatchesEncodes()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack/external-mppenc-standard.mpc"));
        using JsonDocument rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, "musepack/sv7-encoders.json")));
        foreach (JsonElement row in rows.RootElement.EnumerateArray())
        {
            byte[] data = (byte[])stream.Clone();
            WriteBits(data, 72, 4, row.GetProperty("profile").GetInt32());
            WriteBits(data, 80, 16, row.GetProperty("max_level").GetInt32());
            WriteBits(data, 160, 1, row.GetProperty("gapless").GetBoolean() ? 1 : 0);
            WriteBits(data, 161, 11, row.GetProperty("last").GetInt32());
            WriteBits(data, 192, 8, row.GetProperty("encoder").GetInt32());
            StreamInfo info = Encspect.Host.Analyzer.CreateDefault().Analyze("encoder.mpc", new MemoryStream(data)).Streams.Single();
            Assert.True(row.GetProperty("detected").GetString() == info.Encoder, $"{row.GetProperty("build").GetString()}: {info.Encoder}");
            Assert.Equal("High", info.EncoderConfidence?.ToString());
        }
    }

    // Writes value into count bits at bit of the 32-bit little-endian words, read from their most
    // significant bit.
    private static void WriteBits(byte[] data, int bit, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            int position = bit + i;
            int index = ((position >> 5) * 4) + 3 - ((position & 31) >> 3);
            int mask = 0x80 >> (position & 7);
            data[index] = (byte)(((value >> (count - 1 - i)) & 1) == 1 ? data[index] | mask : data[index] & ~mask);
        }
    }

    // A truncated copy of every encoder-made fixture, at every 97th byte, yields a report without a
    // parser failure, and its findings and streams point into the tree.
    [Theory]
    [InlineData("musepack/external-mppenc-extra-frame.mpc")]
    [InlineData("musepack/external-mpcenc-packets.mpc")]
    [InlineData("musepack/external-mpcenc-chapters.mpc")]
    [InlineData("musepack/external-mppsv5-vbr.mpc")]
    [InlineData("musepack/external-mppsv4-superframes.mpc")]
    public void TruncatedCopiesYieldFindings(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        for (int length = 1; length < data.Length; length += 97)
        {
            Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("cut.mpc", new MemoryStream(data[..length]));
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
            Assert.All(report.Findings, f => Assert.NotNull(f.Path));
            Assert.All(report.Streams, s => Assert.All(s.NodePaths, p => Assert.NotNull(p)));
            TestFiles.CheckInsideParents(report.Root);
        }
    }

    // The mid/side subbands as the manifest writes them: a hexadecimal mask.
    private static string Mask(string list)
    {
        if (list == "none")
        {
            return "0";
        }

        uint mask = 0;
        foreach (string part in list.Split(", "))
        {
            string[] range = part.Split(" to ");
            int first = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
            int last = int.Parse(range[^1], System.Globalization.CultureInfo.InvariantCulture);
            for (int band = first; band <= last; band++)
            {
                mask |= 1u << band;
            }
        }

        return mask.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
    {
        JsonElement info = expected.EnumerateArray().Single();
        if (streams.Count == 0)
        {
            Assert.Equal(0, info.GetProperty("samples").GetInt64());
            return;
        }

        StreamInfo stream = streams.Single();
        Assert.Equal(info.GetProperty("codec").GetString(), stream.Codec);
        Assert.Equal(info.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
        Assert.Equal(info.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
        if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 9);
        }

        Assert.Equal(stream.Channels switch { 1 => ["front center"], 2 => ["front left", "front right"], _ => Array.Empty<string>() },
            stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
        if (info.TryGetProperty("encoder", out JsonElement encoder))
        {
            Assert.Equal(encoder.ValueKind == JsonValueKind.Null ? null : encoder.GetString(), stream.Encoder);
            if (info.TryGetProperty("encoder_confidence", out JsonElement confidence))
            {
                Assert.Equal(confidence.GetString(), stream.EncoderConfidence?.ToString());
            }
        }
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
