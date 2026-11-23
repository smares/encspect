using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Tak.Decoding.Lossless;

// The reconstruction stages of docs/design/tak-format.md that work on values, not bits: the prediction filter's coefficients and its dot product, the integration
// of a difference order and the decorrelation of two channels. All arithmetic is 32-bit two's complement with wraparound, which the lossless decoders may use
// freely: integers give the same bits everywhere.
internal static class Reconstruction
{
    // Section 5, the order table: the entry for u(4); 15 means no prediction filter.
    public static ReadOnlySpan<int> Orders => [4, 8, 12, 16, 24, 32, 48, 64, 80, 96, 128, 160, 192, 224, 256, 0];

    public const int MaxOrder = 256;

    // From the parameters (k) of a filter of order k.Length to its coefficients, in the order of the history: coefficient d (the value d places back) lands at
    // coefficients[order - d], so that the window of the last order values, oldest first, multiplies it element by element. terms is scratch of the same length.
    public static void Coefficients(ReadOnlySpan<int> k, int precision, Span<int> terms, Span<short> coefficients)
    {
        int order = k.Length;
        if (order == 0)
        {
            return;
        }

        unchecked
        {
            terms[0] = 64 * k[0];
            for (int i = 1; i < order; i++)
            {
                int factor = k[i];
                int low = 0;
                int high = i - 1;
                while (low < high)
                {
                    int a = terms[low];
                    int b = terms[high];
                    terms[low] = a + (((factor * b) + 256) >> 9);
                    terms[high] = b + (((factor * a) + 256) >> 9);
                    low++;
                    high--;
                }

                if (low == high)
                {
                    int a = terms[low];
                    terms[low] = a + (((factor * a) + 256) >> 9);
                }

                terms[i] = 64 * factor;
            }

            int shift = 15 - precision;
            int round = 1 << (shift - 1);
            for (int d = 1; d <= order; d++)
            {
                coefficients[order - d] = (short)(-((terms[d - 1] + round) >> shift));
            }
        }
    }

    // The sum of the products of the history and the coefficients, 32-bit with wraparound, whatever the order the products are added in.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Dot(ReadOnlySpan<short> history, ReadOnlySpan<short> coefficients)
    {
        int n = coefficients.Length;
        history = history[..n];
        int s0 = 0;
        int s1 = 0;
        int s2 = 0;
        int s3 = 0;
        int i = 0;
        unchecked
        {
            for (; i + 4 <= n; i += 4)
            {
                s0 += history[i] * coefficients[i];
                s1 += history[i + 1] * coefficients[i + 1];
                s2 += history[i + 2] * coefficients[i + 2];
                s3 += history[i + 3] * coefficients[i + 3];
            }

            for (; i < n; i++)
            {
                s0 += history[i] * coefficients[i];
            }
        }

        return s0 + s1 + s2 + s3;
    }

    // Section 7.1: the sequence of differences of the order, turned back into values, in place. Nothing happens for fewer than 2 values or order 0.
    public static void Integrate(Span<int> values, int order)
    {
        if (values.Length < 2 || order == 0)
        {
            return;
        }

        unchecked
        {
            if (order == 1)
            {
                for (int i = 1; i < values.Length; i++)
                {
                    values[i] += values[i - 1];
                }

                return;
            }

            int d = values[1];
            values[1] = values[0] + d;
            if (order == 2)
            {
                for (int i = 2; i < values.Length; i++)
                {
                    d += values[i];
                    values[i] = values[i - 1] + d;
                }

                return;
            }

            if (values.Length < 3)
            {
                return;
            }

            int e = values[2];
            d += e;
            values[2] = values[1] + d;
            for (int i = 3; i < values.Length; i++)
            {
                e += values[i];
                d += e;
                values[i] = values[i - 1] + d;
            }
        }
    }

    public static int Clip14(int value) => value < -8192 ? -8192 : value > 8191 ? 8191 : value;

    // Section 6, the modes with no fields of their own, on positions 1 to n - 1 of the two channels (position 0 is never changed).
    public static void Add(Span<int> x, Span<int> y)
    {
        unchecked
        {
            for (int i = 1; i < x.Length; i++)
            {
                y[i] = x[i] + y[i];
            }
        }
    }

    public static void Subtract(Span<int> x, Span<int> y)
    {
        unchecked
        {
            for (int i = 1; i < x.Length; i++)
            {
                x[i] = y[i] - x[i];
            }
        }
    }

    public static void MidSide(Span<int> x, Span<int> y)
    {
        unchecked
        {
            for (int i = 1; i < x.Length; i++)
            {
                int a = x[i] - (y[i] >> 1);
                x[i] = a;
                y[i] = a + y[i];
            }
        }
    }

    public static void Scaled(Span<int> x, Span<int> y, int shift, int gain)
    {
        unchecked
        {
            for (int i = 1; i < x.Length; i++)
            {
                x[i] = ((((gain * (y[i] >> shift)) + 128) >> 8) << shift) - x[i];
            }
        }
    }

    // The filter mode over positions 1 to n - 1; ys is scratch of n - 1 shorts, coefficients the order's g values.
    public static void Filter(Span<int> x, Span<int> y, int shift, ReadOnlySpan<short> coefficients, bool isHeadAdded, bool isTailAdded, Span<short> ys)
    {
        int k = x.Length - 1;
        int order = coefficients.Length;
        int half = order / 2;
        unchecked
        {
            for (int j = 0; j < k; j++)
            {
                ys[j] = (short)(y[j + 1] >> shift);
            }

            if (isHeadAdded)
            {
                for (int j = 0; j < half; j++)
                {
                    x[j + 1] += y[j + 1];
                }
            }

            for (int j = half; j <= k - half; j++)
            {
                int a = 512 + Dot(ys.Slice(j - half, order), coefficients);
                x[j + 1] = (Clip14(a >> 10) << shift) - x[j + 1];
            }

            if (isTailAdded)
            {
                for (int j = k - half + 1; j < k; j++)
                {
                    x[j + 1] += y[j + 1];
                }
            }
        }
    }
}
