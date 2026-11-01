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
    [Theory]
    [InlineData(26_000, 60)]
    [InlineData(26_500, 60)]
    [InlineData(27_000, 53)]
    [InlineData(27_500, 60)]
    [InlineData(28_000, 60)]
    [InlineData(28_500, 60)]
    [InlineData(29_000, 45)]
    [InlineData(29_500, 9)]
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

    // At 29,500 the stray run's first frame is rejected for its end trim (no loss) and the frame read on from it fails in a later call. The
    // decoder keeps the run's bits held, so at the next header step the count bits are appended to them and refused (268,734 bits do not fit
    // 262,144): they are not passed, and the packet's payload becomes a stray run again. ffmpeg logs four refusals; the reader reports four.
    [Fact]
    public void ALaterFrameErrorKeepsTheBitsHeld()
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.RawStream(29_500, 60), 1, 29_500);
        Assert.Equal(4, Assert.Single(context.Findings, f => f.Code == "wma.run-too-long").Count);
    }

    // Streams of 60 raw frames with a sequence number changed, and with a media object cut to two bytes (the count then reads as 0, or, with
    // the second byte 0xFF, above 0). The frame counts are ffmpeg 6.1.1's, from the same objects in Matroska and, for the gaps, in ASF.
    [Theory]
    [InlineData(29_500, 2, 16, 3)]
    [InlineData(29_500, 3, 9, 4)]
    [InlineData(29_500, 4, 9, 4)]
    [InlineData(30_000, 2, 8, 3)]
    [InlineData(30_000, 4, 15, 3)]
    [InlineData(31_000, 6, 7, 4)]
    [InlineData(32_000, 3, 1, 6)]
    [InlineData(29_000, 3, 31, 1)]
    public void ASequenceGapKeepsTheHeldBitsUnderTheBufferRule(int blockAlignment, int gap, int frames, int refusals)
    {
        CodecContext context = LosslessBuilder.Run(LosslessBuilder.RawStream(blockAlignment, 60, k => k == gap ? 9 : k & 15), 1, blockAlignment);
        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
        Assert.Equal(refusals, context.Findings.Where(f => f.Code == "wma.run-too-long").Sum(f => f.Count));
    }

    // A media object shorter than its header: with a count that reads as 0 the run it does not touch stays held; with one above 0 the run is
    // emptied. ffmpeg's frame counts, from Matroska. (ffmpeg also logs a refused buffer for the short packet itself, which the reader does not
    // report as a run that is too long.)
    [Theory]
    [InlineData(29_500, 3, false, 9, 3)]
    [InlineData(29_500, 3, true, 9, 3)]
    [InlineData(30_000, 3, false, 15, 2)]
    [InlineData(31_000, 3, true, 16, 3)]
    [InlineData(32_000, 3, false, 1, 4)]
    public void APacketShorterThanItsHeaderLeavesTheLossPending(int blockAlignment, int index, bool hasCount, int frames, int refusals)
    {
        List<byte[]> objects = LosslessBuilder.RawStream(blockAlignment, 60);
        objects[index] = hasCount ? [objects[index][0], 0xFF] : objects[index][..2];
        CodecContext context = LosslessBuilder.Run(objects, 1, blockAlignment);
        Assert.Equal(frames * (long)LosslessBuilder.FrameLength, context.Streams[0].Samples);
        Assert.Equal(refusals, context.Findings.Where(f => f.Code == "wma.run-too-long").Sum(f => f.Count));
    }

    // The run's bit offset in its first byte counts at an append. Packets of 16,383 bytes: the header is 23 bits, so a tail saved from the
    // payload's first bit starts at offset 7; packet 1 only continues it (its count covers the payload), and packet 2's count of 55 bits is
    // refused (7 + 131,041 + 131,041 + 55 = 262,144) where 54 fits.
    [Theory]
    [InlineData(54, 0)]
    [InlineData(55, 1)]
    public void TheOffsetOfTheRunCountsAtAnAppend(int count, int refusals)
    {
        const int blockAlignment = 16_383;
        int width = LosslessBuilder.WidthOf(blockAlignment);
        List<byte[]> objects = [Object(blockAlignment, width, 0, 0), Object(blockAlignment, width, 1, 8 * blockAlignment), Object(blockAlignment, width, 2, count)];
        CodecContext context = LosslessBuilder.Run(objects, 1, blockAlignment);
        Assert.Equal(refusals, context.Findings.Where(f => f.Code == "wma.run-too-long").Sum(f => f.Count));
    }

    // The +8 at a save: the tail of packet 0 (payload from its first bit, offset 1) takes 262,143 bits at 32,771 bytes (1 + 262,143 + 8 reaches
    // 32,769 bytes) and 262,135 at 32,770.
    [Theory]
    [InlineData(32_770, 0)]
    [InlineData(32_771, 1)]
    public void TheSaveRuleAddsEightBits(int blockAlignment, int refusals)
    {
        int width = LosslessBuilder.WidthOf(blockAlignment);
        CodecContext context = LosslessBuilder.Run([Object(blockAlignment, width, 0, 0)], 1, blockAlignment);
        Assert.Equal(refusals, context.Findings.Where(f => f.Code == "wma.run-too-long").Sum(f => f.Count));
    }

    // The bits that finish a lost frame are read from the bit offset the last save of a tail left (7 here), not from their first bit. Packet 0's
    // tail is saved at offset 7; packet 1 states a wrong sequence number and covers its payload, which empties the run; packet 2 states 32,781
    // bits that finish a frame: seven zero bits and a raw PCM frame. Read from bit 7 it is a whole frame (2,048 samples); from bit 0 it is not.
    [Fact]
    public void TheLostFrameIsReadFromTheOffsetOfTheLastSave()
    {
        const int blockAlignment = 9_000;
        int width = LosslessBuilder.WidthOf(blockAlignment);
        var frame = new LosslessBits();
        frame.Zeros(7);
        LosslessBuilder.RawFrame(frame, 1);
        frame.PutBit(false);
        byte[] third = Object(blockAlignment, width, 6, (int)frame.Count, frame);
        List<byte[]> objects = [Object(blockAlignment, width, 0, 0), Object(blockAlignment, width, 5, 8 * blockAlignment), third];
        CodecContext context = LosslessBuilder.Run(objects, 1, blockAlignment);
        Assert.Equal(LosslessBuilder.FrameLength, context.Streams[0].Samples);
    }

    // A media object of zero payload bits except a count, and the bits that follow the header where given.
    private static byte[] Object(int blockAlignment, int width, int sequence, int count, LosslessBits? payload = null)
    {
        var bits = new LosslessBits();
        bits.Put(4, (ulong)sequence);
        bits.Put(2, 0);
        bits.Put(width, (ulong)count);
        long header = bits.Count;
        byte[] packet = new byte[blockAlignment];
        for (long b = 0; b < header; b++)
        {
            if (bits.Bit(b))
            {
                packet[b >> 3] |= (byte)(0x80 >> (int)(b & 7));
            }
        }

        for (long b = 0; payload is not null && b < payload.Count; b++)
        {
            if (payload.Bit(b))
            {
                long at = header + b;
                packet[at >> 3] |= (byte)(0x80 >> (int)(at & 7));
            }
        }

        return packet;
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
