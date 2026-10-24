#!/usr/bin/env python3
"""Fails unless the shard filters of shards.json partition the tests of Encspect.Tests: every test, theory
row included, is listed by exactly one shard, and no shard is empty or names a class that does not exist.

Needs the test project built in Release:   python3 scripts/ci/check_shards.py
"""

import collections
import pathlib
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import shards as shard_config  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]


def listed(filter_expression=None):
    command = ["dotnet", "test", "tests/Encspect.Tests", "--no-build", "--configuration", "Release", "--list-tests"]
    if filter_expression:
        command += ["--filter", filter_expression]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    if result.returncode != 0:
        sys.stderr.write(result.stdout + result.stderr)
        raise SystemExit(f"dotnet test --list-tests failed with exit code {result.returncode}")
    tests = [line[4:] for line in result.stdout.splitlines() if line.startswith("    ") and line.strip()]
    return collections.Counter(tests)


def main():
    shards = shard_config.load()
    everything = listed()
    problems = []
    classes = {name.split("(")[0].rsplit(".", 1)[0].rsplit(".", 1)[-1] for name in everything}
    for shard in shards[:-1]:
        for name in shard["classes"]:
            if name not in classes:
                problems.append(f"shard {shard['name']} lists {name}, which is not a test class")

    owners = collections.defaultdict(list)
    for shard in shards:
        members = listed(shard_config.filter_for(shards, shard["name"]))
        print(f"{shard['name']}: {sum(members.values())} tests")
        if not members:
            problems.append(f"shard {shard['name']} runs no test")
        for test, count in members.items():
            owners[test].append((shard["name"], count))

    # The tests of the nightly run only (the trait Category=Nightly) are the ones a pull request and a push to main leave out, and
    # nothing else: every shard's filter without them lists the shard's tests less those, and there are some, or the exclusion is idle.
    nightly = listed(shard_config.NIGHTLY_TRAIT)
    if not nightly:
        problems.append("no test carries the trait Category=Nightly, so the exclusion of the pull request run excludes nothing")
    skipped = collections.Counter()
    for shard in shards:
        skipped.update(listed(shard_config.filter_for(shards, shard["name"], include_nightly=False)))
    for test in everything:
        if (skipped[test] != 0) == (test in nightly):
            problems.append(f"{test} {'is' if test in nightly else 'is not'} a test of the nightly run only, and the shards without those tests run it {skipped[test]} times")
    print(f"{sum(nightly.values())} tests of the nightly run only, {sum(skipped.values())} in the shards of a pull request")

    for test, count in everything.items():
        found = owners.get(test, [])
        if len(found) == 0:
            problems.append(f"no shard runs {test}")
        elif len(found) > 1:
            problems.append(f"{test} is in shards {', '.join(n for n, _ in found)}")
        elif found[0][1] != count:
            problems.append(f"{test} is listed {count} times overall and {found[0][1]} times in {found[0][0]}")
    extra = set(owners) - set(everything)
    problems += [f"a shard lists {test}, which the full list does not" for test in sorted(extra)]

    for problem in problems[:50]:
        print(f"::error::{problem}")
    total = sum(everything.values())
    print(f"{total} tests in {len(shards)} shards, {len(problems)} problems.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
