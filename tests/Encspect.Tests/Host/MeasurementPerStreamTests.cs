using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A file whose configuration changes has several streams, each with its own decoding session; each stream gets the measurements of its own session, not those of the first stream
// (the records were kept by the stream's node, which the streams of one region share).
public class MeasurementPerStreamTests
{
    public static TheoryData<string> Files => new()
    {
        "mpeg-audio/cut-frame-stream-change.mp3",
        "mpeg-audio/layer3/reservoir-free-to-fixed.mp3",
        "mpeg-audio/layer3/ancillary-streams-apart.mp3",
        "mpeg-audio/layer3/reservoir-stream-change.mp3",
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_stream_has_the_measurements_of_its_own_session(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options);

        StreamInfo[] streams = [.. report.Streams.Where(s => s.Verification is not null)];
        Assert.True(streams.Length >= 2, $"{name} has {streams.Length} decoded streams");
        foreach (StreamInfo info in streams)
        {
            StreamMeasurements m = Assert.IsType<StreamMeasurements>(info.Measurements);
            Assert.Equal(info.SampleRate, m.SampleRate);
            Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: a stream states {info.Samples} samples at {info.SampleRate} Hz, its measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples} at {m.SampleRate} Hz");
        }

        Assert.True(streams.Select(s => s.Measurements!.Samples + s.Measurements.LostSamples).Distinct().Count() > 1 || streams.Select(s => s.SampleRate).Distinct().Count() > 1,
            $"{name}: the streams differ in length or rate, so equal measurements for all of them would be the first stream's");
    }
}
