using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The host asks the parser it picked for its declared end without looking at what the parser implements (reflection on the interface map is
// not available under NativeAOT), so a parser that implements DeclaredEnd is heard however the set of parsers is made up.
public class DeclaredEndTests
{
    private sealed class Counting : IFormatParser
    {
        public int Calls { get; private set; }

        public string Name => "Counting";

        public SniffResult Sniff(SniffContext context) => new(50, context.Reader.Start);

        public long? DeclaredEnd(SniffContext context)
        {
            Calls++;
            return null;
        }

        public void Parse(ParseContext context) => context.Root.Add("Header", 0, 4);
    }

    [Fact]
    public void TheHostAsksEveryParserForItsDeclaredEnd()
    {
        var parser = new Counting();
        new Analyzer([parser]).Analyze("sample.bin", new MemoryStream(new byte[64]));
        Assert.True(parser.Calls > 0);
    }
}
