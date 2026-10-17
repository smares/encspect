using System.Text.RegularExpressions;

namespace Encspect.Tests;

// release.yml is read as text, like ci.yml in CiWorkflowTests: job keys are the two-space-indented keys under "jobs:".
public sealed class ReleaseWorkflowTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void TheTargetsAreTheFullMatrixOfTheChangeFilter()
    {
        string changes = File.ReadAllText(Path.Combine(Root, "scripts", "ci", "changes.py"));
        string[] full = [.. Regex.Matches(changes, @"\{""os"": ""([^""]+)"", ""rid"": ""([^""]+)""\}").Select(m => m.Groups[1].Value + " " + m.Groups[2].Value)];
        Assert.Equal(6, full.Length);

        string[] lines = [.. Job("build").Select(l => l.Trim())];
        string[] targets = [.. lines.Select((l, i) => (l, i))
            .Where(p => p.l.StartsWith("- os: ", StringComparison.Ordinal))
            .Select(p => p.l["- os: ".Length..] + " " + lines[p.i + 1]["rid: ".Length..])];

        Assert.Equal(full.Order(StringComparer.Ordinal), targets.Order(StringComparer.Ordinal));
        Assert.Contains("    runs-on: ${{ matrix.os }}", Job("build"));
    }

    [Fact]
    public void ARunStartsFromAVersionTagOrAManualRunWithATag()
    {
        string text = Workflow();
        Assert.Contains("    tags: [\"v*.*.*\"]", text, StringComparison.Ordinal);
        Assert.Matches(@"(?s)workflow_dispatch:\s+inputs:\s+tag:.*?required: true", text);
        Assert.Contains("TAG: ${{ inputs.tag || github.ref_name }}", text, StringComparison.Ordinal);
    }

    // The workflow is read-only, and a job that is not the release job writes nothing.
    [Fact]
    public void OnlyTheReleaseJobCanWriteContents()
    {
        string text = Workflow();
        Assert.Contains("\npermissions:\n  contents: read\n", text, StringComparison.Ordinal);

        string[] writes = [.. text.Split('\n').Where(l => Regex.IsMatch(l, @":\s*write\s*$") || l.Contains("write-all", StringComparison.Ordinal))];
        Assert.Equal(["      contents: write"], writes);
        Assert.Contains("      contents: write", Job("release"));
        Assert.Contains("    permissions:", Job("release"));
        Assert.DoesNotContain(Job("build"), l => l.Contains("permissions:", StringComparison.Ordinal));
        Assert.DoesNotContain(Job("check-tag"), l => l.Contains("permissions:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheJobsRunInOrderAndTheReleaseIsAPreReleaseWithTheNotes()
    {
        Assert.Contains("    needs: check-tag", Job("build"));
        Assert.Contains("    needs: [check-tag, build]", Job("release"));

        string release = string.Join('\n', Job("release"));
        Assert.Contains("--verify-tag", release, StringComparison.Ordinal);
        Assert.Contains("--prerelease", release, StringComparison.Ordinal);
        Assert.Contains("--title \"$title\"", release, StringComparison.Ordinal);
        Assert.Contains("title=\"Encspect $VERSION\"", release, StringComparison.Ordinal);
        Assert.Contains("notes=\"docs/releases/$VERSION.md\"", release, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", release, StringComparison.Ordinal);
        Assert.Contains("encspect-\"$VERSION\"-*", release, StringComparison.Ordinal);

        string check = string.Join('\n', Job("check-tag"));
        Assert.Contains("[ \"$TAG\" != \"v$version\" ]", check, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor HEAD refs/remotes/origin/main", check, StringComparison.Ordinal);
    }

    // A pull request that changes the release files runs the builds, so the release-only steps run before a tag; it must
    // neither upload nor publish.
    [Fact]
    public void APullRequestBuildsButNeverUploadsOrPublishes()
    {
        string text = Workflow();
        Assert.Matches(@"(?s)\n  pull_request:\n    paths:\n(      - [^\n]+\n)*      - \.github/workflows/release\.yml\n", text);
        Assert.Contains("      - THIRD-PARTY-NOTICES.md", text, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: ${{ github.event_name == 'pull_request' }}", text, StringComparison.Ordinal);
        Assert.Contains("SOURCE_REF: ${{ github.event_name == 'pull_request' && github.sha || format('refs/tags/{0}', inputs.tag || github.ref_name) }}", text, StringComparison.Ordinal);

        Assert.Contains("    if: github.event_name != 'pull_request'", Job("release"));

        string[] build = Job("build");
        int upload = Array.FindIndex(build, l => l.Contains("uses: actions/upload-artifact@", StringComparison.Ordinal));
        Assert.True(upload >= 0);
        Assert.Equal("        if: github.event_name != 'pull_request'", build[upload + 1]);
        Assert.Single(build, l => l.Contains("upload-artifact", StringComparison.Ordinal));

        // The tag and main checks are skipped on a pull request, the version and notes checks are not.
        string check = string.Join('\n', Job("check-tag"));
        Assert.Contains("if [ \"$GITHUB_EVENT_NAME\" != pull_request ]; then", check, StringComparison.Ordinal);
        Assert.True(check.IndexOf("docs/releases/$version.md", StringComparison.Ordinal) > check.IndexOf("merge-base", StringComparison.Ordinal));
        Assert.Contains("ref: ${{ env.SOURCE_REF }}", check, StringComparison.Ordinal);
    }

    // The jobs build the commit check-tag verified, and a manual run is accepted only from main.
    [Fact]
    public void TheVerifiedCommitIsBuiltAndAManualRunNeedsMain()
    {
        Assert.Contains("    if: github.event_name != 'workflow_dispatch' || github.ref == 'refs/heads/main'", Job("check-tag"));
        Assert.Contains("      sha: ${{ steps.check.outputs.sha }}", Job("check-tag"));
        Assert.Contains("needs.check-tag.outputs.sha", string.Join('\n', Job("build")), StringComparison.Ordinal);
        Assert.Contains("          ref: ${{ needs.check-tag.outputs.sha }}", Job("release"));
        Assert.DoesNotContain("ref: refs/tags/", Workflow(), StringComparison.Ordinal);
        Assert.Contains("--draft=false", string.Join('\n', Job("release")), StringComparison.Ordinal);
    }

    // The builds are made and checked the way CI makes and checks them.
    [Fact]
    public void TheBuildPublishesAndChecksAsCiDoes()
    {
        string ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        string workflow = Workflow();

        foreach (string name in new[]
        {
            "Publish the self-contained CLI",
            "Publish the self-contained GUI",
            "Run the published GUI's self-check",
            "Run the published GUI's self-check on Wayland and X11",
        })
        {
            Assert.Equal(Step(ci, name), Step(workflow, name));
        }

        // The CLI step is CI's plus the check of --version.
        Assert.StartsWith(Step(ci, "Run the published CLI"), Step(workflow, "Run the published CLI"), StringComparison.Ordinal);
        Assert.Contains("--version", Step(workflow, "Run the published CLI"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryActionIsPinnedToAMajorVersion()
    {
        string[] uses = [.. Regex.Matches(Workflow(), @"uses: (\S+)").Select(m => m.Groups[1].Value)];
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.Matches(@"^actions/[a-z-]+(/[a-z]+)?@v\d+$", u));

        string ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        foreach (string action in uses.Where(u => !u.StartsWith("actions/download-artifact", StringComparison.Ordinal)))
        {
            Assert.Contains("uses: " + action, ci, StringComparison.Ordinal);
        }
    }

    // The version is set once; a release needs its notes, the license files its archives carry and the notices the GUI embeds.
    [Fact]
    public void TheVersionHasNotesAndTheArchiveFilesExist()
    {
        string props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Assert.Single(Regex.Matches(props, "<Version>"));
        string version = Regex.Match(props, @"<Version>(\d+\.\d+\.\d+)</Version>").Groups[1].Value;
        Assert.NotEmpty(version);

        Assert.True(File.Exists(Path.Combine(Root, "docs", "releases", version + ".md")), "no release notes for " + version);
        Assert.True(File.Exists(Path.Combine(Root, "docs", "releases", "package-readme.md")));
        Assert.True(File.Exists(Path.Combine(Root, "LICENSE")));
        string notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"));
        foreach (string component in new[] { "Avalonia", "SkiaSharp", "HarfBuzz", "ANGLE", "Avalonia.Native", ".NET runtime" })
        {
            Assert.Contains(component, notices, StringComparison.Ordinal);
        }

        Assert.Contains("LogicalName=\"THIRD-PARTY-NOTICES.md\"", File.ReadAllText(Path.Combine(Root, "src", "Encspect.Gui", "Encspect.Gui.csproj")), StringComparison.Ordinal);
    }

    private static string Workflow() => File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));

    private static string[] Job(string id)
    {
        string[] lines = Workflow().Split('\n');
        int start = Array.IndexOf(lines, "  " + id + ":");
        Assert.True(start >= 0, "release.yml has no job " + id);
        int end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^  \S"));
        return lines[(start + 1)..(end < 0 ? lines.Length : end)];
    }

    // The lines of the step with this name, up to the next step; a step with the name is exactly one.
    private static string Step(string workflow, string name)
    {
        string[] lines = workflow.Split('\n');
        int[] starts = [.. Enumerable.Range(0, lines.Length).Where(i => lines[i] == "      - name: " + name)];
        Assert.True(starts.Length == 1, $"{starts.Length} steps are named '{name}'");
        int end = Array.FindIndex(lines, starts[0] + 1, l => l.StartsWith("      - ", StringComparison.Ordinal) || Regex.IsMatch(l, @"^  \S"));
        // The comment lines above the next step belong to it.
        List<string> body = [.. lines[starts[0]..(end < 0 ? lines.Length : end)]];
        while (body.Count > 0 && (body[^1].Trim().StartsWith('#') || body[^1].Trim().Length == 0))
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join('\n', body);
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
