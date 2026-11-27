using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// The Nyquist edge of docs/design/spectral-analysis.md, Step 10, on spectra made row by row (thousandths of a dB) and counters made cell by cell: the window's bounds, the four tests
// (25 dB, a local maximum, no further fall above, the frames), the rates tested, the level above against its mirror and the order of several edges.
public class NyquistEdgeFinderTests
{
    private const int Pass = -60_000;
    private const int Floor = -160_000;

    // 48 kHz at 4,096 points: a row is 46.875 Hz, W is 21 rows and the Nyquist frequency of 44.1 kHz (22,050 Hz) lies 470.4 rows up.
    private static readonly CutoffGeometry Geometry48 = new(48000, 4096);

    private static int[] Rows(Func<int, int> level) => [.. Enumerable.Range(0, MeasurementLimits.Rows).Select(level)];

    // A brickwall: the pass level under the boundary, the floor from it.
    private static int[] Wall(int boundary) => Rows(r => r < boundary ? Pass : Floor);

    // Counters in which every candidate row has `frames` frames at one level, `showing` of them showing an own step.
    private static CutoffCounters Counters(int frames = 10, int showing = 10)
    {
        var counters = new CutoffCounters();
        for (int r = 0; r < MeasurementLimits.Rows; r++)
        {
            counters.All[(r * CutoffCounters.Levels) + 40] = frames;
            counters.Show[(r * CutoffCounters.Levels) + 40] = showing;
        }

        return counters;
    }

    private static IReadOnlyList<NyquistEdge> Find(int[] rows, CutoffGeometry geometry, int showing = 10) => NyquistEdgeFinder.Find(rows, Counters(10, showing), geometry);

    [Fact]
    public void AStepAtTheNyquistFrequencyOfALowerRateIsFoundWithItsLevelAboveAgainstItsMirror()
    {
        // The step at boundary 460 (21,562 Hz, 2.2 % under 22,050 Hz): the 21 rows from the first row above 22,050 Hz (row 471) are at the floor and the rows they mirror under
        // 22,050 Hz (448 to 468, from the highest down) are 12 rows of pass level and 9 of floor.
        IReadOnlyList<NyquistEdge> edges = Find(Wall(460), Geometry48);

        NyquistEdge edge = Assert.Single(edges);
        Assert.Equal(44100, edge.LowerRate);
        Assert.Equal(Geometry48.Hertz(460), edge.StepFrequency);
        Assert.Equal(1000, edge.StepTenthsOfDb);
        double mirror = ((12 * Pass) + (9 * Floor)) / 21.0;
        Assert.Equal((int)Math.Floor(((Floor - mirror) / 100) + 0.5), edge.AboveTenthsOfDb);
        Assert.Equal(10, edge.FramesShowing);
        Assert.Equal(10, edge.FramesWithSignal);
    }

    // 97 f >= ... : 22,050 Hz at 48 kHz has its window from boundary 457 (21,422 Hz, the first boundary of 97 % of 22,050 Hz) up to boundary 471 (22,078 Hz, the first above).
    [Theory]
    [InlineData(456, false)]
    [InlineData(457, true)]
    [InlineData(471, true)]
    [InlineData(472, false)]
    public void TheWindowRunsFromThreePercentUnderTheNyquistFrequencyToTheFirstBoundaryOverIt(int boundary, bool isFound)
    {
        IReadOnlyList<NyquistEdge> edges = Find(Wall(boundary), Geometry48);

        Assert.Equal(isFound ? [44100] : [], edges.Select(e => e.LowerRate));
        if (isFound)
        {
            Assert.Equal(Geometry48.Hertz(boundary), edges[0].StepFrequency);
        }
    }

    [Fact]
    public void AStepUnderTwentyFiveDecibelsIsNotAnEdge()
    {
        Assert.Empty(Find(Rows(r => r < 460 ? -100_000 : -124_900), Geometry48));
        Assert.Single(Find(Rows(r => r < 460 ? -100_000 : -125_000), Geometry48));
    }

    // A slope: the step grows with the boundary, so its peak is above the window, not a local maximum inside it.
    [Fact]
    public void AStepThatKeepsGrowingAboveTheWindowIsNotALocalMaximum()
    {
        Assert.Empty(Find(Rows(r => -60_000 - (Math.Max(0, r - 440) * 1_000)), Geometry48));
    }

    // The step at the window's boundaries is the largest at 460; a slope that has its peak under the window (a lower edge) is not taken at all.
    [Fact]
    public void AnEdgeUnderTheWindowIsNotTakenForTheRatesAboveIt()
    {
        Assert.Empty(Find(Wall(400), Geometry48));
    }

    [Fact]
    public void TheBandAboveMayNotGoOnFallingByMoreThanAQuarterOfTheStep()
    {
        int[] Falling(int second) => Rows(r => r < 460 ? Pass : r < 492 ? Floor : Floor - second);

        // A step of 100 dB; the 21 rows from row 471 against the next 21 (492 to 512, clipped at 506: 15 rows).
        Assert.Single(Find(Falling(25_000), Geometry48));
        Assert.Empty(Find(Falling(25_001), Geometry48));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void AtLeastHalfOfTheFramesWithSignalMustShowTheStep(int showing, bool isFound)
    {
        IReadOnlyList<NyquistEdge> edges = Find(Wall(460), Geometry48, showing);

        Assert.Equal(isFound, edges.Count == 1);
    }

    [Fact]
    public void NoFrameWithSignalIsNoEdge()
    {
        Assert.Empty(NyquistEdgeFinder.Find(Wall(460), new CutoffCounters(), Geometry48));
    }

    [Fact]
    public void SeveralEdgesAreListedHighestFirst()
    {
        // 96 kHz at 8,192 points: a row is 93.75 Hz, W is 11 rows. Boundary 232 (21,750 Hz) is under 22,050 Hz and boundary 167 (15,656 Hz) under 16,000 Hz.
        var geometry = new CutoffGeometry(96000, 8192);
        int[] rows = Rows(r => r < 167 ? -50_000 : r < 232 ? -100_000 : -160_000);

        IReadOnlyList<NyquistEdge> edges = NyquistEdgeFinder.Find(rows, Counters(), geometry);

        Assert.Equal([44100, 32000], edges.Select(e => e.LowerRate));
        Assert.Equal([geometry.Hertz(232), geometry.Hertz(167)], edges.Select(e => e.StepFrequency));
    }

    [Fact]
    public void OnlyRatesUnderTheStreamsOwnAreTested()
    {
        // The step at 22,050 Hz in a 44.1 kHz stream is at its own Nyquist frequency: no candidate (44,100 Hz is not under the stream's rate).
        var geometry = new CutoffGeometry(44100, 4096);
        Assert.Empty(NyquistEdgeFinder.Find(Wall(500), Counters(), geometry));
        // 32,000 Hz at 44.1 kHz: its Nyquist frequency 16,000 Hz is 371.5 rows up (43.07 Hz a row); a step at boundary 365 (15,723 Hz) is found.
        Assert.Equal([32000], NyquistEdgeFinder.Find(Wall(365), Counters(), geometry).Select(e => e.LowerRate));
    }

    [Fact]
    public void AtHalfAHertzTheNyquistFrequencyOf11025HzIsKept()
    {
        // 22.05 kHz at 2,048 points: a row is 21.533 Hz, and boundary 256 is exactly 5,512.5 Hz; the first boundary above it is 257.
        var geometry = new CutoffGeometry(22050, 2048);
        foreach ((int boundary, bool isFound) in new[] { (256, true), (257, true), (258, false) })
        {
            IReadOnlyList<NyquistEdge> edges = NyquistEdgeFinder.Find(Wall(boundary), Counters(), geometry);
            Assert.Equal(isFound ? [11025] : [], edges.Select(e => e.LowerRate));
        }
    }

    [Fact]
    public void ARateUnderEightThousandHertzIsNotSearched()
    {
        var geometry = new CutoffGeometry(7999, 1024);
        Assert.False(NyquistEdgeFinder.IsSearched(geometry, Counters()));
        Assert.Empty(NyquistEdgeFinder.Find(Wall(300), Counters(), geometry));
        Assert.False(NyquistEdgeFinder.IsSearched(Geometry48, null));
        Assert.True(NyquistEdgeFinder.IsSearched(Geometry48, Counters()));
    }

    [Fact]
    public void TheTextNamesTheStepTheRateAndTheLevelAboveAgainstItsMirror()
    {
        string text = MeasurementText.NyquistEdgeText(new NyquistEdge { LowerRate = 44100, StepFrequency = 21516, StepTenthsOfDb = 522, AboveTenthsOfDb = -309, FramesShowing = 186, FramesWithSignal = 186 });
        Assert.Equal("a step of 52.2 dB at 21,516 Hz, 534 Hz under 22,050 Hz (the Nyquist frequency of 44,100 Hz), in 186 of 186 frames with signal; the band above 22,050 Hz is 30.9 dB under its mirror below", text);

        string half = MeasurementText.NyquistEdgeText(new NyquistEdge { LowerRate = 11025, StepFrequency = 5513, StepTenthsOfDb = 700, AboveTenthsOfDb = 5, FramesShowing = 3, FramesWithSignal = 4 });
        Assert.Equal("a step of 70.0 dB at 5,513 Hz, 0.5 Hz over 5,512.5 Hz (the Nyquist frequency of 11,025 Hz), in 3 of 4 frames with signal; the band above 5,512.5 Hz is 0.5 dB over its mirror below", half);

        string on = MeasurementText.NyquistEdgeText(new NyquistEdge { LowerRate = 32000, StepFrequency = 16000, StepTenthsOfDb = 700, AboveTenthsOfDb = 0, FramesShowing = 1, FramesWithSignal = 1 });
        Assert.Equal("a step of 70.0 dB at 16,000 Hz, the Nyquist frequency of 32,000 Hz, in 1 of 1 frames with signal; the band above 16,000 Hz is level with its mirror below", on);
    }
}
