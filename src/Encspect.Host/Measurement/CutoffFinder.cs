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

    // Frames whose band under the row stands 25 dB above the mean power of everything above it, up to the top of the search; kept for
    // the combined spectrum only (the partial cutoff, docs/design/spectral-analysis.md, Step 7 (b)). At the cell of All.
    public readonly int[]? Ceiling;

    // 10^(25 / 10): a frame shows a ceiling at a row when the mean power of the band under it is at least this times the mean power of
    // every row above it.
    public const double CeilingRatio = 316.22776601683796;

    public CutoffCounters(bool hasCeiling = false)
    {
        if (hasCeiling)
        {
            Ceiling = new int[MeasurementLimits.Rows * Levels];
        }
    }

    public static long Bytes => 2L * MeasurementLimits.Rows * Levels * sizeof(int);

    public static long CeilingBytes => (long)MeasurementLimits.Rows * Levels * sizeof(int);
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

    // The frequency in whole hertz of the boundary above row (boundary - 1): boundary * BinsPerRow * SampleRate / FftSize, rounded.
    public int Hertz(int boundary) => (int)(((2L * boundary * BinsPerRow * SampleRate) + FftSize) / (2L * FftSize));
}

internal static class CutoffFinder
{
    // The level of a row without power in the search, in tenths of a dB: -200 dB.
    private const int SilentTenths = -2000;

    private const int StepThresholdTenths = 250;

    // The floor's run stays within 3 dB of the floor; the band under it must be 20 dB above it.
    private const int BandwidthToleranceTenths = 30;

    private const int BandwidthStepTenths = 200;

    public static SpectralCutoff Find(ReadOnlySpan<short> rows, CutoffCounters? counters, CutoffGeometry geometry)
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
            level[r] = rows[r] == MeasurementLimits.NoPower ? SilentTenths : rows[r];
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

        int stepTenths = (int)FloorDivide((2 * bestNumerator) + bestDenominator, 2 * bestDenominator);

        (int withSignal, int showing) = Frames(counters, best, counters.Show);

        int stepHertz = geometry.Hertz(best + 1);
        bool isFound = stepTenths >= StepThresholdTenths && withSignal > 0 && 2L * showing >= withSignal;
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
        for (int r = best; r >= 0 && (long)level[r] * w < underSum - (30L * w); r--)
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
    public static SpectralBandwidth FindBandwidth(ReadOnlySpan<short> rows, CutoffGeometry geometry)
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
            prefix[r + 1] = prefix[r] + (rows[r] == MeasurementLimits.NoPower ? SilentTenths : rows[r]);
        }

        long floorSum = prefix[top + 1] - prefix[top - w + 1];
        int end = top;
        while (end - 1 >= w - 1)
        {
            long band = prefix[end] - prefix[end - w];
            if (Math.Abs(band - floorSum) > BandwidthToleranceTenths * w)
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
        if (under < floorSum + (BandwidthStepTenths * w))
        {
            return new SpectralBandwidth { Reason = SpectralBandwidthReason.Slope };
        }

        return new SpectralBandwidth
        {
            IsFound = true,
            Frequency = geometry.Hertz(lowest),
            FloorTenthsOfDb = (int)FloorDivide((2 * floorSum) + w, 2L * w),
        };
    }

    // The partial cutoff of Step 7 (b): over the candidate rows, the one at which the most frames with signal show a ceiling (the lowest
    // on a tie), then the lowest row below it, walking down, whose count stays at least half of that maximum, which gives the frequency (so
    // it lands on the edge and not up to W - 1 rows above it); the count is that maximum, and the figure is found where at least 20 % of the
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
        bool isFound = withSignal > 0 && 5L * shown >= withSignal;
        return new PartialCutoff { IsFound = isFound, Frequency = shown > 0 ? geometry.Hertz(row + 1) : 0, FramesShowing = shown, FramesWithSignal = withSignal };
    }

    // The frames at a row: the reference level is the highest level that at least 1 % of the row's frames reach; frames with signal are
    // those at most ten levels (30.1 dB) below it; the second figure counts those that show their own step there.
    private static (int WithSignal, int Showing) Frames(CutoffCounters counters, int row, int[] shows)
    {
        int baseIndex = row * CutoffCounters.Levels;
        long total = 0;
        for (int l = 0; l < CutoffCounters.Levels; l++)
        {
            total += counters.All[baseIndex + l];
        }

        int withSignal = 0;
        int showing = 0;
        if (total > 0)
        {
            long cumulative = 0;
            int reference = 0;
            for (int l = CutoffCounters.Levels - 1; l >= 0; l--)
            {
                cumulative += counters.All[baseIndex + l];
                if (cumulative * 100 >= total)
                {
                    reference = l;
                    break;
                }
            }

            for (int l = Math.Max(0, reference - 10); l < CutoffCounters.Levels; l++)
            {
                withSignal += counters.All[baseIndex + l];
                showing += shows[baseIndex + l];
            }
        }

        return (withSignal, showing);
    }

    private static long FloorDivide(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }
}
