using Encspect.Core;
using Encspect.Formats.Vorbis.Decoding;

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
        Assert.DoesNotContain(withheldFrom, got.Keys.Where(p => p >= withheldFrom && p < afterGap));
        Assert.True(got.ContainsKey(afterGap));
        for (long p = afterGap; p < afterGap + 200; p++)
        {
            Assert.Equal(whole[p + lostReturned], got[p]);
        }
    }

    // The decoded output of stereo with square polar coupling differs from one without only through the coupling, so the file that has it decodes to channels that are not equal.
    [Fact]
    public void Coupled_channels_decode_to_two_different_channels()
    {
        (List<byte[]> packets, long[] positions) = Source("ogg/external-oggenc-q5.ogg");

        VorbisStreams.RecordingSink sink = VorbisStreams.Decode(VorbisStreams.Ogg(packets, positions)).Sink;

        float[][] block = sink.Blocks[^1].Channels;
        Assert.Equal(2, block.Length);
        Assert.NotEqual(block[0], block[1]);
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
