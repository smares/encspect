using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Opus.Decoding;

// The shape of the bands of a CELT frame: the folding, the pulse vectors with their rotation, the angles, the stereo merge and the time-frequency changes, replayed from
// the integers the entropy pass stored (CeltSyntax) in the order it read them. The control flow is that of quant_all_bands, quant_band, quant_band_stereo and
// quant_partition (celt/bands.c) and exp_rotation and renormalise_vector (celt/vq.c) of libopus 1.5.2 (BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md); the float work is
// the reference's, single precision, in its order.
internal sealed class CeltBands
{
    private const float NormScaling = 1.0f;
    private static readonly byte[] OrderyTable = [1, 0, 3, 0, 2, 1, 7, 0, 4, 3, 6, 1, 5, 2, 15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5];

    private readonly float[] _norm = new float[2 * 21 * 8 * 22];
    private readonly float[] _lowbandScratch = new float[512];
    private readonly float[] _hadamardScratch = new float[512];
    private readonly int[] _y = new int[512];
    private ReadOnlySpan<short> EBands => CeltTables.EBand5Ms;

    private uint _seed;
    private int _spread;
    private int _tfChange;

    public uint Seed => _seed;

    // The bands of one frame into x (channels * n values, the second channel after the first); seed is the decoder's random state, which the noise folding advances.
    public void Replay(ref IntReader r, float[] x, int start, int end, int lm, int channels, bool shortBlocks, int spread, bool dualStereo, int intensity,
        ReadOnlySpan<int> tfRes, ref uint seed)
    {
        int m = 1 << lm;
        int b = shortBlocks ? m : 1;
        int n = m * 120;
        int normOffset = m * EBands[start];
        int normLength = (m * EBands[20]) - normOffset;
        Span<float> norm = _norm.AsSpan(0, channels * normLength);
        Span<float> norm2 = channels == 2 ? _norm.AsSpan(normLength, normLength) : [];
        norm.Clear();
        _seed = seed;
        _spread = spread;
        for (int i = start; i < end; i++)
        {
            bool last = i == end - 1;
            int effectiveLowband = r.Next();
            int width = (m * EBands[i + 1]) - (m * EBands[i]);
            int xAt = m * EBands[i];
            int yAt = n + xAt;
            if (i == start + 1)
            {
                // special_hybrid_folding: the first folded band repeats the end of the band before it.
                int n1 = m * (EBands[start + 1] - EBands[start]);
                int n2 = m * (EBands[start + 2] - EBands[start + 1]);
                for (int j = 0; j < n2 - n1; j++)
                {
                    norm[n1 + j] = norm[(2 * n1) - n2 + j];
                }

                if (dualStereo)
                {
                    for (int j = 0; j < n2 - n1; j++)
                    {
                        norm2[n1 + j] = norm2[(2 * n1) - n2 + j];
                    }
                }
            }

            _tfChange = tfRes[i - start];
            if (dualStereo && i == intensity)
            {
                dualStereo = false;
                for (int j = 0; j < (m * EBands[i]) - normOffset; j++)
                {
                    norm[j] = 0.5f * (norm[j] + norm2[j]);
                }
            }

            int outAt = (m * EBands[i]) - normOffset;
            if (dualStereo)
            {
                QuantBand(ref r, x, xAt, width, b, norm, effectiveLowband, lm, last ? default : norm, outAt, 1.0f);
                QuantBand(ref r, x, yAt, width, b, norm2, effectiveLowband, lm, last ? default : norm2, outAt, 1.0f);
            }
            else if (channels == 2)
            {
                QuantBandStereo(ref r, x, xAt, yAt, width, b, norm, effectiveLowband, lm, last ? default : norm, outAt);
            }
            else
            {
                QuantBand(ref r, x, xAt, width, b, norm, effectiveLowband, lm, last ? default : norm, outAt, 1.0f);
            }
        }

        seed = _seed;
    }

    private static uint Lcg(uint seed) => unchecked((1664525 * seed) + 1013904223);

    private void QuantBandN1(ref IntReader r, float[] x, int xAt, int yAt, Span<float> lowbandOut, int outAt)
    {
        int sign = r.Next();
        x[xAt] = sign != 0 ? -NormScaling : NormScaling;
        if (yAt >= 0)
        {
            sign = r.Next();
            x[yAt] = sign != 0 ? -NormScaling : NormScaling;
        }

        if (!lowbandOut.IsEmpty)
        {
            lowbandOut[outAt] = x[xAt];
        }
    }

    private void QuantBand(ref IntReader r, float[] x, int xAt, int n, int bBlocks, Span<float> normSource, int effectiveLowband, int lm, Span<float> lowbandOut, int outAt, float gain)
    {
        int n0 = n;
        int b0 = bBlocks;
        bool longBlocks = b0 == 1;
        int nB = n / bBlocks;
        if (n == 1)
        {
            QuantBandN1(ref r, x, xAt, -1, lowbandOut, outAt);
            return;
        }

        int tfChange = _tfChange;
        int recombine = tfChange > 0 ? tfChange : 0;
        int timeDivide = 0;
        bool hasLowband = effectiveLowband != -1;
        Span<float> lowband = default;
        if (hasLowband)
        {
            lowband = normSource.Slice(effectiveLowband);
            if (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1)
            {
                lowband.Slice(0, n).CopyTo(_lowbandScratch);
                lowband = _lowbandScratch.AsSpan(0, n);
            }
        }

        for (int k = 0; k < recombine; k++)
        {
            if (hasLowband)
            {
                Haar1(lowband, n >> k, 1 << k);
            }
        }

        bBlocks >>= recombine;
        nB <<= recombine;
        while ((nB & 1) == 0 && tfChange < 0)
        {
            if (hasLowband)
            {
                Haar1(lowband, nB, bBlocks);
            }

            bBlocks <<= 1;
            nB >>= 1;
            timeDivide++;
            tfChange++;
        }

        b0 = bBlocks;
        int nB0 = nB;
        if (b0 > 1 && hasLowband)
        {
            DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks);
        }

        QuantPartition(ref r, x, xAt, n, bBlocks, lowband, hasLowband, lm, gain);
        Span<float> xs = x.AsSpan(xAt, n);
        if (b0 > 1)
        {
            InterleaveHadamard(xs, nB >> recombine, b0 << recombine, longBlocks);
        }

        nB = nB0;
        bBlocks = b0;
        for (int k = 0; k < timeDivide; k++)
        {
            bBlocks >>= 1;
            nB <<= 1;
            Haar1(xs, nB, bBlocks);
        }

        for (int k = 0; k < recombine; k++)
        {
            Haar1(xs, n0 >> k, 1 << k);
        }

        if (!lowbandOut.IsEmpty)
        {
            float scale = (float)Math.Sqrt(n0);
            for (int j = 0; j < n0; j++)
            {
                lowbandOut[outAt + j] = scale * xs[j];
            }
        }
    }

    private void QuantBandStereo(ref IntReader r, float[] x, int xAt, int yAt, int n, int bBlocks, Span<float> normSource, int effectiveLowband, int lm, Span<float> lowbandOut, int outAt)
    {
        if (n == 1)
        {
            QuantBandN1(ref r, x, xAt, yAt, lowbandOut, outAt);
            return;
        }

        int itheta = r.Next();
        int inv = r.Next();
        (int imid, int iside) = ThetaGains(itheta);
        float mid = (1.0f / 32768) * imid;
        float side = (1.0f / 32768) * iside;
        if (n == 2)
        {
            int sign = r.Next();
            bool c = itheta > 8192;
            int x2 = c ? yAt : xAt;
            int y2 = c ? xAt : yAt;
            QuantBand(ref r, x, x2, n, bBlocks, normSource, effectiveLowband, lm, lowbandOut, outAt, 1.0f);
            float sg = 1 - (2 * sign);
            x[y2] = -sg * x[x2 + 1];
            x[y2 + 1] = sg * x[x2];
            x[xAt] = mid * x[xAt];
            x[xAt + 1] = mid * x[xAt + 1];
            x[yAt] = side * x[yAt];
            x[yAt + 1] = side * x[yAt + 1];
            float tmp = x[xAt];
            x[xAt] = tmp - x[yAt];
            x[yAt] = tmp + x[yAt];
            tmp = x[xAt + 1];
            x[xAt + 1] = tmp - x[yAt + 1];
            x[yAt + 1] = tmp + x[yAt + 1];
        }
        else
        {
            bool midFirst = r.Next() != 0;
            if (midFirst)
            {
                QuantBand(ref r, x, xAt, n, bBlocks, normSource, effectiveLowband, lm, lowbandOut, outAt, 1.0f);
                QuantBand(ref r, x, yAt, n, bBlocks, default, -1, lm, default, 0, side);
            }
            else
            {
                QuantBand(ref r, x, yAt, n, bBlocks, default, -1, lm, default, 0, side);
                QuantBand(ref r, x, xAt, n, bBlocks, normSource, effectiveLowband, lm, lowbandOut, outAt, 1.0f);
            }

            StereoMerge(x.AsSpan(xAt, n), x.AsSpan(yAt, n), mid);
        }

        if (inv != 0)
        {
            for (int j = 0; j < n; j++)
            {
                x[yAt + j] = -x[yAt + j];
            }
        }
    }

    private static (int Mid, int Side) ThetaGains(int itheta)
    {
        if (itheta == 0)
        {
            return (32767, 0);
        }

        if (itheta == 16384)
        {
            return (0, 32767);
        }

        return (CeltSyntax.BitexactCos(itheta), CeltSyntax.BitexactCos(16384 - itheta));
    }

    private void QuantPartition(ref IntReader r, float[] x, int xAt, int n, int bBlocks, Span<float> lowband, bool hasLowband, int lm, float gain)
    {
        if (r.Next() == 1)
        {
            int itheta = r.Next();
            bool midFirst = r.Next() != 0;
            n >>= 1;
            int yAt = xAt + n;
            lm -= 1;
            bBlocks = (bBlocks + 1) >> 1;
            (int imid, int iside) = ThetaGains(itheta);
            float mid = (1.0f / 32768) * imid;
            float side = (1.0f / 32768) * iside;
            Span<float> next = hasLowband ? lowband.Slice(n) : default;
            if (midFirst)
            {
                QuantPartition(ref r, x, xAt, n, bBlocks, lowband, hasLowband, lm, gain * mid);
                QuantPartition(ref r, x, yAt, n, bBlocks, next, hasLowband, lm, gain * side);
            }
            else
            {
                QuantPartition(ref r, x, yAt, n, bBlocks, next, hasLowband, lm, gain * side);
                QuantPartition(ref r, x, xAt, n, bBlocks, lowband, hasLowband, lm, gain * mid);
            }

            return;
        }

        int k = r.Next();
        Span<float> xs = x.AsSpan(xAt, n);
        if (k > 0)
        {
            ReadOnlySpan<int> y = r.Take(n);
            float ryy = 0;
            for (int j = 0; j < n; j++)
            {
                ryy += y[j] * y[j];
            }

            float g = (1.0f / (float)Math.Sqrt(ryy)) * gain;
            for (int j = 0; j < n; j++)
            {
                xs[j] = g * y[j];
            }

            ExpRotation(xs, n, -1, bBlocks, k, _spread);
            return;
        }

        int mode = r.Next();
        if (mode == 0)
        {
            xs.Clear();
        }
        else if (mode == 1)
        {
            for (int j = 0; j < n; j++)
            {
                _seed = Lcg(_seed);
                xs[j] = (int)_seed >> 20;
            }

            RenormaliseVector(xs, gain);
        }
        else
        {
            for (int j = 0; j < n; j++)
            {
                _seed = Lcg(_seed);
                float tmp = 1.0f / 256;
                tmp = (_seed & 0x8000) != 0 ? tmp : -tmp;
                xs[j] = lowband[j] + tmp;
            }

            RenormaliseVector(xs, gain);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void RenormaliseVector(Span<float> x, float gain)
    {
        float e = 1e-15f;
        for (int i = 0; i < x.Length; i++)
        {
            e += x[i] * x[i];
        }

        float g = (1.0f / (float)Math.Sqrt(e)) * gain;
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = g * x[i];
        }
    }

    private static void ExpRotation1(Span<float> x, int len, int stride, float c, float s)
    {
        float ms = -s;
        int p = 0;
        for (int i = 0; i < len - stride; i++)
        {
            float x1 = x[p];
            float x2 = x[p + stride];
            x[p + stride] = (c * x2) + (s * x1);
            x[p++] = (c * x1) + (ms * x2);
        }

        p = len - (2 * stride) - 1;
        for (int i = len - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[p];
            float x2 = x[p + stride];
            x[p + stride] = (c * x2) + (s * x1);
            x[p--] = (c * x1) + (ms * x2);
        }
    }

    private static void ExpRotation(Span<float> x, int len, int dir, int stride, int k, int spread)
    {
        if ((2 * k) >= len || spread == 0)
        {
            return;
        }

        int factor = spread == 1 ? 15 : spread == 2 ? 10 : 5;
        float gain = (1.0f * len) / (len + (factor * k));
        float theta = 0.5f * (gain * gain);
        const float pi = 3.141592653f;
        float c = (float)DetMath.Cos((0.5f * pi) * theta);
        float s = (float)DetMath.Cos((0.5f * pi) * (1.0f - theta));
        int stride2 = 0;
        if (len >= 8 * stride)
        {
            stride2 = 1;
            while (((((stride2 * stride2) + stride2) * stride) + (stride >> 2)) < len)
            {
                stride2++;
            }
        }

        len /= stride;
        for (int i = 0; i < stride; i++)
        {
            Span<float> part = x.Slice(i * len);
            if (dir < 0)
            {
                if (stride2 != 0)
                {
                    ExpRotation1(part, len, stride2, s, c);
                }

                ExpRotation1(part, len, 1, c, s);
            }
            else
            {
                ExpRotation1(part, len, 1, c, -s);
                if (stride2 != 0)
                {
                    ExpRotation1(part, len, stride2, s, -c);
                }
            }
        }
    }

    private static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        const float h = 0.70710678f;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float tmp1 = h * x[(stride * 2 * j) + i];
                float tmp2 = h * x[(stride * ((2 * j) + 1)) + i];
                x[(stride * 2 * j) + i] = tmp1 + tmp2;
                x[(stride * ((2 * j) + 1)) + i] = tmp1 - tmp2;
            }
        }
    }

    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = _hadamardScratch.AsSpan(0, n);
        if (hadamard)
        {
            int ordery = stride - 2;
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(OrderyTable[ordery + i] * n0) + j] = x[(j * stride) + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(i * n0) + j] = x[(j * stride) + i];
                }
            }
        }

        tmp.CopyTo(x);
    }

    private void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int n = n0 * stride;
        Span<float> tmp = _hadamardScratch.AsSpan(0, n);
        if (hadamard)
        {
            int ordery = stride - 2;
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(OrderyTable[ordery + i] * n0) + j];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(i * n0) + j];
                }
            }
        }

        tmp.CopyTo(x);
    }

    private static void StereoMerge(Span<float> x, Span<float> y, float mid)
    {
        float xp = 0;
        float side = 0;
        for (int j = 0; j < x.Length; j++)
        {
            xp += y[j] * x[j];
            side += y[j] * y[j];
        }

        xp = mid * xp;
        float mid2 = mid;
        float el = (mid2 * mid2) + side - (2 * xp);
        float er = (mid2 * mid2) + side + (2 * xp);
        if (er < 6e-4f || el < 6e-4f)
        {
            x.CopyTo(y);
            return;
        }

        float lgain = 1.0f / (float)Math.Sqrt(el);
        float rgain = 1.0f / (float)Math.Sqrt(er);
        for (int j = 0; j < x.Length; j++)
        {
            float l = mid * x[j];
            float rr = y[j];
            x[j] = lgain * (l - rr);
            y[j] = rgain * (l + rr);
        }
    }
}
