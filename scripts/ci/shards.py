#!/usr/bin/env python3
"""The shards Encspect.Tests is split into on CI, as dotnet test filters.

shards.json names the classes of each shard but the last, whose classes are null: it runs every test no
other shard lists, so a new test class lands in a shard by itself. A class is matched by its dotted name
inside the fully qualified one (".Name."), so a class whose name starts with another's is not caught by it.

    shards.py names             # one shard name per line
    shards.py filter <name>     # the --filter expression of a shard
"""

import json
import pathlib
import sys

CONFIG = pathlib.Path(__file__).resolve().with_name("shards.json")


def load(path=CONFIG):
    shards = json.loads(pathlib.Path(path).read_text())["shards"]
    names = [s["name"] for s in shards]
    if len(set(names)) != len(names):
        raise SystemExit("shards.json: a shard name is used twice")
    if [s["classes"] is None for s in shards] != [False] * (len(shards) - 1) + [True]:
        raise SystemExit("shards.json: exactly the last shard has classes: null")
    classes = [c for s in shards[:-1] for c in s["classes"]]
    if len(set(classes)) != len(classes):
        raise SystemExit("shards.json: a class is listed in two shards")
    return shards


def filter_for(shards, name):
    listed = [s for s in shards if s["name"] == name]
    if len(listed) != 1:
        raise SystemExit(f"no shard named {name}")
    if listed[0]["classes"] is not None:
        return "|".join(f"FullyQualifiedName~.{c}." for c in listed[0]["classes"])
    others = [c for s in shards[:-1] for c in s["classes"]]
    return "&".join(f"FullyQualifiedName!~.{c}." for c in others)


def main(argv):
    shards = load()
    if argv[:1] == ["names"]:
        print("\n".join(s["name"] for s in shards))
    elif argv[:1] == ["filter"] and len(argv) == 2:
        print(filter_for(shards, argv[1]))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
