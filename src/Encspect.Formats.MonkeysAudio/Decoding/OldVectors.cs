// Follows the Monkey's Audio SDK 13.44 (Old/AntiPredictorHighOld.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The loop of the filter of 16 taps of CAntiPredictorHigh3800To3929 over 32-bit values: the dot product of the last 16 values with the weights, and the weights' step
// along the signs of those values, in the direction of the coded value's sign. All integer and wrapping on 32 bits, so every path gives the same result.
internal static class OldVectors
{
    public const int Taps = 16;

    // Returns the dot product of values[0..16) and weights, then adds the sign of each value (−1 for zero or above, 1 below) to its weight where direction is 1, subtracts
    // it where direction is −1, and leaves the weights where it is 0.
    public static int HighTaps(ReadOnlySpan<int> values, Span<int> weights, int direction)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            return HighTaps256(values, weights, direction);
        }

        return Vector128.IsHardwareAccelerated ? HighTaps128(values, weights, direction) : HighTapsScalar(values, weights, direction);
    }

    internal static int HighTaps256(ReadOnlySpan<int> values, Span<int> weights, int direction)
    {
        ref int pv = ref MemoryMarshal.GetReference(values);
        ref int pw = ref MemoryMarshal.GetReference(weights);
        if (values.Length < Taps || weights.Length < Taps)
        {
            throw new ArgumentException("Sixteen taps are needed");
        }

        Vector256<int> v0 = Vector256.LoadUnsafe(ref pv, 0);
        Vector256<int> v1 = Vector256.LoadUnsafe(ref pv, 8);
        Vector256<int> w0 = Vector256.LoadUnsafe(ref pw, 0);
        Vector256<int> w1 = Vector256.LoadUnsafe(ref pw, 8);
        int dot = Vector256.Sum((v0 * w0) + (v1 * w1));
        Vector256<int> one = Vector256.Create(1);
        Vector256<int> two = Vector256.Create(2);
        Vector256<int> neg = Vector256.Create(direction >> 31);
        Vector256<int> nz = Vector256.Create(direction | -direction) >> 31;
        Vector256<int> s0 = (((v0 >> 30) & two) - one);
        Vector256<int> s1 = (((v1 >> 30) & two) - one);
        ((w0 + (((s0 ^ neg) - neg) & nz))).StoreUnsafe(ref pw, 0);
        ((w1 + (((s1 ^ neg) - neg) & nz))).StoreUnsafe(ref pw, 8);
        return dot;
    }

    internal static int HighTaps128(ReadOnlySpan<int> values, Span<int> weights, int direction)
    {
        ref int pv = ref MemoryMarshal.GetReference(values);
        ref int pw = ref MemoryMarshal.GetReference(weights);
        if (values.Length < Taps || weights.Length < Taps)
        {
            throw new ArgumentException("Sixteen taps are needed");
        }

        Vector128<int> one = Vector128.Create(1);
        Vector128<int> two = Vector128.Create(2);
        Vector128<int> neg = Vector128.Create(direction >> 31);
        Vector128<int> nz = Vector128.Create(direction | -direction) >> 31;
        Vector128<int> sum = Vector128<int>.Zero;
        for (nuint i = 0; i < Taps; i += 4)
        {
            Vector128<int> v = Vector128.LoadUnsafe(ref pv, i);
            Vector128<int> w = Vector128.LoadUnsafe(ref pw, i);
            sum += v * w;
            Vector128<int> s = ((v >> 30) & two) - one;
            (w + (((s ^ neg) - neg) & nz)).StoreUnsafe(ref pw, i);
        }

        return Vector128.Sum(sum);
    }

    internal static int HighTapsScalar(ReadOnlySpan<int> values, Span<int> weights, int direction)
    {
        int dot = 0;
        for (int j = 0; j < Taps; j++)
        {
            int value = values[j];
            dot += value * weights[j];
            int step = ((value >> 30) & 2) - 1;
            if (direction > 0)
            {
                weights[j] += step;
            }
            else if (direction < 0)
            {
                weights[j] -= step;
            }
        }

        return dot;
    }
}
