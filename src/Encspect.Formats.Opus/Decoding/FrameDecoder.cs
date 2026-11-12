using Encspect.Core;

namespace Encspect.Formats.Opus.Decoding;

// The decoding state of one Opus file: a decoder for each stream of its packets, the channel mapping that turns the streams' channels into the output's, and the span of
// output a gap or an empty frame withholds.
internal sealed class FrameDecoder : IFrameDecoder
{
    // The 80 ms the decoder withholds after a gap or an empty frame: the pre-roll RFC 7845 gives decoders "sufficient history ... that it will have converged" (sections 4.2 and
    // 4.6), 3,840 samples at 48 kHz.
    private const int Preroll = 3840;
    private const int Rate = 48000;

    // The units a sample of a channel costs on top of the floor of the budget: CELT's dequantization, inverse transform and window about 2 + 2 * log2 of the frame's length + 2,
    // its postfilter and de-emphasis 6; SILK's synthesis (the order and the long-term predictor) and its resampler about 16 per output sample.
    private const int CeltUnits = 22;
    private const int SilkUnits = 24;

    // What a CELT frame costs whatever its length (the energy and allocation passes, the band loop, the transform's set-up), measured on the frames of 5 ms, which are the cheapest per
    // sample to charge (docs/design/decoders.md, "Opus").
    private const int CeltFrameUnits = 8000;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly int[] _streamChannels;
    private readonly OpusStreamDecoder[] _decoders;
    private readonly int[] _mappingStream;
    private readonly int[] _mappingChannel;
    private readonly bool[] _isSilent;
    private float[][][] _streamPcm;
    private float[] _planar = [];
    private int _stride;
    private IPcmSink? _sink;
    private long _withheldUntil;
    private bool _lostBefore;

    public FrameDecoder(DecodeContext context, OpusHead head)
    {
        _context = context;
        _channels = head.Channels;
        int streams = head.StreamCount;
        int coupled = head.Family == 0 ? (head.Channels == 2 ? 1 : 0) : head.Coupled;
        _streamChannels = new int[streams];
        _decoders = new OpusStreamDecoder[streams];
        for (int s = 0; s < streams; s++)
        {
            _streamChannels[s] = s < coupled ? 2 : 1;
            _decoders[s] = new OpusStreamDecoder(_streamChannels[s], head.OutputGain);
        }

        _streamPcm = new float[streams][][];
        _mappingStream = new int[_channels];
        _mappingChannel = new int[_channels];
        _isSilent = new bool[_channels];
        for (int c = 0; c < _channels; c++)
        {
            int m = head.Family == 0 ? c : head.Mapping[c];
            if (m == 255)
            {
                _isSilent[c] = true;
                continue;
            }

            if (m < 2 * coupled)
            {
                _mappingStream[c] = m / 2;
                _mappingChannel[c] = m % 2;
            }
            else
            {
                _mappingStream[c] = coupled + (m - (2 * coupled));
                _mappingChannel[c] = 0;
            }
        }
    }

    private void ResetAll()
    {
        foreach (OpusStreamDecoder decoder in _decoders)
        {
            decoder.Reset();
        }
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            // The host lends at most 2^24 ints and this packet's records could need more: it is not decoded, and the one after it follows a loss.
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The packet at 0x{frame.Bytes.FileStart:X} could hold more values than the host lends buffers for, so it is not decoded.");
            _lostBefore = true;
            return 0;
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        if (frame.FollowsGap || _lostBefore)
        {
            _lostBefore = false;
            ResetAll();
            _context.Withhold(frame.FirstSample, Preroll, WithholdReason.AfterGap);
            _withheldUntil = Math.Max(_withheldUntil, frame.FirstSample + Preroll);
        }

        var r = new IntReader(lent.Narrow);
        int streams = r.Next();
        if (streams != _decoders.Length)
        {
            throw new InvalidDataException("The entropy pass stored another number of streams than the decoder has");
        }

        int samples = frame.Samples;
        EnsureBuffers(samples);
        _context.AddWork(DecodeWork.ForSample(4) * samples * _channels);
        for (int s = 0; s < streams; s++)
        {
            int frames = r.Next();
            int position = 0;
            OpusStreamDecoder decoder = _decoders[s];
            float[][] pcm = _streamPcm[s];
            for (int f = 0; f < frames; f++)
            {
                int kind = r.Next();
                int mode = r.Next();
                int bandwidth = r.Next();
                int size = r.Next();
                _ = r.Next();
                if (kind == 1)
                {
                    // No data: the stream starts over, and the frame and the 80 ms after it are withheld.
                    decoder.Reset();
                    _context.Withhold(frame.FirstSample + position, size + Preroll, WithholdReason.EmptyFrame);
                    _withheldUntil = Math.Max(_withheldUntil, frame.FirstSample + position + size + Preroll);
                    for (int c = 0; c < _streamChannels[s]; c++)
                    {
                        Array.Clear(pcm[c], position, size);
                    }
                }
                else
                {
                    _context.AddWork(((long)size * _streamChannels[s] * (mode == 0 ? SilkUnits : mode == 1 ? SilkUnits + CeltUnits : CeltUnits)) + (mode == 0 ? 0 : CeltFrameUnits));
                    decoder.DecodeFrame(ref r, pcm, position, mode, bandwidth, size);
                }

                position += size;
            }
        }

        // The output channels from the streams'.
        for (int c = 0; c < _channels; c++)
        {
            Span<float> target = _planar.AsSpan(c * _stride, samples);
            if (_isSilent[c])
            {
                target.Clear();
            }
            else
            {
                _streamPcm[_mappingStream[c]][_mappingChannel[c]].AsSpan(0, samples).CopyTo(target);
            }
        }

        long first = Math.Max(frame.FirstSample, _withheldUntil);
        int skip = (int)Math.Min(samples, first - frame.FirstSample);
        int count = samples - skip;
        if (count <= 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(Rate, _channels, 32, SampleFormat.Float32));
        _sink.Write(new PcmBlock(first, count, _channels, _stride, _planar.AsSpan(skip)));
        return count;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    private void EnsureBuffers(int samples)
    {
        if (samples <= _stride)
        {
            return;
        }

        _stride = samples;
        _planar = new float[_channels * _stride];
        for (int s = 0; s < _decoders.Length; s++)
        {
            _streamPcm[s] = new float[_streamChannels[s]][];
            for (int c = 0; c < _streamChannels[s]; c++)
            {
                _streamPcm[s][c] = new float[_stride];
            }
        }
    }
}
