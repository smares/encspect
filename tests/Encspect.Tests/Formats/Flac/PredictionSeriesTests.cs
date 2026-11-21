using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The series of the concept "Prediction" (docs/design/coding-graphs.md, "Prediction"): FLAC's subframe tallies as category series and numeric series, and the
// predictor order or passes of ALAC, TrueHD and WavPack under the same concept. CategorySummaryTests holds each category series equal to its tally on every fixture.
public class PredictionSeriesTests
{
    [Fact]
    public void A_flac_stream_has_the_subframe_series_under_the_prediction_concept()
    {
        Report report = Fixtures.AnalyzeMpeg("flac/external-16-stereo-b1152.flac");
        StreamInfo stream = Assert.Single(report.Streams);

        Assert.Equal(["Stereo Coding", "Subframe Types", "Partition Orders", "Rice With 5-Bit Parameters", "Subframes With Wasted Bits"], stream.CategorySeries.Select(c => c.Name).Where(n => n != "Fixed Predictor Orders"));
        // The predictor each subframe uses is "Prediction"; the residual and the wasted bits stay under "Other" (the design's survey).
        Assert.All(stream.CategorySeries.Where(c => c.Name is "Subframe Types" or "Fixed Predictor Orders"), c => Assert.Equal(GraphConcept.Prediction, c.Concept));
        Assert.All(stream.CategorySeries.Where(c => c.Name is "Partition Orders" or "Rice With 5-Bit Parameters" or "Subframes With Wasted Bits"), c => Assert.Equal(GraphConcept.None, c.Concept));
        Assert.True(stream.CategorySeries.Single(c => c.Name == "Partition Orders").IsOrdered);

        Series order = stream.Series.Single(s => s.Name == "Highest Predictor Order");
        Series precision = stream.Series.Single(s => s.Name == "Coefficient Precision");
        Assert.Equal(GraphConcept.Prediction, order.Concept);
        Assert.Equal("order", order.Unit);
        Assert.Equal("bits", precision.Unit);
        Assert.Equal(order.X.Length, precision.X.Length);
        Assert.Equal(SeriesAxis.Seconds, stream.Series.Single(s => s.Name == "Highest Predictor Order Over Time").Axis);

        // A fixed block size has no block size series.
        Assert.DoesNotContain(stream.Series, s => s.Name == "Block Size");
    }

    [Fact]
    public void A_stream_whose_frames_differ_in_size_has_the_block_size_series()
    {
        Report report = Fixtures.AnalyzeMpeg("flac/built-variable-block-sizes.flac");

        Series sizes = Assert.Single(report.Streams.Single().Series, s => s.Name == "Block Size");

        Assert.Equal("samples", sizes.Unit);
        Assert.Equal(report.Streams.Single().Series.Single(s => s.Name == "Bitrate").X.Length, sizes.Values.Length);
    }

    [Theory]
    [InlineData("alac/apple-alac-m4a-st44q16.m4a", "Highest Predictor Order", "order")]
    [InlineData("wavpack/built-ape-and-id3v1.wv", "Decorrelation Passes", "passes")]
    [InlineData("truehd/built-atmos-signaled.thd", "Highest Predictor Order", "order")]
    public void Other_lossless_formats_have_their_predictor_series_under_the_same_concept(string fixture, string name, string unit)
    {
        Report report = Fixtures.AnalyzeMpeg(fixture);

        Series series = report.Streams.SelectMany(s => s.Series).First(s => s.Name == name);

        Assert.Equal(GraphConcept.Prediction, series.Concept);
        Assert.Equal(unit, series.Unit);
        Assert.Equal(SeriesAxis.FrameOffset, series.Axis);
        Assert.NotEmpty(series.Values.ToArray());
    }
}
