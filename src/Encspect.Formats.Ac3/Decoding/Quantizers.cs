using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The mantissa quantizers of A/52 7.3 and the transform tables of 7.9, in single precision from DetMath so that every platform gets the same values.
internal static class Quantizers
{
    // Tables 7.19 to 7.23 (bap 1 to 5): the value of each mantissa code, (2 * code - (levels - 1)) / levels.
    public static readonly float[][] Symmetric =
    [
        [],
        Make(3),
        Make(5),
        Make(7),
        Make(11),
        Make(15),
    ];

    // 2^-n for n 0 to 63: the shifts of exponents and of the asymmetric quantizers' fractions.
    public static readonly float[] Shift = [.. Enumerable.Range(0, 64).Select(n => (float)DetMath.PowerOfTwo(-n))];

    // The bits of the mantissa of bap 6 to 15 (Table 7.18).
    public static readonly int[] AsymmetricBits = [0, 0, 0, 0, 0, 0, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16];

    private static float[] Make(int levels)
    {
        var table = new float[levels];
        for (int code = 0; code < levels; code++)
        {
            table[code] = (float)(((2.0 * code) - (levels - 1)) / levels);
        }

        return table;
    }

    // Table 7.33 (the window of the transform, 256 values for the first half; the second half is its mirror image): a Kaiser-Bessel derived window with the
    // parameter 5, w[n] = sqrt(sum of kaiser[0..n] / sum of kaiser[0..256]) where kaiser[j] = I0(5 pi sqrt(1 - (2j/256 - 1)^2)). A test compares every value with the table's.
    public static float[] Window()
    {
        var kaiser = new double[257];
        double total = 0;
        for (int j = 0; j <= 256; j++)
        {
            double x = (2.0 * j / 256.0) - 1.0;
            kaiser[j] = BesselI0(5.0 * Math.PI * Math.Sqrt(1.0 - (x * x)));
            total += kaiser[j];
        }

        var window = new float[256];
        double sum = 0;
        for (int n = 0; n < 256; n++)
        {
            sum += kaiser[n];
            window[n] = (float)Math.Sqrt(sum / total);
        }

        return window;
    }

    // The modified Bessel function of the first kind of order 0 by its power series, which converges for every argument used here.
    private static double BesselI0(double x)
    {
        double half = x / 2.0;
        double term = 1.0;
        double sum = 1.0;
        for (int k = 1; k < 200; k++)
        {
            term *= half / k;
            double square = term * term;
            sum += square;
            if (square < 1e-18 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
