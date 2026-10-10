using System.Text.Json;
using Encspect.Core;

namespace Encspect.Tests;

// Checks every tag fixture against the expectations its generator wrote down: regions in file
// order, versions, confidence, selected field values and the exact set of findings.
public class TagFixtureTests
{
    public static TheoryData<string> Names => Fixtures.TagFixtures();

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesManifest(string name)
    {
        Report report = Fixtures.Analyze(name);
        JsonElement manifest = Fixtures.LoadManifest(name);

        if (manifest.TryGetProperty("length", out JsonElement length))
        {
            Assert.Equal(length.GetInt64(), report.Root.Length);
        }

        JsonElement[] regions = manifest.GetProperty("regions").EnumerateArray().ToArray();
        Node[] actual = report.Root.Children.ToArray();
        Assert.Equal(regions.Select(r => r.GetProperty("name").GetString()), actual.Select(n => n.Name));

        for (int i = 0; i < regions.Length; i++)
        {
            CheckRegion(regions[i], actual[i]);
        }

        CheckFindings(manifest.GetProperty("findings"), report.Findings);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToJson(Fixtures.Analyze(name));
        string path = Fixtures.PathOf(name, ".report.json");

        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    private static void CheckRegion(JsonElement expected, Node node)
    {
        string name = node.Name;
        if (expected.TryGetProperty("offset", out JsonElement offset))
        {
            Assert.True(offset.GetInt64() == node.Offset, $"{name}: offset {node.Offset}, expected {offset.GetInt64()}");
        }

        if (expected.TryGetProperty("length", out JsonElement length))
        {
            Assert.True(length.GetInt64() == node.Length, $"{name} at {node.Offset}: length {node.Length}, expected {length.GetInt64()}");
        }

        if (expected.TryGetProperty("version", out JsonElement version))
        {
            Assert.Equal(version.GetString(), node.Value);
        }

        if (expected.TryGetProperty("confidence", out JsonElement confidence))
        {
            Node detection = Assert.Single(node.Children, c => c.Name == "Detection");
            Assert.True(confidence.GetString() == detection.Value, $"{name} at {node.Offset}: confidence {detection.Value}, expected {confidence.GetString()}");
        }

        if (expected.TryGetProperty("fields", out JsonElement fields))
        {
            foreach (JsonProperty field in fields.EnumerateObject())
            {
                string[] actual = Resolve(node, field.Name).Select(n => n.Value ?? string.Empty).ToArray();
                string[] wanted = field.Value.ValueKind == JsonValueKind.Array
                    ? field.Value.EnumerateArray().Select(v => v.GetString()!).ToArray()
                    : [field.Value.GetString()!];

                if (field.Value.ValueKind != JsonValueKind.Array)
                {
                    actual = actual.Take(1).ToArray();
                }

                Assert.True(wanted.SequenceEqual(actual), $"{name} at {node.Offset}, field {field.Name}: [{string.Join(", ", actual)}], expected [{string.Join(", ", wanted)}]");
            }
        }
    }

    // "A/B[1]/C": node names separated by "/", with an optional index among same-named
    // siblings. All nodes matching the last segment are returned.
    private static IEnumerable<Node> Resolve(Node start, string path)
    {
        string[] segments = path.Split('/');
        Node current = start;
        for (int i = 0; i < segments.Length; i++)
        {
            (string name, int? index) = ParseSegment(segments[i]);
            Node[] matches = current.Children.Where(c => c.Name == name).ToArray();
            if (i == segments.Length - 1 && index is null)
            {
                return matches;
            }

            int pick = index ?? 0;
            if (pick >= matches.Length)
            {
                return [];
            }

            current = matches[pick];
        }

        return [current];
    }

    private static (string Name, int? Index) ParseSegment(string segment)
    {
        int bracket = segment.LastIndexOf('[');
        if (bracket > 0 && segment.EndsWith(']') && int.TryParse(segment[(bracket + 1)..^1], out int index))
        {
            return (segment[..bracket], index);
        }

        return (segment, null);
    }

    private static void CheckFindings(JsonElement expected, IReadOnlyList<Finding> findings)
    {
        // No format parser exists yet, so every file with untagged data gets this finding.
        var remaining = findings.Where(f => f.Code != "format.unrecognized").ToList();
        foreach (JsonElement item in expected.EnumerateArray())
        {
            string code = item.GetProperty("code").GetString()!;
            string contains = item.GetProperty("contains").GetString()!;
            Finding? match = remaining.FirstOrDefault(f => f.Code == code && f.Message.Contains(contains, StringComparison.Ordinal));
            Assert.True(match is not null, $"Missing finding {code} containing \"{contains}\". Findings: {Describe(findings)}");
            remaining.Remove(match);
        }

        Assert.True(remaining.Count == 0, $"Unexpected findings: {Describe(remaining)}");
    }

    private static string Describe(IEnumerable<Finding> findings) =>
        string.Join("; ", findings.Select(f => $"{f.Code}: {f.Message}"));
}
