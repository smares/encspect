using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encspect.Tests;

// The ruleset requires only the all-checks job, so a job missing from its needs list would go
// unchecked. The workflow is read as text: job keys are the two-space-indented keys under "jobs:".
public sealed class CiWorkflowTests
{
    private const string AggregateJob = "all-checks";
    private const string DecisionJob = "changes";

    // Run on every event; every other job except the aggregate runs only when the changes job says so.
    private static readonly string[] UnconditionalJobs = [DecisionJob, "docs"];

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

    // A job skipped by the change filter is accepted by the aggregate job only because the changes job
    // published "false" under the job's own ID: each conditional job must test that output, and the changes
    // job must publish it.
    [Fact]
    public void EveryConditionalJobIsGatedByItsOwnChangesOutput()
    {
        Dictionary<string, string[]> jobs = ReadJobs();
        string[] changesLines = jobs[DecisionJob];

        foreach ((string job, string[] lines) in jobs.Where(j => j.Key != AggregateJob && !UnconditionalJobs.Contains(j.Key)))
        {
            Assert.Contains("    needs: " + DecisionJob, lines);
            Assert.Contains($"    if: needs.{DecisionJob}.outputs.{job} == 'true'", lines);
            Assert.Contains($"      {job}: ${{{{ steps.pipeline.outputs.changed == 'true' && 'true' || steps.decide.outputs.{job} }}}}", changesLines);
        }

        foreach (string job in UnconditionalJobs.Where(j => j != DecisionJob))
        {
            Assert.DoesNotContain(jobs[job], l => Regex.IsMatch(l, @"^    if:"));
        }
    }

    // A change to the pipeline is detected in a plain step of the changes job and accepted by no skip, in a step
    // of all-checks that does not depend on the scripts a pull request may change.
    [Fact]
    public void AChangeToThePipelineRunsEverythingAndAcceptsNoSkip()
    {
        Dictionary<string, string[]> jobs = ReadJobs();
        Assert.Contains("      pipeline: ${{ steps.pipeline.outputs.changed }}", jobs[DecisionJob]);
        Assert.Contains(jobs[DecisionJob], l => l.Contains("^(\\.github/|scripts/ci/)", StringComparison.Ordinal));
        string[] aggregate = jobs[AggregateJob];
        Assert.Contains(aggregate, l => l.Contains("PIPELINE: ${{ needs.changes.outputs.pipeline }}", StringComparison.Ordinal));
        Assert.Contains(aggregate, l => l.Contains("any(.[]; .result == \"skipped\")", StringComparison.Ordinal));
    }

    // Runs that are not pull requests must not share a group: GitHub cancels all but the newest pending run of a
    // group, which would drop the result of a commit on main.
    [Fact]
    public void RunsOfMainAreNeverCanceled()
    {
        string text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.Contains("group: ci-${{ github.event_name == 'pull_request' && github.ref || github.run_id }}", text, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", text, StringComparison.Ordinal);
    }

    // A step that may fail without failing the job would be a check nobody reads.
    [Fact]
    public void NoJobOrStepMayFailWithoutFailingTheRun()
    {
        string text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.DoesNotMatch(@"(?m)^\s*continue-on-error:", text);
    }

    // Every fixture the corrupt-file tests damage is in exactly one of their classes, so none is dropped or run twice
    // when a class is added or a prefix changes.
    [Fact]
    public void EveryCorruptFileFixtureIsInExactlyOneClass()
    {
        var rows = new List<string>();
        foreach (Type type in typeof(CiWorkflowTests).Assembly.GetTypes().Where(t => t.IsPublic && t.Name.StartsWith("Corrupt", StringComparison.Ordinal) && t.Name.EndsWith("FileTests", StringComparison.Ordinal)))
        {
            var names = (TheoryData<string>)type.GetProperty("Names")!.GetValue(null)!;
            foreach (string row in names)
            {
                rows.Add(row);
            }
        }

        string[] all = [.. CorruptFiles.All()];
        Assert.NotEmpty(all);
        Assert.Equal(all.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AggregateJobJudgesTheResultsWithTheScript()
    {
        string[] lines = ReadJobs()[AggregateJob];
        Assert.Contains(lines, l => l.Contains("scripts/ci/check_results.py", StringComparison.Ordinal));
        Assert.Contains("    if: always()", lines);
        Assert.Contains("      - " + DecisionJob, lines);
    }

    [Fact]
    public void TheNightlyRunIsScheduledAtAnOddMinuteAndCanBeStartedByHand()
    {
        string text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        Match cron = Regex.Match(text, @"^    - cron: ""(\d+) (\d+) \* \* \*""", RegexOptions.Multiline);
        Assert.True(cron.Success, "ci.yml has no daily schedule");
        Assert.DoesNotContain(int.Parse(cron.Groups[1].Value), new[] { 0, 15, 30, 45 });
        Assert.Contains("  workflow_dispatch:", text.Split('\n').Select(l => l.TrimEnd('\r')));
    }

    // The test classes of the shards named in scripts/ci/shards.json exist, none is listed twice and the
    // last shard takes the rest. That the filters partition the tests is proved by scripts/ci/check_shards.py
    // in the shard-guard job, which lists them.
    [Fact]
    public void ShardsNameExistingTestClassesAndTheLastTakesTheRest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "ci", "shards.json")));
        JsonElement[] shards = [.. document.RootElement.GetProperty("shards").EnumerateArray()];
        Assert.True(shards.Length >= 2, "one shard is no split");
        Assert.Equal(JsonValueKind.Null, shards[^1].GetProperty("classes").ValueKind);

        string[] names = [.. shards.Select(s => s.GetProperty("name").GetString()!)];
        Assert.Equal(names.Length, names.Distinct().Count());

        HashSet<string> testClasses = [.. typeof(CiWorkflowTests).Assembly.GetTypes().Where(t => t.IsPublic).Select(t => t.Name)];
        List<string> listed = [];
        foreach (JsonElement shard in shards[..^1])
        {
            string[] classes = [.. shard.GetProperty("classes").EnumerateArray().Select(c => c.GetString()!)];
            Assert.NotEmpty(classes);
            Assert.All(classes, c => Assert.True(testClasses.Contains(c), $"{c} is not a test class"));
            listed.AddRange(classes);
        }

        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Contains("scripts/ci/check_shards.py", File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml")), StringComparison.Ordinal);
    }

    private static Dictionary<string, string[]> ReadJobs()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        int jobsStart = Array.FindIndex(lines, l => l == "jobs:");
        Assert.True(jobsStart >= 0, "ci.yml has no jobs section");
        Dictionary<string, string[]> jobs = [];
        string? current = null;
        List<string> body = [];
        foreach (string line in lines[(jobsStart + 1)..])
        {
            if (Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*$") is { Success: true } key)
            {
                if (current is not null)
                {
                    jobs[current] = [.. body];
                }

                current = key.Groups[1].Value;
                body = [];
            }
            else if (current is not null)
            {
                body.Add(line);
            }
        }

        if (current is not null)
        {
            jobs[current] = [.. body];
        }

        return jobs;
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
