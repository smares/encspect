using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// What the report states for an AC-3 stream (its Samples: 256 for each audio block of every frame, cut and broken frames included) is what the measurements keep and skip:
// measured + the samples lost + the samples withheld (docs/design/samples-definition.md). AC-3's syntax trims nothing (decision 8 of docs/design/lossy-decoders.md), and the
// edit lists of MP4 are not applied, so no trimmed samples enter.
public class Ac3SamplesTests
{
    public static TheoryData<string> Ac3Fixtures => Ac3DecoderFixtureTests.Names;

    [Theory]
    [MemberData(nameof(Ac3Fixtures))]
    public void The_measurements_keep_what_the_report_states(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        foreach (StreamInfo info in Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Where(s => s.Codec == "AC-3" && s.Verification is not null))
        {
            if (info.Measurements is { Status: MeasurementStatus.Complete } m)
            {
                Assert.Equal(0, m.LeadingTrimmedSamples + m.TrailingTrimmedSamples + m.InnerTrimmedSamples);
                Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: the report states {info.Samples}, the measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples}");
                Assert.True(m.LostSamples >= info.LostSamples, $"{name}: the measurements count {m.LostSamples} lost, the report {info.LostSamples}");
            }
        }
    }
}
