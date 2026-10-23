using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The entropy decoder of WavPack (libwavpack 5.6.0 read_words.c and entropy_utils.c): words coded as a unary count of
// which of three median zones they fall in, a position inside the zone and a sign, with runs of zeros. For a hybrid
// block the position inside the zone is only sent down to the error limit the bitrate control derives (update_error_limit).
// It holds the state of one block's bitstream; the cursor is the caller's. Past the end of the bitstream the cursor
// reads zero bits and sets IsOverrun, which the caller treats as the end of the data.
internal sealed class WordDecoder
{
    private const int LimitOnes = 16;
    private const int SlowLevelShift = 8;
    private const int SlowLevelOffset = 1 << (SlowLevelShift - 1);

    private readonly uint[] _median = new uint[6];
    private readonly uint[] _slowLevel = new uint[2];
    private readonly uint[] _errorLimit = new uint[2];
    private readonly uint[] _bitrateAcc = new uint[2];
    private readonly uint[] _bitrateDelta = new uint[2];
    private uint _holdingOne;
    private bool _holdingZero;
    private uint _zerosAcc;
    private uint _flags;

    public void Start(BlockContents contents, uint flags)
    {
        _flags = flags;
        Array.Copy(contents.Medians, _median, 6);
        Array.Copy(contents.SlowLevels, _slowLevel, 2);
        Array.Clear(_errorLimit);
        Array.Copy(contents.BitrateAccumulators, _bitrateAcc, 2);
        Array.Copy(contents.BitrateDeltas, _bitrateDelta, 2);
        _holdingOne = 0;
        _holdingZero = false;
        _zerosAcc = 0;
    }

    // get_word: false where the bitstream ends or an escape code is invalid (libwavpack's WORD_EOF).
    public bool TryGetWord(ref LsbBitCursor bits, int channel, out int value)
    {
        value = 0;
        int m = channel * 3;
        if ((_median[0] & ~1u) == 0 && !_holdingZero && _holdingOne == 0 && (_median[3] & ~1u) == 0)
        {
            if (_zerosAcc != 0)
            {
                if (--_zerosAcc != 0)
                {
                    _slowLevel[channel] -= (_slowLevel[channel] + SlowLevelOffset) >> SlowLevelShift;
                    return !bits.IsOverrun;
                }
            }
            else
            {
                int count = bits.CountOnes(33);
                if (count == 33)
                {
                    return false;
                }

                _zerosAcc = count < 2 ? (uint)count : bits.Read(count - 1) | (1u << (count - 1));
                if (_zerosAcc != 0)
                {
                    _slowLevel[channel] -= (_slowLevel[channel] + SlowLevelOffset) >> SlowLevelShift;
                    Array.Clear(_median);
                    return !bits.IsOverrun;
                }
            }
        }

        uint ones;
        if (_holdingZero)
        {
            ones = 0;
            _holdingZero = false;
        }
        else
        {
            int count = bits.CountOnes(LimitOnes + 1);
            if (count > LimitOnes)
            {
                return false;
            }

            ones = (uint)count;
            if (count == LimitOnes)
            {
                int escape = bits.CountOnes(33);
                if (escape == 33)
                {
                    return false;
                }

                ones = escape < 2 ? (uint)escape : bits.Read(escape - 1) | (1u << (escape - 1));
                ones += LimitOnes;
            }

            if (_holdingOne != 0)
            {
                _holdingOne = ones & 1;
                ones = (ones >> 1) + 1;
            }
            else
            {
                _holdingOne = ones & 1;
                ones >>= 1;
            }

            _holdingZero = (~_holdingOne & 1) != 0;
        }

        if ((_flags & BlockHeader.HybridFlag) != 0 && channel == 0)
        {
            UpdateErrorLimit();
        }

        uint low;
        uint high;
        if (ones == 0)
        {
            low = 0;
            high = Median(m, 0) - 1;
            _median[m] -= (((_median[m] + 126) / 128) * 2);
        }
        else
        {
            low = Median(m, 0);
            _median[m] += ((_median[m] + 128) / 128) * 5;
            if (ones == 1)
            {
                high = low + Median(m, 1) - 1;
                _median[m + 1] -= ((_median[m + 1] + 62) / 64) * 2;
            }
            else
            {
                low += Median(m, 1);
                _median[m + 1] += ((_median[m + 1] + 64) / 64) * 5;
                if (ones == 2)
                {
                    high = low + Median(m, 2) - 1;
                    _median[m + 2] -= ((_median[m + 2] + 30) / 32) * 2;
                }
                else
                {
                    low += (ones - 2) * Median(m, 2);
                    high = low + Median(m, 2) - 1;
                    _median[m + 2] += ((_median[m + 2] + 32) / 32) * 5;
                }
            }
        }

        low &= 0x7FFFFFFF;
        high &= 0x7FFFFFFF;
        if (low > high)
        {
            high = low;
        }

        uint mid = (high + low + 1) >> 1;
        uint limit = _errorLimit[channel];
        if (limit == 0)
        {
            mid = ReadCode(ref bits, high - low) + low;
        }
        else
        {
            while (high - low > limit)
            {
                // Zero bits past the end would keep the range from narrowing.
                if (bits.IsOverrun)
                {
                    return false;
                }

                if (bits.ReadBit())
                {
                    low = mid;
                    mid = (high + low + 1) >> 1;
                }
                else
                {
                    high = mid - 1;
                    mid = (high + low + 1) >> 1;
                }
            }
        }

        bool isNegative = bits.ReadBit();
        if ((_flags & BlockHeader.HybridBitrateFlag) != 0)
        {
            _slowLevel[channel] -= (_slowLevel[channel] + SlowLevelOffset) >> SlowLevelShift;
            _slowLevel[channel] += (uint)Log2(mid);
        }

        value = (int)(isNegative ? ~mid : mid);
        return !bits.IsOverrun;
    }

    private uint Median(int m, int zone) => (_median[m + zone] >> 4) + 1;

    // read_code: a value from 0 to maxCode, in as few bits as the range needs.
    private static uint ReadCode(ref LsbBitCursor bits, uint maxCode)
    {
        if (maxCode < 2)
        {
            return maxCode != 0 && bits.ReadBit() ? 1u : 0u;
        }

        int count = 32 - BitOperations.LeadingZeroCount(maxCode);
        uint extras = (uint)((1UL << count) - maxCode - 1);
        uint code = bits.Read(count - 1);
        if (code >= extras)
        {
            code = (code << 1) - extras + (bits.ReadBit() ? 1u : 0u);
        }

        return code;
    }

    // update_error_limit.
    private void UpdateErrorLimit()
    {
        _bitrateAcc[0] += _bitrateDelta[0];
        int bitrate0 = (int)(_bitrateAcc[0] >> 16);
        bool isMono = (_flags & (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag)) != 0;
        if (isMono)
        {
            if ((_flags & BlockHeader.HybridBitrateFlag) != 0)
            {
                int slow0 = (int)((_slowLevel[0] + SlowLevelOffset) >> SlowLevelShift);
                _errorLimit[0] = slow0 - bitrate0 > -0x100 ? (uint)BlockContents.Exp2(slow0 - bitrate0 + 0x100) : 0;
            }
            else
            {
                _errorLimit[0] = (uint)BlockContents.Exp2(bitrate0);
            }

            return;
        }

        _bitrateAcc[1] += _bitrateDelta[1];
        int bitrate1 = (int)(_bitrateAcc[1] >> 16);
        if ((_flags & BlockHeader.HybridBitrateFlag) != 0)
        {
            int slow0 = (int)((_slowLevel[0] + SlowLevelOffset) >> SlowLevelShift);
            int slow1 = (int)((_slowLevel[1] + SlowLevelOffset) >> SlowLevelShift);
            if ((_flags & BlockHeader.HybridBalanceFlag) != 0)
            {
                int balance = (slow1 - slow0 + bitrate1 + 1) >> 1;
                if (balance > bitrate0)
                {
                    bitrate1 = bitrate0 * 2;
                    bitrate0 = 0;
                }
                else if (-balance > bitrate0)
                {
                    bitrate0 *= 2;
                    bitrate1 = 0;
                }
                else
                {
                    bitrate1 = bitrate0 + balance;
                    bitrate0 -= balance;
                }
            }

            _errorLimit[0] = slow0 - bitrate0 > -0x100 ? (uint)BlockContents.Exp2(slow0 - bitrate0 + 0x100) : 0;
            _errorLimit[1] = slow1 - bitrate1 > -0x100 ? (uint)BlockContents.Exp2(slow1 - bitrate1 + 0x100) : 0;
        }
        else
        {
            _errorLimit[0] = (uint)BlockContents.Exp2(bitrate0);
            _errorLimit[1] = (uint)BlockContents.Exp2(bitrate1);
        }
    }

    // wp_log2: the base 2 logarithm in 8 bits of precision.
    private static int Log2(uint value)
    {
        value += value >> 9;
        int bits;
        if (value < 1 << 8)
        {
            bits = BitLength(value);
            return (bits << 8) + Log2Table[(int)((value << (9 - bits)) & 0xFF)];
        }

        bits = value < 1 << 16 ? BitLength(value >> 8) + 8 : value < 1 << 24 ? BitLength(value >> 16) + 16 : BitLength(value >> 24) + 24;
        return (bits << 8) + Log2Table[(int)((value >> (bits - 9)) & 0xFF)];
    }

    private static int BitLength(uint value) => 32 - BitOperations.LeadingZeroCount(value);

    private static readonly byte[] Log2Table =
    [
        0x00, 0x01, 0x03, 0x04, 0x06, 0x07, 0x09, 0x0a, 0x0b, 0x0d, 0x0e, 0x10, 0x11, 0x12, 0x14, 0x15,
        0x16, 0x18, 0x19, 0x1a, 0x1c, 0x1d, 0x1e, 0x20, 0x21, 0x22, 0x24, 0x25, 0x26, 0x28, 0x29, 0x2a,
        0x2c, 0x2d, 0x2e, 0x2f, 0x31, 0x32, 0x33, 0x34, 0x36, 0x37, 0x38, 0x39, 0x3b, 0x3c, 0x3d, 0x3e,
        0x3f, 0x41, 0x42, 0x43, 0x44, 0x45, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4d, 0x4e, 0x4f, 0x50, 0x51,
        0x52, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x5c, 0x5d, 0x5e, 0x5f, 0x60, 0x61, 0x62, 0x63,
        0x64, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72, 0x74, 0x75,
        0x76, 0x77, 0x78, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85,
        0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95,
        0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4,
        0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf, 0xb0, 0xb1, 0xb2, 0xb2,
        0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xb9, 0xba, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0, 0xc0,
        0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcb, 0xcc, 0xcd, 0xce,
        0xcf, 0xd0, 0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd8, 0xd9, 0xda, 0xdb,
        0xdc, 0xdc, 0xdd, 0xde, 0xdf, 0xe0, 0xe0, 0xe1, 0xe2, 0xe3, 0xe4, 0xe4, 0xe5, 0xe6, 0xe7, 0xe7,
        0xe8, 0xe9, 0xea, 0xea, 0xeb, 0xec, 0xed, 0xee, 0xee, 0xef, 0xf0, 0xf1, 0xf1, 0xf2, 0xf3, 0xf4,
        0xf4, 0xf5, 0xf6, 0xf7, 0xf7, 0xf8, 0xf9, 0xf9, 0xfa, 0xfb, 0xfc, 0xfc, 0xfd, 0xfe, 0xff, 0xff
    ];
}
