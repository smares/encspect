#!/usr/bin/env python3
"""Tests of how the cross-checks treat a MediaInfo that fails, and of the WavPack model of its Data_Parse_Fill:
python3 scripts/ci/test_mediainfo_tool.py"""

import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import crosscheck_wavpack_fixtures as wavpack  # noqa: E402
import mediainfo_tool  # noqa: E402


def failing(code):
    return mock.patch.object(mediainfo_tool, "read", side_effect=mediainfo_tool.Failed(code))


def tally_of(crashes=None):
    tally = mediainfo_tool.Tally(None, crashes)
    tally.start("fixture")
    return tally


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

    def test_the_block_that_ends_the_file(self):
        self.assertEqual(wavpack.fill_block([self.block(0, 10)], 100)["offset"], 0)
        self.assertIsNone(wavpack.fill_block([self.block(0, 10)], 1000))


if __name__ == "__main__":
    unittest.main()
