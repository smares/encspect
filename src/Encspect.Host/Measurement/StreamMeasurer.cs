using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// Measures one stream's decoded PCM as the host's sink passes it on (docs/design/spectral-analysis.md). Host code: it never throws to
// the decoder (a bug becomes Failure and the stream's figures are dropped), never throws on cancellation (it stops and remembers),
// and keeps one frame of overlap and bounded arrays whatever the stream's length. The result depends only on the samples and their
// positions, never on how the decoder cuts blocks.
internal sealed class StreamMeasurer
{
    // The work per sample of a measured channel, in the decoders' unit (about 0.6 ns), by FFT size 1,024 to 32,768 points.
    private static readonly int[] SpectrumWeights = [29, 24, 23, 27, 27, 30];

    private const int CheckEvery = 1 << 16;

    // The work per sample of a measured channel for the levels, in the decoders' unit.
    private const int LevelsWeight = 4;

    private const double SmallestSample = 1e-30;

    private const double LargestSample = 1024.0;

    private readonly PcmFormat _format;
    private readonly CancellationToken _token;
    private readonly Func<long, bool> _charge;
    private readonly int _channels;
    private readonly int _measured;
    private readonly int[] _combined;
    private readonly bool _isMeasuringSpectrum;
    private readonly LevelsMeter? _levels;
    private double[] _scratch = [];
    private readonly FftPlan? _plan;
    private readonly CutoffGeometry _geometry;
    private readonly int _hop;
    private readonly double _scale;
    private readonly int _weight;
    private readonly Action<string>? _probe;
    private long _origin;

    private readonly double[][] _buffers = [];
    private readonly double[] _re = [];
    private readonly double[] _im = [];
    private readonly double[] _rows = [];
    private readonly double[] _suffix = new double[MeasurementLimits.Rows + 1];
    private readonly int[] _highs = new int[MeasurementLimits.Rows];
    private readonly double[] _aboveRows = new double[MeasurementLimits.Rows];
    private readonly double _inverseBand;
    private readonly double _ratioTimesBand;
    private readonly double[] _average = [];
    private CutoffCounters[] _counters = [];
    private float[] _cells = [];
    private readonly int[] _counts = new int[MeasurementLimits.MaxColumns];
    private readonly double[] _minimum = [];
    private readonly double[] _maximum = [];

    private bool _hasStarted;
    private long _firstPosition;
    private long _next;
    private long _bufferStart;
    private int _fill;
    private int _frames;
    private int _shift;
    private int _used;
    private long _samples;
    private long _lost;
    private int _gaps;
    private bool _isStopped;
    private long _nonFinite;

    public StreamMeasurer(PcmFormat format, MeasureOptions options, CancellationToken token, Func<long, bool> charge)
    {
        _format = format;
        _token = token;
        _charge = charge;
        _probe = options.Probe;
        _channels = format.Channels;
        _measured = Math.Min(format.Channels, MeasurementLimits.MaxChannels);
        MeasurementKinds kinds = options.Include ?? MeasurementSets.All;
        _isMeasuringSpectrum = (kinds & MeasurementKinds.Spectrum) != 0 && format.SampleRate > 0;
        if ((kinds & MeasurementKinds.Levels) != 0)
        {
            _levels = new LevelsMeter(_measured, format.Format, format.BitsPerSample);
        }


        // The speakers: the combined figures leave out the LFE; a stream without a layout uses every channel.
        var combined = new List<int>();
        for (int c = 0; c < _measured; c++)
        {
            if (format.ChannelLayout is not { } layout || c >= layout.Count || layout[c] is not (Speaker.LowFrequency or Speaker.LowFrequency2))
            {
                combined.Add(c);
            }
        }

        _combined = combined.Count > 0 ? [.. combined] : [.. Enumerable.Range(0, _measured)];
        _scale = format.Format == SampleFormat.Integer ? DetMath.PowerOfTwo(-(Math.Clamp(format.BitsPerSample, 1, 32) - 1)) : 1.0;
        if (!_isMeasuringSpectrum)
        {
            return;
        }

        _plan = new FftPlan(FftPlan.SizeFor(format.SampleRate));
        _geometry = new CutoffGeometry(format.SampleRate, _plan.Size);
        _hop = _plan.Size / 2;
        _weight = SpectrumWeights[BitOperationsLog2(_plan.Size) - 10];
        _buffers = new double[_measured][];
        for (int c = 0; c < _measured; c++)
        {
            _buffers[c] = new double[_plan.Size];
        }

        for (int r = 0; r < MeasurementLimits.Rows; r++)
        {
            _highs[r] = Math.Min(r + _geometry.BandRows, CutoffGeometry.TopRow);
            _aboveRows[r] = _highs[r] - r;
        }

        _inverseBand = 1.0 / _geometry.BandRows;
        _ratioTimesBand = CutoffCounters.OwnStepRatio * _geometry.BandRows;
        _re = _plan.NewScratch();
        _im = _plan.NewScratch();
        _rows = new double[(_measured + 1) * MeasurementLimits.Rows];
        _average = new double[(_measured + 1) * MeasurementLimits.Rows];
        _minimum = new double[_measured * MeasurementLimits.MaxColumns];
        _maximum = new double[_measured * MeasurementLimits.MaxColumns];
        Array.Fill(_minimum, double.MaxValue);
        Array.Fill(_maximum, double.MinValue);
    }

    // The state of a stream of this format, in bytes, for the memory bound (decided before anything is allocated).
    public static long EstimateBytes(PcmFormat format, MeasureOptions options)
    {
        int measured = Math.Min(format.Channels, MeasurementLimits.MaxChannels);
        long bytes = 64 * 1024;
        MeasurementKinds kinds = options.Include ?? MeasurementSets.All;
        bool isMeasuringSpectrum = (kinds & MeasurementKinds.Spectrum) != 0 && format.SampleRate > 0;
        if ((kinds & MeasurementKinds.Levels) != 0)
        {
            bytes += LevelsMeter.EstimateBytes(measured, format.Format, isMeasuringSpectrum);

            // The figures the record keeps: a record of the levels for each channel and one for the stream.
            bytes += 256L * (measured + 1);
        }

        if (!isMeasuringSpectrum)
        {
            return bytes;
        }

        int size = FftPlan.SizeFor(format.SampleRate);
        int rows = MeasurementLimits.Rows;
        bytes += (long)measured * size * 8;
        bytes += 8L * size * 6;
        bytes += 2L * (measured + 1) * rows * 8 * 2;
        if (format.SampleRate >= CutoffGeometry.MinimumRate)
        {
            bytes += (measured + 1) * CutoffCounters.Bytes;
        }

        bytes += 4L * MeasurementLimits.MaxColumns * rows;
        bytes += 2L * MeasurementLimits.MaxColumns * rows;
        bytes += 2L * 8 * MeasurementLimits.MaxColumns * measured;

        // The figures the record keeps: the grids as bytes and 16-bit codes, and the rows.
        bytes += (long)MeasurementLimits.MaxColumns * rows;
        bytes += 4L * MeasurementLimits.MaxColumns * measured;
        bytes += 2L * (measured + 1) * rows;
        return bytes;
    }

    public Exception? Failure { get; private set; }

    public bool IsLimited { get; private set; }

    public bool IsCanceled { get; private set; }

    public bool HasFormatChanged { get; private set; }

    public bool HasPositionJumped { get; private set; }

    // The position of the block that went back, or the sample count at which the format changed.
    public long StopPosition { get; private set; }

    public bool HasClamped { get; private set; }

    public long NonFiniteSamples => _nonFinite;

    public long Samples => _samples;

    public int SampleRate => _format.SampleRate;

    public int Channels => _channels;

    public int ChannelsMeasured => _measured;

    public bool IsStarted => _hasStarted;

    // The sink's side: never throws.
    public void Write(PcmBlock block)
    {
        if (_isStopped)
        {
            return;
        }

        try
        {
            Process(block);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failure = ex;
            _isStopped = true;
        }
    }

    private void Process(PcmBlock block)
    {
        _probe?.Invoke("block");
        if (_token.IsCancellationRequested)
        {
            IsCanceled = true;
            _isStopped = true;
            return;
        }

        if (block.Channels != _channels || block.Format != _format.Format)
        {
            HasFormatChanged = true;
            StopPosition = _samples;
            _isStopped = true;
            return;
        }

        if (!_hasStarted)
        {
            _hasStarted = true;
            _firstPosition = block.Position;

            // The grids count hops from the first one the stream touches, so a stream that starts late has no empty columns before it.
            _origin = _hop > 0 ? block.Position / _hop * _hop : block.Position;
            _next = block.Position;
            _bufferStart = block.Position;
        }

        if (block.Position < _next)
        {
            HasPositionJumped = true;
            StopPosition = block.Position;
            _isStopped = true;
            return;
        }

        if (block.Length == 0)
        {
            return;
        }

        if (!_charge((long)block.Length * _measured * Math.Max(1, (_isMeasuringSpectrum ? _weight : 0) + (_levels is null ? 0 : LevelsWeight))))
        {
            IsLimited = true;
            _isStopped = true;
            return;
        }

        if (block.Position > _next)
        {
            _lost += block.Position - _next;
            _gaps++;
            _next = block.Position;
            _fill = 0;
            _bufferStart = block.Position;
            _levels?.Gap();
        }

        if (!_isMeasuringSpectrum)
        {
            for (int done = 0; done < block.Length;)
            {
                if (done > 0 && _token.IsCancellationRequested)
                {
                    IsCanceled = true;
                    _isStopped = true;
                    return;
                }

                int count = Math.Min(LevelsMeter.MaxPiece, block.Length - done);
                AddLevels(block, done, count);
                _samples += count;
                _next += count;
                done += count;
            }

            return;
        }

        for (int done = 0; done < block.Length;)
        {
            if (done > 0 && _token.IsCancellationRequested)
            {
                IsCanceled = true;
                _isStopped = true;
                return;
            }

            int count = Math.Min(CheckEvery, block.Length - done);
            Append(block, done, count);
            done += count;
        }
    }

    // Takes count samples of the block from index start, in pieces that end at a hop boundary or when the frame is full.
    private void Append(PcmBlock block, int start, int count)
    {
        FftPlan plan = _plan!;
        while (count > 0)
        {
            long position = block.Position + start;
            int toHop = (int)(_hop - ((position - _origin) % _hop));
            int piece = Math.Min(Math.Min(count, plan.Size - _fill), toHop);
            long hopIndex = (position - _origin) / _hop;
            EnsureColumns(hopIndex);
            int column = (int)(hopIndex >> _shift);
            _used = Math.Max(_used, column + 1);
            for (int c = 0; c < _measured; c++)
            {
                Convert(block, c, start, piece, _buffers[c].AsSpan(_fill, piece), out double low, out double high);
                int cell = (c * MeasurementLimits.MaxColumns) + column;
                if (low < _minimum[cell])
                {
                    _minimum[cell] = low;
                }

                if (high > _maximum[cell])
                {
                    _maximum[cell] = high;
                }

                if (_levels is not null)
                {
                    if (block.Format == SampleFormat.Integer)
                    {
                        _levels.AddIntegers(c, block.Integers.Slice((c * block.Stride) + start, piece));
                    }
                    else
                    {
                        _levels.AddDoubles(c, _buffers[c].AsSpan(_fill, piece));
                    }
                }

                if (block.Format != SampleFormat.Integer)
                {
                    foreach (ref double v in _buffers[c].AsSpan(_fill, piece))
                    {
                        if (Math.Abs(v) < SmallestSample)
                        {
                            v = 0;
                        }
                    }
                }
            }

            _levels?.EndPiece(piece);

            _fill += piece;
            _samples += piece;
            _next += piece;
            start += piece;
            count -= piece;
            if (_fill == plan.Size)
            {
                if (!ProcessFrame())
                {
                    return;
                }

                for (int c = 0; c < _measured; c++)
                {
                    _buffers[c].AsSpan(_hop, _hop).CopyTo(_buffers[c]);
                }

                _fill = _hop;
                _bufferStart += _hop;
            }
        }
    }

    // The levels of a piece when the spectrum is not measured: a float block goes through the same safe conversion into a scratch array.
    private void AddLevels(PcmBlock block, int start, int count)
    {
        if (_levels is null)
        {
            return;
        }

        for (int c = 0; c < _measured; c++)
        {
            if (block.Format == SampleFormat.Integer)
            {
                _levels.AddIntegers(c, block.Integers.Slice((c * block.Stride) + start, count));
            }
            else
            {
                if (_scratch.Length < count)
                {
                    _scratch = new double[LevelsMeter.MaxPiece];
                }

                Span<double> target = _scratch.AsSpan(0, count);
                Convert(block, c, start, count, target, out _, out _);
                _levels.AddDoubles(c, target);
            }
        }

        _levels.EndPiece(count);
    }

    // Scales a channel's samples to full scale +-1 in doubles, with the minimum and maximum. Non-finite floats are 0 and counted, floats
    // beyond 1,024 times full scale are clamped, and floats under 1e-30 are 0, before anything reaches the arithmetic.
    private void Convert(PcmBlock block, int channel, int start, int count, Span<double> target, out double low, out double high)
    {
        low = double.MaxValue;
        high = double.MinValue;
        int at = (channel * block.Stride) + start;
        switch (block.Format)
        {
            case SampleFormat.Integer:
                ReadOnlySpan<int> integers = block.Integers.Slice(at, count);
                double scale = _scale;
                int smallest = int.MaxValue;
                int largest = int.MinValue;
                for (int i = 0; i < integers.Length; i++)
                {
                    int v = integers[i];
                    target[i] = v * scale;
                    smallest = v < smallest ? v : smallest;
                    largest = v > largest ? v : largest;
                }

                low = smallest * scale;
                high = largest * scale;
                break;
            case SampleFormat.Float32:
                ReadOnlySpan<float> floats = block.Floats.Slice(at, count);
                for (int i = 0; i < floats.Length; i++)
                {
                    double v = Sanitize(floats[i]);
                    target[i] = v;
                    if (v < low)
                    {
                        low = v;
                    }

                    if (v > high)
                    {
                        high = v;
                    }
                }

                break;
            default:
                ReadOnlySpan<double> doubles = block.Doubles.Slice(at, count);
                for (int i = 0; i < doubles.Length; i++)
                {
                    double v = Sanitize(doubles[i]);
                    target[i] = v;
                    if (v < low)
                    {
                        low = v;
                    }

                    if (v > high)
                    {
                        high = v;
                    }
                }

                break;
        }
    }

    private double Sanitize(double v)
    {
        double magnitude = Math.Abs(v);
        if (magnitude >= SmallestSample && magnitude <= LargestSample)
        {
            return v;
        }

        if (v == 0)
        {
            return v;
        }

        if (!double.IsFinite(v))
        {
            _nonFinite++;
            return 0;
        }

        // Too small for the spectrum (under 1e-30, -600 dB) is kept here, since the levels count it as a sample that is not 0; the spectrum's
        // buffers are flushed after the levels have read them. Too large is clamped and says so.
        if (magnitude < SmallestSample)
        {
            return v;
        }

        HasClamped = true;
        return v < 0 ? -LargestSample : LargestSample;
    }

    // Merges adjacent columns until the hop's column fits: whenever the columns would pass 1,024, pairs are merged and each column then
    // covers twice the hops. A gap of any size takes at most about 54 merges of an at most half-empty grid.
    private void EnsureColumns(long hopIndex)
    {
        while ((hopIndex >> _shift) >= MeasurementLimits.MaxColumns)
        {
            Merge();
        }
    }

    private void Merge()
    {
        int rows = MeasurementLimits.Rows;
        int half = MeasurementLimits.MaxColumns / 2;
        int usedPairs = (_used + 1) / 2;
        for (int i = 0; _cells.Length > 0 && i < usedPairs; i++)
        {
            int a = 2 * i;
            int b = a + 1;
            for (int r = 0; r < rows; r++)
            {
                _cells[(i * rows) + r] = _cells[(a * rows) + r] + _cells[(b * rows) + r];
            }

            _counts[i] = _counts[a] + _counts[b];
        }

        if (_cells.Length > 0)
        {
            Array.Clear(_cells, usedPairs * rows, (MeasurementLimits.MaxColumns - usedPairs) * rows);
        }

        Array.Clear(_counts, usedPairs, MeasurementLimits.MaxColumns - usedPairs);
        for (int c = 0; c < _measured; c++)
        {
            int baseIndex = c * MeasurementLimits.MaxColumns;
            for (int i = 0; i < half; i++)
            {
                _minimum[baseIndex + i] = Math.Min(_minimum[baseIndex + (2 * i)], _minimum[baseIndex + (2 * i) + 1]);
                _maximum[baseIndex + i] = Math.Max(_maximum[baseIndex + (2 * i)], _maximum[baseIndex + (2 * i) + 1]);
            }

            Array.Fill(_minimum, double.MaxValue, baseIndex + half, half);
            Array.Fill(_maximum, double.MinValue, baseIndex + half, half);
        }

        _shift++;
        _used = usedPairs;
    }

    // The big arrays (the grid's cells and the cutoff's counters, about 2.3 MiB and 0.3 MiB a channel) are allocated with the first
    // frame, and charged as the work of a few milliseconds, so that a stream that never fills a frame costs nothing.
    private const long AllocationUnits = 1 << 21;

    private bool Allocate()
    {
        if (!_charge(AllocationUnits))
        {
            return false;
        }

        if (_geometry.IsSearched)
        {
            _counters = new CutoffCounters[_measured + 1];
            for (int i = 0; i < _counters.Length; i++)
            {
                _counters[i] = new CutoffCounters();
            }
        }

        _cells = new float[MeasurementLimits.MaxColumns * MeasurementLimits.Rows];
        return true;
    }

    private bool ProcessFrame()
    {
        if (_cells.Length == 0 && !Allocate())
        {
            IsLimited = true;
            _isStopped = true;
            return false;
        }

        FftPlan plan = _plan!;
        int rows = MeasurementLimits.Rows;
        for (int c = 0; c < _measured; c++)
        {
            Span<double> channelRows = _rows.AsSpan(c * rows, rows);
            plan.Rows(_buffers[c], _re, _im, channelRows);
            Span<double> sums = _average.AsSpan(c * rows, rows);
            for (int r = 0; r < rows; r++)
            {
                sums[r] += channelRows[r];
            }

            if (_geometry.IsSearched)
            {
                Count(channelRows, _counters[c]);
            }
        }

        // The combined spectrum: the mean power of the channels other than the LFE, in channel order.
        Span<double> combined = _rows.AsSpan(_measured * rows, rows);
        combined.Clear();
        foreach (int c in _combined)
        {
            ReadOnlySpan<double> channelRows = _rows.AsSpan(c * rows, rows);
            for (int r = 0; r < rows; r++)
            {
                combined[r] += channelRows[r];
            }
        }

        double divisor = _combined.Length;
        for (int r = 0; r < rows; r++)
        {
            combined[r] /= divisor;
        }

        Span<double> combinedSums = _average.AsSpan(_measured * rows, rows);
        for (int r = 0; r < rows; r++)
        {
            combinedSums[r] += combined[r];
        }

        if (_geometry.IsSearched)
        {
            Count(combined, _counters[_measured]);
        }

        long hopIndex = (_bufferStart - _origin) / _hop;
        EnsureColumns(hopIndex);
        int column = (int)(hopIndex >> _shift);
        _used = Math.Max(_used, column + 1);
        Span<float> cells = _cells.AsSpan(column * rows, rows);
        for (int r = 0; r < rows; r++)
        {
            double p = combined[r];
            if (p >= SmallestSample)
            {
                cells[r] += (float)p;
            }
        }

        _counts[column]++;
        _frames++;
        return true;
    }

    // The counters of one frame's rows: suffix sums from the top row of the search (506) down, so that a band near the edge is not added to the bass.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Count(ReadOnlySpan<double> rows, CutoffCounters counters)
    {
        double[] suffix = _suffix;
        ref double sfx = ref MemoryMarshal.GetArrayDataReference(suffix);
        ref double source = ref MemoryMarshal.GetReference(rows);
        double sum = 0;
        Unsafe.Add(ref sfx, CutoffGeometry.TopRow + 1) = 0;
        for (int r = CutoffGeometry.TopRow; r >= 0; r--)
        {
            sum += Unsafe.Add(ref source, r);
            Unsafe.Add(ref sfx, r) = sum;
        }

        int w = _geometry.BandRows;
        int first = _geometry.FirstCandidate;
        int last = CutoffGeometry.LastCandidate;
        double inverseBand = _inverseBand;
        double ratio = _ratioTimesBand;
        ref int all = ref MemoryMarshal.GetArrayDataReference(counters.All);
        ref int show = ref MemoryMarshal.GetArrayDataReference(counters.Show);
        ref int highs = ref MemoryMarshal.GetArrayDataReference(_highs);
        ref double aboveRows = ref MemoryMarshal.GetArrayDataReference(_aboveRows);
        for (int r = first; r <= last; r++)
        {
            double under = Unsafe.Add(ref sfx, r - w + 1) - Unsafe.Add(ref sfx, r + 1);
            double over = Unsafe.Add(ref sfx, r + 1) - Unsafe.Add(ref sfx, Unsafe.Add(ref highs, r) + 1);
            int exponent = (int)((BitConverter.DoubleToInt64Bits(under * inverseBand) >> 52) & 0x7FF) - 1023;
            exponent = exponent < CutoffCounters.FirstExponent ? 0 : exponent > CutoffCounters.FirstExponent + CutoffCounters.Levels - 1 ? CutoffCounters.Levels - 1 : exponent - CutoffCounters.FirstExponent;
            int cell = (r * CutoffCounters.Levels) + exponent;
            Unsafe.Add(ref all, cell)++;
            double lhs = under * Unsafe.Add(ref aboveRows, r);
            if (lhs > 0 && lhs >= ratio * over)
            {
                Unsafe.Add(ref show, cell)++;
            }
        }
    }

    // The bytes a record keeps in the report: the grids and the rows. It stays reserved in the memory bound until the analysis ends.
    public static long RecordBytes(StreamMeasurements record)
    {
        long bytes = 512;
        if (record.Spectrum is { } spectrum)
        {
            bytes += 2L * spectrum.Combined.Length;
            foreach (ReadOnlyMemory<short> channel in spectrum.PerChannel)
            {
                bytes += 2L * channel.Length;
            }

            bytes += 64L * (1 + spectrum.ChannelCutoffs.Count);
        }

        bytes += record.Levels is { } levels ? 256L * (levels.Channels.Count + 1) : 0;
        bytes += record.Spectrogram?.Power.Length ?? 0;
        bytes += 2L * ((record.Waveform?.Minimum.Length ?? 0) + (record.Waveform?.Maximum.Length ?? 0));
        return bytes;
    }

    // The record of what was measured. Called once, when the stream's decoding has ended.
    public StreamMeasurements Result(MeasurementStatus status, string? reason)
    {
        _probe?.Invoke("result");
        var record = new StreamMeasurements
        {
            Status = status,
            Reason = reason,
            Samples = _samples,
            FirstPosition = _firstPosition,
            LostSamples = _lost,
            Gaps = _gaps,
            SampleRate = _format.SampleRate,
            ChannelLayout = _format.ChannelLayout is { } layout ? [.. layout.Take(_measured)] : null,
            Channels = _channels,
            ChannelsMeasured = _measured,
            HasClampedSamples = HasClamped,
            NonFiniteSamples = _nonFinite,
        };
        if (status == MeasurementStatus.Failed || !_hasStarted)
        {
            return record;
        }

        record = record with { Levels = _levels?.Build() };
        if (!_isMeasuringSpectrum)
        {
            return record;
        }

        if (_frames == 0 && _fill > 0)
        {
            // A stream shorter than one frame is measured as one frame, padded with zeros.
            for (int c = 0; c < _measured; c++)
            {
                _buffers[c].AsSpan(_fill).Clear();
            }

            ProcessFrame();
        }

        int rows = MeasurementLimits.Rows;
        short[][] perChannel = new short[_measured][];
        var cutoffs = new List<SpectralCutoff>();
        for (int c = 0; c < _measured; c++)
        {
            perChannel[c] = Levels(_average.AsSpan(c * rows, rows));
            cutoffs.Add(CutoffFinder.Find(perChannel[c], _counters.Length > 0 ? _counters[c] : null, _geometry));
        }

        short[] combined = Levels(_average.AsSpan(_measured * rows, rows));
        SpectralCutoff cutoff = CutoffFinder.Find(combined, _counters.Length > 0 ? _counters[_measured] : null, _geometry);
        return record with
        {
            Spectrum = new SpectrumFigures
            {
                FftSize = _plan!.Size,
                Frames = _frames,
                Combined = combined,
                PerChannel = perChannel.Select(p => (ReadOnlyMemory<short>)p).ToList(),
                Cutoff = cutoff,
                ChannelCutoffs = cutoffs,
            },
            Spectrogram = Grid(),
            Waveform = Wave(),
        };
    }

    private short[] Levels(ReadOnlySpan<double> sums)
    {
        var result = new short[sums.Length];
        for (int r = 0; r < sums.Length; r++)
        {
            result[r] = _frames == 0 || sums[r] <= 0 ? MeasurementLimits.NoPower : (short)Math.Clamp(DetMath.Tenths(sums[r] / _frames), short.MinValue + 1, short.MaxValue);
        }

        return result;
    }

    private SpectrogramGrid Grid()
    {
        int rows = MeasurementLimits.Rows;
        byte[] power = new byte[_used * rows];
        for (int column = 0; column < _used; column++)
        {
            int count = _counts[column];
            if (count == 0)
            {
                continue;
            }

            for (int r = 0; r < rows; r++)
            {
                double mean = (double)_cells[(column * rows) + r] / count;
                int value = mean > 0 ? (int)Math.Floor(DetMath.Decibels(mean) + 0.5) + MeasurementLimits.GridOffset : 1;
                power[(column * rows) + r] = (byte)Math.Clamp(value, 1, 220);
            }
        }

        return new SpectrogramGrid { Columns = _used, HopsPerColumn = 1 << _shift, HopSamples = _hop, Power = power };
    }

    private WaveformGrid Wave()
    {
        short[] minimum = new short[_measured * _used];
        short[] maximum = new short[_measured * _used];
        for (int c = 0; c < _measured; c++)
        {
            for (int i = 0; i < _used; i++)
            {
                double low = _minimum[(c * MeasurementLimits.MaxColumns) + i];
                double high = _maximum[(c * MeasurementLimits.MaxColumns) + i];
                if (low > high)
                {
                    continue;
                }

                minimum[(c * _used) + i] = (short)Math.Clamp(Math.Floor(low * 32768.0), -32768.0, 32767.0);
                maximum[(c * _used) + i] = (short)Math.Clamp(Math.Ceiling(high * 32768.0), -32768.0, 32767.0);
            }
        }

        return new WaveformGrid { Columns = _used, Channels = _measured, Minimum = minimum, Maximum = maximum };
    }

    private static int BitOperationsLog2(int value) => System.Numerics.BitOperations.Log2((uint)value);
}
