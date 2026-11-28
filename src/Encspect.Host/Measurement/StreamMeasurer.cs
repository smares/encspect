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

    // The work per sample of a channel of the pair for the stored bins, the mid and side rows and their counters (docs/design/spectral-analysis.md,
    // Step 7, "Cost, memory and determinism": about 6 units of 0.6 ns, 3.6 ns, a sample of each channel of the pair).
    internal const int PairWeight = 8;

    private const int CheckEvery = 1 << 16;

    // The work per sample of a measured channel for the levels, in the decoders' unit: 5 for integers, with the low bits unused per span (2 to 3 ns; the FLAC reference runs at 0.38 ns a unit on Windows ARM, where 32-bit stereo takes 3.06 ns), 14 for floats, which are converted
    // safely first and checked for the bit-depth grids (7.5 to 10.6 ns a sample measured for doubles of any magnitude, a ratio of 1.0 to 1.3 to the reference).
    private const int IntegerLevelsWeight = 5;

    private const int FloatLevelsWeight = 14;

    // The work per sample of a measured channel for loudness (the K-weighting and the squares) and for the true peak by oversampling (4, 2 and
    // none: the sample peak, whose weight is the conversion to doubles and the scan). The measurements are in docs/design/spectral-analysis.md,
    // step 5, "Measured" (true peak alone).
    private const int LoudnessWeight = 8;

    private static readonly int[] TruePeakWeights = [4, 30, 38];

    // The work per block and measured channel besides the per-sample work, in the decoders' unit: the fixed cost of a piece (the scans, the
    // branches, the call into each measurement) that a block of 1 to 16 samples would otherwise cost without being charged.
    internal const int BlockUnitsPerChannel = 700;

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
    private readonly LoudnessMeter? _loudness;
    private readonly TruePeakMeter? _truePeak;
    private readonly int[]? _loudnessWeights;
    private readonly string? _loudnessNotMeasured;
    private readonly string? _truePeakNotMeasured;
    private readonly bool _isMeasuringLoudness;
    private readonly bool _isMeasuringTruePeak;
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
    private readonly int _pairLeft = -1;
    private readonly int _pairRight = -1;
    private readonly double[][] _bins = [];
    private readonly long _pairUnits;
    private readonly double[] _suffix = new double[MeasurementLimits.Rows + 1];
    private readonly int[] _highs = new int[MeasurementLimits.Rows];
    private readonly double[] _aboveRows = new double[MeasurementLimits.Rows];
    private readonly double _inverseBand;
    private readonly double _ratioTimesBand;
    private readonly double[] _average = [];
    private readonly double[] _crossSums = [];
    private readonly double[] _crossRows = [];
    private readonly double[][] _coherenceSuffix = [];
    private CoherenceCounters? _coherence;
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
    private bool _hasReceived;
    private long _received;
    private long _firstReceived;
    private long _trimStart;
    private long _trimEnd = long.MaxValue;
    private long _trimmedLeading;
    private long _trimmedTrailing;
    private long _trimmedInner;
    private long _pendingInner;
    private readonly List<(long First, long End)> _innerTrims = [];
    private readonly List<(long First, long End)> _damaged = [];
    private readonly List<(int From, int To)> _segments = [];
    private readonly long[] _withheld = new long[WithholdReasons.Count];
    private readonly List<Span> _pending = [];
    private readonly List<Span> _leading = [];
    private long _overflow;
    private long _withheldBeforeStart;
    private long _leadingOverflow;
    private readonly long[] _overflowBy = new long[WithholdReasons.Count];
    private readonly long[] _leadingOverflowBy = new long[WithholdReasons.Count];
    private bool _hasCodedRange;
    private long _codedStart;
    private long _codedEnd;
    private bool _isStopped;
    private bool _isLoudnessCharged;
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
            _levels = new LevelsMeter(_measured, format.Format, format.BitsPerSample, format.SampleRate);
        }

        _isMeasuringLoudness = (kinds & MeasurementKinds.Loudness) != 0;
        if (_isMeasuringLoudness)
        {
            if (format.SampleRate < MeasurementLimits.MinimumLoudnessRate)
            {
                _loudnessNotMeasured = format.SampleRate <= 0 ? "the sample rate is not stated" : "loudness needs a sample rate of 8,000 Hz or more";
            }
            else if (LoudnessWeights.For(format.ChannelLayout, _measured, out string? reason) is { } weights)
            {
                _loudnessWeights = weights;
                _loudness = new LoudnessMeter(format.SampleRate, weights);
            }
            else
            {
                _loudnessNotMeasured = reason;
            }
        }

        _isMeasuringTruePeak = (kinds & MeasurementKinds.TruePeak) != 0;
        if (_isMeasuringTruePeak)
        {
            if (format.SampleRate <= 0)
            {
                _truePeakNotMeasured = "the sample rate is not stated";
            }
            else
            {
                _truePeak = new TruePeakMeter(_measured, format.SampleRate);
            }
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

        (_pairLeft, _pairRight) = FindPair(format.ChannelLayout, _measured);
        _plan = new FftPlan(FftPlan.SizeFor(format.SampleRate));
        _geometry = new CutoffGeometry(format.SampleRate, _plan.Size);
        _hop = _plan.Size / 2;
        _weight = SpectrumWeights[BitOperationsLog2(_plan.Size) - 10] + 1;
        int sets = _measured + 1 + (HasPair ? 2 : 0);
        if (HasPair)
        {
            _pairUnits = 2L * PairWeight;
            _bins = [new double[_plan.Half], new double[_plan.Half], new double[_plan.Half], new double[_plan.Half]];

            // The stream's sums of the cross rows (real, then imaginary), the frame's cross rows, and the four suffix arrays of the frame's test.
            _crossSums = new double[2 * MeasurementLimits.Rows];
            _crossRows = new double[2 * MeasurementLimits.Rows];
            _coherenceSuffix = [new double[MeasurementLimits.Rows + 1], new double[MeasurementLimits.Rows + 1], new double[MeasurementLimits.Rows + 1], new double[MeasurementLimits.Rows + 1]];
        }
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
        _rows = new double[sets * MeasurementLimits.Rows];
        _average = new double[sets * MeasurementLimits.Rows];
        _minimum = new double[_measured * MeasurementLimits.MaxColumns];
        _maximum = new double[_measured * MeasurementLimits.MaxColumns];
        Array.Fill(_minimum, double.MaxValue);
        Array.Fill(_maximum, double.MinValue);
    }

    private bool HasPair => _pairLeft >= 0;

    // The pair the mid and side spectra are made of: the first channels the layout names front left and front right, or channels 0 and 1 of a
    // stream of two channels without a layout; (-1, -1) where there is none. Channels past the cap are not measured and so not paired.
    internal static (int Left, int Right) FindPair(IReadOnlyList<Speaker>? layout, int measured)
    {
        if (layout is null)
        {
            return measured == 2 ? (0, 1) : (-1, -1);
        }

        int left = -1;
        int right = -1;
        for (int c = 0; c < Math.Min(layout.Count, measured); c++)
        {
            if (layout[c] == Speaker.FrontLeft && left < 0)
            {
                left = c;
            }
            else if (layout[c] == Speaker.FrontRight && right < 0)
            {
                right = c;
            }
        }

        return left >= 0 && right >= 0 ? (left, right) : (-1, -1);
    }

    // The counters of each measured channel, then of the combined spectrum, then of the mid and of the side spectrum where there is a pair (the tests pin their raw output).
    internal IReadOnlyList<CutoffCounters> CounterSets => _counters;

    // The counters of the pair's coherence and the stream's sums of the cross rows (the real parts, then the imaginary ones), which the tests pin; null or empty without a pair.
    internal CoherenceCounters? CoherenceCounterSet => _coherence;

    internal double[] CrossSums => _crossSums;

    // The state of a stream of this format, in bytes, for the memory bound (decided before anything is allocated).
    public static long EstimateBytes(PcmFormat format, MeasureOptions options)
    {
        int measured = Math.Min(format.Channels, MeasurementLimits.MaxChannels);
        long bytes = (64 * 1024) + (3L * MaxPendingSpans * 24);
        MeasurementKinds kinds = options.Include ?? MeasurementSets.All;
        bool isMeasuringSpectrum = (kinds & MeasurementKinds.Spectrum) != 0 && format.SampleRate > 0;
        if ((kinds & MeasurementKinds.Levels) != 0)
        {
            bytes += LevelsMeter.EstimateBytes(measured, format.Format, isMeasuringSpectrum);

            // The figures the record keeps: a record of the levels for each channel and one for the stream.
            bytes += 256L * (measured + 1);
        }

        bool needsDoubles = (kinds & (MeasurementKinds.Loudness | MeasurementKinds.TruePeak)) != 0 && format.SampleRate > 0;
        if ((kinds & MeasurementKinds.Loudness) != 0 && format.SampleRate >= MeasurementLimits.MinimumLoudnessRate)
        {
            bytes += LoudnessMeter.EstimateBytes(measured, format.SampleRate);
        }

        if ((kinds & MeasurementKinds.TruePeak) != 0 && format.SampleRate > 0)
        {
            bytes += TruePeakMeter.EstimateBytes(measured, format.SampleRate);
        }

        if (needsDoubles && !isMeasuringSpectrum && !((kinds & MeasurementKinds.Levels) != 0 && format.Format != SampleFormat.Integer))
        {
            bytes += 8L * LevelsMeter.MaxPiece;
        }

        // The figures the record keeps: a weight and a peak for each channel, the series of the short-term loudness.
        bytes += 16L * (measured + 1) + (needsDoubles ? 24L * MeasurementLimits.MaxSeriesPoints : 0);
        if (!isMeasuringSpectrum)
        {
            return bytes;
        }

        int size = FftPlan.SizeFor(format.SampleRate);
        int rows = MeasurementLimits.Rows;
        bytes += (long)measured * size * 8;
        bytes += 8L * size * 6;
        bytes += 2L * (measured + 1) * rows * 8 * 2;
        (int pairLeft, _) = FindPair(format.ChannelLayout, measured);
        bool hasPair = pairLeft >= 0;
        if (hasPair)
        {
            // The stored bins of the two channels of the pair (16 N bytes), and the mid and side rows and averages.
            bytes += 16L * size;
            bytes += 2L * 2 * rows * 8 * 2;

            // The stream's sums of the cross rows and the frame's (2 x 2 x 512 doubles), the four suffix arrays, and the figure's rows and shares.
            bytes += (2L * 2 * rows * 8) + (4L * (rows + 1) * 8) + (4L * (rows + 1) * 8) + (2L * 2 * rows);
        }

        if (format.SampleRate >= CutoffGeometry.MinimumRate)
        {
            bytes += ((measured + 1) * CutoffCounters.Bytes) + CutoffCounters.CeilingBytes + CutoffCounters.HolesBytes;
            if (hasPair)
            {
                bytes += 2 * (CutoffCounters.Bytes + CutoffCounters.HolesBytes);
                bytes += CoherenceCounters.Bytes;
            }
        }

        bytes += 4L * MeasurementLimits.MaxColumns * rows;
        bytes += 2L * MeasurementLimits.MaxColumns * rows;
        bytes += 2L * 8 * MeasurementLimits.MaxColumns * measured;

        // The figures the record keeps: the grids as bytes and 16-bit codes, and the rows.
        bytes += (long)MeasurementLimits.MaxColumns * rows;
        bytes += 4L * MeasurementLimits.MaxColumns * measured;
        bytes += 2L * (measured + 1) * rows;
        if (hasPair)
        {
            // The mid and side rows and three more figures, and the shares of the holes of the three spectra.
            bytes += 2L * 2 * rows;
            bytes += 2L * 3 * rows;
        }

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

    // The most spans of withheld output kept between two blocks; contiguous spans of one reason merge, so only a decoder that
    // alternates reasons or leaves gaps between its spans reaches it. Past it a span is counted at once, as if it lay in a jump. A span may arrive before the block it lies in; one past the cap that lies in a later jump than the next
    // block is taken back at that block and counts as lost there (it takes more than 1,024 spans between two blocks).
    public const int MaxPendingSpans = 1024;

    private readonly record struct Span(long First, long End, WithholdReason Reason);

    // The sink's side: never throws. The positions [firstSample, firstSample + samples) are output the decoder holds back; the part of them
    // that lies in a jump between blocks, before the first block or after the last within the coded range is counted as withheld, with the
    // reason, and the rest of the jump as lost.
    public void Withhold(long firstSample, long samples, WithholdReason reason)
    {
        if (_isStopped || samples <= 0 || firstSample < 0 || !Enum.IsDefined(reason))
        {
            return;
        }

        try
        {
            long end = firstSample > long.MaxValue - samples ? long.MaxValue : firstSample + samples;
            List<Span> spans = _pending;
            long floor = spans.Count > 0 ? spans[^1].End : long.MinValue;
            long first = Math.Max(firstSample, floor);
            if (first >= end)
            {
                return;
            }

            if (spans.Count > 0 && spans[^1] is { } last && last.End == first && (last.Reason == reason || spans.Count >= MaxPendingSpans))
            {
                spans[^1] = last with { End = end };
            }
            else if (spans.Count < MaxPendingSpans)
            {
                spans.Add(new Span(first, end, reason));
            }
            else
            {
                WithholdPastTheCap(end - first, reason);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failure = ex;
            _isStopped = true;
        }
    }

    // Output the session withheld before the declaration of the output and could not list (past MaxPendingSpans): counted at once, as the
    // measurer counts the spans it cannot keep.
    public void WithholdPastTheCap(long samples, WithholdReason reason)
    {
        if (_isStopped || samples <= 0 || !Enum.IsDefined(reason))
        {
            return;
        }

        _withheld[(int)reason] += samples;
        _overflow += samples;
        _overflowBy[(int)reason] += samples;
    }

    // The sink's side: the range the stream's syntax trims to (IDecodeSession.Trim). The part of a block before the start, once the
    // measuring has not begun, and from the end on is not measured; the leading trim only holds before the first measured sample.
    public void Trim(long firstPosition, long endPosition)
    {
        // Blocks past the current end are already counted as trimmed and the next measured block would follow the measured ones as if nothing lay
        // between: once the end has been passed it can only stay or shrink. The start only matters before the measuring begins.
        long end = _hasReceived && _received > _trimEnd ? Math.Min(_trimEnd, endPosition) : endPosition;
        (_trimStart, _trimEnd) = (_hasStarted ? _trimStart : firstPosition, end);
    }

    // The sink's side: positions whose output is written but left out of every figure (a failed unit's, docs/design/damaged-units.md). Kept until
    // the blocks they lie in arrive; the part of a block inside them is cut out of it and counted as withheld ("checksum failed"), and measuring
    // resumes after it as after a jump. Spans past MaxPendingSpans are not kept, so their output is measured.
    public void WithholdWritten(long firstPosition, long samples)
    {
        if (_isStopped || samples <= 0 || firstPosition < 0)
        {
            return;
        }

        long end = firstPosition > long.MaxValue - samples ? long.MaxValue : firstPosition + samples;
        long floor = _damaged.Count > 0 ? _damaged[^1].End : long.MinValue;
        long first = Math.Max(firstPosition, floor);
        if (first >= end)
        {
            return;
        }

        if (_damaged.Count > 0 && _damaged[^1].End == first)
        {
            _damaged[^1] = (_damaged[^1].First, end);
        }
        else if (_damaged.Count < MaxPendingSpans)
        {
            _damaged.Add((first, end));
        }
    }

    // The sink's side: a range inside the stream that its syntax trims (IDecodeSession.TrimInner). Kept until the blocks it lies in arrive; the part
    // of a block inside it is not measured and is counted as trimmed, inner where measured samples follow it and trailing where none does.
    public void TrimInner(long firstPosition, long samples)
    {
        if (_isStopped || samples <= 0 || firstPosition < 0)
        {
            return;
        }

        long end = firstPosition > long.MaxValue - samples ? long.MaxValue : firstPosition + samples;
        long floor = _innerTrims.Count > 0 ? _innerTrims[^1].End : long.MinValue;
        long first = Math.Max(firstPosition, floor);
        if (first >= end || _innerTrims.Count >= MaxPendingSpans)
        {
            return;
        }

        _innerTrims.Add((first, end));
    }

    // Takes the pending spans' overlap with [from, to) into the totals and returns it; the spans stay.
    private long Overlap(List<Span> spans, long from, long to, long[] byReason)
    {
        long total = 0;
        foreach (Span span in spans)
        {
            long a = Math.Max(span.First, from);
            long b = Math.Min(span.End, to);
            if (b > a)
            {
                byReason[(int)span.Reason] += b - a;
                total += b - a;
            }
        }

        return total;
    }

    private static void Prune(List<Span> spans, long position)
    {
        int keep = 0;
        while (keep < spans.Count && spans[keep].End <= position)
        {
            keep++;
        }

        if (keep > 0)
        {
            spans.RemoveRange(0, keep);
        }
    }

    // The sink's side: never throws.
    public void Write(PcmBlock block)
    {
        if (_isStopped)
        {
            return;
        }

        try
        {
            if (_damaged.Count == 0 || block.Length == 0)
            {
                Process(block);
            }
            else
            {
                WriteAroundDamaged(block);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failure = ex;
            _isStopped = true;
        }
    }

    // The parts of the block outside the withheld written spans are processed as blocks of their own; each span part joins the pending spans
    // as withheld output before the part after it, so it is a jump that is withheld whole.
    private void WriteAroundDamaged(PcmBlock block)
    {
        long start = block.Position;
        long end = start + block.Length;
        int drop = 0;
        while (drop < _damaged.Count && _damaged[drop].End <= start)
        {
            drop++;
        }

        if (drop > 0)
        {
            _damaged.RemoveRange(0, drop);
        }

        int at = 0;
        foreach ((long first, long spanEnd) in _damaged)
        {
            if (first >= end)
            {
                break;
            }

            int a = (int)Math.Max(at, Math.Clamp(first - start, 0, block.Length));
            int b = (int)Math.Clamp(spanEnd - start, a, block.Length);
            if (a > at)
            {
                Process(Slice(block, at, a - at));
            }

            if (b > a)
            {
                WithholdDamagedPart(block, start, a, b);
            }

            at = b;
            if (_isStopped)
            {
                return;
            }
        }

        if (at < block.Length)
        {
            Process(at == 0 ? block : Slice(block, at, block.Length - at));
        }
    }

    // The part [a, b) of the block lies in a failed unit. What the syntax trims there (before the start while measuring has not begun, from the end on,
    // the inner ranges) is not a sample of the stream, so it goes through Process as trimmed; the rest is withheld.
    private void WithholdDamagedPart(PcmBlock block, long start, int a, int b)
    {
        long cursor = start + a;
        long end = start + b;
        while (cursor < end && !_isStopped)
        {
            long lowest = _hasStarted ? long.MinValue : _trimStart;
            long next = end;
            bool isTrimmed = false;
            if (cursor < lowest)
            {
                (next, isTrimmed) = (Math.Min(end, lowest), true);
            }
            else if (cursor >= _trimEnd)
            {
                isTrimmed = true;
            }
            else
            {
                next = Math.Min(next, _trimEnd);
                foreach ((long first, long spanEnd) in _innerTrims)
                {
                    if (first <= cursor && cursor < spanEnd)
                    {
                        (next, isTrimmed) = (Math.Min(end, spanEnd), true);
                        break;
                    }

                    if (first > cursor)
                    {
                        next = Math.Min(next, first);
                        break;
                    }
                }
            }

            if (isTrimmed)
            {
                Process(Slice(block, (int)(cursor - start), (int)(next - cursor)));
            }
            else
            {
                Withhold(cursor, next - cursor, WithholdReason.ChecksumFailed);
            }

            cursor = next;
        }
    }

    private static PcmBlock Slice(PcmBlock block, int from, int length) => block.Format switch
    {
        SampleFormat.Integer => new PcmBlock(block.Position + from, length, block.Channels, block.Stride, block.Integers[from..]),
        SampleFormat.Float32 => new PcmBlock(block.Position + from, length, block.Channels, block.Stride, block.Floats[from..]),
        _ => new PcmBlock(block.Position + from, length, block.Channels, block.Stride, block.Doubles[from..]),
    };

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

        if (!_hasReceived)
        {
            _hasReceived = true;
            _received = _firstReceived = block.Position;

            // Spans withheld before the first block lie in the stream's leading positions.
            _leading.AddRange(_pending);
            _pending.Clear();
            (_leadingOverflow, _overflow) = (_overflow, 0);
            Array.Copy(_overflowBy, _leadingOverflowBy, _overflowBy.Length);
            Array.Clear(_overflowBy);
        }

        if (block.Position < _received)
        {
            HasPositionJumped = true;
            StopPosition = block.Position;
            _isStopped = true;
            return;
        }

        // The part of the block the stream's syntax trims: its start while the measuring has not begun, and its end past the trimmed range.
        long blockEnd = block.Position + block.Length;
        int lead = _hasStarted ? 0 : (int)Math.Clamp(_trimStart - block.Position, 0, block.Length);
        int trail = (int)Math.Clamp(blockEnd - Math.Max(_trimEnd, block.Position + lead), 0, block.Length - lead);
        int keep = block.Length - lead - trail;

        bool isJump = block.Position > _received;
        if (isJump)
        {
            RecordJump(_received, block.Position);
        }
        else if (_overflow > 0)
        {
            // Spans past the cap that lie in no jump were counted as withheld at once; they are output the block carries, so take them back.
            for (int i = 0; i < _withheld.Length; i++)
            {
                _withheld[i] = Math.Max(0, _withheld[i] - _overflowBy[i]);
            }

            _overflow = 0;
            Array.Clear(_overflowBy);
        }

        // The ranges the syntax trims inside the block's kept part cut it into segments; what lies before the first measured sample is leading.
        int cut = CutInner(block.Position, lead, lead + keep);
        keep -= cut;
        int regionEnd = lead + keep + cut;
        int first = _segments.Count > 0 ? _segments[0].From : regionEnd;
        int head = first - lead;
        if (keep == 0 && block.Length > 0)
        {
            _trimmedLeading += lead + (_hasStarted ? 0 : cut);
            _pendingInner += _hasStarted ? cut : 0;
            _trimmedTrailing += trail;
            _received = blockEnd;
            return;
        }

        if (!_hasStarted)
        {
            lead = first;
            long startAt = block.Position + lead;
            _hasStarted = true;
            _firstPosition = startAt;

            // The grids count hops from the first one the stream touches, so a stream that starts late has no empty columns before it.
            _origin = _hop > 0 ? startAt / _hop * _hop : startAt;
            _next = startAt;
            _bufferStart = startAt;
            _loudness?.Start(startAt);
            _levels?.Start(startAt);
        }
        else
        {
            long pendingBefore = _pendingInner;
            _trimmedInner += _pendingInner + head;
            _pendingInner = 0;

            // Measuring continues at the block's first measured sample where the stream jumped, a range cut its start or ranges before it took samples
            // that nothing measured follows (a block inside a range, a range that ended a block).
            if (isJump || head > 0 || pendingBefore > 0)
            {
                ResumeAt(block.Position + first);
            }
        }

        _trimmedLeading += lead;
        _trimmedTrailing += trail;
        _received = blockEnd;
        if (block.Length == 0)
        {
            return;
        }

        if (!_charge(((long)_measured * BlockUnitsPerChannel) + ((long)keep * _pairUnits) + ((long)keep * _measured * Math.Max(1, (_isMeasuringSpectrum ? _weight : 0) + (_levels is null ? 0 : _format.Format == SampleFormat.Integer ? IntegerLevelsWeight : FloatLevelsWeight) + (_loudness is null ? 0 : LoudnessWeight) + (_truePeak is null ? 0 : TruePeakWeights[BitOperationsLog2(_truePeak.Oversampling)])))))
        {
            IsLimited = true;
            _isStopped = true;
            return;
        }

        if (_loudness is { HasAllocated: false } && !_isLoudnessCharged)
        {
            // The histograms are made when the first 400 ms are in; the work of making them is charged with the stream's first samples.
            _isLoudnessCharged = true;
            if (!_charge(LoudnessMeter.AllocationUnits))
            {
                IsLimited = true;
                _isStopped = true;
                return;
            }
        }

        for (int k = 0; k < _segments.Count; k++)
        {
            (int from, int to) = _segments[k];
            if (k > 0)
            {
                _trimmedInner += from - _segments[k - 1].To;
                ResumeAt(block.Position + from);
            }

            if (!MeasureSegment(block, from, to))
            {
                return;
            }
        }

        // What the block's last range trims after the last segment waits for the next measured sample.
        _pendingInner += regionEnd - _segments[^1].To;
    }

    // Measures samples [from, to) of the block; false once the measuring has stopped.
    private bool MeasureSegment(PcmBlock block, int from, int to)
    {
        if (!_isMeasuringSpectrum)
        {
            for (int done = from; done < to;)
            {
                if (done > from && _token.IsCancellationRequested)
                {
                    IsCanceled = true;
                    _isStopped = true;
                    return false;
                }

                int count = Math.Min(LevelsMeter.MaxPiece, to - done);
                AddPiece(block, done, count);
                _samples += count;
                _next += count;
                done += count;
            }

            return true;
        }

        for (int done = from; done < to;)
        {
            if (done > from && _token.IsCancellationRequested)
            {
                IsCanceled = true;
                _isStopped = true;
                return false;
            }

            int count = Math.Min(CheckEvery, to - done);
            Append(block, done, count);
            done += count;
        }

        return true;
    }

    // Measuring continues at position after positions that hold no measured sample: as after a jump, with the frame in progress dropped.
    private void ResumeAt(long position)
    {
        _next = position;
        _fill = 0;
        _bufferStart = position;
        _levels?.Gap(position);
        _loudness?.Start(position);
        _truePeak?.Gap();
    }

    // Fills _segments with the parts of the block's samples [from, to) outside the ranges the syntax trims inside the stream and returns how many
    // samples the ranges cut. Ranges that end before the block are dropped.
    private int CutInner(long blockPosition, int from, int to)
    {
        _segments.Clear();
        int drop = 0;
        while (drop < _innerTrims.Count && _innerTrims[drop].End <= blockPosition + from)
        {
            drop++;
        }

        if (drop > 0)
        {
            _innerTrims.RemoveRange(0, drop);
        }

        int cut = 0;
        int at = from;
        foreach ((long first, long end) in _innerTrims)
        {
            if (first >= blockPosition + to)
            {
                break;
            }

            int a = (int)Math.Max(at, Math.Clamp(first - blockPosition, from, to));
            int b = (int)Math.Clamp(end - blockPosition, a, to);
            if (a > at)
            {
                _segments.Add((at, a));
            }

            cut += b - a;
            at = b;
        }

        if (at < to)
        {
            _segments.Add((at, to));
        }

        return cut;
    }

    // A jump from the received position to the next block's: the pending spans of withheld output are withheld, the rest is lost.
    private void RecordJump(long from, long to)
    {
        long withheld = Overlap(_pending, from, to, _withheld);

        // A jump before the first measured sample (a first block the syntax trims whole): its withheld positions lie before the figures' span, as leading ones.
        if (!_hasStarted)
        {
            _withheldBeforeStart += withheld;
        }

        _lost += Math.Max(0, to - from - withheld - _overflow);
        _overflow = 0;
        Array.Clear(_overflowBy);
        _gaps++;
        Prune(_pending, to);
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

                _loudness?.AddChannel(c, _buffers[c].AsSpan(_fill, piece));
                _truePeak?.AddChannel(c, _buffers[c].AsSpan(_fill, piece));
            }

            _levels?.EndPiece(piece);
            _loudness?.EndPiece(piece);

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

    // A piece when the spectrum is not measured: the levels read the integers as they are and a float block through the same safe conversion into a
    // scratch array; loudness and the true peak read the converted samples, with those under 1e-30 flushed to 0 as the spectrum's buffers are.
    private void AddPiece(PcmBlock block, int start, int count)
    {
        bool isInteger = block.Format == SampleFormat.Integer;
        bool needsDoubles = _loudness is not null || _truePeak is not null;
        for (int c = 0; c < _measured; c++)
        {
            if (isInteger && _levels is not null)
            {
                _levels.AddIntegers(c, block.Integers.Slice((c * block.Stride) + start, count));
            }

            if (!isInteger || needsDoubles)
            {
                if (_scratch.Length < count)
                {
                    _scratch = new double[LevelsMeter.MaxPiece];
                }

                Span<double> target = _scratch.AsSpan(0, count);
                Convert(block, c, start, count, target, out _, out _);
                if (!isInteger)
                {
                    _levels?.AddDoubles(c, target);
                    foreach (ref double v in target)
                    {
                        if (Math.Abs(v) < SmallestSample)
                        {
                            v = 0;
                        }
                    }
                }

                _loudness?.AddChannel(c, target);
                _truePeak?.AddChannel(c, target);
            }
        }

        _levels?.EndPiece(count);
        _loudness?.EndPiece(count);
    }

    // Scales a channel's samples to full scale +-1 in doubles, with the minimum and maximum. Non-finite floats are 0 and counted, floats
    // beyond 1,024 times full scale are clamped, before anything reaches the arithmetic. Floats under 1e-30 are kept in the target, where the levels read
    // them as samples that are not 0; the minimum and maximum (the waveform) read them as 0, and the spectrum's buffers are flushed after the levels.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

                    // The waveform reads a value the spectrum flushes as 0.
                    double flushed = Math.Abs(v) < SmallestSample ? 0 : v;
                    if (flushed < low)
                    {
                        low = flushed;
                    }

                    if (flushed > high)
                    {
                        high = flushed;
                    }
                }

                break;
            default:
                ReadOnlySpan<double> doubles = block.Doubles.Slice(at, count);
                for (int i = 0; i < doubles.Length; i++)
                {
                    double v = Sanitize(doubles[i]);
                    target[i] = v;

                    // The waveform reads a value the spectrum flushes as 0.
                    double flushed = Math.Abs(v) < SmallestSample ? 0 : v;
                    if (flushed < low)
                    {
                        low = flushed;
                    }

                    if (flushed > high)
                    {
                        high = flushed;
                    }
                }

                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
            _counters = new CutoffCounters[_measured + 1 + (HasPair ? 2 : 0)];
            for (int i = 0; i < _counters.Length; i++)
            {
                _counters[i] = new CutoffCounters(hasCeiling: i == _measured, hasHoles: i >= _measured);
            }
        }

        if (_geometry.IsSearched && HasPair)
        {
            _coherence = new CoherenceCounters();
        }

        _cells = new float[MeasurementLimits.MaxColumns * MeasurementLimits.Rows];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
            if (c == _pairLeft || c == _pairRight)
            {
                // The bins of the pair are kept for the mid and side rows; the rows are summed from them in the order Rows sums them.
                int slot = c == _pairLeft ? 0 : 2;
                plan.Bins(_buffers[c], _re, _im, _bins[slot], _bins[slot + 1]);
                plan.RowsFromBins(_bins[slot], _bins[slot + 1], channelRows);
            }
            else
            {
                plan.Rows(_buffers[c], _re, _im, channelRows);
            }

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

        if (HasPair)
        {
            Span<double> mid = _rows.AsSpan((_measured + 1) * rows, rows);
            Span<double> side = _rows.AsSpan((_measured + 2) * rows, rows);
            plan.MidSideRows(_bins[0], _bins[1], _bins[2], _bins[3], mid, side);
            for (int set = _measured + 1; set <= _measured + 2; set++)
            {
                ReadOnlySpan<double> setRows = _rows.AsSpan(set * rows, rows);
                Span<double> setSums = _average.AsSpan(set * rows, rows);
                for (int r = 0; r < rows; r++)
                {
                    setSums[r] += setRows[r];
                }

                if (_geometry.IsSearched)
                {
                    Count(setRows, _counters[set]);
                }
            }

            plan.CrossRows(_bins[0], _bins[1], _bins[2], _bins[3], _crossRows.AsSpan(0, rows), _crossRows.AsSpan(rows, rows));
            for (int i = 0; i < 2 * rows; i++)
            {
                _crossSums[i] += _crossRows[i];
            }

            if (_coherence is not null)
            {
                CountCoherence(_rows.AsSpan(_pairLeft * rows, rows), _rows.AsSpan(_pairRight * rows, rows), _coherence);
            }
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
        int[]? ceilingCounts = counters.Ceiling;
        ref int ceiling = ref ceilingCounts is null ? ref Unsafe.NullRef<int>() : ref MemoryMarshal.GetArrayDataReference(ceilingCounts);
        double ceilingRatio = CutoffCounters.CeilingRatio * w;
        int[]? holeCounts = counters.Holes;
        ref int holes = ref holeCounts is null ? ref Unsafe.NullRef<int>() : ref MemoryMarshal.GetArrayDataReference(holeCounts);
        int firstHole = _geometry.FirstHoleRow;
        double holeRatio = CutoffCounters.HoleRatio;
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

            // The ceiling: the band under the row against the mean of every row above it, up to the top of the search (rows r + 1 to 506).
            if (ceilingCounts is not null && under > 0 && under * (CutoffGeometry.TopRow - r) >= ceilingRatio * Unsafe.Add(ref sfx, r + 1))
            {
                Unsafe.Add(ref ceiling, cell)++;
            }

            // A hole: the row's power at most a thousandth of the mean power of the W rows under it (r - W to r - 1) and of the rows over it (r + 1 up,
            // clipped at the top row), both with power.
            if (holeCounts is not null && r >= firstHole)
            {
                double own = (Unsafe.Add(ref sfx, r) - Unsafe.Add(ref sfx, r + 1)) * holeRatio;
                double below = Unsafe.Add(ref sfx, r - w) - Unsafe.Add(ref sfx, r);
                if (below > 0 && over > 0 && own * w <= below && own * Unsafe.Add(ref aboveRows, r) <= over)
                {
                    Unsafe.Add(ref holes, cell)++;
                }
            }
        }
    }

    // The counters of the pair's coherence for one frame: the band of W rows under each row from the first full band, as Count forms its bands, the pair's mean power
    // selecting the level and the test of CoherenceFinder deciding the second counter.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CountCoherence(ReadOnlySpan<double> left, ReadOnlySpan<double> right, CoherenceCounters counters)
    {
        int rows = MeasurementLimits.Rows;
        double[] sl = _coherenceSuffix[0];
        double[] sr = _coherenceSuffix[1];
        double[] scr = _coherenceSuffix[2];
        double[] sci = _coherenceSuffix[3];
        int top = CutoffGeometry.TopRow;
        sl[top + 1] = 0;
        sr[top + 1] = 0;
        scr[top + 1] = 0;
        sci[top + 1] = 0;
        for (int r = top; r >= 0; r--)
        {
            sl[r] = sl[r + 1] + left[r];
            sr[r] = sr[r + 1] + right[r];
            scr[r] = scr[r + 1] + _crossRows[r];
            sci[r] = sci[r + 1] + _crossRows[rows + r];
        }

        int w = _geometry.BandRows;
        double inverseBand = _inverseBand;
        int[] all = counters.All;
        int[] coherent = counters.Coherent;
        for (int r = w - 1; r <= CutoffGeometry.LastCandidate; r++)
        {
            double underLeft = sl[r - w + 1] - sl[r + 1];
            double underRight = sr[r - w + 1] - sr[r + 1];
            int exponent = (int)((BitConverter.DoubleToInt64Bits((underLeft + underRight) * 0.5 * inverseBand) >> 52) & 0x7FF) - 1023;
            exponent = exponent < CutoffCounters.FirstExponent ? 0 : exponent > CutoffCounters.FirstExponent + CutoffCounters.Levels - 1 ? CutoffCounters.Levels - 1 : exponent - CutoffCounters.FirstExponent;
            int cell = (r * CutoffCounters.Levels) + exponent;
            all[cell]++;
            if (CoherenceFinder.IsCoherent(scr[r - w + 1] - scr[r + 1], sci[r - w + 1] - sci[r + 1], underLeft, underRight))
            {
                coherent[cell]++;
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

            bytes += (64L * (1 + spectrum.ChannelCutoffs.Count)) + (32L * (2 + spectrum.ChannelBandwidths.Count)) + (32L * (1 + spectrum.ChannelSlopes.Count));
            bytes += 2L * spectrum.Holes.RowShares.Length;
            if (spectrum.MidSide is { } midSide)
            {
                bytes += 2L * (midSide.Mid.Length + midSide.Side.Length + midSide.MidHoles.RowShares.Length + midSide.SideHoles.RowShares.Length) + 256;
            }

            if (spectrum.Coherence is { } coherence)
            {
                bytes += (2L * (coherence.Rows.Length + coherence.Shares.Length)) + 128;
            }
        }

        bytes += record.Levels is { } levels ? (256L * (levels.Channels.Count + 1)) + (levels.Spans is { } spans ? 256L + (8L * spans.Distribution.Count) : 0) : 0;
        bytes += record.Loudness is { } loudness ? 256L + (24L * (loudness.ShortTerm?.Values.Length ?? 0)) + (4L * loudness.ChannelWeights.Count) : 0;
        bytes += record.TruePeak is { } truePeak ? 128L + (4L * truePeak.ChannelTenths.Count) : 0;
        bytes += record.Spectrogram?.Power.Length ?? 0;
        bytes += 2L * ((record.Waveform?.Minimum.Length ?? 0) + (record.Waveform?.Maximum.Length ?? 0));
        return bytes;
    }

    // The positions the units of the stream cover at the coded stage, from the first one's start to the last one's end, as the host saw
    // them handed to the decoding. A lost first or last unit lies outside the blocks, so without the span it would be in no figure.
    public void SetCodedRange(long start, long end)
    {
        (_hasCodedRange, _codedStart, _codedEnd) = (end > start, start, end);
    }

    // The record of what was measured. Called once, when the stream's decoding has ended.
    public StreamMeasurements Result(MeasurementStatus status, string? reason)
    {
        _probe?.Invoke("result");

        // The units before the first block and after the last are lost only where the decoding reached the stream's end: a stream that was
        // stopped has unmeasured samples, which are not lost ones.
        long leading = 0;
        long trailing = 0;
        long leadingWithheld = 0;
        long trailingWithheld = 0;
        long leadingTrimmedGap = 0;
        long trailingTrimmedGap = 0;
        long[] withheld = [.. _withheld];
        if (!_hasStarted)
        {
            // Every written block lay in failed units or in what the syntax trims: nothing was measured, and the positions say why (the spans hold
            // only positions that are no trimmed ones).
            withheld[(int)WithholdReason.ChecksumFailed] += _leading.Concat(_pending).Where(p => p.Reason == WithholdReason.ChecksumFailed).Sum(p => p.End - p.First);
        }

        if (_hasCodedRange && _hasStarted && status is MeasurementStatus.Complete or MeasurementStatus.Incomplete)
        {
            // Positions the syntax trims are not samples of the stream, so a decoder's gap or withheld span that covers them is counted only outside them (docs/design/samples-definition.md):
            // the part before the start trimming and the part from the end trimming on.
            long leadingFrom = Math.Max(_codedStart, Math.Min(_trimStart, _firstReceived));
            long trailingTo = Math.Min(_codedEnd, Math.Max(_trimEnd, _received));

            // Those positions no block carried are trimmed samples all the same.
            leadingTrimmedGap = leadingFrom - _codedStart;
            trailingTrimmedGap = _codedEnd - trailingTo;
            leading = Math.Max(0, _firstReceived - leadingFrom);
            trailing = Math.Max(0, trailingTo - _received);
            leadingWithheld = leading > 0 ? Math.Min(leading, Overlap(_leading, leadingFrom, _firstReceived, withheld) + _leadingOverflow) : 0;
            trailingWithheld = trailing > 0 ? Overlap(_pending, _received, trailingTo, withheld) : 0;
        }

        if (leading == 0)
        {
            // Spans withheld before the first block that overflowed the list lie in positions no figure counts without a leading region.
            for (int i = 0; i < withheld.Length; i++)
            {
                withheld[i] -= _leadingOverflowBy[i];
            }
        }

        long trailingLost = Math.Max(0, trailing - trailingWithheld - _overflow);
        var record = new StreamMeasurements
        {
            Status = status,
            Reason = reason,
            Samples = _samples,
            FirstPosition = _firstPosition,
            LostSamples = _lost + (leading - leadingWithheld) + trailingLost,
            LeadingLostSamples = leading - leadingWithheld,
            TrailingLostSamples = trailingLost,
            WithheldSamples = withheld.Sum(),
            LeadingWithheldSamples = leadingWithheld + (_hasStarted ? _withheldBeforeStart : 0),
            TrailingWithheldSamples = trailingWithheld,
            WithheldByReason = WithheldCounts.From(withheld),
            LeadingTrimmedSamples = _trimmedLeading + leadingTrimmedGap,
            InnerTrimmedSamples = _trimmedInner,
            TrailingTrimmedSamples = _trimmedTrailing + _pendingInner + trailingTrimmedGap,
            Gaps = _hasStarted ? _gaps + (leading > 0 ? 1 : 0) + (trailing > 0 ? 1 : 0) : 0,
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

        record = record with { Levels = _levels?.Build(), Loudness = BuildLoudness(), TruePeak = BuildTruePeak() };
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
        var bandwidths = new List<SpectralBandwidth>();
        var slopes = new List<SpectralSlope>();
        for (int c = 0; c < _measured; c++)
        {
            perChannel[c] = Levels(_average.AsSpan(c * rows, rows));
            int[] fine = FineLevels(_average.AsSpan(c * rows, rows));
            cutoffs.Add(CutoffFinder.Find(fine, _counters.Length > 0 ? _counters[c] : null, _geometry));
            bandwidths.Add(CutoffFinder.FindBandwidth(fine, _geometry));
            slopes.Add(CutoffFinder.FindSlope(fine, _geometry));
        }

        short[] combined = Levels(_average.AsSpan(_measured * rows, rows));
        int[] combinedFine = FineLevels(_average.AsSpan(_measured * rows, rows));
        CutoffCounters? combinedCounters = _counters.Length > 0 ? _counters[_measured] : null;
        SpectralCutoff cutoff = CutoffFinder.Find(combinedFine, combinedCounters, _geometry);
        SpectralBandwidth combinedBandwidth = CutoffFinder.FindBandwidth(combinedFine, _geometry);
        MidSideFigures? midSide = null;
        if (HasPair)
        {
            // The mid and side spectra have a cutoff and holes of their own; the bandwidth only bounds their holes.
            short[] mid = Levels(_average.AsSpan((_measured + 1) * rows, rows));
            short[] side = Levels(_average.AsSpan((_measured + 2) * rows, rows));
            int[] midFine = FineLevels(_average.AsSpan((_measured + 1) * rows, rows));
            int[] sideFine = FineLevels(_average.AsSpan((_measured + 2) * rows, rows));
            CutoffCounters? midCounters = _counters.Length > 0 ? _counters[_measured + 1] : null;
            CutoffCounters? sideCounters = _counters.Length > 0 ? _counters[_measured + 2] : null;
            SpectralCutoff midCutoff = CutoffFinder.Find(midFine, midCounters, _geometry);
            SpectralCutoff sideCutoff = CutoffFinder.Find(sideFine, sideCounters, _geometry);
            midSide = new MidSideFigures
            {
                LeftChannel = _pairLeft,
                RightChannel = _pairRight,
                Mid = mid,
                Side = side,
                MidCutoff = midCutoff,
                SideCutoff = sideCutoff,
                MidHoles = CutoffFinder.FindHoles(midCounters, _geometry, midCutoff, CutoffFinder.FindBandwidth(midFine, _geometry)),
                SideHoles = CutoffFinder.FindHoles(sideCounters, _geometry, sideCutoff, CutoffFinder.FindBandwidth(sideFine, _geometry)),
            };
        }

        CoherenceFigures? coherenceFigures = HasPair
            ? CoherenceFinder.Find(_geometry, _frames, _average.AsSpan(_pairLeft * rows, rows), _average.AsSpan(_pairRight * rows, rows), _crossSums.AsSpan(0, rows), _crossSums.AsSpan(rows, rows), _coherence, cutoff, combinedBandwidth, _pairLeft, _pairRight)
            : null;
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
                Bandwidth = combinedBandwidth,
                ChannelBandwidths = bandwidths,
                Slope = CutoffFinder.FindSlope(combinedFine, _geometry),
                ChannelSlopes = slopes,
                PartialCutoff = CutoffFinder.FindPartial(_counters.Length > 0 ? _counters[_measured] : null, _geometry),
                Holes = CutoffFinder.FindHoles(_counters.Length > 0 ? _counters[_measured] : null, _geometry, cutoff, combinedBandwidth),
                MidSide = midSide,
                Coherence = coherenceFigures,
                IsNyquistEdgeSearched = NyquistEdgeFinder.IsSearched(_geometry, combinedCounters),
                NyquistEdges = NyquistEdgeFinder.Find(combinedFine, combinedCounters, _geometry),
            },
            Spectrogram = Grid(),
            Waveform = Wave(),
        };
    }

    private LoudnessFigures? BuildLoudness() =>
        !_isMeasuringLoudness ? null
        : _loudness is not null ? _loudness.Build(_loudnessWeights!)
        : new LoudnessFigures { NotMeasured = _loudnessNotMeasured };

    private TruePeakFigures? BuildTruePeak() =>
        !_isMeasuringTruePeak ? null
        : _truePeak is not null ? _truePeak.Build(_measured)
        : new TruePeakFigures { NotMeasured = _truePeakNotMeasured };

    private short[] Levels(ReadOnlySpan<double> sums)
    {
        var result = new short[sums.Length];
        for (int r = 0; r < sums.Length; r++)
        {
            result[r] = _frames == 0 || sums[r] <= 0 ? MeasurementLimits.NoPower : (short)Math.Clamp(DetMath.Tenths(sums[r] / _frames), short.MinValue + 1, short.MaxValue);
        }

        return result;
    }

    // The levels the searches use, in thousandths of a dB, so that a figure is rounded to tenths once (CutoffFinder).
    private int[] FineLevels(ReadOnlySpan<double> sums)
    {
        var result = new int[sums.Length];
        for (int r = 0; r < sums.Length; r++)
        {
            result[r] = _frames == 0 || sums[r] <= 0 ? CutoffFinder.NoPower : CutoffFinder.FineLevel(sums[r] / _frames);
        }

        return result;
    }

    // A stream whose blocks were all empty touched no hop: it has no grid, as a stream without a spectrum has none.
    private SpectrogramGrid? Grid()
    {
        if (_used == 0)
        {
            return null;
        }

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

    private WaveformGrid? Wave()
    {
        if (_used == 0)
        {
            return null;
        }

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
