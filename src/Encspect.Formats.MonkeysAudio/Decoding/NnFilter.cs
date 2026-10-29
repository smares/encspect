// Follows the Monkey's Audio SDK 13.44 (NNFilter.cpp, NNFilterGeneric.cpp, NNFilterCommon.h, Interim.cpp), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
namespace Encspect.Formats.MonkeysAudio.Decoding;

// A neural network filter of the decoder: an adaptive FIR filter over the last output samples, whose weights follow the sign of each coded value
// (NNFilter.h, CNNFilter). A filter holds the 16-bit history and weights that samples of up to 24 bits use (NnFilter16: the SDK's
// CNNFilter<int, short>), or the 32-bit ones of 32-bit samples (NnFilter32: CNNFilter<int64, int>). The sums wrap like the SDK's, on 32 bits and
// on 64 bits with 32-bit products, and the rest of the arithmetic is in the sizes the SDK uses, so the output is the same for any input.
internal sealed class NnFilter16
{
    private const int Window = 512;

    private readonly int _order;
    private readonly int _shift;
    private readonly int _round;
    private readonly bool _isInterim;
    private readonly short[] _weights;
    private readonly short[] _input;
    private readonly short[] _delta;
    private int _current;
    private int _runningAverage;

    public NnFilter16(int order, int shift, bool isInterim)
    {
        _order = order;
        _shift = shift;
        _round = 1 << (shift - 1);
        _isInterim = isInterim;
        _weights = new short[order];
        _input = new short[Window + order];
        _delta = new short[Window + order];
        Flush();
    }

    public void Flush()
    {
        Array.Clear(_weights);
        Array.Clear(_input, 0, _order + 1);
        Array.Clear(_delta, 0, _order + 1);
        _current = _order;
        _runningAverage = 0;
    }

    // Undoes the filter on one value (NNFilterGeneric.cpp, DecompressGeneric; with isInterim, Interim.cpp's, which adds the rounding in 64 bits).
    public int Decompress(int value, bool isNewDelta)
    {
        int order = _order;
        int dot = NnVectors.Dot(_input.AsSpan(_current - order, order), _weights);
        int output = _isInterim
            ? value + (int)(((long)dot + _round) >> _shift)
            : value + ((dot + _round) >> _shift);
        Adapt(value);
        Span<short> delta = _delta;
        int at = _current;
        if (isNewDelta)
        {
            int abs = output < 0 ? -output : output;
            if (abs > _runningAverage * 3)
            {
                delta[at] = (short)(((output >> 25) & 64) - 32);
            }
            else if (abs > _runningAverage * 4 / 3)
            {
                delta[at] = (short)(((output >> 26) & 32) - 16);
            }
            else if (abs > 0)
            {
                delta[at] = (short)(((output >> 27) & 16) - 8);
            }
            else
            {
                delta[at] = 0;
            }

            _runningAverage += (abs - _runningAverage) / 16;
            delta[at - 1] >>= 1;
            delta[at - 2] >>= 1;
            delta[at - 8] >>= 1;
        }
        else
        {
            delta[at] = (short)(output == 0 ? 0 : ((output >> 28) & 8) - 4);
            delta[at - 4] >>= 1;
            delta[at - 8] >>= 1;
        }

        short saturated = (short)output;
        if (saturated != output)
        {
            saturated = (short)((output >> 31) ^ 0x7FFF);
        }

        _input[at] = saturated;
        _current++;
        if (_current == Window + order)
        {
            Roll();
        }

        return output;
    }

    private void Roll()
    {
        Array.Copy(_input, _current - _order, _input, 0, _order);
        Array.Copy(_delta, _current - _order, _delta, 0, _order);
        _current = _order;
    }

    private void Adapt(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        ReadOnlySpan<short> delta = _delta.AsSpan(_current - _order, _order);
        NnVectors.AddOrSubtract(_weights, delta, isSubtract: direction > 0);
    }
}

// The filter of 32-bit samples (CNNFilter<int64, int>): history and weights of 32 bits, products of 32 bits summed in 64 bits.
internal sealed class NnFilter32
{
    private const int Window = 512;

    private readonly int _order;
    private readonly int _shift;
    private readonly int _round;
    private readonly bool _isInterim;
    private readonly int[] _weights;
    private readonly int[] _input;
    private readonly int[] _delta;
    private int _current;
    private long _runningAverage;

    public NnFilter32(int order, int shift, bool isInterim)
    {
        _order = order;
        _shift = shift;
        _round = 1 << (shift - 1);
        _isInterim = isInterim;
        _weights = new int[order];
        _input = new int[Window + order];
        _delta = new int[Window + order];
        Flush();
    }

    public void Flush()
    {
        Array.Clear(_weights);
        Array.Clear(_input, 0, _order + 1);
        Array.Clear(_delta, 0, _order + 1);
        _current = _order;
        _runningAverage = 0;
    }

    public long Decompress(long value, bool isNewDelta)
    {
        int order = _order;
        long dot = NnVectors.Dot(_input.AsSpan(_current - order, order), _weights);
        long output = value + ((dot + _round) >> _shift);
        Adapt(value);
        Span<int> delta = _delta;
        int at = _current;
        if (isNewDelta)
        {
            long abs = output < 0 ? -output : output;
            if (abs > _runningAverage * 3)
            {
                delta[at] = (int)(((output >> 25) & 64) - 32);
            }
            else if (abs > _runningAverage * 4 / 3)
            {
                delta[at] = (int)(((output >> 26) & 32) - 16);
            }
            else if (abs > 0)
            {
                delta[at] = (int)(((output >> 27) & 16) - 8);
            }
            else
            {
                delta[at] = 0;
            }

            _runningAverage += (abs - _runningAverage) / 16;
            delta[at - 1] >>= 1;
            delta[at - 2] >>= 1;
            delta[at - 8] >>= 1;
        }
        else
        {
            delta[at] = (int)(output == 0 ? 0 : ((output >> 28) & 8) - 4);
            delta[at - 4] >>= 1;
            delta[at - 8] >>= 1;
        }

        short saturated = (short)output;
        if (saturated != output)
        {
            saturated = (short)((output >> 63) ^ 0x7FFF);
        }

        _input[at] = saturated;
        _current++;
        if (_current == Window + order)
        {
            Array.Copy(_input, _current - order, _input, 0, order);
            Array.Copy(_delta, _current - order, _delta, 0, order);
            _current = order;
        }

        return output;
    }

    private void Adapt(long direction)
    {
        if (direction == 0)
        {
            return;
        }

        ReadOnlySpan<int> delta = _delta.AsSpan(_current - _order, _order);
        NnVectors.AddOrSubtract(_weights, delta, isSubtract: direction > 0);
    }
}
