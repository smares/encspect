using Encspect.Core;

namespace Encspect.Formats.Dts;

internal sealed partial class UnitSyntax
{
    // The bits of SEL by ABITS 1 to 10 (Table 5-21), and how many SEL values call for an ADJ.
    private static readonly int[] SelBits = [1, 2, 2, 2, 2, 3, 3, 3, 3, 3];
    private static readonly int[] AdjGroup = [1, 3, 3, 3, 3, 7, 7, 7, 7, 7];
    private static readonly double[] Adjustment = [1.0, 1.125, 1.25, 1.4375];

    // 5.4.3 Table 5-21: the primary audio coding header, or 6.4.3 Table 6-18: XCh's audio header, which has no SUBFS. Returns
    // the number of channels it codes.
    private int ReadAudioHeader(bool isCore, CoreHeader header)
    {
        bool hasCrc = isCore ? header.HasCrc : Facts.HasCore && Facts.Core.HasCrc;
        int amode = isCore ? header.Amode : 0;
        _tree?.Open(isCore ? "Primary Audio Coding Header" : "Audio Header", _pos, isCore ? Explain.AudioHeader : Explain.XchAudioHeader, Topics.CoreCodingHeader);
        int start = _pos;
        int subframes = 0;
        if (isCore)
        {
            subframes = (int)Read(4) + 1;
            _tree?.Field("Subframes", start, _pos, NodeValue.Integer(subframes, "subframes", "subframe"), Explain.Subfs, Topics.CoreCodingHeader);
            if (header.IsNormalFrame && (header.Blocks < 8 * subframes || header.Blocks > 32 * subframes))
            {
                Report(Issue.SubframeCount, "Subframes", start, _pos, $"{subframes} subframes cannot hold {header.Blocks} blocks of 32 samples, which need 8 to 32 blocks each");
            }
        }

        start = _pos;
        int channels = (int)Read(3) + 1;
        _tree?.Field(isCore ? "Primary Channels" : "Extension Channels", start, _pos, NodeValue.Integer(channels, "channels", "channel"), isCore ? Explain.Pchs : Explain.XchPchs, Topics.CoreCodingHeader);
        if (isCore)
        {
            int expected = Tables.CoreChannels(amode);
            if (channels > 5 || (expected > 0 && channels != expected))
            {
                Report(Issue.ChannelCount, "Primary Channels", start, _pos, channels > 5
                    ? $"{channels} primary channels, where 5.4.3 allows up to 5"
                    : $"{channels} primary channels for the arrangement {Tables.ArrangementShort(amode)}, which has {expected}");
            }

            Facts.PrimaryChannels = channels;
            Facts.Subframes = subframes;
        }

        if (channels > 8)
        {
            throw new EndOfStreamException();
        }

        PerChannel("Subband Activity Count", 5, Explain.Subs, channels, _subs, v => NodeValue.Integer(v + 2, "subbands", "subband"), Topics.CoreCodingHeader,
            (ch, v) => v + 2 > 32 ? $"channel {ch + 1} has {v + 2} active subbands, where there are 32" : null, Issue.SubbandCount);
        PerChannel("High Frequency VQ Start Subband", 5, Explain.Vqsub, channels, _vqsub, v => NodeValue.Integer(v + 1, "subbands", "subband"), Topics.CoreCodingHeader);
        PerChannel("Joint Intensity Coding Index", 3, Explain.Joinx, channels, _joinx, v => v == 0 ? "0 (disabled)" : $"{v} (copies channel {v})", Topics.CoreCodingHeader,
            (ch, v) => v > ch ? $"channel {ch + 1} copies channel {v}, which does not come before it" : null, Issue.JointSource);
        PerChannel("Transient Mode Code Book", 2, Explain.Thuff, channels, _thuff, v => ((char)('A' + v)) + "4", Topics.CoreCodingHeader);
        PerChannel("Scale Factor Code Book", 3, Explain.Shuff, channels, _shuff, ScaleFactorBook, Topics.CoreCodingHeader,
            (_, v) => v == 7 ? "SHUFF 7 is invalid (Table 5-24)" : null, Issue.ReservedValue);
        PerChannel("Bit Allocation Quantizer Select", 3, Explain.Bhuff, channels, _bhuff, BitAllocationBook, Topics.CoreCodingHeader,
            (_, v) => v == 7 ? "BHUFF 7 is invalid (Table 5-25)" : null, Issue.ReservedValue);
        _tree?.Open("Quantization Index Codebook Select", _pos, Explain.Sel, Topics.CoreCodingHeader);
        for (int n = 0; n < 10; n++)
        {
            _tree?.Open($"ABITS {n + 1}", _pos, Explain.Sel, Topics.CoreCodingHeader);
            for (int ch = 0; ch < channels; ch++)
            {
                int at = _pos;
                _sel[ch][n] = (int)Read(SelBits[n]);
                _tree?.Field($"Channel {ch + 1}", at, _pos, NodeValue.Integer(_sel[ch][n]), Explain.Sel, Topics.CoreCodingHeader);
            }

            _tree?.Close(Here);
        }

        _tree?.Close(Here);
        ReadAdjustments(channels);
        if (hasCrc)
        {
            Crc("Audio Header CRC", Explain.Ahcrc, 2, "not verified: 5.4.3 says the CRC value test shall not be applied");
        }

        _tree?.Close(Here);
        return channels;
    }

    // ADJ for each quantizer whose SEL names a Huffman code (Table 5-21); nothing where no channel has one.
    private void ReadAdjustments(int channels)
    {
        bool anyAdjustment = false;
        for (int n = 0; n < 10 && !anyAdjustment; n++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                anyAdjustment |= _sel[ch][n] < AdjGroup[n];
            }
        }

        if (!anyAdjustment)
        {
            return;
        }

        _tree?.Open("Scale Factor Adjustment Index", _pos, Explain.Adj, Topics.CoreCodingHeader);
        for (int n = 0; n < 10; n++)
        {
            bool any = false;
            for (int ch = 0; ch < channels; ch++)
            {
                any |= _sel[ch][n] < AdjGroup[n];
            }

            if (!any)
            {
                continue;
            }

            _tree?.Open($"ABITS {n + 1}", _pos, Explain.Adj, Topics.CoreCodingHeader);
            for (int ch = 0; ch < channels; ch++)
            {
                if (_sel[ch][n] >= AdjGroup[n])
                {
                    continue;
                }

                int at = _pos;
                int adj = (int)Read(2);
                _tree?.Field($"Channel {ch + 1}", at, _pos, NodeValue.Decimal(Adjustment[adj], 4), Explain.Adj, Topics.CoreCodingHeader);
            }

            _tree?.Close(Here);
        }

        _tree?.Close(Here);
    }

    private void PerChannel(string name, int width, string description, int channels, int[] values, Func<int, NodeValue> value, string topic,
        Func<int, int, string?>? check = null, Issue issue = Issue.ReservedValue)
    {
        _tree?.Open(name, _pos, description, topic);
        for (int ch = 0; ch < channels; ch++)
        {
            int start = _pos;
            values[ch] = (int)Read(width);
            _tree?.Field($"Channel {ch + 1}", start, _pos, value(values[ch]), description, topic);
            if (check?.Invoke(ch, values[ch]) is { } detail)
            {
                Report(issue, $"Channel {ch + 1}", start, _pos, detail);
            }
        }

        _tree?.Close(Here);
    }

    // Table 5-24.
    private static NodeValue ScaleFactorBook(int v) => v switch
    {
        <= 4 => $"{v}: {(char)('S' + 0)}{(char)('A' + v)}129, 6-bit table",
        5 => "5: 6-bit linear, 6-bit table",
        6 => "6: 7-bit linear, 7-bit table",
        _ => "7 (invalid)",
    };

    // Table 5-25.
    private static NodeValue BitAllocationBook(int v) => v switch
    {
        <= 4 => $"{v}: {(char)('A' + v)}12",
        5 => "5: linear, 4 bits",
        6 => "6: linear, 5 bits",
        _ => "7 (invalid)",
    };
}
