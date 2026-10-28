// Follows the Monkey's Audio SDK 13.42 (Old/UnBitArrayOld.cpp, UnBitArrayBase.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
using System.Numerics;

namespace Encspect.Formats.MonkeysAudio.Decoding;

// The bits of a frame of a file before version 3.93, most significant bit first in the stream bytes (WordOrder.ToStream), which the SDK's bit array reads as
// 32-bit words. Reading beyond the frame's bytes is the end of the coded data.
internal sealed class OldBits
{
    private byte[] _data = [];
    private long _length;
    private long _position;

    // The bit the next read starts at, counted from the first stream byte.
    public long Position => _position;

    public byte[] Data => _data;

    public int ByteLength => (int)(_length >> 3);

    public void Begin(byte[] data, int length, long startBit)
    {
        _data = data;
        _length = length * 8L;
        _position = startBit;
    }

    public void AlignToByte() => _position = (_position + 7) & ~7L;

    public void MoveTo(long bit) => _position = bit;

    // DecodeValueXBits: bits (0 to 32) as an unsigned number.
    public uint Read(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        long end = _position + bits;
        if (end > _length)
        {
            throw new InvalidDataException("The coded data ends before the samples do");
        }

        int first = (int)(_position >> 3);
        int offset = (int)(_position & 7);
        ulong window = 0;
        int needed = (offset + bits + 7) >> 3;
        for (int i = 0; i < needed; i++)
        {
            window = (window << 8) | _data[first + i];
        }

        _position = end;
        window >>= (needed * 8) - offset - bits;
        return (uint)(window & (bits == 32 ? 0xFFFFFFFFUL : (1UL << bits) - 1));
    }

    // The zeros before the next one bit, which is read too (the overflow of a Rice value).
    public int ReadUnary()
    {
        long start = _position;
        long position = start;
        while (true)
        {
            if (position >= _length)
            {
                throw new InvalidDataException("The coded data ends before the samples do");
            }

            int index = (int)(position >> 3);
            int offset = (int)(position & 7);
            uint bits = (uint)(_data[index] << offset) & 0xFF;
            if (bits != 0)
            {
                position += BitOperations.LeadingZeroCount(bits) - 24;
                break;
            }

            position += 8 - offset;
        }

        _position = position + 1;
        long zeros = position - start;
        if (zeros > int.MaxValue)
        {
            throw new InvalidDataException("A run of zeros in the coded data is longer than any value");
        }

        return (int)zeros;
    }
}

// The Rice coding of a channel's array of files before version 3.90: the arrays are resumable, so the decoder charges its work and looks at the cancellation
// token between chunks.
internal abstract class OldArrayDecoder
{
    protected static readonly uint[] KSumMinimum =
    [
        0, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 8388608, 16777216, 33554432, 67108864,
        134217728, 268435456, 536870912, 1073741824, 2147483648U, 0, 0, 0, 0,
    ];

    protected OldBits Bits { get; private set; } = null!;

    // Starts an array of count values.
    public virtual void Begin(OldBits bits, int count) => Bits = bits;

    // Decodes the values [from, to) of the array, signed (the values before to − 64 of the older coding wait for Finish).
    public abstract void Decode(int[] output, int from, int to);

    // Makes the last values signed, where the coding converts them late.
    public virtual void Finish(int[] output)
    {
    }

    protected static int Signed(uint value) => (value & 1) != 0 ? (int)(value >> 1) + 1 : -(int)(value >> 1);

    // The conversion of the older coding, which shifts the signed integer the unsigned value was stored in.
    protected static int SignedOld(int value) => (value & 1) != 0 ? (value >> 1) + 1 : -(value >> 1);
}

// Up to version 3.85 (CUnBitArrayOld::GenerateArrayOld): the first five values with Rice parameter 10, the next 59 with a parameter that follows the mean so far, the
// rest with one that follows the sum of the last 64 values. A value is converted to signed 64 values after it is read, since the sum is over the unsigned ones.
internal sealed class RiceUpTo385 : OldArrayDecoder
{
    // K_SUM_MIN_BOUNDARY_OLD and K_SUM_MAX_BOUNDARY_OLD.
    private static readonly uint[] OldMinimum = BuildMinimum();
    private static readonly uint[] OldMaximum = BuildMaximum();

    private int _count;
    private uint _ksum;
    private uint _k;
    private uint _kmin;
    private uint _kmax;

    private static uint[] BuildMinimum()
    {
        var table = new uint[33];
        for (int i = 1; i <= 25; i++)
        {
            table[i] = 1u << (i + 6);
        }

        return table;
    }

    private static uint[] BuildMaximum()
    {
        var table = new uint[33];
        for (int i = 0; i <= 24; i++)
        {
            table[i] = 1u << (i + 7);
        }

        return table;
    }

    public override void Begin(OldBits bits, int count)
    {
        base.Begin(bits, count);
        _count = count;
        _ksum = 0;
        _k = 0;
    }

    private uint RiceUnsigned(uint k)
    {
        int overflow = Bits.ReadUnary();
        if (k == 0)
        {
            return (uint)overflow;
        }

        return ((uint)overflow << (int)k) | Bits.Read((int)k);
    }

    // Get_K: the number of bits of x.
    private static uint GetK(uint x)
    {
        if (x == 0)
        {
            return 0;
        }

        uint k = 0;
        while (x >= (1u << (int)++k))
        {
            if (k == 31)
            {
                throw new InvalidDataException("The coded data holds a sum no Rice parameter fits");
            }
        }

        return k;
    }

    public override void Decode(int[] output, int from, int to)
    {
        for (int q = from; q < to; q++)
        {
            if (q < 5)
            {
                output[q] = (int)RiceUnsigned(10);
                if (q == 4 && _count > 5)
                {
                    _ksum = (uint)(output[0] + output[1] + output[2] + output[3] + output[4]);
                    _k = GetK(_ksum / 10);
                }
            }
            else if (q < 64)
            {
                output[q] = (int)RiceUnsigned(_k);
                _ksum += (uint)output[q];
                _k = GetK(_ksum / (uint)(q + 1) / 2);
                if (q == 63 && _count > 64)
                {
                    _k = GetK(_ksum >> 7);
                    _kmin = OldMinimum[_k];
                    _kmax = OldMaximum[_k];
                }
            }
            else
            {
                uint v;
                int overflow = Bits.ReadUnary();
                v = _k == 0 ? (uint)overflow : ((uint)overflow << (int)_k) | Bits.Read((int)_k);
                output[q] = (int)v;
                _ksum += (uint)(output[q] - output[q - 64]);
                output[q - 64] = SignedOld(output[q - 64]);
                if (_ksum < _kmin || _ksum >= _kmax)
                {
                    if (_ksum < _kmin)
                    {
                        do
                        {
                            _k--;
                        }
                        while (_ksum < OldMinimum[_k]);
                    }
                    else
                    {
                        while (OldMaximum[_k + 1] != 0)
                        {
                            _k++;
                            if (_ksum < OldMaximum[_k])
                            {
                                break;
                            }
                        }
                    }

                    _kmax = OldMaximum[_k];
                    _kmin = OldMinimum[_k];
                }
            }
        }
    }

    public override void Finish(int[] output)
    {
        for (int q = Math.Max(0, _count - 64); q < _count; q++)
        {
            output[q] = SignedOld(output[q]);
        }
    }
}

// From version 3.86 to 3.89 (CUnBitArrayOld::GenerateArrayRice, DecodeValueNew): one Rice parameter that follows a running sum; from 3.89 an overflow of 16 or more
// adds 4 to the parameter for each 16.
internal sealed class RiceFrom386 : OldArrayDecoder
{
    private static readonly uint[] KSumMaximum = BuildMaximum();

    private readonly bool _isCapped;
    private uint _k;
    private uint _ksum;

    public RiceFrom386(bool isCapped) => _isCapped = isCapped;

    private static uint[] BuildMaximum()
    {
        var table = new uint[32];
        for (int i = 0; i < 31; i++)
        {
            table[i] = KSumMinimum[i + 1];
        }

        return table;
    }

    public override void Begin(OldBits bits, int count)
    {
        base.Begin(bits, count);
        _k = 10;
        _ksum = 1024 * 16;
    }

    public override void Decode(int[] output, int from, int to)
    {
        OldBits bits = Bits;
        for (int q = from; q < to; q++)
        {
            int overflow = bits.ReadUnary();
            if (_isCapped)
            {
                while (overflow >= 16)
                {
                    _k += 4;
                    overflow -= 16;
                }
            }

            if (_k > 31)
            {
                throw new InvalidDataException("The coded data holds a value wider than 31 bits");
            }

            uint v;
            if (_k != 0)
            {
                v = ((uint)overflow << (int)_k) | bits.Read((int)_k);
            }
            else
            {
                v = (uint)overflow;
            }

            _ksum += v - ((_ksum + 8) >> 4);
            if (_k > 31)
            {
                _k = 31;
            }

            if (_ksum < KSumMinimum[_k])
            {
                _k--;
            }
            else if (KSumMaximum[_k] != 0 && _ksum >= KSumMaximum[_k])
            {
                _k++;
            }

            output[q] = Signed(v);
        }
    }
}

// From version 3.90 to 3.92 (CUnBitArray3891To3989::GenerateArrayRange): the range coder of the later versions, an array at a time. Each array starts on a byte
// boundary, and ends where Finalize leaves the position.
internal sealed class RangeArray : OldArrayDecoder
{
    private readonly RangeDecoder _range;
    private ChannelState _state;

    public RangeArray(int version) => _range = new RangeDecoder(version);

    public override void Begin(OldBits bits, int count)
    {
        base.Begin(bits, count);
        bits.AlignToByte();
        _state.Flush();
        _range.Begin(bits.Data, bits.ByteLength, (int)(bits.Position >> 3));
    }

    public override void Decode(int[] output, int from, int to)
    {
        for (int q = from; q < to; q++)
        {
            output[q] = unchecked((int)_range.DecodeValue(ref _state));
        }
    }

    public override void Finish(int[] output) => Bits.MoveTo(_range.Finalize() * 8L);
}
