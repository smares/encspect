using Encspect.Core;
using Encspect.Formats.MpegAudio;
using Encspect.Host;

namespace Encspect.Tests;

// A free-format stream is read as MPEG audio wherever the parser is asked to, with or without a CRC. A chain that data other than MPEG
// audio could make has a low score and never identifies a file: it is reported as unrecognized, with the weak-candidate finding.
public class MpegFreeFormatDetectionTests
{
    private static byte[] Bytes(string directory, string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, directory, name));

    private static int Sniff(byte[] data) => new MpegAudioParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data)), "mpeg")).Class;

    public static TheoryData<string, string, int, string, int> Streams() => new()
    {
        { "mpeg-audio", "mpeg1-layer1-free-format.mp1", 6, "Layer I", 90 },
        { "mpeg-audio/layer2", "free-format-44100-crc-mismatch.mp2", 6, "Layer II", 90 },
        { "mpeg-audio/layer2", "free-format-44100.mp2", 6, "Layer II", 90 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void FreeFormatStreamsAreMpegAudioAloneAndWhenForced(string directory, string name, int frames, string layer, int score)
    {
        byte[] data = Bytes(directory, name);
        Assert.Equal(score, Sniff(data));
        foreach (var analyzer in new[] { Analyzer.CreateDefault(), new Analyzer([new MpegAudioParser()]) })
        {
            Report report = analyzer.Analyze(name, new MemoryStream(data));
            Assert.Equal("MPEG Audio", report.Root.Value?.ToString());
            StreamInfo stream = Assert.Single(report.Streams);
            Assert.Contains(layer, stream.Codec);
            Assert.Equal(frames, stream.Samples / (layer == "Layer I" ? 384 : 1152));
        }
    }

    [Fact]
    public void TheFirstFrameWithABadCrcIsReported()
    {
        Report report = Analyzer.CreateDefault().Analyze("damaged.mp2", new MemoryStream(Bytes("mpeg-audio/layer2", "free-format-44100-crc-mismatch.mp2")));
        Assert.Contains(report.Findings, f => f.Code == "mpeg.crc-mismatch");
    }

    // A chain of free-format headers that hold nothing a frame holds is what DTS data with junk in front makes: it ranks below DTS's 50.
    [Fact]
    public void AFreeFormatChainWithoutCredibleFramesRanksBelowDtsFurtherIn()
    {
        byte[] junk = [.. Enumerable.Range(0, 30).Select(i => (byte)(0x80 | i))];
        foreach (string name in new[] { "core-16be.dts", "junk-around.dts", "external-ffmpeg-5.1-48k-768k.dts", "external-ffmpeg-stereo-44k-256k.dts", "14le-x96.dts", "exss-lossless.dts" })
        {
            Assert.True(Sniff([.. junk, .. Bytes("dts", name)]) < 50, name);
        }

        byte[] zeroLayer2 = [.. Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 0xFF, 0xFD, 0x04, 0x00 }.Concat(new byte[236]))];
        Assert.Equal(30, Sniff(zeroLayer2));
    }

    // An unprotected free-format Layer II chain holds nothing but the frames themselves; it is no file's format, and the finding names it.
    [Fact]
    public void AWeakChainDoesNotIdentifyTheFileButIsNamedAndReadWhenForced()
    {
        byte[] zeroLayer2 = [.. Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 0xFF, 0xFD, 0x04, 0x00 }.Concat(new byte[236]))];
        foreach (var analyzer in new[] { Analyzer.CreateDefault(), new Analyzer([new MpegAudioParser()]) })
        {
            Report report = analyzer.Analyze("zero.mp2", new MemoryStream(zeroLayer2));
            Assert.Empty(report.Streams);
            Assert.Null(report.Detection);
            Assert.NotEqual("MPEG Audio", report.Root.Value?.ToString());
            Assert.Equal(["format.unrecognized", "format.weak-candidate"], report.Findings.Select(f => f.Code));
            Finding weak = report.Findings[1];
            Assert.Equal(Severity.Info, weak.Severity);
            Assert.Contains("MPEG Audio", weak.Message);
            Assert.Contains("at 0x0", weak.Message);
            Assert.Contains("6 frames", weak.Message);
        }

        Report forced = Analyzer.CreateDefault().Analyze("zero.mp2", new MemoryStream(zeroLayer2), new AnalysisOptions { ForcedFormat = "mpeg" });
        Assert.Equal("MPEG Audio", forced.Root.Value?.ToString());
        Assert.Equal(6 * 1152, Assert.Single(forced.Streams).Samples);
    }

    // Binary data with a header-like word at a fixed distance is not MPEG audio, whatever else claims it.
    [Fact]
    public void AWeakChainLosesToAnotherFormatsClaimWithoutDisturbingIt()
    {
        byte[] wave = File.ReadAllBytes(Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "wav"), "*.wav").Order(StringComparer.Ordinal).First());
        byte[] chain = Bytes("mpeg-audio", "free-format-weak-chain.mp3");
        Report report = Analyzer.CreateDefault().Analyze("both.wav", new MemoryStream([.. wave, .. chain]));
        Assert.Equal("WAV", report.Root.Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code is "format.weak-candidate" or "format.unrecognized");
    }
}
