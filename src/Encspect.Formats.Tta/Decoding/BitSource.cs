using System.Numerics;

namespace Encspect.Formats.Tta.Decoding;

// The bits of a frame's coded bytes, read as TTA's Rice codes need them: bytes in order, each from its least significant bit to its most significant.
internal ref struct BitSource
{
    private readonly ReadOnlySpan<byte> _data;
    private int _next;
    private ulong _cache;
    private int _count;

    public BitSource(ReadOnlySpan<byte> data)
    {
        _data = data;
        _next = 0;
        _cache = 0;
        _count = 0;
    }

    // The bytes the bits read so far touch.
    public readonly long ConsumedBytes => ((8L * _next) - _count + 7) / 8;

    private void Fill()
    {
        while (_count <= 56 && _next < _data.Length)
        {
            _cache |= (ulong)_data[_next++] << _count;
            _count += 8;
        }
    }

    // The number of 1 bits before the next 0 bit, which is consumed too. A run can be as long as the frame.
    public long ReadUnary()
    {
        long ones = 0;
        while (true)
        {
            if (_count == 0)
            {
                Fill();
                if (_count == 0)
                {
                    throw new EndOfStreamException();
                }
            }

            ulong valid = _count == 64 ? ulong.MaxValue : (1UL << _count) - 1;
            ulong zeros = ~_cache & valid;
            if (zeros != 0)
            {
                int run = BitOperations.TrailingZeroCount(zeros);
                ones += run;
                int used = run + 1;
                _cache = used == 64 ? 0 : _cache >> used;
                _count -= used;
                return ones;
            }

            ones += _count;
            _cache = 0;
            _count = 0;
        }
    }

    // The next count bits (0 to 32), the first one read being the least significant.
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_count < count)
        {
            Fill();
            if (_count < count)
            {
                throw new EndOfStreamException();
            }
        }

        uint value = (uint)(_cache & ((1UL << count) - 1));
        _cache >>= count;
        _count -= count;
        return value;
    }
}
