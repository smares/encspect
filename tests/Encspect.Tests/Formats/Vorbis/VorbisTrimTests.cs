using System.Globalization;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests;

// How the Vorbis parser counts its start and end trimming, against what libvorbis counts.
public class VorbisTrimTests
{
    private static Node? Find(Node node, string name) =>
        node.Name == name ? node : node.Children.Select(c => Find(c, name)).FirstOrDefault(n => n is not null);

    // libvorbis continues its sample count from every page's granule position, so a repeated packet or a jump of the positions in
    // the middle is not charged to the end trimming (oggdec writes 30,220 and 29,196 samples for these two files). Counted from the
    // first page, the trims would be 1,524 (capped at the 1,024 the last packet returns, with vorbis.end-trimming-large) and 800.
    [Theory]
    [InlineData("ogg/vorbis-packet-repeated.ogg", 30_720, 500, 30_220)]
    [InlineData("ogg/vorbis-granule-midstream.ogg", 29_696, 500, 29_196)]
    public void The_end_trimming_is_counted_from_the_latest_granule_position(string name, long decoded, long trim, long samples)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.Equal(samples, report.Streams.Single().Samples);
        Assert.Equal($"{decoded} samples", Find(report.Root, "Coded Samples")?.Value?.ToString());
        Assert.Equal($"{trim} samples", Find(report.Root, "End Trimming")?.Value?.ToString());
        Assert.Contains(report.Findings, f => f.Code == "vorbis.granule-inconsistent");
        Assert.DoesNotContain(report.Findings, f => f.Code == "vorbis.end-trimming-large");
    }

    // The same, for what libvorbis does not take: a position at the smallest 64-bit value (the count continues from it, so the last
    // position is beyond it and nothing is trimmed, with no figure that overflows), a position on an empty packet (libvorbis is not
    // given it and loses its count at the packet after), and an end-of-stream page written twice (it trims at the first and loses
    // its count at the hole libogg reports for the copy). The samples are those oggdec writes.
    [Theory]
    [InlineData("ogg/vorbis-granule-extreme.ogg", 29_696, 0, 29_696)]
    [InlineData("ogg/vorbis-empty-carrier.ogg", 29_696, 0, 29_696)]
    [InlineData("ogg/vorbis-end-page-repeated.ogg", 39_936, 500, 39_436)]
    public void The_end_trimming_follows_the_positions_libvorbis_takes(string name, long decoded, long trim, long samples)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.Equal(samples, report.Streams.Single().Samples);
        Assert.Equal($"{decoded} samples", Find(report.Root, "Coded Samples")?.Value?.ToString());
        Assert.Equal($"{trim} samples", Find(report.Root, "End Trimming")?.Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code == "vorbis.end-trimming-large");
        Assert.Equal(name.Contains("extreme", StringComparison.Ordinal), report.Findings.Any(f => f.Code == "vorbis.granule-beyond-packets"));
    }

    // The same, for the positions libvorbis takes at an end-of-stream page and after it loses its count (block.c, vorbis_synthesis_blockin).
    // oggdec writes 28,896 for the first two files and 28,172, 28,672 and 25,684 for the third, the fifth and the last (the last trims what the fifth packet returns, 576, not the 128 of the first two blocks); the libvorbis run with the granule
    // positions writes 29,696 for vorbis-skip-before-first (oggdec 28,672, a reader behavior like the one of vorbis-packet-errors.ogg).
    [Theory]
    [InlineData("ogg/vorbis-eos-then-eos.ogg", 0, 800, 0, 28_896)]
    [InlineData("ogg/vorbis-eos-mid-then-more.ogg", 0, 800, 0, 28_896)]
    [InlineData("ogg/vorbis-first-extreme.ogg", 1_024, 500, 0, 28_172)]
    [InlineData("ogg/vorbis-skip-before-first.ogg", 0, 0, 0, 29_696)]
    [InlineData("ogg/vorbis-skip-then-small.ogg", 0, 0, 1_024, 28_672)]
    [InlineData("ogg/vorbis-short-start-trim.ogg", 576, 300, 0, 25_684)]
    public void The_trims_follow_where_libvorbis_counts_from(string name, long start, long end, long gap, long samples)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.Equal(samples, report.Streams.Single().Samples);
        Assert.Equal($"{start} samples", Find(report.Root, "Start Trimming")?.Value?.ToString());
        Assert.Equal($"{end} samples", Find(report.Root, "End Trimming")?.Value?.ToString());
        Assert.Equal(gap == 0 ? null : $"{gap} samples", Find(report.Root, "Gap Trimming")?.Value?.ToString());
        Assert.Equal(name.Contains("first-extreme", StringComparison.Ordinal) || name.Contains("short-start-trim", StringComparison.Ordinal), report.Findings.Any(f => f.Code == "vorbis.start-trimming-large"));
    }

    // The pages after an end-of-stream page follow from the position libvorbis took there, so none disagrees with it.
    [Fact]
    public void A_page_after_an_end_of_stream_page_is_compared_with_its_position()
    {
        Report report = Fixtures.AnalyzeMpeg("ogg/vorbis-eos-mid-then-more.ogg");

        Assert.DoesNotContain(report.Findings, f => f.Code == "vorbis.granule-inconsistent");
    }

    private static StreamInfo Measured(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Single();
    }

    // Matroska's DiscardPadding of the last block is the end trimming the decoder is told before the last packet (the feed runs one packet behind),
    // so the measurements leave those samples out as trailing trimmed samples: external-ffmpeg-vorbis states 986 (oggdec and FFmpeg write 66,150 for
    // the same packets in Ogg and in Matroska); the hand-built files carry 100 and a padding larger than the last packet returns, which is capped.
    [Theory]
    [InlineData("matroska/external-ffmpeg-vorbis.mka", 67_136, 986)]
    [InlineData("matroska/external-ffmpeg-vorbis-webm.webm", 67_136, 986)]
    [InlineData("matroska/vorbis-discard-padding.mka", -1, 100)]
    [InlineData("matroska/vorbis-discard-padding-large.mka", -1, -1)]
    public void The_container_padding_is_trimmed_from_the_measurements(string name, long coded, long trim)
    {
        StreamInfo info = Measured(name);
        StreamMeasurements m = info.Measurements!;

        long codedSamples = coded >= 0 ? coded : m.Samples + m.TrimmedSamples;
        long expected = trim >= 0 ? trim : codedSamples - info.Samples;
        Assert.Equal(expected, m.TrailingTrimmedSamples);
        Assert.Equal((0L, 0L), (m.LeadingTrimmedSamples, m.InnerTrimmedSamples));
        Assert.Equal(info.Samples, m.Samples);
        Assert.Equal(info.Samples + expected, codedSamples);
        Assert.Equal(codedSamples, info.Verification!.DecodedSamples);
    }

    // The last packet of vorbis-discard-padding-large returns 128 samples; its padding of 2,646 is capped at them, with a finding.
    [Fact]
    public void A_padding_larger_than_the_last_packet_is_capped_with_a_finding()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/vorbis-discard-padding-large.mka");

        Assert.Contains(report.Findings, f => f.Code == "vorbis.end-trimming-large");
        Assert.Equal("128 samples", Find(report.Root, "End Trimming")?.Value?.ToString());
        Assert.Equal(128, Measured("matroska/vorbis-discard-padding-large.mka").Measurements!.TrailingTrimmedSamples);
    }

    // Gap Trimming: a position below the samples counted since the count was lost discards the difference at the start of the packet that carries
    // it (block.c, "trim the beginning"), which the measurements leave out as inner trimmed samples, counted apart from the end trimming.
    [Fact]
    public void Gap_trimming_is_an_inner_trimmed_range_of_the_measurements()
    {
        const string name = "ogg/vorbis-skip-then-small.ogg";
        Report report = Fixtures.AnalyzeMpeg(name);
        Assert.Equal("1024 samples", Find(report.Root, "Gap Trimming")?.Value?.ToString());

        StreamInfo info = Measured(name);
        StreamMeasurements m = info.Measurements!;
        Assert.Equal((1024L, 0L, 0L), (m.InnerTrimmedSamples, m.LeadingTrimmedSamples, m.TrailingTrimmedSamples));
        Assert.Equal((info.Samples, 0L, 0L, 0), (m.Samples, m.LostSamples, m.WithheldSamples, m.Gaps));
    }
}
