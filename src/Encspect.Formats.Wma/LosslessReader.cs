using System.Numerics;

namespace Encspect.Formats.Wma;

internal enum LosslessFault
{
    None,
    TileHeader,
    ArithmeticCoding,
    FilterOrder,
    WaitingForSeekable,
    NoBitsLeft,
    ZeroLowBits,
    TrimTooLarge,
    NeedsMoreBits,
    LengthPrefix,
}

internal sealed class LosslessTally
{
    public long Subframes;
    public long Seekable;
    public long RawPcm;
    public long ChannelSubframes;
    public long CodedChannels;
    public long CodableChannelSubframes;
    public long Residuals;
    public long Escapes;
    public long Transients;
    public long ZeroLowBitSubframes;
    public long MidSide;
    public long RecursiveFilters;
    public long MultichannelFilters;
    public long LpcFilters;
    public long MinFilters = long.MaxValue;
    public long MaxFilters;
}

internal static class LosslessTag
{
    public const int End = 0;
    public const int Seekable = 1;
    public const int ArithmeticCoding = 2;
    public const int FilterFlags = 3;
    public const int RecursiveParameters = 4;
    public const int MultichannelParameters = 5;
    public const int CascadedFilters = 6;
    public const int MeanScaling = 7;
    public const int QuantizationStep = 8;
    public const int RawPcm = 9;
    public const int CodedChannels = 10;
    public const int Lpc = 11;
    public const int ZeroLowBits = 12;
    public const int Samples = 13;
    public const int Residuals = 14;
}

// Reads the syntax of one Lossless frame (docs/design/wma-lossless-format.md, "Frame", "Subframe"): it finds where every field ends. The
// entropy code of the residuals depends on a running sum per channel that lasts from one subframe to the next, so one instance reads a
// stream's frames in order and keeps that state. Nothing is reconstructed.
internal sealed class LosslessFrameReader(LosslessParameters parameters, LosslessTally tally) : IFrameSyntax
{
    private const int MaxPerChannel = 32;
    private const int MaxCascadedOrder = 256;

    private readonly int _channels = parameters.Channels;
    private readonly ushort[] _lengths = new ushort[parameters.Channels * (MaxPerChannel + 1)];
    private readonly int[] _count = new int[parameters.Channels];
    private readonly int[] _done = new int[parameters.Channels];
    private readonly int[] _next = new int[parameters.Channels];
    private readonly bool[] _coded = new bool[parameters.Channels];
    private readonly uint[] _sums = new uint[parameters.Channels];

    private Bits _bits = null!;
    private int _limit;
    private TraceWriter _trace = null!;
    private bool _isReady;
    private bool _isMidSide;
    private int _meanScaling;

    public int SubframesRead { get; private set; }

    public FrameOutcome Read(Bits bits, int start, int limit, int declaredLength, TraceWriter trace)
    {
        LosslessParameters p = parameters;
        _bits = bits;
        _limit = limit;
        _trace = trace;
        trace.Clear();
        SubframesRead = 0;
        var result = new FrameOutcome();
        bits.Position = start;

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
            return Finish(ref result, LosslessFault.TileHeader, start);
        }

        if (p.HasDynamicRange)
        {
            trace.Put(Tag.DynamicRange);
            trace.Put(bits.Read(8));
        }

        int trimAt = bits.Position;
        bool hasTrim = bits.ReadBit() != 0;
        int trimStart = 0;
        if (hasTrim)
        {
            if (bits.ReadBit() != 0)
            {
                trimStart = (int)bits.Read(p.TrimBits);
            }

            if (bits.ReadBit() != 0)
            {
                result.TrimEnd = (int)bits.Read(p.TrimBits);
            }
        }

        result.TrimStart = trimStart;
        trace.Put(Tag.Trim);
        trace.Put(bits.Position - trimAt);
        trace.Put(hasTrim ? 1 : 0);
        trace.Put(trimStart);
        trace.Put(result.TrimEnd);

        if (result.TrimEnd >= p.FrameLength)
        {
            // A decoder stops here, before any subframe and without a loss.
            return Finish(ref result, LosslessFault.TrimTooLarge, start);
        }

        LosslessFault fault = ReadSubframes(ref result);
        if (fault != LosslessFault.None)
        {
            return Finish(ref result, fault, start);
        }

        result.AreSubframesRead = true;
        if (p.HasLengthPrefix)
        {
            int used = bits.Position - start;
            if (used + 2 > Math.Min(declaredLength, limit - start))
            {
                return Finish(ref result, LosslessFault.NeedsMoreBits, start);
            }

            if (used + 2 != declaredLength)
            {
                return Finish(ref result, LosslessFault.LengthPrefix, start);
            }

            bits.Skip(1);
            trace.Put(Tag.Padding);
            trace.Put(1);
        }

        result.Trailer = bits.ReadBit() != 0;
        if (bits.Position > limit)
        {
            return Finish(ref result, LosslessFault.NeedsMoreBits, start);
        }

        trace.Put(Tag.Trailer);
        trace.Put(result.Trailer ? 1 : 0);
        return Finish(ref result, LosslessFault.None, start);
    }

    private FrameOutcome Finish(ref FrameOutcome result, LosslessFault fault, int start)
    {
        result.Fault = (int)fault;
        result.Bits = _bits.Position - start;
        _trace.Put(Tag.End);
        return result;
    }

    private bool ReadTileHeader()
    {
        LosslessParameters p = parameters;
        int frame = p.FrameLength;
        int min = p.MinSubframe;
        Array.Clear(_count);
        Array.Clear(_done);
        Array.Clear(_next);
        bool isAligned = _bits.ReadBit() != 0;
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

            if (_bits.Position > _limit)
            {
                return false;
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
            bool isAny = false;
            for (int c = 0; c < _channels; c++)
            {
                takes[c] = false;
                if (_done[c] != smallest)
                {
                    continue;
                }

                takes[c] = isAligned || p.MaxSubframes == 1 || candidates == 1 || isLast || _bits.ReadBit() != 0;
                isAny |= takes[c];
            }

            if (!isAny)
            {
                return false;
            }

            int length = isLast ? min : (((int)_bits.Read(p.LengthCodeWidth)) + 1) * min;
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

    private LosslessFault ReadSubframes(ref FrameOutcome result)
    {
        LosslessParameters p = parameters;
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
                return LosslessFault.None;
            }

            int length = 0;
            uint mask = 0;
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
                    mask |= 1u << c;
                    members++;
                }
            }

            _trace.Put(Tag.Subframe);
            _trace.Put(mask);
            _trace.Put(length);
            _trace.Put(start);
            SubframesRead++;
            tally.Subframes++;
            tally.ChannelSubframes += members;

            LosslessFault fault = ReadSubframe(length, mask);
            _trace.Put(LosslessTag.End);
            if (fault != LosslessFault.None)
            {
                return fault;
            }

            if ((mask & 1) != 0)
            {
                result.CompletedSamples += length;
            }

            for (int c = 0; c < _channels; c++)
            {
                if ((mask & (1u << c)) != 0)
                {
                    _done[c] += length;
                    _next[c]++;
                }
            }
        }
    }

    private LosslessFault ReadSubframe(int length, uint members)
    {
        Bits bits = _bits;
        int b = parameters.SampleSize;
        int at = bits.Position;
        bool isSeekable = bits.ReadBit() != 0;
        _trace.Put(LosslessTag.Seekable);
        _trace.Put(isSeekable ? 1 : 0);
        if (isSeekable)
        {
            tally.Seekable++;
            Array.Clear(_sums);
            LosslessFault fault = ReadSeekableParameters();
            if (fault != LosslessFault.None)
            {
                // The arithmetic coding flag stops the subframe with the sums cleared and the readiness as it was; a rejected filter order
                // makes the decoder wait for the next seekable subframe.
                if (fault == LosslessFault.FilterOrder)
                {
                    _isReady = false;
                }

                return fault;
            }

            _isReady = true;
        }

        at = bits.Position;
        bool isRaw = bits.ReadBit() != 0;
        _trace.Put(LosslessTag.RawPcm);
        _trace.Put(isRaw ? 1 : 0);
        if (!isRaw && !_isReady)
        {
            return LosslessFault.WaitingForSeekable;
        }

        if (isRaw)
        {
            tally.RawPcm++;
            for (int c = 0; c < _channels; c++)
            {
                _coded[c] = true;
            }
        }
        else
        {
            at = bits.Position;
            uint codedMask = 0;
            for (int c = 0; c < _channels; c++)
            {
                _coded[c] = bits.ReadBit() != 0;
                if (_coded[c])
                {
                    codedMask |= 1u << c;
                }
            }

            _trace.Put(LosslessTag.CodedChannels);
            _trace.Put(bits.Position - at);
            _trace.Put(codedMask);
            tally.CodedChannels += BitOperations.PopCount(codedMask & members);
            tally.CodableChannelSubframes += BitOperations.PopCount(members);
            if (parameters.HasLpcFlags)
            {
                at = bits.Position;
                int order = 0;
                if (bits.ReadBit() != 0)
                {
                    order = (int)bits.Read(5) + 1;
                    int scaling = (int)bits.Read(4);
                    int integerBits = (int)bits.Read(3) + 1;
                    bits.Skip(_channels * order * (scaling + integerBits));
                    tally.LpcFilters++;
                }

                _trace.Put(LosslessTag.Lpc);
                _trace.Put(bits.Position - at);
                _trace.Put(order);
            }
        }

        if (bits.Position >= _limit)
        {
            return LosslessFault.NoBitsLeft;
        }

        at = bits.Position;
        int zeroBits = 0;
        if (bits.ReadBit() != 0)
        {
            zeroBits = (int)bits.Read(5);
        }

        _trace.Put(LosslessTag.ZeroLowBits);
        _trace.Put(bits.Position - at);
        _trace.Put(zeroBits);
        if (zeroBits > 0)
        {
            tally.ZeroLowBitSubframes++;
        }

        if (isRaw ? b - zeroBits < 1 : zeroBits > b)
        {
            return LosslessFault.ZeroLowBits;
        }

        if (isRaw)
        {
            int width = b - zeroBits;
            for (int c = 0; c < _channels; c++)
            {
                at = bits.Position;
                bits.Skip(length * width);
                _trace.Put(LosslessTag.Samples);
                _trace.Put(c);
                _trace.Put(bits.Position - at);
                _trace.Put(width);
            }

            return LosslessFault.None;
        }

        for (int c = 0; c < _channels; c++)
        {
            if (_coded[c])
            {
                ReadResiduals(c, length, isSeekable);
            }
        }

        return LosslessFault.None;
    }

    private LosslessFault ReadSeekableParameters()
    {
        Bits bits = _bits;
        bool isArithmetic = bits.ReadBit() != 0;
        _trace.Put(LosslessTag.ArithmeticCoding);
        _trace.Put(isArithmetic ? 1 : 0);
        if (isArithmetic)
        {
            return LosslessFault.ArithmeticCoding;
        }

        bool hasRecursive = bits.ReadBit() != 0;
        bool isMidSide = bits.ReadBit() != 0;
        bool hasMultichannel = bits.ReadBit() != 0;
        _trace.Put(LosslessTag.FilterFlags);
        _trace.Put(hasRecursive ? 1 : 0);
        _trace.Put(isMidSide ? 1 : 0);
        _trace.Put(hasMultichannel ? 1 : 0);
        _isMidSide = isMidSide;
        if (isMidSide)
        {
            tally.MidSide++;
        }

        int at = bits.Position;
        if (hasRecursive)
        {
            int order = (int)bits.Read(4) + 1;
            int scaling = (int)bits.Read(4);
            bits.Skip(order * scaling);
            tally.RecursiveFilters++;
            _trace.Put(LosslessTag.RecursiveParameters);
            _trace.Put(bits.Position - at);
            _trace.Put(order);
            _trace.Put(scaling);
        }

        at = bits.Position;
        if (hasMultichannel)
        {
            int order = (((int)bits.Read(4)) + 1) * 2;
            int scaling = (int)bits.Read(4);
            if (bits.ReadBit() != 0)
            {
                int width = (int)bits.Read(CeilLog2(scaling + 1)) + 2;
                bits.Skip(width * ((order * _channels * _channels) + (_channels * (_channels - 1) / 2)));
            }

            tally.MultichannelFilters++;
            _trace.Put(LosslessTag.MultichannelParameters);
            _trace.Put(bits.Position - at);
            _trace.Put(order);
            _trace.Put(scaling);
        }

        // The cascaded filters: whether coefficients are sent, then for each channel the filters' orders, scalings and coefficients.
        at = bits.Position;
        bool hasCoefficients = bits.ReadBit() != 0;
        long filters = 0;
        int firstChannelFilters = 0;
        Span<int> orders = stackalloc int[8];
        Span<int> scalings = stackalloc int[8];
        for (int c = 0; c < _channels; c++)
        {
            int n = (int)bits.Read(3) + 1;
            filters += n;
            if (c == 0)
            {
                firstChannelFilters = n;
            }

            for (int i = 0; i < n; i++)
            {
                orders[i] = (((int)bits.Read(7)) + 1) * 8;
                if (orders[i] > MaxCascadedOrder)
                {
                    _trace.Put(LosslessTag.CascadedFilters);
                    _trace.Put(bits.Position - at);
                    _trace.Put(firstChannelFilters);
                    _trace.Put(filters);
                    return LosslessFault.FilterOrder;
                }
            }

            for (int i = 0; i < n; i++)
            {
                scalings[i] = (int)bits.Read(4);
            }

            if (hasCoefficients)
            {
                for (int i = 0; i < n; i++)
                {
                    int count = (int)bits.Read(CeilLog2(orders[i])) + 1;
                    int width = (int)bits.Read(CeilLog2(scalings[i] + 1)) + 2;
                    bits.Skip(count * width);
                }
            }
        }

        _trace.Put(LosslessTag.CascadedFilters);
        _trace.Put(bits.Position - at);
        _trace.Put(firstChannelFilters);
        _trace.Put(filters);
        tally.MinFilters = Math.Min(tally.MinFilters, filters / _channels);
        tally.MaxFilters = Math.Max(tally.MaxFilters, filters / _channels);

        _meanScaling = (int)bits.Read(3);
        _trace.Put(LosslessTag.MeanScaling);
        _trace.Put(_meanScaling);
        int step = (int)bits.Read(8) + 1;
        _trace.Put(LosslessTag.QuantizationStep);
        _trace.Put(step);
        return LosslessFault.None;
    }

    private void ReadResiduals(int channel, int length, bool isSeekable)
    {
        Bits bits = _bits;
        int b = parameters.SampleSize;
        int at = bits.Position;
        bool isTransient = bits.ReadBit() != 0;
        if (isTransient)
        {
            bits.Skip(BitOperations.Log2((uint)length));
            tally.Transients++;
        }

        int codes = length;
        if (isSeekable)
        {
            uint mean = bits.Read(b);
            _sums[channel] = mean << (_meanScaling + 1);
            bits.Skip(_isMidSide ? b + 1 : b);
            codes = length - 1;
        }

        long escapes = 0;
        uint sum = _sums[channel];
        int k = _meanScaling;
        for (int i = 0; i < codes; i++)
        {
            uint q = 0;
            bool isOutOfBits = false;
            while (true)
            {
                if (bits.Position >= _limit)
                {
                    isOutOfBits = true;
                    break;
                }

                if (bits.ReadBit() == 0)
                {
                    break;
                }

                q++;
            }

            if (isOutOfBits)
            {
                break;
            }

            if (q >= 32)
            {
                int code = (int)bits.Read(5);
                q += bits.Read(code + 1);
                escapes++;
            }

            uint mean = (sum + (1u << k)) >> (k + 1);
            uint u = q;
            if (mean > 1)
            {
                int width = 32 - BitOperations.LeadingZeroCount(mean - 1);
                u = (q << width) + bits.Read(width);
            }

            sum = sum + u - (sum >> k);
        }

        _sums[channel] = sum;
        tally.Residuals += length;
        tally.Escapes += escapes;
        _trace.Put(LosslessTag.Residuals);
        _trace.Put(channel);
        _trace.Put(bits.Position - at);
        _trace.Put(length);
        _trace.Put(escapes);
        _trace.Put(isTransient ? 1 : 0);
    }

    private static int CeilLog2(int value) => value <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(value - 1));
}
