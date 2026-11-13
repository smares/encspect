using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio.Decoding;

// The reconstruction stages of Layer III after the entropy pass: requantization, stereo processing, reordering, the antialias butterflies and the inverse transforms.
// The arithmetic follows minimp3 (CC0 1.0, https://github.com/lieff/minimp3, commit ea99364f; copied in third_party/minimp3) operation by operation, in single
// precision, so the results are the same on every platform: no fused multiply-add, no platform math library (the powers come from DetMath), every sum in one fixed order.
internal static class Layer3Kernels
{
    private const int BitsDequantizerOut = -1;
    private const int MaxScalefactor = 255 + (BitsDequantizerOut * 4) - 210;
    private const int MaxScalefactorIndex = (MaxScalefactor + 3) & ~3;

    private static readonly float[] ExpFraction = [9.31322575e-10f, 7.83145814e-10f, 6.58544508e-10f, 5.53767716e-10f];

    private static readonly byte[] Preamp = [1, 1, 1, 1, 2, 2, 3, 3, 3, 2];

    // x^(4/3) for the magnitudes a Huffman code can state: 15 plus the 13 bits of linbits.
    private static readonly float[] Pow43 = CreatePow43();

    private static float[] CreatePow43()
    {
        var table = new float[8207];
        for (int i = 1; i < table.Length; i++)
        {
            table[i] = (float)DetMath.Pow(i, 4.0 / 3.0);
        }

        return table;
    }

    // y * 2^(-exp / 4), in steps that keep the float in range (minimp3's L3_ldexp_q2).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float LdexpQ2(float y, int exponent)
    {
        int e;
        do
        {
            e = Math.Min(30 * 4, exponent);
            y *= ExpFraction[e & 3] * (1 << 30 >> (e >> 2));
        }
        while ((exponent -= e) > 0);
        return y;
    }

    // The scalefactor of every band of one granule and channel, with the global gain folded in (minimp3's L3_decode_scalefactors after the scalefactors are read).
    // scalefactors are the values as coded; isMidSide takes 3 dB off for the sum and difference of mid/side stereo.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Scalefactors(in GranuleSyntax gr, ReadOnlySpan<int> coded, bool isMidSide, Span<float> scale)
    {
        Span<int> iscf = stackalloc int[40];
        for (int i = 0; i < 40; i++)
        {
            iscf[i] = coded[i];
        }

        int shift = gr.ScalefacScale + 1;
        if (gr.ShortBandCount != 0)
        {
            int sh = 3 - shift;
            for (int i = 0; i < gr.ShortBandCount; i += 3)
            {
                iscf[gr.LongBandCount + i + 0] += gr.SubblockGain0 << sh;
                iscf[gr.LongBandCount + i + 1] += gr.SubblockGain1 << sh;
                iscf[gr.LongBandCount + i + 2] += gr.SubblockGain2 << sh;
            }
        }
        else if (gr.Preflag != 0)
        {
            for (int i = 0; i < 10; i++)
            {
                iscf[11 + i] += Preamp[i];
            }
        }

        int gainExponent = gr.GlobalGain + (BitsDequantizerOut * 4) - 210 - (isMidSide ? 2 : 0);
        float gain = LdexpQ2(1 << (MaxScalefactorIndex / 4), MaxScalefactorIndex - gainExponent);
        int bands = gr.LongBandCount + gr.ShortBandCount;
        for (int i = 0; i < bands; i++)
        {
            scale[i] = LdexpQ2(gain, iscf[i] << shift);
        }
    }

    // Requantizes the 576 coded values of a granule and channel: the value to the 4/3 power times the scalefactor of the band it lies in. dst is zeroed where the
    // bands end (the sum of the widths is 576 for every row, and lines past the coded ones are zero values).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Requantize(in GranuleSyntax gr, ReadOnlySpan<int> values, ReadOnlySpan<float> scale, Span<float> dst)
    {
        ReadOnlySpan<byte> widths = Layer3Syntax.BandWidths(gr);
        int line = 0;
        for (int band = 0; line < 576 && band < widths.Length && widths[band] != 0; band++)
        {
            float one = scale[band];
            int end = Math.Min(576, line + widths[band]);
            for (; line < end; line++)
            {
                int q = values[line];
                if (q == 0)
                {
                    dst[line] = 0;
                    continue;
                }

                int magnitude = q < 0 ? -q : q;
                float scaled = Pow43[Math.Min(magnitude, Pow43.Length - 1)] * one;
                dst[line] = q < 0 ? -scaled : scaled;
            }
        }

        dst[line..].Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void MidSide(Span<float> left, Span<float> right, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float a = left[i];
            float b = right[i];
            left[i] = a + b;
            right[i] = a - b;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void IntensityBand(Span<float> left, Span<float> right, int count, float kl, float kr)
    {
        for (int i = 0; i < count; i++)
        {
            right[i] = left[i] * kr;
            left[i] = left[i] * kl;
        }
    }

    // The highest band of each window (index modulo 3) in which the right channel has a nonzero pair (minimp3's L3_stereo_top_band). The parser's Layer3Walker.IntensityStart
    // applies the same rule to the quantized values to report the intensity stereo bound; keep the two together.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TopBands(ReadOnlySpan<float> right, ReadOnlySpan<byte> widths, int bandCount, Span<int> maxBand)
    {
        maxBand[0] = maxBand[1] = maxBand[2] = -1;
        int position = 0;
        for (int i = 0; i < bandCount; i++)
        {
            for (int k = 0; k < widths[i]; k += 2)
            {
                if (right[position + k] != 0 || right[position + k + 1] != 0)
                {
                    maxBand[i % 3] = i;
                    break;
                }
            }

            position += widths[i];
        }
    }

    // Intensity stereo (and mid/side stereo in the bands below the intensity ones), minimp3's L3_intensity_stereo and L3_stereo_process: left and right hold the
    // channels' requantized lines; positions are the right channel's intensity positions (255 where illegal), which the call completes for the top bands.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void IntensityStereo(MpegHeader header, in GranuleSyntax left, in GranuleSyntax right, Span<float> leftLines, Span<float> rightLines, Span<byte> positions, bool isMidSide)
    {
        ReadOnlySpan<byte> widths = Layer3Syntax.BandWidths(left);
        Span<int> maxBand = stackalloc int[3];
        int bandCount = left.LongBandCount + left.ShortBandCount;
        int maxBlocks = left.ShortBandCount != 0 ? 3 : 1;
        TopBands(rightLines, widths, bandCount, maxBand);
        if (left.LongBandCount != 0)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
        }

        for (int i = 0; i < maxBlocks; i++)
        {
            int defaultPosition = header.IsMpeg1 ? 3 : 0;
            int top = bandCount - maxBlocks + i;
            int previous = top - maxBlocks;
            positions[top] = maxBand[i] >= previous ? (byte)defaultPosition : positions[previous];
        }

        int mpeg2Shift = right.ScalefacCompress & 1;
        uint maxPosition = header.IsMpeg1 ? 7u : 64u;
        int offset = 0;
        for (int i = 0; widths[i] != 0; i++)
        {
            uint position = positions[i];
            int width = widths[i];
            if (i > maxBand[i % 3] && position < maxPosition)
            {
                float kl;
                float kr;
                float s = isMidSide ? 1.41421356f : 1;
                if (header.IsMpeg1)
                {
                    kl = SynthesisTables.PanFactors[2 * position];
                    kr = SynthesisTables.PanFactors[(2 * position) + 1];
                }
                else
                {
                    kl = 1;
                    kr = LdexpQ2(1, (int)((position + 1) >> 1 << mpeg2Shift));
                    if ((position & 1) != 0)
                    {
                        kl = kr;
                        kr = 1;
                    }
                }

                IntensityBand(leftLines.Slice(offset, width), rightLines.Slice(offset, width), width, kl * s, kr * s);
            }
            else if (isMidSide)
            {
                MidSide(leftLines.Slice(offset, width), rightLines.Slice(offset, width), width);
            }

            offset += width;
        }
    }

    // Puts the lines of the short blocks in frequency order: the data come band by band, each band one window after the other (minimp3's L3_reorder).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Reorder(Span<float> lines, Span<float> scratch, ReadOnlySpan<byte> widths)
    {
        int src = 0;
        int dst = 0;
        for (int band = 0; band + 2 < widths.Length && widths[band] != 0; band += 3)
        {
            int length = widths[band];

            // The 8 kHz row of minimp3's mixed band table does not add up to the lines past the long subbands; what it would read beyond them is left as it is.
            if (src + (3 * length) > lines.Length)
            {
                break;
            }

            for (int i = 0; i < length; i++, src++)
            {
                scratch[dst++] = lines[src];
                scratch[dst++] = lines[src + length];
                scratch[dst++] = lines[src + (2 * length)];
            }

            src += 2 * length;
        }

        scratch[..dst].CopyTo(lines);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Antialias(Span<float> lines, int bands)
    {
        ReadOnlySpan<float> aa = SynthesisTables.Antialias;
        int offset = 0;
        for (; bands > 0; bands--, offset += 18)
        {
            for (int i = 0; i < 8; i++)
            {
                float u = lines[offset + 18 + i];
                float d = lines[offset + 17 - i];
                lines[offset + 18 + i] = (u * aa[i]) - (d * aa[8 + i]);
                lines[offset + 17 - i] = (u * aa[8 + i]) + (d * aa[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Dct3Of9(Span<float> y)
    {
        float s0 = y[0];
        float s2 = y[2];
        float s4 = y[4];
        float s6 = y[6];
        float s8 = y[8];
        float t0 = s0 + (s6 * 0.5f);
        s0 -= s6;
        float t4 = (s4 + s2) * 0.93969262f;
        float t2 = (s8 + s2) * 0.76604444f;
        s6 = (s4 - s8) * 0.17364818f;
        s4 += s8 - s2;

        s2 = s0 - (s4 * 0.5f);
        y[4] = s4 + s0;
        s8 = t0 - t2 + s6;
        s0 = t0 - t4 + t2;
        s4 = t0 + t4 - s6;

        float s1 = y[1];
        float s3 = y[3];
        float s5 = y[5];
        float s7 = y[7];

        s3 *= 0.86602540f;
        t0 = (s5 + s1) * 0.98480775f;
        t4 = (s5 - s7) * 0.34202014f;
        t2 = (s1 + s7) * 0.64278761f;
        s1 = (s1 - s5 - s7) * 0.86602540f;

        s5 = t0 - s3 - t2;
        s7 = t4 - s3 - t0;
        s3 = t4 + s3 - t2;

        y[0] = s4 - s7;
        y[1] = s2 + s1;
        y[2] = s0 - s3;
        y[3] = s8 + s5;
        y[5] = s8 - s5;
        y[6] = s0 + s3;
        y[7] = s2 - s1;
        y[8] = s4 + s7;
    }

    // The 36-point inverse transform with windowing and overlap-add for nbands subbands (minimp3's L3_imdct36). window is 18 entries of the long or stop window.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Imdct36(Span<float> lines, Span<float> overlap, ReadOnlySpan<float> window, int nbands)
    {
        ReadOnlySpan<float> twiddle = SynthesisTables.Twiddle9;
        Span<float> co = stackalloc float[9];
        Span<float> si = stackalloc float[9];
        int gr = 0;
        int ov = 0;
        for (int j = 0; j < nbands; j++, gr += 18, ov += 9)
        {
            co[0] = -lines[gr];
            si[0] = lines[gr + 17];
            for (int i = 0; i < 4; i++)
            {
                si[8 - (2 * i)] = lines[gr + (4 * i) + 1] - lines[gr + (4 * i) + 2];
                co[1 + (2 * i)] = lines[gr + (4 * i) + 1] + lines[gr + (4 * i) + 2];
                si[7 - (2 * i)] = lines[gr + (4 * i) + 4] - lines[gr + (4 * i) + 3];
                co[2 + (2 * i)] = -(lines[gr + (4 * i) + 3] + lines[gr + (4 * i) + 4]);
            }

            Dct3Of9(co);
            Dct3Of9(si);

            si[1] = -si[1];
            si[3] = -si[3];
            si[5] = -si[5];
            si[7] = -si[7];

            for (int i = 0; i < 9; i++)
            {
                float ovl = overlap[ov + i];
                float sum = (co[i] * twiddle[9 + i]) + (si[i] * twiddle[i]);
                overlap[ov + i] = (co[i] * twiddle[i]) - (si[i] * twiddle[9 + i]);
                lines[gr + i] = (ovl * window[i]) - (sum * window[9 + i]);
                lines[gr + 17 - i] = (ovl * window[9 + i]) + (sum * window[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Idct3(float x0, float x1, float x2, Span<float> dst)
    {
        float m1 = x1 * 0.86602540f;
        float a1 = x0 - (x2 * 0.5f);
        dst[1] = x0 + x2;
        dst[0] = a1 + m1;
        dst[2] = a1 - m1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Imdct12(ReadOnlySpan<float> x, int xo, Span<float> dst, int dstOffset, Span<float> overlap, int overlapOffset)
    {
        ReadOnlySpan<float> twiddle = SynthesisTables.Twiddle3;
        Span<float> co = stackalloc float[3];
        Span<float> si = stackalloc float[3];
        Idct3(-x[xo], x[xo + 6] + x[xo + 3], x[xo + 12] + x[xo + 9], co);
        Idct3(x[xo + 15], x[xo + 12] - x[xo + 9], x[xo + 6] - x[xo + 3], si);
        si[1] = -si[1];

        for (int i = 0; i < 3; i++)
        {
            float ovl = overlap[overlapOffset + i];
            float sum = (co[i] * twiddle[3 + i]) + (si[i] * twiddle[i]);
            overlap[overlapOffset + i] = (co[i] * twiddle[i]) - (si[i] * twiddle[3 + i]);
            dst[dstOffset + i] = (ovl * twiddle[2 - i]) - (sum * twiddle[5 - i]);
            dst[dstOffset + 5 - i] = (ovl * twiddle[5 - i]) + (sum * twiddle[2 - i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ImdctShort(Span<float> lines, Span<float> overlap, int nbands)
    {
        Span<float> tmp = stackalloc float[18];
        int gr = 0;
        int ov = 0;
        for (; nbands > 0; nbands--, ov += 9, gr += 18)
        {
            lines.Slice(gr, 18).CopyTo(tmp);
            overlap.Slice(ov, 6).CopyTo(lines.Slice(gr, 6));
            Imdct12(tmp, 0, lines, gr + 6, overlap, ov + 6);
            Imdct12(tmp, 1, lines, gr + 12, overlap, ov + 6);
            Imdct12(tmp, 2, overlap, ov, overlap, ov + 6);
        }
    }

    // Flips the sign of every odd line of the odd subbands, which the polyphase filterbank's frequency inversion needs.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ChangeSign(Span<float> lines)
    {
        int offset = 18;
        for (int b = 0; b < 32; b += 2, offset += 36)
        {
            for (int i = 1; i < 18; i += 2)
            {
                lines[offset + i] = -lines[offset + i];
            }
        }
    }

    // The inverse transforms of a granule's 32 subbands: the first longBands with the long window, the rest with the window of the block type (minimp3's L3_imdct_gr).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ImdctGranule(Span<float> lines, Span<float> overlap, int blockType, int longBands)
    {
        ReadOnlySpan<float> windows = SynthesisTables.MdctWindow;
        if (longBands != 0)
        {
            Imdct36(lines, overlap, windows[..18], longBands);
            lines = lines[(18 * longBands)..];
            overlap = overlap[(9 * longBands)..];
        }

        if (blockType == Layer3Syntax.ShortBlockType)
        {
            ImdctShort(lines, overlap, 32 - longBands);
        }
        else
        {
            Imdct36(lines, overlap, windows.Slice(blockType == Layer3Syntax.StopBlockType ? 18 : 0, 18), 32 - longBands);
        }
    }
}
