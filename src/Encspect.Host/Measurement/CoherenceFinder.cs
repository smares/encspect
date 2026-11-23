using Encspect.Core;

namespace Encspect.Host.Measurement;

// The per-row, per-level counters of the inter-channel coherence of the pair (docs/design/spectral-analysis.md, Step 8): for every row from the first full band
// of 1 kHz, how many frames had the pair's mean power under it at the level, and in how many of them the band was at or over the threshold.
internal sealed class CoherenceCounters
{
    public readonly int[] All = new int[MeasurementLimits.Rows * CutoffCounters.Levels];

    public readonly int[] Coherent = new int[MeasurementLimits.Rows * CutoffCounters.Levels];

    public static long Bytes => 2L * MeasurementLimits.Rows * CutoffCounters.Levels * sizeof(int);
}

// The inter-channel coherence of Step 8, from the stream's sums of the two power rows and the cross rows and from the frames' counters, in integers and in the
// comparisons of products the design lists: no logarithm and no division in a decision.
internal static class CoherenceFinder
{
    // The test of the design: 20 |C|^2 >= 17 Sxx Syy (0.85), both powers over 0. The same expression for a stream's band and a frame's.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool IsCoherent(double crossRe, double crossIm, double left, double right) =>
        left > 0 && right > 0 && 20.0 * ((crossRe * crossRe) + (crossIm * crossIm)) >= 17.0 * left * right;

    // The coherence in tenths of a percent, rounded to the nearest (halves up) from one IEEE division; NoPower where a channel has none.
    private static short Tenths(double crossRe, double crossIm, double left, double right)
    {
        if (!(left > 0) || !(right > 0))
        {
            return MeasurementLimits.NoPower;
        }

        double c = (((crossRe * crossRe) + (crossIm * crossIm)) / left) / right;
        return (short)Math.Clamp(Math.Floor((1000.0 * c) + 0.5), 0.0, 1000.0);
    }

    public static CoherenceFigures Find(
        CutoffGeometry geometry,
        int frames,
        ReadOnlySpan<double> left,
        ReadOnlySpan<double> right,
        ReadOnlySpan<double> crossRe,
        ReadOnlySpan<double> crossIm,
        CoherenceCounters? counters,
        SpectralCutoff cutoff,
        SpectralBandwidth bandwidth,
        int leftChannel,
        int rightChannel)
    {
        if (!geometry.IsSearched || counters is null)
        {
            return new CoherenceFigures { IsSearched = false, LeftChannel = leftChannel, RightChannel = rightChannel };
        }

        int rowCount = MeasurementLimits.Rows;
        int w = geometry.BandRows;
        int top = CutoffGeometry.TopRow;
        var rows = new short[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            rows[r] = Tenths(crossRe[r], crossIm[r], left[r], right[r]);
        }

        // Suffix sums from the top row of the search down, as the counters form them: a band is a difference of two.
        double[] sl = new double[rowCount + 1];
        double[] sr = new double[rowCount + 1];
        double[] scr = new double[rowCount + 1];
        double[] sci = new double[rowCount + 1];
        sl[top + 1] = 0;
        sr[top + 1] = 0;
        scr[top + 1] = 0;
        sci[top + 1] = 0;
        for (int r = top; r >= 0; r--)
        {
            sl[r] = sl[r + 1] + left[r];
            sr[r] = sr[r + 1] + right[r];
            scr[r] = scr[r + 1] + crossRe[r];
            sci[r] = sci[r + 1] + crossIm[r];
        }

        // The band of W rows that ends at row e.
        bool Band(int e) => IsCoherent(scr[e - w + 1] - scr[e + 1], sci[e - w + 1] - sci[e + 1], sl[e - w + 1] - sl[e + 1], sr[e - w + 1] - sr[e + 1]);

        // The range is the holes': to the cutoff's edge where found, else the spectral bandwidth's content, else row 505.
        int last = CutoffGeometry.LastCandidate;
        if (cutoff.IsFound && geometry.RowOfUpperEdge(cutoff.Frequency) is >= 0 and var edge)
        {
            last = Math.Min(last, edge);
        }
        else if (bandwidth.IsFound && geometry.RowOfUpperEdge(bandwidth.Frequency) is >= 0 and var content)
        {
            last = Math.Min(last, content);
        }

        var shares = new short[rowCount];
        Array.Fill(shares, MeasurementLimits.NoPower);
        int firstRow = w - 1;
        for (int r = firstRow; r <= CutoffGeometry.LastCandidate; r++)
        {
            (int withSignal, int coherent) = CutoffFinder.Frames(counters.All, r, counters.Coherent);
            if (withSignal > 0)
            {
                shares[r] = (short)(((2000L * coherent) + withSignal) / (2L * withSignal));
            }
        }

        var figures = new CoherenceFigures { LeftChannel = leftChannel, RightChannel = rightChannel, Frames = frames, Rows = rows, Shares = shares };
        if (frames < MeasurementLimits.MinimumCoherenceFrames)
        {
            return figures with { Reason = CoherenceReason.TooFewFrames };
        }

        if (last < firstRow)
        {
            return figures with { Reason = CoherenceReason.TooShort };
        }

        short topTenths = Tenths(scr[last - w + 1] - scr[last + 1], sci[last - w + 1] - sci[last + 1], sl[last - w + 1] - sl[last + 1], sr[last - w + 1] - sr[last + 1]);
        int topBand = topTenths == MeasurementLimits.NoPower ? 0 : topTenths;
        if (!Band(last))
        {
            return figures with { Reason = CoherenceReason.NotCoherent, TopBandTenths = topBand };
        }

        // The run walks down one row at a time while the band under the next row is coherent; every band of the run is.
        int end = last;
        while (end - 1 >= firstRow && Band(end - 1))
        {
            end--;
        }

        if (end == firstRow)
        {
            return figures with { Reason = CoherenceReason.ReachesStart, TopBandTenths = topBand };
        }

        // The frames of the run's first full band: the band of W rows that starts at the start frequency, which ends at row end.
        (int withSignalFrames, int showingFrames) = CutoffFinder.Frames(counters.All, end, counters.Coherent);
        return figures with
        {
            IsFound = true,
            FromFrequency = geometry.Hertz(end - w + 1),
            ToFrequency = geometry.Hertz(last + 1),
            TopBandTenths = topBand,
            FramesWithSignal = withSignalFrames,
            FramesShowing = showingFrames,
        };
    }
}
