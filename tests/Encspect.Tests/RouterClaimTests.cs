using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The claims a codec parser makes (the comment of a Vorbis stream) are about its stream: they stay with it, and the claims a container makes about
// one stream (the route's claims) reach that stream's codec parser alone.
public class RouterClaimTests
{
    private static EncoderClaim Claim(string text, Node source) => new(text, source, "test", ClaimRole.Writer);

    [Fact]
    public void AClaimACodecParserMakesDoesNotReachTheNextOne()
    {
        var seen = new Dictionary<string, List<string>>();
        var codecs = new ICodecParser[] { new FakeCodec("first", seen, "made by first"), new FakeCodec("second", seen, "made by second") };
        var container = new FakeContainer(["first", "second"]);

        Report report = new Analyzer(DefaultParsers.Tags, [container], codecs).Analyze("x.fake", new MemoryStream(new byte[16]));

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        Assert.Empty(seen["first"]);
        Assert.Empty(seen["second"]);
        Assert.Empty(container.ClaimsAfter);
    }

    [Fact]
    public void TheClaimsOfARouteReachThatStreamAlone()
    {
        var seen = new Dictionary<string, List<string>>();
        var codecs = new ICodecParser[] { new FakeCodec("first", seen), new FakeCodec("second", seen), new FakeCodec("third", seen) };
        var container = new FakeContainer(["first", "second", "third"]) { RouteClaims = new() { ["first"] = "for first", ["third"] = "for third" } };

        new Analyzer(DefaultParsers.Tags, [container], codecs).Analyze("x.fake", new MemoryStream(new byte[16]));

        Assert.Equal(["for first"], seen["first"]);
        Assert.Empty(seen["second"]);
        Assert.Equal(["for third"], seen["third"]);
        Assert.Empty(container.ClaimsAfter);
    }

    // Files where a codec parser's run produces claims and a later run exists to receive them: whatever one run produced, no later run is handed it.
    // These fail with the router's old pass-back (Ogg's comment claims reached the next logical stream's codec parser, Vorbis' reached AAC).
    [Theory]
    [InlineData("ogg/multiplexed.ogg")]
    [InlineData("ogg/chained.ogg")]
    [InlineData("matroska/external-mkvmerge-vorbis-aac.mka")]
    public void NoCodecParserIsHandedAnotherOnesClaims(string name) => CheckNoLeak(name, expectsClaims: true);

    // Regression guards: no codec in these files makes a claim a later run could receive (MP4 and ASF codec parsers make none; PCM has no codec
    // parser; the Vorbis comment comes after the one that could have received it, or only Vorbis, which filters by its own comment, follows), so
    // they cannot catch the leak, but they hold the containers' routes to the same rule if a codec parser starts making claims.
    [Theory]
    [InlineData("mp4/two-tracks.m4a")]
    [InlineData("asf/two-streams.asf")]
    [InlineData("matroska/external-mkvmerge-vorbis-pcm.mka")]
    [InlineData("matroska/external-mkvmerge-pcm-vorbis.mka")]
    [InlineData("matroska/external-ffmpeg-two-audio.mka")]
    public void NoCodecParserIsHandedAnotherOnesClaimsGuard(string name) => CheckNoLeak(name, expectsClaims: false);

    private static void CheckNoLeak(string name, bool expectsClaims)
    {
        var produced = new List<EncoderClaim>();
        var leaked = new List<string>();
        var codecs = DefaultParsers.Codecs.Select(c => (ICodecParser)new Recording(c, produced, leaked)).ToList();
        var analyzer = new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, codecs);

        Report report = analyzer.AnalyzeFile(Path.Combine(Fixtures.FixtureDirectory, name));
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Empty(leaked);
        if (expectsClaims)
        {
            Assert.NotEmpty(produced);
        }
    }

    private sealed class FakeCodec(string id, Dictionary<string, List<string>> seen, string? makes = null) : ICodecParser
    {
        public string Id => id;

        public void Parse(CodecContext context)
        {
            seen[id] = [.. context.EncoderClaims.Select(c => c.Text)];
            if (makes is not null)
            {
                context.AddEncoderClaim(Claim(makes, context.Parent));
            }

            foreach (MappedBytes _ in context.Packets ?? [])
            {
            }
        }
    }

    private sealed class FakeContainer(string[] codecs) : IFormatParser
    {
        public Dictionary<string, string> RouteClaims { get; init; } = [];

        public List<string> ClaimsAfter { get; } = [];

        public string Name => "Fake";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            Node file = context.Root.Add(new Node("Fake", context.Reader.Start, context.Reader.Length));
            foreach (string codec in codecs)
            {
                Node node = file.Add(new Node(codec, context.Reader.Start, 1));
                IReadOnlyList<EncoderClaim>? claims = RouteClaims.TryGetValue(codec, out string? text) ? [Claim(text, node)] : null;
                context.ParsePackets(codec, node, [], null, claims);
            }

            ClaimsAfter.AddRange(context.EncoderClaims.Select(c => c.Text));
        }
    }

    // Wraps a codec parser: notes the claims its run produced and fails when a later run is handed one of them.
    private sealed class Recording(ICodecParser inner, List<EncoderClaim> produced, List<string> leaked) : ICodecParser
    {
        public string Id => inner.Id;

        public void Parse(CodecContext context)
        {
            EncoderClaim[] before = [.. context.EncoderClaims];
            leaked.AddRange(before.Where(produced.Contains).Select(c => $"{inner.Id} was handed {c.Description} at 0x{c.Source.Offset:X}"));
            inner.Parse(context);
            produced.AddRange(context.EncoderClaims.Except(before));
        }
    }
}
