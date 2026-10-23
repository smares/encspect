#!/usr/bin/env python3
"""Checks that a pull request changes golden reports only as its change files say (docs/design/coding-graphs.md,
"Golden reports"; docs/development.md, "Golden-change files").

    golden_additions.py [--repo DIR] [--base REV] [--head REV] [change-file ...]

It compares each changed golden (tests/**/*.report.json) at the merge base of --base and --head with its version at
--head, as parsed JSON trees and never as bytes. A golden is the JSON array of reports the writer writes (one report
for a file): the old and new arrays must have the same length and are compared pairwise. The change file removes from the new tree what it lists as added,
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
                  {"scope": "stream", "array": "series", "name": "Bitrate", "key": "concept"},  # a key of those elements
                  {"scope": "report", "key": "someKey"},
                  {"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Stereo Coding"}],
      "renamed": [{"parent": ["MPEG Audio", "Statistics"], "from": "Bandwidth", "to": "Coded Bandwidth"}],  # only where the old report has it
      "moved":   [{"name": "Long Blocks", "from": ["Ogg", "Statistics"], "fromPosition": 4,
                   "to": ["Ogg", "Statistics", "Block Sizes"], "position": 0}],
      "changed": [{"path": ["MPEG Audio", "Statistics", "Channel Pairs"], "fields": ["value"]}]
    }

Paths and names are those of the new report, except "from" of renamed and moved, which are the old names. A rename counts only
where the old report has a node of the old name under the parent; where it has none, the node of the new name is an addition and the
change lists it as added too (the AAC step's "Coded Bandwidth" replaces "Bandwidth" in some goldens and is new in the others). A node
whose value or children change with its name is listed as changed under both names.
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


def has_child(report, parent, name):
    return any(child.get("name") == name for node in matching(report["root"], parent) for child in node.get("children", []))


def remove_added(report, added, renamed_nodes=frozenset(), old=None):
    """Takes what the change adds out of the new report. A node that the old report has under another name (renamed_nodes: its parent's
    path and its new name) is the renamed node and stays, and so does one that the old report has under that name; a node comes out only
    when its children have come out too, which takes an entry for each (the deepest entries run first), so a child nobody listed fails."""
    for entry in sorted(added, key=lambda e: -len(e["parent"]) if e.get("scope") == "node" else 1):
        scope = entry.get("scope")
        if scope == "report":
            report.pop(entry["key"], None)
        elif scope == "stream":
            for stream in report.get("streams", []):
                if "array" not in entry:
                    stream.pop(entry["key"], None)
                elif "key" in entry:
                    for element in stream.get(entry["array"], []):
                        if element.get("name") == entry["name"]:
                            element.pop(entry["key"], None)
                else:
                    # An array that the stream has empty stays; one that holds only what the change adds is the absent one.
                    elements = stream.get(entry["array"], [])
                    kept = [e for e in elements if e.get("name") != entry["name"]]
                    if len(kept) == len(elements):
                        continue
                    if kept:
                        stream[entry["array"]] = kept
                    else:
                        stream.pop(entry["array"], None)
        elif scope == "node":
            if (tuple(entry["parent"]), entry["name"]) in renamed_nodes or (old is not None and has_child(old, entry["parent"], entry["name"])):
                continue
            for parent in matching(report["root"], entry["parent"]):
                kept = [c for c in parent.get("children", []) if c.get("name") != entry["name"] or c.get("children")]
                if kept:
                    parent["children"] = kept
                else:
                    parent.pop("children", None)
        else:
            raise ChangeError(f"unknown scope in added: {entry}")


def detach_moved(report, moved):
    """Takes every moved node out of its new parent, and returns what to put back where it came from."""
    detached = []
    for entry in sorted(moved, key=lambda e: e["fromPosition"]):
        nodes = []
        for target in matching(report["root"], entry["to"]):
            children = target.get("children", [])
            nodes += [c for c in children if c.get("name") == entry["name"]]
            kept = [c for c in children if c.get("name") != entry["name"]]
            if kept:
                target["children"] = kept
            else:
                target.pop("children", None)
        detached.append((entry, nodes))
    return detached


def reattach_moved(report, detached):
    """Puts the moved nodes back at their old positions, once the added nodes are gone, so that the positions count
    the same siblings as in the old report."""
    for entry, nodes in detached:
        origins = matching(report["root"], entry["from"])
        if not origins:
            raise ChangeError(f"moved: no node at {entry['from']}")
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


def renamed_in(old, renamed):
    """The renames the old report shows: where it has a node of the old name under the parent, the node of the new name is that one
    renamed; where it has none, the node of the new name is an addition, which the change lists as added."""
    return frozenset((tuple(entry["parent"]), entry["to"]) for entry in renamed
                     if any(child.get("name") == entry["from"] for parent in matching(old["root"], entry["parent"]) for child in parent.get("children", [])))


def changed_in_old(changed, renamed, counted):
    """The changed entries as they read in the old report: a path through a node that the old report has under the old name of a
    rename goes by that name, so a change file lists such a node once, under its new name."""
    out = []
    for entry in changed:
        path = list(entry["path"])
        for rename in renamed:
            parent = list(rename["parent"])
            if (tuple(parent), rename["to"]) in counted and path[:len(parent) + 1] == parent + [rename["to"]]:
                path[len(parent)] = rename["from"]
        out.append({**entry, "path": path})
    return out


def transform(new, change, old=None):
    """A new report with the change taken back, ready to compare with the old one. Without the old report every rename counts as one."""
    report = copy.deepcopy(new)
    tag(report["root"], [])
    drop_changed(report, change.get("changed", []))
    detached = detach_moved(report, change.get("moved", []))
    renamed = change.get("renamed", [])
    remove_added(report, change.get("added", []), renamed_in(old, renamed) if old is not None else frozenset((tuple(e["parent"]), e["to"]) for e in renamed), old)
    reattach_moved(report, detached)
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


def reports(golden):
    """The reports of a golden: the JSON array the writer writes, or one bare report."""
    return golden if isinstance(golden, list) else [golden]


def check_golden(old, new, change):
    """The problems of one golden (an array of reports) against one change file."""
    olds, news = reports(old), reports(new)
    if len(olds) != len(news):
        return [f"{len(olds)} reports in the old golden, {len(news)} in the new"]
    problems = []
    for index, (before, after) in enumerate(zip(olds, news)):
        before = copy.deepcopy(before)
        counted = renamed_in(before, change.get("renamed", []))
        # A renamed node counts only if the new report has it under the new name and no longer under the old.
        for entry in change.get("renamed", []):
            if (tuple(entry["parent"]), entry["to"]) in counted:
                for parent in matching(after["root"], entry["parent"]):
                    names = [c.get("name") for c in parent.get("children", [])]
                    if entry["from"] in names:
                        problems.append(f"[{index}]: the node {entry['from']} is still there under {'/'.join(entry['parent'])}, which the change renames to {entry['to']}")
                    if entry["to"] not in names:
                        problems.append(f"[{index}]: the node {entry['to']} is missing under {'/'.join(entry['parent'])}, where the change renames {entry['from']} to it")
        drop_changed(before, changed_in_old(change.get("changed", []), change.get("renamed", []), counted))
        try:
            expected = transform(after, change, before)
        except ChangeError as error:
            return [str(error)]
        problems += differences(before, expected, f"[{index}]")
    return problems


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
