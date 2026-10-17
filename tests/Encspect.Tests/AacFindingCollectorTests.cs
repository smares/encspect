using Encspect.Core;
using Encspect.Formats.Aac;

namespace Encspect.Tests;

public class AacFindingCollectorTests
{
    // Cases counted by the hundreds of millions (a run of counted frames stands for many) add up without wrapping around.
    [Fact]
    public void CountsSaturateInsteadOfWrapping()
    {
        var collector = new FindingCollector();
        var node = new Node("Frame", 0, 7);
        collector.Add(Severity.Warning, "aac.streams-limited", node, count => $"{count} frames", int.MaxValue - 5);
        collector.Add(Severity.Warning, "aac.streams-limited", node, count => $"{count} frames", 1_000);
        collector.Add(Severity.Warning, "aac.streams-limited", node, count => $"{count} frames");

        var reader = new ByteReader(new MemoryStream([0]));
        var context = new ParseContext(reader, new Node("Root", 0, 1));
        collector.Flush(context);

        Finding finding = Assert.Single(context.Findings);
        Assert.Equal(int.MaxValue, finding.Count);
    }
}
