using System.Text.Json;
using Encspect.Cli;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.WavPack;

// The pair of a hybrid .wv file and its .wvc file found beside it (docs/design/correction-files.md, "WavPack"): the name rule on real files, the
// statuses of the lookups, and the words around the pair. The damaged pairs are the fixtures' manifests (WavPackFixtureTests).
public class WavPackPairTests
{
    private static readonly string FixtureFolder = Path.Combine(Fixtures.FixtureDirectory, "wavpack");

    private static string Fixture(string name) => Path.Combine(FixtureFolder, name);

    private static readonly string Main = Fixture("external-hybrid-b4-correction.wv");

    private static readonly string Correction = Fixture("external-hybrid-b4-correction-wvc.wvc");

    private static Report AnalyzeFile(string path, AnalysisOptions? options = null) => Analyzer.CreateDefault().AnalyzeFile(path, options);

    private static StreamInfo Stream(Report report) => Assert.Single(report.Streams);

    [Fact]
    public void ABesidePairIsConsistentAndTheCorrectionFileIsAReportOfItsOwn()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Main, "song.wv");
        folder.Copy(Correction, "song.wvc");

        Report report = AnalyzeFile(wv);

        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal((CorrectionStatus.Consistent, "song.wvc", CorrectionBasis.Structure), (pairing.Status, pairing.Source, pairing.Basis));
        Assert.Empty(report.Findings);
        CompanionReport pair = Assert.Single(report.Companions);
        Assert.Equal(Path.Combine(folder.Path, "song.wvc"), pair.Report.Source);
        StreamInfo correction = Assert.Single(pair.Report.Streams);
        Assert.True(correction.IsCorrectionFile);
        Assert.Equal("WavPack correction data", correction.Codec);
        Assert.Empty(pair.Report.Findings);

        // The checksums of both files are compared; the lossless audio is for the decoder.
        ChecksumTally tally = Stream(report).Verification!.Checksums.Single(t => t.Name == "correction block checksum");
        Assert.Equal((5, 5, 0), (tally.Stored, tally.Checked, tally.Failed));
    }

    [Fact]
    public void TheExtensionIsWrittenInTheCaseOfTheInputs()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Main, "SONG.WV");
        folder.Copy(Correction, "SONG.WVC");

        CorrectionPairing pairing = Stream(AnalyzeFile(wv)).Correction!;
        Assert.Equal((CorrectionStatus.Consistent, "SONG.WVC"), (pairing.Status, pairing.Source));
    }

    [Fact]
    public void AHybridFileUnderAnotherExtensionIsPairedByItsStem()
    {
        using var folder = new TempFolder();
        string bin = folder.Copy(Main, "song.bin");
        folder.Copy(Correction, "song.wvc");

        Assert.Equal((CorrectionStatus.Consistent, "song.wvc"), (Stream(AnalyzeFile(bin)).Correction!.Status, Stream(AnalyzeFile(bin)).Correction!.Source));
    }

    [Fact]
    public void AWrittenCorrectionFileThatIsNotThereIsMissing()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Fixture("external-hybrid-b4-correction-md5.wv"), "song.wv");

        Report report = AnalyzeFile(wv);

        Assert.Equal((CorrectionStatus.Missing, "song.wvc"), (Stream(report).Correction!.Status, Stream(report).Correction!.Source));
        Assert.Equal("correction.missing", Assert.Single(report.Findings).Code);
        Assert.Empty(report.Companions);
    }

    [Fact]
    public void AFileWrittenWithoutACorrectionFileHasNoneWrittenAndNoFinding()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Fixture("external-hybrid-b4-md5.wv"), "song.wv");

        Report report = AnalyzeFile(wv);

        Assert.Equal(CorrectionStatus.NotWritten, Stream(report).Correction!.Status);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void AStreamHasNoFolderSoThePairIsUnchecked()
    {
        using var stream = new MemoryStream(File.ReadAllBytes(Main));

        Report report = Analyzer.CreateDefault().Analyze("song.wv", stream);

        Assert.Equal(CorrectionStatus.Unchecked, Stream(report).Correction!.Status);
        Assert.Equal("correction.unchecked", Assert.Single(report.Findings).Code);
        Assert.Empty(report.Companions);
    }

    [Fact]
    public void ALosslessFileAsksForNoCompanion()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Fixture("external-lossless-16-stereo.wv"), "song.wv");
        folder.Copy(Correction, "song.wvc");

        Report report = AnalyzeFile(wv);

        Assert.Null(Stream(report).Correction);
        Assert.Empty(report.Companions);
    }

    [Fact]
    public void ACorrectionFileOpenedAloneSaysToAnalyzeTheMainFile()
    {
        Report report = AnalyzeFile(Correction);

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("wavpack.correction-file", finding.Code);
        Assert.Contains("analyze the .wv file beside it", finding.Message);
        Assert.Empty(report.Companions);
        Assert.True(Stream(report).IsCorrectionFile);
        Assert.Null(Stream(report).Correction);
    }

    [Fact]
    public void ACorrectionFileGivenAsTheCompanionIsNotAskedForAnother()
    {
        using var folder = new TempFolder();
        string wv = folder.Copy(Main, "song.wv");
        folder.Copy(Main, "song.wvc");

        Report report = AnalyzeFile(wv);

        // A hybrid file in the place of the correction file holds no correction data, and its own lookup is not made.
        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal(CorrectionStatus.Inconsistent, pairing.Status);
        Assert.Equal(CorrectionProblemKind.NotCorrectionData, Assert.Single(pairing.Problems).Kind);
        Assert.Empty(Assert.Single(report.Companions).Report.Streams);
    }

    [Fact]
    public void AFolderWalkAnalyzesEveryFileAndThePairOnceFromTheMainFile()
    {
        using var folder = new TempFolder();
        folder.Copy(Main, "song.wv");
        folder.Copy(Correction, "song.wvc");

        using var stdout = new MemoryStream();
        Assert.Equal(CommandLine.Success, CommandLine.Run(["--json", folder.Path], stdout, new StringWriter()));
        using JsonDocument document = JsonDocument.Parse(stdout.ToArray());

        JsonElement[] reports = [.. document.RootElement.EnumerateArray()];
        Assert.Equal(["song.wv", "song.wvc"], reports.Select(r => Path.GetFileName(r.GetProperty("source").GetString()!)));
        Assert.Equal(1, reports[0].GetProperty("companions").GetArrayLength());
        Assert.False(reports[1].TryGetProperty("companions", out _));
        Assert.Equal("WavPack correction data", reports[1].GetProperty("streams")[0].GetProperty("codec").GetString());
    }
}
