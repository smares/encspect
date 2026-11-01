using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Lossless: what the frame-level reader finds in the committed fixtures (the table of docs/design/wma-lossless-format.md,
// which ffmpeg's decoder confirms through crosscheck_asf_fixtures.py) and what it does with damaged packets.
public class WmaLosslessTests
{
    // packets, frames, samples, seekable subframes, end trim
    [Theory]
    [InlineData("asf/external-windows-wma92lossless-stereo-44k-16.wma", 15, 44, 88200, 5, 1912)]
    [InlineData("asf/external-windows-wma92lossless-stereo-44k-24.wma", 29, 44, 88200, 5, 1912)]
    [InlineData("asf/external-windows-wma92lossless-5.1-48k-24.wma", 26, 12, 24000, 2, 576)]
    public void FixturesGiveTheDescriptionsTable(string name, int packets, int frames, long samples, int seekable, int trim)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(samples, stream.Samples);
        Assert.Empty(report.Findings);
        Node node = stream.Nodes[0];
        Assert.Equal($"{frames} frames", node.Value!.ToString());
        Node statistics = WmaStream.Child(node, "Statistics");
        Assert.Equal(packets.ToString(), WmaStream.Child(statistics, "Packets").Value!.ToString());
        Assert.Equal(frames.ToString(), WmaStream.Child(statistics, "Frames").Value!.ToString());
        Assert.StartsWith($"{seekable} subframes (", WmaStream.Child(statistics, "Seekable Subframes").Value!.ToString(), StringComparison.Ordinal);
        Assert.Equal(frames.ToString(), WmaStream.Child(statistics, "Subframes").Value!.ToString());
        Assert.Equal(trim, WmaStream.Child(statistics, "Trimmed Samples").Value!.AsInteger());
        Assert.StartsWith("0 subframes (", WmaStream.Child(statistics, "Raw PCM Subframes").Value!.ToString(), StringComparison.Ordinal);
        Node frameList = WmaStream.Child(node, "Frames");
        Assert.Equal(frames, frameList.Children.Count);
        Node last = frameList.Children[^1];
        Assert.Equal($"0 samples at the start, {trim} at the end", WmaStream.Child(last, "Trim").Value!.ToString());
        Assert.All(frameList.Children, f => Assert.Equal("1 subframe", WmaStream.Child(f, "Tile Header").Value!.ToString()));
    }

    // The first packets of the 16-bit stereo fixture, as the description's example gives them.
    [Fact]
    public void TheFirstPacketsAreTheDescriptionsExample()
    {
        Node node = Fixtures.AnalyzeMpeg(WmaStream.Lossless).Streams[0].Nodes[0];
        Node packets = WmaStream.Child(node, "Packets");
        Node frames = WmaStream.Child(node, "Frames");
        string[] values = [.. packets.Children.Take(4).Select(p => p.Value!.ToString()!)];
        Assert.Equal(["3 frames", "3 frames", "3 frames", "3 frames"], values);
        Assert.Equal([0L, 4028L, 6179L, 7613L], packets.Children.Take(4).Select(p => WmaStream.Child(p, "Bits of Previous Frame").Value!.AsInteger()!.Value));
        Assert.Equal([true, false, false, true], packets.Children.Take(4).Select(p => WmaStream.Child(p, "Has Seekable Frame in Packet").Value!.AsBoolean()!.Value));
        Assert.Equal("1 subframe, 37493 bits", frames.Children[0].Value!.ToString());
        Assert.Equal("1 subframe, 36834 bits", frames.Children[1].Value!.ToString());
        Assert.Equal("1 subframe, 36678 bits", frames.Children[2].Value!.ToString());
        Assert.Equal("Frame Start", packets.Children[0].Children.Last(c => c.Name.StartsWith("Frame", StringComparison.Ordinal)).Name);
        Assert.True(WmaStream.Child(frames.Children[0], "Has More Frames").Value!.AsBoolean());
        Assert.True(WmaStream.Child(frames.Children[1], "Has More Frames").Value!.AsBoolean());
        Assert.False(WmaStream.Child(frames.Children[2], "Has More Frames").Value!.AsBoolean());
        Node subframe = WmaStream.Child(frames.Children[0], "Subframe");
        Assert.True(WmaStream.Child(subframe, "Is Seekable Subframe").Value!.AsBoolean());
        Assert.Equal("order 1, scaling 6", WmaStream.Child(subframe, "Recursive Filter Parameters").Value!.ToString());
        Assert.Equal(1L, WmaStream.Child(subframe, "Quantization Step").Value!.AsInteger());
        Assert.Equal(5L, WmaStream.Child(subframe, "Mean Scaling").Value!.AsInteger());
    }

    // In the 5.1 fixture every frame spans several packets of 98,281 payload bits, and the packets between state a count that covers the payload.
    [Fact]
    public void FramesOfTheFiveChannelFixtureSpanSeveralPackets()
    {
        Node node = Fixtures.AnalyzeMpeg(WmaStream.LosslessFive).Streams[0].Nodes[0];
        Node packets = WmaStream.Child(node, "Packets");
        Assert.Equal(98281L, WmaStream.Child(packets.Children[1], "Bits of Previous Frame").Value!.AsInteger());
        Assert.Equal(98281L, WmaStream.Child(packets.Children[3], "Bits of Previous Frame").Value!.AsInteger());
        Assert.Equal("0 frames", packets.Children[1].Value!.ToString());
        Assert.Equal("1 frame", packets.Children[2].Value!.ToString());
        Node sixteen = packets.Children[16];
        Assert.Equal(98278L, WmaStream.Child(sixteen, "Bits of Previous Frame").Value!.AsInteger());
        Assert.Equal(3, WmaStream.Child(sixteen, "Frame Start").BitLength);
        Assert.Equal("12", WmaStream.Statistic(node, "Frames Spanning Packets"));
        Assert.Equal("1", WmaStream.Statistic(node, "Most Frames in a Packet"));
    }

    // Changing the sequence number of packet 5 loses 6 frames (12,288 samples) in ffmpeg 6.1.1 (the description's black-box check): the frames that
    // start in packet 4, dropped when packet 5 shows the gap, and those that start in packet 5, dropped when packet 6 does not follow either.
    [Fact]
    public void AWrongSequenceNumberCostsTheFramesOfThePacketsAround()
    {
        WmaStream stream = WmaStream.LoadLossless();
        stream.Objects[5][0] = (byte)((stream.Objects[5][0] & 0x0F) | 0xF0);
        CodecContext context = stream.RunLossless();
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Equal(2, gap.Count);
        Assert.Equal(88200 - 12288, context.Streams[0].Samples);
    }

    [Fact]
    public void TheSplicedBitIsAnInfoFinding()
    {
        WmaStream stream = WmaStream.LoadLossless();
        stream.Objects[3][0] |= 0x04;
        CodecContext context = stream.RunLossless();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.lossless-spliced");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(88200, context.Streams[0].Samples);
    }

    [Fact]
    public void ACutLastPacketGivesAnInvalidFrameAndStillCountsWhatADecoderOutputs()
    {
        WmaStream stream = WmaStream.LoadLossless();
        stream.Objects[^1] = stream.Objects[^1][..6000];
        CodecContext context = stream.RunLossless();
        Assert.Contains(context.Findings, f => f.Code == "wma.packet-short");
        Assert.DoesNotContain(context.Findings, f => f.Code == "parse.failed");
        // The cut frame runs past its bits and is output whole, as a decoder does (ffmpeg: 43 frames, 88,064 samples).
        Assert.Equal(88_064, context.Streams[0].Samples);
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid");
    }

    public static TheoryData<string, string> Unsupported => new()
    {
        { "short data", "the codec-specific data is 17 bytes, and FFmpeg's decoder reads 18 or more" },
        { "sample size 8", "the sample size is 8 bits, and only 16 and 24 are read" },
        { "block alignment 0", "the block alignment is 0" },
        { "64 subframes", "64 subframes per frame (64 or 128)" },
        { "nine channels", "9 channels" },
        { "no sample rate", "no sample rate" },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void AConfigurationThatFailsACheckIsNotAnalyzed(string change, string reason)
    {
        WmaStream stream = WmaStream.LoadLossless();
        switch (change)
        {
            case "short data":
                stream.Data = stream.Data[..17];
                break;
            case "sample size 8":
                stream.Data[0] = 8;
                break;
            case "block alignment 0":
                stream.Format = stream.Format with { BlockAlign = 0 };
                break;
            case "64 subframes":
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x01B1);
                break;
            case "nine channels":
                stream.Format = stream.Format with { Channels = 9 };
                BinaryPrimitives.WriteUInt32LittleEndian(stream.Data.AsSpan(2), 0x1FF);
                break;
            case "no sample rate":
                stream.Format = stream.Format with { SampleRate = 0 };
                break;
        }

        CodecContext context = stream.RunLossless();
        if (change == "short data")
        {
            // One problem, one finding: the size of the data, which the stream's treatment follows from.
            Assert.Single(context.Findings, f => f.Code == "wma.codec-data-size");
            Assert.DoesNotContain(context.Findings, f => f.Code == "wma.format-unsupported");
            Assert.Equal("not analyzed", WmaStream.StreamNode(context).Value!.ToString());
            return;
        }

        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.format-unsupported");
        Assert.Equal($"The stream is not analyzed: {reason}.", finding.Message);
        Assert.Equal("not analyzed", WmaStream.StreamNode(context).Value!.ToString());
        Assert.Equal(0, context.Streams[0].Samples);
    }

    // The sample size is the low byte of its field, as a decoder keeps it in 8 bits.
    [Fact]
    public void TheSampleSizeIsTheLowByteOfItsField()
    {
        WmaStream stream = WmaStream.LoadLossless();
        BinaryPrimitives.WriteUInt16LittleEndian(stream.Data, 0x0110);
        CodecContext context = stream.RunLossless();
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.format-unsupported");
        Assert.Equal(88200, context.Streams[0].Samples);
    }
}
