using System.Numerics;

namespace Encspect.Formats.Wma;

// The codebooks of Windows Media Audio Professional, built once from the lengths ProTables holds.
internal static class ProCodes
{
    public static readonly Vlc Scale = Build(ProTables.ScaleLengths, ProTables.ScaleSymbols.Select(s => (int)s));
    public static readonly Vlc ScaleRl = Build(ProTables.ScaleRlLengths, ProTables.ScaleRlSymbols.Select(s => (int)s));
    public static readonly Vlc Coefficient0 = Build(ProTables.Coefficient0Lengths, ProTables.Coefficient0Symbols.Select(s => (int)s));
    public static readonly Vlc Coefficient1 = Build(ProTables.Coefficient1Lengths, ProTables.Coefficient1Symbols.Select(s => (int)s));
    public static readonly Vlc Vector4 = Build(ProTables.Vector4Lengths, ProTables.Vector4Symbols.Select(s => (int)s));
    public static readonly Vlc Vector2 = Build(ProTables.Vector2Lengths, ProTables.Vector2Symbols.Select(s => (int)s));
    public static readonly Vlc Vector1 = Build(ProTables.Vector1Lengths, ProTables.Vector1Symbols.Select(s => (int)s));

    private static Vlc Build(byte[] lengths, IEnumerable<int> symbols) => Vlc.FromLengths(lengths, symbols.ToArray());
}

internal enum ProFault
{
    None,
    TileHeader,
    FillBits,
    ReservedBit,
    ChannelTransform,
    VectorCount,
    ScaleFactorRun,
    NeedsMoreBits,
    LengthPrefix,
}

// Figures over the subframes read, which the statistics sum up: every subframe counts, also those of frames that turn out invalid.
internal sealed class ProTally
{
    public long Subframes;
    public long ChannelSubframes;
    public long[] Sizes = new long[8];
    public long Groups;
    public long GroupsWithTransform;
    public long BandsTransformed;
    public long BandsOfTransformedGroups;
    public long ExtendedHeaders;
    public long VectorCounted;
    public long CodedChannels;
    public long ScaleFactorsSent;
    public long ScaleFactorDifferences;
    public long SecondCodebook;
    public long MinStep = long.MaxValue;
    public long MaxStep = long.MinValue;
    public long NonZero;
    public long Escapes;
    public long Damaged;
}

// Reads the syntax of one frame (docs/design/wma-pro-format.md, "Frame" and "Subframe"): it finds where every field ends, which is all the
// layout needs, and keeps what the nodes show.
internal sealed class ProFrameReader(ProParameters parameters, ProTally tally) : IFrameSyntax
{
    private const int MaxPerChannel = 32;

    private readonly int _channels = parameters.Channels;
    private readonly ushort[] _lengths = new ushort[parameters.Channels * (MaxPerChannel + 1)];
    private readonly int[] _count = new int[parameters.Channels];
    private readonly int[] _done = new int[parameters.Channels];
    private readonly int[] _next = new int[parameters.Channels];
    private readonly bool[] _hasSet = new bool[parameters.Channels];
    private readonly int[] _members = new int[parameters.Channels];
    private readonly int[] _vectorCounts = new int[parameters.Channels];
    private readonly bool[] _sends = new bool[parameters.Channels];

    private Bits _bits = null!;
    private int _limit;
    private TraceWriter _trace = null!;
    private bool _hasCounts;

    // The subframes read so far, for the statistics of the frame level (the number is also in the trace).
    public int SubframesRead { get; private set; }

    public FrameOutcome Read(Bits bits, int start, int limit, int declaredLength, TraceWriter trace)
    {
        _bits = bits;
        _limit = limit;
        _trace = trace;
        trace.Clear();
        SubframesRead = 0;
        var result = new FrameOutcome();
        bits.Position = start;
        ProParameters p = parameters;

        if (p.HasLengthPrefix)
        {
            bits.Position += p.CountWidth;
            trace.Put(Tag.FrameLength);
            trace.Put(declaredLength);
        }

        int tileStart = bits.Position;
        bool isTileValid = ReadTileHeader();
        trace.Put(Tag.TileHeader);
        trace.Put(bits.Position - tileStart);
        if (!isTileValid)
        {
            return Finish(ref result, ProFault.TileHeader, start);
        }

        if (_channels > 1)
        {
            int at = bits.Position;
            int first = bits.ReadBit();
            result.HasPostprocessing = first != 0;
            if (first != 0 && bits.ReadBit() != 0)
            {
                bits.Skip(4 * _channels * _channels);
            }

            trace.Put(Tag.Postprocessing);
            trace.Put(bits.Position - at);
            trace.Put(first);
        }

        if (p.HasDynamicRange)
        {
            int gain = (int)bits.Read(8);
            trace.Put(Tag.DynamicRange);
            trace.Put(gain);
        }

        int trimAt = bits.Position;
        bool hasTrim = bits.ReadBit() != 0;
        if (hasTrim)
        {
            if (bits.ReadBit() != 0)
            {
                result.TrimStart = (int)bits.Read(p.TrimBits);
            }

            if (bits.ReadBit() != 0)
            {
                result.TrimEnd = (int)bits.Read(p.TrimBits);
            }
        }

        trace.Put(Tag.Trim);
        trace.Put(bits.Position - trimAt);
        trace.Put(hasTrim ? 1 : 0);
        trace.Put(result.TrimStart);
        trace.Put(result.TrimEnd);

        ProFault fault = ReadSubframes();
        if (fault != ProFault.None)
        {
            return Finish(ref result, fault, start);
        }

        result.AreSubframesRead = true;
        if (p.HasLengthPrefix)
        {
            int used = bits.Position - start;
            if (used + 2 > Math.Min(declaredLength, limit - start))
            {
                return Finish(ref result, ProFault.NeedsMoreBits, start);
            }

            if (used + 2 != declaredLength)
            {
                return Finish(ref result, ProFault.LengthPrefix, start);
            }

            bits.Skip(1);
            trace.Put(Tag.Padding);
            trace.Put(1);
        }
        else
        {
            int count = 0;
            while (bits.Position < limit)
            {
                count++;
                if (bits.ReadBit() != 0)
                {
                    break;
                }
            }

            trace.Put(Tag.Padding);
            trace.Put(count);
        }

        result.Trailer = bits.ReadBit() != 0;
        if (bits.Position > limit)
        {
            return Finish(ref result, ProFault.NeedsMoreBits, start);
        }

        trace.Put(Tag.Trailer);
        trace.Put(result.Trailer ? 1 : 0);
        return Finish(ref result, ProFault.None, start);
    }

    private FrameOutcome Finish(ref FrameOutcome result, ProFault fault, int start)
    {
        result.Fault = (int)fault;
        result.Bits = _bits.Position - start;
        _trace.Put(Tag.End);
        return result;
    }

    private bool ReadTileHeader()
    {
        ProParameters p = parameters;
        int frame = p.FrameLength;
        int min = p.MinSubframe;
        Array.Clear(_count);
        Array.Clear(_done);
        Array.Clear(_next);
        Array.Clear(_hasSet);
        bool isAligned = p.MaxSubframes == 1 || _bits.ReadBit() != 0;
        var takes = new bool[_channels];
        while (true)
        {
            int smallest = int.MaxValue;
            bool isFinished = true;
            for (int c = 0; c < _channels; c++)
            {
                smallest = Math.Min(smallest, _done[c]);
                isFinished &= _done[c] == frame;
            }

            if (isFinished)
            {
                return true;
            }

            int candidates = 0;
            for (int c = 0; c < _channels; c++)
            {
                if (_done[c] == smallest)
                {
                    candidates++;
                }
            }

            bool isLast = smallest == frame - min;
            bool any = false;
            for (int c = 0; c < _channels; c++)
            {
                takes[c] = false;
                if (_done[c] != smallest)
                {
                    continue;
                }

                takes[c] = isAligned || candidates == 1 || isLast || _bits.ReadBit() != 0;
                any |= takes[c];
            }

            int length;
            if (isLast)
            {
                length = min;
            }
            else
            {
                if (_bits.Position >= _limit)
                {
                    return false;
                }

                int width = p.LengthCodeWidth;
                if (p.SubframeCountBits is 2 or 4)
                {
                    length = _bits.ReadBit() == 0 ? frame : frame >> ((int)_bits.Read(width - 1) + 1);
                }
                else
                {
                    length = frame >> (int)_bits.Read(width);
                }

                if (length < min || length > frame)
                {
                    return false;
                }
            }

            if (!any)
            {
                if (_bits.Position > _limit)
                {
                    return false;
                }

                continue;
            }

            for (int c = 0; c < _channels; c++)
            {
                if (!takes[c])
                {
                    continue;
                }

                if (_count[c] >= MaxPerChannel || _done[c] + length > frame)
                {
                    return false;
                }

                _lengths[(c * (MaxPerChannel + 1)) + _count[c]++] = (ushort)length;
                _done[c] += length;
            }
        }
    }

    private ProFault ReadSubframes()
    {
        ProParameters p = parameters;
        Array.Clear(_done);
        Array.Clear(_next);
        while (true)
        {
            int start = int.MaxValue;
            for (int c = 0; c < _channels; c++)
            {
                start = Math.Min(start, _done[c]);
            }

            if (start >= p.FrameLength)
            {
                return ProFault.None;
            }

            int length = 0;
            int members = 0;
            for (int c = 0; c < _channels; c++)
            {
                if (_done[c] != start)
                {
                    continue;
                }

                int next = _lengths[(c * (MaxPerChannel + 1)) + _next[c]];
                if (length == 0)
                {
                    length = next;
                }

                if (next == length)
                {
                    _members[members++] = c;
                }
            }

            int size = BitOperations.Log2((uint)(p.FrameLength / length));
            uint mask = 0;
            for (int i = 0; i < members; i++)
            {
                mask |= 1u << _members[i];
            }

            _trace.Put(Tag.Subframe);
            _trace.Put(mask);
            _trace.Put(size);
            _trace.Put(start);
            SubframesRead++;
            tally.Subframes++;
            tally.ChannelSubframes += members;
            tally.Sizes[size]++;

            ProFault fault = ReadSubframe(members, length, size);
            _trace.Put(Tag.End);
            if (fault != ProFault.None)
            {
                return fault;
            }

            if (_bits.Position > _limit)
            {
                return ProFault.NeedsMoreBits;
            }

            for (int i = 0; i < members; i++)
            {
                int c = _members[i];
                _done[c] += length;
                _next[c]++;
            }
        }
    }

    private ProFault ReadSubframe(int members, int length, int size)
    {
        ProParameters p = parameters;
        Bits bits = _bits;

        int at = bits.Position;
        int fill = 0;
        if (bits.ReadBit() != 0)
        {
            int n = (int)bits.Read(2);
            if (n == 0)
            {
                int k = (int)bits.Read(4);
                n = (int)bits.Read(k) + 1;
            }

            fill = n;
            if (bits.Position + n > _limit)
            {
                _trace.Put(Tag.ExtendedHeader);
                _trace.Put(bits.Position - at);
                _trace.Put(fill);
                return ProFault.FillBits;
            }

            bits.Skip(n);
            tally.ExtendedHeaders++;
        }

        _trace.Put(Tag.ExtendedHeader);
        _trace.Put(bits.Position - at);
        _trace.Put(fill);

        bool isReserved = bits.ReadBit() != 0;
        _trace.Put(Tag.ReservedBit);
        _trace.Put(isReserved ? 1 : 0);
        if (isReserved)
        {
            return ProFault.ReservedBit;
        }

        int bands = p.BandEdges[size].Length - 1;
        if (_channels > 1 && !ReadTransform(members, bands))
        {
            return ProFault.ChannelTransform;
        }

        at = bits.Position;
        uint sent = 0;
        bool isAnySent = false;
        for (int i = 0; i < members; i++)
        {
            bool isSent = bits.ReadBit() != 0;
            _sends[i] = isSent;
            isAnySent |= isSent;
            if (isSent)
            {
                sent |= 1u << i;
            }
        }

        _trace.Put(Tag.CoefficientsSent);
        _trace.Put(bits.Position - at);
        _trace.Put(sent);
        if (!isAnySent)
        {
            return ProFault.None;
        }

        at = bits.Position;
        bool hasCounts = bits.ReadBit() != 0;
        _hasCounts = hasCounts;
        int countWidth = BitOperations.Log2((uint)((length + 3) / 4)) + 1;
        bool isCountValid = true;
        for (int i = 0; i < members; i++)
        {
            _vectorCounts[i] = length;
            if (hasCounts)
            {
                _vectorCounts[i] = (int)bits.Read(countWidth) * 4;
                isCountValid &= _vectorCounts[i] <= length;
            }
        }

        _trace.Put(Tag.VectorCounts);
        _trace.Put(bits.Position - at);
        _trace.Put(hasCounts ? 1 : 0);
        if (hasCounts)
        {
            for (int i = 0; i < members; i++)
            {
                _trace.Put(_vectorCounts[i]);
            }

            tally.VectorCounted++;
        }

        if (!isCountValid)
        {
            return ProFault.VectorCount;
        }

        ReadQuantizationStep(members);
        return ReadScaleFactorsAndCoefficients(members, length, size, bands);
    }

    private bool ReadTransform(int members, int bands)
    {
        Bits bits = _bits;
        int at = bits.Position;
        bool isUnsupported = bits.ReadBit() != 0;
        if (isUnsupported)
        {
            _trace.Put(Tag.ChannelTransform);
            _trace.Put(bits.Position - at);
            _trace.Put(0);
            return false;
        }

        // The groups are traced after the total, which is known at the end.
        var groups = new List<(uint Mask, int Bits, int Bands)>(4);
        uint remaining = (1u << members) - 1;
        bool isValid = true;
        while (remaining != 0 && groups.Count < members)
        {
            int groupAt = bits.Position;
            int left = BitOperations.PopCount(remaining);
            uint group = remaining;
            if (left > 2)
            {
                group = 0;
                for (int i = 0; i < members; i++)
                {
                    if ((remaining & (1u << i)) != 0 && bits.ReadBit() != 0)
                    {
                        group |= 1u << i;
                    }
                }
            }

            int size = BitOperations.PopCount(group);
            bool isOn = false;
            if (size == 2)
            {
                if (bits.ReadBit() == 0)
                {
                    isOn = true;
                }
                else if (bits.ReadBit() != 0)
                {
                    isValid = false;
                }
            }
            else if (size > 2 && bits.ReadBit() != 0)
            {
                isOn = true;
                if (bits.ReadBit() != 0)
                {
                    bits.Skip((size * (size - 1) / 2 * 6) + size);
                }
            }

            int onBands = -1;
            if (isOn && isValid)
            {
                onBands = 0;
                if (bits.ReadBit() != 0)
                {
                    onBands = bands;
                }
                else
                {
                    for (int b = 0; b < bands; b++)
                    {
                        onBands += bits.ReadBit();
                    }
                }
            }

            groups.Add((group, bits.Position - groupAt, onBands));
            tally.Groups++;
            if (onBands >= 0)
            {
                tally.GroupsWithTransform++;
                tally.BandsTransformed += onBands;
                tally.BandsOfTransformedGroups += bands;
            }

            remaining &= ~group;
            if (!isValid)
            {
                break;
            }
        }

        _trace.Put(Tag.ChannelTransform);
        _trace.Put(bits.Position - at);
        _trace.Put(groups.Count + 1);
        foreach ((uint mask, int length, int on) in groups)
        {
            _trace.Put(mask);
            _trace.Put(length);
            _trace.Put(on + 1);
        }

        return isValid;
    }

    private void ReadQuantizationStep(int members)
    {
        ProParameters p = parameters;
        Bits bits = _bits;
        int at = bits.Position;
        long step = (90 * p.SampleSize) >> 4;
        int value = (int)bits.Read(6);
        if (value >= 32)
        {
            value -= 64;
        }

        step += value;
        if (value is 31 or -32)
        {
            long sum = 0;
            int last = -1;
            bool isStopped = false;
            while (_limit - bits.Position > 5)
            {
                last = (int)bits.Read(5);
                sum += last;
                if (last != 31)
                {
                    isStopped = true;
                    break;
                }
            }

            if (!isStopped)
            {
                sum += last >= 0 ? last : value;
            }

            step += value > 0 ? sum : -sum;
        }

        if (members > 1)
        {
            int k = (int)bits.Read(3);
            for (int i = 0; i < members; i++)
            {
                if (bits.ReadBit() != 0)
                {
                    bits.Skip(k);
                }
            }
        }

        _trace.Put(Tag.QuantizationStep);
        _trace.Put(bits.Position - at);
        _trace.PutSigned(step);
        tally.MinStep = Math.Min(tally.MinStep, step);
        tally.MaxStep = Math.Max(tally.MaxStep, step);
    }

    private ProFault ReadScaleFactorsAndCoefficients(int members, int length, int size, int bands)
    {
        Bits bits = _bits;
        for (int i = 0; i < members; i++)
        {
            int c = _members[i];
            int at = bits.Position;
            bool isSent = _next[c] == 0 || bits.ReadBit() != 0;
            int mode = 2;
            int step = 0;
            bool isInvalid = false;
            if (isSent)
            {
                if (!_hasSet[c])
                {
                    mode = 0;
                    step = (int)bits.Read(2) + 1;
                    for (int b = 0; b < bands; b++)
                    {
                        ProCodes.Scale.Read(bits);
                    }
                }
                else
                {
                    mode = 1;
                    int index = 0;
                    while (index < bands)
                    {
                        int symbol = ProCodes.ScaleRl.Read(bits);
                        if (symbol == 1)
                        {
                            break;
                        }

                        int run;
                        if (symbol <= 0)
                        {
                            run = (int)((bits.Read(14) >> 1) & 31);
                        }
                        else
                        {
                            run = ProTables.ScaleRlRun[symbol];
                            bits.ReadBit();
                        }

                        index += run;
                        if (index >= bands)
                        {
                            isInvalid = true;
                            break;
                        }

                        index++;
                    }
                }

                _hasSet[c] = true;
            }

            _trace.Put(Tag.ScaleFactors);
            _trace.Put(i);
            _trace.Put(bits.Position - at);
            _trace.Put(mode);
            _trace.Put(step);
            if (isInvalid)
            {
                return ProFault.ScaleFactorRun;
            }

            if (_sends[i])
            {
                tally.CodedChannels++;
                if (mode != 2)
                {
                    tally.ScaleFactorsSent++;
                }

                if (mode == 1)
                {
                    tally.ScaleFactorDifferences++;
                }
            }
        }

        for (int i = 0; i < members; i++)
        {
            if (!_sends[i] || bits.Position >= _limit)
            {
                continue;
            }

            ReadChannelCoefficients(i, length);
        }

        return ProFault.None;
    }

    private void ReadChannelCoefficients(int member, int length)
    {
        Bits bits = _bits;
        int at = bits.Position;
        int codebook = bits.ReadBit();
        if (codebook != 0)
        {
            tally.SecondCodebook++;
        }

        Vlc table = codebook == 0 ? ProCodes.Coefficient0 : ProCodes.Coefficient1;
        int vectorLimit = _vectorCounts[member];
        bool hasCounts = _hasCounts;
        int nonZero = 0;
        int escapes = 0;
        bool isDamaged = false;
        int position = 0;
        int zeros = 0;
        bool isSwitched = false;
        int zeroLimit = length >> 8;
        Span<int> values = stackalloc int[4];
        while (position + 3 < vectorLimit && !isSwitched)
        {
            int code = ProCodes.Vector4.Read(bits);
            if (code <= 0)
            {
                for (int pair = 0; pair < 2; pair++)
                {
                    int code2 = ProCodes.Vector2.Read(bits);
                    if (code2 <= 0)
                    {
                        for (int single = 0; single < 2; single++)
                        {
                            int value = ProCodes.Vector1.Read(bits);
                            if (value == 100)
                            {
                                value += LargeValue(bits);
                            }

                            values[(pair * 2) + single] = value;
                        }
                    }
                    else
                    {
                        values[pair * 2] = ((code2 - 1) >> 4) & 15;
                        values[(pair * 2) + 1] = (code2 - 1) & 15;
                    }
                }
            }
            else
            {
                values[0] = ((code - 1) >> 12) & 15;
                values[1] = ((code - 1) >> 8) & 15;
                values[2] = ((code - 1) >> 4) & 15;
                values[3] = (code - 1) & 15;
            }

            for (int i = 0; i < 4; i++)
            {
                if (values[i] != 0)
                {
                    bits.ReadBit();
                    nonZero++;
                    zeros = 0;
                }
                else if (++zeros > zeroLimit && !hasCounts)
                {
                    isSwitched = true;
                }
            }

            position += 4;
        }

        while (position < length)
        {
            int symbol = table.Read(bits);
            if (symbol == 1)
            {
                break;
            }

            int run;
            if (symbol <= 0)
            {
                LargeValue(bits);
                escapes++;
                if (bits.ReadBit() == 0)
                {
                    run = 0;
                }
                else if (bits.ReadBit() == 0)
                {
                    run = (int)bits.Read(2) + 1;
                }
                else if (bits.ReadBit() == 0)
                {
                    run = (int)bits.Read(BitOperations.Log2((uint)(length - 1)) + 1) + 4;
                }
                else
                {
                    isDamaged = true;
                    break;
                }
            }
            else
            {
                run = codebook == 0 ? ProTables.Coefficient0Run[symbol] : ProTables.Coefficient1Run[symbol];
            }

            bits.ReadBit();
            position += run;
            if (position >= length)
            {
                isDamaged = true;
                break;
            }

            nonZero++;
            position++;
        }

        tally.NonZero += nonZero;
        tally.Escapes += escapes;
        if (isDamaged)
        {
            tally.Damaged++;
        }

        _trace.Put(Tag.Coefficients);
        _trace.Put(member);
        _trace.Put(bits.Position - at);
        _trace.Put(nonZero);
        _trace.Put(escapes);
        _trace.Put(codebook);
        _trace.Put(isDamaged ? 1 : 0);
    }

    private static int LargeValue(Bits bits)
    {
        if (bits.ReadBit() == 0)
        {
            return (int)bits.Read(8);
        }

        if (bits.ReadBit() == 0)
        {
            return (int)bits.Read(16);
        }

        return bits.ReadBit() == 0 ? (int)bits.Read(24) : (int)bits.Read(31);
    }
}
