using System.Runtime.CompilerServices;
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

    // The units a sample of a channel costs on top of the floor of the budget: CELT's 44 and SILK's 24 are measured (docs/design/decoders.md, "Opus"); SILK's synthesis (the order and the long-term
    // predictor) and its resampler dominate its figure. CELT's units were 22 until the nightly runs of 2026-11-13 and 2026-11-15 read its cases at up to 2.31 times the reference on
    // macos-15-intel.
    private const int CeltUnits = 44;
    private const int SilkUnits = 24;

    // What a CELT frame costs whatever its length (the energy and allocation passes, the band loop, the transform's set-up), measured on the frames of 5 ms, which are the cheapest per
    // sample to charge (docs/design/decoders.md, "Opus").
    private const int CeltFrameUnits = 12000;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly int[] _streamChannels;
    private readonly OpusStreamDecoder[] _decoders;
    private readonly int[] _mappingStream;
    private readonly int[] _mappingChannel;
    private readonly bool[] _isSilent;

    // Family 3 (RFC 8486, section 3.2): the demixing matrix in Q15, column-major, C rows by N + M columns, applied to the decoded channels (streams in order, a coupled stream's
    // left then right); null for the other families.
    private readonly float[]? _demix;
    private readonly int _decodedChannels;
    private float[] _decoded = [];
    private float[][][] _streamPcm;
    private float[] _planar = [];
    private int _stride;
    private IPcmSink? _sink;
    private long _withheldUntil;
    private bool _lostBefore;

    // After a packet that failed its checksum (CodedFrame.FailsChecksum) the decoder keeps its state, and the Preroll samples after the packet, the span a gap's reset withholds
    // too, depend on it: the samples still to withhold from the measurements (docs/design/damaged-units.md).
    private int _dependentLeft;

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
        int sources = head.Family == 3 ? streams + coupled : _channels;
        _mappingStream = new int[sources];
        _mappingChannel = new int[sources];
        _isSilent = new bool[_channels];
        _decodedChannels = streams + coupled;
        if (head.Family == 3)
        {
            _demix = new float[head.Mapping.Length / 2];
            for (int i = 0; i < _demix.Length; i++)
            {
                _demix[i] = (short)(head.Mapping[2 * i] | (head.Mapping[(2 * i) + 1] << 8)) * (1f / 32768f);
            }
        }

        for (int c = 0; c < sources; c++)
        {
            int m = head.Family == 0 ? c : head.Family == 3 ? c : head.Mapping[c];
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

    // The output channels from the streams'.
    private void MapChannels(int samples)
    {
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
    }

    // Each output channel is the sum over the decoded channels in order of the coefficient times the channel, as libopus's mapping_matrix_multiply_channel_out_float adds
    // them: the coefficient's Q15 scale is applied to it first, then the product is added to the running sum of that output channel.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Demix(int samples)
    {
        float[] matrix = _demix!;
        for (int row = 0; row < _channels; row++)
        {
            Span<float> target = _planar.AsSpan(row * _stride, samples);
            target.Clear();
            for (int col = 0; col < _decodedChannels; col++)
            {
                float coefficient = matrix[(col * _channels) + row];
                ReadOnlySpan<float> source = _streamPcm[_mappingStream[col]][_mappingChannel[col]].AsSpan(0, samples);
                for (int i = 0; i < samples; i++)
                {
                    float product = coefficient * source[i];
                    target[i] += product;
                }
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
            _dependentLeft = 0;
            return 0;
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        if (frame.FollowsGap || _lostBefore)
        {
            _lostBefore = false;
            _dependentLeft = 0;
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
                    _dependentLeft = 0;
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

        if (_demix is not null)
        {
            Demix(samples);
        }
        else
        {
            MapChannels(samples);
        }

        long first = Math.Max(frame.FirstSample, _withheldUntil);
        int skip = (int)Math.Min(samples, first - frame.FirstSample);
        int count = samples - skip;

        // Both spans count from the packet's start, so where they overlap the larger one is withheld, not their sum.
        int dependent = Math.Min(_dependentLeft, samples);
        _dependentLeft -= dependent;
        if (frame.FailsChecksum)
        {
            _dependentLeft = Preroll;
        }

        if (count <= 0)
        {
            return 0;
        }

        if (dependent > skip)
        {
            _context.WithholdFromMeasurements(first, dependent - skip);
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
