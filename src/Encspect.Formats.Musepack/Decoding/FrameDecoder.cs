// Reading derived from libmpcdec r495 (mpc_decoder.c: mpc_decoder_requantisierung, mpc_random_int in synth_filter.c, requant.c), Copyright (c) 2005-2009 The Musepack Development Team,
// under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Musepack.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    internal const int FrameSamples = 1152;

    // Per output sample of each channel, besides the fixed DecodeWork.ForSample: the polyphase synthesis's 26 (docs/design/lossy-decoders.md, "Work units per format"); and per
    // coefficient of both channels, whatever the output's channel count (libmpcdec requantizes both), requantization 2 and mid/side 1.
    internal const long SynthesisUnits = 26;
    internal const long RequantizationUnits = 3;

    // After a gap the output that depends on the lost frames is withheld: for SV4 to SV7 32 frames, libmpcdec's seeking window (FAST_SEEKING_WINDOW): the scalefactors are
    // differences from the frame before and nothing in the stream says when each subband was last coded absolutely; for SV8 the synthesis filter's 15 slots of history,
    // since a packet's first frame codes its scalefactors absolutely (docs/design/lossy-decoders.md, decision 15).
    internal const int Sv7WithheldAfterGap = 32 * FrameSamples;
    internal const int Sv8WithheldAfterGap = Synthesis.History / 2;

    private enum State
    {
        Fresh,
        Valid,
        Lost,
    }

    // The scalefactor of each index, as libmpcdec computes it (requant.c, mpc_decoder_scale_output): 1 / 32768 times 0.83298066476582673961 to the power of the index less 1, built
    // by repeated multiplication in double precision, the indexes taken modulo 256 as libmpcdec's unsigned char indexes do.
    private static readonly float[] Scale = CreateScale();

    private static readonly float[] Coefficients = [.. SynthesisTables.Coefficients.ToArray()];

    private readonly DecodeContext _context;
    private readonly MusepackConfiguration _setup;
    private readonly int _channels;
    private readonly Synthesis[] _synthesis = [new(), new()];
    private readonly float[][] _subbands = [new float[FrameSamples], new float[FrameSamples]];
    private readonly float[][] _noise = [new float[SubbandStateBands * 36], new float[SubbandStateBands * 36]];
    private readonly float[] _planar;
    private State _state = State.Fresh;
    private int _withheldLeft;
    private uint _first = 1;
    private uint _second = 1;
    private IPcmSink? _sink;

    private const int SubbandStateBands = SubbandState.Bands;

    public FrameDecoder(DecodeContext context, MusepackConfiguration setup)
    {
        _context = context;
        _setup = setup;
        _channels = setup.Channels;
        _planar = new float[_channels * FrameSamples];
    }

    private static float[] CreateScale()
    {
        var scale = new float[256];
        double factor = 1.0 / 32768.0;
        double up = factor;
        double down = factor;
        scale[1] = (float)factor;
        up *= 0.83298066476582673961;
        down *= 1 / 0.83298066476582673961;
        for (int n = 1; n <= 128; n++)
        {
            scale[(byte)(1 + n)] = (float)up;
            scale[(byte)(1 - n)] = (float)down;
            up *= 0.83298066476582673961;
            down *= 1 / 0.83298066476582673961;
        }

        return scale;
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (lent.IsEmpty || lent.Channels < 2 || lent.Stride < LentLayout.PerChannel)
        {
            throw new InvalidDataException("The frame was not walked into the lent buffers");
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        if (frame.FollowsGap)
        {
            Reset();
            _state = State.Lost;
        }

        _context.AddWork(checked(((DecodeWork.ForSample(4) + SynthesisUnits) * FrameSamples * _channels) + (RequantizationUnits * FrameSamples * 2)));
        if (_state == State.Lost)
        {
            _withheldLeft = _setup.Version == 8 ? Sv8WithheldAfterGap : Sv7WithheldAfterGap;
        }

        _state = State.Valid;
        Requantize(lent);
        for (int channel = 0; channel < _channels; channel++)
        {
            _synthesis[channel].Run(_subbands[channel], _planar.AsSpan(channel * FrameSamples, FrameSamples));
        }

        return Write(frame);
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    private void Reset()
    {
        _synthesis[0].Reset();
        _synthesis[1].Reset();
        _first = 1;
        _second = 1;
        _withheldLeft = 0;
    }

    // Writes the frame's samples to the sink as planar floats, leaving out the span after a gap (Withhold). Returns the samples written.
    private int Write(in CodedFrame frame)
    {
        int withheld = Math.Min(_withheldLeft, FrameSamples);
        if (withheld > 0)
        {
            _context.Withhold(frame.FirstSample, withheld, WithholdReason.AfterGap);
            _withheldLeft -= withheld;
        }

        int count = FrameSamples - withheld;
        if (count == 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_setup.SampleRate, _channels, 32, SampleFormat.Float32)
        {
            ChannelLayout = _channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo,
        });
        if (withheld > 0)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                _planar.AsSpan((channel * FrameSamples) + withheld, count).CopyTo(_planar.AsSpan(channel * FrameSamples, count));
            }
        }

        _sink.Write(new PcmBlock(frame.FirstSample + withheld, count, _channels, FrameSamples, _planar));
        return count;
    }

    // libmpcdec's pseudo-random generator for noise-substituted subbands (synth_filter.c, mpc_random_int): two shift registers of opposite direction whose periods are coprime.
    private uint Random()
    {
        uint first = _first;
        uint second = _second;
        uint feedbackFirst = (uint)System.Numerics.BitOperations.PopCount(first & 0xF5) & 1;
        uint feedbackSecond = (uint)System.Numerics.BitOperations.PopCount((second >> 25) & 0x63) & 1;
        _first = (first >> 1) | (feedbackFirst << 31);
        _second = (second + second) | feedbackSecond;
        return _first ^ _second;
    }

    // libmpcdec draws noise for the bands below the frame's used count in SV8 (a crafted frame can raise it above the header's last band, or leave a stale -1 above it), and up to the header's last band before.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void FillNoise(ReadOnlySpan<int> left, ReadOnlySpan<int> right, int bands)
    {
        for (int band = 0; band < bands; band++)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                if ((channel == 0 ? left : right)[LentLayout.ResolutionOffset + band] != -1)
                {
                    continue;
                }

                float[] noise = _noise[channel];
                for (int k = 0; k < 36; k++)
                {
                    uint value = Random();
                    noise[(band * 36) + k] = (int)(((value >> 24) & 0xFF) + ((value >> 16) & 0xFF) + ((value >> 8) & 0xFF) + (value & 0xFF)) - 510;
                }
            }
        }
    }

    // Requantization and mid/side (libmpcdec's mpc_decoder_requantisierung): each subband's samples times the coefficient of its resolution and the scalefactor of its third of the
    // frame, into the 36 rows of 32 subband samples of each channel.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Requantize(SampleBuffers lent)
    {
        ReadOnlySpan<int> left = lent.Narrow.Slice(0, LentLayout.PerChannel);
        ReadOnlySpan<int> right = lent.Narrow.Slice(lent.Stride, LentLayout.PerChannel);
        float[] yLeft = _subbands[0];
        float[] yRight = _subbands[1];
        int lastBand = Math.Min(_setup.MaxBand, SubbandStateBands - 1);
        FillNoise(left, right, _setup.Version == 8 ? Math.Min(left[LentLayout.UsedOffset], SubbandStateBands) : lastBand + 1);
        for (int band = 0; band <= lastBand; band++)
        {
            int resolutionLeft = left[LentLayout.ResolutionOffset + band];
            int resolutionRight = right[LentLayout.ResolutionOffset + band];
            bool midSide = left[LentLayout.MidSideOffset + band] != 0;
            for (int part = 0; part < 3; part++)
            {
                float factorLeft = resolutionLeft != 0 ? Coefficients[resolutionLeft + 1] * Scale[left[LentLayout.ScalefactorOffset + (band * 3) + part] & 0xFF] : 0;
                float factorRight = resolutionRight != 0 ? Coefficients[resolutionRight + 1] * Scale[right[LentLayout.ScalefactorOffset + (band * 3) + part] & 0xFF] : 0;
                for (int n = part * 12; n < (part + 1) * 12; n++)
                {
                    int index = (n * 32) + band;
                    float valueLeft = 0;
                    float valueRight = 0;
                    if (resolutionLeft != 0)
                    {
                        valueLeft = factorLeft * (resolutionLeft == -1 ? _noise[0][(band * 36) + n] : left[(band * 36) + n]);
                    }

                    if (resolutionRight != 0)
                    {
                        valueRight = factorRight * (resolutionRight == -1 ? _noise[1][(band * 36) + n] : right[(band * 36) + n]);
                    }

                    if (midSide)
                    {
                        if (resolutionLeft != 0 && resolutionRight != 0)
                        {
                            yLeft[index] = valueLeft + valueRight;
                            yRight[index] = valueLeft - valueRight;
                        }
                        else if (resolutionLeft != 0)
                        {
                            yLeft[index] = valueLeft;
                            yRight[index] = valueLeft;
                        }
                        else if (resolutionRight != 0)
                        {
                            yLeft[index] = valueRight;
                            yRight[index] = -valueRight;
                        }
                        else
                        {
                            yLeft[index] = 0;
                            yRight[index] = 0;
                        }
                    }
                    else
                    {
                        yLeft[index] = valueLeft;
                        yRight[index] = valueRight;
                    }
                }
            }
        }
    }
}
