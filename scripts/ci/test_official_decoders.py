#!/usr/bin/env python3
"""Tests of the Core Audio columns of the official-decoder comparison (rollout step 5): python3 scripts/ci/test_official_decoders.py"""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import compare_official_decoders as compare  # noqa: E402
import official_decode as driver  # noqa: E402


def probe_child(lines, exit_code=0, timed_out=False):
    return {"lines": lines, "exit": exit_code, "signal": None, "timedOut": timed_out, "elapsedMs": 1, "stderr": "", "linesElided": False, "lastLineTruncated": False}


def format_(rate, channels, frames_per_packet=1024):
    return {"formatID": "aac ", "sampleRate": rate, "channels": channels, "framesPerPacket": frames_per_packet}


class DriverTests(unittest.TestCase):
    def test_the_converter_record_counts_frames_and_lists_the_units_it_refuses(self):
        lines = [
            {"type": "setup", "source": "audiofile", "input": format_(44100.0, 2)},
            {"type": "converter", "status": 0, "output": {"formatID": "lpcm", "sampleRate": 44100.0, "channels": 2}},
            {"type": "unit", "index": 0, "offset": 0, "declared": 9, "received": 9, "consumed": True, "status": 0, "noMoreData": False, "frames": 0},
            {"type": "unit", "index": 1, "offset": 9, "declared": 9, "received": 9, "consumed": True, "status": 0x6E6F6D6F, "noMoreData": True, "frames": 1024},
            {"type": "unit", "index": 2, "offset": 18, "declared": 9, "received": 9, "consumed": True, "status": 0x62616461, "noMoreData": False, "frames": 0},
            {"type": "drain", "index": 3, "status": 0, "noMoreData": False, "frames": 1000},
            {"type": "end", "stoppedBy": "end", "calls": 4, "failedCalls": 1, "codedFrames": 2024},
        ]
        record = driver.coreaudio_converter_result(probe_child(lines), {})
        self.assertEqual("end", record["stoppedBy"])
        self.assertEqual(2024, record["totalSamples"])
        self.assertEqual([("no output", 0), ("refused", 2)], [(u["result"], u["index"]) for u in record["units"]])
        self.assertEqual("0x62616461", record["units"][1]["status"])
        self.assertEqual([{"rate": 44100, "channels": 2, "samples": 2024, "outputs": 1}], record["runs"])

    def test_a_converter_that_could_not_be_built_is_an_error_not_a_total(self):
        lines = [{"type": "setup", "source": "unit-list", "input": format_(44100.0, 2)}, {"type": "converter", "status": 0x666D743F, "output": {"sampleRate": 44100.0, "channels": 2}},
                 {"type": "end", "stoppedBy": "error", "converter": False}]
        record = driver.coreaudio_converter_result(probe_child(lines), {})
        self.assertFalse(record["opened"])
        self.assertEqual("error", record["stoppedBy"])
        self.assertNotIn("totalSamples", record)

    def test_a_killed_reader_leaves_a_partial_count(self):
        lines = [{"type": "open", "status": 0}, {"type": "clientFormat", "status": 0, "format": format_(44100.0, 2)}, {"type": "read", "index": 0, "status": 0, "frames": 4096}]
        record = driver.coreaudio_reader_result(probe_child(lines, exit_code=None, timed_out=True))
        self.assertEqual("timeout", record["stoppedBy"])
        self.assertEqual(4096, record["partialSamples"])
        self.assertNotIn("totalSamples", record)


class ColumnTests(unittest.TestCase):
    def test_a_total_counts_only_when_the_path_reached_the_end(self):
        done = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 9000}]}
        self.assertEqual((9000, {"rate": 44100, "channels": 2}, None), compare.ca_total(done))
        self.assertIsNone(compare.ca_total({**done, "stoppedBy": "timeout"})[0])
        self.assertIsNone(compare.ca_total({**done, "opened": False})[0])
        self.assertEqual((0, None, None), compare.ca_total({**done, "runs": []}))

    def test_totals_count_in_the_same_duration(self):
        self.assertEqual((12288, True), compare.at_rate(24576, 44100, 22050))
        self.assertEqual((24576, True), compare.at_rate(12288, 22050, 44100))

    def figure(self, analyzer, reference, rate=44100, **extra):
        return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": analyzer, "reference": reference, "referencePath": "fdk direct" if reference is not None else None,
                "referenceKind": "official", "witnesses": {}, "analyzerRates": [rate], **extra}

    def converter(self, samples, rate=44100, refused=0, units=10, **extra):
        return {"opened": True, "stoppedBy": "end", "runs": [{"rate": rate, "channels": 2, "samples": samples, "outputs": 10}] if samples else [], "units": [], "unitCount": units,
                "refusedUnits": refused, "noOutputUnits": 0, "drainFrames": 0, "input": format_(rate, 2), **extra}

    def attach(self, figure, conv, reader=None, trimmed=(), timescale=44100):
        paths = {"converter": conv}
        if reader:
            paths["reader"] = reader
        record = {"paths": {"direct": {"units": []}}, "unitList": {"timescale": timescale}}
        figures = [figure, *trimmed]
        compare.attach_coreaudio(figures, {"paths": paths}, None, record, [])
        return figures

    def test_the_owners_that_disagree_are_marked_and_explained_where_the_analyzer_equals_the_reference(self):
        figure = self.figure(65536, 65536)
        self.attach(figure, self.converter(61440))
        differences = compare.coreaudio_differences([figure], None, None, {})
        self.assertEqual(["coreaudio-differs"], [d["kind"] for d in differences])
        self.assertEqual("explained", differences[0]["status"])
        self.assertTrue(differences[0]["ownersDisagree"])

    def test_decoders_that_all_disagree_are_left_pending_for_the_lead(self):
        figure = self.figure(10000, 9000)
        self.attach(figure, self.converter(8000))
        self.assertEqual(["pending"], [d["status"] for d in compare.coreaudio_differences([figure], None, None, {})])

    def test_where_fdk_refuses_the_converter_is_the_reference(self):
        figure = self.figure(12288, None, rate=22050, noReference="FDK refuses", refused="x")
        self.attach(figure, self.converter(24576))
        self.assertEqual((12288, "coreaudio converter"), (figure["reference"], figure["referencePath"]))
        kinds = [d["kind"] for d in compare.coreaudio_differences([figure], None, None, {})]
        self.assertEqual(["rate-differs"], kinds)

    def test_a_converter_that_outputs_nothing_for_any_unit_is_no_reference(self):
        figure = self.figure(30720, None, noReference="FDK refuses", refused="x")
        self.attach(figure, self.converter(0, refused=27))
        self.assertIsNone(figure["reference"])
        differences = compare.coreaudio_differences([figure], None, None, {})
        self.assertEqual([("decoder-refuses-stream", "noted")], [(d["kind"], d["status"]) for d in differences])

    def test_core_audios_rate_for_implicit_signaling_is_noted_where_fdk_equals_the_analyzer(self):
        figure = self.figure(71680, 71680)
        self.attach(figure, self.converter(35840, rate=22050))
        differences = compare.coreaudio_differences([figure], None, None, {})
        rate = next(d for d in differences if d["kind"] == "rate-differs")
        self.assertEqual("noted", rate["status"])

    def test_the_trimmed_figure_is_held_to_the_readers_total_at_the_core_rate(self):
        trimmed = {"id": "samples.iTunSMPB", "stage": "trimmed", "analyzerFigure": "Samples (iTunSMPB)", "streams": [0], "analyzer": 33075, "reference": None, "referencePath": None,
                   "referenceKind": None, "witnesses": {}, "noReference": "waits"}
        reader = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 66150}], "packetTable": {"validFrames": 33075, "primingFrames": 2048, "remainderFrames": 717},
                  "fileFormat": format_(44100, 2)}
        figures = self.attach(self.figure(71680, 71680), self.converter(71680), reader, trimmed=[trimmed], timescale=22050)
        self.assertEqual((33075, "coreaudio reader"), (figures[1]["reference"], figures[1]["referencePath"]))
        self.assertNotIn("noReference", figures[1])
        self.assertEqual([], compare.coreaudio_differences(figures, None, None, {}))

    def test_where_both_sources_differ_the_reader_holds_only_the_one_it_applied(self):
        edit = {"id": "samples.editList", "stage": "trimmed", "analyzerFigure": "Samples (Edit List)", "streams": [0], "analyzer": 1000, "reference": None, "referencePath": None,
                "referenceKind": None, "witnesses": {}}
        smpb = {**edit, "id": "samples.iTunSMPB", "analyzerFigure": "Samples (iTunSMPB)", "analyzer": 900}
        reader = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 1000}], "packetTable": None, "fileFormat": format_(44100, 2)}
        figures = self.attach(self.figure(2048, 2048), self.converter(2048), reader, trimmed=[edit, smpb])
        self.assertEqual(1000, figures[1]["reference"])
        self.assertIsNone(figures[2]["reference"])
        self.assertIn("other source", figures[2]["noReference"])


class Ac3Tests(unittest.TestCase):
    def record(self, conv_total, reader_total=None, units=6):
        conv = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 48000, "channels": 2, "samples": conv_total}] if conv_total else [], "units": [], "unitCount": units, "refusedUnits": 0,
                "noOutputUnits": 0, "drainFrames": 0, "input": format_(48000, 2, 1536)}
        paths = {"converter": conv}
        if reader_total is not None:
            paths["reader"] = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 48000, "channels": 2, "samples": reader_total}], "packetTable": None, "fileFormat": format_(48000, 2, 1536)}
        return {"paths": paths, "unitList": {"cutUnits": 0}}

    analyzer = {"streams": [{"index": 0, "codec": "AC-3", "samples": 9216, "sampleRate": 48000, "channels": 2}], "gapless": {}, "findings": []}

    def test_the_converter_is_288_frames_short_on_a_file_that_agrees(self):
        compared = compare.compare_ac3(self.record(8928, 8928), self.analyzer, None)
        figure = compared["figures"][0]
        self.assertEqual((8928, -288, "coreaudio converter"), (figure["reference"], figure["referenceOffset"], figure["referencePath"]))
        self.assertEqual([], compared["differences"])

    def test_a_total_that_is_not_the_analyzers_less_288_is_pending(self):
        compared = compare.compare_ac3(self.record(7392), self.analyzer, None)
        self.assertEqual([("analyzer-differs", "pending")], [(d["kind"], d["status"]) for d in compared["differences"]])

    def test_a_reader_that_differs_while_the_analyzer_agrees_with_the_converter_is_explained(self):
        compared = compare.compare_ac3(self.record(8928, 7392), self.analyzer, None)
        self.assertEqual([("witness-differs", "explained")], [(d["kind"], d["status"]) for d in compared["differences"]])

    def test_a_stream_the_converter_refuses_throughout_is_no_reference(self):
        record = self.record(0)
        record["paths"]["converter"].update({"refusedUnits": 6, "units": [{"index": i, "offset": i * 10, "declared": 10, "received": 10, "result": "refused", "frames": 0, "status": "0x21646174"} for i in range(6)]})
        compared = compare.compare_ac3(record, self.analyzer, None)
        self.assertIsNone(compared["figures"][0]["reference"])
        self.assertEqual([("decoder-refuses-stream", "noted")], [(d["kind"], d["status"]) for d in compared["differences"]])

    def test_the_edit_list_of_an_mp4_file_has_no_reference(self):
        analyzer = {**self.analyzer, "gapless": {"Samples (Edit List)": 8000}}
        compared = compare.compare_ac3(self.record(8928, 8928), analyzer, None)
        trimmed = next(f for f in compared["figures"] if f["stage"] == "trimmed")
        self.assertIsNone(trimmed["reference"])
        self.assertIn("not compared", trimmed["noReference"])


if __name__ == "__main__":
    unittest.main()
