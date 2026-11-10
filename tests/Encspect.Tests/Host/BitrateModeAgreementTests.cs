using System.Text.Json;

namespace Encspect.Tests;

// One rule in every format: the stream's isVbr is "variable" by the same decision as the stream's "Bitrate Mode" statistic (the mode the stream or its
// container states, else the units' own bitrates; BitrateDistribution.IsVariable). Checked over the golden reports of every fixture.
public class BitrateModeAgreementTests
{
    public static TheoryData<string> Goldens
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.report.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(Fixtures.FixtureDirectory, path).Replace('\\', '/'));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void TheStreamsIsVbrFollowsItsBitrateMode(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.FixtureDirectory, name)), new JsonDocumentOptions { MaxDepth = 512 });
        var problems = new List<string>();
        foreach (JsonElement report in document.RootElement.EnumerateArray())
        {
            JsonElement root = report.GetProperty("root");
            if (!report.TryGetProperty("streams", out JsonElement streams))
            {
                continue;
            }

            // Streams that share their nodes (the regions of one MPEG audio file) cannot be told apart by the statistics under them.
            JsonElement[] all = [.. streams.EnumerateArray()];
            int index = 0;
            foreach (JsonElement stream in all)
            {
                index++;
                string key = stream.GetProperty("nodes").GetRawText();
                if (all.Count(s => s.GetProperty("nodes").GetRawText() == key) > 1)
                {
                    continue;
                }

                var modes = new List<string>();
                foreach (JsonElement path in stream.GetProperty("nodes").EnumerateArray())
                {
                    JsonElement node = root;
                    foreach (JsonElement step in path.EnumerateArray())
                    {
                        node = node.GetProperty("children")[step.GetInt32()];
                    }

                    Collect(node, modes);
                }

                if (modes.Count != 1)
                {
                    continue;
                }

                bool isVbr = stream.TryGetProperty("isVbr", out JsonElement value) && value.GetBoolean();
                if (isVbr != (modes[0] == "variable"))
                {
                    problems.Add($"stream {index}: isVbr is {isVbr}, Bitrate Mode is {modes[0]}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static void Collect(JsonElement node, List<string> modes)
    {
        if (node.TryGetProperty("name", out JsonElement name) && name.GetString() == "Bitrate Mode" && node.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            modes.Add(value.GetString()!);
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                Collect(child, modes);
            }
        }
    }
}
