using Encspect.Core;

namespace Encspect.Formats.Tta.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // The most sample times (one sample of every channel) decoded between two checks of the token, two charges and two writes to the sink.
    internal const int MaxChunk = 4096;

    // The values of a channel's filter, Rice codes and predictor. Every frame starts from the same state.
    internal sealed class Channel
    {
        public readonly int[] W = new int[8];
        public readonly int[] V = new int[8];
        public readonly int[] S = new int[8];
        public readonly RiceState Rice = new();
        public int PreviousResidual;
        public int Previous;

        public void Reset()
        {
            Array.Clear(W);
            Array.Clear(V);
            Array.Clear(S);
            PreviousResidual = 0;
            Previous = 0;
            Rice.Reset();
        }
    }

    // Per sample of each channel, besides the fixed work of DecodeWork.ForSample: the filter (eight taps to predict from and eight to update and step), the predictor
    // and the undoing of the decorrelation (10: 16-bit stereo, the costliest per unit in DecoderWorkTests, set it).
    internal const long FilterUnits = 24;
    internal const long PredictorUnits = 10;

    private readonly DecodeContext _context;
    private readonly uint _sampleRate;
    private readonly int _channels;
    private readonly int _depth;
    private readonly int _filterShift;
    private readonly int _predictorShift;
    private readonly int _minimum;
    private readonly int _maximum;
    private readonly int _chunk;
    private readonly Channel[] _state;
    private readonly int[] _coded;
    private readonly int[] _output;
    private readonly long _perSample;
    private IPcmSink? _sink;

    public FrameDecoder(DecodeContext context, uint sampleRate, int channels, int bitsPerSample)
    {
        _context = context;
        _sampleRate = sampleRate;
        _channels = channels;
        _depth = (bitsPerSample + 7) / 8;
        _filterShift = _depth == 2 ? 9 : 10;
        _predictorShift = _depth == 1 ? 4 : 5;
        _minimum = -(1 << ((8 * _depth) - 1));
        _maximum = (1 << ((8 * _depth) - 1)) - 1;
        _chunk = Math.Clamp((1 << 16) / _channels, 1, MaxChunk);
        _state = new Channel[_channels];
        for (int c = 0; c < _channels; c++)
        {
            _state[c] = new Channel();
        }

        _coded = new int[_channels];
        _output = new int[_chunk * _channels];
        _perSample = DecodeWork.ForSample(_depth) + FilterUnits + PredictorUnits;
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Bytes.Span;
        if (data.Length < 4)
        {
            throw new InvalidDataException("The frame is shorter than its CRC-32");
        }

        int samples = frame.Samples;
        if (samples <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> coded = data[..^4];

        ChargeCodes(coded.Length);

        _sink ??= _context.DeclareOutput(new PcmFormat((int)Math.Min(_sampleRate, int.MaxValue), _channels, 8 * _depth, SampleFormat.Integer)
        {
            ChannelLayout = _channels switch { 1 => [Speaker.FrontCenter], 2 => [Speaker.FrontLeft, Speaker.FrontRight], _ => null },
        });

        foreach (Channel channel in _state)
        {
            channel.Reset();
        }

        var bits = new BitSource(coded);
        for (int done = 0; done < samples;)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(_chunk, samples - done);

            // A sample of every channel takes one bit or more, so the bytes left cap what a chunk can decode; the charge follows the smaller count.
            long possible = Math.Max(1, ((8L * (coded.Length - bits.ConsumedBytes)) + 8) / _channels);
            _context.AddWork(checked(_perSample * _channels * Math.Min(count, possible)));
            DecodeChunk(ref bits, count);
            _sink.Write(new PcmBlock(frame.FirstSample + done, count, _channels, _chunk, _output));
            done += count;
        }

        // The reference ignores bytes after the last code and its byte of padding; they are reported, since no checksum over the decoded audio would show a
        // frame whose codes end early (tta-format.md, section 8).
        long after = coded.Length - bits.ConsumedBytes;
        if (after > 0)
        {
            MappedBytes bytes = frame.Bytes;
            IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
            _context.AddFinding(Severity.Info, "tta.frame-bytes-after-codes",
                $"The codes of the frame at 0x{bytes.FileStart:X} end {Counts.Of(after, "byte", "bytes")} before its CRC-32; the bytes after them are not read, as the reference decoder does not read them either.",
                new Node(frame.NodeName, bytes.FileStart, bytes.FileEnd - bytes.FileStart) { Ranges = ranges.Count > 1 ? ranges : null });
        }

        return samples;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    // The Rice codes are charged for the bytes they come from; a frame of the largest size the parser hands over takes 2^30 units, so they are charged in slices.
    private void ChargeCodes(long length)
    {
        for (long left = length; left > 0;)
        {
            long slice = Math.Min(left, 1L << 22);
            _context.AddWork(slice * DecodeWork.PerCodedByte);
            _context.CancellationToken.ThrowIfCancellationRequested();
            left -= slice;
        }
    }

    private void DecodeChunk(ref BitSource bits, int count)
    {
        int channels = _channels;
        int[] x = _coded;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                Channel state = _state[c];
                int residual = Codes.ReadResidual(ref bits, state.Rice);
                int filtered = Filter(state, residual, _filterShift);
                unchecked
                {
                    int value = filtered + ((state.Previous * ((1 << _predictorShift) - 1)) >> _predictorShift);
                    state.Previous = value;
                    x[c] = value;
                }
            }

            // The channels are coded as differences of neighbors and a last channel from the difference before it; the division is by truncation.
            unchecked
            {
                int last = channels - 1;
                int next = channels == 1 ? x[0] : x[last] + (x[last - 1] / 2);
                Put(last, i, next);
                for (int c = last - 1; c >= 0; c--)
                {
                    next -= x[c];
                    Put(c, i, next);
                }
            }
        }
    }

    private void Put(int channel, int index, int value)
    {
        if (value < _minimum || value > _maximum)
        {
            throw new InvalidDataException($"A decoded sample is {value}, outside the {8 * _depth}-bit range");
        }

        _output[(channel * _chunk) + index] = value;
    }

    // The adaptive filter of eight taps whose coefficients follow the signs of the history: sign-sign least mean squares, in 32-bit wraparound arithmetic.
    internal static int Filter(Channel state, int residual, int shift)
    {
        int[] w = state.W;
        int[] v = state.V;
        int[] s = state.S;
        unchecked
        {
            if (state.PreviousResidual > 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    w[j] += s[j];
                }
            }
            else if (state.PreviousResidual < 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    w[j] -= s[j];
                }
            }

            int sum = 1 << (shift - 1);
            for (int j = 0; j < 8; j++)
            {
                sum += v[j] * w[j];
            }

            s[0] = s[1];
            s[1] = s[2];
            s[2] = s[3];
            s[3] = s[4];
            s[4] = v[4] >= 0 ? 1 : -1;
            s[5] = v[5] >= 0 ? 2 : -2;
            s[6] = v[6] >= 0 ? 2 : -2;
            s[7] = v[7] >= 0 ? 4 : -4;

            int y = residual + (sum >> shift);
            state.PreviousResidual = residual;
            v[0] = v[1];
            v[1] = v[2];
            v[2] = v[3];
            v[3] = v[4];
            int d1 = y - v[7];
            int d2 = d1 - v[6];
            int d3 = d2 - v[5];
            v[4] = d3;
            v[5] = d2;
            v[6] = d1;
            v[7] = y;
            return y;
        }
    }
}
