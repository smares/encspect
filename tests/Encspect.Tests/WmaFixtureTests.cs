using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks the WMA fixtures ffmpeg's encoders wrote (make_external_asf_fixtures.py) against what ffprobe
// and ffmpeg's decoder say of them, and the structure of their superframes against what the encoder
// writes: every superframe is a whole number of block alignment bytes, holds one frame and is padded
// with 'N' bytes.
public class WmaFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "external-ffmpeg-*.wma").Order(StringComparer.Ordinal))
            {
                data.Add($"asf/{Path.GetFileName(path)}");
            }

            return data;
        }
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    private static long Integer(Node parent, string name) => Child(parent, name).Value!.AsInteger()!.Value;

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesTools(string name)
    {
        JsonElement tools = Fixtures.LoadMpegManifest(name).GetProperty("ffprobe");
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(tools.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.Equal(tools.GetProperty("rate").GetInt32(), stream.SampleRate);
        Assert.Equal(tools.GetProperty("codec").GetString() == "wmav1" ? "Windows Media Audio 1" : "Windows Media Audio 2", stream.Codec);
        Assert.Equal(tools.GetProperty("channels").GetInt32() == 1 ? "mono" : "stereo", stream.ChannelLayoutName);

        Node node = stream.Nodes[0];
        Node statistics = Child(node, "Statistics");
        long packets = tools.GetProperty("packets").GetInt64();
        Assert.Equal(packets, Integer(statistics, "Superframes"));
        long frames = Integer(statistics, "Frames");
        Assert.Equal(packets, frames);
        long frameLength = Integer(statistics, "Frame Length");

        // The decoder's output starts one frame late: it returns the overlapped half of the frame before.
        Assert.Equal(tools.GetProperty("decoded_samples").GetInt64(), (frames - 1) * frameLength);
        Assert.Equal(frames * frameLength, stream.Samples);
        Assert.Equal(tools.GetProperty("packet_sizes").EnumerateArray().Single().GetInt64(), Integer(statistics, "Block Alignment"));
        Assert.False(Child(statistics, "Uses Bit Reservoir").Value!.AsBoolean()!.Value);
        Assert.False(Child(statistics, "Uses Variable Block Length").Value!.AsBoolean()!.Value);
        Assert.Equal(frames, Integer(statistics, "Blocks"));
        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void SuperframesAreFramesPaddedWithN(string name)
    {
        JsonElement tools = Fixtures.LoadMpegManifest(name).GetProperty("ffprobe");
        Report report = Fixtures.AnalyzeMpeg(name);
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        Node superframes = Child(report.Streams[0].Nodes[0], "Superframes");
        long blockAlign = tools.GetProperty("packet_sizes").EnumerateArray().Single().GetInt64();
        Assert.Equal(tools.GetProperty("packets").GetInt32(), superframes.Children.Count);
        foreach (Node superframe in superframes.Children)
        {
            Assert.Equal(blockAlign, superframe.Length);
            Assert.Equal("1 frame", superframe.Value!.ToString());
            Node? padding = superframe.Children.FirstOrDefault(c => c.Name == "Padding");
            if (padding is not null)
            {
                Assert.Equal(superframe.End, padding.End);
                Assert.All(file.AsSpan((int)padding.Offset, (int)padding.Length).ToArray(), b => Assert.Equal((byte)'N', b));
            }

            Node data = Child(superframe, "Frame Data");
            Assert.Equal(superframe.Offset, data.Offset);
            Assert.True(padding is null || data.End <= padding.Offset + 1);
        }
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
}
