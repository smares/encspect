using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// Parametric stereo (ISO/IEC 14496-3 8.6.4, HE-AAC v2): the QMF domain of a mono core with SBR becomes the QMF domains of two channels. A hybrid filter bank splits the lowest QMF bands, the
// signal is decorrelated by all-pass chains and delays, and each frame's intensity differences (IID) and coherences (ICC) mix it with the original in two-by-two matrices that are interpolated
// between the envelope borders. The structure and the tables follow libxaac (github.com/ittiam-systems/libxaac at 2fbadd5, Apache 2.0, Copyright (C) The Android Open Source Project; the
// notice is in THIRD-PARTY-NOTICES.md), ixheaacd_ps_dec_flt.c and ixheaacd_ps_bitdec.c, in single precision; the 34-band mode, which libxaac maps to 20 bands, is computed in 34.
// The hybrid analysis looks 6 slots ahead: the six slots after the frame are the low band's analysis slots the SBR buffer already holds (the standard's delay of the SBR data, as libxaac takes
// them), so the output is not later than the input; the inter-channel phase differences (IPD and OPD) are ignored as the baseline decoder ignores them (8.A.4), and matrix Ra mixes every mode (coherence modes 3 to 5 read as 0 to 2).
internal sealed class PsDecoder
{
    public const int Slots = 32;
    private const int Bands = 64;
    private const int History = 6;
    private const int LookAhead = 6;
    private const int MaxSub = 32;
    private const int Links = 3;
    private const int HighDelay = 14;
    private const int AllPassDelay = 2;
    private const int AllPassBands = 23;
    private const int NegateMask = 0x1000;
    private const float PeakDecay = 0.765928338364649f;
    private const float EnergyFilter = 0.25f;
    private const float DecaySlope = 0.05f;
    private static readonly int[] LinkDelay = [3, 4, 5];
    private static readonly int[] BandCount = [10, 20, 34];

    // h11, h12, h21, h22 for each IID index and ICC index: coarse (7 steps each way) and fine (15).
    private static readonly float[] CoarseMix = BuildMix(7, PsTableData.ScaleFactorsFlt);
    private static readonly float[] FineMix = BuildMix(15, PsTableData.ScaleFactorsFineFlt);

    // The input and the hybrid analysis: the last 12 slots of the QMF domain and this frame's.
    private readonly float[] _extRe = new float[(History + Slots + LookAhead) * Bands];
    private readonly float[] _extIm = new float[(History + Slots + LookAhead) * Bands];
    private readonly float[] _hybLeftRe = new float[Slots * MaxSub];
    private readonly float[] _hybLeftIm = new float[Slots * MaxSub];
    private readonly float[] _hybRightRe = new float[Slots * MaxSub];
    private readonly float[] _hybRightIm = new float[Slots * MaxSub];
    private readonly float[] _leftRe = new float[Slots * Bands];
    private readonly float[] _leftIm = new float[Slots * Bands];
    private readonly float[] _rightRe = new float[Slots * Bands];
    private readonly float[] _rightIm = new float[Slots * Bands];

    // The decorrelator's state.
    private readonly float[] _subDelayRe = new float[HighDelay * Bands];
    private readonly float[] _subDelayIm = new float[HighDelay * Bands];
    private readonly float[] _delayRe = new float[HighDelay * Bands];
    private readonly float[] _delayIm = new float[HighDelay * Bands];
    private readonly float[] _subSerRe = new float[Links * 5 * Bands];
    private readonly float[] _subSerIm = new float[Links * 5 * Bands];
    private readonly float[] _serRe = new float[Links * 5 * Bands];
    private readonly float[] _serIm = new float[Links * 5 * Bands];
    private readonly int[] _qmfDelayAt = new int[Bands];
    private readonly int[] _serAt = new int[Links];
    private int _delayAt;
    private readonly float[] _peak = new float[34];
    private readonly float[] _previousDifference = new float[34];
    private readonly float[] _energy = new float[34];
    private readonly float[] _power = new float[Slots * 34];
    private readonly float[] _transient = new float[Slots * 34];

    // The mixing matrices of the last envelope, in the bands of the resolution in use, and what the parameters carry from frame to frame.
    private readonly float[] _h = new float[4 * 34];
    private readonly float[] _hPrevious = new float[4 * 34];
    private readonly float[] _hScratch = new float[34];
    private readonly int[] _iidPrevious = new int[34];
    private readonly int[] _iccPrevious = new int[34];
    private readonly int[,] _iid = new int[6, 34];
    private readonly int[,] _icc = new int[6, 34];
    private readonly int[] _border = new int[7];
    private bool _enableIid;
    private bool _enableIcc;
    private int _iidMode;
    private int _iccMode;
    private bool _fine;
    private bool _wasWide;

    // Whether the previous frame held ps_data and whether this one does (ISO/IEC 14496-3 8.A.3: a frame after one without ps_data starts from reset decorrelator states).
    private bool _previousPresent;

    private bool _present;
    private int _envelopes;

    private readonly float[][] _states;

    public PsDecoder()
    {
        _states = [_peak, _previousDifference, _energy, _subDelayRe, _subDelayIm, _delayRe, _delayIm, _subSerRe, _subSerIm, _serRe, _serIm];
        Reset();
    }

    public ReadOnlySpan<float> LeftRe => _leftRe;

    public ReadOnlySpan<float> LeftIm => _leftIm;

    public ReadOnlySpan<float> RightRe => _rightRe;

    public ReadOnlySpan<float> RightIm => _rightIm;

    // At a loss: the filters' histories, the decorrelator and the parameters start again.
    public void Reset()
    {
        Array.Clear(_extRe);
        Array.Clear(_extIm);
        Array.Clear(_subDelayRe);
        Array.Clear(_subDelayIm);
        Array.Clear(_delayRe);
        Array.Clear(_delayIm);
        ClearAllPass();
        Array.Clear(_qmfDelayAt);
        Array.Clear(_serAt);
        _delayAt = 0;
        Array.Clear(_peak);
        Array.Clear(_previousDifference);
        Array.Clear(_energy);
        Array.Clear(_iidPrevious);
        Array.Clear(_iccPrevious);
        Array.Clear(_hPrevious);
        for (int b = 0; b < 34; b++)
        {
            _hPrevious[b] = 1;
            _hPrevious[34 + b] = 1;
        }

        _enableIid = false;
        _enableIcc = false;
        _iidMode = 0;
        _iccMode = 0;
        _fine = false;
        _wasWide = false;
        _previousPresent = false;
        _present = false;
    }

    // Recursive states below 1e-30 are zero at the start of each frame (the determinism rule for decoders).
    private void FlushStates()
    {
        foreach (float[] state in _states)
        {
            for (int i = 0; i < state.Length; i++)
            {
                if (Math.Abs(state[i]) < 1e-30f)
                {
                    state[i] = 0;
                }
            }
        }
    }

    // The decorrelator's state variables, d_k(n) and s_k(n) of 8.A.3 and 8.6.4.6.1 (Table 8.47): the delay lines and the all-pass links of both banks.
    private void ClearDecorrelator()
    {
        Array.Clear(_subDelayRe);
        Array.Clear(_subDelayIm);
        Array.Clear(_delayRe);
        Array.Clear(_delayIm);
        ClearAllPass();
    }

    // Whether any of the decorrelator's state variables is not zero (for the tests of its resets).
    internal bool HasDecorrelatorState => Any(_subDelayRe) || Any(_subDelayIm) || Any(_delayRe) || Any(_delayIm) || Any(_subSerRe) || Any(_subSerIm) || Any(_serRe) || Any(_serIm);

    private static bool Any(float[] state) => Array.Exists(state, x => x != 0);

    private void ClearAllPass()
    {
        Array.Clear(_subSerRe);
        Array.Clear(_subSerIm);
        Array.Clear(_serRe);
        Array.Clear(_serIm);
    }

    // One frame: the QMF domain of the mono channel (32 slots of 64 bands) and the frame's ps_data (the stored SbrRecord.Ps values) or none, the six slots of the low band that follow the frame, with the first band above the SBR range, whose
    // all-pass state is cleared. The outputs are the two channels' QMF domains.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(ReadOnlySpan<float> re, ReadOnlySpan<float> im, ReadOnlySpan<float> lookAheadRe, ReadOnlySpan<float> lookAheadIm, ReadOnlySpan<int> ps, int upperBand)
    {
        FlushStates();
        bool wide = Parameters(ps);
        if (!_previousPresent)
        {
            ClearDecorrelator();
        }

        Array.Copy(_extRe, Slots * Bands, _extRe, 0, History * Bands);
        Array.Copy(_extIm, Slots * Bands, _extIm, 0, History * Bands);
        re[..(Slots * Bands)].CopyTo(_extRe.AsSpan(History * Bands));
        im[..(Slots * Bands)].CopyTo(_extIm.AsSpan(History * Bands));
        lookAheadRe[..(LookAhead * Bands)].CopyTo(_extRe.AsSpan((History + Slots) * Bands));
        lookAheadIm[..(LookAhead * Bands)].CopyTo(_extIm.AsSpan((History + Slots) * Bands));
        for (int sb = Math.Clamp(upperBand, 0, Bands); sb < Bands; sb++)
        {
            for (int m = 0; m < Links; m++)
            {
                for (int k = 0; k < LinkDelay[m]; k++)
                {
                    _serRe[(((m * 5) + k) * Bands) + sb] = 0;
                    _serIm[(((m * 5) + k) * Bands) + sb] = 0;
                }
            }

            for (int k = 0; k < HighDelay; k++)
            {
                _delayRe[(k * Bands) + sb] = 0;
                _delayIm[(k * Bands) + sb] = 0;
            }
        }

        int hybridBands = wide ? 5 : 3;
        int[] borders = wide ? PsTableData.GroupBorders34Tbl : PsTableData.GroupBorders20Tbl;
        int[] map = wide ? PsTableData.BinGroupMap34 : PsTableData.BinGroupMap20;
        int groups = wide ? 50 : 22;
        int subGroups = wide ? 32 : 10;
        int bins = wide ? 34 : 20;

        HybridAnalysis(wide);
        // The QMF domain of the bands the hybrid bank leaves alone, and the hybrid bands' sum of their subbands later.
        for (int l = 0; l < Slots; l++)
        {
            Array.Copy(_extRe, (l + History) * Bands, _leftRe, l * Bands, Bands);
            Array.Copy(_extIm, (l + History) * Bands, _leftIm, l * Bands, Bands);
        }

        if (wide != _wasWide)
        {
            ClearDecorrelator();
            MapMatrices(wide);
        }

        Decorrelate(wide, borders, map, groups, subGroups, bins);
        Mix(wide, borders, map, groups, subGroups, bins);
        Synthesize(hybridBands, wide);
        _wasWide = wide;
        _previousPresent = _present;
    }

    // The parameters of the frame: the envelopes' IID and ICC indices in the bands in use, the borders and whether the 34-band resolution is in use (ixheaacd_decode_ps_data).
    private bool Parameters(ReadOnlySpan<int> ps)
    {
        bool present = !ps.IsEmpty && ps[SbrRecord.PsEnvelopes] >= 0;
        _present = present;
        int frameClass = 0;
        _envelopes = 0;
        if (present)
        {
            _enableIid = ps[SbrRecord.PsEnableIid] != 0;
            _iidMode = ps[SbrRecord.PsIidMode];
            _fine = ps[SbrRecord.PsIidFine] != 0;
            _enableIcc = ps[SbrRecord.PsEnableIcc] != 0;
            _iccMode = ps[SbrRecord.PsIccMode];
            frameClass = ps[SbrRecord.PsFrameClass];
            _envelopes = ps[SbrRecord.PsEnvelopes];
        }

        int iidStep = _iidMode == 0 ? 2 : 1;
        int iccStep = _iccMode == 0 ? 2 : 1;
        int levels = _fine ? 15 : 7;
        for (int e = 0; e < _envelopes; e++)
        {
            int dt = ps[SbrRecord.PsIidTime + e];
            int at = 0;
            for (int i = 0; i < BandCount[_iidMode]; i++)
            {
                int raw = ps[SbrRecord.PsIid + (e * SbrRecord.PsStride) + i];
                int value;
                if (!_enableIid)
                {
                    value = 0;
                }
                else if (dt != 0)
                {
                    value = (e == 0 ? _iidPrevious[at] : _iid[e - 1, at]) + raw;
                    at += iidStep;
                }
                else
                {
                    value = i == 0 ? raw : _iid[e, i - 1] + raw;
                }

                _iid[e, i] = Math.Clamp(value, -levels, levels);
            }

            if (_iidMode == 0)
            {
                for (int i = 19; i > 0; i--)
                {
                    _iid[e, i] = _iid[e, i >> 1];
                }
            }

            dt = ps[SbrRecord.PsIccTime + e];
            at = 0;
            for (int i = 0; i < BandCount[_iccMode]; i++)
            {
                int raw = ps[SbrRecord.PsIcc + (e * SbrRecord.PsStride) + i];
                int value;
                if (!_enableIcc)
                {
                    value = 0;
                }
                else if (dt != 0)
                {
                    value = (e == 0 ? _iccPrevious[at] : _icc[e - 1, at]) + raw;
                    at += iccStep;
                }
                else
                {
                    value = i == 0 ? raw : _icc[e, i - 1] + raw;
                }

                _icc[e, i] = Math.Clamp(value, 0, 7);
            }

            if (_iccMode == 0)
            {
                for (int i = 19; i > 0; i--)
                {
                    _icc[e, i] = _icc[e, i >> 1];
                }
            }
        }

        if (_envelopes == 0)
        {
            _envelopes = 1;
            for (int i = 0; i < 34; i++)
            {
                _iid[0, i] = _enableIid ? Math.Clamp(_iidPrevious[i], -levels, levels) : 0;
                _icc[0, i] = _enableIcc ? _iccPrevious[i] : 0;
            }
        }

        for (int i = 0; i < 34; i++)
        {
            _iidPrevious[i] = _iid[_envelopes - 1, i];
            _iccPrevious[i] = _icc[_envelopes - 1, i];
        }

        _border[0] = 0;
        if (frameClass == 0)
        {
            int shift = _envelopes switch { 2 => 1, 4 => 2, _ => 0 };
            for (int e = 1; e < _envelopes; e++)
            {
                _border[e] = (e * Slots) >> shift;
            }

            _border[_envelopes] = Slots;
        }
        else
        {
            for (int e = 1; e <= _envelopes; e++)
            {
                _border[e] = ps[SbrRecord.PsBorders + e];
            }

            if (_border[_envelopes] < Slots)
            {
                _envelopes++;
                _border[_envelopes] = Slots;
                for (int i = 0; i < 34; i++)
                {
                    _iid[_envelopes - 1, i] = _iid[_envelopes - 2, i];
                    _icc[_envelopes - 1, i] = _icc[_envelopes - 2, i];
                }
            }

            for (int e = 1; e < _envelopes; e++)
            {
                int threshold = Slots - (_envelopes - e);
                if (_border[e] > threshold)
                {
                    _border[e] = threshold;
                }
                else if (_border[e] < _border[e - 1] + 1)
                {
                    _border[e] = _border[e - 1] + 1;
                }
            }
        }

        // The resolution is the finer of the two parameter sets; a set of 20 bands is spread over 34 where the other is finer.
        bool wide = _iidMode == 2 || _iccMode == 2;
        if (wide)
        {
            for (int e = 0; e < _envelopes; e++)
            {
                if (_iidMode != 2)
                {
                    Spread(_iid, e);
                }

                if (_iccMode != 2)
                {
                    Spread(_icc, e);
                }
            }
        }

        return wide;
    }

    // The 20-band index of each of the 34 bands (Table 8.45, 8.6.4.6.1); bands 1 and 4 are averages of two, which Spread forms.
    private static readonly int[] Wider = [0, 0, 1, 2, 2, 3, 4, 4, 5, 5, 6, 7, 8, 8, 9, 9, 10, 11, 12, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 18, 18, 19, 19];

    internal static void Spread(int[,] table, int e)
    {
        Span<int> copy = stackalloc int[20];
        for (int i = 0; i < 20; i++)
        {
            copy[i] = table[e, i];
        }

        for (int i = 0; i < 34; i++)
        {
            // The averages are of the integer indices before dequantization, in ANSI C integer arithmetic (division truncates toward zero, as C# does).
            table[e, i] = i switch { 1 => (copy[0] + copy[1]) / 2, 4 => (copy[2] + copy[3]) / 2, _ => copy[Wider[i]] };
        }
    }

    // ixheaacd_hyb_anal: the subbands of the lowest QMF bands, a 13-tap filter of each band's complex samples modulated into 8 or 2 (12, 8 and 4 of the 34-band bank) channels; the band's value 6 slots
    // before the newest is the filter's center.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void HybridAnalysis(bool wide)
    {
        int offset = 0;
        int count = wide ? 5 : 3;
        for (int band = 0; band < count; band++)
        {
            int resolution = wide ? PsTableData.BandResHyb34[band] : PsTableData.BandResHyb20[band];
            for (int i = 0; i < Slots; i++)
            {
                for (int q = 0; q < resolution; q++)
                {
                    float real = 0;
                    float imag = 0;
                    for (int n = 0; n < 13; n++)
                    {
                        float xr = _extRe[((i + n) * Bands) + band];
                        float xi = _extIm[((i + n) * Bands) + band];
                        float p;
                        if (resolution == 2)
                        {
                            p = PsTableData.P21320[n];
                            float c = PsTableData.CosMod2channel[(q * 13) + n];
                            real += p * (xr * c);
                            imag += p * (xi * c);
                        }
                        else
                        {
                            (p, float[] table) = resolution switch
                            {
                                4 => (PsTableData.P41334[n], PsTableData.CosSinMod4channel),
                                8 => (wide ? PsTableData.P81334[n] : PsTableData.P81320[n], PsTableData.CosSinMod8channel),
                                _ => (PsTableData.P121334[n], PsTableData.CosSinMod12channel),
                            };
                            float c = table[(((q * 13) + n) * 2)];
                            float s = table[(((q * 13) + n) * 2) + 1];
                            real += p * ((xr * c) - (xi * s));
                            imag += p * ((xi * c) + (xr * s));
                        }
                    }

                    _hybLeftRe[(i * MaxSub) + offset + q] = real;
                    _hybLeftIm[(i * MaxSub) + offset + q] = imag;
                }
            }

            offset += resolution;
        }

        if (!wide)
        {
            for (int i = 0; i < Slots; i++)
            {
                int row = i * MaxSub;
                _hybLeftRe[row + 3] += _hybLeftRe[row + 4];
                _hybLeftIm[row + 3] += _hybLeftIm[row + 4];
                _hybLeftRe[row + 4] = 0;
                _hybLeftIm[row + 4] = 0;
                _hybLeftRe[row + 2] += _hybLeftRe[row + 5];
                _hybLeftIm[row + 2] += _hybLeftIm[row + 5];
                _hybLeftRe[row + 5] = 0;
                _hybLeftIm[row + 5] = 0;
            }
        }
    }

    // ixheaacd_esbr_ps_de_correlate: the transient ratio of each parameter band, and the all-pass chains (the three links of a fractional delay each) for the lowest bands and the delays for the
    // highest, which make the right channel's input from the left's.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Decorrelate(bool wide, int[] borders, int[] map, int groups, int subGroups, int bins)
    {
        float[] subPhaseRe = wide ? PsTableData.FracDelayPhaseFacQmfSubRe34 : PsTableData.FracDelayPhaseFacQmfSubRe20;
        float[] subPhaseIm = wide ? PsTableData.FracDelayPhaseFacQmfSubIm34 : PsTableData.FracDelayPhaseFacQmfSubIm20;
        float[] subSerRe = wide ? PsTableData.FracDelayPhaseFacSerQmfSubRe34 : PsTableData.FracDelayPhaseFacSerQmfSubRe20;
        float[] subSerIm = wide ? PsTableData.FracDelayPhaseFacSerQmfSubIm34 : PsTableData.FracDelayPhaseFacSerQmfSubIm20;
        Array.Clear(_power, 0, Slots * 34);
        for (int gr = 0; gr < groups; gr++)
        {
            int bin = map[gr] & ~NegateMask;
            if (gr < subGroups)
            {
                int sb = borders[gr];
                for (int k = 0; k < Slots; k++)
                {
                    float a = _hybLeftRe[(k * MaxSub) + sb];
                    float b = _hybLeftIm[(k * MaxSub) + sb];
                    _power[(k * 34) + bin] += (a * a) + (b * b);
                }
            }
            else
            {
                for (int sb = borders[gr]; sb < borders[gr + 1]; sb++)
                {
                    for (int k = 0; k < Slots; k++)
                    {
                        float a = _leftRe[(k * Bands) + sb];
                        float b = _leftIm[(k * Bands) + sb];
                        _power[(k * 34) + bin] += (a * a) + (b * b);
                    }
                }
            }
        }

        for (int bin = 0; bin < bins; bin++)
        {
            for (int k = 0; k < Slots; k++)
            {
                float power = _power[(k * 34) + bin];
                _peak[bin] *= PeakDecay;
                if (_peak[bin] < power)
                {
                    _peak[bin] = power;
                }

                float difference = _previousDifference[bin];
                difference += EnergyFilter * (_peak[bin] - power - _previousDifference[bin]);
                _previousDifference[bin] = difference;
                float energy = _energy[bin];
                energy += EnergyFilter * (power - _energy[bin]);
                _energy[bin] = energy;
                _transient[(k * 34) + bin] = 1.5f * difference <= energy ? 1f : energy / (1.5f * difference);
            }
        }

        int cutoff = wide ? 5 : 3;
        Span<int> serAt = stackalloc int[Links];
        for (int gr = 0; gr < groups; gr++)
        {
            bool isSub = gr < subGroups;
            int first = borders[gr];
            int last = isSub ? first + 1 : borders[gr + 1];
            int bin = map[gr] & ~NegateMask;
            for (int sb = first; sb < last; sb++)
            {
                float decay = 1f;
                if (!isSub)
                {
                    decay = sb <= cutoff ? 1f : 1f + (cutoff * DecaySlope) - (DecaySlope * sb);
                    decay = Math.Max(decay, 0f);
                }

                int delay = _delayAt;
                for (int m = 0; m < Links; m++)
                {
                    serAt[m] = _serAt[m];
                }

                for (int k = 0; k < Slots; k++)
                {
                    float inRe;
                    float inIm;
                    if (isSub)
                    {
                        inRe = _hybLeftRe[(k * MaxSub) + sb];
                        inIm = _hybLeftIm[(k * MaxSub) + sb];
                    }
                    else
                    {
                        inRe = _leftRe[(k * Bands) + sb];
                        inIm = _leftIm[(k * Bands) + sb];
                    }

                    float outRe;
                    float outIm;
                    bool plainDelay = !isSub && gr >= (wide ? 32 : 10) && sb >= AllPassBands;
                    if (plainDelay)
                    {
                        int at = (_qmfDelayAt[sb] * Bands) + sb;
                        outRe = _delayRe[at];
                        outIm = _delayIm[at];
                        _delayRe[at] = inRe;
                        _delayIm[at] = inIm;
                    }
                    else
                    {
                        float[] bufRe = isSub ? _subDelayRe : _delayRe;
                        float[] bufIm = isSub ? _subDelayIm : _delayIm;
                        int at = (delay * Bands) + sb;
                        float real0 = bufRe[at];
                        float imag0 = bufIm[at];
                        bufRe[at] = inRe;
                        bufIm[at] = inIm;
                        float pr = isSub ? subPhaseRe[sb] : PsTableData.QmfFractDelayPhaseFactorRe[sb];
                        float pi = isSub ? subPhaseIm[sb] : PsTableData.QmfFractDelayPhaseFactorIm[sb];
                        float rr = (real0 * pr) - (imag0 * pi);
                        float ri = (real0 * pi) + (imag0 * pr);
                        float[] serRe = isSub ? _subSerRe : _serRe;
                        float[] serIm = isSub ? _subSerIm : _serIm;
                        for (int m = 0; m < Links; m++)
                        {
                            int s = (((m * 5) + serAt[m]) * Bands) + sb;
                            float a = serRe[s];
                            float b = serIm[s];
                            float qr = isSub ? subSerRe[(sb * Links) + m] : PsTableData.QmfSerFractDelayPhaseFactorRe[(sb * Links) + m];
                            float qi = isSub ? subSerIm[(sb * Links) + m] : PsTableData.QmfSerFractDelayPhaseFactorIm[(sb * Links) + m];
                            float g = decay * PsTableData.AllPassLinkDecaySer[m];
                            float nr = (a * qr) - (b * qi);
                            float ni = (a * qi) + (b * qr);
                            nr += -g * rr;
                            ni += -g * ri;
                            serRe[s] = rr + (g * nr);
                            serIm[s] = ri + (g * ni);
                            rr = nr;
                            ri = ni;
                        }

                        outRe = rr;
                        outIm = ri;
                    }

                    float ratio = _transient[(k * 34) + bin];
                    if (isSub)
                    {
                        _hybRightRe[(k * MaxSub) + sb] = ratio * outRe;
                        _hybRightIm[(k * MaxSub) + sb] = ratio * outIm;
                    }
                    else
                    {
                        _rightRe[(k * Bands) + sb] = ratio * outRe;
                        _rightIm[(k * Bands) + sb] = ratio * outIm;
                    }

                    if (++delay >= AllPassDelay)
                    {
                        delay = 0;
                    }

                    if (plainDelay && ++_qmfDelayAt[sb] >= PsTableData.QmfDelayIdxTbl[sb])
                    {
                        _qmfDelayAt[sb] = 0;
                    }

                    for (int m = 0; m < Links; m++)
                    {
                        if (++serAt[m] >= LinkDelay[m])
                        {
                            serAt[m] = 0;
                        }
                    }
                }

                if (gr == groups - 1 && sb == last - 1)
                {
                    _delayAt = delay;
                    for (int m = 0; m < Links; m++)
                    {
                        _serAt[m] = serAt[m];
                    }
                }
            }
        }
    }

    // ixheaacd_esbr_ps_apply_rotation, without inter-channel phase: each envelope's matrices from the IID and ICC indices, interpolated linearly slot by slot from the last envelope's.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Mix(bool wide, int[] borders, int[] map, int groups, int subGroups, int bins)
    {
        float[] mix = _fine ? FineMix : CoarseMix;
        int steps = _fine ? 15 : 7;
        for (int e = 0; e < _envelopes; e++)
        {
            for (int bin = 0; bin < bins; bin++)
            {
                int at = (((_iid[e, bin] + steps) * 8) + _icc[e, bin]) * 4;
                for (int c = 0; c < 4; c++)
                {
                    _h[(c * 34) + bin] = mix[at + c];
                }
            }

            int length = _border[e + 1] - _border[e];
            for (int gr = 0; gr < groups; gr++)
            {
                bool isSub = gr < subGroups;
                int bin = map[gr] & ~NegateMask;
                float h11 = _hPrevious[bin];
                float h12 = _hPrevious[34 + bin];
                float h21 = _hPrevious[68 + bin];
                float h22 = _hPrevious[102 + bin];
                float d11 = (_h[bin] - h11) / length;
                float d12 = (_h[34 + bin] - h12) / length;
                float d21 = (_h[68 + bin] - h21) / length;
                float d22 = (_h[102 + bin] - h22) / length;
                int last = isSub ? borders[gr] + 1 : borders[gr + 1];
                for (int i = _border[e]; i < _border[e + 1]; i++)
                {
                    h11 += d11;
                    h12 += d12;
                    h21 += d21;
                    h22 += d22;
                    for (int sb = borders[gr]; sb < last; sb++)
                    {
                        float[] leftRe = isSub ? _hybLeftRe : _leftRe;
                        float[] leftIm = isSub ? _hybLeftIm : _leftIm;
                        float[] rightRe = isSub ? _hybRightRe : _rightRe;
                        float[] rightIm = isSub ? _hybRightIm : _rightIm;
                        int at = (i * (isSub ? MaxSub : Bands)) + sb;
                        float lr = leftRe[at];
                        float li = leftIm[at];
                        float rr = rightRe[at];
                        float ri = rightIm[at];
                        leftRe[at] = (h11 * lr) + (h21 * rr);
                        leftIm[at] = (h11 * li) + (h21 * ri);
                        rightRe[at] = (h12 * lr) + (h22 * rr);
                        rightIm[at] = (h12 * li) + (h22 * ri);
                    }
                }
            }

            for (int bin = 0; bin < bins; bin++)
            {
                for (int c = 0; c < 4; c++)
                {
                    _hPrevious[(c * 34) + bin] = _h[(c * 34) + bin];
                }
            }
        }
    }

    // ixheaacd_hyb_synth: the hybrid bands' subbands add up to their QMF bands.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Synthesize(int hybridBands, bool wide)
    {
        int offset = 0;
        for (int band = 0; band < hybridBands; band++)
        {
            int resolution = wide ? PsTableData.BandResHyb34[band] : PsTableData.BandResHyb20[band];
            for (int n = 0; n < Slots; n++)
            {
                float lr = 0;
                float li = 0;
                float rr = 0;
                float ri = 0;
                for (int k = 0; k < resolution; k++)
                {
                    int at = (n * MaxSub) + offset + k;
                    lr += _hybLeftRe[at];
                    li += _hybLeftIm[at];
                    rr += _hybRightRe[at];
                    ri += _hybRightIm[at];
                }

                _leftRe[(n * Bands) + band] = lr;
                _leftIm[(n * Bands) + band] = li;
                _rightRe[(n * Bands) + band] = rr;
                _rightIm[(n * Bands) + band] = ri;
            }

            offset += resolution;
        }
    }

    // The matrices of the last envelope in the other resolution (ixheaacd_map_20_float_to_34 and ixheaacd_map_34_float_to_20).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void MapMatrices(bool wide)
    {
        for (int c = 0; c < 4; c++)
        {
            Span<float> row = _hPrevious.AsSpan(c * 34, 34);
            if (wide)
            {
                Span<float> t = _hScratch;
                t[0] = row[0];
                t[1] = (row[0] + row[1]) / 2f;
                t[2] = row[1];
                t[3] = row[2];
                t[4] = (row[2] + row[3]) / 2f;
                t[5] = row[3];
                t[6] = row[4];
                t[7] = row[4];
                t[8] = row[5];
                t[9] = row[5];
                t[10] = row[6];
                t[11] = row[7];
                t[12] = row[8];
                t[13] = row[8];
                t[14] = row[9];
                t[15] = row[9];
                t[16] = row[10];
                t[17] = row[11];
                t[18] = row[12];
                t[19] = row[13];
                t[20] = row[14];
                t[21] = row[14];
                t[22] = row[15];
                t[23] = row[15];
                t[24] = row[16];
                t[25] = row[16];
                t[26] = row[17];
                t[27] = row[17];
                t[28] = row[18];
                t[29] = row[18];
                t[30] = row[18];
                t[31] = row[18];
                t[32] = row[19];
                t[33] = row[19];
                t.CopyTo(row);
            }
            else
            {
                row[0] = ((2 * row[0]) + row[1]) / 3f;
                row[1] = (row[1] + (2 * row[2])) / 3f;
                row[2] = ((2 * row[3]) + row[4]) / 3f;
                row[3] = (row[4] + (2 * row[5])) / 3f;
                row[4] = (row[6] + row[7]) / 2f;
                row[5] = (row[8] + row[9]) / 2f;
                row[6] = row[10];
                row[7] = row[11];
                row[8] = (row[12] + row[13]) / 2f;
                row[9] = (row[14] + row[15]) / 2f;
                row[10] = row[16];
                row[11] = row[17];
                row[12] = row[18];
                row[13] = row[19];
                row[14] = (row[20] + row[21]) / 2f;
                row[15] = (row[22] + row[23]) / 2f;
                row[16] = (row[24] + row[25]) / 2f;
                row[17] = (row[26] + row[27]) / 2f;
                row[18] = (row[28] + row[29] + row[30] + row[31]) / 4f;
                row[19] = (row[32] + row[33]) / 2f;
            }
        }
    }

    // The matrices of a coherence and an intensity difference: the intensity difference sets the channels' gains and the coherence rotates them apart.
    private static float[] BuildMix(int steps, float[] scale)
    {
        int rows = (2 * steps) + 1;
        float[] table = new float[rows * 8 * 4];
        for (int i = 0; i < rows; i++)
        {
            float right = scale[steps + (i - steps)];
            float left = scale[steps - (i - steps)];
            for (int c = 0; c < 8; c++)
            {
                float alpha = PsTableData.Alphas[c];
                float beta = alpha * (right - left) / 1.4142135f;
                int at = ((i * 8) + c) * 4;
                table[at] = (float)(left * DetMath.Cos(beta + alpha));
                table[at + 1] = (float)(right * DetMath.Cos(beta - alpha));
                table[at + 2] = (float)(left * DetMath.Sin(beta + alpha));
                table[at + 3] = (float)(right * DetMath.Sin(beta - alpha));
            }
        }

        return table;
    }
}
