using System.Text.RegularExpressions;

namespace Encspect.Tests;

// Every finding code the ASF and Windows Media Audio parsers emit is asserted by a test or listed by a fixture's
// manifest: a code that no input is known to raise is a code nobody knows still works.
public partial class FindingCoverageTests
{
    [GeneratedRegex("\"((?:asf|wma)\\.[a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex Code();

    [Fact]
    public void EveryCodeTheParsersEmitIsAsserted()
    {
        string root = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", ".."));
        string[] sources = [.. new[] { "Encspect.Formats.Asf", "Encspect.Formats.Wma" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
        var emitted = sources.SelectMany(path => Code().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.True(emitted.Count > 90, $"Found only {emitted.Count} codes.");

        string tests = Path.Combine(root, "tests", "Encspect.Tests");
        string self = Path.GetFileName(typeof(FindingCoverageTests).Name) + ".cs";
        string text = string.Concat(Directory.EnumerateFiles(tests, "*.cs").Where(p => Path.GetFileName(p) != self).Select(File.ReadAllText));
        text += string.Concat(Directory.EnumerateFiles(Path.Combine(root, "tests", "fixtures", "asf"), "*.expected.json").Select(File.ReadAllText));

        string[] missing = [.. emitted.Where(code => !text.Contains($"\"{code}\"", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No test or manifest names: {string.Join(", ", missing)}");
    }
}
