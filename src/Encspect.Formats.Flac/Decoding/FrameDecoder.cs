using Encspect.Core;

namespace Encspect.Formats.Flac.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    private readonly DecodeContext _context;
    private readonly StreamInfoBlock _info;
    private readonly MappedBytes _configuration;
    private readonly Md5 _md5 = new();
    private readonly int _bytesPerSample;
    private IPcmSink? _sink;
    private int[] _narrow = [];
    private long[] _wide = [];
    private int[] _output = [];
    private byte[] _bytes = [];
    private bool _isIncomplete;

    public FrameDecoder(DecodeContext context, StreamInfoBlock info, MappedBytes configuration)
    {
        _context = context;
        _info = info;
        _configuration = configuration;
        _bytesPerSample = CanonicalPcm.BytesPerSample(info.BitsPerSample);
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Bytes.Span;
        HeaderFault fault = FrameHeader.TryRead(data, out FrameHeader header);
        if (fault is not (HeaderFault.None or HeaderFault.Crc8))
        {
            if (fault == HeaderFault.Truncated)
            {
                throw new EndOfStreamException();
            }

            throw new InvalidDataException("The frame does not start with a header that reads.");
        }

        header = header.Resolve(_info.SampleRate, _info.BitsPerSample);
        int channels = header.Channels;
        if (channels != _info.Channels)
        {
            throw new InvalidDataException($"The frame has {Counts.Of(channels, "channel", "channels")}, but STREAMINFO says {_info.Channels}.");
        }

        if (header.BitsPerSample != _info.BitsPerSample)
        {
            throw new InvalidDataException($"The frame has {Counts.Of(header.BitsPerSample, "bit", "bits")} per sample, but STREAMINFO says {_info.BitsPerSample}.");
        }

        int n = header.BlockSize;
        if ((long)Math.Max(n, frame.Samples) * channels > FlacDecoder.MaxSamplesPerFrame)
        {
            _isIncomplete = true;
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The frame at 0x{frame.Bytes.FileStart:X} has more samples than the {FlacDecoder.MaxSamplesPerFrame} over all channels that the largest legal frame holds, so it is not decoded.");
            return 0;
        }

        int stride = n + SubframeLayout.MetaSlots;
        SampleBuffers lent = frame.IsWalked ? _context.LentBuffers : default;
        bool needsWide = header.BitsPerSample == 32 && header.Assignment != ChannelAssignment.Independent;
        Span<int> narrow;
        Span<long> wide;
        if (!lent.IsEmpty && lent.Stride == stride && lent.Channels == channels && (!needsWide || !lent.Wide.IsEmpty))
        {
            narrow = lent.Narrow;
            wide = lent.Wide;
        }
        else
        {
            Walk(data, header, stride, needsWide, frame.Bytes.FileStart);
            narrow = _narrow.AsSpan(0, channels * stride);
            wide = _wide.AsSpan(0, needsWide ? channels * stride : 0);
        }

        if (_output.Length < channels * n)
        {
            _output = new int[channels * n];
        }

        for (int c = 0; c < channels; c++)
        {
            Restore(narrow.Slice(c * stride, stride), needsWide ? wide.Slice(c * stride, stride) : default, n, header.BitsPerSample + (header.IsSide(c) ? 1 : 0));
        }

        Combine(header, narrow, wide, stride, n);
        var block = new PcmBlock(frame.FirstSample, n, channels, n, _output.AsSpan(0, channels * n));
        _sink ??= _context.DeclareOutput(new PcmFormat(_info.SampleRate, channels, _info.BitsPerSample, SampleFormat.Integer) { ChannelLayout = Summary.Layout(channels) });
        _sink.Write(block);
        int size = channels * n * _bytesPerSample;
        if (_bytes.Length < size)
        {
            _bytes = new byte[size];
        }

        CanonicalPcm.Write(block, _info.BitsPerSample, _bytes);
        _md5.AppendData(_bytes.AsSpan(0, size));
        return n;
    }

    // The frame was not walked into buffers the session lent, so the same walk runs here, into buffers of this decoder.
    private void Walk(ReadOnlySpan<byte> data, in FrameHeader header, int stride, bool needsWide, long offset)
    {
        int channels = header.Channels;
        if (_narrow.Length < channels * stride)
        {
            _narrow = new int[channels * stride];
        }

        if (needsWide && _wide.Length < channels * stride)
        {
            _wide = new long[channels * stride];
        }

        var bits = new BitCursor(data);
        bits.Skip(header.Length * 8);
        for (int c = 0; c < channels; c++)
        {
            var samples = new SubframeSamples(_narrow.AsSpan(c * stride, stride), needsWide ? _wide.AsSpan(c * stride, stride) : default);
            SubframeFacts facts = SubframeSyntax.Read(ref bits, header.BlockSize, header.BitsPerSample, header.IsSide(c), samples);
            if (facts.Fault == SubframeFault.Overrun)
            {
                throw new EndOfStreamException();
            }

            if (facts.Fault != SubframeFault.None)
            {
                throw new InvalidDataException($"The frame at 0x{offset:X} cannot be read: {State.Describe(facts.Fault, c)}.");
            }
        }
    }

    // Rebuilds one channel in place: the predictor, then the wasted bits.
    private static void Restore(Span<int> narrow, Span<long> wide, int n, int sampleBits)
    {
        Span<int> meta = narrow[n..];
        var type = (SubframeType)meta[SubframeLayout.Type];
        int order = meta[SubframeLayout.Order];
        int wasted = meta[SubframeLayout.WastedBits];
        bool isWide = meta[SubframeLayout.IsWide] != 0;
        int bits = sampleBits - wasted;
        if (isWide)
        {
            Span<long> d = wide[..n];
            switch (type)
            {
                case SubframeType.Constant:
                    d.Fill(d[0]);
                    break;
                case SubframeType.Fixed:
                    Prediction.RestoreFixed(d, order, n);
                    break;
                case SubframeType.Lpc:
                    Prediction.RestoreLpc(d, meta.Slice(SubframeLayout.Coefficients, order), order, meta[SubframeLayout.Shift], n);
                    break;
            }

            if (wasted > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    d[i] <<= wasted;
                }
            }

            return;
        }

        Span<int> samples = narrow[..n];
        switch (type)
        {
            case SubframeType.Constant:
                samples.Fill(samples[0]);
                break;
            case SubframeType.Fixed:
                Prediction.RestoreFixed(samples, order, n);
                break;
            case SubframeType.Lpc:
                int precision = meta[SubframeLayout.Precision];
                Prediction.RestoreLpc(samples, meta.Slice(SubframeLayout.Coefficients, order), order, meta[SubframeLayout.Shift], n, Prediction.FitsInt(bits, precision, order));
                break;
        }

        if (wasted > 0)
        {
            for (int i = 0; i < n; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    // Undoes the channel assignment and writes the planar output (RFC 9639, section 9.2.3 for the side channel's extra bit).
    private void Combine(in FrameHeader header, Span<int> narrow, Span<long> wide, int stride, int n)
    {
        int channels = header.Channels;
        Span<int> output = _output;
        if (header.Assignment == ChannelAssignment.Independent)
        {
            for (int c = 0; c < channels; c++)
            {
                narrow.Slice(c * stride, n).CopyTo(output.Slice(c * n, n));
            }

            return;
        }

        int first = 0;
        int second = stride;
        bool firstIsWide = !wide.IsEmpty && narrow[n + SubframeLayout.IsWide] != 0;
        bool secondIsWide = !wide.IsEmpty && narrow[stride + n + SubframeLayout.IsWide] != 0;
        for (int i = 0; i < n; i++)
        {
            long a = firstIsWide ? wide[first + i] : narrow[first + i];
            long b = secondIsWide ? wide[second + i] : narrow[second + i];
            switch (header.Assignment)
            {
                case ChannelAssignment.LeftSide:
                    output[i] = (int)a;
                    output[n + i] = (int)(a - b);
                    break;
                case ChannelAssignment.SideRight:
                    output[i] = (int)(a + b);
                    output[n + i] = (int)b;
                    break;
                default:
                    long mid = (a << 1) | (b & 1);
                    output[i] = (int)((mid + b) >> 1);
                    output[n + i] = (int)((mid - b) >> 1);
                    break;
            }
        }
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        if (!end.IsComplete || _isIncomplete || StreamInfoBlock.IsMd5Unknown(_configuration.Span))
        {
            return [];
        }

        Span<byte> computed = stackalloc byte[Md5.HashLength];
        _md5.GetHashAndReset(computed);
        ReadOnlySpan<byte> stored = StreamInfoBlock.Md5(_configuration.Span);
        bool matches = computed.SequenceEqual(stored);
        if (!matches)
        {
            _context.AddFinding(Severity.Warning, "flac.md5-mismatch",
                $"The MD5 of the decoded audio is {Convert.ToHexStringLower(computed)}, but STREAMINFO stores {Convert.ToHexStringLower(stored)}: the audio differs from what the encoder read.",
                Nodes.Md5Field(_configuration));
        }

        return [new ChecksumResult(ChecksumNames.StreamInfoMd5, 1, matches ? 0 : 1)];
    }
}
