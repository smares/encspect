using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Damaged and absurd PCM files analyzed with decoding and measuring: a finding, never an exception or a hang (CLAUDE.md, "Architecture rules"). The route reads
// whole sample frames of what the file holds, so a data chunk cut short, a size that is not a multiple of the frame, a block alignment that disagrees with the
// channels and the width, a header of 65,535 channels, and widths no decoder takes are all reports.
public class PcmMalformedInputTests
{
    private static byte[] Wav(ushort tag, ushort channels, uint rate, ushort align, ushort bits, byte[] data, uint? dataSize = null, uint? average = null)
    {
        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        file.AddRange(BitConverter.GetBytes((uint)(4 + 24 + 8 + data.Length)));
        file.AddRange(Encoding.ASCII.GetBytes("WAVEfmt "));
        file.AddRange(BitConverter.GetBytes(16u));
        file.AddRange(BitConverter.GetBytes(tag));
        file.AddRange(BitConverter.GetBytes(channels));
        file.AddRange(BitConverter.GetBytes(rate));
        file.AddRange(BitConverter.GetBytes(average ?? rate * align));
        file.AddRange(BitConverter.GetBytes(align));
        file.AddRange(BitConverter.GetBytes(bits));
        file.AddRange(Encoding.ASCII.GetBytes("data"));
        file.AddRange(BitConverter.GetBytes(dataSize ?? (uint)data.Length));
        file.AddRange(data);
        return [.. file];
    }

    private static byte[] Aiff(ushort channels, uint frames, ushort bits, byte[] sound, uint offset = 0, string? type = null, double rate = 44100)
    {
        byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var comm = new List<byte> { (byte)(channels >> 8), (byte)channels };
        comm.AddRange(Be32(frames));
        comm.AddRange([(byte)(bits >> 8), (byte)bits]);
        comm.AddRange(rate == 44100 ? new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 } : new byte[10]);
        if (type is not null)
        {
            comm.AddRange(Encoding.ASCII.GetBytes(type));
            comm.AddRange([0, 0]);
        }

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("FORM"));
        file.AddRange(Be32((uint)(4 + 8 + comm.Count + 8 + 8 + sound.Length)));
        file.AddRange(Encoding.ASCII.GetBytes(type is null ? "AIFF" : "AIFC"));
        file.AddRange(Encoding.ASCII.GetBytes("COMM"));
        file.AddRange(Be32((uint)comm.Count));
        file.AddRange(comm);
        file.AddRange(Encoding.ASCII.GetBytes("SSND"));
        file.AddRange(Be32((uint)(8 + sound.Length)));
        file.AddRange(Be32(offset));
        file.AddRange(Be32(0));
        file.AddRange(sound);
        return [.. file];
    }

    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        new Random(1).NextBytes(bytes);
        return bytes;
    }

    private static Report Analyze(string name, byte[] data, bool measures = true)
    {
        var options = new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = 1 << 28, Output = _ => new DecoderWorkTests.NullSink(), Measure = measures ? new MeasureOptions { MaxWork = 1 << 28 } : null } };
        var started = new TaskCompletionSource();
        Task<Report> work = Task.Run(() =>
        {
            started.SetResult();
            return Analyzer.CreateDefault().Analyze(name, new MemoryStream(data), options);
        });
        started.Task.Wait();
        Assert.True(work.Wait(TimeSpan.FromSeconds(20)), $"{name} took longer than 20 s.");
        Report report = work.Result;
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "measure.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        return report;
    }

    public static TheoryData<string, byte[]> Files()
    {
        byte[] sixteenBit = Noise(4000);
        return new TheoryData<string, byte[]>
        {
            { "data chunk cut short by the end of the file.wav", Wav(1, 2, 44100, 4, 16, sixteenBit, dataSize: 100_000) },
            { "data chunk of an odd size.wav", Wav(1, 2, 44100, 4, 16, Noise(4001)) },
            { "data chunk of less than a sample frame.wav", Wav(1, 2, 44100, 4, 16, Noise(3)) },
            { "empty data chunk.wav", Wav(1, 2, 44100, 4, 16, []) },
            { "data size of 0xFFFFFFFF.wav", Wav(1, 2, 44100, 4, 16, sixteenBit, dataSize: uint.MaxValue) },
            { "block alignment of 1 for 16-bit stereo.wav", Wav(1, 2, 44100, 1, 16, sixteenBit) },
            { "block alignment of 0.wav", Wav(1, 2, 44100, 0, 16, sixteenBit) },
            { "block alignment of 65535.wav", Wav(1, 2, 44100, 65535, 16, sixteenBit) },
            { "65535 channels with less than a frame.wav", Wav(1, 65535, 44100, 65535, 8, Noise(1000)) },
            { "65535 channels of 16 bits.wav", Wav(1, 65535, 44100, 0xFFFE, 16, Noise(300_000)) },
            { "65535 channels of 64-bit floats.wav", Wav(3, 65535, 44100, 0xFFF8, 64, Noise(1_100_000)) },
            { "no channels.wav", Wav(1, 0, 44100, 0, 16, sixteenBit) },
            { "no bits.wav", Wav(1, 2, 44100, 4, 0, sixteenBit) },
            { "sample rate of 0.wav", Wav(1, 2, 0, 4, 16, sixteenBit) },
            { "sample rate of 4 billion.wav", Wav(1, 2, uint.MaxValue, 4, 16, sixteenBit) },
            { "samples of 12 bits.wav", Wav(1, 2, 44100, 4, 12, sixteenBit) },
            { "samples of 40 bits.wav", Wav(1, 2, 44100, 10, 40, sixteenBit) },
            { "samples of 64 bits.wav", Wav(1, 2, 44100, 16, 64, sixteenBit) },
            { "samples of 65535 bits.wav", Wav(1, 1, 44100, 8192, 65535, sixteenBit) },
            { "floats of 33 bits.wav", Wav(3, 2, 44100, 10, 33, sixteenBit) },
            { "floats of 16 bits.wav", Wav(3, 2, 44100, 4, 16, sixteenBit) },
            { "floats of 64 bits with a block alignment for 32.wav", Wav(3, 2, 44100, 8, 64, sixteenBit) },
            { "A-law of 16 bits.wav", Wav(6, 2, 44100, 4, 16, sixteenBit) },
            { "mu-law of 4 bits.wav", Wav(7, 2, 44100, 2, 4, sixteenBit) },
            { "mu-law of 8 bits.wav", Wav(7, 2, 44100, 2, 8, sixteenBit) },
            { "8-bit unsigned noise.wav", Wav(1, 1, 8000, 1, 8, sixteenBit) },
            { "floats of NaN.wav", Wav(3, 1, 44100, 4, 32, [.. Enumerable.Repeat(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, 3000).SelectMany(b => b)]) },
            { "floats of subnormals.wav", Wav(3, 1, 44100, 8, 64, [.. Enumerable.Repeat(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, 3000).SelectMany(b => b)]) },
            { "AIFF with no frames stated.aiff", Aiff(2, 0, 16, sixteenBit) },
            { "AIFF with 4 billion frames stated.aiff", Aiff(2, uint.MaxValue, 16, sixteenBit) },
            { "AIFF with a sample size of 0.aiff", Aiff(2, 100, 0, sixteenBit) },
            { "AIFF with a sample size of 33.aiff", Aiff(2, 100, 33, sixteenBit) },
            { "AIFF with a sample size of 12.aiff", Aiff(2, 100, 12, sixteenBit) },
            { "AIFF with 65535 channels.aiff", Aiff(65535, 1, 16, Noise(200_000)) },
            { "AIFF with a sound data offset past the end.aiff", Aiff(2, 100, 16, sixteenBit, offset: 1_000_000) },
            { "AIFF with a sound data offset of 4 billion.aiff", Aiff(2, 100, 16, sixteenBit, offset: uint.MaxValue) },
            { "AIFF with a sample rate of 0.aiff", Aiff(2, 100, 16, sixteenBit, rate: 0) },
            { "AIFF-C sowt of 8 bits.aifc", Aiff(2, 100, 8, sixteenBit, type: "sowt") },
            { "AIFF-C in24 of 16 bits.aifc", Aiff(2, 100, 16, sixteenBit, type: "in24") },
            { "AIFF-C fl32 of 16 bits.aifc", Aiff(2, 100, 16, sixteenBit, type: "fl32") },
            { "AIFF-C fl64 of 64 bits.aifc", Aiff(2, 100, 64, Noise(1600), type: "fl64") },
            { "AIFF-C alaw of 16 bits.aifc", Aiff(2, 100, 16, sixteenBit, type: "alaw") },
            { "AIFF-C unknown type.aifc", Aiff(2, 100, 16, sixteenBit, type: "abcd") },
            { "AIFF-C ima4 packets.aifc", Aiff(2, 10, 16, Noise(680), type: "ima4") },
        };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AReportComesBackWithDecodingAndMeasuring(string name, byte[] data)
    {
        Report plain = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));
        Report report = Analyze(name, data);

        // Decoding and measuring change no node and no finding the parser raised.
        Assert.Equal(plain.Findings.Select(f => f.Code), report.Findings.Where(f => !f.Code.StartsWith("decode.", StringComparison.Ordinal) && !f.Code.StartsWith("measure.", StringComparison.Ordinal)).Select(f => f.Code));
        foreach (StreamInfo stream in report.Streams)
        {
            Assert.True(stream.Verification is null or { HasChecksums: false });
            if (stream.Verification is { Decoding: DecodeStatus.Decoded } verification)
            {
                Assert.True(verification.DecodedSamples <= stream.Samples, $"{name}: {verification.DecodedSamples} samples decoded of {stream.Samples}.");
                Assert.NotNull(stream.Measurements);
            }
        }
    }

    [Fact]
    public void ADataChunkCutShortDecodesTheWholeFramesItHolds()
    {
        Report report = Analyze("cut.wav", Wav(1, 2, 44100, 4, 16, Noise(4001), dataSize: 100_000));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(DecodeStatus.Decoded, stream.Verification!.Decoding);
        Assert.Equal(1000, stream.Verification.DecodedSamples);
        Assert.Equal(1000, stream.Measurements!.Samples);
    }

    [Theory]
    [InlineData(1, 2, 8, 100)]
    [InlineData(1, 1, 16, 7)]
    public void ABlockAlignmentThatDisagreesWithTheChannelsAndWidthStartsNoSession(ushort tag, ushort channels, ushort bits, ushort align)
    {
        Report report = Analyze("align.wav", Wav(tag, channels, 44100, align, bits, Noise(1000)));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Null(stream.Verification);
        Assert.Null(stream.Measurements);
    }

    [Theory]
    [InlineData(40, 10)]
    [InlineData(64, 16)]
    public void IntegersWiderThan32BitsAreNotDecoded(ushort bits, ushort align)
    {
        Report report = Analyze("wide.wav", Wav(1, 2, 44100, align, bits, Noise(1000)));

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(DecodeStatus.NotPossible, stream.Verification!.Decoding);
        Assert.Contains(report.Findings, f => f.Code == "decode.not-possible" && f.Severity == Severity.Info);
        Assert.Equal(MeasurementStatus.NotPossible, stream.Measurements!.Status);
    }

    [Fact]
    public void AWidthNoCodingHasStartsNoSession()
    {
        foreach ((ushort tag, ushort bits, ushort align) in new (ushort, ushort, ushort)[] { (3, 16, 4), (3, 33, 10), (6, 16, 4), (7, 4, 2), (1, 0, 4) })
        {
            StreamInfo stream = Assert.Single(Analyze("width.wav", Wav(tag, 2, 44100, align, bits, Noise(1000))).Streams);
            Assert.Null(stream.Verification);
        }
    }

    [Fact]
    public void AnAiffOfPackedSamplesOrOfAnUnknownTypeStartsNoSession()
    {
        foreach (string type in new[] { "ima4", "abcd" })
        {
            StreamInfo stream = Assert.Single(Analyze("packed.aifc", Aiff(2, 10, 16, Noise(680), type: type)).Streams);
            Assert.Null(stream.Verification);
        }
    }

    [Fact]
    public void AnAiffWithASoundDataOffsetPastTheEndDecodesNothing()
    {
        StreamInfo stream = Assert.Single(Analyze("offset.aiff", Aiff(2, 100, 16, Noise(400), offset: 1_000_000)).Streams);
        Assert.Equal(0, stream.Verification?.DecodedSamples ?? 0);
    }

    [Fact]
    public void AnAiffOfFloatsDecodesThemBigEndian()
    {
        var sink = new PcmHashingSink();
        byte[] one = [0x3F, 0x80, 0, 0];
        using var stream = new MemoryStream(Aiff(1, 2, 32, [.. one, .. one], type: "fl32"));
        Report report = Analyzer.CreateDefault().Analyze("one.aifc", stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });

        Assert.Equal(DecodeStatus.Decoded, report.Streams.Single().Verification!.Decoding);
        Assert.Equal(SampleFormat.Float32, sink.Format);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData([0, 0, 0x80, 0x3F, 0, 0, 0x80, 0x3F])), sink.Hash());
    }

    // Truncating a correct file at every length gives a report each time.
    [Fact]
    public void EveryTruncationOfAWaveFileGivesAReport()
    {
        byte[] file = Wav(1, 2, 44100, 4, 16, Noise(300));
        for (int length = 0; length <= file.Length; length++)
        {
            Report report = Analyze("cut.wav", file[..length]);
            Assert.DoesNotContain(report.Findings, f => f.Code == "decode.failed");
        }
    }

    [Fact]
    public void ADecoderThatIsHandedTheWrongSetupFailsAsAFindingNotAnException()
    {
        // A session with a coding the decoder refuses: the parser's own bug would be parse.failed, a setup that states a width of 12 bits is decode.not-possible.
        var parser = new DecodeRouteTests.FrameParser { CodecId = "pcm", Channels = 2, BitsPerSample = 16, Stored = [] };
        var report = new Analyzer([], [parser], [], DefaultParsers.Decoders).Analyze("x.bin", new MemoryStream(new byte[64]), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        Assert.Equal(DecodeStatus.NotPossible, parser.Verification!.Decoding);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
    }
}
