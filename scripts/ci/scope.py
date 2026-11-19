#!/usr/bin/env python3
"""The unit tests a pull request needs: the scope of a change that touches only format-owned or tag-owned .NET code.

areas.json maps the tests of Encspect.Tests to the format and tag projects (the areas) that own them. A change whose
.NET-relevant paths all belong to areas selects those areas and the areas that carry them through a host route; the
run then excludes the tests owned by the areas it did not select and runs every other test. Whatever cannot be
placed runs everything (changes.py), and so does anything but a pull request.

    scope.py --files-from list.txt     # the scope of a list of changed paths and the filter of each shard
"""

import functools
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import shards as shard_config  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
CONFIG = pathlib.Path(__file__).resolve().with_name("areas.json")
TESTS = "tests/Encspect.Tests"

CLASS = re.compile(r"^(?:(?:public|internal)\s+)?(?:(?:sealed|static|abstract|partial)\s+)*class\s+(\w+)", re.MULTILINE)
TEST_METHOD = re.compile(r"^\s+\[(?:Fact|Theory)[^\]]*\](?:\s*\[[^\]]*\])*\s*public\s+(?:async\s+)?(?:void|Task|ValueTask)\s+(\w+)\s*\(", re.MULTILINE)


def load(path=CONFIG):
    return json.loads(pathlib.Path(path).read_text())


def area_of(path, config):
    """The areas a changed path belongs to, or None when it belongs to none (the run is then the whole suite).
    A project file is none: it may add a reference between projects."""
    parts = path.split("/")
    areas = config["areas"]
    if parts[0] == "src" and len(parts) >= 3:
        match = re.fullmatch(r"Encspect\.(?:Formats|Tags)\.(\w+)", parts[1])
        return {match.group(1)} if match and match.group(1) in areas and not path.endswith((".csproj", ".props", ".targets")) else None
    if parts[:2] == ["tests", "Encspect.Tests"] and len(parts) >= 5 and parts[2] in ("Formats", "Tags"):
        return {parts[3]} if parts[3] in areas else None
    if parts[:2] == ["tests", "fixtures"] and len(parts) >= 4:
        listed = config["fixtures"].get(parts[2])
        return down(set(listed), config) if listed else None
    return None


def down(selected, config):
    """A container's fixtures are read by the tests of the codecs it carries too."""
    result = set(selected)
    for container in selected:
        result |= {config["codecs"][codec] for codec in config["routes"].get(container, [])}
    return result


def carriers(selected, config):
    """The selected areas and every area that carries one of them: a container that hands a payload to the codec by its ID,
    a format or container that holds a tag the area parses."""
    result = set(selected)
    while True:
        more = {container for container, ids in config["routes"].items() if any(config["codecs"][i] in result for i in ids)}
        more |= {carrier for tag, carried in config["tags"].items() if tag in result for carrier in carried}
        if more <= result:
            return result
        result |= more


def sources_of(root=ROOT):
    return sorted((root / TESTS).rglob("*.cs"))


def class_extent(text):
    """(name, start, end) of every top-level class of a file."""
    found = [(m.group(1), m.start()) for m in CLASS.finditer(text)]
    return [(name, start, found[i + 1][1] if i + 1 < len(found) else len(text)) for i, (name, start) in enumerate(found)]


@functools.lru_cache(maxsize=None)
def _inventory(root, config_key):
    """(owners, methods): the owners of every test class in the area folders, and the test methods of each class of a file
    that has any ('Class' -> ['Method', ...])."""
    config = json.loads(config_key)
    owners = {}
    methods = {}
    markers = {area: [re.compile(rf"using Encspect\.(?:Formats|Tags)\.{area}\b"), re.compile(rf"\b{area}(?:Builder|Streams)\b")] for area in config["areas"]}
    for folder, listed in config["fixtures"].items():
        markers[listed[0]].append(re.compile(rf'["/]{re.escape(folder)}(?:/|")'))
    for file in sources_of(root):
        text = file.read_text(encoding="utf-8", errors="replace")
        relative = file.relative_to(root / TESTS).parts
        home = relative[1] if relative[0] in ("Formats", "Tags") and len(relative) > 2 and relative[1] in config["areas"] else None
        for name, start, end in class_extent(text):
            body = text[start:end]
            found = TEST_METHOD.findall(body)
            if found:
                methods[name] = found
            if home is not None and found:
                owners[name] = {home} | {area for area, patterns in markers.items() if area != home and any(p.search(text) for p in patterns)}
    return owners, methods


def inventory(root=ROOT, config=None):
    return _inventory(root, json.dumps(config or load(), sort_keys=True))


class Scope:
    def __init__(self, changed, selected, excluded_classes, excluded_methods, empty_classes):
        self.changed = changed
        self.selected = selected
        self.excluded_classes = excluded_classes  # sorted class names
        self.excluded_methods = excluded_methods  # class -> sorted method names
        self.empty_classes = empty_classes  # classes with no test left

    def describe(self):
        order = sorted(self.selected, key=lambda a: (a not in self.changed, a))
        return "unit tests scoped to " + ", ".join(order)


def build(selected, root=ROOT, config=None):
    """The scope that selects these areas (carriers added by the caller)."""
    config = config or load()
    owners, methods = inventory(root, config)
    excluded = sorted(c for c, o in owners.items() if not o & selected)
    for name in config["classes"]:
        if not set(config["classes"][name]) & selected:
            excluded.append(name)
    excluded = sorted(set(excluded))
    excluded_methods = {}
    empty = set(excluded)
    for name, per_method in config["methods"].items():
        if name in excluded:
            continue
        gone = sorted(m for m, o in per_method.items() if not set(o) & selected)
        if gone:
            excluded_methods[name] = gone
        if set(methods.get(name, ["?"])) <= set(gone):
            empty.add(name)
    return Scope(set(selected), selected, excluded, excluded_methods, empty)


def plan(files, root=ROOT, config=None):
    """The scope of a pull request, or None when the unit tests run in full: files are the .NET-relevant changed paths, and
    every one must belong to an area."""
    config = config or load()
    if not files:
        return None
    changed = set()
    for path in files:
        areas = area_of(path, config)
        if areas is None:
            return None
        changed |= areas
    scope = build(carriers(changed, config), root, config)
    scope.changed = changed
    return scope


def exclusion(scope, shard, shards):
    """The clauses of the filter that leave out of this shard the tests the scope excludes. A shard that names its classes
    needs only those of them; the rest shard already excludes the classes the others name."""
    named = {c for s in shards[:-1] for c in s["classes"]}
    mine = set(shard["classes"]) if shard["classes"] is not None else None

    def counts(name):
        return name in mine if mine is not None else name not in named

    clauses = [f"FullyQualifiedName!~.{c}." for c in scope.excluded_classes if counts(c)]
    clauses += [f"FullyQualifiedName!~.{c}.{m}" for c, gone in sorted(scope.excluded_methods.items()) if counts(c) for m in gone]
    return clauses


def is_empty(scope, shard):
    """Whether the scope leaves a shard that names its classes no test: every class of it is excluded or has no test left."""
    return shard["classes"] is not None and all(c in scope.empty_classes for c in shard["classes"])


def shard_filter(scope, shard, shards, include_nightly):
    base = shard_config.filter_for(shards, shard["name"], include_nightly)
    clauses = exclusion(scope, shard, shards)
    return f"({base})&({'&'.join(clauses)})" if clauses else base


def main(argv):
    import changes
    if argv[:1] != ["--files-from"] or len(argv) != 2:
        raise SystemExit(__doc__)
    files = pathlib.Path(argv[1]).read_text().splitlines()
    flags, reason = changes.decide("pull_request", files)
    print(reason)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
