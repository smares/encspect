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
    private static byte[] Damaged(string name, string finding) => Damaged(name, finding, null);

    // With firstFrameAt, the change is tried at each byte from that offset on instead, so that the first frame is the one that is lost.
    private static byte[] Damaged(string name, string finding, int? firstFrameAt)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack", name));
        int from = firstFrameAt ?? original.Length / 4;
        int to = firstFrameAt + 64 ?? original.Length / 2;
        int step = firstFrameAt is null ? 509 : 1;
        for (int at = from; at < to; at += step)
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

    [Fact]
    public void An_SV7_stream_whose_first_frame_is_lost_withholds_the_frames_after_it()
    {
        StreamInfo info = Measure(Damaged("external-mppenc-standard.mpc", "musepack.frame-length-mismatch", 0x18), out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.Equal(32 * 1152, m.WithheldSamples);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason.AfterGap);
    }

    // Frame 0 of an SV8 packet has no length of its own, so no change of one byte loses it: a packet of 2 bytes of payload put before the first audio packet (at 0x2D) does, since its
    // first frame cannot be read from them.
    [Fact]
    public void An_SV8_stream_whose_first_frame_is_lost_withholds_the_filters_history_of_the_next_packet()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "musepack", "external-mpcenc-packets.mpc"));
        Assert.Equal((byte)'A', original[0x2D]);
        Assert.Equal((byte)'P', original[0x2E]);
        byte[] file = [.. original[..0x2D], (byte)'A', (byte)'P', 5, 0xFF, 0xFF, .. original[0x2D..]];

        StreamInfo info = Measure(file, out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.DoesNotContain(findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Contains(findings, f => f.Code == "musepack.packet-length-mismatch" && f.Message.Contains("first at frame 0 of the packet at 0x2D", StringComparison.Ordinal));
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.True(m.LostSamples > 0);
        Assert.Equal(480, m.WithheldSamples);
        Assert.Equal(m.WithheldSamples, m.WithheldByReason.AfterGap);
    }
}
