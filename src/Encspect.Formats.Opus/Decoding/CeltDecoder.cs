using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Opus.Decoding;

// The reconstruction of CELT (RFC 6716, section 4.3): the energies, the denormalization, the anti-collapse, the inverse MDCT with its overlap, the postfilter and the
// de-emphasis, from the integers the entropy pass stored (CeltSyntax). It follows celt_decoder.c, quant_bands.c, bands.c and celt.c of libopus 1.5.2 (BSD-3-Clause,
// notice in THIRD-PARTY-NOTICES.md) in single precision, in the order of the reference, so that the output is the same on every platform. The decoder holds the state that
// crosses frames: the history of the output (the overlap and the postfilter's memory), the energies of the frames before (the coarse energy prediction and the
// anti-collapse), the postfilter's parameters, the random state and the de-emphasis memory. Where the reference conceals a lost frame (celt_decode_lost, PlcLost) the decoder
// does it only for the 5 ms that RFC 6716, section 4.5, recommends at a change of mode.
internal sealed partial class CeltDecoder(int channels)
{
    public const int DecodeBufferSize = 2048;
    public const int Overlap = 120;
    private const int Bands = 21;
    private const int LpcOrder = 24;
    private const int MaxPeriod = 1024;
    private const float Preemph = 0.85000610f;
    private const float VerySmall = 1e-30f;

    private readonly float[][] _decodeMem = [new float[DecodeBufferSize + Overlap], new float[DecodeBufferSize + Overlap]];
    private readonly float[] _lpc = new float[2 * LpcOrder];
    private readonly float[] _oldBandE = new float[2 * Bands];
    private readonly float[] _oldLogE = new float[2 * Bands];
    private readonly float[] _oldLogE2 = new float[2 * Bands];
    private readonly float[] _backgroundLogE = new float[2 * Bands];
    private readonly float[] _preemphMemD = new float[2];
    private readonly float[] _x = new float[2 * 960];
    private readonly float[] _freq = new float[960];
    private readonly float[] _etmp = new float[Overlap];
    private readonly int[] _tfRes = new int[Bands];
    private readonly CeltBands _bands = new();
    private uint _rng;
    private int _lastPitchIndex;
    private int _lossDuration;
    private bool _skipPlc;
    private int _postfilterPeriod;
    private int _postfilterPeriodOld;
    private float _postfilterGain;
    private float _postfilterGainOld;
    private int _postfilterTapset;
    private int _postfilterTapsetOld;
    private bool _prefilterAndFold;

    public int Channels { get; } = channels;

    public int StreamChannels { get; set; } = channels;

    public int Start { get; set; }

    public int End { get; set; } = Bands;

    public bool DisableInv { get; set; } = channels == 1;

    public uint Rng => _rng;

    // OPUS_RESET_STATE: everything from the random state on starts at zero, the logs of the energies at -28.
    public void Reset()
    {
        for (int c = 0; c < 2; c++)
        {
            Array.Clear(_decodeMem[c]);
        }

        Array.Clear(_lpc);
        Array.Clear(_oldBandE);
        Array.Clear(_backgroundLogE);
        Array.Clear(_preemphMemD);
        _rng = 0;
        _lastPitchIndex = 0;
        _lossDuration = 0;
        _postfilterPeriod = 0;
        _postfilterPeriodOld = 0;
        _postfilterGain = 0;
        _postfilterGainOld = 0;
        _postfilterTapset = 0;
        _postfilterTapsetOld = 0;
        _prefilterAndFold = false;
        Array.Fill(_oldLogE, -28.0f);
        Array.Fill(_oldLogE2, -28.0f);
        _skipPlc = true;
    }

    // The determinism rule for recursive state: values below 1e-30 are flushed at the start of each frame.
    private void Flush()
    {
        for (int c = 0; c < Channels; c++)
        {
            float[] mem = _decodeMem[c];
            for (int i = 0; i < mem.Length; i++)
            {
                if (Math.Abs(mem[i]) < 1e-30f)
                {
                    mem[i] = 0;
                }
            }
        }

        for (int c = 0; c < 2; c++)
        {
            if (Math.Abs(_preemphMemD[c]) < 1e-30f)
            {
                _preemphMemD[c] = 0;
            }
        }
    }

    private static float Exp2(float x) => (float)DetMath.Exp(0.6931471805599453094 * x);

    // Decodes the frame of the record r at r's position: planar output, channels * N samples scaled to full scale 1.0, to pcm[c] from pcmAt.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Decode(ref IntReader r, float[][] pcm, int pcmAt)
    {
        Flush();
        int flags = r.Next();
        int lm = r.Next();
        int start = r.Next();
        int end = r.Next();
        int c = r.Next();
        Start = start;
        End = end;
        StreamChannels = c;
        int cc = Channels;
        int m = 1 << lm;
        int n = m * 120;
        int effEnd = Math.Min(end, Bands);
        bool silence = (flags & 1) != 0;
        bool isTransient = (flags & 2) != 0;
        bool intra = (flags & 4) != 0;
        bool antiCollapse = (flags & 8) != 0;
        bool dualStereo = (flags & 16) != 0;
        int postfilterPitch = r.Next();
        float postfilterGain = 0.09375f * r.Next();
        int postfilterTapset = r.Next();
        int spread = r.Next();
        int intensity = r.Next();
        int count = end - start;
        ReadOnlySpan<int> tf = r.Take(count);
        ReadOnlySpan<int> coarse = r.Take(count * c);
        ReadOnlySpan<int> fineQuant = r.Take(count);
        int fineCount = 0;
        for (int i = 0; i < count; i++)
        {
            fineCount += fineQuant[i] > 0 ? c : 0;
        }

        ReadOnlySpan<int> fineBits = r.Take(fineCount);
        ReadOnlySpan<int> pulses = r.Take(count);
        int bandsLength = r.Next();
        ReadOnlySpan<int> bandsData = r.Take(bandsLength);
        ReadOnlySpan<int> finalBits = r.Take(count * c);
        ReadOnlySpan<int> collapse = r.Take(count * c);
        uint finalRange = r.NextUInt();

        if (_lossDuration == 0)
        {
            _skipPlc = false;
        }

        if (c == 1)
        {
            for (int i = 0; i < Bands; i++)
            {
                _oldBandE[i] = Math.Max(_oldBandE[i], _oldBandE[Bands + i]);
            }
        }

        if (!intra && _lossDuration != 0)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                float safety = 0;
                int missing = Math.Min(10, _lossDuration >> lm);
                if (lm == 0)
                {
                    safety = 1.5f;
                }
                else if (lm == 1)
                {
                    safety = 0.5f;
                }

                for (int i = start; i < end; i++)
                {
                    int at = (ch * Bands) + i;
                    if (_oldBandE[at] < Math.Max(_oldLogE[at], _oldLogE2[at]))
                    {
                        float e0 = _oldBandE[at];
                        float e1 = _oldLogE[at];
                        float e2 = _oldLogE2[at];
                        float slope = Math.Max(e1 - e0, 0.5f * (e2 - e0));
                        e0 -= Math.Max(0, (1 + missing) * slope);
                        _oldBandE[at] = Math.Max(-20.0f, e0);
                    }
                    else
                    {
                        _oldBandE[at] = Math.Min(Math.Min(_oldBandE[at], _oldLogE[at]), _oldLogE2[at]);
                    }

                    _oldBandE[at] -= safety;
                }
            }
        }

        // unquant_coarse_energy
        {
            float coef;
            float beta;
            if (intra)
            {
                coef = 0;
                beta = 4915 / 32768.0f;
            }
            else
            {
                ReadOnlySpan<float> predCoef = [29440 / 32768.0f, 26112 / 32768.0f, 21248 / 32768.0f, 16384 / 32768.0f];
                ReadOnlySpan<float> betaCoef = [30147 / 32768.0f, 22282 / 32768.0f, 12124 / 32768.0f, 6554 / 32768.0f];
                beta = betaCoef[lm];
                coef = predCoef[lm];
            }

            Span<float> prev = stackalloc float[2];
            for (int i = start; i < end; i++)
            {
                for (int ch = 0; ch < c; ch++)
                {
                    float q = coarse[((i - start) * c) + ch];
                    int at = i + (ch * Bands);
                    _oldBandE[at] = Math.Max(-9.0f, _oldBandE[at]);
                    float tmp = (coef * _oldBandE[at]) + prev[ch] + q;
                    _oldBandE[at] = tmp;
                    prev[ch] = prev[ch] + q - (beta * q);
                }
            }
        }

        // unquant_fine_energy
        {
            int at = 0;
            for (int i = start; i < end; i++)
            {
                int fq = fineQuant[i - start];
                if (fq <= 0)
                {
                    continue;
                }

                for (int ch = 0; ch < c; ch++)
                {
                    int q2 = fineBits[at++];
                    float offset = ((q2 + 0.5f) * (1 << (14 - fq)) * (1.0f / 16384)) - 0.5f;
                    _oldBandE[i + (ch * Bands)] += offset;
                }
            }
        }

        for (int ch = 0; ch < cc; ch++)
        {
            float[] mem = _decodeMem[ch];
            Array.Copy(mem, n, mem, 0, DecodeBufferSize - n + Overlap);
        }

        for (int i = 0; i < count; i++)
        {
            _tfRes[i] = tf[i];
        }

        Span<float> x = _x.AsSpan(0, c * n);
        x.Clear();
        var bandsReader = new IntReader(bandsData);
        _bands.Replay(ref bandsReader, _x, start, end, lm, c, isTransient, spread, dualStereo, intensity, _tfRes.AsSpan(0, count), ref _rng);

        // unquant_energy_finalise
        for (int i = start; i < end; i++)
        {
            for (int ch = 0; ch < c; ch++)
            {
                int q2 = finalBits[((i - start) * c) + ch];
                if (q2 >= 0)
                {
                    _oldBandE[i + (ch * Bands)] += (q2 - 0.5f) * (1 << (14 - fineQuant[i - start] - 1)) * (1.0f / 16384);
                }
            }
        }

        if (antiCollapse)
        {
            AntiCollapse(_x, collapse, lm, c, n, start, end, pulses);
        }

        if (silence)
        {
            for (int i = 0; i < c * Bands; i++)
            {
                _oldBandE[i] = -28.0f;
            }
        }

        if (_prefilterAndFold)
        {
            PrefilterAndFold(n);
        }

        Synthesize(_x, start, effEnd, c, cc, isTransient, lm, silence);
        for (int ch = 0; ch < cc; ch++)
        {
            float[] mem = _decodeMem[ch];
            int outSyn = DecodeBufferSize - n;
            _postfilterPeriod = Math.Max(_postfilterPeriod, 15);
            _postfilterPeriodOld = Math.Max(_postfilterPeriodOld, 15);
            CombFilter(mem, outSyn, mem, outSyn, _postfilterPeriodOld, _postfilterPeriod, 120, _postfilterGainOld, _postfilterGain, _postfilterTapsetOld, _postfilterTapset, true, Overlap);
            if (lm != 0)
            {
                CombFilter(mem, outSyn + 120, mem, outSyn + 120, _postfilterPeriod, postfilterPitch, n - 120, _postfilterGain, postfilterGain, _postfilterTapset, postfilterTapset, true, Overlap);
            }
        }

        _postfilterPeriodOld = _postfilterPeriod;
        _postfilterGainOld = _postfilterGain;
        _postfilterTapsetOld = _postfilterTapset;
        _postfilterPeriod = postfilterPitch;
        _postfilterGain = postfilterGain;
        _postfilterTapset = postfilterTapset;
        if (lm != 0)
        {
            _postfilterPeriodOld = _postfilterPeriod;
            _postfilterGainOld = _postfilterGain;
            _postfilterTapsetOld = _postfilterTapset;
        }

        if (c == 1)
        {
            Array.Copy(_oldBandE, 0, _oldBandE, Bands, Bands);
        }

        if (!isTransient)
        {
            Array.Copy(_oldLogE, _oldLogE2, 2 * Bands);
            Array.Copy(_oldBandE, _oldLogE, 2 * Bands);
        }
        else
        {
            for (int i = 0; i < 2 * Bands; i++)
            {
                _oldLogE[i] = Math.Min(_oldLogE[i], _oldBandE[i]);
            }
        }

        float maxBackgroundIncrease = Math.Min(160, _lossDuration + m) * 0.001f;
        for (int i = 0; i < 2 * Bands; i++)
        {
            _backgroundLogE[i] = Math.Min(_backgroundLogE[i] + maxBackgroundIncrease, _oldBandE[i]);
        }

        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = 0; i < start; i++)
            {
                _oldBandE[(ch * Bands) + i] = 0;
                _oldLogE[(ch * Bands) + i] = _oldLogE2[(ch * Bands) + i] = -28.0f;
            }

            for (int i = end; i < Bands; i++)
            {
                _oldBandE[(ch * Bands) + i] = 0;
                _oldLogE[(ch * Bands) + i] = _oldLogE2[(ch * Bands) + i] = -28.0f;
            }
        }

        _rng = finalRange;
        Deemphasis(pcm, pcmAt, n);
        _lossDuration = 0;
        _prefilterAndFold = false;
    }

    // anti_collapse (bands.c).
    private void AntiCollapse(float[] x, ReadOnlySpan<int> collapseMasks, int lm, int c, int size, int start, int end, ReadOnlySpan<int> pulses)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBand5Ms;
        uint seed = _rng;
        for (int i = start; i < end; i++)
        {
            int n0 = eBands[i + 1] - eBands[i];
            int depth = (int)((uint)(1 + pulses[i - start]) / (uint)n0) >> lm;
            float thresh = 0.5f * Exp2(-0.125f * depth);
            float sqrt1 = 1.0f / (float)Math.Sqrt(n0 << lm);
            for (int ch = 0; ch < c; ch++)
            {
                float prev1 = _oldLogE[(ch * Bands) + i];
                float prev2 = _oldLogE2[(ch * Bands) + i];
                if (c == 1)
                {
                    prev1 = Math.Max(prev1, _oldLogE[Bands + i]);
                    prev2 = Math.Max(prev2, _oldLogE2[Bands + i]);
                }

                float ediff = _oldBandE[(ch * Bands) + i] - Math.Min(prev1, prev2);
                ediff = Math.Max(0, ediff);
                float r = 2.0f * Exp2(-ediff);
                if (lm == 3)
                {
                    r *= 1.41421356f;
                }

                r = Math.Min(thresh, r);
                r *= sqrt1;
                int at = (ch * size) + (eBands[i] << lm);
                bool renormalize = false;
                for (int k = 0; k < 1 << lm; k++)
                {
                    if ((collapseMasks[((i - start) * c) + ch] & (1 << k)) == 0)
                    {
                        for (int j = 0; j < n0; j++)
                        {
                            seed = unchecked((1664525 * seed) + 1013904223);
                            x[at + (j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }

                        renormalize = true;
                    }
                }

                if (renormalize)
                {
                    CeltBands.RenormaliseVector(x.AsSpan(at, n0 << lm), 1.0f);
                }
            }
        }
    }

    // denormalise_bands (bands.c) into freq[0..n).
    private void Denormalise(float[] x, int xAt, Span<float> freq, int oldAt, int start, int end, int m, bool silence)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBand5Ms;
        int n = m * 120;
        int bound = m * eBands[end];
        if (silence)
        {
            bound = 0;
            start = end = 0;
        }

        int f = 0;
        int xi = xAt + (m * eBands[start]);
        for (int i = 0; i < m * eBands[start]; i++)
        {
            freq[f++] = 0;
        }

        for (int i = start; i < end; i++)
        {
            int j = m * eBands[i];
            int bandEnd = m * eBands[i + 1];
            float lg = _oldBandE[oldAt + i] + CeltTables.EMeans[i];
            float g = Exp2(Math.Min(32.0f, lg));
            do
            {
                freq[f++] = x[xi++] * g;
            }
            while (++j < bandEnd);
        }

        freq.Slice(bound, n - bound).Clear();
    }

    // celt_synthesis (celt_decoder.c): the inverse MDCT of every channel into the end of the history.
    private void Synthesize(float[] x, int start, int effEnd, int c, int cc, bool isTransient, int lm, bool silence)
    {
        int m = 1 << lm;
        int n = 120 << lm;
        int b;
        int nb;
        int shift;
        if (isTransient)
        {
            b = m;
            nb = 120;
            shift = 3;
        }
        else
        {
            b = 1;
            nb = 120 << lm;
            shift = 3 - lm;
        }

        Span<float> freq = _freq.AsSpan(0, n);
        int outSyn = DecodeBufferSize - n;
        if (cc == 2 && c == 1)
        {
            Denormalise(x, 0, freq, 0, start, effEnd, m, silence);
            for (int ch = 0; ch < 2; ch++)
            {
                for (int bl = 0; bl < b; bl++)
                {
                    Mdct.Backward(freq, bl, _decodeMem[ch], outSyn + (nb * bl), Overlap, shift, b);
                }
            }
        }
        else if (cc == 1 && c == 2)
        {
            Span<float> freq2 = stackalloc float[n];
            Denormalise(x, 0, freq, 0, start, effEnd, m, silence);
            Denormalise(x, n, freq2, Bands, start, effEnd, m, silence);
            for (int i = 0; i < n; i++)
            {
                freq[i] = (0.5f * freq[i]) + (0.5f * freq2[i]);
            }

            for (int bl = 0; bl < b; bl++)
            {
                Mdct.Backward(freq, bl, _decodeMem[0], outSyn + (nb * bl), Overlap, shift, b);
            }
        }
        else
        {
            for (int ch = 0; ch < cc; ch++)
            {
                Denormalise(x, ch * n, freq, ch * Bands, start, effEnd, m, silence);
                for (int bl = 0; bl < b; bl++)
                {
                    Mdct.Backward(freq, bl, _decodeMem[ch], outSyn + (nb * bl), Overlap, shift, b);
                }
            }
        }
    }

    // prefilter_and_fold (celt_decoder.c).
    private void PrefilterAndFold(int n)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        for (int ch = 0; ch < Channels; ch++)
        {
            float[] mem = _decodeMem[ch];
            CombFilter(_etmp, 0, mem, DecodeBufferSize - n, _postfilterPeriodOld, _postfilterPeriod, Overlap, -_postfilterGainOld, -_postfilterGain, _postfilterTapsetOld, _postfilterTapset,
                false, 0);
            for (int i = 0; i < Overlap / 2; i++)
            {
                mem[DecodeBufferSize - n + i] = (window[i] * _etmp[Overlap - 1 - i]) + (window[Overlap - i - 1] * _etmp[i]);
            }
        }
    }

    private static readonly float[] Gains =
    [
        0.3066406250f, 0.2170410156f, 0.1296386719f,
        0.4638671875f, 0.2680664062f, 0.0f,
        0.7998046875f, 0.1000976562f, 0.0f,
    ];

    // comb_filter (celt.c): y may be x; the taps read before they are written, as the reference does.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void CombFilter(float[] y, int yAt, float[] x, int xAt, int t0, int t1, int n, float g0, float g1, int tapset0, int tapset1, bool useWindow, int overlap)
    {
        if (g0 == 0 && g1 == 0)
        {
            if (!ReferenceEquals(x, y) || xAt != yAt)
            {
                Array.Copy(x, xAt, y, yAt, n);
            }

            return;
        }

        t0 = Math.Max(t0, 15);
        t1 = Math.Max(t1, 15);
        float g00 = g0 * Gains[(tapset0 * 3) + 0];
        float g01 = g0 * Gains[(tapset0 * 3) + 1];
        float g02 = g0 * Gains[(tapset0 * 3) + 2];
        float g10 = g1 * Gains[(tapset1 * 3) + 0];
        float g11 = g1 * Gains[(tapset1 * 3) + 1];
        float g12 = g1 * Gains[(tapset1 * 3) + 2];
        float x1 = x[xAt - t1 + 1];
        float x2 = x[xAt - t1];
        float x3 = x[xAt - t1 - 1];
        float x4 = x[xAt - t1 - 2];
        if (g0 == g1 && t0 == t1 && tapset0 == tapset1)
        {
            overlap = 0;
        }

        ReadOnlySpan<float> window = CeltTables.Window120;
        int i;
        for (i = 0; i < overlap; i++)
        {
            float x0 = x[xAt + i - t1 + 2];
            float f = window[i] * window[i];
            float oneMinus = 1.0f - f;
            y[yAt + i] = x[xAt + i]
                + ((oneMinus * g00) * x[xAt + i - t0])
                + ((oneMinus * g01) * (x[xAt + i - t0 + 1] + x[xAt + i - t0 - 1]))
                + ((oneMinus * g02) * (x[xAt + i - t0 + 2] + x[xAt + i - t0 - 2]))
                + ((f * g10) * x2)
                + ((f * g11) * (x1 + x3))
                + ((f * g12) * (x0 + x4));
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }

        _ = useWindow;
        if (g1 == 0)
        {
            if (!ReferenceEquals(x, y) || xAt != yAt)
            {
                Array.Copy(x, xAt + overlap, y, yAt + overlap, n - overlap);
            }

            return;
        }

        // comb_filter_const
        x4 = x[xAt + i - t1 - 2];
        x3 = x[xAt + i - t1 - 1];
        x2 = x[xAt + i - t1];
        x1 = x[xAt + i - t1 + 1];
        for (; i < n; i++)
        {
            float x0 = x[xAt + i - t1 + 2];
            y[yAt + i] = x[xAt + i] + (g10 * x2) + (g11 * (x1 + x3)) + (g12 * (x0 + x4));
            x4 = x3;
            x3 = x2;
            x2 = x1;
            x1 = x0;
        }
    }

    // deemphasis (celt_decoder.c) of the history's last n samples into the planar output.
    private void Deemphasis(float[][] pcm, int pcmAt, int n)
    {
        for (int ch = 0; ch < Channels; ch++)
        {
            float[] mem = _decodeMem[ch];
            int at = DecodeBufferSize - n;
            float m = _preemphMemD[ch];
            float[] y = pcm[ch];
            for (int j = 0; j < n; j++)
            {
                float tmp = mem[at + j] + VerySmall + m;
                m = Preemph * tmp;
                y[pcmAt + j] = tmp * (1.0f / 32768.0f);
            }

            _preemphMemD[ch] = m;
        }
    }
}
