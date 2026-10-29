// Follows the Monkey's Audio SDK 13.44 (NNFilterAVX2.cpp, NNFilterAVX512.cpp, NNFilterNeon.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
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
// instruction set at hand, 512-bit only where the runtime prefers 512-bit vectors (as the SDK's NNFilterAVX2.cpp, NNFilterAVX512.cpp and NNFilterNeon.cpp do), the other loops the portable vectors.
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
        if (Avx512BW.IsSupported && Vector512.IsHardwareAccelerated)
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

        return total + Dot128(ref pa, ref pb, n, i);
    }

    // The 128-bit loop (or the portable one where there is none) from element i, and the elements after it. Separate so that a test reaches
    // it on a processor whose Dot takes the wider paths first.
    internal static int Dot128(ReadOnlySpan<short> a, ReadOnlySpan<short> b) =>
        Dot128(ref MemoryMarshal.GetReference(a), ref MemoryMarshal.GetReference(b), Math.Min(a.Length, b.Length), 0);

    private static int Dot128(ref short pa, ref short pb, int n, int i)
    {
        int total = 0;
        if (Sse2.IsSupported)
        {
            // Four independent sums, so the 128-bit loop (all there is under NativeAOT's x86-64-v2 baseline) is not bound by the
            // latency of one chain; sums wrap on 32 bits, so the order of the additions does not change the result.
            Vector128<int> sum0 = Vector128<int>.Zero;
            Vector128<int> sum1 = Vector128<int>.Zero;
            Vector128<int> sum2 = Vector128<int>.Zero;
            Vector128<int> sum3 = Vector128<int>.Zero;
            for (; i <= n - 32; i += 32)
            {
                sum0 += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)i), Vector128.LoadUnsafe(ref pb, (nuint)i));
                sum1 += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)(i + 8)), Vector128.LoadUnsafe(ref pb, (nuint)(i + 8)));
                sum2 += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)(i + 16)), Vector128.LoadUnsafe(ref pb, (nuint)(i + 16)));
                sum3 += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)(i + 24)), Vector128.LoadUnsafe(ref pb, (nuint)(i + 24)));
            }

            for (; i <= n - 8; i += 8)
            {
                sum0 += Sse2.MultiplyAddAdjacent(Vector128.LoadUnsafe(ref pa, (nuint)i), Vector128.LoadUnsafe(ref pb, (nuint)i));
            }

            total += Vector128.Sum((sum0 + sum1) + (sum2 + sum3));
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
            // Four independent sums, as in the 16-bit loop: under NativeAOT's x86-64-v2 baseline Vector<T> is 128 bits wide, and one
            // chain of widening adds bound the loop. The sums are 64 bits, so the order of the additions does not change the result.
            int step = Vector<int>.Count;
            Vector<long> sum0 = Vector<long>.Zero;
            Vector<long> sum1 = Vector<long>.Zero;
            Vector<long> sum2 = Vector<long>.Zero;
            Vector<long> sum3 = Vector<long>.Zero;
            for (; i <= n - (4 * step); i += 4 * step)
            {
                sum0 += Products(ref pa, ref pb, i);
                sum1 += Products(ref pa, ref pb, i + step);
                sum2 += Products(ref pa, ref pb, i + (2 * step));
                sum3 += Products(ref pa, ref pb, i + (3 * step));
            }

            for (; i <= n - step; i += step)
            {
                sum0 += Products(ref pa, ref pb, i);
            }

            total += Vector.Sum((sum0 + sum1) + (sum2 + sum3));
        }

        for (; i < n; i++)
        {
            total += Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
        }

        return total;
    }

    // The products of a vector of elements, wrapped to 32 bits, as 64-bit sums of two halves.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<long> Products(ref int pa, ref int pb, int i)
    {
        Vector<int> product = Vector.LoadUnsafe(ref pa, (nuint)i) * Vector.LoadUnsafe(ref pb, (nuint)i);
        Vector.Widen(product, out Vector<long> low, out Vector<long> high);
        return low + high;
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
        }

        AddOrSubtract128(ref pw, ref pd, n, i, isSubtract);
    }

    internal static void AddOrSubtract128(Span<short> weights, ReadOnlySpan<short> delta, bool isSubtract) =>
        AddOrSubtract128(ref MemoryMarshal.GetReference(weights), ref MemoryMarshal.GetReference(delta), Math.Min(weights.Length, delta.Length), 0, isSubtract);

    private static void AddOrSubtract128(ref short pw, ref short pd, int n, int i, bool isSubtract)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            // The direction is tested once, not in the loop, and the loop takes two vectors a step.
            if (isSubtract)
            {
                for (; i <= n - 16; i += 16)
                {
                    (Vector128.LoadUnsafe(ref pw, (nuint)i) - Vector128.LoadUnsafe(ref pd, (nuint)i)).StoreUnsafe(ref pw, (nuint)i);
                    (Vector128.LoadUnsafe(ref pw, (nuint)(i + 8)) - Vector128.LoadUnsafe(ref pd, (nuint)(i + 8))).StoreUnsafe(ref pw, (nuint)(i + 8));
                }
            }
            else
            {
                for (; i <= n - 16; i += 16)
                {
                    (Vector128.LoadUnsafe(ref pw, (nuint)i) + Vector128.LoadUnsafe(ref pd, (nuint)i)).StoreUnsafe(ref pw, (nuint)i);
                    (Vector128.LoadUnsafe(ref pw, (nuint)(i + 8)) + Vector128.LoadUnsafe(ref pd, (nuint)(i + 8))).StoreUnsafe(ref pw, (nuint)(i + 8));
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
        }

        AddOrSubtract128(ref pw, ref pd, n, i, isSubtract);
    }

    internal static void AddOrSubtract128(Span<int> weights, ReadOnlySpan<int> delta, bool isSubtract) =>
        AddOrSubtract128(ref MemoryMarshal.GetReference(weights), ref MemoryMarshal.GetReference(delta), Math.Min(weights.Length, delta.Length), 0, isSubtract);

    private static void AddOrSubtract128(ref int pw, ref int pd, int n, int i, bool isSubtract)
    {
        if (Vector128.IsHardwareAccelerated)
        {
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
