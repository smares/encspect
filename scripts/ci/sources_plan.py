#!/usr/bin/env python3
"""Decides what the sources workflow checks on a pull request (see .github/workflows/sources.yml).

Usage: python3 scripts/ci/sources_plan.py --files CHANGED_FILES
       python3 scripts/ci/sources_plan.py BASE_LIST HEAD_LIST CHANGED_FILES OUT

The first form prints the reason for a full check, or nothing, from the changed files alone (it needs no list,
so it works when the base commit's check_sources.py has no --list).

The second form runs when the first printed nothing. BASE_LIST and HEAD_LIST are the outputs of
`check_sources.py --list` on the base commit and on the pull request's merge commit, CHANGED_FILES holds the
files the pull request changes, one per line. OUT gets, for `check_sources.py --only`, the entries of HEAD_LIST
that BASE_LIST lacks, and every entry of the kinds whose checking code the pull request changes (KIND_MODULES).
"""

import json
import pathlib
import sys

# A change to one of these can break any entry: the download and its digest (downloads.py), the loops and the
# list itself (check_sources.py), the plan (sources_plan.py) and the workflow. The check also runs code in
# the modules of KIND_MODULES, and imports address lists from modules whose changes show in the list diff.
CHECK_FILES = (
    "scripts/fixtures/downloads.py",
    "scripts/fixtures/check_sources.py",
    "scripts/ci/sources_plan.py",
    ".github/workflows/sources.yml",
)

# Modules that decide the outcome of one kind of entry besides listing it: a change to one checks every entry
# of that kind, though no entry changed. Entries of the FFmpeg archive are its members and the archive itself.
KIND_MODULES = {
    "scripts/fixtures/old_lame_tool.py": ("svn",),
    "scripts/fixtures/faac_tool.py": ("tag",),
    "scripts/fixtures/ffmpeg_sources.py": ("member", "archive"),
}


def full_reason(changed_files):
    """The changed file that makes the pull request check everything, or None."""
    for path in CHECK_FILES:
        if path in changed_files:
            return path
    return None


def full_message(path):
    return f"full check: the pull request changes {path}, which affects every download"


def key(entry):
    return entry["name"], entry["sha256"], entry["address"]


def added_or_changed(base, head):
    """Entries of head that base does not hold identically (a changed digest or address is a new entry)."""
    known = {key(entry) for entry in base}
    return [entry for entry in head if key(entry) not in known]


def of_kind(entries, kind):
    if kind != "archive":
        return [entry for entry in entries if entry.get("kind") == kind]
    members = {entry["name"].partition("/")[0] for entry in entries if entry.get("kind") == "member"}
    return [entry for entry in entries if entry.get("kind") == "download" and entry["name"] in members]


def plan(base, head, changed_files):
    """(reason, entries to check): the entries head adds or changes, and all of a kind whose module changed."""
    chosen = {key(entry): entry for entry in added_or_changed(base, head)}
    modules = []
    for path, kinds in KIND_MODULES.items():
        if path in changed_files:
            modules.append(path)
            for kind in kinds:
                for entry in of_kind(head, kind):
                    chosen[key(entry)] = entry
    entries = [chosen[k] for k in sorted(chosen)]
    reason = f"changed only: {len(entries)} of {len(head)} entries are added or changed"
    if modules:
        reason += f" or checked by the changed {', '.join(modules)}"
    return reason, entries


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--files":
        path = full_reason(set(pathlib.Path(sys.argv[2]).read_text(encoding="utf-8").split()))
        if path is not None:
            print(full_message(path))
        return
    base_list, head_list, changed, out = sys.argv[1:5]
    changed_files = set(pathlib.Path(changed).read_text(encoding="utf-8").split())
    base = json.loads(pathlib.Path(base_list).read_text(encoding="utf-8"))
    head = json.loads(pathlib.Path(head_list).read_text(encoding="utf-8"))
    reason, entries = plan(base, head, changed_files)
    print(reason)
    pathlib.Path(out).write_text(json.dumps(entries, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
