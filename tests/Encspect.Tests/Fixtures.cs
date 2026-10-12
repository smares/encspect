using System.Text;
using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

internal static class Fixtures
{
    public static string TagDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "tags");

    public static string FixtureDirectory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");

    // Paths relative to tests/fixtures of the MPEG audio, Layer III, Layer II, VBR header and statistics fixtures.
    public static TheoryData<string> MpegFixtures()
    {
        var data = new TheoryData<string>();
        string[] extensions = [".mp1", ".mp2", ".mp3"];
        foreach (string directory in new[] { "mpeg", "layer3", "layer2", "vbr", "stats" })
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(FixtureDirectory, directory))
                .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            {
                data.Add($"{directory}/{Path.GetFileName(path)}");
            }
        }

        return data;
    }

    public static Report AnalyzeMpeg(string name)
    {
        using FileStream stream = File.OpenRead(Path.Combine(FixtureDirectory, name));
        return Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream);
    }

    public static string MpegCompanion(string name, string suffix) =>
        Path.Combine(FixtureDirectory, Path.GetDirectoryName(name)!, Path.GetFileNameWithoutExtension(name) + suffix);

    public static JsonElement LoadMpegManifest(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(MpegCompanion(name, ".expected.json")));
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

    // The tool version names the build, so golden reports hold a placeholder instead.
    public static string ToJson(Report report)
    {
        using var stream = new MemoryStream();
        ReportJsonWriter.Write([report], stream);
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n")
            .Replace($"\"version\": \"{ReportJsonWriter.ToolVersion}\"", "\"version\": \"(build)\"", StringComparison.Ordinal) + "\n";
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
        int i = 0;
        while (i < segments.Length)
        {
            // A name may hold a slash ("Mid/side stereo"), so a step takes the fewest segments that
            // name a child.
            int end = i;
            (string name, int? index) = ParseSegment(segments[i]);
            Node[] matches = current.Children.Where(c => c.Name == name).ToArray();
            while (matches.Length == 0 && end + 1 < segments.Length)
            {
                end++;
                (name, index) = ParseSegment(string.Join('/', segments[i..(end + 1)]));
                matches = current.Children.Where(c => c.Name == name).ToArray();
            }

            if (end == segments.Length - 1 && index is null)
            {
                return matches;
            }

            int pick = index ?? 0;
            if (pick >= matches.Length)
            {
                return [];
            }

            current = matches[pick];
            i = end + 1;
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
