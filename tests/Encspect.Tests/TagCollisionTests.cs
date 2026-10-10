using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// User text inside a valid tag that spells another tag's identifier exactly where a probe looks
// for it must not change which tags are found, and must not produce warnings or errors; the
// generator is in scripts/fixtures/make_tag_fixtures.py.
public class TagCollisionTests
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Cases = new(() =>
    {
        string path = Path.Combine(Fixtures.TagDirectory, "..", "collisions", "cases.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.Clone());
    });

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string name in Cases.Value.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ForgedIdentifierDoesNotChangeTheTags(string name)
    {
        JsonElement testCase = Cases.Value[name];
        byte[] data = Convert.FromHexString(testCase.GetProperty("data").GetString()!);

        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

        string expected = string.Join(", ", testCase.GetProperty("regions").EnumerateArray()
            .Select(r => $"{r.GetProperty("name").GetString()}@{r.GetProperty("offset").GetInt64()}+{r.GetProperty("length").GetInt64()}"));
        string actual = string.Join(", ", report.Root.Children.Select(n => $"{n.Name}@{n.Offset}+{n.Length}"));
        Assert.True(expected == actual, $"Regions: {actual}; expected {expected}. Findings: {Describe(report.Findings)}");

        Finding[] serious = report.Findings.Where(f => f.Severity != Severity.Info).ToArray();
        Assert.True(serious.Length == 0, $"Unexpected findings: {Describe(serious)}");
    }

    private static string Describe(IEnumerable<Finding> findings) =>
        string.Join("; ", findings.Select(f => $"[{f.Severity}] {f.Code}: {f.Message}"));
}
