#!/usr/bin/env python3
"""Tests of changes.py against the repository's own scripts: python3 scripts/ci/test_changes.py"""

import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import changes  # noqa: E402
import scope  # noqa: E402


def run(*files, event="pull_request"):
    flags, _ = changes.decide(event, list(files))
    return {job for job, on in flags.items() if on}


class ChangesTests(unittest.TestCase):
    def test_docs_only_runs_nothing(self):
        self.assertEqual(run("docs/cli.md", "docs/formats/mp4.md", "docs/gui.md"), set())

    def test_topic_article_runs_only_the_build_and_the_unit_tests(self):
        self.assertEqual(run("src/Encspect.Host/Topics/lame-tag.md"), {"build-test", "unit-tests"})
        self.assertEqual(run("src/Encspect.Host/Topics/lame-tag.md", "src/Encspect.Host/Analyzer.cs"), set(changes.ALL_JOBS))
        self.assertEqual(run("src/Encspect.Host/Topics/lame-tag.md", "docs/cli.md"), {"build-test", "unit-tests"})
        self.assertIn("unit-tests", run("src/Encspect.Host/Topics/lame-tag.md", "src/Encspect.Formats.WavPack/WavPackParser.cs"))

    def test_conformance_manifests_are_no_dotnet_input(self):
        got = run("tests/conformance/truehd-samples.json")
        self.assertFalse(got & set(changes.CI_JOBS_DOTNET), got)
        self.assertIn("truehd-crosscheck", got)
        self.assertIn("unit-tests", run("tests/conformance/truehd-samples.json", "src/Encspect.Formats.TrueHd/TrueHdParser.cs"))

    def test_named_paths_are_found_written_either_way(self):
        found = named_paths('File.ReadAllText(Path.Combine(Root, "docs", "cli.md")); File.Exists(Path.Combine(Root, "LICENSE")); var a = "scripts/release/make_notices.py"; '
                            'var b = "README.md"; Path.Combine(root, "scripts", "release", "dotnet-pin.txt"); Path.Combine(Root, "docs", "releases", version + ".md"); '
                            'Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.md"); "tests\\conformance\\a.json"', {"LICENSE", "README.md", "THIRD-PARTY-NOTICES.md"})
        self.assertLessEqual({"docs/cli.md", "LICENSE", "scripts/release/make_notices.py", "README.md", "scripts/release/dotnet-pin.txt", "docs/releases",
                              "THIRD-PARTY-NOTICES.md", "tests/conformance/a.json"}, found)

    def test_the_probes_of_the_review_are_covered_or_caught(self):
        def covered(path):
            return bool(changes.is_dotnet(path) or changes.reader_of(path) or changes.matches(path, changes.SHARED))
        for path in ("LICENSE", "README.md", "CLAUDE.md", "THIRD-PARTY-NOTICES.md", "scripts/release/dotnet-pin.txt", "docs/development.md", "docs/releases/0.4.0.md", "src/Encspect.Host/Topics/a.md"):
            self.assertTrue(covered(path), path)
        for path in ("docs/cli.md", "scripts/release/make_notices.py", "tests/conformance/a.json", "scripts/fixtures/make_x.py", "docs/formats/wavpack.md"):
            self.assertFalse(covered(path), path)
        self.assertEqual(run("LICENSE"), {"unit-tests"})
        self.assertEqual(run("README.md"), {"unit-tests"})
        self.assertEqual(run("newfile.txt"), set(changes.ALL_JOBS), "a new top-level file runs everything, RepositoryRootTests among it")

    @unittest.skipUnless((changes.ROOT / "src").is_dir() and (changes.ROOT / "tests/Encspect.Tests").is_dir(), "needs src and tests/Encspect.Tests")
    def test_every_non_dotnet_file_a_dotnet_test_reads_is_registered_as_read(self):
        """A .NET test or project that names a path changes.py does not count as .NET input (written as a path or as the pieces of a
        Path.Combine) must find it in READERS, or a change to it runs no test; tests/conformance is read by none."""
        root_files = {f.name for f in changes.ROOT.iterdir() if f.is_file()}
        uncovered = {}
        for project in ("src", "tests/Encspect.Tests", "tests/Encspect.Gui.Tests", "tests/Encspect.Fuzz", "tests/Encspect.PcmDump"):
            for file in (changes.ROOT / project).rglob("*"):
                if file.suffix not in (".cs", ".csproj", ".props", ".targets", ".axaml") or {"bin", "obj"} & set(file.parts):
                    continue
                for path in named_paths(file.read_text(encoding="utf-8", errors="replace"), root_files):
                    target = changes.ROOT / path
                    if not target.exists() or path == ".git" or "/" not in path and target.is_dir():
                        continue
                    probe = path + "/x.md" if target.is_dir() else path
                    covered = changes.is_dotnet(probe) or changes.reader_of(probe) or changes.matches(probe, changes.SHARED)
                    if not covered and not path.startswith(("tests/fixtures/", "scripts/fixtures/", "tests/conformance")) or path.startswith("tests/conformance"):
                        uncovered.setdefault(path, []).append(file.name)
        self.assertEqual(uncovered, {}, "register these in READERS of changes.py (or, for tests/conformance, stop reading it)")

    def test_the_release_scripts_run_no_job_of_this_workflow(self):
        self.assertEqual(run("scripts/release/make_notices.py", "scripts/release/licenses/libspeex.txt"), set())
        self.assertEqual(run("scripts/release/make_notices.py", "src/Encspect.Formats.WavPack/WavPackParser.cs"), run("src/Encspect.Formats.WavPack/WavPackParser.cs"))

    def test_files_that_tests_read_run_those_tests(self):
        self.assertEqual(run("THIRD-PARTY-NOTICES.md"), {"build-test", "unit-tests"})
        self.assertEqual(run("scripts/release/dotnet-pin.txt"), {"unit-tests"})
        self.assertEqual(run("docs/development.md"), {"unit-tests"})
        self.assertEqual(run("docs/releases/0.4.0.md"), {"unit-tests"})
        self.assertEqual(run("docs/formats/wavpack.md"), set())

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

    def test_flac_parser_decoder_scripts_and_fixtures_belong_to_the_flac_job(self):
        for path in ("src/Encspect.Formats.Flac/FlacParser.cs", "src/Encspect.Formats.Flac/Decoding/FrameDecoder.cs", "tests/fixtures/flac/a.flac",
                     "scripts/fixtures/flac_model.py", "scripts/fixtures/make_flac_fixtures.py"):
            self.assertIn("flac-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_flac_fixtures.py"), {"flac-crosscheck", "fixtures"})

    def test_monkeys_audio_parser_decoder_scripts_and_fixtures_belong_to_the_monkeys_audio_job(self):
        for path in ("src/Encspect.Formats.MonkeysAudio/MonkeysAudioParser.cs", "src/Encspect.Formats.MonkeysAudio/Decoding/RangeDecoder.cs",
                     "tests/fixtures/monkeys-audio/a.ape", "scripts/fixtures/monkeys_audio_model.py", "scripts/fixtures/make_monkeys_audio_fixtures.py",
                     "scripts/fixtures/make_external_monkeys_audio_fixtures.py", "scripts/fixtures/make_legacy_monkeys_audio_fixtures.py",
                     "scripts/fixtures/make_interim_monkeys_audio_fixture.py", "scripts/fixtures/monkeys_audio_legacy.py", "scripts/fixtures/monkeys_audio_old.py",
                     "scripts/fixtures/check_monkeys_audio_samples.py", "tests/conformance/monkeys-audio-samples.json"):
            self.assertIn("monkeys-audio-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_monkeys_audio_fixtures.py"), {"monkeys-audio-crosscheck", "fixtures"})
        self.assertEqual(run("scripts/fixtures/check_monkeys_audio_samples.py"), {"monkeys-audio-crosscheck", "fixtures"})
        self.assertNotIn("monkeys-audio-crosscheck", run("src/Encspect.Formats.Flac/FlacParser.cs"))

    def test_alac_parser_scripts_and_fixtures_belong_to_the_alac_job(self):
        for path in ("src/Encspect.Formats.Alac/AlacCodecParser.cs", "tests/fixtures/alac/a.m4a", "scripts/fixtures/alac_model.py", "scripts/fixtures/make_alac_fixtures.py",
                     "scripts/fixtures/make_external_alac_fixtures.py", "scripts/fixtures/alac_tool.py"):
            self.assertIn("alac-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_alac_fixtures.py"), {"alac-crosscheck", "fixtures"})
        self.assertIn("alac-crosscheck", run("scripts/fixtures/crosscheck_alac_pcm.py"))
        self.assertNotIn("alac-crosscheck", run("src/Encspect.Formats.Flac/FlacParser.cs"))

    def test_tta_parser_scripts_and_fixtures_belong_to_the_tta_job(self):
        for path in ("src/Encspect.Formats.Tta/TtaParser.cs", "tests/fixtures/tta/a.tta", "scripts/fixtures/tta_model.py", "scripts/fixtures/make_tta_fixtures.py",
                     "scripts/fixtures/make_external_tta_fixtures.py"):
            self.assertIn("tta-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_tta_fixtures.py"), {"tta-crosscheck", "fixtures"})
        for path in ("scripts/fixtures/crosscheck_tta_pcm.py", "scripts/fixtures/compare_pcm.py", "scripts/fixtures/libtta_tool.py"):
            self.assertIn("tta-crosscheck", run(path), path)
        self.assertNotIn("tta-crosscheck", run("src/Encspect.Formats.Flac/FlacParser.cs"))

    def test_sample_kit_fixtures_and_script_belong_to_the_sample_kit_job(self):
        for path in ("tests/fixtures/aac/sample-kit/external-qaac-he-cbr64.m4a", "tests/fixtures/mpeg-audio/sample-kit/external-nero-rom-mp3-cbr128.mp3",
                     "scripts/fixtures/import_sample_kit_fixtures.py"):
            self.assertIn("sample-kit-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_sample_kit_fixtures.py"), {"sample-kit-crosscheck", "fixtures"})
        for path in ("tests/fixtures/aac/sample-kit/external-qaac-he-cbr64.report.json", "tests/fixtures/mpeg-audio/sample-kit/external-nero-rom-mp3-cbr128.report.json"):
            self.assertEqual(run(path) & {"mp4-crosscheck", "mpeg-crosscheck", "alac-crosscheck"}, set(), path)
        # The script reads the committed files, manifests and golden reports with ffprobe, ffmpeg and MediaInfo, never the analyzer.
        self.assertNotIn("sample-kit-crosscheck", run("src/Encspect.Formats.Aac/AacParser.cs"))

    def test_optimfrog_parser_scripts_and_fixtures_belong_to_the_optimfrog_job(self):
        for path in ("src/Encspect.Formats.OptimFrog/OptimFrogParser.cs", "tests/fixtures/optimfrog/a.ofr", "scripts/fixtures/optimfrog_model.py",
                     "scripts/fixtures/make_external_optimfrog_fixtures.py", "scripts/fixtures/optimfrog_tool.py"):
            self.assertIn("optimfrog-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_optimfrog_fixtures.py"), {"optimfrog-crosscheck", "fixtures"})
        self.assertNotIn("optimfrog-crosscheck", run("src/Encspect.Formats.Flac/FlacParser.cs"))

    def test_tak_parser_scripts_and_fixtures_belong_to_the_tak_job(self):
        for path in ("src/Encspect.Formats.Tak/TakParser.cs", "tests/fixtures/tak/a.tak", "scripts/fixtures/tak_model.py", "scripts/fixtures/make_tak_fixtures.py",
                     "scripts/fixtures/tak_tool.py", "scripts/fixtures/import_windows_tak_fixtures.py", "scripts/fixtures/windows/make_windows_tak_fixtures.ps1",
                     "scripts/fixtures/windows/TakTools.cs", "tests/conformance/tak-samples.json"):
            self.assertIn("tak-crosscheck", run(path), path)
        self.assertNotIn("tak-crosscheck", run("src/Encspect.Formats.Flac/FlacParser.cs"))

    def test_wav_and_aiff_parsers_scripts_and_fixtures_belong_to_the_wav_job(self):
        for path in ("src/Encspect.Formats.Wav/WavParser.cs", "src/Encspect.Formats.Aiff/AiffParser.cs", "tests/fixtures/wav/a.wav", "tests/fixtures/aiff/a.aiff",
                     "scripts/fixtures/wav_model.py", "scripts/fixtures/aiff_model.py", "scripts/fixtures/pcm_model.py", "scripts/fixtures/make_wav_fixtures.py",
                     "scripts/fixtures/make_external_aiff_fixtures.py"):
            self.assertIn("wav-crosscheck", run(path), path)
        # The measurement cross-check reads the PCM files through the same models.
        for path in ("scripts/fixtures/pcm_model.py", "scripts/fixtures/wav_model.py", "scripts/fixtures/aiff_model.py"):
            self.assertIn("flac-crosscheck", run(path), path)
        self.assertNotIn("wavpack-crosscheck", run("scripts/fixtures/make_wav_fixtures.py"))
        self.assertEqual(run("scripts/fixtures/crosscheck_wav_fixtures.py"), {"wav-crosscheck", "fixtures"})

    def test_cross_check_script_runs_only_its_job(self):
        self.assertEqual(run("scripts/fixtures/crosscheck_asf_fixtures.py"), {"asf-crosscheck", "fixtures"})

    def test_the_matroska_parser_its_codec_parsers_scripts_and_fixtures_belong_to_the_matroska_job(self):
        for path in ("src/Encspect.Formats.Matroska/Tree.cs", "src/Encspect.Formats.Opus/OpusParser.cs", "tests/fixtures/matroska/webm.webm",
                     "scripts/fixtures/matroska_tables.py", "scripts/fixtures/make_matroska_fixtures.py", "scripts/fixtures/matroska_model.py", "scripts/fixtures/matroska_encodings_model.py"):
            self.assertIn("matroska-crosscheck", run(path), path)
        self.assertEqual(run("scripts/fixtures/crosscheck_matroska_fixtures.py"), {"matroska-crosscheck", "fixtures"})
        self.assertNotIn("matroska-crosscheck", run("src/Encspect.Formats.Musepack/MusepackParser.cs"))

    def test_a_module_a_cross_check_imports_runs_it(self):
        got = run("scripts/fixtures/wma_signals.py")
        self.assertIn("asf-crosscheck", got)
        self.assertNotIn("ogg-crosscheck", got)

    def test_the_mp4box_build_runs_the_mp4_job_and_the_version_module_every_cross_check(self):
        self.assertEqual(run("scripts/fixtures/gpac_tool.py"), {"mp4-crosscheck", "fixtures"})
        self.assertEqual(run("scripts/fixtures/tool_versions.py"), set(changes.AREAS) | {"fixtures"})

    def test_shared_modules_run_everything(self):
        for path in ("src/Encspect.Host/Analyzer.cs", "src/Encspect.Core.Abstractions/Node.cs", "src/Encspect.Cli/Program.cs",
                     "scripts/fixtures/downloads.py", "scripts/fixtures/mediainfo_tool.py", ".github/workflows/ci.yml", "scripts/ci/changes.py"):
            self.assertEqual(run(path), set(changes.ALL_JOBS), path)

    def test_gui_change_runs_no_cross_check(self):
        self.assertEqual(run("src/Encspect.Gui/App.cs"), set(changes.CI_JOBS_DOTNET))

    def test_fuzz_inputs_run_only_the_dotnet_jobs(self):
        self.assertEqual(run("tests/fixtures/fuzz/a.bin"), set(changes.CI_JOBS_DOTNET) | {"fixtures"})

    def test_unknown_path_runs_everything(self):
        self.assertEqual(run("src/Encspect.Formats.Vqf/VqfParser.cs"), set(changes.ALL_JOBS))
        self.assertEqual(run("somewhere/else.bin"), set(changes.ALL_JOBS))

    def test_encoder_script_reruns_only_the_reproducibility_job(self):
        self.assertEqual(run("scripts/fixtures/faac_tool.py"), {"fixtures"})

    def test_new_check_script_runs_everything(self):
        self.assertEqual(run("scripts/fixtures/crosscheck_vqf_fixtures.py"), set(changes.ALL_JOBS))

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
        self.assertEqual(run("tests/fixtures/aac/apple/apple-passthru-m4a-from-faac-m4a.m4a"),
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

    def test_the_truehd_parser_decoder_scripts_manifests_and_fixtures_belong_to_the_truehd_job(self):
        for path in ("src/Encspect.Formats.TrueHd/TrueHdParser.cs", "src/Encspect.Formats.TrueHd/Decoding/StreamDecoder.cs", "tests/fixtures/truehd/a.thd",
                     "scripts/fixtures/truehd_model.py", "scripts/fixtures/make_truehd_fixtures.py", "scripts/fixtures/make_external_truehd_fixtures.py"):
            self.assertIn("truehd-crosscheck", run(path), path)
        for path in ("scripts/fixtures/crosscheck_truehd_fixtures.py", "scripts/fixtures/check_truehd_samples.py"):
            self.assertEqual(run(path), {"fixtures", "truehd-crosscheck"}, path)
        manifest = run("tests/conformance/truehd-samples.json")
        self.assertIn("truehd-crosscheck", manifest)
        self.assertFalse(manifest & {"mpeg-crosscheck", "dts-crosscheck", "flac-crosscheck"})

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
        self.assertIn("asf-crosscheck", run("src/Encspect.Formats.Wma/WmaVoiceParser.cs"))

    def test_the_wma_professional_and_lossless_samples_belong_to_the_asf_job(self):
        self.assertEqual(run("scripts/fixtures/check_wma_samples.py"), {"fixtures", "asf-crosscheck"})
        manifest = run("tests/conformance/wma-samples.json")
        self.assertIn("asf-crosscheck", manifest)
        self.assertFalse(manifest & {"mpeg-crosscheck", "mp4-crosscheck", "ac3-crosscheck", "dts-crosscheck"})
        self.assertIn("asf-crosscheck", run("src/Encspect.Formats.Wma/WmaProParser.cs"))
        self.assertIn("asf-crosscheck", run("src/Encspect.Formats.Wma/WmaLosslessParser.cs"))

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
        self.assertEqual(len(changes.platforms("workflow_dispatch")), 6)

    def test_a_pull_request_runs_one_macos_job_and_no_macos_shard(self):
        shards = changes.shard_config.load()
        pr = changes.test_platforms("pull_request")
        self.assertEqual([p["os"] for p in changes.platforms("pull_request") if p["os"].startswith("macos-")], ["macos-latest"])
        self.assertFalse([p for p in pr if p["os"].startswith("macos-")])
        self.assertEqual(len(pr), 3)
        self.assertFalse([e for e in changes.test_matrix(pr, shards, False) if e["os"].startswith("macos-")])
        for event in ("push", "schedule", "workflow_dispatch"):
            self.assertEqual(changes.test_platforms(event), changes.platforms(event), event)
            macos = {e["os"] for e in changes.test_matrix(changes.test_platforms(event), shards, event in changes.NIGHTLY_EVENTS)
                     if e["os"].startswith("macos-")}
            self.assertEqual(macos, {"macos-latest", "macos-15-intel"}, event)

    def test_the_linux_platforms_use_the_pinned_ubuntu_26_04_images(self):
        def labels(event):
            return [p["os"] for p in changes.platforms(event)]
        for event in ("pull_request", "push", "schedule", "workflow_dispatch"):
            self.assertIn("ubuntu-26.04", labels(event), event)
            self.assertFalse([label for label in labels(event) if label.startswith("ubuntu-") and "26.04" not in label], event)
        for event in ("push", "schedule", "workflow_dispatch"):
            self.assertIn("ubuntu-26.04-arm", labels(event), event)
            self.assertEqual(len(set(labels(event))), 6, event)

    def test_only_the_nightly_run_and_a_manual_run_include_the_tests_of_the_nightly_run(self):
        shards = changes.shard_config.load()
        for event in ("pull_request", "push", "schedule", "workflow_dispatch"):
            include = event in changes.NIGHTLY_EVENTS
            for shard in shards:
                expression = changes.shard_config.filter_for(shards, shard["name"], include)
                self.assertEqual("Category!=Nightly" in expression, not include, (event, shard["name"]))
        self.assertEqual(changes.NIGHTLY_EVENTS, ("schedule", "workflow_dispatch"))

    def test_a_shard_of_the_nightly_run_only_runs_in_the_nightly_run_and_a_manual_run_only(self):
        shards = changes.shard_config.load()
        nightly = [s["name"] for s in shards if s.get("nightly")]
        self.assertTrue(nightly)
        for event in ("pull_request", "push", "schedule", "workflow_dispatch"):
            include = event in changes.NIGHTLY_EVENTS
            matrix = changes.test_matrix(changes.test_platforms(event), shards, include)
            names = {entry["shard"] for entry in matrix}
            for name in nightly:
                self.assertEqual(name in names, include, (event, name))
            self.assertEqual(len(matrix), len(changes.test_platforms(event)) * len(names), event)

    def test_a_shard_filter_without_the_nightly_tests_keeps_its_own_clauses(self):
        shards = changes.shard_config.load()
        first = shards[0]["name"]
        full = changes.shard_config.filter_for(shards, first)
        skipped = changes.shard_config.filter_for(shards, first, include_nightly=False)
        self.assertEqual(skipped, f"({full})&Category!=Nightly")
        rest = changes.shard_config.filter_for(shards, shards[-1]["name"])
        self.assertEqual(changes.shard_config.filter_for(shards, shards[-1]["name"], include_nightly=False), f"{rest}&Category!=Nightly")


REPOSITORY_HAS_SOURCES = (changes.ROOT / "src").is_dir() and (changes.ROOT / "tests/Encspect.Tests").is_dir()


def named_paths(text, root_files=()):
    """The repository paths a C# file names: string literals with a slash (either kind), the literals of a Path.Combine joined (the run after its first
    argument), and a literal that is a file of the repository root."""
    found = set()
    for literal in re.findall(r'"((?:[^"\\\n]|\\.)*)"', text):
        path = literal.replace("\\\\", "/").replace("\\", "/")
        if "/" in path and not path.startswith("/") and " " not in path and ".." not in path:
            found.add(path.strip("/"))
        if literal in root_files:
            found.add(literal)
    for call in re.findall(r"Path\.Combine\(([^;]*?)\)(?:;|\))", text):
        run = []
        for argument in (a.strip() for a in call.split(",")):
            match = re.fullmatch(r'"([^"]*)"', argument)
            if match:
                run.append(match.group(1))
            elif run:
                break
        if run and ".." not in run:
            found.add("/".join(run))
    return found


def scoped(*files, event="pull_request"):
    flags, reason, scope = changes.decide_with_scope(event, list(files))
    return scope, reason, changes.test_matrix(changes.test_platforms(event), changes.shard_config.load(), event in changes.NIGHTLY_EVENTS, scope)


def shard_names(matrix):
    return {entry["shard"] for entry in matrix}


FILTER_TOKEN = re.compile(r"\s*(\(|\)|&|\||(?:FullyQualifiedName|DisplayName)(?:!?~|!=)[A-Za-z0-9_.-]+|Category!?=[A-Za-z]+)")


def valid_filter(expression):
    """Whether a --filter expression is of the grammar dotnet test reads here: clauses joined by & and |, in balanced parentheses."""
    tokens, at = [], 0
    while at < len(expression):
        match = FILTER_TOKEN.match(expression, at)
        if not match:
            return False
        tokens.append(match.group(1))
        at = match.end()
    depth, expect_clause = 0, True
    for token in tokens:
        if expect_clause:
            if token == "(":
                depth += 1
            elif token in (")", "&", "|"):
                return False
            else:
                expect_clause = False
        elif token == ")":
            depth -= 1
            if depth < 0:
                return False
        elif token in ("&", "|"):
            expect_clause = True
        else:
            return False
    return depth == 0 and not expect_clause


@unittest.skipUnless(REPOSITORY_HAS_SOURCES, "needs src and tests/Encspect.Tests")
class ScopeTests(unittest.TestCase):
    def setUp(self):
        self.config = scope.load()
        self.shards = changes.shard_config.load()

    def test_a_wavpack_change_scopes_to_wavpack_and_the_formats_that_carry_it(self):
        for path in ("src/Encspect.Formats.WavPack/WavPackParser.cs", "src/Encspect.Formats.WavPack/Decoding/WavPackDecoder.cs",
                     "tests/Encspect.Tests/Formats/WavPack/WavPackTests.cs", "tests/fixtures/wavpack/a.wv"):
            chosen, reason, matrix = scoped(path)
            self.assertEqual(chosen.selected, {"WavPack", "Matroska", "Ape", "Id3"}, path)
            self.assertIn("unit tests scoped to WavPack, Ape, Id3, Matroska", reason)
            names = shard_names(matrix)
            self.assertNotIn("corrupt-c", names, "its classes belong to other formats, so nothing is left of it")
            self.assertIn("rest", names)
            self.assertIn("corrupt-e", names, "CorruptWavPackBuiltFileTests")
            for entry in matrix:
                self.assertTrue(valid_filter(entry["filter"]), entry["filter"])
        corrupt_a = next(e["filter"] for e in matrix if e["shard"] == "corrupt-a")
        self.assertIn("FullyQualifiedName!~.CorruptMpegFileTests.", corrupt_a)
        self.assertNotIn("FullyQualifiedName!~.CorruptWavPackFileTests.", corrupt_a)
        self.assertNotIn("FullyQualifiedName!~.CorruptMatroskaFileTests.", corrupt_a)

    def test_a_scoped_run_excludes_the_tests_of_the_other_areas_and_never_what_nobody_owns(self):
        chosen, _, matrix = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs")
        rest = next(e["filter"] for e in matrix if e["shard"] == "rest")
        self.assertIn("FullyQualifiedName!~.TtaFixtureTests.", rest)
        self.assertNotIn("FullyQualifiedName!~.WavPackFixtureTests.", rest)
        self.assertNotIn("FullyQualifiedName!~.MatroskaFixtureTests.", rest)
        for never in ("DescriptionTests", "FindingCoverageTests", "DecoderDeterminismTests", "AnalyzerTests", "MeasurementDeterminismTests"):
            self.assertNotIn(never, rest)
        corrupt_b = next(e["filter"] for e in matrix if e["shard"] == "corrupt-b")
        self.assertIn("FullyQualifiedName!=Encspect.Tests.Host.MemoryTests.ManySpeexPacketsStayCompact", corrupt_b)
        self.assertNotIn("FullyQualifiedName!=Encspect.Tests.Host.MemoryTests.ManyWavPackBlocksStayCompact", corrupt_b)

    def test_a_codec_change_scopes_to_the_containers_that_carry_it_and_a_tag_change_to_what_holds_the_tag(self):
        self.assertEqual(scoped("src/Encspect.Formats.Opus/OpusParser.cs")[0].selected, {"Opus", "Ogg", "Matroska", "Mp4", "VorbisComment"})
        self.assertEqual(scoped("src/Encspect.Formats.Dts/DtsParser.cs")[0].selected, {"Dts", "Mp4", "Matroska", "Wav", "DtsHd", "Ape", "Id3"})
        self.assertEqual(scoped("src/Encspect.Tags.Lyrics3/Lyrics3TagParser.cs")[0].selected, {"Lyrics3", "MpegAudio", "OptimFrog"})
        self.assertIn("Aiff", scoped("src/Encspect.Formats.Wav/WavParser.cs")[0].selected)

    def test_several_areas_and_a_fixture_folder_of_a_container_select_its_codecs(self):
        chosen = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", "src/Encspect.Formats.Tta/TtaParser.cs", "docs/formats/tta.md")[0]
        self.assertEqual(chosen.selected, {"WavPack", "Tta", "Matroska", "Ape", "Id3"})
        self.assertLessEqual({"Matroska", "Opus", "Vorbis", "WavPack", "Flac"}, scoped("tests/fixtures/matroska/a.mka")[0].selected)

    def test_a_scoped_run_drops_the_shards_it_leaves_no_test(self):
        _, _, matrix = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs")
        self.assertEqual(len(matrix), len(changes.test_platforms("pull_request")) * len(shard_names(matrix)))
        self.assertLess(len(shard_names(matrix)), len([s for s in self.shards if not s.get("nightly")]))

    def test_whatever_is_not_an_area_runs_the_whole_suite(self):
        everything = {s["name"] for s in self.shards if not s.get("nightly")}
        for path in ("src/Encspect.Host/Analyzer.cs", "src/Encspect.Core.Abstractions/ByteReader.cs", "src/Encspect.Cli/Program.cs", "src/Encspect.Gui/App.axaml.cs",
                     "tests/Encspect.Tests/Support/Fixtures.cs", "tests/Encspect.Tests/Ci/CiWorkflowTests.cs", "tests/Encspect.Tests/Encspect.Tests.csproj", "tests/Encspect.Tests/Formats/NewFormat/Tests.cs",
                     "tests/fixtures/fuzz/a.bin", "tests/fixtures/measurement/a.wav", "tests/fixtures/newformat/a.bin", "scripts/ci/shards.json", "scripts/ci/areas.json",
                     ".github/workflows/ci.yml", "Directory.Build.props", "Encspect.slnx", "src/Encspect.Formats.WavPack/Encspect.Formats.WavPack.csproj",
                     "src/Encspect.Formats.NewFormat/Parser.cs", "something-unknown.bin"):
            chosen, reason, matrix = scoped(path)
            self.assertIsNone(chosen, path)
            self.assertNotIn("scoped", reason)
            self.assertEqual(shard_names(matrix), everything, path)
            self.assertEqual([e["filter"] for e in matrix], [changes.shard_config.filter_for(self.shards, e["shard"], False) for e in matrix], path)
        # One shared path among area paths is enough.
        self.assertIsNone(scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", "src/Encspect.Host/Analyzer.cs")[0])
        self.assertIsNone(scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", "tests/Encspect.Tests/Support/Fixtures.cs")[0])

    def test_changed_tests_of_the_shared_folders_join_a_scope_and_stay_owned_by_their_areas(self):
        chosen, reason, matrix = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", "tests/Encspect.Tests/Host/MemoryTests.cs",
                                        "tests/Encspect.Tests/Decoding/DecoderChargeTests.cs", "tests/Encspect.Tests/Measurement/MeasurementTests.cs",
                                        "tests/Encspect.Tests/Core/NodeTests.cs", "tests/Encspect.Tests/Cli/CommandLineTests.cs")
        self.assertEqual(chosen.selected, {"WavPack", "Matroska", "Ape", "Id3"})
        corrupt_b = next(e["filter"] for e in matrix if e["shard"] == "corrupt-b")
        self.assertNotIn("MemoryTests.ManySpeexPacketsStayCompact", corrupt_b, "the changed file's classes run whole")
        only = scoped("tests/Encspect.Tests/Host/AnalyzerTests.cs")
        self.assertEqual(only[0].selected, set())
        self.assertIn("scoped to no area", only[1])
        for path in ("tests/Encspect.Tests/Support/Fixtures.cs", "tests/Encspect.Tests/Ci/CiWorkflowTests.cs", "scripts/ci/scope.py", "scripts/ci/areas.json"):
            self.assertIsNone(scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", path)[0], path)

    def test_a_changed_container_selects_the_codecs_it_carries_and_their_fixture_folders_select_it(self):
        chosen = scoped("src/Encspect.Formats.Mp4/Mp4Parser.cs")[0]
        self.assertLessEqual({"Mp4", "Alac", "Aac", "Opus", "Flac", "TrueHd"}, chosen.selected)
        for name in ("AlacDecoderTests", "AlacFixtureTests", "CorruptAlacFileTests", "CorruptAlacBuiltFileTests"):
            self.assertNotIn(name, chosen.excluded_classes, name)
        self.assertIn("Opus", scoped("src/Encspect.Formats.Ogg/OggParser.cs")[0].selected)
        self.assertLessEqual({"Alac", "Mp4", "Matroska"}, scoped("tests/fixtures/alac/a.m4a")[0].selected)

    def test_an_article_change_runs_only_the_tests_that_read_the_articles(self):
        chosen, reason, matrix = scoped("src/Encspect.Host/Topics/mid-side-stereo.md")
        readers = scope.article_readers()
        self.assertIn("DescriptionTests", readers)
        self.assertIn("scoped to the tests that read the changed files", reason)
        self.assertEqual(set(chosen.only), set(readers))
        self.assertEqual(shard_names(matrix), {"rest"}, "every reader is in the rest shard, so the others have nothing to run")
        for entry in matrix:
            self.assertTrue(valid_filter(entry["filter"]), entry["filter"])
            self.assertTrue(entry["filter"].endswith("|".join(f"FullyQualifiedName~.{c}." for c in readers) + ")"))
        # Every test file that loads the TopicLibrary is a reader.
        for file in scope.sources_of():
            if "TopicLibrary" in file.read_text(encoding="utf-8", errors="replace"):
                self.assertTrue(set(n for n, _, _ in scope.class_extent(file.read_text(encoding="utf-8", errors="replace"))) <= set(readers), file.name)
        # With other changes, those decide.
        mixed = scoped("src/Encspect.Host/Topics/mid-side-stereo.md", "src/Encspect.Formats.WavPack/WavPackParser.cs")[0]
        self.assertIsNone(mixed.only)
        self.assertEqual(mixed.selected, {"WavPack", "Matroska", "Ape", "Id3"})
        self.assertIsNone(scoped("src/Encspect.Host/Topics/mid-side-stereo.md", "src/Encspect.Host/Analyzer.cs")[0])
        self.assertIsNone(scoped("src/Encspect.Host/Topics/mid-side-stereo.md", "tests/Encspect.Tests/Support/Fixtures.cs")[0])

    def test_a_change_to_a_file_the_tests_read_runs_the_classes_that_name_it(self):
        for path, expected in (("THIRD-PARTY-NOTICES.md", {"ReleaseWorkflowTests", "RepositoryRootTests"}), ("scripts/release/dotnet-pin.txt", {"ReleaseWorkflowTests"}),
                               ("docs/development.md", {"ReleaseWorkflowTests", "RepositoryRootTests"})):
            chosen, reason, matrix = scoped(path)
            self.assertLessEqual(expected, set(chosen.only), path)
            self.assertIn("read the changed files", reason)
            self.assertTrue(all(valid_filter(e["filter"]) for e in matrix))
        self.assertEqual(shard_names(scoped("scripts/release/dotnet-pin.txt")[2]), {"rest"})
        self.assertIsNone(scoped("THIRD-PARTY-NOTICES.md", "src/Encspect.Host/Analyzer.cs")[0])

    def test_a_release_script_change_has_no_unit_tests_to_scope(self):
        flags, reason, chosen = changes.decide_with_scope("pull_request", ["scripts/release/make_notices.py"])
        self.assertFalse(any(flags.values()))
        self.assertIsNone(chosen)

    def test_carriers_stop_at_one_level(self):
        mp4 = scoped("src/Encspect.Formats.Mp4/Mp4Parser.cs")[0].selected
        self.assertTrue({"Ogg", "Matroska", "Asf"}.isdisjoint(mp4), "the containers of Mp4's codecs are not selected")
        self.assertNotIn("Aiff", scoped("src/Encspect.Formats.Dts/DtsParser.cs")[0].selected)
        opus = scoped("src/Encspect.Formats.Opus/OpusParser.cs")[0].selected
        self.assertEqual(opus, {"Opus", "Ogg", "Matroska", "Mp4", "VorbisComment"})
        self.assertEqual(scoped("src/Encspect.Formats.WavPack/WavPackParser.cs")[0].selected, {"WavPack", "Matroska", "Ape", "Id3"})
        self.assertEqual(scoped("src/Encspect.Tags.Ape/ApeTagParser.cs")[0].selected, {"Ape", "Dts", "Flac", "Matroska", "MonkeysAudio", "MpegAudio", "Musepack", "Ogg", "OptimFrog", "Tak", "Tta", "WavPack"})

    def test_every_fixture_folder_lists_the_containers_of_its_files(self):
        containers = {"m4a": "Mp4", "mp4": "Mp4", "m4b": "Mp4", "3gp": "Mp4", "mov": "Mp4", "mka": "Matroska", "mkv": "Matroska", "webm": "Matroska",
                      "ogg": "Ogg", "opus": "Ogg", "spx": "Ogg", "asf": "Asf", "wma": "Asf", "wav": "Wav", "w64": "Wav", "aiff": "Aiff", "aifc": "Aiff"}
        for folder, listed in self.config["fixtures"].items():
            used = {containers[f.suffix[1:]] for f in (changes.ROOT / "tests/fixtures" / folder).rglob("*") if f.suffix[1:] in containers}
            self.assertLessEqual(used, set(listed), f"tests/fixtures/{folder} holds files of containers that areas.json does not list for it")

    def test_a_changed_shared_test_file_runs_every_class_and_method_it_defines(self):
        path = "tests/Encspect.Tests/Host/CorruptTagFileTests.cs"
        chosen = scoped(path)[0]
        self.assertEqual(chosen.selected, set())
        text = (changes.ROOT / path).read_text()
        defined = {n for n in self.config["classes"] if f"class {n}" in text}
        self.assertTrue(defined)
        self.assertFalse(defined & set(chosen.excluded_classes))
        self.assertNotIn("CorruptTagFileTests", chosen.excluded_methods)
        self.assertNotIn("MemoryTests", scoped("tests/Encspect.Tests/Host/MemoryTests.cs")[0].excluded_methods)
        self.assertIn("MemoryTests", scoped("tests/Encspect.Tests/Core/Crc32Tests.cs")[0].excluded_methods)

    def test_a_changed_shared_helper_runs_the_owned_tests_that_use_it(self):
        for path in ("tests/Encspect.Tests/Host/MemoryTestBase.cs", "tests/Encspect.Tests/Decoding/DecoderWorkTests.cs"):
            chosen = scoped(path)[0]
            self.assertEqual(chosen.selected, set())
            self.assertNotIn("MemoryTests", chosen.excluded_methods, path)
            self.assertNotIn("MusepackAndContainerMemoryTests", chosen.excluded_methods, path)

    def test_a_test_class_that_other_areas_call_selects_the_callers(self):
        owners, _ = scope.inventory()
        for caller, area in (("AlacDecoderFixtureTests", "Flac"), ("TtaFixtureTests", "Flac"), ("WavPackDecoderFixtureTests", "Flac"), ("MonkeysAudioFixtureTests", "WavPack"),
                             ("MonkeysAudioDecoderFixtureTests", "WavPack"), ("MatroskaPcmRouteTests", "Flac")):
            self.assertIn(area, owners[caller], caller)
        self.assertNotIn("AlacDecoderFixtureTests", scoped("tests/Encspect.Tests/Formats/Flac/DecoderFixtureTests.cs")[0].excluded_classes)
        self.assertNotIn("MonkeysAudioFixtureTests", scoped("tests/Encspect.Tests/Formats/WavPack/WavPackDecoderFixtureTests.cs")[0].excluded_classes)

    def test_calls_are_followed_through_several_files_and_partial_classes_are_joined(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            files = {"Formats/A/One.cs": "namespace Encspect.Tests;\npublic partial class Shared\n{\n    [Fact]\n    public void FromA() { }\n}\npublic class Helper { }\n",
                     "Formats/B/Two.cs": "namespace Encspect.Tests;\npublic partial class Shared\n{\n    [Fact]\n    public void FromB() { }\n}\npublic class UsesHelper\n{\n    [Fact]\n    public void T() { Helper.Run(); }\n}\n",
                     "Formats/C/Three.cs": "namespace Encspect.Tests;\npublic class UsesUsesHelper\n{\n    [Fact]\n    public void T() { UsesHelper.Run(); }\n}\n"}
            for name, text in files.items():
                (root / "tests/Encspect.Tests" / name).parent.mkdir(parents=True, exist_ok=True)
                (root / "tests/Encspect.Tests" / name).write_text(text)
            owners, methods = scope.inventory(root, {"areas": ["A", "B", "C"], "fixtures": {}})
            self.assertEqual(sorted(methods["Shared"]), ["FromA", "FromB"])
            self.assertEqual(owners["Shared"], {"A", "B"})
            self.assertEqual(owners["UsesHelper"], {"A", "B"})
            self.assertEqual(owners["UsesUsesHelper"], {"A", "B", "C"})

    def test_a_changed_format_selects_the_tag_areas_whose_tests_go_through_it(self):
        chosen = scoped("src/Encspect.Formats.MpegAudio/MpegAudioParser.cs")[0]
        self.assertLessEqual({"Ape", "Id3", "Lyrics3"}, chosen.selected)
        for name in ("ApeItemLimitTests", "Id3v2FrameSizeTests"):
            self.assertNotIn(name, chosen.excluded_classes)
        self.assertIn("VorbisComment", scoped("src/Encspect.Formats.Ogg/OggParser.cs")[0].selected)
        self.assertNotIn("Ape", scoped("src/Encspect.Formats.Opus/OpusParser.cs")[0].selected, "a tag list is not carried further")

    def test_documentation_and_fixture_scripts_do_not_widen_a_scope(self):
        chosen = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", "docs/formats/wavpack.md", "scripts/fixtures/make_wavpack_fixtures.py")[0]
        self.assertEqual(chosen.selected, {"WavPack", "Matroska", "Ape", "Id3"})
        self.assertIsNone(scoped("docs/formats/wavpack.md")[0])

    def test_a_deleted_script_runs_everything(self):
        _, _, chosen = changes.decide_with_scope("pull_request", ["src/Encspect.Formats.WavPack/WavPackParser.cs"], removed=["scripts/fixtures/wavpack_model.py"])
        self.assertIsNone(chosen)

    def test_the_other_events_run_every_test_unchanged(self):
        for event in ("push", "schedule", "workflow_dispatch"):
            chosen, reason, matrix = scoped("src/Encspect.Formats.WavPack/WavPackParser.cs", event=event)
            self.assertIsNone(chosen)
            include = event in changes.NIGHTLY_EVENTS
            self.assertEqual([(e["shard"], e["filter"]) for e in matrix], [(s["name"], changes.shard_config.filter_for(self.shards, s["name"], include))
                                                                             for p in changes.test_platforms(event) for s in self.shards if changes.shard_config.runs(s, include)])
        _, _, chosen = changes.decide_with_scope("pull_request", None)
        self.assertIsNone(chosen)

    def test_the_filters_are_valid_and_stay_short(self):
        longest = 0
        for selected in (set(), {"WavPack"}, {"Mp4", "Aac"}, set(self.config["areas"])):
            chosen = scope.build(selected)
            for shard in self.shards:
                for include in (False, True):
                    expression = scope.shard_filter(chosen, shard, self.shards, include)
                    self.assertTrue(valid_filter(expression), (shard["name"], expression[:200]))
                    longest = max(longest, len(expression))
        self.assertLess(longest, 12000, "the longest filter, which the runners pass on the command line")

    def test_selecting_every_area_excludes_nothing(self):
        chosen = scope.build(set(self.config["areas"]))
        self.assertEqual((chosen.excluded_classes, chosen.excluded_methods), ([], {}))
        for shard in self.shards:
            self.assertEqual(scope.shard_filter(chosen, shard, self.shards, False), changes.shard_config.filter_for(self.shards, shard["name"], False))

    def test_the_rest_shard_is_never_dropped_and_names_only_what_the_others_do_not(self):
        chosen = scope.build(set())
        self.assertFalse(scope.is_empty(chosen, self.shards[-1]))
        listed = {c for s in self.shards[:-1] for c in s["classes"]}
        rest = scope.exclusion(chosen, self.shards[-1], self.shards)
        self.assertFalse([c for c in rest if any(f".{name}." in c or f".{name}" == c.split("~")[1][:len(name) + 1] for name in listed)])

    def test_the_areas_are_the_projects(self):
        projects = {p.name.split(".", 2)[2] for p in (changes.ROOT / "src").iterdir() if re.fullmatch(r"Encspect\.(Formats|Tags)\.\w+", p.name)}
        self.assertEqual(projects, set(self.config["areas"]), "a new format or tag project goes into areas.json, with its routes")
        for folder in ("Formats", "Tags"):
            for area in (changes.ROOT / "tests/Encspect.Tests" / folder).iterdir():
                self.assertIn(area.name, projects, f"tests/Encspect.Tests/{folder}/{area.name} has no project")

    def test_every_name_in_areas_exists(self):
        owners, methods = scope.inventory()
        for name, listed in self.config["classes"].items():
            self.assertIn(name, methods, f"the class {name} has no test")
            self.assertNotIn(name, owners, f"{name} is in an area folder, which owns it already")
            self.assertTrue(listed and set(listed) <= set(self.config["areas"]), name)
        for name, per_method in self.config["methods"].items():
            self.assertIn(name, methods, name)
            for method, listed in per_method.items():
                self.assertIn(method, methods[name], f"{name}.{method} is no test")
                self.assertTrue(listed and set(listed) <= set(self.config["areas"]), (name, method))
                # A filter clause is a substring: no other method of the class may start with this one's name.
                self.assertEqual([m for m in methods[name] if m.startswith(method)], [method], f"{name}.{method} is the start of another test's name")
        # Read from git, not the working tree: the docs job's sparse checkout leaves the fixtures out.
        tracked = set(subprocess.run(["git", "ls-tree", "-d", "--name-only", "HEAD", "tests/fixtures/"], cwd=changes.ROOT,
                                     capture_output=True, text=True, check=True).stdout.split())
        for folder, listed in self.config["fixtures"].items():
            self.assertIn(f"tests/fixtures/{folder}", tracked, folder)
            self.assertTrue(set(listed) <= set(self.config["areas"]), folder)
        named = set(self.config["codecs"].values()) | set(self.config["routes"]) | set(self.config["tags"]) | {a for c in self.config["tags"].values() for a in c}
        self.assertLessEqual(named, set(self.config["areas"]))

    def test_every_fixture_folder_of_a_cross_check_is_mapped(self):
        mapped = set(self.config["fixtures"])
        known = {folder for spec in changes.AREAS.values() for folder in spec["fixtures"]}
        self.assertEqual(known - mapped - {"measurement"}, set(), "a fixture folder no scope knows runs the whole suite; add it to areas.json")

    def test_the_damaged_input_classes_of_every_format_are_owned(self):
        _, methods = scope.inventory()
        corrupt = {name for name in methods if name.startswith("Corrupt") and name.endswith("FileTests")}
        self.assertEqual(corrupt - set(self.config["classes"]) - {"CorruptTagFileTests"}, set(), "a damaged-input class names its owners in areas.json, or runs always")
        self.assertIn("CorruptTagFileTests", self.config["methods"])

    def test_the_container_routes_name_every_codec_ID_the_sources_mention(self):
        """The table of areas.json against the sources: a container project that names a codec ID of another area, or holds a
        tag of another area, is in the table, so that a change to that codec reaches the container's tests."""
        ids = self.config["codecs"]
        for project in sorted((changes.ROOT / "src").glob("Encspect.Formats.*")):
            area = project.name.split(".", 2)[2]
            text = "\n".join(f.read_text(encoding="utf-8", errors="replace") for f in project.rglob("*.cs"))
            named = {i for i in ids if f'"{i}"' in text and ids[i] != area}
            self.assertLessEqual(named, set(self.config["routes"].get(area, [])), f"{area} names codec IDs that routes of areas.json lacks")
            if re.search(r'ParseTag\(', text):
                self.assertTrue(any(area in carried for carried in self.config["tags"].values()), f"{area} hands tags to a tag parser")
            for literal, tag in (('"ID3v2"', "Id3"), ('"Vorbis Comment"', "VorbisComment")):
                if literal in text and area != tag:
                    self.assertIn(area, self.config["tags"][tag], f"{area} names the tag {literal}")



class ShardGuardTests(unittest.TestCase):
    """The last shard runs only the damaged-input classes it names, so a class a shard loses cannot fall into it unnoticed."""

    def write(self, shards):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False)
        with handle:
            json.dump({"note": "", "shards": shards}, handle)
        self.addCleanup(pathlib.Path(handle.name).unlink)
        return handle.name

    def test_the_committed_split_leaves_no_damaged_input_class_to_the_rest_unnamed(self):
        shards = changes.shard_config.load()
        classes = {c for s in shards[:-1] for c in s["classes"]} | set(shards[-1].get("damagedInputClasses", []))
        self.assertEqual([], changes.shard_config.damaged_input_in_rest(shards, classes))

    def test_a_class_a_shard_lost_is_named(self):
        path = self.write([{"name": "a", "classes": ["CorruptMpegFileTests"]},
                           {"name": "rest", "classes": None, "damagedInputClasses": ["CorruptOggFileTests"]}])
        shards = changes.shard_config.load(path)
        found = changes.shard_config.damaged_input_in_rest(
            shards, {"CorruptMpegFileTests", "CorruptOggFileTests", "CorruptLayerFileTests", "GoldenTests"})
        self.assertEqual(["CorruptLayerFileTests"], found)

    def test_only_the_last_shard_names_damaged_input_classes(self):
        path = self.write([{"name": "a", "classes": ["CorruptMpegFileTests"], "damagedInputClasses": []},
                           {"name": "rest", "classes": None}])
        with self.assertRaises(SystemExit):
            changes.shard_config.load(path)

    def test_a_class_cannot_be_both_in_a_shard_and_meant_for_the_rest(self):
        path = self.write([{"name": "a", "classes": ["CorruptMpegFileTests"]},
                           {"name": "rest", "classes": None, "damagedInputClasses": ["CorruptMpegFileTests"]}])
        with self.assertRaises(SystemExit):
            changes.shard_config.load(path)


    def split(self, first, second):
        return self.write([{"name": "a", "classes": ["BigTests", "Other"], "cases": {"BigTests": first}},
                           {"name": "b", "classes": ["BigTests"], "cases": {"BigTests": second}},
                           {"name": "rest", "classes": None}])

    def test_a_split_class_is_filtered_by_display_name_and_the_last_part_takes_the_remainder(self):
        shards = changes.shard_config.load(self.split(["flac-", "ape-"], None))
        self.assertEqual(changes.shard_config.filter_for(shards, "a"),
                         "(FullyQualifiedName~.BigTests.&(DisplayName~flac-|DisplayName~ape-))|FullyQualifiedName~.Other.")
        self.assertEqual(changes.shard_config.filter_for(shards, "b"), "(FullyQualifiedName~.BigTests.&DisplayName!~flac-&DisplayName!~ape-)")
        self.assertEqual(changes.shard_config.filter_for(shards, "rest"), "FullyQualifiedName!~.BigTests.&FullyQualifiedName!~.Other.")

    def test_a_split_class_needs_a_remainder_and_distinct_strings(self):
        for first, second in ((["a"], ["b"]), (None, None), (["a"], ["a"]), ([], None), (["a", "a"], None)):
            with self.subTest(first=first, second=second), self.assertRaises(SystemExit):
                changes.shard_config.load(self.split(first, second))

    def test_a_class_listed_twice_without_a_split_or_split_in_a_shard_that_lacks_it_is_refused(self):
        twice = self.write([{"name": "a", "classes": ["BigTests"]}, {"name": "b", "classes": ["BigTests"]}, {"name": "rest", "classes": None}])
        with self.assertRaises(SystemExit):
            changes.shard_config.load(twice)
        lone = self.write([{"name": "a", "classes": ["BigTests"], "cases": {"BigTests": None}}, {"name": "rest", "classes": None}])
        with self.assertRaises(SystemExit):
            changes.shard_config.load(lone)
        stray = self.write([{"name": "a", "classes": ["X"], "cases": {"BigTests": ["a"]}}, {"name": "rest", "classes": None}])
        with self.assertRaises(SystemExit):
            changes.shard_config.load(stray)

    def test_the_committed_split_classes_are_shared_by_shards_that_keep_the_nightly_flag_together(self):
        shards = changes.shard_config.load()
        for name in {c for s in shards[:-1] for c in s.get("cases", {})}:
            sharing = [s for s in shards[:-1] if name in s.get("cases", {})]
            self.assertEqual(1, len({bool(s.get("nightly")) for s in sharing}), name)


if __name__ == "__main__":
    unittest.main()
