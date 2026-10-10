using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every MPEG fixture against its manifest. Hand-built fixtures list every frame, gap and
// finding, and for Layer III the side information and how each frame's data splits into main data,
// bit reservoir and ancillary data; encoder-made ones list the encoder settings, the frames
// ffprobe found, and the side information minimp3 read. Where a manifest records mp3guessenc's
// statistics, the same statistics computed from the report must match.
public class MpegFixtureTests
{
    public static TheoryData<string> Names => Fixtures.MpegFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string fileName)
    {
        Report report = Fixtures.AnalyzeMpeg(fileName);
        JsonElement manifest = Fixtures.LoadMpegManifest(fileName);

        if (manifest.TryGetProperty("encoder", out JsonElement encoder))
        {
            CheckEncoded(manifest, encoder, report);
        }
        else
        {
            CheckHandBuilt(manifest, report);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string fileName)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(fileName));
        string path = Fixtures.MpegCompanion(fileName, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static void CheckHandBuilt(JsonElement manifest, Report report)
    {
        Assert.Equal(manifest.GetProperty("length").GetInt64(), report.Root.Length);

        string expectedRegions = string.Join(", ", manifest.GetProperty("regions").EnumerateArray()
            .Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"));
        string actualRegions = string.Join(", ", report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));
        Assert.Equal(expectedRegions, actualRegions);

        Node[] frames = Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Frame")).ToArray();
        JsonElement[] expectedFrames = manifest.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(
            expectedFrames.Select(f => $"{f.GetProperty("offset").GetInt64()}+{f.GetProperty("length").GetInt64()}"),
            frames.Select(f => $"{f.Offset}+{f.Length}"));

        for (int i = 0; i < frames.Length; i++)
        {
            foreach (JsonProperty field in expectedFrames[i].GetProperty("fields").EnumerateObject())
            {
                string? actual = Fixtures.Resolve(frames[i], field.Name).FirstOrDefault()?.Value;
                Assert.True(field.Value.GetString() == actual, $"Frame {i} at {frames[i].Offset}, field {field.Name}: \"{actual}\", expected \"{field.Value.GetString()}\"");
            }

            if (expectedFrames[i].TryGetProperty("side_info", out JsonElement sideInfo))
            {
                CheckSideInfo(frames[i], sideInfo);
            }

            if (expectedFrames[i].TryGetProperty("scale_info", out JsonElement scaleInfo))
            {
                CheckScaleInfo(frames[i], scaleInfo);
            }

            if (expectedFrames[i].TryGetProperty("data", out JsonElement data))
            {
                IReadOnlyList<Node> children = frames[i].Children;
                int sideInfoIndex = children.ToList().FindIndex(c => c.Name == "Side Info");
                Assert.Equal(
                    data.EnumerateArray().Select(d => $"{d.GetProperty("name").GetString()}@{d.GetProperty("offset").GetInt64()}+{d.GetProperty("length").GetInt64()}: {d.GetProperty("value").GetString()}"),
                    children.Skip(sideInfoIndex + 1).Select(c => $"{c.Name}@{c.Offset}+{c.Length}: {c.Value}"));
            }
        }

        if (manifest.TryGetProperty("mp3guessenc", out JsonElement guessenc))
        {
            CheckGuessencStatistics(guessenc, frames);
        }

        Assert.Equal(
            manifest.GetProperty("gaps").EnumerateArray().Select(g => $"{g.GetProperty("offset").GetInt64()}+{g.GetProperty("length").GetInt64()}"),
            Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Gap")).Select(g => $"{g.Offset}+{g.Length}"));

        var remaining = report.Findings.ToList();
        foreach (JsonElement item in manifest.GetProperty("findings").EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {Describe(report.Findings)}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {Describe(remaining)}");
    }

    private static void CheckEncoded(JsonElement manifest, JsonElement encoder, Report report)
    {
        Node[] frames = Streams(report).SelectMany(s => s.Children.Where(c => c.Name == "Frame")).ToArray();
        bool hasInfoFrame = encoder.GetProperty("info_frame").GetBoolean();

        // ffprobe does not list the Info/Xing frame LAME writes first, so it is skipped here.
        Node[] audioFrames = hasInfoFrame ? frames.Skip(1).ToArray() : frames;
        if (manifest.TryGetProperty("ffprobe_frames", out JsonElement ffprobe))
        {
            Assert.Equal(
                ffprobe.EnumerateArray().Select(f => $"{f[0].GetInt64()}+{f[1].GetInt64()}"),
                audioFrames.Select(f => $"{f.Offset}+{f.Length}"));
        }
        else
        {
            Assert.Equal(manifest.GetProperty("mpg123_frames").GetInt32(), frames.Length);
        }

        string version = encoder.GetProperty("version").GetString() switch
        {
            "1" => "MPEG-1",
            "2" => "MPEG-2",
            _ => "MPEG-2.5",
        };
        string layer = encoder.GetProperty("layer").GetInt32() switch
        {
            1 => "I",
            2 => "II",
            _ => "III",
        };

        var expected = new Dictionary<string, string>
        {
            ["Header/Version"] = version,
            ["Header/Layer"] = layer,
            ["Header/Sample Rate"] = $"{encoder.GetProperty("sample_rate").GetInt32()} Hz",
            ["Header/Channel Mode"] = encoder.GetProperty("channel_mode").GetString()!,
            ["Header/IsProtected"] = encoder.TryGetProperty("protected", out JsonElement p) && p.GetBoolean() ? "true" : "false",
        };
        if (encoder.TryGetProperty("bitrate", out JsonElement bitrate) && bitrate.ValueKind == JsonValueKind.Number)
        {
            expected["Header/Bitrate"] = $"{bitrate.GetInt32()} kbps";
        }

        if (encoder.TryGetProperty("free_format", out JsonElement free) && free.GetBoolean())
        {
            expected["Header/Bitrate"] = "free";
        }

        if (encoder.TryGetProperty("copyright", out JsonElement copyright))
        {
            expected["Header/IsCopyrighted"] = copyright.GetBoolean() ? "true" : "false";
        }

        if (encoder.TryGetProperty("original", out JsonElement original))
        {
            expected["Header/IsOriginal"] = original.GetBoolean() ? "true" : "false";
        }

        if (encoder.TryGetProperty("emphasis", out JsonElement emphasis))
        {
            expected["Header/Emphasis"] = emphasis.GetString()!;
        }

        foreach (Node frame in audioFrames)
        {
            foreach ((string path, string value) in expected)
            {
                string? actual = Fixtures.Resolve(frame, path).FirstOrDefault()?.Value;
                Assert.True(value == actual, $"Frame at {frame.Offset}, field {path}: \"{actual}\", expected \"{value}\"");
            }
        }

        if (manifest.TryGetProperty("minimp3_side_info", out JsonElement minimp3))
        {
            Dictionary<long, Node> byOffset = frames.ToDictionary(f => f.Offset);
            Assert.Equal(minimp3.GetArrayLength(), frames.Length);
            foreach (JsonElement row in minimp3.EnumerateArray())
            {
                Node frame = byOffset[row.GetProperty("offset").GetInt64()];
                if (row.TryGetProperty("layer", out _))
                {
                    CheckScaleInfo(frame, row);
                }
                else
                {
                    CheckSideInfo(frame, row);
                }
            }
        }

        if (manifest.TryGetProperty("mp3guessenc", out JsonElement guessenc))
        {
            CheckGuessencStatistics(guessenc, audioFrames);
        }

        Finding[] serious = report.Findings.Where(f => f.Severity != Severity.Info).ToArray();
        Assert.True(serious.Length == 0, $"Unexpected findings: {Describe(serious)}");
    }

    // Side information as minimp3_side_info.c prints it: compared field by field where minimp3
    // keeps the coded value, and skipped for frames minimp3 rejects.
    private static void CheckSideInfo(Node frame, JsonElement expected)
    {
        int mainDataBegin = expected.GetProperty("main_data_begin").GetInt32();
        if (mainDataBegin < 0)
        {
            return;
        }

        var actual = new Dictionary<string, string> { ["Side Info/Main Data Begin"] = $"{mainDataBegin} bytes" };
        if (expected.TryGetProperty("reservoir", out JsonElement reservoir))
        {
            // minimp3 keeps up to 511 bytes whatever the version; main_data_begin reaches 255 in MPEG-2 and 2.5.
            int reach = Value(frame, "Header/Version") == "MPEG-1" ? 511 : 255;
            actual["Side Info/Reservoir Fill"] = $"{Math.Min(reservoir.GetInt32(), reach)} bytes";
        }

        int channels = Value(frame, "Header/Channel Mode") == "Mono" ? 1 : 2;
        string[] blockTypes = ["Normal", "Start", "Short", "Stop"];
        int i = 0;
        foreach (JsonElement g in expected.GetProperty("granules").EnumerateArray())
        {
            string path = $"Side Info/Granule {i / channels}/Channel {i % channels}/";
            actual[path + "Part 2 and 3 Length"] = $"{g.GetProperty("part2_3_length").GetInt32()} bits";
            actual[path + "Big Values"] = $"{g.GetProperty("big_values").GetInt32()}";
            actual[path + "Global Gain"] = $"{g.GetProperty("global_gain").GetInt32()}";
            actual[path + "Scalefactor Compress"] = $"{g.GetProperty("scalefac_compress").GetInt32()}";
            actual[path + "Block Type"] = blockTypes[g.GetProperty("block_type").GetInt32()];
            actual[path + "HasCoarseScalefactors"] = Flag(g.GetProperty("scalefac_scale"));
            actual[path + "Count1 Table"] = g.GetProperty("count1_table").GetInt32() == 0 ? "A" : "B";
            actual[path + "Table Select"] = string.Join(", ", g.GetProperty("table_select").EnumerateArray().Select(v => v.GetInt32()));
            if (g.TryGetProperty("mixed_block_flag", out JsonElement mixed))
            {
                actual[path + "IsMixedBlock"] = Flag(mixed);
                actual[path + "Subblock Gain"] = string.Join(", ", g.GetProperty("subblock_gain").EnumerateArray().Select(v => v.GetInt32()));
            }
            else
            {
                actual[path + "Region 0 Count"] = $"{g.GetProperty("region0_count").GetInt32()}";
                actual[path + "Region 1 Count"] = $"{g.GetProperty("region1_count").GetInt32()}";
            }

            if (g.TryGetProperty("preflag", out JsonElement preflag))
            {
                actual[path + "HasPreflag"] = Flag(preflag);
            }

            if (g.TryGetProperty("scfsi", out JsonElement scfsi))
            {
                actual[$"Side Info/SCFSI/Channel {i % channels}"] = Convert.ToString(scfsi.GetInt32(), 2).PadLeft(4, '0');
            }

            i++;
        }

        foreach ((string path, string value) in actual)
        {
            Assert.True(Value(frame, path) == value, $"Frame at {frame.Offset}, field {path}: \"{Value(frame, path)}\", expected \"{value}\"");
        }

        static string Flag(JsonElement value) => value.GetInt32() != 0 ? "true" : "false";
    }

    // Layer II allocation, scfsi and scalefactors as minimp3_side_info.c prints them: allocations as
    // minimp3's quantization classes, scalefactors as the single-precision floats minimp3 computes.
    private static void CheckScaleInfo(Node frame, JsonElement expected)
    {
        Node allocation = Fixtures.Resolve(frame, "Bit Allocation").Single();
        Assert.Equal(expected.GetProperty("bands").GetInt32(), allocation.Children.Count);
        int sb = 0;
        foreach (JsonElement subband in expected.GetProperty("subbands").EnumerateArray())
        {
            int ch = 0;
            foreach (JsonElement channel in subband.EnumerateArray())
            {
                int cls = channel.GetProperty("class").GetInt32();
                int levels = cls switch { 0 => 0, 17 => 3, 18 => 5, 19 => 9, _ => (1 << cls) - 1 };
                Node coded = allocation.Children[sb].Children.Count == 1 ? allocation.Children[sb].Children[0] : allocation.Children[sb].Children[ch];
                Assert.True(coded.Value == (levels == 0 ? "None" : $"{levels} levels"), $"Frame at {frame.Offset}, subband {sb}, channel {ch}: {coded.Value}, minimp3 {levels} levels");
                if (levels != 0)
                {
                    int scfsi = channel.GetProperty("scfsi").GetInt32();
                    Assert.Equal($"{scfsi}", Value(frame, $"SCFSI/Subband {sb}/Channel {ch}"));
                    int[] sent = Value(frame, $"Scalefactors/Subband {sb}/Channel {ch}")!.Split(", ").Select(int.Parse).ToArray();
                    int[] expanded = scfsi switch { 0 => sent, 1 => [sent[0], sent[0], sent[1]], 2 => [sent[0], sent[0], sent[0]], _ => [sent[0], sent[1], sent[1]] };
                    double[] minimp3 = channel.GetProperty("scalefactors").EnumerateArray().Select(s => HexFloat(s.GetString()!)).ToArray();
                    Assert.Equal(minimp3, expanded.Select(index => Minimp3Scalefactor(cls, index)));
                }

                ch++;
            }

            sb++;
        }
    }

    // minimp3's g_deq_L12 entry for the class times 2 to the power of 21 - index / 3, in single precision.
    private static double Minimp3Scalefactor(int cls, int index)
    {
        int[] divisors = [3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9];
        float[] bases = [9.53674316e-07f, 7.56931807e-07f, 6.00777173e-07f];
        float step = bases[index % 3] / divisors[cls - 2];
        return step * (float)(1 << 21 >> (index / 3));
    }

    // A C99 hexadecimal float such as 0x1.972b8p-11, as printf %a writes it.
    private static double HexFloat(string text)
    {
        int p = text.IndexOf('p', StringComparison.Ordinal);
        string[] parts = text[2..p].Split('.');
        double mantissa = Convert.ToInt64(parts[0], 16);
        string fraction = parts.Length > 1 ? parts[1] : string.Empty;
        for (int i = 0; i < fraction.Length; i++)
        {
            mantissa += Convert.ToInt32(fraction[i].ToString(), 16) / Math.Pow(16, i + 1);
        }

        return mantissa * Math.Pow(2, int.Parse(text[(p + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    // The statistics mp3guessenc prints about a Layer III stream, computed from the report. A
    // frame's ancillary data is what follows its main data in the reservoir until the next frame's
    // main data starts.
    private static void CheckGuessencStatistics(JsonElement expected, Node[] frames)
    {
        var blocks = new Dictionary<string, int> { ["long"] = 0, ["switch"] = 0, ["short"] = 0, ["mixed"] = 0 };
        var modes = new Dictionary<string, int>();
        var gains = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        string[] extensions = ["None", "Intensity stereo", "MS stereo", "MS and intensity stereo"];
        var ancillary = new List<long>();
        long? previousFree = null;
        bool scalefacScale = false;
        bool scfsi = false;
        int maxMainDataBegin = 0;

        foreach (Node frame in frames)
        {
            if (Value(frame, "Header/Channel Mode") == "Joint stereo")
            {
                string key = $"{Array.IndexOf(extensions, Value(frame, "Header/Mode Extension"))}";
                modes[key] = modes.GetValueOrDefault(key) + 1;
            }

            Node sideInfo = Fixtures.Resolve(frame, "Side Info").Single();
            int mainDataBegin = int.Parse(Value(frame, "Side Info/Main Data Begin")!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            maxMainDataBegin = Math.Max(maxMainDataBegin, mainDataBegin);
            long bits = 0;
            foreach (Node granule in sideInfo.Children.Where(c => c.Name.StartsWith("Granule", StringComparison.Ordinal)))
            {
                foreach (Node channel in granule.Children)
                {
                    string type = Value(channel, "Block Type")!;
                    string kind = type == "Normal" ? "long" : type != "Short" ? "switch" : Value(channel, "IsMixedBlock") == "true" ? "mixed" : "short";
                    blocks[kind]++;
                    string number = channel.Name.Split(' ')[1];
                    if (!gains.TryGetValue(number, out List<int>? list))
                    {
                        gains[number] = list = [];
                    }

                    list.Add(int.Parse(Value(channel, "Global Gain")!, System.Globalization.CultureInfo.InvariantCulture));
                    bits += long.Parse(Value(channel, "Part 2 and 3 Length")!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                    scalefacScale |= Value(channel, "HasCoarseScalefactors") == "true";
                }
            }

            scfsi |= Fixtures.Resolve(frame, "Side Info/SCFSI").SelectMany(s => s.Children).Any(c => c.Value != "0000");
            if (previousFree is { } free)
            {
                ancillary.Add(free - mainDataBegin);
            }

            previousFree = frame.End - sideInfo.End + mainDataBegin - ((bits + 7) / 8);
        }

        if (previousFree is { } last)
        {
            ancillary.Add(last);
        }

        long[] positive = ancillary.Where(a => a > 0).ToArray();
        var actual = new Dictionary<string, string>
        {
            ["blocks"] = string.Join(", ", blocks.Select(b => $"{b.Key} {b.Value}")),
            ["mode_extensions"] = string.Join(", ", modes.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} {m.Value}")),
            ["global_gain"] = string.Join(", ", gains.Select(g => $"{g.Key} {g.Value.Min()}-{g.Value.Max()}")),
            ["max_main_data_begin"] = $"{maxMainDataBegin}",
            ["ancillary"] = positive.Length == 0 ? "0 0 0" : $"{positive.Sum()} {positive.Min()} {positive.Max()}",
            ["scalefac_scale_used"] = $"{scalefacScale}",
            ["scfsi_used"] = $"{scfsi}",
        };
        var wanted = new Dictionary<string, string>
        {
            ["blocks"] = string.Join(", ", expected.GetProperty("blocks").EnumerateObject().Select(b => $"{b.Name} {b.Value.GetInt32()}")),
            ["mode_extensions"] = string.Join(", ", expected.GetProperty("mode_extensions").EnumerateObject()
                .Where(m => m.Name != "None").OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => $"{m.Name} {m.Value.GetInt32()}")),
            ["global_gain"] = string.Join(", ", expected.GetProperty("global_gain").EnumerateObject().OrderBy(g => g.Name, StringComparer.Ordinal)
                .Select(g => $"{g.Name} {g.Value[0].GetInt32()}-{g.Value[1].GetInt32()}")),
            ["max_main_data_begin"] = $"{expected.GetProperty("max_main_data_begin").GetInt32()}",
            ["ancillary"] = string.Join(" ", expected.GetProperty("ancillary").EnumerateArray().Select(a => a.GetInt64())),
            ["scalefac_scale_used"] = $"{expected.GetProperty("scalefac_scale_used").GetBoolean()}",
            ["scfsi_used"] = $"{expected.GetProperty("scfsi_used").GetBoolean()}",
        };
        Assert.Equal(wanted, actual);
    }

    private static string? Value(Node node, string path) => Fixtures.Resolve(node, path).FirstOrDefault()?.Value;

    private static IEnumerable<Node> Streams(Report report) => report.Root.Children.Where(c => c.Name == "MPEG Audio");

    private static string Describe(IEnumerable<Finding> findings) =>
        string.Join("; ", findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}"));
}
