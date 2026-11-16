// Reading derived from libspeex 1.2.1 (sb_celp.c: sb_decoder_init, sb_decode, sb_decode_lost; modes_wb.c: the wideband submodes), Copyright (c) 2002-2008 Jean-Marc Valin and
// Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using static Encspect.Formats.Speex.Decoding.Fixed;

namespace Encspect.Formats.Speex.Decoding;

// A wideband or ultra-wideband layer with the band below it: the sub-band CELP decoder of libspeex's fixed-point build (sb_decode). The low band is the narrowband decoder for the
// wideband mode and the wideband decoder for the ultra-wideband mode; the layer adds the upper half of the band, which the quadrature mirror filter then joins to the lower.
internal sealed class Subband
{
    private const int LpcSize = 8;
    private const int Subframes = 4;
    private const int LspMargin = 410;
    private const int QmfOrder = 64;
    private const int LowInnovation = -1;

    private static readonly short[] GainBound = [125, 164, 215, 282, 370, 484, 635, 832, 1090, 1428, 1871, 2452, 3213, 4210, 5516, 7228];

    private readonly Narrowband? _narrowband;
    private readonly Subband? _low;
    private readonly int _frameSize;
    private readonly int _subframeSize;
    private readonly int _foldingGain;
    private readonly int _layerOffset;
    private readonly short[] _g0Memory = new short[QmfOrder];
    private readonly short[] _g1Memory = new short[QmfOrder];
    private readonly short[] _excBuffer;
    private readonly short[] _oldQlsp = new short[LpcSize];
    private readonly short[] _interpQlpc = new short[LpcSize];
    private readonly int[] _memSp = new int[2 * LpcSize];
    private readonly int[] _piGain = new int[Subframes];
    private readonly short[] _excRms = new short[Subframes];
    private readonly int[] _lowPiGain = new int[Subframes];
    private readonly int[] _lowExcRms = new int[Subframes];
    private readonly short[] _qmfWork;
    private readonly int[] _lpcWork = new int[2 * ((LpcSize / 2) + 1) * (LpcSize + 3)];
    private bool _isFirst = true;
    private uint _seed = 1000;
    private int _lastEnergy;

    // The wideband decoder (low: the narrowband one) when isUltra is false, the ultra-wideband decoder (low: the wideband one) when it is true.
    public Subband(bool isUltra)
    {
        _frameSize = isUltra ? 320 : 160;
        _subframeSize = isUltra ? 80 : 40;
        _foldingGain = isUltra ? 22938 : 29491;
        _layerOffset = isUltra ? SpeexLayout.UltraWideband : SpeexLayout.Wideband;
        if (isUltra)
        {
            _low = new Subband(false);
        }
        else
        {
            _narrowband = new Narrowband(true);
        }

        _excBuffer = new short[_subframeSize];
        _qmfWork = new short[2 * ((QmfOrder / 2) + (_frameSize))];
    }

    public bool IsDtx => _narrowband?.IsDtx ?? _low!.IsDtx;

    public ReadOnlySpan<int> PiGain => _piGain;

    public int ExcitationRms(int subframe) => _excRms[subframe];

    // Decodes one frame into output, 2 * the band's frame size samples (the lower half is the low band's, the upper half the layer's before the filter joins them);
    // innovationSave is the span the layer above reads its low band's innovation from, empty at the top.
    public void Decode(ReadOnlySpan<int> frame, Span<short> output, Span<short> innovationSave)
    {
        Span<short> lowInnovation = output.Slice(_frameSize);
        if (_narrowband is not null)
        {
            _narrowband.Decode(frame.Slice(SpeexLayout.Narrowband), frame[SpeexLayout.NarrowbandSubmode], output, lowInnovation);
        }
        else
        {
            _low!.Decode(frame, output, lowInnovation);
        }

        bool isDtx = IsLowDtx;
        int layer = frame[_layerOffset == SpeexLayout.Wideband ? SpeexLayout.WidebandSubmode : SpeexLayout.UltraWidebandSubmode];
        int submode = layer == FrameLayerAbsent ? 0 : layer;
        Span<short> high = output.Slice(_frameSize, _frameSize);
        if (submode == 0)
        {
            if (isDtx)
            {
                DecodeDtx(output);
                return;
            }

            high.Clear();
            _isFirst = true;
            Filters.IirMem16(high, _interpQlpc, _frameSize, LpcSize, _memSp);
            Filters.QmfSynth(output.Slice(0, _frameSize), high, Filters.Qmf, output, 2 * _frameSize, QmfOrder, _g0Memory, _g1Memory, _qmfWork);
            return;
        }

        ReadOnlySpan<int> d = frame.Slice(_layerOffset);
        for (int sub = 0; sub < Subframes; sub++)
        {
            _lowPiGain[sub] = _narrowband is not null ? _narrowband.PiGain[sub] : _low!.PiGain[sub];
            _lowExcRms[sub] = _narrowband is not null ? _narrowband.ExcitationRms(sub) : _low!.ExcitationRms(sub);
        }

        Span<short> qlsp = stackalloc short[LpcSize];
        Span<short> interpQlsp = stackalloc short[LpcSize];
        Span<short> ak = stackalloc short[LpcSize];
        Lsp.UnquantHigh(d.Slice(SpeexLayout.HighLsp, 2), qlsp, LpcSize);
        if (_isFirst)
        {
            qlsp.CopyTo(_oldQlsp);
        }

        Span<int> excitation = stackalloc int[_subframeSize];
        Span<int> second = stackalloc int[_subframeSize];
        int energySum = 0;
        for (int sub = 0; sub < Subframes; sub++)
        {
            int offset = _subframeSize * sub;
            Span<short> sp = high.Slice(offset, _subframeSize);
            ReadOnlySpan<int> f = d.Slice(SpeexLayout.HighSubframes + (sub * SpeexLayout.HighSubframe), SpeexLayout.HighSubframe);
            if (!innovationSave.IsEmpty)
            {
                innovationSave.Slice(2 * offset, 2 * _subframeSize).Clear();
            }

            Lsp.Interpolate(_oldQlsp, qlsp, interpQlsp, LpcSize, sub, Subframes, LspMargin);
            Lsp.ToLpc(interpQlsp, ak, LpcSize, _lpcWork);
            int rh = LpcScaling;
            int pi = LpcScaling;
            for (int i = 0; i < LpcSize; i += 2)
            {
                rh += ak[i + 1] - ak[i];
                pi += ak[i] + ak[i + 1];
            }

            _piGain[sub] = pi;
            int rl = _lowPiGain[sub];
            int filterRatio = (short)Saturate(Pdiv32(Shl32(rl + 82, 7), 82 + rh), 32767);
            excitation.Clear();
            if (submode == 1)
            {
                int quant = f[0];
                int g = Exp(Mult16_16(256, quant - 10));
                g = Pdiv32(g, filterRatio);
                for (int i = 0; i < _subframeSize; i += 2)
                {
                    excitation[i] = Shl32(Mult16_32_P15(Mult16_16_Q15(_foldingGain, lowInnovation[offset + i]), Shl32(g, 6)), SigShift);
                    excitation[i + 1] = -Shl32(Mult16_32_P15(Mult16_16_Q15(_foldingGain, lowInnovation[offset + i + 1]), Shl32(g, 6)), SigShift);
                }
            }
            else
            {
                int gc = Mult16_16_Q15(28626, GainBound[f[0]]);
                if (_subframeSize == 80)
                {
                    gc = Mult16_16_P14(23170, gc);
                }

                int el = _lowExcRms[sub];
                int scale = Shl32(Pdiv32(Shl32(Mult16_16(gc, el), 3), filterRatio), SigShift - 3);
                Innovation(excitation, submode, f.Slice(SpeexLayout.HighShapes, 5));
                Filters.SignalMultiply(excitation, excitation, scale, _subframeSize);
                if (submode == 4)
                {
                    second.Clear();
                    Innovation(second, submode, f.Slice(SpeexLayout.HighSecondShapes, 5));
                    Filters.SignalMultiply(second, second, Mult16_32_P15(13107, scale), _subframeSize);
                    for (int i = 0; i < _subframeSize; i++)
                    {
                        excitation[i] += second[i];
                    }
                }
            }

            if (!innovationSave.IsEmpty)
            {
                for (int i = 0; i < _subframeSize; i++)
                {
                    innovationSave[(2 * offset) + (2 * i)] = (short)Pshr(excitation[i], SigShift);
                }
            }

            Filters.IirMem16(_excBuffer, _interpQlpc, sp, _subframeSize, LpcSize, _memSp);
            for (int i = 0; i < _subframeSize; i++)
            {
                _excBuffer[i] = (short)Pshr(excitation[i], SigShift);
            }

            ak.CopyTo(_interpQlpc);
            _excRms[sub] = (short)Filters.Rms16(_excBuffer, _subframeSize);
            energySum += Divide(Mult16_16(_excRms[sub], _excRms[sub]), Subframes);
        }

        _lastEnergy = Sqrt(energySum);
        Filters.QmfSynth(output.Slice(0, _frameSize), high, Filters.Qmf, output, 2 * _frameSize, QmfOrder, _g0Memory, _g1Memory, _qmfWork);
        qlsp.CopyTo(_oldQlsp);
        _isFirst = false;
    }

    // Starts over as at the beginning of the stream, as after a gap.
    public void Reset()
    {
        _narrowband?.Reset();
        _low?.Reset();
        Array.Clear(_g0Memory);
        Array.Clear(_g1Memory);
        Array.Clear(_excBuffer);
        Array.Clear(_oldQlsp);
        Array.Clear(_interpQlpc);
        Array.Clear(_memSp);
        Array.Clear(_piGain);
        Array.Clear(_excRms);
        _isFirst = true;
        _seed = 1000;
        _lastEnergy = 0;
    }

    private const int FrameLayerAbsent = 15;
    private const int SigShift = Fixed.SigShift;
    private const int LpcScaling = Fixed.LpcScaling;

    private bool IsLowDtx => _narrowband?.IsDtx ?? _low!.IsDtx;

    // sb_decode_lost with its discontinuous transmission flag: the high band is noise at the last frame's level through the last frame's filter.
    private void DecodeDtx(Span<short> output)
    {
        _isFirst = true;
        Span<short> high = output.Slice(_frameSize, _frameSize);
        for (int i = 0; i < _frameSize; i++)
        {
            high[i] = (short)Rand(_lastEnergy, ref _seed);
        }

        Filters.IirMem16(high, _interpQlpc, _frameSize, LpcSize, _memSp);
        Filters.QmfSynth(output.Slice(0, _frameSize), high, Filters.Qmf, output, 2 * _frameSize, QmfOrder, _g0Memory, _g1Memory, _qmfWork);
    }

    // split_cb_shape_sign_unquant for the wideband layer's codebooks: the entries named by the indexes, negated where the sign bit is set.
    private static void Innovation(Span<int> excitation, int submode, ReadOnlySpan<int> indexes)
    {
        ReadOnlySpan<sbyte> codebook = submode == 2 ? SpeexTables.HighExcitation10x32 : SpeexTables.HighExcitation8x128;
        int size = submode == 2 ? 10 : 8;
        int subvectors = submode == 2 ? 4 : 5;
        for (int i = 0; i < subvectors; i++)
        {
            int index = indexes[i] & 0xFF;
            bool isNegative = (indexes[i] >> SpeexLayout.SignBit) != 0;
            for (int j = 0; j < size; j++)
            {
                int value = Shl32(codebook[(index * size) + j], SigShift - 5);
                excitation[(size * i) + j] = isNegative ? -value : value;
            }
        }
    }
}
