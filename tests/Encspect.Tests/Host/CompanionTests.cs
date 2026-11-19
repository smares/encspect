using System.Text;
using System.Text.Json;
using Encspect.Cli;
using Encspect.Core;
using Encspect.Formats.OptimFrog;
using Encspect.Host;

namespace Encspect.Tests;

// The host's companion files (docs/design/correction-files.md): where the host looks (CompanionSource), how the file is prepared and reported
// (Report.Companions), and how the pairing reads in the findings, the text, the JSON and the CSV. OptimFROG's pair is the smallest consumer: its parser asks
// for the .ofc, so these tests use the committed .ofs and .ofc files.
public class CompanionTests
{
    private static readonly string Fixture = Path.Combine(Fixtures.FixtureDirectory, "optimfrog");

    private static readonly string Main = Path.Combine(Fixture, "external-ofs-mono-quality3.ofs");

    private static readonly string Correction = Path.Combine(Fixture, "external-ofs-mono-quality3-correction-data.ofc");

    private static Report AnalyzeFile(string path, AnalysisOptions? options = null) => Analyzer.CreateDefault().AnalyzeFile(path, options);

    private static StreamInfo Stream(Report report) => Assert.Single(report.Streams);

    [Fact]
    public void ABesideCompanionIsFoundByTheNameRuleInTheFolderOfTheInput()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        folder.Copy(Correction, "song.ofc");

        Report report = AnalyzeFile(ofs);

        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal((CorrectionStatus.Consistent, "song.ofc"), (pairing.Status, pairing.Source));
        CompanionReport companion = Assert.Single(report.Companions);
        Assert.Equal(Path.Combine(folder.Path, "song.ofc"), companion.Report.Source);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void TheExtensionIsWrittenInTheCaseOfTheInputs()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "SONG.OFS");
        folder.Copy(Correction, "SONG.OFC");

        CorrectionPairing pairing = Stream(AnalyzeFile(ofs)).Correction!;
        Assert.Equal((CorrectionStatus.Consistent, "SONG.OFC"), (pairing.Status, pairing.Source));
    }

    [Fact]
    public void AnInputWithAnotherExtensionIsPairedByItsStem()
    {
        using var folder = new TempFolder();
        string bin = folder.Copy(Main, "song.bin");
        folder.Copy(Correction, "song.ofc");

        // The extension of the .bin is no signal of the lossy part, and the bytes alone make a claim at low confidence, which asks for the companion.
        Assert.Equal(CorrectionStatus.Consistent, Stream(AnalyzeFile(bin)).Correction!.Status);
    }

    [Fact]
    public void OneNameIsTriedSoADifferentCaseIsMissingOnACaseSensitiveFileSystem()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.OFS");
        folder.Copy(Correction, "song.ofc");

        // Only where the file system tells the case apart.
        if (!File.Exists(Path.Combine(folder.Path, "song.OFC")))
        {
            Assert.Equal(CorrectionStatus.Missing, Stream(AnalyzeFile(ofs)).Correction!.Status);
        }
    }

    [Fact]
    public void AMissingCompanionIsReportedAndTheStreamIsItsLossyPart()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");

        Report report = AnalyzeFile(ofs);

        Assert.Equal((CorrectionStatus.Missing, "song.ofc"), (Stream(report).Correction!.Status, Stream(report).Correction!.Source));
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("correction.missing", Severity.Info), (finding.Code, finding.Severity));
        Assert.Contains("song.ofc", finding.Message);
        Assert.Empty(report.Companions);
        Assert.False(Stream(report).Verification!.WithCorrection);
    }

    [Fact]
    public void AFolderNamedLikeTheCompanionIsUnreadable()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        Directory.CreateDirectory(folder["song.ofc"]);

        Report report = AnalyzeFile(ofs);

        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal((CorrectionStatus.Unchecked, true), (pairing.Status, pairing.IsUnreadable));
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("correction.unreadable", Severity.Warning), (finding.Code, finding.Severity));
        Assert.Empty(report.Companions);
    }

    [Fact]
    public void AFifoNamedLikeTheCompanionIsNotOpened()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        folder.MakeFifo("song.ofc");

        Report report = Task.Run(() => AnalyzeFile(ofs)).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();

        Assert.Equal(("correction.unreadable", true), (Assert.Single(report.Findings).Code, Stream(report).Correction!.IsUnreadable));
    }

    [Fact]
    public void AStreamHasNoFolderSoThePairIsUnchecked()
    {
        using var stream = new MemoryStream(File.ReadAllBytes(Main));

        Report report = Analyzer.CreateDefault().Analyze("song.ofs", stream);

        CorrectionPairing pairing = Stream(report).Correction!;
        Assert.Equal(CorrectionStatus.Unchecked, pairing.Status);
        Assert.Contains("the input is a stream", pairing.Reason);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("correction.unchecked", Severity.Info), (finding.Code, finding.Severity));
        Assert.Empty(report.Companions);
        Assert.Equal(Confidence.Medium, Stream(report).Claims.Single().Confidence);
    }

    [Fact]
    public void NoneLooksForNothingEvenForAFile()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        folder.Copy(Correction, "song.ofc");

        Report report = AnalyzeFile(ofs, new AnalysisOptions { Companions = CompanionSource.None });

        Assert.Equal(CorrectionStatus.Unchecked, Stream(report).Correction!.Status);
        Assert.Equal("correction.unchecked", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void ACallerWithNoFileSystemHandsOverTheStreamForTheExtensionAsked()
    {
        var asked = new List<string>();
        var opened = new List<TrackedStream>();
        var options = new AnalysisOptions
        {
            Companions = CompanionSource.From(extension =>
            {
                asked.Add(extension);
                var stream = new TrackedStream(File.ReadAllBytes(Correction));
                opened.Add(stream);
                return stream;
            }),
        };
        using var main = new MemoryStream(File.ReadAllBytes(Main));

        Report report = Analyzer.CreateDefault().Analyze("whatever.ofs", main, options);

        Assert.Equal(["ofc"], asked);
        Assert.Equal((CorrectionStatus.Consistent, "whatever.ofc"), (Stream(report).Correction!.Status, Stream(report).Correction!.Source));
        Assert.Equal(Confidence.High, Stream(report).Claims.Single().Confidence);
        Assert.True(Stream(report).Verification!.WithCorrection);

        // The host owns the stream and disposes it with the analysis.
        Assert.All(opened, s => Assert.True(s.IsDisposed));
    }

    [Fact]
    public void AFunctionThatReturnsNullIsAMissingCompanionAndOneThatThrowsAnIoErrorIsUnreadable()
    {
        using var main = new MemoryStream(File.ReadAllBytes(Main));
        Report none = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => null) });
        Assert.Equal(CorrectionStatus.Missing, Stream(none).Correction!.Status);

        main.Position = 0;
        Report fails = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => throw new IOException("disk gone")) });
        Assert.True(Stream(fails).Correction!.IsUnreadable);
        Assert.Contains("disk gone", Assert.Single(fails.Findings).Message);

        main.Position = 0;
        Report cannotSeek = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => new ForwardOnlyStream(File.ReadAllBytes(Correction))) });
        Assert.Equal("correction.unreadable", Assert.Single(cannotSeek.Findings).Code);
    }

    [Fact]
    public void AnotherExceptionFromTheFunctionPassesThroughAnalyze()
    {
        using var main = new MemoryStream(File.ReadAllBytes(Main));

        Assert.Throws<InvalidOperationException>(() =>
            Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => throw new InvalidOperationException("bug")) }));
    }

    [Fact]
    public void ALosslessFileMakesNoClaimAndAsksForNoCompanion()
    {
        bool isAsked = false;
        using var main = new MemoryStream(File.ReadAllBytes(Path.Combine(Fixture, "external-md5.ofr")));

        Report report = Analyzer.CreateDefault().Analyze("x.ofr", main, new AnalysisOptions { Companions = CompanionSource.From(_ => { isAsked = true; return null; }) });

        Assert.False(isAsked);
        Assert.Null(Stream(report).Correction);
        Assert.Empty(report.Companions);
    }

    [Fact]
    public void ACorrectionFileAlsoGetsItsTagsFoundAtBothEnds()
    {
        // An ID3v1 tag after the blocks of the .ofc: the host finds it as it would in a file of its own, and the pair stays consistent.
        byte[] tag = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
        Encoding.ASCII.GetBytes("Song").CopyTo(tag, 3);
        tag[127] = 0xFF;
        byte[] correction = [.. File.ReadAllBytes(Correction), .. tag];
        using var main = new MemoryStream(File.ReadAllBytes(Main));

        Report pair = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => new MemoryStream(correction)) });
        Report alone = Analyzer.CreateDefault().Analyze("a.ofc", new MemoryStream(correction));

        Assert.Equal(CorrectionStatus.Consistent, Stream(pair).Correction!.Status);
        Report companion = Assert.Single(pair.Companions).Report;
        Assert.Equal(alone.Root.Children.Select(c => (c.Name, c.Offset, c.Length)), companion.Root.Children.Select(c => (c.Name, c.Offset, c.Length)));
        Assert.Contains(companion.Root.Children, c => c.Kind == NodeKind.Tag);
    }

    [Fact]
    public void AnEmptyCompanionHoldsNoCorrectionData()
    {
        using var main = new MemoryStream(File.ReadAllBytes(Main));

        Report report = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => new MemoryStream()) });

        CorrectionProblem problem = Assert.Single(Stream(report).Correction!.Problems);
        Assert.Equal(CorrectionProblemKind.NotCorrectionData, problem.Kind);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("correction.inconsistent", Severity.Warning), (finding.Code, finding.Severity));
        Assert.Contains(report.Companions.Single().Report.Findings, f => f.Code == "file.empty");
        Assert.False(Stream(report).Verification!.WithCorrection);
        Assert.Equal(Confidence.Medium, Stream(report).Claims.Single().Confidence);
    }

    [Fact]
    public void TheFindingsOfAnInconsistentPairPointToTheMainFilesNodesOnePerKind()
    {
        byte[] correction = File.ReadAllBytes(Correction);
        byte[] damaged = [.. correction.AsSpan(0, 8), (byte)(correction[8] ^ 1), .. correction.AsSpan(9), .. correction];
        using var main = new MemoryStream(File.ReadAllBytes(Main));

        Report report = Analyzer.CreateDefault().Analyze("a.ofs", main, new AnalysisOptions { Companions = CompanionSource.From(_ => new MemoryStream(damaged)) });

        Assert.Equal(["correction.inconsistent", "correction.inconsistent"], report.Findings.Select(f => f.Code));
        Assert.Equal([CorrectionProblemKind.BlocksUnmatched, CorrectionProblemKind.ChecksumsFail], Stream(report).Correction!.Problems.Select(p => p.Kind));
        Assert.All(report.Findings, f => Assert.True(f.Path is { Count: > 0 }));
        Assert.All(report.Findings, f => Assert.Equal(Severity.Warning, f.Severity));

        // The companion's own damage is reported in its report, with the codes it gets alone.
        Assert.Contains(report.Companions.Single().Report.Findings, f => f.Code == "optimfrog.block-crc-mismatch");
        Assert.DoesNotContain(report.Companions.Single().Report.Findings, f => f.Code == "optimfrog.correction-file");
    }

    [Fact]
    public void TheWordsAreTheSameForEveryStatus()
    {
        Node node = new("n", 0, 1);
        Assert.Equal("consistent (x.wvc), lossless with its correction file", CorrectionText.Describe(new CorrectionPairing { Status = CorrectionStatus.Consistent, Source = "x.wvc" }));
        Assert.Equal("inconsistent (x.wvc: 3 correction blocks missing), lossy without its correction file", CorrectionText.Describe(new CorrectionPairing
        {
            Status = CorrectionStatus.Inconsistent,
            Source = "x.wvc",
            Problems = [new CorrectionProblem(CorrectionProblemKind.BlocksMissing, 3, node)],
        }));
        Assert.Equal("missing (x.wvc), lossy without its correction file", CorrectionText.Describe(new CorrectionPairing { Status = CorrectionStatus.Missing, Source = "x.wvc" }));
        Assert.Equal("none written", CorrectionText.Describe(new CorrectionPairing { Status = CorrectionStatus.NotWritten }));
        Assert.Equal("unchecked (no folder to look in: the input is a stream), lossy without its correction file",
            CorrectionText.Describe(new CorrectionPairing { Status = CorrectionStatus.Unchecked, Reason = "no folder to look in: the input is a stream" }));
        Assert.Equal(["consistent", "inconsistent", "missing", "notWritten", "unchecked"], Enum.GetValues<CorrectionStatus>().Select(CorrectionText.Word));
    }

    [Fact]
    public void ThePairReadsInTheTextTheJsonAndTheCsv()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        folder.Copy(Correction, "song.ofc");
        Report report = AnalyzeFile(ofs);

        var text = new StringWriter();
        ReportTextWriter.Write(report, text, showOffsets: false);
        string[] lines = text.ToString().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains(lines, l => l.TrimStart() == "Correction file: consistent (song.ofc), lossless with its correction file");
        Assert.Contains(lines, l => l.TrimStart().StartsWith("Verified: yes (with correction file; block CRC-32 1 of 1, correction block CRC-32 1 of 1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("song.ofc (correction file)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart() == "Correction File: OptimFROG");

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        JsonElement root = document.RootElement[0];
        JsonElement stream = root.GetProperty("streams")[0];
        Assert.Equal(("consistent", "song.ofc", "structure"), (stream.GetProperty("correction").GetProperty("status").GetString(), stream.GetProperty("correction").GetProperty("source").GetString(),
            stream.GetProperty("correction").GetProperty("basis").GetString()));
        Assert.Equal(0, stream.GetProperty("correction").GetProperty("problems").GetArrayLength());
        Assert.True(stream.GetProperty("verification").GetProperty("withCorrection").GetBoolean());
        JsonElement companion = Assert.Single(root.GetProperty("companions").EnumerateArray());
        Assert.Equal("correction", companion.GetProperty("role").GetString());
        Assert.False(companion.TryGetProperty("schemaVersion", out _));
        Assert.True(companion.GetProperty("streams")[0].GetProperty("isCorrectionFile").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] rows = csv.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] header = rows[0].Split(',');
        string[] row = rows[1].Split(',');
        int column = Array.IndexOf(header, "Correction File");
        Assert.Equal(Array.IndexOf(header, "Lossy Part Confidence") + 1, column);
        Assert.Equal("consistent", row[column]);
        Assert.Equal("High", row[column - 1]);
    }

    [Fact]
    public void AFileWithoutTheConceptHasNoPairingFields()
    {
        Report report = Analyzer.CreateDefault().Analyze("x.ofr", new MemoryStream(File.ReadAllBytes(Path.Combine(Fixture, "external-md5.ofr"))));

        var json = new MemoryStream();
        ReportJsonWriter.Write([report], json);
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        Assert.False(document.RootElement[0].GetProperty("streams")[0].TryGetProperty("correction", out _));
        Assert.False(document.RootElement[0].TryGetProperty("companions", out _));
        var csv = new StringWriter();
        ReportCsvWriter.Write([report], csv);
        string[] rows = csv.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("", rows[1].Split(',')[Array.IndexOf(rows[0].Split(','), "Correction File")]);
    }

    [Fact]
    public void TheCountsOfASummaryAndTheExitCodeIncludeTheCompanionsFindings()
    {
        using var folder = new TempFolder();
        string ofs = folder.Copy(Main, "song.ofs");
        byte[] correction = File.ReadAllBytes(Correction);
        correction[8] ^= 1;
        File.WriteAllBytes(folder["song.ofc"], correction);

        Report report = AnalyzeFile(ofs);
        ReportSummary summary = ReportSummary.From(report);

        // One warning of the pair on the file and one of the correction file's CRC-32 in its own report.
        Assert.Equal(2, summary.Warnings);
        Assert.Equal(CorrectionStatus.Inconsistent, summary.Correction);

        using var stdout = new MemoryStream();
        Assert.Equal(CommandLine.FindingsFound, CommandLine.Run(["--fail-on", "warning", ofs], stdout, new StringWriter()));
        Assert.Equal(CommandLine.Success, CommandLine.Run(["--fail-on", "error", ofs], stdout, new StringWriter()));
    }

    [Fact]
    public void AFolderWalkAnalyzesEveryFileAndThePairOnceFromTheMainFile()
    {
        using var folder = new TempFolder();
        folder.Copy(Main, "song.ofs");
        folder.Copy(Correction, "song.ofc");

        using var stdout = new MemoryStream();
        Assert.Equal(CommandLine.Success, CommandLine.Run(["--json", folder.Path], stdout, new StringWriter()));
        using JsonDocument document = JsonDocument.Parse(stdout.ToArray());

        JsonElement[] reports = [.. document.RootElement.EnumerateArray()];
        Assert.Equal(["song.ofc", "song.ofs"], reports.Select(r => Path.GetFileName(r.GetProperty("source").GetString()!)));
        Assert.False(reports[0].TryGetProperty("companions", out _));
        Assert.Equal("OptimFROG correction data", reports[0].GetProperty("streams")[0].GetProperty("codec").GetString());
        Assert.Equal(1, reports[1].GetProperty("companions").GetArrayLength());
    }

    [Fact]
    public void TheCorrectionFileOpenedAloneSaysToAnalyzeTheMainFile()
    {
        Report report = AnalyzeFile(Correction);

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("optimfrog.correction-file", finding.Code);
        Assert.Contains("analyze the .ofs file beside it", finding.Message);
        Assert.Empty(report.Companions);
        Assert.True(Stream(report).IsCorrectionFile);
        Assert.Null(Stream(report).Correction);
    }

    [Fact]
    public void ANoRouterContextAsksNobodyAndIsUnchecked()
    {
        Companion companion = new ParseContext(new ByteReader(new MemoryStream([0])), new Node("File", 0, 1)).OpenCompanion("ofc");

        Assert.Equal(CompanionStatus.Unchecked, companion.Status);
        Assert.Null(companion.Context);
        Assert.True(new CorrectionPairing { Status = CorrectionStatus.Consistent }.IsLossless);
        Assert.False(new CorrectionPairing { Status = CorrectionStatus.Missing }.IsLossless);
    }

    // A stream that records that it was disposed.
    private sealed class TrackedStream(byte[] data) : MemoryStream(data)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
