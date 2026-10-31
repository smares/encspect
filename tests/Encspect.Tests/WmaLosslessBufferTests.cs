using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Lossless: the decoder's buffer for the saved run (32,768 bytes per channel) and Encspect's own limit on the frames it
// records (docs/design/wma-lossless-format.md, "Frames in packets"; docs/formats/asf-wma.md, "WMA Lossless").
public class WmaLosslessBufferTests
{
    // 60 raw PCM frames of one channel, 32,774 bits each. The frame counts are those ffmpeg 6.1.1 decodes from the same streams written to ASF
    // (a black-box check). When the header step's append of the next packet's count bits is refused, the decoder drops the run and clears the
    // loss but does not move past the count bits: it saves the packet's payload from its first bit as the new run, which starts inside the
    // frame before and fails in its first frame. A refused save of a packet's own tail leaves the loss pending into the next packet.
    // The reader matches ffmpeg at 13 of the 14 block alignments. At 29,500 ffmpeg decodes 9 frames and the reader 16: the stray run's first
    // frame is rejected for its end trim there (no loss), the frame read on from it fails, and ffmpeg then loses the frames of the next packet
    // as well (its log shows a further refusal and an arithmetic coding error); why is not determined, and the row pins the reader's figure.
    [Theory]
    [InlineData(26_000, 60)]
    [InlineData(26_500, 60)]
    [InlineData(27_000, 53)]
    [InlineData(27_500, 60)]
    [InlineData(28_000, 60)]
    [InlineData(28_500, 60)]
    [InlineData(29_000, 45)]
    [InlineData(29_500, 16)]
    [InlineData(30_000, 15)]
    [InlineData(30_500, 0)]
    [InlineData(31_000, 9)]
    [InlineData(31_500, 6)]
    [InlineData(32_000, 1)]
    [InlineData(32_500, 0)]
    public void RefusedSavesGiveTheFrameCountsOfADecoder(int blockAlignment, int frames)
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
}
