// Reading derived from libspeex 1.2.1 (nb_celp.c: nb_decoder_init, nb_decode; ltp.c: pitch_unquant_3tap, forced_pitch_unquant; cb_search.c: split_cb_shape_sign_unquant,
// noise_codebook_unquant; modes.c: the narrowband submodes), Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation and the other holders named in its COPYING, under
// the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using static Encspect.Formats.Speex.Decoding.Fixed;

namespace Encspect.Formats.Speex.Decoding;

// The narrowband layer of a frame: the CELP decoder of libspeex's fixed-point build (nb_decode), from the parameters the parser's walk read (SpeexLayout) instead of from the
// bits. Packet loss concealment (nb_decode_lost) is not part of it: a frame that cannot be decoded is a gap, and the decoder starts over after one (SpeexFrameDecoder).
internal sealed class Narrowband
{
    public const int Order = 10;
    public const int FrameSize = 160;
    public const int SubframeSize = 40;
    public const int Subframes = 4;
    private const int PitchStart = 17;
    private const int PitchEnd = 144;
    private const int Buffer = FrameSize + (2 * PitchEnd) + SubframeSize + 12;
    private const int ExcBase = (2 * PitchEnd) + SubframeSize + 6;
    private const int LspMargin = 16;

    // The submode parameters the reconstruction needs (modes.c): the pitch variation, whether the pitch is forced, the bits of the gain correction, the second codebook, the
    // low-rate LSP quantizer, the gain codebook and its bits, the shape codebook with its subvectors, and the gain of the comb filter in Q15.
    private readonly record struct Submode(int LbrPitch, bool IsForced, int HaveSubframeGain, bool IsDouble, bool IsLowRate, int GainBits, int Subvectors, int CombGain);

    private static readonly Submode[] Submodes =
    [
        default,
        new(0, true, 0, false, true, 0, 0, -1),
        new(0, false, 0, false, true, 5, 4, 19661),
        new(-1, false, 1, false, true, 5, 4, 18022),
        new(-1, false, 1, false, true, 5, 5, 14746),
        new(-1, false, 3, false, false, 7, 8, 8192),
        new(-1, false, 3, false, false, 7, 8, 4915),
        new(-1, false, 3, true, false, 7, 8, 1638),
        new(0, true, 0, false, true, 0, 2, 16384),
    ];

    private static readonly int[] OpenLoopGainTable =
    [
        18900, 25150, 33468, 44536, 59265, 78865, 104946, 139653, 185838, 247297, 329081, 437913, 582736, 775454, 1031906, 1373169, 1827293, 2431601, 3235761, 4305867, 5729870,
        7624808, 10146425, 13501971, 17967238, 23909222, 31816294, 42338330, 56340132, 74972501, 99766822, 132760927,
    ];

    private static readonly short[] GainScale3 = [1002, 2680, 5086, 7016, 9108, 11781, 15380, 21740];

    private static readonly short[] GainScale1 = [11546, 17224];

    private readonly short[] _excBuffer = new short[Buffer];
    private readonly short[] _oldQlsp = new short[Order];
    private readonly short[] _interpQlpc = new short[Order];
    private readonly int[] _memSp = new int[Order];
    private readonly int[] _memHp = new int[2];
    private readonly int[] _piGain = new int[Subframes];
    private readonly bool _isWideband;
    private readonly int[] _work = new int[2 * ((Order / 2) + 1) * (Order + 3)];
    private bool _isFirst = true;
    private uint _seed = 1000;
    private int _vocM1;
    private int _vocM2;
    private int _vocMean;
    private int _vocOffset;

    public Narrowband(bool isWideband)
    {
        _isWideband = isWideband;
    }

    // SPEEX_GET_DTX_STATUS: whether the last frame of submode 1 announced discontinuous transmission.
    public bool IsDtx { get; private set; }

    // SPEEX_GET_PI_GAIN: the gain of the synthesis filter at pi of each subframe, as of the last frame that had a submode other than 0.
    public ReadOnlySpan<int> PiGain => _piGain;

    // SPEEX_GET_EXC: the RMS of the excitation of a subframe.
    public int ExcitationRms(int subframe) => Filters.Rms16(_excBuffer.AsSpan(ExcBase + (subframe * SubframeSize), SubframeSize), SubframeSize);

    // Decodes one frame into output (160 samples); innovationSave receives the innovation of each subframe where the wideband layers use it (SPEEX_SET_INNOVATION_SAVE), empty where nothing does.
    public void Decode(ReadOnlySpan<int> d, int submodeId, Span<short> output, Span<short> innovationSave)
    {
        short[] excBuffer = _excBuffer;
        excBuffer.AsSpan(FrameSize, (2 * PitchEnd) + SubframeSize + 12).CopyTo(excBuffer);
        if (submodeId == 0)
        {
            DecodeNull(output);
            return;
        }

        Submode submode = Submodes[submodeId];
        Span<short> qlsp = stackalloc short[Order];
        if (submode.IsLowRate)
        {
            Lsp.UnquantLowRate(d.Slice(SpeexLayout.NarrowbandLsp, 3), qlsp);
        }
        else
        {
            Lsp.UnquantNarrowband(d.Slice(SpeexLayout.NarrowbandLsp, 5), qlsp);
        }

        if (_isFirst)
        {
            qlsp.CopyTo(_oldQlsp);
        }

        int olPitch = 0;
        if (submode.LbrPitch != -1)
        {
            olPitch = PitchStart + d[SpeexLayout.OpenLoopPitch];
        }

        int olPitchCoef = 0;
        if (submode.IsForced)
        {
            olPitchCoef = Mult16_16_P15(2185, Shl16(d[SpeexLayout.PitchCoefficient], GainShift));
        }

        int olGain = Mult16_32_Q15(28406, OpenLoopGainTable[d[SpeexLayout.OpenLoopGain]]);
        if (submodeId == 1)
        {
            IsDtx = d[SpeexLayout.VocoderExtra] == 15;
        }

        if (submodeId > 1)
        {
            IsDtx = false;
        }

        int bestPitch = 40;
        int bestPitchGain = 0;
        int pitchAverage = 0;
        Span<int> excitation32 = stackalloc int[SubframeSize];
        Span<int> innovation = stackalloc int[SubframeSize];
        Span<int> second = stackalloc int[SubframeSize];
        Span<short> pitchGain = stackalloc short[3];
        for (int sub = 0; sub < Subframes; sub++)
        {
            int offset = SubframeSize * sub;
            int exc = ExcBase + offset;
            ReadOnlySpan<int> f = d.Slice(SpeexLayout.NarrowbandSubframes + (sub * SpeexLayout.NarrowbandSubframe), SpeexLayout.NarrowbandSubframe);
            excBuffer.AsSpan(exc, SubframeSize).Clear();

            int pitMin;
            int pitMax;
            if (submode.LbrPitch != -1)
            {
                int margin = submode.LbrPitch;
                if (margin != 0)
                {
                    pitMin = olPitch - margin + 1;
                    if (pitMin < PitchStart)
                    {
                        pitMin = PitchStart;
                    }

                    pitMax = olPitch + margin;
                    if (pitMax > PitchEnd)
                    {
                        pitMax = PitchEnd;
                    }
                }
                else
                {
                    pitMin = pitMax = olPitch;
                }
            }
            else
            {
                pitMin = PitchStart;
                pitMax = PitchEnd;
            }

            int pitch;
            if (submode.IsForced)
            {
                pitch = ForcedPitch(excBuffer, exc, excitation32, pitMin, olPitchCoef, pitchGain);
            }
            else
            {
                pitch = PitchThreeTap(excBuffer, exc, excitation32, pitMin, submode, f, pitchGain);
            }

            Filters.Sanitize(excitation32, -262144000, 262144000);

            int tmp = (short)(Math.Abs(pitchGain[1]) + (pitchGain[0] > 0 ? pitchGain[0] : -(pitchGain[0] >> 1)) + (pitchGain[2] > 0 ? pitchGain[2] : -(pitchGain[2] >> 1)));
            pitchAverage = (short)(pitchAverage + tmp);
            if ((tmp > bestPitchGain && Math.Abs((2 * bestPitch) - pitch) >= 3 && Math.Abs((3 * bestPitch) - pitch) >= 4 && Math.Abs((4 * bestPitch) - pitch) >= 5)
                || (tmp > Mult16_16_Q15(19661, bestPitchGain) && (Math.Abs(bestPitch - (2 * pitch)) < 3 || Math.Abs(bestPitch - (3 * pitch)) < 4 || Math.Abs(bestPitch - (4 * pitch)) < 5))
                || (Mult16_16_Q15(21955, tmp) > bestPitchGain && (Math.Abs((2 * bestPitch) - pitch) < 3 || Math.Abs((3 * bestPitch) - pitch) < 4 || Math.Abs((4 * bestPitch) - pitch) < 5)))
            {
                bestPitch = pitch;
                if (tmp > bestPitchGain)
                {
                    bestPitchGain = tmp;
                }
            }

            innovation.Clear();
            int energy;
            if (submode.HaveSubframeGain == 3)
            {
                energy = Mult16_32_Q14(GainScale3[f[SpeexLayout.SubframeGain]], olGain);
            }
            else if (submode.HaveSubframeGain == 1)
            {
                energy = Mult16_32_Q14(GainScale1[f[SpeexLayout.SubframeGain]], olGain);
            }
            else
            {
                energy = olGain;
            }

            Innovation(innovation, submodeId, submode.Subvectors, SubframeShapeCodebook(submodeId), SubframeShapeSize(submodeId), f.Slice(SpeexLayout.SubframeShapes, 8));
            Filters.SignalMultiply(innovation, innovation, energy, SubframeSize);
            if (submode.IsDouble)
            {
                second.Clear();
                Innovation(second, submodeId, submode.Subvectors, SubframeShapeCodebook(submodeId), SubframeShapeSize(submodeId), f.Slice(SpeexLayout.SubframeSecondShapes, 8));
                Filters.SignalMultiply(second, second, Mult16_32_Q15(14895, energy), SubframeSize);
                for (int i = 0; i < SubframeSize; i++)
                {
                    innovation[i] += second[i];
                }
            }

            for (int i = 0; i < SubframeSize; i++)
            {
                excBuffer[exc + i] = (short)Saturate(Pshr(Shl32(excitation32[i], 1) + innovation[i], SigShift), 32767);
            }

            if (!innovationSave.IsEmpty)
            {
                for (int i = 0; i < SubframeSize; i++)
                {
                    innovationSave[offset + i] = (short)Pshr(innovation[i], SigShift);
                }
            }

            if (submodeId == 1)
            {
                Vocoder(excBuffer, exc, innovation, olPitch, olPitchCoef, olGain);
            }
        }

        Span<short> interpQlsp = stackalloc short[Order];
        Span<short> ak = stackalloc short[Order];
        if (submode.CombGain > 0)
        {
            Filters.MultiComb(excBuffer, ExcBase - SubframeSize, output, 2 * SubframeSize, bestPitch, 40, submode.CombGain);
            Filters.MultiComb(excBuffer, ExcBase + SubframeSize, output.Slice(2 * SubframeSize), 2 * SubframeSize, bestPitch, 40, submode.CombGain);
        }
        else
        {
            excBuffer.AsSpan(ExcBase - SubframeSize, FrameSize).CopyTo(output);
        }

        for (int sub = 0; sub < Subframes; sub++)
        {
            Span<short> sp = output.Slice(SubframeSize * sub, SubframeSize);
            Lsp.Interpolate(_oldQlsp, qlsp, interpQlsp, Order, sub, Subframes, LspMargin);
            Lsp.ToLpc(interpQlsp, ak, Order, _work);
            int piGain = LpcScaling;
            for (int i = 0; i < Order; i += 2)
            {
                piGain += ak[i + 1] - ak[i];
            }

            _piGain[sub] = piGain;
            Filters.IirMem16(sp, _interpQlpc, SubframeSize, Order, _memSp);
            ak.CopyTo(_interpQlpc);
        }

        Filters.Highpass(output, output, FrameSize, (_isWideband ? 2 : 0) | 1, _memHp);
        qlsp.CopyTo(_oldQlsp);
        _isFirst = false;
    }

    // Starts over as at the beginning of the stream, as after a gap (SPEEX_RESET_STATE of the library, and the state it does not reset).
    public void Reset()
    {
        Array.Clear(_excBuffer);
        Array.Clear(_oldQlsp);
        Array.Clear(_interpQlpc);
        Array.Clear(_memSp);
        Array.Clear(_memHp);
        Array.Clear(_piGain);
        _isFirst = true;
        _seed = 1000;
        _vocM1 = 0;
        _vocM2 = 0;
        _vocMean = 0;
        _vocOffset = 0;
        IsDtx = false;
    }

    // Submode 0: no transmission. The excitation is noise at the level of the last frame's, shaped by the last frame's filter with its bandwidth widened (comfort noise).
    private void DecodeNull(Span<short> output)
    {
        Span<short> lpc = stackalloc short[Order];
        Filters.BandwidthLpc(30474, _interpQlpc, lpc, Order);
        Span<short> excitation = _excBuffer.AsSpan(ExcBase, FrameSize);
        int gain = Filters.Rms16(excitation, FrameSize);
        for (int i = 0; i < FrameSize; i++)
        {
            excitation[i] = (short)Rand(gain, ref _seed);
        }

        _isFirst = true;
        Filters.IirMem16(excitation, lpc, output, FrameSize, Order, _memSp);
    }

    // pitch_unquant_3tap: the pitch lag and the three taps of the pitch predictor, and the excitation they make of the past excitation (into excitation32, Q13 times the samples).
    private static int PitchThreeTap(short[] buffer, int exc, Span<int> excitation32, int start, Submode submode, ReadOnlySpan<int> f, Span<short> gain)
    {
        int pitch = f[SpeexLayout.SubframePitch] + start;
        int index = f[SpeexLayout.SubframePitchGain];
        ReadOnlySpan<sbyte> codebook = submode.GainBits == 7 ? SpeexTables.PitchGainNarrowband : SpeexTables.PitchGainLowRate;
        gain[0] = (short)Add16(32, codebook[index * 4]);
        gain[1] = (short)Add16(32, codebook[(index * 4) + 1]);
        gain[2] = (short)Add16(32, codebook[(index * 4) + 2]);
        int g0 = Shl16(gain[0], 7);
        int g1 = Shl16(gain[1], 7);
        int g2 = Shl16(gain[2], 7);
        excitation32.Clear();
        for (int i = 0; i < 3; i++)
        {
            int g = i == 0 ? g2 : i == 1 ? g1 : g0;
            int pp = pitch + 1 - i;
            int tmp1 = SubframeSize;
            if (tmp1 > pp)
            {
                tmp1 = pp;
            }

            for (int j = 0; j < tmp1; j++)
            {
                excitation32[j] += Mult16_16(g, buffer[exc + j - pp]);
            }

            int tmp3 = SubframeSize;
            if (tmp3 > pp + pitch)
            {
                tmp3 = pp + pitch;
            }

            for (int j = tmp1; j < tmp3; j++)
            {
                excitation32[j] += Mult16_16(g, buffer[exc + j - pp - pitch]);
            }
        }

        return pitch;
    }

    // forced_pitch_unquant: the pitch is the open-loop one and the gain the coefficient; the excitation is the past excitation of one period, written to the buffer as well.
    private static int ForcedPitch(short[] buffer, int exc, Span<int> excitation32, int start, int pitchCoef, Span<short> gain)
    {
        if (pitchCoef > 63)
        {
            pitchCoef = 63;
        }

        for (int i = 0; i < SubframeSize; i++)
        {
            excitation32[i] = Mult16_16(buffer[exc + i - start], Shl16(pitchCoef, 7));
            buffer[exc + i] = (short)Pshr(excitation32[i], 13);
        }

        gain[0] = 0;
        gain[2] = 0;
        gain[1] = (short)pitchCoef;
        return start;
    }

    // The innovation of a subframe: the shape codebook entries named by the indexes (split_cb_shape_sign_unquant), or noise (noise_codebook_unquant) for submode 1.
    private void Innovation(Span<int> innovation, int submodeId, int subvectors, ReadOnlySpan<sbyte> codebook, int size, ReadOnlySpan<int> indexes)
    {
        if (submodeId == 1)
        {
            for (int i = 0; i < SubframeSize; i++)
            {
                innovation[i] = Shl32(Rand(1, ref _seed), SigShift);
            }

            return;
        }

        for (int i = 0; i < subvectors; i++)
        {
            for (int j = 0; j < size; j++)
            {
                innovation[(size * i) + j] = Shl32(codebook[(indexes[i] * size) + j], SigShift - 5);
            }
        }
    }

    private static ReadOnlySpan<sbyte> SubframeShapeCodebook(int submode) => submode switch
    {
        2 => SpeexTables.Excitation10x16,
        3 => SpeexTables.Excitation10x32,
        4 => SpeexTables.Excitation8x128,
        5 or 7 => SpeexTables.Excitation5x64,
        6 => SpeexTables.Excitation5x256,
        8 => SpeexTables.Excitation20x32,
        _ => default,
    };

    private static int SubframeShapeSize(int submode) => submode switch
    {
        2 or 3 => 10,
        4 => 8,
        8 => 20,
        _ => 5,
    };

    // The vocoder mode (submode 1): the excitation is a pulse train at the open-loop pitch, mixed with the noise innovation.
    private void Vocoder(short[] buffer, int exc, ReadOnlySpan<int> innovation, int olPitch, int olPitchCoef, int olGain)
    {
        int g = Mult16_16_P14(24576, olPitchCoef - 13);
        if (g < 0)
        {
            g = 0;
        }

        if (g > GainScaling)
        {
            g = GainScaling;
        }

        buffer.AsSpan(exc, SubframeSize).Clear();
        while (_vocOffset < SubframeSize)
        {
            if (_vocOffset >= 0)
            {
                buffer[exc + _vocOffset] = (short)Mult16_16(Sqrt(Mult16_16_16(2, olPitch)), (short)Pshr(Mult16_16(g, Pshr(olGain, SigShift)), 6));
            }

            _vocOffset += olPitch;
        }

        _vocOffset -= SubframeSize;
        for (int i = 0; i < SubframeSize; i++)
        {
            int exci = buffer[exc + i];
            buffer[exc + i] = (short)Add16(
                Add16(Mult16_16_Q15(22938, buffer[exc + i]), Mult16_16_Q15(9830, _vocM1)),
                Sub16(
                    Mult16_16_Q15(Q15One - Mult16_16_16(435, g), (short)Pshr(innovation[i], SigShift)),
                    Mult16_16_Q15(Mult16_16_16(77, g), (short)Pshr(_vocM2, SigShift))));
            _vocM1 = (short)exci;
            _vocM2 = innovation[i];
            _vocMean = (short)Pshr(Mult16_16(26214, _vocMean) + Mult16_16(6554, buffer[exc + i]), 15);
            buffer[exc + i] = (short)(buffer[exc + i] - _vocMean);
        }
    }

    private const int GainShift = Fixed.GainShift;
    private const int GainScaling = Fixed.GainScaling;
    private const int SigShift = Fixed.SigShift;
    private const int LpcScaling = Fixed.LpcScaling;
    private const int Q15One = Fixed.Q15One;
}
