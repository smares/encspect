// Reading derived from libspeex 1.2.1 (arch.h and fixed_generic.h: the fixed-point macros; math_approx.h: spx_sqrt, spx_exp, spx_cos, speex_rand), Copyright (c) 2002-2008 Jean-Marc Valin
// and Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;

namespace Encspect.Formats.Speex.Decoding;

// libspeex's fixed-point arithmetic. The C macros compute in int; the 16-bit ones (spx_word16_t) cut their operands to 16 bits where the macro says so, and a value stored in a
// spx_word16_t is cut by the store: the port keeps the same casts, so every intermediate result equals the C build's, which wraps on overflow.
internal static class Fixed
{
    public const int LpcScaling = 8192;
    public const int SigShift = 14;
    public const int GainShift = 6;
    public const int GainScaling = 64;
    public const int Q15One = 32767;

    // spx_word16_t -> int and back.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Extract16(int x) => (short)x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Shr(int a, int shift) => a >> shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Shl32(int a, int shift) => (int)((uint)a << shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Shl16(int a, int shift) => (short)((ushort)a << shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pshr(int a, int shift) => (a + ((1 << shift) >> 1)) >> shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Vshr32(int a, int shift) => shift > 0 ? a >> shift : Shl32(a, -shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Saturate(int x, int a) => x > a ? a : x < -a ? -a : x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Add16(int a, int b) => (short)((short)a + (short)b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sub16(int a, int b) => (short)a - (short)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16(int a, int b) => (short)a * (short)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_16(int a, int b) => (short)((short)a * (short)b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_Q13(int a, int b) => Mult16_16(a, b) >> 13;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_Q14(int a, int b) => Mult16_16(a, b) >> 14;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_Q15(int a, int b) => Mult16_16(a, b) >> 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_P13(int a, int b) => (4096 + Mult16_16(a, b)) >> 13;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_P14(int a, int b) => (8192 + Mult16_16(a, b)) >> 14;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_16_P15(int a, int b) => (16384 + Mult16_16(a, b)) >> 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_32_Q14(int a, int b) => Mult16_16(a, b >> 14) + ((Mult16_16(a, b & 0x3fff)) >> 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_32_Q15(int a, int b) => Mult16_16(a, b >> 15) + ((Mult16_16(a, b & 0x7fff)) >> 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mult16_32_P15(int a, int b) => Mult16_16(a, b >> 15) + Pshr(Mult16_16(a, b & 0x7fff), 15);

    // The divisions of the macros, which trap in the C build where the divisor is 0 or the quotient overflows: a stream that makes them do that is not decodable.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Divide(int a, int b)
    {
        if (b == 0 || (b == -1 && a == int.MinValue))
        {
            throw new InvalidDataException("The frame divides by zero in the decoder's arithmetic, which libspeex does not survive either");
        }

        return a / b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Div32_16(int a, int b) => (short)Divide(a, (short)b);

    // (a + (b >> 1)) / b with b an spx_word16_t, as the PDIV32_16 macro of fixed_generic.h.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pdiv32_16(int a, int b) => (short)Divide(a + ((short)b >> 1), (short)b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pdiv32(int a, int b) => Divide(a + ((short)b >> 1), b);

    // spx_ilog4 (math_approx.h).
    private static int Ilog4(uint x)
    {
        int r = 0;
        if (x >= 65536)
        {
            x >>= 16;
            r += 8;
        }

        if (x >= 256)
        {
            x >>= 8;
            r += 4;
        }

        if (x >= 16)
        {
            x >>= 4;
            r += 2;
        }

        if (x >= 4)
        {
            r += 1;
        }

        return r;
    }

    // sqrt(x) ~= 0.22178 + 1.29227 x - 0.77070 x^2 + 0.25659 x^3 for .25 < x < 1, scaled: spx_sqrt of math_approx.h (C0 3634, C1 21173, C2 -12627, C3 4204).
    public static int Sqrt(int x)
    {
        int k = Ilog4((uint)x) - 6;
        x = Vshr32(x, k << 1);
        int rt = Add16(3634, Mult16_16_Q14(x, Add16(21173, Mult16_16_Q14(x, Add16(-12627, Mult16_16_Q14(x, 4204))))));
        rt = Vshr32(rt, 7 - k);
        return (short)rt;
    }

    // spx_exp2 and spx_exp: input in Q11, output in Q16.
    public static int Exp(int x)
    {
        if (x > 21290)
        {
            return 0x7fffffff;
        }

        if (x < -21290)
        {
            return 0;
        }

        return Exp2((short)Mult16_16_P14(23637, x));
    }

    private static int Exp2(int x)
    {
        int integer = x >> 11;
        if (integer > 14)
        {
            return 0x7fffffff;
        }

        if (integer < -15)
        {
            return 0;
        }

        int frac = Shl16(x - Shl16(integer, 11), 3);
        frac = Add16(16384, Mult16_16_Q14(frac, Add16(11356, Mult16_16_Q14(frac, Add16(3726, Mult16_16_Q14(1301, frac))))));
        return Vshr32(frac, -integer - 2);
    }

    // spx_cos (K1 8192, K2 -4096, K3 340, K4 -10): the cosine of an angle in Q13 radians, as a Q13 value.
    public static int Cos(int x)
    {
        int x2;
        if (x < 12868)
        {
            x2 = (short)Mult16_16_P13(x, x);
            return (short)(8192 + Mult16_16_P13(x2, -4096 + Mult16_16_P13(x2, 340 + Mult16_16_P13(-10, x2))));
        }

        x = Sub16(25736, x);
        x2 = (short)Mult16_16_P13(x, x);
        return (short)(-8192 - Mult16_16_P13(x2, -4096 + Mult16_16_P13(x2, 340 + Mult16_16_P13(-10, x2))));
    }

    // speex_rand: the generator of comfort noise and of the vocoder's excitation; seed starts at 1000 in each decoder.
    public static int Rand(int std, ref uint seed)
    {
        seed = (1664525 * seed) + 1013904223;
        int res = Mult16_16((short)(seed >> 16), std);
        return (short)Pshr(res - (res >> 3), 14);
    }
}
