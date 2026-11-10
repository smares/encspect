using Encspect.Core;

namespace Encspect.Tests;

// BitrateDistribution and UnitCategoryTally, the helpers that the formats with units of varying size share.
public class SharedTallyTests
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
}
