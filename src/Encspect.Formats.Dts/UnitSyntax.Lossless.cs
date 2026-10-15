using Encspect.Core;

namespace Encspect.Formats.Dts;

// What the common header of the lossless extension (8.3.1) says that the rest of the frame needs.
internal readonly record struct XllCommon(int Sets, int FrameSize, int SegmentsLog, int SegmentBits, bool IsScalable, int MaskBits, int FrameSizeStart, int FrameSizeEnd);

internal sealed partial class UnitSyntax
{
    // FFmpeg's dca_syncwords.h: the words dca_xll.c looks for after the band data of a lossless frame, for DTS:X and for DTS:X IMAX
    // (its test ignores the lowest bit of the second).
    private const uint XSync = 0x02000850;
    private const uint XImaxSync = 0xF14000D0;

    // dca_xll.c refuses a navigation index of more than this many entries.
    private const int NaviMaxEntries = 1024;

    // ff_dca_dmix_primary_nch: the channels each downmix type of Table 8-8 mixes the primary channel sets to.
    private static readonly int[] DownmixChannels = [1, 2, 2, 3, 3, 4, 4, 0];

    // 8.3.2 to 8.4.2: the channel set sub-headers, the navigation index and the band data of a lossless frame that starts at unit byte at.
    // Sub-headers are read as far as Table 8-4's fields go for the primary channel sets; the prediction and decorrelation parameters of
    // the frequency bands are left as bytes.
    private void ReadXllFrame(int at, int endBit, AssetFacts asset, int headerSize, XllCommon common)
    {
        int setAt = at + headerSize;
        var sets = new List<XllSetFacts>();
        int hierChannels = 0;
        bool complete = true;
        bool allFacts = true;
        var bandsOf = new List<int>();
        for (int index = 0; index < common.Sets; index++)
        {
            if (setAt * 8 >= _limit)
            {
                complete = false;
                break;
            }

            XllSetFacts? set = ReadXllSet(index, setAt, asset, common, hierChannels, out int size, out int bands, out int hierAdd, out bool isStructural);
            if (size <= 0)
            {
                complete = false;
                break;
            }

            bandsOf.Add(bands);
            if (!isStructural)
            {
                complete = false;
            }

            if (set is null)
            {
                allFacts = false;
            }
            else
            {
                sets.Add(set);
            }

            // FFmpeg's hier_ofs: the channels of the hierarchical sets so far, whether or not this set's facts are kept.
            hierChannels += hierAdd;

            setAt += size;
        }

        if (!complete || bandsOf.Count != common.Sets)
        {
            AddCoreData(setAt * 8, endBit, "Lossless Data", Explain.XllData);
            asset.XllSets.AddRange(sets);
            return;
        }

        asset.XllSets.AddRange(sets);
        asset.IsXllComplete = allFacts;
        ReadXllNavigation(at, endBit, asset, setAt, common, bandsOf, allFacts);
    }

    // 8.3.2, Table 8-4: one channel set sub-header, up to the frequency band parameters, whose bytes are shown as such, and its CRC16.
    private XllSetFacts? ReadXllSet(int index, int setAt, AssetFacts asset, XllCommon common, int hierChannels, out int size, out int bands, out int hierAdd, out bool isStructural)
    {
        isStructural = false;
        size = 0;
        bands = 1;
        hierAdd = 0;
        int outerLimit = _limit;
        int depth = _tree?.Depth ?? 0;
        _pos = setAt * 8;
        EnsureBytes(setAt, 2);
        _tree?.Open($"Channel Set {index + 1}", _pos, Explain.XllChannelSet, Topics.Lossless);
        int groupStart = _pos;
        int headerEnd = _pos;
        int? fixedEnd = null;
        int GroupEnd() => fixedEnd ??= Math.Min(Math.Max(Math.Max(headerEnd, _pos), groupStart), outerLimit);
        XllSetFacts? facts = null;
        try
        {
            size = HeaderSize(10, Explain.XllSetHeaderSize, Topics.Lossless);
            headerEnd = (setAt + size) * 8;
            EnsureBytes(setAt, size);
            int verdict = size >= 6 ? CrcVerdict(setAt, size) : 2;
            int crcBit = headerEnd - 16;
            bool fits = size >= 6;
            _limit = Math.Min(outerLimit, Math.Max(crcBit, _pos));
            try
            {
                facts = ReadXllSetFields(common, asset, hierChannels, crcBit, out bands, out hierAdd);
            }
            catch (EndOfStreamException)
            {
                facts = null;
                if (crcBit <= outerLimit)
                {
                    ReportGroup(Issue.SubstreamOverrun, $"Channel Set {index + 1}", groupStart, GroupEnd(), $"the channel set {index + 1} sub-header's fields end past the {size} bytes its size field gives");
                    fits = false;
                }
                else if (!IsIncomplete)
                {
                    ReportGroup(Issue.HeaderOverrun, $"Channel Set {index + 1}", groupStart, GroupEnd(), $"the data ends inside the channel set {index + 1} sub-header");
                }
            }

            _limit = outerLimit;
            if (fits && _pos <= crcBit)
            {
                if (_pos < crcBit)
                {
                    Skip("Band Parameters", crcBit - _pos, Explain.XllBandParameters, NodeValue.Integer((crcBit - _pos + 7) / 8, "bytes"));
                }

                int crcStart = _pos;
                Crc("Header CRC", Explain.XllSetCrc, verdict, "not checked");
                if (verdict == 0)
                {
                    Report(Issue.CrcMismatch, "Header CRC", crcStart, _pos, $"The lossless extension's channel set {index + 1} sub-header CRC16 (8.3.2) fails");
                }
            }

            isStructural = verdict == 1 && fits && bands > 0 && _pos > groupStart;
            if (verdict != 1 || !fits)
            {
                facts = null;
            }
        }
        catch (EndOfStreamException)
        {
            facts = null;
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, $"Channel Set {index + 1}", groupStart, GroupEnd(), $"the data ends inside the channel set {index + 1} sub-header");
            }
        }

        _limit = outerLimit;
        _tree?.CloseTo(depth + 1, Here);
        _tree?.Close(GroupEnd(), NodeValue.Integer(size, "bytes"));
        return facts;
    }

    private XllSetFacts? ReadXllSetFields(XllCommon common, AssetFacts asset, int hierChannels, int crcBit, out int bands, out int hierAdd)
    {
        int channels = Num("Channels", 4, Explain.XllSetChannels, Topics.Lossless, "channels", 1) + 1;
        hierAdd = channels;
        int start = _pos;
        uint residual = Read(channels);
        _tree?.Field("Residual Channel Encoding", start, _pos, NodeValue.Hex(residual, (channels + 3) / 4), Explain.XllResidual, Topics.Lossless);
        start = _pos;
        int resolution = Num("Source PCM Resolution", 5, Explain.XllResolution, Topics.Lossless, "bits", 1) + 1;
        int resolutionEnd = _pos;
        int width = Num("Storage Bit Width", 5, Explain.XllStorageWidth, Topics.Lossless, "bits", 1) + 1;
        if (resolution > width)
        {
            Report(Issue.LosslessInconsistent, "Source PCM Resolution", start, resolutionEnd, $"a channel set's source resolution of {resolution} bits is more than its storage width of {width} bits (8.3.2: the samples are stored in the width)");
        }

        int rate = Tables.ExtensionSampleRate(Num("Sampling Frequency", 4, Explain.XllRate, v => NodeValue.Integer(Tables.ExtensionSampleRate(v), "Hz"), Topics.Lossless));
        Num("Sampling Frequency Interpolation", 2, Explain.XllInterpolation, v => NodeValue.Integer(1 << v), Topics.Lossless);
        int replacement = Num("Replacement Set", 2, Explain.XllReplacement, v => v == 0 ? "0 (not a member of a replacement set)" : $"{v} (member of replacement set {v})", Topics.Lossless);
        if (replacement > 0)
        {
            Flag("IsActiveReplacementSet", Explain.XllActiveReplacement, Topics.Lossless);
        }

        bool primary = true;
        bool hasMask = false;
        uint mask = 0;
        bool read = true;
        if (asset.IsOneToOne)
        {
            primary = Flag("IsPrimaryChannelSet", Explain.XllPrimary, Topics.Lossless);
            bool hasCoefficients = Flag("HasDownmixCoefficients", Explain.XllHasDownmix, Topics.Lossless);
            if (hasCoefficients)
            {
                Flag("IsDownmixEmbedded", Explain.XllDownmixEmbedded, Topics.Lossless);
            }

            int type = -1;
            if (hasCoefficients && primary)
            {
                start = _pos;
                type = Num("Downmix Type", 3, Explain.XllDownmixType, v => v switch
                {
                    0 => "0 (1/0)",
                    1 => "1 (Lo/Ro)",
                    2 => "2 (Lt/Rt)",
                    3 => "3 (3/0)",
                    4 => "4 (2/1)",
                    5 => "5 (2/2)",
                    6 => "6 (3/1)",
                    _ => "7 (unused)",
                }, Topics.Lossless);
                if (type == 7)
                {
                    Report(Issue.ReservedValue, "Downmix Type", start, _pos, "nLLDownmixType 7 is unused (Table 8-8)");
                }
            }

            hierAdd = Flag("IsHierarchicalChannelSet", Explain.XllHierarchical, Topics.Lossless) ? channels : 0;
            if (hasCoefficients)
            {
                // FFmpeg's parse_dmix_coeffs: a primary set mixes to the channels its downmix type names; any other set has a row for each
                // channel of the sets before it in the hierarchy, each a scale and a coefficient for every channel of this set (9 bits each).
                int count = primary ? DownmixChannels[Math.Clamp(type, 0, 7)] * channels : hierChannels * (channels + 1);
                Skip("Downmix Coefficients", count * 9, Explain.XllDownmixCoefficients, NodeValue.Integer(count, "coefficients", "coefficient"));
            }

            hasMask = Flag("HasChannelMask", Explain.XllHasMask, Topics.Lossless);
            if (hasMask)
            {
                start = _pos;
                mask = Hex("Channel Mask", common.MaskBits, Explain.XllChannelMask, Topics.DtsChannelArrangements);
                if (_tree?.Last is { } node)
                {
                    node.Add(_tree.Locator.Field("Speakers", start, _pos, ChannelLayouts.Speakers(Layouts.Speakers(Layouts.SpeakerMask(mask))), Explain.XllSpeakers, Topics.DtsChannelArrangements));
                }

                int named = System.Numerics.BitOperations.PopCount(mask);
                if (named != channels)
                {
                    Report(Issue.LosslessInconsistent, "Channel Mask", start, _pos, $"a channel set of {channels} channels has a channel mask of {named} speakers");
                }
            }
            else
            {
                Skip("Speaker Positions", 25 * channels, Explain.XllSpeakerPositions, NodeValue.Integer(channels, "speakers", "speaker"));
            }
        }
        else if (Flag("HasMappingCoefficients", Explain.XllHasMapping, Topics.Lossless))
        {
            read = false;
        }

        bands = 1;
        if (read && rate > 96000)
        {
            start = _pos;
            bool extra = Flag("HasExtraFrequencyBands", Explain.XllExtraBands, Topics.Lossless);
            bands = extra ? 4 : 2;
            _tree?.Field("Frequency Bands", start, _pos, NodeValue.Integer(bands, "bands", "band"), Explain.XllBands, Topics.Lossless);
        }

        if (!read)
        {
            // The channel-to-speaker mapping coefficients of a set that is not a loudspeaker feed are not read, nor what follows them.
            bands = 0;
            if (_pos < crcBit)
            {
                Skip("Mapping Coefficients and Band Parameters", crcBit - _pos, Explain.XllMappingParameters, NodeValue.Integer((crcBit - _pos + 7) / 8, "bytes"));
            }

            return new XllSetFacts(channels, resolution, width, rate, bands, 0, false, true);
        }

        // A set whose fields contradict each other says nothing about the layer.
        if (resolution > width || (hasMask && System.Numerics.BitOperations.PopCount(mask) != channels))
        {
            return null;
        }

        return new XllSetFacts(channels, resolution, width, rate, bands, mask, hasMask, primary);
    }

    // 8.4.2: the sizes of the band data of every frequency band, segment and channel set, its CRC16, and the band data they add up to;
    // then the extension data after it that FFmpeg's dca_xll.c reads as DTS:X (it aligns to 32 bits and compares one word).
    private void ReadXllNavigation(int at, int endBit, AssetFacts asset, int naviAt, XllCommon common, List<int> bandsOf, bool areSetsAccepted)
    {
        int maxBands = bandsOf.Max();
        int segments = 1 << common.SegmentsLog;
        long entries = 0;
        foreach (int bands in bandsOf)
        {
            entries += (long)bands * segments;
        }

        // FFmpeg refuses more than 1,024 entries counted as the most bands of any set times the segments times the sets.
        long counted = (long)maxBands * segments * bandsOf.Count;
        _pos = naviAt * 8;
        if (counted > NaviMaxEntries)
        {
            _tree?.Field("Navigation Index", _pos, Math.Min(_limit, endBit), NodeValue.Integer(counted, "entries", "entry"), Explain.XllNavigation, Topics.Lossless);
            Report(Issue.LosslessInconsistent, "Navigation Index", _pos, Math.Min(_limit, endBit), $"the navigation index would have {counted} entries (the most bands, times the segments, times the channel sets), more than the {NaviMaxEntries} that FFmpeg reads");
            return;
        }

        int naviBytes = (int)((entries * common.SegmentBits + 7) / 8) + 2;
        EnsureBytes(naviAt, naviBytes);
        int verdict = CrcVerdict(naviAt, naviBytes);
        _tree?.Open("Navigation Index", _pos, Explain.XllNavigation, Topics.Lossless);
        long total = 0;
        int depth = (_tree?.Depth ?? 1) - 1;
        int sizesStart = _pos;
        try
        {
            for (int band = 0; band < maxBands; band++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    foreach (int bands in bandsOf)
                    {
                        if (bands > band)
                        {
                            long size = (long)Read(common.SegmentBits);
                            total += size + 1;
                        }
                    }
                }
            }

            _tree?.Field("Band Data Sizes", sizesStart, _pos, NodeValue.Integer(entries, "sizes", "size"), Explain.XllNaviSizes, Topics.Lossless);
            if (Aligned(_pos) > _pos)
            {
                Skip("Padding", Aligned(_pos) - _pos, Explain.Reserved);
            }

            int crcStart = _pos;
            Crc("CRC", Explain.XllNaviCrc, verdict, "not checked");
            if (verdict == 0)
            {
                Report(Issue.CrcMismatch, "CRC", crcStart, _pos, "The lossless extension's navigation index CRC16 (8.4.2) fails");
            }
        }
        catch (EndOfStreamException)
        {
            if (!IsIncomplete)
            {
                ReportGroup(Issue.HeaderOverrun, "Navigation Index", naviAt * 8, Math.Min(_pos, _limit), "the data ends inside the navigation index");
            }

            _tree?.CloseTo(depth + 1, Here);
            _tree?.Close(Here, NodeValue.Integer(total, "bytes"));
            return;
        }

        _tree?.CloseTo(depth + 1, Here);
        _tree?.Close(Here, NodeValue.Integer(total, "bytes"));

        int dataAt = _pos / 8;
        long dataEnd = dataAt + total;
        long frameEnd = at + (long)common.FrameSize;
        if (dataEnd > frameEnd)
        {
            Report(Issue.LosslessInconsistent, "Frame Size", common.FrameSizeStart, common.FrameSizeEnd, $"the band data sizes of the navigation index end {dataEnd - frameEnd} bytes past the {common.FrameSize} bytes of the frame");
        }

        int available = _limit / 8;
        int dataLimit = (int)Math.Min(dataEnd, Math.Min(frameEnd, available));
        if (dataLimit > dataAt)
        {
            _tree?.Field("Band Data", dataAt * 8, dataLimit * 8, NodeValue.Integer(dataLimit - dataAt, "bytes"), Explain.XllBandData, Topics.Lossless);
        }

        // FFmpeg stops before it looks for the word at a navigation index whose CRC16 fails, and, earlier, in parse_sub_headers, at a channel
        // set it refuses (a failing CRC16, a source resolution beyond the storage width, a channel mask of another number of speakers than
        // channels); so does the analyzer. MediaInfo 24.01 still reads the word after such a set. A band data size of the frame's size or
        // more puts the end of the band data past the frame, where there is no room for the word.
        if (verdict == 1 && areSetsAccepted)
        {
            ReadXllExtensionData(at, endBit, asset, dataEnd, common);
        }
    }

    // dca_xll.c: when the frame is larger than its band data aligned to 32 bits (counted from the sync word), the word there is DTS:X's
    // (0x02000850) or DTS:X IMAX's (0xF14000D0 or 0xF14000D1). A word needs its 4 bytes here; FFmpeg reads it with 1 to 3 left too.
    private void ReadXllExtensionData(int at, int endBit, AssetFacts asset, long dataEnd, XllCommon common)
    {
        long aligned = at + ((dataEnd - at + 3) & ~3L);
        long frameEnd = at + (long)common.FrameSize;
        int available = Math.Min(_limit / 8, (int)Math.Min(frameEnd, int.MaxValue));
        if (aligned >= frameEnd || aligned + 4 > available)
        {
            return;
        }

        int start = (int)aligned;
        EnsureBytes(start, available - start);
        _pos = start * 8;
        uint word = Read(32);
        XllExtension kind = word == XSync ? XllExtension.X : (word >> 1) == (XImaxSync >> 1) ? XllExtension.XImax : XllExtension.None;
        asset.XllExtension = kind;
        _limit = Math.Min(_limit, available * 8);
        if (kind == XllExtension.None)
        {
            _tree?.Field("Data After Band Data", start * 8, _limit, NodeValue.Integer(available - start, "bytes"), Explain.XllTrailing, Topics.Lossless);
            return;
        }

        _pos = start * 8;
        _tree?.Open("Extension Data", _pos, Explain.XllExtensionData, Topics.Lossless);
        _tree?.Field("Sync Word", _pos, _pos + 32, NodeValue.Hex(word, 8), kind == XllExtension.X ? Explain.XllXSync : Explain.XllXImaxSync, Topics.Lossless);
        _pos += 32;
        if (_pos < _limit)
        {
            Skip("Data", _limit - _pos, Explain.XllExtensionPayload, NodeValue.Integer((_limit - _pos + 7) / 8, "bytes"));
        }

        _tree?.Close(Here, NodeValue.Integer(available - start, "bytes"));
    }
}
