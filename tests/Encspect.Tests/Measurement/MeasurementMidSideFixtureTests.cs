using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Measurement;

// The mid and side spectra and the spectral holes on the fixtures built to show them (synthetic, make_measurement_fixtures.py) and on the encoder-made stereo
// files (make_external_measurement_fixtures.py), whose figures docs/design/spectral-analysis.md, "As built: step 7, second change", records.
public class MeasurementMidSideFixtureTests
{
    private static SpectrumFigures Spectrum(string name) => MeasurementFixtureTests.Analyze(name, new MeasureOptions()).Streams.Single().Measurements!.Spectrum!;

    [Fact]
    public void MidLinesTo16kHzAndSideLinesTo11kHzGiveTwoCutoffs()
    {
        SpectrumFigures spectrum = Spectrum("synthetic-stereo-mid-16000-side-11000");
        MidSideFigures pair = spectrum.MidSide!;

        Assert.InRange(pair.MidCutoff.Frequency, 15950, 16100);
        Assert.InRange(pair.SideCutoff.Frequency, 10950, 11100);
        Assert.True(pair.MidCutoff.IsFound && pair.SideCutoff.IsFound);
        Assert.Equal(20, pair.MidCutoff.FramesWithSignal);
        Assert.Equal(20, pair.SideCutoff.FramesWithSignal);
        Assert.Equal(20, pair.SideCutoff.FramesShowing);

        // The channels carry both signals, so their cutoffs and the combined one are the mid's.
        Assert.InRange(spectrum.Cutoff.Frequency, 15950, 16100);
        Assert.All(spectrum.ChannelCutoffs, c => Assert.InRange(c.Frequency, 15950, 16100));
    }

    [Fact]
    public void IdenticalChannelsHaveASideWithoutPower()
    {
        SpectrumFigures spectrum = Spectrum("synthetic-stereo-identical-channels");
        MidSideFigures pair = spectrum.MidSide!;

        Assert.All(pair.Side.ToArray(), v => Assert.Equal(MeasurementLimits.NoPower, v));
        Assert.True(pair.Mid.Span.SequenceEqual(spectrum.PerChannel[0].Span));
        Assert.Equal(0, pair.SideHoles.CellsWithSignal);
        Assert.False(pair.SideCutoff.IsFound);
    }

    [Fact]
    public void IndependentNoiseInEachChannelGivesAMidAndASideAlike()
    {
        MidSideFigures pair = Spectrum("synthetic-stereo-independent-noise").MidSide!;

        double difference = 0;
        for (int r = 50; r < 500; r++)
        {
            difference += pair.Mid.Span[r] - pair.Side.Span[r];
            Assert.InRange(Math.Abs(pair.Mid.Span[r] - pair.Side.Span[r]), 0, 60);
        }

        Assert.InRange(Math.Abs(difference / 450), 0, 5);

        Assert.False(pair.MidCutoff.IsFound);
        Assert.False(pair.SideCutoff.IsFound);
    }

    // Lines at every bin with bands of 2, 4, 8 and 16 rows left out: the row next to content reads the window's leakage, so only the inner rows are holes: none
    // for 2 rows, 2 for 4, 6 for 8 and 14 for 16, in every frame. The 16 kHz band is 64 bins wide at 4 bins a row, and each inner row has content within 23 rows on both sides.
    [Theory]
    [InlineData(80, 2, 0)]
    [InlineData(160, 4, 2)]
    [InlineData(240, 8, 6)]
    [InlineData(360, 16, 14)]
    public void OnlyTheInnerRowsOfARemovedBandAreHoles(int first, int width, int inner)
    {
        SpectralHoles holes = Spectrum("synthetic-holes-bands-2-4-8-16").Holes;
        ReadOnlySpan<short> shares = holes.RowShares.Span;

        Assert.Equal(0, shares[first - 1]);
        Assert.Equal(0, shares[first]);
        for (int r = first + 1; r < first + 1 + inner; r++)
        {
            Assert.Equal(1000, shares[r]);
        }

        Assert.Equal(0, shares[first + width - 1]);
        Assert.Equal(0, shares[first + width]);
    }

    [Fact]
    public void TheHoleCellsOfTheRemovedBandsAreEveryFrameTimesTheInnerRows()
    {
        SpectralHoles holes = Spectrum("synthetic-holes-bands-2-4-8-16").Holes;

        Assert.Equal(20 * (0 + 2 + 6 + 14), holes.HoleCells);
        Assert.Equal(1981, holes.FromFrequency);
        Assert.InRange(holes.ToFrequency, 19900, 20100);
        Assert.InRange(holes.ShareTenths, 50, 56);
    }

    // The band of 8 rows is absent in the second half of the stream: 9 of the 20 frames lie entirely in it (two straddle the middle), so a share of 45 % at its inner rows.
    [Fact]
    public void ABandRemovedInHalfOfTheFramesIsAHoleInThoseFrames()
    {
        SpectralHoles holes = Spectrum("synthetic-holes-band-in-half-of-the-frames").Holes;
        ReadOnlySpan<short> shares = holes.RowShares.Span;

        Assert.Equal(0, shares[200]);
        for (int r = 201; r <= 206; r++)
        {
            Assert.Equal(450, shares[r]);
        }

        Assert.Equal(0, shares[207]);
        Assert.Equal(20 * 0 + (9 * 6), holes.HoleCells);
    }

    // Sparse spectra: the gaps between a pure tone's harmonics are holes by the definition (the model's shares, 72 and 79 %).
    [Theory]
    [InlineData("synthetic-harmonic-440-20000", 650, 800)]
    [InlineData("synthetic-harmonic-440-8000", 700, 850)]
    public void TheGapsBetweenHarmonicsAreHoles(string name, int least, int most)
    {
        SpectralHoles holes = Spectrum(name).Holes;

        Assert.InRange(holes.ShareTenths, least, most);
    }

    // Noise with no gap has none: white noise, and pink noise through a lowpass.
    [Theory]
    [InlineData("synthetic-white-44100")]
    [InlineData("synthetic-rolloff-1")]
    [InlineData("synthetic-rolloff-4")]
    [InlineData("synthetic-lines-16000-44100")]
    [InlineData("lossy-lame-v0")]
    [InlineData("lossy-oggenc-q8")]
    public void ContinuousSpectraHaveNoHoles(string name)
    {
        SpectralHoles holes = Spectrum(name).Holes;

        Assert.True(holes.CellsWithSignal > 0);
        Assert.InRange(holes.ShareTenths, 0, 5);
    }

    // The mono lossy-derived files: LAME 3.100 leaves a few cells (0.2 to 0.8 % in Encspect's test files), the others fewer than 0.2 %.
    [Theory]
    [MemberData(nameof(MeasurementFixtureTests.LossyNames), MemberType = typeof(MeasurementFixtureTests))]
    public void TheMonoLossyFilesHaveFewHoles(string name)
    {
        if (name.Contains("stereo", StringComparison.Ordinal))
        {
            return;
        }

        SpectralHoles holes = Spectrum(name).Holes;

        Assert.True(holes.CellsWithSignal > 0, name);
        Assert.InRange(holes.ShareTenths, 0, 10);
    }

    // What the encoders made of the stereo input (2 s of pink mid and a pink side 12 dB lower), as observed with LAME 3.100, vorbis-tools 1.4.2, FFmpeg 6.1.1 and opus-tools 0.2:
    // the side cutoff below the mid's for LAME's joint stereo, and holes in the side spectrum of LAME -m j -b 64 (12 %) that its forced stereo and the mid spectrum do not have.
    [Theory]
    [InlineData("lossy-stereo-lame-j-b64", 100, 400)]
    [InlineData("lossy-stereo-lame-j-b128", 300, 500)]
    public void LamesJointStereoLeavesTheSideCutoffUnderTheMids(string name, int least, int most)
    {
        MidSideFigures pair = Spectrum(name).MidSide!;

        Assert.True(pair.MidCutoff.IsFound && pair.SideCutoff.IsFound, name);
        Assert.InRange(pair.MidCutoff.Frequency - pair.SideCutoff.Frequency, least, most);
    }

    [Fact]
    public void LamesForcedStereoLeavesTheSideCutoffAtTheMids()
    {
        MidSideFigures pair = Spectrum("lossy-stereo-lame-s-b128").MidSide!;

        Assert.InRange(pair.MidCutoff.Frequency - pair.SideCutoff.Frequency, -100, 100);
    }

    [Fact]
    public void LamesJointStereoAt64KbpsLeavesHolesInTheSideSpectrumOnly()
    {
        MidSideFigures pair = Spectrum("lossy-stereo-lame-j-b64").MidSide!;

        Assert.InRange(pair.SideHoles.ShareTenths, 100, 140);
        Assert.InRange(pair.MidHoles.ShareTenths, 0, 5);
        Assert.InRange(Spectrum("lossy-stereo-lame-j-b64").Holes.ShareTenths, 0, 5);
        Assert.InRange(Spectrum("lossy-stereo-lame-s-b128").MidSide!.SideHoles.ShareTenths, 0, 5);
    }

    [Theory]
    [InlineData("lossy-stereo-lame-j-b64")]
    [InlineData("lossy-stereo-lame-j-b128")]
    [InlineData("lossy-stereo-lame-s-b128")]
    [InlineData("lossy-stereo-oggenc-q0")]
    [InlineData("lossy-stereo-aac-64k")]
    [InlineData("lossy-stereo-opus-48k")]
    public void TheEncoderMadeStereoFilesHaveAPairWithBothCutoffsFound(string name)
    {
        SpectrumFigures spectrum = Spectrum(name);

        Assert.True(spectrum.Cutoff.IsFound, name);
        Assert.NotNull(spectrum.MidSide);
        Assert.True(spectrum.MidSide.MidCutoff.IsFound, name);
        Assert.True(spectrum.MidSide.SideCutoff.IsFound, name);
        Assert.InRange(spectrum.MidSide.SideCutoff.Frequency, spectrum.MidSide.MidCutoff.Frequency - 600, spectrum.MidSide.MidCutoff.Frequency + 100);
    }
}
