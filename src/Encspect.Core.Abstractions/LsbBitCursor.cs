using System.Buffers.Binary;
using System.Numerics;

namespace Encspect.Core;

// The same members for bits least significant first: the first bit read is the least significant bit of the first
// byte and of the value read (TTA, WavPack).
public ref struct LsbBitCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private long _position;

    public LsbBitCursor(ReadOnlySpan<byte> data)
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

        uint value = (uint)(Window() >> (int)(_position & 7)) & (uint.MaxValue >> (32 - count));
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

        ulong low = Read(32);
        return low | ((ulong)Read(count - 32) << 32);
    }

    // 1 to 32 bits, two's complement.
    public int ReadSigned(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        return (int)(Read(count) << (32 - count)) >> (32 - count);
    }

    // 1 to 64 bits.
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
        return index < _data.Length && ((_data[(int)index] >> (int)(position & 7)) & 1) != 0;
    }

    // As BitCursor.CountZeros, in this bit order.
    public int CountZeros(int limit)
    {
        // The usual case: the next one bit lies in the window and within the limit.
        long index = _position >> 3;
        if (limit > 0 && index + 8 <= _data.Length)
        {
            ulong window = BinaryPrimitives.ReadUInt64LittleEndian(_data[(int)index..]) >> (int)(_position & 7);
            if (window != 0)
            {
                int zeros = BitOperations.TrailingZeroCount(window);
                if (zeros < limit)
                {
                    _position += zeros + 1;
                    return zeros;
                }
            }
        }

        return Count(limit, ones: false);
    }

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
                _position = Length + 1;
                return total;
            }

            int offset = (int)(_position & 7);
            ulong window = Window() >> offset;
            int available = (int)Math.Min(64 - offset, remaining);
            int run = Math.Min(BitOperations.TrailingZeroCount(ones ? ~window : window), available);
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
            return BinaryPrimitives.ReadUInt64LittleEndian(_data[(int)index..]);
        }

        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (index + i < _data.Length && index >= 0)
            {
                value |= (ulong)_data[(int)(index + i)] << (8 * i);
            }
        }

        return value;
    }
}
