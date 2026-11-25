using System.Text.Json;

namespace Encspect.Tests;

// The "tags" table of scripts/ci/areas.json lists, per tag parser's area, the areas whose tests reach it, which a pull request that changes the tag parser selects. The goldens say
// where the host finds each tag, so a folder whose goldens hold the tag has its first area in the tag's list, or a change to the tag parser would pass without running that format's
// goldens and turn main red.
public sealed class AreasTagsTests
{
    // The names of Tag nodes in the goldens, and the tag parser's area that owns each.
    private static readonly Dictionary<string, string> TagAreas = new()
    {
        ["ID3v1"] = "Id3",
        ["ID3v2"] = "Id3",
        ["APE"] = "Ape",
        ["Lyrics3"] = "Lyrics3",
        ["Vorbis Comment"] = "VorbisComment",
    };

    [Fact]
    public void EveryFolderWhoseGoldensHoldATagIsInThatTagsList()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        using JsonDocument areas = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "ci", "areas.json")));
        JsonElement fixtures = areas.RootElement.GetProperty("fixtures");
        JsonElement tags = areas.RootElement.GetProperty("tags");
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*.report.json", SearchOption.AllDirectories))
        {
            string folder = Path.GetRelativePath(Fixtures.FixtureDirectory, path).Split(Path.DirectorySeparatorChar)[0];
            if (folder == "tags" || !fixtures.TryGetProperty(folder, out JsonElement listed))
            {
                continue;
            }

            // Most goldens hold no tag: only one with a Tag node is parsed.
            string text = File.ReadAllText(path);
            if (!text.Contains("\"kind\": \"Tag\"", StringComparison.Ordinal))
            {
                continue;
            }

            string area = listed[0].GetString()!;
            using JsonDocument golden = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1024 });
            foreach (string tag in TagsIn(golden.RootElement).Distinct())
            {
                if (!tags.GetProperty(tag).EnumerateArray().Any(a => a.GetString() == area))
                {
                    missing.Add($"{tag}: {area} ({folder})");
                }
            }
        }

        Assert.True(missing.Count == 0, "areas.json \"tags\" lacks: " + string.Join(", ", missing));
    }

    private static IEnumerable<string> TagsIn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "Tag"
                    && element.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String && TagAreas.TryGetValue(name.GetString()!, out string? area))
                {
                    yield return area;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (string found in TagsIn(property.Value))
                    {
                        yield return found;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string found in TagsIn(item))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }
}
