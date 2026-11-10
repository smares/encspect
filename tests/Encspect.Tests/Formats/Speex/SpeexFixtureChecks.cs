using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// The Speex part of the Ogg fixture manifests (OggFixtureTests).
public partial class OggFixtureTests
{
    private static void CheckSpeex(JsonElement speex, Node stream, string where)
    {
        Node header = stream.Children.Single(c => c.Name == "Speex Header");
        if (speex.GetProperty("header").ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty field in speex.GetProperty("header").EnumerateObject())
            {
                Assert.True(field.Value.GetString() == Field(header, field.Name), $"{where}: header field {field.Name} is \"{Field(header, field.Name)}\", expected \"{field.Value.GetString()}\"");
            }
        }

        if (speex.TryGetProperty("comment", out JsonElement comment))
        {
            Node tag = stream.Children.Single(c => c.Kind == NodeKind.Tag);
            Assert.Equal(comment.GetProperty("vendor").GetString(), Field(tag, "Vendor"));
            Assert.Equal(comment.GetProperty("comments").EnumerateArray().Select(c => c.GetString()), tag.Children.Where(IsComment).Select(CommentText));
        }

        Node? packets = stream.Children.SingleOrDefault(c => c.Name == "Audio Packets");
        JsonElement[] expected = speex.GetProperty("packets").EnumerateArray().ToArray();
        List<Node> actual = packets is null ? [] : [.. packets.Children];
        Assert.Equal(expected.Length, actual.Count);
        JsonElement[] frames = speex.GetProperty("frames").EnumerateArray().ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            JsonElement[] item = expected[i].EnumerateArray().ToArray();
            Node packet = actual[i];
            Assert.Equal(NodeKind.Frame, packet.Kind);
            Assert.True(item[0].GetInt64() == packet.Offset, $"{where}: packet {i} at {packet.Offset}, expected {item[0].GetInt64()}");
            IReadOnlyList<ByteRange> ranges = packet.Ranges ?? [new ByteRange(packet.Offset, item[1].GetInt64())];
            Assert.Equal(item[1].GetInt64(), ranges.Sum(r => r.Length));
            if (item.Length > 2)
            {
                Assert.Equal(item.Skip(2).Select(r => $"{r[0].GetInt64()}+{r[1].GetInt64()}"), ranges.Select(r => $"{r.Offset}+{r.Length}"));
            }
            else
            {
                Assert.Null(packet.Ranges);
            }

            Assert.Equal(
                frames[i].EnumerateArray().Select(f => string.Join(",", f.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? "-" : v.GetInt32().ToString(CultureInfo.InvariantCulture)))),
                packet.Children.Where(c => c.Name == "Frame").Select(FrameModes));
        }

        if (speex.GetProperty("statistics").ValueKind == JsonValueKind.Object)
        {
            Node statistics = stream.Children.Single(c => c.Kind == NodeKind.Statistics);
            foreach (JsonProperty field in speex.GetProperty("statistics").EnumerateObject())
            {
                string? value = Fixtures.Resolve(statistics, field.Name).FirstOrDefault()?.Value?.ToString();
                Assert.True(field.Value.GetString() == value, $"{where}: statistics {field.Name} is \"{value}\", expected \"{field.Value.GetString()}\"");
            }

            Assert.Equal(speex.GetProperty("statistics").EnumerateObject().Count(), Leaves(statistics).Count());
        }
        else
        {
            Assert.DoesNotContain(stream.Children, c => c.Kind == NodeKind.Statistics);
        }

        if (speex.TryGetProperty("encoder", out JsonElement encoder))
        {
            Node node = stream.Children.Single(c => c.Name == "Encoder");
            Assert.Equal(encoder.GetProperty("name").GetString(), node.Value?.ToString());
            Assert.Equal(encoder.GetProperty("confidence").GetString(), Field(node, "Confidence"));
        }

        CheckWriter(speex, stream);
    }


    // "nb,wb,uwb,prefix" as the manifest lists a frame, "-" for a layer the frame does not have.
    private static string FrameModes(Node frame)
    {
        string Submode(string layer) => frame.Children.FirstOrDefault(c => c.Name == layer) is { } node ? Field(node, "Submode") ?? "?" : "-";
        string prefix = frame.Children.FirstOrDefault(c => c.Name == "In-band Data") is { } inBand ? inBand.Value!.AsInteger()!.Value.ToString(CultureInfo.InvariantCulture) : "0";
        return $"{Submode("Narrowband Layer")},{Submode("Wideband Layer")},{Submode("Ultra-Wideband Layer")},{prefix}";
    }
}
