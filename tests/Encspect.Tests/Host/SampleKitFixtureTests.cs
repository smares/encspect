using System.Security.Cryptography;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The files of the private sample kit that import_sample_kit_fixtures.py took into tests/fixtures/<format>/sample-kit (docs/development.md, "Sample kit
// fixtures"): encodes by Nero Burning ROM, neroAacEnc, qaac, iTunes, Winamp and Windows' Media Foundation encoders. Each has a provenance record, a manifest of
// what ffprobe reads (the analyzer is never its source) and the full golden report (ENCSPECT_UPDATE_GOLDEN=1 rewrites it).
public class SampleKitFixtureTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.provenance.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "sample-kit")
            {
                data.Add(Path.GetRelativePath(Fixtures.FixtureDirectory, path)[..^".provenance.json".Length].Replace('\\', '/'));
            }
        }

        return data;
    }

    private static string FileOf(string stem)
    {
        string directory = Path.Combine(Fixtures.FixtureDirectory, Path.GetDirectoryName(stem)!);
        return Directory.EnumerateFiles(directory, Path.GetFileName(stem) + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
    }

    private static string Companion(string stem, string suffix) => Path.Combine(Fixtures.FixtureDirectory, stem + suffix);

    private static Report Analyze(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(path), stream);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheFileIsTheOneItsProvenanceDescribes(string stem)
    {
        string file = FileOf(stem);
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(Companion(stem, ".provenance.json")));
        JsonElement described = provenance.RootElement.GetProperty("file");
        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal(described.GetProperty("bytes").GetInt64(), bytes.Length);
        Assert.Equal(described.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        string text = File.ReadAllText(Companion(stem, ".provenance.json"));
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheAnalysisAgreesWithWhatFfprobeReads(string stem)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Companion(stem, ".expected.json")));
        JsonElement[] expected = [.. manifest.RootElement.GetProperty("ffprobe").GetProperty("streams").EnumerateArray()];
        StreamInfo[] actual = [.. Analyze(FileOf(stem)).Streams.Where(s => s.Kind == StreamKind.Audio)];
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // For HE-AAC of one channel without explicit parametric stereo signaling (an ADTS stream, or an MP4 sample entry that says two channels),
            // ffprobe reports two: it takes the core for a possible parametric stereo stream. The analyzer counts the channels the syntax codes.
            int channels = expected[i].GetProperty("channels").GetInt32();
            bool impliedByFfprobe = channels == 2 && actual[i].Channels == 1 && expected[i].GetProperty("profile").GetString()!.StartsWith("HE-AAC", StringComparison.Ordinal);
            Assert.True(channels == actual[i].Channels || impliedByFfprobe, $"{stem}: {actual[i].Channels} channels against {channels}");
            // ffprobe reports the core rate of HE-AAC without its explicit signaling.
            int rate = expected[i].GetProperty("sample_rate").GetInt32();
            Assert.True(actual[i].SampleRate == rate || actual[i].SampleRate == rate * 2, $"{stem}: rate {actual[i].SampleRate} against {rate}");
            // ffprobe estimates the duration of an ADTS stream from its bitrate.
            if (expected[i].TryGetProperty("duration", out JsonElement duration) && manifest.RootElement.GetProperty("ffprobe").GetProperty("format_name").GetString() != "aac")
            {
                Assert.InRange(actual[i].Duration, duration.GetDouble() - 0.15, duration.GetDouble() + 0.15);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheReportMatchesTheGolden(string stem)
    {
        string file = FileOf(stem);
        string actual = Fixtures.ToGoldenJson(Path.GetRelativePath(Fixtures.FixtureDirectory, file), Analyze(file));
        string path = Companion(stem, ".report.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void TheSelectionIsNotEmpty() => Assert.NotEmpty(Names());
}
