// Follows the Monkey's Audio SDK 13.44 (UnBitArray.cpp, UnBitArrayBase.h, UnBitArrayBase.cpp, Old/UnBitArrayOld.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
namespace Encspect.Formats.MonkeysAudio.Decoding;

// The state a channel's range decoding keeps within a frame (UNBIT_ARRAY_STATE): the running sum that sets the pivot of the next value.
internal struct ChannelState
{
    public uint K;
    public uint KSum;

    public void Flush()
    {
        K = 10;
        KSum = (1 << 10) * 16;
    }
}

// The range decoder, reading the bytes of a frame in stream order (WordOrder). Files of version 3.99 and later code a value as an overflow, a symbol
// of a fixed 64-symbol model, and a base below a pivot that follows the average size of the values of its channel. Files from 3.90 to 3.98 use the
// same range coder with another model and code the value as the symbol and the bits of a width that follows the average (CUnBitArray3891To3989).
internal sealed class RangeDecoder
{
    private const uint TopValue = 1u << 31;
    private const int ExtraBits = 7;
    private const uint BottomValue = TopValue >> 8;
    private const int OverflowShift = 16;
    private const int ModelElements = 64;
    private const uint OverflowSignal = 1;
    private const uint OverflowPivot = 32768;

    private static readonly uint[] RangeTotal =
    [
        0, 19578, 36160, 48417, 56323, 60899, 63265, 64435, 64971, 65232, 65351, 65416, 65447, 65466, 65476, 65482, 65485, 65488, 65490, 65491, 65492, 65493, 65494, 65495,
        65496, 65497, 65498, 65499, 65500, 65501, 65502, 65503, 65504, 65505, 65506, 65507, 65508, 65509, 65510, 65511, 65512, 65513, 65514, 65515, 65516, 65517, 65518, 65519,
        65520, 65521, 65522, 65523, 65524, 65525, 65526, 65527, 65528, 65529, 65530, 65531, 65532, 65533, 65534, 65535, 65536,
    ];

    private static readonly uint[] RangeWidth =
    [
        19578, 16582, 12257, 7906, 4576, 2366, 1170, 536, 261, 119, 65, 31, 19, 10, 6, 3, 3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    private static readonly uint[] LegacyRangeTotal =
    [
        0, 14824, 28224, 39348, 47855, 53994, 58171, 60926, 62682, 63786, 64463, 64878, 65126, 65276, 65365, 65419, 65450, 65469, 65480, 65487, 65491, 65493, 65494, 65495,
        65496, 65497, 65498, 65499, 65500, 65501, 65502, 65503, 65504, 65505, 65506, 65507, 65508, 65509, 65510, 65511, 65512, 65513, 65514, 65515, 65516, 65517, 65518, 65519,
        65520, 65521, 65522, 65523, 65524, 65525, 65526, 65527, 65528, 65529, 65530, 65531, 65532, 65533, 65534, 65535, 65536,
    ];

    private static readonly uint[] LegacyRangeWidth =
    [
        14824, 13400, 11124, 8507, 6139, 4177, 2755, 1756, 1104, 677, 415, 248, 150, 89, 54, 31, 19, 11, 7, 4, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    private static readonly uint[] KSumMinimum =
    [
        0, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 8388608, 16777216, 33554432, 67108864,
        134217728, 268435456, 536870912, 1073741824, 2147483648U, 0, 0, 0, 0,
    ];

    // For every 16-bit value, the symbol whose slot of the model holds it (RangeOverflowTable).
    private static readonly byte[] OverflowSymbol = BuildOverflowTable(RangeTotal);
    private static readonly byte[] LegacyOverflowSymbol = BuildOverflowTable(LegacyRangeTotal);

    private readonly int _version;
    private byte[] _data = [];
    private int _length;
    private int _position;
    private uint _low;
    private uint _range;
    private uint _buffer;

    // version is the file version times 1000; from 3.99 on, the current coding.
    public RangeDecoder(int version) => _version = version;

    private bool IsLegacy => _version < 3990;

    private static byte[] BuildOverflowTable(uint[] total)
    {
        var table = new byte[65536];
        byte symbol = 0;
        for (int z = 0; z < 65536; z++)
        {
            if (z >= total[symbol + 1])
            {
                symbol++;
            }

            table[z] = symbol;
        }

        return table;
    }

    // Starts on the bytes of a frame after its CRC and special codes: one dummy byte, the first byte of the code, and the initial range
    // (UnBitArray.cpp, FlushBitArray).
    public void Begin(byte[] data, int length, int position)
    {
        _data = data;
        _length = length;
        _position = position;
        ReadByte();
        _buffer = ReadByte();
        _low = _buffer >> (8 - ExtraBits);
        _range = 1u << ExtraBits;
    }

    // Where the next array starts once the values of one are decoded (CUnBitArray3891To3989::Finalize): the position steps over a byte for each shift the range
    // still needs to be above the bottom value, and back over the last two bytes up to version 3.95, which the encoder of those versions left to the next array.
    public int Finalize()
    {
        uint range = _range;
        int position = _position;
        while (range <= BottomValue)
        {
            position++;
            range <<= 8;
            if (range == 0)
            {
                return position;
            }
        }

        return _version <= 3950 ? position - 2 : position;
    }

    private uint ReadByte()
    {
        if (_position >= _length)
        {
            throw new InvalidDataException("The coded data ends before the samples do");
        }

        return _data[_position++];
    }

    private void Normalize()
    {
        while (_range <= BottomValue)
        {
            _buffer = (_buffer << 8) | ReadByte();
            _low = (_low << 8) | ((_buffer >> 1) & 0xFF);
            _range <<= 8;
        }
    }

    // RangeDecodeFast: a value of the given number of bits without the update of the low end, or 0 where the range has run out.
    private uint DecodeFast(int shift)
    {
        while (_range <= BottomValue)
        {
            _buffer = (_buffer << 8) | ReadByte();
            _low = (_low << 8) | ((_buffer >> 1) & 0xFF);
            _range <<= 8;
            if (_range == 0)
            {
                return 0;
            }
        }

        _range >>= shift;
        return _low / _range;
    }

    private uint DecodeWithUpdate(int shift)
    {
        while (_range <= BottomValue)
        {
            if (_range == 0)
            {
                throw new InvalidDataException("The range of the range coder ran out");
            }

            _buffer = (_buffer << 8) | ReadByte();
            _low = (_low << 8) | ((_buffer >> 1) & 0xFF);
            _range <<= 8;
        }

        _range >>= shift;
        if (_range == 0)
        {
            throw new InvalidDataException("The range of the range coder ran out");
        }

        uint result = _low / _range;
        _low %= _range;
        return result;
    }

    private uint DecodeOverflow(ref uint pivot)
    {
        while (true)
        {
            uint total = DecodeFast(OverflowShift);
            if (total >= 65536)
            {
                throw new InvalidDataException("The coded data holds a value the model cannot code");
            }

            uint overflow = OverflowSymbol[total];
            _low -= _range * RangeTotal[overflow];
            _range *= RangeWidth[overflow];
            if (overflow != ModelElements - 1)
            {
                return overflow;
            }

            overflow = DecodeWithUpdate(16) << 16;
            overflow |= DecodeWithUpdate(16);
            if (overflow != OverflowSignal)
            {
                return overflow;
            }

            pivot = OverflowPivot;
        }
    }

    // One value of a channel, signed (UnBitArray.cpp, DecodeValueRange).
    public long DecodeValue(ref ChannelState state) => IsLegacy ? DecodeLegacyValue(ref state) : DecodeCurrentValue(ref state);

    // One value of a file from 3.90 to 3.98 (Old/UnBitArrayOld.cpp, CUnBitArray3891To3989::DecodeValueRange): the model's symbol is the overflow, or
    // for its last symbol the width of the bits that follow, and the working width k follows the average.
    private long DecodeLegacyValue(ref ChannelState state)
    {
        uint total = DecodeFast(OverflowShift);
        if (total >= 65536)
        {
            throw new InvalidDataException("The coded data holds a value the model cannot code");
        }

        uint overflow = LegacyOverflowSymbol[total];
        _low -= _range * LegacyRangeTotal[overflow];
        _range *= LegacyRangeWidth[overflow];
        uint width;
        if (overflow == ModelElements - 1)
        {
            width = DecodeWithUpdate(5);
            overflow = 0;
        }
        else
        {
            width = state.K < 1 ? 0 : state.K - 1;
        }

        long value;
        if (width <= 16 || _version < 3910)
        {
            value = DecodeWithUpdate((int)width);
        }
        else
        {
            uint low = DecodeWithUpdate(16);
            uint high = DecodeWithUpdate((int)(width - 16));
            value = low | ((long)high << 16);
        }

        value += (long)overflow << (int)width;
        state.KSum += (uint)((value + 1) / 2) - ((state.KSum + 16) >> 5);
        if (state.KSum < KSumMinimum[state.K])
        {
            state.K--;
        }
        else if (KSumMinimum[state.K + 1] != 0 && state.KSum >= KSumMinimum[state.K + 1])
        {
            state.K++;
        }

        return (value & 1) != 0 ? (value >> 1) + 1 : -(value >> 1);
    }

    private long DecodeCurrentValue(ref ChannelState state)
    {
        uint pivot = Math.Max(state.KSum / 32, 1u);
        uint overflow = DecodeOverflow(ref pivot);
        uint baseValue;
        if (pivot >= (1u << 16))
        {
            int bits = 0;
            while ((pivot >> bits) > 0)
            {
                bits++;
            }

            int shift = bits >= 16 ? bits - 16 : 0;
            uint split = 1u << shift;
            uint pivotA = (pivot / split) + 1;
            uint pivotB = split;
            Normalize();
            _range /= pivotA;
            if (_range == 0)
            {
                throw new InvalidDataException("The range of the range coder ran out");
            }

            uint baseA = _low / _range;
            _low %= _range;
            Normalize();
            _range /= pivotB;
            if (_range == 0)
            {
                throw new InvalidDataException("The range of the range coder ran out");
            }

            uint baseB = _low / _range;
            _low %= _range;
            baseValue = (baseA * split) + baseB;
        }
        else
        {
            while (_range <= BottomValue)
            {
                _buffer = (_buffer << 8) | ReadByte();
                _low = (_low << 8) | ((_buffer >> 1) & 0xFF);
                _range <<= 8;
                if (_range == 0)
                {
                    return 0;
                }
            }

            _range /= pivot;
            if (_range == 0)
            {
                throw new InvalidDataException("The range of the range coder ran out");
            }

            baseValue = _low / _range;
            _low %= _range;
        }

        long value = baseValue + ((long)overflow * pivot);
        state.KSum += (uint)((value + 1) / 2) - ((state.KSum + 16) >> 5);
        if (state.KSum < KSumMinimum[state.K])
        {
            state.K--;
        }
        else if (KSumMinimum[state.K + 1] != 0 && state.KSum >= KSumMinimum[state.K + 1])
        {
            state.K++;
        }

        return (value & 1) != 0 ? (value >> 1) + 1 : -(value >> 1);
    }
}
