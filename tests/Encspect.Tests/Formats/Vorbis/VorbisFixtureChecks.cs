using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// The Vorbis part of the Ogg fixture manifests (OggFixtureTests).
public partial class OggFixtureTests
{
    // Vorbis: the identification header's fields, the comment, the setup header's summary and
    // groups, every audio packet with its block, the statistics and the encoder.
    private static void CheckVorbis(JsonElement vorbis, Node stream, string where)
    {
        CheckFields(vorbis.GetProperty("header"), stream.Children.Single(c => c.Name == "Identification Header"), where);
        CheckComment(vorbis, stream);
        if (vorbis.TryGetProperty("setup", out JsonElement setup))
        {
            Node node = stream.Children.Single(c => c.Name == "Setup Header");
            Assert.Equal(NodeKind.Detail, node.Kind);
            Assert.True(setup.GetProperty("value").GetString() == node.Value?.ToString(), $"{where}: setup header is \"{node.Value}\"");
            CheckFields(setup.GetProperty("groups"), node, where);
        }

        string[] blocks = vorbis.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()!).ToArray();
        List<Node> packets = CheckPackets(vorbis, stream, where);
        Assert.Equal(blocks, packets.Select(VorbisBlock));
        CheckStatistics(vorbis, stream, where);
        CheckEncoder(vorbis, stream);
    }

    private static string VorbisBlock(Node packet)
    {
        string summary = packet.Value!.ToString()!;
        return summary switch
        {
            "empty" => "empty",
            _ when summary.EndsWith("not audio", StringComparison.Ordinal) => "not-audio",
            _ when summary.EndsWith("mode not readable", StringComparison.Ordinal) => "bad-mode",
            _ when summary.Contains(" block of ", StringComparison.Ordinal) =>
                (summary.Contains("long block", StringComparison.Ordinal) ? "L" : "S") + summary[(summary.LastIndexOf("mode ", StringComparison.Ordinal) + 5)..],
            _ => "unread",
        };
    }
}
