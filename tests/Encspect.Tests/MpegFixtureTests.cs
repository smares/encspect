using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every MPEG fixture against its manifest. Hand-built fixtures list every frame, gap and
// finding; encoder-made ones list the encoder settings and the frames ffprobe found.
public class MpegFixtureTests
{
    public static TheoryData<string> Names => Fixtures.MpegFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string fileName)
    {
        Report report = Fixtures.AnalyzeMpeg(fileName);
        JsonElement manifest = Fixtures.LoadMpegManifest(fileName);

        if (manifest.TryGetProperty("encoder", out JsonElement encoder))
        {
            CheckEncoded(manifest, encoder, report);
        }
        else
        {
            CheckHandBuilt(manifest, report);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string fileName)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(fileName));
        string path = Path.Combine(Fixtures.MpegDirectory, Path.GetFileNameWithoutExtension(fileName) + ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static void CheckHandBuilt(JsonElement manifest, Report report)
    {
        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);

        string expectedRegions = string.Join(", ", manifest.GetProperty("regions").EnumerateArray()
            .Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"));
        string actualRegions = string.Join(", ", report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));
        Assert.Equal(expectedRegions, actualRegions);

        Node[] frames = Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Frame")).ToArray();
        JsonElement[] expectedFrames = manifest.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(
            expectedFrames.Select(f => $"{f.GetProperty("offset").GetInt64()}+{f.GetProperty("length").GetInt64()}"),
            frames.Select(f => $"{f.Offset}+{f.Length}"));

        for (int i = 0; i < frames.Length; i++)
        {
            foreach (JsonProperty field in expectedFrames[i].GetProperty("fields").EnumerateObject())
            {
                string? actual = Fixtures.Resolve(frames[i], field.Name).FirstOrDefault()?.Value;
                Assert.True(field.Value.GetString() == actual, $"Frame {i} at {frames[i].Offset}, field {field.Name}: \"{actual}\", expected \"{field.Value.GetString()}\"");
            }
        }

        Assert.Equal(
            manifest.GetProperty("gaps").EnumerateArray().Select(g => $"{g.GetProperty("offset").GetInt64()}+{g.GetProperty("length").GetInt64()}"),
            Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Gap")).Select(g => $"{g.Offset}+{g.Length}"));

        var remaining = report.Findings.ToList();
        foreach (JsonElement item in manifest.GetProperty("findings").EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {Describe(report.Findings)}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {Describe(remaining)}");
    }

    private static void CheckEncoded(JsonElement manifest, JsonElement encoder, Report report)
    {
        Node[] frames = Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Frame")).ToArray();
        bool hasInfoFrame = encoder.GetProperty("info_frame").GetBoolean();

        // ffprobe does not list the Info/Xing frame LAME writes first, so it is skipped here.
        Node[] audioFrames = hasInfoFrame ? frames.Skip(1).ToArray() : frames;
        if (manifest.TryGetProperty("ffprobe_frames", out JsonElement ffprobe))
        {
            Assert.Equal(
                ffprobe.EnumerateArray().Select(f => $"{f[0].GetInt64()}+{f[1].GetInt64()}"),
                audioFrames.Select(f => $"{f.Offset}+{f.Length}"));
        }
        else
        {
            Assert.Equal(manifest.GetProperty("mpg123_frames").GetInt32(), frames.Length);
        }

        string version = encoder.GetProperty("version").GetString() switch
        {
            "1" => "MPEG-1",
            "2" => "MPEG-2",
            _ => "MPEG-2.5",
        };
        string layer = encoder.GetProperty("layer").GetInt32() switch
        {
            1 => "I",
            2 => "II",
            _ => "III",
        };

        var expected = new Dictionary<string, string>
        {
            ["Header/Version"] = version,
            ["Header/Layer"] = layer,
            ["Header/Sample Rate"] = $"{encoder.GetProperty("sample_rate").GetInt32()} Hz",
            ["Header/Channel Mode"] = encoder.GetProperty("channel_mode").GetString()!,
            ["Header/IsProtected"] = encoder.TryGetProperty("protected", out JsonElement p) && p.GetBoolean() ? "true" : "false",
        };
        if (encoder.TryGetProperty("bitrate", out JsonElement bitrate) && bitrate.ValueKind == JsonValueKind.Number)
        {
            expected["Header/Bitrate"] = $"{bitrate.GetInt32()} kbps";
        }

        if (encoder.TryGetProperty("free_format", out JsonElement free) && free.GetBoolean())
        {
            expected["Header/Bitrate"] = "free";
        }

        if (encoder.TryGetProperty("copyright", out JsonElement copyright))
        {
            expected["Header/IsCopyrighted"] = copyright.GetBoolean() ? "true" : "false";
        }

        if (encoder.TryGetProperty("original", out JsonElement original))
        {
            expected["Header/IsOriginal"] = original.GetBoolean() ? "true" : "false";
        }

        if (encoder.TryGetProperty("emphasis", out JsonElement emphasis))
        {
            expected["Header/Emphasis"] = emphasis.GetString()!;
        }

        foreach (Node frame in audioFrames)
        {
            foreach ((string path, string value) in expected)
            {
                string? actual = Fixtures.Resolve(frame, path).FirstOrDefault()?.Value;
                Assert.True(value == actual, $"Frame at {frame.Offset}, field {path}: \"{actual}\", expected \"{value}\"");
            }
        }

        Finding[] serious = report.Findings.Where(f => f.Severity != Severity.Info).ToArray();
        Assert.True(serious.Length == 0, $"Unexpected findings: {Describe(serious)}");
    }

    private static IEnumerable<Node> Streams(Report report) => report.Root.Children.Where(c => c.Name == "MPEG Audio");

    private static string Describe(IEnumerable<Finding> findings) =>
        string.Join("; ", findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}"));
}
