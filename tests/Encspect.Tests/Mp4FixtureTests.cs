using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every MP4 and raw AAC fixture against its manifest: the boxes with their offsets and sizes,
// the streams the codecs report, every access unit or frame, the fields the manifest names and exactly
// the findings it lists; and against its golden report.
public class Mp4FixtureTests
{
    public static TheoryData<string> Names => Fixtures.Mp4Fixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string fileName)
    {
        Report report = Fixtures.AnalyzeMpeg(fileName);
        JsonElement manifest = Fixtures.LoadMpegManifest(fileName);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Assert.Equal(manifest.GetProperty("format").GetString(), report.Root.Value?.ToString());

        if (manifest.TryGetProperty("boxes", out JsonElement boxes) && boxes.GetArrayLength() > 0)
        {
            // An encoder-written fixture records the boxes MP4Box lists, which have no offsets.
            Node file = report.Root.Children.Single(c => c.Name == "MP4");
            var actual = new List<(string Path, long Offset, long Size)>();
            CollectBoxes(file, string.Empty, actual);
            Assert.Equal(
                boxes.EnumerateArray().Select(b => $"{b[0].GetString()}@{(b[1].ValueKind == JsonValueKind.Null ? "?" : b[1].GetInt64())}+{b[2].GetInt64()}"),
                actual.Zip(boxes.EnumerateArray(), (a, b) => $"{a.Path}@{(b[1].ValueKind == JsonValueKind.Null ? "?" : a.Offset)}+{a.Size}")
                    .Concat(actual.Skip(boxes.GetArrayLength()).Select(a => $"{a.Path}@{a.Offset}+{a.Size}")));
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
            if (expected.TryGetProperty("encoder", out JsonElement encoder))
            {
                Assert.Equal((encoder[0].GetString(), encoder[1].GetString()), (stream.Encoder, stream.EncoderConfidence?.ToString()));
            }
            else
            {
                Assert.Null(stream.Encoder);
            }

            if (expected.TryGetProperty("writer", out JsonElement writer))
            {
                Assert.Equal((writer[0].GetString(), writer[1].GetString()), (stream.Writer, stream.WriterConfidence?.ToString()));
            }
            else
            {
                Assert.Null(stream.Writer);
            }

            Assert.All(stream.NodePaths, path => Assert.NotNull(path));
            CheckProtection(expected, stream);
        }

        // Units without an offset (MP4Box lists only sizes) are compared by size.
        Node[] actualUnits = report.Streams.SelectMany(s => s.Nodes).Distinct().SelectMany(n => n.Children.Where(c => c.Kind == NodeKind.Frame)).ToArray();
        JsonElement[] expectedUnits = manifest.GetProperty("units").EnumerateArray().ToArray();
        Assert.Equal(expectedUnits.Length, actualUnits.Length);
        Assert.Equal(
            expectedUnits.Select(u => $"{(u[0].ValueKind == JsonValueKind.Null ? "?" : u[0].GetInt64())}+{u[1].GetInt64()}"),
            actualUnits.Zip(expectedUnits, (a, u) => $"{(u[0].ValueKind == JsonValueKind.Null ? "?" : a.Offset)}+{a.Length}"));

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

    // A stream the manifest gives no "protection" is not protected; the others have the scheme, the encryption and the systems it lists.
    private static void CheckProtection(JsonElement expected, StreamInfo stream)
    {
        if (!expected.TryGetProperty("protection", out JsonElement protection))
        {
            Assert.Null(stream.Protection);
            Assert.False(stream.IsProtected);
            return;
        }

        Assert.True(stream.IsProtected);
        Assert.Equal(protection.GetProperty("scheme").GetString(), stream.Protection!.Scheme);
        Assert.Equal(protection.GetProperty("is_encrypted").GetBoolean(), stream.Protection.IsEncrypted);
        Assert.Equal(protection.GetProperty("systems").EnumerateArray().Select(s => s.GetString()), stream.Protection.Systems);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string fileName)
    {
        string actual = Fixtures.ToGoldenJson(fileName, Fixtures.AnalyzeMpeg(fileName));
        string path = Fixtures.MpegCompanion(fileName, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    // A box node has its header's Size and Type as its first fields.
    private static void CollectBoxes(Node parent, string path, List<(string Path, long Offset, long Size)> boxes)
    {
        foreach (Node child in parent.Children)
        {
            IReadOnlyList<Node> fields = child.Children;
            if (fields.Count < 2 || fields[0].Name != "Size" || fields[1].Name != "Type" || fields[1].Offset != child.Offset + 4)
            {
                continue;
            }

            string type = fields[1].Value!.ToString();
            string full = path.Length == 0 ? type : $"{path}/{type}";
            boxes.Add((full, child.Offset, child.Length));
            CollectBoxes(child, full, boxes);
        }
    }
}
