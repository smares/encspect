using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// The series over the blocks of Windows Media Audio versions 1 and 2 (block sizes, noise substitution, coded bandwidth), over the subframes of
// Professional and the frames of Voice.
public class WmaBlockSeriesTests
{
    // 44.1 kHz mono at 24 kbit/s: noise substitution is on, and a frame holds one block of 2,048 samples.
    private static StdParameters Parameters() =>
        StdParameters.Create(new AudioFormat(0x0161, 1, 44100, 3000, 400, 16), WmaStandardStreams.CodecData(2, 0x0001));

    private static StreamInfo Run(WmaBitWriter frame)
    {
        byte[] data = new byte[400];
        Array.Fill(data, (byte)'N');
        frame.ToBytes((frame.Length + 7) / 8).CopyTo(data, 0);
        return Assert.Single(WmaStandardStreams.Run(2, 1, 44100, 3000, 400, WmaStandardStreams.CodecData(2, 0x0001), [data, data]).Streams);
    }

    private static CategorySeries Category(StreamInfo stream, string name) => Assert.Single(stream.CategorySeries, s => s.Name == name);

    private static Node Statistic(StreamInfo stream, string name) => Assert.Single(stream.Nodes[0].Children.Single(c => c.Name == "Statistics").Children, c => c.Name == name);

    // Ten coefficient codes end at position 9, so the block's coded bandwidth is 10 coefficients of 44,100 / 2 / 2,048 Hz each, rounded: 108 Hz.
    // The flagged band lies above them.
    [Fact]
    public void ABlockCodedBandwidthIsTheEdgeOfItsLastNonZeroCoefficient()
    {
        StdParameters p = Parameters();
        StreamInfo stream = Run(WmaStandardStreams.MonoFrame(p, 20, 10, flaggedBand: 0));

        Series bandwidth = Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth");
        Assert.Equal("Hz", bandwidth.Unit);
        Assert.Equal(GraphConcept.CodedBandwidth, bandwidth.Concept);
        Assert.Equal([108d, 108d], bandwidth.Values.ToArray());
        Series bitrate = Assert.Single(stream.Series, s => s.Name == "Bitrate");
        Assert.Equal(bitrate.X.ToArray(), bandwidth.X.ToArray());
        Series perSecond = Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth Over Time");
        Assert.Equal([108d], perSecond.Values.ToArray());
        Assert.Same(Statistic(stream, "Coded Bandwidth"), perSecond.Anchor);
        Node bandwidthNode = Statistic(stream, "Coded Bandwidth");
        Assert.Equal(108, Assert.Single(bandwidthNode.Children, c => c.Name == "Highest").Value!.AsInteger());
        Assert.Equal(108, Assert.Single(bandwidthNode.Children, c => c.Name == "Lowest").Value!.AsInteger());

        CategorySeries noise = Category(stream, "Noise-Substituted Blocks");
        Assert.Equal([2L, 0L], noise.Counts.ToArray());
        Assert.Equal(2, noise.Total);
        CategorySeries sizes = Category(stream, "Block Sizes");
        Assert.Equal([2L], sizes.Counts.ToArray());
        Assert.Equal("2048 samples", sizes.Categories[0].Name);
        Assert.Equal(GraphConcept.BlockSwitching, sizes.Concept);
        Assert.Equal(1, sizes.ValueCount(0));
    }

    [Fact]
    public void ABlockWithoutAFlaggedBandIsWithoutNoiseSubstitution()
    {
        StdParameters p = Parameters();
        StreamInfo stream = Run(WmaStandardStreams.MonoFrame(p, 20, 10));

        Assert.Equal([0L, 2L], Category(stream, "Noise-Substituted Blocks").Counts.ToArray());
    }

    // The last coefficient that fits one flagged band less is at position 1,798: the bandwidth is the edge of that line, counted among the
    // coefficients the channel codes.
    [Fact]
    public void AnEscapeCodedCoefficientSetsTheBandwidth()
    {
        StdParameters p = Parameters();
        StreamInfo stream = Run(WmaStandardStreams.MonoFrame(p, 20, 0, flaggedBand: 0, escapeRun: 1798));

        Assert.Equal([19369d, 19369d], Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth").Values.ToArray());
    }

    [Fact]
    public void ABlockWithNoNonZeroCoefficientHasNoCodedBandwidth()
    {
        StdParameters p = Parameters();
        StreamInfo stream = Run(WmaStandardStreams.MonoFrame(p, 20, 0));

        Assert.DoesNotContain(stream.Series, s => s.Name.StartsWith("Coded Bandwidth", StringComparison.Ordinal));
        Assert.DoesNotContain(stream.Nodes[0].Children.Single(c => c.Name == "Statistics").Children, c => c.Name == "Coded Bandwidth");
        Assert.Equal([2L], Category(stream, "Block Sizes").Counts.ToArray());
    }

    // The frames of a Microsoft-encoded stream have blocks of five sizes, which the series keep as the tally counts them, and the blocks of a
    // frame share the frame's offset.
    [Fact]
    public void BlockSizesAndBandwidthsOfAStreamWithVariableBlocks()
    {
        StreamInfo stream = Fixtures.AnalyzeMpeg("asf/external-windows-wma92-cbr-stereo-44k-128.wma").Streams[0];

        CategorySeries sizes = Category(stream, "Block Sizes");
        Assert.Equal([34L, 33L, 27L, 30L, 80L], sizes.Counts.ToArray());
        Assert.True(sizes.IsOrdered);
        Series bitrate = Assert.Single(stream.Series, s => s.Name == "Bitrate");
        Assert.Same(bitrate, sizes.XSeries);
        Assert.Equal(bitrate.X.Length, sizes.Count);
        Assert.True(sizes.Total > sizes.Count, "A frame holds several blocks.");

        Series bandwidth = Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth");
        HashSet<double> offsets = [.. bitrate.X.ToArray()];
        Assert.All(bandwidth.X.ToArray(), x => Assert.Contains(x, offsets));
        Assert.Contains(bandwidth.X.ToArray().GroupBy(x => x), g => g.Count() > 1);
        Assert.All(bandwidth.Values.ToArray(), v => Assert.InRange(v, 1, 22050));
        Node highest = Assert.Single(Statistic(stream, "Coded Bandwidth").Children, c => c.Name == "Highest");
        Assert.Equal((long)bandwidth.Values.ToArray().Max(), highest.Value!.AsInteger());
        Assert.Equal(bandwidth.Values.ToArray().Max(), Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth Over Time").Values.ToArray().Max());
    }

    [Fact]
    public void ProfessionalSubframeSizesAreOneValuePerSubframe()
    {
        StreamInfo stream = Fixtures.AnalyzeMpeg("asf/external-windows-wma10pro-cbr-stereo-44k-128.wma").Streams[0];

        CategorySeries sizes = Category(stream, "Subframe Sizes");
        Series bitrate = Assert.Single(stream.Series, s => s.Name == "Bitrate");
        Assert.Same(bitrate, sizes.XSeries);
        Assert.Equal(GraphConcept.BlockSwitching, sizes.Concept);
        Assert.Equal(Statistic(stream, "Subframes").Value!.AsInteger(), sizes.Total);
        Assert.Equal("Subframe Sizes", sizes.Anchor!.Name);
    }

    [Fact]
    public void VoiceFrameTypesAreThreeValuesPerSuperframe()
    {
        StreamInfo stream = Fixtures.AnalyzeMpeg("asf/external-windows-wmavoice-cbr-mono-8k-5.wma").Streams[0];

        CategorySeries types = Category(stream, "Frame Types");
        Series bitrate = Assert.Single(stream.Series, s => s.Name == "Bitrate");
        Assert.Same(bitrate, types.XSeries);
        Assert.Equal(3, types.ValuesPerUnit);
        Assert.Equal(17, types.Categories.Count);
        Assert.Equal(Statistic(stream, "Frames").Value!.AsInteger(), types.Total);
        Assert.Equal(GraphConcept.None, types.Concept);
    }
}
