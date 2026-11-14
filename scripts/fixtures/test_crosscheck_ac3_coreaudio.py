import array
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import crosscheck_ac3_coreaudio as c  # noqa: E402


class DolbyReading(unittest.TestCase):
    def test_the_first_frames_dialnorm_is_read_for_ac3_and_eac3(self):
        import ac3_model
        import crosscheck_ac3_dolby_mft as d
        self.assertEqual(d.dialnorm(ac3_model.ac3_frame(dialnorm=27)), 27)
        self.assertEqual(d.dialnorm(ac3_model.eac3_frame(dialnorm=24, words=200)), 24)
        self.assertEqual(d.dialnorm(ac3_model.ac3_frame(dialnorm=0)), 31)

    def test_the_slots_follow_the_input_layout_and_a_silent_input_keeps_its_channels(self):
        import tempfile
        import crosscheck_ac3_dolby_mft as d
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "out.f32"
            frames = d.DELAY + 100
            values = array.array("f", [0.0] * (frames * 6))
            for n in range(frames):
                values[n * 6 + 2] = 0.5  # the center slot
                values[n * 6 + 4] = 0.25  # the left surround slot
            path.write_bytes(values.tobytes())
            self.assertEqual(d.slots_of(path, 6, {"acmod": 7, "lfeon": 1}), [2, 4])
            self.assertEqual(d.slots_of(path, 6, {"acmod": 2, "lfeon": 0}), [0, 1])


class Replacement(unittest.TestCase):
    def test_the_channels_a_dependent_substream_replaces_are_those_its_map_names_that_the_independent_one_has(self):
        import json
        import os
        import stat
        import tempfile
        import ac3_model
        data = ac3_model.eac3_frame(acmod=7, lfeon=1, words=200) + ac3_model.eac3_frame(strmtyp=1, acmod=2, chanmap=0x1A00, words=160)
        report = {"root": {"children": [{"name": "Channel Map", "value": "0x1A00 (left surround, right surround, left/right rear surround)"}]}}
        with tempfile.TemporaryDirectory() as folder:
            tool = Path(folder) / "tool"
            tool.write_text("#!/bin/sh\ncat <<'EOF'\n" + json.dumps(report) + "\nEOF\n")
            os.chmod(tool, os.stat(tool).st_mode | stat.S_IXUSR)
            frames = [(0, len(ac3_model.eac3_frame(acmod=7, lfeon=1, words=200))), (len(ac3_model.eac3_frame(acmod=7, lfeon=1, words=200)), 0)]
            self.assertEqual(c.replaced_names(tool, Path("x.eac3"), data, frames), ["side left", "side right"])
            self.assertEqual(c.replaced_names(tool, Path("x.eac3"), data[:frames[1][0]], frames[:1]), [])


class ComparisonLogic(unittest.TestCase):
    def test_partial_map_takes_the_independent_channels_where_nothing_correlates(self):
        import random
        rng = random.Random(1)
        ours = array.array("f", (rng.uniform(-1, 1) for _ in range(4 * 20000)))
        reference = array.array("f", (rng.uniform(-1, 1) for _ in range(2 * 20000)))
        keep, chosen, lines = c.partial_map(ours, 4, reference, 2, 0)
        self.assertEqual((keep, chosen), ([0, 1], [0, 1]))
        self.assertTrue(any("independent substream" in line for line in lines))

    def test_reorder_moves_smpte_5_1_into_the_coded_order(self):
        # frame of reference channels L R C LFE Ls Rs = 0..5; coded order L C R Ls Rs LFE
        raw = array.array("f", [0, 1, 2, 3, 4, 5, 10, 11, 12, 13, 14, 15]).tobytes()
        out = array.array("f")
        out.frombytes(c.reorder(raw, 6, c.ORDERS[6]["smpte"]))
        self.assertEqual(list(out), [0, 2, 1, 4, 5, 3, 10, 12, 11, 14, 15, 13])

    def test_candidates_cover_orders_and_first_positions(self):
        labels = [l for l, _, _ in c.candidates(6)]
        self.assertEqual(len(labels), 4)
        self.assertIn("smpte order, first position 288", labels)
        self.assertEqual(len(c.candidates(2)), 2)

    def test_first_passing_stops_at_the_first_attempt_without_problems(self):
        label, lines = c.first_passing([("a", ["largest 1"]), ("b", []), ("c", [])])
        self.assertEqual(label, "b")
        self.assertEqual(len(lines), 2)

    def test_first_passing_lists_every_attempt_when_none_passes(self):
        label, lines = c.first_passing([("a", ["x"]), ("b", ["y"])])
        self.assertIsNone(label)
        self.assertEqual(lines, ["    a: x", "    b: y"])

    def test_estimate_finds_the_priming_and_the_gain(self):
        import random
        r = random.Random(1)
        ours = array.array("f", [r.uniform(-1, 1) for _ in range(20000)])
        reference = array.array("f", [0.5 * v for v in ours[288:]])
        p0, gain, correlation = c.estimate(ours, reference, 1)
        self.assertEqual(p0, 288)
        self.assertAlmostEqual(gain, 2.0, places=4)
        self.assertAlmostEqual(correlation, 1.0, places=4)

    def test_estimate_on_two_channels_reads_channel_0(self):
        import random
        r = random.Random(2)
        left = [r.uniform(-1, 1) for _ in range(20000)]
        ours = array.array("f", [v for x in left for v in (x, 0.0)])
        reference = array.array("f", [v for x in left[100:] for v in (x, 1.0)])
        self.assertEqual(c.estimate(ours, reference, 2)[0], 100)

    def test_block_errors_names_the_block_that_differs_most(self):
        ours = array.array("f", [0.0] * (256 * 8))
        reference = array.array("f", [0.0] * (256 * 8 - 256))
        reference[3 * 256 + 5] = 0.25
        rows = c.block_errors(ours, reference, 1, 256)
        self.assertEqual(rows[0], (4, [0.25]))


if __name__ == "__main__":
    unittest.main()
