using System.Runtime.CompilerServices;

namespace Encspect.Formats.Opus.Decoding;

// The complex FFT and the inverse MDCT of CELT (celt/kiss_fft.c, mdct.c and _kiss_fft_guts.h of libopus 1.5.2, derived from KISS FFT: BSD-3-Clause, Copyright (c) 2003-2010
// Mark Borgerding, and libopus's own BSD-3-Clause, notices in THIRD-PARTY-NOTICES.md). Single precision, every butterfly in the order of the reference, so that the output
// is the same on every platform; the twiddles and the bit reversal tables are the reference's own (OpusTables.cs).
internal sealed class KissFft
{
    private readonly int _nfft;
    private readonly int _shift;
    private readonly int[] _factors = new int[32];
    private readonly short[] _bitrev;

    public KissFft(int nfft, int shift, short[] bitrev)
    {
        _nfft = nfft;
        _shift = shift;
        _bitrev = bitrev;
        Factor(nfft, _factors);
    }

    public ReadOnlySpan<short> Bitrev => _bitrev;

    // kf_factor: the radixes of the transform, then the sizes of the sub-transforms.
    private static void Factor(int n, int[] facbuf)
    {
        int p = 4;
        int stages = 0;
        int nbak = n;
        do
        {
            while (n % p != 0)
            {
                switch (p)
                {
                    case 4:
                        p = 2;
                        break;
                    case 2:
                        p = 3;
                        break;
                    default:
                        p += 2;
                        break;
                }

                if (p > 32000 || p * p > n)
                {
                    p = n;
                }
            }

            n /= p;
            facbuf[2 * stages] = p;
            if (p == 2 && stages > 1)
            {
                facbuf[2 * stages] = 4;
                facbuf[2] = 2;
            }

            stages++;
        }
        while (n > 1);
        n = nbak;
        for (int i = 0; i < stages / 2; i++)
        {
            (facbuf[2 * i], facbuf[2 * (stages - i - 1)]) = (facbuf[2 * (stages - i - 1)], facbuf[2 * i]);
        }

        for (int i = 0; i < stages; i++)
        {
            n /= facbuf[2 * i];
            facbuf[(2 * i) + 1] = n;
        }
    }

    // opus_fft_impl: the in-place transform of the bit-reversed input; data holds interleaved real and imaginary parts from index at.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Impl(Span<float> data, int at)
    {
        Span<int> fstride = stackalloc int[9];
        int shift = _shift > 0 ? _shift : 0;
        fstride[0] = 1;
        int l = 0;
        int m;
        int p;
        do
        {
            p = _factors[2 * l];
            m = _factors[(2 * l) + 1];
            fstride[l + 1] = fstride[l] * p;
            l++;
        }
        while (m != 1);
        m = _factors[(2 * l) - 1];
        for (int i = l - 1; i >= 0; i--)
        {
            int m2 = i != 0 ? _factors[(2 * i) - 1] : 1;
            switch (_factors[2 * i])
            {
                case 2:
                    Bfly2(data, at, m, fstride[i]);
                    break;
                case 4:
                    Bfly4(data, at, fstride[i] << shift, m, fstride[i], m2);
                    break;
                case 3:
                    Bfly3(data, at, fstride[i] << shift, m, fstride[i], m2);
                    break;
                case 5:
                    Bfly5(data, at, fstride[i] << shift, m, fstride[i], m2);
                    break;
            }

            m = m2;
        }
    }

    private static void Bfly2(Span<float> d, int at, int m, int n)
    {
        int f = at * 2;
        if (m == 1)
        {
            for (int i = 0; i < n; i++)
            {
                float tr = d[f + 2];
                float ti = d[f + 3];
                d[f + 2] = d[f] - tr;
                d[f + 3] = d[f + 1] - ti;
                d[f] += tr;
                d[f + 1] += ti;
                f += 4;
            }
        }
        else
        {
            const float tw = 0.7071067812f;
            for (int i = 0; i < n; i++)
            {
                int o = f + 8;
                float tr = d[o];
                float ti = d[o + 1];
                d[o] = d[f] - tr;
                d[o + 1] = d[f + 1] - ti;
                d[f] += tr;
                d[f + 1] += ti;

                tr = (d[o + 2] + d[o + 3]) * tw;
                ti = (d[o + 3] - d[o + 2]) * tw;
                d[o + 2] = d[f + 2] - tr;
                d[o + 3] = d[f + 3] - ti;
                d[f + 2] += tr;
                d[f + 3] += ti;

                tr = d[o + 5];
                ti = -d[o + 4];
                d[o + 4] = d[f + 4] - tr;
                d[o + 5] = d[f + 5] - ti;
                d[f + 4] += tr;
                d[f + 5] += ti;

                tr = (d[o + 7] - d[o + 6]) * tw;
                ti = -(d[o + 7] + d[o + 6]) * tw;
                d[o + 6] = d[f + 6] - tr;
                d[o + 7] = d[f + 7] - ti;
                d[f + 6] += tr;
                d[f + 7] += ti;
                f += 16;
            }
        }
    }

    private static void Bfly4(Span<float> d, int at, int fstride, int m, int n, int mm)
    {
        ReadOnlySpan<float> tw = CeltTables.FftTwiddles;
        if (m == 1)
        {
            int f = at * 2;
            for (int i = 0; i < n; i++)
            {
                float s0r = d[f] - d[f + 4];
                float s0i = d[f + 1] - d[f + 5];
                d[f] += d[f + 4];
                d[f + 1] += d[f + 5];
                float s1r = d[f + 2] + d[f + 6];
                float s1i = d[f + 3] + d[f + 7];
                d[f + 4] = d[f] - s1r;
                d[f + 5] = d[f + 1] - s1i;
                d[f] += s1r;
                d[f + 1] += s1i;
                s1r = d[f + 2] - d[f + 6];
                s1i = d[f + 3] - d[f + 7];
                d[f + 2] = s0r + s1i;
                d[f + 3] = s0i - s1r;
                d[f + 6] = s0r - s1i;
                d[f + 7] = s0i + s1r;
                f += 8;
            }
        }
        else
        {
            int m2 = 2 * m;
            int m3 = 3 * m;
            for (int i = 0; i < n; i++)
            {
                int f = (at + (i * mm)) * 2;
                int t1 = 0;
                int t2 = 0;
                int t3 = 0;
                for (int j = 0; j < m; j++)
                {
                    float s0r = (d[f + (2 * m)] * tw[2 * t1]) - (d[f + (2 * m) + 1] * tw[(2 * t1) + 1]);
                    float s0i = (d[f + (2 * m)] * tw[(2 * t1) + 1]) + (d[f + (2 * m) + 1] * tw[2 * t1]);
                    float s1r = (d[f + (2 * m2)] * tw[2 * t2]) - (d[f + (2 * m2) + 1] * tw[(2 * t2) + 1]);
                    float s1i = (d[f + (2 * m2)] * tw[(2 * t2) + 1]) + (d[f + (2 * m2) + 1] * tw[2 * t2]);
                    float s2r = (d[f + (2 * m3)] * tw[2 * t3]) - (d[f + (2 * m3) + 1] * tw[(2 * t3) + 1]);
                    float s2i = (d[f + (2 * m3)] * tw[(2 * t3) + 1]) + (d[f + (2 * m3) + 1] * tw[2 * t3]);
                    float s5r = d[f] - s1r;
                    float s5i = d[f + 1] - s1i;
                    d[f] += s1r;
                    d[f + 1] += s1i;
                    float s3r = s0r + s2r;
                    float s3i = s0i + s2i;
                    float s4r = s0r - s2r;
                    float s4i = s0i - s2i;
                    d[f + (2 * m2)] = d[f] - s3r;
                    d[f + (2 * m2) + 1] = d[f + 1] - s3i;
                    t1 += fstride;
                    t2 += fstride * 2;
                    t3 += fstride * 3;
                    d[f] += s3r;
                    d[f + 1] += s3i;
                    d[f + (2 * m)] = s5r + s4i;
                    d[f + (2 * m) + 1] = s5i - s4r;
                    d[f + (2 * m3)] = s5r - s4i;
                    d[f + (2 * m3) + 1] = s5i + s4r;
                    f += 2;
                }
            }
        }
    }

    private static void Bfly3(Span<float> d, int at, int fstride, int m, int n, int mm)
    {
        ReadOnlySpan<float> tw = CeltTables.FftTwiddles;
        int m2 = 2 * m;
        float epi3i = tw[(2 * fstride * m) + 1];
        for (int i = 0; i < n; i++)
        {
            int f = (at + (i * mm)) * 2;
            int t1 = 0;
            int t2 = 0;
            for (int k = 0; k < m; k++)
            {
                float s1r = (d[f + (2 * m)] * tw[2 * t1]) - (d[f + (2 * m) + 1] * tw[(2 * t1) + 1]);
                float s1i = (d[f + (2 * m)] * tw[(2 * t1) + 1]) + (d[f + (2 * m) + 1] * tw[2 * t1]);
                float s2r = (d[f + (2 * m2)] * tw[2 * t2]) - (d[f + (2 * m2) + 1] * tw[(2 * t2) + 1]);
                float s2i = (d[f + (2 * m2)] * tw[(2 * t2) + 1]) + (d[f + (2 * m2) + 1] * tw[2 * t2]);
                float s3r = s1r + s2r;
                float s3i = s1i + s2i;
                float s0r = s1r - s2r;
                float s0i = s1i - s2i;
                t1 += fstride;
                t2 += fstride * 2;
                d[f + (2 * m)] = d[f] - (s3r * 0.5f);
                d[f + (2 * m) + 1] = d[f + 1] - (s3i * 0.5f);
                s0r *= epi3i;
                s0i *= epi3i;
                d[f] += s3r;
                d[f + 1] += s3i;
                d[f + (2 * m2)] = d[f + (2 * m)] + s0i;
                d[f + (2 * m2) + 1] = d[f + (2 * m) + 1] - s0r;
                d[f + (2 * m)] -= s0i;
                d[f + (2 * m) + 1] += s0r;
                f += 2;
            }
        }
    }

    private static void Bfly5(Span<float> d, int at, int fstride, int m, int n, int mm)
    {
        ReadOnlySpan<float> tw = CeltTables.FftTwiddles;
        float yar = tw[2 * fstride * m];
        float yai = tw[(2 * fstride * m) + 1];
        float ybr = tw[2 * fstride * 2 * m];
        float ybi = tw[(2 * fstride * 2 * m) + 1];
        for (int i = 0; i < n; i++)
        {
            int f0 = (at + (i * mm)) * 2;
            int f1 = f0 + (2 * m);
            int f2 = f0 + (4 * m);
            int f3 = f0 + (6 * m);
            int f4 = f0 + (8 * m);
            for (int u = 0; u < m; ++u)
            {
                float s0r = d[f0];
                float s0i = d[f0 + 1];
                int a = u * fstride;
                float s1r = (d[f1] * tw[2 * a]) - (d[f1 + 1] * tw[(2 * a) + 1]);
                float s1i = (d[f1] * tw[(2 * a) + 1]) + (d[f1 + 1] * tw[2 * a]);
                a = 2 * u * fstride;
                float s2r = (d[f2] * tw[2 * a]) - (d[f2 + 1] * tw[(2 * a) + 1]);
                float s2i = (d[f2] * tw[(2 * a) + 1]) + (d[f2 + 1] * tw[2 * a]);
                a = 3 * u * fstride;
                float s3r = (d[f3] * tw[2 * a]) - (d[f3 + 1] * tw[(2 * a) + 1]);
                float s3i = (d[f3] * tw[(2 * a) + 1]) + (d[f3 + 1] * tw[2 * a]);
                a = 4 * u * fstride;
                float s4r = (d[f4] * tw[2 * a]) - (d[f4 + 1] * tw[(2 * a) + 1]);
                float s4i = (d[f4] * tw[(2 * a) + 1]) + (d[f4 + 1] * tw[2 * a]);

                float s7r = s1r + s4r;
                float s7i = s1i + s4i;
                float s10r = s1r - s4r;
                float s10i = s1i - s4i;
                float s8r = s2r + s3r;
                float s8i = s2i + s3i;
                float s9r = s2r - s3r;
                float s9i = s2i - s3i;

                d[f0] += s7r + s8r;
                d[f0 + 1] += s7i + s8i;

                float s5r = s0r + ((s7r * yar) + (s8r * ybr));
                float s5i = s0i + ((s7i * yar) + (s8i * ybr));
                float s6r = (s10i * yai) + (s9i * ybi);
                float s6i = -((s10r * yai) + (s9r * ybi));
                d[f1] = s5r - s6r;
                d[f1 + 1] = s5i - s6i;
                d[f4] = s5r + s6r;
                d[f4 + 1] = s5i + s6i;

                float s11r = s0r + ((s7r * ybr) + (s8r * yar));
                float s11i = s0i + ((s7i * ybr) + (s8i * yar));
                float s12r = (s9i * yai) - (s10i * ybi);
                float s12i = (s10r * ybi) - (s9r * yai);
                d[f2] = s11r + s12r;
                d[f2 + 1] = s11i + s12i;
                d[f3] = s11r - s12r;
                d[f3 + 1] = s11i - s12i;

                f0 += 2;
                f1 += 2;
                f2 += 2;
                f3 += 2;
                f4 += 2;
            }
        }
    }
}

// clt_mdct_backward of mdct.c: the inverse MDCT of a CELT frame (or of one short block of it), windowed and overlapped into the output in place.
internal static class Mdct
{
    private const int N = 1920;
    private static readonly KissFft[] Ffts =
    [
        new KissFft(480, -1, [.. CeltTables.FftBitrev480]),
        new KissFft(240, 1, [.. CeltTables.FftBitrev240]),
        new KissFft(120, 2, [.. CeltTables.FftBitrev120]),
        new KissFft(60, 3, [.. CeltTables.FftBitrev60]),
    ];

    // in: the coefficients, read from inAt with the stride of the interleaved short blocks; output: written from outAt (the overlap's first half is at outAt, the frame after
    // overlap / 2). overlap is 120 for every frame size of the 48 kHz mode.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Backward(ReadOnlySpan<float> input, int inAt, Span<float> output, int outAt, int overlap, int shift, int stride)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        ReadOnlySpan<float> trig = CeltTables.MdctTwiddles;
        int n = N;
        int trigAt = 0;
        for (int i = 0; i < shift; i++)
        {
            n >>= 1;
            trigAt += n;
        }

        int n2 = n >> 1;
        int n4 = n >> 2;
        KissFft fft = Ffts[shift];
        int yp = outAt + (overlap >> 1);
        {
            int xp1 = inAt;
            int xp2 = inAt + (stride * (n2 - 1));
            ReadOnlySpan<short> bitrev = fft.Bitrev;
            for (int i = 0; i < n4; i++)
            {
                int rev = bitrev[i];
                float yr = (input[xp2] * trig[trigAt + i]) + (input[xp1] * trig[trigAt + n4 + i]);
                float yi = (input[xp1] * trig[trigAt + i]) - (input[xp2] * trig[trigAt + n4 + i]);
                output[yp + (2 * rev) + 1] = yr;
                output[yp + (2 * rev)] = yi;
                xp1 += 2 * stride;
                xp2 -= 2 * stride;
            }
        }

        fft.Impl(output, yp / 2);
        {
            int yp0 = yp;
            int yp1 = yp + n2 - 2;
            for (int i = 0; i < (n4 + 1) >> 1; i++)
            {
                float re = output[yp0 + 1];
                float im = output[yp0];
                float t0 = trig[trigAt + i];
                float t1 = trig[trigAt + n4 + i];
                float yr = (re * t0) + (im * t1);
                float yi = (re * t1) - (im * t0);
                re = output[yp1 + 1];
                im = output[yp1];
                output[yp0] = yr;
                output[yp1 + 1] = yi;
                t0 = trig[trigAt + n4 - i - 1];
                t1 = trig[trigAt + n2 - i - 1];
                yr = (re * t0) + (im * t1);
                yi = (re * t1) - (im * t0);
                output[yp1] = yr;
                output[yp0 + 1] = yi;
                yp0 += 2;
                yp1 -= 2;
            }
        }

        {
            int xp1 = outAt + overlap - 1;
            int yp1 = outAt;
            int wp1 = 0;
            int wp2 = overlap - 1;
            for (int i = 0; i < overlap / 2; i++)
            {
                float x1 = output[xp1];
                float x2 = output[yp1];
                output[yp1++] = (window[wp2] * x2) - (window[wp1] * x1);
                output[xp1--] = (window[wp1] * x2) + (window[wp2] * x1);
                wp1++;
                wp2--;
            }
        }
    }
}
