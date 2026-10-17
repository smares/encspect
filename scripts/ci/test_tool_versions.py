#!/usr/bin/env python3
"""Tests of the version assertion of the cross-checks: python3 scripts/ci/test_tool_versions.py"""

import pathlib
import subprocess
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import tool_versions  # noqa: E402


def printing(text, code=0):
    return mock.patch("subprocess.run", return_value=subprocess.CompletedProcess([], code, stdout=text, stderr=""))


class MatchTests(unittest.TestCase):
    def test_a_version_matches_when_the_package_revision_follows(self):
        self.assertTrue(tool_versions.matches("8.0.1-3ubuntu2", "8.0.1"))
        self.assertTrue(tool_versions.matches("7:8.0.1-3ubuntu2", "8.0.1"))
        self.assertTrue(tool_versions.matches("2:0.1~r495-2", "0.1~r495"))
        self.assertTrue(tool_versions.matches("2.2.1-revUNKNOWN_REV", "2.2.1"))

    def test_a_version_that_continues_the_number_does_not(self):
        for found in ("8.0.10", "8.0.1.2", "8.0", "6.1.1-3ubuntu5", "18.0.1"):
            self.assertFalse(tool_versions.matches(found, "8.0.1"), found)


class ProgramTests(unittest.TestCase):
    def test_versions_are_read_from_each_tools_output(self):
        outputs = {
            "ffmpeg": "ffmpeg version 8.0.1-3ubuntu2 Copyright (c) 2000-2025 the FFmpeg developers\n",
            "ffprobe": "ffprobe version 8.0.1-3ubuntu2 Copyright (c) 2007-2025 the FFmpeg developers\n",
            "mkvinfo": "mkvinfo v97.0 ('Odd Fishes') 64-bit\n",
            "wvunpack": "wvunpack 5.9.0\nlibwavpack 5.9.0\n",
            "MP4Box": "MP4Box - GPAC version 2.2.1-revUNKNOWN_REV\n(c) 2000-2022 Telecom Paris\n",
            "faad": " *********** Ahead Software MPEG-4 AAC Decoder V2.11.2 ******************\n",
            "mpg123": "mpg123 1.33.3; build for x86-64\n",
            "php": "8.5.1",
            "oggz": "oggz version 1.1.3\n",
            "ogginfo": "ogginfo from vorbis-tools 1.4.3\n",
        }
        self.assertEqual(set(outputs), set(tool_versions.PROGRAMS))
        for name, output in outputs.items():
            with printing(output):
                self.assertEqual(tool_versions.problems(name), [], name)

    def test_another_version_is_named_with_the_tool(self):
        with printing("ffmpeg version 6.1.1-3ubuntu5 Copyright\n"):
            self.assertEqual(tool_versions.problems("ffmpeg"), ["ffmpeg 8.0.1 is needed, found 6.1.1-3ubuntu5"])

    def test_a_tool_that_prints_no_version_or_is_missing_fails(self):
        with printing("usage: mkvinfo\n"):
            self.assertIn("no version was found", tool_versions.problems("mkvinfo")[0])
        with mock.patch("subprocess.run", side_effect=FileNotFoundError("mkvinfo")):
            self.assertIn("no version was found", tool_versions.problems("mkvinfo")[0])

    def test_every_failure_is_listed_and_require_exits(self):
        with printing("nothing\n"):
            self.assertEqual(len(tool_versions.problems("ffmpeg", "ffprobe")), 2)
            with self.assertRaises(SystemExit) as raised:
                tool_versions.require("ffmpeg", "ffprobe")
            self.assertIn("ffprobe", str(raised.exception))

    def test_a_package_is_read_from_dpkg(self):
        with printing("2:0.1~r495-2"):
            self.assertEqual(tool_versions.problems("musepack"), [])
        with printing("0.7.3-1"):
            self.assertEqual(tool_versions.problems("a52"), ["liba52-0.7.4-dev (dpkg) 0.7.4 is needed, found 0.7.3-1"])
        with printing("", code=1):
            self.assertIn("no version was found", tool_versions.problems("dca")[0])
        for name, found in (("madplay", "0.15.2b-10.2"), ("speex", "1.2.1-3build1"), ("opus-tools", "0.2-1build5")):
            with printing(found):
                self.assertEqual(tool_versions.problems(name), [], name)
        with printing("0.2.1-1"):
            self.assertEqual(len(tool_versions.problems("opus-tools")), 1)

    def test_an_unknown_tool_is_a_mistake_of_the_script(self):
        with self.assertRaises(KeyError):
            tool_versions.problems("sox")


if __name__ == "__main__":
    unittest.main()
