// Reading derived from libspeex 1.2.1 (lsp.c: lsp_to_lpc, lsp_interpolate; quant_lsp.c: lsp_unquant_nb, lsp_unquant_lbr, lsp_unquant_high), Copyright (c) 2002-2008 Jean-Marc Valin and
// Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using static Encspect.Formats.Speex.Decoding.Fixed;

namespace Encspect.Formats.Speex.Decoding;

// Line spectral pairs: their codebooks, their interpolation between frames and their conversion to the coefficients of the synthesis filter, in libspeex's fixed-point arithmetic
// (angles in Q13 radians, coefficients in Q13).
internal static class Lsp
{
    // lsp_unquant_nb: five stages of the full quantizer. ids hold the narrowband layer's LSP indexes.
    public static void UnquantNarrowband(ReadOnlySpan<int> ids, Span<short> lsp)
    {
        for (int i = 0; i < 10; i++)
        {
            lsp[i] = (short)Shl16(i + 1, 11);
        }

        int id = ids[0];
        for (int i = 0; i < 10; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspNarrowband[(id * 10) + i], 5));
        }

        id = ids[1];
        for (int i = 0; i < 5; i++)
        {
            lsp[i] = (short)Add16(lsp[i], Shl16(SpeexTables.LspNarrowbandLow1[(id * 5) + i], 4));
        }

        id = ids[2];
        for (int i = 0; i < 5; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspNarrowbandLow2[(id * 5) + i], 3));
        }

        id = ids[3];
        for (int i = 0; i < 5; i++)
        {
            lsp[i + 5] = (short)(lsp[i + 5] + Shl16(SpeexTables.LspNarrowbandHigh1[(id * 5) + i], 4));
        }

        id = ids[4];
        for (int i = 0; i < 5; i++)
        {
            lsp[i + 5] = (short)(lsp[i + 5] + Shl16(SpeexTables.LspNarrowbandHigh2[(id * 5) + i], 3));
        }
    }

    // lsp_unquant_lbr: the low-rate quantizer, three stages.
    public static void UnquantLowRate(ReadOnlySpan<int> ids, Span<short> lsp)
    {
        for (int i = 0; i < 10; i++)
        {
            lsp[i] = (short)Shl16(i + 1, 11);
        }

        int id = ids[0];
        for (int i = 0; i < 10; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspNarrowband[(id * 10) + i], 5));
        }

        id = ids[1];
        for (int i = 0; i < 5; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspNarrowbandLow1[(id * 5) + i], 4));
        }

        id = ids[2];
        for (int i = 0; i < 5; i++)
        {
            lsp[i + 5] = (short)(lsp[i + 5] + Shl16(SpeexTables.LspNarrowbandHigh1[(id * 5) + i], 4));
        }
    }

    // lsp_unquant_high: the wideband layers' quantizer, two stages of eight values.
    public static void UnquantHigh(ReadOnlySpan<int> ids, Span<short> lsp, int order)
    {
        for (int i = 0; i < order; i++)
        {
            lsp[i] = (short)Add16(Mult16_16_16(i, 2560), 6144);
        }

        int id = ids[0];
        for (int i = 0; i < order; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspHigh1[(id * order) + i], 5));
        }

        id = ids[1];
        for (int i = 0; i < order; i++)
        {
            lsp[i] = (short)(lsp[i] + Shl16(SpeexTables.LspHigh2[(id * order) + i], 4));
        }
    }

    // lsp_interpolate: the LSPs of a subframe between the last frame's and this one's, kept apart by margin.
    public static void Interpolate(ReadOnlySpan<short> oldLsp, ReadOnlySpan<short> newLsp, Span<short> lsp, int len, int subframe, int subframes, int margin)
    {
        int m = margin;
        int m2 = 25736 - margin;
        int tmp = Div32_16(Shl32(1 + subframe, 14), subframes);
        int tmp2 = 16384 - tmp;
        for (int i = 0; i < len; i++)
        {
            lsp[i] = (short)(Mult16_16_P14(tmp2, oldLsp[i]) + Mult16_16_P14(tmp, newLsp[i]));
        }

        if (lsp[0] < m)
        {
            lsp[0] = (short)m;
        }

        if (lsp[len - 1] > m2)
        {
            lsp[len - 1] = (short)m2;
        }

        for (int i = 1; i < len - 1; i++)
        {
            if (lsp[i] < lsp[i - 1] + m)
            {
                lsp[i] = (short)(lsp[i - 1] + m);
            }

            if (lsp[i] > lsp[i + 1] - m)
            {
                lsp[i] = (short)((lsp[i] >> 1) + ((lsp[i + 1] - m) >> 1));
            }
        }
    }

    // lsp_to_lpc: the coefficients of the synthesis filter (Q13) from the LSPs, by the cascade of second-order sections in Q21. work holds 2 * (order / 2 + 1) * (order + 3) integers.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ToLpc(ReadOnlySpan<short> freq, Span<short> ak, int order, Span<int> work)
    {
        int m = order >> 1;
        int stride = order + 3;
        Span<int> xp = work.Slice(0, (m + 1) * stride);
        Span<int> xq = work.Slice((m + 1) * stride, (m + 1) * stride);
        xp.Clear();
        xq.Clear();
        Span<short> freqn = stackalloc short[order];
        for (int i = 0; i < order; i++)
        {
            freqn[i] = (short)Shl16(Cos(freq[i]), 2);
        }

        const int xin = 1 << 20;
        for (int i = 0; i <= m; i++)
        {
            xp[(i * stride) + 1] = 0;
            xp[(i * stride) + 2] = xin;
            xp[(i * stride) + 2 + (2 * i)] = xin;
            xq[(i * stride) + 1] = 0;
            xq[(i * stride) + 2] = xin;
            xq[(i * stride) + 2 + (2 * i)] = xin;
        }

        xp[stride + 3] = -Mult16_32_Q14(freqn[0], xp[2]);
        xq[stride + 3] = -Mult16_32_Q14(freqn[1], xq[2]);
        int xout1 = 0;
        int xout2 = 0;
        for (int i = 1; i < m; i++)
        {
            int j;
            for (j = 1; j < (2 * (i + 1)) - 1; j++)
            {
                int mult = Mult16_32_Q14(freqn[2 * i], xp[(i * stride) + j + 1]);
                xp[((i + 1) * stride) + j + 2] = xp[(i * stride) + j + 2] - mult + xp[(i * stride) + j];
                mult = Mult16_32_Q14(freqn[(2 * i) + 1], xq[(i * stride) + j + 1]);
                xq[((i + 1) * stride) + j + 2] = xq[(i * stride) + j + 2] - mult + xq[(i * stride) + j];
            }

            int last = Mult16_32_Q14(freqn[2 * i], xp[(i * stride) + j + 1]);
            xp[((i + 1) * stride) + j + 2] = xp[(i * stride) + j] - last;
            last = Mult16_32_Q14(freqn[(2 * i) + 1], xq[(i * stride) + j + 1]);
            xq[((i + 1) * stride) + j + 2] = xq[(i * stride) + j] - last;
        }

        for (int j = 1; j <= order; j++)
        {
            int a = Pshr(xp[(m * stride) + j + 2] + xout1 + xq[(m * stride) + j + 2] - xout2, 8);
            xout1 = xp[(m * stride) + j + 2];
            xout2 = xq[(m * stride) + j + 2];
            if (a < -32767)
            {
                a = -32767;
            }

            if (a > 32767)
            {
                a = 32767;
            }

            ak[j - 1] = (short)a;
        }
    }
}
