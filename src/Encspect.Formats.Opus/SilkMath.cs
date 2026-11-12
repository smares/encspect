using System.Numerics;
using System.Runtime.CompilerServices;

namespace Encspect.Formats.Opus;

// The fixed-point operations of SILK (silk/macros.h, SigProc_FIX.h and Inlines.h of libopus 1.5.2, BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md), with the 64-bit
// definitions the reference uses on 64-bit machines. Integers wrap as C's do where the reference does not saturate.
internal static class SilkMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwb(int a, int b) => (int)(((long)a * (short)b) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlawb(int a, int b, int c) => unchecked(a + Smulwb(b, c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulww(int a, int b) => (int)(((long)a * b) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlaww(int a, int b, int c) => unchecked(a + (int)(((long)b * c) >> 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulwt(int a, int b) => (int)(((long)a * (b >> 16)) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulbb(int a, int b) => (short)a * (short)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smlabb(int a, int b, int c) => unchecked(a + ((short)b * (short)c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulbt(int a, int b) => (short)a * (b >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smulltt(int a, int b) => (a >> 16) * (b >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Smmul(int a, int b) => (int)(((long)a * b) >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RshiftRound(int a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RshiftRound64(long a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sat16(int a) => a > short.MaxValue ? short.MaxValue : a < short.MinValue ? short.MinValue : a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddSat32(int a, int b)
    {
        long sum = (long)a + b;
        return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SubSat32(int a, int b)
    {
        long diff = (long)a - b;
        return diff > int.MaxValue ? int.MaxValue : diff < int.MinValue ? int.MinValue : (int)diff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LshiftSat32(int a, int shift)
    {
        int low = int.MinValue >> shift;
        int high = int.MaxValue >> shift;
        int limited = a > high ? high : a < low ? low : a;
        return limited << shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Limit(int a, int limit1, int limit2) => limit1 > limit2 ? (a > limit1 ? limit1 : a < limit2 ? limit2 : a) : (a > limit2 ? limit2 : a < limit1 ? limit1 : a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clz32(int value) => BitOperations.LeadingZeroCount((uint)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rand(int seed) => unchecked(907633515 + (seed * 196314165));

    public static int Ror32(int a, int rot)
    {
        uint x = (uint)a;
        return rot == 0 ? a : rot < 0 ? (int)(x << -rot) | (int)(x >> (32 + rot)) : (int)(x << (32 - rot)) | (int)(x >> rot);
    }

    public static void ClzFrac(int input, out int lz, out int fracQ7)
    {
        int lzeros = Clz32(input);
        lz = lzeros;
        fracQ7 = Ror32(input, 24 - lzeros) & 0x7f;
    }

    public static int SqrtApprox(int x)
    {
        if (x <= 0)
        {
            return 0;
        }

        ClzFrac(x, out int lz, out int fracQ7);
        int y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= lz >> 1;
        return Smlawb(y, y, Smulbb(213, fracQ7));
    }

    public static int Div32VarQ(int a32, int b32, int qRes)
    {
        int aHeadrm = Clz32(Math.Abs(a32)) - 1;
        int a32Nrm = a32 << aHeadrm;
        int bHeadrm = Clz32(Math.Abs(b32)) - 1;
        int b32Nrm = b32 << bHeadrm;
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = Smulwb(a32Nrm, b32Inv);
        a32Nrm = unchecked(a32Nrm - (Smmul(b32Nrm, result) << 3));
        result = Smlawb(result, a32Nrm, b32Inv);
        int lshift = 29 + aHeadrm - bHeadrm - qRes;
        if (lshift < 0)
        {
            return LshiftSat32(result, -lshift);
        }

        return lshift < 32 ? result >> lshift : 0;
    }

    public static int Inverse32VarQ(int b32, int qRes)
    {
        int bHeadrm = Clz32(Math.Abs(b32)) - 1;
        int b32Nrm = b32 << bHeadrm;
        int b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        int result = b32Inv << 16;
        int errQ32 = ((1 << 29) - Smulwb(b32Nrm, b32Inv)) << 3;
        result = Smlaww(result, errQ32, b32Inv);
        int lshift = 61 - bHeadrm - qRes;
        if (lshift <= 0)
        {
            return LshiftSat32(result, -lshift);
        }

        return lshift < 32 ? result >> lshift : 0;
    }

    // silk_log2lin: 2^(x / 128) with x in Q7.
    public static int Log2Lin(int inLogQ7)
    {
        if (inLogQ7 < 0)
        {
            return 0;
        }

        if (inLogQ7 >= 3967)
        {
            return int.MaxValue;
        }

        int output = 1 << (inLogQ7 >> 7);
        int fracQ7 = inLogQ7 & 0x7F;
        if (inLogQ7 < 2048)
        {
            output = unchecked(output + ((output * Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174)) >> 7));
        }
        else
        {
            output = unchecked(output + ((output >> 7) * Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174)));
        }

        return output;
    }

    // silk_sum_sqr_shift: the energy of x with the shift that keeps it in 32 bits.
    public static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> x)
    {
        int len = x.Length;
        int shft = 31 - Clz32(len);
        int nrg = len;
        int i;
        for (i = 0; i < len - 1; i += 2)
        {
            uint nrgTmp = (uint)Smulbb(x[i], x[i]);
            nrgTmp = unchecked(nrgTmp + (uint)Smulbb(x[i + 1], x[i + 1]));
            nrg = unchecked((int)((uint)nrg + (nrgTmp >> shft)));
        }

        if (i < len)
        {
            uint nrgTmp = (uint)Smulbb(x[i], x[i]);
            nrg = unchecked((int)((uint)nrg + (nrgTmp >> shft)));
        }

        shft = Math.Max(0, shft + 3 - Clz32(nrg));
        nrg = 0;
        for (i = 0; i < len - 1; i += 2)
        {
            uint nrgTmp = (uint)Smulbb(x[i], x[i]);
            nrgTmp = unchecked(nrgTmp + (uint)Smulbb(x[i + 1], x[i + 1]));
            nrg = unchecked((int)((uint)nrg + (nrgTmp >> shft)));
        }

        if (i < len)
        {
            uint nrgTmp = (uint)Smulbb(x[i], x[i]);
            nrg = unchecked((int)((uint)nrg + (nrgTmp >> shft)));
        }

        shift = shft;
        energy = nrg;
    }
}
