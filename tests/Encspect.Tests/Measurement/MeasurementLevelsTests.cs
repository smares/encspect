using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The levels of docs/design/spectral-analysis.md ("Levels") on signals built here: exact sums wider than 64 bits, the most negative code, runs
// and silence across blocks and gaps, floats (NaN, huge values, bit-depth grids), and figures that do not depend on how a decoder cuts blocks.
public class MeasurementLevelsTests
{
    private static readonly MeasureOptions LevelsOnly = new() { Include = MeasurementKinds.Levels };

    // Measures segments of integer samples; each segment starts at its position, a gap lies between segments that do not touch.
    private static StreamMeasurements Run(int bits, int blockSize, MeasureOptions options, int rate, params (long Position, int[][] Channels)[] segments)
    {
        int channels = segments[0].Channels.Length;
        var format = new PcmFormat(rate, channels, bits, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, options, CancellationToken.None, _ => true);
        foreach ((long position, int[][] data) in segments)
        {
            for (int start = 0; start < data[0].Length; start += blockSize)
            {
                int n = Math.Min(blockSize, data[0].Length - start);
                int[] planar = new int[channels * n];
                for (int c = 0; c < channels; c++)
                {
                    data[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
                }

                measurer.Write(new PcmBlock(position + start, n, channels, n, planar));
            }
        }

        Assert.Null(measurer.Failure);
        return measurer.Result(MeasurementStatus.Complete, null);
    }

    private static LevelFigures Levels(int bits, int[][] channels, int blockSize = 4096) => Run(bits, blockSize, LevelsOnly, 44100, (0, channels)).Levels!;

    private static LevelFigures Floats(float[][] channels, int blockSize = 4096, SampleFormat format = SampleFormat.Float32, MeasureOptions? options = null, int rate = 44100)
    {
        var pcm = new PcmFormat(rate, channels.Length, 32, format);
        var measurer = new StreamMeasurer(pcm, options ?? LevelsOnly, CancellationToken.None, _ => true);
        for (int start = 0; start < channels[0].Length; start += blockSize)
        {
            int n = Math.Min(blockSize, channels[0].Length - start);
            if (format == SampleFormat.Float32)
            {
                float[] planar = new float[channels.Length * n];
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].AsSpan(start, n).CopyTo(planar.AsSpan(c * n, n));
                }

                measurer.Write(new PcmBlock(start, n, channels.Length, n, planar));
            }
            else
            {
                double[] planar = new double[channels.Length * n];
                for (int c = 0; c < channels.Length; c++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        planar[(c * n) + i] = channels[c][start + i];
                    }
                }

                measurer.Write(new PcmBlock(start, n, channels.Length, n, planar));
            }
        }

        Assert.Null(measurer.Failure);
        StreamMeasurements result = measurer.Result(MeasurementStatus.Complete, null);
        return result.Levels!;
    }

    [Fact]
    public void AFullScaleSquareWaveReadsZeroDecibelsAndEveryHalfPeriodIsARun()
    {
        int[] square = [.. Enumerable.Range(0, 5000).Select(i => (i / 50) % 2 == 0 ? 32767 : -32768)];
        ChannelLevels stream = Levels(16, [square]).Stream;

        Assert.Equal(0, stream.SamplePeakTenths);
        Assert.Equal(0, stream.RmsTenths);
        Assert.Equal((100, 5000, 50), (stream.ClippingRuns, stream.ClippedSamples, stream.LongestClippingRun));
        Assert.Equal(32768, stream.HighestMagnitude);
    }

    // Runs of 16 or more are skipped with a vectorized search: lengths around the switch, one that ends exactly at the end of the block, and one
    // longer than a piece of 16,384 samples, which the block's pieces cut in two.
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(300)]
    [InlineData(40000)]
    public void ARunOfAnyLengthIsCountedOnceWhereverItEnds(int length)
    {
        int[] block = [.. Enumerable.Repeat(7, 5), .. Enumerable.Repeat(-32768, length), 7, 7, .. Enumerable.Repeat(32767, length)];
        ChannelLevels stream = Levels(16, [block]).Stream;

        Assert.Equal((2, 2L * length, (long)length), (stream.ClippingRuns, stream.ClippedSamples, stream.LongestClippingRun));
    }

    [Fact]
    public void TheMostNegativeCodeCountsAsFullScaleAndTheMostPositiveDoesNot()
    {
        Assert.Equal(0, Levels(8, [[-128, 0, 5]]).Stream.SamplePeakTenths);
        Assert.Equal(-1, Levels(8, [[127, 0, 5]]).Stream.SamplePeakTenths);
        Assert.Equal(0, Levels(16, [[32767, 0]]).Stream.SamplePeakTenths);
    }

    [Fact]
    public void ASilentChannelHasNoPeakNoLevelAndNoBitInUse()
    {
        LevelFigures levels = Levels(16, [new int[1000], [.. Enumerable.Repeat(100, 1000)]]);

        ChannelLevels silent = levels.Channels[0];
        Assert.Equal(MeasurementLimits.NoLevel, silent.SamplePeakTenths);
        Assert.Equal(MeasurementLimits.NoLevel, silent.RmsTenths);
        Assert.Equal(MeasurementLimits.NoLevel, silent.DcOffsetTenths);
        Assert.Null(silent.LowBitsUnused);
        Assert.Equal((1000, 1000, 1000), (silent.SilentAtStart, silent.SilentAtEnd, silent.ZeroSamples));

        // The stream's figures are the other channel's where this one has none; its sample frames are not silent.
        Assert.Equal(levels.Channels[1].SamplePeakTenths, levels.Stream.SamplePeakTenths);
        Assert.Equal(0, levels.Stream.ZeroSamples);
        Assert.Equal(2, levels.Stream.LowBitsUnused);
    }

    [Fact]
    public void TheDcOffsetIsTheExactMeanInMillionthsOfAPercent()
    {
        ChannelLevels left = Levels(16, [[.. Enumerable.Repeat(1000, 4096)]]).Stream;
        ChannelLevels right = Levels(16, [[.. Enumerable.Repeat(-300, 4096)]]).Stream;

        Assert.Equal(3051758, left.DcOffsetMicropercent);
        Assert.Equal(-915527, right.DcOffsetMicropercent);
        Assert.InRange(left.DcOffsetTenths, -304, -302);
    }

    [Fact]
    public void SumsWiderThan64BitsAreExact()
    {
        // 400,000 samples of -2^23 square to 400,000 * 2^46, more than 2^64; 100 samples of -2^31 pass it within two samples.
        ChannelLevels bits24 = Levels(24, [[.. Enumerable.Repeat(-8388608, 400_000)]]).Stream;
        ChannelLevels bits32 = Levels(32, [[.. Enumerable.Repeat(int.MinValue, 100_000)]]).Stream;

        foreach (ChannelLevels full in new[] { bits24, bits32 })
        {
            Assert.Equal(0, full.RmsTenths);
            Assert.Equal(0, full.SamplePeakTenths);
            Assert.Equal(-100_000_000, full.DcOffsetMicropercent);
            Assert.Equal(0, full.DcOffsetTenths);
        }

        Assert.Equal(2147483648, bits32.HighestMagnitude);
        Assert.Equal(31, bits32.LowBitsUnused);
    }

    [Fact]
    public void ARunIsCountedOnceItReachesThreeSamplesAndGrowsFromThere()
    {
        int[] samples = new int[100];
        samples[10] = 32767;
        samples[20] = samples[21] = 32767;
        samples[30] = samples[31] = samples[32] = 32767;
        samples[40] = samples[41] = samples[42] = samples[43] = samples[44] = -32768;
        samples[50] = samples[51] = 32767;
        samples[52] = samples[53] = -32768;

        ChannelLevels stream = Levels(16, [samples], 7).Stream;

        Assert.Equal((2, 8, 5), (stream.ClippingRuns, stream.ClippedSamples, stream.LongestClippingRun));
    }

    [Fact]
    public void NoRunAndNoSilentStretchIsCountedAcrossAGap()
    {
        int[] before = [.. Enumerable.Repeat(5, 10), 32767, 32767, 0, 0, 0];
        int[] after = [0, 0, 32767, 32767, .. Enumerable.Repeat(5, 10)];

        ChannelLevels together = Run(16, 3, LevelsOnly, 44100, (0, [[.. before, .. after]])).Levels!.Stream;
        ChannelLevels apart = Run(16, 3, LevelsOnly, 44100, (0, [before]), (1000, [after])).Levels!.Stream;

        Assert.Equal(0, together.ClippingRuns);
        Assert.Equal(0, apart.ClippingRuns);
        Assert.Equal((5, 5), (together.ZeroSamples, apart.ZeroSamples));
        Assert.Equal(0, together.SilentAtStart);

        // The silent stretch at the end is the one after the gap's last sample; the one before the gap does not join it.
        int[] silenceBeforeAndAfter = [.. new int[10], 7, 7, .. new int[10]];
        StreamMeasurements gap = Run(16, 4, LevelsOnly, 44100, (0, [silenceBeforeAndAfter[..12]]), (100, [silenceBeforeAndAfter[12..]]));
        Assert.Equal((10, 10, 20), (gap.Levels!.Stream.SilentAtStart, gap.Levels.Stream.SilentAtEnd, gap.Levels.Stream.ZeroSamples));
        Assert.Equal(1, gap.Gaps);
    }

    [Fact]
    public void ARunThatWouldReachThreeSamplesOnlyAcrossAGapIsNoRun()
    {
        int[] before = [5, 5, 32767, 32767];
        int[] after = [32767, 5, 5];
        int[] negative = [-32768, -32768, 5];
        int[] negativeAfter = [-32768, 5];

        Assert.Equal(0, Run(16, 2, LevelsOnly, 44100, (0, [before]), (50, [after])).Levels!.Stream.ClippingRuns);
        Assert.Equal(0, Run(16, 100, LevelsOnly, 44100, (0, [negative]), (50, [negativeAfter])).Levels!.Stream.ClippingRuns);
        Assert.Equal(1, Run(16, 2, LevelsOnly, 44100, (0, [before]), (4, [after])).Levels!.Stream.ClippingRuns);
    }

    [Fact]
    public void ASilentStretchDoesNotJoinAcrossAGap()
    {
        // Both stretches end at the gap: the leading one is closed there, and the trailing one starts again after it.
        LevelFigures levels = Run(16, 2, LevelsOnly, 44100, (0, [[0, 0, 0]]), (50, [[0, 0, 5]])).Levels!;
        Assert.Equal((3, 0, 5), (levels.Stream.SilentAtStart, levels.Stream.SilentAtEnd, levels.Stream.ZeroSamples));

        levels = Run(16, 2, LevelsOnly, 44100, (0, [[5, 0, 0]]), (50, [[0, 0, 0]])).Levels!;
        Assert.Equal((0, 3, 5), (levels.Stream.SilentAtStart, levels.Stream.SilentAtEnd, levels.Stream.ZeroSamples));
    }

    [Theory]
    [InlineData(8, 1445)]
    [InlineData(16, 963)]
    [InlineData(24, 482)]
    public void ASampleBeyondTheDeclaredBitsDoesNotWrapTheSumOfSquares(int bits, int tenths)
    {
        // A decoder that does not clamp (FLAC's restore on a corrupt frame) can write 32-bit values in a narrower stream; their squares pass the
        // 64-bit partial sum sized for the declared bits within a few samples. Ten samples of -2^31 against a full scale of 2^(bits - 1) are
        // 20 log10 2^(33 - bits) dB, as a peak and as an RMS level.
        int[] wild = [.. Enumerable.Repeat(int.MinValue, 10)];
        ChannelLevels stream = Levels(bits, [wild]).Stream;

        Assert.Equal(tenths, stream.RmsTenths);
        Assert.Equal(tenths, stream.SamplePeakTenths);
    }

    [Fact]
    public void TheJointStretchesOfSeveralChannelsEndAtAGap()
    {
        int[][] zeros = [[0, 0, 0], [0, 0, 0]];
        int[][] endsWithSignal = [[0, 0, 5], [0, 0, 5]];
        int[][] startsWithSignal = [[5, 0, 0], [5, 0, 0]];

        LevelFigures leading = Run(16, 2, LevelsOnly, 44100, (0, zeros), (50, endsWithSignal)).Levels!;
        LevelFigures trailing = Run(16, 2, LevelsOnly, 44100, (0, startsWithSignal), (50, zeros)).Levels!;

        Assert.Equal((3, 0, 5), (leading.Stream.SilentAtStart, leading.Stream.SilentAtEnd, leading.Stream.ZeroSamples));
        Assert.Equal((0, 3, 5), (trailing.Stream.SilentAtStart, trailing.Stream.SilentAtEnd, trailing.Stream.ZeroSamples));
    }

    [Fact]
    public void TheStreamsDcOffsetIsThatOfTheChannelWithTheLargestSumEvenWhereEveryFigureRoundsTo0()
    {
        // The first channel has no offset at all (no level); the second has one sample of 1 in 100,000, which rounds to 0 millionths of a percent.
        int[] flat = new int[100_000];
        int[] one = new int[100_000];
        one[7] = 1;

        ChannelLevels stream = Levels(16, [flat, one]).Stream;

        Assert.Equal(0, stream.DcOffsetMicropercent);
        Assert.NotEqual(MeasurementLimits.NoLevel, stream.DcOffsetTenths);
    }

    [Fact]
    public void SubnormalDoublesAreSamplesThatAreNot0AndTakePartInNoSum()
    {
        double[] samples = [.. Enumerable.Repeat(1e-310, 100), .. Enumerable.Repeat(0.5, 100)];
        var measurer = new StreamMeasurer(new PcmFormat(44100, 1, 64, SampleFormat.Float64), LevelsOnly, CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, 200, 1, 200, samples));
        ChannelLevels stream = measurer.Result(MeasurementStatus.Complete, null).Levels!.Stream;

        Assert.Equal(0, stream.ZeroSamples);
        Assert.Equal(-60, stream.SamplePeakTenths);
        Assert.False(stream.HasOnly24BitValues);

        // The mean square of the 100 samples of 0.5 over 200: 0.125, -9.0 dB.
        Assert.Equal(-90, stream.RmsTenths);
    }

    [Fact]
    public void FloatsUnderTheSpectrumsFlushLimitAreSamplesThatAreNot0()
    {
        float[] tiny = [.. Enumerable.Repeat(1e-35f, 100)];

        foreach (MeasureOptions options in new[] { LevelsOnly, new MeasureOptions() })
        {
            ChannelLevels stream = Floats([tiny], 50, options: options).Stream;
            Assert.Equal(0, stream.ZeroSamples);
            Assert.Equal(0, stream.SilentAtStart);
            Assert.NotEqual(MeasurementLimits.NoLevel, stream.SamplePeakTenths);
            Assert.True(stream.SamplePeakTenths < -6000);
        }
    }

    [Fact]
    public void AStreamThatIsSilentBeforeAGapHasItsLeadingStretchClosedThere()
    {
        StreamMeasurements gap = Run(16, 4, LevelsOnly, 44100, (0, [new int[10]]), (50, [new int[10]]));

        Assert.Equal((10, 10, 20), (gap.Levels!.Stream.SilentAtStart, gap.Levels.Stream.SilentAtEnd, gap.Levels.Stream.ZeroSamples));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(1 << 17)]
    public void TheLevelsDoNotDependOnHowTheDecoderCutsBlocks(int blockSize)
    {
        int[] first = MeasurementSignals.ToIntegers(MeasurementSignals.Noise(300_000, 1).Select(v => v * 1.5).ToArray(), 16);
        int[] second = [.. first.Select(v => v / 3)];
        for (int i = 20_000; i < 20_100; i++)
        {
            first[i] = 32767;
            second[i + 40_000] = 0;
        }

        int[][] data = [first, second];
        LevelFigures reference = Run(16, 4096, LevelsOnly, 44100, (0, data)).Levels!;
        LevelFigures other = Run(16, blockSize, LevelsOnly, 44100, (0, data)).Levels!;

        Assert.Equal(reference.Stream, other.Stream);
        Assert.True(reference.Channels.SequenceEqual(other.Channels));
        Assert.True(reference.Stream.ClippingRuns > 0);
    }

    [Fact]
    public void TheLevelsAreTheSameWithAndWithoutTheSpectrum()
    {
        int[][] data = [MeasurementSignals.ToIntegers(MeasurementSignals.Noise(100_000, 3).Select(v => v * 1.2).ToArray(), 16), MeasurementSignals.ToIntegers(MeasurementSignals.Noise(100_000, 4), 16)];

        LevelFigures alone = Run(16, 5000, LevelsOnly, 44100, (0, data)).Levels!;
        LevelFigures both = Run(16, 5000, new MeasureOptions(), 44100, (0, data)).Levels!;
        StreamMeasurements spectrumOnly = Run(16, 5000, new MeasureOptions { Include = MeasurementKinds.Spectrum }, 44100, (0, data));

        Assert.Equal(alone.Stream, both.Stream);
        Assert.True(alone.Channels.SequenceEqual(both.Channels));
        Assert.Null(spectrumOnly.Levels);
        Assert.Null(Run(16, 5000, LevelsOnly, 44100, (0, data)).Spectrum);
    }

    [Fact]
    public void TheLevelsDoNotNeedASampleRate()
    {
        StreamMeasurements m = Run(16, 100, LevelsOnly, 0, (0, [[1, 2, 3, 4, 5]]));

        Assert.NotNull(m.Levels);
        Assert.Equal(5, m.Samples);
    }

    [Fact]
    public void TheStreamsFiguresAreThePeakOfTheLoudestAndTheRmsOfAllChannelsTogether()
    {
        int[] quiet = [.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 1000 : -1000)];
        int[] loud = [.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 8000 : -8000)];
        LevelFigures levels = Levels(16, [quiet, loud]);

        Assert.Equal(levels.Channels[1].SamplePeakTenths, levels.Stream.SamplePeakTenths);
        Assert.True(levels.Channels[0].SamplePeakTenths < levels.Channels[1].SamplePeakTenths);

        // Mean square of both: (1000^2 + 8000^2) / 2 = 32.5 million, against 32,768^2: -15.2 dBFS.
        Assert.Equal(-152, levels.Stream.RmsTenths);
    }

    [Fact]
    public void TheDigitalSilenceOfTheStreamIsWhereEveryChannelIsZero()
    {
        int[] a = [.. new int[300], .. Enumerable.Repeat(9, 700)];
        int[] b = [.. new int[100], .. Enumerable.Repeat(9, 800), .. new int[100]];
        LevelFigures levels = Levels(16, [a, b]);

        Assert.Equal((100, 0, 100), (levels.Stream.SilentAtStart, levels.Stream.SilentAtEnd, levels.Stream.ZeroSamples));
        Assert.Equal((300, 0, 300), (levels.Channels[0].SilentAtStart, levels.Channels[0].SilentAtEnd, levels.Channels[0].ZeroSamples));
        Assert.Equal((100, 100, 200), (levels.Channels[1].SilentAtStart, levels.Channels[1].SilentAtEnd, levels.Channels[1].ZeroSamples));
    }

    [Fact]
    public void TheLowBitsUnusedAreTheTrailingZerosOfTheOrOfEverySample()
    {
        int[] shifted = [.. Enumerable.Range(1, 500).Select(i => (i % 200) << 8)];
        int[] odd = [.. Enumerable.Range(1, 500).Select(i => (i % 200) << 12)];

        LevelFigures levels = Levels(24, [shifted, odd]);

        Assert.Equal(8, levels.Channels[0].LowBitsUnused);
        Assert.Equal(12, levels.Channels[1].LowBitsUnused);
        Assert.Equal(8, levels.Stream.LowBitsUnused);
        Assert.Equal(24, levels.BitsPerSample);
    }

    [Fact]
    public void AFloatStreamIsMeasuredInDoubleAndFullScaleIsOne()
    {
        float[] samples = [1.0f, 1.0f, 1.0f, -1.0f, 0.5f, 0.25f, 0f, 0f, -1.0f, -1.5f];
        LevelFigures levels = Floats([samples], 3);

        Assert.Equal(SampleFormat.Float32, levels.Format);
        Assert.Equal(35, levels.Stream.SamplePeakTenths);
        Assert.Equal((1, 3, 3), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples, levels.Stream.LongestClippingRun));
        Assert.Equal((0, 0, 2), (levels.Stream.SilentAtStart, levels.Stream.SilentAtEnd, levels.Stream.ZeroSamples));
        Assert.False(levels.Stream.HasOnly16BitValues);
        Assert.Null(levels.Stream.LowBitsUnused);
    }

    [Fact]
    public void AFloatThatIsAWholeMultipleOfTheGridHasOnlyThoseBitsOfValue()
    {
        float[] sixteen = [.. Enumerable.Range(-100, 200).Select(i => i / 32768f)];
        float[] twentyFour = [.. sixteen, 1f / 8388608f];
        float[] neither = [.. sixteen, 1f / 16777216f];
        float[] one = [.. sixteen, 1.0f];

        Assert.True(Floats([sixteen]).Stream is { HasOnly16BitValues: true, HasOnly24BitValues: true });
        Assert.True(Floats([twentyFour]).Stream is { HasOnly16BitValues: false, HasOnly24BitValues: true });
        Assert.True(Floats([neither]).Stream is { HasOnly16BitValues: false, HasOnly24BitValues: false });

        // [-1, 1): a sample of exactly +1.0 is not a 16-bit value.
        Assert.True(Floats([one]).Stream is { HasOnly16BitValues: false, HasOnly24BitValues: false });
        Assert.True(Floats([[-1f, -0.5f]]).Stream.HasOnly16BitValues);
    }

    [Fact]
    public void NonFiniteFloatsAreMeasuredAsZeroAndHugeOnesAreClamped()
    {
        float[] samples = [float.NaN, float.PositiveInfinity, 0.5f, 3e38f, -3e38f];
        var pcm = new PcmFormat(44100, 1, 32, SampleFormat.Float32);
        var measurer = new StreamMeasurer(pcm, LevelsOnly, CancellationToken.None, _ => true);
        measurer.Write(new PcmBlock(0, samples.Length, 1, samples.Length, samples));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);

        Assert.Equal(2, m.NonFiniteSamples);
        Assert.True(m.HasClampedSamples);

        // 1,024 times full scale is +60.2 dB; the two huge samples have opposite signs, so they are no run, and the other three are 0 and 0.5 only.
        Assert.Equal(602, m.Levels!.Stream.SamplePeakTenths);
        Assert.Equal(0, m.Levels.Stream.ClippingRuns);
        Assert.Equal(2, m.Levels.Stream.ZeroSamples);
    }

    [Fact]
    public void ADoubleStreamGivesTheSameFiguresAsTheFloatsItHolds()
    {
        float[] samples = [.. Enumerable.Range(0, 5000).Select(i => ((i % 97) - 48) / 64f)];

        LevelFigures asFloats = Floats([samples]);
        LevelFigures asDoubles = Floats([samples], format: SampleFormat.Float64);

        Assert.Equal(asFloats.Stream, asDoubles.Stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void TheLevelsOfFloatsDoNotDependOnHowTheDecoderCutsBlocks(int blockSize)
    {
        float[] samples = [.. MeasurementSignals.Noise(30_000, 21).Select(v => (float)(v * 1.3))];
        Array.Fill(samples, 1.0f, 5000, 12);
        Array.Fill(samples, 0f, 9000, 2000);

        LevelFigures reference = Floats([samples, [.. samples.Select(v => v / 3)]], 4096);
        LevelFigures other = Floats([samples, [.. samples.Select(v => v / 3)]], blockSize);

        Assert.Equal(reference.Stream, other.Stream);
        Assert.True(reference.Channels.SequenceEqual(other.Channels));
        Assert.True(reference.Stream.ClippingRuns > 0);
    }

    [Fact]
    public void AFloatStreamWithTheSpectrumGivesTheSameLevelsAsWithout()
    {
        float[] samples = [.. MeasurementSignals.Noise(30_000, 6).Select(v => (float)(v * 1.3))];

        LevelFigures alone = Floats([samples, samples], 1000);
        LevelFigures both = Floats([samples, samples], 1000, options: new MeasureOptions());

        Assert.Equal(alone.Stream, both.Stream);
        Assert.True(alone.Channels.SequenceEqual(both.Channels));
    }

    [Fact]
    public void TheLevelsChargeFourUnitsForEverySampleOfAChannel()
    {
        long units = 0;
        var pcm = new PcmFormat(44100, 2, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(pcm, LevelsOnly, CancellationToken.None, u =>
        {
            units += u;
            return true;
        });
        int[] planar = new int[2 * 1000];
        measurer.Write(new PcmBlock(0, 1000, 2, 1000, planar));

        Assert.Equal(1000L * 2 * 4, units);
    }

    [Fact]
    public void TheLevelsOfFloatsChargeMoreThanThoseOfIntegers()
    {
        long units = 0;
        var measurer = new StreamMeasurer(new PcmFormat(44100, 2, 32, SampleFormat.Float32), LevelsOnly, CancellationToken.None, u =>
        {
            units += u;
            return true;
        });
        measurer.Write(new PcmBlock(0, 1000, 2, 1000, new float[2000]));

        Assert.Equal(1000L * 2 * 14, units);
    }

    [Fact]
    public void ABudgetThatPassesStopsTheLevelsToo()
    {
        var pcm = new PcmFormat(44100, 1, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(pcm, LevelsOnly, CancellationToken.None, _ => false);
        measurer.Write(new PcmBlock(0, 10, 1, 10, new int[10]));

        Assert.True(measurer.IsLimited);
        Assert.Null(measurer.Result(MeasurementStatus.Stopped, null).Levels);
    }

    [Theory]
    [InlineData(SampleFormat.Integer, false)]
    [InlineData(SampleFormat.Float32, false)]
    [InlineData(SampleFormat.Float32, true)]
    public void TheMemoryEstimateCoversWhatTheLevelsAllocateEvenForAHeaderOf65535Channels(SampleFormat format, bool withSpectrum)
    {
        var pcm = new PcmFormat(44100, 65535, 32, format);
        var options = new MeasureOptions { Include = withSpectrum ? MeasurementSets.All : MeasurementKinds.Levels };
        long estimate = StreamMeasurer.EstimateBytes(pcm, options);
        int[] integers = new int[65535 * 8];
        float[] floats = new float[65535 * 8];

        long before = GC.GetAllocatedBytesForCurrentThread();
        var measurer = new StreamMeasurer(pcm, options, CancellationToken.None, _ => true);
        measurer.Write(format == SampleFormat.Integer ? new PcmBlock(0, 8, 65535, 8, integers) : new PcmBlock(0, 8, 65535, 8, floats));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Complete, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(measurer.Failure);
        Assert.Equal(32, m.Levels!.Channels.Count);
        Assert.True(allocated < estimate, $"allocated {allocated:N0}, estimated {estimate:N0}");
    }
}
