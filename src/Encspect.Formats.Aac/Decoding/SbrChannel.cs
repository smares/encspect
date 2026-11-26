using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// The reconstruction of one channel's high band by SBR (ISO/IEC 14496-3 4.6.18): the QMF analysis of the core's samples, the high frequency generator (patching with the inverse
// filtering of the low band, 4.6.18.6), the adjuster (envelope and noise levels, the limiter, the added sinusoids and noise, 4.6.18.7), and the synthesis. In single precision, following
// libxaac's floating-point path (ixheaacd_sbrdec_lpfuncs.c ixheaacd_generate_hf, ixheaacd_esbr_envcal.c ixheaacd_sbr_env_calc and ixheaacd_sbr_dec.c; Apache License 2.0, Copyright (C)
// The Android Open Source Project; the notice is in THIRD-PARTY-NOTICES.md), which writes the standard's formulas for the original SBR tools out directly.
// The QMF buffers hold 40 columns of 64 bands: the first 8 are the last columns of the frame before (2 for the generator's history, 6 of delay), the next 32 are this frame's.
internal sealed class SbrChannel
{
    public const int Columns = 40;
    public const int Offset = 2;
    private const float Epsilon = 1e-12f;
    private const float RelaxationParameter = 0.999999f;

    private static readonly float[,] ChirpTable =
    {
        { 0.00f, 0.60f, 0.90f, 0.98f },
        { 0.60f, 0.75f, 0.90f, 0.98f },
        { 0.00f, 0.75f, 0.90f, 0.98f },
        { 0.00f, 0.75f, 0.90f, 0.98f },
    };

    private static readonly float[] LimiterGainTable = [0.70795f, 1.0f, 1.41254f, 1e10f];

    private static readonly float[][] SmoothingFilters =
    [
        [1.0f],
        [0.33333333333333f, 0.66666666666666f],
        [0.12500000000000f, 0.37500000000000f, 0.50000000000000f],
        [0.05857864376269f, 0.20000000000000f, 0.34142135623731f, 0.40000000000000f],
        [0.03183050093751f, 0.11516383427084f, 0.21816949906249f, 0.30150283239582f, 0.33333333333333f],
    ];

    private readonly SbrQmf _qmf = new();
    private readonly float[] _gainHistory = new float[5 * 64];
    private readonly float[] _noiseHistory = new float[5 * 64];
    private readonly float[] _chirpPrevious = new float[5];
    private readonly bool[] _harmonicPrevious = new bool[64];
    private readonly float[] _estimate = new float[64];
    private readonly float[] _reference = new float[64];
    private readonly float[] _gain = new float[64];
    private readonly float[] _tone = new float[64];
    private readonly float[] _noiseLevel = new float[64];
    private readonly float[] _alphaRe = new float[64 * 2];
    private readonly float[] _alphaIm = new float[64 * 2];
    private readonly bool[] _harmonic = new bool[64];

    public float[] LowRe { get; } = new float[Columns * 64];

    public float[] LowIm { get; } = new float[Columns * 64];

    public float[] HighRe { get; } = new float[Columns * 64];

    public float[] HighIm { get; } = new float[Columns * 64];

    // The previous frame's inverse filtering modes, envelope values (as indexes, on the high resolution bands) and noise values, for what is coded across time.
    public int[] PreviousInverseFilter { get; } = new int[5];

    public int[] PreviousEnvelope { get; } = new int[64];

    public int[] PreviousNoise { get; } = new int[5];

    public float[] PreviousNoiseLevel { get; } = new float[5];

    public bool StartUp { get; set; } = true;

    private int _phaseIndex;
    private int _harmonicIndex;
    private int _shortPrevious = -1;
    private int _previousEnd = 16;

    // The frame class and borders of the frame before, for the time borders of the envelope that starts this one.
    public int PreviousEnd => _previousEnd;

    public void Reset()
    {
        _qmf.Reset();
        Array.Clear(LowRe);
        Array.Clear(LowIm);
        Array.Clear(HighRe);
        Array.Clear(HighIm);
        ResetState();
    }

    // At a header whose frequency tables differ (the standard's reset): what the generator and adjuster keep across frames starts again.
    public void ResetState()
    {
        Array.Clear(_gainHistory);
        Array.Clear(_noiseHistory);
        Array.Clear(_chirpPrevious);
        Array.Clear(_harmonicPrevious);
        Array.Clear(PreviousInverseFilter);
        Array.Clear(PreviousEnvelope);
        Array.Clear(PreviousNoise);
        Array.Clear(PreviousNoiseLevel);
        StartUp = true;
        _phaseIndex = 0;
        _harmonicIndex = 0;
        _shortPrevious = -1;
    }

    // Moves the last 8 columns to the front, for the next frame.
    public void Carry()
    {
        int from = SbrQmf.Slots * 64;
        Array.Copy(LowRe, from, LowRe, 0, 8 * 64);
        Array.Copy(LowIm, from, LowIm, 0, 8 * 64);
        Array.Copy(HighRe, from, HighRe, 0, 8 * 64);
        Array.Copy(HighIm, from, HighIm, 0, 8 * 64);
    }

    // The 32 analysis slots of a frame of the core's samples (1,024 of them, scaled to 16-bit values by the caller) into the columns 8 to 39 of the low band.
    public void Analyze(ReadOnlySpan<float> core)
    {
        for (int slot = 0; slot < SbrQmf.Slots; slot++)
        {
            int column = (8 + slot) * 64;
            _qmf.Analyze(core.Slice(slot * 32, 32), LowRe.AsSpan(column, 32), LowIm.AsSpan(column, 32));
            Array.Clear(LowRe, column + 32, 32);
            Array.Clear(LowIm, column + 32, 32);
        }
    }

    internal SbrQmf Qmf => _qmf;

    // ixheaacd_generate_hf: the high band's QMF columns from the low band's, patch by patch, with the inverse filter of each noise floor band.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Generate(SbrLayout layout, ReadOnlySpan<int> frame, Span<float> chirp)
    {
        int kx = layout.Kx;
        int k2 = layout.K2;
        int envelopes = frame[SbrRecord.EnvelopeCount];
        int start = frame[SbrRecord.Borders] * 2;
        int end = 32 + ((frame[SbrRecord.Borders + envelopes] - 16) * 2);
        int length = 38;
        int[] noise = layout.Tables.Noise;
        int bandsLow = layout.Tables.Master[0];

        for (int i = 0; i < layout.Noise; i++)
        {
            float bw = ChirpTable[PreviousInverseFilter[i], frame[SbrRecord.InverseFilterMode + i]];
            float previous = _chirpPrevious[i];
            bw = bw < previous ? (0.75000f * bw) + (0.25000f * previous) : (0.90625f * bw) + (0.09375f * previous);
            if (bw < 0.015625f)
            {
                bw = 0;
            }

            chirp[i] = bw;
        }

        for (int i = start; i < end; i++)
        {
            int row = (i + Offset) * 64;
            Array.Clear(HighRe, row + k2, 64 - k2);
            Array.Clear(HighIm, row + k2, 64 - k2);
        }

        // The prediction coefficients of the low band's QMF bands below the patches' start (the covariance method of order 2).
        for (int k = 1; k < bandsLow; k++)
        {
            Covariance(k, length, out float phi01Re, out float phi01Im, out float phi02Re, out float phi02Im, out float phi11, out float phi12Re, out float phi12Im, out float phi22);
            float det = (phi11 * phi22) - (((phi12Re * phi12Re) + (phi12Im * phi12Im)) * RelaxationParameter);
            float a1Re = 0;
            float a1Im = 0;
            float a0Re = 0;
            float a0Im = 0;
            if (det != 0)
            {
                float factor = 1.0f / det;
                a1Re = ((phi01Re * phi12Re) - (phi01Im * phi12Im) - (phi02Re * phi11)) * factor;
                a1Im = ((phi01Im * phi12Re) + (phi01Re * phi12Im) - (phi02Im * phi11)) * factor;
            }

            if (phi11 != 0)
            {
                float factor = 1.0f / phi11;
                a0Re = -(phi01Re + (a1Re * phi12Re) + (a1Im * phi12Im)) * factor;
                a0Im = -(phi01Im + (a1Im * phi12Re) - (a1Re * phi12Im)) * factor;
            }

            if (((a0Re * a0Re) + (a0Im * a0Im) >= 16.0f) || ((a1Re * a1Re) + (a1Im * a1Im) >= 16.0f))
            {
                a0Re = a0Im = a1Re = a1Im = 0;
            }

            _alphaRe[(k * 2) + 0] = a0Re;
            _alphaIm[(k * 2) + 0] = a0Im;
            _alphaRe[(k * 2) + 1] = a1Re;
            _alphaIm[(k * 2) + 1] = a1Im;
        }

        for (int patch = 0; patch < layout.PatchCount; patch++)
        {
            int sb = layout.PatchStart[patch];
            for (int k2i = sb; k2i < sb + layout.PatchBands[patch]; k2i++)
            {
                int k = k2i - layout.PatchStride[patch];
                int bwIndex = 0;
                while (bwIndex < layout.Noise && k2i >= noise[bwIndex + 1])
                {
                    bwIndex++;
                }

                bwIndex = Math.Min(bwIndex, layout.Noise - 1);
                float bw = chirp[bwIndex];
                float a0r = bw * _alphaRe[k * 2];
                float a0i = bw * _alphaIm[k * 2];
                bw *= bw;
                float a1r = bw * _alphaRe[(k * 2) + 1];
                float a1i = bw * _alphaIm[(k * 2) + 1];
                for (int i = start; i < end; i++)
                {
                    int at = ((i + Offset) * 64) + k;
                    float re = LowRe[at];
                    float im = LowIm[at];
                    if (bw > 0.0f)
                    {
                        re += (a0r * LowRe[at - 64]) - (a0i * LowIm[at - 64]) + (a1r * LowRe[at - 128]) - (a1i * LowIm[at - 128]);
                        im += (a0i * LowRe[at - 64]) + (a0r * LowIm[at - 64]) + (a1i * LowRe[at - 128]) + (a1r * LowIm[at - 128]);
                    }

                    HighRe[((i + Offset) * 64) + k2i] = re;
                    HighIm[((i + Offset) * 64) + k2i] = im;
                }
            }
        }

        for (int i = 0; i < layout.Noise; i++)
        {
            _chirpPrevious[i] = chirp[i];
        }

        _ = kx;
    }

    private void Covariance(int band, int length, out float phi01Re, out float phi01Im, out float phi02Re, out float phi02Im, out float phi11, out float phi12Re, out float phi12Im, out float phi22)
    {
        phi01Re = phi01Im = phi02Re = phi02Im = phi11 = phi12Re = phi12Im = phi22 = 0;
        for (int j = 0; j < length; j++)
        {
            int at = ((j + Offset) * 64) + band;
            float xr = LowRe[at];
            float xi = LowIm[at];
            float yr = LowRe[at - 64];
            float yi = LowIm[at - 64];
            float zr = LowRe[at - 128];
            float zi = LowIm[at - 128];
            phi01Re += (xr * yr) + (xi * yi);
            phi01Im += (xi * yr) - (xr * yi);
            phi02Re += (xr * zr) + (xi * zi);
            phi02Im += (xi * zr) - (xr * zi);
            phi11 += (yr * yr) + (yi * yi);
            phi12Re += (yr * zr) + (yi * zi);
            phi12Im += (yi * zr) - (yr * zi);
            phi22 += (zr * zr) + (zi * zi);
        }
    }

    // ixheaacd_sbr_env_calc, for the original SBR tools: the gains that bring the generated bands to the envelope's energy, the noise floor and the sinusoids, limited and smoothed.
    // reference holds the dequantized envelope energies in order (an envelope's bands after the one before's); noiseFloor the dequantized noise floors (each of the noise bands).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Adjust(SbrLayout layout, ReadOnlySpan<int> frame, ReadOnlySpan<int> header, ReadOnlySpan<float> energies, ReadOnlySpan<float> noiseFloor, bool reset)
    {
        int kx = layout.Kx;
        int bands = layout.K2 - kx;
        int envelopes = frame[SbrRecord.EnvelopeCount];
        int transient = frame[SbrRecord.Transient];
        int smoothing = header[SbrRecord.SmoothingMode] != 0 ? 0 : 4;
        bool interpolated = header[SbrRecord.InterpolatedFrequency] != 0;
        int limiterBand = header[SbrRecord.LimiterBands];
        int limiterGains = header[SbrRecord.LimiterGains];
        int[] high = layout.Tables.High;
        int[] low = layout.Tables.Low;
        int[] noiseTable = layout.Tables.Noise;
        int noiseBands = layout.Noise;
        if (reset)
        {
            _phaseIndex = 0;
            StartUp = true;
        }

        Array.Clear(_harmonic);
        if (frame[SbrRecord.HasHarmonics] != 0)
        {
            for (int i = 0; i < layout.High; i++)
            {
                int center = (high[i + 1] + high[i] - (kx << 1)) >> 1;
                if (center is >= 0 and < 64)
                {
                    _harmonic[center] = frame[SbrRecord.Harmonics + i] != 0;
                }
            }
        }

        int kk = 0;
        int next = -1;
        int m = 0;
        bool startUp = StartUp;
        for (int e = 0; e < envelopes; e++)
        {
            int envelopeStart = frame[SbrRecord.Borders + e];
            int envelopeEnd = frame[SbrRecord.Borders + e + 1];
            if (envelopeStart == frame[SbrRecord.NoiseBorders + kk])
            {
                kk++;
                next++;
            }

            bool absence = e == transient || e == _shortPrevious;
            int filterLength = absence ? 0 : smoothing;
            float[] filter = SmoothingFilters[filterLength];
            int[] table = frame[SbrRecord.FrequencyResolution + e] != 0 ? high : low;
            int bandCount = table.Length - 1;
            int c = 0;
            int o = 0;
            for (int j = 0; j < bandCount; j++)
            {
                int li = table[j];
                int ui = table[j + 1];
                int ui2 = noiseTable[o + 1];
                bool flag = false;
                float average = 0;
                int first = c;
                if (envelopeStart >= envelopeEnd)
                {
                    for (int k = li; k < ui; k++)
                    {
                        flag |= _harmonic[c] && (e >= transient || _harmonicPrevious[c + kx]);
                        _estimate[c++] = 0;
                    }
                }
                else
                {
                    int count = 2 * (envelopeEnd - envelopeStart);
                    for (int k = li; k < ui; k++)
                    {
                        float energy = 0;
                        for (int l = 2 * envelopeStart; l < 2 * envelopeEnd; l++)
                        {
                            int at = ((l + Offset) * 64) + k;
                            energy += (HighRe[at] * HighRe[at]) + (HighIm[at] * HighIm[at]);
                        }

                        flag |= _harmonic[c] && (e >= transient || _harmonicPrevious[c + kx]);
                        _estimate[c++] = energy / count;
                    }
                }

                if (!interpolated && ui != li)
                {
                    float sum = 0;
                    for (int k = first; k < c; k++)
                    {
                        sum += _estimate[k];
                    }

                    average = sum / (ui - li);
                }

                c = first;
                for (int k = 0; k < ui - li; k++)
                {
                    if (k + li >= ui2)
                    {
                        o++;
                    }

                    o = Math.Min(o, noiseBands - 1);
                    ui2 = noiseTable[o + 1];
                    float reference = energies[m];
                    _reference[c] = reference;
                    if (!interpolated)
                    {
                        _estimate[c] = average;
                    }

                    _tone[c] = 0;
                    float floor = noiseFloor[(next * noiseBands) + o];
                    double ratio = floor / (1 + floor + 1e-17);
                    if (flag)
                    {
                        _gain[c] = (float)Math.Sqrt(reference * ratio / (_estimate[c] + 1));
                        if (_harmonic[c] && (e >= transient || _harmonicPrevious[c + kx]))
                        {
                            _tone[c] = (float)Math.Sqrt(reference * ratio / Math.Abs(floor + 1e-17));
                        }
                    }
                    else if (absence)
                    {
                        _gain[c] = (float)Math.Sqrt(reference / (_estimate[c] + 1));
                    }
                    else
                    {
                        _gain[c] = (float)Math.Sqrt(reference * ratio / ((_estimate[c] + 1) * Math.Abs(floor + 1e-17)));
                    }

                    _noiseLevel[c] = (float)Math.Sqrt(reference * ratio);
                    c++;
                }

                m++;
            }

            // The limiter: each limiter band's gain is held to the band's average gain times the limit, and then the band is boosted back to the energy it should have.
            int[] limiter = layout.Limiter[limiterBand];
            for (int band = 0; band < layout.LimiterCount[limiterBand]; band++)
            {
                float pReference = 0;
                float pEstimate = 0;
                for (int k = limiter[band]; k < limiter[band + 1]; k++)
                {
                    pReference += _reference[k];
                    pEstimate += _estimate[k];
                }

                float average = (float)Math.Sqrt((pReference + Epsilon) / (pEstimate + Epsilon));
                float maxGain = average * LimiterGainTable[limiterGains];
                if (maxGain > 1.0e5f)
                {
                    maxGain = 1.0e5f;
                }

                for (int k = limiter[band]; k < limiter[band + 1]; k++)
                {
                    if (maxGain <= _gain[k])
                    {
                        _noiseLevel[k] = (float)(_noiseLevel[k] * (maxGain / (_gain[k] + 1e-17)));
                        _gain[k] = maxGain;
                    }
                }

                float pAdjusted = 0;
                for (int k = limiter[band]; k < limiter[band + 1]; k++)
                {
                    pAdjusted += _gain[k] * _gain[k] * _estimate[k];
                    if (_tone[k] != 0)
                    {
                        pAdjusted += _tone[k] * _tone[k];
                    }
                    else if (!absence)
                    {
                        pAdjusted += _noiseLevel[k] * _noiseLevel[k];
                    }
                }

                float boost = (float)Math.Sqrt((pReference + Epsilon) / (pAdjusted + Epsilon));
                if (boost > 1.584893192f)
                {
                    boost = 1.584893192f;
                }

                for (int k = limiter[band]; k < limiter[band + 1]; k++)
                {
                    _gain[k] *= boost;
                    _noiseLevel[k] *= boost;
                    _tone[k] *= boost;
                }
            }

            if (startUp)
            {
                for (int n = 0; n < 4; n++)
                {
                    Array.Copy(_gain, 0, _gainHistory, n * 64, bands);
                    Array.Copy(_noiseLevel, 0, _noiseHistory, n * 64, bands);
                }

                startUp = false;
            }

            for (int l = 2 * envelopeStart; l < 2 * envelopeEnd; l++)
            {
                int row = (l + Offset) * 64;
                for (int k = 0; k < bands; k++)
                {
                    _gainHistory[(4 * 64) + k] = _gain[k];
                    _noiseHistory[(4 * 64) + k] = _noiseLevel[k];
                    float sbGain = 0;
                    float sbNoise = 0;
                    int tap = 0;
                    for (int n = 4 - filterLength; n <= 4; n++)
                    {
                        sbGain += _gainHistory[(n * 64) + k] * filter[tap];
                        sbNoise += _noiseHistory[(n * 64) + k] * filter[tap];
                        tap++;
                    }

                    _phaseIndex = (_phaseIndex + 1) & 511;
                    if (_tone[k] != 0 || absence)
                    {
                        sbNoise = 0;
                    }

                    int at = row + kx + k;
                    HighRe[at] = (HighRe[at] * sbGain) + (sbNoise * SbrTableData.Noise[_phaseIndex * 2]);
                    HighIm[at] = (HighIm[at] * sbGain) + (sbNoise * SbrTableData.Noise[(_phaseIndex * 2) + 1]);
                }

                for (int n = 0; n < 4; n++)
                {
                    Array.Copy(_gainHistory, (n + 1) * 64, _gainHistory, n * 64, 64);
                    Array.Copy(_noiseHistory, (n + 1) * 64, _noiseHistory, n * 64, 64);
                }
                // The oldest column is the one just consumed; it moves to the end and is overwritten at the next column.
            }

            for (int l = 2 * envelopeStart; l < 2 * envelopeEnd; l++)
            {
                int row = (l + Offset) * 64;
                int sign = (kx & 1) != 0 ? -1 : 1;
                for (int k = 0; k < bands; k++)
                {
                    HighRe[row + kx + k] += _tone[k] * SineReal[_harmonicIndex];
                    HighIm[row + kx + k] += _tone[k] * sign * SineImaginary[_harmonicIndex];
                    sign = -sign;
                }

                _harmonicIndex = (_harmonicIndex + 1) & 3;
            }
        }

        StartUp = startUp;
        for (int i = 0; i < 64; i++)
        {
            if (i + kx < 64)
            {
                _harmonicPrevious[i + kx] = _harmonic[i];
            }
        }

        _shortPrevious = transient == envelopes ? 0 : -1;
        _previousEnd = frame[SbrRecord.Borders + envelopes];
    }

    private static readonly float[] SineReal = [1.0f, 0.0f, -1.0f, 0.0f];
    private static readonly float[] SineImaginary = [0.0f, 1.0f, 0.0f, -1.0f];
}
