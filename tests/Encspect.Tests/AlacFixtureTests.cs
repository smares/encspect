using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Checks every ALAC fixture against its manifest (make_alac_fixtures.py and make_external_alac_fixtures.py, both worked out by alac_model.py, a reading of
// Apple's reference decoder apart from the analyzer, and confirmed by crosscheck_alac_fixtures.py): the magic cookie, every frame and the elements of the
// detailed ones, the statistics, the stream info and exactly the findings listed.
public partial class AlacFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "alac");
            foreach (string manifest in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
            {
                string stem = Path.GetFileName(manifest)[..^".expected.json".Length];
                string file = Directory.EnumerateFiles(directory, stem + ".*").Single(p => !p.EndsWith(".json", StringComparison.Ordinal));
                data.Add("alac/" + Path.GetFileName(file));
            }

            return data;
        }
    }

    private static readonly string[] ElementNames =
        ["Single Channel Element", "Channel Pair Element", "Coupling Channel Element", "LFE Element", "Data Stream Element", "Program Config Element", "Fill Element", "End"];

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex LeadingNumber();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);

        Node? magicCookie = Find(report.Root, n => n.Name == "Magic Cookie");
        CheckCookie(manifest.GetProperty("cookie"), magicCookie);
        Node? framesNode = Find(report.Root, n => n.Name == "Frames" && n.Kind == NodeKind.Stream);
        JsonElement[] frames = [.. manifest.GetProperty("frames").EnumerateArray()];
        if (frames.Length == 0)
        {
            Assert.True(framesNode is null || framesNode.Children.Count == 0);
        }
        else
        {
            Assert.NotNull(framesNode);
            Assert.Equal(frames.Length, framesNode.Children.Count);
            Assert.Equal(frames.Length, framesNode.Value?.AsInteger());
            for (int i = 0; i < Math.Min(frames.Length, 8); i++)
            {
                CheckFrame(frames[i], framesNode.Children[i], i, $"{name}, frame {i}");
            }

            CheckFrame(frames[^1], framesNode.Children[^1], frames.Length - 1, $"{name}, last frame");
        }

        Node? statistics = framesNode?.Parent(report.Root)?.Children.FirstOrDefault(c => c.Name == "Statistics");
        JsonElement tally = manifest.GetProperty("statistics");
        if (manifest.GetProperty("stream_infos") is { ValueKind: JsonValueKind.Array } infos && infos.GetArrayLength() > 0)
        {
            Assert.NotNull(statistics);
            CheckStatistics(tally, statistics);
        }
        else
        {
            Assert.Null(statistics);
        }

        CheckStreamInfos(manifest.GetProperty("stream_infos"), report.Streams);
        if (manifest.GetProperty("stream_infos").ValueKind == JsonValueKind.Array && report.Streams.Count > 0)
        {
            FlacFixtureTests.CheckVerification(manifest.GetProperty("verification").GetProperty("default"), report.Streams.Single().Verification, DecodeStatus.NoDecoder);
        }

        CheckFindings(manifest.GetProperty("findings"), report.Findings);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToGoldenJson(name, Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static Node? Find(Node start, Func<Node, bool> match)
    {
        if (match(start))
        {
            return start;
        }

        // The frames node is a leaf of the search: its children are the frames.
        if (start.Kind == NodeKind.Stream && start.Name == "Frames")
        {
            return null;
        }

        foreach (Node child in start.Children)
        {
            if (Find(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static long Value(Node parent, string name) => parent.Children.Single(c => c.Name == name).Value!.AsInteger()!.Value;

    private static void CheckCookie(JsonElement cookie, Node? node)
    {
        if (cookie.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(node);
            return;
        }

        Assert.NotNull(node);
        Assert.Equal(cookie.GetProperty("length").GetInt32(), node.Length);
        Assert.Equal(cookie.GetProperty("format_atom").ValueKind != JsonValueKind.Null, node.Children.Any(c => c.Name == "Format Atom"));
        Assert.Equal(cookie.GetProperty("config_atom").ValueKind != JsonValueKind.Null, node.Children.Any(c => c.Name == "Config Atom Header"));
        Assert.Equal(cookie.GetProperty("terminator").ValueKind != JsonValueKind.Null, node.Children.Any(c => c.Name == "Terminator Atom"));
        Assert.Equal(cookie.GetProperty("extra").GetInt32() > 0, node.Children.Any(c => c.Name == "Extra Data"));
        if (!cookie.GetProperty("complete").GetBoolean())
        {
            Node shortConfig = node.Children.Single(c => c.Name == "Specific Config");
            Assert.Empty(shortConfig.Children);
            return;
        }

        Node config = node.Children.Single(c => c.Name == "Specific Config");
        Assert.Equal(cookie.GetProperty("config_at").GetInt32() + node.Offset, config.Offset);
        Assert.Equal(24, config.Length);
        Assert.Equal(cookie.GetProperty("frame_length").GetInt64(), Value(config, "Frame Length"));
        Assert.Equal(cookie.GetProperty("version").GetInt64(), Value(config, "Compatible Version"));
        Assert.Equal(cookie.GetProperty("bit_depth").GetInt64(), Value(config, "Bit Depth"));
        Assert.Equal(cookie.GetProperty("pb").GetInt64(), Value(config, "History Weight (pb)"));
        Assert.Equal(cookie.GetProperty("mb").GetInt64(), Value(config, "Initial History (mb)"));
        Assert.Equal(cookie.GetProperty("kb").GetInt64(), Value(config, "Maximum Golomb Parameter (kb)"));
        Assert.Equal(cookie.GetProperty("channels").GetInt64(), Value(config, "Channels"));
        Assert.Equal(cookie.GetProperty("max_run").GetInt64(), Value(config, "Maximum Run"));
        Assert.Equal(cookie.GetProperty("rate").GetInt64(), Value(config, "Sample Rate"));
        long maximum = cookie.GetProperty("max_frame_bytes").GetInt64();
        Assert.Equal(maximum == 0 ? null : maximum, config.Children.Single(c => c.Name == "Maximum Frame Size").Value?.AsInteger());
        long average = cookie.GetProperty("average_bitrate").GetInt64();
        Assert.Equal(average == 0 ? null : average, config.Children.Single(c => c.Name == "Average Bitrate").Value?.AsInteger());
        JsonElement layout = cookie.GetProperty("layout");
        Node? layoutNode = node.Children.FirstOrDefault(c => c.Name == "Channel Layout Info");
        Assert.Equal(layout.ValueKind != JsonValueKind.Null, layoutNode is not null);
        if (layoutNode is not null)
        {
            Assert.Equal(layout.GetProperty("offset").GetInt32() + node.Offset, layoutNode.Offset);
            Assert.Equal(layout.GetProperty("size").GetInt64(), Value(layoutNode, "Size"));
            Assert.Equal(layout.GetProperty("descriptions").GetInt64(), Value(layoutNode, "Channel Description Count"));
            Assert.Contains($"0x{layout.GetProperty("tag").GetInt64():X8}", layoutNode.Children.Single(c => c.Name == "Channel Layout Tag").Value!.ToString(), StringComparison.Ordinal);
        }
    }

    private static void CheckFrame(JsonElement expected, Node frame, int index, string where)
    {
        long offset = expected.GetProperty("offset").GetInt64();
        long size = expected.GetProperty("size").GetInt64();
        long samples = expected.GetProperty("samples").GetInt64();
        Assert.True(offset == frame.Offset && size == frame.Length, $"{where} is at {frame.Offset}+{frame.Length}, expected {offset}+{size}");
        if (size == 0)
        {
            return;
        }

        Assert.Equal(samples > 0 ? samples : size, frame.Value?.AsInteger());
        string status = expected.GetProperty("status").GetString()!;
        IReadOnlyList<Node> children = frame.Children;
        if (status == "not-walked")
        {
            Assert.Equal("Frame Data", Assert.Single(children).Name);
            return;
        }

        JsonElement[] elements = [.. expected.GetProperty("elements").EnumerateArray()];
        if (index >= 4096)
        {
            Assert.Equal("Frame Data", Assert.Single(children).Name);
            return;
        }

        int count = expected.GetProperty("element_count").GetInt32();
        for (int i = 0; i < elements.Length; i++)
        {
            bool isLast = i == count - 1;
            CheckElement(elements[i], children[i], offset, isLast && status is not ("ok" or "end-missing"), $"{where}, element {i}");
        }

        if (count > elements.Length)
        {
            Assert.Equal("Further Elements", children[elements.Length].Name);
            Assert.Equal(count - elements.Length, children[elements.Length].Value?.AsInteger());
        }

        string[] names = [.. children.Skip(Math.Min(count, elements.Length) + (count > elements.Length ? 1 : 0)).Select(c => c.Name)];
        int walked = expected.GetProperty("walked").GetInt32();
        if (status == "ok")
        {
            Assert.Equal(walked < size, names.Contains("Trailing Data"));
            if (names.Contains("Trailing Data"))
            {
                Node trailing = children.Single(c => c.Name == "Trailing Data");
                Assert.Equal(offset + walked, trailing.Offset);
                Assert.Equal(size - walked, trailing.Length);
            }
        }
        else
        {
            Assert.DoesNotContain("Trailing Data", names);
        }
    }

    private static void CheckElement(JsonElement expected, Node node, long frameOffset, bool isCut, string where)
    {
        int tag = expected.GetProperty("tag").GetInt32();
        long start = expected.GetProperty("start").GetInt64();
        long end = expected.GetProperty("end").GetInt64();
        Assert.Equal(ElementNames[tag], node.Name);
        Assert.True(node.Offset == frameOffset + (start / 8), $"{where} starts at {node.Offset}, expected {frameOffset + (start / 8)}");
        Assert.True(node.End == frameOffset + ((end + 7) / 8) || end == start, $"{where} ends at {node.End}, expected {frameOffset + ((end + 7) / 8)}");
        if (start % 8 != 0 || end % 8 != 0)
        {
            Assert.Equal((int)(start % 8), node.BitOffset);
            Assert.Equal((int)(end - start), node.BitLength);
        }

        if (isCut)
        {
            return;
        }

        Node Child(string name) => node.Children.Single(c => c.Name == name);
        if (tag is 0 or 1 or 3 && expected.TryGetProperty("samples", out JsonElement samples))
        {
            Assert.Equal(samples.GetInt64(), node.Value?.AsInteger());
            Assert.Equal(expected.GetProperty("instance").GetInt64(), Value(node, "Instance Tag"));
            Assert.Equal(expected.GetProperty("partial").GetBoolean(), Child("IsPartialFrame").Value?.AsBoolean());
            Assert.Equal(expected.GetProperty("shifted").GetInt64(), Value(node, "Bytes Shifted"));
            Assert.Equal(expected.GetProperty("uncompressed").GetBoolean(), Child("IsUncompressed").Value?.AsBoolean());
            Assert.Equal(expected.GetProperty("partial").GetBoolean(), node.Children.Any(c => c.Name == "Sample Count"));
            Assert.Equal(expected.GetProperty("reserved_zero").GetBoolean(), Child("Reserved Bits").Value?.AsInteger() == 0);
            if (expected.GetProperty("uncompressed").GetBoolean())
            {
                Assert.Contains(node.Children, c => c.Name == "Uncompressed Samples");
                return;
            }

            Assert.Equal(expected.GetProperty("mix_bits").GetInt64(), Value(node, "Mix Bits"));
            Assert.Equal(expected.GetProperty("mix_res").GetInt64(), Value(node, "Mix Resolution"));
            string[] names = ["First Channel", "Second Channel"];
            JsonElement[] channels = [.. expected.GetProperty("channels").EnumerateArray()];
            for (int c = 0; c < channels.Length; c++)
            {
                JsonElement ch = channels[c];
                Node predictor = Child($"{names[c]} Predictor");
                Assert.Equal(ch.GetProperty("type").GetInt64(), Value(predictor, "Prediction Type"));
                Assert.Equal(ch.GetProperty("shift").GetInt64(), Value(predictor, "Predictor Shift"));
                Assert.Equal(ch.GetProperty("weight").GetInt64(), Value(predictor, "History Weight Factor"));
                Assert.Equal(ch.GetProperty("order").GetInt64(), Value(predictor, "Predictor Order"));
                JsonElement coefficients = ch.GetProperty("coefficients");
                Node? list = predictor.Children.FirstOrDefault(p => p.Name == "Predictor Coefficients");
                if (coefficients.GetArrayLength() == 0)
                {
                    Assert.Null(list);
                }
                else
                {
                    Assert.Equal(string.Join(", ", coefficients.EnumerateArray().Select(v => v.GetInt64().ToString(CultureInfo.InvariantCulture))), list!.Value?.AsText());
                }

                Node residuals = Child($"{names[c]} Residuals");
                Assert.Equal(ch.GetProperty("zero_runs").GetInt64(), Value(residuals, "Zero Runs"));
                Assert.Equal(ch.GetProperty("long_codes").GetInt64(), Value(residuals, "Long Codes"));
                Assert.Equal(frameOffset + ((ch.GetProperty("residual_end").GetInt64() + 7) / 8), residuals.End);
            }
        }
        else if (tag is 4 or 6)
        {
            Assert.Equal(expected.GetProperty("bytes").GetInt64(), node.Value?.AsInteger());
        }
    }

    private static void CheckStatistics(JsonElement t, Node statistics)
    {
        Node Stat(string name) => statistics.Children.Single(c => c.Name == name);
        long Number(Node node) => node.Value?.AsInteger() ?? long.Parse(LeadingNumber().Match(node.Value!.ToString()).Value, CultureInfo.InvariantCulture);
        long Entry(Node parent, string name) => parent.Children.Where(c => c.Name == name).Select(Number).SingleOrDefault();
        Assert.Equal(t.GetProperty("frames").GetInt64(), Number(Stat("Frames")));
        Node frames = Stat("Frames");
        Assert.Equal(t.GetProperty("partial_frames").GetInt64(), Entry(frames, "Partial Frames"));
        Assert.Equal(t.GetProperty("uncompressed_frames").GetInt64(), Entry(frames, "Uncompressed Frames"));
        Assert.Equal(t.GetProperty("unreadable_frames").GetInt64(), Entry(frames, "Unreadable Frames"));
        if (!statistics.Children.Any(c => c.Name == "Elements"))
        {
            Assert.Equal(0, t.GetProperty("elements").EnumerateArray().Sum(e => e.GetInt64()));
            return;
        }

        Node elements = Stat("Elements");
        JsonElement[] byTag = [.. t.GetProperty("elements").EnumerateArray()];
        for (int tag = 0; tag < 8; tag++)
        {
            Assert.Equal(byTag[tag].GetInt64(), Entry(elements, ElementNames[tag]));
        }

        long compressed = t.GetProperty("compressed").GetInt64();
        long uncompressed = t.GetProperty("uncompressed").GetInt64();
        Node? coding = statistics.Children.FirstOrDefault(c => c.Name == "Element Coding");
        Assert.Equal(compressed + uncompressed, coding is null ? 0 : Number(coding));
        if (coding is not null)
        {
            Assert.Equal(compressed, Entry(coding, "Compressed"));
            Assert.Equal(uncompressed, Entry(coding, "Uncompressed"));
        }

        long pairs = t.GetProperty("pairs").GetInt64();
        Node? stereo = statistics.Children.FirstOrDefault(c => c.Name == "Stereo Coding");
        Assert.Equal(pairs, stereo is null ? 0 : Number(stereo));
        if (stereo is not null)
        {
            Assert.Equal(pairs - t.GetProperty("mixed_pairs").GetInt64(), Entry(stereo, "Left/right stereo"));
            Assert.Equal(t.GetProperty("mixed_pairs").GetInt64(), Entry(stereo, "Mid/side stereo"));
        }

        long channels = t.GetProperty("channels").GetInt64();
        Node? coded = statistics.Children.FirstOrDefault(c => c.Name == "Compressed Channels");
        Assert.Equal(channels, coded is null ? 0 : Number(coded));
        if (coded is not null)
        {
            CheckDistribution(coded, "Prediction Types", t.GetProperty("types"), "Type ");
            CheckDistribution(coded, "Predictor Orders", t.GetProperty("orders"), "Order ");
            CheckDistribution(coded, "Predictor Shifts", t.GetProperty("shifts"), "Shift ");
            CheckDistribution(coded, "History Weight Factors", t.GetProperty("weights"), "Factor ");
            Assert.Equal(t.GetProperty("zero_runs").GetInt64(), Entry(coded, "Zero Runs"));
            Assert.Equal(t.GetProperty("long_codes").GetInt64(), Entry(coded, "Long Codes"));
        }

        Node? shifted = statistics.Children.FirstOrDefault(c => c.Name == "Bytes Shifted");
        JsonElement[] shifts = [.. t.GetProperty("bytes_shifted").EnumerateArray()];
        Assert.Equal(compressed > 0, shifted is not null);
        for (int i = 0; i < 3 && shifted is not null; i++)
        {
            Assert.Equal(shifts[i].GetInt64(), Entry(shifted, $"{i} {(i == 1 ? "byte" : "bytes")}"));
        }

        Assert.Equal(t.GetProperty("fill_bytes").GetInt64(), statistics.Children.Where(c => c.Name == "Fill Bytes").Select(Number).SingleOrDefault());
        Assert.Equal(t.GetProperty("data_stream_bytes").GetInt64(), statistics.Children.Where(c => c.Name == "Data Stream Bytes").Select(Number).SingleOrDefault());

        void CheckDistribution(Node parent, string name, JsonElement counts, string prefix)
        {
            Node node = parent.Children.Single(c => c.Name == name);
            JsonElement[] values = [.. counts.EnumerateArray()];
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i].GetInt64(), Entry(node, $"{prefix}{i}"));
            }
        }
    }

    private static void CheckStreamInfos(JsonElement expected, IReadOnlyList<StreamInfo> streams)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            // No cookie: the container describes the track itself, and the codec parser adds no stream.
            Assert.All(streams, s => Assert.Empty(s.CategorySeries));
            return;
        }

        if (expected.GetArrayLength() == 0)
        {
            Assert.Empty(streams);
            return;
        }

        JsonElement info = expected.EnumerateArray().Single();
        StreamInfo stream = streams.Single();
        Assert.Equal(info.GetProperty("codec").GetString(), stream.Codec);
        Assert.Equal(info.GetProperty("sample_rate").GetInt32(), stream.SampleRate);
        Assert.Equal(info.GetProperty("channels").GetInt32(), stream.Channels);
        Assert.Equal(info.GetProperty("samples").GetInt64(), stream.Samples);
        Assert.Equal(info.GetProperty("duration").GetDouble(), stream.Duration, 9);
        if (info.GetProperty("bits_per_sample").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.BitsPerSample);
        }
        else
        {
            Assert.Equal(info.GetProperty("bits_per_sample").GetInt32(), stream.BitsPerSample);
        }

        if (info.GetProperty("average_bitrate").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(stream.AverageBitrate);
        }
        else
        {
            Assert.Equal(info.GetProperty("average_bitrate").GetDouble(), stream.AverageBitrate!.Value, 6);
        }

        Assert.Equal(info.GetProperty("is_vbr").GetBoolean(), stream.IsVbr);
        Assert.Equal(info.GetProperty("layout").EnumerateArray().Select(s => s.GetString()), stream.ChannelLayout?.Select(ChannelLayouts.Name) ?? []);
        Assert.All(stream.NodePaths, path => Assert.NotNull(path));
        Assert.Equal(info.GetProperty("units").GetInt32(), stream.Series.FirstOrDefault(s => s.Name == "Bitrate")?.X.Length ?? 0);
        CategorySeries? pairs = stream.CategorySeries.FirstOrDefault(s => s.Name == "Stereo Coding");
        CategorySeries? elements = stream.CategorySeries.FirstOrDefault(s => s.Name == "Element Coding");
        JsonElement[] pairCodes = [.. info.GetProperty("pair_codes").EnumerateArray()];
        JsonElement[] elementCodes = [.. info.GetProperty("element_codes").EnumerateArray()];
        Assert.Equal(info.GetProperty("units").GetInt32() > 0, elements is not null);
        for (int unit = 0; unit < pairCodes.Length; unit++)
        {
            JsonElement[] codes = [.. pairCodes[unit].EnumerateArray()];
            Assert.Equal(codes.Length, pairs?.ValueCount(unit) ?? 0);
            for (int v = 0; v < codes.Length; v++)
            {
                Assert.Equal(codes[v].GetInt32(), pairs![unit, v]);
            }
        }

        for (int unit = 0; unit < elementCodes.Length; unit++)
        {
            JsonElement[] codes = [.. elementCodes[unit].EnumerateArray()];
            Assert.Equal(codes.Length, elements!.ValueCount(unit));
            for (int v = 0; v < codes.Length; v++)
            {
                Assert.Equal(codes[v].GetInt32(), elements[unit, v]);
            }
        }
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        var wanted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            wanted[code] = wanted.GetValueOrDefault(code) + (item.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 1);
        }

        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (Finding finding in findings)
        {
            found[finding.Code] = found.GetValueOrDefault(finding.Code) + finding.Count;
        }

        Assert.True(wanted.SequenceEqual(found), $"Expected findings {string.Join(", ", wanted.Select(w => $"{w.Key} x{w.Value}"))}; found {string.Join("; ", findings.Select(f => $"{f.Code} x{f.Count}: {f.Message}"))}");
        Assert.All(findings, f => Assert.True(f.Path is not null, $"The node of {f.Code} is not in the tree."));
    }
}

internal static class NodeParents
{
    // The parent of a node in the tree under root, by identity.
    public static Node? Parent(this Node? node, Node root)
    {
        if (node is null)
        {
            return null;
        }

        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Node current = queue.Dequeue();
            if (current.Children.Any(c => ReferenceEquals(c, node)))
            {
                return current;
            }

            foreach (Node child in current.Children)
            {
                if (child.Kind != NodeKind.Frame && !(child.Kind == NodeKind.Stream && child.Name == "Frames"))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return null;
    }
}
