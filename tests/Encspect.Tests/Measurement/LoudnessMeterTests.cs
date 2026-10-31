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

    // The gating blocks of a signal (400 ms every 100 ms, K-weighted, mono) as the Recommendation defines them, in energy.
    private static double[] BlockEnergies(double[] x)
    {
        KWeighting k = KWeighting.At48000;
        double s1 = 0, s2 = 0, t1 = 0, t2 = 0;
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++)
        {
            double y = (k.B0 * x[i]) + s1;
            s1 = (k.B1 * x[i]) - (k.A1 * y) + s2;
            s2 = (k.B2 * x[i]) - (k.A2 * y);
            double z = y + t1;
            t1 = (-2 * y) - (k.HighpassA1 * z) + t2;
            t2 = y - (k.HighpassA2 * z);
            prefix[i + 1] = prefix[i] + (z * z);
        }

        var blocks = new List<double>();
        for (int from = 0; from + 19200 <= x.Length; from += 4800)
        {
            blocks.Add((prefix[from + 19200] - prefix[from]) / 19200);
        }

        return [.. blocks];
    }

    private static double Loudness(double energy) => -0.691 + (10 * Math.Log10(energy));

    // Many blocks sit in the one 0.01 LU bin that holds the relative gate, some just above it and some just below: BS.1770-5 equation 7 keeps
    // the first and drops the second, one by one. A histogram that takes the bin whole reads about 2 LU low here. A loud tone, then two quiet
    // tones 0.0009 dB apart, set by bisection so that the gate falls between them.
    [Fact]
    public void ManyBlocksInTheBinOfTheRelativeGateAreGatedOneByOne()
    {
        double[] Segment(double seconds, double amplitude, int lead) => [.. new double[lead * 48000], .. Tone(48000, seconds, 20 * Math.Log10(amplitude)), .. new double[48000]];
        double[] a = Segment(10, 1, 0);
        double[] b1 = Segment(30, 1, 11);
        double[] b2 = Segment(30, 1, 42);
        int length = 73 * 48000;
        double[] Pad(double[] x) => [.. x, .. new double[length - x.Length]];
        double[] za = BlockEnergies(Pad(a));
        double[] zb1 = BlockEnergies(Pad(b1));
        double[] zb2 = BlockEnergies(Pad(b2));
        const double Delta = 1e-4;
        double absolute = Math.Pow(10, (-70 + 0.691) / 10);

        (double Gate, double Level, double Exact, double Whole) At(double amplitude)
        {
            double u = amplitude * amplitude;
            double[] blocks = new double[za.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = za[i] + (u * (1 + Delta) * (1 + Delta) * zb1[i]) + (u * (1 - Delta) * (1 - Delta) * zb2[i]);
            }

            double[] above = [.. blocks.Where(e => e > absolute)];
            double gate = above.Sum() / above.Length / 10;
            double[] kept = [.. above.Where(e => e > gate)];
            return (gate, u * zb1.Max(), Loudness(kept.Sum() / kept.Length), Loudness(above.Sum() / above.Length));
        }

        double low = 0.0001, high = 0.2;
        for (int i = 0; i < 80; i++)
        {
            double mid = (low + high) / 2;
            (double gate, double level, _, _) = At(mid);
            if (gate > level)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        double amplitude = (low + high) / 2;
        (double g, _, double exact, double all) = At(amplitude);
        Assert.True(Math.Abs(exact - all) > 1.5, $"the case does not straddle: exact {exact:F3}, all in {all:F3}");

        double[] pa = Pad(a), p1 = Pad(b1), p2 = Pad(b2);
        double[] signal = new double[length];
        for (int i = 0; i < length; i++)
        {
            signal[i] = pa[i] + (amplitude * (1 + Delta) * p1[i]) + (amplitude * (1 - Delta) * p2[i]);
        }

        LoudnessFigures l = Measure([signal], 48000, kinds: MeasurementKinds.Loudness).Loudness!;
        Assert.True(l.IsGatedExactly);
        Assert.True(Math.Abs((l.IntegratedTenths / 10.0) - exact) <= 0.06, $"{l.IntegratedTenths / 10.0} against the exact {exact:F3} (gate {g:E3})");
    }

    // A relative gate above +30 LUFS: the first version's histogram ended there and read +42.2 for a stream at +45.0.
    [Fact]
    public void AGateAbove30LufsIsAppliedLikeAnyOther()
    {
        double[] signal = [.. Tone(48000, 10, 48, 997), .. Tone(48000, 10, 35, 997)];
        LoudnessFigures l = Measure([signal], 48000, kinds: MeasurementKinds.Loudness).Loudness!;
        (double integrated, double range, _, _) = Reference([signal], [100]);
        Assert.True(integrated > 40);
        Assert.True(Math.Abs((l.IntegratedTenths / 10.0) - integrated) <= 0.06, $"{l.IntegratedTenths / 10.0} against {integrated:F3}");
        Assert.True(Math.Abs((l.RangeTenths / 10.0) - range) <= 0.12, $"{l.RangeTenths / 10.0} against {range:F3}");
    }

    // The most values kept: past it the histograms decide, and the figure says so. The loudness is still close, for blocks of one level.
    [Fact]
    public void AStreamBeyondTheKeptValuesFallsBackToTheHistogramAndSaysSo()
    {
        double[] tone = Tone(48000, 30, -23, 997);
        var meter = new LoudnessMeter(48000, [100], maxExact: 50);
        meter.Start(0);
        for (int from = 0; from < tone.Length; from += LoudnessMeter.MaxPiece)
        {
            int count = Math.Min(LoudnessMeter.MaxPiece, tone.Length - from);
            meter.AddChannel(0, tone.AsSpan(from, count));
            meter.EndPiece(count);
        }

        LoudnessFigures l = meter.Build([100]);
        Assert.False(l.IsGatedExactly);
        Assert.True(Math.Abs((l.IntegratedTenths / 10.0) - (-26.0)) <= 0.1, $"{l.IntegratedTenths / 10.0}");

        StreamMeasurements m = Measure([tone], 48000, kinds: MeasurementKinds.Loudness) with { Loudness = l };
        Assert.Contains("gated on a histogram of 0.01 LU steps", LineText(m, "Integrated Loudness"));
        Assert.Contains("gated on a histogram of 0.01 LU steps", LineText(m, "Loudness Range"));
        Assert.True(Measure([tone], 48000, kinds: MeasurementKinds.Loudness).Loudness!.IsGatedExactly);
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

    private static string LineText(StreamMeasurements m, string name) => MeasurementText.Lines(m, isLossy: false).Single(l => l.Name == name).Text;

    [Fact]
    public void ABlockExactlyAtTheAbsoluteGateDoesNotPassButTheNextEnergyUpDoes()
    {
        double gate = LoudnessMeter.AbsoluteGate;
        Assert.False(LoudnessMeter.IsAboveAbsoluteGate(gate));
        Assert.False(LoudnessMeter.IsAboveAbsoluteGate(Math.BitDecrement(gate)));
        Assert.True(LoudnessMeter.IsAboveAbsoluteGate(Math.BitIncrement(gate)));
        Assert.Equal(-700, LoudnessMeter.Tenths(gate));
    }

    [Fact]
    public void AStreamBelowTheGateNamesTheValuesThatAreBelowItAndAShortOneTheStretch()
    {
        StreamMeasurements quiet = Measure(Stereo(Tone(48000, 5, -75)), 48000, kinds: MeasurementKinds.Loudness);
        Assert.Equal("none (every block is below the absolute gate of -70 LUFS)", LineText(quiet, "Integrated Loudness"));
        Assert.Equal("none (every short-term value is below the absolute gate of -70 LUFS)", LineText(quiet, "Loudness Range"));

        StreamMeasurements shortStream = Measure(Stereo(Tone(48000, 0.2, -20)), 48000, kinds: MeasurementKinds.Loudness);
        Assert.Equal("none (the stream is shorter than 400 ms)", LineText(shortStream, "Integrated Loudness"));
        Assert.Equal("none (the stream is shorter than 3 s)", LineText(shortStream, "Loudness Range"));
    }

    [Fact]
    public void AStreamThatWasStoppedEarlyDoesNotSayTheStreamIsShort()
    {
        StreamMeasurements stopped = Measure(Stereo(Tone(48000, 0.2, -20)), 48000, kinds: MeasurementKinds.Loudness) with { Status = MeasurementStatus.Stopped };
        Assert.Equal("none (the part of the stream that was measured is shorter than 400 ms)", LineText(stopped, "Integrated Loudness"));
        Assert.Equal("none (the part of the stream that was measured is shorter than 3 s)", LineText(stopped, "Maximum Short-Term Loudness"));

        StreamMeasurements gaps = Measure(Stereo(Tone(48000, 2, -20)), 48000, kinds: MeasurementKinds.Loudness, removed: [(48000, 4800)]) with { Status = MeasurementStatus.Stopped };
        Assert.Equal("none (no stretch between gaps lasts 3 s)", LineText(gaps, "Loudness Range"));
    }

    [Fact]
    public void ASamplePeakAboveFullScaleIsSignedLikeATruePeak()
    {
        StreamMeasurements m = Measure([Tone(48000, 1, 3.5)], 48000, kinds: MeasurementKinds.Levels | MeasurementKinds.TruePeak);
        Assert.StartsWith("+3.5 dBFS", LineText(m, "Sample Peak"));
        Assert.StartsWith("+3.5 dBTP", LineText(m, "Maximum True Peak Level"));
        Assert.Contains("the analyzer's step", MeasurementText.MaximumMomentaryLoudnessDescription);
    }

    [Fact]
    public void AnInputAbove30LufsReadsItsOwnLevelInTheRangeToo()
    {
        LoudnessFigures l = Measure([Tone(48000, 5, 40)], 48000, kinds: MeasurementKinds.Loudness).Loudness!;
        Assert.True(l.IntegratedTenths > 300);
        Assert.Equal(l.IntegratedTenths, l.RangeLowTenths);
        Assert.Equal(l.IntegratedTenths, l.RangeHighTenths);
        Assert.Equal(0, l.RangeTenths);
    }

    [Fact]
    public void TheMomentaryLoudnessDescriptionsSayEvery20Milliseconds()
    {
        Assert.Contains("every 20 ms", MeasurementText.MaximumMomentaryLoudnessDescription);
        Assert.DoesNotContain("100 ms", MeasurementText.MaximumMomentaryLoudnessDescription);
        Assert.Contains("every 100 ms", MeasurementText.MaximumShortTermLoudnessDescription);
    }
}
