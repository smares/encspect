#!/usr/bin/env python3
"""The shards Encspect.Tests is split into on CI, as dotnet test filters.

shards.json names the classes of each shard but the last, whose classes are null: it runs every test no
other shard lists, so a new test class lands in a shard by itself. The last shard also names the damaged-input
classes (Corrupt*Tests) it is meant to run (damagedInputClasses), so that check_shards.py fails when another one
falls into it because a shard lost it. A class is matched by its dotted name
inside the fully qualified one (".Name."), so a class whose name starts with another's is not caught by it.

A class that alone outlasts the time budget (docs/development.md, "Shards") is split by test name: it is listed in each of
the shards that share it, and each of them has an entry under "cases" for it, a list of strings that a test's display name
(the full name: namespace, class, method and theory row, matched without regard to case, such as `ATime...(name: "flac-fixed-order-4")`; a quotation mark cannot be written in the filter, so a string starts at the case name) contains, or null for the shard that takes every test of the
class no other shard names. A short string (Wav, Tak, Ape) also catches a later test whose name contains it: nothing is lost, the case
moves to that shard. The strings of different shards must not repeat, and check_shards.py proves that every test lands in exactly one shard.

    shards.py names                  # one shard name per line
    shards.py filter <name>          # the --filter expression of a shard, every test of it
    shards.py filter <name> --skip-nightly   # the same without the tests of the nightly run only

A test of the nightly run only (DecoderWorkTests and MeasurementWorkTests, which time the decoders and the measurements) carries the trait Category=Nightly; the pull request and the push
to main add NIGHTLY_EXCLUSION to every shard's filter, the nightly schedule and a manual run do not (changes.py). A shard with
"nightly": true holds only such tests and runs in the nightly schedule and a manual run only (runs).
"""

import json
import pathlib
import sys

CONFIG = pathlib.Path(__file__).resolve().with_name("shards.json")

NIGHTLY_TRAIT = "Category=Nightly"
NIGHTLY_EXCLUSION = "Category!=Nightly"


def load(path=CONFIG):
    shards = json.loads(pathlib.Path(path).read_text())["shards"]
    names = [s["name"] for s in shards]
    if len(set(names)) != len(names):
        raise SystemExit("shards.json: a shard name is used twice")
    if [s["classes"] is None for s in shards] != [False] * (len(shards) - 1) + [True]:
        raise SystemExit("shards.json: exactly the last shard has classes: null")
    if any(s.get("nightly") and s["classes"] is None for s in shards):
        raise SystemExit("shards.json: the shard that takes the rest cannot be of the nightly run only")
    classes = [c for s in shards[:-1] for c in s["classes"]]
    if any("cases" in s for s in shards[-1:]):
        raise SystemExit("shards.json: the shard that takes the rest cannot split a class")
    for shard in shards[:-1]:
        stray = set(shard.get("cases", {})) - set(shard["classes"])
        if stray:
            raise SystemExit(f"shards.json: {shard['name']} splits {sorted(stray)[0]}, which it does not list")
    for name in {c for c in classes if classes.count(c) > 1}:
        sharing = [s.get("cases", {}) for s in shards[:-1] if name in s["classes"]]
        if any(name not in cases for cases in sharing):
            raise SystemExit(f"shards.json: a class is listed in two shards: {name}")
    for name in {c for s in shards[:-1] for c in s.get("cases", {})}:
        sharing = [s["cases"][name] for s in shards[:-1] if name in s.get("cases", {})]
        if len(sharing) < 2 or sharing.count(None) != 1:
            raise SystemExit(f"shards.json: {name} is split, so two shards or more share it and exactly one of them takes the rest (null)")
        words = [w for names in sharing if names is not None for w in names]
        if not words or len(set(words)) != len(words):
            raise SystemExit(f"shards.json: the strings that split {name} are empty or repeat")
    if any("damagedInputClasses" in s for s in shards[:-1]):
        raise SystemExit("shards.json: only the last shard names its damagedInputClasses")
    if set(shards[-1].get("damagedInputClasses", [])) & set(classes):
        raise SystemExit("shards.json: a class of the last shard's damagedInputClasses is listed in another shard")
    return shards


def damaged_input_in_rest(shards, test_classes):
    """The damaged-input classes (Corrupt*Tests) that fall into the last shard without being named in its
    damagedInputClasses: a class a shard lost, which the last shard would otherwise run unnoticed."""
    named = {c for s in shards[:-1] for c in s["classes"]}
    meant = set(shards[-1].get("damagedInputClasses", []))
    return sorted(c for c in test_classes if c.startswith("Corrupt") and c.endswith("Tests") and c not in named and c not in meant)


def runs(shard, include_nightly):
    """Whether a shard runs in an event: a shard of the nightly run only runs where the tests of the nightly run only do."""
    return include_nightly or not shard.get("nightly", False)


def class_term(shards, shard, name):
    """The filter clause of one class in a shard: the class, and where the class is split, the tests the shard takes of it."""
    clause = f"FullyQualifiedName~.{name}."
    cases = shard.get("cases", {})
    if name not in cases:
        return clause
    if cases[name] is not None:
        return f"({clause}&({'|'.join(f'DisplayName~{w}' for w in cases[name])}))"
    others = [w for s in shards[:-1] for w in (s.get("cases", {}).get(name) or [])]
    return f"({clause}&{'&'.join(f'DisplayName!~{w}' for w in others)})"


def filter_for(shards, name, include_nightly=True):
    listed = [s for s in shards if s["name"] == name]
    if len(listed) != 1:
        raise SystemExit(f"no shard named {name}")
    if listed[0]["classes"] is not None:
        expression = "|".join(class_term(shards, listed[0], c) for c in listed[0]["classes"])
        return expression if include_nightly else f"({expression})&{NIGHTLY_EXCLUSION}"
    others = list(dict.fromkeys(c for s in shards[:-1] for c in s["classes"]))
    expression = "&".join(f"FullyQualifiedName!~.{c}." for c in others)
    return expression if include_nightly else f"{expression}&{NIGHTLY_EXCLUSION}"


def main(argv):
    shards = load()
    if argv[:1] == ["names"]:
        print("\n".join(s["name"] for s in shards))
    elif argv[:1] == ["filter"] and len(argv) == 2:
        print(filter_for(shards, argv[1]))
    elif argv[:1] == ["filter"] and len(argv) == 3 and argv[2] == "--skip-nightly":
        print(filter_for(shards, argv[1], include_nightly=False))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
