using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// Damage in the coded data of a Speex stream: a packet whose frames the walk cannot read is lost (libspeex stops decoding at the frame), the decoder starts over at the next
// packet it is handed and withholds the output that depends on what it lost: 20 frames of 20 ms (docs/design/lossy-decoders.md, "As built: Speex").
public class SpeexGapTests
{
    private static StreamInfo Measure(byte[] file, out IReadOnlyList<Finding> findings)
    {
        using var stream = new MemoryStream(file);
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("damaged.spx", stream, options);
        findings = report.Findings;
        return report.Streams.Single(s => s.Codec.StartsWith("Speex", StringComparison.Ordinal));
    }

    // The first change of one byte at a multiple of 61 from the second tenth of the file that loses samples without losing the whole stream.
    private static byte[] Damaged(string name)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg", name));
        for (int at = original.Length / 10; at < original.Length / 2; at += 61)
        {
            byte[] variant = [.. original];
            variant[at] ^= 0xA5;
            StreamInfo info = Measure(variant, out IReadOnlyList<Finding> findings);
            if (findings.Any(f => f.Code is "speex.frame-invalid" or "speex.frame-overrun") && info.LostSamples > 0 && info.LostSamples < info.Samples / 8)
            {
                return variant;
            }
        }

        throw new InvalidOperationException($"No change of one byte of {name} makes a packet corrupt.");
    }

    [Theory]
    [InlineData("external-speexenc-nb.spx", 160)]
    [InlineData("external-speexenc-wb.spx", 320)]
    [InlineData("external-speexenc-uwb.spx", 640)]
    public void A_corrupt_packet_is_lost_and_the_20_frames_after_it_are_withheld(string name, int frameSize)
    {
        StreamInfo info = Measure(Damaged(name), out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.True(m.WithheldSamples > 0);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason.AfterGap);
        Assert.True(m.WithheldSamples <= 20 * frameSize);
    }

    [Fact]
    public void A_stream_that_ends_in_the_withheld_span_still_keeps_what_the_report_states()
    {
        StreamInfo info = Measure(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg", "sequence-gap.spx")), out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.True(m.WithheldSamples > 0);
    }
}
