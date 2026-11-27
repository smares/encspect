using Encspect.Core;

namespace Encspect.Host.Measurement;

// The per-row, per-level counters of one spectrum (docs/design/spectral-analysis.md, "Spectral cutoff", step 4): for every candidate
// row, how many frames had the level under it, and how many of them showed an own step of 15 dB there.
internal sealed class CutoffCounters
{
    public const int Levels = 72;

    public const int FirstExponent = -64;

    // 10^(15 / 10): the own step of a frame reaches T = 15 dB when the mean power under is at least this times the mean power above.
    public const double OwnStepRatio = 31.622776601683793;

    public readonly int[] All = new int[MeasurementLimits.Rows * Levels];

    public readonly int[] Show = new int[MeasurementLimits.Rows * Levels];

    // Frames whose band under the row stands 30 dB above the mean power of everything above it, up to the top of the search; kept for
    // the combined spectrum only (the partial cutoff, docs/design/spectral-analysis.md, Step 7 (b)). At the cell of All.
    public readonly int[]? Ceiling;

    // 10^(30 / 10): a frame shows a ceiling at a row when the mean power of the band under it is at least this times the mean power of
    // every row above it.
    public const double CeilingRatio = 1000.0;

    // Frames in which the row's power is at most a thousandth of the mean power of the W rows under it and of the W rows over it (the
    // spectral holes, docs/design/spectral-analysis.md, Step 7 (d)); kept for the combined, mid and side spectra. At the cell of All.
    public readonly int[]? Holes;

    // 10^(30 / 10): a row is a hole when its power times this is at most the mean power of the band on each side.
    public const double HoleRatio = 1000.0;

    public CutoffCounters(bool hasCeiling = false, bool hasHoles = false)
    {
        if (hasCeiling)
        {
            Ceiling = new int[MeasurementLimits.Rows * Levels];
        }

        if (hasHoles)
        {
            Holes = new int[MeasurementLimits.Rows * Levels];
        }
    }

    public static long Bytes => 2L * MeasurementLimits.Rows * Levels * sizeof(int);

    public static long CeilingBytes => (long)MeasurementLimits.Rows * Levels * sizeof(int);

    public static long HolesBytes => (long)MeasurementLimits.Rows * Levels * sizeof(int);
}

// The geometry the search needs, from the sample rate and the FFT size, in integers.
internal readonly struct CutoffGeometry
{
    public CutoffGeometry(int sampleRate, int fftSize)
    {
        SampleRate = sampleRate;
        FftSize = fftSize;
        BinsPerRow = fftSize / (MeasurementLimits.Rows * 2);
        long rowHertzTimesSize = (long)sampleRate * BinsPerRow;

        // 1 kHz in rows, rounded; the first candidate is the row whose upper edge reaches 2 kHz; the last is row 505, the last whose upper
        // edge is within 99 % of Nyquist at every size (506 / 1,024 < 0.99 * 0.5 * 2).
        BandRows = (int)Math.Max(1, ((2000L * fftSize) + rowHertzTimesSize) / (2 * rowHertzTimesSize));
        long first = ((2000L * fftSize) + rowHertzTimesSize - 1) / rowHertzTimesSize;
        FirstCandidate = (int)Math.Max(BandRows - 1, first - 1);
    }

    public const int LastCandidate = 505;

    // The highest row the search reaches: the last row that starts below 99 % of Nyquist (506 / 1,024 < 0.495), so the bands above a
    // candidate stop short of the notch that digital filters have at Nyquist.
    public const int TopRow = 506;

    public const int MinimumRate = 8000;

    public int SampleRate { get; }

    public int FftSize { get; }

    public int BinsPerRow { get; }

    public int BandRows { get; }

    public int FirstCandidate { get; }

    public bool IsSearched => SampleRate >= MinimumRate;

    // The first row tested for a hole: the first candidate, and one with a full band under it.
    public int FirstHoleRow => Math.Max(FirstCandidate, BandRows);

    // The last row whose upper edge is the frequency (a cutoff's or a spectral bandwidth's), or -1 where none is.
    public int RowOfUpperEdge(int hertz)
    {
        for (int r = 0; r < MeasurementLimits.Rows; r++)
        {
            int edge = Hertz(r + 1);
            if (edge == hertz)
            {
                return r;
            }

            if (edge > hertz)
            {
                break;
            }
        }

        return -1;
    }

    // The frequency in whole hertz of the boundary above row (boundary - 1): boundary * BinsPerRow * SampleRate / FftSize, rounded.
    public int Hertz(int boundary) => (int)(((2L * boundary * BinsPerRow * SampleRate) + FftSize) / (2L * FftSize));
}

internal static class CutoffFinder
{
    // The searches run on rows in thousandths of a dB (the rows' tenths would be rounded once more in every band mean and difference; a figure is
    // rounded to tenths once, at the end). A row without power holds NoPower.
    public const int NoPower = int.MinValue;

    // The level of a row without power in the search: -200 dB.
    private const int SilentLevel = -200_000;

    private const int StepThreshold = 25_000;

    // The floor's run stays within 3 dB of the floor; the band under it must be 20 dB above it.
    private const int BandwidthTolerance = 3_000;

    private const int BandwidthStep = 20_000;

    // The slope starts where the top band is 5 dB under the band below it and goes on while a band is 3 dB under the W rows over it.
    private const int SlopeStart = 5_000;

    private const int SlopeContinue = 3_000;

    // The rows' level in thousandths of a dB from the mean power: floor(10000 log10(mean) + 0.5), the half up.
    public static int FineLevel(double meanPower) => (int)Math.Clamp(Math.Floor((10000.0 * DetMath.Log10(meanPower)) + 0.5), -2_000_000.0, 2_000_000.0);

    // A quotient in thousandths of a dB, as tenths rounded to the nearest with halves up: floor(numerator / denominator / 100 + 0.5).
    internal static int Tenths(long numerator, long denominator) => (int)FloorDivide((2 * numerator) + (100 * denominator), 200 * denominator);

    public static SpectralCutoff Find(ReadOnlySpan<int> rows, CutoffCounters? counters, CutoffGeometry geometry)
    {
        if (!geometry.IsSearched || counters is null)
        {
            return new SpectralCutoff { IsSearched = false };
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        Span<long> prefix = stackalloc long[rowCount + 1];
        Span<int> level = stackalloc int[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            level[r] = rows[r] == NoPower ? SilentLevel : rows[r];
            prefix[r + 1] = prefix[r] + level[r];
        }

        // The largest step: the mean of dB under the candidate against the mean above it, compared as fractions (the band above is
        // clipped at the top row), the lowest row on a tie.
        int best = -1;
        long bestNumerator = 0;
        long bestDenominator = 1;
        for (int r = geometry.FirstCandidate; r <= CutoffGeometry.LastCandidate; r++)
        {
            int high = Math.Min(r + w, CutoffGeometry.TopRow);
            int above = high - r;
            long under = prefix[r + 1] - prefix[r - w + 1];
            long over = prefix[high + 1] - prefix[r + 1];
            long numerator = (under * above) - (over * w);
            long denominator = (long)w * above;
            if (best < 0 || numerator * bestDenominator > bestNumerator * denominator)
            {
                best = r;
                bestNumerator = numerator;
                bestDenominator = denominator;
            }
        }

        if (best < 0)
        {
            return new SpectralCutoff { IsSearched = false };
        }

        int stepTenths = Tenths(bestNumerator, bestDenominator);

        (int withSignal, int showing) = Frames(counters, best, counters.Show);

        int stepHertz = geometry.Hertz(best + 1);
        bool isFound = bestNumerator >= (long)StepThreshold * bestDenominator && withSignal > 0 && 2L * showing >= withSignal;
        if (!isFound)
        {
            return new SpectralCutoff { StepFrequency = stepHertz, StepTenthsOfDb = stepTenths, FramesShowing = showing, FramesWithSignal = withSignal };
        }

        // The edge: walk up from the step's row while at least half of the frames with signal show their own step, and stop at the first
        // row that does not, so a tone above the edge cannot set it. The last showing row is one band above the edge for a brickwall (a frame
        // shows a step while the band under the row still reaches its signal), so the edge is the lowest row of the band under it; where the
        // shift is not exact the clamp to the step's row decides.
        int last = best;
        while (last < CutoffGeometry.LastCandidate)
        {
            (int signalNext, int showingNext) = Frames(counters, last + 1, counters.Show);
            if (signalNext == 0 || 2L * showingNext < signalNext)
            {
                break;
            }

            last++;
        }

        int edge = Math.Max(best, last - w + 1);

        // The transition starts at the lowest row, walking down from the step, still more than 3 dB below the mean of the band under it.
        long underSum = prefix[best + 1] - prefix[best - w + 1];
        int start = best + 1;
        for (int r = best; r >= 0 && (long)level[r] * w < underSum - ((long)SlopeContinue * w); r--)
        {
            start = r;
        }

        return new SpectralCutoff
        {
            IsFound = true,
            Frequency = geometry.Hertz(edge + 1),
            TransitionStart = geometry.Hertz(start),
            StepFrequency = stepHertz,
            StepTenthsOfDb = stepTenths,
            FramesShowing = showing,
            FramesWithSignal = withSignal,
        };
    }

    // The spectral bandwidth of docs/design/spectral-analysis.md, Step 7 (a): the floor is the mean level of the band of W rows that ends at
    // the top row of the search; the run goes down while the band that ends at the row has a mean within 3 dB of the floor, and the
    // bandwidth is the run's lowest row where the band of W rows under it is at least 20 dB above the floor. Sums of W integers are
    // compared, so it is exact.
    public static SpectralBandwidth FindBandwidth(ReadOnlySpan<int> rows, CutoffGeometry geometry)
    {
        if (!geometry.IsSearched)
        {
            return new SpectralBandwidth { IsSearched = false };
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        int top = CutoffGeometry.TopRow;
        Span<long> prefix = stackalloc long[rowCount + 1];
        for (int r = 0; r < rowCount; r++)
        {
            prefix[r + 1] = prefix[r] + (rows[r] == NoPower ? SilentLevel : rows[r]);
        }

        long floorSum = prefix[top + 1] - prefix[top - w + 1];
        int end = top;
        while (end - 1 >= w - 1)
        {
            long band = prefix[end] - prefix[end - w];
            if (Math.Abs(band - floorSum) > (long)BandwidthTolerance * w)
            {
                break;
            }

            end--;
        }

        // The run's lowest row is the lowest row of the lowest band within the tolerance: the shift by W - 1 rows of the cutoff's edge.
        int lowest = end - w + 1;
        if (lowest < geometry.FirstCandidate || lowest < w)
        {
            return new SpectralBandwidth { Reason = SpectralBandwidthReason.ReachesTop };
        }

        long under = prefix[lowest] - prefix[lowest - w];
        if (under < floorSum + ((long)BandwidthStep * w))
        {
            return new SpectralBandwidth { Reason = SpectralBandwidthReason.Slope };
        }

        return new SpectralBandwidth
        {
            IsFound = true,
            Frequency = geometry.Hertz(lowest),
            FloorTenthsOfDb = Tenths(floorSum, w),
        };
    }

    // The spectral slope of docs/design/spectral-analysis.md, Step 7b, in integers: the top band is the band of W rows that ends at the top
    // row; the figure is found where the band under it is at least 5 dB above it; the run goes down from there while the cutoff's step at the
    // row is at least 3 dB, and stops at the first candidate.
    public static SpectralSlope FindSlope(ReadOnlySpan<int> rows, CutoffGeometry geometry)
    {
        if (!geometry.IsSearched)
        {
            return new SpectralSlope { IsSearched = false };
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        int top = CutoffGeometry.TopRow;
        Span<long> prefix = stackalloc long[rowCount + 1];
        for (int r = 0; r < rowCount; r++)
        {
            prefix[r + 1] = prefix[r] + (rows[r] == NoPower ? SilentLevel : rows[r]);
        }

        // The sum of the band of W rows that ends at row e.
        long topSum = prefix[top + 1] - prefix[top - w + 1];
        int start = top - w;
        long startSum = prefix[start + 1] - prefix[start - w + 1];
        int topStep = Tenths(startSum - topSum, w);
        if (startSum - topSum < (long)SlopeStart * w)
        {
            return new SpectralSlope { TopStepTenthsOfDb = topStep };
        }

        int row = start;
        while (row - 1 >= geometry.FirstCandidate && row - 1 - w + 1 >= 0)
        {
            long band = prefix[row] - prefix[row - w];
            long over = prefix[row + w] - prefix[row];
            if (band - over < (long)SlopeContinue * w)
            {
                break;
            }

            row--;
        }

        long fallSum = (prefix[row + 1] - prefix[row - w + 1]) - topSum;
        long distance = (2L * (top - row)) - w;
        return new SpectralSlope
        {
            IsFound = true,
            FromFrequency = geometry.Hertz(row + 1),
            ToFrequency = geometry.Hertz(top + 1),
            FallTenthsOfDb = Tenths(fallSum, w),
            TenthsOfDbPerKilohertz = (int)FloorDivide((2 * fallSum * 2_048_000L) + (100L * w * distance * geometry.SampleRate), 200L * w * distance * geometry.SampleRate),
            TopStepTenthsOfDb = topStep,
        };
    }

    // The partial cutoff of Step 7 (b): over the candidate rows, the one at which the most frames with signal show a ceiling (the lowest
    // on a tie), then the lowest row below it, walking down, whose count stays at least half of that maximum, which gives the frequency (so
    // it lands on the edge and not up to W - 1 rows above it); the count is that maximum, and the figure is found where at least 10 % of the
    // frames with signal at the row that has it show a ceiling.
    public static PartialCutoff FindPartial(CutoffCounters? counters, CutoffGeometry geometry)
    {
        if (!geometry.IsSearched || counters?.Ceiling is not { } ceiling)
        {
            return new PartialCutoff { IsSearched = false };
        }

        int first = geometry.FirstCandidate;
        int last = CutoffGeometry.LastCandidate;
        var showing = new int[last + 1];
        int best = first;
        for (int r = first; r <= last; r++)
        {
            showing[r] = Frames(counters, r, ceiling).Showing;
            if (showing[r] > showing[best])
            {
                best = r;
            }
        }

        int row = best;
        while (row - 1 >= first && showing[best] > 0 && 2L * showing[row - 1] >= showing[best])
        {
            row--;
        }

        // The frequency is the walk's row; the count is the most frames, at the row that has them.
        (int withSignal, int shown) = Frames(counters, best, ceiling);
        bool isFound = withSignal > 0 && 10L * shown >= withSignal;
        return new PartialCutoff { IsFound = isFound, Frequency = shown > 0 ? geometry.Hertz(row + 1) : 0, FramesShowing = shown, FramesWithSignal = withSignal };
    }

    // The spectral holes of Step 7 (d), from the counters: per row, the hole cells over the frames with signal, and the figure over the rows from the
    // first hole row to the last row of content, which is the cutoff's edge where found, else the spectral bandwidth's lower edge less a row where
    // found, else row 505. The frames with signal at a row are the cutoff's (Frames).
    public static SpectralHoles FindHoles(CutoffCounters? counters, CutoffGeometry geometry, SpectralCutoff cutoff, SpectralBandwidth bandwidth)
    {
        if (!geometry.IsSearched || counters?.Holes is not { } holes)
        {
            return new SpectralHoles { IsSearched = false };
        }

        int first = geometry.FirstHoleRow;
        int last = CutoffGeometry.LastCandidate;
        if (cutoff.IsFound && geometry.RowOfUpperEdge(cutoff.Frequency) is >= 0 and var edge)
        {
            last = Math.Min(last, edge);
        }
        else if (bandwidth.IsFound && geometry.RowOfUpperEdge(bandwidth.Frequency) is >= 0 and var content)
        {
            last = Math.Min(last, content);
        }

        var shares = new short[MeasurementLimits.Rows];
        Array.Fill(shares, MeasurementLimits.NoPower);
        long holeCells = 0;
        long cells = 0;
        for (int r = first; r <= CutoffGeometry.LastCandidate; r++)
        {
            (int withSignal, int count) = Frames(counters, r, holes, countsSilence: false);
            if (withSignal > 0)
            {
                shares[r] = (short)(((2000L * count) + withSignal) / (2L * withSignal));
            }

            if (r <= last)
            {
                holeCells += count;
                cells += withSignal;
            }
        }

        bool isEmpty = last < first;
        return new SpectralHoles
        {
            HoleCells = holeCells,
            CellsWithSignal = cells,
            FromFrequency = isEmpty ? 0 : geometry.Hertz(first),
            ToFrequency = isEmpty ? 0 : geometry.Hertz(last + 1),
            RowShares = shares,
        };
    }

    // The frames at a row: the reference level is the highest level that at least 1 % of the row's frames reach; frames with signal are
    // those at most ten levels (30.1 dB) below it; the second figure counts those that show their own step there.
    // countsSilence false (the holes): a row whose reference level is the lowest cell (no power, or less than 2^-63) has no frames with signal, as a side signal that is
    // exactly 0 has none; the cutoff and the partial cutoff keep counting them.
    private static (int WithSignal, int Showing) Frames(CutoffCounters counters, int row, int[] shows, bool countsSilence = true) => Frames(counters.All, row, shows, countsSilence);

    internal static (int WithSignal, int Showing) Frames(int[] all, int row, int[] shows, bool countsSilence = true)
    {
        int baseIndex = row * CutoffCounters.Levels;
        long total = 0;
        for (int l = 0; l < CutoffCounters.Levels; l++)
        {
            total += all[baseIndex + l];
        }

        int withSignal = 0;
        int showing = 0;
        if (total > 0)
        {
            long cumulative = 0;
            int reference = 0;
            for (int l = CutoffCounters.Levels - 1; l >= 0; l--)
            {
                cumulative += all[baseIndex + l];
                if (cumulative * 100 >= total)
                {
                    reference = l;
                    break;
                }
            }

            if (reference == 0 && !countsSilence)
            {
                return (0, 0);
            }

            for (int l = Math.Max(0, reference - 10); l < CutoffCounters.Levels; l++)
            {
                withSignal += all[baseIndex + l];
                showing += shows[baseIndex + l];
            }
        }

        return (withSignal, showing);
    }

    internal static long FloorDivide(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }
}
