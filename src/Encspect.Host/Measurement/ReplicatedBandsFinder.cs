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
    private const int Padding = 4;

    private readonly double[] _fine = new double[Rows + Padding];
    private readonly double[] _shifted = new double[4 * (Rows + 1)];
    private readonly double[] _logSums = new double[Rows + 1];
    private readonly double[] _squares = new double[Rows + 1];
    private readonly double[] _power = new double[Rows + 1];
    private readonly double[] _targetSquares;
    private readonly bool[] _targetOk;
    private readonly bool[] _sourceOk = new bool[Rows];

    // The fine structure of the previous frame used, and per band whether it had signal there: a frame counts for a band only where the band does not correlate at 1/2 or more with itself in
    // the previous frame used, so that a stationary signal's frames are one trial, not many (the amendment to S28).
    private readonly double[] _previous = new double[Rows + Padding];
    private readonly bool[] _previousOk;
    private bool _hasPrevious;

    // Frames with signal in both bands, and those that match, by shift and band (index shift * bands + band, so that the bands of one shift lie together).
    public readonly int[] Frames;

    public readonly int[] Matches;

    // Per band, the frames in which it had signal, before the gate (the reason of a steady signal is told from no signal by them).
    public readonly int[] SignalFrames;

    public ReplicatedBandsCounters(CutoffGeometry geometry)
    {
        _bandRows = RowsOf(geometry);
        _step = Math.Max(1, _bandRows / 4);
        long rate = geometry.SampleRate;
        _firstShift = (int)Math.Max(1, ((150L * 1024) + (rate / 2)) / rate);
        _halfWidth = Math.Max(2, _firstShift);
        BandCount = CountBands(geometry);
        Frames = new int[BandCount * Rows];
        Matches = new int[BandCount * Rows];
        SignalFrames = new int[BandCount];
        _targetSquares = new double[BandCount];
        _targetOk = new bool[BandCount];
        _previousOk = new bool[BandCount];
    }

    public int BandCount { get; }

    // The rows of a band: those of 1 kHz, and at least MeasurementLimits.MinimumReplicatedBandRows, so that the bands widen with the sample rate above 48 kHz.
    public static int RowsOf(CutoffGeometry geometry) => Math.Max(geometry.BandRows, MeasurementLimits.MinimumReplicatedBandRows);

    // The fine structure of the frame counted last, which the tests pin.
    internal ReadOnlySpan<double> FineRows => _fine.AsSpan(Padding);

    public int BandRows => _bandRows;

    public int Step => _step;

    // The smallest offset tested, in rows (150 Hz).
    public int FirstShift => _firstShift;

    // The first row of grid band k: the bands start at 1 kHz (row W) and every W / 4 rows up, and end at row 505 at the highest.
    public int StartRow(int band) => _bandRows + (band * _step);

    public static long Bytes(CutoffGeometry geometry)
    {
        int bands = CountBands(geometry);
        return (2L * bands * Rows * sizeof(int)) + (long)(((2 * (Rows + Padding)) + (4 * (Rows + 1)) + (3 * (Rows + 1)) + Rows) * sizeof(double)) + (2L * bands * sizeof(double));
    }

    private static int CountBands(CutoffGeometry geometry)
    {
        int w = RowsOf(geometry);
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
        int w = _bandRows;
        int h = _halfWidth;
        logSums[0] = 0;
        power[0] = 0;
        for (int r = 0; r < Rows; r++)
        {
            double p = rows[r];
            double ln = DetMath.Ln(p >= 1e-30 ? p : 1e-30);
            fine[r + Padding] = ln;
            logSums[r + 1] = logSums[r] + ln;
            power[r + 1] = power[r] + p;
        }

        squares[0] = 0;
        for (int r = 0; r < Rows; r++)
        {
            int low = Math.Max(0, r - h);
            int high = Math.Min(Rows - 1, r + h);
            double mean = (logSums[high + 1] - logSums[low]) / (high - low + 1);
            double x = fine[r + Padding] - mean;
            fine[r + Padding] = x;
            squares[r + 1] = squares[r] + (x * x);
        }

        double floor = w * MeasurementLimits.ReplicatedBandsSignalFloor;
        int bands = BandCount;
        double[] targetSquares = _targetSquares;
        bool[] targetOk = _targetOk;
        bool[] sourceOk = _sourceOk;
        for (int k = 0; k < bands; k++)
        {
            int s = StartRow(k);
            targetOk[k] = power[s + w] - power[s] >= floor;
            targetSquares[k] = squares[s + w] - squares[s];
        }

        double[] previous = _previous;
        bool[] previousOk = _previousOk;
        for (int k = 0; k < bands; k++)
        {
            bool hadSignal = targetOk[k];
            if (hadSignal && _hasPrevious && previousOk[k])
            {
                int s = StartRow(k);
                double product = 0;
                double earlier = 0;
                for (int j = s; j < s + w; j++)
                {
                    product += fine[j + Padding] * previous[j + Padding];
                    earlier += previous[j + Padding] * previous[j + Padding];
                }

                if (product > 0 && 4.0 * (product * product) >= targetSquares[k] * earlier)
                {
                    targetOk[k] = false;
                }
            }

            previousOk[k] = hadSignal;
            SignalFrames[k] += hadSignal ? 1 : 0;
        }

        Array.Copy(fine, previous, fine.Length);
        _hasPrevious = true;

        // A source band is a band of W rows from row q = 1 up to the last band's start.
        int top = CutoffGeometry.LastCandidate;
        for (int q = 1; q + w - 1 <= top; q++)
        {
            sourceOk[q] = power[q + w] - power[q] >= floor;
        }

        int[] frames = Frames;
        int[] matches = Matches;
        double[] shifted = _shifted;
        for (int d = _firstShift; d < top; d += 4)
        {
            // Four offsets at a time: the prefix sums of their products run side by side, so that the additions do not wait for one another. Band k has a source
            // from its start row s = W + k * step up where s - d >= 1; the arrays start at the first band of the lowest offset, and the fine structure has
            // zeros in front of row 0 for the rows the higher offsets would read there (no band of theirs uses them).
            int firstBand = d + 1 <= w ? 0 : ((d + 1 - w) + _step - 1) / _step;
            if (firstBand >= bands)
            {
                break;
            }

            int startRow = StartRow(firstBand);
            double p0 = 0;
            double p1 = 0;
            double p2 = 0;
            double p3 = 0;
            shifted[startRow] = 0;
            shifted[Rows + 1 + startRow] = 0;
            shifted[(2 * (Rows + 1)) + startRow] = 0;
            shifted[(3 * (Rows + 1)) + startRow] = 0;
            for (int j = startRow; j <= top; j++)
            {
                double x = fine[j + Padding];
                p0 += x * fine[j + Padding - d];
                p1 += x * fine[j + Padding - d - 1];
                p2 += x * fine[j + Padding - d - 2];
                p3 += x * fine[j + Padding - d - 3];
                shifted[j + 1] = p0;
                shifted[Rows + 1 + j + 1] = p1;
                shifted[(2 * (Rows + 1)) + j + 1] = p2;
                shifted[(3 * (Rows + 1)) + j + 1] = p3;
            }

            for (int lane = 0; lane < 4 && d + lane < top; lane++)
            {
                int offset = d + lane;
                int first = offset + 1 <= w ? 0 : ((offset + 1 - w) + _step - 1) / _step;
                int laneBase = lane * (Rows + 1);
                int baseCell = offset * bands;
                for (int k = first; k < bands; k++)
                {
                    int s = w + (k * _step);
                    if (!(targetOk[k] & sourceOk[s - offset]))
                    {
                        continue;
                    }

                    int cell = baseCell + k;
                    frames[cell]++;
                    double product = shifted[laneBase + s + w] - shifted[laneBase + s];
                    double source = squares[s - offset + w] - squares[s - offset];
                    bool isMatch = (product > 0) & (4.0 * (product * product) >= targetSquares[k] * source);
                    matches[cell] += Unsafe.As<bool, byte>(ref isMatch);
                }
            }
        }
    }

    // The counters' index of band k at offset d.
    public int Cell(int band, int shift) => (shift * BandCount) + band;
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

        int w = ReplicatedBandsCounters.RowsOf(geometry);
        int bandCount = counters.BandCount;
        var bands = new ReplicatedBand[bandCount];
        var shifts = new int[bandCount];
        int maxFrames = 0;
        for (int k = 0; k < bandCount; k++)
        {
            int s = counters.StartRow(k);
            int best = -1;
            int bestMatches = -1;
            for (int d = w; d < s; d++)
            {
                maxFrames = Math.Max(maxFrames, counters.Frames[counters.Cell(k, d)]);
                if (counters.Matches[counters.Cell(k, d)] > bestMatches)
                {
                    bestMatches = counters.Matches[counters.Cell(k, d)];
                    best = d;
                }
            }

            if (best < 0)
            {
                bands[k] = new ReplicatedBand { StartRow = s, ShareTenths = MeasurementLimits.NoPower };
                continue;
            }

            int n = counters.Frames[counters.Cell(k, best)];

            // A band is periodic where it would pass but an offset under 1 kHz matches in at least half as many frames as its own offset does (on noise, where nothing would pass,
            // such an offset matches by chance as often as any, and the band is not called periodic).
            bool wouldPass = n >= MeasurementLimits.MinimumReplicatedBandFramesCounted && 2L * bestMatches >= n;
            bool isPeriodic = false;
            if (wouldPass)
            {
                for (int d = counters.FirstShift; d < w && d < s; d++)
                {
                    if (2L * counters.Matches[counters.Cell(k, d)] >= bestMatches)
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
                Passes = wouldPass && !isPeriodic,
            };
        }

        var figures = new ReplicatedBandsFigures { FramesUsed = framesUsed, BandRows = w, StepRows = counters.Step, Bands = bands };
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
            ReplicatedBandsReason why = counters.SignalFrames.Max() < MeasurementLimits.MinimumReplicatedBandsFrames ? ReplicatedBandsReason.NoSignal : ReplicatedBandsReason.Steady;
            return figures with { Reason = why, RangeFrequency = geometry.Hertz(last + 1) };
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
