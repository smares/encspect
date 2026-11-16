// Reading derived from libspeex 1.2.1 (stereo.c: speex_std_stereo_request_handler, speex_decode_stereo_int; speexdec.c: the frame loop), Copyright (c) 2002-2008 Jean-Marc Valin
// and Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using Encspect.Core;
using static Encspect.Formats.Speex.Decoding.Fixed;

namespace Encspect.Formats.Speex.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // Per output sample of the decoded band besides the fixed DecodeWork.ForSample of each channel: the synthesis and the enhancer, set by DecoderWorkTests
    // (docs/design/decoders.md, "As built: the Speex decoder's work weights").
    internal const long SynthesisUnits = 40;

    // After a gap the output that depends on the lost frames' state is withheld: the excitation history of the pitch predictor and the synthesis filters settle within a frame
    // (docs/design/lossy-decoders.md, "As built: Speex").
    internal const int WithheldFrames = 1;

    private enum State
    {
        Fresh,
        Valid,
        Lost,
    }

    private static readonly short[] RatioQuant = [8192, 10332, 13009, 16384];

    private readonly DecodeContext _context;
    private readonly int _rate;
    private readonly int _mode;
    private readonly int _frameSize;
    private readonly int _channels;
    private readonly Narrowband? _narrowband;
    private readonly Subband? _subband;
    private readonly short[] _output;
    private State _state = State.Fresh;
    private int _withheldLeft;
    private int[] _planar = [];
    private IPcmSink? _sink;

    // The stereo state of speex_stereo.h (RealSpeexStereoState): balance and energy ratio from the last request, the smoothed gains of the channels.
    private int _balance = 65536;
    private int _energyRatio = 16384;
    private int _smoothLeft = 16384;
    private int _smoothRight = 16384;

    public FrameDecoder(DecodeContext context, SpeexHeader header)
    {
        _context = context;
        _rate = header.Rate;
        _mode = header.Mode;
        _frameSize = 160 << _mode;
        _channels = header.DecodedChannels;
        _narrowband = _mode == 0 ? new Narrowband(false) : null;
        _subband = _mode == 0 ? null : new Subband(_mode == 2);
        _output = new short[2 * _frameSize];
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (lent.IsEmpty)
        {
            throw new InvalidDataException("The packet was not walked into the lent buffers");
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        int frames = frame.Samples / _frameSize;
        _context.AddWork(checked(((DecodeWork.ForSample(2) * _channels) + SynthesisUnits) * frame.Samples));
        if (_state == State.Lost || (frame.FollowsGap && _state == State.Valid))
        {
            Reset();
            _withheldLeft = WithheldFrames * _frameSize;
        }

        // A packet that cannot be decoded leaves the decoder in a state libspeex would not have: it starts over at the next packet.
        _state = State.Lost;
        int samples = frames * _frameSize;
        if (_planar.Length < _channels * samples)
        {
            _planar = new int[_channels * samples];
        }

        for (int f = 0; f < frames; f++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<int> fields = lent.Narrow.Slice(f * SpeexLayout.FrameInts, SpeexLayout.FrameInts);
            if (_channels == 2 && (fields[SpeexLayout.Stereo] & 1) != 0)
            {
                int request = fields[SpeexLayout.Stereo];
                int sign = (request & 2) != 0 ? -1 : 1;
                _balance = Exp(Mult16_16(sign, Shl16((request >> 2) & 31, 9)));
                _energyRatio = RatioQuant[(request >> 7) & 3];
            }

            if (_narrowband is not null)
            {
                _narrowband.Decode(fields.Slice(SpeexLayout.Narrowband), fields[SpeexLayout.NarrowbandSubmode], _output, default);
            }
            else
            {
                _subband!.Decode(fields, _output, default);
            }

            if (_channels == 2)
            {
                DecodeStereo(_output, _frameSize);
                for (int i = 0; i < _frameSize; i++)
                {
                    _planar[(f * _frameSize) + i] = _output[2 * i];
                    _planar[samples + (f * _frameSize) + i] = _output[(2 * i) + 1];
                }
            }
            else
            {
                for (int i = 0; i < _frameSize; i++)
                {
                    _planar[(f * _frameSize) + i] = _output[i];
                }
            }
        }

        _state = State.Valid;
        return Write(frame, samples);
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    // Starts over as at the beginning of the stream.
    private void Reset()
    {
        _narrowband?.Reset();
        _subband?.Reset();
        Array.Clear(_output);
        _balance = 65536;
        _energyRatio = 16384;
        _smoothLeft = 16384;
        _smoothRight = 16384;
        _withheldLeft = 0;
    }

    // Writes the packet's samples to the sink as planar integers, leaving out the span after a gap (Withhold). Returns the samples written.
    private int Write(in CodedFrame frame, int samples)
    {
        int withheld = Math.Min(_withheldLeft, samples);
        if (withheld > 0)
        {
            _context.Withhold(frame.FirstSample, withheld, WithholdReason.AfterGap);
            _withheldLeft -= withheld;
        }

        int count = samples - withheld;
        if (count == 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_rate, _channels, 16, SampleFormat.Integer)
        {
            ChannelLayout = _channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo,
        });
        if (withheld > 0)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                _planar.AsSpan((channel * samples) + withheld, count).CopyTo(_planar.AsSpan(channel * samples, count));
            }
        }

        _sink.Write(new PcmBlock(frame.FirstSample + withheld, count, _channels, samples, _planar));
        return count;
    }

    // speex_decode_stereo_int: the mono frame becomes interleaved left and right, in place, from the last end.
    private void DecodeStereo(Span<short> data, int frameSize)
    {
        int eRight = (short)Divide(4194304, Sqrt(Mult16_32_Q15(_energyRatio, 65536 + _balance)));
        int eLeft = (short)(Mult16_16(Sqrt(_balance), eRight) >> 8);
        for (int i = frameSize - 1; i >= 0; i--)
        {
            int tmp = data[i];
            _smoothLeft = (short)Pshr(Mult16_16(_smoothLeft, 32113) + Mult16_16(eLeft, 655), 15);
            _smoothRight = (short)Pshr(Mult16_16(_smoothRight, 32113) + Mult16_16(eRight, 655), 15);
            data[2 * i] = (short)Mult16_16_P14(_smoothLeft, tmp);
            data[(2 * i) + 1] = (short)Mult16_16_P14(_smoothRight, tmp);
        }
    }
}
