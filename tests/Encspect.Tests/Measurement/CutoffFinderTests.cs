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
}
