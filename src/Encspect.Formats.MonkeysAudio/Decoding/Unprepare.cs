// Follows the Monkey's Audio SDK 13.44 (Prepare.cpp, CPrepare::Unprepare), 3-clause BSD license:
// Copyright 2000-2026 Matthew T. Ashland. See THIRD-PARTY-NOTICES.md.
namespace Encspect.Formats.MonkeysAudio.Decoding;

// How the values the predictors give become the samples of the WAV data: which channels are a mid and a side channel that give a pair of
// samples and how each sample takes its bytes. The seventh channel of a seven-channel file is a plain sample like the others of the remaining
// channels (Prepare.cpp: nStartChannel is 6 for 7 channels from SDK 13.44 on; before 13.44 it started at 7, so the channel was never coded and the others were read from shifted samples).
internal sealed class Unprepare
{
    private enum Kind : byte
    {
        Raw,
        PairFirst,
        PairSecond,

        // The center and subwoofer channels of a file of six channels or more, which are plain samples that the SDK checks for 16 bits like a pair's.
        RawChecked,
    }

    private readonly Kind[] _kinds;
    private readonly int _channels;
    private readonly int _bits;
    private readonly bool _isPlain24;

    public Unprepare(int channels, int bits)
    {
        _channels = channels;
        _bits = bits;
        _isPlain24 = channels > 2;
        _kinds = new Kind[channels];
        void Pair(int first) => (_kinds[first], _kinds[first + 1]) = (Kind.PairFirst, Kind.PairSecond);

        if (bits == 32)
        {
            if (channels == 2)
            {
                Pair(0);
            }
        }
        else if (channels > 2)
        {
            if (bits != 8)
            {
                if (channels == 4)
                {
                    Pair(0);
                    Pair(2);
                }
                else if (channels >= 6)
                {
                    _kinds[2] = Kind.RawChecked;
                    _kinds[3] = Kind.RawChecked;
                    Pair(0);
                    Pair(4);
                    if (channels >= 8)
                    {
                        Pair(6);
                    }
                }
            }
        }
        else if (channels == 2)
        {
            Pair(0);
        }
    }

    public int BytesPerSample => _bits / 8;

    // Writes block i of a chunk: the samples of the channels, as signed values into the planar samples (channel c from c * stride), and as the bytes
    // of the WAV data into bytes. values are the predictors' values of the channels. Throws InvalidDataException where a sample of 16 bits would
    // not fit, as the SDK's decoder does.
    public void Write(ReadOnlySpan<int> values, int i, int stride, Span<int> samples, Span<byte> bytes)
    {
        int width = _bits / 8;
        int at = i * _channels * width;
        for (int c = 0; c < _channels; c++)
        {
            int x;
            switch (_kinds[c])
            {
                case Kind.PairFirst:
                    x = values[c] - (values[c + 1] / 2);
                    break;
                case Kind.PairSecond:
                    x = values[c - 1] - (values[c] / 2) + values[c];
                    break;
                default:
                    x = values[c];
                    break;
            }

            if (_bits == 16 && _kinds[c] != Kind.Raw && (x < short.MinValue || x > short.MaxValue))
            {
                throw new InvalidDataException("A sample of the pair does not fit 16 bits");
            }

            int sample;
            switch (_bits)
            {
                case 8:
                    bytes[at] = (byte)(x + 128);
                    sample = (byte)(x + 128) - 128;
                    break;
                case 16:
                    bytes[at] = (byte)x;
                    bytes[at + 1] = (byte)(x >> 8);
                    sample = (short)x;
                    break;
                case 24:
                    uint t = !_isPlain24 && x < 0 ? (uint)((x + 0x800000) | 0x800000) : (uint)x;
                    bytes[at] = (byte)t;
                    bytes[at + 1] = (byte)(t >> 8);
                    bytes[at + 2] = (byte)(t >> 16);
                    sample = (int)(t << 8) >> 8;
                    break;
                default:
                    bytes[at] = (byte)x;
                    bytes[at + 1] = (byte)(x >> 8);
                    bytes[at + 2] = (byte)(x >> 16);
                    bytes[at + 3] = (byte)(x >> 24);
                    sample = x;
                    break;
            }

            samples[(c * stride) + i] = sample;
            at += width;
        }
    }
}
