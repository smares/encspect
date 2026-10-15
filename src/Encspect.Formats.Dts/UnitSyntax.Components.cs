using Encspect.Core;

namespace Encspect.Formats.Dts;

internal sealed partial class UnitSyntax
{
    private const uint SubstreamCoreSync = 0x02B09261;
    private const uint XbrSync = 0x655E315E;
    private const uint LbrSync = 0x0A801921;
    private const uint XllSync = 0x41A29547;

    private static readonly (int Bit, string Name, uint Sync)[] ComponentOrder =
    [
        (Tables.ComponentCore, "Core", SubstreamCoreSync),
        (Tables.ComponentXbr, "XBR", XbrSync),
        (Tables.ComponentXxch, "XXCH", XxchSync),
        (Tables.ComponentX96, "X96", X96Sync),
        (Tables.ComponentLbr, "LBR", LbrSync),
        (Tables.ComponentXll, "XLL", XllSync),
    ];

    // The bytes of the unit from unit byte start on, up to length, for the syntax to read: the analysis fetches them from the file.
    private void EnsureBytes(int start, int length)
    {
        if (_isBuilding || _source is null || length <= 0 || _view.Has(start, length))
        {
            return;
        }

        int from = start & ~1;
        int to = Math.Min(_layout.Length, start + length + (start & 1) + 1) & ~(_layout.Form == StreamForm.LittleEndian16 ? 1 : 0);
        to = Math.Min(Math.Max(to, start + length), _layout.Length);
        if (to <= from)
        {
            return;
        }

        var file = new byte[to - from];
        int read = _source.Read(_layout.Offset + from, file);
        var packed = new byte[read];
        Forms.Unpack(file.AsSpan(0, read), _layout.Form, packed);
        _view.Add(from, packed, read);
    }

    // 7.5.3: the data of each asset of a substream, component by component in the order of Table 7-15, each with its sync word at the
    // place the sizes lead to.
    private void ReadAssetData(ExssRef substream, AssetFacts[] assets, int[] assetSizes, int headerSize)
    {
        int limit = substream.Start + Math.Min(substream.Length, substream.Available);
        int position = substream.Start + headerSize;
        long sum = headerSize;
        for (int i = 0; i < assets.Length; i++)
        {
            sum += assetSizes[i];
        }

        if (sum > substream.Length)
        {
            ReportGroup(Issue.SubstreamOverrun, "Asset Sizes", _assetSizesStart, _assetSizesEnd, $"the header and the assets' sizes add up to {sum} bytes, more than the substream's {substream.Length}");
        }

        for (int i = 0; i < assets.Length; i++)
        {
            AssetFacts asset = assets[i];
            int assetEnd = position + assetSizes[i];
            if (position >= limit)
            {
                break;
            }

            _pos = position * 8;
            _tree?.Open($"Asset {i + 1} Data", _pos, Explain.AssetData, Topics.ExtensionSubstream);
            int assetLimit = Math.Min(assetEnd, limit);
            int at = position;
            int used = asset.ComponentSizes.Values.Sum();
            foreach ((int bit, string name, uint sync) in ComponentOrder)
            {
                if (!asset.ComponentSizes.TryGetValue(bit, out int size))
                {
                    continue;
                }

                if (at >= assetLimit)
                {
                    break;
                }

                ReadComponent(bit, name, sync, at, Math.Min(at + size, assetLimit), asset);
                at += size;
            }

            if (used > assetSizes[i])
            {
                ReportGroup(Issue.SubstreamOverrun, $"Asset {i + 1} Data", position * 8, assetLimit * 8, $"asset {i + 1}'s components add up to {used} bytes, more than the {assetSizes[i]} its size gives");
            }

            _tree?.Close(assetLimit * 8, NodeValue.Integer(assetSizes[i], "bytes"));
            position = assetEnd;
        }
    }

    private void ReadComponent(int bit, string name, uint sync, int at, int end, AssetFacts asset)
    {
        int outer = _limit;
        try
        {
            ReadComponentWithin(bit, name, sync, at, end, asset);
        }
        finally
        {
            _limit = outer;
        }
    }

    private void ReadComponentWithin(int bit, string name, uint sync, int at, int end, AssetFacts asset)
    {
        int startBit = at * 8;
        int endBit = end * 8;
        _limit = Math.Min(_limit, endBit);
        _pos = startBit;
        int depth = _tree?.Depth ?? 0;
        bool isCore = bit == Tables.ComponentCore;
        EnsureBytes(at, Math.Min(end - at, 512));
        if (!isCore)
        {
            _tree?.Open(name, startBit, ComponentDescription(bit), Topics.ExtensionSubstream);
        }

        try
        {
            if (isCore)
            {
                ReadCoreComponent(at, end - at, asset);
            }
            else
            {
                if (!ReadComponentSync(name, sync))
                {
                    _pos = endBit;
                }
                else
                {
                    switch (bit)
                    {
                        case Tables.ComponentXbr:
                            ReadXbr(at, endBit);
                            break;
                        case Tables.ComponentXxch:
                            ReadXxch(new FoundExtension(CoreExtension.Xxch, at, false), endBit, false);
                            break;
                        case Tables.ComponentX96:
                            ReadX96Substream(at, endBit);
                            break;
                        case Tables.ComponentLbr:
                            ReadLbr(asset);
                            break;
                        default:
                            ReadXll(at, endBit, asset);
                            break;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, name, startBit, endBit, $"the data ends inside the {name} component's header");
            }
        }

        if (!isCore)
        {
            _tree?.CloseTo(depth + 1, Here);
            _tree?.Close(endBit, NodeValue.Integer(end - at, "bytes"));
        }
        else
        {
            _tree?.CloseTo(depth, Here);
        }
    }

    // The sync word of a component with a verdict under it: a component whose sync word is not there is not read further.
    private bool ReadComponentSync(string name, uint expected)
    {
        int start = _pos;
        uint word = Read(32);
        bool present = word == expected;
        if (_tree is not null)
        {
            Node sync = _tree.Field("Sync Word", start, _pos, NodeValue.Hex(word, 8), SyncDescription(name), Topics.ExtensionSubstream);
            sync.Add(new Node("Check", sync.Offset, sync.Length, present ? "present" : $"missing: the component should start with 0x{expected:X8}") { Description = Explain.SyncCheck, Ranges = sync.Ranges });
        }

        if (!present)
        {
            Report(Issue.ComponentSync, "Sync Word", start, _pos, $"the {name} component does not start with its sync word 0x{expected:X8}");
        }

        return present;
    }

    private void ReadCoreComponent(int at, int size, AssetFacts asset)
    {
        EnsureBytes(at, Math.Min(size, 256));
        try
        {
            asset.Core = HeaderAt(at);
        }
        catch (EndOfStreamException)
        {
            // ReadCoreFrame names the cut-off header.
        }

        ReadCoreFrame(at, true, size);
    }

    private static string ComponentDescription(int bit) => bit switch
    {
        Tables.ComponentXbr => Explain.Xbr,
        Tables.ComponentXxch => Explain.XxchComponent,
        Tables.ComponentX96 => Explain.X96Component,
        Tables.ComponentLbr => Explain.Lbr,
        _ => Explain.Xll,
    };

    private static string SyncDescription(string name) => name switch
    {
        "XBR" => Explain.XbrSync,
        "XXCH" => Explain.XxchSync,
        "X96" => Explain.X96Sync,
        "LBR" => Explain.LbrSync,
        _ => Explain.XllSync,
    };

    // The end of a header that states its size: reserved bits and the byte alignment up to the CRC16 in its last two bytes, and
    // that CRC with its verdict. Fields that run past the size leave the CRC unread.
    private void ReadHeaderCrc(int at, int headerSize, string name, string description, string mismatch)
    {
        int crcBit = (at + headerSize - 2) * 8;
        if (headerSize < 6 || _pos > crcBit)
        {
            Report(Issue.SubstreamOverrun, "Header Size", _headerSizeStart, _headerSizeEnd, $"the {name} header's fields end at byte {((_pos + 7) / 8) - at}, past the {headerSize} bytes its size field gives");
            return;
        }

        if (_pos < crcBit)
        {
            Skip("Reserved", crcBit - _pos, Explain.Reserved);
        }

        int verdict = CrcVerdict(at + 4, headerSize - 4);
        int crcStart = _pos;
        Crc("Header CRC", description, verdict, "not checked");
        if (verdict == 0)
        {
            Report(Issue.CrcMismatch, "Header CRC", crcStart, _pos, mismatch);
        }
    }

    // 6.3.5 Table 6-12 and 6.3.6 Table 6-13: the XBR frame header with its CRC and its channel sets' sub-headers.
    private void ReadXbr(int at, int endBit)
    {
        int headerSize = HeaderSize(6, Explain.XbrHeaderSize, Topics.ExtensionSubstream);
        int sets = Num("Channel Sets", 2, Explain.XbrSets, Topics.ExtensionSubstream, "channel sets", 1) + 1;
        _tree?.Open("Channel Set Sizes", _pos, Explain.XbrSetSizes, Topics.ExtensionSubstream);
        for (int i = 0; i < sets; i++)
        {
            Num($"Channel Set {i + 1}", 14, Explain.XbrSetSizes, Topics.ExtensionSubstream, "bytes", 1);
        }

        _tree?.Close(Here);
        Flag("UsesTransientModes", Explain.XbrTmode, Topics.ExtensionSubstream);
        for (int i = 0; i < sets; i++)
        {
            _tree?.Open($"Channel Set {i + 1} Sub-Header", _pos, Explain.XbrSubHeader, Topics.ExtensionSubstream);
            int channels = Num("Channels", 3, Explain.XbrChannels, Topics.ExtensionSubstream, "channels", 1) + 1;
            int bits = Num("Active Subband Bits", 2, Explain.XbrBits, v => NodeValue.Integer(v + 5, "bits"), Topics.ExtensionSubstream) + 5;
            _tree?.Open("Active Subbands", _pos, Explain.XbrBands, Topics.ExtensionSubstream);
            for (int channel = 0; channel < channels; channel++)
            {
                Num($"Channel {channel + 1}", bits, Explain.XbrBands, Topics.ExtensionSubstream, "subbands", 1);
            }

            _tree?.Close(Here);
            _tree?.Close(Here);
        }

        ReadHeaderCrc(at, headerSize, "XBR", Explain.XbrCrc, "The XBR frame header's CRC16 (6.3.5) fails");

        AddCoreData(_pos, endBit, "Channel Set Data", Explain.XbrData);
    }

    // 6.2.4.3 Table 6-3: the frame header of X96 in an extension substream.
    private void ReadX96Substream(int at, int endBit)
    {
        int headerSize = HeaderSize(6, Explain.X96HeaderSize, Topics.ExtensionSubstream);
        int start = _pos;
        int revision = Num("Revision Number", 4, Explain.X96Revno, Topics.ExtensionSubstream);
        if (revision == 0)
        {
            Report(Issue.ReservedValue, "Revision Number", start, _pos, "REVNO 0 is reserved (Table 6-2)");
        }

        Flag("HasChannelSetCrc", Explain.X96SetCrc, Topics.Crc);
        int sets = Num("Channel Sets", 2, Explain.X96Sets, Topics.ExtensionSubstream, "channel sets", 1) + 1;
        _tree?.Open("Channel Set Sizes", _pos, Explain.X96SetSizes, Topics.ExtensionSubstream);
        for (int i = 0; i < sets; i++)
        {
            Num($"Channel Set {i + 1}", 12, Explain.X96SetSizes, Topics.ExtensionSubstream, "bytes", 1);
        }

        _tree?.Close(Here);
        _tree?.Open("Channels per Channel Set", _pos, Explain.X96SetChannels, Topics.ExtensionSubstream);
        for (int i = 0; i < sets; i++)
        {
            Num($"Channel Set {i + 1}", 3, Explain.X96SetChannels, Topics.ExtensionSubstream, "channels", 1);
        }

        _tree?.Close(Here);
        ReadHeaderCrc(at, headerSize, "X96", Explain.X96Crc, "The X96 frame header's CRC16 (6.2.4.3) fails");

        AddCoreData(_pos, endBit, "Channel Set Data", Explain.X96Data);
    }

    // 9.3 Table 9-1: the LBR header, with the decoder initialization data where the header type has it.
    private void ReadLbr(AssetFacts asset)
    {
        int code = Num("Header Type", 8, Explain.LbrHeaderType, v => v switch
        {
            1 => "1 (sync word only)",
            2 => "2 (decoder initialization data follows)",
            _ => $"{v} (reserved)",
        }, Topics.ExtensionSubstream);
        if (code != 2)
        {
            return;
        }

        int start = _pos;
        int rateCode = (int)Read(8);
        int rate = rateCode switch { 0 => 8000, 1 => 16000, 2 => 32000, 5 => 22050, 6 => 44100, 10 => 12000, 11 => 24000, 12 => 48000, _ => 0 };
        _tree?.Field("Sample Rate", start, _pos, rate > 0 ? NodeValue.Integer(rate, "Hz") : $"{rateCode} (reserved)", Explain.LbrSampleRate, Topics.ExtensionSubstream);
        asset.LbrSampleRate = rate;
        start = _pos;
        int mask = (int)Le16();
        _tree?.Field("Speaker Mask", start, _pos, NodeValue.Hex(mask, 4), Explain.LbrSpeakerMask, Topics.DtsChannelArrangements);
        asset.LbrSpeakerMask = mask;
        start = _pos;
        int version = (int)Le16();
        _tree?.Field("Bit Stream Version", start, _pos, $"{version >> 8}.{version & 0xFF}", Explain.LbrVersion, Topics.ExtensionSubstream);
        start = _pos;
        int flags = (int)Read(8);
        asset.LbrFlags = flags;
        if (_tree is not null)
        {
            Node node = _tree.Field("Initialization Flags", start, _pos, NodeValue.Hex(flags, 2), Explain.LbrFlags, Topics.ExtensionSubstream);
            Locator locator = _tree.Locator;
            node.Add(locator.Field("Is24Bit", start, _pos, NodeValue.Boolean((flags & 1) != 0), Explain.LbrFlags, Topics.ExtensionSubstream));
            node.Add(locator.Field("HasLfe", start, _pos, NodeValue.Boolean((flags & 2) != 0), Explain.LbrFlags, Topics.Lfe));
            node.Add(locator.Field("HasStereoDownmix", start, _pos, NodeValue.Boolean((flags & 0x20) != 0), Explain.LbrFlags, Topics.ExtensionSubstream));
            node.Add(locator.Field("HasMultichannelDownmix", start, _pos, NodeValue.Boolean((flags & 0x40) != 0), Explain.LbrFlags, Topics.ExtensionSubstream));
        }

        start = _pos;
        int nibbles = (int)Read(8);
        int original = (int)Le16() | ((nibbles & 0x0F) << 16);
        Le16();
        _tree?.Field("Original Bit Rate", start, _pos, NodeValue.Integer(original, "bit/s"), Explain.LbrBitRate, Topics.BitrateModes);
        asset.LbrBitRate = original;
    }

    // A little endian 16-bit field.
    private uint Le16()
    {
        uint low = Read(8);
        uint high = Read(8);
        return (high << 8) | low;
    }

    // 8.3.1 Table 8-1: the common header of the lossless extension, with its CRC; then the channel set sub-headers, the navigation index
    // and the band data (8.3.2 to 8.4.2), which UnitSyntax.Lossless reads.
    private void ReadXll(int at, int endBit, AssetFacts asset)
    {
        int version = Num("Version", 4, Explain.XllVersion, Topics.Lossless, null, 1) + 1;
        int headerSize = HeaderSize(8, Explain.XllHeaderSize, Topics.Lossless);
        int sizeBits = Num("Frame Size Bits", 5, Explain.XllFrameSizeBits, Topics.Lossless, "bits", 1) + 1;
        int frameSizeStart = _pos;
        int frameSize = Num("Frame Size", sizeBits, Explain.XllFrameSize, Topics.Lossless, "bytes", 1) + 1;
        int frameSizeEnd = _pos;
        int sets = Num("Channel Sets", 4, Explain.XllSets, Topics.Lossless, "channel sets", 1) + 1;
        int segmentsLog = (int)Read(4);
        _tree?.Field("Segments", _pos - 4, _pos, NodeValue.Integer(1 << segmentsLog, "segments", "segment"), Explain.XllSegments, Topics.Lossless);
        int samplesLog = (int)Read(4);
        _tree?.Field("Samples per Segment", _pos - 4, _pos, NodeValue.Integer(1 << samplesLog, "samples", "sample"), Explain.XllSamplesInSegment, Topics.Lossless);
        int segmentBits = Num("Segment Size Bits", 5, Explain.XllSegmentBits, Topics.Lossless, "bits", 1) + 1;
        Num("Band Data CRC", 2, Explain.XllBandCrc, v => v switch
        {
            0 => "0 (none)",
            1 => "1 (at the end of the most significant part of band 0)",
            2 => "2 (also at the end of the least significant part)",
            _ => "3 (also at the end of bands 1 to 3)",
        }, Topics.Crc);
        bool scalable = Flag("HasScalableLsbs", Explain.XllScalable, Topics.Lossless);
        int maskBits = Num("Channel Mask Bits", 5, Explain.XllMaskBits, Topics.Lossless, "bits", 1) + 1;
        if (scalable)
        {
            Num("Fixed LSB Width", 4, Explain.XllLsbWidth, Topics.Lossless, "bits");
        }

        ReadHeaderCrc(at, headerSize, "XLL", Explain.XllCrc, "The lossless extension's header CRC16 (8.3.1) fails");

        int bands = asset.MaxSampleRate > 192000 ? 4 : asset.MaxSampleRate > 96000 ? 2 : 1;
        asset.XllSamples = (1 << segmentsLog) * (1 << samplesLog) * bands;
        if (version > 1)
        {
            Report(Issue.ReservedValue, "Version", at * 8 + 32, at * 8 + 36, $"XLL version {version} is newer than the version 1 that 8.3.1 describes, so a decoder of version 1 does not process the stream; its channel sets are not read");
            AddCoreData(Math.Max(_pos, (at + headerSize) * 8), endBit, "Lossless Data", Explain.XllData);
            return;
        }

        ReadXllFrame(at, endBit, asset, headerSize, new XllCommon(sets, frameSize, segmentsLog, segmentBits, scalable, maskBits, frameSizeStart, frameSizeEnd));
    }
}
