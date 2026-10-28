using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using static Encspect.Tests.Measurement.LoudnessSignals;

namespace Encspect.Tests.Measurement;

// The loudness meter's own properties: the filter, the weights, the gates against a brute-force reference, gaps, absences, the bounded series, and that
// the figures do not depend on how the decoder cuts blocks.
public class LoudnessMeterTests
{
    [Fact]
    public void TheDerivedCoefficientsAt48kHzMatchTheRecommendationsTables()
    {
        KWeighting derived = KWeighting.Derive(48000);
        KWeighting table = KWeighting.At48000;
        foreach ((double a, double b) in new[] { (derived.B0, table.B0), (derived.B1, table.B1), (derived.B2, table.B2), (derived.A1, table.A1), (derived.A2, table.A2), (derived.HighpassA1, table.HighpassA1), (derived.HighpassA2, table.HighpassA2) })
        {
            Assert.True(Math.Abs(a - b) < 5e-13, $"{a} against {b}");
        }
    }

    // The magnitude of the two sections in dB at a frequency.
    private static double ResponseDb(KWeighting k, double hertz, int rate)
    {
        double w = 2 * Math.PI * hertz / rate;
        var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
        var z2 = z1 * z1;
        System.Numerics.Complex shelf = (k.B0 + (k.B1 * z1) + (k.B2 * z2)) / (1 + (k.A1 * z1) + (k.A2 * z2));
        System.Numerics.Complex high = (1 - (2 * z1) + z2) / (1 + (k.HighpassA1 * z1) + (k.HighpassA2 * z2));
        return 20 * Math.Log10((shelf * high).Magnitude);
    }

    // The design's drift of the derived response from the 48 kHz one over 20 Hz to 0.45 of the rate (to 20 kHz above 44.1 kHz, where the 48 kHz response has no meaning): 0.50 dB at 8 kHz, 0.29 at 11.025, 0.15 at 16,
    // 0.08 at 22.05 and under 0.05 from 32 kHz.
    [Theory]
    [InlineData(8000, 0.55)]
    [InlineData(11025, 0.32)]
    [InlineData(16000, 0.17)]
    [InlineData(22050, 0.10)]
    [InlineData(32000, 0.06)]
    [InlineData(44100, 0.06)]
    [InlineData(96000, 0.06)]
    [InlineData(192000, 0.06)]
    public void TheDerivedFilterKeepsTheResponseOfThe48kHzOne(int rate, double bound)
    {
        double worst = 0;
        for (double hertz = 20; hertz <= Math.Min(0.45 * rate, 20000); hertz *= 1.05)
        {
            worst = Math.Max(worst, Math.Abs(ResponseDb(KWeighting.For(rate), hertz, rate) - ResponseDb(KWeighting.At48000, hertz, 48000)));
        }

        Assert.True(worst <= bound, $"{rate} Hz: {worst:F3} dB");
    }

    [Fact]
    public void TheDerivedFilterIsStableAtEveryRateFrom8kHz()
    {
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 32000, 44100, 88200, 96000, 176400, 192000, 384000, 2822400 })
        {
            KWeighting k = KWeighting.For(rate);
            foreach ((double a1, double a2) in new[] { (k.A1, k.A2), (k.HighpassA1, k.HighpassA2) })
            {
                Assert.True(Math.Abs(a2) < 1 && Math.Abs(a1) < 1 + a2, $"{rate} Hz");
            }
        }
    }

    [Fact]
    public void TheWeightsFollowTable4()
    {
        int[]? Maybe(int measured, params Speaker[] layout) => LoudnessWeights.For(layout.Length == 0 ? null : layout, measured, out _);
        int[] W(int measured, params Speaker[] layout) => Maybe(measured, layout)!;
        Assert.Equal([100], W(1));
        Assert.Equal([100, 100], W(2));
        Assert.Null(Maybe(3));
        Assert.Equal([100, 100, 100, 0, 141, 141], W(6, Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight));
        Assert.Equal([100, 100, 100, 0, 100, 100, 141, 141], W(8, Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.LowFrequency, Speaker.BackLeft, Speaker.BackRight, Speaker.SideLeft, Speaker.SideRight));
        Assert.Equal([100, 100, 141, 141], W(4, Speaker.FrontLeft, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight));
        Assert.Equal([100, 100, 100, 100, 100, 100], W(6, Speaker.FrontLeft, Speaker.FrontRight, Speaker.TopFrontLeft, Speaker.TopFrontRight, Speaker.TopBackLeft, Speaker.TopBackRight));
        Assert.Equal([100, 0], W(2, Speaker.FrontLeft, Speaker.LowFrequency));
        Assert.Null(Maybe(3, Speaker.FrontLeft, Speaker.Discrete, Speaker.FrontRight));
        Assert.Equal([100, 100], W(2, Speaker.Discrete, Speaker.Discrete));
        Assert.Null(LoudnessWeights.For([Speaker.LowFrequency], 1, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void ThreeChannelsWithoutPositionsAndRatesBelow8kHzAreNotMeasured()
    {
        double[] tone = Tone(48000, 1, -20);
        Assert.Equal("the channel positions are not stated", Measure([tone, tone, tone], 48000).Loudness!.NotMeasured);
        Assert.Null(Measure([tone, tone], 48000).Loudness!.NotMeasured);
        double[] low = Tone(7999, 1, -20);
        Assert.Equal("loudness needs a sample rate of 8,000 Hz or more", Measure([low], 7999).Loudness!.NotMeasured);
        Assert.Equal(4, Measure([low], 7999).TruePeak!.Oversampling);
        Assert.Null(Measure([Tone(8000, 1, -20)], 8000).Loudness!.NotMeasured);
        Assert.Equal("the sample rate is not stated", Measure([Tone(8000, 1, -20)], 0).Loudness!.NotMeasured);
    }

    [Fact]
    public void ShortStreamsAndSilenceSayWhyTheyHaveNoFigure()
    {
        LoudnessFigures tooShort = Measure([Tone(48000, 0.3, -20)], 48000).Loudness!;
        Assert.Equal(LoudnessAbsence.TooShort, tooShort.IntegratedAbsence);
        Assert.Equal(LoudnessAbsence.TooShort, tooShort.MomentaryAbsence);
        LoudnessFigures twoSeconds = Measure([Tone(48000, 2.5, -20)], 48000).Loudness!;
        Assert.Equal(LoudnessAbsence.None, twoSeconds.IntegratedAbsence);
        Assert.Equal(LoudnessAbsence.None, twoSeconds.MomentaryAbsence);
        Assert.Equal(LoudnessAbsence.TooShort, twoSeconds.ShortTermAbsence);
        Assert.Equal(LoudnessAbsence.TooShort, twoSeconds.RangeAbsence);
        Assert.Null(twoSeconds.ShortTerm);
        LoudnessFigures silent = Measure([Silence(48000, 5)], 48000).Loudness!;
        Assert.Equal(LoudnessAbsence.BelowGate, silent.IntegratedAbsence);
        Assert.Equal(LoudnessAbsence.BelowGate, silent.RangeAbsence);
        Assert.Equal(LoudnessAbsence.None, silent.MomentaryAbsence);
        Assert.Equal(MeasurementLimits.NoLevel, silent.MaximumMomentaryTenths);
        Assert.Equal(-700, silent.ShortTerm!.Values.Span[0] * 10);
    }

    // The pattern of EBU's tests with a varying gain, as brute force: filtered with the Recommendation's own coefficients in plain loops, every block's
    // energy listed, the gates applied to the lists and the percentiles read from the sorted values.
    private static (double Integrated, double Range, double MaximumMomentary, double MaximumShortTerm) Reference(double[][] channels, int[] weights)
    {
        KWeighting k = KWeighting.At48000;
        int length = channels[0].Length;
        double[] energy = new double[length];
        for (int c = 0; c < channels.Length; c++)
        {
            double s1 = 0, s2 = 0, t1 = 0, t2 = 0;
            for (int i = 0; i < length; i++)
            {
                double y = (k.B0 * channels[c][i]) + s1;
                s1 = (k.B1 * channels[c][i]) - (k.A1 * y) + s2;
                s2 = (k.B2 * channels[c][i]) - (k.A2 * y);
                double z = y + t1;
                t1 = (-2 * y) - (k.HighpassA1 * z) + t2;
                t2 = y - (k.HighpassA2 * z);
                energy[i] += weights[c] / 100.0 * z * z;
            }
        }

        double[] prefix = new double[length + 1];
        for (int i = 0; i < length; i++)
        {
            prefix[i + 1] = prefix[i] + energy[i];
        }

        double Mean(int from, int count) => (prefix[from + count] - prefix[from]) / count;
        var blocks = new List<double>();
        var shorts = new List<double>();
        double maximumMomentary = 0;
        for (int from = 0; from + 19200 <= length; from += 960)
        {
            maximumMomentary = Math.Max(maximumMomentary, Mean(from, 19200));
        }

        for (int from = 0; from + 19200 <= length; from += 4800)
        {
            blocks.Add(Mean(from, 19200));
        }

        for (int from = 0; from + 144000 <= length; from += 4800)
        {
            shorts.Add(Mean(from, 144000));
        }

        double L(double z) => -0.691 + (10 * Math.Log10(z));
        double gate = Math.Pow(10, (-70 + 0.691) / 10);
        double[] above = [.. blocks.Where(b => b > gate)];
        double relative = above.Sum() / above.Length / 10;
        double[] kept = [.. above.Where(b => b > relative)];
        double integrated = L(kept.Sum() / kept.Length);
        double[] shortAbove = [.. shorts.Where(v => v >= gate)];
        double shortGate = shortAbove.Sum() / shortAbove.Length / 100;
        double[] sorted = [.. shortAbove.Where(v => v >= shortGate).Order()];
        double range = L(sorted[(int)Math.Floor(((sorted.Length - 1) * 0.95) + 0.5)]) - L(sorted[(int)Math.Floor(((sorted.Length - 1) * 0.10) + 0.5)]);
        return (integrated, range, L(maximumMomentary), L(shorts.Max()));
    }

    // Noise of a slowly changing level in two channels, with a quiet stretch and a silence, so that both gates and the percentiles matter.
    private static double[][] VaryingNoise(int seconds, uint seed)
    {
        int length = 48000 * seconds;
        double[][] channels = [new double[length], new double[length]];
        for (int c = 0; c < 2; c++)
        {
            double[] noise = MeasurementSignals.Noise(length, seed + (uint)c);
            for (int i = 0; i < length; i++)
            {
                double t = i / 48000.0;
                double gain = Amplitude(-30 + (12 * Math.Sin(t / 7.0)) + (t % 41 < 8 ? -35 : 0)) * (t > seconds - 6 ? 0 : 1);
                channels[c][i] = noise[i] * gain;
            }
        }

        return channels;
    }

    [Theory]
    [InlineData(60u)]
    [InlineData(61u)]
    [InlineData(62u)]
    public void TheFiguresAgreeWithABruteForceReferenceOnProgramLikeNoise(uint seed)
    {
        double[][] channels = VaryingNoise(100, seed);
        LoudnessFigures l = Measure(channels, 48000, blockSize: 4800, kinds: MeasurementKinds.Loudness).Loudness!;
        (double integrated, double range, double momentary, double shortTerm) = Reference(channels, [100, 100]);
        Assert.True(Math.Abs((l.IntegratedTenths / 10.0) - integrated) <= 0.06, $"I {l.IntegratedTenths / 10.0} against {integrated:F3}");
        Assert.True(Math.Abs((l.RangeTenths / 10.0) - range) <= 0.12, $"LRA {l.RangeTenths / 10.0} against {range:F3}");
        Assert.True(Math.Abs((l.MaximumMomentaryTenths / 10.0) - momentary) <= 0.06, $"M {l.MaximumMomentaryTenths / 10.0} against {momentary:F3}");
        Assert.True(Math.Abs((l.MaximumShortTermTenths / 10.0) - shortTerm) <= 0.06, $"S {l.MaximumShortTermTenths / 10.0} against {shortTerm:F3}");
    }

    // The gate's own bin is taken whole or left out: at most 4.34 p LU (the share p of the passing blocks in that bin) from the exact figure.
    [Fact]
    public void BlocksStraddlingTheRelativeGateChangeTheIntegratedLoudnessByLessThanTheBound()
    {
        // 60 s at a level with 20 s of noise 10 LU lower, whose blocks sit around the gate, then the reference tells the exact figure.
        int length = 48000 * 80;
        double[] noise = MeasurementSignals.Noise(length, 5);
        double[] wiggle = MeasurementSignals.Noise(length / 4800 + 1, 6);
        var signal = new double[length];
        for (int i = 0; i < length; i++)
        {
            double level = i < 48000 * 60 ? -23 : -33.2 + (0.05 * wiggle[i / 4800]);
            signal[i] = noise[i] * Amplitude(level);
        }

        LoudnessFigures l = Measure([signal], 48000, kinds: MeasurementKinds.Loudness).Loudness!;
        (double integrated, _, _, _) = Reference([signal], [100]);
        double p = (double)(l.Blocks - 600) / Math.Max(1, l.BlocksAboveRelativeGate);
        Assert.True(Math.Abs((l.IntegratedTenths / 10.0) - integrated) <= (4.34 * Math.Min(1, p)) + 0.06, $"{l.IntegratedTenths / 10.0} against {integrated:F3}, p {p:F3}");
    }

    [Fact]
    public void TheFiguresDoNotDependOnTheBlockSizes()
    {
        double[][] channels = VaryingNoise(40, 77);
        LoudnessFigures reference = Measure(channels, 48000, blockSize: 4800).Loudness!;
        foreach (int blockSize in new[] { 1, 13, 959, 960, 4096, 4801, 100_000, 3_000_000 })
        {
            MeasurementTests.AssertSameLoudness(reference, Measure(channels, 48000, blockSize: blockSize).Loudness!);
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(11025)]
    [InlineData(96000)]
    public void TheGranulesOfOtherRatesDoNotDependOnTheBlockSizesEither(int rate)
    {
        double[] a = Join(Tone(rate, 4, -23, 440), Tone(rate, 4, -35, 1500), Tone(rate, 2, -50, 90));
        double[][] channels = [a, a.Select(v => v * 0.7).ToArray()];
        LoudnessFigures reference = Measure(channels, rate, blockSize: 4096).Loudness!;
        foreach (int blockSize in new[] { 1, 977, 20_000 })
        {
            MeasurementTests.AssertSameLoudness(reference, Measure(channels, rate, blockSize: blockSize).Loudness!);
        }
    }

    [Fact]
    public void NoBlockSpansAGapAndTheFirstBlockAfterItNeedsFourHundredMilliseconds()
    {
        double[] tone = Tone(48000, 3, -20);
        double[][] channels = [tone];

        // Blocks of 4,800 samples: removing block 10 leaves runs of 48,000 and 91,200 samples (7 and 16 blocks of 400 ms against 27).
        LoudnessFigures gapped = Measure(channels, 48000, blockSize: 4800, kinds: MeasurementKinds.Loudness, removed: (4800 * 10, 4800)).Loudness!;
        LoudnessFigures whole = Measure(channels, 48000, blockSize: 4800, kinds: MeasurementKinds.Loudness).Loudness!;
        Assert.Equal(whole.Blocks - 4, gapped.Blocks);
        Assert.Equal(whole.IntegratedTenths, gapped.IntegratedTenths);
    }

    [Fact]
    public void ALongStreamKeepsASeriesOfAtMost1024PointsInPowerOfTwoSteps()
    {
        // 3 hours of a quiet tone at 8 kHz in one block of the measurer's pieces.
        var format = new PcmFormat(8000, 1, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = MeasurementKinds.Loudness }, CancellationToken.None, _ => true);
        int[] block = [.. MeasurementSignals.ToIntegers(MeasurementSignals.Noise(8000 * 60, 3).Select(v => v * 0.01).ToArray(), 16)];
        for (int minute = 0; minute < 180; minute++)
        {
            measurer.Write(new PcmBlock(minute * 480_000L, block.Length, 1, block.Length, block));
        }

        LoudnessFigures l = measurer.Result(MeasurementStatus.Complete, null).Loudness!;
        Assert.InRange(l.ShortTerm!.Values.Length, 512, 1024);
        Assert.Equal(0, l.StepsPerPoint & (l.StepsPerPoint - 1));
        Assert.True(l.StepsPerPoint >= 128);
        Assert.Equal(LoudnessAbsence.None, l.RangeAbsence);
    }
}
