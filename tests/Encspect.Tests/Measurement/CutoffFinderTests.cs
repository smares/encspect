using Encspect.Core;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// What the edge of the spectral cutoff means, on spectra made row by row: it is where the frames' own steps end, found by walking up from
// the step, and never below the step.
public class CutoffFinderTests
{
    private const int Rate = 44100;
    private const int Size = 4096;
    private const double Pass = 1e-2;
    private const double Floor = 1e-12;

    private static readonly CutoffGeometry Geometry = new(Rate, Size);

    // The rows of one frame in power: the pass level up to the row before edge, the floor from it, plus tones (row, power).
    private static double[] Frame(int edge, params (int Row, double Power)[] tones)
    {
        double[] power = new double[MeasurementLimits.Rows];
        for (int r = 0; r < power.Length; r++)
        {
            power[r] = r < edge ? Pass : Floor;
        }

        foreach ((int row, double tone) in tones)
        {
            power[row] += tone;
        }

        return power;
    }

    // The average spectrum in tenths of a dB and the counters, as the measurer makes them.
    private static SpectralCutoff Find(IReadOnlyList<double[]> frames)
    {
        var counters = new CutoffCounters();
        int w = Geometry.BandRows;
        double[] mean = new double[MeasurementLimits.Rows];
        foreach (double[] power in frames)
        {
            double[] suffix = new double[MeasurementLimits.Rows + 2];
            for (int r = CutoffGeometry.TopRow; r >= 0; r--)
            {
                suffix[r] = suffix[r + 1] + power[r];
            }

            for (int r = Geometry.FirstCandidate; r <= CutoffGeometry.LastCandidate; r++)
            {
                int high = Math.Min(r + w, CutoffGeometry.TopRow);
                double under = suffix[r - w + 1] - suffix[r + 1];
                double over = suffix[r + 1] - suffix[high + 1];
                int level = Math.Clamp(Math.ILogB(under / w), CutoffCounters.FirstExponent, CutoffCounters.FirstExponent + CutoffCounters.Levels - 1) - CutoffCounters.FirstExponent;
                counters.All[(r * CutoffCounters.Levels) + level]++;
                if (under * (high - r) >= CutoffCounters.OwnStepRatio * over * w)
                {
                    counters.Show[(r * CutoffCounters.Levels) + level]++;
                }
            }

            for (int r = 0; r < mean.Length; r++)
            {
                mean[r] += power[r] / frames.Count;
            }
        }

        short[] rows = [.. mean.Select(p => (short)Math.Floor((100 * Math.Log10(p)) + 0.5))];
        return CutoffFinder.Find(rows, counters, Geometry);
    }

    private static IReadOnlyList<double[]> Repeat(int count, Func<int, double[]> frame) => [.. Enumerable.Range(0, count).Select(frame)];

    [Fact]
    public void ABrickwallReadsAtItsEdgeAndTheCutoffIsNeverBelowTheStep()
    {
        SpectralCutoff cutoff = Find(Repeat(20, _ => Frame(360)));

        Assert.True(cutoff.IsFound);
        Assert.Equal(Geometry.Hertz(360), cutoff.Frequency);
        Assert.True(cutoff.Frequency >= cutoff.StepFrequency);
    }

    // The search walks up from the step and stops at the first row without the step, so a tone far above the edge does not set it.
    [Fact]
    public void AToneAboveTheEdgeDoesNotSetTheCutoff()
    {
        SpectralCutoff plain = Find(Repeat(20, _ => Frame(360)));
        SpectralCutoff toned = Find(Repeat(20, _ => Frame(360, (440, 1e-6))));

        Assert.True(toned.IsFound);
        Assert.Equal(plain.Frequency, toned.Frequency);
        Assert.InRange(toned.Frequency, 1, Geometry.Hertz(380));
    }

    // Within W rows of row 505 the walk ends at the last candidate and the shift would fall below the step: the clamp to the step decides.
    [Fact]
    public void AnEdgeWithinOneBandOfTheTopReadsAsTheStep()
    {
        SpectralCutoff cutoff = Find(Repeat(20, _ => Frame(496)));

        Assert.True(cutoff.IsFound);
        Assert.Equal(cutoff.StepFrequency, cutoff.Frequency);
    }

    // One frame whose cut splatters above the edge lifts the average spectrum there, but 19 of 20 frames still end their steps at the edge.
    [Fact]
    public void OneSplatterFrameDoesNotMoveTheCutoff()
    {
        double[] splatter = Frame(360, [.. Enumerable.Range(361, 80).Select(r => (r, 1e-7))]);
        SpectralCutoff plain = Find(Repeat(20, _ => Frame(360)));
        SpectralCutoff splattered = Find(Repeat(20, i => i == 7 ? splatter : Frame(360)));

        Assert.True(splattered.IsFound);
        Assert.Equal(plain.Frequency, splattered.Frequency);
    }

    // The average spectrum in tenths of a dB of a stand-in: a level for each row.
    private static short[] Rows(Func<int, int> tenths) => [.. Enumerable.Range(0, MeasurementLimits.Rows).Select(r => (short)tenths(r))];

    // A floor that reaches the top of the search: the bandwidth is the lowest row of the run within 3 dB of it, which is the edge of the content
    // (the run's lowest band ends one band above the edge: the shift by W - 1 rows of the cutoff's edge), and the floor is the mean of the top band.
    [Fact]
    public void AContentEdgeOnAFlatFloorIsTheBandwidth()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Rows(r => r < 400 ? -300 : -1300), Geometry);

        Assert.True(bandwidth.IsFound);
        Assert.Equal(Geometry.Hertz(400), bandwidth.Frequency);
        Assert.Equal(-1300, bandwidth.FloorTenthsOfDb);
        Assert.Equal(SpectralBandwidthReason.None, bandwidth.Reason);
    }

    // A tone above the content lifts a band of 23 rows by a twenty-third of its height in dB: it does not end the run.
    [Fact]
    public void AToneInTheFloorDoesNotEndTheRun()
    {
        SpectralBandwidth plain = CutoffFinder.FindBandwidth(Rows(r => r < 400 ? -300 : -1300), Geometry);
        SpectralBandwidth toned = CutoffFinder.FindBandwidth(Rows(r => r < 400 ? -300 : r == 450 ? -800 : -1300), Geometry);

        Assert.True(toned.IsFound);
        Assert.Equal(plain.Frequency, toned.Frequency);
    }

    [Fact]
    public void AnEdgeIsFoundWhateverTheFloorsLevel()
    {
        foreach (int floor in new[] { -1900, -1300, -700 })
        {
            Assert.True(CutoffFinder.FindBandwidth(Rows(r => r < 300 ? floor + 400 : floor), Geometry).IsFound, floor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Rows without power read -200 dB.
        SpectralBandwidth silent = CutoffFinder.FindBandwidth(Rows(r => r < 300 ? -300 : MeasurementLimits.NoPower), Geometry);
        Assert.True(silent.IsFound);
        Assert.Equal(-2000, silent.FloorTenthsOfDb);
    }

    // A spectrum flat from the top down to the start of the search has no edge: it reaches the top.
    [Fact]
    public void AFlatSpectrumReachesTheTop()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Rows(_ => -500), Geometry);

        Assert.False(bandwidth.IsFound);
        Assert.Equal(SpectralBandwidthReason.ReachesTop, bandwidth.Reason);
        Assert.Equal(0, bandwidth.Frequency);
    }

    // A slope of 0.1 dB a row (pink noise falls about 3 dB an octave): the run within 3 dB of the top band is shorter than the slope, and the band under
    // it is not 20 dB above the floor.
    [Fact]
    public void ASlopeIsNotAFloor()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Rows(r => -1 * r), Geometry);

        Assert.False(bandwidth.IsFound);
        Assert.Equal(SpectralBandwidthReason.Slope, bandwidth.Reason);
    }

    // The thresholds are 3 dB for the run and 20 dB for the step, both inclusive, in sums of integers: exact.
    [Fact]
    public void TheStepThresholdOf20DbIsInclusive()
    {
        Assert.True(CutoffFinder.FindBandwidth(Rows(r => r < 300 ? -1100 : -1300), Geometry).IsFound);
        Assert.Equal(SpectralBandwidthReason.Slope, CutoffFinder.FindBandwidth(Rows(r => r < 300 ? -1101 : -1300), Geometry).Reason);
    }

    [Fact]
    public void ARateUnder8000HertzIsNotSearchedForABandwidthOrAPartialCutoff()
    {
        var low = new CutoffGeometry(4000, 1024);

        Assert.False(CutoffFinder.FindBandwidth(Rows(_ => -500), low).IsSearched);
        Assert.False(CutoffFinder.FindPartial(new CutoffCounters(hasCeiling: true), low).IsSearched);
        Assert.False(CutoffFinder.FindPartial(null, Geometry).IsSearched);
    }

    // Counters with the frames of each row: frames at the level of the row's signal, of which some show a ceiling.
    private static CutoffCounters Ceilings(int frames, params (int Row, int Showing)[] rows)
    {
        var counters = new CutoffCounters(hasCeiling: true);
        for (int r = Geometry.FirstCandidate; r <= CutoffGeometry.LastCandidate; r++)
        {
            counters.All[(r * CutoffCounters.Levels) + 40] = frames;
        }

        foreach ((int row, int showing) in rows)
        {
            counters.Ceiling![(row * CutoffCounters.Levels) + 40] = showing;
        }

        return counters;
    }

    // The row with the most frames showing, the lowest on a tie; the count is that maximum, the frequency the upper edge of the row.
    [Fact]
    public void ThePartialCutoffIsTheRowWithTheMostFramesTheLowestOnATie()
    {
        PartialCutoff partial = CutoffFinder.FindPartial(Ceilings(20, (370, 10), (371, 10), (372, 10), (373, 9)), Geometry);

        Assert.True(partial.IsFound);
        Assert.Equal(Geometry.Hertz(371), partial.Frequency);
        Assert.Equal(10, partial.FramesShowing);
        Assert.Equal(20, partial.FramesWithSignal);
    }

    // Walking down from the row with the most frames while the count stays at least half of it: the figure lands on the edge, which a plateau
    // that starts a row lower than its top has. The count stays the maximum, not the walked row's.
    [Fact]
    public void ThePartialCutoffWalksDownWhileAtLeastHalfOfTheMaximumShow()
    {
        PartialCutoff partial = CutoffFinder.FindPartial(Ceilings(20, (369, 4), (370, 5), (371, 10), (372, 10)), Geometry);

        Assert.Equal(Geometry.Hertz(371), partial.Frequency);
        Assert.Equal(10, partial.FramesShowing);
        Assert.True(partial.IsFound);

        PartialCutoff stops = CutoffFinder.FindPartial(Ceilings(20, (369, 4), (370, 4), (371, 10)), Geometry);
        Assert.Equal(Geometry.Hertz(372), stops.Frequency);
    }

    // 20 % of the frames with signal is enough, 15 % is not; the row and the count of the closest are still given.
    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public void ThePartialCutoffIsFoundFromAFifthOfTheFramesWithSignal(int showing, bool isFound)
    {
        PartialCutoff partial = CutoffFinder.FindPartial(Ceilings(20, (400, showing)), Geometry);

        Assert.Equal(isFound, partial.IsFound);
        Assert.Equal(Geometry.Hertz(401), partial.Frequency);
        Assert.Equal(showing, partial.FramesShowing);
    }

    [Fact]
    public void WithoutACeilingNothingIsFound()
    {
        PartialCutoff partial = CutoffFinder.FindPartial(Ceilings(20), Geometry);

        Assert.False(partial.IsFound);
        Assert.Equal(0, partial.Frequency);
        Assert.Equal(0, partial.FramesShowing);
    }

    // Frames without signal (more than ten levels under the one 1 % of them reach) are not counted against the ceiling, as for the cutoff.
    [Fact]
    public void FramesWithoutSignalAreNotCounted()
    {
        CutoffCounters counters = Ceilings(10, (400, 10));
        counters.All[(400 * CutoffCounters.Levels) + 5] = 100;

        PartialCutoff partial = CutoffFinder.FindPartial(counters, Geometry);

        Assert.Equal(10, partial.FramesWithSignal);
        Assert.True(partial.IsFound);
    }
}
