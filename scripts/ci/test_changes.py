#!/usr/bin/env python3
"""Tests of changes.py against the repository's own scripts: python3 scripts/ci/test_changes.py"""

import pathlib
import sys
import tempfile
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
        # The MPEG, MP4 and AC-3 jobs run the analyzer over downloaded samples, so any parser change runs them.
        self.assertEqual(run("src/Encspect.Formats.Ogg/OggParser.cs"),
                         set(changes.CI_JOBS_DOTNET) | {"ogg-crosscheck", "mpeg-crosscheck", "mp4-crosscheck", "ac3-crosscheck", "dts-crosscheck"})

    def test_codec_parser_of_a_container_runs_the_container_cross_checks(self):
        got = run("src/Encspect.Formats.Dts/DtsParser.cs")
        self.assertIn("dts-crosscheck", got)
        self.assertNotIn("wavpack-crosscheck", got)

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

    def test_imports_with_several_names_are_followed(self):
        self.assertEqual(changes.imported_modules("import os, a_tool as t, b.c\nfrom d_tool import x\n  import e_tool  # why\n"),
                         {"os", "a_tool", "b", "d_tool", "e_tool"})

    def test_a_helper_that_no_longer_exists_stays_in_the_closure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "scripts" / "fixtures").mkdir(parents=True)
            (root / "scripts" / "fixtures" / "crosscheck_mpeg_fixtures.py").write_text("import encspot_tool, other\n")
            self.assertIn("encspot_tool.py", changes.import_closure(["crosscheck_mpeg_fixtures.py"], root))
            flags, _ = changes.decide("pull_request", ["scripts/fixtures/encspot_tool.py"], root)
            self.assertTrue(flags["mpeg-crosscheck"])

    def test_a_deletion_or_rename_under_scripts_runs_everything(self):
        flags, _ = changes.decide("pull_request", ["scripts/fixtures/encspot_tool.py"], removed=["scripts/fixtures/encspot_tool.py"])
        self.assertTrue(all(flags.values()))
        files, removed = changes.parse_name_status("M\tdocs/a.md\nR100\tscripts/fixtures/old_tool.py\tscripts/fixtures/new_tool.py\nD\tsrc/x.cs\n")
        self.assertEqual(files, ["docs/a.md", "scripts/fixtures/old_tool.py", "scripts/fixtures/new_tool.py", "src/x.cs"])
        self.assertEqual(removed, ["scripts/fixtures/old_tool.py", "src/x.cs"])
        flags, _ = changes.decide("pull_request", files, removed=removed)
        self.assertTrue(all(flags.values()))

    def test_a_change_to_what_the_fixtures_job_reads_runs_it(self):
        self.assertIn("fixtures", run("src/Encspect.Formats.Aac/HuffmanData.cs"))

    def test_unknown_paths_run_everything(self):
        for path in ("scripts/fixtures/some.patch", "scripts/fixtures/data/table.json", "scripts/fixtures/sub/tool.py", "src/Directory.Build.props",
                     "src/Encspect.NewShared/Thing.cs", "tests/NewThing/a.cs", "tests/stray.txt", "tests/fixtures/stray.bin"):
            self.assertEqual(run(path), set(changes.ALL_JOBS), path)

    def test_known_non_python_files_stay_with_their_area(self):
        self.assertIn("dts-crosscheck", run("scripts/fixtures/dca_frames.c"))
        self.assertNotEqual(run("scripts/fixtures/windows/x.bin"), set(changes.ALL_JOBS))

    def test_apple_fixtures_and_their_macos_scripts_are_known(self):
        self.assertEqual(run("tests/fixtures/apple/apple-passthru-m4a-from-faac-m4a.m4a"),
                         set(changes.CI_JOBS_DOTNET) | {"fixtures", "mp4-crosscheck"})
        self.assertIn("mp4-crosscheck", run("tests/fixtures/alac/apple-alac-st44.m4a"))
        self.assertEqual(run("scripts/fixtures/macos/make_macos_aac_fixtures.sh"), {"fixtures"})
        self.assertEqual(run("scripts/fixtures/macos/macos_aac_support.py"), {"fixtures"})
        self.assertEqual(run("scripts/fixtures/macos/window_signals.py"), {"fixtures"})
        self.assertEqual(run("scripts/fixtures/window_shapes.py"), {"fixtures"})

    def test_the_ac3_fixture_models_belong_to_the_ac3_job(self):
        self.assertEqual(run("scripts/fixtures/emdf_model.py"), {"fixtures", "ac3-crosscheck"})
        self.assertEqual(run("scripts/fixtures/make_ac3_fixtures.py"), {"fixtures", "ac3-crosscheck"})
        self.assertEqual(run("scripts/fixtures/crosscheck_ac3_fixtures.py"), {"fixtures", "ac3-crosscheck"})
        self.assertIn("ac3-crosscheck", run("tests/fixtures/ac3/joc-atmos.eac3"))
        manifest = run("tests/conformance/eac3-samples.json")
        self.assertIn("ac3-crosscheck", manifest)
        self.assertFalse(manifest & {"mpeg-crosscheck", "mp4-crosscheck", "dts-crosscheck"})
        self.assertEqual(run("scripts/fixtures/check_eac3_samples.py"), {"fixtures", "ac3-crosscheck"})

    def test_the_dts_scripts_manifests_and_samples_belong_to_the_dts_job(self):
        for path in ("scripts/fixtures/dts_model.py", "scripts/fixtures/dts_lossless_fixtures.py", "scripts/fixtures/make_dts_fixtures.py", "scripts/fixtures/crosscheck_dts_fixtures.py",
                     "scripts/fixtures/check_dts_hd_samples.py", "scripts/fixtures/dts_specs.py"):
            self.assertEqual(run(path), {"fixtures", "dts-crosscheck"}, path)
        manifest = run("tests/conformance/dts-hd-samples.json")
        self.assertIn("dts-crosscheck", manifest)
        self.assertFalse(manifest & {"mpeg-crosscheck", "mp4-crosscheck", "ac3-crosscheck"})
        self.assertIn("dts-crosscheck", run("tests/fixtures/dts/dtshd-all-chunks.dtshd"))
        self.assertIn("dts-crosscheck", run("src/Encspect.Formats.DtsHd/DtsHdParser.cs"))

    def test_the_wma_voice_scripts_manifests_and_samples_belong_to_the_asf_job(self):
        for path in ("scripts/fixtures/voice_model.py", "scripts/fixtures/make_voice_fixtures.py", "scripts/fixtures/check_wmavoice_samples.py",
                     "scripts/fixtures/wma_tables.py", "scripts/fixtures/crosscheck_asf_fixtures.py"):
            self.assertIn("asf-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/check_wmavoice_samples.py"), {"fixtures", "asf-crosscheck"})
        self.assertEqual(run("scripts/fixtures/voice_model.py"), {"fixtures", "asf-crosscheck"})
        manifest = run("tests/conformance/wmavoice-samples.json")
        self.assertIn("asf-crosscheck", manifest)
        self.assertFalse(manifest & {"mpeg-crosscheck", "mp4-crosscheck", "ac3-crosscheck", "dts-crosscheck"})
        self.assertIn("asf-crosscheck", run("tests/fixtures/asf/voice-spillover.asf"))
        self.assertIn("asf-crosscheck", run("src/Encspect.Formats.Wma/VoiceStream.cs"))

    def test_a_parser_change_runs_the_ac3_samples_too(self):
        for path in ("src/Encspect.Formats.Aac/AacCodecParser.cs", "src/Encspect.Tags.Ape/ApeTagParser.cs"):
            self.assertIn("ac3-crosscheck", run(path), path)

    def test_a_parser_change_runs_the_dts_samples_too(self):
        for path in ("src/Encspect.Formats.Aac/AacCodecParser.cs", "src/Encspect.Formats.Ac3/Ac3Parser.cs", "src/Encspect.Tags.Ape/ApeTagParser.cs"):
            self.assertIn("dts-crosscheck", run(path), path)

    def test_platforms(self):
        self.assertEqual(len(changes.platforms("pull_request")), 4)
        self.assertEqual(len(changes.platforms("push")), 6)
        self.assertEqual(len(changes.platforms("schedule")), 6)


if __name__ == "__main__":
    unittest.main()
