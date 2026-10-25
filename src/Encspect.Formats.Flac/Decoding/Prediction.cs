using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Encspect.Formats.Flac.Decoding;

// Turns the warm-up samples and residual a walk stored back into samples (RFC 9639, section 9.2). Arithmetic wraps, so a stream that breaks
// the bounds of its bit depth decodes to something and its MD5 says it differs.
internal static class Prediction
{
    // d[0, order) are the warm-up samples and d[order, n) the residual; on return all of d[0, n) are samples.
    public static void RestoreFixed(Span<int> d, int order, int n)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < n; i++)
                {
                    d[i] += d[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < n; i++)
                {
                    d[i] += (2 * d[i - 1]) - d[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < n; i++)
                {
                    d[i] += (3 * (d[i - 1] - d[i - 2])) + d[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < n; i++)
                {
                    d[i] += (4 * (d[i - 1] + d[i - 3])) - (6 * d[i - 2]) - d[i - 4];
                }

                break;
        }
    }

    public static void RestoreFixed(Span<long> d, int order, int n)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < n; i++)
                {
                    d[i] += d[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < n; i++)
                {
                    d[i] += (2 * d[i - 1]) - d[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < n; i++)
                {
                    d[i] += (3 * (d[i - 1] - d[i - 2])) + d[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < n; i++)
                {
                    d[i] += (4 * (d[i - 1] + d[i - 3])) - (6 * d[i - 2]) - d[i - 4];
                }

                break;
        }
    }

    // Whether the prediction sum of this subframe fits 32 bits: the rule libFLAC uses to pick its narrow path.
    public static bool FitsInt(int sampleBits, int precision, int order) => sampleBits + precision + (32 - BitOperations.LeadingZeroCount((uint)(order - 1))) <= 32;

    // The coefficients of a FLAC subframe number at most this many (RFC 9639, section 9.2.8).
    private const int MaxOrder = 32;

    // sum over j of coefficients[j] * d[i - 1 - j] is the dot product of the coefficients in reverse with the window d[i - order, i), which
    // is contiguous in memory and so can run through vectors; the sums wrap on 32 bits where the subframe fits them and are exact in 64
    // bits otherwise, in either order of summation.
    public static void RestoreLpc(Span<int> d, ReadOnlySpan<int> coefficients, int order, int shift, int n, bool isNarrow)
    {
        if (order is < 1 or > MaxOrder || coefficients.Length < order || n > d.Length)
        {
            RestoreLpcPlain(d, coefficients, order, shift, n, isNarrow);
            return;
        }

        CoefficientBuffer buffer = default;
        Span<int> reversed = buffer;
        for (int j = 0; j < order; j++)
        {
            reversed[j] = coefficients[order - 1 - j];
        }

        ref int c = ref MemoryMarshal.GetReference(reversed);
        ref int origin = ref MemoryMarshal.GetReference(d);
        if (isNarrow)
        {
            for (int i = order; i < n; i++)
            {
                Unsafe.Add(ref origin, i) += DotNarrow(ref Unsafe.Add(ref origin, i - order), ref c, order) >> shift;
            }

            return;
        }

        for (int i = order; i < n; i++)
        {
            Unsafe.Add(ref origin, i) += (int)(DotWide(ref Unsafe.Add(ref origin, i - order), ref c, order) >> shift);
        }
    }

    // The newest samples of the window were stored by the last iterations, and a vector load that overlaps them waits for the stores to
    // retire (a stall longer than the multiplications it feeds); so the vectors take the oldest taps in groups of eight, and the newest
    // four to eleven are scalar.
    [InlineArray(MaxOrder)]
    private struct CoefficientBuffer
    {
        private int _element;
    }

    private static int VectorTaps(int order) => order >= 12 ? (order - 4) & ~7 : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DotNarrow(ref int x, ref int c, int order)
    {
        int j = 0;
        int sum = 0;
        int vectorTaps = VectorTaps(order);
        if (vectorTaps > 0 && Vector256.IsHardwareAccelerated)
        {
            Vector256<int> acc = Vector256<int>.Zero;
            for (; j < vectorTaps; j += 8)
            {
                acc += Vector256.LoadUnsafe(ref x, (nuint)j) * Vector256.LoadUnsafe(ref c, (nuint)j);
            }

            sum = Vector256.Sum(acc);
        }

        for (; j < order; j++)
        {
            sum += Unsafe.Add(ref c, j) * Unsafe.Add(ref x, j);
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DotWide(ref int x, ref int c, int order)
    {
        int j = 0;
        long sum = 0;
        int vectorTaps = VectorTaps(order);
        if (vectorTaps > 0 && Avx2.IsSupported)
        {
            Vector256<long> acc = Vector256<long>.Zero;
            for (; j < vectorTaps; j += 8)
            {
                Vector256<int> a = Vector256.LoadUnsafe(ref x, (nuint)j);
                Vector256<int> b = Vector256.LoadUnsafe(ref c, (nuint)j);
                acc += Avx2.Multiply(a, b);
                acc += Avx2.Multiply(Vector256.ShiftRightLogical(a.AsInt64(), 32).AsInt32(), Vector256.ShiftRightLogical(b.AsInt64(), 32).AsInt32());
            }

            sum = Vector256.Sum(acc);
        }
        else if (vectorTaps > 0 && AdvSimd.IsSupported)
        {
            Vector128<long> acc = Vector128<long>.Zero;
            for (; j < vectorTaps; j += 4)
            {
                Vector128<int> a = Vector128.LoadUnsafe(ref x, (nuint)j);
                Vector128<int> b = Vector128.LoadUnsafe(ref c, (nuint)j);
                acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, a.GetLower(), b.GetLower());
                acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, a, b);
            }

            sum = Vector128.Sum(acc);
        }

        for (; j < order; j++)
        {
            sum += (long)Unsafe.Add(ref c, j) * Unsafe.Add(ref x, j);
        }

        return sum;
    }

    private static void RestoreLpcPlain(Span<int> d, ReadOnlySpan<int> coefficients, int order, int shift, int n, bool isNarrow)
    {
        if (isNarrow)
        {
            for (int i = order; i < n; i++)
            {
                int sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += coefficients[j] * d[i - 1 - j];
                }

                d[i] += sum >> shift;
            }

            return;
        }

        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * d[i - 1 - j];
            }

            d[i] += (int)(sum >> shift);
        }
    }

    public static void RestoreLpc(Span<long> d, ReadOnlySpan<int> coefficients, int order, int shift, int n)
    {
        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += coefficients[j] * d[i - 1 - j];
            }

            d[i] += sum >> shift;
        }
    }
}
