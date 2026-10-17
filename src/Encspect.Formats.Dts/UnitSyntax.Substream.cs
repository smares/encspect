using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Dts;

internal sealed partial class UnitSyntax
{
    // The fields of the extension substream header that the asset descriptors depend on (7.5.2).
    private bool _hasStaticFields;
    private bool _mixEnabled;
    private int _frameDurationCycles;
    private int _sizeBits;
    private int _mixConfigurations;
    private readonly int[] _mixChannels = new int[4];
    private int _clockRate;
    private StaticState? _carry;
    private SubstreamFacts? _substream;
    private int _assetSizesStart;
    private int _assetSizesEnd;

    // 7.5.2, Table 7-2: the extension substream that starts at unit byte substream.Start.
    private void ReadSubstream(ExssRef substream)
    {
        int outer = _limit;
        try
        {
            ReadSubstreamWithin(substream);
        }
        finally
        {
            _limit = outer;
        }
    }

    private void ReadSubstreamWithin(ExssRef substream)
    {
        int baseBit = substream.Start * 8;
        int endBit = baseBit + (Math.Min(substream.Length, substream.Available) * 8);
        _limit = Math.Min(_limit, endBit);
        _pos = baseBit;
        EnsureBytes(substream.Start, Math.Min(substream.Available, ExssHeader.MaximumHeaderSize));
        int depth = _tree?.Depth ?? 0;
        _tree?.Open("Extension Substream", baseBit, Explain.Substream, Topics.ExtensionSubstream);
        var facts = new SubstreamFacts { Index = substream.Index };
        Facts.Substreams.Add(facts);
        _substream = facts;
        int headerEnd = baseBit;
        try
        {
            ReadSubstreamHeader(substream, facts, ref headerEnd);
        }
        catch (EndOfStreamException)
        {
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, "Extension Substream", baseBit, endBit, "the data ends inside the extension substream's header");
            }
        }

        _tree?.CloseTo(depth + 1, Here);
        _tree?.Close(endBit, NodeValue.Integer(substream.Length, "bytes"));
    }

    private void ReadSubstreamHeader(ExssRef substream, SubstreamFacts facts, ref int headerEnd)
    {
        int baseByte = substream.Start;
        Hex("Sync Word", 32, Explain.SubstreamSync, Topics.ExtensionSubstream);
        Num("User Defined Bits", 8, Explain.UserBits, Topics.ExtensionSubstream);
        int index = Num("Extension Substream Index", 2, Explain.ExtSsIndex, Topics.ExtensionSubstream);
        bool wide = Flag("HasLongHeader", Explain.HeaderSizeType, Topics.ExtensionSubstream);
        int headerSize = HeaderSize(wide ? 12 : 8, Explain.ExtSsHeaderSize, Topics.ExtensionSubstream);
        _sizeBits = wide ? 20 : 16;
        Num("Substream Size", _sizeBits, Explain.ExtSsFsize, Topics.ExtensionSubstream, "bytes", 1);
        int start;

        int crcBit = (baseByte + headerSize - 2) * 8;
        facts.IsCrcValid = false;
        _hasStaticFields = Flag("HasStaticFields", Explain.StaticFields, Topics.ExtensionSubstream);
        _mixEnabled = false;
        _mixConfigurations = 0;
        _frameDurationCycles = 0;
        _clockRate = 0;
        _carry = null;
        int presentations = 1;
        int assets = 1;
        facts.HasStaticFields = _hasStaticFields;
        if (_hasStaticFields)
        {
            presentations = ReadStaticFields(facts, index, out assets);
        }
        else
        {
            UseCarriedFields(facts, index);
        }

        var assetSizes = new int[assets];
        _assetSizesStart = _pos;
        _tree?.Open("Asset Sizes", _pos, Explain.AssetSizes, Topics.ExtensionSubstream);
        for (int i = 0; i < assets; i++)
        {
            start = _pos;
            assetSizes[i] = (int)Read(_sizeBits) + 1;
            _tree?.Field($"Asset {i + 1}", start, _pos, NodeValue.Integer(assetSizes[i], "bytes"), Explain.AssetSizes, Topics.ExtensionSubstream);
        }

        _tree?.Close(Here);
        _assetSizesEnd = _pos;
        var descriptors = new AssetFacts[assets];
        for (int i = 0; i < assets; i++)
        {
            descriptors[i] = ReadAssetDescriptor(i, wide);
            facts.Assets.Add(descriptors[i]);
        }

        StaticState? stated = _hasStaticFields ? CarriedFields(descriptors[0]) : null;

        // Table 7-2: whether each audio presentation has a backward compatible core in an extension substream, and where.
        _tree?.Open("Backward Compatible Cores", _pos, Explain.BcCores, Topics.ExtensionSubstream);
        var present = new bool[presentations];
        for (int i = 0; i < presentations; i++)
        {
            present[i] = Flag($"HasBackwardCompatibleCore {i + 1}", Explain.BcCorePresent, Topics.ExtensionSubstream);
        }

        for (int i = 0; i < presentations; i++)
        {
            if (present[i])
            {
                Num($"Core Substream Index {i + 1}", 2, Explain.BcCoreIndex, Topics.ExtensionSubstream);
                Num($"Core Asset Index {i + 1}", 3, Explain.BcCoreAsset, Topics.ExtensionSubstream);
            }
        }

        _tree?.Close(Here);
        if (headerSize < ExssHeader.MinimumHeaderSize || _pos > crcBit)
        {
            Report(Issue.SubstreamOverrun, "Header Size", _headerSizeStart, _headerSizeEnd, $"the header's fields end at byte {((_pos + 7) / 8) - baseByte}, past the {headerSize} bytes its size field gives");
            return;
        }

        if (_pos < crcBit)
        {
            Skip("Reserved", crcBit - _pos, Explain.Reserved);
        }

        // 7.5.2: the CRC16 covers the header from nExtSSIndex (byte 5) to the end of the byte alignment.
        int verdict = CrcVerdict(baseByte + 5, headerSize - 5);
        facts.IsCrcValid = verdict == 1;
        if (stated is not null && verdict != 0 && _staticStates is not null)
        {
            // FFmpeg checks the CRC16 before it reads the fields, and keeps the earlier ones where it fails.
            _staticStates[index] = stated;
        }
        int crcStart = _pos;
        Crc("Header CRC", Explain.ExtSsCrc, verdict, "not checked");
        if (verdict == 0)
        {
            Report(Issue.CrcMismatch, "Header CRC", crcStart, _pos, "The extension substream header's CRC16 (7.5.2) fails");
        }

        headerEnd = _pos;
        ReadAssetData(substream, descriptors, assetSizes, headerSize);
    }

    // 7.5.2: a header without static fields leaves them as the last header of this extension substream index had them.
    private void UseCarriedFields(SubstreamFacts facts, int index)
    {
        StaticState? carry = _isBuilding ? _verdicts.NextCarry() : _staticStates?[index];
        if (!_isBuilding)
        {
            _verdicts.AddCarry(carry);
        }

        _carry = carry;
        if (carry is null)
        {
            return;
        }

        _mixEnabled = carry.IsMixEnabled;
        _mixConfigurations = carry.MixConfigurations;
        carry.MixChannels.CopyTo(_mixChannels, 0);
        _frameDurationCycles = carry.FrameDurationCycles;
        _clockRate = carry.ClockRate;
        facts.Duration = carry.Duration;
    }

    private StaticState CarriedFields(AssetFacts asset)
    {
        var state = new StaticState
        {
            IsMixEnabled = _mixEnabled,
            MixConfigurations = _mixConfigurations,
            FrameDurationCycles = _frameDurationCycles,
            ClockRate = _clockRate,
            BitResolution = asset.BitResolution,
            MaxSampleRate = asset.MaxSampleRate,
            Channels = asset.Channels,
            IsOneToOne = asset.IsOneToOne,
            HasEmbeddedStereo = asset.HasEmbeddedStereo,
            HasEmbeddedSixChannel = asset.HasEmbeddedSixChannel,
            HasSpeakerMask = asset.HasSpeakerMask,
            SpeakerMask = asset.SpeakerMask,
        };
        _mixChannels.CopyTo(state.MixChannels, 0);
        return state;
    }

    // The static fields, 7.5.2 Table 7-2: the reference clock, the frame duration, a time stamp, the audio presentations and the mixing metadata.
    private int ReadStaticFields(SubstreamFacts facts, int index, out int assets)
    {
        int start = _pos;
        int clock = Num("Reference Clock Code", 2, Explain.RefClock, v => v switch
        {
            0 => NodeValue.Integer(32000, "Hz"),
            1 => NodeValue.Integer(44100, "Hz"),
            2 => NodeValue.Integer(48000, "Hz"),
            _ => "3 (unused)",
        }, Topics.ExtensionSubstream);
        if (clock == 3)
        {
            Report(Issue.ReservedValue, "Reference Clock Code", start, _pos, "reference clock code 3 is unused (Table 7-3)");
        }

        int duration = (int)Read(3);
        _frameDurationCycles = 512 * (duration + 1);
        _tree?.Field("Frame Duration", _pos - 3, _pos, NodeValue.Integer(_frameDurationCycles, "reference clock cycles", "reference clock cycle"), Explain.FrameDuration, Topics.ExtensionSubstream);
        int rate = clock switch { 0 => 32000, 1 => 44100, 2 => 48000, _ => 0 };
        _clockRate = rate;
        facts.Duration = rate > 0 ? _frameDurationCycles / (double)rate : 0;
        if (Flag("HasTimeStamp", Explain.TimeStampFlag, Topics.ExtensionSubstream))
        {
            start = _pos;
            long high = Read(32);
            int low = (int)Read(4);
            long stamp = (high << 4) | (uint)low;
            _tree?.Field("Time Stamp", start, _pos, rate > 0 ? TimeStamp(stamp, rate) : NodeValue.Integer(stamp), Explain.TimeStamp, Topics.ExtensionSubstream);
        }

        int presentations = Num("Audio Presentations", 3, Explain.AudioPresentations, Topics.ExtensionSubstream, "presentations", 1) + 1;
        assets = Num("Audio Assets", 3, Explain.AudioAssets, Topics.ExtensionSubstream, "assets", 1) + 1;
        _tree?.Open("Active Extension Substream Masks", _pos, Explain.ActiveExtSs, Topics.ExtensionSubstream);
        var active = new int[presentations];
        for (int i = 0; i < presentations; i++)
        {
            start = _pos;
            active[i] = (int)Read(index + 1);
            _tree?.Field($"Presentation {i + 1}", start, _pos, NodeValue.Hex(active[i], 1), Explain.ActiveExtSs, Topics.ExtensionSubstream);
        }

        _tree?.Close(Here);
        _tree?.Open("Active Asset Masks", _pos, Explain.ActiveAssets, Topics.ExtensionSubstream);
        for (int i = 0; i < presentations; i++)
        {
            for (int substream = 0; substream <= index; substream++)
            {
                if (((active[i] >> substream) & 1) == 1)
                {
                    start = _pos;
                    int mask = (int)Read(8);
                    _tree?.Field($"Presentation {i + 1}, Substream {substream}", start, _pos, NodeValue.Hex(mask, 2), Explain.ActiveAssets, Topics.ExtensionSubstream);
                }
            }
        }

        _tree?.Close(Here);
        _mixEnabled = Flag("HasMixMetadata", Explain.MixEnable, Topics.ExtensionSubstream);
        if (_mixEnabled)
        {
            Num("Mixing Metadata Adjustment Level", 2, Explain.MixAdjLevel, v => v switch
            {
                0 => "0 (bit stream metadata only)",
                1 => "1 (system metadata may adjust the level)",
                2 => "2 (system metadata may adjust level and placement)",
                _ => "3 (reserved)",
            }, Topics.ExtensionSubstream);
            start = _pos;
            int maskBits = (int)(Read(2) + 1) << 2;
            _tree?.Field("Mixer Output Mask Bits", start, _pos, NodeValue.Integer(maskBits, "bits"), Explain.MixMaskBits, Topics.ExtensionSubstream);
            _mixConfigurations = Num("Mixing Configurations", 2, Explain.MixConfigs, Topics.ExtensionSubstream, "configurations", 1) + 1;
            _tree?.Open("Mixer Output Speaker Masks", _pos, Explain.MixOutMask, Topics.DtsChannelArrangements);
            for (int i = 0; i < _mixConfigurations; i++)
            {
                start = _pos;
                uint mask = Read(maskBits);
                _mixChannels[i] = Tables.SpeakerCount((int)mask);
                _tree?.Field($"Configuration {i + 1}", start, _pos, NodeValue.Hex(mask, maskBits / 4), Explain.MixOutMask, Topics.DtsChannelArrangements);
            }

            _tree?.Close(Here);
        }

        return presentations;
    }

    // 7.5.2: the time stamp counts reference clock cycles from the start of the day (hours, minutes and seconds) plus a sample offset.
    private static NodeValue TimeStamp(long stamp, int rate)
    {
        long seconds = stamp / rate;
        return $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2} and {stamp % rate} samples";
    }

    // 7.5.3, Tables 7-5 to 7-7: an audio asset descriptor, from its size to the navigation data and the fields that follow it.
    private AssetFacts ReadAssetDescriptor(int ordinal, bool wide)
    {
        var asset = new AssetFacts { HasStaticFields = _hasStaticFields };
        int groupStart = _pos;
        _tree?.Open($"Asset Descriptor {ordinal + 1}", _pos, Explain.AssetDescriptor, Topics.ExtensionSubstream);
        int size = Num("Descriptor Size", 9, Explain.AssetDescriptorSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
        int end = groupStart + (size * 8);
        asset.Index = Num("Asset Index", 3, Explain.AssetIndex, Topics.ExtensionSubstream);
        bool embeddedStereo = false;
        bool embeddedSix = false;
        bool oneToOne = false;
        int channels = 0;
        if (_hasStaticFields)
        {
            ReadStaticAssetFields(asset, out channels, out oneToOne, out embeddedStereo, out embeddedSix);
        }
        else if (ordinal == 0 && _carry is { } carry)
        {
            asset.BitResolution = carry.BitResolution;
            asset.MaxSampleRate = carry.MaxSampleRate;
            asset.Channels = channels = carry.Channels;
            asset.IsOneToOne = oneToOne = carry.IsOneToOne;
            asset.HasSpeakerMask = carry.HasSpeakerMask;
            asset.SpeakerMask = carry.SpeakerMask;
            embeddedStereo = carry.HasEmbeddedStereo;
            embeddedSix = carry.HasEmbeddedSixChannel;
        }

        asset.AreFiguresKnown = _hasStaticFields || (ordinal == 0 && _carry is not null);
        asset.HasEmbeddedStereo = embeddedStereo;
        asset.HasEmbeddedSixChannel = embeddedSix;

        ReadDynamicAssetFields(channels, embeddedStereo, embeddedSix, out bool mixPresent);
        ReadNavigation(asset, oneToOne, mixPresent);
        if (_pos > end)
        {
            ReportGroup(Issue.AssetDescriptorOverrun, $"Asset Descriptor {ordinal + 1}", groupStart, _pos, $"asset {ordinal + 1}'s fields end {(_pos - end + 7) / 8} bytes past the {size} bytes of its descriptor");
        }

        // Reserved bits and the byte alignment complete the descriptor (nuAssetDescriptFsize).
        if (_pos < end)
        {
            Skip("Reserved", end - _pos, Explain.Reserved);
        }

        _pos = Math.Max(_pos, end);
        _tree?.Close(Here, NodeValue.Integer(size, "bytes"));
        return asset;
    }

    private void ReadStaticAssetFields(AssetFacts asset, out int channels, out bool oneToOne, out bool embeddedStereo, out bool embeddedSix)
    {
        embeddedStereo = false;
        embeddedSix = false;
        if (Flag("HasAssetType", Explain.AssetTypePresent, Topics.ExtensionSubstream))
        {
            Num("Asset Type", 4, Explain.AssetType, v => $"{v}: {Tables.AssetType(v)}", Topics.ExtensionSubstream);
        }

        if (Flag("HasLanguage", Explain.LanguagePresent, Topics.ExtensionSubstream))
        {
            int start = _pos;
            uint code = Read(24);
            string language = new([(char)(code >> 16), (char)((code >> 8) & 0xFF), (char)(code & 0xFF)]);
            _tree?.Field("Language", start, _pos, language, Explain.Language, Topics.ExtensionSubstream);
        }

        if (Flag("HasInfoText", Explain.InfoTextPresent, Topics.ExtensionSubstream))
        {
            int length = Num("Info Text Size", 10, Explain.InfoTextSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
            int start = _pos;
            var text = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                text.Append((char)Read(8));
            }

            _tree?.Field("Info Text", start, _pos, text.ToString(), Explain.InfoText, Topics.ExtensionSubstream);
        }

        asset.BitResolution = Num("Bit Resolution", 5, Explain.BitResolution, Topics.ExtensionSubstream, "bits", 1) + 1;
        asset.MaxSampleRate = Tables.ExtensionSampleRate(Num("Maximum Sample Rate", 4, Explain.MaxSampleRate, v => NodeValue.Integer(Tables.ExtensionSampleRate(v), "Hz"), Topics.ExtensionSubstream));
        channels = Num("Total Channels", 8, Explain.TotalChannels, Topics.ExtensionSubstream, "channels", 1) + 1;
        asset.Channels = channels;
        oneToOne = Flag("IsOneToOneSpeakerMap", Explain.OneToOne, Topics.ExtensionSubstream);
        asset.IsOneToOne = oneToOne;
        if (!oneToOne)
        {
            Num("Representation Type", 3, Explain.RepresentationType, v => v switch
            {
                0 => "0 (audio asset for mixing or replacement)",
                2 => "2 (left total, right total encoded for matrix surround decoding)",
                3 => "3 (processed for headphone playback)",
                _ => $"{v} (reserved)",
            }, Topics.ExtensionSubstream);
            return;
        }

        if (channels > 2)
        {
            embeddedStereo = Flag("HasEmbeddedStereo", Explain.EmbeddedStereo, Topics.ExtensionSubstream);
        }

        if (channels > 6)
        {
            embeddedSix = Flag("HasEmbeddedSixChannel", Explain.EmbeddedSix, Topics.ExtensionSubstream);
        }

        int maskBits = 0;
        if (Flag("HasSpeakerMask", Explain.SpeakerMaskEnabled, Topics.ExtensionSubstream))
        {
            int start = _pos;
            maskBits = (int)(Read(2) + 1) << 2;
            _tree?.Field("Speaker Mask Bits", start, _pos, NodeValue.Integer(maskBits, "bits"), Explain.SpeakerMaskBits, Topics.ExtensionSubstream);
            asset.SpeakerMask = (int)Hex("Speaker Activity Mask", maskBits, Explain.SpeakerMask, Topics.DtsChannelArrangements);
            asset.HasSpeakerMask = true;
        }

        int setsStart = _pos;
        int sets = Num("Speaker Remapping Sets", 3, Explain.RemapSets, Topics.ExtensionSubstream);
        if (sets > 0 && maskBits == 0)
        {
            // The standard layout masks of the sets are nuBits4SpkrMask bits wide (Table 7-5), and FFmpeg refuses sets without a mask (dca_exss.c).
            Report(Issue.ReservedValue, "Speaker Remapping Sets", setsStart, _pos, $"the asset has {sets} speaker remapping set{(sets == 1 ? string.Empty : "s")} but no speaker activity mask, whose width the layout masks of the sets need (Table 7-5)");
        }

        var layouts = new int[sets];
        for (int i = 0; i < sets; i++)
        {
            int start = _pos;
            layouts[i] = (int)Read(maskBits);
            _tree?.Field($"Standard Speaker Layout Mask {i + 1}", start, _pos, NodeValue.Hex(layouts[i], maskBits / 4), Explain.RemapLayout, Topics.DtsChannelArrangements);
        }

        for (int i = 0; i < sets; i++)
        {
            int start = _pos;
            int decoded = (int)Read(5) + 1;
            int speakers = Tables.SpeakerCount(layouts[i]);
            for (int speaker = 0; speaker < speakers; speaker++)
            {
                uint mask = decoded > 32 ? 0 : Read(decoded);
                Advance(System.Numerics.BitOperations.PopCount(mask) * 5);
            }

            _tree?.Field($"Speaker Remapping {i + 1}", start, _pos, NodeValue.Integer(speakers, "speakers", "speaker"), Explain.RemapCodes, Topics.ExtensionSubstream);
        }
    }

    // 7.5.3 Table 7-6: the dynamic metadata.
    private void ReadDynamicAssetFields(int channels, bool embeddedStereo, bool embeddedSix, out bool mixPresent)
    {
        bool drc = Flag("HasDynamicRange", Explain.DrcPresent, Topics.DynamicRange);
        if (drc)
        {
            Num("Dynamic Range Code", 8, Explain.DrcCode, v => NodeValue.Decimal(-32 + ((v + 1) * 0.25), 2, "dB", showsSign: true), Topics.DynamicRange);
        }

        if (Flag("HasDialogueNormalization", Explain.DialNormPresent, Topics.DialogueNormalization))
        {
            Num("Dialogue Normalization", 5, Explain.DialNorm, v => NodeValue.Integer(-v, "dB"), Topics.DialogueNormalization);
        }

        if (drc && embeddedStereo)
        {
            Num("Dynamic Range Code for Stereo Downmix", 8, Explain.DrcStereo, v => NodeValue.Decimal(-32 + ((v + 1) * 0.25), 2, "dB", showsSign: true), Topics.DynamicRange);
        }

        mixPresent = _mixEnabled && Flag("HasMixMetadata", Explain.MixPresent, Topics.ExtensionSubstream);
        if (!mixPresent)
        {
            return;
        }

        int start = _pos;
        _tree?.Open("Mixing Metadata", _pos, Explain.MixMetadata, Topics.ExtensionSubstream);
        Flag("IsExternalMix", Explain.ExternalMix, Topics.ExtensionSubstream);
        Num("Post Mix Gain Adjustment Code", 6, Explain.PostMixGain, Topics.ExtensionSubstream);
        int control = Num("Mixer Dynamic Range Control", 2, Explain.MixerDrcControl, Topics.DynamicRange);
        if (control < 3)
        {
            Num("Mixer Dynamic Range Limit", 3, Explain.MixerDrcLimit, Topics.DynamicRange);
        }
        else
        {
            Num("Custom Dynamic Range Code", 8, Explain.CustomDrc, Topics.DynamicRange);
        }

        ReadMainAudioScale();
        start = _pos;
        int[] decoded = [channels, embeddedSix ? 6 : 0, embeddedStereo ? 2 : 0];
        int maps = 0;
        for (int configuration = 0; configuration < _mixConfigurations; configuration++)
        {
            foreach (int count in decoded.Where((c, i) => i == 0 || c > 0))
            {
                for (int channel = 0; channel < count; channel++)
                {
                    uint mask = _mixChannels[configuration] > 32 ? 0 : Read(_mixChannels[configuration]);
                    Advance(System.Numerics.BitOperations.PopCount(mask) * 6);
                    maps++;
                }
            }
        }

        _tree?.Field("Mixing Map", start, _pos, NodeValue.Integer(maps, "channel maps", "channel map"), Explain.MixMap, Topics.ExtensionSubstream);
        _tree?.Close(Here);
    }

    private void ReadMainAudioScale()
    {
        bool perChannel = Flag("HasPerChannelMainAudioScale", Explain.PerChannelScale, Topics.ExtensionSubstream);
        int start = _pos;
        int codes = 0;
        for (int configuration = 0; configuration < _mixConfigurations; configuration++)
        {
            int count = perChannel ? _mixChannels[configuration] : 1;
            Advance(count * 6);
            codes += count;
        }

        _tree?.Field("Main Audio Scale Codes", start, _pos, NodeValue.Integer(codes, "codes", "code"), Explain.MainAudioScale, Topics.ExtensionSubstream);
    }

    // 7.5.3 Table 7-7: the coding components of the asset and their sizes.
    private void ReadNavigation(AssetFacts asset, bool oneToOne, bool mixPresent)
    {
        int mode = Num("Coding Mode", 2, Explain.CodingMode, v => v switch
        {
            0 => "0 (several coding components)",
            1 => "1 (lossless without a core)",
            2 => "2 (low bit rate)",
            _ => "3 (auxiliary, reserved)",
        }, Topics.ExtensionSubstream);
        asset.CodingMode = mode;
        int start = _pos;
        int mask = 0;
        var sizes = new Dictionary<int, int>();
        int fsizeBits = _sizeBits;
        switch (mode)
        {
            case 0:
                mask = (int)Read(12);
                _tree?.Field("Coding Components", start, _pos, ComponentsValue(mask), Explain.CoreExtensionMask, Topics.ExtensionSubstream);
                if ((mask & Tables.ComponentReserved) != 0)
                {
                    Report(Issue.ReservedValue, "Coding Components", start, _pos, "bits 0x400 and 0x800 of nuCoreExtensionMask are reserved (Table 7-15)");
                }

                if ((mask & Tables.ComponentCore) != 0)
                {
                    sizes[Tables.ComponentCore] = Num("Core Size", 14, Explain.CoreSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
                    if (Flag("HasCoreSync", Explain.CoreSyncPresent, Topics.ExtensionSubstream))
                    {
                        Num("Core Sync Distance", 2, Explain.SyncDistance, v => NodeValue.Integer(1 << v, "frames", "frame"), Topics.ExtensionSubstream);
                    }
                }

                if ((mask & Tables.ComponentXbr) != 0)
                {
                    sizes[Tables.ComponentXbr] = Num("XBR Size", 14, Explain.XbrSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
                }

                if ((mask & Tables.ComponentXxch) != 0)
                {
                    sizes[Tables.ComponentXxch] = Num("XXCH Size", 14, Explain.XxchSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
                }

                if ((mask & Tables.ComponentX96) != 0)
                {
                    sizes[Tables.ComponentX96] = Num("X96 Size", 12, Explain.X96Size, Topics.ExtensionSubstream, "bytes", 1) + 1;
                }

                if ((mask & Tables.ComponentLbr) != 0)
                {
                    sizes[Tables.ComponentLbr] = ReadLbrSize();
                }

                if ((mask & Tables.ComponentXll) != 0)
                {
                    sizes[Tables.ComponentXll] = ReadXllSize(fsizeBits);
                }

                if ((mask & 0x400) != 0)
                {
                    Skip("Reserved", 16, Explain.Reserved);
                }

                if ((mask & 0x800) != 0)
                {
                    Skip("Reserved", 16, Explain.Reserved);
                }

                break;
            case 1:
                mask = Tables.ComponentXll;
                sizes[Tables.ComponentXll] = ReadXllSize(fsizeBits);
                break;
            case 2:
                mask = Tables.ComponentLbr;
                sizes[Tables.ComponentLbr] = ReadLbrSize();
                break;
            default:
                Report(Issue.CodingModeAuxiliary, "Coding Mode", start - 2, start, "coding mode 3");
                Num("Auxiliary Size", 14, Explain.AuxSize, Topics.ExtensionSubstream, "bytes", 1);
                Num("Auxiliary Codec ID", 8, Explain.AuxCodec, Topics.ExtensionSubstream);
                if (Flag("HasAuxiliarySync", Explain.AuxSyncPresent, Topics.ExtensionSubstream))
                {
                    Num("Auxiliary Sync Distance", 3, Explain.SyncDistance, Topics.ExtensionSubstream, "frames", 1);
                }

                break;
        }

        asset.Components = ComponentsOfMask(mask);
        asset.ComponentSizes = sizes;
        if ((mode == 0 && (mask & Tables.ComponentXll) != 0) || mode == 1)
        {
            Num("DTS-HD Stream ID", 3, Explain.HdStreamId, Topics.ExtensionSubstream);
        }

        bool oneToOneMixing = false;
        if (oneToOne && _mixEnabled && !mixPresent)
        {
            oneToOneMixing = Flag("IsOneToOneMixing", Explain.OneToOneMixing, Topics.ExtensionSubstream);
        }

        if (oneToOneMixing)
        {
            ReadMainAudioScale();
        }

        Flag("IsDecodedInSecondaryDecoder", Explain.SecondaryDecoder, Topics.ExtensionSubstream);
        if (Flag("HasDynamicRangeRevision2", Explain.DrcRev2Present, Topics.DynamicRange))
        {
            Num("Dynamic Range Revision 2 Version", 4, Explain.DrcRev2Version, Topics.DynamicRange);
            int count = _frameDurationCycles / 256;
            int first = _pos;
            Advance(count * 8);

            _tree?.Field("Dynamic Range Revision 2 Codes", first, _pos, NodeValue.Integer(count, "codes", "code"), Explain.DrcRev2Codes, Topics.DynamicRange);
        }
    }

    private int ReadLbrSize()
    {
        int size = Num("LBR Size", 14, Explain.LbrSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
        if (Flag("HasLbrSync", Explain.LbrSyncPresent, Topics.ExtensionSubstream))
        {
            Num("LBR Sync Distance", 2, Explain.SyncDistance, v => NodeValue.Integer(1 << v, "frames", "frame"), Topics.ExtensionSubstream);
        }

        return size;
    }

    private int ReadXllSize(int fsizeBits)
    {
        int size = Num("XLL Size", fsizeBits, Explain.XllSize, Topics.ExtensionSubstream, "bytes", 1) + 1;
        if (Flag("HasXllSync", Explain.XllSyncPresent, Topics.ExtensionSubstream))
        {
            Num("Peak Bit Rate Smoothing Buffer Size", 4, Explain.XllBuffer, v => NodeValue.Integer(v << 4, "kB"), Topics.ExtensionSubstream);
            int delayBits = Num("Initial Decoding Delay Bits", 5, Explain.XllDelayBits, Topics.ExtensionSubstream, "bits", 1) + 1;
            Num("Initial Decoding Delay", delayBits, Explain.XllDelay, Topics.ExtensionSubstream, "frames");
            Num("Sync Offset", fsizeBits, Explain.XllSyncOffset, Topics.ExtensionSubstream, "bytes");
        }

        return size;
    }

    // Table 7-15's bits as the components they name.
    private static NodeValue ComponentsValue(int mask)
    {
        var names = new List<string>();
        for (int bit = 0; bit < 12; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                names.Add(Tables.Components[bit]);
            }
        }

        return $"0x{mask:X3} ({(names.Count == 0 ? "none" : string.Join(", ", names))})";
    }

    // The coding components of Table 7-15 in Table E-2's bits: the extension substream's core, XXCH, X96, XBR, XLL and LBR.
    private static int ComponentsOfMask(int mask)
    {
        int bits = 0;
        bits |= (mask & Tables.ComponentCore) != 0 ? Tables.ExtensionCore : 0;
        bits |= (mask & Tables.ComponentXxch) != 0 ? Tables.ExtensionXxch : 0;
        bits |= (mask & Tables.ComponentX96) != 0 ? Tables.ExtensionX96 : 0;
        bits |= (mask & Tables.ComponentXbr) != 0 ? Tables.ExtensionXbr : 0;
        bits |= (mask & Tables.ComponentXll) != 0 ? Tables.ExtensionXll : 0;
        bits |= (mask & Tables.ComponentLbr) != 0 ? Tables.ExtensionLbr : 0;
        bits |= (mask & 0x01) != 0 ? Tables.CoreSubstreamCore : 0;
        bits |= (mask & 0x02) != 0 ? Tables.CoreSubstreamXxch : 0;
        bits |= (mask & 0x04) != 0 ? Tables.CoreSubstreamX96 : 0;
        bits |= (mask & 0x08) != 0 ? Tables.CoreSubstreamXch : 0;
        return bits;
    }
}
