using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// What the report states for an Opus stream (its Samples: the decoded samples less the pre-skip and the end trimming) is what the measurements keep and skip: measured +
// the samples lost + the samples withheld (docs/design/samples-definition.md). The trims come first: positions the syntax trims are not samples of the stream, so a span a
// decoder withholds counts only outside them, and an end trimming reaches back over the packets the parser holds (OpusParser.Decoding.cs, HoldSamples).
public class OpusTrimTests
{
    public static TheoryData<string> OpusFixtures => OpusDecoderFixtureTests.Names;

    // Fixtures the rule does not cover, with the reason.
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
    };

    [Theory]
    [MemberData(nameof(OpusFixtures))]
    public void The_measurements_keep_what_the_report_states(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        foreach (StreamInfo info in Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Where(s => s.Codec == "Opus"))
        {
            if (info.Measurements is { Status: MeasurementStatus.Complete } m && !Exceptions.ContainsKey(name))
            {
                Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: the report states {info.Samples}, the measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples}");
            }
        }
    }
}
