// Derived from libwavpack 5.6.0 (unpack3.c), Copyright (c) 1998 - 2013 Conifer Software, Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Encspect.Formats.WavPack.Decoding;

// The bit reader and the five entropy word readers of the decoder of WavPack's versions 1 to 3 (unpack3.c get_word1, get_old_word1, get_word2, get_word3 and
// get_word4). The bits come least significant first from the bytes of the bitstream; past the data the reader supplies bytes of 0xFF as libwavpack's does (bs_read3),
// which every word reader takes for the end of the coded data (WordEof).
internal sealed partial class LegacyStreamDecoder
{
    // libwavpack's WORD_EOF: the value a word reader returns where the bitstream ends or holds a code no encoder writes.
    internal const int WordEof = int.MinValue;

    private byte[] _input = [];
    private int _pos;
    private int _end;
    private ulong _sr;
    private int _bc;

    // The bytes taken from the bitstream so far, those of 0xFF past the end of the data included: the bitstream ends this many bytes after it begins (the byte
    // that holds the last bit read counts whole), where unpack3.c's bs_unused_bytes starts the wrapper.
    private long _fetched;

    // The word readers' states (unpack3.h: w1, w2, w3 and w4 of WavpackStream3).
    private readonly uint[] _aveLevel = new uint[6];
    private readonly uint[] _wordIndex = new uint[2];
    private readonly uint[] _kValue = new uint[2];
    private readonly uint[] _aveK = new uint[2];
    private uint _zerosAcc;
    private readonly int[] _lastDbits = new int[2];
    private readonly int[] _lastDeltaSign = new int[2];
    private readonly int[] _aveDbits = new int[2];
    private readonly uint[] _fastLevel = new uint[2];
    private readonly uint[] _slowLevel = new uint[2];
    private readonly int[] _bitsAcc = new int[2];
    private int _bitrate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte NextByte()
    {
        _fetched++;
        return _pos < _end ? _input[_pos++] : (byte)0xFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetBit()
    {
        if (_bc == 0)
        {
            _sr = NextByte();
            _bc = 8;
        }

        int bit = (int)(_sr & 1);
        _sr >>= 1;
        _bc--;
        return bit;
    }

    // The next count bits, 0 to 31, as an unsigned value.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetBits(int count)
    {
        while (count > _bc)
        {
            _sr |= (ulong)NextByte() << _bc;
            _bc += 8;
        }

        uint value = (uint)_sr & (uint)((1UL << count) - 1);
        _sr >>= count;
        _bc -= count;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBits(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    private static uint BitMask(int count) => (uint)((1UL << count) - 1);

    private static uint BitSet(int count) => 1u << count;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetWord1(int chan)
    {
        if ((_flags & LegacyHeader.ExtremeDecorrFlag) != 0 && (_flags & LegacyHeader.Over20Flag) == 0)
        {
            if (_zerosAcc != 0)
            {
                if (--_zerosAcc != 0)
                {
                    return 0;
                }
            }
            else if (_aveLevel[0] < 0x20 && _aveLevel[1] < 0x20)
            {
                int cbits;
                for (cbits = 0; cbits < 33 && GetBit() != 0; ++cbits)
                {
                }

                if (cbits == 33)
                {
                    return WordEof;
                }

                if (cbits < 2)
                {
                    _zerosAcc = (uint)cbits;
                }
                else
                {
                    int mask = 1;
                    for (_zerosAcc = 0; --cbits != 0; mask <<= 1)
                    {
                        if (GetBit() != 0)
                        {
                            _zerosAcc |= (uint)mask;
                        }
                    }

                    _zerosAcc |= (uint)mask;
                }

                if (_zerosAcc != 0)
                {
                    return 0;
                }
            }
        }

        // Consecutive ones: 25 or more is an error or the end of the data.
        uint onesCount;
        for (onesCount = 0; onesCount < 25 && GetBit() != 0; ++onesCount)
        {
        }

        if (onesCount == 25)
        {
            return WordEof;
        }

        int k = CountBits((_aveLevel[chan] + (_aveLevel[chan] >> 3) + 0x40) >> 7);
        if ((k & ~31) != 0)
        {
            return WordEof;
        }

        uint avalue;
        if (onesCount == 0)
        {
            avalue = GetBits(k);
        }
        else
        {
            uint tmp1 = BitSet(k);
            k = CountBits((_aveLevel[2 + chan] + (_aveLevel[2 + chan] >> 4) + 0x20) >> 6);
            if ((k & ~31) != 0)
            {
                return WordEof;
            }

            if (onesCount == 1)
            {
                avalue = GetBits(k);
            }
            else
            {
                uint tmp2 = BitSet(k);

                // Exactly 24 ones: the next 24 bits are the value itself.
                if (onesCount == 24)
                {
                    avalue = GetBits(24);
                }
                else
                {
                    k = CountBits((_aveLevel[4 + chan] + 0x10) >> 5);
                    if ((k & ~31) != 0)
                    {
                        return WordEof;
                    }

                    avalue = GetBits(k) + (BitSet(k) * (onesCount - 2));
                }

                _aveLevel[4 + chan] -= (_aveLevel[4 + chan] + 0x8) >> 4;
                _aveLevel[4 + chan] += avalue;
                avalue += tmp2;
            }

            _aveLevel[2 + chan] -= (_aveLevel[2 + chan] + 0x10) >> 5;
            _aveLevel[2 + chan] += avalue;
            avalue += tmp1;
        }

        _aveLevel[chan] -= (_aveLevel[chan] + 0x20) >> 6;
        _aveLevel[chan] += avalue;

        return avalue != 0 && GetBit() != 0 ? -(int)avalue : (int)avalue;
    }

    private const uint WordsPerAveK = 128;

    // The high mode of versions before 3.94 (the same as GetWord1 without the new high flag).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetOldWord1(int chan)
    {
        if (_wordIndex[chan] == 0)
        {
            int guessK = (int)((_aveK[chan] + 128) >> 8);
            int ones;
            for (ones = 0; ones < 72 && GetBit() != 0; ++ones)
            {
            }

            if (ones == 72)
            {
                return WordEof;
            }

            _kValue[chan] = ones % 3 == 1 ? (uint)(guessK - (ones / 3) - 1) : (uint)(guessK + ones - ((ones + 1) / 3));
            _aveK[chan] -= (_aveK[chan] + 0x10) >> 5;
            _aveK[chan] += _kValue[chan] << 3;
        }

        if (++_wordIndex[chan] == WordsPerAveK)
        {
            _wordIndex[chan] = 0;
        }

        // A count outside 0 to 31 is the end of the data at once, where libwavpack reads with it first and then refuses it.
        int k = (int)_kValue[chan];
        if ((k & ~31) != 0)
        {
            return WordEof;
        }

        uint avalue = GetBits(k);
        int bc;
        for (bc = 0; bc < 32 && GetBit() != 0; ++bc)
        {
        }

        if (bc == 32)
        {
            return WordEof;
        }

        avalue += BitSet(k) * (uint)bc;
        return avalue != 0 && GetBit() != 0 ? -(int)avalue : (int)avalue;
    }

    // Versions 1 and 2: a unary count of how the number of bits of the value changes, the bits (up to the lossy limit) and the sign.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetWord2(int chan)
    {
        int cbits = 0;
        while (GetBit() != 0)
        {
            if ((cbits += 2) == 50)
            {
                return WordEof;
            }
        }

        if (GetBit() != 0)
        {
            cbits++;
        }

        int deltaDbits;
        if (cbits == 0)
        {
            deltaDbits = 0;
        }
        else if ((cbits & 1) != 0)
        {
            deltaDbits = (cbits + 1) / 2;
            if (_lastDeltaSign[chan] > 0)
            {
                deltaDbits *= -1;
            }

            _lastDeltaSign[chan] = deltaDbits;
        }
        else
        {
            deltaDbits = cbits / 2;
            if (_lastDeltaSign[chan] <= 0)
            {
                deltaDbits *= -1;
            }
        }

        int dbits = _lastDbits[chan] += deltaDbits;
        if (dbits is < 0 or > 20)
        {
            return WordEof;
        }

        if (dbits == 0)
        {
            return 0;
        }

        int value = 1 << (dbits - 1);
        int mask = 1;
        if (_bits != 0)
        {
            for (; --dbits != 0; mask <<= 1)
            {
                if (dbits < _bits && GetBit() != 0)
                {
                    value |= mask;
                }
            }
        }
        else
        {
            for (; --dbits != 0; mask <<= 1)
            {
                if (GetBit() != 0)
                {
                    value |= mask;
                }
            }
        }

        return GetBit() != 0 ? -value : value;
    }

    // Version 3's fast and lossy coding: a unary count that changes the running number of bits of the value, then the bits with their sign.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetWord3(int chan)
    {
        int cbits;
        for (cbits = 0; cbits < 72 && GetBit() != 0; ++cbits)
        {
        }

        if (cbits == 72)
        {
            return WordEof;
        }

        if (cbits != 0 || GetBit() != 0)
        {
            ++cbits;
        }

        int deltaDbits = (cbits + 1) % 3 == 0 ? (cbits + 1) / 3 : -(cbits - (cbits / 3));
        int dbits = (_aveDbits[chan] >> 8) + 1 + deltaDbits;
        _aveDbits[chan] -= (_aveDbits[chan] + 0x10) >> 5;
        _aveDbits[chan] += dbits << 3;

        if (dbits is < 0 or > 24)
        {
            return WordEof;
        }

        if (dbits == 0)
        {
            return 0;
        }

        if (_bits != 0 && dbits > _bits)
        {
            uint limited = GetBits(_bits);
            if ((limited & BitSet(_bits - 1)) != 0)
            {
                return -(int)(limited & BitMask(_bits)) << (dbits - _bits);
            }

            return (int)((limited & BitMask(_bits - 1)) | BitSet(_bits - 1)) << (dbits - _bits);
        }

        uint value = GetBits(dbits);
        if ((value & BitSet(dbits - 1)) != 0)
        {
            return -(int)(value & BitMask(dbits));
        }

        return (int)((value & BitMask(dbits - 1)) | BitSet(dbits - 1));
    }

    // The hybrid mode's word: a unary count of zones, the bit that tells which half of the zone the value lies in and as many more bits as the bitrate control allows
    // (the rest is the correction file's: read from the correction bitstream where there is one, else left out).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int GetWord4(int chan)
    {
        uint onesCount;
        for (onesCount = 0; onesCount < 25 && GetBit() != 0; ++onesCount)
        {
        }

        if (onesCount == 25)
        {
            return WordEof;
        }

        // Exactly 24 ones: the count goes on in a second code.
        if (onesCount == 24)
        {
            int cbits;
            for (cbits = 0; cbits < 33 && GetBit() != 0; ++cbits)
            {
            }

            if (cbits == 33)
            {
                return WordEof;
            }

            if (cbits < 2)
            {
                onesCount = (uint)cbits;
            }
            else
            {
                int mask = 1;
                for (onesCount = 0; --cbits != 0; mask <<= 1)
                {
                    if (GetBit() != 0)
                    {
                        onesCount |= (uint)mask;
                    }
                }

                onesCount |= (uint)mask;
            }

            onesCount += 24;
        }

        if (chan == 0)
        {
            if (_isMono)
            {
                _bitsAcc[0] += _bitrate + Log2(_fastLevel[0]) - Log2(_slowLevel[0]) + (3 << 8);
                if (_bitsAcc[0] < 0)
                {
                    _bitsAcc[0] = 0;
                }
            }
            else
            {
                int slowLog0 = Log2(_slowLevel[0]);
                int slowLog1 = Log2(_slowLevel[1]);
                int balance = (_flags & LegacyHeader.JointStereoFlag) != 0 ? (slowLog1 - slowLog0 + 257) >> 1 : (slowLog1 - slowLog0 + 1) >> 1;
                _bitsAcc[0] += _bitrate - balance + Log2(_fastLevel[0]) - slowLog0 + (3 << 8);
                _bitsAcc[1] += _bitrate + balance + Log2(_fastLevel[1]) - slowLog1 + (3 << 8);

                if (_bitsAcc[0] + _bitsAcc[1] < 0)
                {
                    _bitsAcc[0] = _bitsAcc[1] = 0;
                }
                else if (_bitsAcc[0] < 0)
                {
                    _bitsAcc[1] += _bitsAcc[0];
                    _bitsAcc[0] = 0;
                }
                else if (_bitsAcc[1] < 0)
                {
                    _bitsAcc[0] += _bitsAcc[1];
                    _bitsAcc[1] = 0;
                }
            }
        }

        uint baseValue = (_fastLevel[chan] + 48) / 96;
        int bitcount = _bitsAcc[chan] >> 8;
        _bitsAcc[chan] &= 0xff;

        int mid;
        int low;
        int high;
        if (baseValue == 0)
        {
            mid = onesCount != 0 ? (GetBit() != 0 ? -(int)onesCount : (int)onesCount) : 0;
            low = high = mid;
        }
        else
        {
            mid = (int)(((onesCount * 2) + 1) * baseValue);
            if (GetBit() != 0)
            {
                mid = -mid;
            }

            low = mid - (int)baseValue;
            high = mid + (int)baseValue - 1;
            while (bitcount-- > 0)
            {
                if (GetBit() != 0)
                {
                    low = mid;
                    mid = (high + low + 1) >> 1;
                }
                else
                {
                    high = mid - 1;
                    mid = (high + low + 1) >> 1;
                }

                if (high == low)
                {
                    break;
                }
            }
        }

        uint magnitude = (uint)Math.Abs((long)mid);
        _fastLevel[chan] -= (_fastLevel[chan] + 0x10) >> 5;
        _fastLevel[chan] += magnitude;
        _slowLevel[chan] -= (_slowLevel[chan] + 0x80) >> 8;
        _slowLevel[chan] += magnitude;
        if (HasCorrection)
        {
            _correction[chan] = ReadCorrection(mid, low, high);
        }

        return mid;
    }

    // An approximate base-2 logarithm with 8 bits of fraction (unpack3.c wp3_log2), which makes comparing levels and working out fractional bitrates easy.
    private static int Log2(uint value)
    {
        int dbits;
        value += value >> 9;
        if (value < 1 << 8)
        {
            dbits = CountBits(value);
            return (dbits << 8) + (int)((value << (9 - dbits)) & 0xff);
        }

        dbits = value < 1 << 16 ? CountBits(value >> 8) + 8 : value < 1 << 24 ? CountBits(value >> 16) + 16 : CountBits(value >> 24) + 24;
        return (dbits << 8) + (int)((value >> (dbits - 9)) & 0xff);
    }
}
