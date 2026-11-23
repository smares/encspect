using Encspect.Core;

namespace Encspect.Formats.Tak.Decoding.Lossless;

// The decoding of one TAK stream: the frames after their header, as docs/design/tak-format.md describes them. Each frame decodes on its own; the stream's state is the
// buffers and the MD5 of the PCM.
internal sealed class FrameDecoder : IFrameDecoder
{
    // Per sample of each channel, besides the fixed work of DecodeWork.ForSample: the integration, the wasted bits, the range check and the interleaving.
    internal const long StageUnits = 4;

    // Per pair of parameters of a filter of order N, the derivation of its coefficients takes N * N / 2 steps of two multiplications each.
    internal const long CoefficientUnits = 1;

    private readonly DecodeContext _context;
    private readonly DecodeShared _setup;
    private readonly int _channels;
    private readonly int _bits;
    private readonly int _bytesPerSample;
    private readonly int _minimum;
    private readonly int _maximum;
    private readonly long _perSample;
    private readonly Md5? _md5;
    private readonly int[] _terms = new int[Reconstruction.MaxOrder];
    private readonly short[] _coefficients = new short[Reconstruction.MaxOrder];
    private IPcmSink? _sink;
    private int[] _values = [];
    private short[] _history = [];
    private short[] _scratch = [];
    private byte[] _bytes = [];

    public FrameDecoder(DecodeContext context, DecodeShared setup)
    {
        _context = context;
        _setup = setup;
        StreamInfoFields info = setup.Info;
        _channels = info.Channels;
        _bits = info.BitsPerSample;
        _bytesPerSample = _bits / 8;
        _minimum = -(1 << (_bits - 1));
        _maximum = (1 << (_bits - 1)) - 1;
        _perSample = DecodeWork.ForSample(_bytesPerSample) + StageUnits;
        _md5 = setup.Md5 is null ? null : new Md5();
    }

    public int Decode(in CodedFrame frame)
    {
        // The parser walked this frame's coded data (FrameSyntax) and stored the values and the fields; the decoder reconstructs from them.
        WalkedFrame walked = _setup.Current ?? throw new InvalidDataException("The parser handed over no walk of the frame");
        int n = walked.Samples;
        if (n <= 0 || !walked.HasValues)
        {
            return 0;
        }

        _context.AddWork(checked(_perSample * _channels * n));
        _values = walked.Values;
        int total = _channels * n;
        if (!walked.IsShort)
        {
            Reconstruct(walked, n);
            Finish(walked, n);
        }
        else
        {
            CheckRange(_values.AsSpan(0, total));
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_setup.Info.SampleRate, _channels, _bits, SampleFormat.Integer) { ChannelLayout = _setup.Layout });
        if (_md5 is not null)
        {
            HashFrame(n);
        }

        _sink.Write(new PcmBlock(frame.FirstSample, n, _channels, n, _values));
        return n;
    }

    // Sections 5 and 6: the subframes of each channel in order, then the decorrelations in the order the fields came in.
    private void Reconstruct(WalkedFrame walked, int n)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        foreach (SubframeRecord subframe in walked.Subframes)
        {
            Predict(walked, subframe, n);
        }

        foreach (DecorrelationRecord d in walked.Decorrelations)
        {
            Span<int> xs = _values.AsSpan(d.X * n, n);
            Span<int> ys = _values.AsSpan(d.Y * n, n);
            switch (d.Kind)
            {
                case DecorrelationKind.Add:
                    Reconstruction.Add(xs, ys);
                    break;
                case DecorrelationKind.Subtract:
                    Reconstruction.Subtract(xs, ys);
                    break;
                case DecorrelationKind.MidSide:
                    Reconstruction.MidSide(xs, ys);
                    break;
                case DecorrelationKind.Scaled:
                    Reconstruction.Scaled(xs, ys, d.Shift, d.Gain);
                    break;
                default:
                    // A tap for each sample of the frame, which the units a sample of the frame's stages do not cover.
                    _context.AddWork(checked((long)d.Filter!.Length * (n - 1) * DecodeWork.PerTap));
                    if (_scratch.Length < n - 1)
                    {
                        _scratch = new short[n - 1];
                    }

                    Reconstruction.Filter(xs, ys, d.Shift, d.Filter, d.IsHeadAdded, d.IsTailAdded, _scratch);
                    break;
            }
        }
    }

    // Section 5: the warm-up values integrated, then the predicted values from the residuals the walk stored at their positions.
    private void Predict(WalkedFrame walked, SubframeRecord s, int n)
    {
        Span<int> x = _values.AsSpan(s.Channel * n, n);
        if (!s.IsPredicted)
        {
            return;
        }

        int order = s.Order;
        int warm = s.IsContinued ? 0 : order;
        if (!s.IsContinued)
        {
            Reconstruction.Integrate(x.Slice(s.Position, order), s.WarmOrder);
        }

        int predicted = s.Length - warm;
        _context.AddWork(checked((((long)predicted * order) + ((long)order * order * CoefficientUnits)) * DecodeWork.PerTap));
        Reconstruction.Coefficients(walked.Parameters.AsSpan(s.ParametersAt, order), s.Precision, _terms, _coefficients);
        int needed = order + predicted;
        if (_history.Length < needed)
        {
            _history = new short[Math.Max(needed, 2 * _history.Length)];
        }

        Span<short> history = _history.AsSpan(0, needed);
        int shift = s.Shift;
        int source = s.IsContinued ? s.Position - order : s.Position;
        unchecked
        {
            for (int i = 0; i < order; i++)
            {
                history[i] = (short)(x[source + i] >> shift);
            }

            ReadOnlySpan<short> coefficients = _coefficients.AsSpan(0, order);
            int round = 1 << (s.Precision - 1);
            int start = s.Position + warm;
            for (int i = 0; i < predicted; i++)
            {
                int a = round + Reconstruction.Dot(history.Slice(i, order), coefficients);
                int value = (Reconstruction.Clip14(a >> s.Precision) << shift) - x[start + i];
                x[start + i] = value;
                history[order + i] = (short)(value >> shift);
            }
        }
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        if (_md5 is null || _setup.Md5 is not { } stored || !end.IsComplete)
        {
            return [];
        }

        Span<byte> computed = stackalloc byte[Md5.HashLength];
        _md5.GetHashAndReset(computed);
        bool matches = computed.SequenceEqual(stored);
        if (!matches)
        {
            _context.AddFinding(Severity.Warning, "tak.md5-mismatch",
                $"The MD5 of the decoded audio is {Convert.ToHexStringLower(computed)}, but the MD5 object stores {Convert.ToHexStringLower(stored)}: the audio differs from what the encoder read.",
                new Node("MD5", _setup.Md5At, Md5.HashLength));
        }

        return [new ChecksumResult(ChecksumNames.Md5, 1, matches ? 0 : 1)];
    }

    // Section 7: the integration of the channel's difference order and its wasted bits, with the range check of section 8.
    private void Finish(WalkedFrame walked, int n)
    {
        for (int c = 0; c < _channels; c++)
        {
            Span<int> x = _values.AsSpan(c * n, n);
            Reconstruction.Integrate(x, walked.DifferenceOrder[c]);
            int wasted = walked.Wasted[c];
            if (wasted > 0)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    x[i] <<= wasted;
                }
            }
        }

        CheckRange(_values.AsSpan(0, _channels * n));
    }

    private void CheckRange(ReadOnlySpan<int> values)
    {
        int low = int.MaxValue;
        int high = int.MinValue;
        foreach (int v in values)
        {
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        if (low < _minimum || high > _maximum)
        {
            throw new InvalidDataException($"A decoded sample is {(low < _minimum ? low : high)}, outside the {_bits}-bit range");
        }
    }

    // Section 9: the MD5 is of the samples as a WAVE file's data chunk holds them, interleaved, little-endian; 8-bit samples are unsigned bytes there.
    private void HashFrame(int n)
    {
        int width = _bytesPerSample;
        int size = checked(n * _channels * width);
        if (_bytes.Length < size)
        {
            _bytes = new byte[size];
        }

        int at = 0;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int v = _values[(c * n) + i];
                if (width == 1)
                {
                    _bytes[at++] = (byte)(v + 128);
                }
                else
                {
                    for (int b = 0; b < width; b++)
                    {
                        _bytes[at++] = (byte)(v >> (8 * b));
                    }
                }
            }
        }

        _md5!.AppendData(_bytes.AsSpan(0, size));
    }
}
