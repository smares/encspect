using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using static Encspect.Tests.Measurement.LoudnessSignals;

namespace Encspect.Tests.Measurement;

// The true-peak signals of EBU Tech 3341 (Table 1, signals 15 to 23, +0.2/-0.4 dBTP) synthesized from the document's description, and the
// properties of the meter that hold for any signal.
public class LoudnessTruePeakTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static TruePeakFigures TruePeak(double[] signal, int rate) => Measure(Stereo(signal), rate, kinds: MeasurementKinds.TruePeak).TruePeak!;

    // A sine at a fraction of the rate with a 10 ms fade in and out.
    private static double[] Tapered(int rate, double seconds, double amplitude, double fraction, double phaseDegrees)
    {
        double[] tone = Tone(rate, seconds, 20 * Math.Log10(amplitude), rate * fraction, phaseDegrees);
        int ramp = rate / 100;
        for (int i = 0; i < ramp; i++)
        {
            double gain = 0.5 - (0.5 * Math.Cos(Math.PI * i / ramp));
            tone[i] *= gain;
            tone[^(i + 1)] *= gain;
        }

        return tone;
    }

    [Theory]
    [InlineData(15, 0.25, 0.5, 0.0, -6.0)]
    [InlineData(16, 0.25, 0.5, 45.0, -6.0)]
    [InlineData(17, 1.0 / 6, 0.5, 60.0, -6.0)]
    [InlineData(18, 1.0 / 8, 0.5, 67.5, -6.0)]
    [InlineData(19, 0.25, 1.41, 45.0, 3.0)]
    public void Signals15To19SinesAtFractionsOfTheRateReadTheirTruePeak(int number, double fraction, double amplitude, double phase, double expected)
    {
        foreach (int rate in new[] { 48000, 44100 })
        {
            int tenths = TruePeak(Tapered(rate, 1, amplitude, fraction, phase), rate).StreamTenths;
            output.WriteLine($"signal {number} at {rate} Hz: {tenths / 10.0:F1} dBTP");
            AssertNear(tenths, expected, 0.2, 0.4, $"signal {number} at {rate} Hz");
        }
    }

    [Theory]
    [InlineData(15, 0.25, 0.5, 0.0, -6.0)]
    [InlineData(16, 0.25, 0.5, 45.0, -6.0)]
    [InlineData(17, 1.0 / 6, 0.5, 60.0, -6.0)]
    [InlineData(18, 1.0 / 8, 0.5, 67.5, -6.0)]
    [InlineData(19, 0.25, 1.41, 45.0, 3.0)]
    public void TheSameSinesAt96kHzAreOversampledTwiceAndStayInTheTolerance(int number, double fraction, double amplitude, double phase, double expected)
    {
        TruePeakFigures t = TruePeak(Tapered(96000, 1, amplitude, fraction, phase), 96000);
        Assert.Equal(2, t.Oversampling);
        output.WriteLine($"signal {number} at 96 kHz: {t.StreamTenths / 10.0:F1} dBTP");
        AssertNear(t.StreamTenths, expected, 0.2, 0.4, $"signal {number} at 96 kHz");
    }

    // Signals 20 to 23: fs/6 sine with one period of fs/4 at amplitude 1.0, continuous in phase, made at 4 fs, lowpass filtered and downsampled
    // with an offset of 0 to 3 samples at the 4 fs rate.
    private static double[] Burst(int rate, int offset)
    {
        int over = rate * 4;
        int length = over / 5;
        var x = new double[length];
        double phase = 0;
        int burstStart = length / 2;
        for (int i = 0; i < length; i++)
        {
            bool inside = i >= burstStart && i < burstStart + 16;
            x[i] = (inside ? 1.0 : 0.5) * Math.Sin(phase);
            phase += 2 * Math.PI / (inside ? 16 : 24);
        }

        // A Kaiser-windowed sinc lowpass at 0.45 of the rate (0.1125 of the oversampled one), 255 taps.
        const int Taps = 255;
        double cutoff = 0.45 / 4;
        double[] h = new double[Taps];
        double sum = 0;
        for (int i = 0; i < Taps; i++)
        {
            double m = i - ((Taps - 1) / 2.0);
            double sinc = Math.Abs(m) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);
            double r = (2.0 * i / (Taps - 1)) - 1;
            double window = Bessel(8.0 * Math.Sqrt(1 - (r * r))) / Bessel(8.0);
            h[i] = sinc * window;
            sum += h[i];
        }

        var filtered = new double[length];
        for (int n = 0; n < length; n++)
        {
            double acc = 0;
            for (int i = 0; i < Taps; i++)
            {
                int j = n - i + ((Taps - 1) / 2);
                if (j >= 0 && j < length)
                {
                    acc += h[i] / sum * x[j];
                }
            }

            filtered[n] = acc;
        }

        int count = (length - offset) / 4;
        var output = new double[count];
        for (int i = 0; i < count; i++)
        {
            output[i] = filtered[offset + (4 * i)];
        }

        return output;
    }

    private static double Bessel(double x)
    {
        double sum = 1;
        double term = 1;
        for (int k = 1; k < 40; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
        }

        return sum;
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(21, 1)]
    [InlineData(22, 2)]
    [InlineData(23, 3)]
    public void Signals20To23AnInterSampleBurstDownsampledAtEveryOffsetReadsFullScale(int number, int offset)
    {
        double[] signal = Burst(48000, offset);
        TruePeakFigures t = TruePeak(signal, 48000);
        double samplePeak = signal.Max(Math.Abs);
        output.WriteLine($"signal {number}: {t.StreamTenths / 10.0:F1} dBTP, sample peak {20 * Math.Log10(samplePeak):F2} dBFS");
        AssertNear(t.StreamTenths, 0.0, 0.2, 0.4, $"signal {number}");
        Assert.True(t.StreamTenths >= (int)Math.Round(200 * Math.Log10(samplePeak)), "the true peak is not below the sample peak");
    }

    [Fact]
    public void TheTruePeakIsNeverBelowTheSamplePeakAndAFullScaleSamplePassesThrough()
    {
        double[] ones = Join(Silence(48000, 0.1), Tone(48000, 0.1, 0, 1000, 90), Silence(48000, 0.1));
        TruePeakFigures t = TruePeak(ones, 48000);
        Assert.True(t.StreamTenths >= 0);
        Assert.Equal(2, t.ChannelTenths.Count);
    }

    [Fact]
    public void FromAbout192kHzTheTruePeakIsTheSamplePeak()
    {
        double[] signal = Tapered(192000, 0.2, 0.7, 0.2, 10);
        TruePeakFigures t = TruePeak(signal, 192000);
        Assert.Equal(1, t.Oversampling);
        Assert.Equal((int)Math.Floor((200 * Math.Log10(signal.Max(Math.Abs))) + 0.5), t.StreamTenths);
    }

    [Theory]
    [InlineData(44100, 4)]
    [InlineData(95999, 4)]
    [InlineData(96000, 2)]
    [InlineData(191999, 2)]
    [InlineData(192000, 1)]
    [InlineData(384000, 1)]
    public void TheOversamplingFollowsTheRate(int rate, int expected)
    {
        Assert.Equal(expected, TruePeakMeter.FactorFor(rate));
    }

    [Fact]
    public void DigitalSilenceHasNoTruePeak()
    {
        TruePeakFigures t = TruePeak(Silence(48000, 1), 48000);
        Assert.Equal(MeasurementLimits.NoLevel, t.StreamTenths);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    public void TheVectorLoopTheScalarLoopAndTheSkippingAgreeBitForBit(int factor)
    {
        double[] noise = MeasurementSignals.Noise(100_000, 11);
        // Loud at the start so that most windows after it can be skipped, then quiet noise with a late short click.
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] *= i < 3000 ? 0.9 : 0.1;
        }

        noise[90_000] = -0.95;
        int rate = factor == 4 ? 48000 : 96000;
        double[] results = new double[3];
        for (int variant = 0; variant < 3; variant++)
        {
            var meter = new TruePeakMeter(1, rate) { UsesVectors = variant != 1, Skips = variant == 0 };
            for (int start = 0; start < noise.Length; start += 777)
            {
                meter.AddChannel(0, noise.AsSpan(start, Math.Min(777, noise.Length - start)));
            }

            results[variant] = meter.Tenths(0);
        }

        Assert.Equal(results[0], results[1]);
        Assert.Equal(results[0], results[2]);
    }

    [Fact]
    public void TheFiguresDoNotDependOnTheBlockSizes()
    {
        double[] signal = Join(Tapered(48000, 2, 0.8, 0.2, 33), Silence(48000, 0.5), Tapered(48000, 1, 1.2, 0.23, 71));
        TruePeakFigures reference = TruePeak(signal, 48000);
        foreach (int blockSize in new[] { 1, 7, 100, 4096, 4801, 100_000 })
        {
            Assert.Equal(reference.StreamTenths, Measure(Stereo(signal), 48000, blockSize: blockSize, kinds: MeasurementKinds.TruePeak).TruePeak!.StreamTenths);
        }
    }
}
