using Encspect.Core;

namespace Encspect.Formats.Opus.Decoding;

// The decoder of one Opus stream: the frame by frame flow of opus_decode_frame (opus_decoder.c of libopus 1.5.2, BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md) over the
// records of the entropy pass (OpusWalker): the SILK and CELT layers, the redundant frames, and at a change of mode without redundancy the 5 ms of concealment that RFC 6716,
// section 4.5, recommends, cross-faded into the new mode's output (decision 6 of docs/design/lossy-decoders.md). Output is planar floats at 48 kHz, full scale 1.0, with the
// header's output gain applied. A frame that carries no data is not decoded here: the host's decoder resets the stream (Reset) and withholds what depends on its state.
internal sealed class OpusStreamDecoder
{
    private const int F5 = 240;
    private const int F25 = 120;
    private const int MaxFrame = 2880;

    private readonly SilkDecoder _silk;
    private readonly CeltDecoder _celt;
    private readonly int _channels;
    private readonly float _gain;
    private readonly short[][] _silkPcm;
    private readonly float[][] _redundant;
    private readonly float[][] _transition;
    private int _previousMode = -1;
    private bool _previousRedundancy;

    public OpusStreamDecoder(int channels, int outputGainQ8)
    {
        _channels = channels;
        _silk = new SilkDecoder(channels);
        _celt = new CeltDecoder(channels);
        _celt.Reset();
        _silkPcm = [new short[MaxFrame + 960], new short[MaxFrame + 960]];
        _redundant = [new float[F5], new float[F5]];
        _transition = [new float[F5], new float[F5]];
        _gain = outputGainQ8 == 0 ? 1.0f : (float)DetMath.Exp(0.6931471805599453094 * (6.48814081e-4f * outputGainQ8));
    }

    public uint Channels => (uint)_channels;

    // The state a decoder starts with.
    public void Reset()
    {
        _silk.Reset();
        _celt.Reset();
        _previousMode = -1;
        _previousRedundancy = false;
    }

    private static int BandsEnd(int bandwidth) => bandwidth switch { 0 => 13, 1 or 2 => 17, 3 => 19, _ => 21 };

    // Decodes the frame whose record starts at r (after its kind): mode, bandwidth, samples at 48 kHz and the stream's channels are the record's first four integers.
    public int DecodeFrame(ref IntReader r, float[][] pcm, int at, int mode, int bandwidth, int audiosize)
    {
        int flags = r.Next();
        bool transition = (flags & 8) != 0;
        int concealMode = (flags >> 4) & 3;
        int previousMode = _previousMode;
        bool previousRedundancy = _previousRedundancy;
        int cc = _channels;
        int frameSize = audiosize;
        int transitionSamples = Math.Min(F5, audiosize);

        if (transition && mode == 2)
        {
            Conceal(concealMode, transitionSamples, _transition, 0);
        }

        if (mode != 2)
        {
            if ((flags & 1) != 0)
            {
                _silk.Reset();
            }

            _silk.DecodeFrames(ref r, _silkPcm, 0);
        }

        bool redundancy = r.Next() != 0;
        bool celtToSilk = r.Next() != 0;
        _ = r.Next();
        if (transition && mode != 2)
        {
            Conceal(concealMode, transitionSamples, _transition, 0);
        }

        if (redundancy && celtToSilk)
        {
            _celt.Decode(ref r, _redundant, 0);
        }

        if (mode != 0 && (flags & 2) != 0)
        {
            _celt.Reset();
        }

        if (mode != 0)
        {
            _celt.Decode(ref r, pcm, at);
        }
        else
        {
            for (int c = 0; c < cc; c++)
            {
                Array.Clear(pcm[c], at, frameSize);
            }

            if ((flags & 4) != 0)
            {
                _celt.Decode(ref r, pcm, at);
            }
        }

        if (mode != 2)
        {
            for (int c = 0; c < cc; c++)
            {
                float[] y = pcm[c];
                short[] s = _silkPcm[c];
                for (int i = 0; i < frameSize; i++)
                {
                    y[at + i] += (1.0f / 32768.0f) * s[i];
                }
            }
        }

        if (redundancy && !celtToSilk)
        {
            _celt.Reset();
            _celt.Decode(ref r, _redundant, 0);
            for (int c = 0; c < cc; c++)
            {
                SmoothFade(pcm[c], at + frameSize - F25, _redundant[c], F25, pcm[c], at + frameSize - F25, F25);
            }
        }

        if (redundancy && celtToSilk && (previousMode != 0 || previousRedundancy))
        {
            for (int c = 0; c < cc; c++)
            {
                for (int i = 0; i < F25; i++)
                {
                    pcm[c][at + i] = _redundant[c][i];
                }

                SmoothFade(_redundant[c], F25, pcm[c], at + F25, pcm[c], at + F25, F25);
            }
        }

        if (transition)
        {
            for (int c = 0; c < cc; c++)
            {
                if (audiosize >= F5)
                {
                    for (int i = 0; i < F25; i++)
                    {
                        pcm[c][at + i] = _transition[c][i];
                    }

                    SmoothFade(_transition[c], F25, pcm[c], at + F25, pcm[c], at + F25, F25);
                }
                else
                {
                    SmoothFade(_transition[c], 0, pcm[c], at, pcm[c], at, F25);
                }
            }
        }

        _ = r.Next();
        ApplyGain(pcm, at, frameSize);
        _previousMode = mode;
        _previousRedundancy = redundancy && !celtToSilk;
        return frameSize;
    }

    // opus_decode_frame without data, for a mode and up to 5 ms: the SILK concealment, the CELT concealment and their sum.
    private void Conceal(int mode, int samples, float[][] pcm, int at)
    {
        int cc = _channels;
        if (mode != 2)
        {
            _silk.Conceal([_silkPcm[0], _silkPcm[1]], 0, samples);
        }

        _celt.Start = mode != 2 ? 17 : 0;
        if (mode != 0)
        {
            _celt.DecodeLost(samples, pcm, at);
        }
        else
        {
            for (int c = 0; c < cc; c++)
            {
                Array.Clear(pcm[c], at, samples);
            }
        }

        if (mode != 2)
        {
            for (int c = 0; c < cc; c++)
            {
                for (int i = 0; i < samples; i++)
                {
                    pcm[c][at + i] += (1.0f / 32768.0f) * _silkPcm[c][i];
                }
            }
        }

        ApplyGain(pcm, at, samples);
        _previousMode = mode;
        _previousRedundancy = false;
    }

    private void ApplyGain(float[][] pcm, int at, int samples)
    {
        if (_gain == 1.0f)
        {
            return;
        }

        for (int c = 0; c < _channels; c++)
        {
            for (int i = 0; i < samples; i++)
            {
                pcm[c][at + i] *= _gain;
            }
        }
    }

    // smooth_fade: the sum of in1 and in2 weighted by the square of the window.
    private static void SmoothFade(float[] in1, int in1At, float[] in2, int in2At, float[] output, int outAt, int overlap)
    {
        ReadOnlySpan<float> window = CeltTables.Window120;
        for (int i = 0; i < overlap; i++)
        {
            float w = window[i] * window[i];
            output[outAt + i] = (w * in2[in2At + i]) + ((1.0f - w) * in1[in1At + i]);
        }
    }
}
