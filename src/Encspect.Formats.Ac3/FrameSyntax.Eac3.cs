using Encspect.Core;

namespace Encspect.Formats.Ac3;

// What only E-AC-3's audio blocks have beyond spectral extension: enhanced coupling's strategy and parameters (A/52 Annex E 2.2.4, 2.3.3.14 to 2.3.3.26, 3.5) and the adaptive hybrid
// transform's mantissas (2.3.3.31 to 2.3.3.40, 3.4).
internal sealed partial class FrameSyntax
{
    // Enhanced coupling strategy, from ecplbegf on (Annex E 2.2.4). The channels in coupling are read already. Where the range is empty the rest of the frame is unreadable.
    private bool EnhancedCouplingStrategy(int coupled)
    {
        _ecplbegf = F("Enhanced Coupling Begin Code", 4, Explain.Ecplbegf, v => $"{v} (from sub-band {EcplBegin(v)}, bin {Tables.EnhancedCouplingSubBandStart[EcplBegin(v)]})", Topics.EnhancedCoupling);
        _ecplBegin = EcplBegin(_ecplbegf);
        if (!_spxinu)
        {
            int endCode = F("Enhanced Coupling End Code", 4, Explain.Ecplendf, v => $"{v} (to sub-band {v + 7}, bin {Tables.EnhancedCouplingSubBandStart[v + 7] - 1})", Topics.EnhancedCoupling);
            _ecplEnd = endCode + 7;
        }
        else
        {
            _ecplEnd = _spxbegf < 6 ? _spxbegf + 5 : _spxbegf * 2;
        }

        if (_ecplBegin >= _ecplEnd)
        {
            Invalid("the enhanced coupling range is empty: its begin sub-band is not below its end sub-band (Annex E 2.3.3.16, 2.3.3.17)");
            return false;
        }

        // The sub-bands up to the begin sub-band or the eighth, whichever is higher, are never joined to the band before (2.3.3.19) and send no bit.
        int first = Math.Max(9, _ecplBegin + 1);
        bool explicitStructure = Flag("HasEnhancedCouplingBandStructure", Explain.Ecplbndstrce, Topics.EnhancedCoupling);
        if (explicitStructure)
        {
            Array.Clear(_ecplbndstrc);
            for (int sub = first; sub < _ecplEnd; sub++)
            {
                _ecplbndstrc[sub] = F("Enhanced Coupling Band Structure", 1, Explain.Ecplbndstrc, topic: Topics.EnhancedCoupling);
            }
        }
        else if (!_ecplUsedInFrame)
        {
            Array.Copy(Tables.DefaultEnhancedCouplingBandStructure, _ecplbndstrc, _ecplbndstrc.Length);
            for (int sub = 0; sub < first; sub++)
            {
                _ecplbndstrc[sub] = 0;
            }
        }

        _necplbnd = 0;
        for (int sub = _ecplBegin; sub < _ecplEnd; sub++)
        {
            _necplbnd += sub == _ecplBegin || _ecplbndstrc[sub] == 0 ? 1 : 0;
        }

        // The ranges of the checks that standard coupling has: the same bins as the sub-band range stands for.
        _cplbegf = _ecplBegin;
        _cplendf = _ecplEnd;
        if (coupled < 2)
        {
            Inconsistent("coupling is in use with fewer than two channels in it (check 2)");
        }

        if (_acmod < 2)
        {
            Inconsistent("coupling is in use in a mono or dual mono stream (check 22)");
        }

        return true;
    }

    // Sub-band of enhanced coupling's begin code (2.3.3.16).
    private static int EcplBegin(int code) => code < 3 ? code * 2 : code < 13 ? code + 2 : (code * 2) - 10;

    // Enhanced coupling parameters: the amplitudes, angles and chaos values of the coupled channels and their transient flags (Annex E 2.2.4; Table E1.4).
    private void EnhancedCouplingParameters()
    {
        _ecplangleintrp = Flag("HasAngleInterpolation", Explain.Ecplangleintrp, Topics.EnhancedCoupling);
        int firstChannel = -1;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (!_chincpl[ch])
            {
                _firstCplCoords[ch] = true;
                continue;
            }

            if (firstChannel < 0)
            {
                firstChannel = ch;
            }

            bool amplitudes;
            bool angles;
            if (_firstCplCoords[ch])
            {
                amplitudes = true;
                angles = ch > firstChannel;
                _firstCplCoords[ch] = false;
            }
            else
            {
                amplitudes = FC("HasEnhancedCouplingAmplitudes", ch, 1, Explain.Ecplparam1e, BooleanValue, Topics.EnhancedCoupling) != 0;
                angles = ch > firstChannel && FC("HasEnhancedCouplingAngles", ch, 1, Explain.Ecplparam2e, BooleanValue, Topics.EnhancedCoupling) != 0;
            }

            if (amplitudes)
            {
                OpenChannel("Enhanced Coupling Amplitudes", ch, Explain.EnhancedCouplingParameters, Topics.EnhancedCoupling);
                for (int band = 0; band < _necplbnd; band++)
                {
                    _ecplamp[ch][band] = F("Enhanced Coupling Amplitude", 5, Explain.Ecplamp, topic: Topics.EnhancedCoupling);
                }

                Close(NodeValue.Integer(_necplbnd, "bands", "band"));
            }

            if (angles)
            {
                OpenChannel("Enhanced Coupling Angles", ch, Explain.EnhancedCouplingParameters, Topics.EnhancedCoupling);
                for (int band = 0; band < _necplbnd; band++)
                {
                    _ecplangle[ch][band] = F("Enhanced Coupling Angle", 6, Explain.Ecplangle, topic: Topics.EnhancedCoupling);
                    _ecplchaos[ch][band] = F("Enhanced Coupling Chaos", 3, Explain.Ecplchaos, topic: Topics.EnhancedCoupling);
                }

                Close(NodeValue.Integer(_necplbnd, "bands", "band"));
            }

            _ecpltrans[ch] = ch > firstChannel && FC("IsInEnhancedCouplingTransient", ch, 1, Explain.Ecpltrans, BooleanValue, Topics.EnhancedCoupling) != 0;
        }
    }

    // Whether a channel (0 to 4 the full bandwidth channels, Cpl, Lfe) codes its mantissas with the adaptive hybrid transform: its six blocks' mantissas all sit in block 0 (Annex E 2.2.4, 3.4).
    private bool IsAht(int ch) => _eac3 && _ahte && (ch == Cpl ? _cplahtinu && _cplInUse : ch == Lfe ? _lfeahtinu && _lfeon : ch < _nfchans && _chahtinu[ch]);

    private bool HasAhtInBlock0(int blk)
    {
        if (blk != 0 || !_eac3 || !_ahte)
        {
            return false;
        }

        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (_chahtinu[ch])
            {
                return true;
            }
        }

        return IsAht(Cpl) || IsAht(Lfe);
    }

    // The block's record of an E-AC-3 frame for the decoder, in integers (the differences from FrameSyntax.WriteBlock's layout for AC-3):
    //   the frame's first block: acmod, lfeon, the number of blocks, and for every full bandwidth channel its spectral extension attenuation code plus 1 (0 for none);
    //   every block: the block switching flags and the dither flags;
    //   spxinu and with it the channels in spectral extension (a bit each), spxstrtf, the begin and end sub-bands, the band structure (a bit per sub-band, set where joined to the band
    //   below) and for each channel in spectral extension its blend, master coordinate and per band exp | mant << 4;
    //   the coupling mode (0 none, 1 standard, 2 enhanced); standard as AC-3's; enhanced: the begin and end sub-bands, the channels in coupling, ecplangleintrp, the band structure
    //   (a bit per sub-band) and for each coupled channel per band ecplamp | angle << 5 | chaos << 11, and its transient flag;
    //   with acmod 2 the number of rematrixing bands and their flags;
    //   then for every full bandwidth channel in order, the coupling channel after the first coupled one and the LFE channel: its kind (0 coded in this block, 1 adaptive hybrid transform
    //   with the mantissas of the frame's six blocks, 2 adaptive hybrid transform with the mantissas of block 0), the number of its bins and, for kind 0, per bin exp | bap << 5 | code << 9
    //   as AC-3's, and for kind 1 per bin exp | hebap << 5 | gain << 10 (0, 1 and 2 for the gains 1, 2 and 4) followed by the vector's index (hebap 1 to 7) or by six values
    //   code << 1 | isLarge (hebap 8 to 19).
    private int WriteBlockEac3(int start, int blk)
    {
        IntWriter w = _lent ?? throw new InvalidOperationException("The block record needs a writer");
        if (blk == 0)
        {
            for (int ch = 0; ch < _nfchans; ch++)
            {
                w.Add(_spxattencod[ch] + 1);
            }
        }

        w.Add(_blkswMask);
        w.Add(_dithMask);
        w.Add(_spxinu ? 1 : 0);
        if (_spxinu)
        {
            int channels = 0;
            for (int ch = 0; ch < _nfchans; ch++)
            {
                channels |= _chinspx[ch] ? 1 << ch : 0;
            }

            int joined = 0;
            for (int sub = _spxBeginBand + 1; sub < _spxEndBand; sub++)
            {
                joined |= _spxbndstrc[sub] << sub;
            }

            w.Add(channels);
            w.Add(_spxstrtf);
            w.Add(_spxBeginBand);
            w.Add(_spxEndBand);
            w.Add(joined);
            int bands = _spxEndBand - _spxBeginBand;
            for (int sub = _spxBeginBand + 1; sub < _spxEndBand; sub++)
            {
                bands -= _spxbndstrc[sub];
            }

            for (int ch = 0; ch < _nfchans; ch++)
            {
                if (_chinspx[ch])
                {
                    w.Add(_spxblnd[ch]);
                    w.Add(_mstrspxco[ch]);
                    for (int band = 0; band < bands; band++)
                    {
                        w.Add(_spxco[ch][band]);
                    }
                }
            }
        }

        int chincpl = 0;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            chincpl |= _chincpl[ch] && _cplInUse ? 1 << ch : 0;
        }

        w.Add(!_cplInUse ? 0 : _ecplinu ? 2 : 1);
        if (_cplInUse && !_ecplinu)
        {
            int combined = 0;
            for (int sub = 1; sub < _ncplsubnd; sub++)
            {
                combined |= _cplbndstrcAbsolute[_cplbegf + sub] << sub;
            }

            w.Add(_cplbegf);
            w.Add(_cplendf);
            w.Add(chincpl);
            w.Add(combined);
            w.Add(_phsflginu ? _phsflg : 0);
            for (int ch = 0; ch < _nfchans; ch++)
            {
                if (_chincpl[ch])
                {
                    for (int band = 0; band < _ncplbnd; band++)
                    {
                        w.Add(_cplco[ch][band]);
                    }
                }
            }
        }
        else if (_cplInUse)
        {
            int joined = 0;
            for (int sub = _ecplBegin + 1; sub < _ecplEnd; sub++)
            {
                joined |= _ecplbndstrc[sub] << sub;
            }

            w.Add(_ecplBegin);
            w.Add(_ecplEnd);
            w.Add(chincpl);
            w.Add(_ecplangleintrp ? 1 : 0);
            w.Add(joined);
            for (int ch = 0; ch < _nfchans; ch++)
            {
                if (_chincpl[ch])
                {
                    for (int band = 0; band < _necplbnd; band++)
                    {
                        w.Add(_ecplamp[ch][band] | (_ecplangle[ch][band] << 5) | (_ecplchaos[ch][band] << 11));
                    }

                    w.Add(_ecpltrans[ch] ? 1 : 0);
                }
            }
        }

        if (_acmod == 2)
        {
            w.Add(_nrematbd);
            w.Add(_rematflg);
        }

        _mantissaBit = start;
        _groupLeft1 = _groupLeft2 = _groupLeft4 = 0;
        _groupFaults = 0;
        bool gotCoupling = false;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            ReadEac3Channel(ch, 0, _endMant[ch], blk, w);
            if (_cplInUse && _chincpl[ch] && !gotCoupling)
            {
                ReadEac3Channel(Cpl, _startMant[Cpl], _endMant[Cpl], blk, w);
                gotCoupling = true;
            }
        }

        if (_lfeon)
        {
            ReadEac3Channel(Lfe, 0, 7, blk, w);
        }

        ReportMantissaFaults(start);
        return _mantissaBit - start;
    }

    private void ReadEac3Channel(int ch, int start, int end, int blk, IntWriter w)
    {
        int count = Math.Max(0, end - start);
        byte[] exp = _exp[ch];
        byte[] bap = _bap[ch];
        if (IsAht(ch))
        {
            w.Add(blk == 0 ? 1 : 2);
            w.Add(count);
            if (blk == 0)
            {
                ReadAht(ch, start, end, w);
            }

            return;
        }

        w.Add(0);
        w.Add(count);
        for (int bin = start; bin < end; bin++)
        {
            int pointer = bap[bin];
            int code = pointer != 0 ? ReadCode(pointer, true) : 0;
            w.Add(exp[bin] | (pointer << 5) | (code << 9));
        }
    }

    // One channel's adaptive hybrid transform data (Annex E 2.2.4, 3.4.2, 3.4.4): the gain adaptive quantization mode and gains, then for each bin its vector's index or its six values.
    private void ReadAht(int ch, int start, int end, IntWriter w)
    {
        byte[] hebap = _bap[ch];
        byte[] exp = _exp[ch];
        int mode = PeekAht(2);
        int endbap = mode < 2 ? 12 : 17;
        int active = 0;
        for (int bin = start; bin < end; bin++)
        {
            active += hebap[bin] is > 7 && hebap[bin] < endbap ? 1 : 0;
        }

        Span<byte> gains = stackalloc byte[256];
        if (mode is 1 or 2)
        {
            for (int i = 0; i < active; i++)
            {
                gains[i] = (byte)(PeekAht(1) != 0 ? (mode == 1 ? 1 : 2) : 0);
            }
        }
        else if (mode == 3)
        {
            for (int i = 0; i < active; i += 3)
            {
                int word = PeekAht(5);
                if (word > 26)
                {
                    _groupFaults |= 32;
                    word = 26;
                }

                gains[i] = (byte)(word / 9);
                if (i + 1 < active)
                {
                    gains[i + 1] = (byte)(word % 9 / 3);
                }

                if (i + 2 < active)
                {
                    gains[i + 2] = (byte)(word % 3);
                }
            }
        }

        int gain = 0;
        for (int bin = start; bin < end; bin++)
        {
            int pointer = hebap[bin];
            bool hasGain = pointer is > 7 && pointer < endbap;
            int g = hasGain && mode != 0 ? gains[gain] : 0;
            gain += hasGain ? 1 : 0;
            w.Add(exp[bin] | (pointer << 5) | (g << 10));
            if (pointer == 0)
            {
                continue;
            }

            if (pointer < 8)
            {
                w.Add(PeekAht(Tables.VectorBits[pointer - 1]));
                continue;
            }

            int m = Tables.GainAdaptiveBits[pointer - 8];
            for (int n = 0; n < 6; n++)
            {
                if (g == 0)
                {
                    w.Add(SignExtend(PeekAht(m), m) << 1);
                    continue;
                }

                int tagBits = g == 1 ? m - 1 : m - 2;
                int tag = SignExtend(PeekAht(tagBits), tagBits);
                if (tag != -(1 << (tagBits - 1)))
                {
                    w.Add(tag << 1);
                    continue;
                }

                int largeBits = g == 1 ? m - 1 : m;
                w.Add((SignExtend(PeekAht(largeBits), largeBits) << 1) | 1);
            }
        }
    }

    private int PeekAht(int width)
    {
        int value = _b.Peek(_mantissaBit, width);
        _mantissaBit += width;
        return value;
    }

    private static int SignExtend(int value, int width) => (value << (32 - width)) >> (32 - width);
}
