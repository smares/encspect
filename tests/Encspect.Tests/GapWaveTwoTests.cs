using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests;

// The shared helpers of gap wave 2 (BitrateDistribution, UnitCategoryTally) and the figures whose counting only crafted input reaches: the buffer
// fullness of mixed ADTS headers, the DiscardPadding of Matroska block groups, the stereo blocks of WavPack, the container length of MP4.
public class GapWaveTwoTests
{
    private static Node Cover(string name, NodeValue? value, string description) => new(name, 0, 10, value) { Description = description };

    private static Node Cover(string name, NodeValue? value, string description, string? topic) => new(name, 0, 10, value) { Description = description, Topic = topic };

    private static IEnumerable<Node> Descendants(Node node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    private static Node Named(Node root, string name) => Descendants(root).First(n => n.Name == name);

    // BitrateDistribution

    [Fact]
    public void Bitrate_distribution_counts_units_in_ranges_of_32_kbps_and_the_last_range_is_open()
    {
        var distribution = new BitrateDistribution();
        foreach (double kbps in new[] { 0, 31.9, 32, 63.99, 64, 40_000 })
        {
            distribution.Add(kbps);
        }

        var statistics = new Node("Statistics", 0, 10);
        distribution.AddTo(statistics, Cover, "Frame", "Frames");

        Node bins = Named(statistics, "Bitrate Distribution");
        Assert.Equal(["0 to 31 kbps", "32 to 63 kbps", "64 to 95 kbps", "32000 kbps and more"], bins.Children.Select(c => c.Name));
        Assert.Equal(["2 frames (33.3%)", "2 frames (33.3%)", "1 frame (16.7%)", "1 frame (16.7%)"], bins.Children.Select(c => c.Value!.ToString()));
        Assert.Equal(6, distribution.Units);
        Assert.Equal(40_000, distribution.Maximum);
        Assert.Equal(0, distribution.Minimum);
    }

    [Fact]
    public void Bitrate_distribution_shares_round_half_to_even_on_the_tenth()
    {
        var distribution = new BitrateDistribution();
        distribution.Add(10, 1);
        distribution.Add(100, 15);
        var statistics = new Node("Statistics", 0, 10);
        distribution.AddTo(statistics, Cover, "Packet", "Packets");

        // 1 of 16 is 6.25 percent: 62.5 tenths rounds to the even 62.
        Assert.Equal("1 packet (6.2%)", Named(statistics, "0 to 31 kbps").Value!.ToString());
        Assert.Equal("15 packets (93.8%)", Named(statistics, "96 to 127 kbps").Value!.ToString());
    }

    [Theory]
    [InlineData(new double[] { }, null, null)]
    [InlineData(new double[] { 128 }, null, "unknown")]
    [InlineData(new double[] { 128, 128 }, null, "constant")]
    [InlineData(new double[] { 128, 129 }, null, "variable")]
    [InlineData(new double[] { 128, 128 }, true, "variable")]
    [InlineData(new double[] { 128, 200 }, false, "constant")]
    [InlineData(new double[] { 128 }, false, "constant")]
    public void Bitrate_mode_is_the_stated_one_or_follows_the_units(double[] rates, bool? stated, string? expected)
    {
        var distribution = new BitrateDistribution();
        foreach (double rate in rates)
        {
            distribution.Add(rate);
        }

        var statistics = new Node("Statistics", 0, 10);
        distribution.AddTo(statistics, Cover, "Frame", "Frames", stated);

        Assert.Equal(expected, statistics.Children.FirstOrDefault(c => c.Name == "Bitrate Mode")?.Value?.ToString());
        Assert.Equal(expected is null ? 0 : 2, statistics.Children.Count);
    }

    // UnitCategoryTally

    private static readonly Category[] Codings = [new("Left/right stereo", "Each channel by itself."), new("Mid/side stereo", "Mean and difference.")];

    [Fact]
    public void A_fixed_tally_counts_units_with_a_value_and_leaves_the_others_out_of_the_total()
    {
        var tally = new UnitCategoryTally("Stereo Coding", Codings, "frame", GraphConcept.StereoCoding);
        tally.Add(0);
        tally.Add(null);
        tally.Add(1);
        tally.Add(1);

        var statistics = new Node("Statistics", 0, 10);
        Node node = tally.AddTo(statistics, Cover, "A tally.", null, "Frame", "Frames")!;
        Series bitrate = new("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 1, 2, 3 }, new double[] { 1, 1, 1, 1 });
        CategorySeries series = tally.Build(bitrate, node, "A series.", null, null)!;

        Assert.Equal(4, tally.Units);
        Assert.Equal(3, tally.Total);
        Assert.Equal("3 frames", node.Value!.ToString());
        Assert.Equal(["Left/right stereo", "Mid/side stereo"], node.Children.Select(c => c.Name));
        Assert.Equal(["1 frame (33.3%)", "2 frames (66.7%)"], node.Children.Select(c => c.Value!.ToString()));
        Assert.Equal(4, series.Count);
        Assert.Equal(3, series.Counts.Sum());
        Assert.Equal(CategorySeries.None, series[1, 0]);
    }

    [Fact]
    public void A_variable_tally_takes_any_number_of_values_for_a_unit()
    {
        var tally = new UnitCategoryTally("Stereo Coding", Codings, "stereo block", GraphConcept.StereoCoding, isVariable: true);
        tally.AddAll([0, 1, 1]);
        tally.AddAll([]);
        tally.AddAll([1]);

        var statistics = new Node("Statistics", 0, 10);
        Node node = tally.AddTo(statistics, Cover, "A tally.", null, "Stereo Block", "Stereo Blocks")!;
        CategorySeries series = tally.Build(new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 1, 2 }, new double[] { 1, 1, 1 }), node, "A series.", null, null)!;

        Assert.Equal(4, tally.Total);
        Assert.Equal(3, tally.Units);
        Assert.Equal([1L, 3L], series.Counts);
        Assert.Equal(3, series.ValueCount(0));
        Assert.Equal(0, series.ValueCount(1));
    }

    [Fact]
    public void A_tally_without_a_value_makes_no_node_and_one_whose_units_differ_from_the_series_makes_no_series()
    {
        var empty = new UnitCategoryTally("Stereo Coding", Codings, "frame", GraphConcept.StereoCoding);
        empty.Add(null);
        Assert.Null(empty.AddTo(new Node("Statistics", 0, 10), Cover, "A tally.", null, "Frame", "Frames"));

        var tally = new UnitCategoryTally("Stereo Coding", Codings, "frame", GraphConcept.StereoCoding);
        tally.Add(0);
        tally.Add(1);
        Node node = tally.AddTo(new Node("Statistics", 0, 10), Cover, "A tally.", null, "Frame", "Frames")!;
        Series three = new("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0, 1, 2 }, new double[] { 1, 1, 1 });

        Assert.Null(tally.Build(three, node, "A series.", null, null));
    }

    // AAC buffer fullness

    private static Node CoverAac(string name, NodeValue? value, NodeKind kind, string description) => new(name, 0, 10, value) { Kind = kind, Description = description };

    [Fact]
    public void Frames_that_signal_a_variable_bitrate_are_no_fullness_and_stay_out_of_the_series()
    {
        var figures = new StreamFigures(44100);
        int[] fullness = [100, StreamFigures.Variable, 300, StreamFigures.Variable, 200];
        for (int i = 0; i < fullness.Length; i++)
        {
            figures.Add(i * 100, 100, 1024);
            figures.SetFullness(fullness[i]);
        }

        Node statistics = figures.Statistics(CoverAac, "Frame", "Frames", out Node bitrate);
        List<Series> series = figures.Series(bitrate, CancellationToken.None);

        Assert.Equal("100 words per channel", Named(statistics, "Minimum Buffer Fullness").Value!.ToString());
        Assert.Equal("300 words per channel", Named(statistics, "Maximum Buffer Fullness").Value!.ToString());
        Assert.Equal(200.0, Named(statistics, "Average Buffer Fullness").Value!.AsDouble());
        Assert.Equal("2 frames", Named(statistics, "Frames Signaling Variable Bitrate").Value!.ToString());
        Series perFrame = series.Single(s => s.Name == "Buffer Fullness");
        Assert.Equal([100.0, 300.0, 200.0], perFrame.Values.ToArray());
        Assert.Equal([0.0, 200.0, 400.0], perFrame.X.ToArray());
        // All five frames start in the first second, and its mean leaves the two that state no fullness out.
        Series overTime = series.Single(s => s.Name == "Buffer Fullness Over Time");
        Assert.Equal([200.0], overTime.Values.ToArray());
    }

    [Fact]
    public void A_stream_whose_frames_all_signal_a_variable_bitrate_has_no_fullness_series()
    {
        var figures = new StreamFigures(44100);
        for (int i = 0; i < 3; i++)
        {
            figures.Add(i * 100, 100, 1024);
            figures.SetFullness(StreamFigures.Variable);
        }

        Node statistics = figures.Statistics(CoverAac, "Frame", "Frames", out Node bitrate);

        Assert.DoesNotContain(statistics.Children, c => c.Name.Contains("Buffer Fullness", StringComparison.Ordinal));
        Assert.Equal("3 frames", Named(statistics, "Frames Signaling Variable Bitrate").Value!.ToString());
        Assert.DoesNotContain(figures.Series(bitrate, CancellationToken.None), s => s.Name.StartsWith("Buffer Fullness", StringComparison.Ordinal));
    }

    // Matroska DiscardPadding

    private static byte[] Signed(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        int start = 0;
        while (start < 7 && ((bytes[start] == 0 && bytes[start + 1] < 0x80) || (bytes[start] == 0xFF && bytes[start + 1] >= 0x80)))
        {
            start++;
        }

        return bytes[start..];
    }

    private static byte[] Block(int timestamp) => MatroskaBuilder.Element(0xA1, [0x81, (byte)(timestamp >> 8), (byte)timestamp, 0x00, 0xFC, 0xFF, 0xFE]);

    private static byte[] Padding(long nanoseconds) => MatroskaBuilder.Element(0x75A2, Signed(nanoseconds));

    private static Report Opus(params byte[][] groups)
    {
        byte[] file = MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.OpusTracks(), MatroskaBuilder.Cluster(0, [.. groups.Select(g => MatroskaBuilder.Element(0xA0, g))]));
        return Analyzer.CreateDefault().Analyze("padding.mka", new MemoryStream(file));
    }

    // Three blocks of 960 samples, a pre-skip of 312: 2,568 samples before any padding. 10 ms is 480 samples.
    [Fact]
    public void A_padding_after_the_block_of_the_last_group_is_the_end_trimming()
    {
        Report report = Opus(Block(0), Block(20), Block(40).Concat(Padding(10_000_000)).ToArray());

        Assert.Equal(2568 - 480, Assert.Single(report.Streams).Samples);
        Assert.Equal("480 samples", Named(report.Root, "End Trimming").Value!.ToString());
    }

    [Fact]
    public void A_padding_before_the_block_of_its_group_counts_as_well()
    {
        Report report = Opus(Block(0), Block(20), Padding(10_000_000).Concat(Block(40)).ToArray());

        Assert.Equal(2568 - 480, Assert.Single(report.Streams).Samples);
    }

    [Fact]
    public void A_padding_on_a_block_that_is_not_the_last_is_not_applied()
    {
        Report report = Opus(Block(0).Concat(Padding(10_000_000)).ToArray(), Block(20), Block(40));

        Assert.Equal(2568, Assert.Single(report.Streams).Samples);
        Assert.DoesNotContain(Descendants(report.Root), n => n.Name == "End Trimming");
    }

    [Fact]
    public void A_negative_padding_pads_the_start_of_the_block_and_is_not_applied()
    {
        Report report = Opus(Block(0), Block(20), Block(40).Concat(Padding(-10_000_000)).ToArray());

        Assert.Equal(2568, Assert.Single(report.Streams).Samples);
        Assert.DoesNotContain(Descendants(report.Root), n => n.Name == "End Trimming");
    }

    [Fact]
    public void The_last_padding_wins_and_the_trimming_cannot_exceed_what_the_packets_hold()
    {
        Report last = Opus(Block(0).Concat(Padding(5_000_000)).ToArray(), Block(20), Block(40).Concat(Padding(10_000_000)).ToArray());
        Report huge = Opus(Block(0), Block(20), Block(40).Concat(Padding(9_000_000_000)).ToArray());

        Assert.Equal(2568 - 480, Assert.Single(last.Streams).Samples);
        Assert.Equal(0, Assert.Single(huge.Streams).Samples);
        Assert.Equal("2568 samples", Named(huge.Root, "End Trimming").Value!.ToString());
    }

    // The padding of the other codecs is applied by the container to the stream and to the statistics together.
    [Fact]
    public void The_padding_of_other_codecs_trims_the_stream_and_its_statistics_alike()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/external-ffmpeg-vorbis.mka");

        StreamInfo stream = Assert.Single(report.Streams);
        Node trimming = Named(report.Root, "End Trimming");
        Node statistics = Descendants(report.Root).First(n => n.Kind == NodeKind.Statistics && n.Children.Any(c => c.Name == "End Trimming"));
        Assert.Equal(stream.Samples, Named(statistics, "Samples").Value!.AsInteger());
        Assert.True(trimming.Value!.AsInteger() > 0);
        Assert.Equal(stream.Samples / (double)stream.SampleRate, Named(statistics, "Duration").Value!.AsDouble()!.Value, 3);
    }

    // ffmpeg's 1.5 s Opus source: 76 packets of 960 samples, a pre-skip of 312 and a DiscardPadding of 13.5 ms (648 samples) on the last block.
    [Fact]
    public void The_opus_track_of_a_real_file_has_the_samples_of_its_source()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/external-ffmpeg-no-crc.mka");

        Assert.Equal(72_000, Assert.Single(report.Streams).Samples);
        Assert.Equal("648 samples", Named(report.Root, "End Trimming").Value!.ToString());
    }

    // WavPack Stereo Coding

    [Theory]
    [InlineData("wavpack/external-joint-stereo-on.wv", 3, 3)]
    [InlineData("wavpack/external-lossless-51-16.wv", 4, 4)]
    public void The_stereo_blocks_of_every_set_are_counted_the_first_of_a_set_included(string name, int stereoBlocks, int midSide)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Node coding = Named(report.Root, "Stereo Coding");
        Assert.Equal($"{stereoBlocks} stereo blocks", coding.Value!.ToString());
        Assert.StartsWith($"{midSide} stereo blocks", Named(coding, "Mid/side stereo").Value!.ToString(), StringComparison.Ordinal);
        Assert.Equal(stereoBlocks, Assert.Single(report.Streams).CategorySeries.Single(c => c.Name == "Stereo Coding").Total);
    }

    // MP4 Container Samples

    [Fact]
    public void Container_samples_need_a_media_timescale_that_is_the_sample_rate()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-lc.m4a"));
        Report original = Analyzer.CreateDefault().Analyze("a.m4a", new MemoryStream(file));
        int mdhd = file.AsSpan().IndexOf("mdhd"u8);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mdhd + 16), 22050);

        Report changed = Analyzer.CreateDefault().Analyze("b.m4a", new MemoryStream(file));

        Assert.Contains(Descendants(original.Root), n => n.Name == "Container Samples");
        Assert.Contains(Descendants(changed.Root), n => n.Name == "Container Duration");
        Assert.DoesNotContain(Descendants(changed.Root), n => n.Name == "Container Samples");
    }

    [Fact]
    public void A_fragmented_file_whose_mdhd_states_no_duration_has_no_container_length()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/fragmented.mp4");

        Assert.DoesNotContain(Descendants(report.Root), n => n.Name is "Container Duration" or "Container Samples");
    }
}
