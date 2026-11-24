import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_tak_pcm as c  # noqa: E402
import tak_cases  # noqa: E402
import tak_pairs  # noqa: E402


class Intervals(unittest.TestCase):
    def test_merge_and_subtract(self):
        self.assertEqual(c.merge([[5, 9], [0, 3], [3, 4], [7, 12], [2, 2]]), [[0, 4], [5, 12]])
        self.assertEqual(c.subtract([[0, 10]], [[2, 4], [6, 7]]), [[0, 2], [4, 6], [7, 10]])
        self.assertEqual(c.subtract([[0, 10]], [[0, 10]]), [])

    def test_withheld_positions_are_the_gaps_between_runs_and_the_missing_tail(self):
        self.assertEqual(c.analyzer_withheld([[0, 100], [200, 50]], 300), [[100, 200], [250, 300]])
        self.assertEqual(c.analyzer_withheld([[0, 300]], 300), [])

    def test_a_damaged_frame_the_sdk_reports_and_the_analyzer_withholds_agree(self):
        read = {"samples": 200, "damage": [{"position": 100, "samples": 100}],
                "calls": [{"position": 100, "read": 100, "result": 3, "positionAfter": 200}]}
        agree, text = c.damaged_report(Path("x.tak"), read, 300, [[0, 100], [200, 100]])
        self.assertTrue(agree)
        self.assertIn("agree", text)

    def test_a_difference_is_listed(self):
        read = {"samples": 300, "damage": [], "calls": []}
        agree, text = c.damaged_report(Path("x.tak"), read, 300, [[0, 100], [200, 100]])
        self.assertFalse(agree)
        self.assertIn("only the analyzer: [[100, 200]]", text)

    def test_a_file_the_library_refuses_is_reported_without_a_verdict(self):
        agree, text = c.damaged_report(Path("x.tak"), {"error": "no"}, 300, [])
        self.assertIsNone(agree)

    def test_the_read_of_an_encoder_written_file_must_give_the_manifest_samples(self):
        manifest = {"decoded_pcm_md5": "aa", "decoded_samples": 10}
        good = {"sdkRead": {"pcmMd5": "aa", "samples": 10, "calls": [{"result": 0}], "damage": []}}
        self.assertEqual(c.read_problems(manifest, good), [])
        self.assertTrue(c.read_problems(manifest, {"sdkRead": {"pcmMd5": "bb", "samples": 10, "calls": [], "damage": []}}))
        self.assertEqual(c.read_problems(manifest, {}), [])


class Pairs(unittest.TestCase):
    def test_a_table_with_paired_entries_is_read(self):
        # 1 (table), E - 1 = 2, entry (channel 0, alone), entry (channel 1, paired, type 2, partner 0), entry (channel 2, paired, type 1, partner 3), as LSB-first bits.
        fields = [(1, 1), (2, 4), (0, 4), (0, 1), (1, 4), (1, 1), (2, 2), (0, 4), (2, 4), (1, 1), (1, 2), (3, 4)]
        value = shift = 0
        for item, width in fields:
            value |= item << shift
            shift += width
        data = value.to_bytes((shift + 7) // 8 + 8, "little")
        self.assertEqual(tak_pairs.read_table(data, 0, 6), [(0, None, None), (1, 2, 0), (2, 1, 3)])
        self.assertIsNone(tak_pairs.read_table(bytes(8), 0, 6))

    def test_the_committed_six_channel_fixtures_have_a_table_in_every_frame_and_no_pair(self):
        path = HERE.parents[1] / "tests" / "fixtures" / "tak" / "external-format-5.1-mask.tak"
        found = tak_pairs.summarize(path)
        self.assertEqual((found["codec"], found["channels"], found["pairedFrames"]), (4, 6, 0))
        self.assertEqual(found["tableFrames"], found["frames"])

    def test_the_pair_signals_keep_their_partners_related(self):
        rate, channels, bits, frames, kind, _ = tak_cases.INPUTS["pairs-identical"]
        values = tak_cases.pair_samples(rate, bits, 50, kind)
        self.assertEqual(values[0::2][:50 * 3], values[1::2][:50 * 3])
        noisy = tak_cases.pair_samples(rate, bits, 50, "pairs-noisy")
        self.assertTrue(all(abs(a - b) <= 2 for a, b in zip(noisy[0::2], noisy[1::2])))
        names = {case["name"] for case in tak_cases.cases()}
        self.assertEqual(len([n for n in names if n.startswith("pair-")]), 9)


if __name__ == "__main__":
    unittest.main()
