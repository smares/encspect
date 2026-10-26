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

    public static long Bytes => 2L * MeasurementLimits.Rows * Levels * sizeof(int);
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
            int high = Math.Min(r + w, rowCount - 1);
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

        // The frames: the reference level at the step's row is the highest level that at least 1 % of its frames reach; frames with
        // signal are those at most ten levels (30.1 dB) below it.
        int baseIndex = best * CutoffCounters.Levels;
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
                showing += counters.Show[baseIndex + l];
            }
        }

        int stepHertz = geometry.Hertz(best + 1);
        bool isFound = stepTenths >= StepThresholdTenths && withSignal > 0 && 2L * showing >= withSignal;
        if (!isFound)
        {
            return new SpectralCutoff { StepFrequency = stepHertz, StepTenthsOfDb = stepTenths, FramesShowing = showing, FramesWithSignal = withSignal };
        }

        // The edge: the highest row, from the step's row up, more than 6 dB above the mean level of everything above the step.
        int aboveCount = rowCount - 1 - best;
        long aboveSum = prefix[rowCount] - prefix[best + 1];
        int edge = best;
        for (int r = rowCount - 1; r >= best; r--)
        {
            if ((long)level[r] * aboveCount > aboveSum + (60L * aboveCount))
            {
                edge = r;
                break;
            }
        }

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

    private static long FloorDivide(long a, long b)
    {
        long q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }
}
