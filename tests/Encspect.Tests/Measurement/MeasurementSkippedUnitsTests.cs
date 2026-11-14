using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Every sample a report states is measured, counted lost, or explained by a finding (docs/design/samples-definition.md): units the decoder does not
// decode, because libwavpack reads past them or the decoder does not follow a change of the stream, are handed to the decoding as unreadable ones.
public class MeasurementSkippedUnitsTests
{
    private static StreamInfo Measured(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Single();
    }

    // The first set is skipped before libwavpack's decoding opens, so its samples lead the measured ones as lost.
    [Theory]
    [InlineData("wavpack/built-block-size-too-small.wv")]
    [InlineData("wavpack/built-block-size-too-large.wv")]
    [InlineData("wavpack/built-block-trailing-byte.wv")]
    public void A_damaged_first_block_is_a_leading_lost_stretch(string name)
    {
        StreamInfo info = Measured(name);
        StreamMeasurements m = info.Measurements!;

        Assert.Equal(MeasurementStatus.Incomplete, m.Status);
        Assert.Equal((11025L, 11025L, 12975L), (m.LeadingLostSamples, m.LostSamples, m.Samples));
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
    }

    // The sets after the first one begin without the initial block flag, so libwavpack decodes none of them: their 2,000 samples are lost, and the
    // 14,000 positions the block indexes jump over stay apart, as for any jump.
    [Fact]
    public void Sets_that_begin_without_the_initial_block_flag_are_lost()
    {
        StreamInfo info = Measured("wavpack/built-set-without-final-limit.wv");

        Assert.Equal(3000, info.Samples);
        Assert.Equal(2000, info.LostSamples);
        Assert.Equal((MeasurementStatus.Incomplete, 1000L), (info.Measurements!.Status, info.Measurements.Samples));
        Assert.Equal(info.Samples, info.Measurements.Samples + info.LostSamples);
    }

    // The decoder does not follow a change of the stretch's parameters, so the 3,200 samples after it are lost ones of the measurements.
    [Fact]
    public void A_stretch_after_a_parameters_change_the_decoder_does_not_follow_is_lost()
    {
        StreamInfo info = Measured("truehd/built-parameters-change.thd");
        StreamMeasurements m = info.Measurements!;

        Assert.Equal(4800, info.Samples);
        Assert.Equal((MeasurementStatus.Incomplete, 1600L, 3200L), (m.Status, m.Samples, m.LostSamples));
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
    }
}
