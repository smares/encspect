using System.Runtime.CompilerServices;
namespace Encspect.Formats.Opus.Decoding;

// The concealment of a CELT frame (celt_decode_lost of celt_decoder.c, with pitch.c and celt_lpc.c of libopus 1.5.2: BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md), which the
// analyzer runs only for the 5 ms that RFC 6716, section 4.5, has a decoder synthesize at a change of mode without redundancy (docs/design/lossy-decoders.md, decision 6).
// A frame that is lost is a gap and is never concealed.
internal sealed partial class CeltDecoder
{
    private const int PlcPitchLagMax = 720;
    private const int PlcPitchLagMin = 100;

    private readonly float[] _exc = new float[MaxPeriod + LpcOrder];
    private readonly float[] _firTmp = new float[MaxPeriod];
    private readonly float[] _lpPitch = new float[DecodeBufferSize >> 1];
    private readonly float[] _plcX = new float[2 * 960];

    // celt_decode_lost followed by the de-emphasis, for a frame of n samples (a multiple of 120 up to 960).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void DecodeLost(int n, float[][] pcm, int pcmAt)
    {
        Flush();
        int lm = n switch { 120 => 0, 240 => 1, 480 => 2, _ => 3 };
        int c = Channels;
        int start = Start;
        int lossDuration = _lossDuration;
        bool noiseBased = lossDuration >= 40 || start != 0 || _skipPlc;
        ReadOnlySpan<short> eBands = CeltTables.EBand5Ms;
        int outSyn = DecodeBufferSize - n;
        if (noiseBased)
        {
            int end = End;
            int effEnd = Math.Max(start, Math.Min(end, Bands));
            Span<float> x = _plcX.AsSpan(0, c * n);
            for (int ch = 0; ch < c; ch++)
            {
                float[] mem = _decodeMem[ch];
                Array.Copy(mem, n, mem, 0, DecodeBufferSize - n + Overlap);
            }

            if (_prefilterAndFold)
            {
                PrefilterAndFold(n);
            }

            float decay = lossDuration == 0 ? 1.5f : 0.5f;
            for (int ch = 0; ch < c; ch++)
            {
                for (int i = start; i < end; i++)
                {
                    _oldBandE[(ch * Bands) + i] = Math.Max(_backgroundLogE[(ch * Bands) + i], _oldBandE[(ch * Bands) + i] - decay);
                }
            }

            uint seed = _rng;
            for (int ch = 0; ch < c; ch++)
            {
                for (int i = start; i < effEnd; i++)
                {
                    int boffs = (n * ch) + (eBands[i] << lm);
                    int blen = (eBands[i + 1] - eBands[i]) << lm;
                    for (int j = 0; j < blen; j++)
                    {
                        seed = unchecked((1664525 * seed) + 1013904223);
                        x[boffs + j] = (int)seed >> 20;
                    }

                    CeltBands.RenormaliseVector(x.Slice(boffs, blen), 1.0f);
                }
            }

            _rng = seed;
            Synthesize(_plcX, start, effEnd, c, c, false, lm, false);
            _prefilterAndFold = false;
            _skipPlc = true;
        }
        else
        {
            float fade = 1.0f;
            int pitchIndex;
            if (lossDuration == 0)
            {
                _lastPitchIndex = pitchIndex = PlcPitchSearch(c);
            }
            else
            {
                pitchIndex = _lastPitchIndex;
                fade = 0.8f;
            }

            int excLength = Math.Min(2 * pitchIndex, MaxPeriod);
            float[] excBuf = _exc;
            ReadOnlySpan<float> window = CeltTables.Window120;
            Span<float> ac = stackalloc float[LpcOrder + 1];
            Span<float> lpcMem = stackalloc float[LpcOrder];
            for (int ch = 0; ch < c; ch++)
            {
                float[] buf = _decodeMem[ch];
                float s1 = 0;
                for (int i = 0; i < MaxPeriod + LpcOrder; i++)
                {
                    excBuf[i] = buf[DecodeBufferSize - MaxPeriod - LpcOrder + i];
                }

                Span<float> lpc = _lpc.AsSpan(ch * LpcOrder, LpcOrder);
                if (lossDuration == 0)
                {
                    Autocorr(excBuf, LpcOrder, ac, window, Overlap, LpcOrder, MaxPeriod);
                    ac[0] *= 1.0001f;
                    for (int i = 1; i <= LpcOrder; i++)
                    {
                        ac[i] -= ac[i] * (0.008f * 0.008f) * i * i;
                    }

                    Lpc(lpc, ac, LpcOrder);
                }

                Fir(excBuf, LpcOrder + MaxPeriod - excLength, lpc, _firTmp, excLength, LpcOrder);
                Array.Copy(_firTmp, 0, excBuf, LpcOrder + MaxPeriod - excLength, excLength);
                float e1 = 1;
                float e2 = 1;
                int decayLength = excLength >> 1;
                for (int i = 0; i < decayLength; i++)
                {
                    float e = excBuf[LpcOrder + MaxPeriod - decayLength + i];
                    e1 += e * e;
                    e = excBuf[LpcOrder + MaxPeriod - (2 * decayLength) + i];
                    e2 += e * e;
                }

                e1 = Math.Min(e1, e2);
                float decay = (float)Math.Sqrt(e1 / e2);
                Array.Copy(buf, n, buf, 0, DecodeBufferSize - n);
                int extrapolationOffset = MaxPeriod - pitchIndex;
                int extrapolationLen = n + Overlap;
                float attenuation = fade * decay;
                int jj = 0;
                for (int i = 0; i < extrapolationLen; i++, jj++)
                {
                    if (jj >= pitchIndex)
                    {
                        jj -= pitchIndex;
                        attenuation *= decay;
                    }

                    buf[outSyn + i] = attenuation * excBuf[LpcOrder + extrapolationOffset + jj];
                    float tmp = buf[DecodeBufferSize - MaxPeriod - n + extrapolationOffset + jj];
                    s1 += tmp * tmp;
                }

                for (int i = 0; i < LpcOrder; i++)
                {
                    lpcMem[i] = buf[outSyn - 1 - i];
                }

                Iir(buf, outSyn, lpc, extrapolationLen, LpcOrder, lpcMem);
                float s2 = 0;
                for (int i = 0; i < extrapolationLen; i++)
                {
                    float tmp = buf[outSyn + i];
                    s2 += tmp * tmp;
                }

                if (!(s1 > 0.2f * s2))
                {
                    for (int i = 0; i < extrapolationLen; i++)
                    {
                        buf[outSyn + i] = 0;
                    }
                }
                else if (s1 < s2)
                {
                    float ratio = (float)Math.Sqrt((s1 + 1) / (s2 + 1));
                    for (int i = 0; i < Overlap; i++)
                    {
                        float tmpG = 1.0f - (window[i] * (1.0f - ratio));
                        buf[outSyn + i] = tmpG * buf[outSyn + i];
                    }

                    for (int i = Overlap; i < extrapolationLen; i++)
                    {
                        buf[outSyn + i] = ratio * buf[outSyn + i];
                    }
                }
            }

            _prefilterAndFold = true;
        }

        _lossDuration = Math.Min(10000, lossDuration + (1 << lm));
        Deemphasis(pcm, pcmAt, n);
    }

    // celt_plc_pitch_search: pitch_downsample, pitch_search.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int PlcPitchSearch(int c)
    {
        float[] xLp = _lpPitch;
        PitchDownsample(c, xLp, DecodeBufferSize);
        PitchSearch(xLp, PlcPitchLagMax >> 1, DecodeBufferSize - PlcPitchLagMax, PlcPitchLagMax - PlcPitchLagMin, out int pitch);
        return PlcPitchLagMax - pitch;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void PitchDownsample(int c, float[] xLp, int len)
    {
        float[] x0 = _decodeMem[0];
        for (int i = 1; i < len >> 1; i++)
        {
            xLp[i] = (0.25f * x0[(2 * i) - 1]) + (0.25f * x0[(2 * i) + 1]) + (0.5f * x0[2 * i]);
        }

        xLp[0] = (0.25f * x0[1]) + (0.5f * x0[0]);
        if (c == 2)
        {
            float[] x1 = _decodeMem[1];
            for (int i = 1; i < len >> 1; i++)
            {
                xLp[i] += (0.25f * x1[(2 * i) - 1]) + (0.25f * x1[(2 * i) + 1]) + (0.5f * x1[2 * i]);
            }

            xLp[0] += (0.25f * x1[1]) + (0.5f * x1[0]);
        }

        Span<float> ac = stackalloc float[5];
        Autocorr(xLp, 0, ac, default, 0, 4, len >> 1);
        ac[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++)
        {
            ac[i] -= ac[i] * (0.008f * i) * (0.008f * i);
        }

        Span<float> lpc = stackalloc float[4];
        Lpc(lpc, ac, 4);
        float tmp = 1.0f;
        for (int i = 0; i < 4; i++)
        {
            tmp = 0.9f * tmp;
            lpc[i] = lpc[i] * tmp;
        }

        const float c1 = 0.8f;
        Span<float> lpc2 = stackalloc float[5];
        lpc2[0] = lpc[0] + 0.8f;
        lpc2[1] = lpc[1] + (c1 * lpc[0]);
        lpc2[2] = lpc[2] + (c1 * lpc[1]);
        lpc2[3] = lpc[3] + (c1 * lpc[2]);
        lpc2[4] = c1 * lpc[3];
        float mem0 = 0;
        float mem1 = 0;
        float mem2 = 0;
        float mem3 = 0;
        float mem4 = 0;
        for (int i = 0; i < len >> 1; i++)
        {
            float sum = xLp[i];
            sum += lpc2[0] * mem0;
            sum += lpc2[1] * mem1;
            sum += lpc2[2] * mem2;
            sum += lpc2[3] * mem3;
            sum += lpc2[4] * mem4;
            mem4 = mem3;
            mem3 = mem2;
            mem2 = mem1;
            mem1 = mem0;
            mem0 = xLp[i];
            xLp[i] = sum;
        }
    }

    // pitch_search (pitch.c), float: the reference x_lp is buf from xLpAt and y is buf from 0, len samples of the reference against len + maxPitch of the signal.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PitchSearch(float[] buf, int xLpAt, int len, int maxPitch, out int pitch)
    {
        int lag = len + maxPitch;
        var xLp4 = new float[len >> 2];
        var yLp4 = new float[lag >> 2];
        var xcorr = new float[maxPitch >> 1];
        for (int j = 0; j < len >> 2; j++)
        {
            xLp4[j] = buf[xLpAt + (2 * j)];
        }

        for (int j = 0; j < lag >> 2; j++)
        {
            yLp4[j] = buf[2 * j];
        }

        Span<int> best = stackalloc int[2];
        for (int i = 0; i < maxPitch >> 2; i++)
        {
            float sum = 0;
            for (int j = 0; j < len >> 2; j++)
            {
                sum += xLp4[j] * yLp4[i + j];
            }

            xcorr[i] = sum;
        }

        FindBestPitch(xcorr, yLp4, len >> 2, maxPitch >> 2, best);
        for (int i = 0; i < maxPitch >> 1; i++)
        {
            xcorr[i] = 0;
            if (Math.Abs(i - (2 * best[0])) > 2 && Math.Abs(i - (2 * best[1])) > 2)
            {
                continue;
            }

            float sum = 0;
            for (int j = 0; j < len >> 1; j++)
            {
                sum += buf[xLpAt + j] * buf[i + j];
            }

            xcorr[i] = Math.Max(-1.0f, sum);
        }

        FindBestPitch(xcorr, buf, len >> 1, maxPitch >> 1, best);
        int offset;
        if (best[0] > 0 && best[0] < (maxPitch >> 1) - 1)
        {
            float a = xcorr[best[0] - 1];
            float b = xcorr[best[0]];
            float c = xcorr[best[0] + 1];
            if (c - a > 0.7f * (b - a))
            {
                offset = 1;
            }
            else if (a - c > 0.7f * (b - c))
            {
                offset = -1;
            }
            else
            {
                offset = 0;
            }
        }
        else
        {
            offset = 0;
        }

        pitch = (2 * best[0]) - offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FindBestPitch(float[] xcorr, float[] y, int len, int maxPitch, Span<int> bestPitch)
    {
        float syy = 1;
        Span<float> bestNum = stackalloc float[2] { -1, -1 };
        Span<float> bestDen = stackalloc float[2] { 0, 0 };
        bestPitch[0] = 0;
        bestPitch[1] = 1;
        for (int j = 0; j < len; j++)
        {
            syy += y[j] * y[j];
        }

        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0)
            {
                float xcorr16 = xcorr[i] * 1e-12f;
                float num = xcorr16 * xcorr16;
                if (num * bestDen[1] > bestNum[1] * syy)
                {
                    if (num * bestDen[0] > bestNum[0] * syy)
                    {
                        bestNum[1] = bestNum[0];
                        bestDen[1] = bestDen[0];
                        bestPitch[1] = bestPitch[0];
                        bestNum[0] = num;
                        bestDen[0] = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum[1] = num;
                        bestDen[1] = syy;
                        bestPitch[1] = i;
                    }
                }
            }

            syy += (y[i + len] * y[i + len]) - (y[i] * y[i]);
            syy = Math.Max(1.0f, syy);
        }
    }

    // _celt_autocorr (celt_lpc.c), float: x from xAt, n values, the first and last overlap of them windowed.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Autocorr(float[] x, int xAt, Span<float> ac, ReadOnlySpan<float> window, int overlap, int lag, int n)
    {
        var xx = new float[n];
        int fastN = n - lag;
        for (int i = 0; i < n; i++)
        {
            xx[i] = x[xAt + i];
        }

        for (int i = 0; i < overlap; i++)
        {
            xx[i] = x[xAt + i] * window[i];
            xx[n - i - 1] = x[xAt + n - i - 1] * window[i];
        }

        for (int k = 0; k <= lag; k++)
        {
            float sum = 0;
            for (int j = 0; j < fastN; j++)
            {
                sum += xx[j] * xx[j + k];
            }

            ac[k] = sum;
        }

        for (int k = 0; k <= lag; k++)
        {
            float d = 0;
            for (int i = k + fastN; i < n; i++)
            {
                d += xx[i] * xx[i - k];
            }

            ac[k] += d;
        }
    }

    // _celt_lpc (celt_lpc.c), float: the Levinson-Durbin recursion.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Lpc(Span<float> lpc, ReadOnlySpan<float> ac, int p)
    {
        lpc.Slice(0, p).Clear();
        float error = ac[0];
        if (ac[0] > 1e-10f)
        {
            for (int i = 0; i < p; i++)
            {
                float rr = 0;
                for (int j = 0; j < i; j++)
                {
                    rr += lpc[j] * ac[i - j];
                }

                rr += ac[i + 1];
                float r = -(rr / error);
                lpc[i] = r;
                for (int j = 0; j < (i + 1) >> 1; j++)
                {
                    float tmp1 = lpc[j];
                    float tmp2 = lpc[i - 1 - j];
                    lpc[j] = tmp1 + (r * tmp2);
                    lpc[i - 1 - j] = tmp2 + (r * tmp1);
                }

                error = error - (r * r * error);
                if (error <= 0.001f * ac[0])
                {
                    break;
                }
            }
        }
    }

    // celt_fir (celt_lpc.c), float: y[i] = x[i] + sum of num[k] * x[i - 1 - k], x read from xAt (with the order's samples before it).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fir(float[] x, int xAt, ReadOnlySpan<float> num, float[] y, int n, int ord)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[xAt + i];
            for (int j = 0; j < ord; j++)
            {
                sum += num[ord - j - 1] * x[xAt + i + j - ord];
            }

            y[i] = sum;
        }
    }

    // celt_iir (celt_lpc.c), float, in place: the output is x[i] less the filter's taps on the outputs before it; mem holds the last outputs, newest first.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Iir(float[] buf, int at, ReadOnlySpan<float> den, int n, int ord, Span<float> mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = buf[at + i];
            for (int j = 0; j < ord; j++)
            {
                sum -= den[j] * mem[j];
            }

            for (int j = ord - 1; j >= 1; j--)
            {
                mem[j] = mem[j - 1];
            }

            mem[0] = sum;
            buf[at + i] = sum;
        }
    }
}
