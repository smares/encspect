using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Wma.Decoding;

// The decoding state of one stream of Windows Media Audio version 1 or 2: per channel the overlap buffer of two frames, the exponents the channel received last (blocks that send
// none reuse them), and the noise generator's position. The section names are those of docs/design/wma-standard-decoding.md.
internal sealed class StdFrameDecoder : IFrameDecoder
{
    // The values of a gain that the dequantization can carry in double precision: 10^(1000 / 20) is 10^50, far beyond what a sample of 16 bits can hold.
    private const int MaxGain = 1000;

    private const double SpectrumLimit = 1e30;
    private const int NoiseLength = 8192;

    // Work units (docs/design/lossy-decoders.md, "Work units per format"): the dequantization 2 for each line, mid/side 1, noise substitution 3, the line spectral pair curve 12,
    // the inverse transform 2 per bit of the window's length for each output sample, the window and the overlap 2 for each.
    private const int DequantizeUnits = 2;
    private const int StereoUnits = 1;
    private const int NoiseUnits = 3;
    private const int CurveUnits = 12;
    private const int OverlapUnits = 2;

    private readonly DecodeContext _context;
    private readonly StdParameters _p;
    private readonly int _channels;
    private readonly int _frame;
    private readonly int _sizes;
    private readonly float[][] _overlap;
    private readonly float[][] _spectrum;
    private readonly float[][] _exponents;
    private readonly float[][] _held;
    private readonly int[] _heldSize;
    private readonly double[] _heldLargest;
    private readonly double[] _largest;
    private readonly bool[] _isTainted;
    private readonly float[] _y;
    private readonly float[] _planar;
    private readonly byte[] _marks;
    private readonly InverseMdct[] _transforms;
    private readonly float[][] _windows;
    private readonly double[][] _angles;
    private readonly float[] _noise = new float[NoiseLength];
    private readonly float[] _bandPowers = new float[160];
    private readonly Block[] _blocks = [.. Enumerable.Range(0, 16).Select(_ => new Block())];
    private readonly double[] _highPower = new double[16];
    private IPcmSink? _sink;
    private int _noiseAt;
    private bool _lostBefore;
    private bool _followsFailedUnit;
    private bool _hasLast;
    private int _lastSize;
    private int _lastNext;

    public StdFrameDecoder(DecodeContext context, StdParameters parameters)
    {
        _context = context;
        _p = parameters;
        _channels = parameters.Channels;
        _frame = parameters.FrameLength;
        _sizes = parameters.SizeCount;
        _overlap = new float[_channels][];
        _spectrum = new float[_channels][];
        _exponents = new float[_channels][];
        _held = new float[_channels][];
        for (int c = 0; c < _channels; c++)
        {
            _overlap[c] = new float[2 * _frame];
            _spectrum[c] = new float[_frame];
            _exponents[c] = new float[_frame];
            _held[c] = new float[_frame];
        }

        _heldSize = new int[_channels];
        Array.Fill(_heldSize, -1);
        _heldLargest = new double[_channels];
        _largest = new double[_channels];
        _isTainted = new bool[_channels];
        _y = new float[2 * _frame];
        _planar = new float[_channels * _frame];
        _marks = new byte[2 * _frame];
        _transforms = new InverseMdct[_sizes];
        _windows = new float[_sizes][];
        _angles = new double[_sizes][];
        for (int k = 0; k < _sizes; k++)
        {
            int length = _frame >> k;
            _transforms[k] = new InverseMdct(2 * length, -1f / 32768f);
            float[] window = new float[length];
            for (int i = 0; i < length; i++)
            {
                DetMath.SinCos((2 * i) + 1, 8 * length, out double sin, out _);
                window[i] = (float)sin;
            }

            _windows[k] = window;
            double[] angle = new double[length];
            for (int x = 0; x < length; x++)
            {
                DetMath.SinCos(x, 2 * length, out _, out double cos);
                angle[x] = 2 * cos;
            }

            _angles[k] = angle;
        }

        for (int v = -60; v <= 95; v++)
        {
            _bandPowers[v + 60] = (float)DetMath.Pow(10, v / 16.0);
        }

        // The noise generator: 8,192 values from a linear congruential sequence, uniform with a root mean square of the amplitude.
        double amplitude = parameters.UsesScaleFactorExponents ? 0.02 : 0.04;
        double scale = Math.Sqrt(3) * amplitude / 2147483648.0;
        uint state = 1;
        for (int i = 0; i < NoiseLength; i++)
        {
            state = unchecked((state * 314159u) + 1);
            _noise[i] = (float)((int)state * scale);
        }
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    // What one block's values lie at in the record.
    private struct Chan
    {
        public bool IsCoded;
        public int Mask;
        public int LevelsAt;
        public int ExponentsAt;
        public int CoefficientsAt;
    }

    private sealed class Block
    {
        public int Size;
        public int Previous;
        public int Next;
        public int Flags;
        public int Gain;
        public bool IsExponentsSent;
        public readonly Chan[] Channels = new Chan[2];

        public int CodedCount => (Channels[0].IsCoded ? 1 : 0) + (Channels[1].IsCoded ? 1 : 0);
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The frame at 0x{frame.Bytes.FileStart:X} holds more values than the host lends buffers for, so it is not decoded.", frame.Node);
            Lose();
            return 0;
        }

        ReadOnlySpan<int> record = lent.Narrow;
        if (record.Length == 1 && record[0] == -1)
        {
            return Flush(frame);
        }

        bool follows = frame.FollowsGap || _lostBefore;
        _lostBefore = false;
        int count;
        try
        {
            count = Parse(record);
        }
        catch (InvalidDataException)
        {
            Lose();
            throw;
        }

        if (follows)
        {
            Reset();
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        Charge(count);

        int position = 0;
        for (int i = 0; i < count; i++)
        {
            ProcessBlock(_blocks[i], record, position);
            position += _frame >> _blocks[i].Size;
        }

        Block last = _blocks[count - 1];
        _hasLast = true;
        _lastSize = last.Size;
        _lastNext = last.Next;

        // The span of the frame's first output samples that the overlap of a frame before it shapes: the blocks before the first one's window and its overlap with the previous
        // block, whose window is the shorter one's (docs/design/wma-standard-decoding.md, "What crosses a lost frame").
        int firstLength = _frame >> _blocks[0].Size;
        int span = (_frame / 2) + (Math.Min(firstLength, _frame >> _blocks[0].Previous) / 2);
        if (follows)
        {
            Array.Clear(_marks, 0, _marks.Length);
            Array.Fill(_marks, (byte)1, 0, Math.Min(span, _frame));
        }
        else if (_followsFailedUnit)
        {
            for (int i = 0; i < Math.Min(span, _frame); i++)
            {
                _marks[i] = Math.Max(_marks[i], (byte)2);
            }
        }

        _followsFailedUnit = frame.FailsChecksum;

        long index = frame.FirstSample / _frame;
        int written = 0;
        for (int c = 0; c < _channels; c++)
        {
            Array.Copy(_overlap[c], 0, _planar, c * _frame, _frame);
            Array.Copy(_overlap[c], _frame, _overlap[c], 0, _frame);
            Array.Clear(_overlap[c], _frame, _frame);
        }

        if (index >= 2)
        {
            written = Emit(frame.FirstSample, _frame);
        }

        Array.Copy(_marks, _frame, _marks, 0, _frame);
        Array.Clear(_marks, _frame, _frame);
        return written;
    }

    // The part of the last block's overlap that no block changes any more, at the end of the stream; frame.Samples is what the parser counts of it.
    private int Flush(in CodedFrame frame)
    {
        if (!_hasLast)
        {
            return 0;
        }

        int own = (_frame - Math.Min(_frame >> _lastSize, _frame >> _lastNext)) / 2;
        int count = Math.Min(frame.Samples, own);
        _hasLast = false;
        if (count <= 0)
        {
            return 0;
        }

        for (int c = 0; c < _channels; c++)
        {
            Array.Copy(_overlap[c], 0, _planar, c * _frame, _frame);
        }

        return Emit(frame.FirstSample, count);
    }

    // Writes the first count samples of the planar frame at a position, leaving out the runs that depend on lost state.
    private int Emit(long first, int count)
    {
        _sink ??= _context.DeclareOutput(new PcmFormat(_p.SampleRate, _channels, 32, SampleFormat.Float32) { ChannelLayout = _channels == 1 ? ChannelLayouts.Mono : ChannelLayouts.Stereo });
        int written = 0;
        int i = 0;
        while (i < count)
        {
            byte mark = _marks[i];
            int j = i + 1;
            while (j < count && _marks[j] == mark)
            {
                j++;
            }

            int length = j - i;
            if (mark == 1)
            {
                _context.Withhold(first + i, length, WithholdReason.AfterGap);
            }
            else
            {
                if (mark == 2)
                {
                    _context.WithholdFromMeasurements(first + i, length);
                }

                _sink.Write(new PcmBlock(first + i, length, _channels, _frame, _planar.AsSpan(i)));
                written += length;
            }

            i = j;
        }

        return written;
    }

    // A frame that is not decoded leaves the overlap and the exponents of a lost one behind.
    private void Lose()
    {
        _lostBefore = true;
        _followsFailedUnit = false;
        _hasLast = false;
    }

    private void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            Array.Clear(_overlap[c]);
            _isTainted[c] = true;
        }

        Array.Clear(_marks);
        _followsFailedUnit = false;
    }

    // Finds where each block's values lie in the record and checks that they are all there.
    private int Parse(ReadOnlySpan<int> r)
    {
        if (r.Length < 1 || r[0] is < 1 or > 16)
        {
            throw new InvalidDataException("the walk stored no blocks");
        }

        int count = r[0];
        int at = 1;
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            if (at + 5 > r.Length)
            {
                throw new InvalidDataException("the walk's values end inside a block");
            }

            Block b = _blocks[i];
            Array.Clear(b.Channels);
            b.Size = r[at];
            b.Previous = r[at + 1];
            b.Next = r[at + 2];
            b.Flags = r[at + 3];
            b.Gain = r[at + 4];
            at += 5;
            if ((uint)b.Size >= (uint)_sizes || (uint)b.Previous >= (uint)_sizes || (uint)b.Next >= (uint)_sizes)
            {
                throw new InvalidDataException("a block's size is not one of the stream's");
            }

            int length = _frame >> b.Size;
            filled += length;
            b.Channels[0].IsCoded = (b.Flags & StdRecordWriter.CodedFlag) != 0;
            b.Channels[1].IsCoded = _channels == 2 && (b.Flags & (StdRecordWriter.CodedFlag << 1)) != 0;
            b.IsExponentsSent = (b.Flags & StdRecordWriter.ExponentsSentFlag) != 0;
            if (b.CodedCount == 0)
            {
                continue;
            }

            if (_p.UsesNoiseSubstitution)
            {
                for (int c = 0; c < _channels; c++)
                {
                    if (b.Channels[c].IsCoded)
                    {
                        b.Channels[c].Mask = At(r, at++);
                    }
                }

                for (int c = 0; c < _channels; c++)
                {
                    if (b.Channels[c].IsCoded)
                    {
                        b.Channels[c].LevelsAt = at;
                        at += System.Numerics.BitOperations.PopCount((uint)b.Channels[c].Mask);
                    }
                }
            }

            if (b.IsExponentsSent)
            {
                for (int c = 0; c < _channels; c++)
                {
                    if (b.Channels[c].IsCoded)
                    {
                        b.Channels[c].ExponentsAt = at;
                        at += _p.UsesScaleFactorExponents ? 1 + length : 10;
                    }
                }
            }

            for (int c = 0; c < _channels; c++)
            {
                if (b.Channels[c].IsCoded)
                {
                    b.Channels[c].CoefficientsAt = at;
                    at += length;
                }
            }

            if (at > r.Length)
            {
                throw new InvalidDataException("the walk's values end inside a block");
            }
        }

        if (filled != _frame)
        {
            throw new InvalidDataException("the walk's blocks do not add up to the frame");
        }

        return count;

        static int At(ReadOnlySpan<int> r, int index) => (uint)index < (uint)r.Length ? r[index] : throw new InvalidDataException("the walk's values end inside a block");
    }

    // The work of the frame before it is done: the floor of every output sample, and the stages of each block.
    private void Charge(int count)
    {
        long units = DecodeWork.ForSample(4) * _frame * _channels;
        for (int i = 0; i < count; i++)
        {
            Block b = _blocks[i];
            int length = _frame >> b.Size;
            int bits = System.Numerics.BitOperations.Log2((uint)(2 * length));
            units += (long)OverlapUnits * length * _channels;
            if (b.CodedCount == 0)
            {
                continue;
            }

            units += (long)(DequantizeUnits + (_p.UsesNoiseSubstitution ? NoiseUnits : 0)) * length * b.CodedCount;
            units += (long)2 * bits * length * _channels;
            if ((b.Flags & StdRecordWriter.MidSideFlag) != 0)
            {
                units += (long)StereoUnits * length * _channels;
            }

            if (!_p.UsesScaleFactorExponents && b.IsExponentsSent)
            {
                units += (long)CurveUnits * length * b.CodedCount;
            }
        }

        _context.AddWork(units);
    }

    private void ProcessBlock(Block b, ReadOnlySpan<int> r, int position)
    {
        int length = _frame >> b.Size;
        for (int c = 0; c < _channels; c++)
        {
            Array.Clear(_spectrum[c], 0, length);
        }

        bool isTainted = false;
        if (b.CodedCount > 0)
        {
            double gain = Math.Min(b.Gain, MaxGain);
            for (int c = 0; c < _channels; c++)
            {
                Chan ch = b.Channels[c];
                if (!ch.IsCoded)
                {
                    continue;
                }

                if (b.IsExponentsSent)
                {
                    Load(c, b.Size, length, r, ch);
                    _isTainted[c] = false;
                }
                else
                {
                    isTainted |= _isTainted[c];
                    Reuse(c, b.Size, length);
                }

                Dequantize(c, b.Size, length, gain, r, ch, !b.IsExponentsSent);
            }

            if ((b.Flags & StdRecordWriter.MidSideFlag) != 0 && _channels == 2)
            {
                float[] first = _spectrum[0];
                float[] second = _spectrum[1];
                MidSide(first, second, length);
            }
        }

        for (int c = 0; c < _channels; c++)
        {
            Synthesize(c, b.Size, b.Previous, b.Next, length, position);
        }

        if (isTainted)
        {
            int from = (_frame / 2) + position - (length / 2);
            Array.Fill(_marks, (byte)1, from, Math.Min(2 * length, _marks.Length - from));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MidSide(float[] first, float[] second, int length)
    {
        for (int x = 0; x < length; x++)
        {
            float a = first[x];
            float b = second[x];
            first[x] = a + b;
            second[x] = a - b;
        }
    }

    // The exponents a block sends (Exponents): the scale factor codes' values per line, or the line spectral pair curve; they replace what the channel held.
    private void Load(int c, int size, int length, ReadOnlySpan<int> r, Chan ch)
    {
        float[] x = _exponents[c];
        double largest;
        if (_p.UsesScaleFactorExponents)
        {
            int at = ch.ExponentsAt;
            largest = _bandPowers[Math.Clamp(r[at] + 60, 0, 155)];
            for (int i = 0; i < length; i++)
            {
                x[i] = _bandPowers[Math.Clamp(r[at + 1 + i] + 60, 0, 155)];
            }
        }
        else
        {
            largest = Curve(r.Slice(ch.ExponentsAt, 10), _angles[size], x, length);
        }

        _largest[c] = largest;
        Array.Copy(x, _held[c], length);
        _heldSize[c] = size;
        _heldLargest[c] = largest;
    }

    // The line spectral pair curve (Line spectral pairs): the exponent of each line, and the largest of them.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Curve(ReadOnlySpan<int> indexes, double[] angles, float[] x, int length)
    {
        double[] lambda = new double[10];
        for (int j = 0; j < 10; j++)
        {
            float[] row = Tables.LspCodebook[j];
            lambda[j] = row[Math.Clamp(indexes[j], 0, row.Length - 1)];
        }

        double largest = 0;
        for (int i = 0; i < length; i++)
        {
            double w = angles[i];
            double p = 0.5 * (w - lambda[1]) * (w - lambda[3]) * (w - lambda[5]) * (w - lambda[7]) * (w - lambda[9]);
            double q = 0.5 * (w - lambda[0]) * (w - lambda[2]) * (w - lambda[4]) * (w - lambda[6]) * (w - lambda[8]);
            double z = Math.Max((p * p * (2 - w)) + (q * q * (2 + w)), 1e-37);
            double value = 1.0 / Math.Sqrt(Math.Sqrt(z));
            x[i] = (float)value;
            largest = Math.Max(largest, x[i]);
        }

        return largest;
    }

    // The exponents a block reuses (Exponents of another block size): the held values at the index of the line of the other block at the same frequency.
    private void Reuse(int c, int size, int length)
    {
        float[] x = _exponents[c];
        float[] held = _held[c];
        int e = _heldSize[c];
        _largest[c] = _heldLargest[c];
        if (e < 0)
        {
            Array.Clear(x, 0, length);
            return;
        }

        int heldLength = _frame >> e;
        int last = heldLength - 1;
        if (e <= size)
        {
            int shift = size - e;
            for (int i = 0; i < length; i++)
            {
                x[i] = held[Math.Min(i << shift, last)];
            }

            return;
        }

        int down = e - size;
        if (!_p.UsesNoiseSubstitution)
        {
            // One segment from the first coded line (the offsets of the lines count from it).
            for (int i = 0; i < length; i++)
            {
                x[i] = held[Math.Min(i >> down, last)];
            }

            return;
        }

        int first = _p.FirstCoefficient;
        int end = _p.CoefficientEnds[size];
        int[] high = _p.HighBands[size];
        int highStart = end - high.Sum();
        int at = 0;
        for (int i = 0; i < Math.Min(first, length); i++)
        {
            x[i] = held[Math.Min(i >> down, last)];
        }

        int baseAt = first >> down;
        at = first;
        for (int i = first; i < highStart && i < length; i++)
        {
            x[i] = held[Math.Min(baseAt + ((i - first) >> down), last)];
        }

        baseAt += (Math.Max(highStart, first) - first) >> down;
        at = highStart;
        foreach (int width in high)
        {
            for (int i = 0; i < width && at + i < length; i++)
            {
                x[at + i] = held[Math.Min(baseAt + (i >> down), last)];
            }

            baseAt += width >> down;
            at += width;
        }

        float tail = held[Math.Clamp(baseAt - 1, 0, last)];
        for (int i = end; i < length; i++)
        {
            x[i] = tail;
        }
    }

    // Dequantization (Dequantization) of a coded channel into its spectrum, with the noise substitution of the stream where it has one.
    private void Dequantize(int c, int size, int length, double gain, ReadOnlySpan<int> r, Chan ch, bool isReused)
    {
        int first = _p.FirstCoefficient;
        int end = _p.CoefficientEnds[size];
        float[] x = _exponents[c];
        float[] spectrum = _spectrum[c];
        double normalization = _p.Version == 2 ? 2.0 / length : Math.Sqrt(2.0 / length);
        double largest = _largest[c];
        double m = DetMath.Pow(10, gain / 20) / largest * normalization;
        ReadOnlySpan<int> levels = r.Slice(ch.CoefficientsAt, length);
        if (!_p.UsesNoiseSubstitution)
        {
            for (int i = first; i < end && i < length; i++)
            {
                // Version 1 takes the exponent of the line three lower; version 2 has no offset.
                spectrum[i] = Limit(levels[i] * (double)x[i - first] * m);
            }

            return;
        }

        int[] high = _p.HighBands[size];
        int highStart = end - high.Sum();
        int at = _noiseAt;
        int mask = NoiseLength - 1;

        for (int i = 0; i < first && i < length; i++)
        {
            spectrum[i] = Limit(_noise[at] * (double)x[i] * m);
            at = (at + 1) & mask;
        }

        for (int i = first; i < highStart && i < length; i++)
        {
            spectrum[i] = Limit((levels[i] + (double)_noise[at]) * x[i] * m);
            at = (at + 1) & mask;
        }

        // The power of each flagged high band, and the noise levels of the flagged ones (Noise substitution).
        int flagged = System.Numerics.BitOperations.PopCount((uint)ch.Mask);
        double lastPower = 1;
        if (flagged > 0)
        {
            int lastBand = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)ch.Mask);
            BandPowers(c, size, high, highStart, isReused);
            lastPower = _highPower[lastBand];
        }

        double amplitude = _p.UsesScaleFactorExponents ? 0.02 : 0.04;
        int level = 0;
        int line = highStart;
        for (int band = 0; band < high.Length; band++)
        {
            bool isFlagged = (ch.Mask & (1 << band)) != 0;
            double mj = 0;
            if (isFlagged)
            {
                double db = Math.Clamp(r[ch.LevelsAt + level++], -1000, 1000);
                mj = Math.Sqrt(_highPower[band] / lastPower) * DetMath.Pow(10, db / 20) / (largest * amplitude) * normalization;
            }

            for (int i = 0; i < high[band] && line + i < length; i++)
            {
                int l = line + i;
                spectrum[l] = isFlagged ? Limit(_noise[at] * (double)x[l] * mj) : Limit((levels[l] + (double)_noise[at]) * x[l] * m);
                at = (at + 1) & mask;
            }

            line += high[band];
        }

        double edge = x[Math.Clamp(end - 1, 0, length - 1)];
        for (int i = end; i < length; i++)
        {
            spectrum[i] = Limit(_noise[at] * edge * m);
            at = (at + 1) & mask;
        }

        _noiseAt = at;
    }

    // The mean of the squared exponents over each high band: the exponents of the block itself, or where it reuses those of a shorter block the held ones at the index its own
    // chain of bases gives (Exponents of another block size).
    private void BandPowers(int c, int size, int[] high, int highStart, bool isReused)
    {
        float[] x = _exponents[c];
        int e = _heldSize[c];
        bool isChained = isReused && e > size;
        float[] held = _held[c];
        int down = isChained ? e - size : 0;
        int last = isChained ? (_frame >> e) - 1 : 0;
        int baseAt = isChained ? highStart >> down : 0;
        int line = highStart;
        for (int band = 0; band < high.Length; band++)
        {
            double sum = 0;
            for (int i = 0; i < high[band]; i++)
            {
                double v = isChained ? held[Math.Min(baseAt + (i >> down), last)] : x[line + i];
                sum += v * v;
            }

            _highPower[band] = high[band] > 0 ? sum / high[band] : 1;
            if (isChained)
            {
                baseAt += high[band] >> down;
            }

            line += high[band];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Limit(double value) => (float)Math.Clamp(value, -SpectrumLimit, SpectrumLimit);

    // The inverse transform, the window and the overlap of a channel's block (Windows and overlap).
    private void Synthesize(int c, int size, int previous, int next, int length, int position)
    {
        float[] spectrum = _spectrum[c];
        float[] y = _y;
        bool isSilent = true;
        for (int i = 0; i < length; i++)
        {
            if (spectrum[i] != 0)
            {
                isSilent = false;
                break;
            }
        }

        if (isSilent)
        {
            Array.Clear(y, 0, 2 * length);
        }
        else
        {
            _transforms[size].Transform(spectrum.AsSpan(0, length), y.AsSpan(0, 2 * length));
        }

        int a = (_frame / 2) + position - (length / 2);
        Overlap(_overlap[c], y, a, length, _frame >> previous, _frame >> next, _windows[size], _windows[previous], _windows[next]);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Overlap(float[] o, float[] y, int a, int length, int previousLength, int nextLength, float[] own, float[] previous, float[] next)
    {
        if (length <= previousLength)
        {
            for (int i = 0; i < length; i++)
            {
                o[a + i] += y[i] * own[i];
            }
        }
        else
        {
            int d = (length - previousLength) / 2;
            for (int i = d; i < d + previousLength; i++)
            {
                o[a + i] += y[i] * previous[i - d];
            }

            for (int i = d + previousLength; i < length; i++)
            {
                o[a + i] = y[i];
            }
        }

        if (length <= nextLength)
        {
            for (int i = 0; i < length; i++)
            {
                o[a + length + i] = y[length + i] * own[length - 1 - i];
            }
        }
        else
        {
            int d = (length - nextLength) / 2;
            for (int i = 0; i < d; i++)
            {
                o[a + length + i] = y[length + i];
            }

            for (int i = d; i < d + nextLength; i++)
            {
                o[a + length + i] = y[length + i] * next[nextLength - 1 - (i - d)];
            }

            for (int i = d + nextLength; i < length; i++)
            {
                o[a + length + i] = 0;
            }
        }
    }
}
