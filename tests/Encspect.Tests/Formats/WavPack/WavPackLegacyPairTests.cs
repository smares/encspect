using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.WavPack;

// The pair of a WavPack 3 hybrid file and its .wvc (docs/design/correction-files.md, "Version 3"): the correction file has no structure, so the pair is unchecked until the
// decoder reads both and compares the second CRC of the header. The pairs' audio and the encoder-written ones' verdicts are held by WavPackLegacyDecoderFixtureTests;
// here are the hand-built pairs against the errors libwavpack counts for them, the words around the pair and the files that ask for none.
public class WavPackLegacyPairTests
{
    private static readonly string Folder = Path.Combine(Fixtures.FixtureDirectory, "wavpack", "legacy");

    private static string Fixture(string name) => Path.Combine(Folder, name);

    private static readonly string Main = Fixture("external-wv397-hybrid-320-correction.wv");

    private static readonly string Correction = Fixture("external-wv397-hybrid-320-correction-wvc.wvc");

    private static readonly AnalysisOptions Decoding = new() { Decoding = new DecodeOptions() };

    public static TheoryData<string> BuiltPairs
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string file in Directory.EnumerateFiles(Folder, "built-legacy-pair-*.wv").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    private static Report Analyze(string name, AnalysisOptions? options = null)
    {
        string path = Fixture(name);
        JsonElement manifest = Fixtures.LoadMpegManifest("wavpack/legacy/" + name);
        string companion = Fixture(manifest.GetProperty("companion").GetString()!);
        using FileStream stream = File.OpenRead(path);
        return Analyzer.CreateDefault().Analyze(name, stream, new AnalysisOptions { Decoding = options?.Decoding, Companions = CompanionSource.From(_ => File.OpenRead(companion)) });
    }

    private static StreamInfo Stream(Report report) => Assert.Single(report.Streams);

    [Theory]
    [MemberData(nameof(BuiltPairs))]
    public void ADecodedPairIsConsistentWhereLibwavpackCountsNoError(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest("wavpack/legacy/" + name);
        int errors = manifest.GetProperty("crosscheck").GetProperty("libwavpack_legacy").GetProperty("pair_errors").GetInt32();
        bool isEmpty = new FileInfo(Fixture(manifest.GetProperty("companion").GetString()!)).Length == 0;

        Report report = Analyze(name, Decoding);

        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal(CorrectionBasis.DecodedAudio, pairing.Basis);
        Assert.Equal(errors == 0 && !isEmpty ? CorrectionStatus.Consistent : CorrectionStatus.Inconsistent, pairing.Status);
        Assert.Equal(pairing.Status == CorrectionStatus.Consistent || isEmpty, Stream(report).Verification!.IsVerified == true);
        Assert.Equal(!isEmpty, Stream(report).Verification!.IsCorrected);
        Assert.Equal(pairing.Status == CorrectionStatus.Consistent, report.Findings.All(f => !f.Code.StartsWith("correction.", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(BuiltPairs))]
    public void WithoutDecodingAPairIsUncheckedUnlessTheCompanionHoldsNothing(string name)
    {
        Report report = Analyze(name);

        CorrectionPairing pairing = Stream(report).Correction!;
        bool isEmpty = name.EndsWith("-empty.wv", StringComparison.Ordinal);
        Assert.Equal(isEmpty ? CorrectionStatus.Inconsistent : CorrectionStatus.Unchecked, pairing.Status);
        if (!isEmpty)
        {
            Assert.Equal("a version 3 correction file has no header; only decoding with it compares it", pairing.Reason);
            Assert.Equal("correction.unchecked", Assert.Single(report.Findings).Code);
        }
    }

    [Fact]
    public void ACutCorrectionFileEndsBeforeTheSamplesAndItsTreeHoldsOneNode()
    {
        Report report = Analyze("built-legacy-pair-cut.wv", Decoding);

        CorrectionProblem problem = Assert.Single(Stream(report).Correction!.Problems);
        Assert.Equal((CorrectionProblemKind.Cut, "Correction CRC"), (problem.Kind, problem.Node.Name));
        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-correction-ended");
        Assert.DoesNotContain(report.Findings, f => f.Code == "wavpack.legacy-crc2-mismatch");
        Assert.Equal("Correction Bitstream", Assert.Single(Assert.Single(report.Companions).Report.Root.Children).Name);
    }

    [Fact]
    public void AFlippedBitFailsTheSecondCrcAndTheMessageSaysSo()
    {
        Report report = Analyze("built-legacy-pair-bit-flipped.wv", Decoding);

        Assert.Equal(CorrectionProblemKind.ChecksumsFail, Assert.Single(Stream(report).Correction!.Problems).Kind);
        Finding inconsistent = Assert.Single(report.Findings, f => f.Code == "correction.inconsistent");
        Assert.Contains("does not have the CRC", inconsistent.Message);
        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-crc2-mismatch");
        ChecksumTally tally = Stream(report).Verification!.Checksums.Single();
        Assert.Equal(("correction CRC", 1, 1, 1), (tally.Name, tally.Stored, tally.Checked, tally.Failed));
    }

    [Fact]
    public void BytesAfterTheCorrectionBitstreamAreANodeOfTheirOwn()
    {
        Report report = Analyze("built-legacy-pair-trailing-bytes.wv", Decoding);

        Assert.Equal(CorrectionStatus.Consistent, Stream(report).Correction!.Status);
        // The tree is the same with and without decoding: one node over all the bytes; the decoder says that 16 were not read.
        Node node = Assert.Single(Assert.Single(report.Companions).Report.Root.Children);
        Assert.Equal(("Correction Bitstream", 28514), (node.Name, node.Length));
        Assert.Contains("16 bytes", Assert.Single(report.Findings, f => f.Code == "wavpack.legacy-correction-unread").Message);
        Node plain = Assert.Single(Assert.Single(Analyze("built-legacy-pair-trailing-bytes.wv").Companions).Report.Root.Children);
        Assert.Equal((node.Name, node.Offset, node.Length), (plain.Name, plain.Offset, plain.Length));
    }

    [Fact]
    public void AnEmptyCorrectionFileHoldsNoCorrectionDataAndIsNotGivenToTheDecoder()
    {
        Report report = Analyze("built-legacy-pair-empty.wv", Decoding);

        Assert.Equal(CorrectionProblemKind.NotCorrectionData, Assert.Single(Stream(report).Correction!.Problems).Kind);
        Assert.True(Stream(report).Verification!.IsLossy);
        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-crc2-needs-correction-file");
    }

    [Fact]
    public void ADecodedConsistentPairIsLosslessWithItsCorrectionFile()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Main, "song.wv");
        folder.Copy(Correction, "song.wvc");

        Report report = Analyzer.CreateDefault().AnalyzeFile(wv, Decoding);

        StreamInfo stream = Stream(report);
        Assert.Equal((CorrectionStatus.Consistent, "song.wvc"), (stream.Correction!.Status, stream.Correction.Source));
        Assert.Equal((true, true, false), (stream.Verification!.IsVerified == true, stream.Verification.IsCorrected, stream.Verification.IsLossy));
        Assert.DoesNotContain(report.Findings, f => f.Code == "wavpack.legacy-crc2-needs-correction-file");
        StreamInfo correction = Assert.Single(Assert.Single(report.Companions).Report.Streams);
        Assert.True(correction.IsCorrectionFile);
    }

    [Fact]
    public void AMissingCorrectionFileLeavesTheLossyDecodeAndItsCrc2Finding()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Main, "song.wv");

        Report report = Analyzer.CreateDefault().AnalyzeFile(wv, Decoding);

        Assert.Equal(CorrectionStatus.Missing, Stream(report).Correction!.Status);
        Assert.Contains(report.Findings, f => f.Code == "correction.missing");
        Assert.Contains(report.Findings, f => f.Code == "wavpack.legacy-crc2-needs-correction-file");
        Assert.Equal((true, false), (Stream(report).Verification!.IsLossy, Stream(report).Verification!.IsCorrected));
        Assert.Empty(report.Companions);
    }

    [Theory]
    [InlineData("external-wv340-lossless-16-stereo.wv")]
    [InlineData("external-wv320-lossy-b6.wv")]
    [InlineData("external-wv200-lossy-stereo-b8.wv")]
    public void AFileWithoutTheHybridCodingAsksForNoCompanion(string name)
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Fixture(name), "song.wv");
        folder.Copy(Correction, "song.wvc");

        Report report = Analyzer.CreateDefault().AnalyzeFile(wv, Decoding);

        Assert.Null(Stream(report).Correction);
        Assert.Empty(report.Companions);
    }

    // The correction bitstream is read bit by bit beside the main one, from bytes of any value and of any length, and the main file may be damaged as well (its headers
    // among them): the analysis ends with findings, never a parse or decode failure.
    [Theory]
    [InlineData("external-wv397-hybrid-320-correction")]
    [InlineData("external-wv397-hybrid-6-correction")]
    [InlineData("external-wv340-hybrid-320-24-correction")]
    public void ADamagedPairNeverFailsTheAnalysis(string stem)
    {
        byte[] main = File.ReadAllBytes(Fixture(stem + ".wv"));
        byte[] correction = File.ReadAllBytes(Fixture(stem + "-wvc.wvc"));
        var random = new Random(23);
        for (int round = 0; round < 72; round++)
        {
            byte[] wvc = [.. correction];
            byte[] damaged = [.. main];
            switch (round % 9)
            {
                case 0:
                    wvc[random.Next(wvc.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    wvc = wvc[..random.Next(wvc.Length)];
                    break;
                case 2:
                    wvc = [.. wvc, .. new byte[random.Next(1, 9000)]];
                    break;
                case 3:
                    random.NextBytes(wvc.AsSpan(random.Next(wvc.Length / 2), 64));
                    break;
                case 4:
                    // A bit of the main file, the WAV and WavPack headers among them.
                    damaged[random.Next(120)] ^= (byte)(1 << random.Next(8));
                    break;
                case 5:
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 6:
                    damaged = damaged[..random.Next(damaged.Length)];
                    break;
                case 7:
                    random.NextBytes(damaged.AsSpan(random.Next(damaged.Length - 200), 200));
                    break;
                default:
                    wvc = new byte[random.Next(correction.Length * 2)];
                    random.NextBytes(wvc);
                    break;
            }

            using var stream = new MemoryStream(damaged);
            Report report = Analyzer.CreateDefault().Analyze(stem + ".wv", stream, new AnalysisOptions { Decoding = new DecodeOptions(), Companions = CompanionSource.From(_ => new MemoryStream(wvc)) });

            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        }
    }

    // A stream of pseudo-random bytes of any length that holds nothing: its bytes are made as they are read.
    private sealed class VirtualStream(long length) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _position);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = (byte)((_position + i) * 2654435761L >> 13);
            }

            _position += n;
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => length + offset };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The header of a pair's main file and noise for its bitstream, so that the stream is many frames long and states more samples than the bytes hold.
    private static byte[] NoiseMain(int bytes)
    {
        byte[] head = File.ReadAllBytes(Main)[..80];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(60), int.MaxValue);
        byte[] noise = new byte[bytes];
        new Random(7).NextBytes(noise);
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] &= 0x7F;
        }

        return [.. head, .. noise];
    }

    [Fact]
    public void ALargeCorrectionFileIsReadByTheDecodersDemandAndHoldsNothingBeyondAWindow()
    {
        byte[] main = NoiseMain(8 << 20);
        var correction = new VirtualStream(2L << 30);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Report report = Analyzer.CreateDefault().Analyze("big.wv", new MemoryStream(main), new AnalysisOptions { Decoding = new DecodeOptions(), Companions = CompanionSource.From(_ => correction) });

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(DecodeStatus.Decoded, Stream(report).Verification!.Decoding);
        // The decoder takes a few MiB of the 2 GiB, and the buffers stay within a window of them.
        Assert.True(correction.BytesRead < 64L << 20, $"{correction.BytesRead} bytes of the correction file were read.");
        Assert.True(allocated < 96L << 20, $"{allocated} bytes were allocated.");
    }

    [Fact]
    public void AMainFileThatEndsEarlyDoesNotReadTheWholeCorrectionFile()
    {
        byte[] main = File.ReadAllBytes(Main);
        main = main[..(main.Length * 2 / 3)];
        var correction = new VirtualStream(8L << 30);

        Report report = Analyzer.CreateDefault().Analyze("early.wv", new MemoryStream(main), new AnalysisOptions { Decoding = new DecodeOptions(), Companions = CompanionSource.From(_ => correction) });

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.True(correction.BytesRead < 64L << 20, $"{correction.BytesRead} bytes of the correction file were read.");
    }

    [Theory]
    [InlineData("external-wv300-stereo-16.wv")]
    [InlineData("external-wv397-hybrid-320-correction.wv")]
    public void AWavPack1To3FileAsTheCompanionOfAVersion4FileIsNotItsCorrectionData(string companion)
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Path.Combine(Fixtures.FixtureDirectory, "wavpack", "external-hybrid-b4-correction.wv"), "song.wv");
        folder.Copy(Fixture(companion), "song.wvc");

        Report report = Analyzer.CreateDefault().AnalyzeFile(wv, Decoding);

        Assert.Equal(CorrectionProblemKind.NotCorrectionData, Assert.Single(Stream(report).Correction!.Problems).Kind);
        CompanionReport pair = Assert.Single(report.Companions);
        Assert.Empty(pair.Report.Streams);
        Assert.DoesNotContain(pair.Report.Findings, f => f.Code.StartsWith("correction.", StringComparison.Ordinal));
    }
}
