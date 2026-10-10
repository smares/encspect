using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

internal static class Fixtures
{
    public static string TagDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "tags");

    public static IEnumerable<string> TagFixtureNames() =>
        Directory.EnumerateFiles(TagDirectory, "*.mp3")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal);

    public static TheoryData<string> TagFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string name in TagFixtureNames())
        {
            data.Add(name);
        }

        return data;
    }

    public static string PathOf(string name, string extension) => Path.Combine(TagDirectory, name + extension);

    public static Report Analyze(string name)
    {
        using FileStream stream = File.OpenRead(PathOf(name, ".mp3"));
        return Analyzer.CreateDefault().Analyze(name + ".mp3", stream);
    }

    public static string ToJson(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream);
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    public static JsonElement LoadManifest(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PathOf(name, ".expected.json")));
        return document.RootElement.Clone();
    }

    // Golden files are rewritten instead of compared when ENCSPECT_UPDATE_GOLDEN=1, so an
    // intended output change shows up as a reviewable diff.
    public static bool UpdateGolden => Environment.GetEnvironmentVariable("ENCSPECT_UPDATE_GOLDEN") == "1";

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Encspect.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
