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
    public void ARunStartsFromAVersionTagOrAManualRunWithAnOptionalTag()
    {
        string text = Workflow();
        Assert.Contains("    tags: [\"v*.*.*\"]", text, StringComparison.Ordinal);
        Assert.Matches(@"(?s)workflow_dispatch:\s+inputs:\s+tag:.*?required: false", text);
        Assert.Contains("leave empty to release the version on main", text, StringComparison.Ordinal);
        Assert.Contains("group: release-${{ github.event_name == 'pull_request' && github.ref || 'publish' }}", text, StringComparison.Ordinal);
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
        Assert.Contains("[ \"$tag\" != \"v$version\" ]", check, StringComparison.Ordinal);
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
        Assert.Contains("SOURCE_REF: ${{ github.event_name == 'push' && format('refs/tags/{0}', github.ref_name) || github.sha }}", text, StringComparison.Ordinal);

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
        string[] check = Job("check-tag");
        Assert.DoesNotContain(check, l => l.StartsWith("    if:", StringComparison.Ordinal));
        int guard = Array.IndexOf(check, "      - name: Require a manual run to start from main");
        Assert.True(guard >= 0 && guard < Array.FindIndex(check, l => l.Contains("actions/checkout@", StringComparison.Ordinal)), "the guard is the first step");
        Assert.Equal("        if: github.event_name == 'workflow_dispatch' && github.ref != 'refs/heads/main'", check[guard + 1]);
        Assert.Contains(check, l => l.Contains("must be started from main", StringComparison.Ordinal));
        Assert.Contains(check, l => l.Trim() == "exit 1");
        Assert.Contains("      sha: ${{ steps.check.outputs.sha }}", Job("check-tag"));
        Assert.Contains("needs.check-tag.outputs.sha", string.Join('\n', Job("build")), StringComparison.Ordinal);
        Assert.Contains("          ref: ${{ needs.check-tag.outputs.sha }}", Job("release"));
        Assert.DoesNotContain("ref: refs/tags/", Workflow(), StringComparison.Ordinal);
        Assert.Contains("--draft=false", string.Join('\n', Job("release")), StringComparison.Ordinal);
    }

    // A manual run may create the tag, but only the release job does, at the commit the checks verified and after the builds.
    [Fact]
    public void OnlyTheReleaseJobCreatesTheTagAndAtTheVerifiedCommit()
    {
        string text = Workflow();
        string release = string.Join('\n', Job("release"));

        Assert.Equal(2, Regex.Matches(text, @"gh release create ").Count);
        Assert.DoesNotContain("git tag", text, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gh api", text, StringComparison.Ordinal);
        Assert.Contains("    needs: [check-tag, build]", Job("release"));
        Assert.Contains("      SHA: ${{ needs.check-tag.outputs.sha }}", Job("release"));
        Assert.Contains("      CREATE_TAG: ${{ needs.check-tag.outputs.create_tag }}", Job("release"));

        string[] creates = [.. release.Split('\n').Where(l => l.Contains("gh release create ", StringComparison.Ordinal))];
        Assert.Equal(2, creates.Length);
        string withTarget = Assert.Single(creates, l => l.Contains("--target", StringComparison.Ordinal));
        Assert.Contains("--target \"$SHA\"", withTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("--verify-tag", withTarget, StringComparison.Ordinal);
        string withVerify = Assert.Single(creates, l => l.Contains("--verify-tag", StringComparison.Ordinal));
        Assert.DoesNotContain("--target", withVerify, StringComparison.Ordinal);

        // The call that creates the tag is guarded by create_tag and by a second look at the remote.
        int branch = release.IndexOf("if [ \"$CREATE_TAG\" = true ]; then", StringComparison.Ordinal);
        int recheck = release.IndexOf("git ls-remote --exit-code --tags origin \"refs/tags/$TAG\"", branch, StringComparison.Ordinal);
        int create = release.IndexOf("--target", StringComparison.Ordinal);
        int verify = release.IndexOf("--verify-tag", StringComparison.Ordinal);
        Assert.True(branch >= 0 && recheck > branch && create > recheck && verify > create, "order: create_tag branch, remote re-check, create with --target, then the --verify-tag path");
        Assert.Single(Regex.Matches(release, "--target"));
        Assert.Contains("create_tag=true", string.Join('\n', Job("check-tag")), StringComparison.Ordinal);

        // The other jobs build the verified commit whether or not the tag exists yet.
        Assert.Contains("needs.check-tag.outputs.sha", string.Join('\n', Job("build")), StringComparison.Ordinal);
        Assert.Contains("          ref: ${{ needs.check-tag.outputs.sha }}", Job("release"));
    }

    // check-tag only reads: the manual run with a missing tag is checked against the commit of the run.
    [Fact]
    public void CheckTagStaysReadOnlyAndChecksAMissingTagAtTheRunsCommit()
    {
        string[] job = Job("check-tag");
        string check = string.Join('\n', job);
        Assert.DoesNotContain(job, l => l.Contains("permissions:", StringComparison.Ordinal));
        Assert.DoesNotContain("gh release", check, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", check, StringComparison.Ordinal);
        Assert.DoesNotContain("git tag", check, StringComparison.Ordinal);
        Assert.DoesNotContain("--target", check, StringComparison.Ordinal);

        Assert.Contains("      create_tag: ${{ steps.check.outputs.create_tag }}", job);
        Assert.Contains("echo \"create_tag=$create_tag\"", check, StringComparison.Ordinal);
        Assert.Contains("create_tag=false", check, StringComparison.Ordinal);
        Assert.Contains("git ls-remote --exit-code --tags origin \"refs/tags/$tag\"", check, StringComparison.Ordinal);
        // The version and notes checks come after the tag was resolved, so a missing tag meets the same ones.
        Assert.True(check.IndexOf("create_tag=true", StringComparison.Ordinal) < check.IndexOf("[ \"$tag\" != \"v$version\" ]", StringComparison.Ordinal));
        Assert.True(check.IndexOf("[ \"$tag\" != \"v$version\" ]", StringComparison.Ordinal) < check.IndexOf("docs/releases/$version.md", StringComparison.Ordinal));
        Assert.Contains("echo \"sha=$(git rev-parse HEAD)\"", check, StringComparison.Ordinal);
    }

    // A tag push (the tag exists by definition) is still released only against the existing tag.
    [Fact]
    public void ATagPushStillUsesVerifyTag()
    {
        string release = string.Join('\n', Job("release"));
        Assert.Contains("gh release create \"$TAG\" dist/* --verify-tag --title", release, StringComparison.Ordinal);
        Assert.Contains("elif gh release view \"$TAG\"", release, StringComparison.Ordinal);
        Assert.Contains("format('refs/tags/{0}', github.ref_name)", Workflow(), StringComparison.Ordinal);
    }

    // The tag is decided once, in check-tag; no other job reads the input or the ref name.
    [Fact]
    public void TheTagIsDerivedInCheckTagAndReadFromItsOutputEverywhereElse()
    {
        string text = Workflow();
        string check = string.Join('\n', Job("check-tag"));
        Assert.Contains("      tag: ${{ steps.check.outputs.tag }}", Job("check-tag"));
        Assert.Contains("tag=\"v$(read_version)\"", check, StringComparison.Ordinal);
        Assert.Contains("if [ -z \"$tag\" ]; then", check, StringComparison.Ordinal);
        Assert.Contains("[ \"$tag\" != \"v$version\" ]", check, StringComparison.Ordinal);
        Assert.Contains("echo \"tag=$tag\"", check, StringComparison.Ordinal);

        Assert.Contains("      TAG: ${{ needs.check-tag.outputs.tag }}", Job("release"));
        Assert.DoesNotContain(Job("build"), l => l.Contains("inputs.", StringComparison.Ordinal) || l.Contains("ref_name", StringComparison.Ordinal) || l.Contains("$TAG", StringComparison.Ordinal));
        Assert.DoesNotContain(Job("release"), l => l.Contains("inputs.", StringComparison.Ordinal) || l.Contains("ref_name", StringComparison.Ordinal));
        Assert.DoesNotContain("\n  TAG:", text, StringComparison.Ordinal);

        // inputs.tag and github.ref_name appear only where the tag is read before check-tag decides: the concurrency group,
        // the checkout of a tag push and check-tag's own step.
        string[] readers = [.. text.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && (l.Contains("inputs.tag", StringComparison.Ordinal) || l.Contains("ref_name", StringComparison.Ordinal)))];
        Assert.Equal(3, readers.Length);
        Assert.Contains(readers, l => l.Contains("SOURCE_REF:", StringComparison.Ordinal));
        Assert.Contains(readers, l => l.Contains("INPUT_TAG: ${{ inputs.tag }}", StringComparison.Ordinal));
        Assert.Contains(readers, l => l.Contains("PUSHED_TAG:", StringComparison.Ordinal));
    }

    // A stale notice must not hide the packaging steps: it is checked after the archive run, before the upload.
    [Fact]
    public void TheNoticesAreCheckedAfterPackagingAndBeforeTheUpload()
    {
        string[] build = Job("build");
        int archive = Array.IndexOf(build, "      - name: Run the CLI and the GUI from the archive");
        int notices = Array.IndexOf(build, "      - name: Check that THIRD-PARTY-NOTICES.md matches this build");
        int upload = Array.FindIndex(build, l => l.Contains("uses: actions/upload-artifact@", StringComparison.Ordinal));
        Assert.True(archive >= 0 && notices > archive && upload > notices, "order: archive run, notices check, upload");
        string step = string.Join('\n', build[notices..upload]);
        Assert.Contains("make_notices.py --self-test", step, StringComparison.Ordinal);
        Assert.Contains("make_notices.py --check --runtime-deps", step, StringComparison.Ordinal);
        Assert.Contains("PYTHONUTF8: 1", step, StringComparison.Ordinal);
    }

    // The builds are made and checked the way CI makes and checks them.
    [Fact]
    public void TheBuildPublishesAndChecksAsCiDoes()
    {
        string ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        string workflow = Workflow();

        foreach (string name in new[]
        {
            "Install the NativeAOT toolchain",
            "Fetch the glibc 2.27 sysroot",
            "Publish the NativeAOT CLI",
            "Check the glibc the CLI needs",
            "Compare the NativeAOT CLI with the JIT build",
            "Publish the NativeAOT CLI for x86-64-v3",
            "Run the published x86-64-v3 CLI",
            "Check the glibc the x86-64-v3 CLI needs",
            "Compare the x86-64-v3 CLI with the JIT build",
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

    // The Linux sysroot is fetched by content digest; the digests are where the workflows, the cache keys and the documentation agree.
    [Fact]
    public void TheSysrootLayersAreNamedByTheSameDigestEverywhere()
    {
        string[] digests =
        [
            "e47eeec6fa3711530ee4a6a7e7388dc338e99a5f3649770cc86a4f5ead96c0e2",
            "b572d3dd8b297da80060077fbec8cd6d5062c219722ed466793128a640ac5e3b",
        ];
        string ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        string documentation = File.ReadAllText(Path.Combine(Root, "docs", "development.md"));
        foreach (string digest in digests)
        {
            // The download here, which restores no cache; CI's also has the keys of its restore and save steps.
            Assert.Single(Regex.Matches(Workflow(), digest));
            Assert.Equal(3, Regex.Matches(ci, digest).Count);
            Assert.Contains(digest, documentation, StringComparison.Ordinal);
        }
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
    // The release ships the runtime of the SDK it builds with, and the notices name that runtime. The pairing is in
    // scripts/release/dotnet-pin.txt; a .NET servicing release changes all three together.
    [Fact]
    public void TheSdkIsPinnedToTheRuntimeTheNoticesName()
    {
        Dictionary<string, string> pin = File.ReadAllLines(Path.Combine(Root, "scripts", "release", "dotnet-pin.txt"))
            .Where(l => l.Contains('=', StringComparison.Ordinal)).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1].Trim());

        string[] setup = [.. Job("build").Where(l => l.TrimStart().StartsWith("dotnet-version:", StringComparison.Ordinal))];
        Assert.Single(setup);
        string sdk = setup[0].Split(':', 2)[1].Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+$", sdk);
        Assert.Equal(pin["sdk"], sdk);

        string notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains($"package, version {pin["runtime"]}.", notices, StringComparison.Ordinal);
        Assert.Contains($"| .NET runtime and base class libraries | {pin["runtime"]} |", notices, StringComparison.Ordinal);

        // CI stays on the newest 10.0 SDK.
        Assert.Contains("dotnet-version: 10.0.x", File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml")), StringComparison.Ordinal);
    }

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
