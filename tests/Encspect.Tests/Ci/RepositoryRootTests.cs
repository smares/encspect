using System.Diagnostics;

namespace Encspect.Tests;

// A folder committed to the root by accident (matroska-specs, in #226) stays unnoticed in review; the root holds
// only what is listed here, so adding an entry is a decision made in this file.
public sealed class RepositoryRootTests
{
    private static readonly string[] AllowedEntries =
    [
        ".claude", ".editorconfig", ".gitattributes", ".github", ".gitignore",
        "CLAUDE.md", "Directory.Build.props", "Directory.Packages.props", "Encspect.slnx",
        "LICENSE", "README.md", "THIRD-PARTY-NOTICES.md",
        "docs", "scripts", "src", "tests", "third_party",
    ];

    [Fact]
    public void TheRootHoldsOnlyTheAllowedTrackedEntries()
    {
        string root = FindRepositoryRoot();
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        string output;
        try
        {
            using Process process = Process.Start(start)!;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return; // not a git checkout (a source archive)
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return; // git is not installed
        }

        string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(entries.Except(AllowedEntries, StringComparer.Ordinal));
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
