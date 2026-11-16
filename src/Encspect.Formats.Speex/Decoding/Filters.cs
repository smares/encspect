// Reading derived from libspeex 1.2.1 (filters.c: bw_lpc, sanitize_values32, highpass, signal_mul, compute_rms16, iir_mem16, qmf_synth, interp_pitch, multicomb; ltp.c: inner_prod),
// Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license. The license text is in
// THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using static Encspect.Formats.Speex.Decoding.Fixed;

namespace Encspect.Formats.Speex.Decoding;

// The decoder's filters in libspeex's fixed-point arithmetic. A spx_word16_t buffer is a span of short and a spx_mem_t or spx_word32_t one of int; a pointer into the middle of a
// buffer is a span from there, with the history before it reached by an offset.
internal static class Filters
{
    private static readonly short[][] HighpassDenominator =
    [
        [16384, -31313, 14991], [16384, -31569, 15249], [16384, -31677, 15328], [16384, -32313, 15947], [16384, -22446, 6537],
    ];

    private static readonly short[][] HighpassNumerator =
    [
        [15672, -31344, 15672], [15802, -31601, 15802], [15847, -31694, 15847], [16162, -32322, 16162], [14418, -28836, 14418],
    ];

    // The fractional-delay filters of interp_pitch (the fixed-point branch of filters.c's shift_filt).
    private static readonly short[][] ShiftFilter =
    [
        [-390, 1540, -4993, 20123, 20123, -4993, 1540],
        [-1064, 2817, -6694, 31589, 6837, -990, -209],
        [-209, -990, 6837, 31589, -6694, 2817, -1064],
    ];

    // The 64-tap quadrature mirror filter of sb_celp.c (h0).
    public static ReadOnlySpan<short> Qmf =>
    [
        2, -7, -7, 18, 15, -39, -25, 75, 35, -130, -41, 212, 38, -327, -17, 483, -32, -689, 124, 956, -283, -1307, 543, 1780, -973, -2467, 1733, 3633, -3339, -6409, 9059, 30153,
        30153, 9059, -6409, -3339, 3633, 1733, -2467, -973, 1780, 543, -1307, -283, 956, 124, -689, -32, 483, -17, -327, 38, 212, -41, -130, 35, 75, -25, -39, 15, 18, -7, -7, 2,
    ];

    public static void BandwidthLpc(int gamma, ReadOnlySpan<short> input, Span<short> output, int order)
    {
        int tmp = gamma;
        for (int i = 0; i < order; i++)
        {
            output[i] = (short)Mult16_16_P15(tmp, input[i]);
            tmp = (short)Mult16_16_P15(tmp, gamma);
        }
    }

    public static void Sanitize(Span<int> vector, int min, int max)
    {
        for (int i = 0; i < vector.Length; i++)
        {
            if (vector[i] < min)
            {
                vector[i] = min;
            }
            else if (vector[i] > max)
            {
                vector[i] = max;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Highpass(ReadOnlySpan<short> x, Span<short> y, int len, int filterId, Span<int> mem)
    {
        if (filterId > 4)
        {
            filterId = 4;
        }

        short[] den = HighpassDenominator[filterId];
        short[] num = HighpassNumerator[filterId];
        for (int i = 0; i < len; i++)
        {
            int xi = x[i];
            int vout = Mult16_16(num[0], xi) + mem[0];
            int yi = (short)Saturate(Pshr(vout, 14), 32767);
            mem[0] = mem[1] + Mult16_16(num[1], xi) + Shl32(Mult16_32_Q15(-den[1], vout), 1);
            mem[1] = Mult16_16(num[2], xi) + Shl32(Mult16_32_Q15(-den[2], vout), 1);
            y[i] = (short)yi;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void SignalMultiply(ReadOnlySpan<int> x, Span<int> y, int scale, int len)
    {
        for (int i = 0; i < len; i++)
        {
            y[i] = Shl32(Mult16_32_Q14((short)(x[i] >> 7), scale), 7);
        }
    }

    // compute_rms16.
    public static int Rms16(ReadOnlySpan<short> x, int len)
    {
        short max = 10;
        for (int i = 0; i < len; i++)
        {
            int tmp = x[i];
            if (tmp < 0)
            {
                tmp = -tmp;
            }

            if (tmp > max)
            {
                max = (short)tmp;
            }
        }

        if (max > 16383)
        {
            int sum = 0;
            for (int i = 0; i < len; i += 4)
            {
                int sum2 = 0;
                for (int j = 0; j < 4; j++)
                {
                    int half = x[i + j] >> 1;
                    sum2 += Mult16_16(half, half);
                }

                sum += sum2 >> 6;
            }

            return Shl16(Sqrt(Divide(sum, len)), 4);
        }
        else
        {
            int sum = 0;
            int shift = 0;
            if (max < 8192)
            {
                shift = 1;
            }

            if (max < 4096)
            {
                shift = 2;
            }

            if (max < 2048)
            {
                shift = 3;
            }

            for (int i = 0; i < len; i += 4)
            {
                int sum2 = 0;
                for (int j = 0; j < 4; j++)
                {
                    int v = Shl16(x[i + j], shift);
                    sum2 += Mult16_16(v, v);
                }

                sum += sum2 >> 6;
            }

            return Shl16(Sqrt(Divide(sum, len)), 3 - shift);
        }
    }

    // iir_mem16: x and y may be the same span.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void IirMem16(ReadOnlySpan<short> x, ReadOnlySpan<short> den, Span<short> y, int n, int order, Span<int> mem)
    {
        for (int i = 0; i < n; i++)
        {
            int yi = (short)Saturate(x[i] + Pshr(mem[0], 13), 32767);
            int nyi = -yi;
            for (int j = 0; j < order - 1; j++)
            {
                mem[j] = mem[j + 1] + Mult16_16(den[j], nyi);
            }

            mem[order - 1] = Mult16_16(den[order - 1], nyi);
            y[i] = (short)yi;
        }
    }

    // iir_mem16 over the same buffer for input and output.
    public static void IirMem16(Span<short> xy, ReadOnlySpan<short> den, int n, int order, Span<int> mem) => IirMem16(xy, den, xy, n, order, mem);

    // qmf_synth: x1 and x2 are the low and the high band of n / 2 samples each, y the n output samples; y may be x1.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void QmfSynth(ReadOnlySpan<short> x1, ReadOnlySpan<short> x2, ReadOnlySpan<short> a, Span<short> y, int n, int m, Span<short> mem1, Span<short> mem2, Span<short> work)
    {
        int m2 = m >> 1;
        int n2 = n >> 1;
        Span<short> xx1 = work.Slice(0, m2 + n2);
        Span<short> xx2 = work.Slice(m2 + n2, m2 + n2);
        for (int i = 0; i < n2; i++)
        {
            xx1[i] = x1[n2 - 1 - i];
        }

        for (int i = 0; i < m2; i++)
        {
            xx1[n2 + i] = mem1[(2 * i) + 1];
        }

        for (int i = 0; i < n2; i++)
        {
            xx2[i] = x2[n2 - 1 - i];
        }

        for (int i = 0; i < m2; i++)
        {
            xx2[n2 + i] = mem2[(2 * i) + 1];
        }

        for (int i = 0; i < n2; i += 2)
        {
            int y0 = 0;
            int y1 = 0;
            int y2 = 0;
            int y3 = 0;
            int x10 = xx1[n2 - 2 - i];
            int x20 = xx2[n2 - 2 - i];
            for (int j = 0; j < m2; j += 2)
            {
                int a0 = a[2 * j];
                int a1 = a[(2 * j) + 1];
                int x11 = xx1[n2 - 1 + j - i];
                int x21 = xx2[n2 - 1 + j - i];
                y0 += Mult16_16(a0, x11) + Mult16_16(-a0, x21);
                y1 += Mult16_16(a1, x11) + Mult16_16(a1, x21);
                y2 += Mult16_16(a0, x10) + Mult16_16(-a0, x20);
                y3 += Mult16_16(a1, x10) + Mult16_16(a1, x20);
                a0 = a[(2 * j) + 2];
                a1 = a[(2 * j) + 3];
                x10 = xx1[n2 + j - i];
                x20 = xx2[n2 + j - i];
                y0 += Mult16_16(a0, x10) + Mult16_16(-a0, x20);
                y1 += Mult16_16(a1, x10) + Mult16_16(a1, x20);
                y2 += Mult16_16(a0, x11) + Mult16_16(-a0, x21);
                y3 += Mult16_16(a1, x11) + Mult16_16(a1, x21);
            }

            y[2 * i] = (short)Saturate(Pshr(y0, 15), 32767);
            y[(2 * i) + 1] = (short)Saturate(Pshr(y1, 15), 32767);
            y[(2 * i) + 2] = (short)Saturate(Pshr(y2, 15), 32767);
            y[(2 * i) + 3] = (short)Saturate(Pshr(y3, 15), 32767);
        }

        for (int i = 0; i < m2; i++)
        {
            mem1[(2 * i) + 1] = xx1[i];
        }

        for (int i = 0; i < m2; i++)
        {
            mem2[(2 * i) + 1] = xx2[i];
        }
    }

    // inner_prod of ltp.c: the sums of four products are shifted right by 6 before they are added.
    public static int InnerProduct(ReadOnlySpan<short> x, ReadOnlySpan<short> y, int len)
    {
        int sum = 0;
        for (int i = 0; i < (len >> 2) * 4; i += 4)
        {
            int part = Mult16_16(x[i], y[i]) + Mult16_16(x[i + 1], y[i + 1]) + Mult16_16(x[i + 2], y[i + 2]) + Mult16_16(x[i + 3], y[i + 3]);
            sum += part >> 6;
        }

        return sum;
    }

    // interp_pitch: the excitation at exc[at] and the len samples after it against the pitch lag; buf holds the history before at. Returns the lag found.
    private static int InterpolatePitch(Span<short> buf, int at, Span<short> interp, int pitch, int len)
    {
        Span<int> corr0 = stackalloc int[7];
        Span<int> corr1 = stackalloc int[7];
        Span<int> corr2 = stackalloc int[7];
        Span<int> corr3 = stackalloc int[7];
        for (int i = 0; i < 7; i++)
        {
            corr0[i] = InnerProduct(buf.Slice(at, len), buf.Slice(at - pitch - 3 + i, len), len);
        }

        for (int i = 0; i < 3; i++)
        {
            Span<int> corr = i == 0 ? corr1 : i == 1 ? corr2 : corr3;
            for (int j = 0; j < 7; j++)
            {
                int i1 = 3 - j;
                if (i1 < 0)
                {
                    i1 = 0;
                }

                int i2 = 10 - j;
                if (i2 > 7)
                {
                    i2 = 7;
                }

                int tmp = 0;
                for (int k = i1; k < i2; k++)
                {
                    tmp += Mult16_32_Q15(ShiftFilter[i][k], corr0[j + k - 3]);
                }

                corr[j] = tmp;
            }
        }

        int maxi = 0;
        int maxj = 0;
        int maxcorr = corr0[0];
        for (int i = 0; i < 4; i++)
        {
            Span<int> corr = i == 0 ? corr0 : i == 1 ? corr1 : i == 2 ? corr2 : corr3;
            for (int j = 0; j < 7; j++)
            {
                if (corr[j] > maxcorr)
                {
                    maxcorr = corr[j];
                    maxi = i;
                    maxj = j;
                }
            }
        }

        for (int i = 0; i < len; i++)
        {
            int tmp = 0;
            if (maxi > 0)
            {
                for (int k = 0; k < 7; k++)
                {
                    tmp += Mult16_16(buf[at + i - (pitch - maxj + 3) + k - 3], ShiftFilter[maxi - 1][k]);
                }
            }
            else
            {
                tmp = Shl32(buf[at + i - (pitch - maxj + 3)], 15);
            }

            interp[i] = (short)Pshr(tmp, 15);
        }

        return pitch - maxj + 3;
    }

    // multicomb: the comb filter of the perceptual enhancer. exc[at - 288 ..] holds the excitation history; the 80 samples from at are enhanced into newExc. The scaled-down samples
    // are written back to buf as libspeex writes them back (a bit lost where they were halved).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void MultiComb(Span<short> buf, int at, Span<short> newExc, int nsf, int pitch, int maxPitch, int combGain)
    {
        Span<short> iexc = stackalloc short[2 * nsf];
        int corrPitch = pitch;
        InterpolatePitch(buf, at, iexc, corrPitch, 80);
        if (corrPitch > maxPitch)
        {
            InterpolatePitch(buf, at, iexc.Slice(nsf), 2 * corrPitch, 80);
        }
        else
        {
            InterpolatePitch(buf, at, iexc.Slice(nsf), -corrPitch, 80);
        }

        bool scaleDown = false;
        for (int i = 0; i < nsf; i++)
        {
            int v = buf[at + i];
            if ((v < 0 ? -v : v) > 16383)
            {
                scaleDown = true;
                break;
            }
        }

        if (scaleDown)
        {
            for (int i = 0; i < nsf; i++)
            {
                buf[at + i] = (short)(buf[at + i] >> 1);
            }

            for (int i = 0; i < 2 * nsf; i++)
            {
                iexc[i] = (short)(iexc[i] >> 1);
            }
        }

        Span<short> exc = buf.Slice(at, nsf);
        int iexc0Mag = Sqrt(1000 + InnerProduct(iexc, iexc, nsf));
        int iexc1Mag = Sqrt(1000 + InnerProduct(iexc.Slice(nsf), iexc.Slice(nsf), nsf));
        int excMag = Sqrt(1 + InnerProduct(exc, exc, nsf));
        int corr0 = InnerProduct(iexc, exc, nsf);
        if (corr0 < 0)
        {
            corr0 = 0;
        }

        int corr1 = InnerProduct(iexc.Slice(nsf), exc, nsf);
        if (corr1 < 0)
        {
            corr1 = 0;
        }

        if (Shl32(iexc0Mag, 6) < excMag)
        {
            iexc0Mag = Add16(1, (short)Pshr((short)excMag, 6));
        }

        if (Shl32(iexc1Mag, 6) < excMag)
        {
            iexc1Mag = Add16(1, (short)Pshr((short)excMag, 6));
        }

        int pgain1 = corr0 > Mult16_16(iexc0Mag, excMag) ? 16384 : Pdiv32_16(Shl32(Pdiv32(corr0, excMag), 14), iexc0Mag);
        int pgain2 = corr1 > Mult16_16(iexc1Mag, excMag) ? 16384 : Pdiv32_16(Shl32(Pdiv32(corr1, excMag), 14), iexc1Mag);
        int gg1 = Pdiv32_16(Shl32(excMag, 8), iexc0Mag);
        int gg2 = Pdiv32_16(Shl32(excMag, 8), iexc1Mag);
        int c1;
        int c2;
        if (combGain > 0)
        {
            // QCONST16(.4, 15) = 13107, QCONST16(.07, 15) = 2294, QCONST16(.5, 15) = 16384, QCONST16(1.72, 14) = 28180.
            c1 = (short)(Mult16_16_Q15(13107, combGain) + 2294);
            c2 = (short)(16384 + Mult16_16_Q14(28180, c1 - 2294));
        }
        else
        {
            c1 = 0;
            c2 = 0;
        }

        int g1 = (short)(32767 - Mult16_16_Q13((short)Mult16_16_Q15(c2, pgain1), pgain1));
        int g2 = (short)(32767 - Mult16_16_Q13((short)Mult16_16_Q15(c2, pgain2), pgain2));
        if (g1 < c1)
        {
            g1 = c1;
        }

        if (g2 < c1)
        {
            g2 = c1;
        }

        g1 = (short)Pdiv32_16(Shl32(c1, 14), (short)g1);
        g2 = (short)Pdiv32_16(Shl32(c1, 14), (short)g2);
        int gain0;
        int gain1;
        if (corrPitch > maxPitch)
        {
            gain0 = (short)Mult16_16_Q15(22938, (short)Mult16_16_Q14(g1, gg1));
            gain1 = (short)Mult16_16_Q15(9830, (short)Mult16_16_Q14(g2, gg2));
        }
        else
        {
            gain0 = (short)Mult16_16_Q15(19661, (short)Mult16_16_Q14(g1, gg1));
            gain1 = (short)Mult16_16_Q15(19661, (short)Mult16_16_Q14(g2, gg2));
        }

        for (int i = 0; i < nsf; i++)
        {
            newExc[i] = (short)Add16(exc[i], (short)Pshr(Mult16_16(gain0, iexc[i]) + Mult16_16(gain1, iexc[i + nsf]), 8));
        }

        int newEnergy = Rms16(newExc, nsf);
        int oldEnergy = Rms16(exc, nsf);
        if (oldEnergy < 1)
        {
            oldEnergy = 1;
        }

        if (newEnergy < 1)
        {
            newEnergy = 1;
        }

        if (oldEnergy > newEnergy)
        {
            oldEnergy = newEnergy;
        }

        int ngain = Pdiv32_16(Shl32(oldEnergy, 14), newEnergy);
        for (int i = 0; i < nsf; i++)
        {
            newExc[i] = (short)Mult16_16_Q14(ngain, newExc[i]);
        }

        if (scaleDown)
        {
            for (int i = 0; i < nsf; i++)
            {
                buf[at + i] = (short)Shl16(buf[at + i], 1);
            }

            for (int i = 0; i < nsf; i++)
            {
                newExc[i] = (short)Shl16(Saturate(newExc[i], 16383), 1);
            }
        }
    }
}
