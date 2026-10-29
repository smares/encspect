#!/usr/bin/env python3
"""Tests of the pull request plan of the sources workflow and of check_sources.py's list:
python3 scripts/ci/test_sources_plan.py"""

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import check_sources  # noqa: E402
import sources_plan  # noqa: E402


def entry(name, digest="a" * 64, address="https://example.invalid/a"):
    return {"name": name, "sha256": digest, "address": address}


class PlanTests(unittest.TestCase):
    base = [entry("a"), entry("b"), entry("c", address="https://example.invalid/c")]

    def test_identical_lists_check_nothing(self):
        reason, entries = sources_plan.plan(self.base, list(self.base), {"scripts/fixtures/xing_tool.py"})
        self.assertEqual(entries, [])
        self.assertIn("0 of 3", reason)

    def test_an_edited_address_is_the_only_entry_checked(self):
        head = [entry("a"), entry("b", address="https://example.invalid/moved"), self.base[2]]
        _, entries = sources_plan.plan(self.base, head, set())
        self.assertEqual(entries, [head[1]])

    def test_changed_digest_added_and_removed_entries(self):
        head = [entry("a", digest="b" * 64), self.base[1], entry("new")]
        _, entries = sources_plan.plan(self.base, head, set())
        self.assertEqual(entries, [head[0], head[2]])

    def test_an_added_copy_of_a_known_file_is_checked_alone(self):
        head = self.base + [entry("a", address="https://example.invalid/copy")]
        _, entries = sources_plan.plan(self.base, head, set())
        self.assertEqual(entries, [head[3]])

    def test_the_files_the_check_runs_on_make_it_full(self):
        for path in sources_plan.CHECK_FILES:
            reason, entries = sources_plan.plan(self.base, self.base, {"README.md", path})
            self.assertIsNone(entries, path)
            self.assertIn(path, reason)

    def test_other_files_do_not(self):
        for path in ("scripts/fixtures/monkeys_audio_tool.py", "scripts/fixtures/faac_tool.py", "tests/conformance/eac3-samples.json"):
            self.assertIsNotNone(sources_plan.plan(self.base, self.base, {path})[1], path)


class CommandLineTests(unittest.TestCase):
    script = pathlib.Path(__file__).resolve().parent / "sources_plan.py"

    def run_script(self, *args):
        return subprocess.run([sys.executable, "-I", str(self.script), *args], capture_output=True, text=True, check=True).stdout

    def test_files_form_prints_a_reason_only_for_a_full_check(self):
        with tempfile.TemporaryDirectory() as temp:
            changed = pathlib.Path(temp) / "changed.txt"
            changed.write_text("scripts/fixtures/xing_tool.py\n.github/workflows/sources.yml\n")
            self.assertIn(".github/workflows/sources.yml", self.run_script("--files", str(changed)))
            changed.write_text("scripts/fixtures/xing_tool.py\n")
            self.assertEqual(self.run_script("--files", str(changed)), "")

    def test_files_are_written_for_only_and_omitted_for_a_full_check(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = pathlib.Path(temp)
            (temp / "base.json").write_text(json.dumps([entry("a"), entry("b")]))
            (temp / "head.json").write_text(json.dumps([entry("a"), entry("b", address="https://example.invalid/b2")]))
            (temp / "changed.txt").write_text("scripts/fixtures/xing_tool.py\n")
            out = temp / "only.json"
            self.assertIn("1 of 2", self.run_script(*(str(temp / n) for n in ("base.json", "head.json", "changed.txt")), str(out)))
            self.assertEqual(json.loads(out.read_text()), [entry("b", address="https://example.invalid/b2")])
            out.unlink()
            (temp / "changed.txt").write_text("scripts/fixtures/downloads.py\n")
            self.assertIn("full check", self.run_script(*(str(temp / n) for n in ("base.json", "head.json", "changed.txt")), str(out)))
            self.assertFalse(out.exists())


class ListTests(unittest.TestCase):
    def test_list_is_sorted_unique_and_holds_svn_trees_and_faac_tags(self):
        listed = check_sources.listing()
        keys = [sources_plan.key(item) for item in listed]
        self.assertEqual(keys, sorted(set(keys)))
        self.assertTrue(all(set(item) == {"name", "sha256", "address"} for item in listed))
        addresses = [item["address"] for item in listed]
        for version in check_sources.old_lame_tool.SVN_TREES:
            self.assertIn(check_sources.old_lame_tool.svn_url(version), addresses)
        for tag, commit, _, _ in check_sources.faac_tool.RELEASES.values():
            for repository in check_sources.faac_tool.REPOSITORIES:
                self.assertIn({"name": tag, "sha256": f"commit:{commit}", "address": f"{repository} refs/tags/{tag}"}, listed)

    def test_every_address_of_sources_is_listed(self):
        wanted = {(name, digest, address) for name, digest, addresses in check_sources.sources() for address in addresses}
        self.assertLessEqual(wanted, {sources_plan.key(item) for item in check_sources.listing()})

    def test_a_changed_svn_tree_digest_or_faac_commit_counts_as_changed(self):
        before = check_sources.listing()
        version = next(iter(check_sources.old_lame_tool.SVN_TREES))
        tag, digest = check_sources.old_lame_tool.SVN_TREES[version]
        check_sources.old_lame_tool.SVN_TREES[version] = (tag, "0" * 64)
        try:
            changed = sources_plan.added_or_changed(before, check_sources.listing())
        finally:
            check_sources.old_lame_tool.SVN_TREES[version] = (tag, digest)
        self.assertEqual([item["name"] for item in changed], [tag])

        faac = check_sources.faac_tool
        release = next(iter(faac.RELEASES))
        original = faac.RELEASES[release]
        faac.RELEASES[release] = (original[0], "0" * 40) + original[2:]
        try:
            changed = sources_plan.added_or_changed(before, check_sources.listing())
        finally:
            faac.RELEASES[release] = original
        self.assertEqual(len(changed), len(faac.REPOSITORIES))

    def test_only_selects_exactly_the_given_entries(self):
        everything = check_sources.listing()
        one = everything[7]
        self.assertEqual(check_sources.listing({sources_plan.key(one)}), [one])


if __name__ == "__main__":
    unittest.main()
