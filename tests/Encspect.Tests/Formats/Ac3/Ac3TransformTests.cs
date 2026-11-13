using Encspect.Formats.Ac3.Decoding;

namespace Encspect.Tests;

// The tables and transforms of the AC-3 decoder against the specification: the window of A/52:2018 Table 7.33 (copied from the specification, five digits each), and the
// inverse transforms of 7.9.4 computed as the pseudo code writes them, with the sums over k and n done directly, in double precision.
public class Ac3TransformTests
{
    // Table 7.33, w[addr] with addr = 10 * A + B, 256 values.
    private static readonly double[] Table =
    [
        0.00014, 0.00024, 0.00037, 0.00051, 0.00067, 0.00086, 0.00107, 0.00130, 0.00157, 0.00187,
        0.00220, 0.00256, 0.00297, 0.00341, 0.00390, 0.00443, 0.00501, 0.00564, 0.00632, 0.00706,
        0.00785, 0.00871, 0.00962, 0.01061, 0.01166, 0.01279, 0.01399, 0.01526, 0.01662, 0.01806,
        0.01959, 0.02121, 0.02292, 0.02472, 0.02662, 0.02863, 0.03073, 0.03294, 0.03527, 0.03770,
        0.04025, 0.04292, 0.04571, 0.04862, 0.05165, 0.05481, 0.05810, 0.06153, 0.06508, 0.06878,
        0.07261, 0.07658, 0.08069, 0.08495, 0.08935, 0.09389, 0.09859, 0.10343, 0.10842, 0.11356,
        0.11885, 0.12429, 0.12988, 0.13563, 0.14152, 0.14757, 0.15376, 0.16011, 0.16661, 0.17325,
        0.18005, 0.18699, 0.19407, 0.20130, 0.20867, 0.21618, 0.22382, 0.23161, 0.23952, 0.24757,
        0.25574, 0.26404, 0.27246, 0.28100, 0.28965, 0.29841, 0.30729, 0.31626, 0.32533, 0.33450,
        0.34376, 0.35311, 0.36253, 0.37204, 0.38161, 0.39126, 0.40096, 0.41072, 0.42054, 0.43040,
        0.44030, 0.45023, 0.46020, 0.47019, 0.48020, 0.49022, 0.50025, 0.51028, 0.52031, 0.53033,
        0.54033, 0.55031, 0.56026, 0.57019, 0.58007, 0.58991, 0.59970, 0.60944, 0.61912, 0.62873,
        0.63827, 0.64774, 0.65713, 0.66643, 0.67564, 0.68476, 0.69377, 0.70269, 0.71150, 0.72019,
        0.72877, 0.73723, 0.74557, 0.75378, 0.76186, 0.76981, 0.77762, 0.78530, 0.79283, 0.80022,
        0.80747, 0.81457, 0.82151, 0.82831, 0.83496, 0.84145, 0.84779, 0.85398, 0.86001, 0.86588,
        0.87160, 0.87716, 0.88257, 0.88782, 0.89291, 0.89785, 0.90264, 0.90728, 0.91176, 0.91610,
        0.92028, 0.92432, 0.92822, 0.93197, 0.93558, 0.93906, 0.94240, 0.94560, 0.94867, 0.95162,
        0.95444, 0.95713, 0.95971, 0.96217, 0.96451, 0.96674, 0.96887, 0.97089, 0.97281, 0.97463,
        0.97635, 0.97799, 0.97953, 0.98099, 0.98236, 0.98366, 0.98488, 0.98602, 0.98710, 0.98811,
        0.98905, 0.98994, 0.99076, 0.99153, 0.99225, 0.99291, 0.99353, 0.99411, 0.99464, 0.99513,
        0.99558, 0.99600, 0.99639, 0.99674, 0.99706, 0.99736, 0.99763, 0.99788, 0.99811, 0.99831,
        0.99850, 0.99867, 0.99882, 0.99895, 0.99908, 0.99919, 0.99929, 0.99938, 0.99946, 0.99953,
        0.99959, 0.99965, 0.99969, 0.99974, 0.99978, 0.99981, 0.99984, 0.99986, 0.99988, 0.99990,
        0.99992, 0.99993, 0.99994, 0.99995, 0.99996, 0.99997, 0.99998, 0.99998, 0.99998, 0.99999,
        0.99999, 0.99999, 0.99999, 1.00000, 1.00000, 1.00000, 1.00000, 1.00000, 1.00000, 1.00000,
        1.00000, 1.00000, 1.00000, 1.00000, 1.00000, 1.00000,
    ];

    [Fact]
    public void The_window_equals_every_value_of_table_7_33_to_its_five_digits()
    {
        float[] window = Quantizers.Window();

        Assert.Equal(256, window.Length);
        for (int n = 0; n < 256; n++)
        {
            Assert.True(Math.Abs(window[n] - Table[n]) <= 5.0e-6 + 1e-7, $"w[{n}] is {window[n]}, the table has {Table[n]}.");
        }
    }

    [Fact]
    public void The_symmetric_quantizers_have_the_values_of_tables_7_19_to_7_23()
    {
        Assert.Equal([-2f / 3, 0f, 2f / 3], Quantizers.Symmetric[1]);
        Assert.Equal([-4f / 5, -2f / 5, 0f, 2f / 5, 4f / 5], Quantizers.Symmetric[2]);
        Assert.Equal(7, Quantizers.Symmetric[3].Length);
        Assert.Equal(-6f / 7, Quantizers.Symmetric[3][0]);
        Assert.Equal(0f, Quantizers.Symmetric[3][3]);
        Assert.Equal(11, Quantizers.Symmetric[4].Length);
        Assert.Equal(-10f / 11, Quantizers.Symmetric[4][0]);
        Assert.Equal(10f / 11, Quantizers.Symmetric[4][10]);
        Assert.Equal(15, Quantizers.Symmetric[5].Length);
        Assert.Equal(-14f / 15, Quantizers.Symmetric[5][0]);
        Assert.Equal(0f, Quantizers.Symmetric[5][7]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_long_transform_equals_the_pseudo_code_of_7_9_4_1(int seed)
    {
        var random = new Random(seed);
        var spectrum = new float[256];
        for (int k = 0; k < 256; k++)
        {
            spectrum[k] = (float)((random.NextDouble() * 2) - 1) * (k < 100 ? 1f : 0.1f);
        }

        var actual = new float[512];
        new InverseTransform().Long(spectrum, actual);
        double[] expected = Direct512(spectrum);

        double scale = expected.Max(Math.Abs);
        for (int n = 0; n < 512; n++)
        {
            Assert.True(Math.Abs(actual[n] - expected[n]) <= 2e-4 * scale, $"x[{n}] is {actual[n]}, the pseudo code gives {expected[n]}.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_short_transforms_equal_the_pseudo_code_of_7_9_4_2(int seed)
    {
        var random = new Random(seed);
        var spectrum = new float[256];
        for (int k = 0; k < 256; k++)
        {
            spectrum[k] = (float)((random.NextDouble() * 2) - 1) * (k < 100 ? 1f : 0.1f);
        }

        var actual = new float[512];
        new InverseTransform().Short(spectrum, actual);
        double[] expected = Direct256(spectrum);

        double scale = expected.Max(Math.Abs);
        for (int n = 0; n < 512; n++)
        {
            Assert.True(Math.Abs(actual[n] - expected[n]) <= 2e-4 * scale, $"x[{n}] is {actual[n]}, the pseudo code gives {expected[n]}.");
        }
    }

    // 7.9.4.1 steps 2 to 5, the inverse FFT as the sum it is.
    private static double[] Direct512(float[] spectrum)
    {
        const int n512 = 512;
        double[] w = Table;
        var zr = new double[128];
        var zi = new double[128];
        for (int k = 0; k < 128; k++)
        {
            double xcos = -Math.Cos(2 * Math.PI * ((8 * k) + 1) / (8 * n512));
            double xsin = -Math.Sin(2 * Math.PI * ((8 * k) + 1) / (8 * n512));
            double a = spectrum[(n512 / 2) - (2 * k) - 1];
            double b = spectrum[2 * k];
            zr[k] = (a * xcos) - (b * xsin);
            zi[k] = (b * xcos) + (a * xsin);
        }

        var yr = new double[128];
        var yi = new double[128];
        for (int n = 0; n < 128; n++)
        {
            double sr = 0;
            double si = 0;
            for (int k = 0; k < 128; k++)
            {
                double angle = 8 * Math.PI * k * n / n512;
                sr += (zr[k] * Math.Cos(angle)) - (zi[k] * Math.Sin(angle));
                si += (zr[k] * Math.Sin(angle)) + (zi[k] * Math.Cos(angle));
            }

            double xcos = -Math.Cos(2 * Math.PI * ((8 * n) + 1) / (8 * n512));
            double xsin = -Math.Sin(2 * Math.PI * ((8 * n) + 1) / (8 * n512));
            yr[n] = (sr * xcos) - (si * xsin);
            yi[n] = (si * xcos) + (sr * xsin);
        }

        var x = new double[n512];
        for (int n = 0; n < n512 / 8; n++)
        {
            x[2 * n] = -yi[(n512 / 8) + n] * w[2 * n];
            x[(2 * n) + 1] = yr[(n512 / 8) - n - 1] * w[(2 * n) + 1];
            x[(n512 / 4) + (2 * n)] = -yr[n] * w[(n512 / 4) + (2 * n)];
            x[(n512 / 4) + (2 * n) + 1] = yi[(n512 / 4) - n - 1] * w[(n512 / 4) + (2 * n) + 1];
            x[(n512 / 2) + (2 * n)] = -yr[(n512 / 8) + n] * w[(n512 / 2) - (2 * n) - 1];
            x[(n512 / 2) + (2 * n) + 1] = yi[(n512 / 8) - n - 1] * w[(n512 / 2) - (2 * n) - 2];
            x[(3 * n512 / 4) + (2 * n)] = yi[n] * w[(n512 / 4) - (2 * n) - 1];
            x[(3 * n512 / 4) + (2 * n) + 1] = -yr[(n512 / 4) - n - 1] * w[(n512 / 4) - (2 * n) - 2];
        }

        return x;
    }

    // 7.9.4.2 steps 1 to 5.
    private static double[] Direct256(float[] spectrum)
    {
        const int n512 = 512;
        double[] w = Table;
        var yr = new double[2][];
        var yi = new double[2][];
        for (int half = 0; half < 2; half++)
        {
            var zr = new double[64];
            var zi = new double[64];
            for (int k = 0; k < 64; k++)
            {
                double xcos = -Math.Cos(2 * Math.PI * ((8 * k) + 1) / (4 * n512));
                double xsin = -Math.Sin(2 * Math.PI * ((8 * k) + 1) / (4 * n512));
                double a = spectrum[(2 * ((n512 / 4) - (2 * k) - 1)) + half];
                double b = spectrum[(2 * (2 * k)) + half];
                zr[k] = (a * xcos) - (b * xsin);
                zi[k] = (b * xcos) + (a * xsin);
            }

            yr[half] = new double[64];
            yi[half] = new double[64];
            for (int n = 0; n < 64; n++)
            {
                double sr = 0;
                double si = 0;
                for (int k = 0; k < 64; k++)
                {
                    double angle = 16 * Math.PI * k * n / n512;
                    sr += (zr[k] * Math.Cos(angle)) - (zi[k] * Math.Sin(angle));
                    si += (zr[k] * Math.Sin(angle)) + (zi[k] * Math.Cos(angle));
                }

                double xcos = -Math.Cos(2 * Math.PI * ((8 * n) + 1) / (4 * n512));
                double xsin = -Math.Sin(2 * Math.PI * ((8 * n) + 1) / (4 * n512));
                yr[half][n] = (sr * xcos) - (si * xsin);
                yi[half][n] = (si * xcos) + (sr * xsin);
            }
        }

        var x = new double[n512];
        for (int n = 0; n < n512 / 8; n++)
        {
            x[2 * n] = -yi[0][n] * w[2 * n];
            x[(2 * n) + 1] = yr[0][(n512 / 8) - n - 1] * w[(2 * n) + 1];
            x[(n512 / 4) + (2 * n)] = -yr[0][n] * w[(n512 / 4) + (2 * n)];
            x[(n512 / 4) + (2 * n) + 1] = yi[0][(n512 / 8) - n - 1] * w[(n512 / 4) + (2 * n) + 1];
            x[(n512 / 2) + (2 * n)] = -yr[1][n] * w[(n512 / 2) - (2 * n) - 1];
            x[(n512 / 2) + (2 * n) + 1] = yi[1][(n512 / 8) - n - 1] * w[(n512 / 2) - (2 * n) - 2];
            x[(3 * n512 / 4) + (2 * n)] = yi[1][n] * w[(n512 / 4) - (2 * n) - 1];
            x[(3 * n512 / 4) + (2 * n) + 1] = -yr[1][(n512 / 8) - n - 1] * w[(n512 / 4) - (2 * n) - 2];
        }

        return x;
    }
}
