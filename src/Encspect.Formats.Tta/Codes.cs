using System.Numerics;

namespace Encspect.Formats.Tta;


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

// The two running parameters and sums of one channel's adaptive Rice codes (docs/design/tta-format.md, section 2), which every frame starts afresh.
internal sealed class RiceState
{
    public int KA;
    public int KB;
    public uint SumA;
    public uint SumB;

    public void Reset()
    {
        KA = 10;
        KB = 10;
        SumA = 1 << 14;
        SumB = 1 << 14;
    }
}

// The coded samples of a frame: its adaptive Rice codes, one for each channel at each sample time, and where they end. The decoder reconstructs the samples
// from the values; the codec parser counts the sample times of a last frame whose count no header states.
internal static class Codes
{
    // The counts of sample times (1 to maxSamples) after which the codes end in the frame's last byte, the rest of it padding: First to Last, more than one
    // only where codes of a bit or two fit the padding. maxSamples where the codes fill it with bytes to spare. Null where the bytes end inside a code first.
    // Throws InvalidDataException for a code no encoder writes.
    public static (int First, int Last)? CountSampleTimes(ReadOnlySpan<byte> coded, int channels, int maxSamples, CancellationToken cancellationToken)
    {
        var states = new RiceState[channels];
        for (int c = 0; c < channels; c++)
        {
            states[c] = new RiceState();
            states[c].Reset();
        }

        var bits = new BitSource(coded);
        int first = -1;
        int last = -1;
        int done = 0;
        try
        {
            for (; done < maxSamples; done++)
            {
                if ((done & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                for (int c = 0; c < channels; c++)
                {
                    ReadResidual(ref bits, states[c]);
                }

                if (bits.ConsumedBytes == coded.Length)
                {
                    first = first < 0 ? done + 1 : first;
                    last = done + 1;
                }
            }
        }
        catch (EndOfStreamException)
        {
            return first > 0 ? (first, last) : null;
        }

        return first > 0 ? (first, last) : (maxSamples, maxSamples);
    }

    // The adaptive Rice code of one value. The widths of the binary parts, and the sums that set them, are the channel's (tta-format.md, section 2).
    public static int ReadResidual(ref BitSource bits, RiceState state)
    {
        long unary = bits.ReadUnary();
        uint m;
        if (unary == 0)
        {
            if (state.KA > 32)
            {
                throw new InvalidDataException("A Rice parameter is beyond the 32 bits a value can have");
            }

            uint value = bits.ReadBits(state.KA);
            if (value >= 0x80000000u)
            {
                throw new InvalidDataException("A Rice code decodes to a value of 31 bits or more");
            }

            m = value;
        }
        else
        {
            if (state.KB > 32)
            {
                throw new InvalidDataException("A Rice parameter is beyond the 32 bits a value can have");
            }

            if (state.KA > 31)
            {
                throw new InvalidDataException("A Rice code decodes to a value of 31 bits or more");
            }

            ulong q = (ulong)(unary - 1) << state.KB;
            q += bits.ReadBits(state.KB);
            ulong full = q + (1UL << state.KA);
            if (full >= 0x80000000UL)
            {
                throw new InvalidDataException("A Rice code decodes to a value of 31 bits or more");
            }

            m = (uint)full;
            unchecked
            {
                state.SumB = state.SumB + (uint)q - (state.SumB >> 4);
            }

            state.KB = Adapt(state.SumB, state.KB);
        }

        unchecked
        {
            state.SumA = state.SumA + m - (state.SumA >> 4);
        }

        state.KA = Adapt(state.SumA, state.KA);
        return (m & 1) != 0 ? (int)((m + 1) >> 1) : -(int)(m >> 1);
    }

    // 2^n, saturated at 2^31 from n = 31 on.
    private static uint Pow2(int n) => n >= 31 ? 0x80000000u : 1u << n;

    private static int Adapt(uint sum, int k)
    {
        if (k > 0 && sum < Pow2(k + 4))
        {
            return k - 1;
        }

        return sum > Pow2(k + 5) ? k + 1 : k;
    }
}
