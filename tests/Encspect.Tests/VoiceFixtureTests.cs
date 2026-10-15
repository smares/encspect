using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Wma;

namespace Encspect.Tests;

// The hand-built Windows Media Audio Voice fixtures (make_voice_fixtures.py, written bit by bit by voice_model.py) hold what the writer put
// in them: the manifest lists, for each superframe, the frame types of its three frames and the samples it states. The analyzer must read
// the same types, count the same superframes and samples, and find the same bits, whatever the spillover cuts. crosscheck_asf_fixtures.py
// has ffmpeg decode the same files to the same sample counts.
public class VoiceFixtureTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string manifest in Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "asf"), "voice-*.expected.json").Order(StringComparer.Ordinal))
            {
                if (JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.TryGetProperty("voice", out _))
                {
                    data.Add($"asf/{Path.GetFileName(manifest)[..^".expected.json".Length]}.asf");
                }
            }

            return data;
        }
    }

    private static Node Child(Node parent, string name) => parent.Children.Single(c => c.Name == name);

    [Theory]
    [MemberData(nameof(Names))]
    public void TheFramesAreThoseTheWriterPut(string name)
    {
        JsonElement voice = Fixtures.LoadMpegManifest(name).GetProperty("voice");
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);
        Node node = stream.Nodes[0];
        JsonElement[] expected = [.. voice.GetProperty("superframes").EnumerateArray()];
        Node[] superframes = [.. Child(node, "Superframes").Children];
        Assert.Equal(expected.Length, superframes.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            long[] types = [.. expected[i].GetProperty("types").EnumerateArray().Select(t => t.GetInt64())];
            Assert.Equal(types, superframes[i].Children.Where(c => c.Name == "Frame").Select(f => Child(f, "Frame Type").Value!.AsInteger()!.Value));
            int samples = expected[i].GetProperty("samples").GetInt32();
            Node? count = superframes[i].Children.FirstOrDefault(c => c.Name == "Sample Count");
            Assert.Equal(samples == 480 ? null : samples, count is null ? (int?)null : (int)count.Value!.AsInteger()!.Value);
            CheckFrames(superframes[i], expected[i].GetProperty("frames"));
            int additional = expected[i].GetProperty("additional").GetInt32();
            Node tail = Child(superframes[i], "Additional Data");
            Assert.Equal(additional == 0 ? "none" : $"{additional * 10} bits", tail.Value!.ToString());
        }

        Assert.Equal(voice.GetProperty("decoded_samples").GetInt64(), stream.Samples);
        long bits = expected.Sum(e => e.GetProperty("bits").GetInt64());
        Node statistics = Child(node, "Statistics");

        // The statistic is rounded to a tenth of a kilobit per second.
        Assert.Equal(bits, Child(statistics, "Average Superframe Bitrate").Value!.AsDouble()!.Value * stream.Duration * 1000, 50 * stream.Duration + 1);
        Assert.Equal(Child(statistics, "Packet Bytes").Value!.AsInteger()!.Value * 8, Math.Round((stream.AverageBitrate ?? 0) * stream.Duration * 1000));
        Assert.DoesNotContain(report.Findings, f => f.Severity != Severity.Info);

        // The statistics count the same frames by type.
        Node counts = Child(Child(node, "Statistics"), "Frame Types");
        long[] written = new long[17];
        foreach (JsonElement e in expected)
        {
            foreach (JsonElement t in e.GetProperty("types").EnumerateArray())
            {
                written[t.GetInt32()]++;
            }
        }

        Assert.Equal(written.Where(c => c > 0).Select(c => c), counts.Children.Select(c => long.Parse(c.Value!.ToString()!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)));
    }

    // Each frame's bits, pitch, silence gain and window index, and its blocks, as the writer put them in. A superframe across two packets has
    // its frames' bits in ranges, where only the fields' values are held.
    private static void CheckFrames(Node superframe, JsonElement written)
    {
        Node[] frames = [.. superframe.Children.Where(c => c.Name == "Frame")];
        JsonElement[] expected = [.. written.EnumerateArray()];
        Assert.Equal(expected.Length, frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            byte[] descriptor = VoiceTables.FrameDescriptors[expected[i].GetProperty("type").GetInt32()];
            if (superframe.Ranges is null)
            {
                Assert.Equal(expected[i].GetProperty("bits").GetInt32(), frames[i].BitLength is { } width ? width : (int)(frames[i].Length * 8));
            }

            Assert.Equal(descriptor[0], Child(frames[i], "Blocks").Children.Count);
            if (descriptor[2] == 1)
            {
                Assert.Equal(expected[i].GetProperty("pitch").GetInt64(), Child(frames[i], "Pitch").Value!.AsInteger());
            }
            else
            {
                Assert.DoesNotContain(frames[i].Children, c => c.Name == "Pitch");
            }

            if (descriptor[3] == 0)
            {
                Assert.Equal(expected[i].GetProperty("parameter").GetInt64(), Child(frames[i], "Silence Gain").Value!.AsInteger());
            }
            else if (descriptor[3] == 2)
            {
                Assert.Equal(expected[i].GetProperty("window_index").GetInt64(), Child(frames[i], "Window Index").Value!.AsInteger());
            }
        }
    }

    // The fixtures cover every frame type, the silence and hardcoded types among them.
    [Fact]
    public void EveryFrameTypeIsWritten()
    {
        var seen = new HashSet<long>();
        foreach (string name in new[] { "asf/voice-8k-residual.asf", "asf/voice-16k-independent.asf", "asf/voice-11k-two-blocks.asf" })
        {
            JsonElement voice = Fixtures.LoadMpegManifest(name).GetProperty("voice");
            foreach (JsonElement superframe in voice.GetProperty("superframes").EnumerateArray())
            {
                foreach (JsonElement type in superframe.GetProperty("types").EnumerateArray())
                {
                    seen.Add(type.GetInt64());
                }
            }
        }

        Assert.Equal(Enumerable.Range(0, 17).Select(t => (long)t), seen.Order());
    }
}
