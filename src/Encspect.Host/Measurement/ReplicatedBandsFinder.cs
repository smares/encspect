using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Host.Measurement;

// The replicated bands of Step 9 (docs/design/spectral-analysis.md): per grid band of 1 kHz and per offset (shift) in rows, in how many frames both the band and the
// band an offset lower have signal, and in how many of those the fine structure of the two correlates at 1/2 or more. The frames are every second frame of the spectrum.
internal sealed class ReplicatedBandsCounters
{
    // Row width is fs / 1,024 at every size (CutoffGeometry): the bands, the envelope and the offsets are in rows.
    private const int Rows = MeasurementLimits.Rows;

    private readonly int _bandRows;
    private readonly int _step;
    private readonly int _firstShift;
    private readonly int _halfWidth;

    // The frame's fine structure, the prefix sums of the logarithms, of the squares of the fine structure, of the power, and of the products of one shift.
    private readonly double[] _fine = new double[Rows];
    private readonly double[] _logSums = new double[Rows + 1];
    private readonly double[] _squares = new double[Rows + 1];
    private readonly double[] _power = new double[Rows + 1];
    private readonly double[] _products = new double[Rows + 1];
    private readonly double[] _targetPower;
    private readonly double[] _targetSquares;

    // Frames with signal in both bands, and those that match, by band and shift (index band * 512 + shift).
    public readonly int[] Frames;

    public readonly int[] Matches;

    public ReplicatedBandsCounters(CutoffGeometry geometry)
    {
        _bandRows = geometry.BandRows;
        _step = Math.Max(1, _bandRows / 4);
        long rate = geometry.SampleRate;
        _firstShift = (int)Math.Max(1, ((150L * 1024) + (rate / 2)) / rate);
        _halfWidth = Math.Max(2, _firstShift);
        BandCount = CountBands(geometry);
        Frames = new int[BandCount * Rows];
        Matches = new int[BandCount * Rows];
        _targetPower = new double[BandCount];
        _targetSquares = new double[BandCount];
    }

    public int BandCount { get; }

    public int BandRows => _bandRows;

    public int Step => _step;

    // The smallest offset tested, in rows (150 Hz).
    public int FirstShift => _firstShift;

    // The first row of grid band k: the bands start at 1 kHz (row W) and every W / 4 rows up, and end at row 505 at the highest.
    public int StartRow(int band) => _bandRows + (band * _step);

    public static long Bytes(CutoffGeometry geometry)
    {
        int bands = CountBands(geometry);
        return (2L * bands * Rows * sizeof(int)) + (long)(Rows * 5 * sizeof(double)) + (2L * bands * sizeof(double));
    }

    private static int CountBands(CutoffGeometry geometry)
    {
        int w = geometry.BandRows;
        int step = Math.Max(1, w / 4);
        int last = CutoffGeometry.LastCandidate;
        return last - (w - 1) >= w ? ((last - (w - 1) - w) / step) + 1 : 0;
    }

    // One frame's rows (the combined power): the fine structure is the logarithm minus the mean of the logarithms within 150 Hz on each side (cut at the ends of the
    // spectrum); each band is tested against every band that lies an offset lower.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Count(ReadOnlySpan<double> rows)
    {
        double[] fine = _fine;
        double[] logSums = _logSums;
        double[] squares = _squares;
        double[] power = _power;
        double[] products = _products;
        int w = _bandRows;
        int h = _halfWidth;
        logSums[0] = 0;
        power[0] = 0;
        for (int r = 0; r < Rows; r++)
        {
            double p = rows[r];
            fine[r] = DetMath.Ln(p >= 1e-30 ? p : 1e-30);
            logSums[r + 1] = logSums[r] + fine[r];
            power[r + 1] = power[r] + p;
        }

        squares[0] = 0;
        for (int r = 0; r < Rows; r++)
        {
            int low = Math.Max(0, r - h);
            int high = Math.Min(Rows - 1, r + h);
            double mean = (logSums[high + 1] - logSums[low]) / (high - low + 1);
            double x = fine[r] - mean;
            fine[r] = x;
            squares[r + 1] = squares[r] + (x * x);
        }

        double floor = w * MeasurementLimits.ReplicatedBandsSignalFloor;
        int bands = BandCount;
        double[] targetPower = _targetPower;
        double[] targetSquares = _targetSquares;
        for (int k = 0; k < bands; k++)
        {
            int s = StartRow(k);
            int e = s + w - 1;
            targetPower[k] = power[e + 1] - power[s];
            targetSquares[k] = squares[e + 1] - squares[s];
        }

        int[] frames = Frames;
        int[] matches = Matches;
        int top = CutoffGeometry.LastCandidate;
        for (int d = _firstShift; d < top; d++)
        {
            // Band k has a source from its start row s = W + k * step up where s - d >= 1.
            int firstBand = d + 1 <= w ? 0 : ((d + 1 - w) + _step - 1) / _step;
            if (firstBand >= bands)
            {
                break;
            }

            int startRow = StartRow(firstBand);
            products[startRow] = 0;
            for (int j = startRow; j <= top; j++)
            {
                products[j + 1] = products[j] + (fine[j] * fine[j - d]);
            }

            for (int k = firstBand; k < bands; k++)
            {
                if (!(targetPower[k] >= floor))
                {
                    continue;
                }

                int s = StartRow(k);
                int e = s + w - 1;
                if (!(power[e - d + 1] - power[s - d] >= floor))
                {
                    continue;
                }

                int cell = (k * Rows) + d;
                frames[cell]++;
                double product = products[e + 1] - products[s];
                if (product > 0)
                {
                    double source = squares[e - d + 1] - squares[s - d];
                    if (4.0 * (product * product) >= targetSquares[k] * source)
                    {
                        matches[cell]++;
                    }
                }
            }
        }
    }
}

// The replicated-bands figure from the counters of the whole stream, in integers: no logarithm and no division decides.
internal static class ReplicatedBandsFinder
{
    public static ReplicatedBandsFigures Find(
        CutoffGeometry geometry,
        int framesUsed,
        ReplicatedBandsCounters? counters,
        SpectralCutoff cutoff,
        SpectralBandwidth bandwidth)
    {
        if (!geometry.IsSearched || counters is null)
        {
            return new ReplicatedBandsFigures { IsSearched = false };
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        int bandCount = counters.BandCount;
        var bands = new ReplicatedBand[bandCount];
        var shifts = new int[bandCount];
        int maxFrames = 0;
        for (int k = 0; k < bandCount; k++)
        {
            int s = counters.StartRow(k);
            int best = -1;
            int bestMatches = -1;
            int baseIndex = k * rowCount;
            for (int d = w; d < s; d++)
            {
                maxFrames = Math.Max(maxFrames, counters.Frames[baseIndex + d]);
                if (counters.Matches[baseIndex + d] > bestMatches)
                {
                    bestMatches = counters.Matches[baseIndex + d];
                    best = d;
                }
            }

            if (best < 0)
            {
                bands[k] = new ReplicatedBand { StartRow = s, ShareTenths = MeasurementLimits.NoPower };
                continue;
            }

            int n = counters.Frames[baseIndex + best];
            bool isPeriodic = false;
            if (bestMatches > 0)
            {
                for (int d = counters.FirstShift; d < w && d < s; d++)
                {
                    if (2L * counters.Matches[baseIndex + d] >= bestMatches)
                    {
                        isPeriodic = true;
                        break;
                    }
                }
            }

            shifts[k] = best;
            bands[k] = new ReplicatedBand
            {
                StartRow = s,
                Offset = geometry.Hertz(best),
                ShareTenths = n > 0 ? (int)(((2000L * bestMatches) + n) / (2L * n)) : MeasurementLimits.NoPower,
                FramesWithSignal = n,
                IsPeriodic = isPeriodic,
                Passes = n >= MeasurementLimits.MinimumReplicatedBandsFrames && 2L * bestMatches >= n && !isPeriodic,
            };
        }

        var figures = new ReplicatedBandsFigures { FramesUsed = framesUsed, Bands = bands };
        if (framesUsed < MeasurementLimits.MinimumReplicatedBandsFrames)
        {
            return figures with { Reason = ReplicatedBandsReason.TooFewFrames };
        }

        // The range is the holes' and the coherence's: to the cutoff's edge where found, else the spectral bandwidth's content, else row 505.
        int last = CutoffGeometry.LastCandidate;
        if (cutoff.IsFound && geometry.RowOfUpperEdge(cutoff.Frequency) is >= 0 and var edge)
        {
            last = Math.Min(last, edge);
        }
        else if (bandwidth.IsFound && geometry.RowOfUpperEdge(bandwidth.Frequency) is >= 0 and var content)
        {
            last = Math.Min(last, content);
        }

        // The top band is the highest grid band that ends at or under the range's last row.
        int topBand = -1;
        for (int k = 0; k < bandCount; k++)
        {
            if (counters.StartRow(k) + w - 1 <= last)
            {
                topBand = k;
            }
        }

        if (topBand < 0)
        {
            return figures with { Reason = ReplicatedBandsReason.TooShort, RangeFrequency = geometry.Hertz(last + 1) };
        }

        if (maxFrames < MeasurementLimits.MinimumReplicatedBandsFrames)
        {
            return figures with { Reason = ReplicatedBandsReason.NoSignal, RangeFrequency = geometry.Hertz(last + 1) };
        }

        ReplicatedBand top = bands[topBand];
        figures = figures with
        {
            RangeFrequency = geometry.Hertz(last + 1),
            TopBandShareTenths = top.ShareTenths == MeasurementLimits.NoPower ? 0 : top.ShareTenths,
            IsTopBandPeriodic = top.IsPeriodic,
        };

        // The stretches: the rows of the passing bands, joined across gaps shorter than half a band.
        var stretches = new List<ReplicatedStretch>();
        int stretchFirst = -1;
        int stretchEnd = -1;
        int firstBand = -1;
        int lastBand = -1;
        int highestEnd = -1;
        void Close()
        {
            if (stretchFirst < 0)
            {
                return;
            }

            int low = int.MaxValue;
            int high = int.MinValue;

            // The runs of consecutive passing bands whose offsets lie within a row of the run's first; a run of one band lends its rows to its neighbor (an
            // offset seen once is the noise of a patch's edge more often than a patch of its own).
            var runs = new List<(int First, int End, int Shift, int Count)>();
            for (int k = firstBand; k <= lastBand; k++)
            {
                if (!bands[k].Passes)
                {
                    continue;
                }

                low = Math.Min(low, bands[k].ShareTenths);
                high = Math.Max(high, bands[k].ShareTenths);
                int s = counters.StartRow(k);
                if (runs.Count > 0 && Math.Abs(shifts[k] - runs[^1].Shift) <= 1)
                {
                    runs[^1] = runs[^1] with { End = s + w - 1, Count = runs[^1].Count + 1 };
                }
                else
                {
                    runs.Add((s, s + w - 1, shifts[k], 1));
                }
            }

            for (int i = 0; i < runs.Count && runs.Count > 1;)
            {
                if (runs[i].Count > 1)
                {
                    i++;
                    continue;
                }

                if (i > 0)
                {
                    runs[i - 1] = runs[i - 1] with { End = Math.Max(runs[i - 1].End, runs[i].End) };
                }
                else
                {
                    runs[i + 1] = runs[i + 1] with { First = runs[i].First };
                }

                runs.RemoveAt(i);
            }

            var offsets = new List<ReplicatedOffset>();
            foreach ((int first, int end, int shift, _) in runs)
            {
                offsets.Add(new ReplicatedOffset { FromFrequency = geometry.Hertz(first), ToFrequency = geometry.Hertz(end + 1), Offset = geometry.Hertz(shift) });
            }

            stretches.Add(new ReplicatedStretch
            {
                FromFrequency = geometry.Hertz(stretchFirst),
                ToFrequency = geometry.Hertz(stretchEnd + 1),
                Offsets = offsets,
                LowestShareTenths = low,
                HighestShareTenths = high,
            });
            highestEnd = stretchEnd;
            stretchFirst = -1;
        }

        for (int k = 0; k < bandCount; k++)
        {
            int s = counters.StartRow(k);
            int e = s + w - 1;
            if (e > last || !bands[k].Passes)
            {
                continue;
            }

            if (stretchFirst >= 0 && 2 * (s - (stretchEnd + 1)) < w)
            {
                stretchEnd = Math.Max(stretchEnd, e);
                lastBand = k;
                continue;
            }

            Close();
            (stretchFirst, stretchEnd, firstBand, lastBand) = (s, e, k, k);
        }

        Close();
        figures = figures with { Stretches = stretches };
        if (stretches.Count == 0)
        {
            return figures with { Reason = ReplicatedBandsReason.NotMatching };
        }

        ReplicatedStretch highest = stretches[^1];
        if (last - highestEnd > w)
        {
            return figures with { Reason = ReplicatedBandsReason.NotAtTop };
        }

        return figures with { IsFound = true, FromFrequency = highest.FromFrequency, ToFrequency = highest.ToFrequency };
    }
}
