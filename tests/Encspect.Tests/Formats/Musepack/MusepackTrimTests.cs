using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// What the report states for a Musepack stream (its Samples: the decoded samples less the synthesis filter's 481 and, in SV8, the beginning silence, up to the stream's
// length) is what the measurements keep and skip: measured + the samples lost + the samples withheld (docs/design/samples-definition.md). The trims are the stream's own
// syntax (StreamInfo.Samples is the range [481 + silence, 481 + silence + Samples) of the coded stage), so a frame the decoder cannot read counts as lost and the frames after
// it as withheld outside them only.
public class MusepackTrimTests
{
    public static TheoryData<string> Fixtures => MusepackDecoderFixtureTests.Names;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_measurements_keep_what_the_report_states(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Tests.Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        foreach (StreamInfo info in Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Where(s => s.Codec.StartsWith("Musepack", StringComparison.Ordinal)))
        {
            if (info.Measurements is { Status: MeasurementStatus.Complete or MeasurementStatus.Incomplete } m)
            {
                Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: the report states {info.Samples}, the measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples}");
            }
        }
    }
}
