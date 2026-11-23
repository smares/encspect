using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The inter-channel coherence of docs/design/spectral-analysis.md, Step 8, on scripted signals: each reason, the minimum of frames, a silent channel, the cuttings of a stream into
// blocks, the test's arithmetic and the stated bound's median. The fixtures built to show it are held to the model by MeasurementFixtureTests.
public class MeasurementCoherenceTests
{
    private const int Rate = 44100;

    private static CoherenceFigures Measure(double[] left, double[] right, int blockSize = 4096) =>
        MeasurementSignals.Measure([MeasurementSignals.ToIntegers(left, 16), MeasurementSignals.ToIntegers(right, 16)], Rate, 16, blockSize, new MeasureOptions { Include = MeasurementKinds.Spectrum }).Spectrum!.Coherence!;

    private static double[] Noise(int length, uint seed, double gain = 0.2) => [.. MeasurementSignals.Noise(length, seed).Select(v => v * gain)];

    [Fact]
    public void IdenticalChannelsAreCoherentFromZeroHertz()
    {
        double[] noise = Noise(60_000, 5);

        CoherenceFigures coherence = Measure(noise, noise);

        Assert.False(coherence.IsFound);
        Assert.Equal(CoherenceReason.ReachesStart, coherence.Reason);
        Assert.Equal(1000, coherence.TopBandTenths);
        Assert.All(coherence.Rows.ToArray().Skip(10).Take(480), v => Assert.Equal(1000, v));
    }

    [Fact]
    public void IndependentNoiseIsNotCoherentAtTheTopBand()
    {
        CoherenceFigures coherence = Measure(Noise(60_000, 5), Noise(60_000, 6));

        Assert.False(coherence.IsFound);
        Assert.Equal(CoherenceReason.NotCoherent, coherence.Reason);
        Assert.InRange(coherence.TopBandTenths, 0, 30);
    }

    [Fact]
    public void FewerThanTenFramesGiveNoFigureWhateverTheSignalIs()
    {
        double[] noise = Noise(4096 + (8 * 2048), 5);

        CoherenceFigures nine = Measure(noise, noise);
        CoherenceFigures ten = Measure(Noise(4096 + (9 * 2048), 5), Noise(4096 + (9 * 2048), 5));

        Assert.Equal(9, nine.Frames);
        Assert.Equal(CoherenceReason.TooFewFrames, nine.Reason);
        Assert.Equal(10, ten.Frames);
        Assert.Equal(CoherenceReason.ReachesStart, ten.Reason);
    }

    [Fact]
    public void AnIdenticalHighSignalOverIndependentLowOnesStartsAtTheRise()
    {
        double[] low1 = BandLimited.Lines(80_000, Rate, 360, 0.01, 11);
        double[] low2 = BandLimited.Lines(80_000, Rate, 360, 0.01, 12);
        double[] high = [.. BandLimited.Lines(80_000, Rate, 1500, 0.004, 13).Zip(BandLimited.Lines(80_000, Rate, 400, 0.004, 13), (a, b) => a - b)];

        CoherenceFigures coherence = Measure([.. low1.Zip(high, (a, b) => a + b)], [.. low2.Zip(high, (a, b) => a + (0.5 * b))]);

        Assert.True(coherence.IsFound, coherence.Reason.ToString());
        Assert.InRange(coherence.FromFrequency, 3000, 4800);
        Assert.InRange(coherence.ToFrequency, 15000, 18000);
        Assert.Equal(coherence.FramesWithSignal, coherence.FramesShowing);
        Assert.True(coherence.FramesWithSignal > 0);
    }

    [Fact]
    public void ARowWhereAChannelHasNoPowerHasNoCoherence()
    {
        CoherenceFigures coherence = Measure(Noise(60_000, 5), new double[60_000]);

        Assert.All(coherence.Rows.ToArray(), v => Assert.Equal(MeasurementLimits.NoPower, v));
        Assert.False(coherence.IsFound);
        Assert.Equal(CoherenceReason.NotCoherent, coherence.Reason);
        Assert.Equal(0, coherence.TopBandTenths);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(7777)]
    [InlineData(65_536)]
    public void EveryCuttingOfAStreamIntoBlocksGivesTheSameFigures(int block)
    {
        double[] shared = Noise(120_000, 7);
        double[] left = [.. shared.Zip(Noise(120_000, 8, 0.05), (a, b) => a + b)];
        double[] right = [.. shared.Zip(Noise(120_000, 9, 0.05), (a, b) => (0.5 * a) + b)];

        CoherenceFigures reference = Measure(left, right);
        CoherenceFigures cut = Measure(left, right, block);

        Assert.Equal(reference.Frames, cut.Frames);
        Assert.True(reference.Rows.Span.SequenceEqual(cut.Rows.Span));
        Assert.True(reference.Shares.Span.SequenceEqual(cut.Shares.Span));
        Assert.Equal((reference.IsFound, reference.Reason, reference.FromFrequency, reference.ToFrequency, reference.FramesShowing, reference.FramesWithSignal),
            (cut.IsFound, cut.Reason, cut.FromFrequency, cut.ToFrequency, cut.FramesShowing, cut.FramesWithSignal));
    }

    [Fact]
    public void AMonoStreamAndALayoutWithoutAPairHaveNoCoherence()
    {
        double[] noise = Noise(60_000, 5);

        Assert.Null(MeasurementSignals.Measure([MeasurementSignals.ToIntegers(noise, 16)], Rate, 16, 4096).Spectrum!.Coherence);
        Assert.Null(MeasurementSignals.Measure([MeasurementSignals.ToIntegers(noise, 16), MeasurementSignals.ToIntegers(noise, 16)], Rate, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum }, [Speaker.FrontCenter, Speaker.LowFrequency]).Spectrum!.Coherence);
    }

    [Fact]
    public void AStreamUnderEightThousandHertzIsNotSearched()
    {
        double[] noise = Noise(30_000, 5);
        StreamMeasurements m = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(noise, 16), MeasurementSignals.ToIntegers(noise, 16)], 6000, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum });

        Assert.False(m.Spectrum!.Coherence!.IsSearched);
    }

    // The test is 20 |C|^2 >= 17 Sxx Syy: exactly 0.85 passes, and both powers must be above 0.
    [Theory]
    [InlineData(0.85, 1.0, 1.0, true)]
    [InlineData(0.8499, 1.0, 1.0, false)]
    [InlineData(0.0, 1.0, 1.0, false)]
    [InlineData(1.0, 0.0, 1.0, false)]
    [InlineData(1.0, 1.0, 0.0, false)]
    public void TheTestIsTwentyTimesTheSquaredCrossSpectrumAgainstSeventeenTimesThePowers(double squared, double left, double right, bool expected)
    {
        Assert.Equal(expected, CoherenceFinder.IsCoherent(Math.Sqrt(squared * left * right), 0, left, right));
        Assert.Equal(expected, CoherenceFinder.IsCoherent(0, Math.Sqrt(squared * left * right), left, right));
    }

    private static StreamInfo StreamWith(params double[] bounds) => new()
    {
        Nodes = [],
        Codec = "MPEG-1 Layer II",
        Series = bounds.Length == 0 ? [] : [new Series("Intensity Stereo Bound", "Hz", SeriesAxis.FrameOffset, new double[bounds.Length], bounds) { Quantity = SeriesQuantities.IntensityStereoBound }],
    };

    private static StreamMeasurements Record(bool isFound) => new()
    {
        Status = MeasurementStatus.Complete,
        Spectrum = new SpectrumFigures
        {
            FftSize = 4096,
            Combined = new short[512],
            PerChannel = [],
            Cutoff = new SpectralCutoff(),
            Coherence = new CoherenceFigures { IsFound = isFound, FromFrequency = isFound ? 2347 : 0 },
        },
    };

    [Theory]
    [InlineData(new[] { 4000.0, 1000.0, 3000.0 }, 3000, 3)]
    [InlineData(new[] { 4000.0, 1000.0, 3000.0, 2000.0 }, 2000, 4)]
    [InlineData(new[] { 2756.5 }, 2757, 1)]
    public void TheStatedBoundIsTheLowerMedianOfTheSeriesValues(double[] bounds, int median, int units)
    {
        StreamMeasurements attached = StatedBound.Attach(StreamWith(bounds), Record(isFound: true))!;

        StatedIntensityBound stated = attached.Spectrum!.Coherence!.Stated!;
        Assert.Equal((median, units, 2347 - median), (stated.MedianFrequency, stated.Units, stated.Difference));
    }

    [Fact]
    public void TheStatedBoundHasNoDifferenceWhereNoStartIsFoundAndNoSeriesMeansNoStatedBound()
    {
        Assert.Null(StatedBound.Attach(StreamWith(3000), Record(isFound: false))!.Spectrum!.Coherence!.Stated!.Difference);
        Assert.Null(StatedBound.Attach(StreamWith(), Record(isFound: true))!.Spectrum!.Coherence!.Stated);
        Assert.Null(StatedBound.Attach(StreamWith(3000), null));
    }
}
