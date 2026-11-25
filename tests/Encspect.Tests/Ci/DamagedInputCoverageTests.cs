using Encspect.Host;

namespace Encspect.Tests;

// What the damaged-input classes (CorruptFiles) cover is a list of named exceptions, not an allow-list: every media file of tests/fixtures is mutated unless its folder is named in
// CorruptFiles.NotMutated, and every file is also decoded unless its folder is named in CorruptFiles.NotDecoded, each with the reason. A folder or a decoder added later is covered by default.
public sealed class CorruptFilesCoverageTests
{
    private static readonly string[] Skipped = [".json", ".md", ".txt", ".py"];

    private static IEnumerable<string> MediaFiles() =>
        Directory.EnumerateFiles(Fixtures.FixtureDirectory, "*", SearchOption.AllDirectories)
            .Where(p => !Skipped.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(Fixtures.FixtureDirectory, p).Replace('\\', '/')).Order(StringComparer.Ordinal);

    [Fact]
    public void EveryMediaFileIsMutatedUnlessItsFolderIsNamedWithAReason()
    {
        HashSet<string> mutated = [.. CorruptFiles.All()];
        string[] left = [.. MediaFiles().Where(n => !mutated.Contains(n) && !CorruptFiles.IsNotMutated(n))];
        Assert.True(left.Length == 0, $"No damaged-input class mutates {left.Length} files, and CorruptFiles.NotMutated does not name them: {string.Join(", ", left.Take(20))}");

        // A named folder still holds files, and none of them is mutated anyway.
        string[] files = [.. MediaFiles()];
        foreach ((string prefix, string reason) in CorruptFiles.NotMutated)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(files, n => n.StartsWith(prefix, StringComparison.Ordinal));
            Assert.DoesNotContain(mutated, n => n.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    // The folders of the fixtures of each registered decoder's codec.
    private static readonly Dictionary<string, string> FamilyOfDecoder = new(StringComparer.Ordinal)
    {
        ["flac"] = "flac/",
        ["wavpack"] = "wavpack/",
        ["monkeys-audio"] = "monkeys-audio/",
        ["tta"] = "tta/",
        ["tak"] = "tak/",
        ["alac"] = "alac/",
        ["truehd"] = "truehd/",
        ["mlp"] = "truehd/",
        ["vorbis"] = "ogg/",
        ["opus"] = "ogg/",
        ["speex"] = "ogg/",
        ["ac-3"] = "ac3/",
        ["e-ac-3"] = "ac3/",
        ["mpeg-audio"] = "mpeg-audio/",
        ["musepack"] = "musepack/",
        ["dts"] = "dts/",
        ["pcm"] = "wav/",
    };

    [Fact]
    public void TheFixturesOfEveryRegisteredDecoderAreDecodedByTheDamagedInputClasses()
    {
        string[] registered = [.. DefaultParsers.Decoders.Select(d => d.Id).Order(StringComparer.Ordinal)];
        Assert.Equal(registered, FamilyOfDecoder.Keys.Order(StringComparer.Ordinal));
        string[] all = [.. CorruptFiles.All()];
        foreach (string id in registered)
        {
            string family = FamilyOfDecoder[id];
            Assert.True(all.Any(n => n.StartsWith(family, StringComparison.Ordinal) && CorruptFiles.IsDecoded(n)), $"The damaged-input classes decode no fixture of {id} ({family}).");
        }

        foreach ((string prefix, string reason) in CorruptFiles.NotDecoded)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(all, n => n.StartsWith(prefix, StringComparison.Ordinal));
            Assert.DoesNotContain(FamilyOfDecoder.Values, family => prefix.StartsWith(family, StringComparison.Ordinal) || family.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}
