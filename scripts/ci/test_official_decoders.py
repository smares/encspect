#!/usr/bin/env python3
"""Tests of the Core Audio columns of the official-decoder comparison (rollout step 5): python3 scripts/ci/test_official_decoders.py"""

import json
import pathlib
import sys
import tempfile
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
        self.assertEqual({"files": 1, "fed": 1}, written["counts"])

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


if __name__ == "__main__":
    unittest.main()
