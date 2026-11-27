using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The replicated bands of docs/design/spectral-analysis.md, Step 9, on scripted signals: each reason, the minima of frames, the ties and the joining of stretches, the frames
// used (every second one, whatever the cutting into blocks), a stream of few rows in reach, and the kind asked alone. The fixtures built to show it are held to the model by
// MeasurementFixtureTests.
public class MeasurementReplicatedBandsTests
{
    private const int Rate = 44100;

    private const int Block = 4096;

    private const int Fade = 32;

    private static readonly double[] Cosines = [.. Enumerable.Range(0, Block).Select(n => Math.Cos(2 * Math.PI * n / Block))];

    // Blocks of 4,096 samples, each a sum of lines at the bin centers of the frame (a frame of the measurer then holds exactly one block, as the used frames start at block
    // starts) with a random amplitude and phase of its own. Lines from bin firstBin to bin lastBin; each patch (source bin, first bin, last bin) copies the amplitudes and phases of the
    // bins from source up to the patch's bin, so that the fine structure of the target rows repeats that of the source rows; mix is the share of the copy (the rest is a line of its own).
    internal static double[] Signal(int blocks, int firstBin, int lastBin, (int Source, int First, int Last)[] patches, double mix = 1.0, double amplitude = 0.004, uint seed = 1, bool changing = false)
    {
        double[] result = new double[blocks * Block];
        double[] previous = [];
        for (int b = 0; b < blocks; b++)
        {
            double[] random = MeasurementSignals.Noise(3 * (lastBin + 2), seed + (uint)(b * 7919));
            double[] amplitudes = new double[lastBin + 2];
            double[] phases = new double[lastBin + 2];
            for (int bin = 0; bin <= lastBin + 1; bin++)
            {
                amplitudes[bin] = amplitude * (0.2 + Math.Abs(random[3 * bin]) * 1.6);
                phases[bin] = random[(3 * bin) + 1] * Math.PI;
            }

            foreach ((int source, int first, int last) in patches)
            {
                int shift = changing ? 2 * (b % 5) : 0;
                for (int bin = first; bin <= last; bin++)
                {
                    int from = source + shift + (bin - first);
                    amplitudes[bin] = (mix * amplitudes[from]) + ((1 - mix) * amplitudes[bin]);
                    phases[bin] = mix >= 1.0 ? phases[from] : phases[bin];
                }
            }

            // The block's period over samples -Fade to Block + Fade (the lines repeat with the frame's period), so that it can be cross-faded into its neighbors: a hard join would
            // spread the average spectrum above the cutoff.
            double[] period = new double[Block + (2 * Fade)];
            for (int bin = firstBin; bin <= lastBin; bin++)
            {
                double c = Math.Cos(phases[bin]);
                double s = Math.Sin(phases[bin]);
                for (int i = 0; i < period.Length; i++)
                {
                    int index = (int)((long)bin * (i - Fade) & (Block - 1));
                    int quarter = (index + (Block / 4)) & (Block - 1);
                    period[i] += amplitudes[bin] * ((c * Cosines[index]) - (s * Cosines[quarter]));
                }
            }

            for (int n = Fade; n < Block - Fade; n++)
            {
                result[(b * Block) + n] = period[n + Fade];
            }

            if (b > 0)
            {
                for (int m = -Fade; m < Fade; m++)
                {
                    double t = 0.5 - (0.5 * Math.Cos(Math.PI * (m + Fade) / (2 * Fade)));
                    result[(b * Block) + m] = ((1 - t) * previous[Block + m + Fade]) + (t * period[m + Fade]);
                }
            }

            previous = period;
        }

        return result;
    }

    private static string Describe(ReplicatedBandsFigures f) =>
        $"{f.IsFound} {f.Reason} from {f.FromFrequency} to {f.ToFrequency}, range {f.RangeFrequency}, used {f.FramesUsed}, top {f.TopBandShareTenths} periodic {f.IsTopBandPeriodic}; stretches "
        + string.Join(" ", f.Stretches.Select(t => $"[{t.FromFrequency}-{t.ToFrequency} {string.Join("/", t.Offsets.Select(o => o.Offset))} {t.LowestShareTenths}-{t.HighestShareTenths}]"))
        + "; bands " + string.Join(" ", f.Bands.Where((_, i) => i % 3 == 0).Select(b => $"{b.StartRow}:{b.Offset}:{b.ShareTenths}:{b.FramesWithSignal}{(b.IsPeriodic ? "P" : string.Empty)}{(b.Passes ? "!" : string.Empty)}"));

    private static ReplicatedBandsFigures Measure(double[] samples, int blockSize = Block, int rate = Rate) =>
        MeasurementSignals.Measure([MeasurementSignals.ToIntegers(samples, 16)], rate, 16, blockSize).Spectrum!.ReplicatedBands!;

    // Lines from 40 Hz (bin 4) to 16 kHz (bin 1486), the bins from 650 (6,998 Hz) up copying the bins 252 lower (63 rows, 2,713 Hz lower).
    private static double[] CopiedSignal(int blocks, uint seed = 1, double mix = 1.0, bool changing = false) =>
        Signal(blocks, 4, 1486, [(398, 650, 1486)], mix, seed: seed, changing: changing);

    [Fact]
    public void ACopyOfTheLowerRowsIsFoundFromWhereItStarts()
    {
        ReplicatedBandsFigures figures = Measure(CopiedSignal(60));

        Assert.True(figures.IsFound, Describe(figures));
        Assert.InRange(figures.FromFrequency, 6500, 7300);
        Assert.InRange(figures.ToFrequency, 15000, 16500);
        ReplicatedStretch stretch = figures.Stretches[^1];
        Assert.Equal(figures.FromFrequency, stretch.FromFrequency);
        Assert.All(stretch.Offsets, o => Assert.InRange(o.Offset, 2690, 2735));
        Assert.InRange(stretch.HighestShareTenths, 900, 1000);
        Assert.Equal(60, figures.FramesUsed);
    }

    [Fact]
    public void WhiteNoiseHasNoReplicatedBands()
    {
        ReplicatedBandsFigures figures = Measure([.. MeasurementSignals.Noise(Block * 60, 3).Select(v => v * 0.2)]);

        Assert.False(figures.IsFound, Describe(figures));
        Assert.Equal(ReplicatedBandsReason.NotMatching, figures.Reason);
        Assert.InRange(figures.TopBandShareTenths, 0, 250);
        Assert.False(figures.IsTopBandPeriodic);
    }

    // The review's B1: frames that repeat are one trial, not many. The same block 60 times, with its copy in it, counts for no band after the first frame.
    [Fact]
    public void FramesThatRepeatCountForNothingAfterTheFirst()
    {
        double[] block = Signal(1, 4, 1486, [(398, 650, 1486)]);
        double[] samples = [.. Enumerable.Range(0, 60).SelectMany(_ => block)];
        ReplicatedBandsFigures figures = Measure(samples);

        Assert.False(figures.IsFound, Describe(figures));
        Assert.Equal(ReplicatedBandsReason.NoSignal, figures.Reason);
        Assert.All(figures.Bands, b => Assert.InRange(b.FramesWithSignal, 0, 1));
    }

    // The review's B2: a band is at least 21 rows, so that white noise at a high rate does not pass by chance (5 rows at 192 kHz read found in 3 of 3 seeds before).
    [Theory]
    [InlineData(96000, 21)]
    [InlineData(192000, 21)]
    public void WhiteNoiseAtAHighRateHasNoReplicatedBandsAndBandsOf21Rows(int rate, int rows)
    {
        for (uint seed = 1; seed <= 3; seed++)
        {
            ReplicatedBandsFigures figures = Measure([.. MeasurementSignals.Noise(rate * 3, seed).Select(v => v * 0.2)], 4096, rate);

            Assert.False(figures.IsFound, $"seed {seed}: {Describe(figures)}");
            Assert.Equal(rows, figures.BandRows);
        }
    }

    [Fact]
    public void AnOffsetThatChangesInEveryFrameMatchesTooLittle()
    {
        ReplicatedBandsFigures figures = Measure(CopiedSignal(60, changing: true));

        Assert.False(figures.IsFound);
    }

    [Fact]
    public void AConstantHarmonicSoundIsNotFoundBecauseItsFramesRepeat()
    {
        // Lines at every twentieth bin (215 Hz, exactly 5 rows apart) with random phases that repeat in every block: the fine structure repeats every 5 rows.
        double[] block = new double[Block];
        double[] phases = MeasurementSignals.Noise(80, 9);
        for (int line = 1; line * 20 <= 1486; line++)
        {
            for (int n = 0; n < Block; n++)
            {
                block[n] += 0.004 * Cosines[(((line * 20 * n) & (Block - 1)) + (int)(phases[line] * 1000 + 1000)) & (Block - 1)];
            }
        }

        ReplicatedBandsFigures figures = Measure([.. Enumerable.Range(0, Block * 60).Select(i => block[i & (Block - 1)])]);

        // Every block repeats the one before, so the bands count in the first frame only (the amendment to S28); a pitch that moves is the periodic case (the `synthetic-replicated-glide` fixture).
        Assert.False(figures.IsFound);
        Assert.Equal(ReplicatedBandsReason.NoSignal, figures.Reason);
    }

    [Fact]
    public void FewerThanTenFramesUsedGiveNoFigureWhateverTheSignalIs()
    {
        // Every second frame is used, and the frames that start at a block start are the used ones: n blocks use n frames.
        ReplicatedBandsFigures nine = Measure(CopiedSignal(9));
        ReplicatedBandsFigures ten = Measure(CopiedSignal(10));

        Assert.Equal(9, nine.FramesUsed);
        Assert.Equal(ReplicatedBandsReason.TooFewFrames, nine.Reason);
        Assert.Equal(10, ten.FramesUsed);
        Assert.NotEqual(ReplicatedBandsReason.TooFewFrames, ten.Reason);
    }

    [Fact]
    public void ASilentStreamHasNoBandWithSignal()
    {
        ReplicatedBandsFigures figures = Measure(new double[Block * 40]);

        Assert.False(figures.IsFound);
        Assert.True(figures.Reason is ReplicatedBandsReason.NoSignal or ReplicatedBandsReason.TooShort, figures.Reason.ToString());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(7777)]
    [InlineData(65_536)]
    public void EveryCuttingOfAStreamIntoBlocksGivesTheSameFigures(int block)
    {
        double[] signal = CopiedSignal(40, mix: 0.7);

        ReplicatedBandsFigures reference = Measure(signal);
        ReplicatedBandsFigures cut = Measure(signal, block);

        Assert.Equal(reference.FramesUsed, cut.FramesUsed);
        Assert.Equal((reference.IsFound, reference.Reason, reference.FromFrequency, reference.ToFrequency), (cut.IsFound, cut.Reason, cut.FromFrequency, cut.ToFrequency));
        Assert.Equal(reference.Bands.Select(b => (b.StartRow, b.Offset, b.ShareTenths, b.FramesWithSignal, b.IsPeriodic, b.Passes)), cut.Bands.Select(b => (b.StartRow, b.Offset, b.ShareTenths, b.FramesWithSignal, b.IsPeriodic, b.Passes)));
    }

    [Fact]
    public void AMixWithNoiseLowersTheShare()
    {
        ReplicatedBandsFigures copy = Measure(CopiedSignal(60));
        ReplicatedBandsFigures mixed = Measure(CopiedSignal(60, mix: 0.6));

        Assert.True(copy.IsFound);
        double Mean(ReplicatedBandsFigures f) => f.Bands.Where(b => b.StartRow is > 180 and < 340 && b.ShareTenths != MeasurementLimits.NoPower).Average(b => b.ShareTenths);
        Assert.True(Mean(mixed) < Mean(copy), $"{Mean(mixed)} against {Mean(copy)}: {Describe(mixed)} / {Describe(copy)}");
    }

    [Fact]
    public void TwoPatchesWithTwoOffsetsAreJoinedIntoOneStretchWithTwoOffsets()
    {
        double[] signal = Signal(60, 4, 1486, [(300, 500, 900), (200, 901, 1486)]);

        ReplicatedBandsFigures figures = Measure(signal);

        Assert.True(figures.IsFound, Describe(figures));
        ReplicatedStretch stretch = figures.Stretches[^1];
        Assert.True(stretch.Offsets.Count >= 2, string.Join(", ", stretch.Offsets.Select(o => o.Offset)));
        Assert.Contains(stretch.Offsets, o => Math.Abs(o.Offset - 2153) <= 45);
        Assert.Contains(stretch.Offsets, o => Math.Abs(o.Offset - 7547) <= 90);
    }

    [Fact]
    public void StretchesThatDoNotReachTheTopOfTheContentAreNotFound()
    {
        // The copy ends at 11 kHz while the content goes on to 16 kHz.
        double[] signal = Signal(60, 4, 1486, [(300, 500, 1000)]);

        ReplicatedBandsFigures figures = Measure(signal);

        Assert.False(figures.IsFound);
        Assert.Equal(ReplicatedBandsReason.NotAtTop, figures.Reason);
        Assert.NotEmpty(figures.Stretches);
    }

    [Fact]
    public void AskedAloneTheKindReportsOnlyItsOwnFigure()
    {
        double[] signal = CopiedSignal(40);
        StreamMeasurements m = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(signal, 16)], Rate, 16, Block, new MeasureOptions { Include = MeasurementKinds.ReplicatedBands });

        Assert.True(m.Spectrum!.IsReplicatedBandsOnly);
        Assert.True(m.Spectrum.ReplicatedBands!.IsFound, Describe(m.Spectrum.ReplicatedBands!));
        Assert.Empty(m.Spectrum.Combined.ToArray());
        Assert.Null(m.Spectrogram);
        Assert.Null(m.Levels);
        IReadOnlyList<MeasurementLine> lines = MeasurementText.Lines(m, isLossy: false);
        Assert.Equal([MeasurementFigure.Measured, MeasurementFigure.ReplicatedBands], lines.Select(l => l.Figure));
    }

    [Fact]
    public void WithoutTheKindThereIsNoFigure()
    {
        StreamMeasurements m = MeasurementSignals.Measure([MeasurementSignals.ToIntegers(CopiedSignal(12), 16)], Rate, 16, Block, new MeasureOptions { Include = MeasurementKinds.Spectrum });

        Assert.Null(m.Spectrum!.ReplicatedBands);
        Assert.DoesNotContain(MeasurementText.Lines(m, isLossy: false), l => l.Figure == MeasurementFigure.ReplicatedBands);
    }

    [Fact]
    public void AStreamUnderEightThousandHertzIsNotSearched()
    {
        StreamMeasurements m = MeasurementSignals.Measure([MeasurementSignals.ToIntegers([.. MeasurementSignals.Noise(40_000, 5).Select(v => v * 0.2)], 16)], 6000, 16, 4096);

        Assert.False(m.Spectrum!.ReplicatedBands!.IsSearched);
        Assert.DoesNotContain(MeasurementText.Lines(m, isLossy: false), l => l.Figure == MeasurementFigure.ReplicatedBands);
    }

    [Fact]
    public void AStreamAtEightThousandHertzHasFewRowsInReachAndStillRuns()
    {
        ReplicatedBandsFigures figures = Measure([.. MeasurementSignals.Noise(1024 * 40, 5).Select(v => v * 0.2)], 1024, 8000);

        Assert.True(figures.IsSearched);
        Assert.Equal(128, figures.BandRows);
        Assert.Equal(32, figures.StepRows);
        Assert.Equal(40, figures.FramesUsed);
        Assert.False(figures.IsFound);
    }

    [Fact]
    public void TheTextNamesTheStretchTheOffsetsAndTheShares()
    {
        ReplicatedBandsFigures found = new()
        {
            IsFound = true,
            FromFrequency = 9518,
            ToFrequency = 20413,
            Stretches =
            [
                new ReplicatedStretch
                {
                    FromFrequency = 9518,
                    ToFrequency = 20413,
                    LowestShareTenths = 530,
                    HighestShareTenths = 900,
                    Offsets = [new ReplicatedOffset { Offset = 6202 }, new ReplicatedOffset { Offset = 11025 }],
                },
            ],
        };

        Assert.Equal("from 9,518 to 20,413 Hz, matching the rows 6,202 and 11,025 Hz lower, in 53.0 to 90.0 % of the frames with signal", MeasurementText.ReplicatedBands(found));
        Assert.Equal("none found (the top band matches in 12.3 % of the frames with signal)", MeasurementText.ReplicatedBands(new ReplicatedBandsFigures { TopBandShareTenths = 123 }));
        Assert.Equal("none found (the top band is periodic)", MeasurementText.ReplicatedBands(new ReplicatedBandsFigures { IsTopBandPeriodic = true }));
        Assert.Equal("none found (fewer than 10 frames used)", MeasurementText.ReplicatedBands(new ReplicatedBandsFigures { Reason = ReplicatedBandsReason.TooFewFrames }));
    }
}
