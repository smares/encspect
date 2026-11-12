using Encspect.Core;
using Encspect.Formats.Vorbis;
using Encspect.Formats.Vorbis.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// The reconstruction of the Vorbis decoder: the transform and the windows against their definitions, and the decoder's output against its own rules for refused
// packets, lost pages and the overlap of blocks of different sizes. Correctness against libvorbis is crosscheck_vorbis_pcm.py's; the hash of each fixture's samples is the
// .decode.json golden's.
public class VorbisDecoderTests
{
    // y[i] = sum over k of X[k] cos(2 pi / n * (i + 1/2 + n/4) * (k + 1/2)), the inverse MDCT of the specification's section 4.3.7 without a factor.
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(2048)]
    public void The_inverse_transform_is_the_sum_of_its_definition(int n)
    {
        var random = new Random(n);
        float[] spectrum = new float[n / 2];
        for (int k = 0; k < spectrum.Length; k++)
        {
            spectrum[k] = (float)(random.NextDouble() - 0.5);
        }

        float[] output = new float[n];
        new InverseMdct(n, 1f).Transform(spectrum, output);

        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int k = 0; k < n / 2; k++)
            {
                sum += spectrum[k] * Math.Cos(2 * Math.PI / n * (i + 0.5 + (n / 4.0)) * (k + 0.5));
            }

            worst = Math.Max(worst, Math.Abs(sum - output[i]));
        }

        Assert.True(worst < 1e-4, $"largest difference {worst}");
    }

    // The slope rises from 0 to 1, and a slope and its mirror image add up in power (Princen-Bradley), which is what lets the overlap of two blocks cancel the aliasing.
    [Theory]
    [InlineData(32)]
    [InlineData(1024)]
    public void The_window_slope_is_power_complementary(int length)
    {
        float[] slope = WindowSlope.Create(length);

        Assert.True(slope[0] < 0.01f && slope[^1] > 0.99f);
        for (int i = 0; i < length; i++)
        {
            Assert.Equal(1.0, (slope[i] * (double)slope[i]) + (slope[length - 1 - i] * (double)slope[length - 1 - i]), 1e-6);
        }
    }

    private static (List<byte[]> Packets, long[] Positions) Source(string name = "ogg/external-oggenc-q5.ogg")
    {
        List<byte[]> packets = VorbisStreams.Packets(name);
        return (packets, VorbisStreams.Positions(packets));
    }

    // The first block returns nothing, and each later block the quarter of its own size and of the one before it: the positions and the count of the whole file.
    [Fact]
    public void The_first_block_returns_nothing_and_the_blocks_return_what_the_overlap_gives()
    {
        (List<byte[]> packets, long[] positions) = Source();

        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(VorbisStreams.Ogg(packets, positions));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid");
        Assert.Equal(positions[^1], sink.Samples);
        Assert.Equal(0, sink.Blocks[0].Position);
        long next = 0;
        foreach ((long position, float[][] channels) in sink.Blocks)
        {
            Assert.Equal(next, position);
            next = position + channels[0].Length;
        }

        Assert.Equal(positions[^1], report.Streams.Single().Verification!.DecodedSamples);
    }

    // libvorbis refuses a packet that is empty, not audio, or names no mode, and returns nothing for it, with no change of its state: the output is that of the
    // stream without it.
    [Fact]
    public void A_packet_a_decoder_refuses_changes_nothing()
    {
        (List<byte[]> packets, long[] positions) = Source();
        VorbisStreams.RecordingSink plain = VorbisStreams.Decode(VorbisStreams.Ogg(packets, positions)).Sink;

        List<byte[]> withRefused = [.. packets];
        withRefused.Insert(10, []);
        withRefused.Insert(20, [0x01, 0x02, 0x03]);
        long[] shifted = VorbisStreams.Positions(withRefused);
        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(VorbisStreams.Ogg(withRefused, shifted));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.Equal(plain.Samples, sink.Samples);
        Dictionary<long, float> expected = VorbisStreams.ByPosition(plain);
        Dictionary<long, float> actual = VorbisStreams.ByPosition(sink);
        Assert.Equal(expected.Count, actual.Count);
        Assert.All(expected, e => Assert.Equal(e.Value, actual[e.Key]));
    }

    // A page that is lost takes the packet in it, and the packet after it overlaps with the packet before it: the decoder resets and withholds the samples the parser counts
    // for that packet, and the packets after it decode as they do in the whole stream (their positions are those the parser gives, which lack the lost packet's samples).
    [Fact]
    public void A_lost_page_withholds_the_packet_after_it_and_the_rest_decodes_as_without_the_loss()
    {
        (List<byte[]> packets, long[] positions) = Source();
        VorbisStreams.RecordingSink plain = VorbisStreams.Decode(VorbisStreams.Ogg(packets, positions)).Sink;
        Dictionary<long, float> whole = VorbisStreams.ByPosition(plain);

        const int Lost = 40;
        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(VorbisStreams.Ogg(packets, positions, new HashSet<int> { Lost }));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Dictionary<long, float> got = VorbisStreams.ByPosition(sink);

        // The packet after the lost one (index Lost - 3 + 1 among the audio packets) returns nothing; the one after it returns its samples as in the whole stream, at
        // positions that lack what the lost packet returned.
        int lostAudio = Lost - 3;
        long lostReturned = positions[lostAudio] - positions[lostAudio - 1];
        long withheldFrom = positions[lostAudio - 1];
        long afterGap = positions[lostAudio + 1] - lostReturned;
        Assert.All(got.Keys, p => Assert.False(p >= withheldFrom && p < afterGap, $"position {p} is inside the withheld span"));
        Assert.True(got.ContainsKey(afterGap));
        for (long p = afterGap; p < afterGap + 200; p++)
        {
            Assert.Equal(whole[p + lostReturned], got[p]);
        }
    }

    // The stress streams of make_vorbis_stress_fixtures.py decode without a failure, the walk and the decoder reading what libvorbis reads (crosscheck_vorbis_pcm.py compares the
    // samples): a residue book without dimensions, and two mappings that mux the channels differently.
    [Theory]
    [InlineData("vorbis-residue-book-without-dimensions.bin")]
    [InlineData("vorbis-two-mappings-different-muxes.bin")]
    public void The_stress_streams_decode_without_failing(string name)
    {
        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", name)));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid" or "decode.frame-too-large");
        Assert.True(sink.Samples > 0);
    }

    // The decoder never writes NaN or infinity, though libvorbis's floor 0 gain is not limited and overflows: random packets over the setup that has a floor 0 (a channel of
    // vorbis-setup-rich.ogg) decode to finite samples for every seed.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_packets_over_a_floor_0_setup_decode_to_finite_samples(int seed)
    {
        List<byte[]> source = VorbisStreams.Packets("ogg/vorbis-setup-rich.ogg");
        Identification id = Identification.Read(source[0])!;
        SetupModel setup = SetupModel.Read(source[2], id.Channels, CancellationToken.None)!;
        var random = new Random(seed);
        var packets = new List<byte[]> { source[0], source[1], source[2] };
        for (int i = 0; i < 120; i++)
        {
            int mode = random.Next(setup.ModeIsLong.Length);
            var bits = new VorbisStreams.Bits().Put(0, 1).Put(mode, setup.ModeBits);
            if (setup.ModeIsLong[mode])
            {
                bits.Put(random.Next(2), 1).Put(random.Next(2), 1);
            }

            byte[] head = bits.Bytes();
            byte[] body = new byte[Math.Max(head.Length, random.Next(1, 700))];
            random.NextBytes(body);
            int headBits = 1 + setup.ModeBits + (setup.ModeIsLong[mode] ? 2 : 0);
            for (int b = 0; b < head.Length; b++)
            {
                int mask = (1 << Math.Min(8, headBits - (8 * b))) - 1;
                body[b] = (byte)((body[b] & ~mask) | (head[b] & mask));
            }

            packets.Add(body);
        }

        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(VorbisStreams.Ogg(packets, new long[packets.Count - 3]));

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
        Assert.True(sink.Samples > 0);
        Assert.All(sink.Blocks, block => Assert.All(block.Channels, channel => Assert.All(channel, value => Assert.True(float.IsFinite(value)))));
    }

    // Where the analyzer's own bounds change what is read, the report says so: a packet that outruns its budget, and a setup that states more codebook entries than its size allows.
    [Fact]
    public void A_packet_cut_at_the_walk_budget_and_a_setup_past_the_entry_bound_are_reported()
    {
        Report capped = Analyzer.CreateDefault().Analyze("capped.ogg", new MemoryStream(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "vorbis-partitions-without-books-255-channels.bin"))));
        Report large = Analyzer.CreateDefault().Analyze("large.ogg", new MemoryStream(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "vorbis-setup-states-millions-of-entries-chained.bin"))));

        Finding walk = Assert.Single(capped.Findings, f => f.Code == "vorbis.walk-capped");
        Assert.True(walk.Count > 1 && walk.Path is not null);
        Assert.Equal(64, large.Findings.Count(f => f.Code == "vorbis.setup-too-large"));
        Assert.All(large.Findings.Where(f => f.Code == "vorbis.setup-too-large"), f => Assert.NotNull(f.Path));
        Assert.DoesNotContain(large.Streams, s => s.Verification is not null);
    }

    // A packet that names more partitions than its bits can pay for is read as ended where the budget runs out, so its walk costs what its bytes allow.
    [Fact]
    public void A_packet_with_partitions_without_books_is_walked_in_time_bounded_by_its_bytes()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "vorbis-partitions-without-books-255-channels.bin"));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Report report = Analyzer.CreateDefault().Analyze("partitions.ogg", new MemoryStream(data));

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The analysis took {watch.Elapsed.TotalSeconds:F1} s.");
    }

    // The configuration a decoder is opened with must hold a header a decoder accepts: a session the parser starts always does, and a stream whose headers are not the decoder's
    // gets no session.
    [Fact]
    public void A_stream_whose_setup_header_libvorbis_refuses_is_not_decoded()
    {
        (Report report, VorbisStreams.RecordingSink sink) = VorbisStreams.Decode(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ogg/vorbis-setup-invalid.ogg")));

        Assert.Equal(0, sink.Samples);
        Assert.All(report.Streams.Where(s => s.Codec == "Vorbis"), s => Assert.Null(s.Verification));
        Assert.Contains(report.Streams, s => s.Codec == "Vorbis");
    }
}
