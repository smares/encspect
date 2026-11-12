using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The DiscardPadding of Matroska block groups, which only crafted input reaches.
public class MatroskaDiscardPaddingTests
{
    private static IEnumerable<Node> Descendants(Node node) => new[] { node }.Concat(node.Children.SelectMany(Descendants));

    private static Node Named(Node root, string name) => Descendants(root).First(n => n.Name == name);

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
    public void The_last_padding_wins_and_the_trimming_cannot_exceed_what_the_last_block_holds()
    {
        Report last = Opus(Block(0).Concat(Padding(5_000_000)).ToArray(), Block(20), Block(40).Concat(Padding(10_000_000)).ToArray());
        Report huge = Opus(Block(0), Block(20), Block(40).Concat(Padding(9_000_000_000)).ToArray());

        Assert.Equal(2568 - 480, Assert.Single(last.Streams).Samples);

        // The padding is per block (RFC 9559, section 5.1.3.5.7): the last block holds one packet of 960 samples, so 9 s is cut to them, with a finding.
        Assert.Equal(2568 - 960, Assert.Single(huge.Streams).Samples);
        Assert.Equal("960 samples", Named(huge.Root, "End Trimming").Value!.ToString());
        Assert.Contains(huge.Findings, f => f.Code == "opus.discard-padding-large");
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
}
