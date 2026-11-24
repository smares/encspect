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

        int[] rows = [.. mean.Select(p => (int)Math.Floor((10000 * Math.Log10(p)) + 0.5))];
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

    // The searches take thousandths of a dB; the stand-ins here are given in tenths.
    private static int[] Fine(short[] tenths) => [.. tenths.Select(t => t == MeasurementLimits.NoPower ? CutoffFinder.NoPower : t * 100)];

    // A floor that reaches the top of the search: the bandwidth is the lowest row of the run within 3 dB of it, which is the edge of the content
    // (the run's lowest band ends one band above the edge: the shift by W - 1 rows of the cutoff's edge), and the floor is the mean of the top band.
    [Fact]
    public void AContentEdgeOnAFlatFloorIsTheBandwidth()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Fine(Rows(r => r < 400 ? -300 : -1300)), Geometry);

        Assert.True(bandwidth.IsFound);
        Assert.Equal(Geometry.Hertz(400), bandwidth.Frequency);
        Assert.Equal(-1300, bandwidth.FloorTenthsOfDb);
        Assert.Equal(SpectralBandwidthReason.None, bandwidth.Reason);
    }

    // A tone above the content lifts a band of 23 rows by a twenty-third of its height in dB: it does not end the run.
    [Fact]
    public void AToneInTheFloorDoesNotEndTheRun()
    {
        SpectralBandwidth plain = CutoffFinder.FindBandwidth(Fine(Rows(r => r < 400 ? -300 : -1300)), Geometry);
        SpectralBandwidth toned = CutoffFinder.FindBandwidth(Fine(Rows(r => r < 400 ? -300 : r == 450 ? -800 : -1300)), Geometry);

        Assert.True(toned.IsFound);
        Assert.Equal(plain.Frequency, toned.Frequency);
    }

    [Fact]
    public void AnEdgeIsFoundWhateverTheFloorsLevel()
    {
        foreach (int floor in new[] { -1900, -1300, -700 })
        {
            Assert.True(CutoffFinder.FindBandwidth(Fine(Rows(r => r < 300 ? floor + 400 : floor)), Geometry).IsFound, floor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Rows without power read -200 dB.
        SpectralBandwidth silent = CutoffFinder.FindBandwidth(Fine(Rows(r => r < 300 ? -300 : MeasurementLimits.NoPower)), Geometry);
        Assert.True(silent.IsFound);
        Assert.Equal(-2000, silent.FloorTenthsOfDb);
    }

    // A spectrum flat from the top down to the start of the search has no edge: it reaches the top.
    [Fact]
    public void AFlatSpectrumReachesTheTop()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Fine(Rows(_ => -500)), Geometry);

        Assert.False(bandwidth.IsFound);
        Assert.Equal(SpectralBandwidthReason.ReachesTop, bandwidth.Reason);
        Assert.Equal(0, bandwidth.Frequency);
    }

    // A slope of 0.1 dB a row (pink noise falls about 3 dB an octave): the run within 3 dB of the top band is shorter than the slope, and the band under
    // it is not 20 dB above the floor.
    [Fact]
    public void ASlopeIsNotAFloor()
    {
        SpectralBandwidth bandwidth = CutoffFinder.FindBandwidth(Fine(Rows(r => -1 * r)), Geometry);

        Assert.False(bandwidth.IsFound);
        Assert.Equal(SpectralBandwidthReason.Slope, bandwidth.Reason);
    }

    // The thresholds are 3 dB for the run and 20 dB for the step, both inclusive, in sums of integers: exact.
    [Fact]
    public void TheStepThresholdOf20DbIsInclusive()
    {
        Assert.True(CutoffFinder.FindBandwidth(Fine(Rows(r => r < 300 ? -1100 : -1300)), Geometry).IsFound);
        Assert.Equal(SpectralBandwidthReason.Slope, CutoffFinder.FindBandwidth(Fine(Rows(r => r < 300 ? -1101 : -1300)), Geometry).Reason);
    }

    // The mean of the W rows that end at a row, in tenths of a dB, as a fraction over W: the sum (the reference of the slope tests, written with loops and no prefix sums).
    private static long BandSum(short[] rows, int end, int w)
    {
        long sum = 0;
        for (int r = end - w + 1; r <= end; r++)
        {
            sum += rows[r] == MeasurementLimits.NoPower ? -2000 : rows[r];
        }

        return sum;
    }

    // The slope worked out by the design's text in integers on rationals (Step 7b), for the shapes below.
    private static SpectralSlope ReferenceSlope(short[] rows, CutoffGeometry geometry)
    {
        int w = geometry.BandRows;
        long top = BandSum(rows, 506, w);
        int row = 506 - w;
        long startSum = BandSum(rows, row, w);
        int topStep = (int)Math.Floor(((double)(startSum - top) / w) + 0.5);
        if (startSum - top < 50 * w)
        {
            return new SpectralSlope { TopStepTenthsOfDb = topStep };
        }

        while (row - 1 >= geometry.FirstCandidate && BandSum(rows, row - 1, w) - BandSum(rows, row - 1 + w, w) >= 30 * w)
        {
            row--;
        }

        long fall = BandSum(rows, row, w) - top;
        long distance = (2L * (506 - row)) - w;
        decimal perKilohertz = (decimal)fall * 2048000m / ((decimal)w * distance * geometry.SampleRate);
        return new SpectralSlope
        {
            IsFound = true,
            FromFrequency = geometry.Hertz(row + 1),
            ToFrequency = geometry.Hertz(507),
            FallTenthsOfDb = (int)Math.Floor((fall / (decimal)w) + 0.5m),
            TenthsOfDbPerKilohertz = (int)Math.Floor(perKilohertz + 0.5m),
            TopStepTenthsOfDb = topStep,
        };
    }

    private static void AssertSlope(SpectralSlope expected, SpectralSlope actual)
    {
        Assert.True(actual.IsSearched);
        Assert.Equal(expected.IsFound, actual.IsFound);
        Assert.Equal(expected.FromFrequency, actual.FromFrequency);
        Assert.Equal(expected.ToFrequency, actual.ToFrequency);
        Assert.Equal(expected.FallTenthsOfDb, actual.FallTenthsOfDb);
        Assert.Equal(expected.TenthsOfDbPerKilohertz, actual.TenthsOfDbPerKilohertz);
        Assert.Equal(expected.TopStepTenthsOfDb, actual.TopStepTenthsOfDb);
    }

    // A fall of 0.5 dB a row stands over the 3 dB test at every row, so the run goes down to the first candidate row (2 kHz) and stops there.
    [Fact]
    public void ARunThatReachesTheFirstCandidateStopsAtIt()
    {
        short[] rows = Rows(r => -100 - (5 * r));
        SpectralSlope found = CutoffFinder.FindSlope(Fine(rows), Geometry);

        Assert.True(found.IsFound);
        Assert.Equal(Geometry.Hertz(Geometry.FirstCandidate + 1), found.FromFrequency);
        Assert.Equal(2024, found.FromFrequency);
        AssertSlope(ReferenceSlope(rows, Geometry), found);
    }

    [Fact]
    public void AFlatTopHasNoSlopeAndATopStepOfZero()
    {
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(Rows(_ => -500)), Geometry);

        Assert.True(slope.IsSearched);
        Assert.False(slope.IsFound);
        Assert.Equal(0, slope.TopStepTenthsOfDb);
        Assert.Equal(0, slope.FromFrequency);
        Assert.Equal(0, slope.TenthsOfDbPerKilohertz);
    }

    // The top band is the 23 rows 484 to 506 at this size. A band of 5.0 dB under it starts the run (inclusive, in sums of integers); 4.9 dB does not.
    // The run then goes down while the band under a row stands 3 dB over the 23 rows above it, which it does until 14 of those rows are in the
    // lower level (14 x 5.0 / 23 = 3.04 dB), at row 474: the fall of 5.0 dB from the band of rows 452 to 474 to the top band over 41 rows less 23 / 2.
    [Fact]
    public void AFallStartsAtExactlyFiveDecibels()
    {
        SpectralSlope found = CutoffFinder.FindSlope(Fine(Rows(r => r < 484 ? -500 : -550)), Geometry);

        Assert.True(found.IsFound);
        Assert.Equal(Geometry.Hertz(475), found.FromFrequency);
        Assert.Equal(Geometry.Hertz(507), found.ToFrequency);
        Assert.Equal(50, found.FallTenthsOfDb);
        Assert.Equal(50, found.TopStepTenthsOfDb);
        AssertSlope(ReferenceSlope(Rows(r => r < 484 ? -500 : -550), Geometry), found);

        SpectralSlope under = CutoffFinder.FindSlope(Fine(Rows(r => r < 484 ? -500 : -549)), Geometry);

        Assert.False(under.IsFound);
        Assert.Equal(49, under.TopStepTenthsOfDb);
        Assert.Equal(0, under.FallTenthsOfDb);
    }

    // A linear fall from a knee: the fall at the middle of the top band is the construction's, 1 dB a row from row 400.
    [Fact]
    public void ALinearFallFromAKneeGivesItsFallAndSlope()
    {
        short[] rows = Rows(r => r < 400 ? -300 : -300 - (10 * (r - 400)));
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(rows), Geometry);

        Assert.True(slope.IsFound);
        AssertSlope(ReferenceSlope(rows, Geometry), slope);

        // 1 dB a row is 1 / 0.04307 kHz = 23.2 dB per kHz; the band means measure it from the knee to the top band, less the knee's share.
        Assert.InRange(slope.TenthsOfDbPerKilohertz, 200, 232);
        Assert.InRange(slope.FromFrequency, Geometry.Hertz(380), Geometry.Hertz(402));
    }

    public static TheoryData<string> Shapes() => new()
    {
        "flat", "tilt", "knee", "dip", "reaches-first-candidate", "skirt", "rise", "edge-near-top", "edge-within-a-band", "steep-ramp", "silent-top", "sparse",
    };

    private static short[] Shape(string name) => name switch
    {
        "flat" => Rows(_ => -500),
        // Pink noise falls about 0.2 dB per kHz at 20 kHz: 0.01 dB a row, which is far under the start.
        "tilt" => Rows(r => -300 - (r / 4)),
        "knee" => Rows(r => r < 440 ? -300 : -300 - (7 * (r - 440))),
        // A run that dips under 3 dB and resumes: a plateau of 30 rows in the middle of the fall ends it.
        "dip" => Rows(r => r < 300 ? -300 : r < 380 ? -300 - (8 * (r - 300)) : r < 420 ? -940 : -940 - (9 * (r - 420))),
        // A fall from the first row: the run reaches the first candidate.
        "reaches-first-candidate" => Rows(r => -(6 * r)),
        // A skirt: a fast fall above an edge that flattens toward the floor.
        "skirt" => Rows(r => r < 400 ? -300 : r < 410 ? -300 - (50 * (r - 400)) : -800 - ((r - 410) / 8)),
        // A rise at the top: a negative top step.
        "rise" => Rows(r => r < 484 ? -500 : -450),
        // An edge within 1 kHz of the top, and within one band of the start.
        "edge-near-top" => Rows(r => r < 490 ? -300 : -1300),
        "edge-within-a-band" => Rows(r => r < 470 ? -300 : -1300),
        "steep-ramp" => Rows(r => r < 400 ? -300 : -300 - (40 * (r - 400))),
        "silent-top" => Rows(r => r < 470 ? -300 : MeasurementLimits.NoPower),
        "sparse" => Rows(r => r % 8 == 0 && r < 480 ? -200 : MeasurementLimits.NoPower),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheSlopeEqualsTheReferenceAtEveryFrameSize(string shape)
    {
        short[] rows = Shape(shape);
        foreach ((int rate, int size) in new[] { (8000, 1024), (22050, 2048), (44100, 4096), (48000, 4096), (96000, 8192), (192000, 16384) })
        {
            var geometry = new CutoffGeometry(rate, size);
            AssertSlope(ReferenceSlope(rows, geometry), CutoffFinder.FindSlope(Fine(rows), geometry));
        }
    }

    [Fact]
    public void ARiseAtTheTopHasANegativeTopStep()
    {
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(Shape("rise")), Geometry);

        Assert.False(slope.IsFound);
        Assert.Equal(-50, slope.TopStepTenthsOfDb);
    }

    // A fall that reaches the first candidate (2 kHz) goes no lower: its start is the upper edge of that row.
    [Fact]
    public void ARunStopsAtTheFirstCandidate()
    {
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(Shape("reaches-first-candidate")), Geometry);

        Assert.True(slope.IsFound);
        Assert.Equal(Geometry.Hertz(Geometry.FirstCandidate + 1), slope.FromFrequency);
    }

    // A plateau in the middle of a fall ends the run at the plateau: nothing under it is counted.
    [Fact]
    public void ARunThatDipsUnderThreeDecibelsStopsAtTheDip()
    {
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(Shape("dip")), Geometry);

        Assert.True(slope.IsFound);
        Assert.True(slope.FromFrequency > Geometry.Hertz(380), $"from {slope.FromFrequency}");
    }

    [Fact]
    public void ARateUnder8000HertzIsNotSearchedForASlope()
    {
        SpectralSlope slope = CutoffFinder.FindSlope(Fine(Rows(r => -(6 * r))), new CutoffGeometry(4000, 1024));

        Assert.False(slope.IsSearched);
        Assert.False(slope.IsFound);
    }

    [Fact]
    public void ARateUnder8000HertzIsNotSearchedForABandwidthOrAPartialCutoff()
    {
        var low = new CutoffGeometry(4000, 1024);

        Assert.False(CutoffFinder.FindBandwidth(Fine(Rows(_ => -500)), low).IsSearched);
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

    // 10 % of the frames with signal is enough, 5 % is not; the row and the count of the closest are still given.
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void ThePartialCutoffIsFoundFromATenthOfTheFramesWithSignal(int showing, bool isFound)
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

    // The rows are rounded once, at the end. A band at -50.04 dB over a top band at -55.06 dB stands 5.02 dB over it, which is 5.0 dB; the same rows
    // rounded to tenths first (-50.0 and -55.1) would give 5.1 dB.
    [Fact]
    public void AStepIsRoundedOnceFromRowsInThousandths()
    {
        var geometry = new CutoffGeometry(44100, 4096);
        int[] rows = new int[MeasurementLimits.Rows];
        for (int r = 0; r < rows.Length; r++)
        {
            rows[r] = r < 484 ? -50_040 : -55_060;
        }

        SpectralSlope found = CutoffFinder.FindSlope(rows, geometry);

        Assert.Equal(50, found.TopStepTenthsOfDb);
    }
}
