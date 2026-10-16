using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// An APEv2 text item with the key Encoder, in any case, is a claim of the writer role, which the format
// parsers that weigh claims (MPEG audio, Musepack and WavPack among them) resolve apart from the encoder.
public class ApeEncoderClaimTests
{
    // An APEv2 tag with header and footer holding these items, each (key, value, flags).
    private static byte[] Tag(params (string Key, byte[] Value, uint Flags)[] items)
    {
        var body = new List<byte>();
        foreach ((string key, byte[] value, uint flags) in items)
        {
            body.AddRange(BitConverter.GetBytes((uint)value.Length));
            body.AddRange(BitConverter.GetBytes(flags));
            body.AddRange(Encoding.ASCII.GetBytes(key));
            body.Add(0);
            body.AddRange(value);
        }

        byte[] Block(uint flags)
        {
            byte[] block = new byte[32];
            "APETAGEX"u8.CopyTo(block);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 2000);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)(body.Count + 32));
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)items.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), flags);
            return block;
        }

        return [.. Block(0xA0000000), .. body, .. Block(0x80000000)];
    }

    private static byte[] Text(string key, string value, uint flags = 0) => Tag((key, Encoding.UTF8.GetBytes(value), flags));

    private static byte[] Fixture(string path) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, path));

    private static Report Analyze(string name, byte[] data) => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    // The APEv2 item key list has no key for the encoder, so the item is the writer's: it differs from the encoder
    // the stream shows, and that is no conflict.
    [Theory]
    [InlineData("Encoder")]
    [InlineData("encoder")]
    [InlineData("ENCODER")]
    public void MpegAudioTakesTheItemAsTheWriter(string key)
    {
        Report report = Analyze("claim.mp3", [.. Fixture("mpeg/external-lame-cbr128-joint.mp3"), .. Text(key, "Shine 3.1")]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.StartsWith("LAME", stream.Encoder, StringComparison.Ordinal);
        Assert.Equal(("Shine 3.1", Confidence.Low), (stream.Writer, stream.WriterConfidence));
        Assert.DoesNotContain(report.Findings, f => f.Code is "mpeg.encoder-claim-differs" or "mpeg.writer-claim-differs");
    }

    [Fact]
    public void TwoWritersThatNameDifferentSoftwareAreAConflict()
    {
        byte[] id3 = TestFiles.Id3v23(("TSSE", "Lavf60.16.100"));
        Report report = Analyze("claim.mp3", [.. id3, .. Fixture("mpeg/external-lame-cbr128-joint.mp3"), .. Text("Encoder", "Shine 3.1")]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("FFmpeg (Lavf60.16.100)", stream.Writer);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "mpeg.writer-claim-differs");
        Assert.Contains("APE Encoder item names \"Shine 3.1\"", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code == "mpeg.encoder-claim-differs");
    }

    [Fact]
    public void MusepackTakesTheItemAsTheWriter()
    {
        Report report = Analyze("claim.mpc", [.. Fixture("musepack/external-mpcenc-standard.mpc"), .. Text("Encoder", "LAME 3.100")]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("LAME 3.100", stream.Writer);
        Assert.NotEqual("LAME 3.100", stream.Encoder);
        Assert.DoesNotContain(report.Findings, f => f.Code is "musepack.encoder-claim-differs" or "musepack.writer-claim-differs");
    }

    [Fact]
    public void WavPackTakesTheItemAsTheWriter()
    {
        Report report = Analyze("claim.wv", [.. Fixture("wavpack/external-lossless-16-stereo.wv"), .. Text("encoder", "LAME 3.100")]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(("LAME 3.100", Confidence.Low), (stream.Writer, stream.WriterConfidence));
        Assert.DoesNotContain(report.Findings, f => f.Code is "wavpack.encoder-claim-differs" or "wavpack.writer-claim-differs");
    }

    // Another key, an empty value, a value that is not text and a value of blanks claim nothing.
    [Fact]
    public void OtherItemsAreNoClaim()
    {
        byte[] file = Fixture("wavpack/external-lossless-16-stereo.wv");
        byte[][] tags =
        [
            Text("Software", "LAME 3.100"),
            Text("Encoder", string.Empty),
            Text("Encoder", "   "),
            Text("Encoder", "LAME 3.100", flags: 2),
            Tag(("Encoder", Encoding.ASCII.GetBytes("LAME 3.100"), 4)),
        ];
        foreach (byte[] tag in tags)
        {
            Report report = Analyze("claim.wv", [.. file, .. tag]);
            Assert.DoesNotContain(report.Findings, f => f.Code is "wavpack.encoder-claim-differs" or "wavpack.writer-claim-differs" or "parse.failed");
            Assert.Single(report.Root.Children, c => c.Name == "APE");
        }
    }
}
