// Follows Apple's ALAC reference implementation (macosforge/alac, dp_dec.c, unpc_block), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using System.Runtime.CompilerServices;

namespace Encspect.Formats.Alac.Decoding;

// The adaptive predictor of ALAC. Every sum wraps on 32 bits and the coefficients on 16, as the C code's do; the output of each step is cut to the channel's width by a shift
// up and back down. The residuals are replaced by the samples where they lie: a sample depends on the ones before it and on the residual at its own position, which is read first.
internal static class Prediction
{
    // A predictor "order" of 31 is not a filter of 31 taps: it selects the first-order difference that the second stage of a channel with a nonzero mode starts with.
    public const int DifferenceOrder = 31;

    private static int Sign(int value) => (int)((uint)-value >> 31) | (value >> 31);

    // The first-order stage: each sample is the residual plus the previous sample (dp_dec.c, numactive 31).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Difference(Span<int> x, int chanBits)
    {
        int shift = 32 - chanBits;
        int previous = x.IsEmpty ? 0 : x[0];
        for (int j = 1; j < x.Length; j++)
        {
            int value = unchecked(x[j] + previous);
            previous = (value << shift) >> shift;
            x[j] = previous;
        }
    }

    // The filter of dp_dec.c with numactive taps (0 to 30, and 31 for the difference): coefs holds the order coefficients, which the sign-sign adaptation changes as it goes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Restore(Span<int> x, Span<short> coefs, int order, int chanBits, int denShift)
    {
        if (order == 0 || x.Length < 2)
        {
            return;
        }

        if (order == DifferenceOrder)
        {
            Difference(x, chanBits);
            return;
        }

        int num = x.Length;
        int shift = 32 - chanBits;
        int half = 1 << (denShift - 1);

        // The first samples, as many as the filter has taps, are first-order differences.
        int warm = Math.Min(order, num - 1);
        for (int j = 1; j <= warm; j++)
        {
            int value = unchecked(x[j] + x[j - 1]);
            x[j] = (value << shift) >> shift;
        }

        int lim = order + 1;
        unchecked
        {
            for (int j = lim; j < num; j++)
            {
                int top = x[j - lim];
                int sum = 0;
                for (int k = 0; k < order; k++)
                {
                    sum += coefs[k] * (x[j - 1 - k] - top);
                }

                int del = x[j];
                int rest = del;
                int sg = Sign(del);
                del += top + ((sum + half) >> denShift);
                x[j] = (del << shift) >> shift;

                if (sg > 0)
                {
                    for (int k = order - 1; k >= 0; k--)
                    {
                        int dd = top - x[j - 1 - k];
                        int sgn = Sign(dd);
                        coefs[k] -= (short)sgn;
                        rest -= (order - k) * ((sgn * dd) >> denShift);
                        if (rest <= 0)
                        {
                            break;
                        }
                    }
                }
                else if (sg < 0)
                {
                    for (int k = order - 1; k >= 0; k--)
                    {
                        int dd = top - x[j - 1 - k];
                        int sgn = Sign(dd);
                        coefs[k] += (short)sgn;
                        rest -= (order - k) * ((-sgn * dd) >> denShift);
                        if (rest >= 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
}
