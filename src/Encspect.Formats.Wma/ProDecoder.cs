using Encspect.Core;

namespace Encspect.Formats.Wma;

// Reads the syntax of a Windows Media Audio Professional frame as FFmpeg's wmaprodec.c does
// (decode_frame, decode_tilehdr, decode_subframe, decode_channel_transform, decode_scale_factors and
// decode_coeffs), keeping the bit positions of each part and the figures that describe them instead of
// the audio: no dequantization, channel transform or inverse transform is made. One instance reads one
// stream's frames, one at a time.
internal sealed class ProDecoder
{
    private const int HuffVec1Size = 101;

    private static readonly Lazy<Vlc> ScaleVlc = new(() => Vlc.FromLengths(ProTables.ScaleLengths, [.. ProTables.ScaleSymbols.Select(s => (int)s)]));
    private static readonly Lazy<Vlc> ScaleRunLevelVlc = new(() => Vlc.FromLengths(ProTables.ScaleRlLengths, [.. ProTables.ScaleRlSymbols.Select(s => (int)s)]));
    private static readonly Lazy<Vlc> Vector4Vlc = new(() => Vlc.FromLengths(ProTables.Vector4Lengths, [.. ProTables.Vector4Symbols.Select(s => (int)s)]));
    private static readonly Lazy<Vlc> Vector2Vlc = new(() => Vlc.FromLengths(ProTables.Vector2Lengths, [.. ProTables.Vector2Symbols.Select(s => (int)s)]));
    private static readonly Lazy<Vlc> Vector1Vlc = new(() => Vlc.FromLengths(ProTables.Vector1Lengths, [.. ProTables.Vector1Symbols.Select(s => (int)s)]));
    private static readonly Lazy<Vlc>[] CoefficientVlcs =
    [
        new(() => Vlc.FromLengths(ProTables.Coefficient0Lengths, [.. ProTables.Coefficient0Symbols.Select(s => (int)s)])),
        new(() => Vlc.FromLengths(ProTables.Coefficient1Lengths, [.. ProTables.Coefficient1Symbols.Select(s => (int)s)])),
    ];

    private readonly ProParameters _p;
    private readonly ChunkedList<ProSubframeRecord> _subframes;
    private readonly ChunkedList<ProChannelRecord> _channels;
    private readonly ChunkedList<ProGroupRecord> _groups;

    // Per channel, for the frame in progress.
    private readonly int[] _subframeCount = new int[ProParameters.MaxChannels];
    private readonly int[][] _subframeLength = [.. Enumerable.Range(0, ProParameters.MaxChannels).Select(_ => new int[ProParameters.MaxSubframes + 1])];
    private readonly int[] _decodedSamples = new int[ProParameters.MaxChannels];
    private readonly int[] _currentSubframe = new int[ProParameters.MaxChannels];
    private readonly bool[] _grouped = new bool[ProParameters.MaxChannels];
    private readonly bool[] _reuseScaleFactors = new bool[ProParameters.MaxChannels];

    // Per subframe.
    private readonly int[] _channelIndexes = new int[ProParameters.MaxChannels];
    private int _channelsForSubframe;
    private int _subframeLen;
    private int _numBands;
    private bool _parsedAll;
    private bool _transmitVectorCounts;

    public ProDecoder(ProParameters parameters, ChunkedList<ProSubframeRecord> subframes, ChunkedList<ProChannelRecord> channels, ChunkedList<ProGroupRecord> groups)
    {
        _p = parameters;
        _subframes = subframes;
        _channels = channels;
        _groups = groups;
    }

    public static Vlc ScaleFactorVlc => ScaleVlc.Value;

    public static Vlc CoefficientVlc(int index) => CoefficientVlcs[index].Value;

    // Reads the frame that starts at the bits' position. The bits end where the buffer's parts do; the
    // record gets the positions and the status. The trailer bit says whether another frame follows.
    public ProStatus DecodeFrame(Bits bits, ref ProFrameRecord frame, out bool more)
    {
        more = false;
        frame.StartBit = bits.Position;
        frame.FirstSubframe = _subframes.Count;
        if (_p.HasLengthPrefix)
        {
            frame.Length = (int)bits.Read(_p.Log2FrameSize);
        }

        frame.LengthEnd = bits.Position;

        if (!DecodeTileHeader(bits))
        {
            frame.TileEnd = frame.PostEnd = frame.HeaderEnd = frame.SubframesEnd = frame.EndBit = bits.Position;
            return ProStatus.TileHeader;
        }

        frame.TileEnd = bits.Position;

        if (_p.Channels > 1 && bits.ReadBit() == 1)
        {
            frame.Flags |= ProFrameRecord.HasPostprocess;
            if (bits.ReadBit() == 1)
            {
                frame.Flags |= ProFrameRecord.PostprocessMatrix;
                bits.Skip(4 * _p.Channels * _p.Channels);
            }
        }

        frame.PostEnd = bits.Position;

        if (_p.HasDynamicRangeCompression)
        {
            frame.Flags |= ProFrameRecord.HasDrc;
            frame.DrcGain = (byte)bits.Read(8);
        }

        if (bits.ReadBit() == 1)
        {
            frame.Flags |= ProFrameRecord.HasTrimInfo;
            int trimBits = ProParameters.Log2(_p.SamplesPerFrame * 2);
            if (bits.ReadBit() == 1)
            {
                frame.TrimStart = (ushort)bits.Read(trimBits);
            }

            if (bits.ReadBit() == 1)
            {
                frame.TrimEnd = (ushort)bits.Read(trimBits);
            }
        }

        frame.HeaderEnd = bits.Position;

        for (int c = 0; c < _p.Channels; c++)
        {
            _decodedSamples[c] = 0;
            _currentSubframe[c] = 0;
            _reuseScaleFactors[c] = false;
        }

        _parsedAll = false;
        int count = 0;
        ProStatus status = ProStatus.Ok;
        while (!_parsedAll)
        {
            status = DecodeSubframe(bits, count);
            count++;
            if (status != ProStatus.Ok)
            {
                break;
            }
        }

        frame.SubframeCount = (ushort)Math.Min(count, ushort.MaxValue);
        frame.SubframesEnd = bits.Position;
        if (status != ProStatus.Ok)
        {
            frame.EndBit = bits.Position;
            return status;
        }

        if (_p.HasLengthPrefix)
        {
            int consumed = bits.Position - frame.StartBit;
            if (frame.Length != consumed + 2)
            {
                frame.EndBit = bits.Position;
                return ProStatus.LengthPrefix;
            }

            bits.Skip(frame.Length - consumed - 1);
        }
        else
        {
            while (bits.Position < bits.Length && bits.ReadBit() == 0)
            {
            }
        }

        more = bits.ReadBit() == 1;
        frame.EndBit = bits.Position;
        if (more)
        {
            frame.Flags |= ProFrameRecord.More;
        }

        return bits.IsOverrun ? ProStatus.Overrun : ProStatus.Ok;
    }

    private bool DecodeTileHeader(Bits bits)
    {
        int channels = _p.Channels;
        Span<int> numSamples = stackalloc int[ProParameters.MaxChannels];
        Span<bool> contains = stackalloc bool[ProParameters.MaxChannels];
        int channelsForCurrent = channels;
        bool fixedLayout = false;
        int minLength = 0;
        for (int c = 0; c < channels; c++)
        {
            _subframeCount[c] = 0;
        }

        if (_p.MaxSubframeCount == 1 || bits.ReadBit() == 1)
        {
            fixedLayout = true;
        }

        do
        {
            for (int c = 0; c < channels; c++)
            {
                if (numSamples[c] == minLength)
                {
                    contains[c] = fixedLayout || channelsForCurrent == 1 || minLength == _p.SamplesPerFrame - _p.MinSamplesPerSubframe || bits.ReadBit() == 1;
                }
                else
                {
                    contains[c] = false;
                }
            }

            int length = DecodeSubframeLength(bits, minLength);
            if (length <= 0)
            {
                return false;
            }

            minLength += length;
            for (int c = 0; c < channels; c++)
            {
                if (contains[c])
                {
                    if (_subframeCount[c] >= ProParameters.MaxSubframes)
                    {
                        return false;
                    }

                    _subframeLength[c][_subframeCount[c]] = length;
                    numSamples[c] += length;
                    _subframeCount[c]++;
                    if (numSamples[c] > _p.SamplesPerFrame)
                    {
                        return false;
                    }
                }
                else if (numSamples[c] <= minLength)
                {
                    if (numSamples[c] < minLength)
                    {
                        channelsForCurrent = 0;
                        minLength = numSamples[c];
                    }

                    ++channelsForCurrent;
                }
            }
        }
        while (minLength < _p.SamplesPerFrame);

        return true;
    }

    private int DecodeSubframeLength(Bits bits, int offset)
    {
        if (offset == _p.SamplesPerFrame - _p.MinSamplesPerSubframe)
        {
            return _p.MinSamplesPerSubframe;
        }

        if (bits.Remaining < 1)
        {
            return -1;
        }

        int shift = 0;
        if (_p.HasMaxSubframeLengthBit)
        {
            if (bits.ReadBit() == 1)
            {
                shift = 1 + (int)bits.Read(_p.SubframeLengthBits - 1);
            }
        }
        else
        {
            shift = (int)bits.Read(_p.SubframeLengthBits);
        }

        int length = _p.SamplesPerFrame >> shift;
        return length < _p.MinSamplesPerSubframe || length > _p.SamplesPerFrame ? -1 : length;
    }

    private ProStatus DecodeSubframe(Bits bits, int index)
    {
        int channels = _p.Channels;
        var subframe = new ProSubframeRecord { StartBit = bits.Position, FirstChannel = _channels.Count, FirstGroup = _groups.Count };
        int offset = _p.SamplesPerFrame;
        int length = _p.SamplesPerFrame;
        int totalSamples = _p.SamplesPerFrame * channels;
        for (int c = 0; c < channels; c++)
        {
            _grouped[c] = false;
            if (offset > _decodedSamples[c])
            {
                offset = _decodedSamples[c];
                length = _subframeLength[c][_currentSubframe[c]];
            }
        }

        _channelsForSubframe = 0;
        for (int c = 0; c < channels; c++)
        {
            int current = _currentSubframe[c];
            totalSamples -= _decodedSamples[c];
            if (offset == _decodedSamples[c] && length == _subframeLength[c][current])
            {
                totalSamples -= _subframeLength[c][current];
                _decodedSamples[c] += _subframeLength[c][current];
                _channelIndexes[_channelsForSubframe++] = c;
            }
        }

        if (totalSamples == 0)
        {
            _parsedAll = true;
        }

        subframe.Offset = (ushort)offset;
        subframe.Length = (ushort)length;
        subframe.ChannelCount = (byte)_channelsForSubframe;
        for (int i = 0; i < _channelsForSubframe; i++)
        {
            subframe.ChannelMask |= (byte)(1 << _channelIndexes[i]);
        }

        int tableIndex = ProParameters.Log2(_p.SamplesPerFrame / length);
        _numBands = tableIndex < _p.BandCounts.Length ? _p.BandCounts[tableIndex] : _p.BandCounts[^1];
        _subframeLen = length;
        int escapeLength = ProParameters.Log2(length - 1) + 1;

        var records = new ProChannelRecord[_channelsForSubframe];
        for (int i = 0; i < _channelsForSubframe; i++)
        {
            records[i].Channel = (byte)_channelIndexes[i];
        }

        ProStatus status = DecodeSubframeBody(bits, ref subframe, records, escapeLength);
        foreach (ProChannelRecord record in records)
        {
            _channels.Add(record);
        }

        subframe.EndBit = bits.Position;
        subframe.ChannelCount = (byte)_channelsForSubframe;
        _subframes.Add(subframe);
        if (status != ProStatus.Ok)
        {
            return status;
        }

        for (int i = 0; i < _channelsForSubframe; i++)
        {
            int c = _channelIndexes[i];
            if (_currentSubframe[c] >= _subframeCount[c])
            {
                return ProStatus.BrokenSubframe;
            }

            _currentSubframe[c]++;
        }

        _ = index;
        return ProStatus.Ok;
    }

    private ProStatus DecodeSubframeBody(Bits bits, ref ProSubframeRecord subframe, ProChannelRecord[] records, int escapeLength)
    {
        // The extended header (fill bits).
        if (bits.ReadBit() == 1)
        {
            subframe.Flags |= ProSubframeRecord.HasExtendedHeader;
            int fill = (int)bits.Read(2);
            if (fill == 0)
            {
                int bitsLength = (int)bits.Read(4);
                fill = (bitsLength == 0 ? 0 : (int)bits.Read(bitsLength)) + 1;
            }

            subframe.FillBits = fill;
            if (bits.Position + fill > bits.Length)
            {
                subframe.FillEnd = bits.Position;
                return ProStatus.FillBits;
            }

            bits.Skip(fill);
        }

        subframe.FillEnd = bits.Position;

        // A reserved bit.
        if (bits.ReadBit() == 1)
        {
            subframe.TransformEnd = subframe.FlagsEnd = subframe.VectorEnd = subframe.StepEnd = bits.Position;
            return ProStatus.ReservedBit;
        }

        ProStatus status = DecodeChannelTransform(bits);
        subframe.GroupCount = (byte)(_groups.Count - subframe.FirstGroup);
        subframe.TransformEnd = bits.Position;
        if (status != ProStatus.Ok)
        {
            subframe.FlagsEnd = subframe.VectorEnd = subframe.StepEnd = bits.Position;
            return status;
        }

        bool transmitAny = false;
        for (int i = 0; i < _channelsForSubframe; i++)
        {
            if (bits.ReadBit() == 1)
            {
                records[i].Flags |= ProChannelRecord.Transmits;
                transmitAny = true;
            }
        }

        subframe.FlagsEnd = subframe.VectorEnd = subframe.StepEnd = bits.Position;
        if (transmitAny)
        {
            subframe.Flags |= ProSubframeRecord.HasCoefficients;
            ProStatus coded = DecodeQuantization(bits, ref subframe, records);
            if (coded != ProStatus.Ok)
            {
                return coded;
            }
        }

        // The coefficients of each channel.
        for (int i = 0; i < _channelsForSubframe; i++)
        {
            ref ProChannelRecord record = ref records[i];
            record.CoefficientStart = record.CoefficientEnd = bits.Position;
            if (record.Has(ProChannelRecord.Transmits) && bits.Position < bits.Length)
            {
                DecodeCoefficients(bits, ref record, escapeLength);
                record.CoefficientEnd = bits.Position;
            }
        }

        return bits.IsOverrun ? ProStatus.Overrun : ProStatus.Ok;
    }

    private ProStatus DecodeChannelTransform(Bits bits)
    {
        int channels = _p.Channels;
        if (channels <= 1)
        {
            return ProStatus.Ok;
        }

        int remaining = _channelsForSubframe;
        if (bits.ReadBit() == 1)
        {
            return ProStatus.ChannelTransform;
        }

        int groupCount = 0;
        while (remaining > 0 && groupCount < _channelsForSubframe)
        {
            var group = new ProGroupRecord { Start = bits.Position };
            int memberCount = 0;
            byte members = 0;
            if (remaining > 2)
            {
                for (int i = 0; i < _channelsForSubframe; i++)
                {
                    int channel = _channelIndexes[i];
                    if (!_grouped[channel] && bits.ReadBit() == 1)
                    {
                        memberCount++;
                        _grouped[channel] = true;
                        members |= (byte)(1 << channel);
                    }
                }
            }
            else
            {
                memberCount = remaining;
                for (int i = 0; i < _channelsForSubframe; i++)
                {
                    int channel = _channelIndexes[i];
                    if (!_grouped[channel])
                    {
                        members |= (byte)(1 << channel);
                    }

                    _grouped[channel] = true;
                }
            }

            bool transform = false;
            if (memberCount == 2)
            {
                if (bits.ReadBit() == 1)
                {
                    if (bits.ReadBit() == 1)
                    {
                        group.End = bits.Position;
                        group.ChannelMask = members;
                        group.ChannelCount = (byte)memberCount;
                        _groups.Add(group);
                        return ProStatus.ChannelTransform;
                    }
                }
                else
                {
                    transform = true;
                }
            }
            else if (memberCount > 2 && bits.ReadBit() == 1)
            {
                transform = true;
                if (bits.ReadBit() == 1)
                {
                    // A matrix of rotations: n(n-1)/2 angles of 6 bits and n signs.
                    bits.Skip(6 * (memberCount * (memberCount - 1) / 2));
                    bits.Skip(memberCount);
                }
            }

            if (transform)
            {
                if (bits.ReadBit() == 0)
                {
                    for (int band = 0; band < _numBands; band++)
                    {
                        if (bits.ReadBit() == 1)
                        {
                            group.BandMask |= 1u << band;
                            group.BandsOn++;
                        }
                    }
                }
                else
                {
                    group.BandMask = (uint)((1UL << _numBands) - 1);
                    group.BandsOn = (byte)_numBands;
                }
            }

            group.End = bits.Position;
            group.ChannelMask = members;
            group.ChannelCount = (byte)memberCount;
            group.Transform = (byte)(transform ? 1 : 0);
            _groups.Add(group);
            remaining -= memberCount;
            groupCount++;
        }

        return ProStatus.Ok;
    }

    private ProStatus DecodeQuantization(Bits bits, ref ProSubframeRecord subframe, ProChannelRecord[] records)
    {
        int quantStep = 90 * _p.BitsPerSample >> 4;

        _transmitVectorCounts = bits.ReadBit() == 1;
        if (_transmitVectorCounts)
        {
            subframe.Flags |= ProSubframeRecord.SendsVectorCounts;
            int numBits = ProParameters.Log2((_subframeLen + 3) / 4) + 1;
            for (int i = 0; i < _channelsForSubframe; i++)
            {
                int count = (int)bits.Read(numBits) << 2;
                if (count > _subframeLen)
                {
                    subframe.VectorEnd = subframe.StepEnd = bits.Position;
                    return ProStatus.VectorCount;
                }

                records[i].VectorCoefficients = (ushort)count;
            }
        }
        else
        {
            for (int i = 0; i < _channelsForSubframe; i++)
            {
                records[i].VectorCoefficients = (ushort)_subframeLen;
            }
        }

        subframe.VectorEnd = bits.Position;

        int step = (int)bits.Read(6);
        step = (step << 26) >> 26;
        quantStep += step;
        if (step is -32 or 31)
        {
            int sign = (step == 31 ? 1 : 0) - 1;
            int quant = 0;
            while (bits.Position + 5 < bits.Length && (step = (int)bits.Read(5)) == 31)
            {
                quant += 31;
            }

            quantStep += ((quant + step) ^ sign) - sign;
        }

        subframe.QuantStep = (short)quantStep;

        if (_channelsForSubframe == 1)
        {
            records[0].QuantStep = (short)quantStep;
        }
        else
        {
            int modifierLength = (int)bits.Read(3);
            for (int i = 0; i < _channelsForSubframe; i++)
            {
                int channelStep = quantStep;
                if (bits.ReadBit() == 1)
                {
                    channelStep += modifierLength != 0 ? (int)bits.Read(modifierLength) + 1 : 1;
                }

                records[i].QuantStep = (short)channelStep;
            }
        }

        subframe.StepEnd = bits.Position;
        return DecodeScaleFactors(bits, records);
    }

    private ProStatus DecodeScaleFactors(Bits bits, ProChannelRecord[] records)
    {
        for (int i = 0; i < _channelsForSubframe; i++)
        {
            int c = _channelIndexes[i];
            ref ProChannelRecord record = ref records[i];
            record.ScaleStart = bits.Position;
            if (_currentSubframe[c] == 0 || bits.ReadBit() == 1)
            {
                record.Flags |= ProChannelRecord.ScalesSent;
                if (!_reuseScaleFactors[c])
                {
                    record.ScaleFactorStep = (byte)(bits.Read(2) + 1);
                    for (int band = 0; band < _numBands; band++)
                    {
                        if (ScaleVlc.Value.Read(bits) < 0)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    record.Flags |= ProChannelRecord.ScalesDifferential;
                    for (int band = 0; band < _numBands; band++)
                    {
                        int index = ScaleRunLevelVlc.Value.Read(bits);
                        int skip;
                        if (index == 0)
                        {
                            uint code = bits.Read(14);
                            skip = (int)((code & 0x3F) >> 1);
                        }
                        else if (index == 1)
                        {
                            break;
                        }
                        else
                        {
                            skip = index < 0 ? 0 : ProTables.ScaleRlRun[index];
                            bits.ReadBit();
                        }

                        band += skip;
                        if (band >= _numBands)
                        {
                            record.ScaleEnd = bits.Position;
                            return ProStatus.ScaleFactors;
                        }
                    }
                }

                _reuseScaleFactors[c] = true;
            }

            record.ScaleEnd = bits.Position;
        }

        return ProStatus.Ok;
    }

    private void DecodeCoefficients(Bits bits, ref ProChannelRecord record, int escapeLength)
    {
        int table = bits.ReadBit();
        if (table == 1)
        {
            record.Flags |= ProChannelRecord.SecondCodebook;
        }

        Vlc vlc = CoefficientVlcs[table].Value;
        ushort[] run = table == 1 ? ProTables.Coefficient1Run : ProTables.Coefficient0Run;
        int current = 0;
        int zeros = 0;
        bool runLevelMode = false;
        int nonZero = 0;
        int escapes = 0;
        Span<int> values = stackalloc int[4];
        bool error = false;

        while ((_transmitVectorCounts || !runLevelMode) && current + 3 < record.VectorCoefficients)
        {
            int index = Vector4Vlc.Value.Read(bits) - 1;
            if (index < 0 || index > 0xFFFF)
            {
                for (int i = 0; i < 4; i += 2)
                {
                    index = Vector2Vlc.Value.Read(bits) - 1;
                    if (index < 0)
                    {
                        int first = Vector1Vlc.Value.Read(bits);
                        if (first == HuffVec1Size - 1)
                        {
                            first += (int)LargeValue(bits);
                        }

                        int second = Vector1Vlc.Value.Read(bits);
                        if (second == HuffVec1Size - 1)
                        {
                            second += (int)LargeValue(bits);
                        }

                        if (first < 0 || second < 0)
                        {
                            error = true;
                        }

                        values[i] = Math.Max(first, 0);
                        values[i + 1] = Math.Max(second, 0);
                    }
                    else
                    {
                        values[i] = index >> 4;
                        values[i + 1] = index & 0xF;
                    }
                }
            }
            else
            {
                values[0] = index >> 12;
                values[1] = (index >> 8) & 0xF;
                values[2] = (index >> 4) & 0xF;
                values[3] = index & 0xF;
            }

            for (int i = 0; i < 4; i++)
            {
                if (values[i] != 0)
                {
                    bits.ReadBit();
                    zeros = 0;
                    nonZero++;
                }
                else
                {
                    runLevelMode |= ++zeros > _subframeLen >> 8;
                }

                current++;
            }
        }

        if (current < _subframeLen)
        {
            int offset = current;
            for (; offset < _subframeLen; offset++)
            {
                int code = vlc.Read(bits);
                if (code > 1)
                {
                    offset += run[code];
                    bits.ReadBit();
                    nonZero++;
                }
                else if (code == 1)
                {
                    break;
                }
                else
                {
                    if (code < 0)
                    {
                        error = true;
                    }

                    LargeValue(bits);
                    if (bits.ReadBit() == 1)
                    {
                        if (bits.ReadBit() == 1)
                        {
                            if (bits.ReadBit() == 1)
                            {
                                error = true;
                                break;
                            }

                            offset += (int)bits.Read(escapeLength) + 4;
                        }
                        else
                        {
                            offset += (int)bits.Read(2) + 1;
                        }
                    }

                    bits.ReadBit();
                    nonZero++;
                    escapes++;
                }
            }

            if (offset > _subframeLen)
            {
                error = true;
            }
        }

        record.NonZero = (ushort)Math.Min(nonZero, ushort.MaxValue);
        record.Escapes = (ushort)Math.Min(escapes, ushort.MaxValue);
        if (error)
        {
            record.Flags |= ProChannelRecord.CoefficientError;
        }
    }

    // wma_common.c: ff_wma_get_large_val.
    private static uint LargeValue(Bits bits)
    {
        int count = 8;
        if (bits.ReadBit() == 1)
        {
            count += 8;
            if (bits.ReadBit() == 1)
            {
                count += 8;
                if (bits.ReadBit() == 1)
                {
                    count += 7;
                }
            }
        }

        return bits.Read(count);
    }
}
