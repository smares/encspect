using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Professional: what the frame-level reader finds in the committed fixtures (the figures of docs/design/wma-pro-format.md,
// which ffmpeg's decoder confirms through crosscheck_asf_fixtures.py), and what it does with damaged packets.
public class WmaProTests
{
    [Theory]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-128.wma", 10, 66, 131788, 8, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-128-sdk.wma", 10, 66, 131788, 7, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-384-sdk.wma", 9, 66, 131788, 0, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-64.wma", 11, 66, 131788, 9, 160)]
    [InlineData("asf/external-windows-wma10pro-vbr-stereo-44k-q98.wma", 3, 66, 131788, 2, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-5.1-48k-384.wma", 9, 72, 144000, 0, 493)]
    [InlineData("asf/external-windows-wma10pro-vbr-5.1-48k-q50.wma", 5, 72, 144000, 4, 493)]
    public void FixturesGiveTheirPacketsFramesAndSamples(string name, int packets, int frames, long samples, int spanning, int subframes)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(samples, stream.Samples);
        Assert.Empty(report.Findings);
        Node node = stream.Nodes[0];
        Assert.Equal($"{frames} frames", node.Value!.ToString());
        Assert.Equal(packets, Assert.Single(Count(node, "Packets")).Children.Count);
        Assert.Equal(frames, Assert.Single(Count(node, "Frames")).Children.Count);
        Node statistics = WmaStream.Child(node, "Statistics");
        Assert.Equal(spanning.ToString(), WmaStream.Child(statistics, "Frames Spanning Packets").Value!.ToString());
        Assert.Equal(subframes.ToString(), WmaStream.Child(statistics, "Subframes").Value!.ToString());
    }

    private static IEnumerable<Node> Count(Node node, string name) => node.Children.Where(c => c.Name == name);

    // The first frame states a trim of the whole frame at the start, the last one at the end (1,332 samples), so the stream is the source's length:
    // 66 x 2,048 - 2,048 - 1,332.
    [Fact]
    public void TheTrimsCutTheStreamToItsSourceLength()
    {
        Node frames = Assert.Single(Count(Fixtures.AnalyzeMpeg(WmaStream.Pro).Streams[0].Nodes[0], "Frames"));
        Assert.Equal("2048 samples at the start, 0 at the end", WmaStream.Child(frames.Children[0], "Trim").Value!.ToString());
        Assert.Equal("0 samples at the start, 1332 at the end", WmaStream.Child(frames.Children[^1], "Trim").Value!.ToString());
        Assert.All(frames.Children.Skip(1).Take(64), f => Assert.Equal("none", WmaStream.Child(f, "Trim").Value!.ToString()));
    }

    // Frame 1 of the stereo fixture is the description's worked example: it starts at bit 2 of the byte at 2,311 and is 2,341 bits long.
    [Fact]
    public void TheWorkedExampleFrameIsRead()
    {
        Node frames = Assert.Single(Count(Fixtures.AnalyzeMpeg(WmaStream.Pro).Streams[0].Nodes[0], "Frames"));
        Node frame = frames.Children[1];
        Assert.Equal(2311, frame.Offset);
        Assert.Equal("1 subframe, 2341 bits", frame.Value!.ToString());
        Assert.Equal(2341L, WmaStream.Child(frame, "Frame Length").Value!.AsInteger());
        Assert.Equal(2, WmaStream.Child(frame, "Frame Length").BitOffset);
        Node tile = WmaStream.Child(frame, "Tile Header");
        Assert.Equal("1 subframe", tile.Value!.ToString());
        Assert.Equal(2, tile.BitLength);
        Assert.Equal("2048", WmaStream.Child(tile, "Channel 1 Subframes").Value!.ToString());
        Node subframe = WmaStream.Child(frame, "Subframe");
        Assert.Equal("2048 samples at 0, channels 1 2", subframe.Value!.ToString());
        Node transform = WmaStream.Child(subframe, "Channel Transform");
        Assert.Equal("channels 1 2, transform in 26 bands", WmaStream.Child(transform, "Channel Group").Value!.ToString());
        Assert.Equal(83L, WmaStream.Child(subframe, "Quantization Step").Value!.AsInteger());
        Assert.Equal("a new set, step 2", WmaStream.Child(subframe, "Scale Factors (channel 1)").Value!.ToString());
        Assert.Equal("yes, yes", WmaStream.Child(subframe, "Coefficients Sent").Value!.ToString());
        Assert.True(WmaStream.Child(frame, "More Frames").Value!.AsBoolean());
        Assert.Equal(1L, WmaStream.Child(frame, "Frame Padding").Value!.AsInteger());
    }

    // A quantization step of -32 in its 6 bits sends escape groups while more than 5 bits are left before the frame's end. With none left the
    // step is the start value: the escape subtracts the 6-bit value, -32, which cancels the -32 added before it (docs/design/wma-pro-format.md,
    // "Quantization step"). Frame 1 of the stereo fixture is cut right after its step field by a shorter length prefix.
    [Fact]
    public void AStepOfMinus32WithNoRoomForAGroupStaysAtTheStartValue()
    {
        WmaStream stream = WmaStream.LoadPro();
        Node frame = Assert.Single(Count(WmaStream.StreamNode(stream.RunPro()), "Frames")).Children[1];
        Node step = WmaStream.Child(WmaStream.Child(frame, "Subframe"), "Quantization Step");
        Assert.Equal(83L, step.Value!.AsInteger());
        int Bit(Node n) => (int)((n.Offset - 200) * 8) + (n.BitOffset ?? 0);
        int stepStart = Bit(step);
        int frameStart = Bit(WmaStream.Child(frame, "Frame Length"));
        WmaStream.SetBits(stream.Objects[0], stepStart, 6, 0b100000);
        WmaStream.SetBits(stream.Objects[0], frameStart, 16, (uint)(stepStart + 6 + 2 - frameStart));
        CodecContext context = stream.RunPro();
        Node changed = Assert.Single(Count(WmaStream.StreamNode(context), "Frames")).Children[1];
        Assert.Equal(90L, WmaStream.Child(WmaStream.Child(changed, "Subframe"), "Quantization Step").Value!.AsInteger());
    }

    // The sequence number of packet 2 changed: the packet and the one after it show a gap, and a decoder drops the frame that crosses into each
    // (frames 12 and 17), so two frames of 2,048 samples are gone.
    [Fact]
    public void AWrongSequenceNumberCostsTheFramesThatCrossIntoTwoPackets()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[2][0] = (byte)((stream.Objects[2][0] & 0x0F) | 0xA0);
        CodecContext context = stream.RunPro();
        Finding gap = Assert.Single(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.Equal(2, gap.Count);
        Assert.Equal(Severity.Warning, gap.Severity);
        Assert.StartsWith("2 packets' sequence numbers do not follow the packet before (the first is 10 for 2)", gap.Message, StringComparison.Ordinal);
        Assert.Equal("Packet", gap.Node!.Name);
        Assert.Equal(131788 - (2 * 2048), context.Streams[0].Samples);
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.frame-invalid");
    }

    // A media object shorter than the block alignment is not read and counts as a loss: the next packet's frame that crosses is dropped and its
    // sequence number is not checked.
    [Fact]
    public void AShortObjectIsALoss()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[2] = stream.Objects[2][..100];
        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.packet-short");
        Assert.StartsWith("A media object holds fewer bytes than the block alignment of 5945, which a decoder rejects (wmaprodec.c); the first is at 0x", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.packet-sequence");
        Node packet = WmaStream.Child(WmaStream.StreamNode(context), "Packets").Children[2];
        Assert.Equal("not read", packet.Value!.ToString());
        Assert.True(context.Streams[0].Samples < 131788);
    }

    [Fact]
    public void ALongObjectIsReadAsOnePacketAndSaysSo()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[1] = [.. stream.Objects[1], .. new byte[50]];
        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings);
        Assert.Equal("wma.packet-long", finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(131788, context.Streams[0].Samples);
    }

    // The length prefix of the first frame is raised by 1: the frame's data ends where it did, which is no longer where the prefix says.
    [Fact]
    public void ALengthPrefixThatIsNotWhereTheDataEndsMakesTheFrameInvalid()
    {
        WmaStream stream = WmaStream.LoadPro();
        uint length = WmaStream.GetBits(stream.Objects[0], 22, 16);
        WmaStream.SetBits(stream.Objects[0], 22, 16, length + 1);
        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.frame-invalid");
        Assert.Equal("A frame is invalid: its length prefix is not where its data ends; a decoder drops it and the frame that crosses into the next packet. The first is at 0xCA.", finding.Message);
        Assert.Equal("Frame", finding.Node!.Name);
        Assert.StartsWith("invalid: its length prefix", finding.Node.Value!.ToString(), StringComparison.Ordinal);
        Node statistics = WmaStream.Child(WmaStream.StreamNode(context), "Statistics");
        Assert.Equal("1", WmaStream.Child(statistics, "Invalid Frames").Value!.ToString());
        Assert.True(context.Streams[0].Samples < 131788);
    }

    public static TheoryData<string, string> Unsupported => new()
    {
        { "block alignment 0", "the block alignment is 0" },
        { "short data", "the codec-specific data is 17 bytes, and FFmpeg's decoder reads 18 or more" },
        { "sample size 0", "the sample size is 0 bits (0, or above 32)" },
        { "sample size 33", "the sample size is 33 bits (0, or above 32)" },
        { "huge block alignment", "the block alignment is too large" },
        { "64 subframes", "64 subframes per frame (64 or 128)" },
        { "128 subframes", "128 subframes per frame (64 or 128)" },
        { "short subframes", "subframes of 32 samples (below 64)" },
        { "large frame", "a frame of more than 8,192 samples" },
        { "no sample rate", "no sample rate" },
        { "no channels", "no channels" },
        { "nine channels", "9 channels" },
        { "more channels than the format", "6 channels in the channel mask against 2 in the format" },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void AConfigurationThatFailsACheckIsNotAnalyzed(string change, string reason)
    {
        WmaStream stream = WmaStream.LoadPro();
        switch (change)
        {
            case "block alignment 0":
                stream.Format = stream.Format with { BlockAlign = 0 };
                break;
            case "short data":
                stream.Data = stream.Data[..17];
                break;
            case "sample size 0":
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data, 0);
                break;
            case "sample size 33":
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data, 33);
                break;
            case "huge block alignment":
                stream.Format = stream.Format with { BlockAlign = 1 << 22 };
                break;
            case "64 subframes":
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x00F0);
                break;
            case "128 subframes":
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x00F8);
                break;
            case "short subframes":
                stream.Format = stream.Format with { SampleRate = 8000 };
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x00D6);
                break;
            case "large frame":
                stream.Format = stream.Format with { SampleRate = 192000 };
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x00E2);
                break;
            case "no sample rate":
                stream.Format = stream.Format with { SampleRate = 0 };
                BinaryPrimitives.WriteUInt16LittleEndian(stream.Data.AsSpan(14), 0x0050);
                break;
            case "no channels":
                stream.Format = stream.Format with { Channels = 0 };
                BinaryPrimitives.WriteUInt32LittleEndian(stream.Data.AsSpan(2), 0);
                break;
            case "nine channels":
                stream.Format = stream.Format with { Channels = 9 };
                BinaryPrimitives.WriteUInt32LittleEndian(stream.Data.AsSpan(2), 0x1FF);
                break;
            case "more channels than the format":
                BinaryPrimitives.WriteUInt32LittleEndian(stream.Data.AsSpan(2), 0x3F);
                break;
        }

        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.format-unsupported");
        Assert.Equal($"The stream is not analyzed: {reason}.", finding.Message);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.packet-short" or "wma.packet-sequence" or "wma.frame-invalid");
        Assert.Equal("not analyzed", WmaStream.StreamNode(context).Value!.ToString());
        Assert.Equal(0, context.Streams[0].Samples);
        Assert.Equal(0.0, context.Streams[0].Duration);
        Assert.Null(context.Streams[0].AverageBitrate);
    }

    [Fact]
    public void ShortCodecDataHasItsOwnFindingToo()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Data = stream.Data[..10];
        CodecContext context = stream.RunPro();
        Finding size = Assert.Single(context.Findings, f => f.Code == "wma.codec-data-size");
        Assert.Equal("The codec-specific data of Windows Media Audio Professional is 10 bytes; FFmpeg's decoder needs 18 and refuses fewer.", size.Message);
        Assert.Contains(context.Findings, f => f.Code == "wma.format-unsupported");
    }

    // The last packet is left out, so the packet before it ends with the start of a frame that nothing finishes.
    [Fact]
    public void AFrameThatNoPacketFinishesIsReported()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects.RemoveAt(stream.Objects.Count - 1);
        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.frame-unfinished");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Frames", finding.Node!.Name);
        Assert.StartsWith("The last packet ends with ", finding.Message, StringComparison.Ordinal);
        Node packets = WmaStream.Child(WmaStream.StreamNode(context), "Packets");
        Assert.Equal("Unfinished Frame", packets.Children[^1].Children[^1].Name);
    }

    // Without the length prefix a frame's end is found by reading it, and the frames of a packet are read when the next one has supplied their
    // end. A stream whose flag is cleared is read that way: the frames it finds are garbage, but the reading ends cleanly.
    [Fact]
    public void ClearingTheLengthPrefixFlagStillGivesACleanReport()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Data[14] &= 0xBF;
        CodecContext context = stream.RunPro();
        Assert.DoesNotContain(context.Findings, f => f.Code == "parse.failed");
        Node node = WmaStream.StreamNode(context);
        Assert.All(WmaStream.Child(node, "Packets").Children, p => Assert.Equal("not read", p.Value!.ToString()));
        Assert.Equal("false", WmaStream.Statistic(node, "Has Length Prefix"));
    }

    // More media objects than the reader keeps records for are counted and not read.
    [Fact]
    public void MoreThanTheLimitOfMediaObjectsAreCountedAndNotRead()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Format = stream.Format with { BlockAlign = 64 };
        stream.Objects = [.. Enumerable.Range(0, 262_144 + 7).Select(_ => new byte[64])];
        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings, f => f.Code == "wma.records-limit");
        Assert.Equal("The stream has more than 262144 media objects; the 7 after them are not read, so the statistics and the series cover the first 262144.", finding.Message);
    }

    [Fact]
    public void TheFiveChannelFixtureNamesItsLowFrequencyChannelAndLayout()
    {
        Report report = Fixtures.AnalyzeMpeg(WmaStream.ProFive);
        Node node = report.Streams[0].Nodes[0];
        Node statistics = WmaStream.Child(node, "Statistics");
        Assert.Equal("4", WmaStream.Child(statistics, "Low-Frequency Channel").Value!.ToString());
        Assert.Equal(6, report.Streams[0].Channels);
        Assert.Equal("5.1", report.Streams[0].ChannelLayoutName);
        Assert.Null(report.Streams[0].ChannelMode);
    }
}
