#!/usr/bin/env python3
"""Tests of scripts/fixtures/compare_pcm.py, the by-position comparison of decoded PCM: python3 scripts/ci/test_compare_pcm.py"""

import array
import contextlib
import io
import json
import math
import pathlib
import random
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import compare_pcm as compare  # noqa: E402


def noise(count, seed, amplitude=0.25):
    generator = random.Random(seed)
    return [(generator.random() * 2 - 1) * amplitude for _ in range(count)]


def interleave(channels):
    return [v for frame in zip(*channels) for v in frame]


class Case:
    """An analyzer dump of float samples in runs, and a reference file, in a temporary folder."""

    def __init__(self, test, channels, runs, reference_format="float32", reference_first=0):
        self.directory = pathlib.Path(tempfile.mkdtemp())
        test.addCleanup(lambda: [p.unlink() for p in self.directory.iterdir()] and self.directory.rmdir())
        self.channels = channels
        self.runs = runs
        self.reference_format = reference_format
        self.reference_first = reference_first
        data = array.array("f", interleave(channels))
        (self.directory / "x.s0.pcm").write_bytes(data.tobytes())
        manifest = {"file": "x", "streams": [{"index": 0, "codec": "test", "sampleRate": 48000, "channels": len(channels), "format": "float32", "bitsPerSample": 32,
                                              "pcm": "x.s0.pcm", "samples": sum(n for _, n in runs), "runs": runs}]}
        (self.directory / "x.pcm.json").write_text(json.dumps(manifest))

    def write_reference(self, channels):
        values = interleave(channels)
        if self.reference_format == "float32":
            blob = array.array("f", values).tobytes()
        else:
            blob = array.array("h", values).tobytes()
        (self.directory / "ref.raw").write_bytes(blob)

    def run(self, *extra):
        args = [str(self.directory / "x.pcm.json"), "--reference", str(self.directory / "ref.raw"), "--reference-format", self.reference_format,
                "--reference-channels", str(len(self.channels)), "--reference-first-position", str(self.reference_first), "--json", *extra]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = compare.main(args)
        return code, (json.loads(out.getvalue()) if out.getvalue() else None), err.getvalue()


class FloatReferenceTests(unittest.TestCase):
    def test_identical_samples_pass_and_a_difference_above_the_bound_fails(self):
        left = noise(1000, 1)
        case = Case(self, [left, noise(1000, 2)], [[0, 1000]])
        case.write_reference(case.channels)
        code, result, _ = case.run()
        self.assertEqual(code, 0)
        self.assertEqual(result["compared"], 1000)
        self.assertTrue(result["passes"])

        shifted = [[v + 2 ** -13 for v in left], case.channels[1]]
        case.write_reference(shifted)
        code, result, _ = case.run()
        self.assertEqual(code, 1)
        self.assertFalse(result["channels"][0]["passes"])
        self.assertTrue(result["channels"][1]["passes"])

    def test_the_rms_bound_is_the_full_accuracy_one(self):
        samples = noise(4000, 3)
        case = Case(self, [samples], [[0, 4000]])
        small = 2 ** -15 / math.sqrt(12) * 0.9
        case.write_reference([[v + (small if i % 2 else -small) for i, v in enumerate(samples)]])
        self.assertEqual(case.run()[0], 0)
        big = 2 ** -15 / math.sqrt(12) * 1.2
        case.write_reference([[v + (big if i % 2 else -big) for i, v in enumerate(samples)]])
        code, result, _ = case.run()
        self.assertEqual(code, 1)
        self.assertLess(result["channels"][0]["max"], 2 ** -14)

    def test_positions_only_one_side_has_are_listed_and_left_out(self):
        full = noise(1000, 4)
        runs = [[100, 300], [600, 400]]
        analyzer = full[:300] + full[300:700]
        case = Case(self, [analyzer], runs)
        reference = noise(1200, 5)
        # The reference agrees with the analyzer where both have positions.
        reference[100:400] = analyzer[:300]
        reference[600:1000] = analyzer[300:700]
        case.write_reference([reference])
        code, result, _ = case.run()
        self.assertEqual(code, 0, result)
        self.assertEqual(result["compared"], 700)
        self.assertEqual(result["notComparedOnlyInReference"], [[0, 100], [400, 600], [1000, 1200]])
        self.assertEqual(result["notComparedOnlyInAnalyzer"], [])

    def test_a_reference_that_starts_later_and_a_range_it_conceals(self):
        analyzer = noise(500, 6)
        case = Case(self, [analyzer], [[0, 500]], reference_first=100)
        reference = analyzer[100:500]
        reference[50:60] = [0.5] * 10
        case.write_reference([reference])
        code, result, _ = case.run()
        self.assertEqual(code, 1)
        code, result, _ = case.run("--exclude", "150:160")
        self.assertEqual(code, 0, result)
        self.assertEqual((result["compared"], result["excluded"], result["notComparedOnlyInAnalyzer"]), (390, [[150, 160]], [[0, 100]]))

    def test_samples_at_full_scale_are_left_out_on_request(self):
        analyzer = noise(200, 7)
        analyzer[10] = 1.5
        case = Case(self, [analyzer], [[0, 200]])
        reference = list(analyzer)
        reference[10] = 1.0
        case.write_reference([reference])
        self.assertEqual(case.run()[0], 1)
        code, result, _ = case.run("--skip-full-scale")
        self.assertEqual(code, 0)
        self.assertEqual(result["channels"][0]["skippedAtFullScale"], 1)


class FixedReferenceTests(unittest.TestCase):
    def test_floats_are_rounded_to_the_references_width_and_compared_in_lsb(self):
        analyzer = array.array("f", noise(2000, 8)).tolist()
        case = Case(self, [analyzer], [[0, 2000]], reference_format="int16")
        exact = [compare.round_to_width(v, 16) for v in analyzer]
        case.write_reference([exact])
        code, result, _ = case.run()
        self.assertEqual((code, result["unit"], result["channels"][0]["max"]), (0, "LSB", 0))

        off_by_one = [v + (1 if i % 5 == 0 else 0) for i, v in enumerate(exact)]
        case.write_reference([off_by_one])
        self.assertEqual(case.run()[0], 0)

        off_by_three = [v + (3 if i % 100 == 0 else 0) for i, v in enumerate(exact)]
        case.write_reference([off_by_three])
        code, result, _ = case.run()
        self.assertEqual((code, result["channels"][0]["max"]), (1, 3))

    def test_halves_round_up_and_the_range_is_clipped(self):
        self.assertEqual(compare.round_to_width(0.5 / 32768, 16), 1)
        self.assertEqual(compare.round_to_width(-0.5 / 32768, 16), 0)
        self.assertEqual(compare.round_to_width(1.5, 16), 32767)
        self.assertEqual(compare.round_to_width(-2.0, 16), -32768)

    def test_a_reference_that_clips_is_left_out_at_full_scale(self):
        analyzer = array.array("f", noise(100, 9)).tolist()
        analyzer[5] = 1.4
        case = Case(self, [analyzer], [[0, 100]], reference_format="int16")
        reference = [compare.round_to_width(v, 16) for v in analyzer]
        case.write_reference([reference])
        self.assertEqual(case.run()[0], 0)
        self.assertEqual(case.run("--skip-full-scale")[1]["channels"][0]["skippedAtFullScale"], 1)


class BandTests(unittest.TestCase):
    def test_band_energies_are_compared_within_the_tolerance(self):
        analyzer = noise(8192, 10)
        case = Case(self, [analyzer], [[0, 8192]])
        case.write_reference([[v * 1.04 for v in analyzer]])
        code, result, _ = case.run("--bands", "0,3000,8000,16000,24000", "--no-sample-bounds")
        self.assertEqual(code, 0, result)
        self.assertEqual(result["bands"]["channels"][0]["bandsOutside"], 0)
        self.assertGreater(result["bands"]["channels"][0]["bandsCompared"], 20)

        case.write_reference([[v * 1.2 for v in analyzer]])
        code, result, _ = case.run("--bands", "0,3000,8000,16000,24000", "--no-sample-bounds")
        self.assertEqual(code, 1)
        self.assertGreater(result["bands"]["channels"][0]["largestDifferenceDb"], 1.5)

    def test_a_tone_lands_in_the_band_that_holds_it(self):
        tone = [0.5 * math.sin(2 * math.pi * 5000 * i / 48000) for i in range(1024)]
        rows = compare.band_energies(tone, 48000, 1024, [0, 3000, 8000, 24000])
        self.assertLess(rows[0][0], -60)
        self.assertGreater(rows[0][1], -15)
        self.assertLess(rows[0][2], -60)

    def test_a_frame_never_spans_a_jump(self):
        analyzer = noise(2048, 11)
        case = Case(self, [analyzer], [[0, 1024], [4096, 1024]])
        reference = noise(5120, 12)
        reference[0:1024] = analyzer[:1024]
        reference[4096:5120] = analyzer[1024:]
        case.write_reference([reference])
        code, result, _ = case.run("--bands", "0,6000,24000", "--no-sample-bounds")
        self.assertEqual(code, 0, result)
        self.assertEqual(result["bands"]["channels"][0]["bandsCompared"], 4)


class UsageTests(unittest.TestCase):
    def test_a_channel_count_that_differs_is_a_usage_error(self):
        case = Case(self, [noise(100, 13)], [[0, 100]])
        case.write_reference(case.channels)
        out = io.StringIO()
        args = [str(case.directory / "x.pcm.json"), "--reference", str(case.directory / "ref.raw"), "--reference-format", "float32", "--reference-channels", "2"]
        with contextlib.redirect_stderr(out):
            self.assertEqual(compare.main(args), 2)
        self.assertIn("2", out.getvalue())

    def test_no_common_position_is_a_usage_error(self):
        case = Case(self, [noise(100, 14)], [[0, 100]], reference_first=1000)
        case.write_reference(case.channels)
        code, _, error = case.run()
        self.assertEqual(code, 2)
        self.assertIn("no position", error)

    def test_range_arithmetic(self):
        self.assertEqual(compare.subtract([(0, 10), (20, 30)], [(5, 25)]), [(0, 5), (25, 30)])
        self.assertEqual(compare.intersect([(0, 10), (20, 30)], [(5, 25)]), [(5, 10), (20, 25)])
        self.assertEqual(compare.merge([(5, 8), (0, 6), (10, 12)]), [(0, 8), (10, 12)])


if __name__ == "__main__":
    unittest.main()
