using Encspect.Core;

namespace Encspect.Tests;

public class CompressionRatioTests
{
    private const long Pcm = 1_411_200;

    [Fact]
    public void The_pcm_size_is_the_rate_times_the_channels_times_the_bits_and_0_where_one_is_missing()
    {
        Assert.Equal(1_411_200, CompressionRatio.PcmBitsPerSecond(44100, 2, 16));
        Assert.Equal(0, CompressionRatio.PcmBitsPerSecond(44100, 2, null));
        Assert.Equal(0, CompressionRatio.PcmBitsPerSecond(0, 2, 16));
        Assert.Equal(0, CompressionRatio.PcmBitsPerSecond(44100, 0, 16));
    }

    [Fact]
    public void A_ratio_is_the_bitrate_over_the_pcm_bitrate_in_percent_rounded_half_to_even_to_one_decimal()
    {
        Assert.Equal(50, CompressionRatio.Percent(705.6, Pcm));
        Assert.Equal(19.9, CompressionRatio.Percent(281.1375, Pcm));
        Assert.Equal(0.1, CompressionRatio.Percent(1.4112, Pcm));
    }

    [Fact]
    public void The_series_take_the_bitrate_series_x_values_the_concept_and_a_quantity_of_their_own()
    {
        var anchor = new Node("Compression Ratio", 0, 10);
        double[] x = [0, 100, 250];
        var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, x, new double[] { 705.6, 352.8, 1411.2 });
        var perSecond = new Series("Bitrate Over Time", "kbps", SeriesAxis.Seconds, new double[] { 0, 1 }, new double[] { 705.6, 1000 }, new Node("Average Bitrate", 0, 10));
        List<Series> series = [bitrate, perSecond];

        CompressionRatio.AddTo(series, Pcm, anchor);

        Series ratio = series.Single(s => s.Name == "Compression Ratio");
        Series over = series.Single(s => s.Name == "Compression Ratio Over Time");
        Assert.Equal([50.0, 25.0, 100.0], ratio.Values.ToArray());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(ratio.X, out ArraySegment<double> own)
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array));
        Assert.Equal([50.0, 70.9], over.Values.ToArray());
        Assert.Same(anchor, over.Anchor);
        Assert.Null(ratio.Anchor);
        Assert.All([ratio, over], s =>
        {
            Assert.Equal("%", s.Unit);
            Assert.Equal(GraphConcept.BitDistribution, s.Concept);
            Assert.Equal("Compression Ratio", s.Quantity);
            Assert.False(string.IsNullOrEmpty(s.Description));
        });
    }

    [Fact]
    public void Nothing_is_added_without_a_pcm_size_or_an_anchor()
    {
        var bitrate = new Series("Bitrate", "kbps", SeriesAxis.FrameOffset, new double[] { 0 }, new double[] { 100 });
        List<Series> series = [bitrate];

        CompressionRatio.AddTo(series, 0, new Node("Compression Ratio", 0, 1));
        CompressionRatio.AddTo(series, Pcm, null);

        Assert.Single(series);
    }

    [Fact]
    public void The_statistics_node_has_the_lowest_overall_and_highest_ratio()
    {
        var statistics = new Node("Statistics", 0, 10);

        Node? node = CompressionRatio.AddTo(statistics, (name, value, description) => new Node(name, 0, 10, value) { Description = description }, Pcm, 705.6, 352.8, 1411.2);

        Assert.NotNull(node);
        Assert.Equal(["Lowest", "Overall", "Highest"], node.Children.Select(c => c.Name));
        Assert.Equal([25.0, 50.0, 100.0], node.Children.Select(c => c.Value!.AsDouble()!.Value));
        Assert.Null(CompressionRatio.AddTo(statistics, (name, value, description) => new Node(name, 0, 10, value), 0, 705.6, 352.8, 1411.2));
        Assert.Null(CompressionRatio.AddTo(statistics, (name, value, description) => new Node(name, 0, 10, value), Pcm, null, 0, 0));
    }
}
