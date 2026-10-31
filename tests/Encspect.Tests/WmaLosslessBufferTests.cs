using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Lossless: the decoder's buffer for the saved run (32,768 bytes per channel) and Encspect's own limit on the frames it
// records (docs/design/wma-lossless-format.md, "Frames in packets"; docs/formats/asf-wma.md, "WMA Lossless").
public class WmaLosslessBufferTests
{
    // 60 raw PCM frames of one channel, 32,774 bits each. The frame counts are those of a decoder's packet loop with the two kinds of refused save:
    // a refused append of the next packet's bits that finish the last frame of a run drops the run and no more (the next packet's number is
    // checked, and the run saved from this packet's tail survives); a refused save of a packet's own tail leaves the loss pending into the
    // next packet. With the pending loss kept for both, the same streams gave 45, 30, 15, 0, 14, 13 and 5 frames.
    [Theory]
    [InlineData(29_000, 52)]
    [InlineData(29_500, 44)]
    [InlineData(30_000, 36)]
    [InlineData(30_500, 21)]
    [InlineData(31_000, 28)]
    [InlineData(31_500, 20)]
    [InlineData(32_000, 12)]
    public void ARefusedAppendIsNoLossAndARefusedTailIs(int blockAlignment, int frames)
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.RawStream(blockAlignment, 60), 1, blockAlignment);
        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
    }

    // Packets of 40,000 bytes are longer than the decoder's buffer, so every run is refused: no frame is read, and the finding says why. At
    // 20,000 bytes only the last packet, which does not start a frame and so only continues the run, pushes it over the limit.
    [Theory]
    [InlineData(40_000, 0)]
    [InlineData(20_000, 35)]
    public void ARunThatIsTooLongForTheBufferGivesAFinding(int blockAlignment, int frames)
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.RawStream(blockAlignment, 40), 1, blockAlignment);
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.run-too-long");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("32768 bytes", finding.Message.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
        Assert.DoesNotContain(context.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void AControlStreamFitsTheBuffer()
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.RawStream(12_000, 40), 1, 12_000);
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.run-too-long");
        Assert.Equal(40 * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
    }

    // Frames of 7 bits are valid, so a stream of media objects holds far more frames than objects. Past the limit the frames are read and counted
    // (the samples, the statistics) but not listed or described, and the series cover the listed ones.
    [Fact]
    public void FramesPastTheLimitAreCountedButNotListed()
    {
        const int frames = 1000;
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.TinyStream(3000, frames), 1, 3000, new WmaLosslessParser { FrameLimit = 100 });

        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
        Node stream = WmaStream.StreamNode(context);
        Assert.Equal("1000", WmaStream.Statistic(stream, "Frames"));
        Node list = WmaStream.Child(stream, "Frames");
        Assert.Equal(100, list.Children.Count);
        Assert.Equal("100 frames", list.Value!.ToString());
        Finding limit = Assert.Single(context.Findings, f => f.Code == "wma.records-limit");
        Assert.Contains("more than 100 frames", limit.Message, StringComparison.Ordinal);
        Assert.Contains("the 900 after them", limit.Message, StringComparison.Ordinal);
        Assert.Equal(100, context.Streams[0].Series.First(s => s.Name == "Bitrate").X.Length);
        Assert.Equal("1000", WmaStream.Statistic(stream, "Subframes"));
        Assert.DoesNotContain(context.Findings, f => f.Code is "parse.failed" or "decode.failed" or "wma.frame-invalid");
    }

    [Fact]
    public void AStreamWithinTheLimitHasNoLimitFinding()
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.TinyStream(3000, 100), 1, 3000, new WmaLosslessParser { FrameLimit = 100 });
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.records-limit");
        Assert.Equal(100, WmaStream.Child(WmaStream.StreamNode(context), "Frames").Children.Count);
    }

    // 3,000,000 frames of 7 bits (one seekable frame first) in 88 media objects of 30,000 bytes: without a limit the records of the frames take
    // about 130 bytes each and the heap grows by hundreds of megabytes; with it the records are those of the first 524,288 frames.
    [Fact]
    public void MinimalFramesStayBounded()
    {
        const int frames = 3_000_000;
        List<byte[]> objects = LosslessBuilder.TinyStream(30_000, frames);
        Assert.True(objects.Count < 100);
        long before = GC.GetTotalMemory(true);
        CodecContext context = LosslessBuilder.Run(objects, 1, 30_000);
        long kept = GC.GetTotalMemory(true) - before;

        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
        Assert.Equal(WmaPacketReader.MaxFrames, WmaStream.Child(WmaStream.StreamNode(context), "Frames").Children.Count);
        Assert.Contains(context.Findings, f => f.Code == "wma.records-limit");
        Assert.True(kept < WmaPacketReader.MaxFrames * 160L, $"The report keeps {kept / WmaPacketReader.MaxFrames} bytes per listed frame.");
        GC.KeepAlive(context);
    }
}
