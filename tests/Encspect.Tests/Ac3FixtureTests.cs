using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every AC-3 and E-AC-3 fixture against its manifest (make_ac3_fixtures.py,
// make_external_ac3_fixtures.py, confirmed by crosscheck_ac3_fixtures.py): the format, every frame
// and gap, where liba52 ends each audio block of the encoder-written AC-3 frames, the streams with
// their channel layouts, the fields the manifest names and exactly the findings it lists; and
// against its golden report.
public class Ac3FixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "ac3");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add($"ac3/{Path.GetFileName(file)}");
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

        if (manifest.TryGetProperty("block_ends", out JsonElement blockEnds))
        {
            foreach (JsonProperty frame in blockEnds.EnumerateObject())
            {
                Node node = frames.Single(f => f.Offset == long.Parse(frame.Name, System.Globalization.CultureInfo.InvariantCulture));
                long[] ends = [.. node.Children.Where(c => c.Name == "Audio Block").Select(b => EndBit(b, node.Offset))];
                Assert.Equal(frame.Value.EnumerateArray().Select(e => e.GetInt64()), ends);
            }
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

    // A frame's nodes, built from its record, read exactly the bits the analysis kept, and every
    // node lies inside its parent, after the sibling before it.
    [Theory]
    [MemberData(nameof(Names))]
    public void FrameNodesCoverTheirFrames(string name)
    {
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<string>();
        var parser = new Encspect.Formats.Ac3.Ac3Parser
        {
            RecordMismatch = (offset, kept, read) => mismatches.Add($"{offset}: {kept} bits kept, {read} read"),
        };
        IEnumerable<IFormatParser> formats = DefaultParsers.Formats.Select(f => f is Encspect.Formats.Ac3.Ac3Parser ? parser : f);
        var analyzer = new Analyzer(DefaultParsers.Tags, formats, DefaultParsers.Codecs);
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = analyzer.Analyze(Path.GetFileName(name), stream);
        foreach (Node frame in report.Streams.SelectMany(s => s.Nodes).Distinct().SelectMany(n => n.Children.Where(c => c.Kind == NodeKind.Frame)))
        {
            CheckChildren(frame, frame.Offset);
        }

        Assert.Empty(mismatches);
    }

    private static void CheckChildren(Node parent, long frameOffset)
    {
        long previous = StartBit(parent, frameOffset);
        long end = EndBit(parent, frameOffset);
        foreach (Node child in parent.Children)
        {
            long start = StartBit(child, frameOffset);
            Assert.True(start >= previous, $"{child.Name} at bit {start} of the frame at {frameOffset} starts before the node before it ends ({previous}).");
            previous = EndBit(child, frameOffset);
            Assert.True(previous <= end, $"{child.Name} of the frame at {frameOffset} ends at bit {previous}, after {parent.Name} ({end}).");
            CheckChildren(child, frameOffset);
        }
    }

    private static long StartBit(Node node, long frameOffset) => ((node.Offset - frameOffset) * 8) + (node.BitOffset ?? 0);

    private static long EndBit(Node node, long frameOffset) =>
        node.BitLength is { } bits ? ((node.Offset - frameOffset) * 8) + node.BitOffset!.Value + bits : (node.End - frameOffset) * 8;
}
