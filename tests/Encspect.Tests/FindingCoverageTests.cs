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
