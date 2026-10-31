using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio versions 1 and 2, on streams written bit by bit in the test from docs/design/wma-standard-format.md.
public class WmaStandardTests
{
    // 44.1 kHz mono at 24 kbit/s: b is 0.544, below the 0.61 at which noise substitution is off, so h is 0.4 of the Nyquist frequency.
    private static StdParameters NoiseParameters() =>
        StdParameters.Create(new AudioFormat(0x0161, 1, 44100, 3000, 400, 16), WmaStandardStreams.CodecData(2, 0x0001));

    [Fact]
    public void HighBandsOfAMono44KilohertzStreamWithNoiseSubstitution()
    {
        StdParameters p = NoiseParameters();

        Assert.True(p.UsesNoiseSubstitution);
        Assert.Equal(2048, p.FrameLength);
        Assert.Equal([65, 232, 324, 424], p.HighBands[0]);
        Assert.Equal(1864, p.CoefficientEnds[0]);
    }

    private static Result RunFrame(StdParameters p, WmaBitWriter frame, int align = 400)
    {
        byte[] data = new byte[align];
        Array.Fill(data, (byte)'N');
        frame.ToBytes((frame.Length + 7) / 8).CopyTo(data, 0);
        return new Result(WmaStandardStreams.Run(2, 1, 44100, 3000, align, WmaStandardStreams.CodecData(2, 0x0001), [data, data]));
    }

    private sealed record Result(WmaStandardStreams.Result Run)
    {
        public Node Stream => Run.Parent.Children.Single(c => c.Name == "Windows Media Audio 2");

        public Node FirstBlock => WmaStandardStreams.Find(Stream.Children.Single(c => c.Name == "Frames"), "Block");
    }

    [Fact]
    public void AFrameWithAFlaggedHighBandIsReadToItsEnd()
    {
        StdParameters p = NoiseParameters();
        Result result = RunFrame(p, WmaStandardStreams.MonoFrame(p, 20, 10, flaggedBand: 0));

        Assert.DoesNotContain(result.Run.Findings, f => f.Code == "wma.frame-invalid");
        Assert.Equal("2 frames", result.Stream.Value!.ToString());
        Assert.Equal("1 of 4 high bands", Child(result.FirstBlock, "Noise Substitution").Value!.ToString());
        Node levels = Child(result.FirstBlock, "Noise Levels");
        Assert.Equal(1, levels.Value!.AsInteger());
        Assert.Equal("bands", levels.Value.Unit);
        Assert.Equal("10 non-zero, last at 9", Child(result.FirstBlock, "Coefficients (channel 1)").Value!.ToString());
    }

    // A flagged band's coefficients are not coded, so the channel codes 1,864 less its 65 coefficients: a coefficient at position 1,798 is the
    // last that fits then, and 1,863 without the flag.
    [Theory]
    [InlineData(-1, 1863, true)]
    [InlineData(-1, 1864, false)]
    [InlineData(0, 1798, true)]
    [InlineData(0, 1799, false)]
    public void CoefficientsStopShortByTheFlaggedWidths(int flaggedBand, int run, bool isValid)
    {
        StdParameters p = NoiseParameters();
        Result result = RunFrame(p, WmaStandardStreams.MonoFrame(p, 20, 0, flaggedBand, run));

        Assert.Equal(!isValid, result.Run.Findings.Any(f => f.Code == "wma.frame-invalid"));
        if (isValid)
        {
            Assert.Equal($"1 non-zero, last at {run}", Child(result.FirstBlock, "Coefficients (channel 1)").Value!.ToString());
        }
    }

    // A frame that begins in a superframe inside a frame, goes on through another and ends in the next, is read to its end.
    [Fact]
    public void AFrameEndsAfterASuperframeInsideIt()
    {
        var p = StdParameters.Create(new AudioFormat(0x0161, 1, 8000, 1000, 100, 16), WmaStandardStreams.CodecData(2, 0x0003));
        int w = p.BitOffsetBits;
        WmaBitWriter frame;
        int per;
        int coefficients = 0;
        do
        {
            frame = WmaStandardStreams.MonoFrame(p, 20, coefficients++);
            per = (frame.Length + w + 23) / 24;
        }
        while (16 * per >= frame.Length);

        int align = per + 1;
        byte[] first = new byte[align];
        byte[] second = new byte[align];
        first[0] = 0x01;
        second[0] = 0x10;
        byte[] body = frame.ToBytes(2 * per);
        body.AsSpan(0, per).CopyTo(first.AsSpan(1));
        body.AsSpan(per, per).CopyTo(second.AsSpan(1));
        var last = new WmaBitWriter().Write(0x21, 8).Write((uint)(frame.Length - (16 * per)), w);
        for (int i = 16 * per; i < frame.Length; i++)
        {
            last.Write(frame[i] ? 1u : 0u, 1);
        }

        WmaStandardStreams.Result result = WmaStandardStreams.Run(2, 1, 8000, 1000, align, WmaStandardStreams.CodecData(2, 0x0003), [first, second, last.ToBytes(align)]);

        Assert.DoesNotContain(result.Findings, f => f.Severity != Severity.Info);
        Node stream = result.Parent.Children.Single(c => c.Name == "Windows Media Audio 2");
        Assert.Equal("1 frame", stream.Value!.ToString());
        Node superframes = Child(stream, "Superframes");
        Assert.Equal(["middle of a frame", "middle of a frame", "1 frame"], superframes.Children.Select(c => c.Value!.ToString()));
        Node frameNode = Assert.Single(Child(stream, "Frames").Children);
        Assert.Equal(3, frameNode.Ranges!.Count);
        Assert.Equal($"1 block, {frame.Length} bits", frameNode.Value!.ToString());
    }

    [Fact]
    public void AConfigurationTheDecodersDoNotOpenIsNotAnalyzed()
    {
        WmaStandardStreams.Result result = WmaStandardStreams.Run(2, 2, 44100, 4000, 0, WmaStandardStreams.CodecData(2, 1), [new byte[10]]);

        Assert.Contains(result.Findings, f => f.Code == "wma.format-unsupported");
        StreamInfo stream = Assert.Single(result.Streams);
        Assert.Equal(0, stream.Samples);
        Assert.Equal("not analyzed", result.Parent.Children.Single(c => c.Name == "Windows Media Audio 2").Value!.ToString());
    }

    [Fact]
    public void AnEncryptedStreamIsNotReadAndHasNoFormatFinding()
    {
        WmaStandardStreams.Result result = WmaStandardStreams.Run(2, 2, 44100, 4000, 0, WmaStandardStreams.CodecData(2, 1), [], encrypted: true);

        Assert.DoesNotContain(result.Findings, f => f.Code == "wma.format-unsupported");
        Assert.Equal("frames not read: encrypted", result.Parent.Children.Single(c => c.Name == "Windows Media Audio 2").Value!.ToString());
        Assert.Equal(44100, Assert.Single(result.Streams).Samples);
    }

    [Fact]
    public void FFmpegsPaddingNamesItsEncoder()
    {
        StdParameters p = NoiseParameters();
        Result result = RunFrame(p, WmaStandardStreams.MonoFrame(p, 20, 3));

        Assert.Equal("FFmpeg (wmav2 encoder)", Assert.Single(result.Run.Streams).Encoder);
        Assert.Equal(Confidence.High, Assert.Single(result.Run.Streams).EncoderConfidence);
    }

    [Theory]
    [InlineData(1, 3, Severity.Warning, false)]
    [InlineData(1, 6, Severity.Info, true)]
    [InlineData(2, 5, Severity.Warning, false)]
    [InlineData(2, 12, Severity.Info, true)]
    public void CodecDataOfAnotherSizeThanTheFormatDefinesIsReported(int version, int size, Severity severity, bool hasOptions)
    {
        byte[] data = new byte[size];
        WmaStandardStreams.Result result = WmaStandardStreams.Run(version, 1, 8000, 1000, 100, data, [new byte[100]]);

        Finding finding = Assert.Single(result.Findings, f => f.Code == "wma.codec-data-size");
        Assert.Equal(severity, finding.Severity);
        Assert.Equal(hasOptions, !finding.Message.Contains("takes none", StringComparison.Ordinal));
    }

    [Fact]
    public void AStreamHandedOverAsOnePayloadIsNotAnalyzed()
    {
        var parent = new Node("Stream", 0, 100);
        var context = new CodecContext(parent, new ByteReader(new MemoryStream(new byte[100])));
        new WmaStandardParser().Parse(context);

        Assert.Contains(context.Findings, f => f.Code == "wma.payload-not-analyzed");
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);
}
