#!/usr/bin/env python3
"""Tests of the pull request plan of the sources workflow and of check_sources.py's list:
python3 scripts/ci/test_sources_plan.py"""

import contextlib
import io
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import check_sources  # noqa: E402
import sources_plan  # noqa: E402


def entry(name, digest="a" * 64, address="https://example.invalid/a", kind="download"):
    return {"name": name, "sha256": digest, "address": address, "kind": kind}


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

    def test_check_files_are_pinned(self):
        self.assertEqual(sources_plan.CHECK_FILES, (
            "scripts/fixtures/downloads.py",
            "scripts/fixtures/check_sources.py",
            "scripts/ci/sources_plan.py",
            ".github/workflows/sources.yml",
        ))

    def test_the_files_the_check_runs_on_make_it_full(self):
        for path in ("scripts/fixtures/downloads.py", "scripts/fixtures/check_sources.py", "scripts/ci/sources_plan.py", ".github/workflows/sources.yml"):
            self.assertEqual(sources_plan.full_reason({"README.md", path}), path)
        self.assertIsNone(sources_plan.full_reason({"README.md", "scripts/ci/test_sources_plan.py"}))

    kinds = [
        entry("svn-a", kind="svn"), entry("svn-b", kind="svn"),
        entry("tag-a", kind="tag"), entry("tag-b", kind="tag"),
        entry("arc.tar.xz", kind="download"), entry("arc.tar.xz/m1", kind="member"), entry("arc.tar.xz/m2", kind="member"),
        entry("other.zip", kind="download"),
    ]

    def names(self, changed):
        return [e["name"] for e in sources_plan.plan(self.kinds, self.kinds, changed)[1]]

    def test_a_kind_module_checks_all_entries_of_its_kind_only(self):
        self.assertEqual(self.names({"scripts/fixtures/old_lame_tool.py"}), ["svn-a", "svn-b"])
        self.assertEqual(self.names({"scripts/fixtures/faac_tool.py"}), ["tag-a", "tag-b"])
        self.assertEqual(self.names({"scripts/fixtures/ffmpeg_sources.py"}), ["arc.tar.xz", "arc.tar.xz/m1", "arc.tar.xz/m2"])
        self.assertEqual(self.names({"scripts/fixtures/old_lame_tool.py", "scripts/fixtures/faac_tool.py"}), ["svn-a", "svn-b", "tag-a", "tag-b"])
        self.assertEqual(self.names({"scripts/fixtures/xing_tool.py"}), [])

    def test_a_kind_module_adds_to_the_changed_entries(self):
        head = self.kinds + [entry("other.zip", address="https://example.invalid/copy")]
        _, entries = sources_plan.plan(self.kinds, head, {"scripts/fixtures/faac_tool.py"})
        self.assertEqual([e["name"] for e in entries], ["other.zip", "tag-a", "tag-b"])

    def test_a_changed_member_list_counts_as_changed(self):
        head = self.kinds + [entry("arc.tar.xz/m3", kind="member")]
        _, entries = sources_plan.plan(self.kinds, head, set())
        self.assertEqual([e["name"] for e in entries], ["arc.tar.xz/m3"])

    def test_the_planner_forces_the_full_check(self):
        with tempfile.TemporaryDirectory() as temp:
            changed = pathlib.Path(temp) / "changed.txt"
            changed.write_text("scripts/ci/sources_plan.py\n")
            self.assertIn("scripts/ci/sources_plan.py", CommandLineTests().run_script("--files", str(changed)))

    def test_the_workflow_triggers_on_the_check_files_and_every_fixture_script(self):
        workflow = (pathlib.Path(__file__).resolve().parents[2] / ".github" / "workflows" / "sources.yml").read_text(encoding="utf-8")
        head = workflow.split("permissions:")[0]
        paths = [line.strip()[2:] for line in head.split("    paths:\n")[1].splitlines() if line.strip().startswith("- ")]
        self.assertEqual(paths, ["scripts/fixtures/**", "tests/conformance/**", "scripts/ci/sources_plan.py",
                                 "scripts/ci/test_sources_plan.py", ".github/workflows/sources.yml"])

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

    def test_entries_are_written_for_only(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = pathlib.Path(temp)
            (temp / "base.json").write_text(json.dumps([entry("a"), entry("b")]))
            (temp / "head.json").write_text(json.dumps([entry("a"), entry("b", address="https://example.invalid/b2")]))
            (temp / "changed.txt").write_text("scripts/fixtures/xing_tool.py\n")
            out = temp / "only.json"
            self.assertIn("1 of 2", self.run_script(*(str(temp / n) for n in ("base.json", "head.json", "changed.txt")), str(out)))
            self.assertEqual(json.loads(out.read_text()), [entry("b", address="https://example.invalid/b2")])


class ListTests(unittest.TestCase):
    def test_list_is_sorted_unique_and_holds_svn_trees_and_faac_tags(self):
        listed = check_sources.listing()
        keys = [sources_plan.key(item) for item in listed]
        self.assertEqual(keys, sorted(set(keys)))
        self.assertTrue(all(set(item) == {"name", "sha256", "address", "kind"} for item in listed))
        addresses = [item["address"] for item in listed]
        for version in check_sources.old_lame_tool.SVN_TREES:
            self.assertIn(check_sources.old_lame_tool.svn_url(version), addresses)
        for tag, commit, _, _ in check_sources.faac_tool.RELEASES.values():
            for repository in check_sources.faac_tool.REPOSITORIES:
                self.assertIn({"name": tag, "sha256": f"commit:{commit}", "address": f"{repository} refs/tags/{tag}", "kind": "tag"}, listed)

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

    def test_every_ffmpeg_member_is_an_entry_with_the_archives_digest_and_address(self):
        ffmpeg = check_sources.ffmpeg_sources
        listed = {item["name"]: item for item in check_sources.listing() if item["kind"] == "member"}
        self.assertEqual(set(listed), {f"{ffmpeg.ARCHIVE}/{m}" for m in ffmpeg.FILES})
        for item in listed.values():
            self.assertEqual((item["sha256"], item["address"]), (ffmpeg.SHA256, ffmpeg.ADDRESSES[0]))

    def test_a_new_ffmpeg_member_is_a_changed_entry(self):
        ffmpeg = check_sources.ffmpeg_sources
        before = check_sources.listing()
        ffmpeg.FILES.append("libavcodec/new_member.c")
        try:
            changed = sources_plan.added_or_changed(before, check_sources.listing())
        finally:
            ffmpeg.FILES.pop()
        self.assertEqual([item["name"] for item in changed], [f"{ffmpeg.ARCHIVE}/libavcodec/new_member.c"])

    def test_only_selects_exactly_the_given_entries(self):
        everything = check_sources.listing()
        one = everything[7]
        self.assertEqual(check_sources.listing({sources_plan.key(one)}), [one])


class MainTests(unittest.TestCase):
    """check_sources.main() with --only, the network stubbed: exactly the listed entries are checked."""

    def run_main(self, wanted, missing=()):
        calls = {"download": [], "tree": [], "tag": [], "member": []}
        listing = {sources_plan.key(item): item for item in check_sources.listing()}
        digests = {item["address"]: item["sha256"] for item in listing.values() if item["kind"] in ("download", "member")}

        def download(address, path):
            calls["download"].append(address)
            return None

        def fetch_tree(url, directory):
            calls["tree"].append(url)

        def tag_problems(only=None):
            calls["tag"].extend(sorted(only))
            return []

        def members_missing(path, names=None):
            calls["member"].extend(names)
            return list(missing)

        with tempfile.TemporaryDirectory() as temp:
            only = pathlib.Path(temp) / "only.json"
            only.write_text(json.dumps(wanted))
            patches = [
                mock.patch.object(check_sources.downloads, "download", download),
                mock.patch.object(check_sources.downloads, "has_token", lambda address: True),
                mock.patch.object(check_sources.downloads, "sha256", lambda path: self.current),
                mock.patch.object(check_sources.old_lame_tool, "fetch_tree", fetch_tree),
                mock.patch.object(check_sources.old_lame_tool, "tree_digest", lambda tree: self.tree),
                mock.patch.object(check_sources.faac_tool, "tag_problems", tag_problems),
                mock.patch.object(check_sources.ffmpeg_sources, "missing", members_missing),
                mock.patch.object(check_sources.time, "sleep", lambda seconds: None),
                mock.patch.object(sys, "argv", ["check_sources.py", "--only", str(only)]),
            ]
            out, err = io.StringIO(), io.StringIO()
            with contextlib.ExitStack() as stack:
                for patch in patches:
                    stack.enter_context(patch)
                stack.enter_context(contextlib.redirect_stdout(out))
                stack.enter_context(contextlib.redirect_stderr(err))
                with self.assertRaises(SystemExit) as raised:
                    check_sources.main()
        return calls, raised.exception.code, out.getvalue(), err.getvalue()

    current = ""
    tree = ""

    def setUp(self):
        self.listing = check_sources.listing()
        self.by_kind = {}
        for item in self.listing:
            self.by_kind.setdefault(item["kind"], []).append(item)
        # A download whose digest matches whatever the stub serves.
        self.download = self.by_kind["download"][3]
        self.current = self.download["sha256"]

    def test_one_download_is_the_only_thing_downloaded(self):
        calls, code, _, err = self.run_main([self.download])
        self.assertEqual(calls, {"download": [self.download["address"]], "tree": [], "tag": [], "member": []})
        self.assertEqual(code, 0)
        self.assertIn("Checked 1 entries, 0 problems", err)

    def test_one_svn_tree_is_the_only_thing_fetched(self):
        svn = self.by_kind["svn"][0]
        self.tree = svn["sha256"]
        calls, code, _, _ = self.run_main([svn])
        self.assertEqual(calls, {"download": [], "tree": [svn["address"]], "tag": [], "member": []})
        self.assertEqual(code, 0)

    def test_one_faac_tag_is_the_only_tag_asked_for(self):
        tag = self.by_kind["tag"][0]
        repository, _, ref = tag["address"].partition(" refs/tags/")
        calls, code, _, _ = self.run_main([tag])
        self.assertEqual(calls, {"download": [], "tree": [], "tag": [(tag["name"], repository)], "member": []})
        self.assertEqual(code, 0)

    def test_one_ffmpeg_member_downloads_the_archive_and_checks_that_member_alone(self):
        member = self.by_kind["member"][5]
        self.current = member["sha256"]
        calls, code, out, _ = self.run_main([member], missing=[member["name"].partition("/")[2]])
        self.assertEqual(calls["download"], [member["address"]])
        self.assertEqual(calls["member"], [member["name"].partition("/")[2]])
        self.assertEqual((calls["tree"], calls["tag"]), ([], []))
        self.assertEqual(code, 1)
        self.assertIn("does not hold", out)

    def test_a_wrong_digest_is_a_problem(self):
        self.current = "0" * 64
        _, code, out, _ = self.run_main([self.download])
        self.assertEqual(code, 1)
        self.assertIn("serves SHA-256", out)

    def test_an_unknown_entry_is_refused_before_anything_is_checked(self):
        unknown = dict(self.download, address="https://example.invalid/unknown")
        calls, code, _, _ = self.run_main([self.download, unknown])
        self.assertIn("--list does not hold", str(code))
        self.assertEqual(calls, {"download": [], "tree": [], "tag": [], "member": []})

    def test_a_mix_of_kinds_checks_each_once(self):
        svn, tag = self.by_kind["svn"][1], self.by_kind["tag"][1]
        self.tree = svn["sha256"]
        calls, code, _, _ = self.run_main([self.download, svn, tag])
        self.assertEqual(calls["download"], [self.download["address"]])
        self.assertEqual(calls["tree"], [svn["address"]])
        self.assertEqual(len(calls["tag"]), 1)
        self.assertEqual(code, 0)


class TagProblemTests(unittest.TestCase):
    def test_only_the_given_tags_and_repositories_are_asked_for(self):
        faac = check_sources.faac_tool
        tag, commit, _, _ = next(iter(faac.RELEASES.values()))
        repository = faac.REPOSITORIES[0]
        asked = []

        def run(command, **kwargs):
            asked.append((command[2], command[3]))
            return subprocess.CompletedProcess(command, 0, stdout=f"{commit}\trefs/tags/{tag}\n", stderr="")

        with mock.patch.object(faac.subprocess, "run", run):
            self.assertEqual(faac.tag_problems({(tag, repository)}), [])
            self.assertEqual(asked, [(repository, f"refs/tags/{tag}")])
            asked.clear()
            self.assertEqual(faac.tag_problems(set()), [])
            self.assertEqual(asked, [])
            faac.tag_problems()
            self.assertEqual(len(asked), len(faac.RELEASES) * len(faac.REPOSITORIES))


if __name__ == "__main__":
    unittest.main()
