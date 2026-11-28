using Encspect.Core;

namespace Encspect.Host.Measurement;

// The Nyquist edge of docs/design/spectral-analysis.md, Step 10: for the Nyquist frequency N of each lower common sample rate, a step of the
// combined spectrum (the cutoff's, on 1 kHz bands) whose peak lies at most 3 % under N and at most a row over it, with a fall above N of
// at most a quarter of the step, found in at least half of the frames with signal. Everything is integer arithmetic on the rows in thousandths
// of a dB, as the cutoff's: sums are long, steps are fractions compared by cross products.
internal static class NyquistEdgeFinder
{
    // The rates whose halves are tested, highest first (decision S36).
    private static readonly int[] Rates = [192_000, 176_400, 96_000, 88_200, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000];

    // The step must reach the cutoff's 25 dB.
    private const int StepThreshold = 25_000;

    // N x (1 - 3 %): the lowest boundary of the window is where 200 f >= 97 rate.
    private const int ToleranceNumerator = 97;

    private const int ToleranceDenominator = 200;

    public static bool IsSearched(CutoffGeometry geometry, CutoffCounters? counters) => geometry.IsSearched && counters is not null;

    public static IReadOnlyList<NyquistEdge> Find(ReadOnlySpan<int> rows, CutoffCounters? counters, CutoffGeometry geometry)
    {
        if (!IsSearched(geometry, counters))
        {
            return [];
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        Span<long> prefix = stackalloc long[rowCount + 1];
        for (int r = 0; r < rowCount; r++)
        {
            prefix[r + 1] = prefix[r] + (rows[r] == CutoffFinder.NoPower ? -200_000 : rows[r]);
        }

        var edges = new List<NyquistEdge>();
        foreach (int rate in Rates)
        {
            if (rate >= geometry.SampleRate)
            {
                continue;
            }

            if (Test(prefix, counters!, geometry, w, rate) is { } edge)
            {
                edges.Add(edge);
            }
        }

        return edges;
    }

    private static NyquistEdge? Test(ReadOnlySpan<long> prefix, CutoffCounters counters, CutoffGeometry geometry, int w, int rate)
    {
        const int top = CutoffGeometry.TopRow;
        long binsRate = (long)geometry.BinsPerRow * geometry.SampleRate;
        long size = geometry.FftSize;

        // The first row above N (its lower edge is at or over N): ceil(N / row width), N = rate / 2. It must leave half a band to the top.
        long first = (((long)rate * size) + (2 * binsRate) - 1) / (2 * binsRate);
        if (first + ((w + 1) / 2) - 1 > top)
        {
            return null;
        }

        int a0 = (int)first;
        int a1 = Math.Min(a0 + w - 1, top);

        // The window: boundaries b = k + 1 with 200 f >= 97 rate (3 % under N) up to the first boundary above N, f <= N + row width.
        int best = -1;
        long bestNumerator = 0;
        long bestDenominator = 1;
        for (int k = geometry.FirstCandidate; k <= CutoffGeometry.LastCandidate; k++)
        {
            long boundary = k + 1;
            if (ToleranceDenominator * boundary * binsRate < ToleranceNumerator * (long)rate * size)
            {
                continue;
            }

            if (2 * (boundary - 1) * binsRate > (long)rate * size)
            {
                break;
            }

            (long numerator, long denominator) = Step(prefix, k, w);
            if (best < 0 || numerator * bestDenominator > bestNumerator * denominator)
            {
                best = k;
                bestNumerator = numerator;
                bestDenominator = denominator;
            }
        }

        if (best < 0 || bestNumerator < StepThreshold * bestDenominator)
        {
            return null;
        }

        // A local maximum: smaller at the boundary under, not larger at the boundary over (both outside the window where the window ends there).
        if (best - 1 < w - 1)
        {
            return null;
        }

        (long underNumerator, long underDenominator) = Step(prefix, best - 1, w);
        if (underNumerator * bestDenominator >= bestNumerator * underDenominator)
        {
            return null;
        }

        if (best + 1 < top)
        {
            (long overNumerator, long overDenominator) = Step(prefix, best + 1, w);
            if (overNumerator * bestDenominator > bestNumerator * overDenominator)
            {
                return null;
            }
        }

        // No further fall above N: the mean of the W rows from the first row above N against the mean of the next W rows, at most a quarter of the step.
        int count = a1 - a0 + 1;
        long above = prefix[a1 + 1] - prefix[a0];
        int nextFirst = a1 + 1;
        if (nextFirst <= top)
        {
            int nextLast = Math.Min(nextFirst + w - 1, top);
            int nextCount = nextLast - nextFirst + 1;
            long next = prefix[nextLast + 1] - prefix[nextFirst];
            long fallNumerator = (above * nextCount) - (next * count);
            long fallDenominator = (long)count * nextCount;
            if (4 * fallNumerator * bestDenominator > bestNumerator * fallDenominator)
            {
                return null;
            }
        }

        // The band the one above mirrors: the rows whose span lies under the mirror of the band's lower edge, from the highest down.
        int mirrorHigh = (int)((((long)rate * size) / binsRate) - a0 - 1);
        int mirrorLow = mirrorHigh - (a1 - a0);
        if (mirrorLow < 0)
        {
            return null;
        }

        (int withSignal, int showing) = CutoffFinder.Frames(counters.All, best, counters.Show);
        if (withSignal == 0 || 2L * showing < withSignal)
        {
            return null;
        }

        long mirror = prefix[mirrorHigh + 1] - prefix[mirrorLow];
        return new NyquistEdge
        {
            LowerRate = rate,
            StepFrequency = geometry.Hertz(best + 1),
            StepTenthsOfDb = CutoffFinder.Tenths(bestNumerator, bestDenominator),
            AboveTenthsOfDb = CutoffFinder.Tenths(above - mirror, count),
            FramesShowing = showing,
            FramesWithSignal = withSignal,
        };
    }

    // The cutoff's step at the boundary above row k: the mean of the W rows under it against the mean of the W rows over it, clipped at the top row.
    private static (long Numerator, long Denominator) Step(ReadOnlySpan<long> prefix, int k, int w)
    {
        int high = Math.Min(k + w, CutoffGeometry.TopRow);
        int above = high - k;
        long under = prefix[k + 1] - prefix[k - w + 1];
        long over = prefix[high + 1] - prefix[k + 1];
        return ((under * above) - (over * w), (long)w * above);
    }
}
