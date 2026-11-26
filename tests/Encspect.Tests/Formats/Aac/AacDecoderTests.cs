using Encspect.Core;
using Encspect.Formats.Aac.Decoding;
using Encspect.Host;

namespace Encspect.Tests.Formats.Aac;

// The parts of the AAC LC decoder that a fixture's hash does not explain: the filter bank against the sum it computes, the windows, the tables, the output after a gap and
// the end trimming a container states.
public class AacDecoderTests
{
    [Theory]
    [InlineData(256)]
    [InlineData(2048)]
    public void The_inverse_MDCT_equals_the_sum_it_computes(int n)
    {
        var random = new Random(7);
        var spectrum = new float[n / 2];
        for (int k = 0; k < spectrum.Length; k++)
        {
            spectrum[k] = (float)((random.NextDouble() * 2) - 1) * (1 + (k % 5));
        }

        const float scale = 1f / 1024f;
        var output = new float[n];
        new InverseMdct(n, scale).Transform(spectrum, output);

        double peak = 0;
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int k = 0; k < spectrum.Length; k++)
            {
                sum += spectrum[k] * Math.Cos(2 * Math.PI / n * (i + 0.5 + (n / 4.0)) * (k + 0.5));
            }

            sum *= scale;
            peak = Math.Max(peak, Math.Abs(sum));
            worst = Math.Max(worst, Math.Abs(sum - output[i]));
        }

        Assert.True(worst < peak * 1e-5, $"The largest difference is {worst:G3} for a peak of {peak:G3}.");
    }

    // The two halves of a window add up in power to one where they overlap (ISO/IEC 14496-3 4.6.11.3.2), for both shapes and both lengths.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void A_window_slope_and_its_mirror_add_up_to_one_in_power(int shape, bool isLong)
    {
        float[] rise = isLong ? Tables.LongRise[shape] : Tables.ShortRise[shape];

        Assert.Equal(isLong ? 1024 : 128, rise.Length);
        for (int n = 0; n < rise.Length; n++)
        {
            Assert.InRange((rise[n] * rise[n]) + (rise[rise.Length - 1 - n] * rise[rise.Length - 1 - n]), 0.99999, 1.00001);
        }

        Assert.True(rise[0] < rise[^1]);
    }

    [Fact]
    public void The_tables_hold_what_their_formulas_give()
    {
        Assert.Equal(0f, Tables.Pow43[0]);
        Assert.Equal(16f, Tables.Pow43[8], 4);
        Assert.Equal(1f, Tables.Gain[100]);
        Assert.Equal(2f, Tables.Gain[104]);
        Assert.Equal(0.5f, Tables.Gain[96]);
        Assert.Equal(1f, Tables.IntensityGain[255]);
        Assert.Equal(0.5f, Tables.IntensityGain[255 + 4]);
        // 3-bit parcor coefficients: sin(index / iqfac) with iqfac = (4 - 0.5) / (pi / 2) for the positive indexes and (4 + 0.5) / (pi / 2) for the others.
        Assert.Equal(0f, Tables.Tns[0][4]);
        Assert.Equal(-0.98481, Tables.Tns[0][0], 4);
        Assert.Equal(0.97492, Tables.Tns[0][7], 4);
    }

    private sealed class CollectingSink : IPcmSink
    {
        public SortedDictionary<long, float[]> Blocks { get; } = [];

        public void Write(PcmBlock block) => Blocks[block.Position] = block.Floats.ToArray();

        public void End(long samples)
        {
        }
    }

    private static (Report Report, CollectingSink Sink) Decode(byte[] file, string name)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink } };
        return (Analyzer.CreateDefault().Analyze(name, new MemoryStream(file, writable: false), options), sink);
    }

    private static List<byte[]> Frames(byte[] file)
    {
        var frames = new List<byte[]>();
        for (int at = 0; at + 7 <= file.Length;)
        {
            int length = ((file[at + 3] & 3) << 11) | (file[at + 4] << 3) | (file[at + 5] >> 5);
            frames.Add(file[at..(at + length)]);
            at += length;
        }

        return frames;
    }

    // Bytes that are no frame between two frames lose ADTS sync: the next frame follows a gap, and the decoder withholds its output, which depends on the overlap of a frame
    // it did not get. The frames after it decode to what an undamaged stream gives, since their state is a function of the frame before them alone.
    [Fact]
    public void A_gap_withholds_the_block_after_it_and_the_rest_decodes_as_without_it()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "apple", "apple-passthru-adts-from-fdkaac-m4a.aac"));
        List<byte[]> frames = Frames(file);
        const int At = 12;
        var damaged = new List<byte>();
        for (int i = 0; i < frames.Count; i++)
        {
            if (i == At)
            {
                damaged.AddRange(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            }

            damaged.AddRange(frames[i]);
        }

        (_, CollectingSink clean) = Decode(file, "clean.aac");
        (Report report, CollectingSink gapped) = Decode([.. damaged], "gap.aac");

        Assert.Contains(report.Findings, f => f.Code == "aac.sync-lost");
        Assert.DoesNotContain(clean.Blocks.Keys, k => k % 1024 != 0);
        Assert.Equal(frames.Count, clean.Blocks.Count);
        Assert.Equal(frames.Count - 1, gapped.Blocks.Count);
        Assert.DoesNotContain(At * 1024L, gapped.Blocks.Keys);
        foreach ((long position, float[] samples) in gapped.Blocks)
        {
            Assert.Equal(clean.Blocks[position], samples);
        }
    }

    // A Matroska block's DiscardPadding (RFC 9559, 5.1.3.5.7) is the end trimming of the track's last block: the decoder is told before the frame the padding takes samples from,
    // so the measurements leave those samples out as trimmed and the measured samples are the stream's.
    [Theory]
    [InlineData("matroska/aac-discard-padding.mka", 512)]
    [InlineData("matroska/aac-discard-padding-large.mka", 1024)]
    public void The_end_trimming_of_a_container_is_stated_before_the_frame_it_reaches(string name, long trimmed)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = new MeasureOptions() } };
        StreamInfo info = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options).Streams.Single();

        Assert.Equal((6 * 1024) - trimmed, info.Samples);
        Assert.NotNull(info.Measurements);
        Assert.Equal(trimmed, info.Measurements.TrailingTrimmedSamples);
        Assert.Equal(info.Samples, info.Measurements.Samples + info.Measurements.LostSamples + info.Measurements.WithheldSamples);
    }
}
