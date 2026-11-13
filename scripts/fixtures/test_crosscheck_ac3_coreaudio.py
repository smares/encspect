import array
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import crosscheck_ac3_coreaudio as c  # noqa: E402


class ComparisonLogic(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
