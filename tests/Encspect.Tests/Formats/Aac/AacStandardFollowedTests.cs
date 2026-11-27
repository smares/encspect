using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.Aac.Decoding;
using Encspect.Host;

namespace Encspect.Tests.Formats.Aac;

// Where the decoder follows ISO/IEC 14496-3 and not libxaac's order of the tools (docs/formats/aac.md, "Decoding"): each test decodes a hand-built file whose blocks the standard makes
// equal, between silent blocks so that a block's output depends on its own spectra alone.
public class AacStandardFollowedTests
{
    private sealed class CollectingSink : IPcmSink
    {
        public SortedDictionary<long, float[]> Blocks { get; } = [];

        public void Write(PcmBlock block) => Blocks[block.Position] = block.Floats.ToArray();

        public void End(long samples)
        {
        }
    }

    private static (Report Report, CollectingSink Sink) Decode(string name)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink } };
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", name));
        return (Analyzer.CreateDefault().Analyze(name, new MemoryStream(file, writable: false), options), sink);
    }

    private static float[] Channel(CollectingSink sink, int block, int channel) => sink.Blocks[block * 1024L].AsSpan(channel * 1024, 1024).ToArray();

    private static float Peak(float[] samples) => samples.Max(Math.Abs);

    // 4.6.8.2.3: invert_intensity is 1 - 2 * ms_used only for ms_mask_present 1, and 1 for mask 2: block 1 (mask 2) equals block 3 (mask 1, flags on the bands of mid/side only).
    [Fact]
    public void Intensity_stereo_is_not_inverted_under_mask_2()
    {
        (Report report, CollectingSink sink) = Decode("stereo-tool-order.aac");

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) && f.Severity >= Severity.Warning);
        for (int channel = 0; channel < 2; channel++)
        {
            float[] mask2 = Channel(sink, 1, channel);
            Assert.True(Peak(mask2) > 0.001f);
            Assert.Equal(mask2, Channel(sink, 3, channel));
        }
    }

    // 4.6.8.1.3 and 4.6.13.3: no mid/side on a band where a channel codes noise, so the second channel's Huffman bands are those of the same pair without a mask (block 5 against 7).
    [Fact]
    public void Mid_side_skips_a_band_where_only_the_first_channel_codes_noise()
    {
        (_, CollectingSink sink) = Decode("stereo-tool-order.aac");

        float[] flagged = Channel(sink, 5, 1);
        Assert.True(Peak(flagged) > 0.001f);
        Assert.Equal(flagged, Channel(sink, 7, 1));
    }

    // Figure 4.2: noise substitution comes before intensity stereo, which scales what the first channel holds, noise included: with position 0 the second channel equals the first
    // (block 9), and the flags under mask 1 invert it (block 11).
    [Fact]
    public void Intensity_stereo_copies_the_noise_of_the_first_channel()
    {
        (_, CollectingSink sink) = Decode("stereo-tool-order.aac");

        float[] left = Channel(sink, 9, 0);
        Assert.True(Peak(left) > 0.001f);
        Assert.Equal(left, Channel(sink, 9, 1));
        float[] inverted = Channel(sink, 11, 0);
        Assert.True(Peak(inverted) > 0.001f);
        Assert.Equal(inverted.Select(x => -x), Channel(sink, 11, 1));
    }

    // 4.6.9.3: a filter of an order above 12 takes its first 12 coefficients: the blocks are decoded, not refused, and the blocks of order 13, 20 and 31 (2 to 4, after one of order 12
    // with the same spectrum) equal the block of order 12 (1) sample for sample.
    [Fact]
    public void A_TNS_filter_of_an_order_above_12_is_clamped_to_12()
    {
        (Report report, CollectingSink sink) = Decode("tns-order-above-12.aac");

        Assert.Equal(5, sink.Blocks.Count);
        Assert.All(sink.Blocks.Values, block => Assert.All(block, x => Assert.True(float.IsFinite(x))));
        Assert.True(Peak(Channel(sink, 1, 0)) > 0.001f);
        for (int block = 2; block <= 4; block++)
        {
            Assert.Equal(Channel(sink, 1, 0), Channel(sink, block, 0));
        }

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "decode.frame-invalid");
        Assert.Contains(report.Findings, f => f.Code == "aac.tns-order-too-high");
    }

    // 4.6.11.3.2: the first block's left window half has its own shape, here Kaiser-Bessel derived: its first 1,024 samples are the inverse transform of the one line times the rise.
    [Fact]
    public void The_first_blocks_left_half_has_its_own_window_shape()
    {
        (_, CollectingSink sink) = Decode("first-block-kbd.aac");

        // Line 2 of band 0, quantized value 1 at the scalefactor 138 (the generator's 140 - 2): 2^((138 - 100) / 4).
        var spectrum = new float[1024];
        spectrum[2] = (float)Math.Pow(2, (138 - 100) / 4.0);
        var raw = new float[2048];
        new InverseMdct(2048, 1f / (1024f * 32768f)).Transform(spectrum, raw);
        float[] output = Channel(sink, 0, 0);
        float[] kbd = Tables.LongRise[1];
        float[] sine = Tables.LongRise[0];
        double worst = 0;
        double sineGap = 0;
        for (int n = 0; n < 1024; n++)
        {
            worst = Math.Max(worst, Math.Abs(output[n] - (raw[n] * kbd[n])));
            sineGap = Math.Max(sineGap, Math.Abs((raw[n] * sine[n]) - (raw[n] * kbd[n])));
        }

        Assert.True(sineGap > 10 * worst, "The shapes must differ for the test to mean anything.");
        Assert.True(worst < Peak(output) * 1e-4, $"The first block's left half is off its own shape by {worst:G3} (peak {Peak(output):G3}).");
    }

    // 4.5.2.8.2.2 and 1.6.5.2: the SBR data after a coupling channel element is read as a single channel element's and signals SBR, so the stream is HE-AAC and no finding says
    // the payload follows no element.
    [Fact]
    public void SBR_data_after_a_coupling_channel_element_is_read_and_counts()
    {
        (Report report, _) = Decode("sbr-after-coupling-channel.aac");

        StreamInfo stream = report.Streams.Single();
        Assert.Equal("HE-AAC", stream.Codec);
        Assert.DoesNotContain(report.Findings, f => f.Code == "aac.sbr-without-channel-element");
        Assert.Contains(report.Findings, f => f.Code == "aac.implicit-sbr");
    }

    // 4.5.2.8.2.2: a dependently switched coupling channel element has no SBR element of its own: sbr-after-coupling-element.aac alternates the two kinds, so the payloads after the
    // dependent ones (6 of 12) are reported and still count as evidence.
    [Fact]
    public void SBR_data_after_a_dependently_switched_coupling_channel_element_is_reported()
    {
        (Report report, _) = Decode("sbr-after-coupling-element.aac");

        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.sbr-without-channel-element");
        Assert.Contains("6 raw data blocks", finding.Message);
        Assert.Equal("HE-AAC", report.Streams.Single().Codec);
    }

    // 4.5.2.8.1: an EXT_SBR_DATA_CRC payload's CRC-10 is verified, and the blocks that fail it are decoded and withheld from the measurements as units whose checksum fails.
    [Fact]
    public void The_CRC_of_an_SBR_payload_is_verified()
    {
        (Report report, _) = Decode("sbr-crc-checked.aac");

        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.sbr-crc-mismatch");
        Assert.Contains("2 raw data blocks", finding.Message);
        // The two failing blocks (2 and 4) and the blocks after them (their overlap) are decoded and left out of the measurements: 4 blocks of 2,048 samples.
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "sbr-crc-checked.aac"));
        StreamInfo measured = Analyzer.CreateDefault().Analyze("sbr-crc-checked.aac", new MemoryStream(file, writable: false), new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } }).Streams.Single();
        Assert.Equal(4 * 2048, measured.Measurements!.WithheldSamples);
        ChecksumTally tally = Assert.Single(report.Streams.Single().Verification!.Checksums, c => c.Name == "SBR CRC-10");
        Assert.Equal(6, tally.Stored);
        Assert.Equal(2, tally.Failed);
    }

    // The CRC-10 of 4.5.2.8.1 (generator x^10 + x^9 + x^5 + x^4 + x + 1, initial value 0): a single one bit gives the polynomial's low bits, the CRC of the CRC's bits appended is 0.
    [Fact]
    public void The_CRC_10_is_the_one_of_the_polynomial()
    {
        static AacBits Bits(params byte[] bytes) => new(bytes, 0, bytes.Length * 8);

        Assert.Equal(0, Crc10.Of(Bits(0, 0)));
        // 7 bits of zero, then a one: x^0 shifted through the register leaves the polynomial's remainder 0x233.
        Assert.Equal(0x233, Crc10.Of(new AacBits([0x01], 0, 8)));
        int crc = Crc10.Of(Bits(0xA5, 0x3C));
        Assert.Equal(0, Crc10.Of(new AacBits([0xA5, 0x3C, (byte)(crc >> 2), (byte)((crc & 3) << 6)], 0, 26)));
    }

    // 4.5.2.1.5.2: a fill element with an SBR payload holds no other payload.
    [Fact]
    public void A_fill_element_with_an_SBR_payload_and_another_one_is_reported()
    {
        (Report report, _) = Decode("fill-sbr-with-other-payload.aac");

        Assert.Contains(report.Findings, f => f.Code == "aac.fill-sbr-with-other-payload");
    }

    // 4.6.18.3.6: the frequency tables of a header need at most five patches.
    [Fact]
    public void A_header_that_needs_six_patches_is_reported()
    {
        (Report report, _) = Decode("sbr-too-many-patches.aac");

        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.sbr-header-invalid");
        Assert.Contains("6 patches", finding.Message);
    }

    // 4.5.2.1.3.1: the LFE element's restrictions: the last two blocks break them, the first two keep to them.
    [Fact]
    public void An_LFE_channel_stream_that_breaks_its_restrictions_is_reported()
    {
        (Report report, _) = Decode("lfe-restrictions-pce.aac");

        Finding finding = Assert.Single(report.Findings, f => f.Code == "aac.lfe-restrictions");
        Assert.Contains("2 raw data blocks", finding.Message);
    }
}
