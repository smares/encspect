using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Windows Media Audio Professional: what the frame-level reader finds in the committed fixtures (the figures of docs/design/wma-pro-format.md,
// which ffmpeg's decoder confirms through crosscheck_asf_fixtures.py), and what it does with damaged packets.
public class WmaProTests
{
    // A file cut inside its last media object: the object's frames are counted from its header and length prefixes, and the walk ends at the frame
    // whose trailer bit says no frame follows, so the padding after the last frame is no frame. The whole file has 66 frames.
    [Theory]
    [InlineData(500, 135_168L, 16_384L)]
    [InlineData(1_500, 135_168L, 16_384L)]
    [InlineData(4_500, 126_976L, 8_192L)]
    public void ACutLastObjectCountsTheFramesItDeclaresAndNoPadding(int cut, long coded, long lost)
    {
        byte[] whole = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf/external-windows-wma10pro-cbr-stereo-44k-128.wma"));
        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("cut.wma", new MemoryStream(whole[..^cut]));
        StreamInfo stream = Assert.Single(report.Streams);
        Node statistics = WmaStream.Child(report.Streams[0].Nodes[0], "Statistics");
        Assert.Equal(coded, WmaStream.Child(statistics, "Coded Samples").Value!.AsInteger());
        Assert.Equal(lost, stream.LostSamples);
        Assert.True(coded <= 66 * 2048L);
    }

    // Bytes of a packet changed so that a frame is invalid: with the length prefix its prefix still says where the next frame starts, so the
    // frames after it in the packet (and the one it announces into the next packet) are declared and in the file, thrown away unread by the loss;
    // each counts a frame length and is lost, and the stream keeps the 66 frames of the file.
    [Theory]
    [InlineData(12_000)]
    [InlineData(20_000)]
    [InlineData(45_000)]
    public void FramesAfterAnInvalidFrameInItsPacketAreLost(int at)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf/external-windows-wma10pro-cbr-stereo-44k-128.wma"));
        for (int i = 0; i < 6; i++)
        {
            data[at + i] ^= 0xA5;
        }

        Report report = Encspect.Host.Analyzer.CreateDefault().Analyze("damaged.wma", new MemoryStream(data));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Contains(report.Findings, f => f.Code == "wma.frame-invalid");
        Node statistics = WmaStream.Child(stream.Nodes[0], "Statistics");
        Assert.Equal(66 * 2048L, WmaStream.Child(statistics, "Coded Samples").Value!.AsInteger());
        Assert.True(stream.LostSamples > 2048);
        Assert.Equal(0, stream.LostSamples % 2048);
    }

    [Theory]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-128.wma", 10, 66, 132300, 8, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-128-sdk.wma", 10, 66, 132300, 7, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-384-sdk.wma", 9, 66, 132300, 0, 209)]
    [InlineData("asf/external-windows-wma10pro-cbr-stereo-44k-64.wma", 11, 66, 132300, 9, 160)]
    [InlineData("asf/external-windows-wma10pro-vbr-stereo-44k-q98.wma", 3, 66, 132300, 2, 209)]
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
        Assert.Equal("none at the start, 1332 at the end", WmaStream.Child(frames.Children[^1], "Trim").Value!.ToString());
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
        Assert.True(WmaStream.Child(frame, "Has More Frames").Value!.AsBoolean());
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

    // Microsoft's decoder outputs the samples the transform has completed (docs/design/wma-pro-format.md, "What Microsoft's decoder outputs"). The stereo
    // files end in two subframes of 1,024 samples, which gives (2,048 - 1,024) / 2 = 512 more than the frames' count; 66 x 2,048 + 512 - 2,048 - 1,332.
    [Fact]
    public void TheStereoFileCountsTheSamplesTheTransformHasCompleted()
    {
        CodecContext context = WmaStream.LoadPro().RunPro();
        Assert.Equal(132300, context.Streams[0].Samples);
        Node statistics = WmaStream.Child(WmaStream.StreamNode(context), "Statistics");
        Assert.StartsWith("Counted by the rule", WmaStream.Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.Equal("3380 samples", WmaStream.Child(statistics, "Trimmed Samples").Value!.ToString());
    }

    // The figures of the Windows runs (docs/development.md): variants of the fixtures and what the decoder gave for them.
    [Theory]
    [InlineData(66, 2048, 512, 1024, 2048, true, 2048, 1332, 132300)] // the fixture
    [InlineData(72, 2048, 2048, 2048, 2048, true, 2048, 1408, 144000)] // the 5.1 fixture: the low-frequency channel's subframe is a whole frame
    [InlineData(66, 2048, 512, 1024, 2048, true, 2048, 3000, 131072)] // an end trim above the last frame's output (2,560) cuts that output only
    [InlineData(66, 2048, 512, 1024, 2048, true, 2048, 2561, 131072)]
    [InlineData(66, 2048, 512, 1024, 2048, true, 2048, 2559, 131073)]
    [InlineData(66, 2048, 512, 1024, 2048, true, 0, 1332, 134348)] // a stated start trim of 0 cuts nothing
    [InlineData(66, 2048, 512, 1024, 2048, true, 4095, 1332, 130253)] // a start trim is not limited to the first frame's output
    [InlineData(66, 2048, 512, 1024, 2048, false, 0, 1332, 133068)] // none stated: 2,048 / 2 + 512 / 2 = 1,280
    [InlineData(72, 2048, 2048, 2048, 2048, false, 0, 1408, 144000)] // none stated, first size 2,048: 2,048
    [InlineData(58, 2048, 512, 1024, 2048, false, 0, 1332, 116684)] // `from-1` of the stereo file
    [InlineData(2, 2048, 512, 2048, 512, true, 2048, 1500, 768)] // `cut-1-end-1500`: the last frame's output is 1,280
    [InlineData(35, 2048, 512, 256, 2048, true, 2048, 500, 70028)] // `cut-34-end-500`: a last size of 256 gives 896 more
    [InlineData(2205, 4096, 256, 256, 256, true, 4096, 0, 9029504)] // `Classical_96_24_6_Q75_2_13.wma`: (4,096 - 256) / 2 = 1,920 more than 9,027,584
    public void TheRuleGivesTheFiguresOfMicrosoftsDecoder(long frames, long frame, int firstSize, int lastSize, int previousLastSize, bool hasStart, long start, long end, long expected)
    {
        Assert.Equal(expected, WmaPacketReader.DecoderRule(frames, frame, firstSize, lastSize, previousLastSize, hasStart, start, end));
    }

    // An end trim written into the last frame is cut from that frame's output, at most all of it: 3,000 samples remove the 2,560 the frame outputs.
    [Theory]
    [InlineData(1332u, 132300)]
    [InlineData(2000u, 131632)]
    [InlineData(3000u, 131072)]
    public void AnEndTrimOfTheLastFrameIsCutFromItsOutput(uint trim, long expected)
    {
        WmaStream stream = WmaStream.LoadPro();
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node at = WmaStream.Child(Assert.Single(Count(root, "Frames")).Children[^1], "Trim");
        Node packet = Assert.Single(Count(root, "Packets")).Children[^1];
        WmaStream.SetBits(stream.Objects[9], (int)((at.Offset - packet.Offset) * 8) + (at.BitOffset ?? 0) + 3, 12, trim);
        Assert.Equal(expected, stream.RunPro().Streams[0].Samples);
    }

    // The conditions of the rule: a stream that fails one keeps the frames' count (the first frame is not output, then the trims). Each damaged copy
    // is also what the Windows runs have no figures for.
    [Fact]
    public void ALossKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[2][0] = (byte)((stream.Objects[2][0] & 0x0F) | 0xA0);
        CodecContext context = stream.RunPro();
        // The frames the loss drops are declared and lost: they count in the samples, and the samples less the lost ones are the frames read.
        Assert.Equal(131788 - (2 * 2048), context.Streams[0].Samples - context.Streams[0].LostSamples);
        Assert.StartsWith("The samples counted from the frames", WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(context), "Statistics"), "Samples").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortObjectKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[9] = stream.Objects[9][..100];
        CodecContext context = stream.RunPro();
        Assert.StartsWith("The samples counted from the frames", WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(context), "Statistics"), "Samples").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongObjectKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects[1] = [.. stream.Objects[1], .. new byte[50]];
        Assert.Equal(131788, stream.RunPro().Streams[0].Samples);
    }

    [Fact]
    public void AnInvalidFrameKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        uint length = WmaStream.GetBits(stream.Objects[0], 22, 16);
        WmaStream.SetBits(stream.Objects[0], 22, 16, length + 1);
        CodecContext context = stream.RunPro();
        Assert.StartsWith("The samples counted from the frames", WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(context), "Statistics"), "Samples").Description, StringComparison.Ordinal);
    }

    // The stream cut to its first packet: the frames in it end with a trailer bit of 0, so nothing is lost, and the count is the rule's for eight frames.
    [Fact]
    public void AStreamCutAfterAWholeFrameStillFollowsTheRule()
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects = [stream.Objects[0]];
        CodecContext context = stream.RunPro();
        Assert.StartsWith("Counted by the rule", WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(context), "Statistics"), "Samples").Description, StringComparison.Ordinal);
        Assert.Equal((8 * 2048) - 2048, context.Streams[0].Samples);
    }

    // A stream of one frame: the first packet's first frame is made the last, its trailer bit cleared. The rule holds for it with no end trim
    // (`cut-0` of the Windows runs: 2,048 + (2,048 - 512) / 2 - 2,048 = 768).
    [Fact]
    public void AStreamOfOneFrameFollowsTheRule()
    {
        WmaStream stream = WmaStream.LoadPro();
        int frameBits = (int)WmaStream.GetBits(stream.Objects[0], 22, 16);
        WmaStream.SetBits(stream.Objects[0], 22 + frameBits - 1, 1, 0);
        stream.Objects = [stream.Objects[0]];
        CodecContext context = stream.RunPro();
        Node statistics = WmaStream.Child(WmaStream.StreamNode(context), "Statistics");
        Assert.Equal("1", WmaStream.Child(statistics, "Frames").Value!.ToString());
        Assert.StartsWith("Counted by the rule", WmaStream.Child(statistics, "Samples").Description, StringComparison.Ordinal);
        Assert.Equal(768, context.Streams[0].Samples);
    }

    // The length prefix of frame 12, which crosses from packet 1 into packet 2, raised by 1: the crossing frame fails its length check, which
    // is no loss for the reader, so only the invalid frame keeps the frames' count.
    [Fact]
    public void AnInvalidCrossingFrameKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node length = WmaStream.Child(Assert.Single(Count(root, "Frames")).Children[12], "Frame Length");
        Node packet = Assert.Single(Count(root, "Packets")).Children[1];
        int at = (int)((length.Offset - packet.Offset) * 8) + (length.BitOffset ?? 0);
        WmaStream.SetBits(stream.Objects[1], at, 16, WmaStream.GetBits(stream.Objects[1], at, 16) + 1);
        CodecContext context = stream.RunPro();
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid");
        Assert.DoesNotContain(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    // A stream of one frame that states an end trim as well (12 bits inserted into the first frame's trim, whose end flag is set): no run covers it, so
    // the frames' count stays.
    [Fact]
    public void AStreamOfOneFrameWithAnEndTrimKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node frame = Assert.Single(Count(root, "Frames")).Children[0];
        Node packet = Assert.Single(Count(root, "Packets")).Children[0];
        Node length = WmaStream.Child(frame, "Frame Length");
        Node trim = WmaStream.Child(frame, "Trim");
        int lengthAt = (int)((length.Offset - packet.Offset) * 8) + (length.BitOffset ?? 0);
        int trimAt = (int)((trim.Offset - packet.Offset) * 8) + (trim.BitOffset ?? 0);
        byte[] first = stream.Objects[0];
        string bits = BitString(first);
        Assert.Equal("11", bits.Substring(trimAt, 2));
        Assert.Equal('0', bits[trimAt + 14]);
        bits = string.Concat(bits.AsSpan(0, trimAt + 14), "1", Convert.ToString(500, 2).PadLeft(12, '0'), bits.AsSpan(trimAt + 15))[..(first.Length * 8)];
        Store(first, bits);
        uint newLength = WmaStream.GetBits(first, lengthAt, 16) + 12;
        WmaStream.SetBits(first, lengthAt, 16, newLength);
        WmaStream.SetBits(first, lengthAt + (int)newLength - 1, 1, 0);
        stream.Objects = [first];
        CodecContext context = stream.RunPro();
        Node statistics = WmaStream.Child(WmaStream.StreamNode(context), "Statistics");
        Assert.Equal("1", WmaStream.Child(statistics, "Frames").Value!.ToString());
        Assert.StartsWith(FramesCount, WmaStream.Child(statistics, "Samples").Description, StringComparison.Ordinal);
    }

    // The 5.1 file's first frame without a start trim (its flag cleared, 12 bits removed): the output starts F / 2 + the largest first subframe / 2
    // = 1,024 + 1,024 in, the low-frequency channel's whole frame deciding, as in the Windows run (`trim-off-first`): 147,456 - 2,048 - 1,408.
    [Fact]
    public void TheLargestFirstSubframeOverTheChannelsDecidesTheStartWithoutATrim()
    {
        WmaStream stream = WmaStream.LoadPro(WmaStream.ProFive);
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node frame = Assert.Single(Count(root, "Frames")).Children[0];
        Node packet = Assert.Single(Count(root, "Packets")).Children[0];
        Node length = WmaStream.Child(frame, "Frame Length");
        Node trim = WmaStream.Child(frame, "Trim");
        int lengthAt = (int)((length.Offset - packet.Offset) * 8) + (length.BitOffset ?? 0);
        int trimAt = (int)((trim.Offset - packet.Offset) * 8) + (trim.BitOffset ?? 0);
        byte[] first = stream.Objects[0];
        string bits = BitString(first);
        Assert.Equal("11", bits.Substring(trimAt, 2));
        // The trim "1 1 <12 bits> 0" becomes "1 0 0".
        bits = string.Concat(bits.AsSpan(0, trimAt + 1), "00", bits.AsSpan(trimAt + 15), new string('0', 12));
        Store(first, bits);
        // The length prefix of the 5.1 file is 18 bits wide (the block alignment of 16,384 bytes).
        WmaStream.SetBits(first, lengthAt, 18, WmaStream.GetBits(first, lengthAt, 18) - 12);
        CodecContext context = stream.RunPro();
        Assert.Empty(context.Findings);
        Assert.Equal(144000, context.Streams[0].Samples);
    }

    // Not tested here: the records limit (`Skipped > 0`, packets past WmaPacketReader.MaxPackets). A test needs 262,146 packets and takes about 4 s,
    // so that condition is left to code review.

    private const string FramesCount = "The samples counted from the frames";
    private const string RuleCount = "Counted by the rule fitted to the output of Microsoft";

    private static string DescriptionOfSamples(CodecContext context) =>
        WmaStream.Child(WmaStream.Child(WmaStream.StreamNode(context), "Statistics"), "Samples").Description!;

    private static string BitString(byte[] bytes) => string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

    private static void Store(byte[] bytes, string bits)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(bits.Substring(i * 8, 8), 2);
        }
    }

    // A stream whose first packet states bits that finish a frame before it starts inside a frame; a decoder drops them without a loss, and no
    // run covers such a stream.
    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void AStreamThatStartsInsideAFrameKeepsTheFramesCount(int first)
    {
        WmaStream stream = WmaStream.LoadPro();
        stream.Objects = stream.Objects[first..];
        CodecContext context = stream.RunPro();
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    // The stream cut after frame 3 with its trailer bit set and the bits after it zeroed (the variant cut-open-3 of make_wma_pro_variants.py): a
    // frame follows and no packet finishes one, so the last packet ends in an unfinished frame. The finding stays, and the rule of Microsoft's
    // decoder counts the 4 frames, the last of which ends in subframes of 128 samples: 4 x 2,048 + (2,048 - 128) / 2 - 2,048 = 7,104, which is
    // the figure Windows gave for that variant (run 41363084542); the frames' count is 3 x 2,048 = 6,144.
    [Fact]
    public void AnUnfinishedFrameAtTheEndGetsTheRulesCountAndKeepsItsFinding()
    {
        WmaStream stream = WmaStream.LoadPro();
        Node frame3 = Assert.Single(Count(WmaStream.StreamNode(stream.RunPro()), "Frames")).Children[3];
        Node length = WmaStream.Child(frame3, "Frame Length");
        int start = (int)((length.Offset - 200) * 8) + (length.BitOffset ?? 0);
        int bits = (int)WmaStream.GetBits(stream.Objects[0], start, 16);
        int end = start + bits;
        WmaStream.SetBits(stream.Objects[0], end - 1, 1, 1);
        for (int bit = end; bit < stream.Objects[0].Length * 8; bit++)
        {
            WmaStream.SetBits(stream.Objects[0], bit, 1, 0);
        }

        stream.Objects = [stream.Objects[0]];
        CodecContext context = stream.RunPro();
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-unfinished");
        Assert.StartsWith(RuleCount, DescriptionOfSamples(context), StringComparison.Ordinal);
        Node node = WmaStream.StreamNode(context);
        Assert.Equal("4", WmaStream.Statistic(node, "Frames"));
        Assert.Equal("7104 samples", WmaStream.Statistic(node, "Samples"));
    }

    // The last frame states a start trim of 100 as well (12 bits inserted, the length prefix raised): a start trim anywhere but in the first frame
    // is a case the runs do not cover, so the frames' count stays: 65 x 2,048 - 100 - 1,332.
    [Fact]
    public void AStartTrimInTheLastFrameKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro();
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node frame = Assert.Single(Count(root, "Frames")).Children[^1];
        Node packet = Assert.Single(Count(root, "Packets")).Children[^1];
        int packetStart = (int)packet.Offset;
        Node length = WmaStream.Child(frame, "Frame Length");
        int lengthAt = (int)((length.Offset - packetStart) * 8) + (length.BitOffset ?? 0);
        Node trim = WmaStream.Child(frame, "Trim");
        int trimAt = (int)((trim.Offset - packetStart) * 8) + (trim.BitOffset ?? 0);
        byte[] last = stream.Objects[^1];
        string bits = BitString(last);
        Assert.Equal("101", bits.Substring(trimAt, 3));
        bits = string.Concat(bits.AsSpan(0, trimAt + 1), "1", Convert.ToString(100, 2).PadLeft(12, '0'), bits.AsSpan(trimAt + 2));
        bits = bits[..(last.Length * 8)];
        Store(last, bits);
        WmaStream.SetBits(last, lengthAt, 16, WmaStream.GetBits(last, lengthAt, 16) + 12);
        CodecContext context = stream.RunPro();
        Assert.Empty(context.Findings);
        Assert.Equal(65 * 2048 - 100 - 1332, context.Streams[0].Samples);
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    // The 5.1 file's last frame has a subframe of every channel of 2,048 samples; a trim of 4,095 cuts that frame's output, 2,048, whichever
    // channel the sizes are read from for the frame before it (the largest over the channels of its last subframes is 2,048, a single channel's may be less).
    [Fact]
    public void TheLargestLastSubframeOverTheChannelsDecidesTheOutputOfTheFrameBeforeTheLast()
    {
        WmaStream stream = WmaStream.LoadPro(WmaStream.ProFive);
        Node root = WmaStream.StreamNode(stream.RunPro());
        Node at = WmaStream.Child(Assert.Single(Count(root, "Frames")).Children[^1], "Trim");
        Node packet = Assert.Single(Count(root, "Packets")).Children[^1];
        WmaStream.SetBits(stream.Objects[^1], (int)((at.Offset - packet.Offset) * 8) + (at.BitOffset ?? 0) + 3, 12, 4095);
        Assert.Equal(143360, stream.RunPro().Streams[0].Samples);
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
        // The frames the loss drops are declared and lost: they count in the samples, and the samples less the lost ones are the frames read.
        Assert.Equal(131788 - (2 * 2048), context.Streams[0].Samples - context.Streams[0].LostSamples);
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

        // The frame that no packet finishes counts in the coded stage and is lost (samples-definition.md, question 9 (c)).
        Node node = WmaStream.StreamNode(context);
        long frames = long.Parse(WmaStream.Statistic(node, "Frames"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"{(frames + 1) * 2048} samples", WmaStream.Statistic(node, "Coded Samples"));
        Assert.Equal("2048 samples", WmaStream.Statistic(node, "Lost Samples"));
        Assert.Equal(2048, context.Streams[0].LostSamples);
    }

    // The two fixtures rewritten without the length prefix (WithoutLengthPrefix): the frames that start in the last packet are read from the
    // run that packet leaves, so the stream has the frames, the trims and the samples of the original, and the rule of Microsoft's decoder
    // counts them. Windows gives the three public files without the prefix 10,803, 33,331 and 1,921 samples more than the frames before the
    // last packet's, which the read frames now give (Classical_44_16_6_256000_0_20.wma: 8,364,595 against 8,353,792).
    [Theory]
    [InlineData(WmaStream.Pro, 66, 132300)]
    [InlineData(WmaStream.ProFive, 72, 144000)]
    public void TheFramesOfTheLastPacketWithoutTheLengthPrefixAreRead(string fixture, int frames, long samples)
    {
        WmaStream original = WmaStream.LoadPro(fixture);
        WmaStream stream = original.WithoutLengthPrefix();
        CodecContext context = stream.RunPro();
        Node node = WmaStream.StreamNode(context);
        Assert.Empty(context.Findings);
        Assert.Equal("false", WmaStream.Statistic(node, "Has Length Prefix"));
        Assert.Equal(frames.ToString(), WmaStream.Statistic(node, "Frames"));
        Assert.Equal($"{samples} samples", WmaStream.Statistic(node, "Samples"));
        Assert.Equal(WmaStream.Statistic(WmaStream.StreamNode(original.RunPro()), "Trimmed Samples"), WmaStream.Statistic(node, "Trimmed Samples"));
        Assert.StartsWith(RuleCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    // A frame that starts in the last packet and fails (every third bit of the 600 bits that follow the first 8 bits of the first whole frame
    // there is set) is a finding, and the stream keeps the count of FFmpeg's decoder, which reads none of that packet's frames.
    [Fact]
    public void ADamagedFrameInTheLastPacketWithoutTheLengthPrefixKeepsTheFramesCount()
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        byte[] last = stream.Objects[^1];
        int first = 22 + (int)WmaStream.GetBits(last, 6, 16);
        for (int bit = first + 8; bit < first + 600; bit += 3)
        {
            WmaStream.SetBits(last, bit, 1, 1);
        }

        CodecContext context = stream.RunPro();
        Assert.Contains(context.Findings, f => f.Code == "wma.frame-invalid");
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    private static (int Frames, int LastSize) FramesAndLastSize(Node stream)
    {
        Node last = Assert.Single(Count(stream, "Frames")).Children[^1];
        int size = WmaStream.Child(last, "Tile Header").Children.Max(c => int.Parse(c.Value!.ToString()!.Split(',')[^1]));
        return (Assert.Single(Count(stream, "Frames")).Children.Count, size);
    }

    // The stream without the length prefix cut after a packet: the last packet's run ends inside a frame no packet finishes. As with the
    // prefix this is the information that the last packet ends in an unfinished frame, not an invalid frame, and the rule counts the frames
    // read: N x 2,048 + (2,048 - L) / 2 less the first frame's start trim of 2,048 (the cut stream has no end trim), L the largest, over the
    // channels, of the last subframe of the last frame. The basis is the rule fitted to Microsoft's decoder; the prefix variants cut open
    // (cut-open-3 and the like) are the ones Windows gave this figure for.
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void ARunTheLastPacketCannotFinishIsAnUnfinishedFrame(int packets)
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        stream.Objects = stream.Objects[..packets];
        CodecContext context = stream.RunPro();
        Node node = WmaStream.StreamNode(context);
        Finding finding = Assert.Single(context.Findings);
        Assert.Equal("wma.frame-unfinished", finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
        (int frames, int lastSize) = FramesAndLastSize(node);
        Assert.Equal(frames.ToString(), WmaStream.Statistic(node, "Frames"));
        Assert.StartsWith(RuleCount, DescriptionOfSamples(context), StringComparison.Ordinal);
        Assert.Equal($"{(frames * 2048) + ((2048 - lastSize) / 2) - 2048} samples", WmaStream.Statistic(node, "Samples"));
    }

    // The last frame says another follows (its trailer bit is set) and zero padding is all that is left: the padding is not read as a frame.
    // All 66 frames are read, the finding is the information, and the rule gives the original's 132,300.
    [Fact]
    public void ZeroPaddingAfterALastFrameThatSaysAnotherFollowsIsNotAFrame()
    {
        CodecContext context = WmaStream.LoadPro().WithoutLengthPrefix(true).RunPro();
        Node node = WmaStream.StreamNode(context);
        Finding finding = Assert.Single(context.Findings);
        Assert.Equal("wma.frame-unfinished", finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("66", WmaStream.Statistic(node, "Frames"));
        Assert.Equal("132300 samples", WmaStream.Statistic(node, "Samples"));
    }

    public static TheoryData<string> ProfessionalFixtures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "external-windows-wma10pro-*.wma").Order(StringComparer.Ordinal))
            {
                data.Add($"asf/{Path.GetFileName(path)}");
            }

            return data;
        }
    }

    // The 11 committed Professional fixtures (external-windows-wma10pro-*) rewritten without the length prefix have the frames of the original: as
    // many, with the same subframes in each, the statistics counted from them, and the samples and trimmed samples.
    [Theory]
    [MemberData(nameof(ProfessionalFixtures))]
    public void EveryProfessionalFixtureRewrittenWithoutTheLengthPrefixHasItsFrames(string fixture)
    {
        WmaStream original = WmaStream.LoadPro(fixture);
        CodecContext before = original.RunPro();
        CodecContext after = original.WithoutLengthPrefix().RunPro();
        Node source = WmaStream.StreamNode(before);
        Node rewritten = WmaStream.StreamNode(after);
        Assert.DoesNotContain(after.Findings, f => f.Code is "wma.frame-invalid" or "wma.frame-unfinished");
        string[] SubframeCounts(Node stream) => [.. Assert.Single(Count(stream, "Frames")).Children.Select(f => f.Value!.ToString()!.Split(',')[0])];
        Assert.Equal(SubframeCounts(source), SubframeCounts(rewritten));
        foreach (string name in SourceStatistics.Where(n => n != "Frames Spanning Packets"))
        {
            Assert.Equal(WmaStream.Statistic(source, name), WmaStream.Statistic(rewritten, name));
        }
    }

    // The 256 kbit/s source rewritten without the length prefix has its last frame start before the last packet and end in it, so the last
    // packet's run is only zero padding: nothing there, no finding. (In Microsoft's files the trailer bit of a frame that crosses into a packet
    // is 0 and frames still follow it, so the run is padding only because every bit of it is zero.)
    [Fact]
    public void ALastPacketThatIsOnlyZeroPaddingIsNothing()
    {
        CodecContext context = WmaStream.LoadPro("asf/external-windows-wma10pro-cbr-stereo-44k-256.wma").WithoutLengthPrefix().RunPro();
        Node node = WmaStream.StreamNode(context);
        Assert.Empty(context.Findings);
        Assert.Equal("66", WmaStream.Statistic(node, "Frames"));
        Assert.Equal("132300 samples", WmaStream.Statistic(node, "Samples"));
    }

    // The last packet is zeroed after its first 100 real bits (its first frame cannot be finished): not padding, since the bits are not all zero,
    // so the frame is an unfinished one and the stream is reported as cut.
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void ALastPacketDamagedAfterSomeRealBitsIsAnUnfinishedFrame(int kept)
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        byte[] last = stream.Objects[^1];
        int first = 22 + (int)WmaStream.GetBits(last, 6, 16);
        for (int bit = first + kept; bit < last.Length * 8; bit++)
        {
            WmaStream.SetBits(last, bit, 1, 0);
        }

        CodecContext context = stream.RunPro();
        Finding finding = Assert.Single(context.Findings);
        Assert.Equal("wma.frame-unfinished", finding.Code);
    }

    // The last media object is shorter than the block alignment: a loss is pending at the end, so the run of the packet before is dropped as at
    // every other packet, and the frames in it are not read: no invalid frame next to the packet's warning. The frames are those of the stream
    // without that object, less the frames that start in its last packet (the run the loss drops).
    [Fact]
    public void ALossPendingAtTheEndDropsTheLastRun()
    {
        WmaStream without = WmaStream.LoadPro().WithoutLengthPrefix();
        without.Objects.RemoveAt(without.Objects.Count - 1);
        Node withoutFrames = Assert.Single(Count(WmaStream.StreamNode(without.RunPro()), "Frames"));
        long lastPacket = 200 + ((without.Objects.Count - 1) * 20_000L);
        int expected = withoutFrames.Children.Count(f => f.Offset < lastPacket);
        Assert.True(expected < withoutFrames.Children.Count);

        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        stream.Objects[^1] = stream.Objects[^1][..100];
        CodecContext context = stream.RunPro();
        Assert.Contains(context.Findings, f => f.Code == "wma.packet-short");
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.frame-invalid" or "wma.frame-unfinished");
        Assert.Equal(expected.ToString(), WmaStream.Statistic(WmaStream.StreamNode(context), "Frames"));
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
    }

    // A stream of one packet has all its frames in the flush: "Most Frames in a Packet" counts them.
    [Fact]
    public void TheFlushedFramesCountForTheMostFramesInAPacket()
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        stream.Objects = stream.Objects[..1];
        Node node = WmaStream.StreamNode(stream.RunPro());
        int frames = Assert.Single(Count(node, "Frames")).Children.Count;
        Assert.True(frames > 1);
        Assert.Equal(frames.ToString(), WmaStream.Statistic(node, "Most Frames in a Packet"));
    }

    private static readonly string[] SourceStatistics =
    [
        "Frames", "Samples", "Trimmed Samples", "Frames Spanning Packets", "Most Subframes in a Frame", "Subframes", "Subframe Sizes", "Channels per Subframe", "Channel Groups",
        "Bands Transformed", "Frames with a Postprocessing Transform", "Frames with a Dynamic Range Gain", "Trimmed Frames", "Subframes with an Extended Header",
        "Subframes with Vector Counts", "Coded Channel Subframes", "Scale Factors Sent", "Scale Factors as Differences", "Second Coefficient Codebook",
        "Smallest Quantization Step", "Largest Quantization Step", "Non-Zero Coefficients", "Escape-Coded Coefficients",
    ];

    // The flush reads each frame once for the statistics: a stream rewritten without the length prefix has the figures of its source (the
    // subframes, groups, coefficients and the rest are counted from the same frames), whether it is built here or committed.
    [Theory]
    [InlineData(WmaStream.Pro)]
    [InlineData(WmaStream.ProFive)]
    public void TheFlushedFramesAreTalliedOnceInTheStatistics(string fixture)
    {
        WmaStream original = WmaStream.LoadPro(fixture);
        Node source = WmaStream.StreamNode(original.RunPro());
        Node rewritten = WmaStream.StreamNode(original.WithoutLengthPrefix().RunPro());
        foreach (string name in SourceStatistics.Where(n => n != "Frames Spanning Packets"))
        {
            Assert.Equal(WmaStream.Statistic(source, name), WmaStream.Statistic(rewritten, name));
        }
    }

    [Fact]
    public void TheCommittedFileWithoutTheLengthPrefixHasTheStatisticsOfItsSource()
    {
        Node source = Fixtures.AnalyzeMpeg(WmaStream.Pro).Streams[0].Nodes[0];
        Node derived = Fixtures.AnalyzeMpeg("asf/derived-windows-wma10pro-stereo-44k-128-no-length-prefix.asf").Streams[0].Nodes[0];
        Assert.Equal("false", WmaStream.Statistic(derived, "Has Length Prefix"));
        foreach (string name in SourceStatistics.Where(n => n != "Frames Spanning Packets"))
        {
            Assert.Equal(WmaStream.Statistic(source, name), WmaStream.Statistic(derived, name));
        }
    }

    // The packet limit cuts the stream where a run goes on in a packet nobody read: the run is dropped, with no frame-invalid next to
    // wma.records-limit.
    [Fact]
    public void ARunCutByTheRecordsLimitIsDropped()
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        CodecContext context = stream.Run(new WmaProParser { PacketLimit = 5 });
        Assert.Contains(context.Findings, f => f.Code == "wma.records-limit");
        Assert.DoesNotContain(context.Findings, f => f.Code is "wma.frame-invalid" or "wma.frame-unfinished");
    }

    // A stream without the length prefix that fails the rule (a sequence number out of order) keeps FFmpeg's count, which never reads the
    // frames of the last packet: (frames listed less those that start in the last packet, less the first, which only fills the overlap) x 2,048.
    [Fact]
    public void TheFallbackCountLeavesOutTheFramesOfTheLastPacket()
    {
        WmaStream stream = WmaStream.LoadPro().WithoutLengthPrefix();
        WmaStream.SetBits(stream.Objects[4], 0, 4, (WmaStream.GetBits(stream.Objects[4], 0, 4) + 5) & 15);
        CodecContext context = stream.RunPro();
        Assert.Contains(context.Findings, f => f.Code == "wma.packet-sequence");
        Assert.StartsWith(FramesCount, DescriptionOfSamples(context), StringComparison.Ordinal);
        Node node = WmaStream.StreamNode(context);
        long lastPacket = 200 + ((stream.Objects.Count - 1) * 20_000L);
        int listed = Assert.Single(Count(node, "Frames")).Children.Count;
        int flushed = Assert.Single(Count(node, "Frames")).Children.Count(f => f.Offset >= lastPacket);
        Assert.True(flushed > 0);
        Assert.Equal((listed - flushed - 1) * 2048L, context.Streams[0].Samples - context.Streams[0].LostSamples);
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
