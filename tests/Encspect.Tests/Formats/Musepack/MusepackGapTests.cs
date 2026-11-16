using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// Damage in the coded data of a Musepack stream: a frame the walk cannot read is lost (libmpcdec stops decoding at it), the decoder starts again at the next frame it is
// handed and withholds the output that depends on what it lost (docs/design/lossy-decoders.md, decisions 4 and 15): 32 frames in SV4 to SV7, 480 samples in SV8.
public class MusepackGapTests
{
    private static StreamInfo Measure(byte[] file, out IReadOnlyList<Finding> findings)
    {
        using var stream = new MemoryStream(file);
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("damaged.mpc", stream, options);
        findings = report.Findings;
        return report.Streams.Single(s => s.Codec.StartsWith("Musepack", StringComparison.Ordinal));
    }

    // The first change of one byte at a multiple of 509 from the quarter of the file that loses samples without losing the whole stream.
    private static byte[] Damaged(string name, string finding)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack", name));
        for (int at = original.Length / 4; at < original.Length / 2; at += 509)
        {
            byte[] variant = [.. original];
            variant[at] ^= 0xA5;
            StreamInfo info = Measure(variant, out IReadOnlyList<Finding> findings);
            if (findings.Any(f => f.Code == finding) && info.LostSamples > 0 && info.LostSamples < info.Samples)
            {
                return variant;
            }
        }

        throw new InvalidOperationException($"No change of one byte of {name} gives {finding}.");
    }

    [Fact]
    public void An_SV7_frame_that_takes_other_bits_than_its_length_is_lost_and_the_32_frames_after_it_are_withheld()
    {
        StreamInfo info = Measure(Damaged("external-mppenc-standard.mpc", "musepack.frame-length-mismatch"), out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.True(m.WithheldSamples > 0);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason.AfterGap);
        Assert.True(m.WithheldSamples <= 32 * 1152);
    }

    [Fact]
    public void An_SV8_packet_with_a_damaged_frame_loses_the_frames_after_it_and_the_next_packet_withholds_the_filters_history()
    {
        StreamInfo info = Measure(Damaged("external-mpcenc-packets.mpc", "musepack.packet-length-mismatch"), out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.Equal(480, m.WithheldSamples);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason.AfterGap);
    }
}
