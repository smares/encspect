using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Formats.Tta;
using Encspect.Formats.Tta.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// The TTA decoder against the worked examples of the clean-room description (the Rice codes, the filter and predictor, the decorrelation) and against
// TtaEncoder, an inverse of the same description, for every depth, channel counts, signals that reach the ends of the range and long unary runs; the
// damage it must report as findings. crosscheck_tta_pcm.py is what holds the description to the files of libtta-c and FFmpeg.
public class TtaDecoderTests
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

    private static (Report Report, CollectingSink Sink) Decode(byte[] file)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } };
        return (Analyzer.CreateDefault().Analyze("test.tta", new MemoryStream(file), options), sink);
    }

    private static int[][] Signal(string kind, int channels, int bits, int count)
    {
        int max = (1 << (bits - 1)) - 1;
        int min = -(1 << (bits - 1));
        var random = new Random(bits * 1000 + channels * 10 + kind.Length);
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
                    // Silence long enough for the Rice parameters to fall to 0, then a loud burst: long unary runs.
                    "burst" => t < count / 2 ? 0 : random.Next(min, max + 1),
                    // Odd negative differences between neighbors, where a shift and a division by two part.
                    "odd" => ((t * 7) + (c * 3)) % 2 == 0 ? -((t % 50) + 1) : (t % 41) - 20,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
            }
        }

        return samples;
    }

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string kind in new[] { "silence", "sine", "noise", "extremes", "burst", "odd" })
        {
            foreach (int bits in new[] { 8, 16, 24 })
            {
                foreach (int channels in new[] { 1, 2, 3, 6 })
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
        // 8,000 Hz gives frames of 8,359 samples: three frames, the last short.
        int[][] samples = Signal(kind, channels, bits, 20000);
        (byte[] file, _) = TtaEncoder.Encode(8000, bits, samples);

        (Report report, CollectingSink sink) = Decode(file);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "parse.failed" or "tta.frame-crc-mismatch");
        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
        Assert.Equal(8 * ((bits + 7) / 8), sink.Format!.BitsPerSample);
        for (int c = 0; c < channels; c++)
        {
            Assert.Equal(samples[c], sink.Channels[c]);
        }

        Assert.Equal(0, sink.Starts[0]);
        Assert.Contains(8359L, sink.Starts);
        Assert.Contains(16718L, sink.Starts);
    }

    [Fact]
    public void ABitDepthThatIsNotAMultipleOfEightIsDecodedAtTheContainerWidth()
    {
        // 12-bit audio is coded as 16-bit: left-justified, the low four bits zero.
        int[][] samples = Signal("sine", 2, 16, 3000);
        foreach (int[] channel in samples)
        {
            for (int t = 0; t < channel.Length; t++)
            {
                channel[t] &= ~0xF;
            }
        }

        (byte[] file, _) = TtaEncoder.Encode(8000, 12, samples);
        (Report report, CollectingSink sink) = Decode(file);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
        Assert.Equal(16, sink.Format!.BitsPerSample);
        Assert.Equal(samples[1], sink.Channels[1]);
    }

    [Fact]
    public void TheRiceExampleOfTheDescriptionDecodesToItsValuesAndSums()
    {
        byte[] data = [0x06, 0x00, 0xF0, 0x01, 0x00, 0x00, 0x00, 0x00];
        var bits = new BitSource(data);
        var state = new RiceState();
        state.Reset();

        Assert.Equal(2, Codes.ReadResidual(ref bits, state));
        Assert.Equal((9, 15363u), (state.KA, state.SumA));
        Assert.Equal(-128, Codes.ReadResidual(ref bits, state));
        Assert.Equal((9, 14659u), (state.KA, state.SumA));
        Assert.Equal(-1792, Codes.ReadResidual(ref bits, state));
        Assert.Equal((10, 17327u, 10, 18432u), (state.KA, state.SumA, state.KB, state.SumB));
    }

    [Fact]
    public void TheFilterAndPredictorExampleOfTheDescriptionHolds()
    {
        int[] residuals = [100, 50, -20, 0, 30, -7];
        int[] filtered = [100, 52, -21, -1, 32, -7];
        int[] predicted = [100, 148, 122, 117, 145, 133];
        var state = new FrameDecoder.Channel();
        state.Reset();
        int[][] steps = [[0, 0, 0, 0, 1, 2, 2, 4], [0, 0, 0, 1, 1, 2, 2, 4], [0, 0, 1, 1, -1, -2, -2, 4], [0, 1, 1, -1, 1, -2, -2, -4], [1, 1, -1, 1, 1, 2, 2, -4], [1, -1, 1, 1, -1, 2, 2, 4]];
        int[][] history = [[0, 0, 0, 0, 100, 100, 100, 100], [0, 0, 0, 100, -248, -148, -48, 52], [0, 0, 100, -248, 123, -25, -73, -21]];
        for (int t = 0; t < residuals.Length; t++)
        {
            int y = FrameDecoder.Filter(state, residuals[t], 9);
            Assert.Equal(filtered[t], y);
            Assert.Equal(steps[t], state.S);
            if (t < history.Length)
            {
                Assert.Equal(history[t], state.V);
            }

            int x = y + ((state.Previous * 31) >> 5);
            state.Previous = x;
            Assert.Equal(predicted[t], x);
        }
    }

    [Fact]
    public void TheDecorrelationExamplesOfTheDescriptionHold()
    {
        // Stereo: right = 7 + trunc(-5 / 2) = 5, left = 5 - (-5) = 10; three channels: 8, 11, 7. The encoder side is x[i] = in[i + 1] - in[i], x[last] = in[last] - trunc(x[last - 1] / 2).
        int[][] stereo = [[10, 10, -3], [5, -4, 8]];
        int[][] three = [[8, 1], [11, -2], [7, 3]];
        foreach (int[][] samples in new[] { stereo, three })
        {
            (byte[] file, _) = TtaEncoder.Encode(8000, 16, samples);
            (_, CollectingSink sink) = Decode(file);
            for (int c = 0; c < samples.Length; c++)
            {
                Assert.Equal(samples[c], sink.Channels[c]);
            }
        }
    }

    [Fact]
    public void AParameterBeyond32BitsIsDamageAndNotAnException()
    {
        var state = new RiceState();
        state.Reset();
        state.KA = 33;
        byte[] data = new byte[16];
        Assert.Throws<InvalidDataException>(() =>
        {
            var bits = new BitSource(data);
            Codes.ReadResidual(ref bits, state);
        });
        state.KA = 10;
        state.KB = 40;
        Assert.Throws<InvalidDataException>(() =>
        {
            var bits = new BitSource([0x01, 0, 0, 0, 0, 0, 0, 0]);
            Codes.ReadResidual(ref bits, state);
        });
    }

    [Fact]
    public void ARunOfOnesThatCrossesTheFrameIsTruncation()
    {
        byte[] data = [.. Enumerable.Repeat((byte)0xFF, 40)];
        Assert.Throws<EndOfStreamException>(() =>
        {
            var bits = new BitSource(data);
            bits.ReadUnary();
        });
        var counted = new BitSource([.. Enumerable.Repeat((byte)0xFF, 20), 0x07]);
        Assert.Equal((20 * 8) + 3, counted.ReadUnary());
    }

    [Fact]
    public void AValueOutsideTheRangeOfTheDepthIsAFrameThatCannotBeDecoded()
    {
        // 40,000 does not fit 16 bits: the encoder writes it all the same, and the frame's CRC-32 holds.
        int[][] samples = [[1, 2, 3, 40000, 5]];
        (byte[] file, _) = TtaEncoder.Encode(8000, 16, samples);

        (Report report, _) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Contains("outside the 16-bit range", finding.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void CodedDataThatEndsInsideASampleIsAFrameThatCannotBeDecoded()
    {
        int[][] samples = Signal("noise", 2, 16, 3000);
        (byte[] file, List<byte[]> frames) = TtaEncoder.Encode(8000, 16, samples);
        // The same frame cut by 20 bytes, its CRC-32 computed again so the parser finds nothing wrong.
        byte[] frame = frames[0];
        byte[] cut = new byte[frame.Length - 20];
        Array.Copy(frame, cut, cut.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(cut.Length - 4), Crc32.Of(cut.AsSpan(0, cut.Length - 4)));
        byte[] rebuilt = Unpooled.Join([TtaBuilder.Header(8000, 2, 16, 3000), TtaBuilder.Table([(uint)cut.Length]), cut]);

        (Report report, CollectingSink sink) = Decode(rebuilt);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.frame-invalid");
        Assert.Contains("ends inside", finding.Message);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.True(sink.Channels[0].Count < 3000);
        Assert.NotEmpty(file);
    }

    [Fact]
    public void BytesAfterAFramesCodesAreReportedAndTheSamplesStillDecode()
    {
        int[][] samples = Signal("sine", 2, 16, 3000);
        (_, List<byte[]> frames) = TtaEncoder.Encode(8000, 16, samples);
        byte[] coded = frames[0][..^4];
        byte[] padded = [.. coded, 0x55, 0xAA, 0x0F, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(padded.Length - 4), Crc32.Of(padded.AsSpan(0, padded.Length - 4)));
        byte[] file = Unpooled.Join([TtaBuilder.Header(8000, 2, 16, 3000), TtaBuilder.Table([(uint)padded.Length]), padded]);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.frame-bytes-after-codes");
        Assert.Contains("3 bytes", finding.Message);
        Assert.NotNull(finding.Path);
        Assert.Equal(samples[1], sink.Channels[1]);
    }

    [Theory]
    [InlineData(2, 16, "password")]
    [InlineData(3, 16, "audio format 3")]
    [InlineData(0, 16, "audio format 0")]
    [InlineData(1, 32, "32 bits per sample")]
    [InlineData(1, 0, "of 0 bits")]
    public void AStreamThatTheDecoderDoesNotTakeIsNotPossible(int format, int bits, string reason)
    {
        byte[] file = Unpooled.Join([TtaBuilder.Header(44100, 2, bits, 46080, format), TtaBuilder.Table([204u]), TtaBuilder.Frame(200)]);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Contains(reason, finding.Message);
        Assert.Empty(sink.Starts);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }

    [Fact]
    public void FramesTheParserCallsDamagedAreStillDecodedAndTheOnesItCannotReadAreNotDecoded()
    {
        int[][] samples = Signal("sine", 2, 16, 20000);
        (byte[] file, List<byte[]> frames) = TtaEncoder.Encode(8000, 16, samples);
        // The second frame's CRC-32 is changed: the frame is still decoded (the coded bytes are fine), and the parser reports the CRC-32.
        byte[] damaged = (byte[])file.Clone();
        int at = TtaBuilder.HeaderLength + (4 * frames.Count) + 4 + frames[0].Length + frames[1].Length - 1;
        damaged[at] ^= 0xFF;
        (Report report, CollectingSink sink) = Decode(damaged);
        Assert.Contains(report.Findings, f => f.Code == "tta.frame-crc-mismatch");
        Assert.Equal(samples[0], sink.Channels[0]);

        // The data ends inside the last frame: that frame is not decoded, the ones before it are.
        (Report cutReport, CollectingSink cutSink) = Decode(file[..^10]);
        Assert.Contains(cutReport.Findings, f => f.Code == "tta.frame-cut");
        Assert.Equal(samples[0][..16718], cutSink.Channels[0]);
    }

    [Fact]
    public void DecodingNeedsAFrontEndThatWantsTheSamples()
    {
        int[][] samples = Signal("sine", 2, 16, 3000);
        (byte[] file, _) = TtaEncoder.Encode(8000, 16, samples);

        Report plain = Analyzer.CreateDefault().Analyze("test.tta", new MemoryStream(file));
        Report verify = Analyzer.CreateDefault().Analyze("test.tta", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

        Assert.Equal(DecodeStatus.NotRequested, plain.Streams.Single().Verification!.Decoding);
        Assert.Equal(DecodeStatus.NotRequested, verify.Streams.Single().Verification!.Decoding);
        Assert.Equal(true, verify.Streams.Single().Verification!.IsVerified);
        Assert.DoesNotContain(verify.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
    }

    [Fact]
    public void AFrameBeyondTheLimitIsNotReadIntoMemory()
    {
        // One frame of 64 MiB and 4 bytes more: the seek table states it and the data holds it, but it is not decoded.
        const int size = (64 << 20) + 8;
        byte[] frame = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(size - 4), Crc32.Of(frame.AsSpan(0, size - 4)));
        byte[] file = Unpooled.Join([TtaBuilder.Header(8000, 1, 16, 8359), TtaBuilder.Table([(uint)size]), frame]);

        (Report report, CollectingSink sink) = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "tta.frame-not-decoded");
        Assert.NotNull(finding.Path);
        Assert.Empty(sink.Starts);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }
}
