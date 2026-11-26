using System.Text.RegularExpressions;

namespace Encspect.Tests.Ci;

// A filter by area (FullyQualifiedName~Encspect.Tests.Formats.Flac.) and the IDE's default namespace both rely on the folder
// naming the namespace.
public sealed class TestNamespaceTests
{
    // The negative controls of DecodingBoundaryTests, whose names that test matches.
    private static readonly string[] Exceptions = ["Decoding/BoundarySamples.cs"];

    [Fact]
    public void EveryTestFileHasTheNamespaceOfItsFolder()
    {
        string folder = Path.GetFullPath(Path.Combine(Fixtures.FixtureDirectory, "..", "Encspect.Tests"));
        List<string> wrong = [];
        int checkedFiles = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal) || Exceptions.Contains(relative))
            {
                continue;
            }

            checkedFiles++;
            string[] directories = relative.Split('/')[..^1];
            string expected = string.Join('.', directories.Prepend("Encspect.Tests"));
            string[] declared = [.. Regex.Matches(File.ReadAllText(file), @"^namespace\s+([\w.]+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
            if (declared.Length != 1 || declared[0] != expected)
            {
                wrong.Add($"{relative}: {(declared.Length == 0 ? "no namespace" : string.Join(", ", declared))}, expected {expected}");
            }
        }

        Assert.True(checkedFiles > 100, "the check found too few test files to be reading the folder");
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }
}
