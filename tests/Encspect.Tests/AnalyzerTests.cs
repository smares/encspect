using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

public class AnalyzerTests
{
    private sealed class FakeParser : IFormatParser
    {
        private readonly Func<ByteReader, int> _sniff;
        private readonly Action<ParseContext> _parse;

        public FakeParser(string name, Func<ByteReader, int> sniff, Action<ParseContext>? parse = null)
        {
            Name = name;
            _sniff = sniff;
            _parse = parse ?? (context => context.Root.Add("Header", 0, 4));
        }

        public string Name { get; }

        public int ParseCalls { get; private set; }

        public int Sniff(ByteReader reader) => _sniff(reader);

        public void Parse(ParseContext context)
        {
            ParseCalls++;
            _parse(context);
        }
    }

    private static MemoryStream CreateStream(int length) => new(new byte[length]);

    [Fact]
    public void FileWithoutMatchingParserProducesFinding()
    {
        var analyzer = new Analyzer([]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(3));

        Assert.Equal(3L, report.Root.Length);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("format.unrecognized", finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void ParserWithZeroConfidenceIsNotUsed()
    {
        var parser = new FakeParser("none", _ => 0);
        var analyzer = new Analyzer([parser]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(8));

        Assert.Equal(0, parser.ParseCalls);
        Assert.Equal("format.unrecognized", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void ParserWithHighestConfidenceWins()
    {
        var low = new FakeParser("low", _ => 10);
        var high = new FakeParser("high", _ => 90);
        var analyzer = new Analyzer([low, high]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(8));

        Assert.Equal(0, low.ParseCalls);
        Assert.Equal(1, high.ParseCalls);
        Assert.Equal("high", report.Root.Value);
        Assert.Equal("Header", Assert.Single(report.Root.Children).Name);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ReaderIsResetToTheStartForEverySniffAndForParse()
    {
        long positionSeenByParse = -1;
        var first = new FakeParser(
            "first",
            reader =>
            {
                reader.ReadBytes(2);
                return 10;
            });
        var second = new FakeParser(
            "second",
            reader => reader.Position == 0 ? 50 : 0,
            context => positionSeenByParse = context.Reader.Position);
        var analyzer = new Analyzer([first, second]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(8));

        Assert.Equal("second", report.Root.Value);
        Assert.Equal(0L, positionSeenByParse);
    }

    [Fact]
    public void ShortFileDuringSniffCountsAsNoMatch()
    {
        var parser = new FakeParser(
            "needs-eight-bytes",
            reader =>
            {
                reader.ReadBytes(8);
                return 100;
            });
        var analyzer = new Analyzer([parser]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(3));

        Assert.Equal(0, parser.ParseCalls);
        Assert.Equal("format.unrecognized", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void TruncatedStructureBecomesErrorFinding()
    {
        var parser = new FakeParser("truncating", _ => 100, _ => throw new EndOfStreamException());
        var analyzer = new Analyzer([parser]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(8));

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("parse.truncated", finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
    }

    [Fact]
    public void InvalidDataBecomesErrorFindingWithMessage()
    {
        var parser = new FakeParser("invalid", _ => 100, _ => throw new InvalidDataException("bad header"));
        var analyzer = new Analyzer([parser]);

        Report report = analyzer.Analyze("sample.bin", CreateStream(8));

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("parse.invalid", finding.Code);
        Assert.Equal("bad header", finding.Message);
    }
}
