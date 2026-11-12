using static Encspect.Formats.Opus.SilkMath;

namespace Encspect.Formats.Opus.Decoding;

// The state and the reconstruction of one channel of the SILK decoder (RFC 6716, section 4.2): from the quantization indices and the pulses of a frame to its samples
// at the internal rate, with the concealment the analyzer runs at a change of mode (silk_PLC, silk_CNG). It follows decode_frame.c, decode_parameters.c, decode_core.c,
// NLSF_decode.c, NLSF_stabilize.c, NLSF2A.c, LPC_fit.c, LPC_inv_pred_gain.c, bwexpander*.c, gain_quant.c, decode_pitch.c, PLC.c and CNG.c of libopus 1.5.2 (BSD-3-Clause,
// notice in THIRD-PARTY-NOTICES.md). SILK is integer code in every build of the reference, so the output is exact.
internal sealed class SilkChannel
{
    public const int MaxLpcOrder = 16;
    public const int LtpOrder = 5;
    public const int MaxFrameLength = 320;
    private const int MaxNbSubfr = 4;
    private const int RandBufSize = 128;
    private const int RandBufMask = RandBufSize - 1;
    private const int Offset = 2090;
    private const int InvScaleQ16 = 1907825;

    // The control structure of a frame (silk_decoder_control).
    private readonly int[] _pitchL = new int[MaxNbSubfr];
    private readonly int[] _gainsQ16 = new int[MaxNbSubfr];
    private readonly short[] _predCoefQ12 = new short[2 * MaxLpcOrder];
    private readonly short[] _ltpCoefQ14 = new short[LtpOrder * MaxNbSubfr];
    private int _ltpScaleQ14;

    // The state that crosses frames (silk_decoder_state).
    public readonly int[] ExcQ14 = new int[MaxFrameLength];
    private readonly int[] _sLpcQ14Buf = new int[MaxLpcOrder];
    private readonly short[] _outBuf = new short[MaxFrameLength + (2 * 80)];
    private readonly short[] _prevNlsfQ15 = new short[MaxLpcOrder];
    public int LagPrev;
    public int LastGainIndex;
    public int FsKHz;
    public int FsApiHz;
    public int NbSubfr;
    public int FrameLength;
    public int SubfrLength;
    public int LtpMemLength;
    public int LpcOrder;
    public bool FirstFrameAfterReset;
    public int FramesDecoded;
    public int LossCnt;
    public int PrevSignalType;
    private int _prevGainQ16;
    private bool _wide;

    public SilkIndices Indices = new();
    public readonly Resampler Resampler = new();

    // The concealment's state (silk_PLC_struct).
    private int _plcPitchLQ8;
    private readonly short[] _plcLtpCoefQ14 = new short[LtpOrder];
    private readonly short[] _plcPrevLpcQ12 = new short[MaxLpcOrder];
    private bool _plcLastFrameLost;
    private int _plcRandSeed;
    private short _plcRandScaleQ14;
    private int _plcConcEnergy;
    private int _plcConcEnergyShift;
    private short _plcPrevLtpScaleQ14;
    private readonly int[] _plcPrevGainQ16 = new int[2];
    private int _plcFsKHz;
    private int _plcSubfrLength;
    private int _plcNbSubfr;

    // The comfort noise's state (silk_CNG_struct).
    private readonly short[] _cngSmthNlsfQ15 = new short[MaxLpcOrder];
    private readonly int[] _cngExcBufQ14 = new int[MaxFrameLength];
    private int _cngSmthGainQ16;
    private int _cngRandSeed;
    private readonly int[] _cngSynthState = new int[MaxLpcOrder];
    private int _cngFsKHz;

    public SilkChannel()
    {
        Init();
    }

    // silk_init_decoder / silk_reset_decoder: every field zero, then the states a fresh decoder has.
    public void Init()
    {
        Array.Clear(ExcQ14);
        Array.Clear(_sLpcQ14Buf);
        Array.Clear(_outBuf);
        Array.Clear(_prevNlsfQ15);
        Array.Clear(_pitchL);
        Array.Clear(_gainsQ16);
        Array.Clear(_predCoefQ12);
        Array.Clear(_ltpCoefQ14);
        FramesDecoded = 0;
        LagPrev = 0;
        LastGainIndex = 0;
        FsKHz = 0;
        FsApiHz = 0;
        NbSubfr = 0;
        FrameLength = 0;
        SubfrLength = 0;
        LtpMemLength = 0;
        LpcOrder = 0;
        LossCnt = 0;
        PrevSignalType = 0;
        Indices = new SilkIndices();
        _plcFsKHz = 0;
        _plcLastFrameLost = false;
        _plcRandSeed = 0;
        _plcRandScaleQ14 = 0;
        _plcConcEnergy = 0;
        _plcConcEnergyShift = 0;
        _plcPrevLtpScaleQ14 = 0;
        Array.Clear(_plcLtpCoefQ14);
        Array.Clear(_plcPrevLpcQ12);
        _cngFsKHz = 0;
        Array.Clear(_cngExcBufQ14);
        Array.Clear(_cngSynthState);
        Resampler.Clear();
        FirstFrameAfterReset = true;
        _prevGainQ16 = 65536;
        CngReset();
        PlcReset();
    }

    // The side channel starts over where it is coded again after frames that coded the mid channel only (dec_API.c).
    public void RestartAfterMidOnly()
    {
        Array.Clear(_outBuf);
        Array.Clear(_sLpcQ14Buf);
        LagPrev = 100;
        LastGainIndex = 10;
        PrevSignalType = 0;
        FirstFrameAfterReset = true;
    }

    private void CngReset()
    {
        int nlsfStepQ15 = short.MaxValue / (LpcOrder + 1);
        int nlsfAccQ15 = 0;
        for (int i = 0; i < LpcOrder; i++)
        {
            nlsfAccQ15 += nlsfStepQ15;
            _cngSmthNlsfQ15[i] = (short)nlsfAccQ15;
        }

        _cngSmthGainQ16 = 0;
        _cngRandSeed = 3176576;
    }

    private void PlcReset()
    {
        _plcPitchLQ8 = FrameLength << (8 - 1);
        _plcPrevGainQ16[0] = 65536;
        _plcPrevGainQ16[1] = 65536;
        _plcSubfrLength = 20;
        _plcNbSubfr = 2;
    }

    // silk_decoder_set_fs: the frame geometry of a packet at the internal rate fsKHz, with the output at 48 kHz.
    public void SetFs(int fsKHz, int nbSubfr, int fsApiHz)
    {
        NbSubfr = nbSubfr;
        SubfrLength = 5 * fsKHz;
        int frameLength = nbSubfr * SubfrLength;
        if (FsKHz != fsKHz || FsApiHz != fsApiHz)
        {
            Resampler.Init(fsKHz * 1000, fsApiHz);
            FsApiHz = fsApiHz;
        }

        if (FsKHz != fsKHz || frameLength != FrameLength)
        {
            if (FsKHz != fsKHz)
            {
                LtpMemLength = 20 * fsKHz;
                _wide = fsKHz == 16;
                LpcOrder = _wide ? 16 : 10;
                FirstFrameAfterReset = true;
                LagPrev = 100;
                LastGainIndex = 10;
                PrevSignalType = 0;
                Array.Clear(_outBuf);
                Array.Clear(_sLpcQ14Buf);
            }

            FsKHz = fsKHz;
            FrameLength = frameLength;
        }
    }

    // silk_decode_frame: one frame from its indices and pulses (the record's), or its concealment where lost is set. pOut receives FrameLength samples from at.
    public void DecodeFrame(short[] pOut, int at, bool lost, SilkIndices indices, int condCoding, ReadOnlySpan<int> pulsesRecord)
    {
        int l = FrameLength;
        if (!lost)
        {
            Indices = indices;
            Span<short> pulses = stackalloc short[336];
            for (int i = 0; i < l; i++)
            {
                pulses[i] = (short)pulsesRecord[i];
            }

            DecodeParameters(condCoding);
            DecodeCore(pOut, at, pulses);
            int mvLen = LtpMemLength - FrameLength;
            Array.Copy(_outBuf, FrameLength, _outBuf, 0, mvLen);
            Array.Copy(pOut, at, _outBuf, mvLen, FrameLength);
            Plc(pOut, at, false);
            LossCnt = 0;
            PrevSignalType = Indices.SignalType;
            FirstFrameAfterReset = false;
        }
        else
        {
            Plc(pOut, at, true);
            int mvLen = LtpMemLength - FrameLength;
            Array.Copy(_outBuf, FrameLength, _outBuf, 0, mvLen);
            Array.Copy(pOut, at, _outBuf, mvLen, FrameLength);
        }

        Cng(pOut, at, l);
        PlcGlueFrames(pOut, at, l);
        LagPrev = _pitchL[NbSubfr - 1];
    }

    private void DecodeParameters(int condCoding)
    {
        Span<short> nlsfQ15 = stackalloc short[MaxLpcOrder];
        Span<short> nlsf0Q15 = stackalloc short[MaxLpcOrder];
        GainsDequant(condCoding == SilkIndices.CodeConditionally);
        NlsfDecode(nlsfQ15);
        Nlsf2A(_predCoefQ12.AsSpan(MaxLpcOrder, MaxLpcOrder), nlsfQ15, LpcOrder);
        if (FirstFrameAfterReset)
        {
            Indices.NlsfInterpCoef = 4;
        }

        if (Indices.NlsfInterpCoef < 4)
        {
            for (int i = 0; i < LpcOrder; i++)
            {
                nlsf0Q15[i] = (short)(_prevNlsfQ15[i] + ((Indices.NlsfInterpCoef * (nlsfQ15[i] - _prevNlsfQ15[i])) >> 2));
            }

            Nlsf2A(_predCoefQ12.AsSpan(0, MaxLpcOrder), nlsf0Q15, LpcOrder);
        }
        else
        {
            Array.Copy(_predCoefQ12, MaxLpcOrder, _predCoefQ12, 0, LpcOrder);
        }

        nlsfQ15.Slice(0, LpcOrder).CopyTo(_prevNlsfQ15);
        if (LossCnt != 0)
        {
            Bwexpander(_predCoefQ12.AsSpan(0, MaxLpcOrder), LpcOrder, 63570);
            Bwexpander(_predCoefQ12.AsSpan(MaxLpcOrder, MaxLpcOrder), LpcOrder, 63570);
        }

        if (Indices.SignalType == 2)
        {
            DecodePitch();
            ReadOnlySpan<sbyte> cbk = Indices.PerIndex == 0 ? SilkTables.LtpVq0Q7 : Indices.PerIndex == 1 ? SilkTables.LtpVq1Q7 : SilkTables.LtpVq2Q7;
            for (int k = 0; k < NbSubfr; k++)
            {
                int ix = Indices.LtpIndex[k];
                for (int i = 0; i < LtpOrder; i++)
                {
                    _ltpCoefQ14[(k * LtpOrder) + i] = (short)(cbk[(ix * LtpOrder) + i] << 7);
                }
            }

            _ltpScaleQ14 = SilkTables.LtpScalesQ14[Indices.LtpScaleIndex];
        }
        else
        {
            Array.Clear(_pitchL, 0, NbSubfr);
            Array.Clear(_ltpCoefQ14, 0, LtpOrder * NbSubfr);
            Indices.PerIndex = 0;
            _ltpScaleQ14 = 0;
        }
    }

    // silk_gains_dequant.
    private void GainsDequant(bool conditional)
    {
        int prevInd = LastGainIndex;
        for (int k = 0; k < NbSubfr; k++)
        {
            if (k == 0 && !conditional)
            {
                prevInd = Math.Max(Indices.Gains[k], prevInd - 16);
            }
            else
            {
                int indTmp = Indices.Gains[k] - 4;
                int doubleStepSizeThreshold = (2 * 36) - 64 + prevInd;
                if (indTmp > doubleStepSizeThreshold)
                {
                    prevInd += (indTmp << 1) - doubleStepSizeThreshold;
                }
                else
                {
                    prevInd += indTmp;
                }
            }

            prevInd = Limit(prevInd, 0, 63);
            _gainsQ16[k] = Log2Lin(Math.Min(Smulwb(InvScaleQ16, prevInd) + Offset, 3967));
        }

        LastGainIndex = prevInd;
    }

    // silk_decode_pitch.
    private void DecodePitch()
    {
        ReadOnlySpan<sbyte> table;
        int cbkSize;
        if (FsKHz == 8)
        {
            if (NbSubfr == 4)
            {
                table = SilkTables.CbLagsStage2;
                cbkSize = 11;
            }
            else
            {
                table = SilkTables.CbLagsStage2TenMs;
                cbkSize = 3;
            }
        }
        else if (NbSubfr == 4)
        {
            table = SilkTables.CbLagsStage3;
            cbkSize = 34;
        }
        else
        {
            table = SilkTables.CbLagsStage3TenMs;
            cbkSize = 12;
        }

        int minLag = 2 * FsKHz;
        int maxLag = 18 * FsKHz;
        int lag = minLag + Indices.LagIndex;
        for (int k = 0; k < NbSubfr; k++)
        {
            _pitchL[k] = lag + table[(k * cbkSize) + Indices.ContourIndex];
            _pitchL[k] = Limit(_pitchL[k], minLag, maxLag);
        }
    }

    // silk_NLSF_decode (NLSF_decode.c) with silk_NLSF_residual_dequant and silk_NLSF_stabilize.
    private void NlsfDecode(Span<short> nlsfQ15)
    {
        int order = LpcOrder;
        Span<short> ecIx = stackalloc short[MaxLpcOrder];
        Span<byte> predQ8 = stackalloc byte[MaxLpcOrder];
        SilkSyntax.NlsfUnpack(ecIx, predQ8, _wide, Indices.Nlsf[0]);
        Span<short> resQ10 = stackalloc short[MaxLpcOrder];
        int quantStepSizeQ16 = _wide ? 9830 : 11796;
        int outQ10 = 0;
        for (int i = order - 1; i >= 0; i--)
        {
            int predQ10 = Smulbb(outQ10, (short)predQ8[i]) >> 8;
            outQ10 = Indices.Nlsf[i + 1] << 10;
            if (outQ10 > 0)
            {
                outQ10 -= 102;
            }
            else if (outQ10 < 0)
            {
                outQ10 += 102;
            }

            outQ10 = Smlawb(predQ10, outQ10, quantStepSizeQ16);
            resQ10[i] = (short)outQ10;
        }

        ReadOnlySpan<byte> cb1 = _wide ? SilkTables.NlsfCb1WbQ8 : SilkTables.NlsfCb1NbMbQ8;
        ReadOnlySpan<short> wght = _wide ? SilkTables.NlsfCb1WbWghtQ9 : SilkTables.NlsfCb1NbMbWghtQ9;
        int at = Indices.Nlsf[0] * order;
        for (int i = 0; i < order; i++)
        {
            int tmp = (((resQ10[i] << 14) / wght[at + i]) + (cb1[at + i] << 7));
            nlsfQ15[i] = (short)Limit(tmp, 0, 32767);
        }

        NlsfStabilize(nlsfQ15, _wide ? SilkTables.NlsfDeltaMinWbQ15 : SilkTables.NlsfDeltaMinNbMbQ15, order);
    }

    private static void NlsfStabilize(Span<short> nlsf, ReadOnlySpan<short> deltaMin, int l)
    {
        int loops;
        for (loops = 0; loops < 20; loops++)
        {
            int minDiff = nlsf[0] - deltaMin[0];
            int idx = 0;
            for (int i = 1; i <= l - 1; i++)
            {
                int diff = nlsf[i] - (nlsf[i - 1] + deltaMin[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    idx = i;
                }
            }

            int diffEnd = (1 << 15) - (nlsf[l - 1] + deltaMin[l]);
            if (diffEnd < minDiff)
            {
                minDiff = diffEnd;
                idx = l;
            }

            if (minDiff >= 0)
            {
                return;
            }

            if (idx == 0)
            {
                nlsf[0] = deltaMin[0];
            }
            else if (idx == l)
            {
                nlsf[l - 1] = (short)((1 << 15) - deltaMin[l]);
            }
            else
            {
                int minCenter = 0;
                for (int k = 0; k < idx; k++)
                {
                    minCenter += deltaMin[k];
                }

                minCenter += deltaMin[idx] >> 1;
                int maxCenter = 1 << 15;
                for (int k = l; k > idx; k--)
                {
                    maxCenter -= deltaMin[k];
                }

                maxCenter -= deltaMin[idx] >> 1;
                short center = (short)Limit(RshiftRound(nlsf[idx - 1] + nlsf[idx], 1), minCenter, maxCenter);
                nlsf[idx - 1] = (short)(center - (deltaMin[idx] >> 1));
                nlsf[idx] = (short)(nlsf[idx - 1] + deltaMin[idx]);
            }
        }

        // The insertion sort and clamps of the last resort.
        for (int i = 1; i < l; i++)
        {
            int value = nlsf[i];
            int j;
            for (j = i - 1; j >= 0 && value < nlsf[j]; j--)
            {
                nlsf[j + 1] = nlsf[j];
            }

            nlsf[j + 1] = (short)value;
        }

        nlsf[0] = (short)Math.Max(nlsf[0], deltaMin[0]);
        for (int i = 1; i < l; i++)
        {
            nlsf[i] = (short)Math.Max(nlsf[i], Sat16(nlsf[i - 1] + deltaMin[i]));
        }

        nlsf[l - 1] = (short)Math.Min(nlsf[l - 1], (1 << 15) - deltaMin[l]);
        for (int i = l - 2; i >= 0; i--)
        {
            nlsf[i] = (short)Math.Min(nlsf[i], nlsf[i + 1] - deltaMin[i + 1]);
        }
    }

    private static void Nlsf2AFindPoly(Span<int> output, ReadOnlySpan<int> cLsf, int cAt, int dd)
    {
        output[0] = 1 << 16;
        output[1] = -cLsf[cAt];
        for (int k = 1; k < dd; k++)
        {
            int ftmp = cLsf[cAt + (2 * k)];
            output[k + 1] = unchecked((output[k - 1] << 1) - (int)RshiftRound64((long)ftmp * output[k], 16));
            for (int n = k; n > 1; n--)
            {
                output[n] = unchecked(output[n] + output[n - 2] - (int)RshiftRound64((long)ftmp * output[n - 1], 16));
            }

            output[1] -= ftmp;
        }
    }

    private static readonly byte[] Ordering16 = [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];
    private static readonly byte[] Ordering10 = [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];

    // silk_NLSF2A: the prediction coefficients of normalized line spectral frequencies.
    internal static void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        byte[] ordering = d == 16 ? Ordering16 : Ordering10;
        Span<int> cosLsfQa = stackalloc int[MaxLpcOrder];
        Span<int> p = stackalloc int[(MaxLpcOrder / 2) + 1];
        Span<int> q = stackalloc int[(MaxLpcOrder / 2) + 1];
        Span<int> a32Qa1 = stackalloc int[MaxLpcOrder];
        for (int k = 0; k < d; k++)
        {
            int fInt = nlsf[k] >> (15 - 7);
            int fFrac = nlsf[k] - (fInt << (15 - 7));
            int cosVal = SilkTables.LsfCosTabQ12[fInt];
            int delta = SilkTables.LsfCosTabQ12[fInt + 1] - cosVal;
            cosLsfQa[ordering[k]] = RshiftRound((cosVal << 8) + (delta * fFrac), 20 - 16);
        }

        int dd = d >> 1;
        Nlsf2AFindPoly(p, cosLsfQa, 0, dd);
        Nlsf2AFindPoly(q, cosLsfQa, 1, dd);
        for (int k = 0; k < dd; k++)
        {
            int ptmp = p[k + 1] + p[k];
            int qtmp = q[k + 1] - q[k];
            a32Qa1[k] = -qtmp - ptmp;
            a32Qa1[d - k - 1] = qtmp - ptmp;
        }

        LpcFit(aQ12, a32Qa1, 12, 17, d);
        for (int i = 0; LpcInversePredGain(aQ12, d) == 0 && i < 16; i++)
        {
            Bwexpander32(a32Qa1, d, 65536 - (2 << i));
            for (int k = 0; k < d; k++)
            {
                aQ12[k] = (short)RshiftRound(a32Qa1[k], 17 - 12);
            }
        }
    }

    private static void LpcFit(Span<short> aQout, Span<int> aQin, int qOut, int qIn, int d)
    {
        int i;
        int idx = 0;
        for (i = 0; i < 10; i++)
        {
            int maxabs = 0;
            for (int k = 0; k < d; k++)
            {
                int absval = Math.Abs(aQin[k]);
                if (absval > maxabs)
                {
                    maxabs = absval;
                    idx = k;
                }
            }

            maxabs = RshiftRound(maxabs, qIn - qOut);
            if (maxabs > short.MaxValue)
            {
                maxabs = Math.Min(maxabs, 163838);
                int chirpQ16 = 65470 - (((maxabs - short.MaxValue) << 14) / ((maxabs * (idx + 1)) >> 2));
                Bwexpander32(aQin, d, chirpQ16);
            }
            else
            {
                break;
            }
        }

        if (i == 10)
        {
            for (int k = 0; k < d; k++)
            {
                aQout[k] = (short)Sat16(RshiftRound(aQin[k], qIn - qOut));
                aQin[k] = aQout[k] << (qIn - qOut);
            }
        }
        else
        {
            for (int k = 0; k < d; k++)
            {
                aQout[k] = (short)RshiftRound(aQin[k], qIn - qOut);
            }
        }
    }

    // silk_bwexpander and silk_bwexpander_32.
    internal static void Bwexpander(Span<short> ar, int d, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = (short)RshiftRound(chirpQ16 * ar[i], 16);
            chirpQ16 += RshiftRound(chirpQ16 * chirpMinusOneQ16, 16);
        }

        ar[d - 1] = (short)RshiftRound(chirpQ16 * ar[d - 1], 16);
    }

    private static void Bwexpander32(Span<int> ar, int d, int chirpQ16)
    {
        int chirpMinusOneQ16 = chirpQ16 - 65536;
        for (int i = 0; i < d - 1; i++)
        {
            ar[i] = Smulww(chirpQ16, ar[i]);
            chirpQ16 += RshiftRound(chirpQ16 * chirpMinusOneQ16, 16);
        }

        ar[d - 1] = Smulww(chirpQ16, ar[d - 1]);
    }

    // silk_LPC_inverse_pred_gain: the inverse of the prediction gain, 0 where the filter is unstable.
    internal static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order)
    {
        Span<int> atmp = stackalloc int[MaxLpcOrder];
        int dcResp = 0;
        for (int k = 0; k < order; k++)
        {
            dcResp += aQ12[k];
            atmp[k] = aQ12[k] << (24 - 12);
        }

        if (dcResp >= 4096)
        {
            return 0;
        }

        const int aLimit = 16773022;
        int invGainQ30 = 1 << 30;
        int k2;
        for (k2 = order - 1; k2 > 0; k2--)
        {
            if (atmp[k2] > aLimit || atmp[k2] < -aLimit)
            {
                return 0;
            }

            int rcQ31 = -(atmp[k2] << (31 - 24));
            int rcMult1Q30 = (1 << 30) - Smmul(rcQ31, rcQ31);
            invGainQ30 = Smmul(invGainQ30, rcMult1Q30) << 2;
            if (invGainQ30 < 107374)
            {
                return 0;
            }

            int mult2Q = 32 - Clz32(Math.Abs(rcMult1Q30));
            int rcMult2 = Inverse32VarQ(rcMult1Q30, mult2Q + 30);
            for (int n = 0; n < (k2 + 1) >> 1; n++)
            {
                int tmp1 = atmp[n];
                int tmp2 = atmp[k2 - n - 1];
                long tmp64 = RshiftRound64((long)SubSat32(tmp1, (int)RshiftRound64((long)tmp2 * rcQ31, 31)) * rcMult2, mult2Q);
                if (tmp64 > int.MaxValue || tmp64 < int.MinValue)
                {
                    return 0;
                }

                atmp[n] = (int)tmp64;
                tmp64 = RshiftRound64((long)SubSat32(tmp2, (int)RshiftRound64((long)tmp1 * rcQ31, 31)) * rcMult2, mult2Q);
                if (tmp64 > int.MaxValue || tmp64 < int.MinValue)
                {
                    return 0;
                }

                atmp[k2 - n - 1] = (int)tmp64;
            }
        }

        if (atmp[k2] > aLimit || atmp[k2] < -aLimit)
        {
            return 0;
        }

        int rc0Q31 = -(atmp[0] << (31 - 24));
        int rcMult1 = (1 << 30) - Smmul(rc0Q31, rc0Q31);
        invGainQ30 = Smmul(invGainQ30, rcMult1) << 2;
        if (invGainQ30 < 107374)
        {
            return 0;
        }

        return invGainQ30;
    }

    // silk_LPC_analysis_filter: out[ix] for ix from d to len - 1, the first d samples zero.
    private static void LpcAnalysisFilter(short[] output, int outAt, short[] input, int inAt, ReadOnlySpan<short> b, int len, int d)
    {
        for (int ix = d; ix < len; ix++)
        {
            int ip = inAt + ix - 1;
            int out32Q12 = Smulbb(input[ip], b[0]);
            out32Q12 = unchecked(out32Q12 + (input[ip - 1] * b[1]));
            out32Q12 = unchecked(out32Q12 + (input[ip - 2] * b[2]));
            out32Q12 = unchecked(out32Q12 + (input[ip - 3] * b[3]));
            out32Q12 = unchecked(out32Q12 + (input[ip - 4] * b[4]));
            out32Q12 = unchecked(out32Q12 + (input[ip - 5] * b[5]));
            for (int j = 6; j < d; j += 2)
            {
                out32Q12 = unchecked(out32Q12 + (input[ip - j] * b[j]));
                out32Q12 = unchecked(out32Q12 + (input[ip - j - 1] * b[j + 1]));
            }

            out32Q12 = unchecked((input[ip + 1] << 12) - out32Q12);
            output[outAt + ix] = (short)Sat16(RshiftRound(out32Q12, 12));
        }

        Array.Clear(output, outAt, d);
    }

    // silk_decode_core.
    private void DecodeCore(short[] xq, int xqAt, ReadOnlySpan<short> pulses)
    {
        int lag = 0;
        short[] sLtp = new short[LtpMemLength];
        int[] sLtpQ15 = new int[LtpMemLength + FrameLength];
        int[] resQ14 = new int[SubfrLength];
        int[] sLpcQ14 = new int[SubfrLength + MaxLpcOrder];
        int offsetQ10 = SilkTables.QuantizationOffsetsQ10[((Indices.SignalType >> 1) * 2) + Indices.QuantOffsetType];
        bool interpolate = Indices.NlsfInterpCoef < 4;
        int randSeed = Indices.Seed;
        for (int i = 0; i < FrameLength; i++)
        {
            randSeed = Rand(randSeed);
            ExcQ14[i] = pulses[i] << 14;
            if (ExcQ14[i] > 0)
            {
                ExcQ14[i] -= 80 << 4;
            }
            else if (ExcQ14[i] < 0)
            {
                ExcQ14[i] += 80 << 4;
            }

            ExcQ14[i] += offsetQ10 << 4;
            if (randSeed < 0)
            {
                ExcQ14[i] = -ExcQ14[i];
            }

            randSeed = unchecked(randSeed + pulses[i]);
        }

        Array.Copy(_sLpcQ14Buf, sLpcQ14, MaxLpcOrder);
        int pexc = 0;
        int pxq = xqAt;
        int sLtpBufIdx = LtpMemLength;
        Span<short> aQ12Tmp = stackalloc short[MaxLpcOrder];
        for (int k = 0; k < NbSubfr; k++)
        {
            int[] presQ14;
            int presAt;
            ReadOnlySpan<short> aQ12 = _predCoefQ12.AsSpan((k >> 1) * MaxLpcOrder, MaxLpcOrder);
            aQ12.Slice(0, LpcOrder).CopyTo(aQ12Tmp);
            Span<short> bQ14 = _ltpCoefQ14.AsSpan(k * LtpOrder, LtpOrder);
            int signalType = Indices.SignalType;
            int gainQ10 = _gainsQ16[k] >> 6;
            int invGainQ31 = Inverse32VarQ(_gainsQ16[k], 47);
            int gainAdjQ16;
            if (_gainsQ16[k] != _prevGainQ16)
            {
                gainAdjQ16 = Div32VarQ(_prevGainQ16, _gainsQ16[k], 16);
                for (int i = 0; i < MaxLpcOrder; i++)
                {
                    sLpcQ14[i] = Smulww(gainAdjQ16, sLpcQ14[i]);
                }
            }
            else
            {
                gainAdjQ16 = 1 << 16;
            }

            _prevGainQ16 = _gainsQ16[k];
            if (LossCnt != 0 && PrevSignalType == 2 && Indices.SignalType != 2 && k < MaxNbSubfr / 2)
            {
                bQ14.Clear();
                bQ14[LtpOrder / 2] = 1 << 12;
                signalType = 2;
                _pitchL[k] = LagPrev;
            }

            if (signalType == 2)
            {
                lag = _pitchL[k];
                if (k == 0 || (k == 2 && interpolate))
                {
                    int startIdx = LtpMemLength - lag - LpcOrder - (LtpOrder / 2);
                    if (k == 2)
                    {
                        Array.Copy(xq, xqAt, _outBuf, LtpMemLength, 2 * SubfrLength);
                    }

                    LpcAnalysisFilter(sLtp, startIdx, _outBuf, startIdx + (k * SubfrLength), aQ12, LtpMemLength - startIdx, LpcOrder);
                    if (k == 0)
                    {
                        invGainQ31 = Smulwb(invGainQ31, _ltpScaleQ14) << 2;
                    }

                    for (int i = 0; i < lag + (LtpOrder / 2); i++)
                    {
                        sLtpQ15[sLtpBufIdx - i - 1] = Smulwb(invGainQ31, sLtp[LtpMemLength - i - 1]);
                    }
                }
                else if (gainAdjQ16 != 1 << 16)
                {
                    for (int i = 0; i < lag + (LtpOrder / 2); i++)
                    {
                        sLtpQ15[sLtpBufIdx - i - 1] = Smulww(gainAdjQ16, sLtpQ15[sLtpBufIdx - i - 1]);
                    }
                }
            }

            if (signalType == 2)
            {
                int predLag = sLtpBufIdx - lag + (LtpOrder / 2);
                presQ14 = resQ14;
                presAt = 0;
                for (int i = 0; i < SubfrLength; i++)
                {
                    int ltpPredQ13 = 2;
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag], bQ14[0]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 1], bQ14[1]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 2], bQ14[2]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 3], bQ14[3]);
                    ltpPredQ13 = Smlawb(ltpPredQ13, sLtpQ15[predLag - 4], bQ14[4]);
                    predLag++;
                    presQ14[i] = unchecked(ExcQ14[pexc + i] + (ltpPredQ13 << 1));
                    sLtpQ15[sLtpBufIdx] = presQ14[i] << 1;
                    sLtpBufIdx++;
                }
            }
            else
            {
                presQ14 = ExcQ14;
                presAt = pexc;
            }

            for (int i = 0; i < SubfrLength; i++)
            {
                int lpcPredQ10 = LpcOrder >> 1;
                for (int j = 0; j < LpcOrder; j++)
                {
                    lpcPredQ10 = Smlawb(lpcPredQ10, sLpcQ14[MaxLpcOrder + i - 1 - j], aQ12Tmp[j]);
                }

                sLpcQ14[MaxLpcOrder + i] = AddSat32(presQ14[presAt + i], LshiftSat32(lpcPredQ10, 4));
                xq[pxq + i] = (short)Sat16(RshiftRound(Smulww(sLpcQ14[MaxLpcOrder + i], gainQ10), 8));
            }

            Array.Copy(sLpcQ14, SubfrLength, sLpcQ14, 0, MaxLpcOrder);
            pexc += SubfrLength;
            pxq += SubfrLength;
        }

        Array.Copy(sLpcQ14, _sLpcQ14Buf, MaxLpcOrder);
    }

    // silk_PLC (PLC.c): the state update of a good frame, or the concealment of a lost one.
    private void Plc(short[] frame, int at, bool lost)
    {
        if (FsKHz != _plcFsKHz)
        {
            PlcReset();
            _plcFsKHz = FsKHz;
        }

        if (lost)
        {
            PlcConceal(frame, at);
            LossCnt++;
        }
        else
        {
            PlcUpdate();
        }
    }

    private void PlcUpdate()
    {
        PrevSignalType = Indices.SignalType;
        int ltpGainQ14 = 0;
        if (Indices.SignalType == 2)
        {
            for (int j = 0; j * SubfrLength < _pitchL[NbSubfr - 1]; j++)
            {
                if (j == NbSubfr)
                {
                    break;
                }

                int tempLtpGainQ14 = 0;
                for (int i = 0; i < LtpOrder; i++)
                {
                    tempLtpGainQ14 += _ltpCoefQ14[((NbSubfr - 1 - j) * LtpOrder) + i];
                }

                if (tempLtpGainQ14 > ltpGainQ14)
                {
                    ltpGainQ14 = tempLtpGainQ14;
                    Array.Copy(_ltpCoefQ14, (NbSubfr - 1 - j) * LtpOrder, _plcLtpCoefQ14, 0, LtpOrder);
                    _plcPitchLQ8 = _pitchL[NbSubfr - 1 - j] << 8;
                }
            }

            Array.Clear(_plcLtpCoefQ14);
            _plcLtpCoefQ14[LtpOrder / 2] = (short)ltpGainQ14;
            if (ltpGainQ14 < 11469)
            {
                int tmp = 11469 << 10;
                int scaleQ10 = tmp / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                {
                    _plcLtpCoefQ14[i] = (short)(Smulbb(_plcLtpCoefQ14[i], scaleQ10) >> 10);
                }
            }
            else if (ltpGainQ14 > 15565)
            {
                int tmp = 15565 << 14;
                int scaleQ14 = tmp / Math.Max(ltpGainQ14, 1);
                for (int i = 0; i < LtpOrder; i++)
                {
                    _plcLtpCoefQ14[i] = (short)(Smulbb(_plcLtpCoefQ14[i], scaleQ14) >> 14);
                }
            }
        }
        else
        {
            _plcPitchLQ8 = Smulbb(FsKHz, 18) << 8;
            Array.Clear(_plcLtpCoefQ14);
        }

        Array.Copy(_predCoefQ12, MaxLpcOrder, _plcPrevLpcQ12, 0, LpcOrder);
        _plcPrevLtpScaleQ14 = (short)_ltpScaleQ14;
        Array.Copy(_gainsQ16, NbSubfr - 2, _plcPrevGainQ16, 0, 2);
        _plcSubfrLength = SubfrLength;
        _plcNbSubfr = NbSubfr;
    }

    private static ReadOnlySpan<short> HarmAttQ15 => [32440, 31130];

    private static ReadOnlySpan<short> RandAttenuateVQ15 => [31130, 26214];

    private static ReadOnlySpan<short> RandAttenuateUvQ15 => [32440, 29491];

    private void PlcConceal(short[] frame, int at)
    {
        int ltpMem = LtpMemLength;
        int[] sLtpQ14 = new int[ltpMem + FrameLength];
        short[] sLtp = new short[ltpMem];
        Span<int> prevGainQ10 = stackalloc int[2];
        prevGainQ10[0] = _plcPrevGainQ16[0] >> 6;
        prevGainQ10[1] = _plcPrevGainQ16[1] >> 6;
        if (FirstFrameAfterReset)
        {
            Array.Clear(_plcPrevLpcQ12);
        }

        // silk_PLC_energy
        short[] excBuf = new short[2 * SubfrLength];
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < SubfrLength; i++)
            {
                excBuf[(k * SubfrLength) + i] = (short)Sat16(Smulww(ExcQ14[i + ((k + NbSubfr - 2) * SubfrLength)], prevGainQ10[k]) >> 8);
            }
        }

        SumSqrShift(out int energy1, out int shift1, excBuf.AsSpan(0, SubfrLength));
        SumSqrShift(out int energy2, out int shift2, excBuf.AsSpan(SubfrLength, SubfrLength));
        int randAt;
        if ((energy1 >> shift2) < (energy2 >> shift1))
        {
            randAt = Math.Max(0, ((_plcNbSubfr - 1) * _plcSubfrLength) - RandBufSize);
        }
        else
        {
            randAt = Math.Max(0, (_plcNbSubfr * _plcSubfrLength) - RandBufSize);
        }

        Span<short> bQ14 = _plcLtpCoefQ14;
        short randScaleQ14 = _plcRandScaleQ14;
        int harmGainQ15 = HarmAttQ15[Math.Min(1, LossCnt)];
        int randGainQ15 = PrevSignalType == 2 ? RandAttenuateVQ15[Math.Min(1, LossCnt)] : RandAttenuateUvQ15[Math.Min(1, LossCnt)];
        Bwexpander(_plcPrevLpcQ12, LpcOrder, 64881);
        Span<short> aQ12 = stackalloc short[MaxLpcOrder];
        _plcPrevLpcQ12.AsSpan(0, LpcOrder).CopyTo(aQ12);
        if (LossCnt == 0)
        {
            randScaleQ14 = 1 << 14;
            if (PrevSignalType == 2)
            {
                for (int i = 0; i < LtpOrder; i++)
                {
                    randScaleQ14 -= bQ14[i];
                }

                randScaleQ14 = (short)Math.Max((short)3277, randScaleQ14);
                randScaleQ14 = (short)(Smulbb(randScaleQ14, _plcPrevLtpScaleQ14) >> 14);
            }
            else
            {
                int invGainQ30 = LpcInversePredGain(_plcPrevLpcQ12, LpcOrder);
                int downScaleQ30 = Math.Min((1 << 30) >> 3, invGainQ30);
                downScaleQ30 = Math.Max((1 << 30) >> 8, downScaleQ30);
                downScaleQ30 <<= 3;
                randGainQ15 = Smulwb(downScaleQ30, randGainQ15) >> 14;
            }
        }

        int randSeed = _plcRandSeed;
        int lag = RshiftRound(_plcPitchLQ8, 8);
        int sLtpBufIdx = ltpMem;
        int idx = ltpMem - lag - LpcOrder - (LtpOrder / 2);
        LpcAnalysisFilter(sLtp, idx, _outBuf, idx, aQ12, ltpMem - idx, LpcOrder);
        int invGainQ30b = Inverse32VarQ(_plcPrevGainQ16[1], 46);
        invGainQ30b = Math.Min(invGainQ30b, int.MaxValue >> 1);
        for (int i = idx + LpcOrder; i < ltpMem; i++)
        {
            sLtpQ14[i] = Smulwb(invGainQ30b, sLtp[i]);
        }

        for (int k = 0; k < NbSubfr; k++)
        {
            int predLag = sLtpBufIdx - lag + (LtpOrder / 2);
            for (int i = 0; i < SubfrLength; i++)
            {
                int ltpPredQ12 = 2;
                ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag], bQ14[0]);
                ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 1], bQ14[1]);
                ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 2], bQ14[2]);
                ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 3], bQ14[3]);
                ltpPredQ12 = Smlawb(ltpPredQ12, sLtpQ14[predLag - 4], bQ14[4]);
                predLag++;
                randSeed = Rand(randSeed);
                idx = (randSeed >> 25) & RandBufMask;
                sLtpQ14[sLtpBufIdx] = Smlawb(ltpPredQ12, ExcQ14[randAt + idx], randScaleQ14) << 2;
                sLtpBufIdx++;
            }

            for (int j = 0; j < LtpOrder; j++)
            {
                bQ14[j] = (short)(Smulbb(harmGainQ15, bQ14[j]) >> 15);
            }

            randScaleQ14 = (short)(Smulbb(randScaleQ14, randGainQ15) >> 15);
            _plcPitchLQ8 = Smlawb(_plcPitchLQ8, _plcPitchLQ8, 655);
            _plcPitchLQ8 = Math.Min(_plcPitchLQ8, Smulbb(18, FsKHz) << 8);
            lag = RshiftRound(_plcPitchLQ8, 8);
        }

        int sLpcAt = ltpMem - MaxLpcOrder;
        Array.Copy(_sLpcQ14Buf, 0, sLtpQ14, sLpcAt, MaxLpcOrder);
        for (int i = 0; i < FrameLength; i++)
        {
            int lpcPredQ10 = LpcOrder >> 1;
            for (int j = 0; j < LpcOrder; j++)
            {
                lpcPredQ10 = Smlawb(lpcPredQ10, sLtpQ14[sLpcAt + MaxLpcOrder + i - j - 1], aQ12[j]);
            }

            sLtpQ14[sLpcAt + MaxLpcOrder + i] = AddSat32(sLtpQ14[sLpcAt + MaxLpcOrder + i], LshiftSat32(lpcPredQ10, 4));
            frame[at + i] = (short)Sat16(Sat16(RshiftRound(Smulww(sLtpQ14[sLpcAt + MaxLpcOrder + i], prevGainQ10[1]), 8)));
        }

        Array.Copy(sLtpQ14, sLpcAt + FrameLength, _sLpcQ14Buf, 0, MaxLpcOrder);
        _plcRandSeed = randSeed;
        _plcRandScaleQ14 = randScaleQ14;
        for (int i = 0; i < MaxNbSubfr; i++)
        {
            _pitchL[i] = lag;
        }
    }

    // silk_PLC_glue_frames: the first frame after a concealed one fades in from the energy of the concealment.
    private void PlcGlueFrames(short[] frame, int at, int length)
    {
        if (LossCnt != 0)
        {
            SumSqrShift(out _plcConcEnergy, out _plcConcEnergyShift, frame.AsSpan(at, length));
            _plcLastFrameLost = true;
        }
        else
        {
            if (_plcLastFrameLost)
            {
                SumSqrShift(out int energy, out int energyShift, frame.AsSpan(at, length));
                if (energyShift > _plcConcEnergyShift)
                {
                    _plcConcEnergy >>= energyShift - _plcConcEnergyShift;
                }
                else if (energyShift < _plcConcEnergyShift)
                {
                    energy >>= _plcConcEnergyShift - energyShift;
                }

                if (energy > _plcConcEnergy)
                {
                    int lz = Clz32(_plcConcEnergy) - 1;
                    _plcConcEnergy <<= lz;
                    energy >>= Math.Max(24 - lz, 0);
                    int fracQ24 = _plcConcEnergy / Math.Max(energy, 1);
                    int gainQ16 = SqrtApprox(fracQ24) << 4;
                    int slopeQ16 = ((1 << 16) - gainQ16) / length;
                    slopeQ16 <<= 2;
                    for (int i = 0; i < length; i++)
                    {
                        frame[at + i] = (short)Smulwb(gainQ16, frame[at + i]);
                        gainQ16 += slopeQ16;
                        if (gainQ16 > 1 << 16)
                        {
                            break;
                        }
                    }
                }
            }

            _plcLastFrameLost = false;
        }
    }

    // silk_CNG: the smoothing of the comfort noise's parameters in a good frame without voice activity, and its synthesis in a concealed frame.
    private void Cng(short[] frame, int at, int length)
    {
        if (FsKHz != _cngFsKHz)
        {
            CngReset();
            _cngFsKHz = FsKHz;
        }

        if (LossCnt == 0 && PrevSignalType == 0)
        {
            for (int i = 0; i < LpcOrder; i++)
            {
                _cngSmthNlsfQ15[i] += (short)Smulwb(_prevNlsfQ15[i] - _cngSmthNlsfQ15[i], 16348);
            }

            int maxGainQ16 = 0;
            int subfr = 0;
            for (int i = 0; i < NbSubfr; i++)
            {
                if (_gainsQ16[i] > maxGainQ16)
                {
                    maxGainQ16 = _gainsQ16[i];
                    subfr = i;
                }
            }

            Array.Copy(_cngExcBufQ14, 0, _cngExcBufQ14, SubfrLength, (NbSubfr - 1) * SubfrLength);
            Array.Copy(ExcQ14, subfr * SubfrLength, _cngExcBufQ14, 0, SubfrLength);
            for (int i = 0; i < NbSubfr; i++)
            {
                _cngSmthGainQ16 += Smulwb(_gainsQ16[i] - _cngSmthGainQ16, 4634);
                if (Smulww(_cngSmthGainQ16, 46396) > _gainsQ16[i])
                {
                    _cngSmthGainQ16 = _gainsQ16[i];
                }
            }
        }

        if (LossCnt != 0)
        {
            int[] cngSigQ14 = new int[length + MaxLpcOrder];
            int gainQ16 = Smulww(_plcRandScaleQ14, _plcPrevGainQ16[1]);
            if (gainQ16 >= 1 << 21 || _cngSmthGainQ16 > 1 << 23)
            {
                gainQ16 = Smulltt(gainQ16, gainQ16);
                gainQ16 = unchecked((Smulltt(_cngSmthGainQ16, _cngSmthGainQ16)) - (gainQ16 << 5));
                gainQ16 = SqrtApprox(gainQ16) << 16;
            }
            else
            {
                gainQ16 = Smulww(gainQ16, gainQ16);
                gainQ16 = unchecked(Smulww(_cngSmthGainQ16, _cngSmthGainQ16) - (gainQ16 << 5));
                gainQ16 = SqrtApprox(gainQ16) << 8;
            }

            int gainQ10 = gainQ16 >> 6;

            // silk_CNG_exc
            int excMask = 255;
            while (excMask > length)
            {
                excMask >>= 1;
            }

            int seed = _cngRandSeed;
            for (int i = 0; i < length; i++)
            {
                seed = Rand(seed);
                int idx = (seed >> 24) & excMask;
                cngSigQ14[MaxLpcOrder + i] = _cngExcBufQ14[idx];
            }

            _cngRandSeed = seed;
            Span<short> aQ12 = stackalloc short[MaxLpcOrder];
            Nlsf2A(aQ12, _cngSmthNlsfQ15, LpcOrder);
            Array.Copy(_cngSynthState, cngSigQ14, MaxLpcOrder);
            for (int i = 0; i < length; i++)
            {
                int lpcPredQ10 = LpcOrder >> 1;
                for (int j = 0; j < LpcOrder; j++)
                {
                    lpcPredQ10 = Smlawb(lpcPredQ10, cngSigQ14[MaxLpcOrder + i - 1 - j], aQ12[j]);
                }

                cngSigQ14[MaxLpcOrder + i] = AddSat32(cngSigQ14[MaxLpcOrder + i], LshiftSat32(lpcPredQ10, 4));
                frame[at + i] = (short)Sat16(frame[at + i] + Sat16(RshiftRound(Smulww(cngSigQ14[MaxLpcOrder + i], gainQ10), 8)));
            }

            Array.Copy(cngSigQ14, length, _cngSynthState, 0, MaxLpcOrder);
        }
        else
        {
            Array.Clear(_cngSynthState, 0, LpcOrder);
        }
    }
}
