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

        public SniffResult Sniff(SniffContext context) => new(_sniff(context.Reader), context.Reader.Start);

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

    // A parser bug must not end a batch: whatever a parser throws becomes an error finding that
    // names the parser and the exception.
    [Fact]
    public void ParserExceptionBecomesParseFailed()
    {
        var parser = new FakeParser("buggy", _ => 100, context =>
        {
            context.Root.Add("Header", 0, 4);
            throw new IndexOutOfRangeException("bug");
        });

        Report report = new Analyzer([parser]).Analyze("sample.bin", CreateStream(8));

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal(("parse.failed", Severity.Error), (finding.Code, finding.Severity));
        Assert.Equal("The buggy parser failed parsing the data: IndexOutOfRangeException: bug", finding.Message);
        Assert.Same(report.Root, finding.Node);
        Assert.Equal("Header", Assert.Single(report.Root.Children).Name);
    }

    [Fact]
    public void SniffExceptionBecomesParseFailedAndNoMatch()
    {
        var buggy = new FakeParser("buggy", _ => throw new NullReferenceException("bug"));
        var other = new FakeParser("other", _ => 10);

        Report report = new Analyzer([buggy, other]).Analyze("sample.bin", CreateStream(8));

        Assert.Equal("other", report.Root.Value);
        Assert.Equal(0, buggy.ParseCalls);
        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("The buggy parser failed sniffing the data: NullReferenceException: bug", finding.Message);
    }

    [Fact]
    public void InvalidDataWhileSniffingCountsAsNoMatch()
    {
        var parser = new FakeParser("strict", _ => throw new InvalidDataException("bad magic"));

        Report report = new Analyzer([parser]).Analyze("sample.bin", CreateStream(8));

        Assert.Equal("format.unrecognized", Assert.Single(report.Findings).Code);
    }

    // A container that slices by a corrupt size field reads as truncated, not as a bug.
    [Fact]
    public void SliceByACorruptSizeIsTruncation()
    {
        var parser = new FakeParser("box", _ => 100, context => context.Reader.Slice(context.Reader.Start + 8, 0xFFFFFF00));

        Report report = new Analyzer([parser]).Analyze("sample.bin", CreateStream(64));

        Assert.Equal("parse.truncated", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void CancellationFromAParserIsNotCaught()
    {
        var sniffing = new FakeParser("sniffing", _ => throw new OperationCanceledException());
        var parsing = new FakeParser("parsing", _ => 100, _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => new Analyzer([sniffing]).Analyze("sample.bin", CreateStream(8)));
        Assert.Throws<OperationCanceledException>(() => new Analyzer([parsing]).Analyze("sample.bin", CreateStream(8)));
    }

    [Fact]
    public void AnalysisThatIsAlreadyCanceledDoesNotStart()
    {
        var parser = new FakeParser("any", _ => 100);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new Analyzer([parser]).Analyze("sample.bin", CreateStream(8), new AnalysisOptions { CancellationToken = cancellation.Token }));
        Assert.Equal(0, parser.ParseCalls);
    }

    [Fact]
    public void ParsersSeeTheCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken seenBySniff = default;
        CancellationToken seenByParse = default;
        var parser = new FakeSniffContextParser(context =>
        {
            seenBySniff = context.CancellationToken;
            return 100;
        }, context => seenByParse = context.CancellationToken);

        new Analyzer([parser]).Analyze("sample.bin", CreateStream(8), new AnalysisOptions { CancellationToken = cancellation.Token });

        Assert.Equal(cancellation.Token, seenBySniff);
        Assert.Equal(cancellation.Token, seenByParse);
    }

    [Theory]
    [InlineData("Song.MP3", "mp3")]
    [InlineData("/music/a.b/track.Mp2", "mp2")]
    [InlineData("track", null)]
    [InlineData("track.", null)]
    public void SniffGetsTheExtensionAsAHint(string source, string? hint)
    {
        string? seen = "not called";
        var parser = new FakeSniffContextParser(context =>
        {
            seen = context.ExtensionHint;
            return 0;
        });

        new Analyzer([parser]).Analyze(source, CreateStream(8));

        Assert.Equal(hint, seen);
    }

    [Fact]
    public void StreamThatCannotSeekBecomesErrorFinding()
    {
        var parser = new FakeParser("any", _ => 100);
        var analyzer = new Analyzer([parser]);

        Report report = analyzer.Analyze("pipe", new ForwardOnlyStream(new byte[8]));

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("file.not-seekable", finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(0, parser.ParseCalls);
    }

    [Fact]
    public void StreamThatCannotBeReadBecomesErrorFinding()
    {
        Report report = new Analyzer([]).Analyze("output", new WriteOnlyStream());

        Assert.Equal("file.unreadable", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void DevicesAreNotOpened()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var analyzer = Analyzer.CreateDefault();

        Report report = analyzer.AnalyzeFile("/dev/zero");

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("file.not-regular", finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("This is a device or kernel file that reports a size of 0 but yields data, not a regular file, so it was not read.", finding.Message);
        Assert.Null(analyzer.DetectFormat("/dev/zero"));
    }

    // A device that yields nothing reads like an empty file; the system's file type tells it apart.
    [Fact]
    public void EmptyDevicesAreNotOpened()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/dev/null"))
        {
            return;
        }

        var analyzer = Analyzer.CreateDefault();

        Report report = analyzer.AnalyzeFile("/dev/null");

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("file.not-regular", finding.Code);
        Assert.Equal("This is a device, not a regular file, so it was not read.", finding.Message);
        Assert.Null(analyzer.DetectFormat("/dev/null"));

        using var folder = new TempFolder();
        string empty = Path.Combine(folder.Path, "empty.mp3");
        File.WriteAllBytes(empty, []);
        Assert.Equal("file.empty", Assert.Single(analyzer.AnalyzeFile(empty).Findings).Code);
    }

    [Fact]
    public async Task FifosAreNotOpened()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TempFolder();
        string fifo = folder.MakeFifo("pipe");
        var analyzer = Analyzer.CreateDefault();

        (Report report, string? format) = await Task.Run(() => (analyzer.AnalyzeFile(fifo), analyzer.DetectFormat(fifo))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("This is a named pipe, socket or terminal, not a regular file, so it was not read.", Assert.Single(report.Findings).Message);
        Assert.Null(format);
    }

    // procfs files report a size of 0, so they used to read as empty files.
    [Fact]
    public void KernelPseudoFilesAreNotOpened()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/status"))
        {
            return;
        }

        Report report = Analyzer.CreateDefault().AnalyzeFile("/proc/self/status");

        Finding finding = Assert.Single(report.Findings);
        Assert.Equal("file.not-regular", finding.Code);
        Assert.Equal("This is a device or kernel file that reports a size of 0 but yields data, not a regular file, so it was not read.", finding.Message);
    }

    [Fact]
    public void EmptyRegularFileIsStillEmpty()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(folder["empty.mp3"], []);

        Report report = Analyzer.CreateDefault().AnalyzeFile(folder["empty.mp3"]);

        Assert.Equal("file.empty", Assert.Single(report.Findings).Code);
    }

    private sealed class FakeSniffContextParser(Func<SniffContext, int> sniff, Action<ParseContext>? parse = null) : IFormatParser
    {
        public string Name => "context";

        public SniffResult Sniff(SniffContext context) => new(sniff(context), context.Reader.Start);

        public void Parse(ParseContext context) => parse?.Invoke(context);
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
