#!/usr/bin/env python3
"""Tests of golden_additions.py: python3 scripts/ci/test_golden_additions.py"""

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import golden_additions  # noqa: E402


def node(name, children=None, **fields):
    result = {"name": name, **fields}
    if children:
        result["children"] = children
    return result


def report(statistics, findings=None, streams=None):
    return {
        "schemaVersion": 1,
        "root": node("File", [node("Header"), node("MPEG Audio", [node("Frame"), node("Statistics", statistics)])]),
        "streams": streams if streams is not None else [{"nodes": [[1]], "codec": "MPEG-1 Layer III", "series": [{"name": "Bitrate"}]}],
        "findings": findings if findings is not None else [],
    }


OLD_STATS = [node("Frames", value=10), node("Bandwidth", value=16000), node("Stereo Coding", value=3), node("Long Blocks", value=7)]


class CheckTests(unittest.TestCase):
    def check(self, old, new, change):
        return golden_additions.check_golden(old, new, change)

    def test_an_unchanged_golden_passes(self):
        self.assertEqual(self.check(report(OLD_STATS), report(OLD_STATS), {}), [])

    def test_a_change_nobody_listed_fails(self):
        new = report([node("Frames", value=11), *OLD_STATS[1:]])
        problems = self.check(report(OLD_STATS), new, {})
        self.assertEqual(len(problems), 1)
        self.assertIn("10 in the old report, 11 in the new", problems[0])

    def test_added_keys_array_elements_and_nodes_are_taken_back(self):
        new = report([node("Frames", value=10), node("Coded Bandwidth", value=1), *OLD_STATS[1:]])
        new["streams"][0]["categorySeries"] = [{"name": "Block Usage"}]
        new["streams"][0]["series"].append({"name": "Coded Bandwidth"})
        change = {"added": [
            {"scope": "stream", "key": "categorySeries"},
            {"scope": "stream", "array": "series", "name": "Coded Bandwidth"},
            {"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Coded Bandwidth"},
        ]}
        self.assertEqual(self.check(report(OLD_STATS), new, change), [])
        self.assertTrue(self.check(report(OLD_STATS), new, {"added": change["added"][:1]}))

    def test_an_added_node_shifts_the_paths_of_findings_and_streams(self):
        old = report(OLD_STATS, findings=[{"code": "x", "path": [1, 1, 3]}])
        new = report([node("Frames", value=10), node("Added"), *OLD_STATS[1:]], findings=[{"code": "x", "path": [1, 1, 4]}])
        change = {"added": [{"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Added"}]}
        self.assertEqual(self.check(old, new, change), [])
        wrong = report([node("Frames", value=10), node("Added"), *OLD_STATS[1:]], findings=[{"code": "x", "path": [1, 1, 3]}])
        self.assertTrue(self.check(old, wrong, change))

        # An node inserted in front of a stream's node moves the stream's path.
        old = report(OLD_STATS)
        new = report(OLD_STATS, streams=[{"nodes": [[2]], "codec": "MPEG-1 Layer III", "series": [{"name": "Bitrate"}]}])
        new["root"]["children"].insert(0, node("Extra"))
        self.assertEqual(self.check(old, new, {"added": [{"scope": "node", "parent": [], "name": "Extra"}]}), [])

    def test_a_finding_inside_an_added_node_fails(self):
        new = report([*OLD_STATS, node("Added", [node("Child")])], findings=[{"code": "x", "path": [1, 1, 4, 0]}])
        change = {"added": [{"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Added"}]}
        self.assertTrue(self.check(report(OLD_STATS), new, change))

    def test_a_rename_is_reversed(self):
        new = report([OLD_STATS[0], node("Coded Bandwidth", value=16000), *OLD_STATS[2:]])
        change = {"renamed": [{"parent": ["MPEG Audio", "Statistics"], "from": "Bandwidth", "to": "Coded Bandwidth"}]}
        self.assertEqual(self.check(report(OLD_STATS), new, change), [])
        self.assertTrue(self.check(report(OLD_STATS), new, {}))

    def test_a_move_is_reversed_with_the_path_shift(self):
        old = report(OLD_STATS, findings=[{"code": "x", "path": [1, 1, 3]}])
        new = report([*OLD_STATS[:3], node("Block Sizes", [node("Long Blocks", value=7)])], findings=[{"code": "x", "path": [1, 1, 3, 0]}])
        change = {"moved": [{"name": "Long Blocks", "from": ["MPEG Audio", "Statistics"], "fromPosition": 3,
                             "to": ["MPEG Audio", "Statistics", "Block Sizes"], "position": 0}],
                  "added": [{"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Block Sizes"}]}
        # The moved node goes back before its new parent is taken away as added.
        self.assertEqual(self.check(old, new, change), [])

    def test_a_listed_field_may_differ_and_no_other(self):
        new = report([node("Frames", value=10), node("Bandwidth", value=17000), *OLD_STATS[2:]])
        change = {"changed": [{"path": ["MPEG Audio", "Statistics", "Bandwidth"], "fields": ["value"]}]}
        self.assertEqual(self.check(report(OLD_STATS), new, change), [])
        other = report([node("Frames", value=11), node("Bandwidth", value=17000), *OLD_STATS[2:]])
        self.assertTrue(self.check(report(OLD_STATS), other, change))

    def test_covers_matches_files_and_folders(self):
        change = {"goldens": ["tests/fixtures/mpeg/", "tests/fixtures/mp4/a.report.json"]}
        self.assertTrue(golden_additions.covers(change, "tests/fixtures/mpeg/x.report.json"))
        self.assertTrue(golden_additions.covers(change, "tests/fixtures/mp4/a.report.json"))
        self.assertFalse(golden_additions.covers(change, "tests/fixtures/mp4/b.report.json"))
        self.assertFalse(golden_additions.covers(change, "tests/fixtures/mpeg2/x.report.json"))


class RepositoryTests(unittest.TestCase):
    """The whole script on a small repository: a base commit, then a pull request branch."""

    def git(self, *args):
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@t", *args], cwd=self.dir, check=True, capture_output=True)

    def write(self, path, data):
        target = pathlib.Path(self.dir, path)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(data, indent=2) + "\n")

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = self._tmp.name
        self.git("init", "-q", "-b", "main")
        self.write("tests/fixtures/mpeg/a.report.json", report(OLD_STATS))
        self.write("tests/fixtures/mpeg/b.report.json", report(OLD_STATS))
        self.git("add", "-A")
        self.git("commit", "-qm", "base")
        self.git("checkout", "-qb", "pr")

    def tearDown(self):
        self._tmp.cleanup()

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "-qm", "pr")

    def run_check(self, *files):
        return golden_additions.run(self.dir, "main", "HEAD", list(files))

    def test_without_a_change_file_the_check_is_not_asked_for(self):
        self.write("tests/fixtures/mpeg/a.report.json", report([node("Frames", value=99)]))
        self.commit()
        self.assertEqual(self.run_check(), 0)

    def test_a_covered_addition_passes_and_an_uncovered_change_fails(self):
        added = report([*OLD_STATS, node("Added", value=1)])
        self.write("tests/fixtures/mpeg/a.report.json", added)
        self.write("tests/fixtures/mpeg/golden-changes.json", {
            "goldens": ["tests/fixtures/mpeg/a.report.json"],
            "added": [{"scope": "node", "parent": ["MPEG Audio", "Statistics"], "name": "Added"}]})
        self.commit()
        self.assertEqual(self.run_check(), 0)

        self.write("tests/fixtures/mpeg/b.report.json", report([node("Frames", value=99)]))
        self.commit()
        self.assertEqual(self.run_check(), 1)

    def test_a_change_the_file_does_not_list_fails(self):
        self.write("tests/fixtures/mpeg/a.report.json", report([node("Frames", value=99), *OLD_STATS[1:]]))
        self.write("tests/fixtures/mpeg/golden-changes.json", {"goldens": ["tests/fixtures/mpeg/"], "added": []})
        self.commit()
        self.assertEqual(self.run_check(), 1)


if __name__ == "__main__":
    unittest.main()
