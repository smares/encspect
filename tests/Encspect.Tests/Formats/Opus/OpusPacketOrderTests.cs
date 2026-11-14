using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A container can hand over packets that do not lie in the file in order, so the audio span starts at the lowest packet offset: the Statistics node's ranges
// must reach from its offset to its end (found by the fuzzer, seed 394485949, iteration 101519).
public class OpusPacketOrderTests
{
    [Fact]
    public void Packets_out_of_file_order_leave_no_parse_failure_and_the_audio_span_holds_every_packet()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "opus-packets-out-of-file-order.bin"));

        Report report = Analyzer.CreateDefault().Analyze("fuzz.bin", new MemoryStream(data));

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Node packets = Find(report.Root, "Audio Packets").First();
        foreach (Node packet in packets.Children)
        {
            Assert.True(packet.Offset >= packets.Offset && packet.Offset + packet.Length <= packets.Offset + packets.Length);
        }
    }

    private static IEnumerable<Node> Find(Node node, string name)
    {
        if (node.Name == name)
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node found in Find(child, name))
            {
                yield return found;
            }
        }
    }
}
