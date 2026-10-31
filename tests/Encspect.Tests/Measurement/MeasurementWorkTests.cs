using System.Diagnostics;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using Xunit.Abstractions;

namespace Encspect.Tests.Measurement;

// Holds the measurements to the time per work unit that their budget assumes (docs/design/spectral-analysis.md, "The work budget"): each case's
// time per unit at most twice the FLAC reference's of DecoderWorkTests, for every FFT size, for one channel and for two, and for the case "an
// impulse, then silence". As in DecoderWorkTests, each round times the reference beside the case and the ratio is the median of the rounds'.
// Alone (the ComplexityTests collection) and in the nightly run only. The absolute time per sample of a channel and per unit is printed.
[Collection(nameof(ComplexityTests))]
[Trait("Category", "Nightly")]
public sealed class MeasurementWorkTests(ITestOutputHelper output)
{
    private const int Rounds = 5;

    // The measurements of steps 1 and 2, which the tests of their weights time without the loudness and the true peak of step 5.
    private const MeasurementKinds SpectrumAndLevels = MeasurementKinds.Spectrum | MeasurementKinds.Levels;

    private const double Factor = 2.0;

    // The least a timed run of Compare lasts, in seconds, and the most times it feeds the signal to get there.
    private const double MinimumRun = 0.15;

    private const int MaxPasses = 16;

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        foreach (int channels in new[] { 2, 1 })
        {
            foreach (int rate in new[] { 8000, 22050, 44100, 88200, 176400, 352800 })
            {
                data.Add(rate, channels);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ATimePerUnitWithinTwiceTheFlacReferenceForEveryFftSizeAndChannelCount(int rate, int channels)
    {
        (double ratio, double perUnit, double perSample) = Compare(rate, channels, Signal.Noise);
        output.WriteLine($"{rate} Hz, {FftPlan.SizeFor(rate)} points, {channels} channel(s): {perSample:F2} ns a sample of a channel, {perUnit:F3} ns a unit; ratio to the reference {ratio:F2}");
        Assert.True(ratio <= Factor, $"The spectrum takes {ratio:F2} times the reference's time per unit, at most {Factor} are allowed. A weight is too low.");
    }

    // The two cases alternate for five rounds and each is taken at its fastest, so that the first round's warm-up and a busy moment of the machine
    // do not decide the ratio.
    [Fact]
    public void AnImpulseThenSilenceCostsAboutTheSameAsZeros()
    {
        int[][] zeroData = Data(2, Signal.Zeros);
        int[][] impulseData = Data(2, Signal.Impulse);
        RunOnce(48000, 2, zeroData);
        double zeros = double.MaxValue;
        double impulse = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            zeros = Math.Min(zeros, RunOnce(48000, 2, zeroData).Seconds);
            impulse = Math.Min(impulse, RunOnce(48000, 2, impulseData).Seconds);
        }

        output.WriteLine($"zeros {zeros:F3} s, an impulse then silence {impulse:F3} s, ratio {impulse / zeros:F2}");
        Assert.True(impulse / zeros <= Factor);
    }

    // The levels beside the spectrum at the rates and channel counts where the spectrum alone is slowest per sample.
    [Theory]
    [InlineData(22050, 1)]
    [InlineData(22050, 2)]
    [InlineData(8000, 1)]
    [InlineData(44100, 1)]
    public void TheLevelsAddAboutTwoNanosecondsToTheSpectrumAtAnyRate(int rate, int channels)
    {
        int[][] data = Data(channels, Signal.Noise);
        for (int warm = 0; warm < 3; warm++)
        {
            RunOnce(rate, channels, data, MeasurementKinds.Spectrum);
            RunOnce(rate, channels, data, SpectrumAndLevels);
        }

        double spectrum = double.MaxValue;
        double both = double.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            spectrum = Math.Min(spectrum, RunOnce(rate, channels, data, MeasurementKinds.Spectrum).Seconds);
            both = Math.Min(both, RunOnce(rate, channels, data, SpectrumAndLevels).Seconds);
        }

        double perSample = (both - spectrum) * 1e9 / ((long)Length * channels);
        output.WriteLine($"{rate} Hz, {channels} channel(s): the spectrum alone {spectrum * 1e9 / ((long)Length * channels):F2} ns a sample, with the levels {both * 1e9 / ((long)Length * channels):F2}: the levels add {perSample:F2} ns");
        Assert.True(perSample < 8, $"The levels add {perSample:F2} ns a sample to the spectrum.");
    }

    // The levels alone, with the Int128 accumulators: one and two channels at 16, 24 and 32 bits, noise, a full-scale square wave (every piece
    // scans for runs) and zeros (every piece is silent). The weight of 4 units is the design's estimate; this holds it to the reference.
    public static TheoryData<int, int, Signal> LevelsCases()
    {
        var data = new TheoryData<int, int, Signal>();
        foreach (int bits in new[] { 16, 24, 32 })
        {
            foreach (int channels in new[] { 2, 1 })
            {
                data.Add(bits, channels, Signal.Noise);
            }
        }

        data.Add(16, 2, Signal.Square);
        data.Add(24, 2, Signal.Zeros);
        return data;
    }

    [Theory]
    [MemberData(nameof(LevelsCases))]
    public void TheLevelsTakeAtMostTwiceTheFlacReferencesTimePerUnit(int bits, int channels, Signal signal)
    {
        (double ratio, double perUnit, double perSample) = Compare(44100, channels, signal, MeasurementKinds.Levels, bits);
        output.WriteLine($"levels, {bits} bits, {channels} channel(s), {signal}: {perSample:F2} ns a sample of a channel, {perUnit:F3} ns a unit; ratio to the reference {ratio:F2}");
        Assert.True(ratio <= Factor, $"The levels take {ratio:F2} times the reference's time per unit, at most {Factor} are allowed. The weight is too low.");
    }

    [Fact]
    public void TheLevelsAddLittleToTheSpectrum()
    {
        (double both, _, double perSampleBoth) = Compare(44100, 2, Signal.Noise, SpectrumAndLevels, 16);
        (double spectrum, _, double perSampleSpectrum) = Compare(44100, 2, Signal.Noise, MeasurementKinds.Spectrum, 16);
        output.WriteLine($"spectrum and levels {perSampleBoth:F2} ns a sample of a channel, the spectrum alone {perSampleSpectrum:F2} ns; ratios to the reference {both:F2} and {spectrum:F2}");
        Assert.True(both <= Factor);
    }

    // A crafted Float64 stream of subnormal numbers (the PCM route of step 3 carries them): every arithmetic operation on one costs 20 to 30 times a
    // normal one, so the levels take no part of their sums (below 1e-150); the time per unit stays within twice the FLAC reference's, as normal
    // values' does.
    [Theory]
    [InlineData(1e-310)]
    [InlineData(1e-160)]
    [InlineData(0.5)]
    public void TheLevelsOfDoublesOfAnyMagnitudeTakeAtMostTwiceTheFlacReferencesTimePerUnit(double scale)
    {
        double[] data = [.. MeasurementSignals.Noise(Length, 5).Select(v => v * scale)];
        DecoderWorkTests.ReferenceRoundNanosecondsPerUnit();
        for (int warm = 0; warm < 3; warm++)
        {
            RunDoubles(data);
        }

        var ratios = new List<double>();
        var perSamples = new List<double>();
        for (int round = 0; round < Rounds; round++)
        {
            double reference = DecoderWorkTests.ReferenceRoundNanosecondsPerUnit();
            (double seconds, long units) = RunDoubles(data);
            ratios.Add(seconds * 1e9 / units / reference);
            perSamples.Add(seconds * 1e9 / Length);
        }

        DecoderWorkTests.ReleaseReference();
        output.WriteLine($"levels of doubles around {scale:E1}: {Median(perSamples):F2} ns a sample, ratio to the reference {Median(ratios):F2}");
        Assert.True(Median(ratios) <= Factor, $"The levels of doubles around {scale:E1} take {Median(ratios):F2} times the reference's time per unit.");
    }

    private static (double Seconds, long Units) RunDoubles(double[] data)
    {
        long units = 0;
        var format = new PcmFormat(44100, 1, 64, SampleFormat.Float64);
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, u =>
        {
            units += u;
            return true;
        });
        long start = Stopwatch.GetTimestamp();
        for (int at = 0; at < Length; at += 4096)
        {
            measurer.Write(new PcmBlock(at, 4096, 1, 4096, data.AsSpan(at, 4096)));
        }

        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Assert.Null(measurer.Failure);
        Assert.NotNull(measurer.Result(MeasurementStatus.Complete, null).Levels);
        return (seconds, units);
    }

    // The loudness (K-weighting and squares) and the true peak (the Annex 2 filter by oversampling) alone, against the weights of the budget: at the rates
    // where they run in the most common ways, for noise, the worst case of the true peak (nothing can be skipped), a full-scale square wave and zeros.
    public static TheoryData<MeasurementKinds, int, int, Signal> StepFiveCases()
    {
        var data = new TheoryData<MeasurementKinds, int, int, Signal>();
        foreach (int rate in new[] { 8000, 44100, 48000, 96000, 192000 })
        {
            foreach (Signal signal in new[] { Signal.Noise, Signal.Square, Signal.Zeros })
            {
                data.Add(MeasurementKinds.Loudness, rate, 2, signal);
                data.Add(MeasurementKinds.TruePeak, rate, 2, signal);
            }
        }

        data.Add(MeasurementKinds.TruePeak, 48000, 6, Signal.Noise);
        data.Add(MeasurementKinds.Loudness, 48000, 1, Signal.Impulse);
        return data;
    }

    [Theory]
    [MemberData(nameof(StepFiveCases))]
    public void TheLoudnessAndTheTruePeakTakeAtMostTwiceTheFlacReferencesTimePerUnit(MeasurementKinds kind, int rate, int channels, Signal signal)
    {
        (double ratio, double perUnit, double perSample) = Compare(rate, channels, signal, kind);
        output.WriteLine($"{kind}, {rate} Hz, {channels} channel(s), {signal}: {perSample:F2} ns a sample of a channel, {perUnit:F3} ns a unit; ratio to the reference {ratio:F2}");
        Assert.True(ratio <= Factor, $"{kind} takes {ratio:F2} times the reference's time per unit, at most {Factor} are allowed. The weight is too low.");
    }

    [Fact]
    public void AllTheKindsTogetherStayWithinTwiceTheReference()
    {
        (double ratio, double perUnit, double perSample) = Compare(44100, 2, Signal.Noise, MeasurementSets.All);
        output.WriteLine($"everything, 44.1 kHz stereo noise: {perSample:F2} ns a sample of a channel, {perUnit:F3} ns a unit; ratio to the reference {ratio:F2}");
        Assert.True(ratio <= Factor);
    }

    public enum Signal
    {
        Noise,
        Zeros,
        Impulse,
        Square,
    }

    private const int Length = 1 << 21;

    private static int[][] Data(int channels, Signal signal, int bits = 16)
    {
        var data = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = signal switch
            {
                Signal.Noise => MeasurementSignals.ToIntegers(MeasurementSignals.Noise(Length, (uint)(c + 1)), bits),
                Signal.Zeros => new int[Length],
                Signal.Square => [.. Enumerable.Range(0, Length).Select(i => (i / 200) % 2 == 0 ? (1 << (bits - 1)) - 1 : -(1 << (bits - 1)))],
                _ => Impulse(Length),
            };
        }

        return data;
    }

    // The median of the rounds' ratios of the case's time per unit to the reference's of the same round, and the median time per unit and per sample.
    private static (double Ratio, double PerUnit, double PerSample) Compare(int rate, int channels, Signal signal, MeasurementKinds? kinds = null, int bits = 16)
    {
        int[][] data = Data(channels, signal, bits);
        DecoderWorkTests.ReferenceRoundNanosecondsPerUnit();

        // Three runs before the timed rounds: the library's vector routines (the scan for samples that are not 0) start as precompiled code and are
        // compiled again once they are hot, which the first two tests of a process would otherwise time.
        double warmSeconds = 0;
        for (int warm = 0; warm < 3; warm++)
        {
            warmSeconds = RunOnce(rate, channels, data, kinds, bits).Seconds;
        }

        // A run of a few milliseconds (the levels) is decided by one scheduler hiccup or a collection, so each timed run covers the signal as
        // many times as it takes to last MinimumRun, by the last warm-up run's time.
        int passes = (int)Math.Clamp(Math.Ceiling(MinimumRun / warmSeconds), 1, MaxPasses);

        var ratios = new List<double>();
        var perUnits = new List<double>();
        var perSamples = new List<double>();
        for (int round = 0; round < Rounds; round++)
        {
            double reference = DecoderWorkTests.ReferenceRoundNanosecondsPerUnit();
            (double seconds, long units, long samples) = RunOnce(rate, channels, data, kinds, bits, passes);
            double perUnit = seconds * 1e9 / units;
            ratios.Add(perUnit / reference);
            perUnits.Add(perUnit);
            perSamples.Add(seconds * 1e9 / samples);
        }

        DecoderWorkTests.ReleaseReference();
        return (Median(ratios), Median(perUnits), Median(perSamples));
    }

    private static double Median(List<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }

    private static (double Seconds, long Units, long Samples) RunOnce(int rate, int channels, int[][] data, MeasurementKinds? kinds = null, int bits = 16, int passes = 1)
    {
        long units = 0;
        var format = new PcmFormat(rate, channels, bits, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = kinds }, CancellationToken.None, u =>
        {
            units += u;
            return true;
        });
        int[] planar = new int[channels * 4096];
        long start = Stopwatch.GetTimestamp();
        for (int pass = 0; pass < passes; pass++)
        {
            for (int at = 0; at < Length; at += 4096)
            {
                for (int c = 0; c < channels; c++)
                {
                    data[c].AsSpan(at, 4096).CopyTo(planar.AsSpan(c * 4096, 4096));
                }

                measurer.Write(new PcmBlock((long)pass * Length + at, 4096, channels, 4096, planar));
            }
        }

        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

        // The figures at the end (the grid's quantization) cost about 20 ms whatever the stream's length and are not timed.
        StreamMeasurements result = measurer.Result(MeasurementStatus.Complete, null);
        Assert.Null(measurer.Failure);
        Assert.True(kinds is null || (kinds & MeasurementKinds.Spectrum) == 0 || result.Spectrum!.Frames > 0);
        Assert.True(kinds is null || (kinds & MeasurementKinds.Levels) == 0 || result.Levels is not null);
        return (seconds, units, (long)Length * channels * passes);
    }

    private static int[] Impulse(int length)
    {
        int[] values = new int[length];
        values[1000] = 30000;
        return values;
    }
}
