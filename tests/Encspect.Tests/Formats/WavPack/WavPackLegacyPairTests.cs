using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

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
        Assert.Equal(!isEmpty, Stream(report).Verification!.WithCorrection);
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
        Node root = Assert.Single(report.Companions).Report.Root;
        Assert.Equal(["Correction Bitstream", "Bytes Not Reached"], root.Children.Select(c => c.Name));
        Assert.Equal(16, root.Children[1].Length);
        Assert.Equal(root.Children[0].End, root.Children[1].Offset);
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
        Assert.Equal((true, true, false), (stream.Verification!.IsVerified == true, stream.Verification.WithCorrection, stream.Verification.IsLossy));
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
        Assert.Equal((true, false), (Stream(report).Verification!.IsLossy, Stream(report).Verification!.WithCorrection));
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

    // The correction bitstream is read bit by bit beside the main one, from bytes of any value and of any length, in frames the main bitstream's progress sets.
    [Theory]
    [InlineData("external-wv397-hybrid-320-correction")]
    [InlineData("external-wv397-hybrid-6-correction")]
    [InlineData("external-wv340-hybrid-320-24-correction")]
    public void ADamagedPairNeverFailsTheAnalysis(string stem)
    {
        byte[] main = File.ReadAllBytes(Fixture(stem + ".wv"));
        byte[] correction = File.ReadAllBytes(Fixture(stem + "-wvc.wvc"));
        var random = new Random(23);
        for (int round = 0; round < 40; round++)
        {
            byte[] wvc = [.. correction];
            switch (round % 4)
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
                default:
                    random.NextBytes(wvc.AsSpan(random.Next(wvc.Length / 2), 64));
                    break;
            }

            using var stream = new MemoryStream(main);
            Report report = Analyzer.CreateDefault().Analyze(stem + ".wv", stream, new AnalysisOptions { Decoding = new DecodeOptions(), Companions = CompanionSource.From(_ => new MemoryStream(wvc)) });

            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
            Assert.NotNull(Stream(report).Correction);
        }
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
