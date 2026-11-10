#!/usr/bin/env python3
"""Tests of the Core Audio and Media Foundation columns of the official-decoder comparison (rollout steps 5 and 6): python3 scripts/ci/test_official_decoders.py"""

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

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
        record = driver.coreaudio_converter_result(probe_child(lines))
        self.assertEqual("end", record["stoppedBy"])
        self.assertEqual(2024, record["totalSamples"])
        self.assertEqual([("no output", 0), ("refused", 2)], [(u["result"], u["index"]) for u in record["units"]])
        self.assertEqual("0x62616461", record["units"][1]["status"])
        self.assertEqual([{"rate": 44100, "channels": 2, "samples": 2024, "outputs": 1}], record["runs"])

    def test_a_converter_that_could_not_be_built_is_an_error_not_a_total(self):
        lines = [{"type": "setup", "source": "unit-list", "input": format_(44100.0, 2)}, {"type": "converter", "status": 0x666D743F, "output": {"sampleRate": 44100.0, "channels": 2}},
                 {"type": "end", "stoppedBy": "error", "converter": False}]
        record = driver.coreaudio_converter_result(probe_child(lines))
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
        compare.attach_coreaudio(figures, {"paths": paths}, record)
        return figures

    def test_the_owners_that_disagree_are_marked_and_explained_where_the_analyzer_equals_the_reference(self):
        figure = self.figure(65536, 65536)
        self.attach(figure, self.converter(61440))
        differences = compare.coreaudio_differences([figure])
        self.assertEqual(["coreaudio-differs"], [d["kind"] for d in differences])
        self.assertEqual("explained", differences[0]["status"])
        self.assertTrue(differences[0]["ownersDisagree"])

    def test_a_converter_that_outputs_part_of_a_lost_unit_is_noted_with_both_figures_and_no_cause(self):
        for lost, c, status, kind in ((2048, 3072, "noted", "outputs-lost-samples"), (2048, 1024, "pending", "coreaudio-differs"), (2048, 2049, "noted", "outputs-lost-samples"), (0, 3072, "pending", "coreaudio-differs")):
            figure = self.figure(4096, 2048, lostSamples=lost)
            self.attach(figure, self.converter(c))
            figure["lostSamples"] = lost
            differences = compare.coreaudio_differences([figure])
            self.assertEqual([(kind, status)], [(d["kind"], d["status"]) for d in differences], (lost, c))
        figure = self.figure(4096, 2048)
        self.attach(figure, self.converter(3072))
        figure["lostSamples"] = 2048
        reason = compare.coreaudio_differences([figure])[0]["reason"]
        self.assertIn("3,072", reason)
        self.assertIn("2,048", reason)
        self.assertNotIn("block", reason)

    def test_decoders_that_all_disagree_are_left_pending_for_the_lead(self):
        figure = self.figure(10000, 9000)
        self.attach(figure, self.converter(8000))
        self.assertEqual(["pending"], [d["status"] for d in compare.coreaudio_differences([figure])])

    def test_where_fdk_refuses_the_converter_is_the_reference(self):
        figure = self.figure(12288, None, rate=22050, noReference="FDK refuses", refused="x")
        self.attach(figure, self.converter(24576))
        self.assertEqual((12288, "coreaudio converter"), (figure["reference"], figure["referencePath"]))
        kinds = [d["kind"] for d in compare.coreaudio_differences([figure])]
        self.assertEqual(["rate-differs"], kinds)

    def test_a_converter_that_outputs_nothing_for_any_unit_is_no_reference(self):
        figure = self.figure(30720, None, noReference="FDK refuses", refused="x")
        self.attach(figure, self.converter(0, refused=27))
        self.assertIsNone(figure["reference"])
        differences = compare.coreaudio_differences([figure])
        self.assertEqual([("decoder-refuses-stream", "noted")], [(d["kind"], d["status"]) for d in differences])

    def test_core_audios_rate_for_implicit_signaling_is_noted_where_fdk_equals_the_analyzer(self):
        figure = self.figure(71680, 71680)
        self.attach(figure, self.converter(35840, rate=22050))
        differences = compare.coreaudio_differences([figure])
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
        self.assertEqual([], compare.coreaudio_differences(figures))

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

    def test_a_cut_frame_counted_and_lost_notes_the_converters_total_with_both_figures_and_explains_the_reader(self):
        analyzer = {**self.analyzer, "streams": [{**self.analyzer["streams"][0], "lostSamples": 1536}]}
        compared = compare.compare_ac3(self.record(8928, 7392), analyzer, None)
        by_kind = {d["kind"]: d for d in compared["differences"]}
        self.assertEqual(("noted", 1536), (by_kind["outputs-lost-samples"]["status"], by_kind["outputs-lost-samples"]["analyzerLost"]))
        self.assertIn("8,928", by_kind["outputs-lost-samples"]["reason"])
        self.assertIn("7,680", by_kind["outputs-lost-samples"]["reason"])
        self.assertEqual("explained", by_kind["witness-differs"]["status"])
        self.assertNotIn("alignment", by_kind["witness-differs"])

    def test_no_lost_samples_leave_the_converters_total_without_a_note(self):
        compared = compare.compare_ac3(self.record(8928, 8928), self.analyzer, None)
        self.assertNotIn("outputs-lost-samples", [d["kind"] for d in compared["differences"]])

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


class PartialTests(unittest.TestCase):
    def test_a_converter_total_is_kept_out_of_a_figure_with_several_rates_and_no_common_one(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0, 1], "analyzer": 100, "reference": None, "referencePath": None, "referenceKind": None,
                  "witnesses": {}, "analyzerRates": [44100, 48000]}
        record = {"paths": {"direct": {"units": []}}, "unitList": {"timescale": 44100}}
        conv = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 100}], "units": [], "unitCount": 1, "refusedUnits": 0, "noOutputUnits": 0, "drainFrames": 0}
        compare.attach_coreaudio([figure], {"paths": {"converter": conv}}, record)
        self.assertIsNone(figure["coreAudio"]["converter"]["samples"])
        self.assertIsNone(figure["reference"])

    def test_the_converter_as_reference_drops_the_refusal_text_of_fdk(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 12288, "reference": None, "referencePath": None, "referenceKind": None,
                  "witnesses": {}, "analyzerRates": [22050], "refused": "x", "noReference": "FDK refuses the stream"}
        record = {"paths": {"direct": {"units": []}}, "unitList": {"timescale": 22050}}
        conv = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 24576}], "units": [], "unitCount": 12, "refusedUnits": 0, "noOutputUnits": 0, "drainFrames": 0}
        compare.attach_coreaudio([figure], {"paths": {"converter": conv}}, record)
        self.assertEqual("coreaudio converter", figure["referencePath"])
        self.assertNotIn("noReference", figure)

    def test_frames_of_several_blocks_keep_the_converter_from_being_the_reference(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 30720, "reference": None, "referencePath": None, "referenceKind": None,
                  "witnesses": {}, "analyzerRates": [44100], "noReference": "FDK holds blocks"}
        record = {"paths": {"direct": {"units": [{"blocks": 3}]}}, "unitList": {}}
        conv = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 10240}], "units": [], "unitCount": 10, "refusedUnits": 0, "noOutputUnits": 0, "drainFrames": 0}
        compare.attach_coreaudio([figure], {"paths": {"converter": conv}}, record)
        self.assertIsNone(figure["reference"])
        differences = compare.coreaudio_differences([figure])
        self.assertEqual([("decoder-one-block-per-frame", "noted")], [(d["kind"], d["status"]) for d in differences])

    def coded(self, analyzer, reader_samples, priming, reference=None, rate=44100, timescale=44100):
        reader = {"samples": reader_samples, "packetTable": {"validFrames": reader_samples * timescale // rate, "primingFrames": priming, "remainderFrames": 0}}
        return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": analyzer, "reference": analyzer if reference is None else reference,
                "referencePath": "fdk direct", "referenceKind": "official", "witnesses": {}, "analyzerRates": [rate], "coreAudio": {"reader": reader}}

    def test_a_reader_short_by_exactly_its_priming_is_reader_priming(self):
        figure = self.coded(20480, 20480 - 2112, 2112)
        self.assertEqual([("reader-priming", "noted")], [(d["kind"], d["status"]) for d in compare.coreaudio_differences([figure])])

    def test_the_priming_counts_at_the_output_rate_for_sbr(self):
        figure = self.coded(61440, 61440 - 4224, 2112, rate=44100, timescale=22050)
        self.assertEqual(["reader-priming"], [d["kind"] for d in compare.coreaudio_differences([figure])])

    def test_a_reader_that_is_short_by_more_than_its_priming_is_a_witness_difference(self):
        figure = self.coded(20480, 20480 - 2112 - 1024, 2112)
        self.assertEqual([("witness-differs", "explained")], [(d["kind"], d["status"]) for d in compare.coreaudio_differences([figure])])
        figure = self.coded(20480, 20480 - 2112 - 1024, 2112, reference=19000)
        self.assertEqual([("witness-differs", "noted")], [(d["kind"], d["status"]) for d in compare.coreaudio_differences([figure])])

    def test_rate_differs_is_noted_only_where_fdk_equals_the_analyzer(self):
        def figure(reference):
            return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 71680, "reference": reference, "referencePath": "fdk direct",
                    "witnesses": {}, "analyzerRates": [44100], "coreAudio": {"converter": {"samples": 35840, "decoderSamples": 35840, "format": {"rate": 22050, "channels": 2}}}}
        agrees = [d for d in compare.coreaudio_differences([figure(71680)]) if d["kind"] == "rate-differs"]
        disagrees = [d for d in compare.coreaudio_differences([figure(70000)]) if d["kind"] == "rate-differs"]
        self.assertEqual("noted", agrees[0]["status"])
        self.assertEqual("pending", disagrees[0]["status"])


class UnitVerdictTests(unittest.TestCase):
    analyzer = {"findings": [{"code": "aac.crc-mismatch", "severity": "Warning", "offset": 10, "length": 2, "count": 1},
                             {"code": "ac3.crc1-mismatch", "severity": "Warning", "offset": 10, "length": 2, "count": 1},
                             {"code": "ac3.leading-junk", "severity": "Warning", "offset": 500, "length": 4, "count": 1}]}

    def conv(self, units, runs=True):
        return {"stoppedBy": "end", "runs": [{"rate": 44100, "channels": 2, "samples": 1}] if runs else [], "unitCount": 10, "units": units}

    def unit(self, index, offset, result, status=None, received=20):
        return {"index": index, "offset": offset, "declared": received, "received": received, "result": result, "frames": 0, **({"status": status} if status else {})}

    def test_a_refused_unit_with_a_finding_at_its_bytes_agrees(self):
        units = compare.coreaudio_unit_verdicts({"paths": {"converter": self.conv([self.unit(1, 5, "refused", "0x62616461")])}}, self.analyzer)
        self.assertEqual([("agree", "refused 0x62616461", "aac.crc-mismatch")], [(u["status"], u["decoder"], u["finding"]) for u in units])
        self.assertEqual("coreaudio converter", units[0]["source"])

    def test_a_unit_that_gives_no_output_or_was_not_fed_has_no_verdict_in_aac(self):
        units = compare.coreaudio_unit_verdicts({"paths": {"converter": self.conv([self.unit(0, 5, "no output"), self.unit(2, 200, "not fed", received=0)])}}, self.analyzer)
        self.assertEqual([], units)

    def test_a_refused_unit_without_a_finding_is_pending_unless_the_file_has_merged_findings_or_the_stream_is_refused(self):
        refused = self.unit(3, 300, "refused", "0x62616461")
        self.assertEqual(["pending"], [u["status"] for u in compare.coreaudio_unit_verdicts({"paths": {"converter": self.conv([refused])}}, self.analyzer)])
        merged = {"findings": [{"code": "aac.crc-mismatch", "severity": "Warning", "offset": 10, "length": 2, "count": 4}]}
        self.assertEqual(["noted"], [u["status"] for u in compare.coreaudio_unit_verdicts({"paths": {"converter": self.conv([refused])}}, merged)])
        self.assertEqual([], compare.coreaudio_unit_verdicts({"paths": {"converter": self.conv([refused], runs=False)}}, self.analyzer))

    def test_ac3_units_follow_the_same_rules_and_list_the_findings_where_the_converter_decodes(self):
        conv = self.conv([self.unit(0, 8, "no output"), self.unit(4, 300, "refused", "0x21646174"), self.unit(5, 900, "no output")])
        units = compare.ac3_unit_verdicts(conv, self.analyzer)
        self.assertEqual({"agree"}, {u["status"] for u in units if u["index"] == 0})
        self.assertEqual("no output", next(u["decoder"] for u in units if u["index"] == 0))
        self.assertEqual(["pending"], [u["status"] for u in units if u["index"] == 4])
        self.assertEqual(1, sum(1 for u in units if u["index"] is None))  # the junk at 500: no unit holds it
        self.assertFalse(any(u["index"] == 5 for u in units))


class WriteOfficialTests(unittest.TestCase):
    def write(self, previous, results, environment, format_="ac3"):
        with tempfile.TemporaryDirectory() as tmp:
            target = pathlib.Path(tmp) / "new.json"
            old = pathlib.Path(tmp) / "old.json"
            old.write_text(json.dumps(previous), encoding="utf-8")
            compare.write_official(target, old, results, environment, ["ffmpeg version 8.0.1"], "2", format_)
            return json.loads(target.read_text(encoding="utf-8"))

    def figure(self, reference, converter):
        return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 9216, "reference": reference, "referencePath": "coreaudio converter",
                "referenceKind": "official", "witnesses": {}, "analyzerRates": [48000], "referenceOffset": -288, "coreAudio": {"converter": {"samples": converter}}}

    def previous(self):
        mac = {"macOS": "15.8.2", "build": "24H232", "machine": "arm64", "audioCodecs": "7.0", "audioToolbox": "1.14", "runId": "1", "commit": "a"}
        old_entry = {"path": "a.ac3", "sha256": "ff", "figures": [self.figure(8928, 8928)], "differences": [{"figure": "samples", "decoder": "ffmpeg", "kind": "witness-differs", "value": 6144,
                     "analyzer": 9216, "status": "explained", "reason": "r", "statusSource": "rule"}], "units": []}
        return {"run": {"id": "1", "coreAudio": mac}, "files": [old_entry]}, mac

    def result(self, reference, diffs):
        return {"file": "a.ac3", "sha256": "ff", "derived": None, "figures": [self.figure(reference, reference)], "differences": diffs, "units": [], "unitList": {"kind": "ac3"}, "codecs": ["AC-3"]}

    def test_a_figure_that_changes_with_the_macos_build_is_marked_build_dependent_and_stays(self):
        previous, mac = self.previous()
        fresh = {"figure": "samples", "decoder": "coreaudio converter", "value": 9216, "analyzer": 9216, "status": "pending", "kind": "analyzer-differs", "reason": "x"}
        written = self.write(previous, [self.result(9216, [fresh])], {"coreAudio": {**mac, "macOS": "15.9", "build": "24J1"}, "runId": "2", "imageVersion": "new"})
        entry = written["files"][0]
        self.assertEqual(8928, entry["figures"][0]["reference"])
        self.assertEqual(8928, entry["figures"][0]["coreAudio"]["converter"]["samples"])
        self.assertEqual(["build-dependent", "witness-differs"], sorted(d["kind"] for d in entry["differences"]))
        marked = next(d for d in entry["differences"] if d["kind"] == "build-dependent")
        self.assertEqual((9216, 8928, "build-dependent"), (marked["value"], marked["reference"], marked["status"]))
        self.assertEqual("24J1", written["run"]["coreAudio"]["build"])
        self.assertEqual({"files": 1, "fed": 1, "coreAudioCarried": 0}, written["counts"])

    def test_the_same_build_replaces_the_figure_and_lists_it_for_review(self):
        previous, mac = self.previous()
        written = self.write(previous, [self.result(9216, [])], {"coreAudio": {**mac, "runId": "2"}, "runId": "2"})
        self.assertEqual(9216, written["files"][0]["figures"][0]["reference"])
        self.assertEqual(["a.ac3 samples: 8928 -> 9216"], written["changedFiguresForReview"])

    def test_fdks_packages_decide_build_dependence_for_aac(self):
        previous, mac = self.previous()
        previous["run"]["fdkAac"] = {"version": "v2.0.2"}
        figure = self.figure(100, 100)
        figure["referencePath"] = "fdk direct"
        previous["files"][0]["figures"][0].update({"reference": 90, "referencePath": "fdk direct"})
        result = {**self.result(100, []), "figures": [figure]}
        written = self.write(previous, [result], {"fdkAac": {"version": "v3.0.0"}, "coreAudio": mac, "runId": "2"}, "aac")
        self.assertIn("build-dependent", [d["kind"] for d in written["files"][0]["differences"]])
        self.assertEqual(90, written["files"][0]["figures"][0]["reference"])

    def test_a_file_the_run_did_not_decode_keeps_its_committed_entry(self):
        previous, mac = self.previous()
        written = self.write(previous, [{"file": "a.ac3", "sha256": "ff", "derived": None, "carriedEntry": previous["files"][0]}], {"coreAudio": mac, "runId": "2"})
        self.assertEqual(previous["files"][0], written["files"][0])

    def test_reviewed_statuses_and_the_core_audio_run_record_are_kept(self):
        previous, mac = self.previous()
        diff = previous["files"][0]["differences"][0]
        diff.update({"status": "keep", "note": "decided", "statusSource": "reviewed"})
        fresh = {key: diff[key] for key in ("figure", "decoder", "kind", "value", "analyzer", "reason")} | {"status": "explained"}
        written = self.write(previous, [self.result(8928, [fresh])], {"coreAudio": mac, "runId": "2"})
        self.assertEqual(("keep", "decided", "reviewed"), tuple(written["files"][0]["differences"][0][k] for k in ("status", "note", "statusSource")))
        self.assertEqual(mac, written["run"]["coreAudio"])


class CarryTests(unittest.TestCase):
    def test_a_file_without_a_macos_record_keeps_the_core_audio_columns_of_the_committed_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = pathlib.Path(tmp) / "official-decoders.json"
            old_figure = {"id": "samples.iTunSMPB", "stage": "trimmed", "reference": 33075, "referencePath": "coreaudio reader", "referenceKind": "official", "format": {"rate": 22050, "channels": 2},
                          "coreAudio": {"reader": {"samples": 33075}}}
            kept = {"figure": "samples.iTunSMPB", "decoder": "coreaudio reader", "kind": "reader-priming", "status": "noted"}
            source.write_text(json.dumps({"files": [{"path": "a.m4a", "sha256": "ab", "figures": [old_figure], "differences": [kept], "units": [{"source": "coreaudio converter", "offset": 1}]}]}), encoding="utf-8")
            result = {"file": "a.m4a", "sha256": "ab", "coreAudioMissing": True, "figures": [{"id": "samples.iTunSMPB", "stage": "trimmed", "reference": None, "noReference": "no Core Audio record"}],
                      "differences": [], "units": []}
            compare.carry_coreaudio([result], source)
        self.assertEqual(33075, result["figures"][0]["reference"])
        self.assertNotIn("noReference", result["figures"][0])
        self.assertEqual([kept], result["differences"])
        self.assertEqual(1, len(result["units"]))
        self.assertTrue(result["coreAudioCarried"])


class ProvenanceTests(unittest.TestCase):
    def committed(self, tmp, carried_from=None):
        entry = {"path": "a.m4a", "sha256": "ab", "figures": [{"id": "samples", "stage": "coded", "coreAudio": {"converter": {"samples": 5}}}], "differences": [], "units": []}
        if carried_from:
            entry["coreAudioFrom"] = carried_from
        source = pathlib.Path(tmp) / "official-decoders.json"
        source.write_text(json.dumps({"run": {"coreAudio": {"runId": "7", "commit": "abcdef0"}}, "files": [entry]}), encoding="utf-8")
        return source, entry

    def test_carried_data_say_which_run_they_come_from(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, _ = self.committed(tmp)
            result = {"file": "a.m4a", "sha256": "ab", "coreAudioMissing": True, "figures": [{"id": "samples", "stage": "coded", "reference": 1}], "differences": [], "units": []}
            compare.carry_coreaudio([result], source)
        self.assertEqual({"run": "7", "commit": "abcdef0"}, result["coreAudioFrom"])

    def test_data_carried_twice_keep_their_first_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, entry = self.committed(tmp, {"run": "3", "commit": "1111111"})
            self.assertEqual({"run": "3", "commit": "1111111"}, compare.carried_from(entry, json.loads(source.read_text(encoding="utf-8"))))

    def test_a_carried_ac3_entry_is_marked_with_its_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, entry = self.committed(tmp)
            carried = compare.carried_ac3("a.ac3", "ab", entry, json.loads(source.read_text(encoding="utf-8")))
        self.assertEqual({"run": "7", "commit": "abcdef0"}, carried["carriedEntry"]["coreAudioFrom"])
        self.assertEqual(entry["figures"], carried["carriedEntry"]["figures"])

    def test_write_official_records_the_provenance_and_counts_it(self):
        mac = {"macOS": "15.8.2", "build": "b", "machine": "arm64", "audioCodecs": "7", "audioToolbox": "1", "runId": "9", "commit": "c"}
        fresh = {"file": "b.ac3", "sha256": "cd", "derived": None, "figures": [], "differences": [], "units": [], "unitList": {"kind": "ac3"}}
        carried = {"file": "a.ac3", "sha256": "ab", "derived": None, "carriedEntry": {"path": "a.ac3", "sha256": "ab", "figures": [], "differences": [], "units": [], "coreAudioFrom": {"run": "7", "commit": "abcdef0"}}}
        with tempfile.TemporaryDirectory() as tmp:
            target = pathlib.Path(tmp) / "new.json"
            compare.write_official(target, pathlib.Path(tmp) / "none.json", [carried, fresh], {"coreAudio": mac, "runId": "9"}, ["ffmpeg version 8.0.1"], "9", "ac3")
            written = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual({"files": 2, "fed": 2, "coreAudioCarried": 1}, written["counts"])
        self.assertEqual([True, False], ["coreAudioFrom" in f for f in written["files"]])

    def test_write_official_keeps_the_provenance_of_an_aac_entry_that_carries_core_audio_data(self):
        mac = {"macOS": "15.8.2", "build": "b", "machine": "arm64", "audioCodecs": "7", "audioToolbox": "1", "runId": "9", "commit": "c"}
        result = {"file": "a.m4a", "sha256": "ab", "derived": None, "figures": [], "differences": [], "units": [], "unitList": {"kind": "mp4"}, "coreAudioFrom": {"run": "7", "commit": "abcdef0"}}
        with tempfile.TemporaryDirectory() as tmp:
            target = pathlib.Path(tmp) / "new.json"
            compare.write_official(target, pathlib.Path(tmp) / "none.json", [result], {"coreAudio": mac, "runId": "9"}, ["ffmpeg version 8.0.1"], "9", "aac")
            written = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual({"run": "7", "commit": "abcdef0"}, written["files"][0]["coreAudioFrom"])
        self.assertEqual(1, written["counts"]["coreAudioCarried"])

    def test_files_missing_from_the_macos_run_are_listed_and_the_carried_ones_noted(self):
        results = [{"folder": "aac", "file": "a.m4a", "coreAudioMissing": True, "coreAudioCarried": True}, {"folder": "mp4", "file": "b.m4a", "coreAudioMissing": True},
                   {"folder": "aac", "file": "c.m4a", "coreAudioMissing": False}]
        self.assertEqual(["coreaudio aac/a.m4a (carried from the committed file)", "coreaudio mp4/b.m4a"], compare.coreaudio_not_run(results))

    def test_a_downloaded_sample_with_records_and_no_file_is_not_called_another_version(self):
        records = {"s1": {"file": "gone.mp4", "derived": {"downloaded": True}}, "s2": {"file": "old.aac"}, "s3": {"file": "kept.mp4", "derived": {"downloaded": True}}}
        self.assertEqual((["old.aac"], ["gone.mp4"]), compare.records_without_file(records, {"s3"}))

    def result(self, folder, name, carried, converter):
        figure = {"id": "samples", "stage": "coded", "analyzer": 10, "reference": 10, "referencePath": "fdk direct", "witnesses": {}, "coreAudio": {"converter": {"samples": converter}}}
        return {"folder": folder, "file": name, "sha256": "ab", "figures": [figure], "units": [], "differences": [], "notes": [], "derived": None, "coreAudioCarried": carried}

    def test_the_agreement_lines_leave_out_carried_figures_and_list_them(self):
        class Args:
            run_id = "1"
        text = compare.render_aac([self.result("aac", "fresh.m4a", False, 10), self.result("aac", "old.m4a", True, 99)], [], [], {}, ["ffmpeg"], [], Args())
        line = next(l for l in text.splitlines() if l.strip().startswith("aac, apple") and "converter totals" in l)
        self.assertIn("converter totals   1, equal to the analyzer   1", line)
        self.assertIn("carried from the committed files, not counted above: 1 files (aac/old.m4a)", text)


class DriverNotFedTests(unittest.TestCase):
    def test_a_unit_with_no_byte_in_the_file_is_not_fed_and_not_a_unit_without_output(self):
        lines = [
            {"type": "setup", "source": "audiofile", "input": format_(44100.0, 2)},
            {"type": "converter", "status": 0, "output": {"formatID": "lpcm", "sampleRate": 44100.0, "channels": 2}},
            {"type": "unit", "index": 0, "offset": 0, "declared": 9, "received": 9, "consumed": True, "status": 0, "noMoreData": False, "frames": 1024},
            {"type": "unit", "index": 1, "offset": 9, "declared": 9, "received": 0, "consumed": False, "notFed": True, "status": 0, "noMoreData": False, "frames": 0},
            {"type": "end", "stoppedBy": "end", "calls": 2, "failedCalls": 0, "codedFrames": 1024},
        ]
        record = driver.coreaudio_converter_result(probe_child(lines))
        self.assertEqual((1, 0, 0), (record["notFedUnits"], record["noOutputUnits"], record["refusedUnits"]))
        self.assertEqual(["not fed"], [u["result"] for u in record["units"]])


class Ac3ReaderTests(unittest.TestCase):
    def test_the_reader_witness_is_only_explained_where_the_analyzer_equals_the_converters_total_plus_the_constant(self):
        conv = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 48000, "channels": 2, "samples": 7000}], "units": [], "unitCount": 6, "refusedUnits": 0, "noOutputUnits": 0, "drainFrames": 0, "input": format_(48000, 2, 1536)}
        reader = {"opened": True, "stoppedBy": "end", "runs": [{"rate": 48000, "channels": 2, "samples": 7392}], "packetTable": None, "fileFormat": format_(48000, 2, 1536)}
        analyzer = {"streams": [{"index": 0, "codec": "AC-3", "samples": 9216, "sampleRate": 48000, "channels": 2}], "gapless": {}, "findings": []}
        compared = compare.compare_ac3({"paths": {"converter": conv, "reader": reader}, "unitList": {"cutUnits": 0}}, analyzer, None)
        statuses = {d["decoder"]: d["status"] for d in compared["differences"]}
        self.assertEqual("pending", statuses["coreaudio converter"])
        self.assertEqual("noted", statuses["coreaudio reader"])


class PartialRunTests(unittest.TestCase):
    """run_aac_comparison on one synthetic fixture: the macOS run has no record of it, or of a downloaded sample the job cannot fetch."""

    def run_comparison(self, ca_records):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            media = tmp / "aac" / "one.m4a"
            media.parent.mkdir()
            media.write_bytes(b"x")
            sha = compare.sha256_of(media)
            committed = {"files": [{"path": "one.m4a", "sha256": sha, "figures": [{"id": "samples", "stage": "coded", "coreAudio": {"converter": {"samples": 5}}}], "differences": [], "units": []}],
                         "run": {"coreAudio": {"runId": "7", "commit": "abcdef0"}}}
            (tmp / "aac" / "official-decoders.json").write_text(json.dumps(committed), encoding="utf-8")
            record = {"bytes": 1, "unitList": {"kind": "mp4"}, "paths": {"direct": {"tool": "t", "stoppedBy": "end"}}, "file": "one.m4a"}
            records = {"aac": {sha: record, "gone": {"file": "sample.mp4", "derived": {"downloaded": True}, "bytes": 1, "unitList": {}, "paths": {}}}, "coreaudio-aac": ca_records}
            args = mock.Mock(no_ffmpeg=True, encspect=None, downloaded=None)
            fresh = {"figures": [{"id": "samples", "stage": "coded", "reference": None}], "differences": [], "units": [], "notes": [], "codecs": []}
            with mock.patch.multiple(compare, FIXTURES=tmp, compare_aac=lambda *a: dict(fresh), aac_ffmpeg=lambda *a: None, render_aac=lambda *a: "text",
                                     load_aac_analyzer=lambda p, c: ({"streams": [], "gapless": {}, "findings": []}, "golden")), \
                    mock.patch.object(driver, "AAC_FOLDERS", ("aac",)), mock.patch.object(compare.probe, "files", lambda folder, ext: [media] if folder == "aac" else []), \
                    mock.patch.object(compare.derive_variants, "write_aac", lambda out: []):
                return compare.run_aac_comparison(args, records, [{"format": "aac"}], ["ffmpeg version 8.0.1"], [], tmp)

    def test_a_file_the_macos_run_has_no_record_of_is_listed_and_carries_its_provenance(self):
        _, document, results, _ = self.run_comparison({"someone-else": {"file": "x"}})
        self.assertIn("coreaudio aac/one.m4a (carried from the committed file)", document["notRun"])
        self.assertEqual({"run": "7", "commit": "abcdef0"}, results[0]["coreAudioFrom"])

    def run_windows_comparison(self, mf_records, committed_figure, ca_records=None, fresh_reference=None, findings=()):
        """run_aac_comparison on one synthetic fixture with a committed entry that has Media Foundation (and Core Audio) data, the way a partial run meets it."""
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            media = tmp / "aac" / "one.m4a"
            media.parent.mkdir()
            media.write_bytes(b"x")
            sha = compare.sha256_of(media)
            committed = {"files": [{"path": "one.m4a", "sha256": sha, "figures": [committed_figure], "differences": [], "units": []}],
                         "run": {"coreAudio": {"runId": "7", "commit": "abcdef0"}, "mediaFoundation": {"runId": "8", "commit": "1234567"}}}
            (tmp / "aac" / "official-decoders.json").write_text(json.dumps(committed), encoding="utf-8")
            record = {"bytes": 1, "unitList": {"kind": "mp4"}, "paths": {"direct": {"tool": "t", "stoppedBy": "end"}}, "file": "one.m4a"}
            records = {"aac": {sha: record}, "coreaudio-aac": ca_records or {}, "mf-aac": {k.replace("SHA", sha): v for k, v in mf_records.items()}}
            args = mock.Mock(no_ffmpeg=True, encspect=None, downloaded=None)
            figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 100, "reference": fresh_reference, "referencePath": "fdk direct" if fresh_reference else None,
                      "referenceKind": "official", "witnesses": {}, "analyzerRates": [44100], "noReference": "FDK refuses"}
            fresh = {"figures": [figure], "differences": [], "units": [], "notes": [], "codecs": []}
            analyzer = {"streams": [{"index": 0, "codec": "AAC LC", "samples": 100, "sampleRate": 44100, "channels": 2}], "gapless": {}, "findings": list(findings)}
            with mock.patch.multiple(compare, FIXTURES=tmp, compare_aac=lambda *a: {**fresh, "figures": [dict(figure)]}, aac_ffmpeg=lambda *a: None, render_aac=lambda *a: "text",
                                     load_aac_analyzer=lambda p, c: (analyzer, "golden")), \
                    mock.patch.object(driver, "AAC_FOLDERS", ("aac",)), mock.patch.object(compare.probe, "files", lambda folder, ext: [media] if folder == "aac" else []), \
                    mock.patch.object(compare.derive_variants, "write_aac", lambda out: []):
                return compare.run_aac_comparison(args, records, [{"format": "aac"}, {"format": "mediafoundation", "runId": "9", "commit": "abc", "currentBuild": "26100", "updateBuildRevision": 1}], ["ffmpeg version 8.0.1"], [], tmp)

    def mf_record(self, samples):
        return {"bytes": 1, "unitList": {"kind": "mp4"}, "file": "one.m4a", "sha256": "SHA", "paths": {"reader": mf_path(samples), "transform": mf_path(samples), "transformWithoutTimestamps": mf_path(samples)}}

    def test_a_windows_record_gives_the_media_foundation_column_and_a_file_without_one_carries_the_committed_data(self):
        committed_figure = {"id": "samples", "stage": "coded", "reference": None, "mediaFoundation": {"transform": {"samples": 5}}}
        _, document, results, _ = self.run_windows_comparison({"SHA": self.mf_record(90)}, committed_figure)
        self.assertEqual(90, results[0]["figures"][0]["mediaFoundation"]["transform"]["samples"])
        self.assertEqual("media foundation transform", results[0]["figures"][0]["referencePath"])
        self.assertNotIn("mediaFoundationFrom", results[0])
        self.assertFalse(any(line.startswith("mediafoundation ") for line in document["notRun"]))
        _, document, results, _ = self.run_windows_comparison({"other": self.mf_record(90)}, committed_figure)
        self.assertIn("mediafoundation aac/one.m4a (carried from the committed file)", document["notRun"])
        self.assertEqual({"run": "8", "commit": "1234567"}, results[0]["mediaFoundationFrom"])
        self.assertEqual({"transform": {"samples": 5}}, results[0]["figures"][0]["mediaFoundation"])

    def test_the_facts_of_the_transforms_output_reach_the_column(self):
        transform = {**mf_path(2048), "noOutputList": [0, 1], "streamChangeUnits": [1, 2], "timeline": {"first": {"unit": 2, "time": 5, "duration": 7, "frames": 1024}, "drain": [{"time": 9, "duration": 7, "frames": 1024}]}}
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 2048, "reference": 2048, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"transform": transform}}, {"paths": {"direct": {"units": []}}, "unitList": {}})
        entry = figure["mediaFoundation"]["transform"]
        self.assertEqual(([0, 1], [1, 2], {"unit": 2, "time": 5, "duration": 7, "frames": 1024}, {"time": 9, "duration": 7, "frames": 1024}),
                         (entry["noOutputList"], entry["streamChangeUnits"], entry["firstOutput"], entry["drainOutput"]))

    def test_the_analyzers_findings_reach_the_documented_limits_of_a_file(self):
        committed_figure = {"id": "samples", "stage": "coded", "reference": None}
        _, _, results, _ = self.run_windows_comparison({"SHA": self.mf_record(0)}, committed_figure, fresh_reference=100, findings=[{"code": "aac.coupling-without-program", "severity": "Warning", "offset": 1, "length": 1, "count": 1}])
        reasons = [d["reason"] for d in results[0]["differences"] if d["decoder"] == "media foundation transform"]
        self.assertTrue(any("coupling channel elements" in reason for reason in reasons), reasons)

    def test_the_transform_is_the_reference_only_after_core_audios_carried_reference(self):
        committed_figure = {"id": "samples", "stage": "coded", "reference": 80, "referencePath": "coreaudio converter", "referenceKind": "official", "format": {"rate": 44100, "channels": 2},
                            "coreAudio": {"converter": {"samples": 80}}}
        _, _, results, _ = self.run_windows_comparison({"SHA": self.mf_record(90)}, committed_figure)
        figure = results[0]["figures"][0]
        self.assertEqual((80, "coreaudio converter"), (figure["reference"], figure["referencePath"]))
        self.assertEqual(90, figure["mediaFoundation"]["transform"]["samples"])

    def test_a_windows_record_of_a_file_with_the_stop_kinds_reaches_the_comparison(self):
        committed_figure = {"id": "samples", "stage": "coded", "reference": None}
        failed = {"bytes": 1, "unitList": {"kind": "mp4"}, "file": "one.m4a", "sha256": "SHA", "paths": driver.windows_failed_paths("the probe did not finish", {"units": 10})}
        _, _, results, _ = self.run_windows_comparison({"SHA": failed}, committed_figure)
        self.assertEqual([("decoder-not-asked", "noted")], [(d["kind"], d["status"]) for d in results[0]["differences"] if d["decoder"] == "media foundation transform"])

    def test_a_downloaded_sample_without_a_file_is_listed_as_not_fetched_with_a_warning(self):
        _, document, _, _ = self.run_comparison({"someone-else": {"file": "x"}})
        self.assertIn("downloaded/sample.mp4 (not fetched by this job)", document["notRun"])
        self.assertEqual([], document["recordsWithoutFile"])
        self.assertTrue(any("could not be fetched by this job" in w for w in document["warnings"]))


def transform_node(units, coded, drain, runs, drain_samples=(), **extra):
    """What the Windows probe writes for the AAC decoder transform: units with their input results and frames, the runs of constant output format, the drain."""
    node = {"withTimestamps": True, "decoder": {"clsid": "32d186a7", "dll": "MSAudDecMFT.dll", "dllVersion": "10.0.26100.1"}, "outputSet": True, "asc": {"stored": "1210", "aot": 2},
            "inputConfigUsed": "asStored", "units": units, "runs": runs, "stoppedBy": "end", "drain": {"frames": drain, "results": "ok", "samples": list(drain_samples)}, "codedSamples": coded, "totalSamples": coded + drain,
            "errorCount": 0, "unitsFed": len(units)}
    node.update(extra)
    return node


def unit(index, frames, **extra):
    return {"index": index, "offset": index * 9, "declared": 9, "received": 9, "inputHresult": "0x00000000", "output": "ok" if frames else "", "frames": frames, **extra}


def run44(samples, rate=44100, channels=2):
    return {"key": "%d Hz, %d channels" % (rate, channels), "rate": rate, "channels": channels, "samples": samples, "outputs": max(samples // 1024, 1)}


class WindowsDriverTests(unittest.TestCase):
    def test_the_transform_record_adds_the_drain_to_the_units_frames_and_lists_what_it_refuses(self):
        node = transform_node([unit(0, 0), unit(1, 1024), unit(2, 0, inputHresult="0xC00D36B5", retryHresult="0xC00D3E8C"), unit(3, 1024), unit(4, 0, output="0xC00D6D60")],
                              coded=2048, drain=1024, runs=[run44(3072)])
        record = driver.windows_transform_result(node, 5)
        self.assertEqual(("end", 3072), (record["stoppedBy"], record["totalSamples"]))
        self.assertEqual([("refused", 2, "0xC00D3E8C"), ("error", 4, "0xC00D6D60")], [(u["result"], u["index"], u["status"]) for u in record["units"]])
        self.assertEqual((2, 1, 1, 1), (record["decodedUnits"], record["noOutputUnits"], record["refusedUnits"], record["errorUnits"]))
        self.assertEqual([], record["silentUnits"])

    def test_a_capped_output_is_no_error_and_a_failure_before_a_retry_is_one(self):
        capped = unit(1, 1024, output="ok,limit")
        before_retry = unit(2, 1024, drainedBeforeRetry="0xC00D6D60", inputHresult="0xC00D36B5", retryHresult="0x00000000")
        self.assertEqual(("decoded", "error"), (driver.windows_unit_result(capped), driver.windows_unit_result(unit(3, 1024, output="ok,0xC00D6D60"))))
        self.assertEqual("error", driver.windows_unit_result(before_retry))
        node = transform_node([unit(0, 1024), capped, before_retry], coded=3072, drain=0, runs=[run44(3072)])
        record = driver.windows_transform_result(node, 3)
        self.assertEqual(([1], 1, "0xC00D6D60"), (record["cappedUnits"], record["errorUnits"], record["units"][0]["status"]))
        self.assertEqual("end", record["stoppedBy"])

    def test_a_drain_that_hit_the_cap_leaves_no_total(self):
        node = transform_node([unit(0, 1024)], coded=1024, drain=64, runs=[run44(1088)])
        node["drain"]["results"] = "ok,limit"
        record = driver.windows_transform_result(node, 1)
        self.assertEqual("limit", record["stoppedBy"])
        self.assertNotIn("totalSamples", record)

    def test_a_unit_that_gives_nothing_after_the_first_output_is_silent_and_one_before_it_is_not(self):
        node = transform_node([unit(0, 0), unit(1, 1024), unit(2, 0), unit(3, 1024)], coded=2048, drain=0, runs=[run44(2048)])
        record = driver.windows_transform_result(node, 4)
        self.assertEqual(([2], 2), (record["silentUnits"], record["noOutputUnits"]))

    def test_a_media_type_the_decoder_refuses_is_an_error_with_no_total(self):
        node = {"withTimestamps": True, "refusedMediaType": True, "inputAttempts": [{"config": "asStored", "setInputHresult": "0xC00D36B4"}]}
        record = driver.windows_transform_result(node, 10)
        self.assertEqual(("error", False, True), (record["stoppedBy"], record["opened"], record["mediaTypeRefused"]))
        self.assertNotIn("totalSamples", record)
        self.assertIn("0xC00D36B4", record["reason"])

    def test_a_stalled_or_crashed_run_leaves_a_partial_count_never_a_total(self):
        stalled = driver.windows_transform_result(transform_node([unit(0, 1024)], coded=1024, drain=0, runs=[run44(1024)], stalled=True, partialSamples=1024), 5)
        self.assertEqual(("stall", 1024), (stalled["stoppedBy"], stalled["partialSamples"]))
        self.assertNotIn("totalSamples", stalled)
        crashed = driver.windows_reader_result({"crashed": True, "exitCode": "0xC0000005", "output": "x"})
        self.assertEqual(("crash", "0xC0000005"), (crashed["stoppedBy"], crashed["exit"]))
        self.assertNotIn("totalSamples", crashed)

    def test_the_reader_reaches_the_end_only_with_the_end_of_stream_flag_and_stops_at_the_first_failed_read(self):
        done = {"opened": True, "outputSet": True, "endOfStream": True, "totalSamples": 2048, "runs": [run44(2048, 22050)], "calls": 3, "reads": []}
        record = driver.windows_reader_result(done)
        self.assertEqual(("end", 2048, [{"rate": 22050, "channels": 2, "samples": 2048, "outputs": 2}]), (record["stoppedBy"], record["totalSamples"], record["runs"]))
        failed = driver.windows_reader_result({**done, "endOfStream": False, "stoppedBy": "the first failed read, 0xC00D6D60", "reads": [{"hresult": "0xC00D6D60", "flagNames": ["error"]}]})
        self.assertEqual(("error", 2048), (failed["stoppedBy"], failed["partialSamples"]))
        self.assertNotIn("totalSamples", failed)
        self.assertEqual("0xC00D6D60", failed["failedReads"][0]["hresult"])
        no_type = driver.windows_reader_result({"opened": True, "outputSet": False, "outputAttempts": [{"tag": 1, "bits": 16, "setHresult": "0xC00D36B4"}]})
        self.assertEqual(("error", False), (no_type["stoppedBy"], no_type["outputSet"]))

    def test_runs_that_do_not_add_up_to_the_total_are_no_total(self):
        record = driver.windows_transform_result(transform_node([unit(0, 1024)], coded=1024, drain=1024, runs=[run44(1024)]), 1)
        self.assertEqual(("limit", 2048), (record["stoppedBy"], record["partialSamples"]))
        self.assertNotIn("totalSamples", record)


def mf_path(samples, rate=44100, **extra):
    runs = [{"rate": rate, "channels": 2, "samples": samples, "outputs": 10}] if samples else []
    return {"opened": True, "stoppedBy": "end", "runs": runs, "unitCount": 10, "refusedUnits": 0, "errorUnits": 0, "noOutputUnits": 0, "drainFrames": 1024, "notFedUnits": 0,
            "silentUnits": [], "units": [], **extra}


class MediaFoundationColumnTests(unittest.TestCase):
    def figure(self, analyzer, reference, path="fdk direct", rate=44100, **extra):
        return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": analyzer, "reference": reference, "referencePath": path if reference is not None else None,
                "referenceKind": "official", "witnesses": {}, "analyzerRates": [rate], **extra}

    def attach(self, figure, transform=None, reader=None, control=None, blocks=1):
        paths = {}
        if transform is not None:
            paths["transform"] = transform
        if reader is not None:
            paths["reader"] = reader
        if control is not None:
            paths["transformWithoutTimestamps"] = control
        record = {"paths": {"direct": {"units": [{"index": 0, "blocks": blocks}]}}, "unitList": {}}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": paths}, record)
        return figure

    def differences(self, figure, codecs=("AAC LC",), channels=2, rate=44100):
        analyzer = {"streams": [{"codec": c, "channels": channels, "sampleRate": rate} for c in codecs]}
        return compare.mediafoundation_differences([figure], analyzer)

    def test_a_total_counts_only_when_the_path_reached_the_end(self):
        done = mf_path(9000)
        self.assertEqual(([{"rate": 44100, "channels": 2, "samples": 9000, "outputs": 10}], None), compare.mf_total(done))
        self.assertEqual("stopped by stall", compare.mf_total({**done, "stoppedBy": "stall"})[1])
        self.assertEqual("refused: x", compare.mf_total({**done, "opened": False, "stoppedBy": "error", "reason": "x"})[1])
        self.assertEqual("refused: x", compare.mf_total({**done, "mediaTypeRefused": True, "stoppedBy": "error", "reason": "x"})[1])
        self.assertEqual("not asked: x", compare.mf_total({**done, "opened": False, "stoppedBy": "harness", "reason": "x"})[1])
        self.assertEqual(([], None), compare.mf_total(mf_path(0)))

    def test_the_sbr_rate_of_the_source_reader_counts_in_the_same_duration(self):
        figure = self.figure(71680, 71680)
        self.attach(figure, mf_path(71680), reader=mf_path(35840, rate=22050))
        column = figure["mediaFoundation"]
        self.assertEqual((71680, 71680), (column["transform"]["samples"], column["reader"]["samples"]))
        self.assertEqual(22050, column["reader"]["format"]["rate"])
        self.assertEqual([], self.differences(figure))

    def test_a_transform_that_equals_the_reference_adds_no_difference(self):
        figure = self.figure(10240, 12288)
        self.attach(figure, mf_path(12288))
        self.assertEqual([], self.differences(figure))

    def test_the_owners_that_disagree_are_explained_where_the_analyzer_equals_the_reference(self):
        figure = self.figure(65536, 65536)
        self.attach(figure, mf_path(0), control=mf_path(0))
        differences = self.differences(figure)
        self.assertEqual([("mediafoundation-differs", "explained", True)], [(d["kind"], d["status"], d.get("ownersDisagree")) for d in differences])

    def test_decoders_that_all_disagree_are_left_pending_for_the_lead(self):
        figure = self.figure(10000, 9000)
        self.attach(figure, mf_path(8000))
        self.assertEqual([("mediafoundation-differs", "pending")], [(d["kind"], d["status"]) for d in self.differences(figure)])

    def test_a_documented_limit_explains_a_disagreement_and_is_named(self):
        figure = self.figure(10000, 9000)
        self.attach(figure, mf_path(8000))
        differences = self.differences(figure, codecs=("AAC LTP",))
        self.assertEqual("explained", differences[0]["status"])
        self.assertIn("Learn lists no Main, SSR or LTP profile", differences[0]["reason"])
        self.assertIn("at most 6 channels", compare.mf_documented({"streams": [{"codec": "AAC LC", "channels": 8, "sampleRate": 48000}]}))
        self.assertIsNone(compare.mf_documented({"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}]}))

    def test_where_fdk_and_core_audio_give_none_the_transform_is_the_reference(self):
        figure = self.figure(12288, None, rate=22050, noReference="FDK refuses", refused="x")
        self.attach(figure, mf_path(24576))
        self.assertEqual((12288, "media foundation transform"), (figure["reference"], figure["referencePath"]))
        self.assertNotIn("noReference", figure)
        self.assertEqual(["rate-differs"], [d["kind"] for d in self.differences(figure, rate=22050)])

    def test_the_transform_is_no_reference_when_core_audio_gave_one(self):
        figure = self.figure(100, 100, path="coreaudio converter")
        self.attach(figure, mf_path(90))
        self.assertEqual("coreaudio converter", figure["referencePath"])

    def test_a_transform_that_outputs_nothing_for_any_unit_is_no_reference(self):
        figure = self.figure(30720, None, noReference="FDK refuses", refused="x")
        self.attach(figure, mf_path(0, refusedUnits=0, errorUnits=3))
        self.assertIsNone(figure["reference"])
        self.assertEqual([("decoder-refuses-stream", "noted")], [(d["kind"], d["status"]) for d in self.differences(figure)])

    def test_frames_of_several_blocks_keep_the_transform_from_being_the_reference(self):
        figure = self.figure(30720, None, noReference="FDK holds blocks", heldBack=True)
        self.attach(figure, mf_path(10240), blocks=3)
        self.assertIsNone(figure["reference"])
        differences = self.differences(figure)
        self.assertEqual([("decoder-one-block-per-frame", "explained")], [(d["kind"], d["status"]) for d in differences])
        self.assertIn("one raw data block per ADTS frame", differences[0]["reason"])

    def test_a_refused_media_type_is_explained_by_a_documented_limit_or_by_fdk_agreeing_and_noted_otherwise(self):
        refused = {"opened": False, "mediaTypeRefused": True, "stoppedBy": "error", "reason": "no decoder accepts the input media type", "unitCount": 10}
        figure = self.figure(9600, 9600)
        self.attach(figure, refused)
        agreeing = self.differences(figure)[0]
        self.assertEqual(("decoder-refuses-stream", "explained", True), (agreeing["kind"], agreeing["status"], agreeing.get("ownersDisagree")))
        figure = self.figure(9600, 9000)
        self.attach(figure, refused)
        self.assertEqual("noted", self.differences(figure)[0]["status"])
        self.assertEqual("explained", self.differences(figure, codecs=("ER AAC ELD",))[0]["status"])

    def test_a_transform_that_stalled_is_noted_and_gives_no_figure(self):
        figure = self.figure(9600, 9600)
        self.attach(figure, {"opened": True, "stoppedBy": "stall", "unitCount": 10})
        self.assertIsNone(figure["mediaFoundation"]["transform"]["samples"])
        self.assertEqual([("decoder-stopped", "noted")], [(d["kind"], d["status"]) for d in self.differences(figure)])

    def test_a_rate_differs_is_noted_where_fdk_equals_the_analyzer_or_has_the_same_rate_and_pending_otherwise(self):
        figure = self.figure(71680, 71680)
        self.attach(figure, mf_path(35840, rate=22050))
        self.assertEqual("noted", next(d for d in self.differences(figure) if d["kind"] == "rate-differs")["status"])
        figure = self.figure(12288, 24576, rate=22050, rateDiffers={"decoderRates": [44100]})
        self.attach(figure, mf_path(24576, rate=44100))
        self.assertEqual("noted", next(d for d in self.differences(figure, rate=22050) if d["kind"] == "rate-differs")["status"])
        figure = self.figure(12288, 12288, rate=22050)
        figure["referencePath"] = "coreaudio converter"
        self.attach(figure, mf_path(24576, rate=44100))
        self.assertEqual("pending", next(d for d in self.differences(figure, rate=22050) if d["kind"] == "rate-differs")["status"])

    def test_a_control_run_without_timestamps_that_differs_is_pending(self):
        figure = self.figure(9000, 9000)
        self.attach(figure, mf_path(9000), control=mf_path(8000))
        self.assertEqual("8,000", compare.show(figure["mediaFoundation"]["transform"]["withoutTimestamps"]))
        self.assertEqual([("timestamps-differ", "pending")], [(d["kind"], d["status"]) for d in self.differences(figure)])
        figure = self.figure(9000, 9000)
        self.attach(figure, mf_path(9000), control=mf_path(9000))
        self.assertEqual([], self.differences(figure))

    def test_the_source_reader_is_a_witness_that_is_explained_only_where_the_analyzer_equals_the_reference(self):
        figure = self.figure(9000, 9000)
        self.attach(figure, mf_path(9000), reader=mf_path(8000))
        self.assertEqual([("media foundation reader", "explained")], [(d["decoder"], d["status"]) for d in self.differences(figure)])
        figure = self.figure(9000, 9500)
        self.attach(figure, mf_path(9500), reader=mf_path(8000))
        self.assertEqual("noted", self.differences(figure)[0]["status"])
        figure = self.figure(9000, 9500)
        self.attach(figure, mf_path(9500), reader=mf_path(9500))
        self.assertEqual([], self.differences(figure))

    def test_the_units_the_transform_refuses_are_listed_with_their_source_and_the_findings_at_their_bytes(self):
        transform = {**mf_path(2048), "units": [{"index": 1, "offset": 9, "received": 9, "result": "refused", "frames": 0, "status": "0xC00D3E8C"},
                                                {"index": 2, "offset": 18, "received": 9, "result": "error", "frames": 0, "status": "0xC00D6D60"},
                                                {"index": 3, "offset": 27, "received": 9, "result": "not fed", "frames": 0}]}
        analyzer = {"findings": [{"code": "aac.crc-mismatch", "severity": "Warning", "offset": 10, "length": 1, "count": 1}]}
        units = compare.mediafoundation_unit_verdicts({"paths": {"transform": transform}}, analyzer)
        self.assertEqual([(1, "agree", "media foundation transform"), (2, "pending", "media foundation transform")], [(u["index"], u["status"], u["source"]) for u in units])
        merged = {"findings": [{"code": "aac.crc-mismatch", "severity": "Warning", "offset": 100, "length": 1, "count": 2}]}
        self.assertEqual({"noted"}, {u["status"] for u in compare.mediafoundation_unit_verdicts({"paths": {"transform": transform}}, merged)})
        self.assertEqual([], compare.mediafoundation_unit_verdicts({"paths": {"transform": {**transform, "stoppedBy": "stall"}}}, analyzer))


class StopKindTests(unittest.TestCase):
    """A crash, a timeout, a stall or a failed probe call is never a refusal by the decoder (review finding 1): the driver's own records go through the comparison."""

    def figure(self):
        return {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 9600, "reference": 9600, "referencePath": "fdk direct", "referenceKind": "official",
                "witnesses": {}, "analyzerRates": [44100]}

    def run_through(self, transform):
        figure = self.figure()
        record = {"paths": {"direct": {"units": []}}, "unitList": {}}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"transform": transform}}, record)
        return figure, compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}]})

    def test_a_crash_a_timeout_and_a_stall_are_decoder_stopped_and_noted(self):
        for node in ({"crashed": True, "exitCode": "0xC0000005", "output": "x"}, {"timedOut": True, "timeoutSeconds": 30}, {"stalled": True, "stalledAfterMs": 20000}):
            record = driver.windows_transform_result(node, 10)
            self.assertIn(record["stoppedBy"], ("crash", "timeout", "stall"))
            figure, differences = self.run_through(record)
            self.assertTrue(figure["mediaFoundation"]["transform"]["why"].startswith("stopped by"))
            self.assertEqual([("decoder-stopped", "noted")], [(d["kind"], d["status"]) for d in differences])

    def test_a_probe_call_that_failed_as_a_whole_is_not_asked_and_noted(self):
        for why in ("the probe did not finish within 150 seconds", "the probe wrote no record (exit 1): x"):
            paths = driver.windows_failed_paths(why, {"units": 10})
            self.assertEqual({"harness"}, {p["stoppedBy"] for p in paths.values()})
            figure, differences = self.run_through(paths["transform"])
            self.assertEqual("not asked: " + why, figure["mediaFoundation"]["transform"]["why"])
            self.assertEqual([("decoder-not-asked", "noted")], [(d["kind"], d["status"]) for d in differences])

    def test_a_configuration_the_probe_cannot_build_a_type_from_is_not_asked(self):
        record = driver.windows_transform_result({"withTimestamps": True, "skipped": "the configuration could not be read"}, 10)
        self.assertEqual(("harness", False), (record["stoppedBy"], record["opened"]))
        self.assertEqual("the configuration could not be read", record["reason"])
        self.assertFalse(compare.mf_total(record)[1].startswith("refused"))
        figure, differences = self.run_through(record)
        reason = differences[0]["reason"]
        self.assertEqual(1, reason.count("not asked"), reason)
        self.assertEqual(1, reason.count("could not be read"), reason)

    def test_a_refusal_of_the_media_type_stays_a_refusal(self):
        record = driver.windows_transform_result({"withTimestamps": True, "refusedMediaType": True, "inputAttempts": [{"config": "asStored", "setInputHresult": "0xC00D36B4"}]}, 10)
        figure, differences = self.run_through(record)
        self.assertTrue(figure["mediaFoundation"]["transform"]["why"].startswith("refused: the transform refuses the input media type"))
        self.assertEqual([("decoder-refuses-stream", "explained")], [(d["kind"], d["status"]) for d in differences])

    def test_core_audios_crash_and_timeout_are_stops_not_refusals(self):
        for stopped in ("crash", "timeout", "limit"):
            self.assertEqual((None, None, "stopped by " + stopped), compare.ca_total({"opened": False, "stoppedBy": stopped}))
        self.assertTrue(compare.ca_total({"opened": False, "stoppedBy": "error", "converterStatus": "0x666D743F"})[2].startswith("did not open"))


class DocumentedLimitTests(unittest.TestCase):
    """Learn's limits are named from the file in every branch (review finding 3)."""

    def analyzer(self, codec="AAC LC", channels=2, rate=44100, findings=()):
        return {"streams": [{"codec": codec, "channels": channels, "sampleRate": rate}], "findings": [{"code": c} for c in findings]}

    def test_each_limit_is_named_from_the_analyzer_and_the_frames(self):
        self.assertIsNone(compare.mf_documented(self.analyzer()))
        self.assertIn("Main, SSR or LTP", compare.mf_documented(self.analyzer("AAC SSR")))
        self.assertIn("AAC-LC, HE-AAC and HE-AAC v2 only", compare.mf_documented(self.analyzer("ER AAC ELD")))
        self.assertIn("at most 6 channels", compare.mf_documented(self.analyzer(channels=8)))
        self.assertIn("at most 48 kHz", compare.mf_documented(self.analyzer(rate=96000)))
        self.assertIsNone(compare.mf_documented(self.analyzer(rate=48000)))
        self.assertIn("one raw data block per ADTS frame; the frames declare up to 4", compare.mf_documented(self.analyzer(), 4))
        self.assertIn("coupling channel elements", compare.mf_documented(self.analyzer(findings=("aac.coupling-without-program",))))
        self.assertNotIn("coupling", compare.mf_documented(self.analyzer(findings=("aac.crc-mismatch",))) or "")

    def attached(self, transform, blocks, reference, path, analyzer_value=10240):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": analyzer_value, "reference": reference, "referencePath": path, "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        record = {"paths": {"direct": {"units": [{"index": 0, "blocks": blocks}]}}, "unitList": {}}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"transform": transform}}, record)
        return figure

    def test_a_multi_block_file_that_the_transform_gives_nothing_for_is_explained_by_the_limit_whatever_the_reference(self):
        for reference, path in ((10240, "fdk direct"), (None, None), (10240, "coreaudio converter")):
            figure = self.attached(mf_path(0), 4, reference, path)
            self.assertEqual(4, figure["mediaFoundation"]["transform"]["blocksPerFrame"])
            differences = compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": []})
            self.assertEqual({"explained"}, {d["status"] for d in differences}, (reference, path))
            self.assertIn("one raw data block per ADTS frame", " ".join(d["reason"] for d in differences))

    def test_a_coupling_file_is_explained_with_the_limit_named(self):
        figure = self.attached(mf_path(0), 1, 4096, "fdk direct", 4096)
        analyzer = {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": [{"code": "aac.coupling-without-program"}]}
        differences = compare.mediafoundation_differences([figure], analyzer)
        self.assertEqual([("mediafoundation-differs", "explained")], [(d["kind"], d["status"]) for d in differences])
        self.assertIn("coupling channel elements", differences[0]["reason"])

    def test_a_refused_type_of_a_multi_block_stream_names_the_limit_too(self):
        refused = {"opened": False, "mediaTypeRefused": True, "stoppedBy": "error", "reason": "x", "unitCount": 10}
        figure = self.attached(refused, 3, None, None)
        self.assertEqual(3, figure["mediaFoundation"]["transform"]["blocksPerFrame"])
        differences = compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": []})
        self.assertEqual([("decoder-refuses-stream", "explained")], [(d["kind"], d["status"]) for d in differences])


class WindowsRecordTests(unittest.TestCase):
    def test_a_reader_that_changes_its_channel_count_has_no_figure(self):
        node = {"opened": True, "outputSet": True, "endOfStream": True, "totalSamples": 30720, "runs": [run44(27648, 48000) | {"bytes": 110592}, run44(3072, 48000, 6) | {"bytes": 36864}], "reads": []}
        record = driver.windows_reader_result(node)
        self.assertEqual(("limit", 30720), (record["stoppedBy"], record["partialSamples"]))
        self.assertNotIn("totalSamples", record)
        self.assertIn("channel count", record["notes"][0])

    def test_a_reader_whose_runs_do_not_add_up_to_its_total_has_no_figure(self):
        record = driver.windows_reader_result({"opened": True, "outputSet": True, "endOfStream": True, "totalSamples": 100, "runs": [run44(50)], "reads": []})
        self.assertEqual(("limit", 100), (record["stoppedBy"], record["partialSamples"]))
        self.assertNotIn("totalSamples", record)

    def reader_node(self, ended, samples=4096, partial=0):
        if ended:
            return {"opened": True, "outputSet": True, "endOfStream": True, "totalSamples": samples, "runs": [run44(samples, 22050)], "reads": []}
        return {"opened": True, "outputSet": True, "stoppedBy": "the first failed read, 0xC00D6D60", "totalSamples": partial, "runs": [run44(partial, 22050)] if partial else [], "reads": []}

    def test_a_reader_whose_three_runs_agree_counts_and_one_whose_runs_differ_has_no_figure(self):
        agreeing = driver.windows_reader_result(self.reader_node(True), [self.reader_node(True), self.reader_node(True)])
        self.assertEqual(("end", 4096, 2), (agreeing["stoppedBy"], agreeing["totalSamples"], agreeing["repeats"]))
        # sbr-in-three-blocks.aac: three runs that stop at the first failed read with 0 or 1,024 frames agree that the reader did not reach the end
        stopping = driver.windows_reader_result(self.reader_node(False), [self.reader_node(False, partial=1024), self.reader_node(False)])
        self.assertEqual("error", stopping["stoppedBy"])
        self.assertIn("first failed read", stopping["reason"])
        differing = driver.windows_reader_result(self.reader_node(False), [self.reader_node(True), self.reader_node(False)])
        self.assertEqual(("error", [], 0), (differing["stoppedBy"], differing["runs"], differing["partialSamples"]))
        self.assertNotIn("totalSamples", differing)
        self.assertIn("runs differ (error, end 4096, error)", differing["reason"])
        other_total = driver.windows_reader_result(self.reader_node(True), [self.reader_node(True, 2048), self.reader_node(True)])
        self.assertEqual("error", other_total["stoppedBy"])

    def test_a_reader_with_no_figure_says_only_how_in_the_column(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 9600, "reference": 9600, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        reader = driver.windows_reader_result(self.reader_node(False), [self.reader_node(True), self.reader_node(False)])
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"reader": reader, "transform": mf_path(9600)}}, {"paths": {"direct": {"units": []}}, "unitList": {}})
        self.assertEqual({"samples": None, "why": "stopped by error"}, figure["mediaFoundation"]["reader"])
        self.assertIn("runs differ", reader["reason"])

    def test_a_reader_has_no_figure_whatever_it_did_where_the_transform_changes_the_stream_at_a_silent_unit(self):
        # sbr-in-three-blocks.aac: stream changes at units 1, 2, 4, 5, 7 and 8, of which 4, 5, 7 and 8 gave no output after the first one
        transform = {**mf_path(4096), "silentUnits": [4, 5, 7, 8], "streamChangeUnits": [1, 2, 4, 5, 7, 8], "streamChangeAtSilentCount": 4, "streamChangeAtSilentUnits": [4, 5, 7, 8]}
        for reader in (self.reader_node(False), self.reader_node(True, 4096)):
            result = driver.windows_reader_result(reader, [reader, reader])
            figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 10240, "reference": 10240, "referencePath": "fdk direct", "referenceKind": "official",
                      "witnesses": {}, "analyzerRates": [22050]}
            compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"reader": result, "transform": transform}}, {"paths": {"direct": {"units": []}}, "unitList": {}})
            self.assertEqual("no figure: the transform signals a stream change at a unit that gives no output after its first output (units 4, 5, 7, 8), where the Source Reader's result varies from run to run",
                             figure["mediaFoundation"]["reader"]["why"])
            self.assertIsNone(figure["mediaFoundation"]["reader"]["samples"])
            self.assertEqual([], [d for d in compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 22050}], "findings": []}) if d["decoder"].endswith("reader")])
        # a stream change at a held-back first unit (HE-AAC) is not that
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 4096, "reference": 4096, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"reader": driver.windows_reader_result(self.reader_node(True, 4096)), "transform": {**mf_path(4096), "silentUnits": [], "streamChangeUnits": [0, 5], "streamChangeAtSilentCount": 0}}},
                                       {"paths": {"direct": {"units": []}}, "unitList": {}})
        self.assertEqual(8192, figure["mediaFoundation"]["reader"]["samples"])

    def reader_after(self, transform_extra):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 4096, "reference": 4096, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"reader": driver.windows_reader_result(self.reader_node(True, 4096)), "transform": {**mf_path(4096), **transform_extra}}},
                                       {"paths": {"direct": {"units": []}}, "unitList": {}})
        return figure["mediaFoundation"]["reader"]

    def test_silent_units_alone_or_a_stream_change_alone_do_not_remove_the_readers_figure(self):
        # damaged-length.aac: silent units after the first output and no stream change
        self.assertEqual(8192, self.reader_after({"silentUnits": [7, 8], "streamChangeUnits": [], "streamChangeAtSilentCount": 0})["samples"])
        # fdkaac-he-adts.aac: a stream change at unit 5, a unit that gave no output before the first output (so not a silent unit)
        self.assertEqual(8192, self.reader_after({"silentUnits": [], "streamChangeUnits": [5], "streamChangeAtSilentCount": 0})["samples"])

    def test_the_driver_counts_the_stream_changes_at_silent_units_before_it_caps_its_lists(self):
        # 100 units gave no output after the first one, each with a stream change: the capped lists hold 64 of them, the count all
        units = [unit(0, 1024)] + [unit(i, 0, output="streamChange") for i in range(1, 101)]
        record = driver.windows_transform_result(transform_node(units, coded=1024, drain=0, runs=[run44(1024)]), 101)
        self.assertEqual((64, 64, 100), (len(record["silentUnits"]), len(record["streamChangeUnits"]), record["streamChangeAtSilentCount"]))
        # a stream change at unit 70 of a run whose first 69 silent units fill the cap: the capped silent list does not hold unit 70, the count does
        late = [unit(0, 1024)] + [unit(i, 0) for i in range(1, 70)] + [unit(70, 0, output="streamChange")]
        record = driver.windows_transform_result(transform_node(late, coded=1024, drain=0, runs=[run44(1024)]), 71)
        self.assertNotIn(70, record["silentUnits"])
        self.assertEqual((1, [70]), (record["streamChangeAtSilentCount"], record["streamChangeAtSilentUnits"]))
        # fdkaac-he-adts.aac: a stream change at a unit before the first output, and silent units without one, are not the pattern
        before = [unit(0, 0), unit(1, 0, output="streamChange"), unit(2, 1024), unit(3, 0), unit(4, 1024)]
        record = driver.windows_transform_result(transform_node(before, coded=2048, drain=0, runs=[run44(2048)]), 5)
        self.assertEqual((0, [3], [1]), (record["streamChangeAtSilentCount"], record["silentUnits"], record["streamChangeUnits"]))

    def test_a_retry_that_succeeds_makes_a_decoded_unit_and_one_that_fails_a_refused_unit(self):
        self.assertEqual("decoded", driver.windows_unit_result(unit(0, 1024, inputHresult="0xC00D36B5", retryHresult="0x00000000")))
        self.assertEqual("refused", driver.windows_unit_result(unit(0, 0, inputHresult="0xC00D36B5", retryHresult="0xC00D36B5")))
        self.assertEqual("refused", driver.windows_unit_result(unit(0, 0, inputHresult="0xC00D36B5")))

    def test_a_run_that_stopped_after_1000_refused_units_is_no_total(self):
        record = driver.windows_transform_result(transform_node([unit(0, 0)], coded=0, drain=0, runs=[], stoppedBy="1000 refused units in a row"), 2000)
        self.assertEqual("limit", record["stoppedBy"])
        self.assertNotIn("totalSamples", record)

    def test_every_unit_that_gave_no_output_stream_changes_and_the_timeline_are_recorded(self):
        units = [unit(0, 0), unit(1, 0), unit(2, 1024, samples=[[0, 232199, 1024]]), unit(3, 0, output="streamChange"), unit(4, 1024, samples=[[500000, 232199, 1024]])]
        record = driver.windows_transform_result(transform_node(units, coded=2048, drain=1024, runs=[run44(3072)], drain_samples=[[900000, 232199, 1024]]), 5)
        self.assertEqual(([0, 1, 3], [3], [3]), (record["noOutputList"], record["streamChangeUnits"], record["silentUnits"]))
        timeline = record["timeline"]
        self.assertEqual((2, 1, 4), (timeline["first"]["unit"], timeline["gapCount"], timeline["last"]["unit"]))
        self.assertEqual({"unit": 4, "time": 500000, "duration": 232199, "frames": 1024, "expected": 232199}, timeline["gaps"][0])
        self.assertEqual([{"time": 900000, "duration": 232199, "frames": 1024}], timeline["drain"])

    def test_a_step_of_one_in_the_100_ns_times_is_no_gap(self):
        units = [unit(0, 1024, samples=[[0, 232200, 1024]]), unit(1, 1024, samples=[[232199, 232200, 1024]]), unit(2, 1024, samples=[[464400, 232200, 1024]])]
        self.assertEqual(0, driver.windows_transform_result(transform_node(units, coded=3072, drain=0, runs=[run44(3072)]), 3)["timeline"]["gapCount"])

    def test_unit_lists_of_two_cases_never_share_a_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            first, second = driver.case_directory(tmp, "units"), driver.case_directory(tmp, "units")
        self.assertNotEqual(first, second)


class ProbeConstantTests(unittest.TestCase):
    def test_the_drivers_count_of_source_reader_runs_is_the_probes_repeats_plus_one(self):
        text = (pathlib.Path(__file__).resolve().parents[1] / "fixtures" / "windows" / "WmaDecodeProbe" / "Program.cs").read_text(encoding="utf-8")
        import re
        repeats = int(re.search(r"private const int SourceReaderRepeats = (\d+);", text).group(1))
        self.assertEqual(repeats + 1, driver.SOURCE_READER_RUNS)


class WindowsUnitVerdictTests(unittest.TestCase):
    def test_a_stream_the_transform_gives_nothing_for_lists_its_refused_units_only_where_they_hold_a_finding(self):
        transform = {**mf_path(0), "units": [{"index": 1, "offset": 9, "received": 9, "result": "refused", "frames": 0, "status": "0xC00D3E8C"},
                                              {"index": 2, "offset": 18, "received": 9, "result": "refused", "frames": 0, "status": "0xC00D3E8C"}]}
        analyzer = {"findings": [{"code": "aac.crc-mismatch", "severity": "Warning", "offset": 10, "length": 1, "count": 1}]}
        units = compare.mediafoundation_unit_verdicts({"paths": {"transform": transform}}, analyzer)
        self.assertEqual([1], [u["index"] for u in units])


class WindowsDriverRunTests(unittest.TestCase):
    def test_two_files_with_the_same_bytes_get_unit_lists_in_directories_of_their_own(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            first, second = tmp / "one.m4a", tmp / "two.m4a"
            first.write_bytes(b"same")
            second.write_bytes(b"same")
            seen = []

            def unit_list(path, directory):
                seen.append(pathlib.Path(directory))
                return directory / "x.units.tsv", {"kind": "mp4", "codec": "aac", "units": 1, "encrypted": False, "note": None}

            args = mock.Mock(out=str(tmp / "out"), probe=str(tmp / "probe.exe"), downloaded=None)
            with mock.patch.object(driver.probe, "unit_list", unit_list), mock.patch.object(driver.probe, "files", lambda folder, ext: [first, second] if folder == "aac" else []), \
                    mock.patch.object(driver.derive_variants, "write_aac", lambda out: []), mock.patch.object(driver, "windows_probe_run", lambda *a, **k: (None, {"error": "x"})), \
                    mock.patch.object(driver, "aac_skip_reason", lambda summary: None), mock.patch.object(driver, "AAC_FOLDERS", ("aac",)):
                driver.run_windows(args)
            self.assertEqual(2, len(seen))
            self.assertNotEqual(seen[0], seen[1])
            records = sorted((tmp / "out" / "records" / "mf-aac").glob("*.json"))
            self.assertEqual(2, len(records))
            self.assertEqual("harness", json.loads(records[0].read_text(encoding="utf-8"))["paths"]["transform"]["stoppedBy"])


    def test_the_repeated_reader_runs_of_the_probe_reach_the_record(self):
        ended = {"opened": True, "outputSet": True, "endOfStream": True, "totalSamples": 1024, "runs": [run44(1024)], "reads": []}
        stopped = {"opened": True, "outputSet": True, "stoppedBy": "the first failed read, 0xC00D6D60", "totalSamples": 0, "runs": [], "reads": []}
        node = {"sourceReader": ended, "sourceReaderRepeats": [stopped, stopped], "direct": transform_node([unit(0, 1024)], coded=1024, drain=0, runs=[run44(1024)]),
                "directWithoutTimestamps": transform_node([unit(0, 1024)], coded=1024, drain=0, runs=[run44(1024)])}
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            media = tmp / "one.m4a"
            media.write_bytes(b"x")
            args = mock.Mock(out=str(tmp / "out"), probe=str(tmp / "probe.exe"), downloaded=None)
            with mock.patch.object(driver.probe, "unit_list", lambda path, directory: (directory / "x.tsv", {"kind": "mp4", "codec": "aac", "units": 1, "encrypted": False, "note": None})), \
                    mock.patch.object(driver.probe, "files", lambda folder, ext: [media] if folder == "aac" else []), mock.patch.object(driver.derive_variants, "write_aac", lambda out: []), \
                    mock.patch.object(driver, "windows_probe_run", lambda *a, **k: (node, {})), mock.patch.object(driver, "aac_skip_reason", lambda summary: None), \
                    mock.patch.object(driver, "AAC_FOLDERS", ("aac",)):
                driver.run_windows(args)
            record = json.loads(next((tmp / "out" / "records" / "mf-aac").glob("*.json")).read_text(encoding="utf-8"))
        self.assertEqual(("error", 2), (record["paths"]["reader"]["stoppedBy"], record["paths"]["reader"]["repeats"]))
        self.assertEqual("end", record["paths"]["transform"]["stoppedBy"])

class ReviewFixTests(unittest.TestCase):
    def test_one_probe_call_has_a_budget_for_its_five_children(self):
        calls = []

        def fake_run(command, **kwargs):
            calls.append(kwargs["timeout"])
            raise subprocess.TimeoutExpired(command, kwargs["timeout"])

        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(subprocess, "run", fake_run):
            node, environment = driver.windows_probe_run("probe.exe", "a.m4a", tmp, pathlib.Path(tmp) / "work", timeout=30)
        self.assertEqual([5 * 30 + 60], calls)
        self.assertEqual((None, "the probe did not finish within 210 seconds"), (node, environment["error"]))

    def test_a_core_audio_converter_that_stopped_is_decoder_stopped_and_noted(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 9600, "reference": 9600, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        record = {"paths": {"direct": {"units": []}}, "unitList": {}}
        crashed = {"opened": False, "stoppedBy": "crash", "units": [], "unitCount": 10, "refusedUnits": 0, "noOutputUnits": 0, "drainFrames": 0, "runs": []}
        compare.attach_coreaudio([figure], {"paths": {"converter": crashed}}, record)
        differences = compare.coreaudio_differences([figure])
        self.assertEqual([("decoder-stopped", "noted", "coreaudio converter")], [(d["kind"], d["status"], d["decoder"]) for d in differences])
        self.assertIn("stopped by crash", differences[0]["reason"])

    def test_the_phrase_for_a_stream_nothing_came_out_of_does_not_count_refused_units_as_held_back(self):
        self.assertEqual("20 refused", compare.nothing_phrase(20, 20))
        self.assertEqual("27 refused, the others and the drain gave no output", compare.nothing_phrase(30, 27))
        self.assertEqual("all 10 and the drain gave no output", compare.nothing_phrase(10, 0))
        self.assertEqual("2 refused, 1 with an output error, 1 not fed, the others and the drain gave no output", compare.nothing_phrase(10, 2, 1, 1))

    def test_a_documented_limit_replaces_the_clause_that_the_owners_disagree(self):
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 4096, "reference": 4096, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"transform": mf_path(0)}}, {"paths": {"direct": {"units": [{"index": 0, "blocks": 4}]}}, "unitList": {}})
        documented = compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": []})[0]
        self.assertNotIn("disagree", documented["reason"])
        self.assertNotIn("ownersDisagree", documented)
        plain = {**figure, "mediaFoundation": {"transform": {"samples": 0, "decoderSamples": 0, "format": None}}}
        undocumented = compare.mediafoundation_differences([plain], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": []})[0]
        self.assertIn("the owners' decoders disagree", undocumented["reason"])
        self.assertTrue(undocumented["ownersDisagree"])

    def test_a_refusal_reason_says_refuses_once(self):
        record = driver.windows_transform_result({"withTimestamps": True, "refusedMediaType": True, "inputAttempts": [{"config": "asStored", "setInputHresult": "0xC00D36B4"}]}, 10)
        figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0], "analyzer": 9600, "reference": 9600, "referencePath": "fdk direct", "referenceKind": "official",
                  "witnesses": {}, "analyzerRates": [44100]}
        compare.attach_mediafoundation({"figures": [figure]}, {"paths": {"transform": record}}, {"paths": {"direct": {"units": []}}, "unitList": {}})
        reason = compare.mediafoundation_differences([figure], {"streams": [{"codec": "AAC LC", "channels": 2, "sampleRate": 44100}], "findings": []})[0]["reason"]
        self.assertEqual(1, reason.count("refuse"), reason)


class UnitDirectoryTests(unittest.TestCase):
    """Two files with the same bytes have one unit list file name: the FDK and Core Audio drivers write each in a directory of its own, as the Windows one does."""

    def run_driver(self, run, args_extra, patched):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            first, second = tmp / "one.m4a", tmp / "two.m4a"
            first.write_bytes(b"same")
            second.write_bytes(b"same")
            seen = []

            def unit_list(path, directory):
                seen.append(pathlib.Path(directory))
                return directory / "x.units.tsv", {"kind": "mp4", "codec": "aac", "units": 1, "encrypted": False, "note": None}

            args = mock.Mock(out=str(tmp / "out"), downloaded=None, **args_extra)
            with mock.patch.object(driver.probe, "unit_list", unit_list), mock.patch.object(driver.probe, "files", lambda folder, ext: [first, second] if folder == "aac" else []), \
                    mock.patch.object(driver.derive_variants, "write_aac", lambda out: []), mock.patch.object(driver, "aac_skip_reason", lambda summary: "skipped"), \
                    mock.patch.object(driver, "coreaudio_skip_reason", lambda summary, ac3: "skipped"), mock.patch.object(driver, "AAC_FOLDERS", ("aac",)), \
                    mock.patch.object(driver, "AC3_FOLDERS", ()), mock.patch.object(driver.probe, "tool_line", lambda command: "x"), mock.patch.object(driver.probe, "plist_version", lambda path: "x"):
                run(args)
            return seen

    def test_the_fdk_driver_writes_each_unit_list_in_a_directory_of_its_own(self):
        seen = self.run_driver(driver.run_aac, {"fdk_decode": "fdk", "fdk_lib": "lib", "fdk_commit": "c", "fdk_version": "v"}, None)
        self.assertEqual(2, len(seen))
        self.assertNotEqual(seen[0], seen[1])

    def test_the_core_audio_driver_writes_each_unit_list_in_a_directory_of_its_own(self):
        seen = self.run_driver(driver.run_coreaudio, {"probe": "probe"}, None)
        self.assertEqual(2, len(seen))
        self.assertNotEqual(seen[0], seen[1])


class MediaFoundationProvenanceTests(unittest.TestCase):
    def committed(self, tmp, carried_from=None):
        entry = {"path": "a.m4a", "sha256": "ab", "figures": [{"id": "samples", "stage": "coded", "referencePath": "media foundation transform", "reference": 9, "referenceKind": "official",
                                                               "format": {"rate": 44100, "channels": 2}, "mediaFoundation": {"transform": {"samples": 9}}}],
                 "differences": [{"decoder": "media foundation transform", "kind": "analyzer-differs", "status": "keep"}, {"decoder": "coreaudio converter", "kind": "x", "status": "noted"}],
                 "units": [{"source": "media foundation transform", "offset": 1}, {"source": "coreaudio converter", "offset": 2}]}
        if carried_from:
            entry["mediaFoundationFrom"] = carried_from
        source = pathlib.Path(tmp) / "official-decoders.json"
        source.write_text(json.dumps({"run": {"mediaFoundation": {"runId": "7", "commit": "abcdef0"}}, "files": [entry]}), encoding="utf-8")
        return source, entry

    def fresh(self):
        return {"file": "a.m4a", "sha256": "ab", "mediaFoundationMissing": True, "figures": [{"id": "samples", "stage": "coded", "reference": None, "noReference": "x"}], "differences": [], "units": []}

    def test_a_file_without_a_windows_record_keeps_the_media_foundation_data_of_the_committed_file_and_only_those(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, _ = self.committed(tmp)
            result = self.fresh()
            compare.carry_mediafoundation([result], source)
        self.assertEqual((9, "media foundation transform"), (result["figures"][0]["reference"], result["figures"][0]["referencePath"]))
        self.assertEqual({"transform": {"samples": 9}}, result["figures"][0]["mediaFoundation"])
        self.assertEqual(["media foundation transform"], [d["decoder"] for d in result["differences"]])
        self.assertEqual(["media foundation transform"], [u["source"] for u in result["units"]])
        self.assertTrue(result["mediaFoundationCarried"])
        self.assertEqual({"run": "7", "commit": "abcdef0"}, result["mediaFoundationFrom"])

    def test_data_carried_twice_keep_their_first_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, entry = self.committed(tmp, {"run": "3", "commit": "1111111"})
            self.assertEqual({"run": "3", "commit": "1111111"}, compare.carried_from(entry, json.loads(source.read_text(encoding="utf-8")), "mediaFoundation"))

    def test_the_core_audio_carry_leaves_the_media_foundation_units_alone(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, _ = self.committed(tmp)
            document = json.loads(source.read_text(encoding="utf-8"))
            document["files"][0]["figures"][0]["coreAudio"] = {"converter": {"samples": 9}}
            source.write_text(json.dumps(document), encoding="utf-8")
            result = {**self.fresh(), "coreAudioMissing": True}
            compare.carry_coreaudio([result], source)
        self.assertEqual(["coreaudio converter"], [u["source"] for u in result["units"]])

    def test_files_missing_from_the_windows_run_are_listed_and_the_carried_ones_noted(self):
        results = [{"folder": "aac", "file": "a.m4a", "mediaFoundationMissing": True, "mediaFoundationCarried": True}, {"folder": "mp4", "file": "b.m4a", "mediaFoundationMissing": True},
                   {"folder": "aac", "file": "c.m4a", "mediaFoundationMissing": False}]
        self.assertEqual(["mediafoundation aac/a.m4a (carried from the committed file)", "mediafoundation mp4/b.m4a"], compare.mediafoundation_not_run(results))

    def test_write_official_records_the_windows_run_and_counts_the_carried_entries(self):
        windows = {"windows": "Windows Server 2025 Datacenter", "build": "26100.1", "machine": "AMD64", "msAudDecMft": "10.0.26100.1", "mfreadwrite": "10.0.26100.1", "mfplat": "10.0.26100.1",
                   "imageOS": "win25", "imageVersion": "1", "runId": "9", "commit": "c"}
        result = {"file": "a.m4a", "sha256": "ab", "derived": None, "figures": [], "differences": [], "units": [], "unitList": {"kind": "mp4"}, "mediaFoundationFrom": {"run": "7", "commit": "abcdef0"}}
        with tempfile.TemporaryDirectory() as tmp:
            target = pathlib.Path(tmp) / "new.json"
            compare.write_official(target, pathlib.Path(tmp) / "none.json", [result], {"mediaFoundation": windows, "runId": "9"}, ["ffmpeg version 8.0.1"], "9", "aac")
            written = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual(windows, written["run"]["mediaFoundation"])
        self.assertEqual({"run": "7", "commit": "abcdef0"}, written["files"][0]["mediaFoundationFrom"])
        self.assertEqual(1, written["counts"]["mediaFoundationCarried"])

    def test_a_reference_of_the_transform_that_changes_with_the_windows_build_is_build_dependent_and_stays(self):
        windows = {"windows": "Windows Server 2025 Datacenter", "build": "26100.1", "machine": "AMD64", "msAudDecMft": "10.0.26100.1", "mfreadwrite": "10.0.26100.1", "mfplat": "10.0.26100.1"}
        figure = {"id": "samples", "stage": "coded", "analyzer": 9, "reference": 9, "referencePath": "media foundation transform", "referenceKind": "official", "witnesses": {}}
        old = {"path": "a.m4a", "sha256": "ab", "figures": [{**figure, "reference": 8}], "differences": [], "units": []}
        fresh = {"file": "a.m4a", "sha256": "ab", "derived": None, "figures": [figure], "differences": [], "units": [], "unitList": {"kind": "mp4"}}
        with tempfile.TemporaryDirectory() as tmp:
            previous = pathlib.Path(tmp) / "old.json"
            previous.write_text(json.dumps({"run": {"id": "1", "mediaFoundation": windows}, "files": [old]}), encoding="utf-8")
            target = pathlib.Path(tmp) / "new.json"
            compare.write_official(target, previous, [fresh], {"mediaFoundation": {**windows, "build": "26100.2"}, "runId": "2"}, ["ffmpeg version 8.0.1"], "2", "aac")
            written = json.loads(target.read_text(encoding="utf-8"))
        self.assertEqual(8, written["files"][0]["figures"][0]["reference"])
        self.assertIn("build-dependent", [d["kind"] for d in written["files"][0]["differences"]])

    def test_the_run_environment_is_read_from_the_drivers_environment(self):
        environment = compare.mf_environment({"productName": "Windows Server 2025 Datacenter", "displayVersion": "24H2", "currentBuild": "26100", "updateBuildRevision": 33650, "machine": "AMD64",
                                              "decodersUsed": {"C:\\Windows\\System32\\MSAudDecMFT.dll": "10.0.26100.33438"}, "systemDlls": {"mfreadwrite.dll": "10.0.26100.1", "mfplat.dll": "10.0.26100.2"},
                                              "runId": "5", "commit": "c", "imageOS": "win25", "imageVersion": "9"})
        self.assertEqual(("Windows Server 2025 Datacenter 24H2", "26100.33650", "10.0.26100.33438", "10.0.26100.1"),
                         (environment["windows"], environment["build"], environment["msAudDecMft"], environment["mfreadwrite"]))

    def test_the_agreement_lines_leave_out_carried_figures_and_list_them(self):
        class Args:
            run_id = "1"

        def result(name, carried, transform):
            figure = {"id": "samples", "stage": "coded", "analyzer": 10, "reference": 10, "referencePath": "fdk direct", "witnesses": {},
                      "mediaFoundation": {"transform": {"samples": transform}, "reader": {"samples": 10}}}
            return {"folder": "aac", "file": name, "sha256": "ab", "figures": [figure], "units": [], "differences": [], "notes": [], "derived": None, "mediaFoundationCarried": carried}
        text = compare.render_aac([result("fresh.m4a", False, 10), result("old.m4a", True, 99)], [], [], {}, ["ffmpeg"], [], Args())
        line = next(l for l in text.splitlines() if l.strip().startswith("aac, apple") and "transform totals" in l)
        self.assertIn("transform totals   1, equal to the analyzer   1", line)
        self.assertIn("carried from the committed files, not counted above: 1 files (aac/old.m4a)", text)


class DeclaredSamplesTests(unittest.TestCase):
    def test_a_committed_reason_is_read_as_the_script_words_it_now(self):
        self.assertEqual("which it counts by their declared samples", compare.reworded("which it counts by their declared positions"))
        self.assertIsNone(compare.reworded(None))
        self.assertEqual(3, compare.reworded(3))

    def test_the_shortfall_of_flagged_units_is_explained_only_where_the_figures_lost_samples_equal_it(self):
        figure = {"id": "samples", "stage": "coded", "streams": [0], "analyzer": 12288, "reference": 11264, "referencePath": "fdk direct", "witnesses": {}, "concealedSamples": 0}
        direct = {"units": [{"index": 0, "blocks": 1}] * 12}
        units = [{"index": 11, "decoder": "refused", "status": "agree"}]
        for lost, status in ((1024, "explained"), (0, "noted"), (2048, "noted")):
            analyzer = {"streams": [{"index": 0, "lostSamples": lost}, {"index": 1, "lostSamples": 4096}], "gapless": {}, "findings": []}
            differences = compare.aac_differences([dict(figure)], direct, ["AAC LC"], analyzer, units)
            self.assertEqual([("analyzer-differs", status)], [(d["kind"], d["status"]) for d in differences])
            self.assertEqual(lost, differences[0]["analyzerLost"])
            self.assertIn("declared samples", differences[0]["reason"])


    def test_a_refused_unit_without_a_finding_or_a_shortfall_that_is_not_the_refused_blocks_is_pending(self):
        figure = {"id": "samples", "stage": "coded", "streams": [0], "analyzer": 12288, "reference": 11264, "referencePath": "fdk direct", "witnesses": {}, "concealedSamples": 0}
        direct = {"units": [{"index": 0, "blocks": 1}] * 12}
        analyzer = {"streams": [{"index": 0, "lostSamples": 1024}], "gapless": {}, "findings": []}
        for units in ([{"index": 11, "decoder": "refused", "status": "pending"}], []):
            differences = compare.aac_differences([dict(figure)], direct, ["AAC LC"], analyzer, units)
            self.assertEqual([("analyzer-differs", "pending")], [(d["kind"], d["status"]) for d in differences])


if __name__ == "__main__":
    unittest.main()
