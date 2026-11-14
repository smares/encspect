using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // Per output sample of each channel, besides the fixed DecodeWork.ForSample: requantization 2, stereo processing 1, the 36-point inverse transform 10, the window and
    // overlap 2 and the polyphase synthesis 26 in Layer III; requantization 2, stereo 1 and synthesis 26 in Layers I and II (docs/design/lossy-decoders.md, "Work units per format").
    internal const long Layer3StageUnits = 41;
    internal const long Layer12StageUnits = 29;

    // Per output sample of each channel of a multichannel stream, besides the Layer II stages: the copies, prediction, dematrixing and de-normalization.
    internal const long MultichannelStageUnits = 8;

    // The samples withheld after a gap: the first granule and 480 samples of the next in Layer III (the overlap and the synthesis history), 480 in Layers I and II.
    internal const int Layer3WithheldAfterGap = 576 + 480;
    internal const int Layer12WithheldAfterGap = 480;

    private enum State
    {
        // Nothing has been decoded: the state is the start of a stream's, and nothing is withheld.
        Fresh,
        Valid,

        // A unit before this one was lost: the state is reset and the output that depends on it is withheld.
        Lost,
    }

    private const int MaxFrameSamples = 1152;

    private readonly DecodeContext _context;
    private readonly MpegHeader _stream;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly GranuleSyntax[] _granules = new GranuleSyntax[4];
    private readonly float[] _overlap = new float[2 * 9 * 32];
    private readonly float[] _history = new float[Polyphase.HistoryLength];
    private readonly float[] _lins = new float[Polyphase.ScratchLength];
    private readonly float[] _lines = new float[2 * 576];
    private readonly float[] _reorder = new float[576];
    private readonly float[][] _scale = [new float[40], new float[40]];
    private readonly byte[] _positions = new byte[40];
    private readonly float[] _interleaved = new float[2 * MaxFrameSamples];
    private readonly float[] _planar;
    private readonly MultichannelPlan? _plan;
    private readonly MultichannelDecoder? _multichannel;
    private readonly Layer12Scale _layer12 = new();
    private State _state = State.Fresh;
    private int _withheldLeft;
    private IPcmSink? _sink;

    public FrameDecoder(DecodeContext context, MpegHeader stream, int channels, MultichannelPlan? plan)
    {
        _context = context;
        _stream = stream;
        _plan = plan;
        _multichannel = plan is null ? null : new MultichannelDecoder(plan);
        _channels = plan?.Channels ?? Math.Clamp(channels, 1, 2);
        _sampleRate = stream.SampleRate;
        _planar = new float[_channels * MaxFrameSamples];
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> bytes = frame.Bytes.Span;
        if (bytes.Length < 4)
        {
            throw new InvalidDataException("The frame is shorter than its header");
        }

        var header = new MpegHeader(BinaryPrimitives.ReadUInt32BigEndian(bytes));
        if (!header.IsValid || !header.IsSameStream(_stream))
        {
            throw new InvalidDataException("The frame's version, layer or sample rate differs from the stream's");
        }

        SampleBuffers lent = _context.LentBuffers;
        int frameChannels = header.IsMono ? 1 : 2;
        if (_plan is not null && (header.Layer != 2 || !header.IsMpeg1 || header.IsMono))
        {
            throw new InvalidDataException("The frame is not a Layer II frame of MPEG-1 stereo, which the multichannel extension needs");
        }

        if (lent.IsEmpty || lent.Channels < (_plan is null ? frameChannels : 6) || lent.Stride < LentLayout.PerChannel)
        {
            throw new InvalidDataException("The frame was not walked into the lent buffers");
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        int samples = header.Samples;
        if (frame.FollowsGap && _state != State.Fresh)
        {
            Reset();
            _state = State.Lost;
        }

        bool isLayer3 = header.Layer == 3;
        int unavailable = isLayer3 ? lent.Narrow[LentLayout.FlagsIndex] & LentLayout.MainDataUnavailable : 0;
        _context.AddWork(checked((DecodeWork.ForSample(4) + (isLayer3 ? Layer3StageUnits : Layer12StageUnits) + (_plan is null ? 0 : MultichannelStageUnits)) * samples * _channels));
        if (unavailable != 0)
        {
            // The frame's main data are not available: nothing is decoded, and the output after it depends on state it would have left, also at the start of a stream
            // (a track split from a continuous stream), so the next frame that decodes withholds the first granule and the history.
            _context.Withhold(frame.FirstSample, samples, WithholdReason.MainDataUnavailable);
            Reset();
            _state = State.Lost;

            return 0;
        }

        if (_state == State.Lost)
        {
            _withheldLeft = isLayer3 ? Layer3WithheldAfterGap : Layer12WithheldAfterGap;
            _state = State.Valid;
        }
        else
        {
            _state = State.Valid;
        }

        if (_multichannel is not null)
        {
            _multichannel.Decode(header, bytes, lent.Narrow, lent.Stride, _planar, MaxFrameSamples);
        }
        else if (isLayer3)
        {
            DecodeLayer3(header, bytes, lent, frameChannels);
        }
        else
        {
            DecodeLayer12(header, bytes, lent, frameChannels);
        }

        return Write(frame, samples, frameChannels);
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    private void Reset()
    {
        Array.Clear(_overlap);
        Array.Clear(_history);
        _multichannel?.Reset();
        _withheldLeft = 0;
    }

    private void DecodeLayer3(MpegHeader header, ReadOnlySpan<byte> bytes, SampleBuffers lent, int channels)
    {
        int count = Layer3Syntax.ReadSideInfo(bytes, header, _granules, out int granuleChannels, out _);
        if (count < 0)
        {
            throw new InvalidDataException("The side information breaks");
        }

        int granules = granuleChannels / channels;
        bool isMidSide = header.ChannelModeBits == 1 && (header.ModeExtension & 2) != 0;
        bool isIntensity = header.ChannelModeBits == 1 && (header.ModeExtension & 1) != 0;
        bool isEightKilohertz = header.VersionBits == 0 && header.SampleRateIndex == 2;
        Span<float> lines = _lines;
        for (int g = 0; g < granules; g++)
        {
            lines.Clear();
            for (int ch = 0; ch < channels; ch++)
            {
                ref readonly GranuleSyntax gr = ref _granules[(g * channels) + ch];
                ReadOnlySpan<int> block = lent.Narrow.Slice((ch * lent.Stride) + (g * LentLayout.GranuleStride), LentLayout.GranuleStride);
                Layer3Kernels.Scalefactors(gr, block.Slice(LentLayout.ScalefactorOffset, LentLayout.Scalefactors), isMidSide, _scale[ch]);
                Layer3Kernels.Requantize(gr, block[..LentLayout.Values], _scale[ch], lines.Slice(576 * ch, 576));
            }

            if (channels == 2)
            {
                if (isIntensity)
                {
                    ReadOnlySpan<int> rightBlock = lent.Narrow.Slice(lent.Stride + (g * LentLayout.GranuleStride) + LentLayout.PositionOffset, LentLayout.Scalefactors);
                    for (int i = 0; i < 40; i++)
                    {
                        _positions[i] = (byte)rightBlock[i];
                    }

                    Layer3Kernels.IntensityStereo(header, _granules[g * 2], _granules[(g * 2) + 1], lines[..576], lines.Slice(576, 576), _positions, isMidSide);
                }
                else if (isMidSide)
                {
                    Layer3Kernels.MidSide(lines[..576], lines.Slice(576, 576), 576);
                }
            }

            for (int ch = 0; ch < channels; ch++)
            {
                ref readonly GranuleSyntax gr = ref _granules[(g * channels) + ch];
                Span<float> channelLines = lines.Slice(576 * ch, 576);
                int longBands = (gr.MixedBlock != 0 ? 2 : 0) << (isEightKilohertz ? 1 : 0);
                int antialiasBands = 31;
                if (gr.ShortBandCount != 0)
                {
                    antialiasBands = longBands - 1;
                    Layer3Kernels.Reorder(channelLines[(longBands * 18)..], _reorder, Layer3Syntax.BandWidths(gr)[gr.LongBandCount..]);
                }

                Layer3Kernels.Antialias(channelLines, antialiasBands);
                // After a mono stretch the right channel's overlap is the one the last stereo frame left, as in minimp3, which the PCM cross-check follows.
                Layer3Kernels.ImdctGranule(channelLines, _overlap.AsSpan(ch * 9 * 32, 9 * 32), gr.BlockType, longBands);
                Layer3Kernels.ChangeSign(channelLines);
            }

            Polyphase.Granule(_history, lines, 18, channels, _interleaved.AsSpan(g * 576 * channels), _lins);
        }
    }

    private void DecodeLayer12(MpegHeader header, ReadOnlySpan<byte> bytes, SampleBuffers lent, int channels)
    {
        if (!_layer12.Read(header, bytes))
        {
            throw new InvalidDataException("The allocation and scalefactors run past the frame");
        }

        int parts = header.Layer == 1 ? 1 : 3;
        Span<float> lines = _lines;
        for (int part = 0; part < parts; part++)
        {
            lines.Clear();
            for (int ch = 0; ch < channels; ch++)
            {
                for (int band = 0; band < _layer12.TotalBands; band++)
                {
                    int entry = (2 * band) + ch;
                    int ba = _layer12.ScalefactorAllocation[entry];
                    // minimp3 scales a Layer I frame's twelve slots by the third scalefactor of each entry, which repeats the first.
                    int index = _layer12.Scalefactor[(entry * 3) + (header.Layer == 1 ? 2 : part)];
                    float scale = index == 0 ? 0 : Layer12Dequantizer.Scale(ba, index - 1);
                    ReadOnlySpan<int> codes = lent.Narrow.Slice((ch * lent.Stride) + (((part * 32) + band) * 12), 12);
                    Span<float> destination = lines.Slice((576 * ch) + (band * 18), 12);
                    for (int k = 0; k < 12; k++)
                    {
                        destination[k] = (float)codes[k] * scale;
                    }
                }
            }

            Polyphase.Granule(_history, lines, 12, channels, _interleaved.AsSpan(part * 384 * channels), _lins);
        }
    }

    // Writes the frame's samples to the sink as planar floats at the stream's channel count, leaving out the span after a gap (Withhold). Returns the samples written.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Write(in CodedFrame frame, int samples, int frameChannels)
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

        int stride = MaxFrameSamples;
        for (int n = 0; n < samples && _multichannel is null; n++)
        {
            if (_channels == frameChannels)
            {
                for (int c = 0; c < _channels; c++)
                {
                    _planar[(c * stride) + n] = _interleaved[(n * frameChannels) + c];
                }
            }
            else if (_channels == 2)
            {
                float value = _interleaved[n];
                _planar[n] = value;
                _planar[stride + n] = value;
            }
            else
            {
                _planar[n] = (_interleaved[2 * n] + _interleaved[(2 * n) + 1]) * 0.5f;
            }
        }

        if (withheld > 0)
        {
            for (int c = 0; c < _channels; c++)
            {
                _planar.AsSpan((c * stride) + withheld, count).CopyTo(_planar.AsSpan(c * stride, count));
            }
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_sampleRate, _channels, 32, SampleFormat.Float32)
        {
            ChannelLayout = _plan?.Layout ?? (_channels == 1 ? ChannelLayouts.Mono : _stream.ChannelModeBits == 2 ? ChannelLayouts.Discrete(2) : ChannelLayouts.Stereo),
        });
        _sink.Write(new PcmBlock(frame.FirstSample + withheld, count, _channels, stride, _planar));
        return count;
    }
}

// The scale of a Layer I or II sample code (minimp3's g_deq_L12): 2^-20 over the quantizer's level count, with the third roots of 2 the scalefactor index adds.
internal static class Layer12Dequantizer
{
    private static readonly float[] Dequantizer = Create();

    private static float[] Create()
    {
        ReadOnlySpan<int> levels = [3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9];
        var table = new float[levels.Length * 3];
        float first = 9.53674316e-07f;
        float second = 7.56931807e-07f;
        float third = 6.00777173e-07f;
        for (int i = 0; i < levels.Length; i++)
        {
            table[(3 * i) + 0] = Divide(first, levels[i]);
            table[(3 * i) + 1] = Divide(second, levels[i]);
            table[(3 * i) + 2] = Divide(third, levels[i]);
        }

        return table;
    }

    private static float Divide(float value, int divisor) => value / divisor;

    // ba: the quantizer (2 to 19), index: the scalefactor index 0 to 63.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Scale(int ba, int index) => Dequantizer[Math.Clamp((ba * 3) - 6 + (index % 3), 0, Dequantizer.Length - 1)] * (1 << 21 >> (index / 3));
}
