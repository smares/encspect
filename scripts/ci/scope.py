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
# Tests no area owns run in every scope, so a change to them selects no area; what areas.json names in them stays owned.
SHARED_TEST_FOLDERS = ("Host", "Decoding", "Measurement", "Core", "Cli")

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
    if parts[:2] == ["tests", "Encspect.Tests"] and len(parts) >= 4 and parts[2] in SHARED_TEST_FOLDERS:
        return set()
    if parts[:2] == ["tests", "fixtures"] and len(parts) >= 4:
        listed = config["fixtures"].get(parts[2])
        return set(listed) if listed else None
    return None


def down(selected, config):
    """The selected areas and the areas of the codecs the selected containers carry: their tests parse files of the container."""
    result = set(selected)
    for container in selected:
        result |= {config["codecs"][codec] for codec in config["routes"].get(container, [])}
    return result


def carriers(changed, config):
    """The areas a change reaches, one level deep. A changed container reaches the codecs it carries (their tests go through it), and a
    changed codec the containers that carry it, but not those containers' other codecs. A changed tag area reaches the formats that hold its
    tag, and a changed format the tag areas whose lists name it; nothing is carried further (the push to main runs everything)."""
    changed = set(changed)
    containers = {container for container, ids in config["routes"].items() if any(config["codecs"][i] in changed for i in ids)}
    forward = {carrier for tag, carried in config["tags"].items() if tag in changed for carrier in carried}
    backward = {tag for tag, carried in config["tags"].items() if set(carried) & changed}
    return down(changed, config) | containers | forward | backward


TYPE = re.compile(r"\b(?:class|struct|record|interface)\s+(\w+)")


def callers_of(path, root):
    """The classes that run when a shared test file changes: those it defines, and those of every test file that names a type defined in it
    (nested types and helpers included), then of the files that name those, and so on."""
    texts = {f: f.read_text(encoding="utf-8", errors="replace") for f in sources_of(root)}
    touched = {root / path}
    names = set()
    while True:
        names = {n for f in touched if f in texts for n in TYPE.findall(texts[f])}
        more = {f for f, text in texts.items() if f not in touched and names and re.search(r"\b(?:" + "|".join(map(re.escape, names)) + r")\b", text)}
        if not more:
            break
        touched |= more
    return {name for f in touched if f in texts for name, _, _ in class_extent(texts[f])}


def sources_of(root=ROOT):
    return sorted((root / TESTS).rglob("*.cs"))


def class_extent(text):
    """(name, start, end) of every top-level class of a file."""
    found = [(m.group(1), m.start()) for m in CLASS.finditer(text)]
    return [(name, start, found[i + 1][1] if i + 1 < len(found) else len(text)) for i, (name, start) in enumerate(found)]


@functools.lru_cache(maxsize=None)
def _inventory(root, config_key):
    """(owners, methods): the owners of every test class in the area folders, and the test methods of each test class
    ('Class' -> ['Method', ...]), both joined across the files of a partial class.
    A file is owned by the area of its folder, by the areas whose namespace, builder or fixture folder it uses, and by the areas of every test
    class of an area folder that it names (a test class that other areas' tests call is relevant to them when its area is selected; owners follow
    such calls through any number of files)."""
    config = json.loads(config_key)
    markers = {area: [re.compile(rf"using Encspect\.(?:Formats|Tags)\.{area}\b"), re.compile(rf"\b{area}(?:Builder|Streams)\b")] for area in config["areas"]}
    for folder, listed in config["fixtures"].items():
        markers[listed[0]].append(re.compile(rf'["/]{re.escape(folder)}(?:/|")'))
    files = {}
    defined = {}
    for file in sources_of(root):
        text = file.read_text(encoding="utf-8", errors="replace")
        relative = file.relative_to(root / TESTS).parts
        home = relative[1] if relative[0] in ("Formats", "Tags") and len(relative) > 2 and relative[1] in config["areas"] else None
        classes = [(name, TEST_METHOD.findall(text[start:end])) for name, start, end in class_extent(text)]
        files[file] = (home, text, classes)
        if home is not None:
            for name, _ in classes:
                defined.setdefault(name, set()).add(file)
    owned = {file: ({home} | {a for a, patterns in markers.items() if a != home and any(p.search(text) for p in patterns)}) if home else set()
             for file, (home, text, _) in files.items() if home}
    called = re.compile(r"\b(" + "|".join(map(re.escape, sorted(defined, key=len, reverse=True))) + r")\b") if defined else None
    uses = {file: {g for n in set(called.findall(files[file][1])) for g in defined[n] if g != file} for file in owned} if called else {}
    changed = True
    while changed:
        changed = False
        for file, others in uses.items():
            for other in others:
                if not owned[other] <= owned[file]:
                    owned[file] |= owned[other]
                    changed = True
    owners = {}
    methods = {}
    for file, (home, _, classes) in files.items():
        for name, found in classes:
            if found:
                methods.setdefault(name, [])
                methods[name] += [m for m in found if m not in methods[name]]
                if home is not None:
                    owners.setdefault(name, set()).update(owned[file])
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
        return "unit tests scoped to " + (", ".join(order) or "no area")


def build(selected, root=ROOT, config=None, forced=frozenset()):
    """The scope that selects these areas (carriers added by the caller); the classes in forced run whole."""
    config = config or load()
    owners, methods = inventory(root, config)
    excluded = sorted(c for c, o in owners.items() if not o & selected and c not in forced)
    for name in config["classes"]:
        if not set(config["classes"][name]) & selected and name not in forced:
            excluded.append(name)
    excluded = sorted(set(excluded))
    excluded_methods = {}
    empty = set(excluded)
    for name, per_method in config["methods"].items():
        if name in excluded or name in forced:
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
    forced = set()
    for path in files:
        areas = area_of(path, config)
        if areas is None:
            return None
        changed |= areas
        if not areas and (root / path).is_file():
            # A changed test file of a shared folder runs every class it defines, whoever owns it.
            forced |= callers_of(path, root)
    scope = build(carriers(changed, config), root, config, frozenset(forced))
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
    clauses += [f"FullyQualifiedName!=Encspect.Tests.{c}.{m}" for c, gone in sorted(scope.excluded_methods.items()) if counts(c) for m in gone]
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
