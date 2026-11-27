using Encspect.Core;
using Encspect.Host;
using Encspect.Tests.Decoding;

namespace Encspect.Tests.Formats.Aac;

// What AAC Main's prediction and long-term prediction depend on after a gap (docs/design/aac-main-ltp-er.md, "Gaps and withheld output"), through the analysis with the measurements on.
public class AacMainLtpDecoderTests
{
    private static StreamInfo Measured(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", name));
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue, Output = _ => new DecoderWorkTests.NullSink(), Measure = new MeasureOptions() } });
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
        return report.Streams.Single();
    }

    // The block after the gap starts with predictors reset, and every block that uses a group not reset since is withheld, as is the one after the last of them for its overlap: the
    // reset of one group in each frame, in a cycle, resynchronizes all 30 after 30 blocks. Frame 21 is lost; frames 22 to 52 are withheld, the 31 of the 39 that follow.
    [Fact]
    public void MainPredictionAfterAGapIsWithheldUntilEveryGroupHasBeenReset()
    {
        StreamInfo stream = Measured("main-prediction-gap.aac");

        Assert.Equal(31 * 1024, stream.Measurements!.WithheldSamples);
        Assert.Equal(DecodeStatus.Decoded, stream.Verification!.Decoding);
    }

    // The history after the gap is zeros: frames 10 to 12 predict from it or from output they withheld, frame 13 has no prediction but adds to the overlap of frame 12 (so is withheld
    // for it), and the two frames without long-term prediction refill the history, so that the frames after them are not withheld.
    [Fact]
    public void LongTermPredictionAfterAGapIsWithheldUntilThePredictionPauses()
    {
        StreamInfo stream = Measured("ltp-gap.aac");

        Assert.Equal(4 * 1024, stream.Measurements!.WithheldSamples);
    }

    // The start of a stream is synchronized: long-term prediction in its first frames reads the zeros the specification starts with, and nothing is withheld.
    [Theory]
    [InlineData("ltp-lags.aac")]
    [InlineData("main-prediction-reset-cycle.aac")]
    public void TheStartOfAStreamIsNotAGap(string name)
    {
        StreamInfo stream = Measured(name);

        Assert.Equal(0, stream.Measurements!.WithheldSamples);
    }
}
