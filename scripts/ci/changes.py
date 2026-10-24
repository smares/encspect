#!/usr/bin/env python3
"""Decides which jobs of ci.yml a change needs, and the platforms and test shards to run them on.

    changes.py --event pull_request          # diffs HEAD^1..HEAD (the merge commit of a pull request)
    changes.py --event pull_request --files-from list.txt   # for tests and local runs
    changes.py --event push                  # runs everything

On a pull request only the jobs whose inputs changed run; on a push to main, the nightly schedule and
a manual run nothing is skipped. Whenever the change cannot be read, or a path is not known to any
area, everything runs: a skipped check must always be a deliberate decision.

Prints key=value lines for $GITHUB_OUTPUT (and to standard output with --print). A job's output has its
own job ID as its name, so the aggregate job (check_results.py) can tell a deliberate skip from a
skipped dependency.
"""

import argparse
import fnmatch
import json
import os
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import shards as shard_config  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURE_SCRIPTS = "scripts/fixtures"

# The cross-check jobs and what each one reads. "projects" are directories under src, "fixtures" are
# directories under tests/fixtures, "scripts" are the entry scripts whose imports (followed in
# scripts/fixtures) belong to the job, "globs" are the rest (C, C++ and PHP sources, the manifests).
AREAS = {
    "mpeg-crosscheck": {
        "projects": ["Encspect.Formats.Mpeg", "Encspect.Tags.Id3", "Encspect.Tags.Ape", "Encspect.Tags.Lyrics3"],
        "fixtures": ["mpeg", "layer2", "layer3", "vbr", "stats", "tags", "collisions"],
        "scripts": ["crosscheck_mpeg_fixtures.py", "conformance_mpeg_audio.py", "check_mp3pro_samples.py"],
        "globs": ["scripts/fixtures/*.c", "scripts/fixtures/*.php", "scripts/fixtures/make_*mpeg*", "scripts/fixtures/make_layer*",
                  "scripts/fixtures/make_vbr*", "scripts/fixtures/make_stats*", "scripts/fixtures/make_*tag*", "scripts/fixtures/stream_stats.py",
                  "tests/conformance/mpeg2-audio.json", "tests/conformance/mp3pro-samples.json", "third_party/**"],
    },
    "mp4-crosscheck": {
        "projects": ["Encspect.Formats.Mp4", "Encspect.Formats.Aac"],
        "fixtures": ["mp4", "adts", "aac", "apple", "alac"],
        "scripts": ["crosscheck_mp4_fixtures.py", "check_usac_samples.py", "check_sbr_samples.py", "check_aac_tables.py", "dashif_systems.py", "gpac_tool.py"],
        "globs": ["scripts/fixtures/make_mp4*", "scripts/fixtures/aac_*", "scripts/fixtures/make_*aac*", "scripts/fixtures/make_*protection*",
                  "tests/conformance/usac-samples.json", "tests/conformance/sbr-samples.json"],
    },
    "ogg-crosscheck": {
        "projects": ["Encspect.Formats.Ogg", "Encspect.Formats.Opus", "Encspect.Formats.Speex", "Encspect.Formats.Vorbis", "Encspect.Tags.VorbisComment"],
        "fixtures": ["ogg"],
        "scripts": ["crosscheck_ogg_fixtures.py"],
        "globs": ["scripts/fixtures/make_*ogg*", "scripts/fixtures/make_*vorbis*", "scripts/fixtures/ogg_model.py"],
    },
    "musepack-crosscheck": {
        "projects": ["Encspect.Formats.Musepack", "Encspect.Tags.Ape"],
        "fixtures": ["musepack"],
        "scripts": ["crosscheck_musepack_fixtures.py"],
        "globs": ["scripts/fixtures/*.c", "scripts/fixtures/*.cpp", "scripts/fixtures/make_*musepack*"],
    },
    "wavpack-crosscheck": {
        "projects": ["Encspect.Formats.WavPack", "Encspect.Tags.Ape"],
        "fixtures": ["wavpack"],
        "scripts": ["crosscheck_wavpack_fixtures.py"],
        "globs": ["scripts/fixtures/*.c", "scripts/fixtures/make_*wavpack*", "scripts/fixtures/wavpack_*"],
    },
    "flac-crosscheck": {
        "projects": ["Encspect.Formats.Flac", "Encspect.Tags.VorbisComment"],
        "fixtures": ["flac"],
        "scripts": ["crosscheck_flac_fixtures.py"],
        "globs": ["scripts/fixtures/make_*flac*", "scripts/fixtures/flac_*"],
    },
    "wav-crosscheck": {
        "projects": ["Encspect.Formats.Wav", "Encspect.Formats.Aiff"],
        "fixtures": ["wav", "aiff"],
        "scripts": ["crosscheck_wav_fixtures.py"],
        "globs": ["scripts/fixtures/make_wav_fixtures.py", "scripts/fixtures/make_external_wav_fixtures.py", "scripts/fixtures/make_aiff_fixtures.py",
                  "scripts/fixtures/make_external_aiff_fixtures.py", "scripts/fixtures/aiff_model.py", "scripts/fixtures/wav_model.py"],
    },
    "asf-crosscheck": {
        "projects": ["Encspect.Formats.Asf", "Encspect.Formats.Wma"],
        "fixtures": ["asf"],
        "scripts": ["crosscheck_asf_fixtures.py", "wma_tables.py", "check_wmavoice_samples.py"],
        "globs": ["scripts/fixtures/make_*asf*", "scripts/fixtures/make_voice*", "scripts/fixtures/windows/**", "tests/conformance/wmavoice-samples.json"],
    },
    "matroska-crosscheck": {
        "projects": ["Encspect.Formats.Matroska", "Encspect.Formats.Aac", "Encspect.Formats.Ac3", "Encspect.Formats.Dts", "Encspect.Formats.Mpeg",
                     "Encspect.Formats.Opus", "Encspect.Formats.Vorbis"],
        "fixtures": ["matroska"],
        "scripts": ["crosscheck_matroska_fixtures.py", "matroska_tables.py"],
        "globs": ["scripts/fixtures/make_*matroska*", "scripts/fixtures/matroska_*model.py"],
    },
    "ac3-crosscheck": {
        "projects": ["Encspect.Formats.Ac3", "Encspect.Formats.Mp4"],
        "fixtures": ["ac3"],
        "scripts": ["crosscheck_ac3_fixtures.py", "check_eac3_samples.py"],
        "globs": ["scripts/fixtures/*.c", "scripts/fixtures/make_*ac3*", "scripts/fixtures/emdf_model.py", "tests/conformance/eac3-samples.json"],
    },
    "dts-crosscheck": {
        "projects": ["Encspect.Formats.Dts", "Encspect.Formats.DtsHd", "Encspect.Formats.Mp4"],
        "fixtures": ["dts"],
        "scripts": ["crosscheck_dts_fixtures.py", "check_dts_hd_samples.py"],
        "globs": ["scripts/fixtures/*.c", "scripts/fixtures/make_*dts*", "scripts/fixtures/dts_*", "tests/conformance/dts-hd-samples.json"],
    },
}

# A change here concerns every job: the analysis every format goes through, the front end the
# cross-checks run, the modules every cross-check shares, and the pipeline itself.
SHARED = [
    "src/Encspect.Core.Abstractions/**", "src/Encspect.Host/**", "src/Encspect.Cli/**",
    "scripts/fixtures/downloads.py", "scripts/fixtures/mediainfo_tool.py", "scripts/fixtures/channel_layouts.py",
    ".github/workflows/**", ".github/actions/**", "scripts/ci/**",
    "Directory.Build.props", "Directory.Packages.props", "global.json", "Encspect.slnx", ".editorconfig", ".gitattributes",
]

# What the .NET jobs (build, tests, fuzz, formatting) read. Anything under these runs them.
DOTNET = ["src/**", "tests/**", "Directory.*.props", "Directory.Packages.props", "global.json", "*.slnx", ".editorconfig", ".gitattributes",
          "scripts/*.sh", ".github/workflows/**", ".github/actions/**", "scripts/ci/**", ".claude/settings.json"]

# Documentation only: nothing here changes what the analyzer does. Markdown under src is a topic article
# (the host serves it, and DescriptionTests reads it), so it is code.
DOCS = ["docs/**", "LICENSE", "**/*.md"]

# Scripts that only write fixtures with encoders no job installs; a change reruns the reproducibility job.
FIXTURE_ONLY = ["scripts/fixtures/*_tool.py", "scripts/fixtures/make_*.py", "scripts/fixtures/import_*.py", "scripts/fixtures/*_specs.py",
                "scripts/fixtures/*_model.py", "scripts/fixtures/*_signal*.py", "scripts/fixtures/*_sources.py", "scripts/fixtures/*_source.py",
                "scripts/fixtures/check_sources.py", "scripts/fixtures/check_restricted_encoders.py", "scripts/fixtures/check_faac_sbr.py",
                "scripts/fixtures/ancillary_scan.py", "scripts/fixtures/vbr_tags.py", "scripts/fixtures/aac_tables.py",
                "scripts/fixtures/macos/**"]

CI_JOBS_DOTNET = ["build-test", "unit-tests", "shard-guard", "fuzz", "format"]
ALL_JOBS = CI_JOBS_DOTNET + ["fixtures"] + list(AREAS)

PULL_REQUEST_PLATFORMS = [
    {"os": "ubuntu-26.04", "rid": "linux-x64"},
    {"os": "windows-latest", "rid": "win-x64"},
    {"os": "macos-latest", "rid": "osx-arm64"},
    {"os": "windows-11-arm", "rid": "win-arm64"},
]
FULL_PLATFORMS = PULL_REQUEST_PLATFORMS + [
    {"os": "ubuntu-26.04-arm", "rid": "linux-arm64"},
    {"os": "macos-15-intel", "rid": "osx-x64"},
]


def glob_match(path, pattern):
    """fnmatch with ** crossing directories and * not."""
    regex = ""
    i = 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            regex += "(?:.*/)?"
            i += 3
        elif pattern.startswith("**", i):
            regex += ".*"
            i += 2
        elif pattern[i] == "*":
            regex += "[^/]*"
            i += 1
        elif pattern[i] == "?":
            regex += "[^/]"
            i += 1
        else:
            regex += re.escape(pattern[i])
            i += 1
    return re.fullmatch(regex, path) is not None


def matches(path, patterns):
    return any(glob_match(path, p) for p in patterns)


IMPORT = re.compile(r"^\s*import\s+(.+?)\s*(?:#.*)?$", re.MULTILINE)
FROM_IMPORT = re.compile(r"^\s*from\s+([A-Za-z_][A-Za-z0-9_]*)\s+import\b", re.MULTILINE)


def imported_modules(text):
    """The top-level module names a script imports: import a, b as c; from d import e."""
    names = set(FROM_IMPORT.findall(text))
    for clause in IMPORT.findall(text):
        for item in clause.split(","):
            name = item.strip().split(" ")[0].split(".")[0]
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name):
                names.add(name)
    return names


def import_closure(entries, root=ROOT):
    """The scripts in scripts/fixtures that the entry scripts import, directly or not, entries included.
    A module is in the closure by name whether or not its file exists: a helper that a change deletes or
    renames must still run the jobs that imported it."""
    directory = root / FIXTURE_SCRIPTS
    seen = set()
    pending = list(entries)
    while pending:
        name = pending.pop()
        if name in seen:
            continue
        seen.add(name)
        if (directory / name).exists():
            text = (directory / name).read_text(encoding="utf-8", errors="replace")
            pending += [f"{module}.py" for module in imported_modules(text)]
    return seen


def area_matches(area, path, closure):
    spec = AREAS[area]
    if any(path.startswith(f"src/{p}/") for p in spec["projects"]):
        return True
    if any(path.startswith(f"tests/fixtures/{d}/") for d in spec["fixtures"]):
        return True
    if path.startswith(FIXTURE_SCRIPTS + "/") and path.count("/") == 2 and path.rsplit("/", 1)[1] in closure:
        return True
    return matches(path, spec["globs"])


def is_docs(path):
    return matches(path, DOCS) and not path.startswith(("src/", "tests/"))


SOURCE_PROJECTS = {project for spec in AREAS.values() for project in spec["projects"]} | {"Encspect.Gui"}
TEST_FOLDERS = {"Encspect.Tests", "Encspect.Gui.Tests", "Encspect.Fuzz", "fixtures", "conformance"}
PARSER_PREFIXES = ("src/Encspect.Formats.", "src/Encspect.Tags.")

# What the fixtures job's scripts read outside scripts/ and tests/: check_aac_tables.py compares the Huffman
# and band tables in the AAC parser with fdk-aac's.
FIXTURES_READ = ["src/Encspect.Formats.Aac/**"]


def unknown_path(path):
    """A path no job is known to read: a new format, a new shared library, a data file of the fixture scripts.
    Anything unknown runs everything."""
    parts = path.split("/")
    if parts[0] == "src":
        return len(parts) < 3 or parts[1] not in SOURCE_PROJECTS
    if parts[0] == "tests":
        if len(parts) < 3 or parts[1] not in TEST_FOLDERS:
            return True
        if parts[1] == "fixtures":
            return len(parts) < 4 or (parts[2] != "fuzz" and not any(parts[2] in spec["fixtures"] for spec in AREAS.values()))
        return False
    if path.startswith(FIXTURE_SCRIPTS + "/"):
        top_level_script = path.count("/") == 2 and path.endswith(".py")
        return (not top_level_script and not any(matches(path, spec["globs"]) for spec in AREAS.values())
                and not matches(path, FIXTURE_ONLY))
    return False


def known_elsewhere(path):
    """Paths no cross-check reads and that are not shared either."""
    return (matches(path, ["src/Encspect.Gui/**", "tests/Encspect.Gui.Tests/**", "tests/Encspect.Tests/**", "tests/Encspect.Fuzz/**"])
            or is_docs(path) or matches(path, FIXTURE_ONLY) or path == "scripts/ci_report.py")


def decide(event, files, root=ROOT, removed=()):
    """The job flags for a change; files is None when the change cannot be read. removed are the paths a change
    deleted or renamed away from."""
    flags = {job: True for job in ALL_JOBS}
    reason = "every job runs on " + event
    if event == "pull_request" and files is not None:
        flags = {job: False for job in ALL_JOBS}
        files = [f for f in files if f]
        closures = {area: import_closure(AREAS[area]["scripts"], root) for area in AREAS}
        reasons = []
        gone = [r for r in removed if r.startswith("scripts/")]
        if gone:
            flags = {job: True for job in ALL_JOBS}
            reasons.append(f"{gone[0]} was deleted or renamed, and what imported it cannot be told")
            files = []
        for path in files:
            if matches(path, SHARED):
                flags = {job: True for job in ALL_JOBS}
                reasons.append(f"{path} is shared")
                break
            if unknown_path(path):
                flags = {job: True for job in ALL_JOBS}
                reasons.append(f"{path} belongs to no known area")
                break
            hit = False
            for area in AREAS:
                if area_matches(area, path, closures[area]):
                    flags[area] = True
                    hit = True
            if matches(path, DOTNET) and not is_docs(path):
                for job in CI_JOBS_DOTNET:
                    flags[job] = True
                hit = True
            if path.startswith(FIXTURE_SCRIPTS + "/") or path.startswith("tests/fixtures/") or path.startswith("tests/conformance/") or matches(path, FIXTURES_READ):
                flags["fixtures"] = True
                hit = True
            if path.startswith(PARSER_PREFIXES):
                # The cross-checks that run the analyzer over downloaded samples (ISO conformance, mp3PRO, xHE-AAC,
                # HE-AAC, Dolby's and FFmpeg's E-AC-3 files, FFmpeg's DTS-HD Master Audio files) are in these four jobs; any
                # parser's detection could start claiming those streams.
                flags["mpeg-crosscheck"] = flags["mp4-crosscheck"] = flags["ac3-crosscheck"] = flags["dts-crosscheck"] = True
                hit = True
            if not hit and not known_elsewhere(path):
                # An unknown path might be a new format or a new check: run everything.
                flags = {job: True for job in ALL_JOBS}
                reasons.append(f"{path} belongs to no known area")
                break
            if (path.startswith(FIXTURE_SCRIPTS + "/") and path.rsplit("/", 1)[1].startswith(("crosscheck_", "check_", "conformance_"))
                    and not any(path.rsplit("/", 1)[1] in c for c in closures.values()) and not matches(path, FIXTURE_ONLY)):
                flags = {job: True for job in ALL_JOBS}
                reasons.append(f"{path} is a check no job is known to run")
                break
        reason = "; ".join(reasons) if reasons else f"{len(files)} changed file(s)"
    elif event == "pull_request":
        reason = "the change could not be read, so every job runs"
    return flags, reason


# The events that run the tests of the nightly run only too (Category=Nightly, see shards.py): the schedule and a manual run.
NIGHTLY_EVENTS = ("schedule", "workflow_dispatch")


def platforms(event):
    return PULL_REQUEST_PLATFORMS if event == "pull_request" else FULL_PLATFORMS


def parse_name_status(text):
    """(paths, removed) from git diff --name-status: removed are deleted paths and the old paths of renames."""
    files, removed = [], []
    for line in text.splitlines():
        fields = line.split("\t")
        if len(fields) < 2:
            continue
        files += fields[1:]
        if fields[0][:1] in ("D", "R"):
            removed.append(fields[1])
    return files, removed


def changed_files(root=ROOT):
    """The files the pull request changes: the merge commit's first parent (the base branch) against the commit."""
    parents = subprocess.run(["git", "rev-list", "--parents", "-n", "1", "HEAD"], cwd=root, capture_output=True, text=True)
    if parents.returncode != 0 or len(parents.stdout.split()) != 3:
        return None, []
    diff = subprocess.run(["git", "diff", "--name-status", "HEAD^1", "HEAD"], cwd=root, capture_output=True, text=True)
    return parse_name_status(diff.stdout) if diff.returncode == 0 else (None, [])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", default=os.environ.get("GITHUB_EVENT_NAME", "push"))
    parser.add_argument("--files-from", help="a file with one changed path per line (or git diff --name-status lines), or - for standard input")
    parser.add_argument("--print", action="store_true", help="print the outputs instead of appending them to $GITHUB_OUTPUT")
    args = parser.parse_args()

    if args.files_from:
        text = sys.stdin.read() if args.files_from == "-" else pathlib.Path(args.files_from).read_text()
        files, removed = parse_name_status(text) if "\t" in text else (text.splitlines(), [])
    else:
        files, removed = changed_files() if args.event == "pull_request" else (None, [])
    flags, reason = decide(args.event, files, removed=removed)

    os_list = platforms(args.event)
    shards = shard_config.load()
    outputs = {job: "true" if on else "false" for job, on in flags.items()}
    outputs["reason"] = reason
    outputs["build-matrix"] = json.dumps(os_list, separators=(",", ":"))
    include_nightly = args.event in NIGHTLY_EVENTS
    outputs["test-matrix"] = json.dumps([{**p, "shard": s["name"], "filter": shard_config.filter_for(shards, s["name"], include_nightly)} for p in os_list for s in shards], separators=(",", ":"))
    lines = [f"{k}={v}" for k, v in outputs.items()]
    if args.print or "GITHUB_OUTPUT" not in os.environ:
        print("\n".join(lines))
    else:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
            out.write("\n".join(lines) + "\n")
        print("\n".join(f"{job}: {outputs[job]}" for job in flags), f"({reason})", sep="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
