using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// CodecConfiguration.Claims: what a container says about one stream reaches the codec parser through the router, after the file's claims, and
// never goes back to the container's context, so a second stream does not see it.
public class ClaimRouteTests
{
    private sealed class Container(EncoderClaim claim) : IFormatParser
    {
        public List<EncoderClaim> FirstSaw { get; } = [];

        public List<EncoderClaim> SecondSaw { get; } = [];

        public List<EncoderClaim> AfterwardsHeld { get; } = [];

        public string Name => "container";

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context)
        {
            var file = new EncoderClaim("file", context.Root, "the file's tag");
            context.AddEncoderClaim(file);
            var node = new Node("stream", 0, 1);
            context.Root.Add(node);
            context.ParseStream("probe", node, context.Reader, new CodecConfiguration(new MappedBytes(new byte[1], 0), node) { Claims = [claim] });
            context.ParseStream("probe", node, context.Reader);
            AfterwardsHeld.AddRange(context.EncoderClaims);
        }
    }

    private sealed class Probe(Container container) : ICodecParser
    {
        private int _calls;

        public string Id => "probe";

        public void Parse(CodecContext context) => (_calls++ == 0 ? container.FirstSaw : container.SecondSaw).AddRange(context.EncoderClaims);
    }

    [Fact]
    public void AStreamsClaimsReachItsCodecParserOnlyAndAfterTheFilesClaims()
    {
        var claim = new EncoderClaim("layout", new Node("x", 0, 1), "a layout") { IsLayout = true };
        var container = new Container(claim);
        var analyzer = new Analyzer([], [container], [new Probe(container)]);
        analyzer.Analyze("test.bin", new MemoryStream(new byte[4]));

        Assert.Equal(["file", "layout"], container.FirstSaw.Select(c => c.Text));
        Assert.Equal(["file"], container.SecondSaw.Select(c => c.Text));
        Assert.Equal(["file"], container.AfterwardsHeld.Select(c => c.Text));
    }
}
