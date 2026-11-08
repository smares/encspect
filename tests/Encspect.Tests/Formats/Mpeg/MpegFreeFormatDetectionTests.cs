using Encspect.Core;
using Encspect.Formats.Mpeg;
using Encspect.Host;

namespace Encspect.Tests;

// A free-format stream is MPEG audio wherever the parser is asked to read it, with or without a CRC; only the detection score of a chain
// that data other than MPEG audio could make is low, so that the format owning that data wins.
public class MpegFreeFormatDetectionTests
{
    private static byte[] Bytes(string directory, string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, directory, name));

    private static int Sniff(byte[] data) => new MpegAudioParser().Sniff(new SniffContext(new ByteReader(new MemoryStream(data)), "mpeg")).Class;

    public static TheoryData<string, string, int, string, int> Streams() => new()
    {
        { "layer2", "free-format-48000-unprotected.mp2", 6, "Layer II", 30 },
        { "mpeg", "mpeg1-layer1-free-format.mp1", 6, "Layer I", 30 },
        { "layer2", "free-format-44100-crc-mismatch.mp2", 6, "Layer II", 90 },
        { "layer2", "free-format-44100.mp2", 6, "Layer II", 90 },
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
        Report report = Analyzer.CreateDefault().Analyze("damaged.mp2", new MemoryStream(Bytes("layer2", "free-format-44100-crc-mismatch.mp2")));
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
        Assert.Equal("MPEG Audio", Analyzer.CreateDefault().Analyze("zero.mp2", new MemoryStream(zeroLayer2)).Root.Value?.ToString());
    }
}
