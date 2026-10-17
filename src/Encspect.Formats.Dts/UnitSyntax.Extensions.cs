using Encspect.Core;

namespace Encspect.Formats.Dts;

internal sealed partial class UnitSyntax
{
    private const uint XchSync = 0x5A5A5A5A;
    private const uint X96Sync = 0x1D95F262;
    private const uint XxchSync = 0x47004A03;

    // 6.4.2 (XCh), 6.2.4.2 (X96): the sync word's distance from the end of the frame must equal the size the extension states, since the
    // word may also occur inside the audio data, and the word lies on a 32-bit boundary. FFmpeg searches backwards from the end
    // of the frame, so a word inside the data does not hide the true one (dca_core.c, parse_optional_info); so does this. XXCH
    // states no size to the frame's end, so its header's CRC16 is the test, as FFmpeg's.
    private FoundExtension? FindExtension(CoreHeader header, int baseByte, int availablePacked, int declared)
    {
        if (!header.HasExtendedAudio || header.ExtensionAudioId is not (0 or 2 or 6))
        {
            return null;
        }

        int firstWord = (((_pos - (baseByte * 8)) + 7) / 8 + 3) / 4;
        int lastWord = (Math.Min(declared, availablePacked) / 4) - 1;
        uint wanted = header.ExtensionAudioId switch { 0 => XchSync, 2 => X96Sync, _ => XxchSync };
        FoundExtension? damaged = null;
        for (int word = lastWord; word >= firstWord; word--)
        {
            int at = baseByte + (word * 4);
            if (PeekWord(at) != wanted || PeekWord(at + 4) is not { } next)
            {
                continue;
            }

            int distance = declared - (word * 4);
            switch (header.ExtensionAudioId)
            {
                case 0:
                    int size = (int)(next >> 22) + 1;
                    if (size >= 96 && (size == distance || size - 1 == distance))
                    {
                        return new FoundExtension(CoreExtension.Xch, at, size != distance);
                    }

                    break;
                case 2:
                    size = (int)(next >> 20) + 1;
                    if (size >= 96 && size == distance)
                    {
                        return new FoundExtension(CoreExtension.X96, at, false);
                    }

                    break;
                default:
                    int headerSize = (int)(next >> 26) + 1;
                    if (headerSize >= 11 && headerSize <= distance)
                    {
                        if (Crc16.Compute(_view.Peek(at + 4, headerSize - 4)) == 0)
                        {
                            return new FoundExtension(CoreExtension.Xxch, at, false);
                        }

                        // A header whose CRC16 fails is still the extension's where its channel set sizes lead to the end of the frame.
                        if (damaged is null && XxchSizesReachEnd(at, headerSize, distance))
                        {
                            damaged = new FoundExtension(CoreExtension.Xxch, at, false);
                        }
                    }

                    break;
            }
        }

        return damaged;
    }

    // 6.5.2 Table 6-21: the header and the channel sets' sizes (14 bits each, after the 14 bits that give the header size, the
    // channel set CRC flag, the speaker mask width and the number of sets) add up to the distance, less the up to three bytes that
    // align the end of the frame.
    private bool XxchSizesReachEnd(int at, int headerSize, int distance)
    {
        int bit = (at + 4) * 8 + 12;
        int sets = PeekBits(bit, 2) + 1;
        if (sets < 1)
        {
            return false;
        }

        long total = headerSize;
        for (int i = 0; i < sets; i++)
        {
            int size = PeekBits(bit + 2 + (i * 14), 14);
            if (size < 0)
            {
                return false;
            }

            total += size + 1;
        }

        return total <= distance && distance - total <= 3;
    }

    // Up to 24 bits at a bit position of the unit, or -1 where the bytes are not there.
    private int PeekBits(int bit, int width)
    {
        int value = 0;
        for (int i = 0; i < width; i++)
        {
            int b = _view.PeekByte((bit + i) >> 3);
            if (b < 0)
            {
                return -1;
            }

            value = (value << 1) | ((b >> (7 - ((bit + i) & 7))) & 1);
        }

        return value;
    }

    private uint? PeekWord(int index)
    {
        int a = _view.PeekByte(index);
        int b = _view.PeekByte(index + 1);
        int c = _view.PeekByte(index + 2);
        int d = _view.PeekByte(index + 3);
        return (a | b | c | d) < 0 ? null : (uint)((a << 24) | (b << 16) | (c << 8) | d);
    }

    private static string HowFound(CoreHeader header) => header.ExtensionAudioId switch
    {
        0 => "no XCh sync word lies at the distance from the end of the frame that its size gives (6.4.2)",
        2 => "no X96 sync word lies at the distance from the end of the frame that its size gives (6.2.4.2)",
        _ => "no XXCH sync word lies in the frame with a header size that fits and either a CRC16 that holds or channel set sizes that lead to the end of the frame (6.5.2)",
    };

    private string ExtensionName(CoreHeader header) => header.ExtensionAudioId switch { 0 => "XCh", 2 => "X96", _ => "XXCH" };

    // The core's data after its headers: the subframes, the optional information, up to the extension or the end of the frame.
    private void AddCoreData(int headerEnd, int dataEnd)
    {
        if (_tree is null || dataEnd <= headerEnd)
        {
            return;
        }

        _tree.Field("Core Audio Data", headerEnd, dataEnd, NodeValue.Integer((dataEnd - headerEnd + 7) / 8, "bytes"), Explain.CoreAudioData, Topics.CoreFrames);
    }

    private void ReadExtension(CoreHeader header, FoundExtension found, int endBit)
    {
        int outer = _limit;
        try
        {
            ReadExtensionWithin(header, found, endBit);
        }
        finally
        {
            _limit = outer;
        }
    }

    private void ReadExtensionWithin(CoreHeader header, FoundExtension found, int endBit)
    {
        int syncBit = found.Byte * 8;
        _limit = Math.Min(_limit, endBit);
        _pos = syncBit;
        int depth = _tree?.Depth ?? 0;
        Facts.Extension = found.Kind;
        string name = found.Kind switch { CoreExtension.Xch => "XCh Extension", CoreExtension.X96 => "X96 Extension", _ => "XXCH Extension" };
        _tree?.Open(name, syncBit, found.Kind switch { CoreExtension.Xch => Explain.Xch, CoreExtension.X96 => Explain.X96, _ => Explain.Xxch }, Topics.CoreExtensions);
        try
        {
            switch (found.Kind)
            {
                case CoreExtension.Xch:
                    ReadXch(header, found, endBit);
                    break;
                case CoreExtension.X96:
                    ReadX96(header, endBit);
                    break;
                default:
                    ReadXxch(found, endBit);
                    break;
            }
        }
        catch (EndOfStreamException)
        {
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, name, syncBit, endBit, $"the frame ends inside the {ExtensionName(header)} extension's header");
            }
        }

        _tree?.CloseTo(depth + 1, Here);
        _tree?.Close(endBit, NodeValue.Integer((endBit - syncBit) / 8, "bytes"));
    }

    // 6.4.2 Table 6-17, 6.4.3 Table 6-18.
    private void ReadXch(CoreHeader header, FoundExtension found, int endBit)
    {
        Hex("Sync Word", 32, Explain.XchSync, Topics.CoreExtensions);
        int start = _pos;
        int fsize = (int)Read(10);
        _tree?.Field("Extension Frame Size", start, _pos, NodeValue.Integer(fsize + 1, "bytes"), Explain.XchFsize, Topics.CoreExtensions);
        if (found.IsLegacySize)
        {
            Report(Issue.ExtensionSizeLegacy, "Extension Frame Size", start, _pos, $"XChFSIZE is {fsize}, the distance to the end of the frame, where 6.4.2 gives XChFSIZE + 1");
        }

        Num("Extension Channel Arrangement", 4, Explain.XchAmode, Topics.CoreExtensions);
        ReadAudioHeader(false, header);
        AddCoreData(_pos, endBit, "Extension Audio Data", Explain.XchData);
    }

    private void AddCoreData(int start, int end, string name, string description)
    {
        if (_tree is not null && end > start)
        {
            _tree.Field(name, start, end, NodeValue.Integer((end - start + 7) / 8, "bytes"), description, Topics.CoreExtensions);
        }
    }

    // 6.2.4.2 Table 6-1, 6.2.4.4 Table 6-4.
    private void ReadX96(CoreHeader header, int endBit)
    {
        Hex("Sync Word", 32, Explain.X96Sync, Topics.CoreExtensions);
        Num("Extension Frame Size", 12, Explain.X96Fsize, Topics.CoreExtensions, "bytes", 1);
        int start = _pos;
        int revision = Num("Revision Number", 4, Explain.X96Revno, Topics.CoreExtensions);
        if (revision == 0)
        {
            Report(Issue.ReservedValue, "Revision Number", start, _pos, "REVNO 0 is reserved (Table 6-2)");
        }
        else if (revision > 8)
        {
            Report(Issue.RevisionIncompatible, "Revision Number", start, _pos, $"X96 REVNO {revision}");
        }

        _tree?.Open("Channel Set Header", _pos, Explain.X96ChannelSetHeader, Topics.CoreExtensions);
        bool highResolution = Flag("IsHighResolution", Explain.X96HighRes, Topics.CoreExtensions);
        if (revision < 8)
        {
            start = _pos;
            int first = Num("First Encoded Subband", 5, Explain.X96Sbs, Topics.CoreExtensions);
            if (first > 27)
            {
                Report(Issue.ReservedValue, "First Encoded Subband", start, _pos, $"nSBS96 is {first}, above the 27 that 6.2.4.4 allows");
            }
        }

        int channels = Math.Max(1, Facts.PrimaryChannels);
        PerChannel("Last Encoded Subband", 6, Explain.X96Sbe, channels, _subs, v => NodeValue.Integer(v, "subband"), Topics.CoreExtensions);
        PerChannel("Joint Intensity Coding Index", 3, Explain.Joinx, channels, _joinx, v => v == 0 ? "0 (disabled)" : $"{v} (copies channel {v})", Topics.CoreExtensions);
        PerChannel("Scale Factor Code Book", 3, Explain.X96Shuff, channels, _shuff, v => v <= 4 ? $"{v}: S{(char)('A' + v)}129, 6-bit table" : v == 5 ? "5: linear, 6-bit table" : $"{v} (invalid)", Topics.CoreExtensions,
            (_, v) => v > 5 ? $"SHUFF96 {v} is invalid (Table 6-6)" : null, Issue.ReservedValue);
        PerChannel("Bit Allocation Quantizer Select", 3, Explain.X96Bhuff, channels, _bhuff, v => v <= 6 ? $"{v}: {(char)('A' + v)}{(highResolution ? 33 : 17)}" : $"7: linear, {(highResolution ? 4 : 3)} bits", Topics.CoreExtensions);
        _tree?.Open("Quantization Index Codebook Select", _pos, Explain.X96Sel, Topics.CoreExtensions);
        for (int n = 0; n < (highResolution ? 10 : 6); n++)
        {
            _tree?.Open($"ABITS {n + 2}", _pos, Explain.X96Sel, Topics.CoreExtensions);
            for (int ch = 0; ch < channels; ch++)
            {
                int at = _pos;
                int value = (int)Read(SelBits[n]);
                _tree?.Field($"Channel {ch + 1}", at, _pos, NodeValue.Integer(value), Explain.X96Sel, Topics.CoreExtensions);
            }

            _tree?.Close(Here);
        }

        _tree?.Close(Here);
        if (header.HasCrc)
        {
            Crc("Audio Header CRC", Explain.Ahcrc96, 2, "not verified: 6.2.4.4 says this value will not be used");
        }

        _tree?.Close(Here);
        AddCoreData(_pos, endBit, "Extension Audio Data", Explain.X96Data);
    }

    // 6.5.2 Table 6-21, 6.5.3 Table 6-23: the frame header with its CRC, and the first fields of each channel set's header.
    private void ReadXxch(FoundExtension found, int endBit, bool readSync = true)
    {
        int syncByte = found.Byte;
        if (readSync)
        {
            Hex("Sync Word", 32, Explain.XxchSync, Topics.CoreExtensions);
        }

        int headerSize = HeaderSize(6, Explain.XxchHeaderSize, Topics.CoreExtensions);
        bool setCrc = Flag("HasChannelSetCrc", Explain.XxchSetCrc, Topics.Crc);
        int maskStart = _pos;
        int maskBits = Num("Speaker Mask Bits", 5, Explain.XxchMaskBits, Topics.CoreExtensions, "bits", 1) + 1;
        if (maskBits <= 6)
        {
            // Table 6-23 reads each channel set's mask with nuBits4SpkrMaskXXCh - 6 bits, and FFmpeg refuses a width up to 6 (dca_core.c).
            Report(Issue.ReservedValue, "Speaker Mask Bits", maskStart, _pos, $"the mask has {maskBits} bits, but the channel set masks are read with 6 bits fewer (Table 6-23), so it needs more than 6; the channel sets are not read");
            _pos = endBit;
            return;
        }

        int sets = Num("Channel Sets", 2, Explain.XxchSets, Topics.CoreExtensions, "channel sets", 1) + 1;
        var sizes = new int[sets];
        _tree?.Open("Channel Set Sizes", _pos, Explain.XxchSetSizes, Topics.CoreExtensions);
        for (int i = 0; i < sets; i++)
        {
            int start = _pos;
            sizes[i] = (int)Read(14) + 1;
            _tree?.Field($"Channel Set {i + 1}", start, _pos, NodeValue.Integer(sizes[i], "bytes"), Explain.XxchSetSizes, Topics.CoreExtensions);
        }

        _tree?.Close(Here);
        uint coreMask = Hex("Core Speaker Activity Mask", maskBits, Explain.XxchCoreMask, Topics.DtsChannelArrangements);
        Facts.XxchCoreMask = (int)coreMask;
        ReadHeaderCrc(syncByte, headerSize, "XXCH", Explain.XxchCrc, "The XXCH frame header's CRC16 (6.5.2) fails");

        int setStart = Math.Max(syncByte + headerSize, Aligned(_pos) / 8);
        uint mask = 0;
        int channels = 0;
        for (int i = 0; i < sets && setStart < endBit / 8; i++)
        {
            int setEnd = Math.Min(setStart + sizes[i], endBit / 8);
            int outerSet = _limit;
            _limit = Math.Min(_limit, setEnd * 8);
            _pos = setStart * 8;
            _tree?.Open($"Channel Set {i + 1}", _pos, Explain.XxchSet, Topics.CoreExtensions);
            int sizeStart = _pos;
            int size = Num("Header Size", 7, Explain.XxchSetHeaderSize, Topics.CoreExtensions, "bytes", 1) + 1;
            int sizeEnd = _pos;
            int count = Num("Channels", 3, Explain.XxchSetChannels, Topics.CoreExtensions, "channels", 1) + 1;
            int start = _pos;
            uint layout = Read(maskBits - 6) << 6;
            _tree?.Field("Speaker Layout Mask", start, _pos, NodeValue.Hex(layout, 8), Explain.XxchSetMask, Topics.DtsChannelArrangements);
            mask |= layout;
            channels += count;
            int headerEnd = (setStart + size) * 8;
            if (_pos > headerEnd - (setCrc ? 16 : 0))
            {
                Report(Issue.SubstreamOverrun, "Header Size", sizeStart, sizeEnd, $"channel set {i + 1}'s header fields end at byte {((_pos + 7) / 8) - setStart}, past the {size} bytes its size field gives");
            }
            else if (setCrc)
            {
                int crcVerdict = CrcVerdict(setStart, size);
                _pos = headerEnd - 16;
                Crc("Header CRC", Explain.XxchSetCrcField, crcVerdict, "not checked");
                if (crcVerdict == 0)
                {
                    Report(Issue.CrcMismatch, "Header CRC", _pos - 16, _pos, $"The CRC16 of XXCH channel set {i + 1}'s header (6.5.3) fails");
                }
            }
            else
            {
                _pos = headerEnd;
            }

            AddCoreData(_pos, setEnd * 8, "Channel Set Data", Explain.XxchSetData);
            _tree?.Close(setEnd * 8, NodeValue.Integer(setEnd - setStart, "bytes"));
            _limit = outerSet;
            setStart += sizes[i];
        }

        Facts.XxchChannels = channels;
        Facts.XxchSpeakerMask = mask;
        _pos = endBit;
    }
}
