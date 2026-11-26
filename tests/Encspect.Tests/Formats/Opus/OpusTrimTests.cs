using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Formats.Opus;

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

    // The crafted files of the gate-0.3 review, made from the 20 ms hybrid fixture: the last granule position lowered so that the end trimming (20,000 samples) is longer than the
    // 5,760 samples the parser holds back, and the first two audio packets replaced by packets of two frames that break RFC 6716's rule R3 (the decoder cannot read them).
    [Fact]
    public void An_end_trimming_longer_than_the_held_packets_is_capped_and_the_measurements_keep_what_the_report_states()
    {
        byte[] file = Rewrite("external-libopus-stereo-hybrid-20ms.opus", lastGranuleDelta: -20_000, brokenAudioPackets: 0);
        StreamInfo info = AnalyzeBytes(file, out IReadOnlyList<Finding> findings);

        StreamMeasurements m = info.Measurements!;
        Assert.Equal(MeasurementStatus.Complete, m.Status);
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
        Assert.Contains(findings, f => f.Code == "opus.end-trimming-large" && f.Message.Contains("trims at most", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_packets_before_the_first_readable_one_are_lost_samples_of_the_measurements()
    {
        byte[] file = Rewrite("external-libopus-stereo-hybrid-20ms.opus", lastGranuleDelta: 0, brokenAudioPackets: 2);
        StreamInfo info = AnalyzeBytes(file, out _);

        // Each broken packet states two frames of 20 ms (code 1), 1,920 samples; the first 312 of the 3,840 are inside the pre-skip. The measurements are incomplete (two frames were
        // not decoded) but keep what the report states.
        StreamMeasurements m = info.Measurements!;
        Assert.Equal(MeasurementStatus.Incomplete, m.Status);
        Assert.Equal((2 * 1920) - 312, m.LostSamples);
        Assert.Equal(info.Samples, m.Samples + m.LostSamples + m.WithheldSamples);
    }

    private static StreamInfo AnalyzeBytes(byte[] file, out IReadOnlyList<Finding> findings)
    {
        using var stream = new MemoryStream(file);
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        Report report = Analyzer.CreateDefault().Analyze("crafted.opus", stream, options);
        findings = report.Findings;
        return report.Streams.Single(s => s.Codec == "Opus");
    }

    // The fixture's pages again, each with its packets as they are, except that the last page's granule position moves by lastGranuleDelta and the first brokenAudioPackets packets
    // after the two header packets become 20 ms packets of code 1 with an odd payload.
    private static byte[] Rewrite(string name, long lastGranuleDelta, int brokenAudioPackets)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg", name));
        var builder = new OggBuilder();
        int packetIndex = 0;
        for (int at = 0; at < data.Length;)
        {
            int segments = data[at + 26];
            long granule = BitConverter.ToInt64(data, at + 6);
            uint serial = BitConverter.ToUInt32(data, at + 14);
            byte flags = data[at + 5];
            int body = at + 27 + segments;
            var packets = new List<byte[]>();
            int length = 0;
            int start = body;
            for (int i = 0; i < segments; i++)
            {
                length += data[at + 27 + i];
                if (data[at + 27 + i] < 255)
                {
                    byte[] packet = data[start..(start + length)];
                    if (packetIndex >= 2 && packetIndex < 2 + brokenAudioPackets)
                    {
                        packet = [(byte)((packet[0] & 0xFC) | 1), 1, 2, 3];
                    }

                    packets.Add(packet);
                    packetIndex++;
                    start += length;
                    length = 0;
                }
            }

            bool isLast = start + length >= data.Length;
            builder.Page(serial, isLast ? granule + lastGranuleDelta : granule, flags, [.. packets]);
            at = start;
        }

        return builder.Bytes;
    }
}
