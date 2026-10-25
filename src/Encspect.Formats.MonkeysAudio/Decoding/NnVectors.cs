using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The two loops of the neural network filters, over the filter's order: the dot product of history and weights, and the weights' step along
// the sign of the coded value. All integer, so every path gives the same result: sums wrap on 32 bits (16-bit filters) or run in 64 bits over
// products of 32 bits (32-bit filters), as the SDK's do. The 16-bit dot product uses the multiply-and-add-adjacent-pairs instruction of the
// instruction set at hand (as the SDK's NNFilterAVX2.cpp, NNFilterAVX512.cpp and NNFilterNeon.cpp do), the other loops the portable vectors.
// The spans are as long as the order; the loops read and write inside them.
internal static class NnVectors
{
    public static int Dot(ReadOnlySpan<short> a, ReadOnlySpan<short> b)
    {
        int n = Math.Min(a.Length, b.Length);
        ref short pa = ref MemoryMarshal.GetReference(a);
        ref short pb = ref MemoryMarshal.GetReference(b);
        int total = 0;
        int i = 0;
        if (Avx512BW.IsSupported)
        {
            Vector512<int> sum = Vector512<int>.Zero;
            for (; i <= n - 32; i += 32)
            {
                sum += Avx512BW.MultiplyAddAdjacent(Vector512.LoadUnsafe(ref pa, (nuint)i), Vector512.LoadUnsafe(ref pb, (nuint)i));
            }

            total += Vector512.Sum(sum);
        }

        if (Avx2.IsSupported)
        {
            Vector256<int> sum = Vector256<int>.Zero;
            for (; i <= n - 16; i += 16)
            {
                sum += Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref pa, (nuint)i), Vector256.LoadUnsafe(ref pb, (nuint)i));
            }

            total += Vector256.Sum(sum);
        }

        if (Sse2.IsSupported)
        {
            Vector128<int> sum = Vector128<int>.Zero;
            for (; i <= n - 8; i += 8)
            {
                sum += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)i), Vector128.LoadUnsafe(ref pb, (nuint)i));
            }

            total += Vector128.Sum(sum);
        }
        else if (AdvSimd.IsSupported)
        {
            Vector128<int> sum = Vector128<int>.Zero;
            for (; i <= n - 8; i += 8)
            {
                Vector128<short> x = Vector128.LoadUnsafe(ref pa, (nuint)i);
                Vector128<short> y = Vector128.LoadUnsafe(ref pb, (nuint)i);
                sum = AdvSimd.MultiplyWideningLowerAndAdd(sum, x.GetLower(), y.GetLower());
                sum = AdvSimd.MultiplyWideningUpperAndAdd(sum, x, y);
            }

            total += Vector128.Sum(sum);
        }
        else if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<short>.Count;
            var sum = Vector<int>.Zero;
            for (; i <= n - step; i += step)
            {
                Vector.Widen(Vector.LoadUnsafe(ref pa, (nuint)i), out Vector<int> a0, out Vector<int> a1);
                Vector.Widen(Vector.LoadUnsafe(ref pb, (nuint)i), out Vector<int> b0, out Vector<int> b1);
                sum += (a0 * b0) + (a1 * b1);
            }

            total += Vector.Sum(sum);
        }

        for (; i < n; i++)
        {
            total += Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
        }

        return total;
    }

    public static long Dot(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        int n = Math.Min(a.Length, b.Length);
        ref int pa = ref MemoryMarshal.GetReference(a);
        ref int pb = ref MemoryMarshal.GetReference(b);
        long total = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int step = Vector<int>.Count;
            var sum = Vector<long>.Zero;
            for (; i <= n - step; i += step)
            {
                Vector<int> product = Vector.LoadUnsafe(ref pa, (nuint)i) * Vector.LoadUnsafe(ref pb, (nuint)i);
                Vector.Widen(product, out Vector<long> low, out Vector<long> high);
                sum += low + high;
            }

            total += Vector.Sum(sum);
        }

        for (; i < n; i++)
        {
            total += Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
        }

        return total;
    }

    // weights[i] += delta[i], or -= with isSubtract.
    public static void AddOrSubtract(Span<short> weights, ReadOnlySpan<short> delta, bool isSubtract)
    {
        int n = Math.Min(weights.Length, delta.Length);
        ref short pw = ref MemoryMarshal.GetReference(weights);
        ref short pd = ref MemoryMarshal.GetReference(delta);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            if (Vector512.IsHardwareAccelerated)
            {
                for (; i <= n - 32; i += 32)
                {
                    Vector512<short> w = Vector512.LoadUnsafe(ref pw, (nuint)i);
                    Vector512<short> d = Vector512.LoadUnsafe(ref pd, (nuint)i);
                    (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
                }
            }

            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= n - 16; i += 16)
                {
                    Vector256<short> w = Vector256.LoadUnsafe(ref pw, (nuint)i);
                    Vector256<short> d = Vector256.LoadUnsafe(ref pd, (nuint)i);
                    (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
                }
            }

            for (; i <= n - 8; i += 8)
            {
                Vector128<short> w = Vector128.LoadUnsafe(ref pw, (nuint)i);
                Vector128<short> d = Vector128.LoadUnsafe(ref pd, (nuint)i);
                (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            Unsafe.Add(ref pw, i) += (short)(isSubtract ? -Unsafe.Add(ref pd, i) : Unsafe.Add(ref pd, i));
        }
    }

    public static void AddOrSubtract(Span<int> weights, ReadOnlySpan<int> delta, bool isSubtract)
    {
        int n = Math.Min(weights.Length, delta.Length);
        ref int pw = ref MemoryMarshal.GetReference(weights);
        ref int pd = ref MemoryMarshal.GetReference(delta);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            if (Vector512.IsHardwareAccelerated)
            {
                for (; i <= n - 16; i += 16)
                {
                    Vector512<int> w = Vector512.LoadUnsafe(ref pw, (nuint)i);
                    Vector512<int> d = Vector512.LoadUnsafe(ref pd, (nuint)i);
                    (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
                }
            }

            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= n - 8; i += 8)
                {
                    Vector256<int> w = Vector256.LoadUnsafe(ref pw, (nuint)i);
                    Vector256<int> d = Vector256.LoadUnsafe(ref pd, (nuint)i);
                    (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
                }
            }

            for (; i <= n - 4; i += 4)
            {
                Vector128<int> w = Vector128.LoadUnsafe(ref pw, (nuint)i);
                Vector128<int> d = Vector128.LoadUnsafe(ref pd, (nuint)i);
                (isSubtract ? w - d : w + d).StoreUnsafe(ref pw, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            Unsafe.Add(ref pw, i) += isSubtract ? -Unsafe.Add(ref pd, i) : Unsafe.Add(ref pd, i);
        }
    }
}
