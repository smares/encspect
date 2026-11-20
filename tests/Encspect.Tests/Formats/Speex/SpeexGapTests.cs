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

    // The first byte of the tenth audio packet becomes the start of a narrowband layer of submode 9, which Speex does not define: the packet is corrupt. The CRC of its page is written
    // again, so the packet is damaged and the page is not (a page that fails its CRC withholds its packets whole: OggDamagedUnitsTests).
    private static byte[] Damaged(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg", name));
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(file));
        Node packets = report.Streams.Single().Nodes[0].Children.Single(c => c.Name == "Audio Packets");
        long at = packets.Children[10].Offset;
        file[at] = (byte)(0x48 | (file[at] & 0x07));
        for (int page = 0; page + 27 <= file.Length;)
        {
            int length = 27 + file[page + 26];
            for (int i = 0; i < file[page + 26]; i++)
            {
                length += file[page + 27 + i];
            }

            if (at < page + length)
            {
                file.AsSpan(page + 22, 4).Clear();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(page + 22), OggBuilder.Crc(file.AsSpan(page, length)));
                break;
            }

            page += length;
        }

        return file;
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
