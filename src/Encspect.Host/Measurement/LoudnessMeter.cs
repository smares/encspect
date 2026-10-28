using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// The loudness of one stream (ITU-R BS.1770-5 Annex 1, EBU R 128, Tech 3341 and 3342; docs/design/spectral-analysis.md, "Loudness and
// loudness range"). Each measured channel runs through the K-weighting filter and its squares are summed over granules of 20 ms (a fifth of
// 100 ms, which is (rate + 5) / 10 samples, to the nearest sample); the weighted sum over the channels of the last 20 granules is a block of
// 400 ms (momentary loudness), that of the last 150 the short-term loudness (3 s). The gating blocks, the short-term values of the loudness
// range and the series are taken every 100 ms, as the Recommendation's 75 % overlap has it; the maximum momentary loudness every 20 ms,
// because EBU Tech 3341 test 13 needs a window that can start at any multiple of 20 ms. Neither the filtered audio nor the blocks are kept:
// the memory is fixed whatever the length, two histograms of 10,000 bins of 0.01 LU (a count and an energy sum each, the energy sums exact),
// the last 150 granules and a series of at most 1,024 points.
//
// Nothing here depends on how the decoder cut its blocks: every sum runs in the order of the samples, the filter states are flushed at
// stream positions that are multiples of 4,096, and a block never spans a gap.
internal sealed class LoudnessMeter
{
    // The most samples of a channel given to AddChannel at once.
    public const int MaxPiece = 16384;

    private const int Bins = 10000;

    private const int GranulesPerStep = 5;

    private const int BlockGranules = 20;

    private const int Ring = 150;

    private const double Flush = 1e-30;

    // Bin b holds the loudness from -70 + b / 100 to -70 + (b + 1) / 100 LUFS: floor(1000 log10(z) + 6930.9) for an energy z.
    private const double BinOffset = 6930.9;

    private readonly int _measured;
    private readonly int _rate;
    private readonly long _stepLength;
    private readonly KWeighting _filter;
    private readonly double[] _weights;
    private readonly double[] _state;
    private readonly double[] _acc;
    private readonly double[] _closed;
    private readonly int _closedCapacity;
    private readonly double[] _ring = new double[Ring];
    private readonly long[] _ringSamples = new long[Ring];

    // The histograms (320 KB) are made with the first block, so a stream that never fills 400 ms costs nothing.
    private long[] _blockCounts = [];
    private double[] _blockSums = [];
    private long[] _shortCounts = [];
    private double[] _shortSums = [];

    // The energy at and above which a block is above the absolute gate of -70 LUFS.
    private readonly double _absoluteGate = DetMath.Pow(10.0, -6.9309);

    private readonly long[] _pointMilliseconds = new long[MeasurementLimits.MaxSeriesPoints];
    private readonly double[] _pointEnergy = new double[MeasurementLimits.MaxSeriesPoints];
    private readonly int[] _pointSteps = new int[MeasurementLimits.MaxSeriesPoints];

    private long _position;
    private long _runSamples;
    private long _granuleEnd;
    private long _granules;
    private long _firstPosition;
    private bool _isStarted;
    private bool _isBreak;
    private long _blocks;
    private long _blocksAbove;
    private double _maxMomentary = -1;
    private double _maxShortTerm = -1;
    private int _points;
    private int _perPoint = 1;

    public LoudnessMeter(int rate, IReadOnlyList<int> weights)
    {
        _rate = rate;
        _measured = weights.Count;
        _stepLength = (rate + 5L) / 10;
        _filter = KWeighting.For(rate);
        _weights = [.. weights.Select(w => w / 100.0)];
        _state = new double[_measured * 4];
        _acc = new double[_measured];
        _closedCapacity = (int)(MaxPiece / Math.Max(1, _stepLength / GranulesPerStep)) + 2;
        _closed = new double[_measured * _closedCapacity];
    }

    // The bytes the working state needs, for the memory bound.
    public static long EstimateBytes(int measured, int rate)
    {
        long capacity = (MaxPiece / Math.Max(1, (rate + 5L) / 10 / GranulesPerStep)) + 2;
        return (4L * Bins * 8) + (measured * ((4 * 8) + 8 + (capacity * 8))) + (MeasurementLimits.MaxSeriesPoints * 20L) + (Ring * 16L) + 1024;
    }

    public int Measured => _measured;

    // The stream's first block, or the first one after a gap: nothing carries over, and no block spans it.
    public void Start(long position)
    {
        if (!_isStarted)
        {
            _isStarted = true;
            _firstPosition = position;
        }

        _position = position;
        _runSamples = 0;
        _granules = 0;
        _granuleEnd = Boundary(1);
        Array.Clear(_state);
        Array.Clear(_acc);
        _isBreak = true;
    }

    // The end, in samples from the start of the run, of granule k - 1: granules are a fifth of a step of 100 ms, rounded to the nearest sample,
    // so that every fifth boundary is a multiple of the step.
    private long Boundary(long k) => ((k * _stepLength) + 2) / GranulesPerStep;

    // The samples of one measured channel, already made safe by the stream measurer (finite, at most 1,024 times full scale, none under
    // 1e-30 but 0). Every channel is given the same piece, then EndPiece.
    public void AddChannel(int channel, ReadOnlySpan<double> samples)
    {
        if (_weights[channel] == 0)
        {
            return;
        }

        long position = _position;
        long run = _runSamples;
        long end = _granuleEnd;
        long granule = _granules;
        int closedAt = channel * _closedCapacity;
        Span<double> state = _state.AsSpan(channel * 4, 4);
        for (int i = 0; i < samples.Length;)
        {
            if ((position & 4095) == 0)
            {
                state[0] = Math.Abs(state[0]) < Flush ? 0 : state[0];
                state[1] = Math.Abs(state[1]) < Flush ? 0 : state[1];
                state[2] = Math.Abs(state[2]) < Flush ? 0 : state[2];
                state[3] = Math.Abs(state[3]) < Flush ? 0 : state[3];
            }

            int take = (int)Math.Min(Math.Min(samples.Length - i, end - run), 4096 - (position & 4095));
            FilterSquares(samples.Slice(i, take), _filter, state, ref _acc[channel]);
            i += take;
            position += take;
            run += take;
            if (run == end)
            {
                _closed[closedAt++] = _acc[channel];
                _acc[channel] = 0;
                granule++;
                end = Boundary(granule + 1);
            }
        }
    }

    // The piece is done for every channel: the granules that closed in it are summed over the channels and become blocks.
    public void EndPiece(int count)
    {
        long run = _runSamples;
        long end = _granuleEnd;
        long granule = _granules;
        long left = count;
        for (int j = 0; run + left >= end; j++)
        {
            double z = 0;
            for (int c = 0; c < _measured; c++)
            {
                if (_weights[c] != 0)
                {
                    z += _weights[c] * _closed[(c * _closedCapacity) + j];
                }
            }

            left -= end - run;
            long start = Boundary(granule);
            run = end;
            granule++;
            Close(z, granule, end - start, _position + (count - left));
            end = Boundary(granule + 1);
        }

        _runSamples = run + left;
        _granuleEnd = end;
        _granules = granule;
        _position += count;
    }

    // The two second-order sections in transposed direct form II, and the sum of the squares of the output, in the order of the samples.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FilterSquares(ReadOnlySpan<double> x, in KWeighting k, Span<double> state, ref double sum)
    {
        double s1 = state[0];
        double s2 = state[1];
        double t1 = state[2];
        double t2 = state[3];
        double total = sum;
        double b0 = k.B0;
        double b1 = k.B1;
        double b2 = k.B2;
        double a1 = k.A1;
        double a2 = k.A2;
        double h1 = k.HighpassA1;
        double h2 = k.HighpassA2;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double y = (b0 * v) + s1;
            s1 = ((b1 * v) - (a1 * y)) + s2;
            s2 = (b2 * v) - (a2 * y);
            double z = y + t1;
            t1 = ((-2.0 * y) - (h1 * z)) + t2;
            t2 = y - (h2 * z);
            total += z * z;
        }

        state[0] = s1;
        state[1] = s2;
        state[2] = t1;
        state[3] = t2;
        sum = total;
    }

    // A closed granule, the granule-th since the run began, with the weighted sum of squares z over its samples, ending at the given stream
    // position.
    private void Close(double z, long granule, long samples, long endPosition)
    {
        int slot = (int)((granule - 1) % Ring);
        _ring[slot] = z;
        _ringSamples[slot] = samples;
        long filled = Math.Min(granule, Ring);
        if (filled < BlockGranules)
        {
            return;
        }

        double momentary = Mean(granule, BlockGranules);
        if (momentary > _maxMomentary)
        {
            _maxMomentary = momentary;
        }

        if (granule % GranulesPerStep != 0)
        {
            return;
        }

        _blocks++;
        if (_blockCounts.Length == 0)
        {
            Allocate();
        }

        if (momentary > _absoluteGate)
        {
            _blocksAbove++;
            Add(_blockCounts, _blockSums, momentary);
        }

        if (filled < Ring)
        {
            return;
        }

        double shortTerm = Mean(granule, Ring);
        if (shortTerm > _maxShortTerm)
        {
            _maxShortTerm = shortTerm;
        }

        if (shortTerm >= _absoluteGate)
        {
            Add(_shortCounts, _shortSums, shortTerm);
        }

        AddPoint(shortTerm, (long)((UInt128)(ulong)(endPosition - _firstPosition) * 1000 / (ulong)_rate));
    }

    // The mean square of the last n granules, summed from the oldest.
    private double Mean(long granule, int n)
    {
        double sum = 0;
        long samples = 0;
        for (long i = granule - n; i < granule; i++)
        {
            int slot = (int)(i % Ring);
            sum += _ring[slot];
            samples += _ringSamples[slot];
        }

        return sum / samples;
    }

    private static int Bin(double energy) => (int)Math.Clamp(Math.Floor((1000.0 * DetMath.Log10(energy)) + BinOffset), 0, Bins - 1);

    private void Allocate()
    {
        _blockCounts = new long[Bins];
        _blockSums = new double[Bins];
        _shortCounts = new long[Bins];
        _shortSums = new double[Bins];
    }

    // The work the histograms are charged as, once, when the first of them is made: a few tenths of a millisecond.
    public const long AllocationUnits = 1 << 18;

    public bool HasAllocated => _blockCounts.Length > 0;

    private static void Add(long[] counts, double[] sums, double energy)
    {
        int bin = Bin(energy);
        counts[bin]++;
        sums[bin] += energy;
    }

    // A short-term value joins the last point while that point covers fewer values than the stream's current step; the points are merged
    // in pairs, doubling the step, whenever they would pass the most. A point after a gap starts a new one.
    private void AddPoint(double energy, long milliseconds)
    {
        bool isBreak = _isBreak;
        _isBreak = false;
        if (!isBreak && _points > 0 && _pointSteps[_points - 1] < _perPoint)
        {
            _pointEnergy[_points - 1] += energy;
            _pointSteps[_points - 1]++;
            return;
        }

        if (_points == MeasurementLimits.MaxSeriesPoints)
        {
            for (int i = 0; i < _points / 2; i++)
            {
                _pointMilliseconds[i] = _pointMilliseconds[2 * i];
                _pointEnergy[i] = _pointEnergy[2 * i] + _pointEnergy[(2 * i) + 1];
                _pointSteps[i] = _pointSteps[2 * i] + _pointSteps[(2 * i) + 1];
            }

            _points /= 2;
            _perPoint *= 2;
            if (!isBreak && _pointSteps[_points - 1] < _perPoint)
            {
                _pointEnergy[_points - 1] += energy;
                _pointSteps[_points - 1]++;
                return;
            }
        }

        _pointMilliseconds[_points] = milliseconds;
        _pointEnergy[_points] = energy;
        _pointSteps[_points] = 1;
        _points++;
    }

    // The loudness in tenths of a LUFS of an energy: floor(10 (10 log10 z - 0.691) + 0.5).
    public static int Tenths(double energy) => (int)Math.Clamp(Math.Floor((100.0 * DetMath.Log10(energy)) - 6.91 + 0.5), -100000, 100000);

    public LoudnessFigures Build(IReadOnlyList<int> weights)
    {
        var figures = new LoudnessFigures
        {
            ChannelWeights = weights,
            Blocks = _blocks,
            BlocksAboveAbsoluteGate = _blocksAbove,
            MomentaryAbsence = _blocks == 0 ? LoudnessAbsence.TooShort : LoudnessAbsence.None,
            MaximumMomentaryTenths = _blocks == 0 || _maxMomentary <= 0 ? MeasurementLimits.NoLevel : Tenths(_maxMomentary),
            ShortTermAbsence = _maxShortTerm < 0 ? LoudnessAbsence.TooShort : LoudnessAbsence.None,
            MaximumShortTermTenths = _maxShortTerm <= 0 ? MeasurementLimits.NoLevel : Tenths(_maxShortTerm),
            StepsPerPoint = _perPoint,
        };

        // Integrated loudness: the blocks above the absolute gate, then those above a gate 10 LU below their mean loudness.
        (long count, double sum) = Totals(_blockCounts, _blockSums);
        if (count == 0)
        {
            figures = figures with { IntegratedAbsence = _blocks == 0 ? LoudnessAbsence.TooShort : LoudnessAbsence.BelowGate };
        }
        else
        {
            (long passed, double energy) = Gated(_blockCounts, _blockSums, sum / count * 0.1);
            figures = figures with
            {
                BlocksAboveRelativeGate = passed,
                IntegratedTenths = passed == 0 ? MeasurementLimits.NoLevel : Tenths(energy / passed),
                IntegratedAbsence = passed == 0 ? LoudnessAbsence.BelowGate : LoudnessAbsence.None,
            };
        }

        // Loudness range: the short-term values above the absolute gate, then those above a gate 20 LU below their mean, and the spread
        // between the 10th and the 95th percentile of what is left.
        (long shortCount, double shortSum) = Totals(_shortCounts, _shortSums);
        if (shortCount == 0)
        {
            figures = figures with { RangeAbsence = _maxShortTerm < 0 ? LoudnessAbsence.TooShort : LoudnessAbsence.BelowGate };
        }
        else
        {
            double gate = shortSum / shortCount * 0.01;
            (long passed, _) = Gated(_shortCounts, _shortSums, gate);
            if (passed == 0)
            {
                figures = figures with { RangeAbsence = LoudnessAbsence.BelowGate };
            }
            else
            {
                int low = Percentile(_shortCounts, _shortSums, gate, passed, 10);
                int high = Percentile(_shortCounts, _shortSums, gate, passed, 95);
                figures = figures with
                {
                    RangeTenths = (high - low + 5) / 10,
                    RangeLowTenths = (((2 * low) + 11) / 20) - 700,
                    RangeHighTenths = (((2 * high) + 11) / 20) - 700,
                };
            }
        }

        return figures with { ShortTerm = BuildSeries() };
    }

    private static (long Count, double Sum) Totals(long[] counts, double[] sums)
    {
        long count = 0;
        double sum = 0;
        for (int b = 0; b < counts.Length; b++)
        {
            count += counts[b];
            sum += sums[b];
        }

        return (count, sum);
    }

    // The bin that holds a gate of the given energy: -1 where the gate is at or below the absolute one, so that every bin is above it.
    private static int GateBin(double gate) => gate <= 0 ? -1 : (int)Math.Min(Math.Floor((1000.0 * DetMath.Log10(gate)) + BinOffset), Bins - 1);

    // Bins above the gate's bin pass whole; the gate's own bin passes whole when the mean energy of its values is above the gate, which is
    // exact for values of one level and wrong by less than the bin's share of the passing values (docs/design/spectral-analysis.md has the
    // bound).
    private static bool Passes(int bin, int gateBin, long[] counts, double[] sums, double gate) =>
        counts[bin] > 0 && (bin > gateBin || (bin == gateBin && sums[bin] > gate * counts[bin]));

    // The count and the energy of the values above a gate of the given energy.
    private static (long Count, double Sum) Gated(long[] counts, double[] sums, double gate)
    {
        int gateBin = GateBin(gate);
        long count = 0;
        double sum = 0;
        for (int b = Math.Max(gateBin, 0); b < counts.Length; b++)
        {
            if (Passes(b, gateBin, counts, sums, gate))
            {
                count += counts[b];
                sum += sums[b];
            }
        }

        return (count, sum);
    }

    // The bin that holds the p-th percentile of the values above the gate, by the rank round((n - 1) p / 100) in ascending order.
    private static int Percentile(long[] counts, double[] sums, double gate, long passed, int p)
    {
        long rank = (((passed - 1) * p) + 50) / 100;
        int gateBin = GateBin(gate);
        long seen = 0;
        for (int b = Math.Max(gateBin, 0); b < counts.Length; b++)
        {
            if (!Passes(b, gateBin, counts, sums, gate))
            {
                continue;
            }

            seen += counts[b];
            if (seen > rank)
            {
                return b;
            }
        }

        return Bins - 1;
    }

    private Series? BuildSeries()
    {
        if (_points == 0)
        {
            return null;
        }

        double[] x = new double[_points];
        double[] values = new double[_points];
        for (int i = 0; i < _points; i++)
        {
            x[i] = _pointMilliseconds[i] / 1000.0;
            double energy = _pointEnergy[i] / _pointSteps[i];
            values[i] = energy <= 0 ? -70.0 : Math.Max(Tenths(energy), -700) / 10.0;
        }

        return new Series("Short-Term Loudness", "LUFS", SeriesAxis.Seconds, x, values) { Description = MeasurementText.ShortTermSeriesDescription };
    }
}
