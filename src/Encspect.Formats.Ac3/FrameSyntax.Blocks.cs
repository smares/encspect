using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The audio blocks (A/52 5.3.3 and 5.4.3; Annex E 2.2.4 and 2.3.3). Every field before the
// mantissas is read; the mantissas are stepped over by the length the bit allocation gives them.
// E-AC-3's spectral extension is read, as its fields only move where the channels end; the
// adaptive hybrid transform and enhanced coupling code their data in ways the analyzer does not
// step over, so reading stops at the first field that depends on them.
internal sealed partial class FrameSyntax
{
    private readonly bool[] _previousChincpl = new bool[5];
    private readonly int[] _cplbndstrcAbsolute = new int[18];
    private bool _couplingUsedInFrame;
    private bool _cplBandsChanged;
    private readonly bool[] _bandwidthChanged = new bool[Channels];
    private bool _spxUsedInFrame;
    private int _rematflg;
    private bool _allocationStarted;

    // What the block being read chose; Blocks counts it with the frame's tallies and records it for the coding series only once
    // the block is read, so that every tally is over the blocks read and equals its series.
    private bool _blockSwitched;
    private int _blockRematrixedBands;
    private int _blockRematrixingBands;
    private bool _blockRematrixed;
    private int _blockEnd;

    private void Blocks()
    {
        _cplInUse = false;
        _previousCplInUse = false;
        _previousCplBegin = -1;
        _previousCplEnd = -1;
        _couplingUsedInFrame = false;
        _spxUsedInFrame = false;
        _spxinu = false;
        _ecplinu = false;
        _nrematbd = 0;
        _rematflg = 0;
        _allocationStarted = false;
        Array.Clear(_chincpl);
        Array.Clear(_previousChincpl);
        Array.Clear(_chinspx);
        Array.Clear(_hasExponents);
        Array.Clear(_endMant);
        Array.Clear(_count1);
        Array.Clear(_count2);
        Array.Clear(_count4);
        Array.Clear(_fixedBits);
        Array.Fill(_deltbae, 2);

        // A block may keep the band structure of an earlier block of the frame; bands no block of
        // this frame set start from the defaults, not from the frame read before, so the frame's
        // nodes, built later from its record alone, read the same fields as the analysis did.
        Array.Copy(Tables.DefaultSpxBandStructure, _spxbndstrc, _spxbndstrc.Length);
        Array.Copy(Tables.DefaultCouplingBandStructure, _cplbndstrcAbsolute, _cplbndstrcAbsolute.Length);
        if (!_eac3)
        {
            Array.Fill(_firstCplCoords, true);
            _firstCplLeak = true;
        }

        for (int block = 0; block < _blocks; block++)
        {
            _t?.Open("Audio Block", _b.Position, Explain.AudioBlock, Topics.AudioBlocks, NodeKind.SideInfo);
            _blockSwitched = false;
            _blockRematrixed = false;
            _blockRematrixedBands = 0;
            _blockRematrixingBands = 0;
            _blockEnd = 255;
            bool read = Block(block);
            _t?.Close(_b.Position, NodeValue.Integer(block));
            if (!read)
            {
                return;
            }

            CommitBlock();
            _r.BlocksRead++;
        }
    }

    private void CommitBlock()
    {
        int at = _r.BlocksRead;
        _r.BlockSwitchBlocks += _blockSwitched ? 1 : 0;
        _r.CouplingBlocks += _cplInUse ? 1 : 0;
        _r.SpectralExtensionBlocks += _spxinu ? 1 : 0;
        _r.RematrixingBands += _blockRematrixingBands;
        _r.RematrixedBands += _blockRematrixedBands;
        _r.RematrixingBlocks += _blockRematrixed ? 1 : 0;
        if (at < _r.BlockFlags.Length)
        {
            _r.BlockFlags[at] = (byte)((_blockSwitched ? FrameResult.BlockSwitched : 0) | (_cplInUse ? FrameResult.BlockCoupled : 0)
                | (_spxinu ? FrameResult.BlockSpectralExtension : 0) | (_blockRematrixed ? FrameResult.BlockRematrixed : 0));
            _r.BlockEnds[at] = (byte)_blockEnd;
        }
    }

    private bool Block(int blk)
    {
        // Block switch and dither flags (5.4.3.1, 5.4.3.2).
        bool switched = false;
        if (!_eac3 || _blkswe)
        {
            for (int ch = 0; ch < _nfchans; ch++)
            {
                switched |= FC("IsBlockSwitched", ch, 1, Explain.Blksw, BooleanValue, Topics.AudioBlocks) != 0;
            }
        }

        _blockSwitched = switched;
        if (!_eac3 || _dithflage)
        {
            for (int ch = 0; ch < _nfchans; ch++)
            {
                FC("IsDithered", ch, 1, Explain.Dithflag, BooleanValue);
            }
        }

        // Dynamic range (5.4.3.3 to 5.4.3.6).
        bool dynamic = false;
        for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
        {
            if (Flag(channel == 0 ? "HasDynamicRange" : "HasDynamicRange 2", Explain.Dynrnge, Topics.DialogueNormalization))
            {
                dynamic = true;
                F(channel == 0 ? "Dynamic Range Gain" : "Dynamic Range Gain 2", 8, Explain.Dynrng, Ac3Names.DynamicRange, Topics.DialogueNormalization);
            }
        }

        _r.DynamicRangeBlocks += dynamic ? 1 : 0;
        if (_eac3 && !SpectralExtension(blk))
        {
            return false;
        }

        if (!CouplingStrategy(blk) || !CouplingCoordinates(blk))
        {
            return false;
        }

        Rematrixing(blk);

        // Exponent strategies (5.4.3.21 to 5.4.3.23; in E-AC-3 from the audio frame).
        Span<int> strategy = stackalloc int[Channels];
        if (!_eac3)
        {
            if (_cplInUse)
            {
                strategy[Cpl] = F("Coupling Exponent Strategy", 2, Explain.Cplexpstr, ExponentStrategyText, Topics.AudioBlocks);
            }

            for (int ch = 0; ch < _nfchans; ch++)
            {
                strategy[ch] = FC("Exponent Strategy", ch, 2, Explain.Chexpstr, ExponentStrategyText, Topics.AudioBlocks);
            }

            if (_lfeon)
            {
                strategy[Lfe] = F("LFE Exponent Strategy", 1, Explain.Lfeexpstr, v => v == 0 ? "reuse" : "D15", Topics.AudioBlocks);
            }
        }
        else
        {
            strategy[Cpl] = _cplInUse ? _cplexpstr[blk] : 0;
            for (int ch = 0; ch < _nfchans; ch++)
            {
                strategy[ch] = _chexpstr[blk, ch];
            }

            strategy[Lfe] = _lfeon ? _lfeexpstr[blk] : 0;
        }

        bool cplChanged = _cplInUse && (!_previousCplInUse || _cplbegf != _previousCplBegin || _cplendf != _previousCplEnd);
        if (_cplInUse && strategy[Cpl] == 0 && (blk == 0 || cplChanged || !_hasExponents[Cpl]))
        {
            Inconsistent("the coupling channel reuses exponents where it has none to reuse (checks 6 and 7)");
        }

        if (_lfeon && strategy[Lfe] == 0 && !_hasExponents[Lfe])
        {
            Inconsistent("the LFE channel reuses exponents in block 0 (check 10)");
        }

        // Channel bandwidth (5.4.3.24, 7.1.3; Annex E 3.3.3).
        for (int ch = 0; ch < _nfchans; ch++)
        {
            int end;
            if (_chincpl[ch])
            {
                end = 37 + (12 * _cplbegf);
            }
            else if (_eac3 && _chinspx[ch])
            {
                end = 25 + (12 * _spxBeginBand);
            }
            else if (strategy[ch] != 0)
            {
                int code = FC("Bandwidth Code", ch, 6, Explain.Chbwcod, v => v <= 60 ? $"{v} (to bin {((v + 12) * 3) + 37})" : $"{v} (invalid)");
                if (code > 60)
                {
                    Invalid("chbwcod is above 60 (check 11)");
                    return false;
                }

                end = ((code + 12) * 3) + 37;
            }
            else
            {
                end = _endMant[ch];
            }

            if (strategy[ch] == 0 && (!_hasExponents[ch] || end != _endMant[ch]))
            {
                Inconsistent(_hasExponents[ch]
                    ? "a channel reuses exponents although its bandwidth changed (check 9)"
                    : "a channel reuses exponents in block 0 (check 8)");
            }

            _bandwidthChanged[ch] = end != _endMant[ch];
            if (end != _endMant[ch])
            {
                _realloc[ch] = true;
            }

            _endMant[ch] = end;
        }

        if (_cplInUse)
        {
            _startMant[Cpl] = 37 + (12 * _cplbegf);
            _endMant[Cpl] = 37 + (12 * (_cplendf + 3));
        }

        _startMant[Lfe] = 0;
        _endMant[Lfe] = 7;

        // The highest bin any full bandwidth channel or the coupling channel codes; the LFE channel is left out.
        int top = 0;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            top = Math.Max(top, _endMant[ch]);
        }

        _blockEnd = Math.Min(254, Math.Max(top, _cplInUse ? _endMant[Cpl] : 0));

        // Exponents (5.4.3.25 to 5.4.3.29, 7.1.3).
        if (_cplInUse && strategy[Cpl] != 0 && !Exponents(Cpl, strategy[Cpl], "Coupling Exponents", Explain.CouplingExponents))
        {
            return false;
        }

        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (strategy[ch] != 0 && !Exponents(ch, strategy[ch], "Exponents", Explain.Exponents))
            {
                return false;
            }
        }

        if (_lfeon && strategy[Lfe] != 0 && !Exponents(Lfe, 1, "LFE Exponents", Explain.LfeExponents))
        {
            return false;
        }

        for (int ch = 0; ch < Channels; ch++)
        {
            bool used = ch < _nfchans || (ch == Cpl && _cplInUse) || (ch == Lfe && _lfeon);
            if (used)
            {
                _r.ExponentStrategies[strategy[ch] == 0 ? 3 : strategy[ch] - 1]++;
            }
        }

        if (!BitAllocationParameters(blk))
        {
            return false;
        }

        // Skip field (5.4.3.58 to 5.4.3.60).
        if ((!_eac3 || _skipflde) && Flag("HasSkipField", Explain.Skiple))
        {
            int length = F("Skip Length", 9, Explain.Skipl, v => NodeValue.Integer(v, "bytes"));
            int start = _b.Position;
            _b.Skip(length * 8);
            int end = _b.Position;
            _t?.Open("Skip Field", start, Explain.Skipfld);
            EmdfRegion(start, end);
            _b.MoveTo(end);
            _t?.Close(end, NodeValue.Integer(length, "bytes"));
            _r.SkipBytes += length;
        }

        if (_eac3 && _ahte && (_chahtinu.AsSpan(0, _nfchans).Contains(true) || (_cplahtinu && _cplInUse) || (_lfeahtinu && _lfeon)))
        {
            Stop(StopReason.Aht, Issue.Aht);
            return false;
        }

        return Mantissas();
    }

    // Spectral extension strategy and coordinates (Annex E 2.2.4, 2.3.3.1 to 2.3.3.13).
    private bool SpectralExtension(int blk)
    {
        bool strategy = blk == 0 || Flag("HasSpectralExtensionStrategy", Explain.Spxstre, Topics.SpectralExtension);
        if (strategy)
        {
            _spxinu = Flag("IsSpectralExtensionInUse", Explain.Spxinu, Topics.SpectralExtension);
            if (_spxinu)
            {
                for (int ch = 0; ch < _nfchans; ch++)
                {
                    _chinspx[ch] = _acmod == 1 || FC("IsInSpectralExtension", ch, 1, Explain.Chinspx, BooleanValue, Topics.SpectralExtension) != 0;
                }

                int copyStart = F("Spectral Extension Copy Start Code", 2, Explain.Spxstrtf, v => $"{v} (from bin {(v * 12) + 25})", Topics.SpectralExtension);
                _spxbegf = F("Spectral Extension Begin Code", 3, Explain.Spxbegf, v => $"{v} (sub-band {(v < 6 ? v + 2 : (v * 2) - 3)})", Topics.SpectralExtension);
                int endCode = F("Spectral Extension End Code", 3, Explain.Spxendf, v => $"{v} (to sub-band {(v < 3 ? v + 5 : (v * 2) + 3)})", Topics.SpectralExtension);
                _spxBeginBand = _spxbegf < 6 ? _spxbegf + 2 : (_spxbegf * 2) - 3;
                _spxEndBand = endCode < 3 ? endCode + 5 : (endCode * 2) + 3;
                if (_spxBeginBand >= _spxEndBand || (copyStart * 12) + 25 >= (_spxBeginBand * 12) + 25)
                {
                    Invalid("the spectral extension range is empty, or its copy region does not start below it, so there is nothing to copy up (Annex E 3.6.4.1)");
                    return false;
                }

                if (Flag("HasSpectralExtensionBandStructure", Explain.Spxbndstrce, Topics.SpectralExtension))
                {
                    int width = _spxEndBand - _spxBeginBand - 1;
                    int bits = F("Spectral Extension Band Structure", width, Explain.Spxbndstrc, v => BandStructureText(v, width), Topics.SpectralExtension);
                    for (int i = 0; i < width; i++)
                    {
                        _spxbndstrc[_spxBeginBand + 1 + i] = (bits >> (width - 1 - i)) & 1;
                    }
                }
                else if (!_spxUsedInFrame)
                {
                    Array.Copy(Tables.DefaultSpxBandStructure, _spxbndstrc, _spxbndstrc.Length);
                }

                _spxUsedInFrame = true;
            }
            else
            {
                Array.Clear(_chinspx);
                Array.Fill(_firstSpxCoords, true);
            }
        }

        if (!_spxinu)
        {
            return true;
        }

        int bands = _spxEndBand - _spxBeginBand;
        for (int band = _spxBeginBand + 1; band < _spxEndBand; band++)
        {
            bands -= _spxbndstrc[band];
        }

        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (!_chinspx[ch])
            {
                _firstSpxCoords[ch] = true;
                continue;
            }

            bool coordinates;
            if (_firstSpxCoords[ch])
            {
                coordinates = true;
                _firstSpxCoords[ch] = false;
            }
            else
            {
                coordinates = FC("HasSpectralExtensionCoordinates", ch, 1, Explain.Spxcoe, BooleanValue, Topics.SpectralExtension) != 0;
            }

            if (coordinates)
            {
                OpenChannel("Spectral Extension Coordinates", ch, Explain.SpxCoordinates, Topics.SpectralExtension);
                F("Spectral Extension Blend", 5, Explain.Spxblnd);
                F("Master Spectral Extension Coordinate", 2, Explain.Mstrspxco);
                for (int band = 0; band < bands; band++)
                {
                    F("Spectral Extension Coordinate Exponent", 4, Explain.Spxcoexp);
                    F("Spectral Extension Coordinate Mantissa", 2, Explain.Spxcomant);
                }

                Close(NodeValue.Integer(bands, "bands", "band"));
            }
        }

        return true;
    }

    // Coupling strategy (5.4.3.7 to 5.4.3.13; Annex E 2.2.4).
    private bool CouplingStrategy(int blk)
    {
        _previousCplInUse = _cplInUse;
        _previousCplBegin = _cplInUse ? _cplbegf : -1;
        _previousCplEnd = _cplInUse ? _cplendf : -1;
        Array.Copy(_chincpl, _previousChincpl, 5);
        _cplBandsChanged = false;
        bool strategy = _eac3 ? _cplstre[blk] : Flag("HasCouplingStrategy", Explain.Cplstre, Topics.Coupling);
        if (!_eac3 && blk == 0 && !strategy)
        {
            Inconsistent("cplstre is 0 in block 0 (check 1)");
        }

        if (!strategy)
        {
            return true;
        }

        bool inUse = _eac3 ? _cplinu[blk] : Flag("IsCouplingInUse", Explain.Cplinu, Topics.Coupling);
        if (!inUse)
        {
            _cplInUse = false;
            Array.Clear(_chincpl);
            Array.Fill(_firstCplCoords, true);
            _firstCplLeak = true;
            _phsflginu = false;
            _ecplinu = false;
            return true;
        }

        if (_eac3)
        {
            _ecplinu = Flag("IsEnhancedCouplingInUse", Explain.Ecplinu, Topics.Coupling);
        }

        int coupled = 0;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            _chincpl[ch] = (_eac3 && _acmod == 2) || FC("IsInCoupling", ch, 1, Explain.Chincpl, BooleanValue, Topics.Coupling) != 0;
            coupled += _chincpl[ch] ? 1 : 0;
        }

        if (_ecplinu)
        {
            Stop(StopReason.EnhancedCoupling, Issue.EnhancedCoupling);
            return false;
        }

        if (_acmod == 2)
        {
            _phsflginu = Flag("HasPhaseFlags", Explain.Phsflginu, Topics.Coupling);
        }

        _cplbegf = F("Coupling Begin Code", 4, Explain.Cplbegf, v => $"{v} (from bin {37 + (12 * v)})", Topics.Coupling);
        if (!_eac3 || !_spxinu)
        {
            _cplendf = F("Coupling End Code", 4, Explain.Cplendf, v => $"{v} (to bin {37 + (12 * (v + 3))})", Topics.Coupling);
        }
        else
        {
            _cplendf = _spxbegf < 6 ? _spxbegf - 2 : (_spxbegf * 2) - 7;
        }

        _ncplsubnd = 3 + _cplendf - _cplbegf;
        if (_ncplsubnd < 1 || _cplbegf + _ncplsubnd > 18)
        {
            Invalid("the coupling range is empty: cplbegf lies above cplendf + 2 (check 3)");
            return false;
        }

        int width = _ncplsubnd - 1;
        bool explicitStructure = !_eac3 || Flag("HasCouplingBandStructure", Explain.Cplbndstrce, Topics.Coupling);
        if (explicitStructure && width > 0)
        {
            int bits = F("Coupling Band Structure", width, Explain.Cplbndstrc, v => BandStructureText(v, width), Topics.Coupling);
            for (int i = 0; i < width; i++)
            {
                int flag = (bits >> (width - 1 - i)) & 1;
                _cplBandsChanged |= _cplbndstrcAbsolute[_cplbegf + 1 + i] != flag;
                _cplbndstrcAbsolute[_cplbegf + 1 + i] = flag;
            }
        }
        else if (!explicitStructure && !_couplingUsedInFrame)
        {
            for (int i = 0; i < _cplbndstrcAbsolute.Length; i++)
            {
                _cplBandsChanged |= _cplbndstrcAbsolute[i] != Tables.DefaultCouplingBandStructure[i];
            }

            Array.Copy(Tables.DefaultCouplingBandStructure, _cplbndstrcAbsolute, _cplbndstrcAbsolute.Length);
        }

        _ncplbnd = _ncplsubnd;
        for (int i = 1; i < _ncplsubnd; i++)
        {
            _ncplbnd -= _cplbndstrcAbsolute[_cplbegf + i];
        }

        if (coupled < 2)
        {
            Inconsistent("coupling is in use with fewer than two channels in it (check 2)");
        }

        if (_acmod < 2)
        {
            Inconsistent("coupling is in use in a mono or dual mono stream (check 22)");
        }

        _cplInUse = true;
        _couplingUsedInFrame = true;
        _realloc[Cpl] = true;
        return true;
    }

    // Coupling coordinates and phase flags (5.4.3.14 to 5.4.3.18).
    private bool CouplingCoordinates(int blk)
    {
        if (!_cplInUse)
        {
            return true;
        }

        bool changed = !_previousCplInUse || _cplbegf != _previousCplBegin || _cplendf != _previousCplEnd || _cplBandsChanged;
        bool any = false;
        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (!_chincpl[ch])
            {
                _firstCplCoords[ch] = true;
                continue;
            }

            bool exist;
            if (_eac3 && _firstCplCoords[ch])
            {
                exist = true;
            }
            else
            {
                exist = FC("HasCouplingCoordinates", ch, 1, Explain.Cplcoe, BooleanValue, Topics.Coupling) != 0;
                if (!_eac3 && !exist && (blk == 0 || changed || !_previousChincpl[ch]))
                {
                    Inconsistent("a coupled channel reuses coupling coordinates it does not have (checks 4, 23 and 24)");
                }
            }

            _firstCplCoords[ch] = false;
            if (exist)
            {
                any |= ch < 2;
                OpenChannel("Coupling Coordinates", ch, Explain.CouplingCoordinates, Topics.Coupling);
                F("Master Coupling Coordinate", 2, Explain.Mstrcplco, v => $"{v} (x 2^-{3 * v})");
                for (int band = 0; band < _ncplbnd; band++)
                {
                    F("Coupling Coordinate Exponent", 4, Explain.Cplcoexp);
                    F("Coupling Coordinate Mantissa", 4, Explain.Cplcomant);
                }

                Close(NodeValue.Integer(_ncplbnd, "bands", "band"));
            }
        }

        if (_acmod == 2 && _phsflginu && any)
        {
            F("Phase Flags", _ncplbnd, Explain.Phsflg, v => BandStructureText(v, _ncplbnd), Topics.Coupling);
        }

        return true;
    }

    // Rematrixing (5.4.3.19, 5.4.3.20, Table 5.15; Annex E 3.3.2).
    private void Rematrixing(int blk)
    {
        if (_acmod != 2)
        {
            return;
        }

        int bands = _cplInUse ? (_cplbegf == 0 ? 2 : _cplbegf < 3 ? 3 : 4) : _spxinu ? (_spxbegf < 2 ? 3 : 4) : 4;
        bool strategy = (_eac3 && blk == 0) || Flag("HasRematrixing", Explain.Rematstr, Topics.Rematrixing);
        if (!_eac3 && blk == 0 && !strategy)
        {
            Inconsistent("rematstr is 0 in block 0 (check 5)");
        }

        if (strategy)
        {
            _rematflg = F("Rematrixing Flags", bands, Explain.Rematflg, v => RematrixingText(v, bands), Topics.Rematrixing);
        }
        else if (bands != _nrematbd)
        {
            Inconsistent("the number of rematrixing bands changed without new rematrixing flags (check 25)");
        }

        _nrematbd = bands;
        int set = System.Numerics.BitOperations.PopCount((uint)_rematflg);
        _blockRematrixingBands = bands;
        _blockRematrixedBands = set;
        _blockRematrixed = set > 0;
    }

    // An exponent set (7.1.3): the absolute exponent, the groups, and for a full bandwidth
    // channel its gain range. The groups are not kept in the frame's record; their validity is.
    private bool Exponents(int ch, int strategy, string name, string description)
    {
        OpenChannel(name, ch, description, Topics.AudioBlocks);
        int start = ch == Cpl ? _startMant[Cpl] : 0;
        int end = _endMant[ch];
        int groupSize = 1 << (strategy - 1);
        int groups = ch switch
        {
            Cpl => (end - start) / (3 * groupSize),
            Lfe => 2,
            _ => strategy switch { 1 => (end - 1) / 3, 2 => (end - 1 + 3) / 6, _ => (end - 1 + 9) / 12 },
        };
        int absolute = F(ch == Cpl ? "Absolute Exponent" : "First Exponent", 4, ch == Cpl ? Explain.Cplabsexp : Explain.FirstExponent);
        int groupsStart = _b.Position;
        bool valid = true;
        byte[] exp = _exp[ch];
        int previous = ch == Cpl ? absolute << 1 : absolute;
        int bin = start;
        if (ch != Cpl)
        {
            exp[bin++] = (byte)previous;
        }

        for (int group = 0; group < groups; group++)
        {
            int value = _b.ReadUnkept(7);
            if (_b.IsBuildingNodes)
            {
                continue;
            }

            valid &= value <= 124;
            for (int i = 0; i < 3; i++)
            {
                int mapped = i == 0 ? value / 25 : i == 1 ? value % 25 / 5 : value % 5;
                previous += mapped - 2;
                valid &= previous is >= 0 and <= 24;
                for (int j = 0; j < groupSize && bin < 256; j++)
                {
                    exp[bin++] = (byte)Math.Clamp(previous, 0, 24);
                }
            }
        }

        _t?.Field("Exponent Groups", groupsStart, _b.Position, NodeValue.Integer(groups, "groups", "group"), Explain.ExponentGroups);
        bool kept = _b.Derived(1, valid ? 1 : 0) != 0;
        if (!kept)
        {
            Report(Issue.BlockInvalid, "Exponent Groups", groupsStart, _b.Position,
                "an exponent group exceeds 124 or an exponent leaves the range 0 to 24 (check 17)");
            _r.Stop = StopReason.Invalid;
            _r.StopBit = _b.Position;
            Close();
            return false;
        }

        if (ch < _nfchans)
        {
            F("Gain Range Code", 2, Explain.Gainrng);
        }

        _hasExponents[ch] = true;
        _realloc[ch] = true;
        Close(Ac3Names.ExponentStrategy(strategy));
        return true;
    }

    // Bit allocation parameters, SNR offsets, fast gains, coupling leak and delta bit allocation
    // (5.4.3.30 to 5.4.3.57; Annex E 2.2.4).
    private bool BitAllocationParameters(int blk)
    {
        bool reallocAll = blk == 0;
        bool parameters = _eac3 && !_bamode ? false : Flag("HasBitAllocationParameters", Explain.Baie, Topics.AudioBlocks);
        if (_eac3 && !_bamode && blk == 0)
        {
            (_sdcycod, _fdcycod, _sgaincod, _dbpbcod, _floorcod) = (2, 1, 1, 2, 7);
        }

        if (parameters)
        {
            Open("Bit Allocation Parameters", Explain.BitAllocationParameters, Topics.AudioBlocks);
            _sdcycod = F("Slow Decay Code", 2, Explain.Sdcycod);
            _fdcycod = F("Fast Decay Code", 2, Explain.Fdcycod);
            _sgaincod = F("Slow Gain Code", 2, Explain.Sgaincod);
            _dbpbcod = F("dB per Bit Code", 2, Explain.Dbpbcod);
            _floorcod = F("Masking Floor Code", 3, Explain.Floorcod);
            Close();
            reallocAll = true;
        }
        else if (blk == 0 && (!_eac3 || _bamode))
        {
            Inconsistent("baie is 0 in block 0 (check 12)");
        }

        if (!_eac3)
        {
            if (Flag("HasSnrOffsets", Explain.Snroffste, Topics.AudioBlocks))
            {
                Open("SNR Offsets", Explain.SnrOffsets, Topics.AudioBlocks);
                _csnroffst = F("Coarse SNR Offset", 6, Explain.Csnroffst);
                if (_cplInUse)
                {
                    _fsnroffst[Cpl] = FC("Fine SNR Offset", Cpl, 4, Explain.Fsnroffst);
                    _fgaincod[Cpl] = FC("Fast Gain Code", Cpl, 3, Explain.Fgaincod);
                }

                for (int ch = 0; ch < _nfchans; ch++)
                {
                    _fsnroffst[ch] = FC("Fine SNR Offset", ch, 4, Explain.Fsnroffst);
                    _fgaincod[ch] = FC("Fast Gain Code", ch, 3, Explain.Fgaincod);
                }

                if (_lfeon)
                {
                    _fsnroffst[Lfe] = FC("Fine SNR Offset", Lfe, 4, Explain.Fsnroffst);
                    _fgaincod[Lfe] = FC("Fast Gain Code", Lfe, 3, Explain.Fgaincod);
                }

                Close();
                reallocAll = true;
            }
            else if (blk == 0)
            {
                Inconsistent("snroffste is 0 in block 0 (check 13)");
            }
        }
        else
        {
            if (_snroffststr == 0)
            {
                _csnroffst = _frmcsnroffst;
                Array.Fill(_fsnroffst, _frmfsnroffst);
            }
            else if (_snroffststr == 3)
            {
                Invalid("the SNR offset strategy is the reserved value 3");
                return false;
            }
            else if (blk == 0 || Flag("HasSnrOffsets", Explain.Snroffste, Topics.AudioBlocks))
            {
                Open("SNR Offsets", Explain.SnrOffsets, Topics.AudioBlocks);
                _csnroffst = F("Coarse SNR Offset", 6, Explain.Csnroffst);
                if (_snroffststr == 1)
                {
                    Array.Fill(_fsnroffst, F("Block Fine SNR Offset", 4, Explain.Blkfsnroffst));
                }
                else
                {
                    if (_cplInUse)
                    {
                        _fsnroffst[Cpl] = FC("Fine SNR Offset", Cpl, 4, Explain.Fsnroffst);
                    }

                    for (int ch = 0; ch < _nfchans; ch++)
                    {
                        _fsnroffst[ch] = FC("Fine SNR Offset", ch, 4, Explain.Fsnroffst);
                    }

                    if (_lfeon)
                    {
                        _fsnroffst[Lfe] = FC("Fine SNR Offset", Lfe, 4, Explain.Fsnroffst);
                    }
                }

                Close();
                reallocAll = true;
            }

            if (_frmfgaincode && Flag("HasFastGainCodes", Explain.Fgaincode))
            {
                Open("Fast Gain Codes", Explain.FastGainCodes, Topics.AudioBlocks);
                if (_cplInUse)
                {
                    _fgaincod[Cpl] = FC("Fast Gain Code", Cpl, 3, Explain.Fgaincod);
                }

                for (int ch = 0; ch < _nfchans; ch++)
                {
                    _fgaincod[ch] = FC("Fast Gain Code", ch, 3, Explain.Fgaincod);
                }

                if (_lfeon)
                {
                    _fgaincod[Lfe] = FC("Fast Gain Code", Lfe, 3, Explain.Fgaincod);
                }

                Close();
                reallocAll = true;
            }
            else
            {
                reallocAll |= _fgaincod.AsSpan().ContainsAnyExcept(4);
                Array.Fill(_fgaincod, 4);
            }

            if (_strmtyp == 0 && Flag("HasConverterSnrOffset", Explain.Convsnroffste))
            {
                F("Converter SNR Offset", 10, Explain.Convsnroffst);
            }
        }

        // Coupling leak (5.4.3.44 to 5.4.3.46).
        if (_cplInUse)
        {
            bool leak;
            if (_eac3 && _firstCplLeak)
            {
                leak = true;
            }
            else
            {
                leak = Flag("HasCouplingLeak", Explain.Cplleake, Topics.Coupling);
                if (!_eac3 && !leak && blk == 0)
                {
                    Inconsistent("cplleake is 0 in block 0 (check 14)");
                }
                else if (!_eac3 && !leak && !_previousCplInUse)
                {
                    Inconsistent("cplleake is 0 where coupling starts after a block without it (5.4.3.44)");
                }
            }

            _firstCplLeak = false;
            if (leak)
            {
                _cplfleak = F("Coupling Fast Leak", 3, Explain.Cplfleak, topic: Topics.Coupling);
                _cplsleak = F("Coupling Slow Leak", 3, Explain.Cplsleak, topic: Topics.Coupling);
                _realloc[Cpl] = true;
            }
        }

        // Delta bit allocation (5.4.3.47 to 5.4.3.57, 7.2.2.6).
        bool deltaSyntax = !_eac3 || _dbaflde;
        int active = 0;
        for (int ch = 0; ch < Channels; ch++)
        {
            active |= _deltbae[ch] is 0 or 1 ? 1 << ch : 0;
        }

        bool delta = deltaSyntax && Flag("HasDeltaBitAllocation", Explain.Deltbaie, Topics.AudioBlocks);
        if (delta)
        {
            Open("Delta Bit Allocation", Explain.DeltaBitAllocation, Topics.AudioBlocks);
            if (_cplInUse)
            {
                _deltbae[Cpl] = FC("Delta Bit Allocation Mode", Cpl, 2, Explain.Deltbae, v => $"{v} ({Ac3Names.DeltaBitAllocation(v)})");
            }

            for (int ch = 0; ch < _nfchans; ch++)
            {
                _deltbae[ch] = FC("Delta Bit Allocation Mode", ch, 2, Explain.Deltbae, v => $"{v} ({Ac3Names.DeltaBitAllocation(v)})");
            }

            // The coupling channel's segments come first, then the channels' (5.3.3, Table E1.4).
            for (int i = _cplInUse ? -1 : 0; i < _nfchans; i++)
            {
                int ch = i < 0 ? Cpl : i;
                if (_deltbae[ch] == 3)
                {
                    Close();
                    Invalid("a delta bit allocation mode is the reserved value 3");
                    return false;
                }

                if (_deltbae[ch] == 1 && !DeltaSegments(ch))
                {
                    Inconsistent("a delta bit allocation reaches past band 49 (checks 15 and 16)");
                }

                if (_deltbae[ch] == 2)
                {
                    Array.Clear(_deltas[ch]);
                }
            }

            Close();
            _r.DeltaBitAllocationBlocks++;
            reallocAll = true;
        }

        if (deltaSyntax && blk > 0)
        {
            DeltaChecks(delta, active);
        }
        else if (blk == 0 && !delta)
        {
            Array.Fill(_deltbae, 2);
            foreach (int[] bands in _deltas)
            {
                Array.Clear(bands);
            }
        }

        if (!_eac3 && _cplInUse && !_previousCplInUse && (!delta || _deltbae[Cpl] == 0) && blk > 0)
        {
            Inconsistent("coupling starts without its delta bit allocation (check 26)");
        }

        if (reallocAll || !_allocationStarted)
        {
            Array.Fill(_realloc, true);
            _allocationStarted = true;
        }

        return true;
    }

    private bool DeltaSegments(int ch)
    {
        OpenChannel("Delta Bit Allocation Segments", ch, Explain.DeltaSegments, Topics.AudioBlocks);
        int segments = F("Segments", 3, Explain.Deltnseg, v => NodeValue.Integer(v + 1, "segments", "segment")) + 1;
        int[] bands = _deltas[ch];
        Array.Clear(bands);
        bool fits = true;
        int band = 0;
        for (int segment = 0; segment < segments; segment++)
        {
            band += F("Offset", 5, Explain.Deltoffst, v => NodeValue.Integer(v, "bands", "band"));
            int length = F("Length", 4, Explain.Deltlen, v => NodeValue.Integer(v, "bands", "band"));
            int ba = F("Adjustment", 3, Explain.Deltba, v => NodeValue.Integer(v >= 4 ? (v - 3) * 6 : (v - 4) * 6, "dB"));
            int value = (ba >= 4 ? ba - 3 : ba - 4) << 7;
            for (int k = 0; k < length; k++, band++)
            {
                if (band < BitAllocation.Bands)
                {
                    bands[band] += value;
                }
                else
                {
                    fits = false;
                }
            }
        }

        Close(NodeValue.Integer(segments, "segments", "segment"));
        return fits;
    }

    // Checks 27 and 28 of 7.10.2: where the coupling range or a channel's bandwidth changes while
    // a delta bit allocation is active, the block must give a new one.
    private void DeltaChecks(bool delta, int active)
    {
        bool cplDeltaActive = (active & (1 << Cpl)) != 0;
        bool cplNew = delta && _deltbae[Cpl] != 0;
        if (_cplInUse && _previousCplInUse && (_cplbegf != _previousCplBegin || _cplendf != _previousCplEnd) && cplDeltaActive && !cplNew)
        {
            Inconsistent("the coupling range changed but the coupling delta bit allocation is kept (check 27)");
        }

        for (int ch = 0; ch < _nfchans; ch++)
        {
            if (_bandwidthChanged[ch] && (active & (1 << ch)) != 0 && !(delta && _deltbae[ch] != 0))
            {
                Inconsistent("a channel's bandwidth changed but its delta bit allocation is kept (check 28)");
                return;
            }
        }
    }

    // The mantissas (5.4.3.61 to 5.4.3.63): their length follows from the bit allocation pointers
    // (7.2.2.7, Table 7.17), with the 3, 5 and 11 level quantizers grouped across the block (7.3.5).
    private bool Mantissas()
    {
        int bits = 0;
        if (!_b.IsBuildingNodes)
        {
            Allocate();
            int grouped1 = 0;
            int grouped2 = 0;
            int grouped4 = 0;
            long total = 0;
            for (int ch = 0; ch < Channels; ch++)
            {
                if (ch < _nfchans || (ch == Cpl && _cplInUse) || (ch == Lfe && _lfeon))
                {
                    grouped1 += _count1[ch];
                    grouped2 += _count2[ch];
                    grouped4 += _count4[ch];
                    total += _fixedBits[ch];
                }
            }

            total += ((grouped1 + 2) / 3 * 5) + ((grouped2 + 2) / 3 * 7) + ((grouped4 + 1) / 2 * 7);
            bits = (int)Math.Min(total, 0xFFFF);
        }

        bits = _b.Derived(16, bits);
        int start = _b.Position;
        int blocksEnd = (_frameWords * 16) - 18;
        if (start + bits > _b.Limit)
        {
            int end = _b.Limit;
            bool truncated = _b.FrameBitCount < blocksEnd && start + bits <= blocksEnd;
            _t?.Field("Mantissas", start, Math.Max(start, end), NodeValue.Integer(bits, "bits"), Explain.Mantissas, Topics.AudioBlocks);
            _r.Stop = truncated ? StopReason.DataEnds : StopReason.Overrun;
            _r.StopBit = start;
            if (!truncated && !Counted(Issue.Overrun))
            {
                Report(Issue.Overrun, "Mantissas", start, Math.Max(start, end),
                    $"the bit allocation gives an audio block's mantissas {Counts.Of(bits, "bit", "bits")}, more than the frame holds after them");
            }

            _b.MoveTo(Math.Max(start, end));
            return false;
        }

        _b.Skip(bits);
        _t?.Field("Mantissas", start, _b.Position, NodeValue.Integer(bits, "bits"), Explain.Mantissas, Topics.AudioBlocks);
        _r.MantissaBits += bits;
        return true;
    }

    // Recomputes the bit allocation pointers of the channels whose inputs changed, and counts their
    // mantissas by kind.
    private void Allocate()
    {
        bool allZero = _csnroffst == 0;
        for (int ch = 0; ch < Channels && allZero; ch++)
        {
            if (ch < _nfchans || (ch == Cpl && _cplInUse) || (ch == Lfe && _lfeon))
            {
                allZero &= _fsnroffst[ch] == 0;
            }
        }

        for (int ch = 0; ch < Channels; ch++)
        {
            bool used = ch < _nfchans || (ch == Cpl && _cplInUse) || (ch == Lfe && _lfeon);
            if (!used || !_realloc[ch])
            {
                continue;
            }

            _realloc[ch] = false;
            int start = ch == Cpl ? _startMant[Cpl] : 0;
            int end = _endMant[ch];
            byte[] bap = _bap[ch];
            if (allZero || end <= start)
            {
                // 7.2.2.1.1: with every SNR offset 0, no mantissa takes bits.
                Array.Clear(bap);
            }
            else
            {
                var parameters = new BitAllocation.Parameters
                {
                    SlowDecay = Tables.SlowDecay[_sdcycod],
                    FastDecay = Tables.FastDecay[_fdcycod],
                    SlowGain = Tables.SlowGain[_sgaincod],
                    DbPerBit = Tables.DbPerBit[_dbpbcod],
                    Floor = Tables.Floor[_floorcod],
                    FastGain = Tables.FastGain[_fgaincod[ch]],
                    SnrOffset = (((_csnroffst - 15) << 4) + _fsnroffst[ch]) << 2,
                    FastLeak = (_cplfleak << 8) + 768,
                    SlowLeak = (_cplsleak << 8) + 768,
                    IsCoupling = ch == Cpl,
                    IsLfe = ch == Lfe,
                    SampleRateCode = _fscod,
                };
                bool hasDelta = ch < Lfe && _deltbae[ch] is 0 or 1;
                BitAllocation.Compute(_exp[ch], start, end, parameters, hasDelta ? _deltas[ch] : [], bap);
            }

            int count1 = 0;
            int count2 = 0;
            int count4 = 0;
            int fixedBits = 0;
            for (int bin = start; bin < end; bin++)
            {
                switch (bap[bin])
                {
                    case 0:
                        break;
                    case 1:
                        count1++;
                        break;
                    case 2:
                        count2++;
                        break;
                    case 3:
                        fixedBits += 3;
                        break;
                    case 4:
                        count4++;
                        break;
                    case 5:
                        fixedBits += 4;
                        break;
                    case 14:
                        fixedBits += 14;
                        break;
                    case 15:
                        fixedBits += 16;
                        break;
                    default:
                        fixedBits += bap[bin] - 1;
                        break;
                }
            }

            _count1[ch] = count1;
            _count2[ch] = count2;
            _count4[ch] = count4;
            _fixedBits[ch] = fixedBits;
        }
    }

    private void Inconsistent(string detail)
    {
        if (!Counted(Issue.BlockInconsistent))
        {
            Report(Issue.BlockInconsistent, LastField(), _lastStart, _lastEnd, detail);
        }
    }

    private void Invalid(string detail)
    {
        _r.Stop = StopReason.Invalid;
        _r.StopBit = _b.Position;
        if (!Counted(Issue.BlockInvalid))
        {
            Report(Issue.BlockInvalid, LastField(), _lastStart, _lastEnd, detail);
        }
    }

    // A field of one channel, named with the channel ("IsBlockSwitched (left)").
    private int FC(string name, int ch, int width, string description, Func<int, NodeValue>? value = null, string? topic = null)
    {
        int start = _b.Position;
        int read = _b.Read(width);
        _lastName = name;
        _lastChannel = ch;
        _lastStart = start;
        _lastEnd = _b.Position;
        _t?.Field(ChannelField(name, ch), start, _b.Position, value is null ? NodeValue.Integer(read) : value(read), description, topic);
        return read;
    }

    private void OpenChannel(string name, int ch, string description, string? topic) => _t?.Open(ChannelField(name, ch), _b.Position, description, topic);

    private string ChannelField(string name, int ch) => $"{name} ({ChannelLabel(ch)})";

    private string LastField() => _lastChannel < 0 ? _lastName : ChannelField(_lastName, _lastChannel);

    private string ChannelLabel(int ch) => ch switch
    {
        Cpl => "coupling",
        Lfe => "LFE",
        _ => _acmod switch
        {
            0 => ch == 0 ? "channel 1" : "channel 2",
            1 => "center",
            2 => ch == 0 ? "left" : "right",
            3 => ch switch { 0 => "left", 1 => "center", _ => "right" },
            4 => ch switch { 0 => "left", 1 => "right", _ => "surround" },
            5 => ch switch { 0 => "left", 1 => "center", 2 => "right", _ => "surround" },
            6 => ch switch { 0 => "left", 1 => "right", 2 => "left surround", _ => "right surround" },
            _ => ch switch { 0 => "left", 1 => "center", 2 => "right", 3 => "left surround", _ => "right surround" },
        },
    };

    private static NodeValue BandStructureText(int bits, int width)
    {
        if (width <= 0)
        {
            return "none";
        }

        Span<char> text = stackalloc char[width];
        for (int i = 0; i < width; i++)
        {
            text[i] = ((bits >> (width - 1 - i)) & 1) != 0 ? '1' : '0';
        }

        return new string(text);
    }

    // 7.5: each band is coded as left/right (0) or as sum/difference (1).
    private static NodeValue RematrixingText(int flags, int bands)
    {
        var parts = new string[bands];
        for (int i = 0; i < bands; i++)
        {
            parts[i] = ((flags >> (bands - 1 - i)) & 1) != 0 ? "sum/difference" : "left/right";
        }

        return string.Join(", ", parts);
    }
}
