using System.Runtime.CompilerServices;

namespace Encspect.Host.Measurement;

// The math the measurements use instead of the platform's library, whose results may differ in the last bit between C libraries
// (docs/design/spectral-analysis.md, "Determinism"): polynomials evaluated with + and * only, and the exponent bits of a double.
// Math.Sqrt, Math.Floor and Math.Abs are correctly rounded or exact everywhere and stay allowed; nothing here fuses a multiply and
// an add (the JIT contracts none on its own) and nothing calls Math.Sin, Cos, Tan, Log, Exp or Pow.
internal static class DetMath
{
    public const double Pi = 3.141592653589793;

    public const double TwoPi = 6.283185307179586;

    public const double HalfPi = 1.5707963267948966;

    // The high and low parts of ln 2 and of pi / 2 below are the split fdlibm also uses, but they are mathematical constants (the digits of ln 2
    // and pi / 2, the high part with trailing zero bits), not code taken from it; the split makes k * Ln2High exact for the |k| of a double's exponent.
    private const double Ln2High = 6.93147180369123816490e-01;

    private const double Ln2Low = 1.90821492927058770002e-10;

    private const double InverseLn2 = 1.4426950408889634;

    private const double Log10E = 0.4342944819032518;

    private const double Sqrt2 = 1.4142135623730951;

    // pi / 2 split for the reduction of an argument: the high part has trailing zero bits, so k * HalfPiHigh is exact for k < 2^20.
    private const double HalfPiHigh = 1.57079632673412561417e+00;

    private const double HalfPiLow = 6.07710050650619224932e-11;

    private static readonly double[] SinCoefficients = Series(odd: true, terms: 11);

    private static readonly double[] CosCoefficients = Series(odd: false, terms: 10);

    private static readonly double[] ExpCoefficients = ExpSeries(16);

    // The coefficients of sin x / x in x^2 (1, -1/3!, 1/5!, ...) or of cos x in x^2 (1, -1/2!, 1/4!, ...), alternating.
    private static double[] Series(bool odd, int terms)
    {
        var result = new double[terms];
        double factorial = 1;
        for (int i = 0; i < terms; i++)
        {
            int degree = odd ? (2 * i) + 1 : 2 * i;
            if (degree > 1)
            {
                factorial *= (degree - 1) * (double)degree;
            }

            result[i] = (i % 2 == 0 ? 1.0 : -1.0) / factorial;
        }

        return result;
    }

    private static double[] ExpSeries(int terms)
    {
        var result = new double[terms];
        double factorial = 1;
        for (int i = 0; i < terms; i++)
        {
            if (i > 0)
            {
                factorial *= i;
            }

            result[i] = 1.0 / factorial;
        }

        return result;
    }

    // sin x for |x| <= pi / 4 (accurate to about 1e-17).
    private static double SinPolynomial(double x)
    {
        double x2 = x * x;
        double sum = SinCoefficients[^1];
        for (int i = SinCoefficients.Length - 2; i >= 0; i--)
        {
            sum = (sum * x2) + SinCoefficients[i];
        }

        return x * sum;
    }

    // cos x for |x| <= pi / 4.
    private static double CosPolynomial(double x)
    {
        double x2 = x * x;
        double sum = CosCoefficients[^1];
        for (int i = CosCoefficients.Length - 2; i >= 0; i--)
        {
            sum = (sum * x2) + CosCoefficients[i];
        }

        return sum;
    }

    // sin and cos of 2 pi k / n for the integers 0 <= k < n, n a multiple of 8, from the polynomials on the first octant and the
    // symmetries of the circle on the integer index, so that the symmetric entries of a table are exact negations or swaps.
    public static void SinCos(int k, int n, out double sin, out double cos)
    {
        int quarter = n / 4;
        int quadrant = k / quarter;
        int j = k % quarter;
        double s;
        double c;

        // j in [0, n / 4): the angle within a quarter, mirrored about n / 8 on the integer index.
        if (j * 2 <= quarter)
        {
            double x = TwoPi * j / n;
            s = SinPolynomial(x);
            c = CosPolynomial(x);
        }
        else
        {
            double x = TwoPi * (quarter - j) / n;
            s = CosPolynomial(x);
            c = SinPolynomial(x);
        }

        // The quadrant adds 90 degrees at a time.
        switch (quadrant)
        {
            case 0:
                sin = s;
                cos = c;
                break;
            case 1:
                sin = c;
                cos = -s;
                break;
            case 2:
                sin = -s;
                cos = -c;
                break;
            default:
                sin = -c;
                cos = s;
                break;
        }
    }

    // sin and cos of any x with |x| up to about 1e6: the argument is reduced by multiples of pi / 2 in two parts.
    public static double Sin(double x) => SinCosOf(x, wantsSine: true);

    public static double Cos(double x) => SinCosOf(x, wantsSine: false);

    private static double SinCosOf(double x, bool wantsSine)
    {
        double k = Math.Floor((x * (1.0 / HalfPi)) + 0.5);
        double r = (x - (k * HalfPiHigh)) - (k * HalfPiLow);
        long quadrant = ((long)k % 4 + 4) % 4;
        double s = SinPolynomial(r);
        double c = CosPolynomial(r);
        return wantsSine
            ? quadrant switch { 0 => s, 1 => c, 2 => -s, _ => -c }
            : quadrant switch { 0 => c, 1 => -s, 2 => -c, _ => s };
    }

    public static double Tan(double x)
    {
        double k = Math.Floor((x * (1.0 / HalfPi)) + 0.5);
        double r = (x - (k * HalfPiHigh)) - (k * HalfPiLow);
        double s = SinPolynomial(r);
        double c = CosPolynomial(r);
        return (long)k % 2 == 0 ? s / c : -(c / s);
    }

    // The natural logarithm of a positive finite x: the exponent from the bits, the mantissa folded into [1 / sqrt 2, sqrt 2) and
    // ln m = 2 atanh((m - 1) / (m + 1)) as a series.
    public static double Ln(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exponent = (int)((bits >> 52) & 0x7FF);
        if (exponent == 0)
        {
            // Subnormal: scale into the normal range first (2^54).
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0);
            exponent = (int)((bits >> 52) & 0x7FF) - 54;
        }

        exponent -= 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > Sqrt2)
        {
            m *= 0.5;
            exponent++;
        }

        double s = (m - 1.0) / (m + 1.0);
        double s2 = s * s;

        // 2 s (1 + s2 / 3 + s2^2 / 5 + ...): s2 is at most 0.0295, so twelve terms leave less than 1e-18.
        double sum = 1.0 / 23.0;
        sum = (sum * s2) + (1.0 / 21.0);
        sum = (sum * s2) + (1.0 / 19.0);
        sum = (sum * s2) + (1.0 / 17.0);
        sum = (sum * s2) + (1.0 / 15.0);
        sum = (sum * s2) + (1.0 / 13.0);
        sum = (sum * s2) + (1.0 / 11.0);
        sum = (sum * s2) + (1.0 / 9.0);
        sum = (sum * s2) + (1.0 / 7.0);
        sum = (sum * s2) + (1.0 / 5.0);
        sum = (sum * s2) + (1.0 / 3.0);
        sum = (sum * s2) + 1.0;
        double lnMantissa = 2.0 * s * sum;
        return (exponent * Ln2High) + ((exponent * Ln2Low) + lnMantissa);
    }

    public static double Log10(double x) => Ln(x) * Log10E;

    // 10 log10 x of a power.
    public static double Decibels(double power) => 10.0 * Log10(power);

    // e^x for x between about -745 and 709: x = k ln 2 + r, the series on |r| <= ln 2 / 2 and a scaling by 2^k.
    public static double Exp(double x)
    {
        if (!(x > -745.0))
        {
            return 0.0;
        }

        if (x > 709.7)
        {
            return double.PositiveInfinity;
        }

        double k = Math.Floor((x * InverseLn2) + 0.5);
        double r = (x - (k * Ln2High)) - (k * Ln2Low);
        double sum = ExpCoefficients[^1];
        for (int i = ExpCoefficients.Length - 2; i >= 0; i--)
        {
            sum = (sum * r) + ExpCoefficients[i];
        }

        int e = (int)k;

        // 2^e in two steps, so that a result near the subnormal range does not overflow the scale.
        int first = e / 2;
        return sum * PowerOfTwo(first) * PowerOfTwo(e - first);
    }

    // a^b for a > 0.
    public static double Pow(double a, double b) => Exp(b * Ln(a));

    // 2^e for -1022 <= e <= 1023, exact.
    public static double PowerOfTwo(int e) => BitConverter.Int64BitsToDouble((long)(e + 1023) << 52);

    // The tenths of a dB of a power: floor(100 log10 p + 0.5), the one rounding of every figure (halves up). A power of 0 is
    // minus infinity, which the caller handles.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Tenths(double power) => (int)Math.Floor((100.0 * Log10(power)) + 0.5);

    // The binary exponent of a positive double, from its bits (0 for zero and subnormals reads as -1023).
    public static int Exponent(double x) => (int)((BitConverter.DoubleToInt64Bits(x) >> 52) & 0x7FF) - 1023;
}
