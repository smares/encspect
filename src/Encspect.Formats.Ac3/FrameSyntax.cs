using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Something a frame's fields show that A/52 does not allow, or that leaves part of it unread. The
// analysis reports it at the field it was found in (FrameSyntax.IssueFound).
internal enum Issue : byte
{
    ReservedValue,
    DialnormReserved,
    LanguageCode,
    BlockInconsistent,
    BlockInvalid,
    Overrun,
    HeaderOverrun,
    UndefinedBsid,
    ReducedRate,
    Aht,
    EnhancedCoupling,
    MixDataOverrun,
    ChannelMapCount,
    AuxiliaryLength,
    EmdfTruncated,
    EmdfLength,
    EmdfPayloadSize,
    EmdfReserved,
    EmdfVersion,
    PayloadShort,
    JocReserved,
    ObjectAudioIncomplete,
    EmdfLimit,
    DownmixModeReserved,
}

// Reads one syncframe field by field: the syncinfo and bit stream information of AC-3 (ATSC
// A/52:2018 5.3) with Annex D's alternate syntax, or of E-AC-3 (Annex E 2.2) with its audio frame
// fields, then every audio block as far as it can be stepped over without decoding, the auxiliary
// data and the error check. Stepping from one audio block to the next takes the length of its
// mantissas, which follows from the exponents and the bit allocation parameters through the
// parametric bit allocation of 7.2 (BitAllocation). One instance serves one analysis; it holds the
// state of the frame being read.
internal sealed partial class FrameSyntax
{
    private const int Cpl = 5;
    private const int Lfe = 6;
    private const int Channels = 7;

    private readonly FrameBits _b;
    private readonly FrameResult _r = new();
    private Tree? _t;
    private Action<Issue, string, int, int, string>? _issue;

    // The field read last, for an issue found in it.
    private string _lastName = string.Empty;
    private int _lastChannel = -1;
    private int _lastStart;
    private int _lastEnd;

    // Bit stream information.
    private int _bsid;
    private bool _eac3;
    private int _acmod;
    private int _nfchans;
    private bool _lfeon;
    private int _fscod;
    private int _strmtyp;
    private int _blocks;
    private int _frameWords;

    // E-AC-3 audio frame (Annex E 2.2.3).
    private bool _expstre;
    private bool _ahte;
    private int _snroffststr;
    private bool _blkswe;
    private bool _dithflage;
    private bool _bamode;
    private bool _frmfgaincode;
    private bool _dbaflde;
    private bool _skipflde;
    private readonly bool[] _cplstre = new bool[6];
    private readonly bool[] _cplinu = new bool[6];
    private readonly int[] _cplexpstr = new int[6];
    private readonly int[,] _chexpstr = new int[6, 5];
    private readonly int[] _lfeexpstr = new int[6];
    private readonly bool[] _chahtinu = new bool[5];
    private bool _cplahtinu;
    private bool _lfeahtinu;
    private int _frmcsnroffst;
    private int _frmfsnroffst;

    // Audio block state that carries over from block to block within a frame.
    private bool _cplInUse;
    private readonly bool[] _chincpl = new bool[5];
    private bool _phsflginu;
    private int _cplbegf;
    private int _cplendf;
    private int _ncplsubnd;
    private int _ncplbnd;
    private readonly int[] _cplbndstrc = new int[18];
    private readonly bool[] _firstCplCoords = new bool[5];
    private bool _firstCplLeak;
    private bool _ecplinu;
    private bool _spxinu;
    private readonly bool[] _chinspx = new bool[5];
    private int _spxBeginBand;
    private int _spxEndBand;
    private int _spxbegf;
    private readonly int[] _spxbndstrc = new int[17];
    private readonly bool[] _firstSpxCoords = new bool[5];
    private int _nrematbd;
    private bool _previousCplInUse;
    private int _previousCplBegin = -1;
    private int _previousCplEnd = -1;

    private readonly int[] _startMant = new int[Channels];
    private readonly int[] _endMant = new int[Channels];
    private readonly byte[][] _exp = [.. Enumerable.Range(0, Channels).Select(_ => new byte[256])];
    private readonly byte[][] _bap = [.. Enumerable.Range(0, Channels).Select(_ => new byte[256])];
    private readonly bool[] _hasExponents = new bool[Channels];
    private readonly bool[] _realloc = new bool[Channels];

    // Mantissas per channel by kind, recounted when the allocation changes: bap 1, 2 and 4 are
    // grouped (7.3.5), the rest take a fixed number of bits.
    private readonly int[] _count1 = new int[Channels];
    private readonly int[] _count2 = new int[Channels];
    private readonly int[] _count4 = new int[Channels];
    private readonly int[] _fixedBits = new int[Channels];

    // Bit allocation parameters (7.2.2.1).
    private int _sdcycod;
    private int _fdcycod;
    private int _sgaincod;
    private int _dbpbcod;
    private int _floorcod;
    private int _csnroffst;
    private readonly int[] _fsnroffst = new int[Channels];
    private readonly int[] _fgaincod = new int[Channels];
    private int _cplfleak;
    private int _cplsleak;
    private readonly int[] _deltbae = new int[Channels];
    private readonly int[][] _deltas = [.. Enumerable.Range(0, Channels).Select(_ => new int[BitAllocation.Bands])];

    public FrameSyntax(FrameBits bits)
    {
        _b = bits;
    }

    public FrameResult Result => _r;

    // Called once per issue with the field it was found in: its name and bits.
    // Called before an issue's finding is built: true where the analysis has reported that issue
    // already and only counted this case, so its field name and text are not built again.
    public Func<Issue, bool>? IssueCounted { get; set; }

    private bool Counted(Issue issue) => !_b.IsBuildingNodes && IssueCounted?.Invoke(issue) == true;

    public Action<Issue, string, int, int, string>? IssueFound
    {
        get => _issue;
        set => _issue = value;
    }

    // Reads a frame while analyzing. bytes holds available of its length bytes.
    public void Analyze(byte[] bytes, int available, SyncFrame header)
    {
        _b.Start(bytes, available);
        _t = null;
        Read(header);
    }

    // Called where building a frame's nodes read another number of the record's bits than the
    // analysis kept (frame offset, kept, read): the two readings went apart. A check for tests.
    internal Action<long, int, int>? RecordMismatch { get; init; }

    // Builds the nodes of a frame from its record. crc1, crc2: the verdicts of its CRCs.
    public List<Node> Nodes(byte[] record, int keptBits, long offset, int length, SyncFrame header, string crc1, string crc2)
    {
        _b.StartNodes(record, keptBits, length);
        _t = new Tree(offset);
        _crc1Verdict = crc1;
        _crc2Verdict = crc2;
        Read(header);
        if (_b.KeptBits != keptBits)
        {
            RecordMismatch?.Invoke(offset, keptBits, _b.KeptBits);
        }

        return _t.Nodes;
    }

    private string _crc1Verdict = string.Empty;
    private string _crc2Verdict = string.Empty;

    private void Read(SyncFrame header)
    {
        _eac3 = header.IsEac3;
        _blocks = header.Blocks;
        _frameWords = header.FrameSizeWords;
        _r.Reset(_blocks);
        string part = "the bit stream information";
        try
        {
            SyncInfo(header);
            if (_eac3)
            {
                Eac3Bsi();
            }
            else
            {
                Ac3Bsi();
            }

            if (_bsid is 9 or 10)
            {
                Stop(StopReason.UndefinedBsid, Issue.UndefinedBsid);
            }
            else
            {
                if (_eac3)
                {
                    part = "the audio frame fields (audfrm)";
                    AudioFrame();
                }

                if (_eac3 && _fscod == 3)
                {
                    Stop(StopReason.ReducedRate, Issue.ReducedRate);
                }
                else
                {
                    // The audio blocks and the auxiliary data end before auxdatae and the error
                    // check, the frame's last 18 bits (5.3.4, 5.3.5; Annex E 2.2.5, 2.2.6).
                    _b.LimitTo((_frameWords * 16) - 18);
                    part = "an audio block";
                    Blocks();
                }
            }
        }
        catch (EndOfStreamException)
        {
            if (_r.Stop == StopReason.None)
            {
                _r.Stop = _b.Position + 32 > _b.FrameBitCount && _b.FrameBitCount < _frameWords * 16 ? StopReason.DataEnds : StopReason.Overrun;
                _r.StopBit = _b.Position;
                if (_r.Stop == StopReason.Overrun && !Counted(part == "an audio block" ? Issue.Overrun : Issue.HeaderOverrun))
                {
                    bool block = part == "an audio block";
                    Report(block ? Issue.Overrun : Issue.HeaderOverrun, LastField(), _lastStart, _lastEnd, block
                        ? $"it reaches past the frame's end after the field {LastField()}, so the rest of the frame's audio blocks are not read"
                        : $"reading {part} reaches past its end after the field {LastField()}");
                }
            }
        }

        _b.Unlimit();
        _t?.CloseAll(_b.Position);
        End();
    }

    // The end of the frame: the auxiliary data (5.3.4) and the error check (5.3.5, Annex E
    // 2.2.6). Their fields sit at fixed places before the frame's end, so they are read wherever the
    // audio blocks stopped; the auxiliary data's own bits run from the last block to them.
    private void End()
    {
        int frameEnd = _frameWords * 16;
        if (_b.FrameBitCount < frameEnd || frameEnd < 40)
        {
            return;
        }

        int dataEnd = _b.Position;
        try
        {
            _b.MoveTo(frameEnd - 18);
            int auxStart = frameEnd - 18;
            int flagStart = _b.Position;
            bool hasLength = _b.Read(1) != 0;
            int auxLength = -1;
            if (hasLength)
            {
                _b.MoveTo(frameEnd - 32);
                auxStart = frameEnd - 32;
                auxLength = _b.Read(14);
            }

            bool blocksRead = _r.Stop == StopReason.None;
            if (dataEnd <= auxStart)
            {
                _t?.Open(blocksRead ? "Auxiliary Data" : "Unread Data", dataEnd, blocksRead ? Explain.AuxiliaryBits : Explain.UnreadData, Topics.FramesAndBsi);
                // The bits after the last audio block read hold the auxiliary data field, the last
                // auxdatal bits before auxdatal (5.4.4), and what an encoder leaves before it; where
                // the blocks were not read to their end, the audio blocks' remaining bits too, with the
                // skip fields they hold. MediaInfo searches the whole frame for a container as well.
                EmdfRegion(dataEnd, auxStart, blocksRead);

                _t?.Close(auxStart, NodeValue.Integer(auxStart - dataEnd, "bits"));
            }

            if (blocksRead)
            {
                _r.AuxiliaryBits = Math.Max(0, auxStart - dataEnd);
                _r.HasAuxiliaryLength = hasLength;
                if (hasLength && auxLength > auxStart - dataEnd && !_b.IsBuildingNodes)
                {
                    Report(Issue.AuxiliaryLength, "Auxiliary Data Length", frameEnd - 32, frameEnd - 18,
                        $"auxdatal gives {auxLength} bits of auxiliary data, but the field holds {Math.Max(0, auxStart - dataEnd)}");
                }
            }

            if (hasLength)
            {
                _t?.Field("Auxiliary Data Length", frameEnd - 32, frameEnd - 18, NodeValue.Integer(auxLength, "bits"), Explain.AuxiliaryLength);
            }

            _t?.Field("HasAuxiliaryLength", flagStart, flagStart + 1, NodeValue.Boolean(hasLength), Explain.AuxiliaryExists);
            _b.MoveTo(frameEnd - 17);
            _t?.Open("Error Check", frameEnd - 17, Explain.ErrorCheck, Topics.Crc);
            F(_eac3 ? "Encoder Information" : "CRC Reserved Bit", 1, _eac3 ? Explain.EncoderInfo : Explain.CrcReserved);
            int crcStart = _b.Position;
            int crc2 = _b.Read(16);
            if (_t is not null)
            {
                Node crc = _t.Field("CRC2", crcStart, crcStart + 16, NodeValue.Hex(crc2, 4), _eac3 ? Explain.Eac3Crc : Explain.Crc2, Topics.Crc);
                crc.Add(new Node("Check", crc.Offset, crc.Length, _crc2Verdict) { Description = Explain.CrcCheck });
            }

            _t?.Close(frameEnd);
        }
        catch (EndOfStreamException)
        {
            _t?.CloseAll(_b.Position);
        }
    }

    private void Stop(StopReason reason, Issue issue)
    {
        _r.Stop = reason;
        _r.StopBit = _b.Position;
        if (!Counted(issue))
        {
            Report(issue, LastField(), _lastStart, _lastEnd, string.Empty);
        }
    }

    // syncinfo (5.3.1; Annex E 2.2.1).
    private void SyncInfo(SyncFrame header)
    {
        Open("Sync Information", Explain.SyncInfo);
        F("Sync Word", 16, Explain.SyncWord, Hex4);
        if (!header.IsEac3)
        {
            int start = _b.Position;
            int crc1 = _b.Read(16);
            if (_t is not null)
            {
                Node crc = _t.Field("CRC1", start, start + 16, NodeValue.Hex(crc1, 4), Explain.Crc1, Topics.Crc);
                crc.Add(new Node("Check", crc.Offset, crc.Length, _crc1Verdict) { Description = Explain.CrcCheck });
            }

            _fscod = F("Sample Rate Code", 2, Explain.Fscod, SampleRateCode);
            F("Frame Size Code", 6, Explain.Frmsizecod, v => FrameSize(v, _fscod));
        }

        Close();
    }

    // bsi of AC-3 (5.3.2) and of Annex D (D2.2).
    private void Ac3Bsi()
    {
        Open("Bit Stream Information", Explain.Bsi);
        _bsid = F("Bit Stream ID", 5, Explain.Bsid, v => $"{v} ({Ac3Names.BitStreamId(v)})");
        int bsmodStart = _b.Position;
        int bsmod = _b.Read(3);
        _acmod = _b.Read(3);
        _t?.Field("Bit Stream Mode", bsmodStart, bsmodStart + 3, $"{bsmod} ({Ac3Names.ServiceType(bsmod, _acmod)})", Explain.Bsmod);
        _t?.Field("Audio Coding Mode", bsmodStart + 3, bsmodStart + 6, $"{_acmod}: {Ac3Names.CodingMode(_acmod)}", Explain.Acmod, Topics.ChannelLayouts);
        _r.Bsmod = bsmod;
        _nfchans = Ac3Names.FullBandwidthChannels(_acmod);
        if ((_acmod & 1) != 0 && _acmod != 1)
        {
            ReservedIf(F("Center Mix Level", 2, Explain.Cmixlev, Ac3Names.CenterMix) == 3);
        }

        if ((_acmod & 4) != 0)
        {
            ReservedIf(F("Surround Mix Level", 2, Explain.Surmixlev, Ac3Names.SurroundMix) == 3);
        }

        if (_acmod == 2)
        {
            int dsurmod = F("Dolby Surround Mode", 2, Explain.Dsurmod, v => $"{v} ({Ac3Names.DolbySurround(v)})");
            _r.DolbySurroundMode = dsurmod;
            ReservedIf(dsurmod == 3);
        }

        _lfeon = Flag("HasLfe", Explain.Lfeon, Topics.ChannelLayouts);
        for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
        {
            string suffix = channel == 0 ? string.Empty : " 2";
            int dialnorm = F("Dialogue Normalization" + suffix, 5, Explain.Dialnorm, Ac3Names.DialogueNormalization, Topics.DialogueNormalization);
            if (channel == 0)
            {
                _r.Dialnorm = dialnorm;
            }

            if (dialnorm == 0)
            {
                Report(Issue.DialnormReserved, "Dialogue Normalization" + suffix, _lastStart, _lastEnd, string.Empty);
            }

            if (Flag("HasCompression" + suffix, Explain.Compre, Topics.DialogueNormalization))
            {
                _r.HasCompression |= channel == 0;
                F("Compression Gain" + suffix, 8, Explain.Compr, Ac3Names.Compression, Topics.DialogueNormalization);
            }

            if (Flag("HasLanguageCode" + suffix, Explain.Langcode))
            {
                if (F("Language Code" + suffix, 8, Explain.Langcod, v => NodeValue.Hex(v, 2)) != 0xFF)
                {
                    Report(Issue.LanguageCode, LastField(), _lastStart, _lastEnd, string.Empty);
                }
            }

            if (Flag("HasProductionInfo" + suffix, Explain.Audprodie))
            {
                F("Mixing Level" + suffix, 5, Explain.Mixlevel, v => NodeValue.Integer(80 + v, "dB SPL"));
                ReservedIf(F("Room Type" + suffix, 2, Explain.Roomtyp, v => $"{v} ({Ac3Names.RoomType(v)})") == 3);
            }
        }

        Flag("IsCopyrighted", Explain.Copyrightb);
        Flag("IsOriginal", Explain.Origbs);
        if (_bsid == 6)
        {
            if (Flag("HasExtendedInfo1", Explain.Xbsi1e))
            {
                Open("Extended Information 1", Explain.Xbsi1);
                DownmixFields(true);
                Close();
            }

            if (Flag("HasExtendedInfo2", Explain.Xbsi2e))
            {
                Open("Extended Information 2", Explain.Xbsi2);
                F("Dolby Surround EX Mode", 2, Explain.Dsurexmod, v => $"{v} ({Ac3Names.DolbySurroundEx(v, _acmod)})");
                ReservedIf(F("Dolby Headphone Mode", 2, Explain.Dheadphonmod, v => $"{v} ({Ac3Names.DolbyHeadphone(v)})") == 3);
                F("A/D Converter Type", 1, Explain.Adconvtyp, v => v == 0 ? "0 (standard)" : "1 (HDCD)");
                ReservedIf(F("Reserved", 8, Explain.Xbsi2Reserved, v => NodeValue.Hex(v, 2)) != 0);
                F("Encoder Information", 1, Explain.EncoderInfo);
                Close();
            }
        }
        else
        {
            if (Flag("HasTimeCode1", Explain.Timecod1e))
            {
                F("Time Code 1", 14, Explain.Timecod1, v => Ac3Names.TimeCode1(v));
            }

            if (Flag("HasTimeCode2", Explain.Timecod2e))
            {
                F("Time Code 2", 14, Explain.Timecod2, v => Ac3Names.TimeCode2(v));
            }
        }

        AdditionalInfo();
        Close();
    }

    // dmixmod and the Lt/Rt and Lo/Ro mix levels (Annex D 2.3.1.2 to 2.3.1.6); E-AC-3 orders them
    // as Lt/Rt then Lo/Ro for the center, then for the surround (Annex E Table E1.2).
    private void DownmixFields(bool annexD)
    {
        if (annexD || _acmod > 2)
        {
            if (F("Preferred Downmix", 2, Explain.Dmixmod, v => $"{v} ({Ac3Names.PreferredDownmix(v)})") == 3)
            {
                Report(Issue.DownmixModeReserved, "Preferred Downmix", _lastStart, _lastEnd, string.Empty);
            }
        }

        if (annexD)
        {
            F("Lt/Rt Center Mix Level", 3, Explain.LtrtCenter, v => Ac3Names.DownmixLevel(v, false));
            ReservedIf(F("Lt/Rt Surround Mix Level", 3, Explain.LtrtSurround, v => Ac3Names.DownmixLevel(v, true)) < 3);
            F("Lo/Ro Center Mix Level", 3, Explain.LoroCenter, v => Ac3Names.DownmixLevel(v, false));
            ReservedIf(F("Lo/Ro Surround Mix Level", 3, Explain.LoroSurround, v => Ac3Names.DownmixLevel(v, true)) < 3);
            return;
        }

        if ((_acmod & 1) != 0 && _acmod > 2)
        {
            F("Lt/Rt Center Mix Level", 3, Explain.LtrtCenter, v => Ac3Names.DownmixLevel(v, false));
            F("Lo/Ro Center Mix Level", 3, Explain.LoroCenter, v => Ac3Names.DownmixLevel(v, false));
        }

        if ((_acmod & 4) != 0)
        {
            ReservedIf(F("Lt/Rt Surround Mix Level", 3, Explain.LtrtSurround, v => Ac3Names.DownmixLevel(v, true)) < 3);
            ReservedIf(F("Lo/Ro Surround Mix Level", 3, Explain.LoroSurround, v => Ac3Names.DownmixLevel(v, true)) < 3);
        }
    }

    // addbsie, addbsil and addbsi (5.4.2.29 to 5.4.2.31).
    private void AdditionalInfo()
    {
        if (!Flag("HasAdditionalInfo", Explain.Addbsie))
        {
            return;
        }

        int length = F("Additional Info Length", 6, Explain.Addbsil, v => NodeValue.Integer(v + 1, "bytes")) + 1;
        int start = _b.Position;
        var text = _t is null ? null : new System.Text.StringBuilder(length * 3);
        // E-AC-3 streams with object audio send flag_ec3_extension_type_a and complexity_index_type_a
        // as the two bytes of addbsi (ETSI TS 103 420 8.3.1). ffmpeg reads the flag from the first
        // byte of any length and MediaInfo the two bytes of any length of two or more; an addbsi of
        // two bytes for some other use cannot be told from the extension.
        bool extension = _eac3 && length == 2;
        if (extension)
        {
            _t?.Open("Additional Info", start, Explain.Addbsi);
            int reserved = F("Reserved", 7, Explain.ExtensionReserved, v => NodeValue.Hex(v, 2));
            ReservedIf(reserved != 0);
            _r.ExtensionFlag = F("HasExtensionTypeA", 1, Explain.ExtensionTypeA, BooleanValue, Topics.ObjectAudio);
            _r.ExtensionStart = _lastStart;
            _r.ExtensionEnd = _lastEnd;
            _r.ComplexityIndex = F("Complexity Index Type A", 8, Explain.ComplexityIndex, topic: Topics.ObjectAudio);
            text?.Append(((reserved << 1) | _r.ExtensionFlag).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                .Append(_r.ComplexityIndex.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 2; i < length; i++)
            {
                int value = _b.Read(8);
                text?.Append(' ').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            _t?.Close(_b.Position, text!.ToString());
            return;
        }

        for (int i = 0; i < length; i++)
        {
            int value = _b.Read(8);
            text?.Append(i == 0 ? string.Empty : " ").Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        _t?.Field("Additional Info", start, _b.Position, text!.ToString(), Explain.Addbsi);
    }

    // bsi of E-AC-3 (Annex E 2.2.2).
    private void Eac3Bsi()
    {
        Open("Bit Stream Information", Explain.Bsi);
        _strmtyp = F("Stream Type", 2, Explain.Strmtyp, v => $"{v} ({Ac3Names.StreamType(v)})", Topics.Eac3Substreams);
        F("Substream ID", 3, Explain.Substreamid, topic: Topics.Eac3Substreams);
        F("Frame Size", 11, Explain.Frmsiz, v => NodeValue.Integer((v + 1) * 2, "bytes"));
        _fscod = F("Sample Rate Code", 2, Explain.Eac3Fscod, SampleRateCode);
        if (_fscod == 3)
        {
            F("Reduced Sample Rate Code", 2, Explain.Fscod2, v => v < 3 ? NodeValue.Integer(SyncFrame.ReducedRates[v], "Hz") : "3 (reserved)");
        }
        else
        {
            F("Blocks Code", 2, Explain.Numblkscod, v => NodeValue.Integer(v switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 }, "blocks", "block"));
        }

        _acmod = F("Audio Coding Mode", 3, Explain.Acmod, v => $"{v}: {Ac3Names.CodingMode(v)}", Topics.ChannelLayouts);
        _nfchans = Ac3Names.FullBandwidthChannels(_acmod);
        _lfeon = Flag("HasLfe", Explain.Lfeon, Topics.ChannelLayouts);
        _bsid = F("Bit Stream ID", 5, Explain.Bsid, v => $"{v} ({Ac3Names.BitStreamId(v)})");
        for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
        {
            string suffix = channel == 0 ? string.Empty : " 2";
            int dialnorm = F("Dialogue Normalization" + suffix, 5, Explain.Dialnorm, Ac3Names.DialogueNormalization, Topics.DialogueNormalization);
            if (channel == 0)
            {
                _r.Dialnorm = dialnorm;
            }

            if (dialnorm == 0)
            {
                Report(Issue.DialnormReserved, LastField(), _lastStart, _lastEnd, string.Empty);
            }

            if (Flag("HasCompression" + suffix, Explain.Compre, Topics.DialogueNormalization))
            {
                _r.HasCompression |= channel == 0;
                F("Compression Gain" + suffix, 8, Explain.Compr, Ac3Names.Compression, Topics.DialogueNormalization);
            }
        }

        if (_strmtyp == 1 && Flag("HasChannelMap", Explain.Chanmape, Topics.Eac3Substreams))
        {
            int map = F("Channel Map", 16, Explain.Chanmap, ChannelMapText, Topics.Eac3Substreams);
            _r.ChannelMap = (ushort)map;
            _r.HasChannelMap = true;
            int mapped = 0;
            for (int bit = 0; bit < 16; bit++)
            {
                if ((map & (0x8000 >> bit)) != 0)
                {
                    mapped += Ac3Names.IsPair(bit) ? 2 : 1;
                }
            }

            if (mapped != _nfchans + (_lfeon ? 1 : 0))
            {
                Report(Issue.ChannelMapCount, LastField(), _lastStart, _lastEnd,
                    $"the channel map names {mapped} channels, but acmod and lfeon code {_nfchans + (_lfeon ? 1 : 0)}");
            }
        }

        if (Flag("HasMixingMetadata", Explain.Mixmdate))
        {
            Open("Mixing Metadata", Explain.MixingMetadata);
            DownmixFields(false);
            if (_lfeon && Flag("HasLfeMixLevel", Explain.Lfemixlevcode))
            {
                F("LFE Mix Level", 5, Explain.Lfemixlevcod, v => NodeValue.Integer(10 - v, "dB"));
            }

            if (_strmtyp == 0)
            {
                for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
                {
                    string suffix = channel == 0 ? string.Empty : " 2";
                    if (Flag("HasProgramScale" + suffix, Explain.Pgmscle))
                    {
                        F("Program Scale" + suffix, 6, Explain.Pgmscl, Ac3Names.ProgramScale);
                    }
                }

                if (Flag("HasExternalProgramScale", Explain.Extpgmscle))
                {
                    F("External Program Scale", 6, Explain.Extpgmscl, Ac3Names.ProgramScale);
                }

                int mixdef = F("Mix Definition", 2, Explain.Mixdef, v => $"{v} ({Ac3Names.MixDefinition(v)})");
                if (mixdef == 1)
                {
                    PremixFields();
                }
                else if (mixdef == 2)
                {
                    F("Mixing Data", 12, Explain.Mixdata, v => NodeValue.Hex(v, 3));
                }
                else if (mixdef == 3)
                {
                    MixingParameters();
                }

                if (_acmod < 2)
                {
                    for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
                    {
                        string suffix = channel == 0 ? string.Empty : " 2";
                        if (Flag("HasPanInfo" + suffix, Explain.Paninfoe))
                        {
                            ReservedIf(F("Pan Mean Direction" + suffix, 8, Explain.Panmean, v => v < 240 ? NodeValue.Decimal(v * 1.5, 1, "degrees") : $"{v} (reserved)") >= 240);
                            F("Pan Information" + suffix, 6, Explain.Paninfo, v => NodeValue.Hex(v, 2));
                        }
                    }
                }

                if (Flag("HasFrameMixingConfiguration", Explain.Frmmixcfginfoe))
                {
                    for (int block = 0; block < _blocks; block++)
                    {
                        if (_blocks == 1 || Flag("HasBlockMixingConfiguration", Explain.Blkmixcfginfoe))
                        {
                            F("Block Mixing Configuration", 5, Explain.Blkmixcfginfo);
                        }
                    }
                }
            }

            Close();
        }

        if (Flag("HasInformationalMetadata", Explain.Infomdate))
        {
            Open("Informational Metadata", Explain.InformationalMetadata);
            int bsmodStart = _b.Position;
            int bsmod = _b.Read(3);
            _t?.Field("Bit Stream Mode", bsmodStart, bsmodStart + 3, $"{bsmod} ({Ac3Names.ServiceType(bsmod, _acmod)})", Explain.Bsmod);
            _r.Bsmod = bsmod;
            Flag("IsCopyrighted", Explain.Copyrightb);
            Flag("IsOriginal", Explain.Origbs);
            if (_acmod == 2)
            {
                int dsurmod = F("Dolby Surround Mode", 2, Explain.Dsurmod, v => $"{v} ({Ac3Names.DolbySurround(v)})");
                _r.DolbySurroundMode = dsurmod;
                ReservedIf(dsurmod == 3);
                ReservedIf(F("Dolby Headphone Mode", 2, Explain.Dheadphonmod, v => $"{v} ({Ac3Names.DolbyHeadphone(v)})") == 3);
            }

            if (_acmod >= 6)
            {
                F("Dolby Surround EX Mode", 2, Explain.Dsurexmod, v => $"{v} ({Ac3Names.DolbySurroundEx(v, _acmod)})");
            }

            for (int channel = 0; channel < (_acmod == 0 ? 2 : 1); channel++)
            {
                string suffix = channel == 0 ? string.Empty : " 2";
                if (Flag("HasProductionInfo" + suffix, Explain.Audprodie))
                {
                    F("Mixing Level" + suffix, 5, Explain.Mixlevel, v => NodeValue.Integer(80 + v, "dB SPL"));
                    ReservedIf(F("Room Type" + suffix, 2, Explain.Roomtyp, v => $"{v} ({Ac3Names.RoomType(v)})") == 3);
                    F("A/D Converter Type" + suffix, 1, Explain.Adconvtyp, v => v == 0 ? "0 (standard)" : "1 (HDCD)");
                }
            }

            if (_fscod < 3)
            {
                Flag("IsSourceRateDouble", Explain.Sourcefscod);
            }

            Close();
        }

        if (_strmtyp == 0 && _blocks != 6)
        {
            Flag("IsConverterSync", Explain.Convsync);
        }

        if (_strmtyp == 2)
        {
            bool blkid = _blocks == 6 || Flag("IsFirstBlockOfAc3Frame", Explain.Blkid);
            if (blkid)
            {
                F("AC-3 Frame Size Code", 6, Explain.ConvertedFrmsizecod, v => $"{v} ({(v < 38 ? $"{Tables.NominalBitrates[v >> 1]} kbps" : "invalid")})");
            }
        }

        AdditionalInfo();
        Close();
    }

    // premixcmpsel, drcsrc and premixcmpscl (Annex E 2.3.1.19 to 2.3.1.21).
    private void PremixFields()
    {
        F("Premix Compression Word", 1, Explain.Premixcmpsel, v => v == 0 ? "0 (dynrng)" : "1 (compr)");
        F("Dynamic Range Source", 1, Explain.Drcsrc, v => v == 0 ? "0 (external program)" : "1 (this program)");
        ReservedIf(F("Premix Compression Scale", 3, Explain.Premixcmpscl, v => $"{v} ({Ac3Names.PremixScale(v)})") == 6);
    }

    // mixing option 4 (Annex E 2.2.2): mixdeflen, then (mixdeflen + 2) bytes of mixing data, of
    // which the structured fields come first; ffmpeg's eac3dec.c skips the same bytes.
    private void MixingParameters()
    {
        int length = F("Mixing Data Length", 5, Explain.Mixdeflen, v => NodeValue.Integer(v + 2, "bytes"));
        int start = _b.Position;
        int end = start + ((length + 2) * 8);
        Open("Mixing Parameters", Explain.MixingParameters);

        // The fields stop where the mixing data ends: a field past it is not read, so the group
        // ends at the length mixdeflen gives.
        bool overrun = false;
        _b.LimitTo(end);
        try
        {
            if (Flag("HasChannelScales", Explain.Mixdata2e))
            {
                PremixFields();
                ChannelScale("Left");
                ChannelScale("Center");
                ChannelScale("Right");
                ChannelScale("Left Surround");
                ChannelScale("Right Surround");
                ChannelScale("LFE");
                if (Flag("HasDownmixScale", Explain.Dmixscle))
                {
                    F("Downmix Scale", 4, Explain.Dmixscl, Ac3Names.ExternalChannelScale);
                }

                if (Flag("HasAuxiliaryChannelScales", Explain.Addche))
                {
                    ChannelScale("First Auxiliary");
                    ChannelScale("Second Auxiliary");
                }
            }

            if (Flag("HasSpeechData", Explain.Mixdata3e))
            {
                F("Speech Data", 5, Explain.Spchdat);
                if (Flag("HasSpeechData1", Explain.Addspchdate))
                {
                    F("Speech Data 1", 5, Explain.Spchdat1);
                    F("Speech Attenuation 1", 2, Explain.Spchan1att);
                    if (Flag("HasSpeechData2", Explain.Addspchdat1e))
                    {
                        F("Speech Data 2", 5, Explain.Spchdat2);
                        F("Speech Attenuation 2", 3, Explain.Spchan2att);
                    }
                }
            }

        }
        catch (EndOfStreamException)
        {
            overrun = true;
        }
        finally
        {
            _b.Unlimit();
        }

        if (overrun)
        {
            if (end > _b.FrameBitCount)
            {
                throw new EndOfStreamException();
            }

            Report(Issue.MixDataOverrun, "Mixing Parameters", start, end,
                $"its fields need more than the {end - start} bits that mixdeflen gives");
        }
        else if (_b.Position < end)
        {
            int rest = _b.Position;
            _b.Skip(end - rest);
            _t?.Field("Mixing Data", rest, end, NodeValue.Integer(end - rest, "bits"), Explain.Mixdata);
        }

        _b.MoveTo(end);
        Close();
    }

    private void ChannelScale(string channel)
    {
        if (Flag($"HasExternal{channel.Replace(" ", string.Empty, StringComparison.Ordinal)}Scale", Explain.ExternalChannelScaleExists))
        {
            F($"External {channel} Scale", 4, Explain.ExternalChannelScale, Ac3Names.ExternalChannelScale);
        }
    }

    // audfrm (Annex E 2.2.3).
    private void AudioFrame()
    {
        Open("Audio Frame", Explain.AudioFrame, Topics.AudioBlocks);
        if (_blocks == 6)
        {
            _expstre = Flag("HasBlockExponentStrategies", Explain.Expstre, Topics.AudioBlocks);
            _ahte = Flag("HasAht", Explain.Ahte);
        }
        else
        {
            _expstre = true;
            _ahte = false;
        }

        _snroffststr = F("SNR Offset Strategy", 2, Explain.Snroffststr, v => $"{v} ({Ac3Names.SnrOffsetStrategy(v)})");
        bool transproce = Flag("HasTransientProcessing", Explain.Transproce);
        _blkswe = Flag("HasBlockSwitchFlags", Explain.Blkswe, Topics.AudioBlocks);
        _dithflage = Flag("HasDitherFlags", Explain.Dithflage);
        _bamode = Flag("HasBitAllocationFields", Explain.Bamode, Topics.AudioBlocks);
        _frmfgaincode = Flag("HasFastGainCodes", Explain.Frmfgaincode);
        _dbaflde = Flag("HasDeltaBitAllocation", Explain.Dbaflde);
        _skipflde = Flag("HasSkipFields", Explain.Skipflde);
        bool spxattene = Flag("HasSpectralExtensionAttenuation", Explain.Spxattene);
        if (_acmod > 1)
        {
            _cplstre[0] = true;
            _cplinu[0] = Flag("IsCouplingInUse", Explain.Cplinu, Topics.Coupling);
            for (int block = 1; block < _blocks; block++)
            {
                _cplstre[block] = Flag("HasCouplingStrategy", Explain.Cplstre, Topics.Coupling);
                _cplinu[block] = _cplstre[block] ? Flag("IsCouplingInUse", Explain.Cplinu, Topics.Coupling) : _cplinu[block - 1];
            }
        }
        else
        {
            Array.Clear(_cplstre);
            Array.Clear(_cplinu);
        }

        int couplingBlocks = 0;
        for (int block = 0; block < _blocks; block++)
        {
            couplingBlocks += _cplinu[block] ? 1 : 0;
        }

        if (_expstre)
        {
            for (int block = 0; block < _blocks; block++)
            {
                if (_cplinu[block])
                {
                    _cplexpstr[block] = F("Coupling Exponent Strategy", 2, Explain.Cplexpstr, ExponentStrategyText, Topics.AudioBlocks);
                }

                for (int ch = 0; ch < _nfchans; ch++)
                {
                    _chexpstr[block, ch] = FC("Exponent Strategy", ch, 2, Explain.Chexpstr, ExponentStrategyText, Topics.AudioBlocks);
                }
            }
        }
        else
        {
            if (_acmod > 1 && couplingBlocks > 0)
            {
                int code = F("Coupling Frame Exponent Strategy", 5, Explain.Frmcplexpstr, FrameStrategyText, Topics.AudioBlocks);
                for (int block = 0; block < 6; block++)
                {
                    _cplexpstr[block] = Tables.FrameExponentStrategies[code][block];
                }
            }

            for (int ch = 0; ch < _nfchans; ch++)
            {
                int code = FC("Frame Exponent Strategy", ch, 5, Explain.Frmchexpstr, FrameStrategyText, Topics.AudioBlocks);
                for (int block = 0; block < 6; block++)
                {
                    _chexpstr[block, ch] = Tables.FrameExponentStrategies[code][block];
                }
            }
        }

        if (_lfeon)
        {
            for (int block = 0; block < _blocks; block++)
            {
                _lfeexpstr[block] = F("LFE Exponent Strategy", 1, Explain.Lfeexpstr, v => v == 0 ? "reuse" : "D15", Topics.AudioBlocks);
            }
        }

        if (_strmtyp == 0)
        {
            bool convexpstre = _blocks == 6 || Flag("HasConverterExponentStrategies", Explain.Convexpstre);
            if (convexpstre)
            {
                for (int ch = 0; ch < _nfchans; ch++)
                {
                    FC("Converter Exponent Strategy", ch, 5, Explain.Convexpstr, FrameStrategyText);
                }
            }
        }

        Array.Clear(_chahtinu);
        _cplahtinu = false;
        _lfeahtinu = false;
        if (_ahte)
        {
            // Annex E 3.4.2: ncplregs, nchregs and nlferegs count the blocks with new exponents.
            int cplRegions = 0;
            for (int block = 0; block < 6; block++)
            {
                cplRegions += _cplstre[block] || _cplexpstr[block] != 0 ? 1 : 0;
            }

            if (couplingBlocks == 6 && cplRegions == 1)
            {
                _cplahtinu = Flag("IsCouplingAht", Explain.Cplahtinu);
            }

            for (int ch = 0; ch < _nfchans; ch++)
            {
                int regions = 0;
                for (int block = 0; block < 6; block++)
                {
                    regions += _chexpstr[block, ch] != 0 ? 1 : 0;
                }

                if (regions == 1)
                {
                    _chahtinu[ch] = FC("IsAht", ch, 1, Explain.Chahtinu, BooleanValue) != 0;
                }
            }

            if (_lfeon)
            {
                int regions = 0;
                for (int block = 0; block < 6; block++)
                {
                    regions += _lfeexpstr[block] != 0 ? 1 : 0;
                }

                if (regions == 1)
                {
                    _lfeahtinu = Flag("IsLfeAht", Explain.Lfeahtinu);
                }
            }
        }

        if (_snroffststr == 0)
        {
            _frmcsnroffst = F("Frame Coarse SNR Offset", 6, Explain.Frmcsnroffst, CoarseSnr, Topics.AudioBlocks);
            _frmfsnroffst = F("Frame Fine SNR Offset", 4, Explain.Frmfsnroffst, FineSnr, Topics.AudioBlocks);
        }

        if (transproce)
        {
            for (int ch = 0; ch < _nfchans; ch++)
            {
                if (FC("IsInTransientProcessing", ch, 1, Explain.Chintransproc, BooleanValue) != 0)
                {
                    F("Transient Location", 10, Explain.Transprocloc, v => NodeValue.Integer(v * 4, "samples"));
                    F("Transient Processing Length", 8, Explain.Transproclen, v => NodeValue.Integer(v, "samples"));
                }
            }
        }

        if (spxattene)
        {
            for (int ch = 0; ch < _nfchans; ch++)
            {
                if (FC("IsInSpectralExtensionAttenuation", ch, 1, Explain.Chinspxatten, BooleanValue, Topics.SpectralExtension) != 0)
                {
                    F("Spectral Extension Attenuation Code", 5, Explain.Spxattencod, topic: Topics.SpectralExtension);
                }
            }
        }

        if (_blocks > 1 && Flag("HasBlockStartInfo", Explain.Blkstrtinfoe))
        {
            // Annex E 2.3.2.27: (numblks - 1) * (4 + ceiling(log2(words_per_frame))) bits.
            int bits = (_blocks - 1) * (4 + (int)Math.Ceiling(Math.Log2(_frameWords)));
            int start = _b.Position;
            _b.Skip(bits);
            _t?.Field("Block Start Info", start, _b.Position, NodeValue.Integer(bits, "bits"), Explain.Blkstrtinfo);
        }

        Array.Fill(_firstSpxCoords, true);
        Array.Fill(_firstCplCoords, true);
        _firstCplLeak = true;
        Close();
    }

    private void Open(string name, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        _t?.Open(name, _b.Position, description, topic, kind);

    private void Close(NodeValue? value = null) => _t?.Close(_b.Position, value);

    private int F(string name, int width, string description, Func<int, NodeValue>? value = null, string? topic = null)
    {
        int start = _b.Position;
        int read = _b.Read(width);
        _lastName = name;
        _lastChannel = -1;
        _lastStart = start;
        _lastEnd = _b.Position;
        _t?.Field(name, start, _b.Position, value is null ? NodeValue.Integer(read) : value(read), description, topic);
        return read;
    }

    private bool Flag(string name, string description, string? topic = null) => F(name, 1, description, BooleanValue, topic) != 0;

    private void ReservedIf(bool condition)
    {
        if (condition && !Counted(Issue.ReservedValue))
        {
            Report(Issue.ReservedValue, LastField(), _lastStart, _lastEnd, string.Empty);
        }
    }

    // An issue at a field; only the analysis reports, so each is reported once per frame read.
    private void Report(Issue issue, string field, int start, int end, string detail)
    {
        if (!_b.IsBuildingNodes && !Counted(issue))
        {
            _issue?.Invoke(issue, field, start, end, detail);
        }
    }

    private static NodeValue BooleanValue(int value) => NodeValue.Boolean(value != 0);

    private static NodeValue Hex4(int value) => NodeValue.Hex(value, 4);

    private static NodeValue SampleRateCode(int value) => value < 3 ? NodeValue.Integer(SyncFrame.Rates[value], "Hz") : "3 (reserved)";

    private static NodeValue FrameSize(int value, int fscod) =>
        value < 38 && fscod < 3 ? $"{value} ({Tables.NominalBitrates[value >> 1]} kbps, {SyncFrame.WordsOf(fscod, value) * 2} bytes)" : $"{value} (invalid)";

    private static NodeValue ExponentStrategyText(int value) => Ac3Names.ExponentStrategy(value);

    private static NodeValue FrameStrategyText(int value)
    {
        byte[] row = Tables.FrameExponentStrategies[value];
        return $"{value} ({string.Join(", ", row.Select(s => s == 0 ? "R" : Ac3Names.ExponentStrategy(s)))})";
    }

    private static NodeValue CoarseSnr(int value) => NodeValue.Integer(value);

    private static NodeValue FineSnr(int value) => NodeValue.Integer(value);

    private static NodeValue ChannelMapText(int map)
    {
        var names = new List<string>();
        for (int bit = 0; bit < 16; bit++)
        {
            if ((map & (0x8000 >> bit)) != 0)
            {
                names.Add(Ac3Names.ChannelMapLocation(bit));
            }
        }

        return $"0x{map:X4} ({(names.Count == 0 ? "none" : string.Join(", ", names))})";
    }
}
