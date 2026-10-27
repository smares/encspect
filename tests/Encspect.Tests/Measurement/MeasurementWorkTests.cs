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

    private const double Factor = 2.0;

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

    [Fact]
    public void AnImpulseThenSilenceCostsAboutTheSameAsZeros()
    {
        (double zeros, _, _) = RunOnce(48000, 2, Data(2, Signal.Zeros));
        (double impulse, _, _) = RunOnce(48000, 2, Data(2, Signal.Impulse));
        output.WriteLine($"zeros {zeros:F3} s, an impulse then silence {impulse:F3} s, ratio {impulse / zeros:F2}");
        Assert.True(impulse / zeros <= Factor);
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
        (double both, _, double perSampleBoth) = Compare(44100, 2, Signal.Noise, MeasurementSets.All, 16);
        (double spectrum, _, double perSampleSpectrum) = Compare(44100, 2, Signal.Noise, MeasurementKinds.Spectrum, 16);
        output.WriteLine($"spectrum and levels {perSampleBoth:F2} ns a sample of a channel, the spectrum alone {perSampleSpectrum:F2} ns; ratios to the reference {both:F2} and {spectrum:F2}");
        Assert.True(both <= Factor);
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
        RunOnce(rate, channels, data, kinds, bits);
        var ratios = new List<double>();
        var perUnits = new List<double>();
        var perSamples = new List<double>();
        for (int round = 0; round < Rounds; round++)
        {
            double reference = DecoderWorkTests.ReferenceRoundNanosecondsPerUnit();
            (double seconds, long units, long samples) = RunOnce(rate, channels, data, kinds, bits);
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

    private static (double Seconds, long Units, long Samples) RunOnce(int rate, int channels, int[][] data, MeasurementKinds? kinds = null, int bits = 16)
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
        for (int at = 0; at < Length; at += 4096)
        {
            for (int c = 0; c < channels; c++)
            {
                data[c].AsSpan(at, 4096).CopyTo(planar.AsSpan(c * 4096, 4096));
            }

            measurer.Write(new PcmBlock(at, 4096, channels, 4096, planar));
        }

        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

        // The figures at the end (the grid's quantization) cost about 20 ms whatever the stream's length and are not timed.
        StreamMeasurements result = measurer.Result(MeasurementStatus.Complete, null);
        Assert.Null(measurer.Failure);
        Assert.True(kinds is null || (kinds & MeasurementKinds.Spectrum) == 0 || result.Spectrum!.Frames > 0);
        Assert.True(kinds is null || (kinds & MeasurementKinds.Levels) == 0 || result.Levels is not null);
        return (seconds, units, (long)Length * channels);
    }

    private static int[] Impulse(int length)
    {
        int[] values = new int[length];
        values[1000] = 30000;
        return values;
    }
}
