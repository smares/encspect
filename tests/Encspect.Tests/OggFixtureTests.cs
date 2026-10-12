using System.Globalization;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every Ogg fixture against its manifest (make_ogg_fixtures.py, make_external_ogg_fixtures.py,
// confirmed by crosscheck_ogg_fixtures.py): the regions, every page and gap with its header fields
// and CRC verdict, every logical stream with its codec, link and counts, and for Speex the header
// fields, the comment, every audio packet with its file ranges and the submodes of its frames, the
// statistics and the encoder, for Vorbis and Opus the headers, every packet's block or TOC bytes and
// the statistics (make_vorbis_opus_fixtures.py); the streams the summary reads; and exactly the
// findings listed.
public class OggFixtureTests
{
    public static TheoryData<string> Names => Fixtures.OggFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);

        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);
        Assert.Equal(
            manifest.GetProperty("regions").EnumerateArray().Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"),
            report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));

        if (manifest.TryGetProperty("format", out JsonElement format))
        {
            Assert.Equal(format.GetString(), report.Root.Value?.ToString());
        }
        else
        {
            Node ogg = report.Root.Children.Single(n => n.Name == "Ogg");
            CheckPages(manifest.GetProperty("pages"), ogg.Children.Single(n => n.Name == "Pages"));
            CheckStreams(manifest.GetProperty("streams"), ogg.Children.Where(n => n.Name == "Logical Stream").ToList());
        }

        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        CheckFindings(manifest.GetProperty("findings"), report.Findings);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static void CheckPages(JsonElement expected, Node pages)
    {
        Assert.Equal(Kind(NodeKind.Stream), Kind(pages.Kind));
        List<Node> actual = [.. pages.Children];
        Assert.Equal(
            expected.EnumerateArray().Select(p => $"{p.GetProperty("name").GetString()}@{p.GetProperty("offset").GetInt64()}+{p.GetProperty("length").GetInt64()}"),
            actual.Select(p => $"{p.Name}@{p.Offset}+{p.Length}"));

        foreach ((JsonElement page, Node node) in expected.EnumerateArray().Zip(actual))
        {
            if (node.Name != "Page")
            {
                continue;
            }

            Assert.Equal(NodeKind.Frame, node.Kind);
            Assert.Equal($"0x{page.GetProperty("serial").GetUInt32():X8}", Field(node, "Serial Number"));
            Assert.Equal(page.GetProperty("sequence").GetUInt32().ToString(CultureInfo.InvariantCulture), Field(node, "Sequence Number"));
            Assert.Equal(page.GetProperty("granule").GetInt64().ToString(CultureInfo.InvariantCulture), Field(node, "Granule Position"));
            Assert.Equal($"0x{page.GetProperty("flags").GetInt32():X2}", Field(node, "Header Type"));
            Node crc = node.Children.Single(c => c.Name == "CRC");
            bool truncated = page.TryGetProperty("truncated", out JsonElement cut) && cut.GetBoolean();
            if (truncated)
            {
                Assert.Empty(crc.Children);
            }
            else
            {
                Assert.True(page.GetProperty("crc_ok").GetBoolean() == (crc.Value == crc.Children.Single().Value), $"CRC verdict of the page at {node.Offset}");
            }
        }
    }

    private static void CheckStreams(JsonElement expected, List<Node> streams)
    {
        Assert.Equal(expected.GetArrayLength(), streams.Count);
        foreach ((JsonElement stream, Node node) in expected.EnumerateArray().Zip(streams))
        {
            string where = $"stream {stream.GetProperty("serial").GetString()}";
            Assert.Equal(stream.GetProperty("serial").GetString(), Field(node, "Serial Number"));
            Assert.Equal(stream.GetProperty("codec").GetString(), node.Value?.ToString());
            Assert.Equal(stream.GetProperty("link").GetInt32().ToString(CultureInfo.InvariantCulture), Field(node, "Chain Link"));
            Assert.Equal(Count(stream.GetProperty("pages").GetInt32(), "page", "pages"), Field(node, "Page Count"));
            Assert.Equal(Count(stream.GetProperty("packets").GetInt32(), "packet", "packets"), Field(node, "Packet Count"));
            Assert.Equal(stream.GetProperty("has_eos").GetBoolean() ? "true" : "false", Field(node, "HasEndOfStream"));

            if (stream.TryGetProperty("vorbis", out JsonElement vorbis))
            {
                CheckVorbis(vorbis, node, where);
                continue;
            }

            if (stream.TryGetProperty("opus", out JsonElement opus))
            {
                CheckOpus(opus, node, where);
                continue;
            }

            if (!stream.TryGetProperty("speex", out JsonElement speex))
            {
                Assert.DoesNotContain(node.Children, c => c.Name is "Speex Header" or "Audio Packets");
                continue;
            }

            CheckSpeex(speex, node, where);
        }
    }

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
    }

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

        CheckStatistics(opus, stream, where);
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

    private static void CheckFields(JsonElement expected, Node node, string where)
    {
        foreach (JsonProperty field in expected.EnumerateObject())
        {
            Assert.True(field.Value.GetString() == Field(node, field.Name), $"{where}: {node.Name} field {field.Name} is \"{Field(node, field.Name)}\", expected \"{field.Value.GetString()}\"");
        }
    }

    private static void CheckComment(JsonElement codec, Node stream)
    {
        if (codec.TryGetProperty("comment", out JsonElement comment))
        {
            Node tag = stream.Children.Single(c => c.Kind == NodeKind.Tag);
            Assert.Equal(comment.GetProperty("vendor").GetString(), Field(tag, "Vendor"));
            Assert.Equal(comment.GetProperty("comments").EnumerateArray().Select(c => c.GetString()), tag.Children.Where(IsComment).Select(CommentText));
        }
        else
        {
            Assert.DoesNotContain(stream.Children, c => c.Kind == NodeKind.Tag);
        }
    }

    // Every audio packet's offset, length and file ranges.
    private static List<Node> CheckPackets(JsonElement codec, Node stream, string where)
    {
        Node? packets = stream.Children.SingleOrDefault(c => c.Name == "Audio Packets");
        JsonElement[] expected = codec.GetProperty("packets").EnumerateArray().ToArray();
        List<Node> actual = packets is null ? [] : [.. packets.Children];
        Assert.Equal(expected.Length, actual.Count);
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
        }

        return actual;
    }

    private static void CheckStatistics(JsonElement codec, Node stream, string where)
    {
        if (codec.GetProperty("statistics").ValueKind == JsonValueKind.Object)
        {
            Node statistics = stream.Children.Single(c => c.Kind == NodeKind.Statistics);
            foreach (JsonProperty field in codec.GetProperty("statistics").EnumerateObject())
            {
                string? value = Fixtures.Resolve(statistics, field.Name).FirstOrDefault()?.Value?.ToString();
                Assert.True(field.Value.GetString() == value, $"{where}: statistics {field.Name} is \"{value}\", expected \"{field.Value.GetString()}\"");
            }

            Assert.Equal(codec.GetProperty("statistics").EnumerateObject().Count(), Leaves(statistics).Count());
        }
        else
        {
            Assert.DoesNotContain(stream.Children, c => c.Kind == NodeKind.Statistics);
        }
    }

    private static void CheckEncoder(JsonElement codec, Node stream)
    {
        if (codec.TryGetProperty("encoder", out JsonElement encoder))
        {
            Node node = stream.Children.Single(c => c.Name == "Encoder");
            Assert.Equal(encoder.GetProperty("name").GetString(), node.Value?.ToString());
            Assert.Equal(encoder.GetProperty("confidence").GetString(), Field(node, "Confidence"));
        }
    }

    // Statistics figures, and the entries of each list among them, as the manifest names them.
    private static IEnumerable<Node> Leaves(Node statistics) => statistics.Children.SelectMany(c => c.Children.Count > 0 ? [c, .. c.Children] : new[] { c });

    private static bool IsComment(Node node) => node.Children.Any(c => c.Name is "Field Name" or "Text");

    private static string CommentText(Node node) =>
        node.Children.FirstOrDefault(c => c.Name == "Text") is { } text
            ? text.Value!.ToString()!
            : $"{Field(node, "Field Name")}={Field(node, "Value")}";

    // "nb,wb,uwb,prefix" as the manifest lists a frame, "-" for a layer the frame does not have.
    private static string FrameModes(Node frame)
    {
        string Submode(string layer) => frame.Children.FirstOrDefault(c => c.Name == layer) is { } node ? Field(node, "Submode") ?? "?" : "-";
        string prefix = frame.Children.FirstOrDefault(c => c.Name == "In-band Data") is { } inBand ? inBand.Value!.AsInteger()!.Value.ToString(CultureInfo.InvariantCulture) : "0";
        return $"{Submode("Narrowband Layer")},{Submode("Wideband Layer")},{Submode("Ultra-Wideband Layer")},{prefix}";
    }

    private static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
    {
        Assert.Equal(expected.GetArrayLength(), streams.Count);
        foreach ((JsonElement info, StreamInfo stream) in expected.EnumerateArray().Zip(streams))
        {
            Assert.Equal(info.GetProperty("codec").GetString(), stream.Codec);
            Assert.Equal(info.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
            Assert.Equal(info.GetProperty("channels").GetInt32(), stream.Channels);
            Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
            Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
            if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
            {
                Assert.Null(stream.AverageBitrate);
            }
            else
            {
                Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 9);
            }

            Assert.Equal(info.GetProperty("is_vbr").GetBoolean(), stream.IsVbr);
            if (info.TryGetProperty("channel_mode", out JsonElement mode))
            {
                Assert.Equal(mode.GetString(), stream.ChannelMode);
            }

            Assert.All(stream.NodePaths, path => Assert.NotNull(path));
            if (info.TryGetProperty("encoder", out JsonElement encoder))
            {
                Assert.Equal(encoder.GetString(), stream.Encoder);
                Assert.Equal(info.GetProperty("encoder_confidence").GetString(), stream.EncoderConfidence?.ToString());
            }
        }
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var remaining = findings.ToList();
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {Describe(findings)}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {Describe(remaining)}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }

    private static string Describe(IEnumerable<Finding> findings) => string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"));

    private static string? Field(Node node, string name) => node.Children.FirstOrDefault(c => c.Name == name)?.Value?.ToString();

    private static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";

    private static string Kind(NodeKind kind) => kind.ToString();
}
