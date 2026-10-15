using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// How the host ranks the formats that match, reports the others whose evidence is real, and runs the
// format a caller forces.
public class FormatDetectionTests
{
    private sealed class Scripted(string name, SniffResult result, long? end = null) : IFormatParser
    {
        public string Name => name;

        public int ParseCalls { get; private set; }

        public SniffResult Sniff(SniffContext context) => result;

        public long? DeclaredEnd(SniffContext context) => end;

        public void Parse(ParseContext context)
        {
            ParseCalls++;
            context.Root.Add("Header", 0, 4);
        }
    }

    private static Report Analyze(params IFormatParser[] parsers) => new Analyzer(parsers).Analyze("sample.bin", new MemoryStream(new byte[64]));

    [Fact]
    public void TheClassComesFirstThenTheEarlierStart()
    {
        Assert.Equal("high", Analyze(new Scripted("early", new(50, 0, 16, 16)), new Scripted("high", new(60, 40))).Root.Value);
        Assert.Equal("early", Analyze(new Scripted("late", new(50, 20, 16, 16)), new Scripted("early", new(50, 5))).Root.Value);
    }

    [Fact]
    public void VerifiedFramesComeBeforeConsistentFrames()
    {
        Assert.Equal("verified", Analyze(new Scripted("long", new(50, 5, 16, 0)), new Scripted("verified", new(50, 5, 3, 1))).Root.Value);
        Assert.Equal("long", Analyze(new Scripted("short", new(50, 5, 3, 1)), new Scripted("long", new(50, 5, 16, 1))).Root.Value);
    }

    [Fact]
    public void ListOrderOnlyBreaksATieTheEvidenceCannotAndTheReportSaysSo()
    {
        Report report = Analyze(new Scripted("first", new(50, 5, 3, 0)), new Scripted("second", new(50, 5, 3, 0, "other chain")));

        Assert.Equal("first", report.Root.Value);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("format.ambiguous", Severity.Info), (finding.Code, finding.Severity));
        Assert.Contains("first", finding.Message);
        Assert.Contains("second", finding.Message);
        Assert.Contains("other chain", finding.Message);
        Assert.Same(report.Root, finding.Node);

        FormatDetection detection = report.Detection!;
        Assert.False(detection.IsForced);
        Assert.Equal("second", Assert.Single(detection.Alternatives).Format);
    }

    [Fact]
    public void AMoreSpecificWinnerLeavesNoAmbiguity()
    {
        Report report = Analyze(new Scripted("first", new(50, 5, 3, 0)), new Scripted("second", new(50, 5, 4, 0)));

        Assert.Equal("second", report.Root.Value);
        Assert.DoesNotContain(report.Findings, f => f.Code == "format.ambiguous");
        Assert.Equal("first", Assert.Single(report.Detection!.Alternatives).Format);
    }

    [Fact]
    public void AlternativesNeedRealEvidence()
    {
        Report report = Analyze(
            new Scripted("best", new(90, 0, 3)),
            new Scripted("same class", new(90, 30, 3)),
            new Scripted("verified", new(50, 30, 3, 1)),
            new Scripted("long chain", new(50, 30, FormatDetection.AlternativeFrames)),
            new Scripted("short chain", new(50, 30, FormatDetection.AlternativeFrames - 1)),
            new Scripted("weak", new(30, 30, 1)),
            new Scripted("distrusted long chain", new(SniffResult.WeakClass, 30, 16, 1)));

        Assert.Equal("best", report.Root.Value);
        Assert.Equal(["same class", "verified", "long chain"], report.Detection!.Alternatives.Select(a => a.Format));
        Assert.Equal(new SniffResult(50, 30, 3, 1), report.Detection.Alternatives[1].Evidence);
        Assert.Empty(report.Findings);
    }

    // The samples of an MP4 file hold AC-3 frames that chain and pass their CRCs, which is the container's payload.
    [Fact]
    public void AFormatInsideTheDataAContainerDeclaresIsItsPayloadNotAnAlternative()
    {
        Report report = Analyze(
            new Scripted("container", new(100, 0, 1), end: 50),
            new Scripted("payload", new(50, 20, 16, 16)),
            new Scripted("after", new(50, 50, 16, 16)));

        Assert.Equal("container", report.Root.Value);
        Assert.Equal("after", Assert.Single(report.Detection!.Alternatives).Format);

        Assert.Null(Analyze(new Scripted("container", new(100, 0, 1), end: 64), new Scripted("payload", new(50, 20, 16, 16))).Detection);
    }

    [Fact]
    public void AReportWithNothingToAddHasNoDetection()
    {
        Assert.Null(Analyze(new Scripted("only", new(90, 0, 3)), new Scripted("weak", new(30, 20, 3))).Detection);
    }

    [Fact]
    public void AForcedFormatSkipsDetectionAndSaysWhatDetectionWouldHaveChosen()
    {
        var chosen = new Scripted("chosen", new(90, 0, 3));
        var forced = new Scripted("forced", SniffResult.None);
        var analyzer = new Analyzer([chosen, forced]);

        Report report = analyzer.Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { ForcedFormat = "FORCED" });

        Assert.Equal((0, 1), (chosen.ParseCalls, forced.ParseCalls));
        Assert.Equal("forced", report.Root.Value);
        FormatDetection detection = report.Detection!;
        Assert.True(detection.IsForced);
        Assert.Equal(("forced", "chosen"), (detection.Format, detection.Detected));
        Assert.Equal(SniffResult.None, detection.Evidence);
        Assert.Equal("chosen", Assert.Single(detection.Alternatives).Format);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("format.forced", Severity.Info), (finding.Code, finding.Severity));
        Assert.Contains("chosen", finding.Message);
    }

    [Fact]
    public void AForcedFormatThatDetectionFindsNowhereSaysSo()
    {
        var analyzer = new Analyzer([new Scripted("only", SniffResult.None)]);

        Report report = analyzer.Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { ForcedFormat = "only" });

        Assert.Equal("only", report.Root.Value);
        Assert.Null(report.Detection!.Detected);
        Assert.Empty(report.Detection.Alternatives);
        Assert.Contains("no format", Assert.Single(report.Findings).Message);
    }

    [Fact]
    public void ForcingTheFormatDetectionChoseStillSaysItWasForced()
    {
        var analyzer = new Analyzer([new Scripted("only", new(90, 0, 3))]);

        Report report = analyzer.Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { ForcedFormat = "only" });

        Assert.True(report.Detection!.IsForced);
        Assert.Equal("only", report.Detection.Detected);
        Assert.Empty(report.Detection.Alternatives);
    }

    [Fact]
    public void FormatNamesMatchByTheStartOfTheName()
    {
        Analyzer analyzer = Analyzer.CreateDefault();

        Assert.Equal(["MPEG Audio"], analyzer.MatchFormats("mpeg"));
        Assert.Equal(["MPEG Audio"], analyzer.MatchFormats("MPEG audio"));
        Assert.Equal(["AAC"], analyzer.MatchFormats("aac"));
        Assert.Equal(["AC-3"], analyzer.MatchFormats("ac-3"));
        Assert.Equal(["AAC", "AC-3", "ASF"], analyzer.MatchFormats("a"));
        Assert.Empty(analyzer.MatchFormats("flac"));
        Assert.Empty(analyzer.MatchFormats(" "));
        Assert.Equal(DefaultParsers.Formats.Select(p => p.Name), analyzer.FormatNames);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("a")]
    public void AnUnknownOrAmbiguousFormatIsRefused(string name)
    {
        Analyzer analyzer = Analyzer.CreateDefault();

        Assert.Throws<ArgumentException>(() => analyzer.Analyze("sample.bin", new MemoryStream(new byte[64]), new AnalysisOptions { ForcedFormat = name }));
    }

    [Fact]
    public void ForcedFilesAreAnalyzedAsTheFormatAndKeepTheirTags()
    {
        string path = Path.Combine(Fixtures.FixtureDirectory, "mpeg", "external-lame-cbr128-joint.mp3");
        Analyzer analyzer = Analyzer.CreateDefault();

        Report plain = analyzer.AnalyzeFile(path);
        Report forced = analyzer.AnalyzeFile(path, new AnalysisOptions { ForcedFormat = "aac" });

        Assert.Null(plain.Detection);
        Assert.Equal("MPEG Audio", plain.Root.Value);
        Assert.Equal("AAC", forced.Root.Value);
        Assert.Equal("MPEG Audio", forced.Detection!.Detected);
        Assert.DoesNotContain(forced.Findings, f => f.Code == "parse.failed");
        Assert.Contains(forced.Findings, f => f.Code == "format.forced");
        Assert.Equal(plain.Root.Children.Where(c => c.Kind == NodeKind.Tag).Select(c => (c.Name, c.Offset)), forced.Root.Children.Where(c => c.Kind == NodeKind.Tag).Select(c => (c.Name, c.Offset)));
    }

    [Fact]
    public void FixturesWithOtherFormatsInsideTheirPayloadHaveNoAlternatives()
    {
        Analyzer analyzer = Analyzer.CreateDefault();

        foreach (string name in new[] { "ac3/joc-dec3.mp4", "ac3/external-ffmpeg-mp4-5.1-448k.mp4", "mp4/mp3-in-mp4.mp4", "dts/external-ffmpeg-mp4-stereo-22k-192k.mp4" })
        {
            Assert.Null(analyzer.AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name)).Detection);
        }

        // AC-3 frames hold ADTS-looking bytes that chain: class 50 for both, the AC-3 chain starts first, and the report lists the other.
        FormatDetection junk = analyzer.AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "ac3/junk-adts-in-frames.eac3")).Detection!;
        Assert.Equal("AC-3", junk.Format);
        FormatCandidate adts = Assert.Single(junk.Alternatives);
        Assert.Equal("AAC", adts.Format);
        Assert.True(adts.Evidence.Offset > junk.Evidence.Offset);

        // The protected MPEG header inside AC-3 frames is a chain its own parser distrusts, which is no alternative.
        Assert.Null(analyzer.AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, "ac3/junk-mpeg-in-frames.ac3")).Detection);
    }

    // The parsers cope with any input, so a wrong format forced on a file gives findings and never a failure.
    [Theory]
    [MemberData(nameof(DescriptionTests.AllFixtures), MemberType = typeof(DescriptionTests))]
    public void EveryFormatForcedOnAFixtureGivesNoParseFailure(string name)
    {
        Analyzer analyzer = Analyzer.CreateDefault();
        string path = Path.Combine(Fixtures.FixtureDirectory, name);
        var problems = new List<string>();

        foreach (string format in analyzer.FormatNames)
        {
            Report report = analyzer.AnalyzeFile(path, new AnalysisOptions { ForcedFormat = format });
            problems.AddRange(report.Findings.Where(f => f.Code.StartsWith("parse.failed", StringComparison.Ordinal)).Select(f => $"{format}: {f.Message}"));
            bool tagsOnly = report.Findings.Any(f => f.Code is "layout.tags-only" or "file.empty");
            if (report.Detection is null ? !tagsOnly : !report.Detection.IsForced || report.Detection.Format != format)
            {
                problems.Add($"{format}: not reported as forced");
            }
        }

        Assert.True(problems.Count == 0, $"{name}:\n" + string.Join("\n", problems));
    }
}
