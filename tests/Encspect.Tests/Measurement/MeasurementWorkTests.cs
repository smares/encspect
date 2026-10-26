using System.Diagnostics;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using Xunit.Abstractions;

namespace Encspect.Tests.Measurement;

// Holds the measurements to the time per work unit that their budget assumes (docs/design/spectral-analysis.md, "The work budget"):
// each case's time per unit at most twice the FLAC reference's of DecoderWorkTests, for every FFT size and for the case "an impulse,
// then silence" against zeros. Alone (the ComplexityTests collection) and in the nightly run only. The absolute time per sample of a
// channel and per unit is printed.
[Collection(nameof(ComplexityTests))]
[Trait("Category", "Nightly")]
public sealed class MeasurementWorkTests(ITestOutputHelper output)
{
    private const int Rounds = 5;

    private const double Factor = 2.0;

    private static double? _reference;

    public static TheoryData<int> Rates() => new() { 8000, 22050, 44100, 88200, 176400, 352800 };

    private static double Reference() => _reference ??= DecoderWorkTests.ReferenceNanosecondsPerUnit();

    [Theory]
    [MemberData(nameof(Rates))]
    public void ATimePerUnitWithinTwiceTheFlacReferenceForEveryFftSize(int rate)
    {
        double reference = Reference();
        (double seconds, long units, long samples) = Time(rate, 2, Signal.Noise);
        double perUnit = seconds * 1e9 / units;
        output.WriteLine($"{rate} Hz, {FftPlan.SizeFor(rate)} points: {seconds * 1e9 / samples:F2} ns a sample of a channel, {perUnit:F3} ns a unit; the reference {reference:F3}; ratio {perUnit / reference:F2}");
        Assert.True(perUnit / reference <= Factor, $"The spectrum takes {perUnit:F3} ns a unit against the reference's {reference:F3}: {perUnit / reference:F2} times, at most {Factor} are allowed. A weight is too low.");
    }

    [Fact]
    public void AnImpulseThenSilenceCostsAboutTheSameAsZeros()
    {
        (double zeros, _, _) = Time(48000, 2, Signal.Zeros);
        (double impulse, _, _) = Time(48000, 2, Signal.Impulse);
        output.WriteLine($"zeros {zeros:F3} s, an impulse then silence {impulse:F3} s, ratio {impulse / zeros:F2}");
        Assert.True(impulse / zeros <= Factor);
    }

    private enum Signal
    {
        Noise,
        Zeros,
        Impulse,
    }

    // The median of the rounds' times to measure about 2^28 units, and the units charged and samples of a channel measured.
    private static (double Seconds, long Units, long Samples) Time(int rate, int channels, Signal signal)
    {
        int size = FftPlan.SizeFor(rate);
        int length = 1 << 21;
        int[][] data = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            data[c] = signal switch
            {
                Signal.Noise => MeasurementSignals.ToIntegers(MeasurementSignals.Noise(length, (uint)(c + 1)), 16),
                Signal.Zeros => new int[length],
                _ => Impulse(length),
            };
        }

        var times = new List<double>();
        long units = 0;
        for (int round = 0; round < Rounds + 1; round++)
        {
            units = 0;
            var format = new PcmFormat(rate, channels, 16, SampleFormat.Integer);
            var measurer = new StreamMeasurer(format, new MeasureOptions(), CancellationToken.None, u =>
            {
                units += u;
                return true;
            });
            int[] planar = new int[channels * 4096];
            long start = Stopwatch.GetTimestamp();
            for (int at = 0; at < length; at += 4096)
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
            Assert.True(result.Spectrum!.Frames > 0 && size == result.Spectrum.FftSize);
            if (round > 0)
            {
                times.Add(seconds);
            }
        }

        times.Sort();
        return (times[times.Count / 2], units, (long)length * channels);
    }

    private static int[] Impulse(int length)
    {
        int[] values = new int[length];
        values[1000] = 30000;
        return values;
    }
}
