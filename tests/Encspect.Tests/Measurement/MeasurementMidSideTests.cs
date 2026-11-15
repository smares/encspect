using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The mid and side spectra and the spectral holes of docs/design/spectral-analysis.md, Step 7 (c) and (d): the pair, the figures of scripted signals and of the
// fixtures built to show them, and the holes' arithmetic on hand-made counters.
public class MeasurementMidSideTests
{
    private const int Rate = 44100;

    private static StreamMeasurements MeasureStereo(double[] left, double[] right, int blockSize = 4096, IReadOnlyList<Speaker>? layout = null, MeasureOptions? options = null) =>
        MeasurementSignals.Measure([MeasurementSignals.ToIntegers(left, 16), MeasurementSignals.ToIntegers(right, 16)], Rate, 16, blockSize, options ?? new MeasureOptions { Include = MeasurementKinds.Spectrum }, layout);

    private static double[] Scaled(double[] values, double gain) => [.. values.Select(v => v * gain)];

    [Theory]
    [InlineData(null, 2, 0, 1)]
    [InlineData(null, 1, -1, -1)]
    [InlineData(null, 3, -1, -1)]
    [InlineData(new[] { Speaker.FrontLeft, Speaker.FrontRight }, 2, 0, 1)]
    [InlineData(new[] { Speaker.FrontRight, Speaker.FrontLeft }, 2, 1, 0)]
    [InlineData(new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight }, 6, 0, 1)]
    [InlineData(new[] { Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight }, 3, 0, 2)]
    [InlineData(new[] { Speaker.FrontCenter, Speaker.BackLeft, Speaker.BackRight }, 3, -1, -1)]
    [InlineData(new[] { Speaker.FrontCenter, Speaker.FrontLeft }, 2, -1, -1)]
    [InlineData(new[] { Speaker.Discrete, Speaker.Discrete }, 2, -1, -1)]
    [InlineData(new[] { Speaker.FrontLeft }, 1, -1, -1)]
    public void ThePairIsTheFirstFrontLeftAndFrontRightOrTheTwoChannelsOfAStreamWithoutALayout(Speaker[]? layout, int channels, int left, int right)
    {
        Assert.Equal((left, right), StreamMeasurer.FindPair(layout, channels));
    }

    [Fact]
    public void ThePairIsLookedForAmongTheMeasuredChannelsOnly()
    {
        Speaker[] layout = [.. Enumerable.Repeat(Speaker.Discrete, 32), Speaker.FrontLeft, Speaker.FrontRight];

        Assert.Equal((-1, -1), StreamMeasurer.FindPair(layout, 32));
    }

    [Fact]
    public void AMonoStreamAndALayoutWithoutAPairHaveNoMidAndSide()
    {
        double[] noise = Scaled(MeasurementSignals.Noise(50_000, 5), 0.2);

        Assert.Null(MeasurementSignals.Measure([MeasurementSignals.ToIntegers(noise, 16)], Rate, 16, 4096).Spectrum!.MidSide);
        Assert.Null(MeasureStereo(noise, noise, layout: [Speaker.FrontCenter, Speaker.LowFrequency]).Spectrum!.MidSide);
    }

    [Fact]
    public void MidAndSideAreMeasuredWithTheSpectrumAlone()
    {
        double[] left = Scaled(MeasurementSignals.Noise(50_000, 5), 0.2);
        double[] right = Scaled(MeasurementSignals.Noise(50_000, 6), 0.2);

        MidSideFigures pair = MeasureStereo(left, right).Spectrum!.MidSide!;

        Assert.Equal((0, 1), (pair.LeftChannel, pair.RightChannel));
        Assert.Equal(MeasurementLimits.Rows, pair.Mid.Length);
        Assert.Equal(MeasurementLimits.Rows, pair.Side.Length);
        Assert.True(pair.MidHoles.IsSearched);
    }

    // The mid bin is (L + R) / 2 and the scaling is exact, so identical channels give a mid that is the channel's own rows to the bit and a side of no power at all
    // (which is what a difference of the rows' sums would not give).
    [Fact]
    public void IdenticalChannelsGiveAMidOfTheChannelsRowsAndASideWithoutPower()
    {
        double[] noise = Scaled(MeasurementSignals.Noise(60_000, 9), 0.3);

        SpectrumFigures spectrum = MeasureStereo(noise, noise).Spectrum!;
        MidSideFigures pair = spectrum.MidSide!;

        Assert.True(pair.Mid.Span.SequenceEqual(spectrum.PerChannel[0].Span));
        Assert.All(pair.Side.ToArray(), v => Assert.Equal(MeasurementLimits.NoPower, v));
        Assert.False(pair.SideCutoff.IsFound);
        Assert.Equal(0, pair.SideHoles.CellsWithSignal);
        Assert.Equal(0, pair.SideHoles.HoleCells);
    }

    // Opposite channels are the mirror case: a mid without power and a side of the channel's rows.
    [Fact]
    public void OppositeChannelsGiveAMidWithoutPowerAndASideOfTheChannelsRows()
    {
        int[] noise = MeasurementSignals.ToIntegers(Scaled(MeasurementSignals.Noise(60_000, 9), 0.3), 16);

        SpectrumFigures spectrum = MeasurementSignals.Measure([noise, [.. noise.Select(v => -v)]], Rate, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum }).Spectrum!;

        Assert.All(spectrum.MidSide!.Mid.ToArray(), v => Assert.Equal(MeasurementLimits.NoPower, v));
        Assert.True(spectrum.MidSide.Side.Span.SequenceEqual(spectrum.PerChannel[0].Span));
    }

    // The pair does not move the channels' rows: a channel measured with its partner (through the stored bins) reads the bits it reads alone.
    [Fact]
    public void TheRowsOfEveryChannelAreTheSameWithAndWithoutThePair()
    {
        double[] left = Scaled(MeasurementSignals.Noise(70_000, 11), 0.3);
        double[] right = Scaled(MeasurementSignals.Noise(70_000, 12), 0.1);

        SpectrumFigures stereo = MeasureStereo(left, right).Spectrum!;
        SpectrumFigures leftAlone = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(left, 16)], Rate, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum }).Spectrum!;
        SpectrumFigures rightAlone = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(right, 16)], Rate, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum }).Spectrum!;

        Assert.True(stereo.PerChannel[0].Span.SequenceEqual(leftAlone.PerChannel[0].Span));
        Assert.True(stereo.PerChannel[1].Span.SequenceEqual(rightAlone.PerChannel[0].Span));
        Assert.Equal(leftAlone.ChannelCutoffs[0], stereo.ChannelCutoffs[0]);
    }

    // For every FFT size, the rows summed from the stored bins are the rows of the one-step transform to the bit.
    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    public void TheRowsFromTheStoredBinsAreTheRowsOfTheTransformToTheBit(int size)
    {
        var plan = new FftPlan(size);
        double[] samples = MeasurementSignals.Noise(size, 21);
        double[] re = plan.NewScratch();
        double[] im = plan.NewScratch();
        double[] direct = new double[MeasurementLimits.Rows];
        double[] stored = new double[MeasurementLimits.Rows];
        double[] binsRe = new double[plan.Half];
        double[] binsIm = new double[plan.Half];

        plan.Rows(samples, re, im, direct);
        plan.Bins(samples, re, im, binsRe, binsIm);
        plan.RowsFromBins(binsRe, binsIm, stored);

        Assert.True(direct.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(stored.Select(BitConverter.DoubleToInt64Bits)));
    }

    // The mid and side rows from the bins of the channels equal the rows of the transform of (L + R) / 2 and (L - R) / 2 up to rounding (the transform is linear).
    [Fact]
    public void TheMidAndSideRowsAreTheRowsOfTheSumAndDifferenceOfTheChannels()
    {
        const int Size = 4096;
        var plan = new FftPlan(Size);
        double[] left = MeasurementSignals.Noise(Size, 31);
        double[] right = MeasurementSignals.Noise(Size, 32);
        double[] mid = [.. left.Zip(right, (a, b) => (a + b) * 0.5)];
        double[] side = [.. left.Zip(right, (a, b) => (a - b) * 0.5)];
        double[] re = plan.NewScratch();
        double[] im = plan.NewScratch();
        double[][] bins = [new double[plan.Half], new double[plan.Half], new double[plan.Half], new double[plan.Half]];
        plan.Bins(left, re, im, bins[0], bins[1]);
        plan.Bins(right, re, im, bins[2], bins[3]);
        double[] midRows = new double[MeasurementLimits.Rows];
        double[] sideRows = new double[MeasurementLimits.Rows];
        double[] midExpected = new double[MeasurementLimits.Rows];
        double[] sideExpected = new double[MeasurementLimits.Rows];

        plan.MidSideRows(bins[0], bins[1], bins[2], bins[3], midRows, sideRows);
        plan.Rows(mid, re, im, midExpected);
        plan.Rows(side, re, im, sideExpected);

        for (int r = 0; r < MeasurementLimits.Rows; r++)
        {
            Assert.InRange(midRows[r] / midExpected[r], 1 - 1e-9, 1 + 1e-9);
            Assert.InRange(sideRows[r] / sideExpected[r], 1 - 1e-9, 1 + 1e-9);
        }
    }

    // Every cutting of one stream into blocks gives the same figures, the mid and side included, and the counters of the three new arrays.
    [Fact]
    public void EveryCuttingIntoBlocksGivesTheSameMidSideAndHoles()
    {
        double[] mid = BandLimited.Lines(150_000, Rate, 1500, 0.004, 3);
        double[] side = BandLimited.Lines(150_000, Rate, 1000, 0.0012, 4);
        double[] left = [.. mid.Zip(side, (a, b) => a + b)];
        double[] right = [.. mid.Zip(side, (a, b) => a - b)];
        StreamMeasurements[] runs = [.. new[] { 4096, 1000, 7777, 1 << 16 }.Select(block => MeasureStereo(left, right, block))];

        foreach (StreamMeasurements run in runs.Skip(1))
        {
            Assert.True(run.Spectrum!.MidSide!.Mid.Span.SequenceEqual(runs[0].Spectrum!.MidSide!.Mid.Span));
            Assert.True(run.Spectrum.MidSide.Side.Span.SequenceEqual(runs[0].Spectrum!.MidSide!.Side.Span));
            Assert.Equal(runs[0].Spectrum!.MidSide!.MidCutoff, run.Spectrum.MidSide.MidCutoff);
            Assert.Equal(runs[0].Spectrum!.MidSide!.SideCutoff, run.Spectrum.MidSide.SideCutoff);
            Assert.Equal(runs[0].Spectrum!.Holes.HoleCells, run.Spectrum.Holes.HoleCells);
            Assert.Equal(runs[0].Spectrum!.MidSide!.SideHoles.CellsWithSignal, run.Spectrum.MidSide.SideHoles.CellsWithSignal);
        }
    }

    // Lines to bin 1,500 in the mid signal and to bin 1,000 in the side signal: a mid cutoff above the side's, and the combined spectrum (of the channels, which
    // carry both) with the edge of the larger.
    [Fact]
    public void TheSideSpectrumHasItsOwnCutoffUnderTheMids()
    {
        double[] mid = BandLimited.Lines(150_000, Rate, 1500, 0.004, 3);
        double[] side = BandLimited.Lines(150_000, Rate, 1000, 0.0012, 4);

        SpectrumFigures spectrum = MeasureStereo([.. mid.Zip(side, (a, b) => a + b)], [.. mid.Zip(side, (a, b) => a - b)]).Spectrum!;
        MidSideFigures pair = spectrum.MidSide!;

        Assert.True(pair.MidCutoff.IsFound);
        Assert.True(pair.SideCutoff.IsFound);
        Assert.InRange(pair.MidCutoff.Frequency, 16000, 16200);
        Assert.InRange(pair.SideCutoff.Frequency, 10700, 10900);
        Assert.Equal(pair.MidCutoff.FramesWithSignal, pair.MidCutoff.FramesShowing);
        Assert.Equal(pair.SideCutoff.FramesWithSignal, pair.SideCutoff.FramesShowing);
        Assert.InRange(spectrum.Cutoff.Frequency, 16000, 16200);
    }

    [Fact]
    public void ARateUnder8000HertzMeasuresTheRowsOfMidAndSideButSearchesNothing()
    {
        int[] noise = MeasurementSignals.ToIntegers(Scaled(MeasurementSignals.Noise(20_000, 5), 0.2), 16);
        int[] other = MeasurementSignals.ToIntegers(Scaled(MeasurementSignals.Noise(20_000, 6), 0.2), 16);

        MidSideFigures pair = MeasurementSignals.Measure([noise, other], 4000, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum }).Spectrum!.MidSide!;

        Assert.False(pair.MidCutoff.IsSearched);
        Assert.False(pair.SideHoles.IsSearched);
        Assert.Contains(pair.Mid.ToArray(), v => v != MeasurementLimits.NoPower);
    }

    // The holes' counters on hand-made counts, at the cells FindHoles reads.
    private static CutoffCounters CountersWithHolesAt(int firstRow, int lastRow, int frames, int holesPerRow, int level = 40)
    {
        var counters = new CutoffCounters(hasHoles: true);
        for (int r = firstRow; r <= lastRow; r++)
        {
            counters.All[(r * CutoffCounters.Levels) + level] = frames;
            counters.Holes![(r * CutoffCounters.Levels) + level] = holesPerRow;
        }

        return counters;
    }

    private static readonly CutoffGeometry Geometry = new(Rate, 4096);

    [Fact]
    public void TheShareIsTheHoleCellsOverTheCellsWithSignalToRow505WhereNothingIsFound()
    {
        CutoffCounters counters = CountersWithHolesAt(Geometry.FirstHoleRow, CutoffGeometry.LastCandidate, 20, 5);

        SpectralHoles holes = CutoffFinder.FindHoles(counters, Geometry, new SpectralCutoff(), new SpectralBandwidth());

        int rows = CutoffGeometry.LastCandidate - Geometry.FirstHoleRow + 1;
        Assert.Equal(5L * rows, holes.HoleCells);
        Assert.Equal(20L * rows, holes.CellsWithSignal);
        Assert.Equal(250, holes.ShareTenths);
        Assert.Equal(Geometry.Hertz(Geometry.FirstHoleRow), holes.FromFrequency);
        Assert.Equal(Geometry.Hertz(CutoffGeometry.LastCandidate + 1), holes.ToFrequency);
        Assert.Equal(250, holes.RowShares.Span[Geometry.FirstHoleRow]);
        Assert.Equal(MeasurementLimits.NoPower, holes.RowShares.Span[Geometry.FirstHoleRow - 1]);
    }

    [Fact]
    public void TheRangeEndsAtTheCutoffsEdgeWhereOneIsFoundElseAtTheBandwidthsFloor()
    {
        CutoffCounters counters = CountersWithHolesAt(Geometry.FirstHoleRow, CutoffGeometry.LastCandidate, 20, 1);
        int edgeRow = 300;
        var cutoff = new SpectralCutoff { IsFound = true, Frequency = Geometry.Hertz(edgeRow + 1) };
        var bandwidth = new SpectralBandwidth { IsFound = true, Frequency = Geometry.Hertz(400) };

        SpectralHoles byCutoff = CutoffFinder.FindHoles(counters, Geometry, cutoff, bandwidth);
        SpectralHoles byBandwidth = CutoffFinder.FindHoles(counters, Geometry, new SpectralCutoff(), bandwidth);

        Assert.Equal(edgeRow - Geometry.FirstHoleRow + 1, byCutoff.HoleCells);
        Assert.Equal(Geometry.Hertz(edgeRow + 1), byCutoff.ToFrequency);
        Assert.Equal(400 - Geometry.FirstHoleRow, byBandwidth.HoleCells);
        Assert.Equal(Geometry.Hertz(400), byBandwidth.ToFrequency);
        Assert.Equal(CutoffGeometry.LastCandidate - Geometry.FirstHoleRow + 1, byCutoff.RowShares.Span.ToArray().Count(v => v != MeasurementLimits.NoPower));
    }

    [Fact]
    public void ACutoffBelowTheFirstRowLeavesAnEmptyRange()
    {
        CutoffCounters counters = CountersWithHolesAt(Geometry.FirstHoleRow, CutoffGeometry.LastCandidate, 20, 1);

        SpectralHoles holes = CutoffFinder.FindHoles(counters, Geometry, new SpectralCutoff { IsFound = true, Frequency = Geometry.Hertz(Geometry.FirstHoleRow) }, new SpectralBandwidth());

        Assert.Equal(0, holes.CellsWithSignal);
        Assert.Equal(0, holes.FromFrequency);
        Assert.Equal(0, holes.ToFrequency);
        Assert.Equal(0, holes.ShareTenths);
    }

    // A row whose frames are all at the lowest cell (no power, or less than 2^-63) has no frames with signal.
    [Fact]
    public void RowsWithoutPowerHaveNoCellsWithSignal()
    {
        CutoffCounters counters = CountersWithHolesAt(Geometry.FirstHoleRow, CutoffGeometry.LastCandidate, 20, 0, level: 0);

        SpectralHoles holes = CutoffFinder.FindHoles(counters, Geometry, new SpectralCutoff(), new SpectralBandwidth());

        Assert.Equal(0, holes.CellsWithSignal);
        Assert.All(holes.RowShares.ToArray(), v => Assert.Equal(MeasurementLimits.NoPower, v));
    }

    [Theory]
    [InlineData(1, 3, 333)]
    [InlineData(1, 8, 125)]
    [InlineData(1, 40, 25)]
    [InlineData(1, 1000, 1)]
    [InlineData(5, 1000, 5)]
    [InlineData(1, 2000, 1)]
    [InlineData(1, 4000, 0)]
    public void TheShareIsRoundedToTheNearestTenthOfAPercentWithHalvesUp(long holes, long cells, int tenths)
    {
        Assert.Equal(tenths, new SpectralHoles { HoleCells = holes, CellsWithSignal = cells }.ShareTenths);
    }

    [Fact]
    public void ARowWithFewerFramesWithSignalThanTheReferenceAllowsIsNotCounted()
    {
        // 100 frames at the top level and 3 far below it (more than ten levels): the 3 are not frames with signal.
        var counters = new CutoffCounters(hasHoles: true);
        int row = Geometry.FirstHoleRow + 10;
        counters.All[(row * CutoffCounters.Levels) + 50] = 100;
        counters.All[(row * CutoffCounters.Levels) + 30] = 3;
        counters.Holes![(row * CutoffCounters.Levels) + 30] = 3;

        SpectralHoles holes = CutoffFinder.FindHoles(counters, Geometry, new SpectralCutoff(), new SpectralBandwidth());

        Assert.Equal(100, holes.CellsWithSignal);
        Assert.Equal(0, holes.HoleCells);
    }

    // A stream measured without a spectrum kind or with a rate under 8,000 Hz has no counters and so no holes searched.
    [Fact]
    public void WithoutCountersNothingIsSearched()
    {
        Assert.False(CutoffFinder.FindHoles(null, Geometry, new SpectralCutoff(), new SpectralBandwidth()).IsSearched);
        Assert.False(CutoffFinder.FindHoles(new CutoffCounters(), Geometry, new SpectralCutoff(), new SpectralBandwidth()).IsSearched);
    }

    // The memory estimate holds with a pair at the largest FFT size and 32 channels, where the stored bins and the three counter sets are largest.
    [Fact]
    public void TheMemoryEstimateHoldsForAPairOf32ChannelsAtTheLargestFftSize()
    {
        const int Channels = 32;
        const int SampleRate = 352800;
        Speaker[] layout = [Speaker.FrontLeft, Speaker.FrontRight, .. Enumerable.Repeat(Speaker.Discrete, Channels - 2)];
        var format = new PcmFormat(SampleRate, Channels, 16, SampleFormat.Integer) { ChannelLayout = layout };
        long estimate = StreamMeasurer.EstimateBytes(format, new MeasureOptions());
        int frame = FftPlan.SizeFor(SampleRate);
        int[] data = new int[Channels * frame];
        Array.Fill(data, 1);
        for (int i = 0; i < data.Length; i += 7)
        {
            data[i] = -300 + (i % 600);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = MeasurementKinds.Spectrum }, CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, frame, Channels, frame, data));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(m.Spectrum!.MidSide);
        Assert.True(allocated < estimate, $"allocated {allocated:N0}, estimated {estimate:N0}");
        Assert.InRange(estimate, 1, 64L << 20);
    }

    [Fact]
    public void TheMemoryEstimateHoldsForStereoAtCdRate()
    {
        var format = new PcmFormat(Rate, 2, 16, SampleFormat.Integer);
        long estimate = StreamMeasurer.EstimateBytes(format, new MeasureOptions());
        int[] data = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(2 * 20_000, 5), 16);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 20_000, 2, 20_000, data));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(m.Spectrum!.MidSide);
        Assert.True(allocated < estimate, $"allocated {allocated:N0}, estimated {estimate:N0}");

        // The pair adds the stored bins (16 N bytes), the mid and side rows and the counters of three spectra: about 1 MB (the design's estimate: 1.2 MB with the combined spectrum's holes).
        var unpaired = new PcmFormat(Rate, 2, 16, SampleFormat.Integer) { ChannelLayout = [Speaker.Discrete, Speaker.Discrete] };
        var spectrumOnly = new MeasureOptions { Include = MeasurementKinds.Spectrum };
        long added = StreamMeasurer.EstimateBytes(format, spectrumOnly) - StreamMeasurer.EstimateBytes(unpaired, spectrumOnly);
        Assert.InRange(added, 900_000, 1_500_000);
    }

    // The text, the JSON and the CSV carry the figures.
    [Fact]
    public void TheTextHasAMidSideLineWhereThereIsAPairAndAHolesLine()
    {
        double[] mid = BandLimited.Lines(150_000, Rate, 1500, 0.004, 3);
        double[] side = BandLimited.Lines(150_000, Rate, 1000, 0.0012, 4);
        StreamMeasurements m = MeasureStereo([.. mid.Zip(side, (a, b) => a + b)], [.. mid.Zip(side, (a, b) => a - b)], layout: null, options: new MeasureOptions { Include = MeasurementKinds.Spectrum });

        IReadOnlyList<MeasurementLine> lines = MeasurementText.Lines(m, isLossy: false);

        MeasurementLine pair = Assert.Single(lines, l => l.Figure == MeasurementFigure.MidSide);
        Assert.StartsWith("mid cutoff 16,", pair.Text, StringComparison.Ordinal);
        Assert.Contains("side cutoff 10,", pair.Text, StringComparison.Ordinal);
        Assert.Equal("Mid/Side", pair.Name);
        MeasurementLine holes = Assert.Single(lines, l => l.Figure == MeasurementFigure.SpectralHoles);
        Assert.Contains("% of the cells with signal from ", holes.Text, StringComparison.Ordinal);
        Assert.Contains("; mid ", holes.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(pair.Description));
        Assert.False(string.IsNullOrEmpty(holes.Description));
    }

    [Fact]
    public void AMonoStreamHasAHolesLineButNoMidSideLine()
    {
        StreamMeasurements m = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(Scaled(MeasurementSignals.Noise(60_000, 5), 0.2), 16)], Rate, 16, 4096, new MeasureOptions { Include = MeasurementKinds.Spectrum });

        IReadOnlyList<MeasurementLine> lines = MeasurementText.Lines(m, isLossy: false);

        Assert.DoesNotContain(lines, l => l.Figure == MeasurementFigure.MidSide);
        Assert.DoesNotContain("; mid ", Assert.Single(lines, l => l.Figure == MeasurementFigure.SpectralHoles).Text, StringComparison.Ordinal);
    }
}
