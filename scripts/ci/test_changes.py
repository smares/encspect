#!/usr/bin/env python3
"""Tests of changes.py against the repository's own scripts: python3 scripts/ci/test_changes.py"""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import changes  # noqa: E402


def run(*files, event="pull_request"):
    flags, _ = changes.decide(event, list(files))
    return {job for job, on in flags.items() if on}


class ChangesTests(unittest.TestCase):
    def test_docs_only_runs_nothing(self):
        self.assertEqual(run("docs/cli.md", "README.md", "docs/formats/mp4.md", "CLAUDE.md"), set())

    def test_topic_article_is_code(self):
        self.assertEqual(run("src/Encspect.Host/Topics/lame-tag.md"), set(changes.ALL_JOBS))

    def test_other_markdown_in_src_is_code(self):
        self.assertIn("build-test", run("src/Encspect.Formats.Ogg/NOTES.md"))

    def test_other_events_run_everything(self):
        for event in ("push", "schedule", "workflow_dispatch"):
            self.assertEqual(run("docs/cli.md", event=event), set(changes.ALL_JOBS))

    def test_unreadable_change_runs_everything(self):
        flags, _ = changes.decide("pull_request", None)
        self.assertTrue(all(flags.values()))

    def test_parser_runs_its_own_cross_check_and_the_dotnet_jobs(self):
        self.assertEqual(run("src/Encspect.Formats.Ogg/OggParser.cs"), set(changes.CI_JOBS_DOTNET) | {"ogg-crosscheck"})

    def test_codec_parser_of_a_container_runs_the_container_cross_checks(self):
        got = run("src/Encspect.Formats.Dts/DtsParser.cs")
        self.assertIn("dts-crosscheck", got)
        self.assertNotIn("mpeg-crosscheck", got)

    def test_fixture_runs_its_cross_check_and_the_reproducibility_job(self):
        self.assertEqual(run("tests/fixtures/wavpack/a.wv"), set(changes.CI_JOBS_DOTNET) | {"wavpack-crosscheck", "fixtures"})

    def test_cross_check_script_runs_only_its_job(self):
        self.assertEqual(run("scripts/fixtures/crosscheck_asf_fixtures.py"), {"asf-crosscheck", "fixtures"})

    def test_a_module_a_cross_check_imports_runs_it(self):
        got = run("scripts/fixtures/wma_signals.py")
        self.assertIn("asf-crosscheck", got)
        self.assertNotIn("ogg-crosscheck", got)

    def test_shared_modules_run_everything(self):
        for path in ("src/Encspect.Host/Analyzer.cs", "src/Encspect.Core.Abstractions/Node.cs", "src/Encspect.Cli/Program.cs",
                     "scripts/fixtures/downloads.py", "scripts/fixtures/mediainfo_tool.py", ".github/workflows/ci.yml", "scripts/ci/changes.py"):
            self.assertEqual(run(path), set(changes.ALL_JOBS), path)

    def test_gui_change_runs_no_cross_check(self):
        self.assertEqual(run("src/Encspect.Gui/App.cs"), set(changes.CI_JOBS_DOTNET))

    def test_fuzz_inputs_run_only_the_dotnet_jobs(self):
        self.assertEqual(run("tests/fixtures/fuzz/a.bin"), set(changes.CI_JOBS_DOTNET) | {"fixtures"})

    def test_unknown_path_runs_everything(self):
        self.assertEqual(run("src/Encspect.Formats.Flac/FlacParser.cs"), set(changes.ALL_JOBS))
        self.assertEqual(run("somewhere/else.bin"), set(changes.ALL_JOBS))

    def test_encoder_script_reruns_only_the_reproducibility_job(self):
        self.assertEqual(run("scripts/fixtures/faac_tool.py"), {"fixtures"})

    def test_new_check_script_runs_everything(self):
        self.assertEqual(run("scripts/fixtures/crosscheck_flac_fixtures.py"), set(changes.ALL_JOBS))

    def test_every_cross_check_script_belongs_to_an_area(self):
        entries = {e for spec in changes.AREAS.values() for e in spec["scripts"]}
        directory = changes.ROOT / changes.FIXTURE_SCRIPTS
        unowned = [p.name for p in directory.glob("crosscheck_*.py") if p.name not in entries]
        self.assertEqual(unowned, [])

    def test_platforms(self):
        self.assertEqual(len(changes.platforms("pull_request")), 4)
        self.assertEqual(len(changes.platforms("push")), 6)
        self.assertEqual(len(changes.platforms("schedule")), 6)


if __name__ == "__main__":
    unittest.main()
