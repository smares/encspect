using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

internal static class Fixtures
{
    public static string TagDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "tags");

    public static string MpegDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "mpeg");

    // File names (with extension) of the MPEG audio fixtures.
    public static TheoryData<string> MpegFixtures()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(MpegDirectory).Where(p => p.EndsWith(".mp1", StringComparison.Ordinal) || p.EndsWith(".mp2", StringComparison.Ordinal) || p.EndsWith(".mp3", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    public static Report AnalyzeMpeg(string fileName)
    {
        using FileStream stream = File.OpenRead(Path.Combine(MpegDirectory, fileName));
        return Analyzer.CreateDefault().Analyze(fileName, stream);
    }

    public static JsonElement LoadMpegManifest(string fileName)
    {
        string path = Path.Combine(MpegDirectory, Path.GetFileNameWithoutExtension(fileName) + ".expected.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

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

    // "A/B[1]/C": node names separated by "/", with an optional index among same-named
    // siblings. All nodes matching the last segment are returned.
    public static IEnumerable<Node> Resolve(Node start, string path)
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
