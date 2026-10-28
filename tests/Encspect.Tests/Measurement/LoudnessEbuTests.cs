using Encspect.Core;
using static Encspect.Tests.Measurement.LoudnessSignals;

namespace Encspect.Tests.Measurement;

// The minimum-requirements signals of EBU Tech 3341 (Table 1) and Tech 3342 (Table 1), synthesized from the documents' descriptions at
// 48 kHz, as the EBU's own files are (the authentic programmes 7 and 8 of Tech 3341 and 5 and 6 of Tech 3342 cannot be synthesized and are
// only in the CI cross-check, which downloads the EBU's set). Tolerances are the documents' own.
public class LoudnessEbuTests
{
    private const int Rate = 48000;

    private static LoudnessFigures Loudness(double[][] channels, IReadOnlyList<Speaker>? layout = null, int blockSize = 4800) =>
        Measure(channels, Rate, layout, blockSize).Loudness!;

    private static LoudnessFigures Stereo1kHz(params (double Seconds, double Dbfs)[] parts) =>
        Loudness(Stereo(Join(parts.Select(p => Tone(Rate, p.Seconds, p.Dbfs)).ToArray())));

    [Theory]
    [InlineData(1, -23.0)]
    [InlineData(2, -33.0)]
    public void Case1And2TheLoudnessOfAStereoSineIsItsLevel(int number, double level)
    {
        LoudnessFigures l = Stereo1kHz((20, level));
        AssertNear(l.IntegratedTenths, level, 0.1, $"case {number} I");
        AssertNear(l.MaximumMomentaryTenths, level, 0.1, $"case {number} M");
        AssertNear(l.MaximumShortTermTenths, level, 0.1, $"case {number} S");
    }

    [Fact]
    public void Case3TheRelativeGateLeavesOutTheQuietTones()
    {
        AssertNear(Stereo1kHz((10, -36), (60, -23), (10, -36)).IntegratedTenths, -23.0, 0.1, "case 3 I");
    }

    [Fact]
    public void Case4TheAbsoluteGateLeavesOutTheSilence()
    {
        AssertNear(Stereo1kHz((10, -72), (10, -36), (60, -23), (10, -36), (10, -72)).IntegratedTenths, -23.0, 0.1, "case 4 I");
    }

    [Fact]
    public void Case5TheLoudSegmentCountsOnTopOfTheQuietOnes()
    {
        AssertNear(Stereo1kHz((20, -26), (20.1, -20), (20, -26)).IntegratedTenths, -23.0, 0.1, "case 5 I");
    }

    [Fact]
    public void Case6FiveChannelsAreWeighted()
    {
        double[][] channels =
        [
            Tone(Rate, 20, -28), Tone(Rate, 20, -28), Tone(Rate, 20, -24), Tone(Rate, 20, -30), Tone(Rate, 20, -30),
        ];
        Speaker[] layout = [Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.BackLeft, Speaker.BackRight];
        AssertNear(Loudness(channels, layout).IntegratedTenths, -23.0, 0.1, "case 6 I");
    }

    [Fact]
    public void Case9TheShortTermLoudnessOfARepeatedPatternIsConstantAfterThreeSeconds()
    {
        double[] pattern = Join(Tone(Rate, 1.34, -20), Tone(Rate, 1.66, -30));
        LoudnessFigures l = Loudness(Stereo(Join(Enumerable.Repeat(pattern, 5).ToArray())));
        AssertNear(l.MaximumShortTermTenths, -23.0, 0.1, "case 9 S");
        foreach (double value in l.ShortTerm!.Values.Span)
        {
            Assert.InRange(value, -23.1, -22.9);
        }
    }

    [Fact]
    public void Case10TheMaximumShortTermLoudnessOfATwentySegmentsEachIsTheTonesLevel()
    {
        for (int i = 0; i < 20; i++)
        {
            LoudnessFigures l = Loudness(Stereo(Join(Silence(Rate, i * 0.15), Tone(Rate, 3, -23), Silence(Rate, 1))));
            AssertNear(l.MaximumShortTermTenths, -23.0, 0.1, $"case 10 segment {i}");
        }
    }

    [Fact]
    public void Case12TheMomentaryLoudnessOfARepeatedPatternIsConstantAfterOneSecond()
    {
        double[] pattern = Join(Tone(Rate, 0.18, -20), Tone(Rate, 0.22, -30));
        LoudnessFigures l = Loudness(Stereo(Join(Enumerable.Repeat(pattern, 25).ToArray())));
        AssertNear(l.MaximumMomentaryTenths, -23.0, 0.1, "case 12 M");
    }

    [Fact]
    public void Case13TheMaximumMomentaryLoudnessOfBurstsAtAnyMultipleOfTwentyMilliseconds()
    {
        for (int i = 0; i < 20; i++)
        {
            LoudnessFigures l = Loudness(Stereo(Join(Silence(Rate, i * 0.02), Tone(Rate, 0.4, -23), Silence(Rate, 1))));
            AssertNear(l.MaximumMomentaryTenths, -23.0, 0.1, $"case 13 segment {i}");
        }
    }

    [Fact]
    public void Cases11And14TheMaximaOfSegmentsAtRisingLevelsFollowTheLevels()
    {
        // The 'live' files of the EBU are one long signal with 20 readings; here each reading is its own stream, which a file-based meter measures alike.
        for (int i = 0; i < 20; i++)
        {
            LoudnessFigures s = Loudness(Stereo(Join(Silence(Rate, i * 0.15), Tone(Rate, 3, -38 + i), Silence(Rate, 3 - (i * 0.15)))));
            AssertNear(s.MaximumShortTermTenths, -38.0 + i, 0.1, $"case 11 reading {i}");
            LoudnessFigures m = Loudness(Stereo(Join(Silence(Rate, i * 0.02), Tone(Rate, 0.4, -38 + i), Silence(Rate, 0.4 - (i * 0.02)))));
            AssertNear(m.MaximumMomentaryTenths, -38.0 + i, 0.1, $"case 14 reading {i}");
        }
    }

    [Theory]
    [InlineData(-20.0, -30.0, 10.0)]
    [InlineData(-20.0, -15.0, 5.0)]
    [InlineData(-40.0, -20.0, 20.0)]
    public void Tech3342Cases1To3TwoTonesGiveTheirDistanceAsTheLoudnessRange(double first, double second, double range)
    {
        LoudnessFigures l = Stereo1kHz((20, first), (20, second));
        AssertNear(l.RangeTenths, range, 1.0, "LRA");
    }

    [Fact]
    public void Tech3342Case4FiveTonesGiveFifteen()
    {
        LoudnessFigures l = Stereo1kHz((20, -50), (20, -35), (20, -20), (20, -35), (20, -50));
        AssertNear(l.RangeTenths, 15.0, 1.0, "LRA");
    }

    [Fact]
    public void Tech3342TheSignalRepeatedGivesTheSameRange()
    {
        double[] once = Join(Tone(Rate, 20, -20), Tone(Rate, 20, -30));
        AssertNear(Loudness(Stereo(Join(once, once, once))).RangeTenths, 10.0, 1.0, "LRA of three repetitions");
    }

    [Fact]
    public void AFullScaleSineInOneChannelReadsMinusThreePointZeroOneLkfs()
    {
        // BS.1770-5 Annex 1: a full-scale 997 Hz sine in the left, center or right input reads -3.01 LKFS.
        foreach (Speaker speaker in new[] { Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight })
        {
            double[][] channels = [Tone(Rate, 10, 0, 997), Silence(Rate, 10), Silence(Rate, 10)];
            Speaker[] layout = speaker == Speaker.FrontLeft ? [Speaker.FrontLeft, Speaker.FrontCenter, Speaker.FrontRight]
                : speaker == Speaker.FrontCenter ? [Speaker.FrontCenter, Speaker.FrontLeft, Speaker.FrontRight] : [Speaker.FrontRight, Speaker.FrontLeft, Speaker.FrontCenter];
            Assert.Equal(-30, Loudness(channels, layout).IntegratedTenths);
        }

        Assert.Equal(-30, Loudness([Tone(Rate, 10, 0, 997)]).IntegratedTenths);
    }
}
