using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// The nodes, statistics and series of Windows Media Audio Voice streams, read from the two files the Windows Media Format
// SDK's writer wrote: what the codec data, the packets, the superframes and the frames report, and that the nodes tile the
// bits they describe. The findings are asserted in WmaFindingTests.
public class WmaVoiceTests
{
    private const string Eight = "asf/external-windows-wmavoice-cbr-mono-8k-5.wma";
    private const string Sixteen = "asf/external-windows-wmavoice-cbr-mono-16k-16.wma";

    public static TheoryData<string> Files => new() { Eight, Sixteen };

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    private static long Integer(Node parent, string name) => Child(parent, name).Value!.AsInteger()!.Value;

    private static Node VoiceStream(Report report) => Assert.Single(report.Streams).Nodes[0];

    private static Node CodecData(Report report)
    {
        Node asf = report.Root.Children.Single(c => c.Name == "ASF");
        Node header = Child(asf, "Header Object");
        return Child(Child(Child(header, "Stream Properties Object"), "Type-Specific Data"), "Codec Specific Data");
    }

    // The first and the end bit of a node, in the bits of the file.
    private static (long Start, long End) Bits(Node node) =>
        node.BitOffset is { } bit ? ((node.Offset * 8) + bit, (node.Offset * 8) + bit + node.BitLength!.Value) : (node.Offset * 8, node.End * 8);

    [Fact]
    public void TheCodecDataIsReadFieldByField()
    {
        Report report = Fixtures.AnalyzeMpeg(Eight);
        Node data = CodecData(report);
        Assert.Equal(46, data.Length);
        Assert.Equal(18, Integer(data, "Professional Header"));
        Node flags = Child(data, "Flags");
        Assert.Equal(data.Offset + 18, flags.Offset);
        Assert.Equal(4, flags.Length);
        Assert.Equal(0x0519809FL, flags.Value!.AsInteger());
        Assert.True(Child(flags, "Uses Postfilter").Value!.AsBoolean());
        Assert.Equal(7, Integer(flags, "Denoise Strength"));
        Assert.False(Child(flags, "Uses Denoise Tilt Correction").Value!.AsBoolean());
        Assert.Equal(1, Integer(flags, "DC Level"));
        Assert.Equal(10, Integer(flags, "LSP Count"));
        Assert.Equal(0, Integer(flags, "LSP Quantizer Mode"));
        Assert.Equal(0, Integer(flags, "LSP Default Mode"));
        Assert.Equal(32, flags.Children.Sum(c => c.BitLength ?? (c.Length * 8)));

        // The table gives each frame type the group of its code: types 2, 3 and 4 have the 2-bit codes, 0, 1 and 5 the 4-bit ones.
        Node table = Child(data, "Frame Type Table");
        Assert.Equal(new long[] { 4, 4, 2, 2, 2, 4, 6, 6, 10, 10, 12, 6, 8, 8, 12, 8, 10 },
            Enumerable.Range(0, 17).Select(i => Integer(table, $"Frame Type {i}")));
        Assert.Equal(51, table.Children.Where(c => c.Name.StartsWith("Frame Type ", StringComparison.Ordinal)).Sum(c => c.BitLength!.Value));

        Report wide = Fixtures.AnalyzeMpeg(Sixteen);
        Node wideFlags = Child(CodecData(wide), "Flags");
        Assert.Equal(16, Integer(wideFlags, "LSP Count"));
        Assert.Equal(3, Integer(wideFlags, "Denoise Strength"));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TheStatisticsAddUp(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Node voice = VoiceStream(report);
        Node statistics = Child(voice, "Statistics");
        long superframes = Integer(statistics, "Superframes");
        Assert.Equal(Child(voice, "Superframes").Children.Count, superframes);
        Assert.Equal(superframes * 3, Integer(statistics, "Frames"));
        Assert.Equal(superframes * 480, Integer(statistics, "Samples"));
        Assert.Equal(stream.Samples, Integer(statistics, "Samples"));
        Assert.Equal(Child(voice, "Packets").Children.Count, Integer(statistics, "Packets"));
        Assert.Equal(Integer(statistics, "Packets") - 1, Integer(statistics, "Superframes Spanning Packets"));
        Assert.Equal(0, Integer(statistics, "Superframes with a Sample Count"));
        Assert.Equal(0, Integer(statistics, "Superframes with Additional Data"));

        // The frame types, the codebooks and the block counts each add up to the frames.
        Assert.Equal(superframes * 3, Child(statistics, "Frame Types").Children.Sum(Frames));
        Assert.Equal(superframes * 3, Child(statistics, "Fixed Codebooks").Children.Sum(Frames));
        Assert.Equal(superframes * 3, Child(statistics, "Adaptive Codebooks").Children.Sum(Frames));
        Assert.Equal(superframes * 3, Child(statistics, "Blocks per Frame").Children.Sum(Frames));
        Assert.Equal(stream.Duration, stream.Samples / (double)stream.SampleRate);

        // The container states the duration the audio lasts, less the preroll.
        Assert.Equal(Child(statistics, "Container Duration").Value!.AsDouble()!.Value, stream.Duration, 3);
        Assert.NotNull(stream.AverageBitrate);
        Assert.All(stream.Series, s => Assert.False(s.Values.IsEmpty));
    }

    private static long Frames(Node share) => long.Parse(share.Value!.ToString()!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TheFramesOfTheFirstSuperframeAreReadToTheirTypes()
    {
        Node superframes = Child(VoiceStream(Fixtures.AnalyzeMpeg(Eight)), "Superframes");
        Node first = superframes.Children[0];
        Assert.Equal(NodeKind.Frame, first.Kind);
        Assert.True(Child(first, "Is Speech").Value!.AsBoolean());
        Assert.False(Child(first, "Has Sample Count").Value!.AsBoolean());
        Assert.Equal("10 LSPs for the three frames", Child(first, "LSPs").Value!.ToString());
        Node[] frames = [.. first.Children.Where(c => c.Name == "Frame")];
        Assert.Equal([15L, 7, 7], frames.Select(f => Integer(f, "Frame Type")));
        Assert.Equal("Type 15: pulse sets, 8 blocks, pitch per block, 2 pulse pairs and 3 single pulses", frames[0].Value!.ToString());
        Assert.Equal(8, Child(frames[0], "Blocks").Children.Count);
        Assert.Equal(4, Child(frames[1], "Blocks").Children.Count);
        Assert.Equal(24, Integer(frames[1], "Pitch"));
        Assert.DoesNotContain(frames[0].Children, c => c.Name == "Pitch");
        Assert.Equal("none", Child(first, "Additional Data").Value!.ToString());
    }

    // A packet's header, the spillover, the superframes and the start of the next one lie one after another and fill the packet.
    [Theory]
    [MemberData(nameof(Files))]
    public void ThePacketsAreTiledByTheirChildren(string name)
    {
        Node packets = Child(VoiceStream(Fixtures.AnalyzeMpeg(name)), "Packets");
        Assert.All(packets.Children, packet =>
        {
            long at = packet.Offset * 8;
            foreach (Node child in packet.Children)
            {
                (long start, long end) = Bits(child);
                Assert.Equal(at, start);
                at = end;
            }

            Assert.Equal(packet.End * 8, at);
        });
    }

    // The nodes of a superframe that lies in one packet follow one another from its first bit to the end of its additional data.
    [Theory]
    [MemberData(nameof(Files))]
    public void ASuperframeInOnePacketIsTiledByItsChildren(string name)
    {
        Node superframes = Child(VoiceStream(Fixtures.AnalyzeMpeg(name)), "Superframes");
        int checkedCount = 0;
        foreach (Node superframe in superframes.Children.Where(s => s.Ranges is null))
        {
            long at = Bits(superframe.Children[0]).Start;
            foreach (Node child in superframe.Children)
            {
                (long start, long end) = Bits(child);
                Assert.Equal(at, start);
                at = end;
            }

            // The frames also tile theirs, down to the blocks.
            foreach (Node frame in superframe.Children.Where(c => c.Name == "Frame"))
            {
                long inner = Bits(frame.Children[0]).Start;
                Assert.Equal(Bits(frame).Start, inner);
                foreach (Node field in frame.Children)
                {
                    Assert.Equal(inner, Bits(field).Start);
                    inner = Bits(field).End;
                }

                Assert.Equal(Bits(frame).End, inner);
            }

            checkedCount++;
        }

        Assert.True(checkedCount > 20);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ASuperframeThatSpansPacketsLiesInTheBytesOfBoth(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Node voice = VoiceStream(report);
        Node[] packets = [.. Child(voice, "Packets").Children];
        Node[] superframes = [.. Child(voice, "Superframes").Children];
        Node[] spanning = [.. superframes.Where(s => s.Ranges is not null)];
        Assert.NotEmpty(spanning);
        foreach (Node superframe in spanning)
        {
            Assert.Equal(2, superframe.Ranges!.Count);
            Assert.Contains(packets, p => p.Offset <= superframe.Ranges[0].Offset && superframe.Ranges[0].End <= p.End);
            Assert.Contains(packets, p => p.Offset <= superframe.Ranges[1].Offset && superframe.Ranges[1].End <= p.End);
        }
    }

    [Fact]
    public void TheBitrateSeriesFollowTheSuperframes()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(Eight).Streams);
        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Assert.Equal(50, bitrate.Values.Length);
        Assert.Equal(SeriesAxis.FrameOffset, bitrate.Axis);
        Series seconds = stream.Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.Equal(3, seconds.Values.Length);
        Assert.Equal(SeriesAxis.Seconds, seconds.Axis);
    }

    [Fact]
    public void TheEncoderIsNamedFromTheContainer()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(Eight).Streams);
        Assert.StartsWith("Windows Media Format SDK", stream.Encoder, StringComparison.Ordinal);
        Assert.Equal(Confidence.Medium, stream.EncoderConfidence);
        Assert.False(stream.IsVbr);
    }

    // Every node the Voice parser builds is shown by a fixture: the files of the Windows writer and the hand-built ones.
    [Fact]
    public void EveryNodeTheParserBuildsAppearsInAFixture()
    {
        var seen = new HashSet<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "*").Where(p => Path.GetFileName(p).StartsWith("voice-", StringComparison.Ordinal) && p.EndsWith(".asf", StringComparison.Ordinal))
            .Concat([Path.Combine(Fixtures.FixtureDirectory, Eight), Path.Combine(Fixtures.FixtureDirectory, Sixteen)]))
        {
            Report report = Fixtures.AnalyzeMpeg(Path.GetRelativePath(Fixtures.FixtureDirectory, path).Replace('\\', '/'));
            var pending = new Stack<Node>([report.Root]);
            while (pending.Count > 0)
            {
                Node node = pending.Pop();
                seen.Add(node.Name);
                foreach (Node child in node.Children)
                {
                    pending.Push(child);
                }
            }
        }

        string[] expected =
        [
            "Professional Header", "Flags", "Uses Postfilter", "Denoise Strength", "Uses Denoise Tilt Correction", "DC Level", "LSP Count", "LSP Quantizer Mode", "LSP Default Mode", "Other Flags",
            "Frame Type Table", "Frame Type 0", "Frame Type 16", "Unused", "Extra Data",
            "Packet", "Packet Sequence Number", "Has Residual LSPs", "Superframes in Packet", "Spillover Bits", "Superframe End", "Padding", "Skipped Spillover", "Superframe Data",
            "Superframe Start", "Unfinished Superframe", "Unread Bits",
            "Superframe", "Is Speech", "Has Sample Count", "Sample Count", "LSPs", "Additional Data",
            "Frame", "Frame Type", "Pitch", "Silence Gain", "Window Index", "Blocks", "Block 1", "Block 8",
            "Encoder", "Statistics", "Packets", "Superframes", "Frames", "Samples", "Duration", "Container Duration", "Block Alignment", "Packet Bytes", "Average Bitrate", "Nominal Bitrate",
            "Smallest Superframe", "Largest Superframe", "Minimum Superframe Bitrate", "Maximum Superframe Bitrate", "Superframes Spanning Packets", "Most Superframes in a Packet",
            "Spillover Field", "Superframes with a Sample Count", "LSPs per Frame", "Superframes with Residual LSPs", "Superframes with Additional Data", "Uses Postfilter", "Pitch Range",
            "Ignored Bits", "Bits After the Last Superframe", "Skipped Spillover Bits", "Frame Types", "Fixed Codebooks", "Adaptive Codebooks", "Blocks per Frame", "Invalid Superframes",
        ];
        string[] missing = [.. expected.Where(name => !seen.Contains(name))];
        Assert.True(missing.Length == 0, $"No fixture shows: {string.Join(", ", missing)}");
    }

    // The 17 frame types of wmavoice.c: 1 to 8 blocks that divide the frame, silence with the only block of its frame, and the
    // pulse sets and the pitch-adaptive window with an adaptive codebook.
    [Fact]
    public void TheFrameDescriptorsAreConsistent()
    {
        Assert.Equal(VoiceParameters.FrameTypes, VoiceTables.FrameDescriptors.Length);
        for (int type = 0; type < VoiceParameters.FrameTypes; type++)
        {
            byte[] d = VoiceTables.FrameDescriptors[type];
            Assert.Equal(d[0], 1 << d[1]);
            Assert.Equal(0, VoiceParameters.FrameSamples % d[0]);
            Assert.Equal(type == 0, d[3] == 0);
            Assert.Equal(d[3] == 1, d[2] == 0 && type == 1);
            Assert.True(d[4] == 0 || d[3] == 3);
            Assert.NotEmpty(VoiceNames.Frame(type));
        }

        Assert.Equal(22, VoiceTables.FrameTypeCodeLengths.Length);
        Assert.Equal(94, VoiceTables.StartOffsets.Length);
    }
}
