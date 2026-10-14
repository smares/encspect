using System.Text.RegularExpressions;

namespace Encspect.Tests;

// The ruleset requires only the all-checks job, so a job missing from its needs list would go
// unchecked. The workflow is read as text: job keys are the two-space-indented keys under "jobs:".
public sealed class CiWorkflowTests
{
    private const string AggregateJob = "all-checks";

    [Fact]
    public void AggregateJobNeedsEveryOtherJob()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        int jobsStart = Array.FindIndex(lines, l => l == "jobs:");
        Assert.True(jobsStart >= 0, "ci.yml has no jobs section");
        string[] jobLines = lines[(jobsStart + 1)..];

        string[] jobs = jobLines
            .Select(l => Regex.Match(l, @"^  ([A-Za-z0-9_-]+):\s*$"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Contains(AggregateJob, jobs);

        int start = Array.FindIndex(jobLines, l => l == "  " + AggregateJob + ":");
        List<string> needs = [];
        bool inNeeds = false;
        foreach (string line in jobLines[(start + 1)..])
        {
            if (Regex.IsMatch(line, @"^  \S"))
            {
                break;
            }

            if (Regex.IsMatch(line, @"^    needs:\s*(#.*)?$"))
            {
                inNeeds = true;
            }
            else if (inNeeds && Regex.Match(line, @"^      - ([A-Za-z0-9_-]+)\s*(#.*)?$") is { Success: true } item)
            {
                needs.Add(item.Groups[1].Value);
            }
            else if (inNeeds && !Regex.IsMatch(line, @"^\s*(#.*)?$"))
            {
                inNeeds = false;
            }
        }

        Assert.Equal(jobs.Where(j => j != AggregateJob).Order(StringComparer.Ordinal), needs.Order(StringComparer.Ordinal));
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
