using System.Buffers.Binary;
using System.Numerics;

namespace Encspect.Core;

// Reads bits most significant first from a span through a 64-bit bit buffer that is refilled a word at a time, without allocating. Past
// the end it reads zero bits and sets IsOverrun, as BitReader does. LsbBitCursor has the same members for least significant first
// (TTA, WavPack). A cursor is for loops over every coded bit; BitReader is the class for header fields. Positions count bits in
// an int, so a span is at most 256 MiB: a parser bounds the unit it hands a cursor (a frame, a block) before it does.
public ref struct BitCursor
{
    private readonly ReadOnlySpan<byte> _data;

    // The next bits to read, the first in the most significant bit. Bits below the _cacheBits valid ones are either zero or already the
    // bits of the bytes that follow, which is what lets a refill OR a whole word in without masking.
    private ulong _cache;
    private int _cacheBits;

    // The index of the next byte to load; it runs past the data, where the bytes read as zero.
    private long _nextByte;

    public BitCursor(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, int.MaxValue / 8, nameof(data));

        _data = data;
    }

    // In bits from the start of the data.
    public readonly int Position => (int)Math.Min(BitPosition, int.MaxValue);

    public readonly int Length => _data.Length * 8;

    public readonly bool IsOverrun => BitPosition > Length;

    // The bits left to read; 0 once the cursor ran past the data.
    public readonly long BitsLeft => Math.Max(0, Length - BitPosition);

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

        uint value = (uint)(_cache >> (64 - count));
        _cache <<= count;
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
        if (_cacheBits == 0)
        {
            Refill();
        }

        bool bit = (long)_cache < 0;
        _cache <<= 1;
        _cacheBits--;
        return bit;
    }

    // Counts the zero bits up to the next one bit, which it consumes. It stops after limit bits without consuming
    // anything more, and at the end of the data, where it sets IsOverrun: the result is then the zero bits that
    // were left. Its time grows with the bits it consumes, a word at a time.
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

            int zeros = BitOperations.LeadingZeroCount(_cache);
            if (zeros < _cacheBits && zeros < limit)
            {
                _cache <<= zeros;
                _cache <<= 1;
                _cacheBits -= zeros + 1;
                return zeros;
            }
        }

        return Count(limit, ones: false);
    }

    // The same for one bits, up to the next zero bit. Past the end every bit is a zero. The fast path takes only a terminator that is real
    // data; where the buffer holds zeros read past the end, the slow path decides where the cursor ends.
    public int CountOnes(int limit)
    {
        if (limit > 0)
        {
            if (_cacheBits <= 32)
            {
                Refill();
            }

            int ones = BitOperations.LeadingZeroCount(~_cache);
            if (ones < _cacheBits && ones < limit && _nextByte <= _data.Length)
            {
                _cache <<= ones;
                _cache <<= 1;
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
            _cache <<= count;
            _cacheBits -= count;
            return;
        }

        MoveTo(BitPosition + count);
    }

    // The cache holds whole bytes less the bits of the byte in hand, so the bits to drop are its remainder.
    public void AlignToByte()
    {
        int drop = _cacheBits & 7;
        _cache <<= drop;
        _cacheBits -= drop;
    }

    private void Refill()
    {
        if (_nextByte + 8 <= _data.Length)
        {
            _cache |= BinaryPrimitives.ReadUInt64BigEndian(_data[(int)_nextByte..]) >> _cacheBits;
            int bytes = (63 - _cacheBits) >> 3;
            _nextByte += bytes;
            _cacheBits += bytes * 8;
            return;
        }

        while (_cacheBits <= 56)
        {
            if (_nextByte < _data.Length)
            {
                _cache |= (ulong)_data[(int)_nextByte] << (56 - _cacheBits);
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
            _cache <<= offset;
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
            ulong window = Window(position >> 3) << offset;
            int available = (int)Math.Min(64 - offset, remaining);
            int run = Math.Min(BitOperations.LeadingZeroCount(ones ? ~window : window), available);
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
