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

    // The end trimming of 500 lies inside the 80 ms withheld after the last frame of no data: those positions are trimmed samples (the pre-skip's 312 and the 500 are 812), and
    // the withheld samples are the ones outside the trims (7,680 less the 500), so that measured 62,088 + lost 960 + withheld 7,180 is the report's 70,228.
    [Fact]
    public void Trimmed_positions_under_a_withheld_span_count_as_trimmed_and_not_as_withheld()
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "ogg/opus-toc-configs.opus"));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        StreamInfo info = Analyzer.CreateDefault().Analyze("opus-toc-configs.opus", stream, options).Streams.Single(s => s.Codec == "Opus");

        StreamMeasurements m = info.Measurements!;
        Assert.Equal(70_228, info.Samples);
        Assert.Equal((312L, 500L), (m.LeadingTrimmedSamples, m.TrailingTrimmedSamples));
        Assert.Equal(812, m.LeadingTrimmedSamples + m.TrailingTrimmedSamples);
        Assert.Equal((62_088L, 960L, 7_180L), (m.Samples, m.LostSamples, m.WithheldSamples));
        Assert.Equal(7_180, m.WithheldByReason.EmptyFrame);
    }
}
