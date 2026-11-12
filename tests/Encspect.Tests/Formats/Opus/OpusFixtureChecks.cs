using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// The Opus part of the Ogg fixture manifests (OggFixtureTests).
public partial class OggFixtureTests
{
    // Opus: OpusHead's fields, the comment and the data after it, every audio packet with each
    // stream's TOC, the statistics and the encoder.
    private static void CheckOpus(JsonElement opus, Node stream, string where)
    {
        if (opus.GetProperty("header").ValueKind == JsonValueKind.Object)
        {
            CheckFields(opus.GetProperty("header"), stream.Children.Single(c => c.Name == "Opus Head"), where);
        }

        CheckComment(opus, stream);
        Assert.Equal(opus.TryGetProperty("extra", out JsonElement extra) ? extra.GetString() : null, stream.Children.SingleOrDefault(c => c.Name == "Extra Data")?.Value?.ToString());
        List<Node> packets = CheckPackets(opus, stream, where);
        JsonElement[] tocs = opus.GetProperty("tocs").EnumerateArray().ToArray();
        for (int i = 0; i < packets.Count; i++)
        {
            Node packet = packets[i];
            List<Node> parts = packet.Children.Any(c => c.Name.StartsWith("Stream ", StringComparison.Ordinal))
                ? [.. packet.Children]
                : [packet];
            Assert.True(
                tocs[i].EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Null ? "-" : string.Join(",", t.EnumerateArray().Select(v => v.GetInt32()))).SequenceEqual(parts.Select(OpusToc)),
                $"{where}: packet {i} has the TOCs {string.Join("; ", parts.Select(OpusToc))}, expected {tocs[i]}");
        }

        CheckStatistics(opus, stream, where, isOpus: true);
        CheckEncoder(opus, stream);
    }

    // "config,stereo,code,frames" of one stream's part, "-" without a TOC.
    private static string OpusToc(Node part)
    {
        if (part.Children.FirstOrDefault(c => c.Name == "TOC") is not { } toc)
        {
            return "-";
        }

        string config = Field(toc, "Config")!.Split(':')[0];
        string stereo = Field(toc, "IsStereo") == "true" ? "1" : "0";
        string code = Field(toc, "Frame Count Code")!.Split(' ')[0];
        return $"{config},{stereo},{code},{part.Children.Count(c => c.Name == "Frame")}";
    }
}
