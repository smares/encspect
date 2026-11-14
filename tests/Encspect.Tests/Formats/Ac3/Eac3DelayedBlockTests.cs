using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// The last block of a frame with enhanced coupling waits for the next frame's first block. Whether the stream ends, a frame is removed or junk replaces one, what the report states stays
// what the measurements keep, lost and withheld: measured + lost + withheld = Samples.
public class Eac3DelayedBlockTests
{
    private const string Name = "ac3/eac3-signal-ecpl.eac3";

    [Theory]
    [InlineData("whole")]
    [InlineData("dropped")]
    [InlineData("junk")]
    [InlineData("cut-last")]
    public void The_samples_add_up_with_a_block_that_waits(string variant)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, Name));
        int length = 4096;
        byte[] changed = variant switch
        {
            "dropped" => [.. data[..(5 * length)], .. data[(6 * length)..]],
            "junk" => [.. data[..(5 * length)], .. new byte[300], .. data[(6 * length)..]],
            "cut-last" => data[..(data.Length - 1000)],
            _ => data,
        };
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("x.eac3", new MemoryStream(changed), options);

        foreach (StreamInfo info in report.Streams.Where(s => s.Measurements is not null))
        {
            StreamMeasurements m = info.Measurements!;
            Assert.True(info.Samples == m.Samples + m.LostSamples + m.WithheldSamples, $"{variant}: {info.Samples} against {m.Samples} + {m.LostSamples} + {m.WithheldSamples}");
        }

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid");
    }
}
