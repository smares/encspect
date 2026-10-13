using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks the WMA fixtures the encoders wrote (make_external_asf_fixtures.py: ffmpeg's wmav1 and wmav2; the
// external-windows-* files from Microsoft's encoders, imported by import_windows_wma_fixtures.py) against what
// ffprobe and ffmpeg's decoder say of them: the samples a decoder outputs, the channels and the rate, no finding
// beyond information, and the encoder the analyzer names. For ffmpeg's files the structure of the superframes
// too: each holds one frame and is padded with 'N' bytes.
public class WmaFixtureTests
{
    public static TheoryData<string> Names => Matching("external-*.wma");

    public static TheoryData<string> FFmpegNames => Matching("external-ffmpeg-*.wma");

    public static TheoryData<string> MicrosoftNames => Matching("external-windows-*.wma");

    public static TheoryData<string> StandardNames => Matching("external-windows-wma92-*.wma");

    public static TheoryData<string> ProNames => Matching("external-windows-wma10pro-*.wma");

    private static TheoryData<string> Matching(string pattern)
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), pattern).Order(StringComparer.Ordinal))
        {
            data.Add($"asf/{Path.GetFileName(path)}");
        }

        return data;
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    private static long Integer(Node parent, string name) => Child(parent, name).Value!.AsInteger()!.Value;

    private static string CodecName(string ffprobe) => ffprobe switch
    {
        "wmav1" => "Windows Media Audio 1",
        "wmav2" => "Windows Media Audio 2",
        "wmapro" => "Windows Media Audio Professional",
        "wmalossless" => "Windows Media Audio Lossless",
        "wmavoice" => "Windows Media Audio Voice",
        _ => throw new ArgumentOutOfRangeException(nameof(ffprobe)),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesTools(string name)
    {
        JsonElement tools = Fixtures.LoadMpegManifest(name).GetProperty("ffprobe");
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        string codec = tools.GetProperty("codec").GetString()!;
        Assert.Equal(CodecName(codec), stream.Codec);
        Assert.Equal(tools.GetProperty("rate").GetInt32(), stream.SampleRate);

        // ffprobe's channel count is the stream's, which the mask can only restate.
        Assert.Equal(tools.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.NotNull(stream.ChannelLayout);

        // The samples a decoder outputs, as ffmpeg decodes them.
        Assert.Equal(tools.GetProperty("decoded_samples").GetInt64(), stream.Samples);
        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code is "wma.frame-invalid" or "wma.frame-boundary");
    }

    [Theory]
    [MemberData(nameof(FFmpegNames))]
    public void FFmpegSuperframesAreFramesPaddedWithN(string name)
    {
        JsonElement tools = Fixtures.LoadMpegManifest(name).GetProperty("ffprobe");
        Report report = Fixtures.AnalyzeMpeg(name);
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        StreamInfo stream = report.Streams[0];
        Node node = stream.Nodes[0];
        Node statistics = Child(node, "Statistics");
        long packets = tools.GetProperty("packets").GetInt64();
        long blockAlign = tools.GetProperty("packet_sizes").EnumerateArray().Single().GetInt64();
        Assert.Equal(packets, Integer(statistics, "Superframes"));
        Assert.Equal(packets, Integer(statistics, "Frames"));
        Assert.Equal(packets, Integer(statistics, "Blocks"));
        Assert.Equal(blockAlign, Integer(statistics, "Block Alignment"));
        Assert.False(Child(statistics, "Uses Bit Reservoir").Value!.AsBoolean()!.Value);
        Assert.False(Child(statistics, "Uses Variable Block Length").Value!.AsBoolean()!.Value);

        Node superframes = Child(node, "Superframes");
        Assert.Equal(packets, superframes.Children.Count);
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

        Assert.Equal($"FFmpeg (wmav{(tools.GetProperty("codec").GetString() == "wmav1" ? 1 : 2)} encoder)", stream.Encoder);
        Assert.Equal(Confidence.High, stream.EncoderConfidence);
    }

    // Microsoft's encoders write the bit reservoir and variable block lengths; the analyzer reads every frame to its
    // end and finds the straddling ones where the superframe headers say.
    [Theory]
    [MemberData(nameof(StandardNames))]
    public void MicrosoftStandardStreamsUseTheReservoir(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Node statistics = Child(report.Streams[0].Nodes[0], "Statistics");
        Assert.True(Child(statistics, "Uses Bit Reservoir").Value!.AsBoolean()!.Value);
        Assert.True(Child(statistics, "Uses Variable Block Length").Value!.AsBoolean()!.Value);
        Assert.True(Integer(statistics, "Frames Spanning Superframes") > 0);
        Assert.DoesNotContain(statistics.Children, c => c.Name == "Invalid Frames");
    }

    // Professional: every frame's length prefix is where the analyzer finds the frame to end, which checks the
    // reading of the tile header, subframes, scale factors and coefficient codes down to the bit.
    [Theory]
    [MemberData(nameof(ProNames))]
    public void ProfessionalFramesEndWhereTheirLengthPrefixSays(string name)
    {
        JsonElement tools = Fixtures.LoadMpegManifest(name).GetProperty("ffprobe");
        Report report = Fixtures.AnalyzeMpeg(name);
        Node node = report.Streams[0].Nodes[0];
        Node statistics = Child(node, "Statistics");
        Assert.DoesNotContain(statistics.Children, c => c.Name == "Invalid Frames");
        Assert.True(Child(statistics, "Has Length Prefix").Value!.AsBoolean()!.Value);
        Assert.Equal(tools.GetProperty("packets").GetInt64(), Integer(statistics, "Packets"));
        Assert.Equal(0, report.Findings.Count(f => f.Code is "wma.frame-invalid" or "wma.packet-sequence"));
        Assert.Equal(Integer(statistics, "Frames"), Child(node, "Frames").Children.Count);
        foreach (Node frame in Child(node, "Frames").Children)
        {
            Assert.StartsWith("Frame", frame.Name, StringComparison.Ordinal);
            Assert.Equal("More Frames", frame.Children[^1].Name);
        }
    }

    [Theory]
    [MemberData(nameof(MicrosoftNames))]
    public void MicrosoftFilesNameTheirWriter(string name)
    {
        string provenancePath = Path.Combine(Fixtures.FixtureDirectory, name[..^".wma".Length] + ".provenance.json");
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(provenancePath));
        string path = provenance.RootElement.GetProperty("encoder").GetProperty("path").GetString()!;
        StreamInfo stream = Fixtures.AnalyzeMpeg(name).Streams[0];
        Assert.NotNull(stream.Encoder);
        if (path == "media-foundation")
        {
            Assert.StartsWith("Media Foundation ", stream.Encoder, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("Windows Media Format SDK ", stream.Encoder, StringComparison.Ordinal);
        }

        Assert.Equal(Confidence.Medium, stream.EncoderConfidence);
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
