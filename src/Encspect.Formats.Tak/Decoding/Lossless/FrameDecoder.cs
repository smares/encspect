using Encspect.Core;

namespace Encspect.Formats.Tak.Decoding.Lossless;

// The decoding of one TAK stream: the frames after their header, as docs/design/tak-format.md describes them. Each frame decodes on its own; the stream's state is the
// buffers and the MD5 of the PCM.
internal sealed class FrameDecoder : IFrameDecoder
{
    // The shortest frame that is coded with predictors (section 2); below it the samples are stored.
    internal const int ShortFrame = 16;

    // Per sample of each channel, besides the fixed work of DecodeWork.ForSample: the integration, the wasted bits, the range check and the interleaving.
    internal const long StageUnits = 4;

    // Per pair of parameters of a filter of order N, the derivation of its coefficients takes N * N / 2 steps of two multiplications each.
    internal const long CoefficientUnits = 1;

    private readonly DecodeContext _context;
    private readonly DecodeShared _setup;
    private readonly int _codec;
    private readonly int _channels;
    private readonly int _bits;
    private readonly int _bytesPerSample;
    private readonly int _frameLength;
    private readonly int _partition;
    private readonly int _unit;
    private readonly int _minimum;
    private readonly int _maximum;
    private readonly long _perSample;
    private readonly Md5? _md5;
    private readonly int[] _wasted;
    private readonly int[] _order;
    private readonly int[] _parameters = new int[Reconstruction.MaxOrder];
    private readonly int[] _terms = new int[Reconstruction.MaxOrder];
    private readonly short[] _coefficients = new short[Reconstruction.MaxOrder];
    private IPcmSink? _sink;
    private int[] _values = [];
    private int[] _residuals = [];
    private short[] _history = [];
    private byte[] _bytes = [];

    public FrameDecoder(DecodeContext context, DecodeShared setup, int frameLength)
    {
        _context = context;
        _setup = setup;
        StreamInfoFields info = setup.Info;
        _codec = info.Codec;
        _channels = info.Channels;
        _bits = info.BitsPerSample;
        _bytesPerSample = _bits / 8;
        _frameLength = frameLength;
        _partition = ResidualCodes.PartitionLength(info.SampleRate);
        _unit = 2 * (((info.SampleRate + 511) >> 9) + 3 & ~3);
        _minimum = -(1 << (_bits - 1));
        _maximum = (1 << (_bits - 1)) - 1;
        _perSample = DecodeWork.ForSample(_bytesPerSample) + StageUnits;
        _md5 = setup.Md5 is null ? null : new Md5();
        _wasted = new int[_channels];
        _order = new int[_channels];
    }

    public int Decode(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Bytes.Span;
        FrameHeader header = FrameHeader.Read(data);
        if (header.Fault == HeaderFault.Cut)
        {
            throw new EndOfStreamException();
        }

        if (header.Fault is HeaderFault.Sync or HeaderFault.Structure)
        {
            throw new InvalidDataException("The frame does not start with a header that reads");
        }

        if (data.Length < header.Length + Layout.CrcLength)
        {
            throw new EndOfStreamException();
        }

        int n = header.IsLast ? header.Samples : _frameLength;
        if (n <= 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> coded = data.Slice(header.Length, data.Length - header.Length - Layout.CrcLength);
        _context.AddWork(checked(_perSample * _channels * n));
        _context.AddWork(checked(DecodeWork.PerCodedByte * coded.Length));
        int total = checked(_channels * n);
        if (_values.Length < total)
        {
            _values = new int[total];
        }

        var bits = new LsbBitCursor(coded);
        if (n < ShortFrame)
        {
            for (int i = 0; i < total; i++)
            {
                _values[i] = bits.ReadSigned(_bits);
            }
        }
        else if (_codec == 2)
        {
            ReadFixedLayout(ref bits, n);
        }
        else
        {
            ReadChannelTable(ref bits, n);
        }

        if (bits.IsOverrun)
        {
            throw new EndOfStreamException();
        }

        bits.AlignToByte();
        int consumed = (bits.Position + 7) / 8;
        if (consumed > coded.Length)
        {
            throw new InvalidDataException("The frame's fields run past its coded data");
        }

        if (n >= ShortFrame)
        {
            Finish(n);
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
        if (consumed < coded.Length)
        {
            MappedBytes bytes = frame.Bytes;
            long after = coded.Length - consumed;
            IReadOnlyList<ByteRange> ranges = bytes.FileRanges();
            _context.AddFinding(Severity.Info, "tak.frame-bytes-after-data",
                $"The coded data of the frame at 0x{bytes.FileStart:X} ends {Counts.Of(after, "byte", "bytes")} before its CRC-24; the bytes after the last field are not read, as TAK's decoder does not read them either.",
                new Node(frame.NodeName, bytes.FileStart, bytes.FileEnd - bytes.FileStart) { Ranges = ranges.Count > 1 ? ranges : null });
        }

        return n;
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

    // Section 2, codec 2: one or two channels, then for two the decorrelation.
    private void ReadFixedLayout(ref LsbBitCursor bits, int n)
    {
        for (int c = 0; c < _channels; c++)
        {
            ReadChannel(ref bits, c, n);
        }

        if (_channels == 2)
        {
            // Section 6.1: a bit and, set, 6 bits the source reads and ignores; then the mode.
            if (bits.ReadBit())
            {
                bits.Read(6);
            }

            int mode = (int)bits.Read(3);
            switch (mode)
            {
                case 0:
                    break;
                case 1:
                    Decorrelate(ref bits, Mode.Add, 0, 1, n);
                    break;
                case 2:
                    Decorrelate(ref bits, Mode.Subtract, 0, 1, n);
                    break;
                case 3:
                    Decorrelate(ref bits, Mode.MidSide, 0, 1, n);
                    break;
                case 4:
                    Decorrelate(ref bits, Mode.Scaled, 1, 0, n);
                    break;
                case 5:
                    Decorrelate(ref bits, Mode.Scaled, 0, 1, n);
                    break;
                case 6:
                    Decorrelate(ref bits, Mode.Filter, 1, 0, n);
                    break;
                default:
                    Decorrelate(ref bits, Mode.Filter, 0, 1, n);
                    break;
            }
        }
    }

    // Section 6.2, codec 4: the table of entries, then each entry's channel data and decorrelation.
    private void ReadChannelTable(ref LsbBitCursor bits, int n)
    {
        int entries = _channels;
        bool isTable = bits.ReadBit();
        Span<int> channel = stackalloc int[16];
        Span<int> type = stackalloc int[16];
        Span<int> partner = stackalloc int[16];
        Span<bool> paired = stackalloc bool[16];
        Span<bool> used = stackalloc bool[16];
        if (!isTable)
        {
            for (int i = 0; i < entries; i++)
            {
                channel[i] = i;
            }
        }
        else
        {
            entries = (int)bits.Read(4) + 1;
            if (entries > _channels)
            {
                throw new InvalidDataException($"The channel table has {entries} entries for {Counts.Of(_channels, "channel", "channels")}");
            }

            for (int i = 0; i < entries; i++)
            {
                int m = (int)bits.Read(4);
                if (m >= _channels || used[m])
                {
                    throw new InvalidDataException($"The channel table lists channel {m} {(m >= _channels ? "which the stream does not have" : "twice")}");
                }

                channel[i] = m;
                if (bits.ReadBit())
                {
                    paired[i] = true;
                    type[i] = (int)bits.Read(2);
                    int r = (int)bits.Read(4);
                    if (r >= _channels)
                    {
                        throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which the stream does not have");
                    }

                    partner[i] = r;
                    if (type[i] == 1)
                    {
                        if (r == m || used[r])
                        {
                            throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which is the same channel or has an entry already");
                        }

                        used[r] = true;
                    }
                    else if (!used[r])
                    {
                        throw new InvalidDataException($"The channel table pairs channel {m} with channel {r}, which has no entry before");
                    }
                }

                used[m] = true;
            }

            for (int c = 0; c < _channels; c++)
            {
                if (!used[c])
                {
                    throw new InvalidDataException($"The channel table leaves channel {c} out");
                }
            }
        }

        for (int i = 0; i < entries; i++)
        {
            if (paired[i] && type[i] == 1)
            {
                ReadChannel(ref bits, partner[i], n);
            }

            ReadChannel(ref bits, channel[i], n);
            if (!paired[i])
            {
                continue;
            }

            int m = channel[i];
            int r = partner[i];
            switch (type[i])
            {
                case 0:
                    Decorrelate(ref bits, Mode.Add, r, m, n);
                    break;
                case 1:
                    Decorrelate(ref bits, Mode.MidSide, r, m, n);
                    break;
                case 2:
                    Decorrelate(ref bits, Mode.Scaled, m, r, n);
                    break;
                default:
                    Decorrelate(ref bits, Mode.Filter, m, r, n);
                    break;
            }
        }
    }

    private enum Mode
    {
        Add,
        Subtract,
        MidSide,
        Scaled,
        Filter,
    }

    // Reads the mode's own fields and applies it to channels x and y (section 6).
    private void Decorrelate(ref LsbBitCursor bits, Mode mode, int x, int y, int n)
    {
        Span<int> xs = _values.AsSpan(x * n, n);
        Span<int> ys = _values.AsSpan(y * n, n);
        switch (mode)
        {
            case Mode.Add:
                Reconstruction.Add(xs, ys);
                break;
            case Mode.Subtract:
                Reconstruction.Subtract(xs, ys);
                break;
            case Mode.MidSide:
                Reconstruction.MidSide(xs, ys);
                break;
            case Mode.Scaled:
                int shift = ReadFlag4(ref bits);
                int gain = bits.ReadSigned(10);
                Reconstruction.Scaled(xs, ys, shift, gain);
                break;
            default:
                ReadFilter(ref bits, xs, ys);
                break;
        }
    }

    private void ReadFilter(ref LsbBitCursor bits, Span<int> x, Span<int> y)
    {
        int shift = ReadFlag4(ref bits);
        int order = bits.ReadBit() ? 16 : 8;
        bool isHead = bits.ReadBit();
        bool isTail = bits.ReadBit();
        Span<short> g = stackalloc short[16];
        for (int i = 0; i < order; i += 4)
        {
            int width = 14 - (int)bits.Read(3);
            for (int j = 0; j < 4; j++)
            {
                g[i + j] = (short)bits.ReadSigned(width);
            }
        }

        if (x.Length - 1 < 256)
        {
            throw new InvalidDataException("The filter mode is used on a frame of fewer than 257 samples");
        }

        // Charged before the work: a tap for each sample of the middle, which the units a sample of the frame's stages do not cover.
        _context.AddWork(checked((long)order * (x.Length - 1) * DecodeWork.PerTap));
        Span<short> scratch = x.Length - 1 <= 1024 ? stackalloc short[1024] : new short[x.Length - 1];
        Reconstruction.Filter(x, y, shift, g[..order], isHead, isTail, scratch);
    }

    // Section 4: the channel data, ending with the subframes' values in the channel's slot of the frame buffer.
    private void ReadChannel(ref LsbBitCursor bits, int channel, int n)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        Span<int> x = _values.AsSpan(channel * n, n);
        int wasted = ReadFlag4(ref bits);
        if (wasted >= _bits)
        {
            throw new InvalidDataException($"A channel states {wasted} wasted bits of {_bits}");
        }

        _wasted[channel] = wasted;
        x[0] = bits.ReadSigned(_bits - wasted);
        _order[channel] = (int)bits.Read(2);
        int count = (int)bits.Read(3) + 1;
        Span<int> boundaries = stackalloc int[8];
        for (int i = 1; i < count; i++)
        {
            boundaries[i] = (int)bits.Read(6);
        }

        // The subframes are the boundaries' multiples of the unit and the rest, each at least 1 sample (section 4): checked before any is read, so that none runs past the channel.
        for (int i = 1; i < count; i++)
        {
            if (boundaries[i] <= boundaries[i - 1])
            {
                throw new InvalidDataException($"A subframe of a channel has {(boundaries[i] - boundaries[i - 1]) * _unit} samples, and at least 1 is needed");
            }
        }

        int rest = n - 1 - (boundaries[count - 1] * _unit);
        if (rest < 1)
        {
            throw new InvalidDataException($"The last subframe of a channel has {rest} samples, and at least 1 is needed");
        }

        int position = 1;
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            int length = i < count - 1 ? (boundaries[i + 1] - boundaries[i]) * _unit : rest;
            ReadSubframe(ref bits, x, position, length, i == 0, previous);
            position += length;
            previous = length;
        }
    }

    // Section 5.
    private void ReadSubframe(ref LsbBitCursor bits, Span<int> x, int position, int length, bool isFirst, int previousLength)
    {
        if (!bits.ReadBit())
        {
            ResidualCodes.Read(ref bits, x.Slice(position, length), _partition);
            return;
        }

        int order = Reconstruction.Orders[(int)bits.Read(4)];
        bool isContinued = !isFirst && bits.ReadBit();
        int warm = 0;
        if (isContinued)
        {
            if (order > previousLength)
            {
                throw new InvalidDataException($"A subframe continues from {order} values, but the one before it has {previousLength}");
            }
        }
        else
        {
            if (order > length)
            {
                throw new InvalidDataException($"A subframe of {length} values has a filter of order {order}");
            }

            int warmOrder = (int)bits.Read(2);
            if (warmOrder == 3)
            {
                throw new InvalidDataException("A subframe's warm-up values have the difference order 3");
            }

            Span<int> warmUp = x.Slice(position, order);
            ResidualCodes.Read(ref bits, warmUp, _partition);
            Reconstruction.Integrate(warmUp, warmOrder);
            warm = order;
        }

        int predicted = length - warm;
        _context.AddWork(checked((((long)predicted * order) + ((long)order * order * CoefficientUnits)) * DecodeWork.PerTap));
        int shift = ReadFlag4(ref bits);
        int width = 6 + (int)bits.Read(1);
        int precision = 10;
        if (bits.ReadBit())
        {
            precision = 10 - ((int)bits.Read(3) + 1);
            if (precision < 3)
            {
                throw new InvalidDataException($"A filter has the precision {precision}, and 3 to 10 are allowed");
            }
        }

        ReadParameters(ref bits, order, width);
        Reconstruction.Coefficients(_parameters.AsSpan(0, order), precision, _terms, _coefficients);

        if (_residuals.Length < predicted)
        {
            _residuals = new int[Math.Max(predicted, 2 * _residuals.Length)];
        }

        Span<int> residuals = _residuals.AsSpan(0, predicted);
        ResidualCodes.Read(ref bits, residuals, _partition);

        int needed = order + predicted;
        if (_history.Length < needed)
        {
            _history = new short[Math.Max(needed, 2 * _history.Length)];
        }

        Span<short> history = _history.AsSpan(0, needed);
        int source = isContinued ? position - order : position;
        unchecked
        {
            for (int i = 0; i < order; i++)
            {
                history[i] = (short)(x[source + i] >> shift);
            }

            ReadOnlySpan<short> coefficients = _coefficients.AsSpan(0, order);
            int round = 1 << (precision - 1);
            int start = position + warm;
            _context.CancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < predicted; i++)
            {
                int a = round + Reconstruction.Dot(history.Slice(i, order), coefficients);
                int value = (Reconstruction.Clip14(a >> precision) << shift) - residuals[i];
                x[start + i] = value;
                history[order + i] = (short)(value >> shift);
            }
        }
    }

    // Section 5, step 6: the filter's parameters, in units of 1/512.
    private void ReadParameters(ref LsbBitCursor bits, int order, int width)
    {
        if (order == 0)
        {
            return;
        }

        int[] k = _parameters;
        k[0] = bits.ReadSigned(10);
        k[1] = bits.ReadSigned(10);
        int scale = 10 - width;
        k[2] = bits.ReadSigned(width) << scale;
        k[3] = bits.ReadSigned(width) << scale;
        if (order > 4)
        {
            int reduced = width - (bits.ReadBit() ? 1 : 0);
            for (int i = 4; i < order; i += 4)
            {
                int q = reduced - (int)bits.Read(2);
                for (int j = 0; j < 4; j++)
                {
                    k[i + j] = bits.ReadSigned(q) << scale;
                }
            }
        }
    }

    // Section 7: the integration of the channel's difference order and its wasted bits, with the range check of section 8.
    private void Finish(int n)
    {
        for (int c = 0; c < _channels; c++)
        {
            Span<int> x = _values.AsSpan(c * n, n);
            Reconstruction.Integrate(x, _order[c]);
            int wasted = _wasted[c];
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

    private static int ReadFlag4(ref LsbBitCursor bits) => bits.ReadBit() ? (int)bits.Read(4) + 1 : 0;

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
