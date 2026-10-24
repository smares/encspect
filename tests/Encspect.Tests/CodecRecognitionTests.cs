using System.Diagnostics;
using Encspect.Core;
using Encspect.Formats.Ac3;
using Encspect.Formats.Dts;
using Encspect.Host;

namespace Encspect.Tests;

// ICodecParser.Recognizes: whether a payload starts with a stream of the coding, by the evidence of its raw files (a chain of consistent frames,
// not a sync word alone), which a container asks where its own fields do not say what the data holds.
public class CodecRecognitionTests
{
    private static ByteReader Reader(byte[] data) => new(new MemoryStream(data));

    private static byte[] Dts(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", name));

    [Theory]
    [InlineData("core-14le.dts")]
    [InlineData("core-14be.dts")]
    [InlineData("core-16le.dts")]
    [InlineData("core-16be.dts")]
    public void ADtsChainAtTheStartIsRecognizedInEveryForm(string name) => Assert.True(new DtsCodecParser().Recognizes(Reader(Dts(name))));

    [Fact]
    public void SilenceBeforeTheChainDoesNotMatterButAnyOtherByteDoes()
    {
        byte[] chain = Dts("core-14le.dts");
        Assert.True(new DtsCodecParser().Recognizes(Reader([.. new byte[4000], .. chain])));
        Assert.False(new DtsCodecParser().Recognizes(Reader([1, .. chain])));
        Assert.False(new DtsCodecParser().Recognizes(Reader([.. new byte[4000], 0x55, .. chain])));
    }

    [Fact]
    public void ASyncWordWithoutAChainIsNoStream()
    {
        byte[] chain = Dts("core-14le.dts");
        Assert.False(new DtsCodecParser().Recognizes(Reader(chain[..6])));
        Assert.False(new DtsCodecParser().Recognizes(Reader([.. chain[..6], .. new byte[2000]])));
        Assert.False(new DtsCodecParser().Recognizes(Reader([])));
    }

    // Silence is skipped to the first byte that is not zero, up to a bound, so a long run of zeros costs a bounded read.
    [Fact]
    public void AnEndlessRunOfZerosIsNoStreamAndCostsABoundedRead()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(new DtsCodecParser().Recognizes(Reader(new byte[64 << 20])));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The check of 64 MiB of silence took {watch.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public void ACodecParserWithoutSuchEvidenceRecognizesNothing()
    {
        Assert.False(((ICodecParser)new Ac3CodecParser("ac-3")).Recognizes(Reader(File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "two-blocks.eac3")))));
    }

    private sealed class Failing : ICodecParser
    {
        public string Id => "failing";

        public void Parse(CodecContext context) => throw new InvalidOperationException();

        public bool Recognizes(ByteReader payload) => throw new NullReferenceException("a bug");
    }

    // A format parser that asks the routes about its whole data and keeps the answers.
    private sealed class Asker(params string[] codecs) : IFormatParser
    {
        public List<bool> Answers { get; } = [];

        public string Name => "asker";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            var node = new Node("Data", context.Reader.Start, context.Reader.Length);
            context.Root.Add(node);
            Answers.AddRange(codecs.Select(codec => context.Recognizes(codec, node, context.Reader)));
        }
    }

    [Fact]
    public void TheRouteAsksTheCodecParserWithTheIDAndAnswersNoForOthers()
    {
        var asker = new Asker("dts", "unknown", "failing");
        var analyzer = new Analyzer([], [asker], [new DtsCodecParser(), new Failing()]);

        Report report = analyzer.Analyze("test.bin", new MemoryStream(Dts("core-14le.dts")));

        Assert.Equal([true, false, false], asker.Answers);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "parse.failed");
        Assert.Contains("failing codec parser failed recognizing", finding.Message, StringComparison.Ordinal);
        Assert.Equal([false], Answers(new Asker("dts"), Dts("core-14le.dts")[..6]));
    }

    private static List<bool> Answers(Asker asker, byte[] data)
    {
        new Analyzer([], [asker], [new DtsCodecParser()]).Analyze("test.bin", new MemoryStream(data));
        return asker.Answers;
    }
}
