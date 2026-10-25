using System.Buffers.Binary;
using System.Numerics;

namespace Encspect.Core;

// The same members for bits least significant first: the first bit read is the least significant bit of the first
// byte and of the value read (TTA, WavPack). As BitCursor, it takes at most 256 MiB.
public ref struct LsbBitCursor
{
    private readonly ReadOnlySpan<byte> _data;

    // The next bits to read, the first in the least significant bit. Bits below the _cacheBits valid ones are either zero or already the
    // bits of the bytes that follow, which is what lets a refill OR a whole word in without masking.
    private ulong _cache;
    private int _cacheBits;

    // The index of the next byte to load; it runs past the data, where the bytes read as zero.
    private long _nextByte;

    public LsbBitCursor(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, int.MaxValue / 8, nameof(data));

        _data = data;
    }

    // In bits from the start of the data.
    public readonly int Position => (int)Math.Min(BitPosition, int.MaxValue);

    public readonly int Length => _data.Length * 8;

    public readonly bool IsOverrun => BitPosition > Length;

    private readonly long BitPosition => (_nextByte * 8) - _cacheBits;

    // 0 to 32 bits.
    public uint Read(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

        if (count == 0)
        {
            return 0;
        }

        if (_cacheBits < count)
        {
            Refill();
        }

        uint value = (uint)_cache & (uint.MaxValue >> (32 - count));
        _cache >>= count;
        _cacheBits -= count;
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
        if (_cacheBits == 0)
        {
            Refill();
        }

        bool bit = (_cache & 1) != 0;
        _cache >>= 1;
        _cacheBits--;
        return bit;
    }

    // As BitCursor.CountZeros, in this bit order.
    public int CountZeros(int limit)
    {
        // The usual case: the next one bit lies in the buffer and within the limit. A one bit is always real data, never the zeros read
        // past the end.
        if (limit > 0)
        {
            if (_cacheBits <= 32)
            {
                Refill();
            }

            int zeros = BitOperations.TrailingZeroCount(_cache);
            if (zeros < _cacheBits && zeros < limit)
            {
                _cache >>= zeros;
                _cache >>= 1;
                _cacheBits -= zeros + 1;
                return zeros;
            }
        }

        return Count(limit, ones: false);
    }

    // The same for one bits, up to the next zero bit. Past the end every bit is a zero.
    public int CountOnes(int limit)
    {
        if (limit > 0)
        {
            if (_cacheBits <= 32)
            {
                Refill();
            }

            int ones = BitOperations.TrailingZeroCount(~_cache);
            if (ones < _cacheBits && ones < limit && _nextByte <= _data.Length)
            {
                _cache >>= ones;
                _cache >>= 1;
                _cacheBits -= ones + 1;
                return ones;
            }
        }

        return Count(limit, ones: true);
    }

    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count < _cacheBits)
        {
            _cache >>= count;
            _cacheBits -= count;
            return;
        }

        MoveTo(BitPosition + count);
    }

    // The cache holds whole bytes less the bits of the byte in hand, so the bits to drop are its remainder.
    public void AlignToByte()
    {
        int drop = _cacheBits & 7;
        _cache >>= drop;
        _cacheBits -= drop;
    }

    private void Refill()
    {
        if (_nextByte + 8 <= _data.Length)
        {
            _cache |= BinaryPrimitives.ReadUInt64LittleEndian(_data[(int)_nextByte..]) << _cacheBits;
            int bytes = (63 - _cacheBits) >> 3;
            _nextByte += bytes;
            _cacheBits += bytes * 8;
            return;
        }

        while (_cacheBits <= 56)
        {
            if (_nextByte < _data.Length)
            {
                _cache |= (ulong)_data[(int)_nextByte] << _cacheBits;
            }

            _nextByte++;
            _cacheBits += 8;
        }
    }

    // Discards the buffer and continues at a bit position, which may lie past the end.
    private void MoveTo(long position)
    {
        _nextByte = position >> 3;
        _cache = 0;
        _cacheBits = 0;
        int offset = (int)(position & 7);
        if (offset != 0)
        {
            Refill();
            _cache >>= offset;
            _cacheBits -= offset;
        }
    }

    private int Count(int limit, bool ones)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        long position = BitPosition;
        int total = 0;
        while (total < limit)
        {
            long remaining = Length - position;
            if (remaining <= 0)
            {
                position = Math.Max(position, Length + 1L);
                MoveTo(position);
                return total;
            }

            int offset = (int)(position & 7);
            ulong window = Window(position >> 3) >> offset;
            int available = (int)Math.Min(64 - offset, remaining);
            int run = Math.Min(BitOperations.TrailingZeroCount(ones ? ~window : window), available);
            int room = limit - total;
            if (run >= room)
            {
                MoveTo(position + room);
                return limit;
            }

            total += run;
            position += run;
            if (run < available)
            {
                MoveTo(position + 1);
                return total;
            }
        }

        MoveTo(position);
        return total;
    }

    // The 64 bits that start at a byte, zero past the end of the data.
    private readonly ulong Window(long index)
    {
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
