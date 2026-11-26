using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;
using Encspect.Tests.Support;

namespace Encspect.Tests.Formats.Speex;

// What the report states for a Speex stream (its Samples: the coded samples less the encoder's delay and the end trimming the granule positions give) is what the
// measurements keep and skip: measured + the samples lost + the samples withheld (docs/design/samples-definition.md). The trims are the stream's own syntax
// (StreamInfo.Samples is the range [delay, delay + Samples) of the coded stage), so a packet the decoder cannot read counts as lost and the frames after it as withheld
// outside them only.
public class SpeexTrimTests
{
    public static TheoryData<string> Fixtures => SpeexDecoderFixtureTests.Names;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_measurements_keep_what_the_report_states(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Support.Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        foreach (StreamInfo info in Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Where(s => s.Codec.StartsWith("Speex", StringComparison.Ordinal)))
        {
            if (info.Measurements is { Status: MeasurementStatus.Complete or MeasurementStatus.Incomplete } m)
            {
                Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{name}: the report states {info.Samples}, the measurements keep {m.Samples} + lost {m.LostSamples} + withheld {m.WithheldSamples}");
            }
        }
    }

    // A stream with no granule position at all has no delay the report could state: the report and the measurements both count from 0, however many packets it holds
    // (the parser held 512 back for the delay, then assumed libspeex's lookahead for the decoder alone).
    [Theory]
    [InlineData(100)]
    [InlineData(600)]
    public void A_stream_without_granule_positions_is_trimmed_alike_in_the_report_and_the_measurements(int packets)
    {
        byte[] frame = [0x38, .. new byte[60], 0x07, 0x80];
        var builder = new OggBuilder()
            .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
            .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
        for (int done = 0; done < packets; done += 50)
        {
            builder.Page(9, -1, done + 50 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 50)]);
        }

        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        StreamInfo info = Analyzer.CreateDefault().Analyze("speex.spx", new MemoryStream(builder.Bytes), options).Streams.Single();

        StreamMeasurements m = info.Measurements!;
        Assert.Equal(packets * 160L, info.Samples);
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
    }
}
