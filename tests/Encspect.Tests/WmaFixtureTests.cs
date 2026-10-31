using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks the WMA fixtures the encoders wrote (make_external_asf_fixtures.py: ffmpeg's wmav1 and wmav2; the
// external-windows-* files from Microsoft's encoders, imported by import_windows_wma_fixtures.py) against what
// ffprobe says of them (the codec, the channels, the rate and for versions 1 and 2 the decoded
// samples), no finding beyond information, and the writer the analyzer names.
public class WmaFixtureTests
{
    public static TheoryData<string> Names => Matching("external-*.wma");

    public static TheoryData<string> MicrosoftNames => Matching("external-windows-*.wma");

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

        // The frames of versions 1 and 2 are read, and the samples equal the ones ffmpeg decodes; the others have the container's duration at the rate (the Voice frames are held by the golden reports and VoiceFixtureTests).
        if (codec is "wmav1" or "wmav2")
        {
            Assert.Equal(tools.GetProperty("decoded_samples").GetInt64(), stream.Samples);
        }
        else
        {
            Assert.Equal((long)Math.Round(stream.Duration * stream.SampleRate), stream.Samples);
        }

        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MicrosoftNames))]
    public void MicrosoftFilesNameTheirWriter(string name)
    {
        string provenancePath = Path.Combine(Fixtures.FixtureDirectory, name[..^".wma".Length] + ".provenance.json");
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(provenancePath));
        string path = provenance.RootElement.GetProperty("encoder").GetProperty("path").GetString()!;
        StreamInfo stream = Fixtures.AnalyzeMpeg(name).Streams[0];
        Assert.NotNull(stream.Writer);
        if (path == "media-foundation")
        {
            Assert.StartsWith("Media Foundation ", stream.Writer, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("Windows Media Format SDK ", stream.Writer, StringComparison.Ordinal);
        }

        Assert.Equal(Confidence.Medium, stream.WriterConfidence);
        // the writer is not the encoder: what the stream shows of the encoder is resolved apart
        Assert.DoesNotContain("Media Foundation", stream.Encoder ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Format SDK", stream.Encoder ?? string.Empty, StringComparison.Ordinal);
    }

    // The same codec at the same constant bitrate, written by the two Windows writers: the headers differ, and the
    // writer is named from them (the codec data and IsVBR are the same).
    [Theory]
    [InlineData("asf/external-windows-wma92-cbr-stereo-44k-128.wma", "asf/external-windows-wma92-cbr-stereo-44k-128-sdk.wma")]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-128.wma", "asf/external-windows-wma10pro-cbr-stereo-44k-128-sdk.wma")]
    public void ConstantBitrateFilesOfTheTwoWritersAreToldApart(string foundation, string sdk)
    {
        StreamInfo viaFoundation = Fixtures.AnalyzeMpeg(foundation).Streams[0];
        StreamInfo viaSdk = Fixtures.AnalyzeMpeg(sdk).Streams[0];
        Assert.StartsWith("Media Foundation ", viaFoundation.Writer, StringComparison.Ordinal);
        Assert.StartsWith("Windows Media Format SDK ", viaSdk.Writer, StringComparison.Ordinal);
        Assert.Equal(Confidence.Medium, viaFoundation.WriterConfidence);
        Assert.Equal(Confidence.Medium, viaSdk.WriterConfidence);
        Assert.False(viaFoundation.IsVbr);
        Assert.False(viaSdk.IsVbr);
    }

    // ffmpeg's ASF muxer copies the attributes of the file it reads, WMFSDKVersion and MediaFoundationVersion among them, and adds its own
    // WM/EncodingSettings after them (make_external_asf_fixtures.py --remuxes-only): the program that wrote this file is FFmpeg, and the
    // copied version is a claim that differs. The files' layouts are not the SDK's or Media Foundation's (no Compatibility Object).
    [Theory]
    [InlineData("asf/external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk.wma", "WMFSDKVersion")]
    [InlineData("asf/external-remux-ffmpeg-wma92-cbr-stereo-22k-32.wma", "MediaFoundationVersion")]
    public void AnFFmpegRemuxIsWrittenByFFmpegNotByTheWriterOfTheSource(string name, string copied)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("FFmpeg (Lavf60.16.100)", stream.Writer);
        Assert.Equal(Confidence.Low, stream.WriterConfidence);
        Assert.Contains(report.Findings, f => f.Code == "wma.writer-claim-differs" && f.Message.Contains(copied, StringComparison.Ordinal));
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        int copiedAt = file.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(copied));
        int stampAt = file.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("Lavf"));
        Assert.True(copiedAt > 0 && stampAt > copiedAt, "the muxer's stamp follows the attribute it copied");
    }

    // The muxer's stamp goes first only where ffmpeg's ASF muxer leaves it, as the last attribute (mux.c appends "encoder", asfenc.c writes the
    // dictionary in order). With an attribute after it, which other software added, file order decides and the stamp is a claim that differs.
    [Theory]
    [InlineData("asf/writer-stamp-last.asf", "FFmpeg (Lavf60.16.100)")]
    [InlineData("asf/writer-stamp-not-last.asf", "Windows Media Format SDK 12.0.26100.1")]
    public void AMuxerStampOutranksCopiedClaimsOnlyAsTheLastAttribute(string name, string writer)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(writer, stream.Writer);
        Assert.Equal(Confidence.Low, stream.WriterConfidence);
        Assert.Contains(report.Findings, f => f.Code == "wma.writer-claim-differs");
    }

    // The Evidence nodes of a stream lie in the container's header or in the codec data; the nodes cover the stream, and the text says where.
    [Fact]
    public void WriterEvidenceDescriptionDoesNotClaimTheNodesBytes()
    {
        Node writer = Find(Fixtures.AnalyzeMpeg("asf/external-windows-wma92-cbr-stereo-44k-128-sdk.wma").Streams[0].Nodes[0], "Writer")!;
        Node evidence = writer.Children.First(c => c.Name == "Evidence");
        Assert.NotEqual(WriterDetection.EvidenceDescription, evidence.Description);
        Assert.Contains("at 0x", evidence.Value!.ToString(), StringComparison.Ordinal);
    }

    private static Node? Find(Node node, string name) => node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null);

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
