using Encspect.Core;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

public class MeasurementKernelTests
{
    [Fact]
    public void TheSineAndCosineTableMatchesTheLibraryAndIsExactlySymmetric()
    {
        foreach (int n in new[] { 1024, 4096, 32768 })
        {
            for (int k = 0; k < n; k++)
            {
                DetMath.SinCos(k, n, out double sin, out double cos);
                Assert.True(Math.Abs(sin - Math.Sin(2 * Math.PI * k / n)) < 1.2e-15, $"sin {k}/{n}");
                Assert.True(Math.Abs(cos - Math.Cos(2 * Math.PI * k / n)) < 1.2e-15, $"cos {k}/{n}");
            }

            DetMath.SinCos(n / 4, n, out double s, out double c);
            Assert.Equal(1.0, s);
            Assert.Equal(0.0, c);
            DetMath.SinCos(n / 2, n, out s, out c);
            Assert.Equal(0.0, s);
            Assert.Equal(-1.0, c);
        }
    }

    [Fact]
    public void TheLogarithmExponentialPowerAndTangentMatchTheLibrary()
    {
        for (double x = 1e-12; x < 1e12; x *= 1.37)
        {
            Assert.True(Math.Abs(DetMath.Log10(x) - Math.Log10(x)) <= 3e-15 * Math.Max(1, Math.Abs(Math.Log10(x))), $"log10 {x}");
            Assert.True(Math.Abs(DetMath.Ln(x) - Math.Log(x)) <= 3e-15 * Math.Max(1, Math.Abs(Math.Log(x))), $"ln {x}");
        }

        Assert.Equal(-323.0, Math.Floor(DetMath.Log10(5e-323)));
        for (double x = -700; x < 700; x += 3.1)
        {
            Assert.True(Math.Abs(DetMath.Exp(x) / Math.Exp(x) - 1) < 1e-14, $"exp {x}");
        }

        Assert.Equal(0.0, DetMath.Exp(-800));
        Assert.True(Math.Abs((DetMath.Pow(10, 3.999843853973347 / 20) / Math.Pow(10, 3.999843853973347 / 20)) - 1) < 1e-14);
        Assert.True(Math.Abs((DetMath.Pow(1.5848931924611136, 0.4996667741545416) / Math.Pow(1.5848931924611136, 0.4996667741545416)) - 1) < 1e-14);
        for (double x = -1.5; x < 1.5; x += 0.0137)
        {
            Assert.True(Math.Abs(DetMath.Tan(x) - Math.Tan(x)) < 1e-14 * Math.Max(1, Math.Abs(Math.Tan(x))), $"tan {x}");
            Assert.True(Math.Abs(DetMath.Sin(x * 100) - Math.Sin(x * 100)) < 1e-13);
            Assert.True(Math.Abs(DetMath.Cos(x * 100) - Math.Cos(x * 100)) < 1e-13);
        }

        // 100 times log10 of a power rounds to the nearest tenth of a dB, halves up.
        Assert.Equal(-60, DetMath.Tenths(0.25));
        Assert.Equal(0, DetMath.Tenths(1.0));
        Assert.Equal(-30, DetMath.Tenths(0.5));
        Assert.Equal(-5, DetMath.Tenths(0.9));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void TheRowsAgreeWithADirectTransform(int size)
    {
        var plan = new FftPlan(size);
        double[] samples = MeasurementSignals.Noise(size, 7);
        double[] rows = new double[512];
        plan.Rows(samples, plan.NewScratch(), plan.NewScratch(), rows);

        int bins = size / 1024;
        double maximum = 0;
        for (int row = 0; row < 512; row += 17)
        {
            double sum = 0;
            for (int bin = row * bins; bin < (row + 1) * bins; bin++)
            {
                double re = 0;
                double im = 0;
                for (int n = 0; n < size; n++)
                {
                    double window = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / size));
                    double angle = -2 * Math.PI * ((long)bin * n % size) / size;
                    re += samples[n] * window * Math.Cos(angle);
                    im += samples[n] * window * Math.Sin(angle);
                }

                sum += ((re * re) + (im * im)) * 16.0 / ((double)size * size);
            }

            double expected = sum / bins;
            maximum = Math.Max(maximum, Math.Abs(rows[row] - expected) / expected);
        }

        Assert.True(maximum < 1e-9, $"relative difference {maximum}");
    }

    [Fact]
    public void ASineCenteredOnABinReadsZeroDecibelsAtItsBinAndMinusSixAtTheNextOnes()
    {
        var plan = new FftPlan(4096);
        double[] samples = new double[4096];
        for (int n = 0; n < samples.Length; n++)
        {
            // Bin 400: 400 periods in 4,096 samples.
            DetMath.SinCos(400 * n % 4096, 4096, out double sin, out _);
            samples[n] = sin;
        }

        double[] rows = new double[512];
        plan.Rows(samples, plan.NewScratch(), plan.NewScratch(), rows);

        // Row 100 holds bins 400 to 403: (1 + 0.25 + 0 + 0) / 4; row 99 holds bin 399 at a quarter.
        Assert.True(Math.Abs(rows[100] - 0.3125) < 1e-12);
        Assert.True(Math.Abs(rows[99] - 0.0625) < 1e-12);
        Assert.True(rows[50] < 1e-24);
    }

    [Theory]
    [InlineData(8000, 1024)]
    [InlineData(11025, 1024)]
    [InlineData(22050, 2048)]
    [InlineData(24000, 2048)]
    [InlineData(32000, 4096)]
    [InlineData(44100, 4096)]
    [InlineData(48000, 4096)]
    [InlineData(88200, 8192)]
    [InlineData(96000, 8192)]
    [InlineData(176400, 16384)]
    [InlineData(192000, 16384)]
    [InlineData(352800, 32768)]
    [InlineData(4000000, 32768)]
    [InlineData(2000, 1024)]
    public void TheFrameSizeFollowsTheSampleRate(int rate, int size) => Assert.Equal(size, FftPlan.SizeFor(rate));
}
