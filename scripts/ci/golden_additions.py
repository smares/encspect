#!/usr/bin/env python3
"""Checks that a pull request changes golden reports only as its change files say (docs/design/coding-graphs.md,
"Golden reports"; docs/development.md, "Golden-change files").

    golden_additions.py [--repo DIR] [--base REV] [--head REV] [change-file ...]

It compares each changed golden (tests/**/*.report.json) at the merge base of --base and --head with its version at
--head, as parsed JSON trees and never as bytes. The change file removes from the new tree what it lists as added,
reverses what it lists as renamed or moved, ignores the fields it lists as changed, maps the paths of findings and
streams through the nodes that shifted, and requires the rest to equal the old tree.

Without change-file arguments the change files are those the pull request adds or changes,
tests/fixtures/*/golden-changes.json; with none the check is not asked for and passes. A changed golden that no change
file covers fails. Names below are node names, as a path from the report's root (the root itself is []); a step matches
every child of that name. A change file:

    {
      "goldens": ["tests/fixtures/mpeg/", "tests/fixtures/mp4/some.report.json"],   # files or folders it covers
      "added":   [{"scope": "stream", "key": "categorySeries"},                      # a key of every stream
                  {"scope": "stream", "array": "series", "name": "Coded Bandwidth"}, # elements of a stream's array
                  {"scope": "report", "key": "someKey"},
                  {"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Stereo Coding"}],
      "renamed": [{"parent": ["MPEG Audio", "Statistics"], "from": "Bandwidth", "to": "Coded Bandwidth"}],
      "moved":   [{"name": "Long Blocks", "from": ["Ogg", "Statistics"], "fromPosition": 4,
                   "to": ["Ogg", "Statistics", "Block Sizes"], "position": 0}],
      "changed": [{"path": ["MPEG Audio", "Statistics", "Channel Pairs"], "fields": ["value"]}]
    }

Paths and names are those of the new report, except "from" of renamed and moved, which are the old names.
"""

import argparse
import copy
import fnmatch
import json
import pathlib
import subprocess
import sys

TAG = "__path"
CHANGE_FILES = "tests/fixtures/*/golden-changes.json"


class ChangeError(Exception):
    pass


def matching(root, names):
    """Every node whose path of names from root is names (root for [])."""
    found = [root]
    for name in names:
        found = [child for node in found for child in node.get("children", []) if child.get("name") == name]
    return found


def tag(node, path):
    node[TAG] = path
    for index, child in enumerate(node.get("children", [])):
        tag(child, path + [index])


def untag(node):
    node.pop(TAG, None)
    for child in node.get("children", []):
        untag(child)


def remove_added(report, added):
    for entry in added:
        scope = entry.get("scope")
        if scope == "report":
            report.pop(entry["key"], None)
        elif scope == "stream":
            for stream in report.get("streams", []):
                if "key" in entry:
                    stream.pop(entry["key"], None)
                else:
                    kept = [e for e in stream.get(entry["array"], []) if e.get("name") != entry["name"]]
                    if kept:
                        stream[entry["array"]] = kept
                    else:
                        stream.pop(entry["array"], None)
        elif scope == "node":
            for parent in matching(report["root"], entry["parent"]):
                kept = [c for c in parent.get("children", []) if c.get("name") != entry["name"]]
                if kept:
                    parent["children"] = kept
                else:
                    parent.pop("children", None)
        else:
            raise ChangeError(f"unknown scope in added: {entry}")


def reverse_moves(report, moved):
    for entry in moved:
        targets = matching(report["root"], entry["to"])
        origins = matching(report["root"], entry["from"])
        if not origins:
            raise ChangeError(f"moved: no node at {entry['from']}")
        for target in targets:
            children = target.get("children", [])
            nodes = [c for c in children if c.get("name") == entry["name"]]
            if not nodes:
                continue
            kept = [c for c in children if c.get("name") != entry["name"]]
            if kept:
                target["children"] = kept
            else:
                target.pop("children", None)
            for origin in origins:
                siblings = origin.setdefault("children", [])
                siblings[entry["fromPosition"]:entry["fromPosition"]] = copy.deepcopy(nodes)


def reverse_renames(report, renamed):
    for entry in renamed:
        for parent in matching(report["root"], entry["parent"]):
            for child in parent.get("children", []):
                if child.get("name") == entry["to"]:
                    child["name"] = entry["from"]


def drop_changed(report, changed):
    for entry in changed:
        for node in matching(report["root"], entry["path"][:-1]):
            for child in node.get("children", []):
                if child.get("name") == entry["path"][-1]:
                    for field in entry["fields"]:
                        child.pop(field, None)


def final_paths(node, path, mapping):
    if TAG in node:
        mapping[tuple(node[TAG])] = path
    for index, child in enumerate(node.get("children", [])):
        final_paths(child, path + [index], mapping)


def map_path(path, mapping):
    """The path in the old tree of a path in the new one, or None where its node is one the change adds."""
    if path is None:
        return None
    return mapping.get(tuple(path), ["<added node>"])


def transform(new, change):
    """The new report with the change taken back, ready to compare with the old one."""
    report = copy.deepcopy(new)
    tag(report["root"], [])
    drop_changed(report, change.get("changed", []))
    reverse_moves(report, change.get("moved", []))
    remove_added(report, change.get("added", []))
    reverse_renames(report, change.get("renamed", []))
    mapping = {}
    final_paths(report["root"], [], mapping)
    for finding in report.get("findings", []):
        if "path" in finding:
            finding["path"] = map_path(finding["path"], mapping)
    for stream in report.get("streams", []):
        if "nodes" in stream:
            stream["nodes"] = [map_path(p, mapping) for p in stream["nodes"]]
    untag(report["root"])
    return report


def differences(old, new, path="", limit=10):
    """Readable differences between two parsed JSON trees, at most limit."""
    found = []

    def walk(a, b, where):
        if len(found) >= limit:
            return
        if isinstance(a, dict) and isinstance(b, dict):
            for key in sorted(set(a) | set(b)):
                if key not in a:
                    found.append(f"{where}/{key}: only in the new report")
                elif key not in b:
                    found.append(f"{where}/{key}: only in the old report")
                else:
                    walk(a[key], b[key], f"{where}/{key}")
        elif isinstance(a, list) and isinstance(b, list):
            if len(a) != len(b):
                found.append(f"{where}: {len(a)} entries in the old report, {len(b)} in the new")
            for index, (x, y) in enumerate(zip(a, b)):
                walk(x, y, f"{where}[{index}]")
        elif a != b or type(a) is not type(b):
            found.append(f"{where}: {json.dumps(a)} in the old report, {json.dumps(b)} in the new")

    walk(old, new, path)
    return found


def check_golden(old, new, change):
    """The problems of one golden against one change file."""
    old = copy.deepcopy(old)
    drop_changed(old, change.get("changed", []))
    try:
        expected = transform(new, change)
    except ChangeError as error:
        return [str(error)]
    return differences(old, expected)


def covers(change, path):
    return any(path == g or (g.endswith("/") and path.startswith(g)) for g in change.get("goldens", []))


def git(repo, *args, check=True):
    result = subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def run(repo, base, head, change_files):
    merge_base = git(repo, "merge-base", base, head).strip()
    status = git(repo, "diff", "--name-status", "--no-renames", merge_base, head).splitlines()
    changes = {}
    if change_files:
        for name in change_files:
            changes[name] = json.loads(pathlib.Path(repo, name).read_text(encoding="utf-8"))
    else:
        for line in status:
            code, _, path = line.partition("\t")
            if code[0] in "AM" and fnmatch.fnmatch(path, CHANGE_FILES):
                changes[path] = json.loads(git(repo, "show", f"{head}:{path}"))
    if not changes:
        print("No change file added or changed: the golden check is not asked for.")
        return 0

    problems = []
    checked = 0
    for line in status:
        code, _, path = line.partition("\t")
        if not path.endswith(".report.json"):
            continue
        if code[0] == "A":
            continue
        if code[0] == "D":
            problems.append(f"{path}: the golden is deleted")
            continue
        owners = [name for name, change in changes.items() if covers(change, path)]
        if not owners:
            problems.append(f"{path}: changed, and no change file covers it")
            continue
        old = json.loads(git(repo, "show", f"{merge_base}:{path}"))
        new = json.loads(git(repo, "show", f"{head}:{path}"))
        found = [check_golden(old, new, changes[owner]) for owner in owners]
        if all(found):
            problems += [f"{path}: {d}" for d in found[0]]
        checked += 1
    for name, change in changes.items():
        if not any(covers(change, line.partition("\t")[2]) for line in status if line.partition("\t")[2].endswith(".report.json")):
            problems.append(f"{name}: covers no changed golden")
    for problem in problems:
        print(problem)
    print(f"{checked} golden(s) checked against {len(changes)} change file(s); {len(problems)} problem(s).")
    return 1 if problems else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--repo", default=".")
    parser.add_argument("--base", default="origin/main")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("change_files", nargs="*")
    args = parser.parse_args(argv)
    return run(args.repo, args.base, args.head, args.change_files)


if __name__ == "__main__":
    sys.exit(main())
