using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every DTS fixture against its manifest (make_dts_fixtures.py, make_external_dts_fixtures.py,
// confirmed by crosscheck_dts_fixtures.py): the format, every frame and gap, the top-level regions
// where tags are embedded, the streams with their channel layouts, the fields the manifest names and
// exactly the findings it lists; and against its golden report.
public class DtsFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "dts");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add($"dts/{Path.GetFileName(file)}");
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
        Assert.Equal(manifest.GetProperty("format").GetString(), report.Root.Value?.ToString());

        Node[] streamNodes = report.Streams.SelectMany(s => s.Nodes).Distinct().ToArray();
        Node[] frames = streamNodes.SelectMany(n => n.Children.Where(c => c.Kind == NodeKind.Frame)).ToArray();
        Assert.Equal(
            manifest.GetProperty("frames").EnumerateArray().Select(f => $"{f[0].GetInt64()}+{f[1].GetInt64()}"),
            frames.Select(f => $"{f.Offset}+{f.Length}"));
        Assert.Equal(
            manifest.GetProperty("gaps").EnumerateArray().Select(f => $"{f[0].GetInt64()}+{f[1].GetInt64()}"),
            streamNodes.SelectMany(n => n.Children.Where(c => c.Name == "Gap")).Select(g => $"{g.Offset}+{g.Length}"));

        if (manifest.TryGetProperty("regions", out JsonElement regions))
        {
            Assert.Equal(
                regions.EnumerateArray().Select(r => $"{r[0].GetString()} {r[1].GetInt64()}+{r[2].GetInt64()}"),
                report.Root.Children.Where(c => c.Kind != NodeKind.Detail).Select(c => $"{c.Name} {c.Offset}+{c.Length}"));
        }

        JsonElement[] streams = manifest.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Equal(streams.Length, report.Streams.Count);
        for (int i = 0; i < streams.Length; i++)
        {
            JsonElement expected = streams[i];
            StreamInfo stream = report.Streams[i];
            Assert.Equal(expected.GetProperty("codec").GetString(), stream.Codec);
            Assert.Equal(expected.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
            Assert.Equal(expected.GetProperty("channels").GetInt32(), stream.Channels);
            Assert.Equal(expected.GetProperty("channel_mode").GetString(), stream.ChannelMode);
            Assert.Equal(expected.GetProperty("channel_layout").EnumerateArray().Select(s => s.GetString()), stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
            Assert.Equal(expected.GetProperty("samples").GetInt64(), stream.Samples);
            Assert.Equal(expected.GetProperty("is_vbr").GetBoolean(), stream.IsVbr);
            Assert.All(stream.NodePaths, path => Assert.NotNull(path));
        }

        foreach (JsonProperty field in manifest.GetProperty("fields").EnumerateObject())
        {
            string? actual = Fixtures.Resolve(report.Root, field.Name).FirstOrDefault()?.Value?.ToString();
            Assert.True(field.Value.GetString() == actual, $"{field.Name}: \"{actual}\", expected \"{field.Value.GetString()}\"");
        }

        JsonElement[] findings = manifest.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Equal(findings.Select(f => f[0].GetString()).Order(StringComparer.Ordinal), report.Findings.Select(f => f.Code).Order(StringComparer.Ordinal));
        foreach (JsonElement finding in findings)
        {
            Assert.Contains(report.Findings, f => f.Code == finding[0].GetString() && f.Message.Contains(finding[1].GetString()!, StringComparison.Ordinal));
        }

        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        TestFiles.CheckInsideParents(report.Root);
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
}
