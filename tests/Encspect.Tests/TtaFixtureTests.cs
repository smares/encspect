using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every TTA fixture against its manifest (make_external_tta_fixtures.py, make_tta_fixtures.py, both worked out by tta_model.py, a reading of
// the TTA authors' format description apart from the analyzer, and confirmed by crosscheck_tta_fixtures.py): the header, the seek table, every
// frame with its CRC-32, the bytes around the stream, the stream info, the verification and exactly the findings listed.
public class TtaFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "tta");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("tta/" + Path.GetFileName(file));
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
        JsonElement[] regions = [.. manifest.GetProperty("regions").EnumerateArray()];
        Assert.Equal(
            regions.Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"),
            report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}").Take(regions.Length));

        Node? stream = report.Root.Children.FirstOrDefault(n => n.Name == "TTA");
        JsonElement header = manifest.GetProperty("header");
        if (stream is not null && header.ValueKind != JsonValueKind.Null)
        {
            CheckHeader(header, stream.Children.Single(c => c.Name == "Header"));
            JsonElement table = manifest.GetProperty("table");
            if (table.ValueKind == JsonValueKind.Null)
            {
                Assert.DoesNotContain(stream.Children, c => c.Name == "Seek Table");
            }
            else
            {
                CheckTable(table, stream.Children.Single(c => c.Name == "Seek Table"));
            }

            long streamEnd = stream.End;
            JsonElement[] expectedFrames = [.. manifest.GetProperty("frames").EnumerateArray().Where(f => !f.GetProperty("status").EnumerateArray().Any(s => s.GetString() == "missing"))];
            List<Node> frames = [.. stream.Children.Where(c => c.Name == "Frame")];
            Assert.Equal(expectedFrames.Length, frames.Count);
            for (int i = 0; i < expectedFrames.Length; i++)
            {
                CheckFrame(expectedFrames[i], frames[i], streamEnd, header.GetProperty("rate").GetInt64(), header.GetProperty("samples").GetInt64(), i, $"{name}, frame {i}");
            }
        }
        else if (stream is not null)
        {
            Assert.DoesNotContain(stream.Children, c => c.Name is "Header" && c.Children.Count > 0);
        }

        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        JsonElement verification = manifest.GetProperty("verification");
        if (report.Streams.Count > 0)
        {
            FlacFixtureTests.CheckVerification(verification.GetProperty("default"), report.Streams.Single().Verification, DecodeStatus.NoDecoder);
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

    private static string Hex(NodeValue value) => value.AsInteger()?.ToString("x8", CultureInfo.InvariantCulture) ?? value.ToString();

    private static void CheckHeader(JsonElement expected, Node header)
    {
        Node Field(string field) => header.Children.Single(c => c.Name == field);
        Assert.Equal(expected.GetProperty("format").GetInt64(), Field("Audio Format").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("channels").GetInt64(), Field("Channels").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("bits").GetInt64(), Field("Bits Per Sample").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("rate").GetInt64(), Field("Sample Rate").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("samples").GetInt64(), Field("Samples").Value?.AsInteger());
        Assert.Equal(expected.GetProperty("crc").GetString(), Hex(Field("CRC-32").Value!));
        Assert.Equal(expected.GetProperty("crc_ok").GetBoolean(), Field("IsCrc32Valid").Value?.AsBoolean());
        Assert.Equal(22, header.Length);
    }

    private static void CheckTable(JsonElement expected, Node table)
    {
        Assert.Equal(expected.GetProperty("offset").GetInt64(), table.Offset);
        JsonElement[] entries = [.. expected.GetProperty("entries").EnumerateArray()];
        Node points = table.Children.Single(c => c.Name == "Seek Points");
        Assert.Equal(entries.Length, points.Children.Count);
        for (int i = 0; i < entries.Length; i++)
        {
            Node point = points.Children[i];
            Assert.Equal(entries[i].GetInt64(), point.Value?.AsInteger());
            Assert.Equal(expected.GetProperty("offset").GetInt64() + (4L * i), point.Offset);
        }

        JsonElement crc = expected.GetProperty("crc");
        Node? stored = table.Children.FirstOrDefault(c => c.Name == "CRC-32");
        if (crc.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stored);
        }
        else
        {
            Assert.Equal(crc.GetString(), Hex(stored!.Value!));
            Assert.Equal(expected.GetProperty("crc_ok").GetBoolean(), table.Children.Single(c => c.Name == "IsCrc32Valid").Value?.AsBoolean());
        }
    }

    private static void CheckFrame(JsonElement expected, Node frame, long streamEnd, long rate, long samples, int index, string where)
    {
        long offset = expected.GetProperty("offset").GetInt64();
        long size = expected.GetProperty("size").GetInt64();
        string[] status = [.. expected.GetProperty("status").EnumerateArray().Select(s => s.GetString()!)];
        long length = Math.Min(size, streamEnd - offset);
        Assert.True(offset == frame.Offset && length == frame.Length, $"{where} is at {frame.Offset}+{frame.Length}, expected {offset}+{length}");
        long frameLength = rate * 256 / 245;
        long count = frameLength == 0 ? 0 : (samples + frameLength - 1) / frameLength;
        long held = index < count - 1 ? frameLength : index == count - 1 ? samples - ((count - 1) * frameLength) : frameLength;
        Assert.Equal(held, frame.Value?.AsInteger());
        Node? crc = frame.Children.FirstOrDefault(c => c.Name == "CRC-32");
        if (status.Contains("cut") || status.Contains("small"))
        {
            Assert.Null(crc);
            return;
        }

        Assert.Equal(expected.GetProperty("crc").GetString(), Hex(crc!.Value!));
        Assert.Equal(!status.Contains("crc"), frame.Children.Single(c => c.Name == "IsCrc32Valid").Value?.AsBoolean());
        Assert.Equal(size - 4, frame.Children.FirstOrDefault(c => c.Name == "Frame Data")?.Length ?? 0);
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
