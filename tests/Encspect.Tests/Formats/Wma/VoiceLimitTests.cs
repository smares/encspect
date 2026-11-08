using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A Voice stream is described up to 262,144 media objects, 262,144 packets and 524,288 superframes; what lies beyond is not read, and one info
// finding says so (wma.records-limit). The statistics and the stream's figures cover what was read.
public class VoiceLimitTests
{
    private static Report Analyze(string fixture, int count)
    {
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take(fixture, 0x000A);
        byte[] data = AsfBuilder.Build(properties, mediaObject, count, 10);
        return Analyzer.CreateDefault().Analyze("limit.asf", new MemoryStream(data));
    }

    [Fact]
    public void SuperframesBeyondTheLimitAreNotRead()
    {
        // 3 superframes in each media object of the 8 kHz file at 5 kbit/s are read in its packet, so 190,000 of them pass 524,288.
        Report report = Analyze("asf/external-windows-wmavoice-cbr-mono-8k-5.wma", 190_000);
        Finding limit = Assert.Single(report.Findings, f => f.Code == "wma.records-limit");
        Assert.Contains("more than 524288 superframes", limit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");

        StreamInfo stream = Assert.Single(report.Streams);
        Node statistics = stream.Nodes[0].Children.Single(c => c.Name == "Statistics");
        long packets = statistics.Children.Single(c => c.Name == "Packets").Value!.AsInteger()!.Value;
        Node packetNodes = stream.Nodes[0].Children.Single(c => c.Name == "Packets");
        Node superframeNodes = stream.Nodes[0].Children.Single(c => c.Name == "Superframes");
        Assert.Equal(packets, packetNodes.Children.Count);
        Assert.Equal(524_288, superframeNodes.Children.Count);
        long invalid = statistics.Children.SingleOrDefault(c => c.Name == "Invalid Superframes")?.Value!.AsInteger() ?? 0;
        Assert.Equal(524_288, statistics.Children.Single(c => c.Name == "Superframes").Value!.AsInteger()!.Value + invalid);
    }
}
