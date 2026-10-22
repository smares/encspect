using Encspect.Core;
using Encspect.Formats.Mpeg;

namespace Encspect.Tests;

// The category series and the numeric series of an MPEG stream's coding (docs/design/coding-graphs.md): each unit's code is held
// equal to what the frame's own nodes state, which the Layer3SideInfo, Layer1AudioData and Layer2AudioData readers build and the
// series do not use, so a code in the wrong place of a unit fails here and not only in a total.
public class MpegCodingSeriesTests
{
    private static readonly string[] ChannelModes = ["Stereo", "Joint stereo", "Dual channel", "Mono"];

    private static readonly string[] Bounds = ["Bands 4 to 31", "Bands 8 to 31", "Bands 12 to 31", "Bands 16 to 31"];

    public static TheoryData<string> Names() => Fixtures.MpegFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void The_codes_of_every_frame_equal_what_its_nodes_state(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        if (report.Streams.Count != 1)
        {
            return;
        }

        StreamInfo stream = report.Streams[0];
        Node[] frames = AudioFrames(stream);
        CategorySeries modes = Series(stream, "Channel Modes")!;
        CategorySeries coding = Series(stream, "Stereo Coding")!;
        CategorySeries? joint = Series(stream, "Joint Stereo Modes");
        CategorySeries? bounds = Series(stream, "Intensity Stereo Bounds");
        CategorySeries? blocks = Series(stream, "Block Usage");
        Assert.Equal(frames.Length, modes.Count);
        Assert.Equal(frames.Length, coding.Count);
        for (int unit = 0; unit < frames.Length; unit++)
        {
            string mode = Field(frames[unit], "Header/Channel Mode")!;
            string extension = Field(frames[unit], "Header/Mode Extension")!;
            bool isLayer3 = Field(frames[unit], "Header/Layer") == "III";
            Assert.Equal(Array.IndexOf(ChannelModes, mode), modes[unit, 0]);
            byte expected = mode switch
            {
                "Stereo" => 0,
                "Dual channel" => 4,
                "Mono" => 5,
                _ when !isLayer3 => 2,
                _ => extension switch
                {
                    "Mid/side stereo" => 1,
                    "Intensity stereo" => 2,
                    "Mid/side and intensity stereo" => 3,
                    _ => 0,
                },
            };
            Assert.True(expected == coding[unit, 0], $"{name}, frame {unit}: {mode}, {extension}");
            if (joint is not null)
            {
                Assert.Equal(mode == "Joint stereo" ? expected : CategorySeries.None, joint[unit, 0]);
            }

            if (bounds is not null)
            {
                Assert.Equal(mode == "Joint stereo" ? (byte)Array.IndexOf(Bounds, extension) : CategorySeries.None, bounds[unit, 0]);
            }

            if (blocks is not null)
            {
                CheckBlocks(name, unit, frames[unit], blocks);
            }
        }
    }

    private static void CheckBlocks(string name, int unit, Node frame, CategorySeries blocks)
    {
        int channels = blocks.ValuesPerUnit / (Field(frame, "Header/Version") == "MPEG-1" ? 2 : 1);
        for (int value = 0; value < blocks.ValuesPerUnit; value++)
        {
            (int granule, int channel) = (value / channels, value % channels);
            Node? node = Fixtures.Resolve(frame, $"Side Info/Granule {granule}/Channel {channel}").FirstOrDefault();
            if (node is null)
            {
                Assert.Equal(CategorySeries.None, blocks[unit, value]);
                continue;
            }

            string type = node.Children.Single(c => c.Name == "Block Type").Value!.ToString()!;
            bool isMixed = node.Children.FirstOrDefault(c => c.Name == "IsMixedBlock")?.Value?.ToString() == "true";
            byte expected = type switch
            {
                "Normal" => 0,
                "Start" => 1,
                "Short" => isMixed ? (byte)3 : (byte)2,
                "Stop" => 4,
                _ => 5,
            };
            Assert.True(expected == blocks[unit, value], $"{name}, frame {unit}, granule {granule}, channel {channel}: {type}");
        }
    }

    // The coded bandwidth of a Layer I or II frame follows from the allocation nodes alone: the upper edge of the highest subband
    // with a value other than "None" in either channel.
    [Theory]
    [MemberData(nameof(Names))]
    public void The_coded_bandwidth_of_every_frame_equals_what_its_allocation_nodes_state(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        if (report.Streams.Count != 1 || report.Streams[0].Series.FirstOrDefault(s => s.Name == "Coded Bandwidth") is not { } bandwidth)
        {
            return;
        }

        Node[] frames = AudioFrames(report.Streams[0]);
        int unit = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            Node? allocation = Fixtures.Resolve(frames[i], "Bit Allocation").FirstOrDefault();
            if (allocation is null)
            {
                continue;
            }

            int rate = int.Parse(Field(frames[i], "Header/Sample Rate")!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            int highest = -1;
            foreach (Node subband in allocation.Children)
            {
                if (subband.Children.Any(c => c.Value!.ToString() != "None"))
                {
                    highest = int.Parse(subband.Name.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            double expected = highest < 0 ? 0 : Math.Round((highest + 1) * rate / 64.0, MidpointRounding.ToEven);
            Assert.True(bandwidth.X.Span[unit] == frames[i].Offset, $"{name}: unit {unit} is at {bandwidth.X.Span[unit]}, frame at {frames[i].Offset}");
            Assert.True(expected == bandwidth.Values.Span[unit], $"{name}, frame {i}: {bandwidth.Values.Span[unit]} Hz, the nodes give {expected}");
            unit++;
        }

        Assert.Equal(unit, bandwidth.Values.Length);
    }

    // Layer III's channel bit share follows from the part2_3_length nodes: the first channel's bits over both channels', in tenths of a percent.
    [Theory]
    [MemberData(nameof(Names))]
    public void The_channel_bit_share_of_every_layer_III_frame_equals_what_its_length_nodes_state(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        if (report.Streams.Count != 1 || report.Streams[0].Series.FirstOrDefault(s => s.Name == "Channel Bit Share") is not { } share
            || !report.Streams[0].Codec.EndsWith("III", StringComparison.Ordinal))
        {
            return;
        }

        int unit = 0;
        foreach (Node frame in AudioFrames(report.Streams[0]))
        {
            Node[] channels = [.. Fixtures.Resolve(frame, "Side Info").FirstOrDefault()?.Children.Where(g => g.Name.StartsWith("Granule", StringComparison.Ordinal)).SelectMany(g => g.Children) ?? []];
            long first = channels.Where(c => c.Name == "Channel 0").Sum(PartBits);
            long second = channels.Where(c => c.Name == "Channel 1").Sum(PartBits);
            if (second == 0 && channels.All(c => c.Name != "Channel 1") || first + second == 0)
            {
                continue;
            }

            double expected = Math.Round(first * 1000.0 / (first + second), MidpointRounding.ToEven) / 10;
            Assert.True(share.X.Span[unit] == frame.Offset, $"{name}: unit {unit}");
            Assert.True(expected == share.Values.Span[unit], $"{name}, frame at {frame.Offset}: {share.Values.Span[unit]}%, the nodes give {expected}%");
            unit++;
        }

        Assert.Equal(unit, share.Values.Length);
    }

    private static long PartBits(Node channel) => channel.Children.Single(c => c.Name == "Part 2 and 3 Length").Value!.AsInteger()!.Value;

    [Fact]
    public void A_layer_III_joint_stereo_stream_has_its_series_under_the_shared_concepts()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/external-lame-cbr128-joint.mp3");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal(["Channel Modes", "Stereo Coding", "Joint Stereo Modes", "Block Usage", "Block Sizes"], stream.CategorySeries.Select(s => s.Name));
        Assert.Equal([GraphConcept.None, GraphConcept.StereoCoding, GraphConcept.StereoCoding, GraphConcept.BlockSwitching, GraphConcept.BlockSwitching],
            stream.CategorySeries.Select(s => s.Concept));
        Assert.All(stream.CategorySeries, s => Assert.Equal(s.Name, s.Anchor!.Name));
        Assert.All(stream.CategorySeries, s => Assert.Equal("Bitrate", s.XSeries!.Name));
        Assert.All(stream.CategorySeries, s => Assert.NotNull(s.Time));
        Assert.Equal(["Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", "Dual channel", "Mono"],
            Series(stream, "Stereo Coding")!.Categories.Select(c => c.Name));
        CategorySeries sizes = Series(stream, "Block Sizes")!;
        Assert.Equal("Block Usage", sizes.Source!.Name);
        Assert.Equal(["Long, start and stop", "Short", "Mixed", "Reserved"], sizes.Categories.Select(c => c.Name));
        Assert.True(Series(stream, "Block Usage")!.Categories[5].IsReserved);
        Assert.Equal(4, Series(stream, "Block Usage")!.ValuesPerUnit);
        Assert.Equal(["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"], Series(stream, "Block Usage")!.ValueNames);

        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        Series perSecond = stream.Series.Single(s => s.Name == "Bitrate Over Time");
        Assert.Equal([GraphConcept.BitDistribution, GraphConcept.BitDistribution], [bitrate.Concept, perSecond.Concept]);
        Assert.Equal("Bitrate", bitrate.Quantity);
        Assert.Equal("Bitrate", perSecond.Quantity);
        Assert.All(stream.Series.Where(s => s.Name.StartsWith("Reservoir", StringComparison.Ordinal)), s => Assert.Equal("Reservoir Fill", s.Quantity));

        Series share = stream.Series.Single(s => s.Name == "Channel Bit Share");
        Series shareOverTime = stream.Series.Single(s => s.Name == "Channel Bit Share Over Time");
        Assert.Equal(["%", "%"], [share.Unit, shareOverTime.Unit]);
        Assert.Equal(SeriesAxis.FrameOffset, share.Axis);
        Assert.Equal(SeriesAxis.Seconds, shareOverTime.Axis);
        Assert.Equal([GraphConcept.BitDistribution, GraphConcept.BitDistribution], [share.Concept, shareOverTime.Concept]);
        Assert.Equal("Channel Bit Share", share.Quantity);
        Assert.Equal("Channel Bit Share", shareOverTime.Quantity);
        Assert.Equal("Channel Bit Share", shareOverTime.Anchor!.Name);
        // The first frame of the stream has no bits (part2_3_length 0 in every granule), so it has no point.
        Assert.Equal(bitrate.X.Length - 1, share.X.Length);
        Assert.All(share.X.ToArray(), x => Assert.Contains(x, bitrate.X.ToArray()));
        Assert.DoesNotContain(stream.Series, s => s.Name.StartsWith("Coded Bandwidth", StringComparison.Ordinal));
    }

    [Fact]
    public void A_layer_II_stream_has_a_coded_bandwidth_and_intensity_stereo_bounds_in_place_of_block_usage()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/external-ffmpeg-mp2.mp2");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.DoesNotContain(stream.CategorySeries, s => s.Name is "Block Usage" or "Block Sizes" or "Joint Stereo Modes");
        Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");
        Series perSecond = stream.Series.Single(s => s.Name == "Coded Bandwidth Over Time");
        Assert.Equal("Hz", bandwidth.Unit);
        Assert.Equal([GraphConcept.CodedBandwidth, GraphConcept.CodedBandwidth], [bandwidth.Concept, perSecond.Concept]);
        Assert.Equal("Coded Bandwidth", bandwidth.Quantity);
        Assert.Equal("Coded Bandwidth", perSecond.Quantity);
        Assert.Equal("Coded Bandwidth", perSecond.Anchor!.Name);
        Assert.Equal(bandwidth.Values.Span.ToArray().Max(), perSecond.Values.Span.ToArray().Max());
        Node anchor = perSecond.Anchor!;
        Assert.Equal($"{bandwidth.Values.Span.ToArray().Max():0} Hz", anchor.Children.Single(c => c.Name == "Highest").Value!.ToString());
    }

    [Fact]
    public void A_mono_stream_has_no_channel_bit_share()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/external-ffmpeg-mono.mp3");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.DoesNotContain(stream.Series, s => s.Name.StartsWith("Channel Bit Share", StringComparison.Ordinal));
        Assert.Equal(2, Series(stream, "Block Usage")!.ValuesPerUnit);
        Assert.Equal(["granule 1, channel 1", "granule 2, channel 1"], Series(stream, "Block Usage")!.ValueNames);
    }

    [Fact]
    public void A_stream_that_changes_between_mono_and_stereo_leaves_the_second_channel_without_a_value()
    {
        Report report = Fixtures.AnalyzeMpeg("mpeg/channel-mode-change.mp3");
        StreamInfo stream = Assert.Single(report.Streams);
        CategorySeries blocks = Series(stream, "Block Usage")!;

        int mono = Enumerable.Range(0, blocks.Count).Count(unit => blocks[unit, 1] == CategorySeries.None && blocks[unit, 3] == CategorySeries.None && blocks[unit, 0] != CategorySeries.None);

        Assert.Equal(4, mono);
        Assert.Equal(4L, Series(stream, "Stereo Coding")!.Counts[5]);
        Assert.Equal(24L, blocks.Total);

        // The frames of this stream state no bits (part2_3_length 0 everywhere), so they have no share to graph.
        Assert.DoesNotContain(stream.Series, s => s.Name.StartsWith("Channel Bit Share", StringComparison.Ordinal));
    }

    private static CategorySeries? Series(StreamInfo stream, string name) => stream.CategorySeries.FirstOrDefault(s => s.Name == name);

    private static string? Field(Node node, string path) => Fixtures.Resolve(node, path).FirstOrDefault()?.Value?.ToString();

    // The complete audio frames of a stream, in file order.
    private static Node[] AudioFrames(StreamInfo stream)
    {
        // The frames of the stream's bitrate series, one point each at the frame's offset: not a VBR header frame, not a truncated
        // or cut one.
        HashSet<long> audio = stream.Series.First(s => s.Name == "Bitrate").X.ToArray().Select(x => (long)x).ToHashSet();
        return [.. stream.Nodes.SelectMany(n => n.Children).Where(c => c.Name == "Frame" && audio.Contains(c.Offset))];
    }
}

// The figures read straight from a frame's bits (FrameFigures), checked against frames assembled by hand with known allocations.
public class FrameFiguresTests
{
    [Fact]
    public void A_layer_I_frame_gives_its_bandwidth_and_each_channels_own_bits()
    {
        // 44,100 Hz stereo, no CRC. Subband 0 allocates 3 bits plus one in the first channel only.
        byte[] frame = [0xFF, 0xFF, 0x10, 0x00, 0x30, .. new byte[60]];

        FrameFigures.Figure figure = FrameFigures.Layer1(frame, new MpegHeader(0xFFFF1000));

        // Per channel 32 allocations of 4 bits; the first channel adds a 6-bit scalefactor and 12 samples of 4 bits.
        Assert.Equal(new FrameFigures.Figure(689, 128 + 6 + 48, 128), figure);
    }

    [Fact]
    public void A_layer_I_joint_stereo_frame_leaves_the_shared_allocations_and_samples_out_of_both_channels()
    {
        // Mode extension 0: intensity stereo from subband 4. Subband 4 allocates 1 (2 bits per sample) for both channels.
        byte[] frame = new byte[80];
        (frame[0], frame[1], frame[2], frame[3]) = (0xFF, 0xFF, 0x10, 0x40);
        frame[4 + 4] = 0x10;

        FrameFigures.Figure figure = FrameFigures.Layer1(frame, new MpegHeader(0xFFFF1040));

        // Below the bound 4 allocations of 4 bits per channel; the allocation of subband 4 is shared, but each channel keeps a 6-bit
        // scalefactor for it, and its shared samples belong to neither channel. The highest subband is 4: 5 * 44,100 / 64 = 3,445.3 Hz.
        Assert.Equal(new FrameFigures.Figure(3445, 16 + 6, 16 + 6), figure);
    }

    [Fact]
    public void A_layer_II_frame_gives_its_bandwidth_and_each_channels_own_bits()
    {
        // 44,100 Hz, 160 kbps stereo: table B.2a, whose 27 subbands take 88 bits of allocation per channel. Subband 0 allocates index
        // 3 (15 levels, a codeword of 12 bits for three samples) in the first channel only, with three scalefactors (scfsi 0).
        byte[] frame = [0xFF, 0xFD, 0x90, 0x00, 0x30, .. new byte[100]];

        FrameFigures.Figure figure = FrameFigures.Layer2(frame, new MpegHeader(0xFFFD9000));

        Assert.Equal(new FrameFigures.Figure(689, 88 + 2 + 18 + 144, 88), figure);
    }

    [Fact]
    public void A_layer_II_frame_reads_the_scfsi_fields_one_after_the_other_before_the_scalefactors_they_count()
    {
        // Subband 0 allocates index 3 (15 levels, 12 bits per codeword) in the first channel and index 2 (7 levels, 9 bits) in the second.
        // The scfsi fields follow the 176 bits of allocation, 2 bits each: 1 (two scalefactors) for the first channel, 3 (two) for the second.
        byte[] frame = new byte[100];
        (frame[0], frame[1], frame[2], frame[3], frame[4]) = (0xFF, 0xFD, 0x90, 0x00, 0x32);
        frame[4 + 22] = 0x70;

        FrameFigures.Figure figure = FrameFigures.Layer2(frame, new MpegHeader(0xFFFD9000));

        Assert.Equal(new FrameFigures.Figure(689, 88 + 2 + 12 + 144, 88 + 2 + 12 + 108), figure);
    }

    [Fact]
    public void A_layer_III_frame_sums_part_2_3_length_over_its_granules_per_channel()
    {
        byte[] sideInfo = new byte[32];
        Set(sideInfo, 20, 12, 400);
        Set(sideInfo, 20 + (59 * 1), 12, 25);
        Set(sideInfo, 20 + (59 * 2), 12, 30);
        Set(sideInfo, 20 + (59 * 3), 12, 100);
        var header = new MpegHeader(0xFFFB1000);

        FrameFigures.Figure figure = FrameFigures.Layer3(sideInfo, header);

        Assert.Equal(new FrameFigures.Figure(FrameFigures.None, 430, 125), figure);
        Assert.True(figure.HasShare);
        Assert.False(figure.HasBandwidth);
    }

    [Fact]
    public void A_mono_frame_has_no_share()
    {
        Assert.False(FrameFigures.Layer3(new byte[17], new MpegHeader(0xFFFB10C0)).HasShare);
        Assert.False(FrameFigures.Layer1(new byte[40], new MpegHeader(0xFFFF10C0)).HasShare);
    }

    [Fact]
    public void Block_types_are_read_from_their_positions_in_both_versions()
    {
        // MPEG-1 stereo: granule 1, channel 0 starts at bit 20 + 2 * 59; window_switching_flag follows 12 + 9 + 8 + 4 bits.
        byte[] sideInfo = new byte[32];
        int start = 20 + (59 * 2);
        Set(sideInfo, start + 33, 1, 1);
        Set(sideInfo, start + 34, 2, 2);
        Set(sideInfo, start + 36, 1, 1);
        var mpeg1 = new MpegHeader(0xFFFB1000);

        Assert.Equal(3, FrameFigures.BlockKind(sideInfo, mpeg1, 1, 0));
        Assert.Equal(0, FrameFigures.BlockKind(sideInfo, mpeg1, 0, 0));
        Set(sideInfo, start + 36, 1, 0);
        Assert.Equal(2, FrameFigures.BlockKind(sideInfo, mpeg1, 1, 0));
        Set(sideInfo, start + 34, 2, 3);
        Assert.Equal(4, FrameFigures.BlockKind(sideInfo, mpeg1, 1, 0));
        Set(sideInfo, start + 34, 2, 0);
        Assert.Equal(5, FrameFigures.BlockKind(sideInfo, mpeg1, 1, 0));

        // MPEG-2 stereo: one granule, 63 bits per channel after 10 bits; the flag follows 12 + 9 + 8 + 9 bits.
        byte[] lsf = new byte[17];
        Set(lsf, 10 + 63 + 38, 1, 1);
        Set(lsf, 10 + 63 + 39, 2, 1);
        Assert.Equal(1, FrameFigures.BlockKind(lsf, new MpegHeader(0xFFF31000), 0, 1));
    }

    private static void Set(byte[] data, int bit, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            int position = bit + i;
            if (((value >> (count - 1 - i)) & 1) != 0)
            {
                data[position >> 3] |= (byte)(0x80 >> (position & 7));
            }
            else
            {
                data[position >> 3] &= (byte)~(0x80 >> (position & 7));
            }
        }
    }
}
