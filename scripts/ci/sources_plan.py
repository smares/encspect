#!/usr/bin/env python3
"""Decides what the sources workflow checks on a pull request (see .github/workflows/sources.yml).

Usage: python3 scripts/ci/sources_plan.py --files CHANGED_FILES
       python3 scripts/ci/sources_plan.py BASE_LIST HEAD_LIST CHANGED_FILES OUT

The first form prints the reason for a full check, or nothing, from the changed files alone (it needs no list,
so it works when the base commit's check_sources.py has no --list).

BASE_LIST and HEAD_LIST are the outputs of `check_sources.py --list` on the base commit and on the pull
request's merge commit, CHANGED_FILES holds the files the pull request changes, one per line. OUT gets
the entries of HEAD_LIST that BASE_LIST lacks, in the same form, for `check_sources.py --only`. The reason
goes to stdout; "full" as the first word means the whole check runs instead, and OUT is not written.
"""

import json
import pathlib
import sys

# Everything the check reads or runs: a change to one of these can break any download.
CHECK_FILES = ("scripts/fixtures/downloads.py", "scripts/fixtures/check_sources.py", ".github/workflows/sources.yml")


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


def plan(base, head, changed_files):
    """(reason, entries to check or None for the full check)."""
    path = full_reason(changed_files)
    if path is not None:
        return full_message(path), None
    entries = added_or_changed(base, head)
    return f"changed only: {len(entries)} of {len(head)} entries are added or changed", entries


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
    if entries is not None:
        pathlib.Path(out).write_text(json.dumps(entries, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
