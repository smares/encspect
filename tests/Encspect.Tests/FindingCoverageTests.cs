using System.Text.RegularExpressions;

namespace Encspect.Tests;

// Every finding code the ASF and Windows Media Audio parsers emit is asserted by a test or listed by a fixture's
// manifest: a code that no input is known to raise is a code nobody knows still works.
public partial class FindingCoverageTests
{
    [GeneratedRegex("\"((?:asf|wma)\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex Code();

    [GeneratedRegex("\\[\"((?:asf|wma)\\.[a-z0-9-]+)\"\\] =")]
    private static partial Regex Table();

    [GeneratedRegex("\"code\": \"((?:asf|wma)\\.[a-z0-9-]+)\"")]
    private static partial Regex ManifestCode();

    [GeneratedRegex(", \"((?:asf|wma)\\.[a-z0-9-]+)\"\\);")]
    private static partial Regex BoundedCode();

    [GeneratedRegex("\"(wavpack\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex WavPackCode();

    [GeneratedRegex("\\[\"(wavpack\\.[a-z0-9-]+)\"\\] =")]
    private static partial Regex WavPackTable();

    [GeneratedRegex("\"code\": \"(wavpack\\.[a-z0-9-]+)\"")]
    private static partial Regex WavPackManifestCode();

    // Every code the WavPack parser emits is raised by a fixture whose manifest lists it, or by a
    // crafted file of WavPackFindingTests, which asserts the finding is present.
    [Fact]
    public void EveryWavPackCodeIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. Directory.EnumerateFiles(Path.Combine(root, "src", "Encspect.Formats.WavPack"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => WavPackCode().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 25, $"Found only {emitted.Count} codes.");

        var asserted = WavPackTable().Matches(File.ReadAllText(Path.Combine(root, "tests", "Encspect.Tests", "WavPackFindingTests.cs"))).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "wavpack"), "*.expected.json"))
        {
            foreach (Match m in WavPackManifestCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        // MemoryTests.Bounded asserts the code it is given is present.
        foreach (string line in File.ReadAllLines(Path.Combine(root, "tests", "Encspect.Tests", "MemoryTests.cs")).Where(l => l.Contains("Bounded(\"", StringComparison.Ordinal) && !l.Contains("expectFinding: false", StringComparison.Ordinal)))
        {
            foreach (Match m in WavPackBoundedCode().Matches(line))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No crafted file or manifest raises: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("\"(matroska\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex MatroskaCode();

    [GeneratedRegex("\"(flac\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex FlacCode();

    // Every code the FLAC parsers and the decoder emit is listed by a fixture's manifest or decode golden, or raised by a crafted file of FlacFindingTests.
    [Fact]
    public void EveryFlacCodeIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. Directory.EnumerateFiles(Path.Combine(root, "src", "Encspect.Formats.Flac"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => FlacCode().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 40, $"Found only {emitted.Count} codes.");

        var asserted = FlacCode().Matches(File.ReadAllText(Path.Combine(root, "tests", "Encspect.Tests", "FlacFindingTests.cs"))).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "flac"), "*.json"))
        {
            foreach (Match m in FlacCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No crafted file, manifest or golden raises: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("\\[\"(matroska\\.[a-z0-9-]+)\"\\] =")]
    private static partial Regex MatroskaTable();

    // Every code the Matroska parser emits is listed by a fixture's manifest or raised by a crafted file of MatroskaFindingTests.
    [Fact]
    public void EveryMatroskaCodeIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. Directory.EnumerateFiles(Path.Combine(root, "src", "Encspect.Formats.Matroska"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => MatroskaCode().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 40, $"Found only {emitted.Count} codes.");

        var asserted = MatroskaTable().Matches(File.ReadAllText(Path.Combine(root, "tests", "Encspect.Tests", "MatroskaFindingTests.cs"))).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "matroska"), "*.expected.json"))
        {
            foreach (Match m in MatroskaCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No crafted file or manifest raises: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("\"(dts\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex DtsCode();

    // Every code the DTS parser emits is listed by a fixture's manifest, whose test compares the findings exactly.
    [Fact]
    public void EveryDtsCodeIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. Directory.EnumerateFiles(Path.Combine(root, "src", "Encspect.Formats.Dts"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => DtsCode().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 30, $"Found only {emitted.Count} codes.");

        var asserted = new HashSet<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "dts"), "*.expected.json"))
        {
            foreach (Match m in DtsCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No fixture's manifest lists: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("\"(dtshd\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex DtsHdCode();

    // Every code the DTS-HD file parser emits is listed by a fixture's manifest, or asserted present by DtsHdTests (the codes a fixture cannot
    // reach: a second header or stream chunk, a first chunk that is not DTSHDHDR, which detection never takes, and no codec parser to route to).
    [Fact]
    public void EveryDtsHdCodeIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. Directory.EnumerateFiles(Path.Combine(root, "src", "Encspect.Formats.DtsHd"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => DtsHdCode().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 10, $"Found only {emitted.Count} codes.");

        var asserted = new HashSet<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "dts"), "*.expected.json"))
        {
            foreach (Match m in DtsHdCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        foreach (Match m in DtsHdCode().Matches(File.ReadAllText(Path.Combine(root, "tests", "Encspect.Tests", "DtsHdTests.cs"))))
        {
            asserted.Add(m.Groups[1].Value);
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No fixture's manifest or DtsHdTests lists: {string.Join(", ", missing)}");
    }

    [GeneratedRegex(", \"(wavpack\\.[a-z0-9-]+)\"\\);")]
    private static partial Regex WavPackBoundedCode();

    [Fact]
    public void EveryCodeTheParsersEmitIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. new[] { "Encspect.Formats.Asf", "Encspect.Formats.Wma" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => Code().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 90, $"Found only {emitted.Count} codes.");

        // A code counts where a test raises it on purpose: a key of the tables of AsfFindingTests and WmaFindingTests (one
        // input each, the finding asserted present), or a finding a fixture's manifest lists (compared exactly), or one
        // of the codes named below, whose own test asserts it. A test that only asserts a code is absent does not count.
        string tests = Path.Combine(root, "tests", "Encspect.Tests");
        string text = string.Concat(new[] { "AsfFindingTests.cs", "WmaFindingTests.cs" }.Select(name => File.ReadAllText(Path.Combine(tests, name))));
        var asserted = Table().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

        // MemoryTests.Bounded asserts the code it is given is present, except where told not to expect it.
        foreach (string line in File.ReadAllLines(Path.Combine(tests, "MemoryTests.cs")).Where(l => l.Contains("Bounded(\"", StringComparison.Ordinal) && !l.Contains("expectFinding: false", StringComparison.Ordinal)))
        {
            foreach (Match m in BoundedCode().Matches(line))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "asf"), "*.expected.json"))
        {
            foreach (Match m in ManifestCode().Matches(File.ReadAllText(path)))
            {
                asserted.Add(m.Groups[1].Value);
            }
        }

        string[] missing = [.. emitted.Where(code => !asserted.Contains(code)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No table of crafted inputs or manifest raises: {string.Join(", ", missing)}");
    }
}
