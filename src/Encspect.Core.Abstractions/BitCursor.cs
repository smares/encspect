using System.Buffers.Binary;
using System.Numerics;

namespace Encspect.Core;

// Reads bits most significant first from a span through a 64-bit window, without allocating. Past the end it reads
// zero bits and sets IsOverrun, as BitReader does. LsbBitCursor has the same members for least significant first
// (TTA, WavPack). A cursor is for loops over every coded bit; BitReader is the class for header fields. Positions count bits in
// an int, so a span is at most 256 MiB: a parser bounds the unit it hands a cursor (a frame, a block) before it does.
public ref struct BitCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private long _position;

    public BitCursor(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, int.MaxValue / 8, nameof(data));

        _data = data;
    }

    // In bits from the start of the data.
    public readonly int Position => (int)Math.Min(_position, int.MaxValue);

    public readonly int Length => _data.Length * 8;

    public readonly bool IsOverrun => _position > Length;

    // 0 to 32 bits.
    public uint Read(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

        if (count == 0)
        {
            return 0;
        }

        uint value = (uint)((Window() << (int)(_position & 7)) >> (64 - count));
        _position += count;
        return value;
    }

    // 0 to 64 bits.
    public ulong ReadLong(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);

        if (count <= 32)
        {
            return Read(count);
        }

        ulong high = Read(count - 32);
        return (high << 32) | Read(32);
    }

    // 1 to 32 bits, two's complement.
    public int ReadSigned(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        return (int)(Read(count) << (32 - count)) >> (32 - count);
    }

    // 1 to 64 bits, for FLAC's 33-bit warm-up samples.
    public long ReadSignedLong(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        ulong value = ReadLong(count);
        return (long)(value << (64 - count)) >> (64 - count);
    }

    public bool ReadBit()
    {
        long position = _position++;
        long index = position >> 3;
        return index < _data.Length && ((_data[(int)index] >> (7 - (int)(position & 7))) & 1) != 0;
    }

    // Counts the zero bits up to the next one bit, which it consumes. It stops after limit bits without consuming
    // anything more, and at the end of the data, where it sets IsOverrun: the result is then the zero bits that
    // were left. Its time grows with the bits it consumes, a word at a time.
    public int CountZeros(int limit)
    {
        // The usual case: the next one bit lies in the window and within the limit.
        long index = _position >> 3;
        if (limit > 0 && index + 8 <= _data.Length)
        {
            ulong window = BinaryPrimitives.ReadUInt64BigEndian(_data[(int)index..]) << (int)(_position & 7);
            if (window != 0)
            {
                int zeros = BitOperations.LeadingZeroCount(window);
                if (zeros < limit)
                {
                    _position += zeros + 1;
                    return zeros;
                }
            }
        }

        return Count(limit, ones: false);
    }

    // The same for one bits, up to the next zero bit. Past the end every bit is a zero.
    public int CountOnes(int limit) => Count(limit, ones: true);

    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        _position += count;
    }

    public void AlignToByte() => _position = (_position + 7) & ~7L;

    private int Count(int limit, bool ones)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        int total = 0;
        while (total < limit)
        {
            long remaining = Length - _position;
            if (remaining <= 0)
            {
                _position = Math.Max(_position, Length + 1L);
                return total;
            }

            int offset = (int)(_position & 7);
            ulong window = Window() << offset;
            int available = (int)Math.Min(64 - offset, remaining);
            int run = Math.Min(BitOperations.LeadingZeroCount(ones ? ~window : window), available);
            int room = limit - total;
            if (run >= room)
            {
                _position += room;
                return limit;
            }

            total += run;
            _position += run;
            if (run < available)
            {
                _position++;
                return total;
            }
        }

        return total;
    }

    // The 64 bits that start at the byte of the position, zero past the end of the data.
    private readonly ulong Window()
    {
        long index = _position >> 3;
        if (index + 8 <= _data.Length)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(_data[(int)index..]);
        }

        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            value <<= 8;
            if (index + i < _data.Length && index >= 0)
            {
                value |= _data[(int)(index + i)];
            }
        }

        return value;
    }
}
