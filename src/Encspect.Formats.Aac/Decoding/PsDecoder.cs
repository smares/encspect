using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// Parametric stereo (ISO/IEC 14496-3 8.6.4, HE-AAC v2): the QMF domain of a mono core with SBR becomes the QMF domains of two channels. A hybrid filter bank splits the lowest QMF bands, the
// signal is decorrelated by all-pass chains and delays, and each frame's intensity differences (IID) and coherences (ICC) mix it with the original in two-by-two matrices that are interpolated
// between the envelope borders. The structure and the tables follow libxaac (github.com/ittiam-systems/libxaac at 2fbadd5, Apache 2.0, Copyright (C) The Android Open Source Project; the
// notice is in THIRD-PARTY-NOTICES.md), ixheaacd_ps_dec_flt.c and ixheaacd_ps_bitdec.c, in single precision; the 34-band mode, which libxaac and the baseline decoder (8.A.4) map to 20 bands, is computed in 34, so the decoder is neither the baseline of HE AAC levels 2 to 4 nor the level 5 tool (Table 1.12 note 1).
// The hybrid analysis looks 6 slots ahead: the six slots after the frame are the low band's analysis slots the SBR buffer already holds (the standard's delay of the SBR data, as libxaac takes
// them), so the output is not later than the input. The coherence modes 0 to 2 mix with procedure Ra and 3 to 5 with Rb (8.6.4.6.2, Table 8.27), and where a frame's extension carries inter-channel and
// overall phase differences (IPD and OPD, 8.4.2) the matrices take their phases (8.6.4.6.3.2), smoothed over the three last envelopes: the full decoder, not the baseline of 8.A.4, which ignores both.
// Rb and the phase follow libxaac's ixheaacd_esbr_ps_apply_rotation in structure, with the standard's formulas and DetMath's functions, and are computed in double precision before they are stored in single.
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

    // The same for mixing procedure Rb (8.6.4.6.2.2).
    internal static readonly float[] CoarseMixB = BuildMixB(7, PsTableData.QuantizedIids);
    internal static readonly float[] FineMixB = BuildMixB(15, PsTableData.QuantizedIidsFine);

    // cos and sin of k pi / 4, the quantization grid of the phase differences (Table 8.31).
    private static readonly double[] PhaseCos = BuildPhase(cosine: true);
    private static readonly double[] PhaseSin = BuildPhase(cosine: false);
    private const double PhaseHistoryWeight1 = 0.5;
    private const double PhaseHistoryWeight2 = 0.25;

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
    private readonly int[,] _ipd = new int[6, 34];
    private readonly int[,] _opd = new int[6, 34];
    private readonly int[] _ipdPrevious = new int[34];
    private readonly int[] _opdPrevious = new int[34];

    // The phase indices of the two envelopes before the one being mixed, in the bands in use (the smoothing of 8.6.4.6.3.2).
    private readonly int[] _ipdHistory1 = new int[34];
    private readonly int[] _ipdHistory2 = new int[34];
    private readonly int[] _opdHistory1 = new int[34];
    private readonly int[] _opdHistory2 = new int[34];
    private readonly float[] _hIm = new float[4 * 34];
    private readonly float[] _hPreviousIm = new float[4 * 34];
    private readonly int[] _border = new int[7];
    private bool _enableIid;
    private bool _enableIcc;
    private int _iidMode;
    private int _iccMode;
    private bool _fine;
    private bool _wasWide;

    // Whether the coherence mode of the header in force selects mixing procedure Rb, whether this frame carries IPD and OPD, whether it holds the last matrices complex as they are (enable_ipdopd without
    // an envelope, Table 8.52) and whether it sets the real matrices at once where the last ones had a phase (an envelope-less frame without IPD and OPD).
    private bool _mixB;
    private bool _phase;
    private bool _holdMatrices;
    private bool _snapMatrices;

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
        Array.Clear(_hPreviousIm);
        Array.Clear(_ipdPrevious);
        Array.Clear(_opdPrevious);
        ClearPhaseHistory();
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
        _mixB = false;
        _phase = false;
        _previousPresent = false;
        _present = false;
    }

    private void ClearPhaseHistory()
    {
        Array.Clear(_ipdHistory1);
        Array.Clear(_ipdHistory2);
        Array.Clear(_opdHistory1);
        Array.Clear(_opdHistory2);
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
            MapMatrices(_hPrevious, wide);
            MapMatrices(_hPreviousIm, wide);
            ClearPhaseHistory();
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
        _phase = false;
        if (present)
        {
            _mixB = ps[SbrRecord.PsMixingB] != 0;
            _phase = ps[SbrRecord.PsEnableIpdopd] != 0;
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

            if (_phase)
            {
                int count = _iidMode switch { 0 => 5, 1 => 11, _ => 17 };
                ReadPhase(ps, e, count, SbrRecord.PsIpdTime, SbrRecord.PsIpd, _ipd, _ipdPrevious);
                ReadPhase(ps, e, count, SbrRecord.PsOpdTime, SbrRecord.PsOpd, _opd, _opdPrevious);
            }
            else
            {
                for (int i = 0; i < 34; i++)
                {
                    _ipd[e, i] = 0;
                    _opd[e, i] = 0;
                }
            }
        }

        _holdMatrices = _phase && _envelopes == 0;
        _snapMatrices = !_phase && _envelopes == 0 && HasPhase(_hPreviousIm);

        if (_envelopes == 0)
        {
            _envelopes = 1;
            for (int i = 0; i < 34; i++)
            {
                _iid[0, i] = _enableIid ? Math.Clamp(_iidPrevious[i], -levels, levels) : 0;
                _icc[0, i] = _enableIcc ? _iccPrevious[i] : 0;
                _ipd[0, i] = 0;
                _opd[0, i] = 0;
            }
        }

        for (int i = 0; i < 34; i++)
        {
            _iidPrevious[i] = _iid[_envelopes - 1, i];
            _iccPrevious[i] = _icc[_envelopes - 1, i];
            _ipdPrevious[i] = _ipd[_envelopes - 1, i];
            _opdPrevious[i] = _opd[_envelopes - 1, i];
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
                    _ipd[_envelopes - 1, i] = _ipd[_envelopes - 2, i];
                    _opd[_envelopes - 1, i] = _opd[_envelopes - 2, i];
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

        // The resolution is the finer of the two parameter sets (Table 8.44); a set that is not transmitted counts as 20 bands, and with neither transmitted the previous frame's number of
        // stereo bands is kept (8.6.4.6.1). A set of 20 bands is spread over 34 where the other is finer.
        bool wide = !_enableIid && !_enableIcc ? _wasWide : (_enableIid && _iidMode == 2) || (_enableIcc && _iccMode == 2);
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

                // The phases follow the intensity differences' mapping (8.6.4.6.1): the grid of their mode, and zero above the bands that carry phases.
                if (_iidMode != 2)
                {
                    Spread(_ipd, e);
                    Spread(_opd, e);
                }
            }
        }

        return wide;
    }

    // One envelope's IPD or OPD indices (ipd_par and opd_par, 8.5.1): the coded differences added to the index of the band before (across frequency) or of the same band in the envelope before (across
    // time, the last envelope of the frame before for the first), modulo 8; 10-band modes (5 values) take the 20-band grid as the intensity differences do, and the bands above the coded ones are 0.
    private void ReadPhase(ReadOnlySpan<int> ps, int e, int count, int timeAt, int dataAt, int[,] table, int[] previous)
    {
        bool time = ps[timeAt + e] != 0;
        int step = _iidMode == 0 ? 2 : 1;
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int raw = ps[dataAt + (e * SbrRecord.PsPhaseStride) + i];
            int basis;
            if (time)
            {
                basis = e == 0 ? previous[at] : table[e - 1, at];
                at += step;
            }
            else
            {
                basis = i == 0 ? 0 : table[e, i - 1];
            }

            table[e, i] = (basis + raw) & 7;
        }

        for (int i = count; i < 34; i++)
        {
            table[e, i] = 0;
        }

        if (_iidMode == 0)
        {
            for (int i = 19; i > 0; i--)
            {
                table[e, i] = table[e, i >> 1];
            }
        }
    }

    private static bool HasPhase(float[] imaginary) => Array.Exists(imaginary, x => x != 0);

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

    // ixheaacd_esbr_ps_apply_rotation: each envelope's matrices from the IID and ICC indices (procedure Ra or Rb), with the phases of the IPD and OPD where the frame carries them, interpolated linearly slot
    // by slot from the last envelope's. The matrices are complex; without phases their imaginary parts are 0 and the products are the real ones.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Mix(bool wide, int[] borders, int[] map, int groups, int subGroups, int bins)
    {
        float[] mix = _mixB ? (_fine ? FineMixB : CoarseMixB) : (_fine ? FineMix : CoarseMix);
        int steps = _fine ? 15 : 7;
        if (_snapMatrices)
        {
            Array.Clear(_hPreviousIm);
            for (int bin = 0; bin < bins; bin++)
            {
                int at = (((_iid[0, bin] + steps) * 8) + _icc[0, bin]) * 4;
                for (int c = 0; c < 4; c++)
                {
                    _hPrevious[(c * 34) + bin] = mix[at + c];
                }
            }
        }

        for (int e = 0; e < _envelopes; e++)
        {
            if (_holdMatrices)
            {
                Array.Copy(_hPrevious, _h, _h.Length);
                Array.Copy(_hPreviousIm, _hIm, _hIm.Length);
            }
            else
            {
                for (int bin = 0; bin < bins; bin++)
                {
                    int at = (((_iid[e, bin] + steps) * 8) + _icc[e, bin]) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        _h[(c * 34) + bin] = mix[at + c];
                        _hIm[(c * 34) + bin] = 0;
                    }

                    if (_phase)
                    {
                        Rotate(e, bin);
                    }
                }
            }

            int length = _border[e + 1] - _border[e];
            for (int gr = 0; gr < groups; gr++)
            {
                bool isSub = gr < subGroups;
                int bin = map[gr] & ~NegateMask;

                // The sub subbands that Table 8.48 and Table 8.49 mark take the complex conjugate of the matrices.
                float sign = (map[gr] & NegateMask) != 0 ? -1f : 1f;
                float h11 = _hPrevious[bin];
                float h12 = _hPrevious[34 + bin];
                float h21 = _hPrevious[68 + bin];
                float h22 = _hPrevious[102 + bin];
                float g11 = sign * _hPreviousIm[bin];
                float g12 = sign * _hPreviousIm[34 + bin];
                float g21 = sign * _hPreviousIm[68 + bin];
                float g22 = sign * _hPreviousIm[102 + bin];
                float d11 = (_h[bin] - h11) / length;
                float d12 = (_h[34 + bin] - h12) / length;
                float d21 = (_h[68 + bin] - h21) / length;
                float d22 = (_h[102 + bin] - h22) / length;
                float e11 = ((sign * _hIm[bin]) - g11) / length;
                float e12 = ((sign * _hIm[34 + bin]) - g12) / length;
                float e21 = ((sign * _hIm[68 + bin]) - g21) / length;
                float e22 = ((sign * _hIm[102 + bin]) - g22) / length;
                int last = isSub ? borders[gr] + 1 : borders[gr + 1];
                float[] leftRe = isSub ? _hybLeftRe : _leftRe;
                float[] leftIm = isSub ? _hybLeftIm : _leftIm;
                float[] rightRe = isSub ? _hybRightRe : _rightRe;
                float[] rightIm = isSub ? _hybRightIm : _rightIm;
                for (int i = _border[e]; i < _border[e + 1]; i++)
                {
                    h11 += d11;
                    h12 += d12;
                    h21 += d21;
                    h22 += d22;
                    g11 += e11;
                    g12 += e12;
                    g21 += e21;
                    g22 += e22;
                    for (int sb = borders[gr]; sb < last; sb++)
                    {
                        int at = (i * (isSub ? MaxSub : Bands)) + sb;
                        float lr = leftRe[at];
                        float li = leftIm[at];
                        float rr = rightRe[at];
                        float ri = rightIm[at];
                        leftRe[at] = (((h11 * lr) - (g11 * li)) + (h21 * rr)) - (g21 * ri);
                        leftIm[at] = (((g11 * lr) + (h11 * li)) + (g21 * rr)) + (h21 * ri);
                        rightRe[at] = (((h12 * lr) - (g12 * li)) + (h22 * rr)) - (g22 * ri);
                        rightIm[at] = (((g12 * lr) + (h12 * li)) + (g22 * rr)) + (h22 * ri);
                    }
                }
            }

            for (int bin = 0; bin < bins; bin++)
            {
                for (int c = 0; c < 4; c++)
                {
                    _hPrevious[(c * 34) + bin] = _h[(c * 34) + bin];
                    _hPreviousIm[(c * 34) + bin] = _hIm[(c * 34) + bin];
                }

                _ipdHistory2[bin] = _ipdHistory1[bin];
                _opdHistory2[bin] = _opdHistory1[bin];
                _ipdHistory1[bin] = _ipd[e, bin];
                _opdHistory1[bin] = _opd[e, bin];
            }
        }
    }

    // 8.6.4.6.3.2: the IPD and OPD of the envelope and of the two before it are smoothed as the angle of the sum of their phasors with the weights 1, 1/2 and 1/4 (the envelope the matrices are for counting most), and the matrices'
    // columns turn by the overall phase and by its difference to the inter-channel phase.
    private void Rotate(int e, int bin)
    {
        int ipd = _ipd[e, bin];
        int opd = _opd[e, bin];
        int ipd1 = _ipdHistory1[bin];
        int opd1 = _opdHistory1[bin];
        int ipd2 = _ipdHistory2[bin];
        int opd2 = _opdHistory2[bin];
        if ((ipd | opd | ipd1 | opd1 | ipd2 | opd2) == 0)
        {
            return;
        }

        double smoothIpd = DetMath.Atan2(
            PhaseSin[ipd] + (PhaseHistoryWeight1 * PhaseSin[ipd1]) + (PhaseHistoryWeight2 * PhaseSin[ipd2]),
            PhaseCos[ipd] + (PhaseHistoryWeight1 * PhaseCos[ipd1]) + (PhaseHistoryWeight2 * PhaseCos[ipd2]));
        double smoothOpd = DetMath.Atan2(
            PhaseSin[opd] + (PhaseHistoryWeight1 * PhaseSin[opd1]) + (PhaseHistoryWeight2 * PhaseSin[opd2]),
            PhaseCos[opd] + (PhaseHistoryWeight1 * PhaseCos[opd1]) + (PhaseHistoryWeight2 * PhaseCos[opd2]));
        double first = smoothOpd;
        double second = smoothOpd - smoothIpd;
        float c1 = (float)DetMath.Cos(first);
        float s1 = (float)DetMath.Sin(first);
        float c2 = (float)DetMath.Cos(second);
        float s2 = (float)DetMath.Sin(second);

        // h11 and h21 take the first phase, h12 and h22 the second.
        _hIm[bin] = _h[bin] * s1;
        _h[bin] *= c1;
        _hIm[34 + bin] = _h[34 + bin] * s2;
        _h[34 + bin] *= c2;
        _hIm[68 + bin] = _h[68 + bin] * s1;
        _h[68 + bin] *= c1;
        _hIm[102 + bin] = _h[102 + bin] * s2;
        _h[102 + bin] *= c2;
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
    private void MapMatrices(float[] matrices, bool wide)
    {
        for (int c = 0; c < 4; c++)
        {
            Span<float> row = matrices.AsSpan(c * 34, 34);
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

    // cos or sin of k pi / 4 for the eight indices of the phase grid, exact at the multiples of a quarter turn.
    private static double[] BuildPhase(bool cosine)
    {
        double[] table = new double[8];
        for (int k = 0; k < 8; k++)
        {
            DetMath.SinCos(k, 8, out double sin, out double cos);
            table[k] = cosine ? cos : sin;
        }

        return table;
    }

    // The matrices of mixing procedure Rb (8.6.4.6.2.2) for each intensity difference index (the quantized differences in dB, with their signs) and coherence index: a rotation of the pair of signals by the angle that
    // diagonalizes their covariance, scaled by the square root of 2, with the coherence floored at 0.05. The coherences' index above 4 (0, -0.589 and -1) all give 0.05.
    private static float[] BuildMixB(int steps, int[] decibels)
    {
        int rows = (2 * steps) + 1;
        float[] table = new float[rows * 8 * 4];
        double root2 = Math.Sqrt(2.0);
        for (int i = 0; i < rows; i++)
        {
            int index = i - steps;
            double c = index == 0 ? 1.0 : DetMath.Pow(10.0, (index > 0 ? 1 : -1) * decibels[Math.Abs(index) - 1] / 20.0);
            for (int k = 0; k < 8; k++)
            {
                double rho = Math.Max(PsTableData.QuantizedRhos[k], 0.05);
                double alpha = index == 0 ? DetMath.Pi / 4 : 0.5 * DetMath.Atan(2.0 * c * rho / ((c * c) - 1.0));

                // The modulo correction into [0, pi / 2).
                alpha -= Math.Floor(alpha / DetMath.HalfPi) * DetMath.HalfPi;
                double sum = c + (1.0 / c);
                double mu = 1.0 + (((4.0 * rho * rho) - 4.0) / (sum * sum));
                double root = Math.Sqrt(Math.Max(mu, 0.0));
                double gamma = DetMath.Atan(Math.Sqrt((1.0 - root) / (1.0 + root)));
                int at = ((i * 8) + k) * 4;
                table[at] = (float)(root2 * DetMath.Cos(alpha) * DetMath.Cos(gamma));
                table[at + 1] = (float)(root2 * DetMath.Sin(alpha) * DetMath.Cos(gamma));
                table[at + 2] = (float)(-root2 * DetMath.Sin(alpha) * DetMath.Sin(gamma));
                table[at + 3] = (float)(root2 * DetMath.Cos(alpha) * DetMath.Sin(gamma));
            }
        }

        return table;
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
