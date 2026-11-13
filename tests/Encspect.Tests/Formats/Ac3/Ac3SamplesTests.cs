using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// What the report states for an AC-3 stream (its Samples: 256 for each audio block of every frame, cut and broken frames included) is what the measurements keep and skip:
// measured + the samples lost + the samples withheld (docs/design/samples-definition.md). AC-3's syntax trims nothing (decision 8 of docs/design/lossy-decoders.md), and the
// edit lists of MP4 are not applied, so no trimmed samples enter. Every decodable AC-3 stream of every container is measured (a session tied to another node than the stream's would
// leave the measurements out), with no exceptions.
public class Ac3SamplesTests
{
    public static TheoryData<string> Ac3Fixtures => Ac3DecoderFixtureTests.Names;

    [Theory]
    [MemberData(nameof(Ac3Fixtures))]
    public void The_measurements_keep_what_the_report_states(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        foreach (StreamInfo info in Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Where(s => s.Codec is "AC-3" or "E-AC-3" && s.Verification is not null))
        {
            // A frame the parser counts lost (cut, broken) or the decoder is not given whole (a dependent frame the data lost) is not decoded, so the measurement of a stream that has one is Incomplete by definition; every other stream is Complete.
            MeasurementStatus expected = info.LostSamples > 0 || info.Measurements?.LostSamples > 0 ? MeasurementStatus.Incomplete : MeasurementStatus.Complete;
            Assert.True(info.Measurements is { } measured && measured.Status == expected, $"{name}: the stream decodes and its measurements are {info.Measurements?.Status.ToString() ?? "missing"}, not {expected}");
            StreamMeasurements m = info.Measurements!;
            // A measurement belongs to the stream it hangs on: with several substreams in a file a mixed-up record would show in another stream's channels or rate.
            // A channel map that names more locations than the frame codes (ac3.channel-map-count) gives the stream more channels than are decoded.
            Assert.True(m.SampleRate == info.SampleRate && (m.Channels == info.Channels || (name.Contains("chanmap-more") && m.Channels < info.Channels)), $"{name}: the stream has {info.Channels} channels at {info.SampleRate} Hz, its measurements {m.Channels} at {m.SampleRate}");
            Assert.Equal(0, m.LeadingTrimmedSamples + m.TrailingTrimmedSamples + m.InnerTrimmedSamples);
            Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: the report states {info.Samples}, the measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples}");
            Assert.True(m.LostSamples >= info.LostSamples, $"{name}: the measurements count {m.LostSamples} lost, the report {info.LostSamples}");
        }
    }

    [Fact]
    public void The_text_report_gives_each_substream_its_verdict_and_measurements_and_a_failed_dependent_frame_fails_its_stream()
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, "ac3/substreams-dependent-crc.eac3"));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("substreams-dependent-crc.eac3", stream, options);
        using var writer = new StringWriter();
        ReportTextWriter.Write(report, writer, showOffsets: false);
        string text = writer.ToString();
        Assert.Contains("Verified (stream 1): no (CRC 1 of 8 differ)", text);
        Assert.Contains("Verified (stream 2): yes", text);
        Assert.Contains("Measured (stream 1)", text);
        Assert.Contains("Measured (stream 2)", text);
    }
}
