using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The host's detection rules with fake tag parsers: anchor walks, evaluation order, ownership
// of bytes, and what a format parser gets to see.
public class AnalyzerTagTests
{
    private sealed class FakeTagParser(
        string name,
        Func<ByteReader, long, TagProbe?>? at = null,
        Func<ByteReader, long, TagProbe?>? before = null,
        Func<long, long, TagProbe?>? find = null) : ITagParser
    {
        public string Name { get; } = name;

        public List<(long From, long Limit)> Searches { get; } = [];

        public TagProbe? ProbeAt(TagProbeContext context, long start) => at?.Invoke(context.Reader, start);

        public TagProbe? ProbeBefore(TagProbeContext context, long end) => before?.Invoke(context.Reader, end);

        public TagProbe? FindFrom(TagProbeContext context, long from, long limit)
        {
            Searches.Add((from, limit));
            return find?.Invoke(from, limit);
        }
    }

    private sealed class RecordingFormatParser(int confidence = 0) : IFormatParser
    {
        public string Name => "recording";

        public (long Start, long End, long Position)? Seen { get; private set; }

        public IReadOnlyList<Node>? Embedded { get; private set; }

        public int Sniff(SniffContext context)
        {
            ByteReader reader = context.Reader;
            Seen = (reader.Start, reader.End, reader.Position);
            return confidence;
        }

        public void Parse(ParseContext context)
        {
            Embedded = context.EmbeddedTags;
        }
    }

    private static TagProbe Accept(string name, long offset, long length) =>
        TagProbe.Accept(new Node(name, offset, length), offset, Confidence.High, [], []);

    // Accepts a fixed-size tag ending at any anchor in ends.
    private static FakeTagParser Trailing(string name, int size, params long[] ends) =>
        new(name, before: (_, end) => ends.Contains(end) ? Accept(name, end - size, size) : null);

    private static Report Analyze(int length, IEnumerable<ITagParser> tags, IEnumerable<IFormatParser>? formats = null) =>
        new Analyzer(tags, formats ?? []).Analyze("test.bin", new MemoryStream(new byte[length]));

    [Fact]
    public void EmptyFileProducesFinding()
    {
        Report report = Analyze(0, []);

        Assert.Equal("file.empty", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void TrailingTagsAreChainedFromTheEnd()
    {
        Report report = Analyze(100, [Trailing("A", 10, 100), Trailing("B", 20, 90)]);

        Assert.Equal(["Data", "B", "A"], report.Root.Children.Select(c => c.Name));
        Assert.Equal(70, report.Root.Children[0].Length);
        Assert.Equal(70, report.Root.Children[1].Offset);
    }

    // A probe that throws is a rejected candidate and a parser failure; the other parsers still
    // probe the anchor.
    [Fact]
    public void ProbeExceptionRejectsTheCandidate()
    {
        var buggy = new FakeTagParser("Buggy", before: (_, end) => end == 100 ? throw new ArgumentOutOfRangeException("size") : null);

        Report report = Analyze(100, [buggy, Trailing("Good", 10, 100)]);

        Assert.Equal(["Data", "Good"], report.Root.Children.Select(c => c.Name));
        Finding failed = Assert.Single(report.Findings, f => f.Code == "parse.failed");
        Assert.Equal(Severity.Error, failed.Severity);
        Assert.StartsWith("The Buggy parser failed probing for a tag at 0x64: ArgumentOutOfRangeException:", failed.Message);
        Assert.Contains("Buggy signature at 0x64 (EOF-0) rejected: the candidate overlaps the Good tag", Assert.Single(report.Findings, f => f.Code == "tag.near-miss").Message);
    }

    [Fact]
    public void SearchExceptionIsAParserFailure()
    {
        var seeking = new FakeTagParser(
            "Seek",
            at: (_, start) => start == 0 ? TagProbe.Accept(new Node("Seek", 0, 10), 0, Confidence.High, [], [], nextTagOffset: 20) : null,
            find: (_, _) => throw new InvalidOperationException("bug"));

        Report report = Analyze(100, [seeking]);

        Assert.Contains(report.Findings, f => f.Code == "parse.failed" && f.Message.StartsWith("The Seek parser failed searching for a tag from 0x14", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Code == "tag.next-missing");
    }

    [Fact]
    public void TagOutsideTheFileIsRejected()
    {
        var parser = new FakeTagParser("Long", at: (_, start) => start == 0 ? TagProbe.Accept(new Node("Long", 0, 120), 0, Confidence.High, [], []) : null);

        Report report = Analyze(100, [parser]);

        Assert.Equal(["Data"], report.Root.Children.Select(c => c.Name));
        Assert.Contains("does not fit the file", Assert.Single(report.Findings, f => f.Code == "tag.rejected").Message);
    }

    // The host shows Detection next to the parser's children without changing the parser's node,
    // so a tag whose children are built on demand is accepted.
    [Fact]
    public void TagWithChildrenBuiltOnDemandIsAccepted()
    {
        var lazy = new Node("Lazy", 0, 10, "v", () => [new Node("Field", 0, 4)]);
        var finding = new Finding(Severity.Warning, "lazy.odd", "Odd.", lazy);
        var parser = new FakeTagParser("Lazy", at: (_, start) => start == 0 ? TagProbe.Accept(lazy, 0, Confidence.High, [], [finding]) : null);

        Report report = Analyze(20, [parser]);

        Node shown = report.Root.Children[0];
        Assert.Equal(("Lazy", 0L, 10L, "v"), (shown.Name, shown.Offset, shown.Length, shown.Value));
        Assert.Equal(["Field", "Detection"], shown.Children.Select(c => c.Name));
        Assert.Equal(["Field"], lazy.Children.Select(c => c.Name));
        Assert.Same(shown, Assert.Single(report.Findings, f => f.Code == "lazy.odd").Node);
    }

    [Fact]
    public void FirstParserInOrderOwnsTheAnchorAndTheOtherIsANearMiss()
    {
        Report report = Analyze(100, [Trailing("First", 30, 100), Trailing("Second", 10, 100)]);

        Assert.Equal(["Data", "First"], report.Root.Children.Select(c => c.Name));
        Finding nearMiss = Assert.Single(report.Findings, f => f.Code == "tag.near-miss");
        Assert.Equal(Severity.Info, nearMiss.Severity);
        Assert.Contains("Second signature at 0x5A (EOF-10)", nearMiss.Message);
        Assert.Equal("First", nearMiss.Node!.Name);
    }

    [Fact]
    public void OfTwoCandidatesWithTheSameConfidenceTheContainingOneWins()
    {
        Report report = Analyze(100, [Trailing("Inner", 10, 100), Trailing("Outer", 30, 100)]);

        Assert.Equal(["Data", "Outer"], report.Root.Children.Select(c => c.Name));
        Finding nearMiss = Assert.Single(report.Findings, f => f.Code == "tag.near-miss");
        Assert.Contains("Inner signature at 0x5A (EOF-10) rejected: part of Outer", nearMiss.Message);
    }

    [Fact]
    public void HigherConfidenceWinsOverContainment()
    {
        var outer = new FakeTagParser("Outer", before: (_, end) => end == 100 ? TagProbe.Accept(new Node("Outer", 70, 30), 70, Confidence.Medium, [], []) : null);

        Report report = Analyze(100, [Trailing("Inner", 10, 100), outer]);

        Assert.Equal(["Data", "Inner"], report.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void LeadingRejectionInsideATrailingTagOfTheSameKindIsNotReported()
    {
        var parser = new FakeTagParser(
            "Footer",
            at: (_, start) => start == 0 ? TagProbe.Reject(0, "a footer starts no tag") : null,
            before: (_, end) => end == 40 ? Accept("Footer", 0, 40) : null);

        Report report = Analyze(40, [parser]);

        Assert.Equal(["Footer"], report.Root.Children.Select(c => c.Name));
        Assert.Equal("layout.tags-only", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void LeadingRejectionInsideATrailingTagOfAnotherKindIsANearMiss()
    {
        var lead = new FakeTagParser("Lead", at: (_, start) => start == 0 ? TagProbe.Reject(0, "bad size") : null);

        Report report = Analyze(40, [lead, Trailing("Tail", 40, 40)]);

        Assert.DoesNotContain(report.Findings, f => f.Code == "tag.rejected");
        Finding nearMiss = Assert.Single(report.Findings, f => f.Code == "tag.near-miss");
        Assert.Contains("Lead signature at 0x0 (EOF-40) rejected: part of Tail", nearMiss.Message);
    }

    [Fact]
    public void RejectedCandidateIsAWarningOnTheDataItSitsIn()
    {
        var parser = new FakeTagParser("Broken", before: (_, end) => TagProbe.Reject(end - 4, "bad checksum"));

        Report report = Analyze(50, [parser]);

        Finding rejected = Assert.Single(report.Findings, f => f.Code == "tag.rejected");
        Assert.Equal(Severity.Warning, rejected.Severity);
        Assert.Equal("Broken candidate at 0x2E (EOF-4) rejected: bad checksum.", rejected.Message);
        Assert.Equal("Data", rejected.Node!.Name);
    }

    [Fact]
    public void TrailingCandidateInsideALeadingTagIsANearMiss()
    {
        var leading = new FakeTagParser("Lead", at: (_, start) => start == 0 ? Accept("Lead", 0, 80) : null);
        FakeTagParser trailing = Trailing("Tail", 40, 100);

        Report report = Analyze(100, [leading, trailing]);

        Assert.Equal(["Lead", "Data"], report.Root.Children.Select(c => c.Name));
        Finding nearMiss = Assert.Single(report.Findings, f => f.Code == "tag.near-miss");
        Assert.Contains("rejected: part of Lead", nearMiss.Message);
    }

    [Fact]
    public void LeadingTagsAreChainedFromTheStart()
    {
        var parser = new FakeTagParser("Lead", at: (_, start) => start < 30 ? Accept("Lead", start, 15) : null);

        Report report = Analyze(100, [parser]);

        Assert.Equal(["Lead", "Lead", "Data"], report.Root.Children.Select(c => c.Name));
        Assert.Equal(30, report.Root.Children[2].Offset);
    }

    [Fact]
    public void ProbeThatRunsOffTheEndBecomesARejection()
    {
        var parser = new FakeTagParser("Short", before: (_, _) => throw new EndOfStreamException());

        Report report = Analyze(20, [parser]);

        Assert.Contains(report.Findings, f => f.Code == "tag.rejected" && f.Message.Contains("ends inside the tag"));
    }

    [Fact]
    public void FormatParserSeesOnlyTheUnclaimedBytes()
    {
        var leading = new FakeTagParser("Lead", at: (_, start) => start == 0 ? Accept("Lead", 0, 10) : null);
        var format = new RecordingFormatParser();

        Analyze(100, [leading, Trailing("Tail", 25, 100)], [format]);

        Assert.Equal((10L, 75L, 10L), format.Seen);
    }

    [Fact]
    public void FileMadeOfTagsReportsThatNoDataIsLeft()
    {
        Report report = Analyze(40, [Trailing("Tail", 20, 40, 20)]);

        Assert.Equal(["Tail", "Tail"], report.Root.Children.Select(c => c.Name));
        Assert.Equal("layout.tags-only", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void AcceptedTagsGetADetectionNodeWithEvidence()
    {
        var parser = new FakeTagParser(
            "Tag",
            before: (_, end) => end == 30
                ? TagProbe.Accept(new Node("Tag", 20, 10), 26, Confidence.Medium, [new Evidence("Signature", 26, 4)], [])
                : null);

        Report report = Analyze(30, [parser]);

        Node detection = Assert.Single(report.Root.Children[1].Children, c => c.Name == "Detection");
        Assert.Equal("Medium", detection.Value);
        Node evidence = Assert.Single(detection.Children);
        Assert.Equal(("Signature", 26L, 4L), (evidence.Value!, evidence.Offset, evidence.Length));
    }

    [Fact]
    public void TagThatDoesNotTouchItsAnchorIsRejected()
    {
        var parser = new FakeTagParser("Drifting", before: (_, end) => Accept("Drifting", 0, 10));

        Report report = Analyze(50, [parser]);

        Assert.Equal(["Data"], report.Root.Children.Select(c => c.Name));
        Assert.Contains(report.Findings, f => f.Code == "tag.rejected" && f.Message.Contains("not anchored"));
    }

    [Fact]
    public void LeadingTagFoundAgainFromTheEndIsNotANearMiss()
    {
        var parser = new FakeTagParser(
            "Footer",
            at: (_, start) => start == 0 ? Accept("Footer", 0, 40) : null,
            before: (_, end) => end == 40 ? Accept("Footer", 0, 40) : null);

        Report report = Analyze(40, [parser]);

        Assert.Equal(["Footer"], report.Root.Children.Select(c => c.Name));
        Assert.Equal("layout.tags-only", Assert.Single(report.Findings).Code);
    }

    private static TagProbe AcceptWithNext(string name, long offset, long length, long next) =>
        TagProbe.Accept(new Node(name, offset, length), offset, Confidence.High, [], [], next);

    [Fact]
    public void NextTagOffsetIsSearchedFromAndSplitsTheData()
    {
        var parser = new FakeTagParser(
            "Lead",
            at: (_, start) => start == 0 ? AcceptWithNext("Lead", 0, 10, 40) : null,
            find: (from, _) => from == 40 ? Accept("Lead", 50, 10) : null);
        var format = new RecordingFormatParser(confidence: 50);

        Report report = Analyze(100, [parser], [format]);

        Assert.Equal((40L, 100L), parser.Searches.Single());
        Assert.Equal(["Lead", "Lead"], report.Root.Children.Select(c => c.Name));
        Assert.Equal(50L, Assert.Single(format.Embedded!).Offset);
    }

    [Fact]
    public void WithoutAFormatParserTheDataAroundAnEmbeddedTagIsSplit()
    {
        var parser = new FakeTagParser(
            "Lead",
            at: (_, start) => start == 0 ? AcceptWithNext("Lead", 0, 10, 40) : null,
            find: (from, _) => from == 40 ? Accept("Lead", 50, 10) : null);

        Report report = Analyze(100, [parser]);

        Assert.Equal(["Lead@0", "Data@10", "Lead@50", "Data@60"], report.Root.Children.Select(c => $"{c.Name}@{c.Offset}"));
    }

    [Fact]
    public void NextTagSearchIsBoundedByTheNextKnownTag()
    {
        var parser = new FakeTagParser(
            "Tag",
            at: (_, start) => start == 0 ? AcceptWithNext("Tag", 0, 10, 20) : null,
            before: (_, end) => end == 100 ? Accept("Tag", 80, 20) : null);

        Report report = Analyze(100, [parser]);

        Assert.Equal((20L, 80L), parser.Searches.Single());
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("tag.next", StringComparison.Ordinal));
    }

    [Fact]
    public void FindingsAreInFileOrder()
    {
        var parser = new FakeTagParser(
            "Tag",
            at: (_, start) => start == 0 ? TagProbe.Accept(new Node("Tag", 0, 10), 0, Confidence.High, [], [new Finding(Severity.Info, "x.late", "late", new Node("n", 5, 1))]) : null,
            before: (_, end) => end == 100 ? TagProbe.Accept(new Node("Tag", 90, 10), 90, Confidence.High, [], [new Finding(Severity.Info, "x.end", "end", new Node("n", 95, 1))]) : null);

        Report report = Analyze(100, [parser]);

        Assert.Equal(["x.late", "format.unrecognized", "x.end"], report.Findings.Select(f => f.Code));
    }
}
