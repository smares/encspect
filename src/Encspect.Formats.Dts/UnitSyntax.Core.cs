using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Dts;

internal sealed partial class UnitSyntax
{
    // The tables of the audio coding header, reused by every header one syntax reads.
    private readonly int[] _subs = new int[8];
    private readonly int[] _vqsub = new int[8];
    private readonly int[] _joinx = new int[8];
    private readonly int[] _thuff = new int[8];
    private readonly int[] _shuff = new int[8];
    private readonly int[] _bhuff = new int[8];
    private readonly int[][] _sel = [new int[10], new int[10], new int[10], new int[10], new int[10], new int[10], new int[10], new int[10]];

    // The core frame at the start of the unit, in the core substream (5.2).
    private void ReadCoreSubstream()
    {
        // 14 bits of each 16-bit word are data; whole bytes of them are all the frame has (a partial one is padding in the file's last word).
        int available = _layout.Form.Is14Bit() ? _layout.CoreLength / 2 * 14 / 8 : _layout.CoreLength;
        ReadCoreFrame(0, false, available);
    }

    // A core frame whose first byte is at unit coordinate baseByte: the core substream (sync word 0x7FFE8001), or the core of an
    // asset in an extension substream, whose sync word is remapped to 0x02B09261 (7.4.1).
    private void ReadCoreFrame(int baseByte, bool inSubstream, int availablePacked)
    {
        int outer = _limit;
        try
        {
            ReadCoreFrameWithin(baseByte, inSubstream, availablePacked);
        }
        finally
        {
            _limit = outer;
        }
    }

    private void ReadCoreFrameWithin(int baseByte, bool inSubstream, int availablePacked)
    {
        int baseBit = baseByte * 8;
        _pos = baseBit;
        CoreHeader header;
        try
        {
            header = HeaderAt(baseByte);
        }
        catch (EndOfStreamException)
        {
            // The data ends inside the 12 bytes every core frame starts with.
            string cutName = inSubstream ? "Core" : "Core Substream";
            int cutEnd = baseBit + (availablePacked * 8);
            _tree?.Open(cutName, baseBit, inSubstream ? Explain.SubstreamCore : Explain.CoreSubstream, Topics.CoreFrames);
            _tree?.Close(cutEnd, NodeValue.Integer(availablePacked, "bytes"));
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, cutName, baseBit, cutEnd, "the core frame ends inside its header");
            }

            return;
        }

        int declared = header.PackedLength;
        int length = Math.Min(declared, availablePacked);
        int endBit = baseBit + (length * 8);
        _limit = Math.Min(_limit, endBit);
        int depth = _tree?.Depth ?? 0;
        _tree?.Open(inSubstream ? "Core" : "Core Substream", baseBit, inSubstream ? Explain.SubstreamCore : Explain.CoreSubstream, Topics.CoreFrames);
        UnitFacts facts = Facts;
        if (!inSubstream)
        {
            facts.HasCore = true;
            facts.Core = header;
            facts.CorePackedLength = declared;
        }

        int headerEnd = baseBit;
        FoundExtension? extension = null;
        try
        {
            if (inSubstream && PeekWord(baseByte) != SubstreamCoreSync)
            {
                // A core whose sync word is not there is not read further.
                int start = _pos;
                uint word = Read(32);
                _tree?.Field("Sync Word", start, _pos, NodeValue.Hex(word, 8), Explain.SubstreamCoreSyncWord, Topics.CoreFrames);
                Report(Issue.ComponentSync, "Sync Word", start, _pos, $"the core component does not start with its sync word 0x{SubstreamCoreSync:X8}");
                _tree?.CloseTo(depth + 1, Here);
                _tree?.Close(endBit, NodeValue.Integer(declared, "bytes"));
                return;
            }

            ReadSync(baseBit, inSubstream);
            ReadFrameHeader(header, inSubstream);
            ReadAudioHeader(true, header);
            headerEnd = _pos;
            if (!inSubstream)
            {
                facts.HasAudioHeader = true;
                if (_isBuilding)
                {
                    extension = _layout.Extension;
                }
                else
                {
                    extension = FindExtension(header, baseByte, length, declared);
                    _layout.Extension = extension;
                }
            }
        }
        catch (EndOfStreamException)
        {
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, inSubstream ? "Core" : "Core Substream", baseBit, endBit, "the core frame ends inside its header");
            }
        }

        _tree?.CloseTo(depth + 1, Here);
        if (!inSubstream && headerEnd > baseBit)
        {
            AddCoreData(headerEnd, extension is { } found ? found.Byte * 8 : endBit);
            if (extension is { } present)
            {
                ReadExtension(header, present, endBit);
            }
            else if (header.HasExtendedAudio && header.ExtensionAudioId is 0 or 2 or 6 && !IsIncomplete && !_isBuilding)
            {
                ReportGroup(Issue.ExtensionMissing, "Core Substream", baseBit, endBit, HowFound(header));
            }
        }

        _tree?.Close(endBit, inSubstream ? NodeValue.Integer(declared, "bytes") : Summary(header));
    }

    private CoreHeader HeaderAt(int baseByte)
    {
        ReadOnlySpan<byte> bytes = _view.Take(baseByte, CoreHeader.Bytes);
        UInt128 raw = 0;
        foreach (byte b in bytes)
        {
            raw = (raw << 8) | b;
        }

        return new CoreHeader(raw);
    }

    private NodeValue Summary(CoreHeader header) =>
        $"{Tables.ArrangementShort(header.Amode)}{(header.Lff > 0 && header.Lff < 3 ? " + LFE" : string.Empty)}, {header.SampleRate} Hz";

    private void ReadSync(int baseBit, bool inSubstream)
    {
        _tree?.Open("Synchronization", _pos, Explain.Synchronization, Topics.CoreFrames);
        int start = _pos;
        uint word = Read(32);
        if (_tree is not null)
        {
            Node sync = _tree.Field("Sync Word", start, _pos, NodeValue.Hex(word, 8), inSubstream ? Explain.SubstreamCoreSyncWord : Explain.SyncWord, Topics.CoreFrames);
            Locator locator = _tree.Locator;
            sync.Add(locator.Field("Word Size", start, _pos, NodeValue.Integer(_layout.Form.WordBits(), "bits"), Explain.WordSize, Topics.CoreBitstreamForms));
            sync.Add(locator.Field("IsBigEndian", start, _pos, NodeValue.Boolean(!_layout.Form.IsLittleEndian()), Explain.IsBigEndian, Topics.CoreBitstreamForms));
        }

        _tree?.Close(Here);
    }

    // 5.4.2, Table 5-1: the fields after the sync word.
    private void ReadFrameHeader(CoreHeader header, bool inSubstream)
    {
        UnitFacts facts = Facts;
        _tree?.Open("Frame Header", _pos, Explain.FrameHeader, Topics.CoreFrames);
        int start = _pos;
        int ftype = (int)Read(1);
        _tree?.Field("IsTerminationFrame", start, _pos, NodeValue.Boolean(ftype == 0), Explain.Ftype, Topics.CoreFrames);
        start = _pos;
        int deficit = Num("Deficit Sample Count", 5, Explain.Short, Topics.CoreFrames);
        if ((ftype == 1) != (deficit == 31))
        {
            Report(Issue.FrameTypeInconsistent, "Deficit Sample Count", start, _pos, ftype == 1 ? $"FTYPE says a normal frame but SHORT is {deficit}" : "FTYPE says a termination frame but SHORT is 31");
        }

        start = _pos;
        bool hasCrc = Flag("HasCrc", Explain.Cpf, Topics.Crc);
        if (hasCrc)
        {
            Report(Issue.CrcPresent, "HasCrc", start, _pos, string.Empty);
        }

        start = _pos;
        int blocks = Num("Sample Blocks", 7, Explain.Nblks, Topics.CoreFrames, "blocks", 1) + 1;
        if (ftype == 1 && blocks is not (8 or 16 or 32 or 64 or 128))
        {
            Report(Issue.BlockCount, "Sample Blocks", start, _pos, $"{blocks} blocks of 32 samples");
        }

        start = _pos;
        Num("Frame Size", 14, Explain.Fsize, Topics.CoreFrames, "bytes", 1);
        start = _pos;
        int amode = Num("Audio Channel Arrangement", 6, Explain.Amode, v => $"{v}: {Tables.Arrangement(v)}", Topics.DtsChannelArrangements);
        if (amode >= 16)
        {
            Report(Issue.UserDefinedArrangement, "Audio Channel Arrangement", start, _pos, $"AMODE {amode}");
        }

        start = _pos;
        Num("Sampling Frequency", 4, Explain.Sfreq, v => Tables.SampleRate(v) is var rate and > 0 ? NodeValue.Integer(rate, "Hz") : $"{v} (invalid)", Topics.CoreFrames);
        start = _pos;
        int rateCode = Num("Bit Rate", 5, Explain.Rate, RateValue, Topics.BitrateModes);
        if (rateCode == Tables.RateLossless)
        {
            Report(Issue.RateLossless, "Bit Rate", start, _pos, string.Empty);
        }
        else if (rateCode is > 24 and not Tables.RateOpen)
        {
            Report(Issue.RateCode, "Bit Rate", start, _pos, rateCode switch
            {
                Tables.RateVariable => "30, which FFmpeg and libdca read as a variable rate",
                _ => $"{rateCode}, which FFmpeg and libdca read as {Tables.DecodersRateKbps(rateCode)} kbit/s",
            });
        }

        CheckRate(header, rateCode, start);
        start = _pos;
        int fixedBit = Num("Reserved", 1, Explain.FixedBit, Topics.CoreFrames);
        if (fixedBit != 0)
        {
            Report(Issue.ReservedValue, "Reserved", start, _pos, "FixedBit is 1, which 5.4.2 says is always 0");
        }

        Flag("HasDynamicRange", Explain.Dynf, Topics.DynamicRange);
        Flag("HasTimeStamp", Explain.Timef, Topics.CoreFrames);
        Flag("HasAuxiliaryData", Explain.Auxf, Topics.CoreFrames);
        Flag("IsHdcd", Explain.Hdcd, Topics.CoreFrames);
        start = _pos;
        int extensionId = Num("Extension Audio Type", 3, Explain.ExtAudioId, v => $"{v}: {Tables.ExtensionAudio(v)}", Topics.CoreExtensions);
        start = _pos;
        bool hasExtension = Flag("HasExtendedAudio", Explain.ExtAudio, Topics.CoreExtensions);
        if (hasExtension && extensionId is not (0 or 2 or 6))
        {
            Report(Issue.ReservedValue, "Extension Audio Type", start - 3, start, $"EXT_AUDIO_ID {extensionId} is reserved (Table 5-11)");
        }

        Flag("HasSyncPerSubsubframe", Explain.Aspf, Topics.CoreFrames);
        start = _pos;
        int lff = Num("LFE Channel", 2, Explain.Lff, v => v switch
        {
            0 => "0 (none)",
            1 => "1 (present, interpolation factor 128)",
            2 => "2 (present, interpolation factor 64)",
            _ => "3 (invalid)",
        }, Topics.Lfe);
        if (lff == 3)
        {
            Report(Issue.ReservedValue, "LFE Channel", start, _pos, "LFF 3 is invalid (Table 5-14)");
        }

        Flag("HasPredictorHistory", Explain.Hflag, Topics.CoreFrames);
        if (hasCrc)
        {
            Crc("Header CRC", Explain.Hcrc, 2, "not verified: 5.4.2 says the CRC value test shall not be applied");
        }

        Flag("HasPerfectReconstructionFilter", Explain.Filts, Topics.FilterBank);
        start = _pos;
        int vernum = Num("Encoder Revision", 4, Explain.Vernum, Topics.CoreFrames);
        if (vernum > 7)
        {
            Report(Issue.RevisionIncompatible, "Encoder Revision", start, _pos, $"VERNUM {vernum}");
        }

        Num("Copy History", 2, Explain.Chist, Topics.CoreFrames);
        start = _pos;
        int pcmr = (int)Read(3);
        if (_tree is not null)
        {
            Node resolution = _tree.Field("Source PCM Resolution", start, _pos, Tables.SourceResolution(pcmr) is var bits and > 0 ? NodeValue.Integer(bits, "bits") : $"{pcmr} (invalid)", Explain.Pcmr, Topics.CoreFrames);
            resolution.Add(_tree.Locator.Field("IsEs", start, _pos, NodeValue.Boolean(Tables.IsEs(pcmr)), Explain.Es, Topics.CoreFrames));
        }

        if (Tables.SourceResolution(pcmr) == 0)
        {
            Report(Issue.ReservedValue, "Source PCM Resolution", start, _pos, $"PCMR {pcmr} is invalid (Table 5-17)");
        }

        bool sumf = Flag("IsFrontSumDifference", Explain.Sumf, Topics.CoreFrames);
        bool sums = Flag("IsSurroundSumDifference", Explain.Sums, Topics.CoreFrames);
        start = _pos;
        int dialnorm = (int)Read(4);
        int? gain = Tables.DialogNormalization(vernum, dialnorm);
        if (gain is { } dng)
        {
            _tree?.Field("Dialogue Normalization", start, _pos, NodeValue.Integer(dng, "dB"), Explain.Dialnorm, Topics.DialogueNormalization);
        }
        else
        {
            _tree?.Field("Unspecified", start, _pos, NodeValue.Integer(dialnorm), Explain.Unspec, Topics.CoreFrames);
        }

        _tree?.Close(Here);
        if (!inSubstream)
        {
            facts.EncoderRevision = vernum;
            facts.Pcmr = pcmr;
            facts.IsSumDifferenceFront = sumf;
            facts.IsSumDifferenceSurround = sums;
            facts.DialogNormalization = gain;
        }

    }

    private static NodeValue RateValue(int code) => code switch
    {
        22 => NodeValue.Decimal(1411.2, 1, "kbps"),
        <= 24 => NodeValue.Integer(Tables.RateTenths(code) / 10, "kbps"),
        Tables.RateOpen => "open",
        Tables.RateVariable => "30 (variable, not in Table 5-7)",
        Tables.RateLossless => "31 (lossless, not in Table 5-7)",
        _ => string.Create(CultureInfo.InvariantCulture, $"{code} (invalid)"),
    };

    // 5.4.2: the targeted rate may be at least the actual bit rate. A frame is rounded up to 32 bits, so four bytes more are no excess. The extensions of the core sit inside FSIZE and are no part of the core's rate.
    private void CheckRate(CoreHeader header, int rateCode, int start)
    {
        if (rateCode > 24 || header.HasExtendedAudio)
        {
            return;
        }

        long target = Tables.RateTenths(rateCode);
        long bytes = header.PackedLength;
        int samples = header.Samples;
        int rate = header.SampleRate;

        // Bytes the targeted rate gives a frame, in units of 100 bit/s * samples / rate.
        double allowed = target * 100.0 / 8 * samples / rate;
        if (bytes > allowed + 4)
        {
            Report(Issue.RateExceeded, "Bit Rate", start, _pos, $"{bytes} bytes where {allowed:F0} fit the targeted {target / 10.0:0.#} kbit/s");
        }
    }
}
