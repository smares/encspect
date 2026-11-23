using Encspect.Core;
using Encspect.Formats.Tak.Decoding.Lossless;
using Encspect.Host;
using Builder = Encspect.Tests.TakBuilder;
using Encoder = Encspect.Tests.TakEncoder;

namespace Encspect.Tests;

// The TAK decoder against the worked examples of the clean-room description (the residual codes, the filter's coefficients and the prediction they give) and against
// TakEncoder, an inverse of the same description, for every residual mode, prediction order, difference order, wasted bits and stereo mode, the paired entries of the channel
// table that no file uses, and sample rates that set the partition and subframe lengths; and the damage it must report as findings. crosscheck_tak_pcm.py is what holds the
// description to the files of the TAK SDK's decoder and FFmpeg.
public class TakDecoderTests
{
    private sealed class CollectingSink : IPcmSink
    {
        public List<int>[] Channels { get; private set; } = [];

        public List<long> Starts { get; } = [];

        public PcmFormat? Format { get; private set; }

        public IPcmSink Declare(DecodedStream stream)
        {
            Format = stream.Format;
            Channels = [.. Enumerable.Range(0, stream.Format.Channels).Select(_ => new List<int>())];
            return this;
        }

        public void Write(PcmBlock block)
        {
            Starts.Add(block.Position);
            for (int c = 0; c < block.Channels; c++)
            {
                Channels[c].AddRange(block.IntegerChannel(c).ToArray());
            }
        }

        public void End(long samples)
        {
        }
    }

    private static (Report Report, CollectingSink Sink) Decode(byte[] file, MeasureOptions? measure = null)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare, Measure = measure } };
        return (Analyzer.CreateDefault().Analyze("test.tak", new MemoryStream(file), options), sink);
    }

    private static string[] DecodeCodes(Report report) => [.. report.Findings.Select(f => f.Code).Where(c => c.StartsWith("decode.", StringComparison.Ordinal) || c.StartsWith("tak.", StringComparison.Ordinal) && c is not "tak.frame-bytes-after-data").Order(StringComparer.Ordinal)];

    private static int[][] Signal(string kind, int channels, int bits, int count, int seed = 0)
    {
        int max = (1 << (bits - 1)) - 1;
        int min = -(1 << (bits - 1));
        var random = new Random((bits * 1000) + (channels * 10) + kind.Length + seed);
        var samples = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            samples[c] = new int[count];
            for (int t = 0; t < count; t++)
            {
                samples[c][t] = kind switch
                {
                    "silence" => 0,
                    "sine" => (int)(Math.Sin((t * 0.01) + c) * max * 0.6) + random.Next(-3, 4),
                    "noise" => random.Next(min, max + 1),
                    "extremes" => (t + c) % 2 == 0 ? min : max,
                    "ramp" => Math.Clamp(((t * 37) % (2 * max)) - max, min, max),
                    "bursts" => (t / 300) % 2 == 0 ? 0 : random.Next(min / 4, (max / 4) + 1),
                    // Multiples of 8: three wasted bits where every sample of a channel has them.
                    "lowbits" => ((int)(Math.Sin((t * 0.02) + c) * max * 0.5) >> 3) << 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
            }
        }

        return samples;
    }

    private static Encoder.Settings Plain(int codec = 2, int bits = 16, int rate = 8000) => new() { Codec = codec, Bits = bits, Rate = rate };

    private static void AssertDecodesTo(byte[] file, int[][] samples, string? name = null)
    {
        (Report report, CollectingSink sink) = Decode(file);

        Assert.True(DecodeCodes(report).Length == 0, $"{name}: {string.Join(", ", DecodeCodes(report))}");
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(samples[0].Length, verification.DecodedSamples);
        for (int c = 0; c < samples.Length; c++)
        {
            Assert.True(samples[c].SequenceEqual(sink.Channels[c]), $"{name}: channel {c} differs");
        }
    }

    // The worked examples of the description, section 12.

    [Fact]
    public void TheResidualCodesOfTheDescriptionDecodeToItsValues()
    {
        byte[] data = [0xD7, 0x76, 0xE7, 0x07, 0xA0, 0x01];
        var bits = new LsbBitCursor(data);
        int[] values = new int[5];

        ResidualCodes.ReadRun(ref bits, 5, values);

        Assert.Equal([-4, -7, 8, 13, 36], values);
        Assert.Equal(41, bits.Position);
    }

    [Fact]
    public void TheCoefficientsAndThePredictionOfTheDescriptionHold()
    {
        int[] k = [-480, 400, -16, 32];
        int[] terms = new int[4];
        short[] coefficients = new short[4];

        Reconstruction.Coefficients(k, 10, terms, coefficients);

        Assert.Equal([-55584, 29017, -4494, 2048], terms);
        // The coefficient of the value d places back, oldest value first: c4, c3, c2, c1.
        Assert.Equal([-64, 140, -907, 1737], coefficients);
        short[] history = [1000, 1100, 1180, 1240];
        int sum = 512 + Reconstruction.Dot(history, coefficients);
        Assert.Equal(1174132, sum);
        Assert.Equal(1146, sum >> 10);
        Assert.Equal(1143, Reconstruction.Clip14(sum >> 10) - 3);
    }

    [Fact]
    public void TheResidualTableFollowsThePatternOfTheDescription()
    {
        Assert.Equal((1, 1u, 1u, 3u, 8u), ResidualCodes.Row(1));
        Assert.Equal((2, 3u, 1u, 7u, 6u), ResidualCodes.Row(2));
        Assert.Equal((3, 5u, 2u, 14u, 13u), ResidualCodes.Row(3));
        for (int m = 4; m <= 50; m++)
        {
            (int k, uint t, uint s, uint a, uint b) = ResidualCodes.Row(m);
            if (m % 2 == 1)
            {
                int j = (m - 1) / 2;
                Assert.Equal((j + 2, 11u << (j - 2), 1u << j, 7u << j, 25u << (j - 2)), (k, t, s, a, b));
            }
            else
            {
                int j = (m - 2) / 2;
                Assert.Equal((j + 2, 3u << (j - 1), 3u << (j - 1), 13u << (j - 1), 3u << (j + 2)), (k, t, s, a, b));
            }
        }
    }

    [Theory]
    [InlineData(8000, 128, 32)]
    [InlineData(11025, 96, 48)]
    [InlineData(22050, 88, 88)]
    [InlineData(32000, 128, 128)]
    [InlineData(44100, 88, 176)]
    [InlineData(48000, 96, 192)]
    [InlineData(96000, 188, 376)]
    [InlineData(192000, 376, 752)]
    public void ThePartitionAndSubframeLengthsAreThoseOfTheDescriptionsTable(int rate, int partition, int unit)
    {
        Assert.Equal(partition, ResidualCodes.PartitionLength(rate));
        Assert.Equal(partition, Encoder.PartitionLength(rate));
        Assert.Equal(unit, Encoder.Unit(rate));
    }

    // Round trips.

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string kind in new[] { "silence", "sine", "noise", "extremes", "ramp", "bursts", "lowbits" })
        {
            foreach (int bits in new[] { 8, 16, 24 })
            {
                foreach (int channels in new[] { 1, 2 })
                {
                    data.Add(kind, bits, channels);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodedSamplesAreTheEncodedOnes(string kind, int bits, int channels)
    {
        int[][] samples = Signal(kind, channels, bits, 1300);
        foreach (int seed in new[] { 1, 2, 3 })
        {
            byte[] file = Encoder.File(samples, Plain(2, bits), seed);

            AssertDecodesTo(file, samples, $"{kind} {bits} bits {channels} channels seed {seed}");
        }
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(22050)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void EverySampleRateSetsItsPartitionAndSubframeLengths(int rate)
    {
        int[][] samples = Signal("sine", 2, 16, 2100);
        foreach (int seed in new[] { 1, 2, 3, 4 })
        {
            AssertDecodesTo(Encoder.File(samples, Plain(2, 16, rate), seed, sizeType: 8), samples, $"{rate} Hz seed {seed}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryStereoModeRoundTrips(int mode)
    {
        int[][] samples = Signal("sine", 2, 16, 1536);
        foreach (int seed in new[] { 1, 2, 3 })
        {
            // Frames of 512 samples, so that the filter modes (which need 257) are possible in every frame.
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { StereoMode = mode }, seed), samples, $"mode {mode} seed {seed}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFilterModeRoundTripsAtBothOrdersAndWithTheStereoBitSet(bool isLong)
    {
        int[][] samples = Signal("noise", 2, 16, 1024);
        foreach (int mode in new[] { 6, 7 })
        {
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { StereoMode = mode, LongFilter = isLong, StereoBit = isLong }, mode), samples, $"filter mode {mode}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void EveryPredictionOrderRoundTrips(int index)
    {
        int[][] samples = Signal("sine", 2, 16, 3072);
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
        {
            // 2,048-sample frames with up to eight subframes of the unit's multiples: orders up to 256 fit the longer ones, and a subframe continues from the one before it.
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { OrderIndex = index, Prediction = 1, Rate = 44100 }, seed, sizeType: 9), samples, $"order index {index} seed {seed}");
        }
    }

    [Fact]
    public void ContinuedSubframesAndWarmUpsBothOccur()
    {
        int[][] samples = Signal("sine", 1, 16, 4096);
        foreach (int seed in Enumerable.Range(1, 12))
        {
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { Prediction = 1, Subframes = 8, Rate = 44100 }, seed, sizeType: 4), samples, $"seed {seed}");
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { Prediction = 1, Subframes = 8, WithoutContinuing = true, Rate = 44100 }, seed, sizeType: 4), samples, $"warm-up seed {seed}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    public void EveryResidualModeRoundTrips(int mode)
    {
        // Small values, which every row can write: with an escape in the rows whose thresholds they pass.
        int[][] samples = Signal("noise", 1, 16, 1300);
        for (int c = 0; c < samples.Length; c++)
        {
            for (int t = 0; t < samples[c].Length; t++)
            {
                samples[c][t] = (samples[c][t] >> 8) + ((t % 97) == 0 ? 2000 : 0);
            }
        }

        AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { ResidualMode = mode, Prediction = 0, DifferenceOrder = 0, WastedBits = 0, Subframes = 3 }, mode), samples, $"mode {mode}");
    }

    [Fact]
    public void LongEscapesAndEveryChangeOfModeBetweenPartitionsRoundTrip()
    {
        int[][] samples = Signal("bursts", 2, 24, 4200);
        foreach (int seed in Enumerable.Range(1, 15))
        {
            AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { Bits = 24, Partitions = 1, Prediction = 0.5, Rate = 44100 }, seed, sizeType: 4), samples, $"seed {seed}");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void WastedBitsAndDifferenceOrdersRoundTrip(int wasted, int order)
    {
        int[][] samples = Signal("lowbits", 2, 16, 1300);
        for (int c = 0; c < 2; c++)
        {
            for (int t = 0; t < samples[c].Length; t++)
            {
                samples[c][t] = (samples[c][t] >> wasted) << wasted;
            }
        }

        AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { WastedBits = wasted, DifferenceOrder = order }, wasted + order), samples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    public void FramesOfFewerThan16SamplesAreStored(int last)
    {
        foreach (int bits in new[] { 8, 16, 24 })
        {
            int[][] samples = Signal("noise", 2, bits, 512 + last);

            AssertDecodesTo(Encoder.File(samples, Plain(2, bits), 1), samples, $"{bits} bits, last frame of {last}");
        }
    }

    public static TheoryData<string> Tables() => new() { "alone", "reordered", "add", "chain", "mid-side", "scaled", "filter", "all-types" };

    private static (int Channel, int Partner, int Type)[]? TableOf(string name) => name switch
    {
        "alone" => null,
        "reordered" => [(3, -1, 0), (1, -1, 0), (0, -1, 0), (2, -1, 0), (5, -1, 0), (4, -1, 0)],
        "add" => [(0, -1, 0), (1, 0, 0), (2, -1, 0), (3, -1, 0), (4, -1, 0), (5, -1, 0)],
        "chain" => [(0, -1, 0), (1, 0, 0), (2, 1, 2), (3, 2, 3), (4, -1, 0), (5, -1, 0)],
        "mid-side" => [(0, 5, 1), (1, -1, 0), (2, -1, 0), (3, -1, 0), (4, -1, 0)],
        "scaled" => [(0, -1, 0), (1, 0, 2), (2, 1, 2), (3, -1, 0), (4, -1, 0), (5, -1, 0)],
        "filter" => [(5, -1, 0), (4, 5, 3), (3, 4, 3), (2, -1, 0), (1, -1, 0), (0, -1, 0)],
        _ => [(0, -1, 0), (1, 0, 0), (2, 3, 1), (4, 2, 2), (5, 4, 3)],
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void ChannelTablesOfCodec4RoundTrip(string name)
    {
        int channels = name == "mid-side" ? 6 : 6;
        int[][] samples = Signal("sine", channels, 16, 1024);
        foreach (int seed in new[] { 1, 2, 3 })
        {
            byte[] file = Encoder.File(samples, new Encoder.Settings { Codec = 4, Table = TableOf(name) }, seed);

            AssertDecodesTo(file, samples, $"{name} seed {seed}");
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void Codec4TakesEveryChannelCountUpToSix(int channels)
    {
        int[][] samples = Signal("noise", channels, 24, 700);

        AssertDecodesTo(Encoder.File(samples, new Encoder.Settings { Codec = 4, Bits = 24 }, channels), samples);
    }

    // The MD5 and the units whose checksum fails.

    [Fact]
    public void TheMd5ObjectIsComparedWithTheDecodedAudio()
    {
        foreach (int bits in new[] { 8, 16, 24 })
        {
            int[][] samples = Signal("sine", 2, bits, 1300);
            byte[] file = Encoder.File(samples, Plain(2, bits), 4, md5: true);

            (Report report, _) = Decode(file);

            ChecksumTally md5 = report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "MD5");
            Assert.Equal((1, 0), (md5.Checked, md5.Failed));
            Assert.DoesNotContain(report.Findings, f => f.Code == "tak.md5-mismatch");
        }
    }

    [Fact]
    public void AnMd5ThatDiffersIsAFindingOnTheMd5Field()
    {
        int[][] samples = Signal("sine", 2, 16, 1300);
        Encoder.Built built = Encoder.Build(samples, Plain(), 4);
        byte[] wrong = Encoder.Md5Of(samples, 16);
        wrong[3] ^= 0x10;
        byte[] file = Encoder.Assemble(Plain(), 2, 1300, 7, built.Coded, wrong).Data;

        (Report report, _) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tak.md5-mismatch");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.NotNull(finding.Path);
        Assert.Equal((1, 1), report.Streams.Single().Verification!.Checksums.Where(c => c.Name == "MD5").Select(c => (c.Checked, c.Failed)).Single());
    }

    [Fact]
    public void AFrameWhoseDataChecksumFailsIsDecodedAndLeftOutOfTheMeasurements()
    {
        int[][] samples = Signal("sine", 2, 16, 1536);
        Encoder.Built built = Encoder.Build(samples, Plain(), 5);
        byte[] damaged = (byte[])built.Data.Clone();
        damaged[built.Offsets[1] + built.Lengths[1] - 1] ^= 0xFF;

        (Report report, CollectingSink sink) = Decode(damaged, new MeasureOptions());

        StreamInfo stream = report.Streams.Single();
        Assert.Contains(report.Findings, f => f.Code == "tak.frame-crc-mismatch");
        Assert.Equal(1, stream.Verification!.Checksums.Single(c => c.Name == "frame data CRC-24").Failed);
        Assert.Equal(1536, stream.Verification.DecodedSamples);
        Assert.Equal(samples[0], sink.Channels[0]);
        StreamMeasurements m = stream.Measurements!;
        Assert.Equal(512, m.WithheldByReason[WithholdReason.ChecksumFailed]);
        Assert.Equal(1, m.Gaps);
        Assert.Equal(0, m.LostSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "measure.failed" or "decode.frame-invalid");
    }

    [Fact]
    public void FramesLostBetweenTwoAreAGapInThePositions()
    {
        int[][] samples = Signal("sine", 1, 16, 2048);
        Encoder.Built built = Encoder.Build(samples, Plain(), 6);
        // Frame 1 removed: the numbers skip one, so the frame after it is 512 samples further on.
        byte[] file = [.. built.Data[..built.Offsets[1]], .. built.Data[built.Offsets[2]..]];

        (Report report, CollectingSink sink) = Decode(file);

        Assert.Equal([0L, 1024, 1536], sink.Starts);
        Assert.Contains(report.Findings, f => f.Code == "tak.frame-number-discontinuity");
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    // Damage in the coded syntax: a frame that cannot be decoded is reported once per stream with a count, and the other frames decode.

    private static byte[] OneFrame(Action<Builder.BitWriter> write, int n = 512, int codec = 2, int channels = 1, int bits = 16)
    {
        var w = new Builder.BitWriter();
        write(w);
        return Encoder.Assemble(new Encoder.Settings { Codec = codec, Bits = bits }, channels, n, 7, [w.ToArray()]).Data;
    }

    // The start of a channel's data: no wasted bits and a first sample of 0.
    private static Builder.BitWriter Start(Builder.BitWriter w, int bits = 16, int order = 0)
    {
        w.Put(0, 1).Put(0, bits).Put(order, 2);
        return w;
    }

    // A channel that is 0 throughout, with one subframe of no prediction and one residual run of mode 0.
    private static void ZeroChannel(Builder.BitWriter w)
    {
        Start(w).Put(0, 3).Put(0, 1).Put(0, 1).Put(0, 6);
    }

    public static TheoryData<string> Damage() => new()
    {
        "wasted-bits",
        "boundaries-equal",
        "last-subframe-empty",
        "order-above-length",
        "warm-up-order-3",
        "precision",
        "partition-count",
        "mode-above-50",
        "mode-below-0",
        "escape-width",
        "filter-on-short-frame",
        "table-entries",
        "table-channel",
        "table-twice",
        "table-partner-same",
        "table-partner-unused",
        "table-leaves-out",
        "ends-inside",
    };

    private static (byte[] File, string Message) Crafted(string name) => name switch
    {
        "wasted-bits" => (OneFrame(w => w.Put(1, 1).Put(15, 4)), "wasted bits"),
        "boundaries-equal" => (OneFrame(w => Start(w).Put(2, 3).Put(3, 6).Put(3, 6)), "at least 1"),
        "last-subframe-empty" => (OneFrame(w => Start(w).Put(1, 3).Put(16, 6)), "at least 1"),
        "order-above-length" => (OneFrame(w => Start(w).Put(0, 3).Put(1, 1).Put(4, 4), n: 20), "order 24"),
        "warm-up-order-3" => (OneFrame(w => Start(w).Put(0, 3).Put(1, 1).Put(0, 4).Put(3, 2)), "difference order 3"),
        "precision" => (OneFrame(w => Start(w).Put(0, 3).Put(1, 1).Put(0, 4).Put(0, 2).Put(0, 1).Put(0, 6).Put(0, 1).Put(0, 1).Put(1, 1).Put(7, 3)), "precision 2"),
        "partition-count" => (OneFrame(w => Start(w).Put(0, 3).Put(0, 1).Put(1, 1), n: 100), "1 partitions"),
        "mode-above-50" => (OneFrame(w => Start(w).Put(0, 3).Put(0, 1).Put(0, 1).Put(51, 6)), "residual mode is 51"),
        "mode-below-0" => (OneFrame(w => Start(w).Put(0, 3).Put(0, 1).Put(1, 1).Put(0, 6).Put(0, 1).Put(1, 1)), "mode is -1"),
        "escape-width" => (OneFrame(w => Start(w).Put(0, 3).Put(0, 1).Put(0, 1).Put(1, 6).Put(1, 1).Put(1, 1).Put(0, 9).Put(7, 3).Put(23, 5)), "escape width of 30"),
        "filter-on-short-frame" => (OneFrame(w =>
        {
            ZeroChannel(w);
            ZeroChannel(w);
            w.Put(0, 1).Put(6, 3).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 1).Put(0, 12);
        }, n: 100, channels: 2), "fewer than 257"),
        "table-entries" => (OneFrame(w => w.Put(1, 1).Put(2, 4), codec: 4, channels: 2), "3 entries"),
        "table-channel" => (OneFrame(w => w.Put(1, 1).Put(0, 4).Put(5, 4), codec: 4, channels: 2), "channel 5"),
        "table-twice" => (OneFrame(w => w.Put(1, 1).Put(1, 4).Put(0, 4).Put(0, 1).Put(0, 4).Put(0, 1), codec: 4, channels: 2), "twice"),
        "table-partner-same" => (OneFrame(w => w.Put(1, 1).Put(0, 4).Put(0, 4).Put(1, 1).Put(1, 2).Put(0, 4), codec: 4, channels: 2), "same channel"),
        "table-partner-unused" => (OneFrame(w => w.Put(1, 1).Put(0, 4).Put(0, 4).Put(1, 1).Put(0, 2).Put(1, 4), codec: 4, channels: 2), "no entry before"),
        "table-leaves-out" => (OneFrame(w => w.Put(1, 1).Put(0, 4).Put(0, 4).Put(0, 1), codec: 4, channels: 2), "leaves channel 1 out"),
        _ => (OneFrame(w => Start(w).Put(0, 3).Put(0, 1)), string.Empty),
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void DamageInTheCodedSyntaxIsAFrameThatCannotBeDecoded(string name)
    {
        (byte[] file, string message) = Crafted(name);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Contains(message, finding.Message);
        Assert.Equal(0, report.Streams.Single().Verification!.DecodedSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Empty(sink.Starts);
    }

    [Fact]
    public void ASampleOutsideTheRangeOfTheDepthIsAFrameThatCannotBeDecoded()
    {
        int[][] samples = [[0, 1, 2, 300, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19]];
        byte[] file = Encoder.File(samples, new Encoder.Settings { Bits = 8, WastedBits = 0 }, 1);

        (Report report, _) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Contains("outside the 8-bit range", finding.Message);
    }

    [Fact]
    public void CodedDataThatEndsInsideAFieldIsAFrameThatCannotBeDecoded()
    {
        int[][] samples = Signal("noise", 2, 16, 1024);
        byte[] file = Encoder.File(samples, Plain(), 3, damage: (i, coded) => i == 1 ? coded[..^40] : coded);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Equal(1, finding.Count);
        Assert.Equal(512, report.Streams.Single().Verification!.DecodedSamples);
        Assert.Equal(512, sink.Channels[0].Count);
    }

    [Fact]
    public void BytesAfterTheLastFieldAreReportedAndTheSamplesStillDecode()
    {
        int[][] samples = Signal("sine", 2, 16, 1024);
        byte[] file = Encoder.File(samples, Plain(), 3, damage: (i, coded) => i == 0 ? [.. coded, 0x55, 0xAA, 0x0F] : coded);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tak.frame-bytes-after-data");
        Assert.Contains("3 bytes", finding.Message);
        Assert.NotNull(finding.Path);
        Assert.Equal(samples[1], sink.Channels[1]);
    }

    public static TheoryData<string> Refused() => new() { "codec-0", "codec-1", "codec-3", "bits-12", "bits-32", "channels-3", "channels-7", "data-type", "frame-size" };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AStreamThatTheDecoderDoesNotTakeIsNotPossible(string name)
    {
        byte[] info = name switch
        {
            "codec-0" => Builder.StreamInfo(codec: 0),
            "codec-1" => Builder.StreamInfo(codec: 1),
            "codec-3" => Builder.StreamInfo(codec: 3),
            "bits-12" => Builder.StreamInfo(bitsCode: 4),
            "bits-32" => Builder.StreamInfo(bitsCode: 24),
            "channels-3" => Builder.StreamInfo(channels: 3),
            "channels-7" => Builder.StreamInfo(codec: 4, channels: 7),
            "data-type" => Builder.StreamInfo(dataType: 1),
            _ => Builder.StreamInfo(sizeType: 12),
        };

        (Report report, CollectingSink sink) = Decode(Builder.Standard(streamInfo: info));

        Assert.Single(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Empty(sink.Starts);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void AStreamWithoutStreamInfoIsNotPossible()
    {
        byte[] file = [.. "tBaK"u8, .. Builder.EncoderInfo(), .. Builder.End(), .. Builder.Frame(0, 100, isLast: true, isInfo: true)];

        (Report report, _) = Decode(file);

        Assert.Single(report.Findings, f => f.Code == "decode.not-possible");
    }

    // The worst legitimate frame for the work a byte pays for: six channels of 16,384 samples of silence (at 96,000 Hz or more), each channel one subframe of no prediction and
    // one residual block of mode 0, with the most wasted bits, so that a channel takes the fewest bits. The allowance is the decoder's WorkPerInputByte for each byte of the frame.
    [Theory]
    [InlineData(8, 7)]
    [InlineData(16, 15)]
    [InlineData(24, 16)]
    public void AFrameOfSilenceChargesLessThanTheAllowanceForItsBytes(int bits, int wasted)
    {
        int[][] samples = Signal("silence", 6, bits, 6 * 16384);
        Encoder.Built built = Encoder.Build(samples, new Encoder.Settings { Codec = 4, Bits = bits, Rate = 96000, WastedBits = wasted, Subframes = 1, Prediction = 0, Partitions = 0, DifferenceOrder = 0 }, 1, sizeType: 6);

        long units = DecoderWorkTests.TakCases.Units(built.Data);

        long allowance = built.Lengths.Sum() * new TakDecoder().WorkPerInputByte;
        Assert.True(units <= allowance, $"{units} units for {built.Lengths.Sum()} bytes: {units / (double)built.Lengths.Sum():F0} a byte, the allowance is {new TakDecoder().WorkPerInputByte}");
        Assert.True(units > allowance / 3, $"{units / (double)built.Lengths.Sum():F0} units a byte is far below the allowance: the derivation is out of date");
    }

    [Fact]
    public void TheDecoderRunsWithoutAFrontEndThatWantsSamplesBecauseTheMd5IsAChecksum()
    {
        int[][] samples = Signal("sine", 2, 16, 1300);
        byte[] file = Encoder.File(samples, Plain(), 4, md5: true);

        Report report = Analyzer.CreateDefault().Analyze("test.tak", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(1300, verification.DecodedSamples);
        Assert.Equal((1, 0), (verification.Checksums.Single(c => c.Name == "MD5").Checked, verification.Checksums.Single(c => c.Name == "MD5").Failed));
        Assert.True(verification.IsVerified);
    }
}
