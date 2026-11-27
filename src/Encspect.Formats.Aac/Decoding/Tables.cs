using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// The tables of the reconstruction, computed once in double with DetMath and rounded to float: they are defined by formulas of ISO/IEC 14496-3 (4.6.2, 4.6.9.3, 4.6.11.3),
// which libxaac (Apache 2.0) tabulates in fixed point.
internal static class Tables
{
    // The largest magnitude a quantized value can take: 8191 from an escape sequence (4.6.1.3), and the 15 of a pulse on top (4.6.3.3).
    public const int MaxQuantized = 8191 + 15;

    // |q|^(4/3) (4.6.2.1).
    public static readonly float[] Pow43 = BuildPow43();

    // 2^((sf - 100) / 4) for the scalefactors 0 to 255 (4.6.2.3).
    public static readonly float[] Gain = BuildGain();

    // The amplitude of noise substitution for the noise energies -100 to 155: 2^(energy / 4) (4.6.13).
    public static readonly float[] NoiseGain = BuildNoiseGain();

    // The intensity stereo factor 2^(-position / 4) for the positions -255 to 255 (4.6.8.2).
    public static readonly float[] IntensityGain = BuildIntensityGain();

    // Rising halves of the long (1,024 samples) and short (128) window of the sine and Kaiser-Bessel derived shapes (4.6.11.3.2): index 0 sine, 1 KBD.
    public static readonly float[][] LongRise = [SineRise(1024), KbdRise(1024, 4)];

    public static readonly float[][] ShortRise = [SineRise(128), KbdRise(128, 6)];

    // The LTP coefficients of ltp_coef 0 to 7 (Table 4.152).
    public static readonly float[] LtpCoefficients = [0.570829f, 0.696616f, 0.813004f, 0.911304f, 0.984900f, 1.067894f, 1.194601f, 1.369533f];

    // The TNS parcor coefficients sin(index / iqfac) for 3 and 4 bit resolutions, indexed by the signed value plus 4 or 8 (4.6.9.3).
    public static readonly float[][] Tns = [BuildTns(3), BuildTns(4)];

    // The largest scalefactor band count of a TNS filter's range, per sampling frequency index: long and short windows (4.6.9.4, Table 4.157, as libxaac's tns_max_bands_tbl holds it).
    public static readonly int[][] TnsMaxBands =
    [
        [31, 9], [31, 9], [34, 10], [40, 14], [42, 14], [51, 14], [46, 14], [46, 14], [42, 14], [42, 14], [42, 14], [39, 14],
    ];

    private static float[] BuildPow43()
    {
        var table = new float[MaxQuantized + 1];
        for (int i = 1; i < table.Length; i++)
        {
            table[i] = (float)DetMath.Pow(i, 4.0 / 3.0);
        }

        return table;
    }

    private static float[] BuildGain()
    {
        var table = new float[256];
        for (int sf = 0; sf < table.Length; sf++)
        {
            table[sf] = (float)DetMath.Pow(2.0, (sf - 100) / 4.0);
        }

        return table;
    }

    private static float[] BuildNoiseGain()
    {
        var table = new float[256];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)DetMath.Pow(2.0, (i - 100) / 4.0);
        }

        return table;
    }

    private static float[] BuildIntensityGain()
    {
        var table = new float[511];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)DetMath.Pow(2.0, -(i - 255) / 4.0);
        }

        return table;
    }

    private static float[] SineRise(int length)
    {
        var slope = new float[length];
        for (int n = 0; n < length; n++)
        {
            slope[n] = (float)DetMath.Sin(Math.PI * (n + 0.5) / (2.0 * length));
        }

        return slope;
    }

    // w(n) = sqrt(sum of W'(p) for p up to n / sum of W'(p) for p up to N/2), with W' the Kaiser window of alpha, N = 2 * length (4.6.11.3.2).
    private static float[] KbdRise(int length, double alpha)
    {
        int n = 2 * length;
        var kernel = new double[length + 1];
        double total = 0;
        for (int p = 0; p <= length; p++)
        {
            double r = (p - (n / 4.0)) / (n / 4.0);
            kernel[p] = BesselI0(Math.PI * alpha * Math.Sqrt(Math.Max(0, 1.0 - (r * r))));
            total += kernel[p];
        }

        var slope = new float[length];
        double sum = 0;
        for (int p = 0; p < length; p++)
        {
            sum += kernel[p];
            slope[p] = (float)Math.Sqrt(sum / total);
        }

        return slope;
    }

    // The modified Bessel function of order 0: the sum of ((x / 2)^k / k!)^2, with a fixed number of terms.
    private static double BesselI0(double x)
    {
        double term = 1;
        double sum = 1;
        double half = x / 2;
        for (int k = 1; k <= 80; k++)
        {
            term *= half / k;
            sum += term * term;
        }

        return sum;
    }

    private static float[] BuildTns(int bits)
    {
        int levels = 1 << (bits - 1);
        double iqfac = (levels - 0.5) / (Math.PI / 2);
        double iqfacNegative = (levels + 0.5) / (Math.PI / 2);
        var table = new float[2 * levels];
        for (int i = 0; i < table.Length; i++)
        {
            int index = i - levels;
            table[i] = (float)DetMath.Sin(index / (index >= 0 ? iqfac : iqfacNegative));
        }

        return table;
    }
}
