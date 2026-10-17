#!/usr/bin/env python3
"""Tests of how the cross-checks treat a MediaInfo that fails, and of the WavPack model of its Data_Parse_Fill:
python3 scripts/ci/test_mediainfo_tool.py"""

import json
import pathlib
import subprocess
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import crosscheck_musepack_fixtures as musepack  # noqa: E402
import crosscheck_wavpack_fixtures as wavpack  # noqa: E402
import mediainfo_tool  # noqa: E402


def failing(code):
    return mock.patch.object(mediainfo_tool, "read", side_effect=mediainfo_tool.Failed(code))


def tally_of(crashes=None):
    tally = mediainfo_tool.Tally(None, crashes)
    tally.start("fixture")
    return tally


def ran(code, stdout=""):
    return mock.patch("subprocess.run", return_value=subprocess.CompletedProcess([], code, stdout=stdout, stderr=""))


class RunTests(unittest.TestCase):
    def test_a_signal_raises_failed_with_the_code(self):
        with ran(-8), self.assertRaises(mediainfo_tool.Failed) as caught:
            mediainfo_tool.read("x")
        self.assertEqual(caught.exception.returncode, -8)

    def test_no_output_raises_failed(self):
        for output in ("", "  \n"):
            with ran(0, output), self.assertRaises(mediainfo_tool.Failed) as caught:
                mediainfo_tool.read("x")
            self.assertEqual(caught.exception.returncode, 0)

    def test_an_error_exit_raises_failed(self):
        with ran(1, "{}"), self.assertRaises(mediainfo_tool.Failed):
            mediainfo_tool.read("x")

    def test_json_gives_the_tracks(self):
        document = {"media": {"track": [{"@type": "General", "Format": "X"}, {"@type": "Audio", "Format": "Y"}]}}
        with ran(0, json.dumps(document)):
            tracks = mediainfo_tool.read("x")
        self.assertEqual(tracks["General"]["Format"], "X")
        self.assertEqual(tracks["Audio"][0]["Format"], "Y")

    def test_read_or_problem_adds_the_failure(self):
        problems = []
        with ran(-8):
            self.assertIsNone(mediainfo_tool.read_or_problem("x", problems))
        self.assertEqual(len(problems), 1)


class MusepackTests(unittest.TestCase):
    """sv8-rate-reserved without the crash: the format is read before the rate, the rate-derived fields are skipped, no division happens."""

    def run_check(self, tracks, crash=True):
        tally = mediainfo_tool.Tally(musepack.MEDIAINFO_READS_OTHERWISE, musepack.MEDIAINFO_CRASHES)
        path = musepack.DIRECTORY / "sv8-rate-reserved.mpc"
        patch = failing(-8) if tracks is None else mock.patch.object(mediainfo_tool, "read", return_value=tracks)
        # The documentation job checks out no fixtures, so the analyzer's report is the part check_mediainfo reads of it.
        report = {"streams": [{"codec": "Musepack SV8"}]}
        with patch, mock.patch.object(mediainfo_tool, "golden", return_value=report):
            problems = musepack.check_mediainfo("sv8-rate-reserved", path, tally)
        return problems, tally

    def test_the_crash_passes(self):
        problems, tally = self.run_check(None)
        self.assertEqual(problems, [])

    def test_no_crash_with_the_format_passes(self):
        problems, tally = self.run_check({"General": {}, "Audio": [{"Format": "Musepack SV8"}]})
        self.assertEqual(problems, [])
        self.assertTrue(any("past the table" in how for _, how in tally.counts))

    def test_no_crash_with_another_format_is_a_problem(self):
        problems, _ = self.run_check({"General": {}, "Audio": [{"Format": "MPEG Audio"}]})
        self.assertEqual(len(problems), 1)


class ReadTests(unittest.TestCase):
    def test_a_crash_is_a_problem(self):
        tally = tally_of()
        with failing(-8):
            self.assertIsNone(tally.read("x"))
        self.assertEqual(len(tally.problems), 1)

    def test_empty_output_is_a_problem(self):
        tally = tally_of()
        with failing(0):
            tally.read("x")
        self.assertEqual(len(tally.problems), 1)

    def test_the_stated_crash_is_none_without_a_problem(self):
        tally = tally_of({"fixture": (-8, "reason", False)})
        with failing(-8):
            self.assertIsNone(tally.read("x"))
        self.assertEqual(tally.problems, [])
        self.assertEqual(tally.unused(), [])

    def test_another_exit_than_the_stated_one_is_a_problem(self):
        tally = tally_of({"fixture": (-8, "reason", False)})
        with failing(1):
            tally.read("x")
        self.assertEqual(len(tally.problems), 1)

    def test_a_crash_that_does_not_happen_expires_unless_optional(self):
        for optional, problems in ((False, 1), (True, 0)):
            tally = tally_of({"fixture": (-8, "reason", optional)})
            with mock.patch.object(mediainfo_tool, "read", return_value={"General": {}, "Audio": []}):
                self.assertIsNotNone(tally.read("x"))
            self.assertEqual(len(tally.problems), problems)

    def test_a_crash_never_read_is_reported(self):
        self.assertEqual(len(tally_of({"fixture": (-8, "reason", False)}).unused()), 1)


class FillBlockTests(unittest.TestCase):
    @staticmethod
    def block(offset, samples, version="0x410"):
        return {"offset": offset, "length": 100, "samples": samples, "version": version}

    def test_the_second_block_with_samples_in_version_4(self):
        blocks = [self.block(0, 10), self.block(100, 0), self.block(200, 10)]
        self.assertEqual(wavpack.fill_block(blocks, 10_000)["offset"], 200)

    def test_every_block_counts_in_another_version(self):
        blocks = [self.block(0, 10, "0x500"), self.block(100, 0, "0x500"), self.block(200, 10, "0x500")]
        self.assertEqual(wavpack.fill_block(blocks, 10_000)["offset"], 100)

    def test_the_jump_needs_a_mebibyte_after_the_header(self):
        blocks = [self.block(0, 10), self.block(100, 10)]
        mebibyte = 2 * 512 * 1024
        self.assertTrue(wavpack.jumps_to_end(blocks, 100 + 8 + mebibyte))
        self.assertFalse(wavpack.jumps_to_end(blocks, 100 + 8 + mebibyte - 1))
        self.assertFalse(wavpack.jumps_to_end(blocks, 100 + mebibyte - 1))

    def test_the_block_that_ends_the_file(self):
        self.assertEqual(wavpack.fill_block([self.block(0, 10)], 100)["offset"], 0)
        self.assertIsNone(wavpack.fill_block([self.block(0, 10)], 1000))


if __name__ == "__main__":
    unittest.main()
