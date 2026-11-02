#!/usr/bin/env python3
"""The comparison of the official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, "Records and the comparison").

  compare_official_decoders.py [--encspect CLI] [--ffmpeg PROGRAM] [--out DIR] [--run-id ID] [--write-official FILE] [--write-official-tta FILE]
                               [--write-official-aac DIR] [--downloaded DIR] ARTIFACT_FOLDER...

Runs on Linux. Reads the records of the artifact folders (records/<format>/<file>.json, environment.json, variants.json), matches them to files
by SHA-256 (the committed fixtures, and the derived variants, which derive_variants.py writes again here), takes the analyzer's figures from the
committed golden reports (or from the CLI given with --encspect, which the derived variants need), runs ffmpeg for its column, and writes

  comparison.txt    one table per format, one row per file and stage: the analyzer's figure, the reference and the path that gave it, ffmpeg's and every
                    other decoder's, and notes; the units where a decoder's verdict differs from the analyzer's; counts of agreement; files not run
  comparison.json   the same, for the check
  with --write-official FILE: the committed results of the fixtures of the format (tests/fixtures/<format>/official-decoders.json), keeping the status
                    and the note of every difference a person has set

Stages: the coded stage is the sum of what the stream's packets code, before any trimming (the direct path, "Decoded Samples"); the trimmed stage is
what the owner's reader writes after the priming and padding of the format (the stream's "samples"). The script computes no trimming of its own.

TTA (comparison-tta.txt, comparison-tta.json, --write-official-tta): one figure per file, the stream's `samples` against the reference. TTA has no priming or padding
(one stage) and `tta -d` is the only official path (decision 6): it gives per-file figures and no per-frame information, so units are not compared.
  reference  the samples `tta -d` wrote (`tta -dp secret` for the file written with that password), counted from the PCM bytes with the channel count and bits per
             sample of the WAVE header it wrote, when it exited 0 and left the output; a nonzero exit, a crash or a timeout gives no official reference
  fallback   ffmpeg's count, only when `tta -d` gave no reference and ffmpeg exited 0, labeled `ffmpeg (fallback)`; else none. A partial count is never a figure

AAC (comparison-aac.txt, comparison-aac.json, --write-official-aac DIR, which writes official-decoders.<folder>.draft.json for tests/fixtures/aac, adts and mp4; the
apple folder has no goldens, so nothing reads its entries and they stay in the run's comparison): FDK AAC's direct path is the reference of the coded stage (a unit counts as
output when IS_OUTPUT_VALID holds, concealed frames included; the frames AACDEC_FLUSH drains are counted apart), compared with the stream's `samples`, per run of constant output
format. The trimmed stage waits for Core Audio (rollout step 5): the analyzer's "Samples (iTunSMPB)" and "Samples (Edit List)" are listed with no reference. The units FDK
refuses, conceals or outputs nothing for, and the units with an analyzer finding, are listed at their byte ranges.
  Totals are compared in the same duration, never as sums across output rates: a run counts at the analyzer's rate (FDK's 1,024 samples at 22,050 Hz are 2,048 at 44,100 Hz), runs
  that cannot be paired are compared as one figure at one rate, and a run whose rate differs from the analyzer's (SBR applied by one and not the other) is also listed as `rate-differs`.
  A total with raw data blocks the library still holds when it answers 0x1002 (frames of several blocks) is no reference (`decoder-holds-blocks`).
  explained   FDK's excess over the analyzer is exactly the samples of the frames it conceals (a future decoder leaves them as gaps, decision of the project lead, 2026-11-01); FDK's
              shortfall is exactly the blocks of units the analyzer flags (a finding at their bytes or covering them) and counts by their declared positions, for which FDK refuses
              or outputs nothing; the 64-stream cap (aac.streams-limited); AAC Main, LTP and SSR, which FDK AAC does not document
  noted       no reference, the analyzer has nothing wrong to change; a unit the analyzer flags through a merged finding, a sample-table finding or the declared length of a damaged unit
  pending     every other difference of the analyzer from FDK, rate differences, and every unit FDK refuses, conceals or outputs nothing for with no finding that covers it

The reference rule for Ogg, per stage:
  trimmed   the reader's total (oggdec, opusdec) when the reader reached the end without a failed read or a stop condition, the direct path fed every
            page, and the reader's count is consistent with the stream's last granule position (below); else none
  coded     the direct path's total when it reached the end and fed every page (libogg dropped none); a stream whose headers the decoder refuses gets a figure
            with no reference and the verdict
A reader's count is consistent when it lies in [low, high]: high is the coded total of the streams of its codec; low is G, the sum over those streams of the
granule position of each stream's last page (in file order), less the pre-skip for Opus, both limited to the coded total. A stream with no granule position makes
the check impossible and the trimmed stage has no reference. A reader that stops early or drops a stretch without a message falls below it; one that is right
about a start offset or wrong granule positions (G above the coded total) falls outside it too, and the rule then has no reference. The sum can hide errors that
cancel across links, which a reader's single total cannot show. A further acceptance (the reader equals the libvorbis run with the granule positions) was declined by
the project lead on 2026-11-01: both runs share libvorbis's granule handling, so agreement would show a complete read, not correct trimming.
"""

import argparse
from fractions import Fraction
import concurrent.futures
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import derive_variants  # noqa: E402
import official_decode as driver  # noqa: E402
import official_probe as probe  # noqa: E402

ROOT = HERE.parent.parent
FIXTURES = ROOT / "tests" / "fixtures"
SCHEMA = 1
UNIT_FINDING = re.compile(r"^(opus\.(packet-r\d|self-delimiting|stream-durations-differ)|vorbis\.(window-flag-mismatch|mode-invalid|not-audio|empty-packet))$")
STATUSES = ("explained", "noted", "pending", "follow", "keep", "build-dependent", "stale")


def sha256_of(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


# ---- the analyzer's figures -------------------------------------------------------------------------------------------

def find_node(node, name):
    if node.get("name") == name:
        return node
    for child in node.get("children", []):
        found = find_node(child, name)
        if found:
            return found
    return None


def child_value(node, name):
    for child in node.get("children", []):
        if child.get("name") == name:
            return child.get("value")
    return None


def ogg_analyzer(report):
    """The logical streams of an Ogg report in file order, with the figures a decoder speaks to; None for a report that is not Ogg."""
    ogg = next((c for c in report["root"].get("children", []) if c.get("name") == "Ogg"), None)
    if ogg is None:
        return None
    infos = {}
    for info in report.get("streams", []):
        nodes = info.get("nodes") or []
        if nodes and nodes[0] and len(nodes[0]) >= 2 and nodes[0][0] == 0:
            infos[nodes[0][1]] = info
    streams, seen = [], {}
    for i, node in enumerate(ogg.get("children", [])):
        if node.get("name") != "Logical Stream":
            continue
        serial = child_value(node, "Serial Number")
        occurrence = seen.get(serial, 0)
        seen[serial] = occurrence + 1
        info = infos.get(i)
        decoded = find_node(node, "Decoded Samples")
        streams.append({
            "serial": serial, "occurrence": occurrence, "codec": node.get("value"), "offset": node.get("offset"), "length": node.get("length"),
            "samples": info["samples"] if info else None,
            "decodedSamples": decoded["value"] if decoded else None,
            "sampleRate": info["sampleRate"] if info else None, "channels": info["channels"] if info else None,
            "lastGranule": child_value(node, "Last Granule Position"),
        })
    findings = [{"code": f["code"], "severity": f["severity"], "offset": f.get("offset"), "length": f.get("length"), "count": f.get("count", 1)} for f in report.get("findings", [])]
    return {"streams": streams, "findings": findings}


def load_analyzer(path, encspect_reports):
    golden = path.with_name(path.stem + ".report.json")
    if golden.exists():
        return ogg_analyzer(json.loads(golden.read_text(encoding="utf-8"))[0]), "golden"
    report = encspect_reports.get(path.name)
    return (ogg_analyzer(report), "CLI") if report else (None, None)


def run_cli(encspect, folder):
    result = subprocess.run([encspect, "--json", str(folder)], capture_output=True, timeout=1800)
    try:
        reports = json.loads(result.stdout.decode("utf-8")) if result.stdout.strip() else []
    except json.JSONDecodeError:
        reports = []
    expected = sum(1 for p in pathlib.Path(folder).iterdir() if p.is_file())
    if len(reports) != expected:
        sys.exit("the command line program gave %d reports for %d files (exit status %s): %s" % (len(reports), expected, result.returncode, result.stderr.decode("utf-8", "replace")[:500]))
    return {pathlib.Path(r["source"]).name: r for r in reports}


# ---- ffmpeg ----------------------------------------------------------------------------------------------------------

def run_tool(command, timeout=60):
    try:
        result = subprocess.run(command, capture_output=True, timeout=timeout)
        return result.returncode, result.stdout, result.stderr.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        return None, b"", "timeout"
    except OSError as error:
        return None, b"", str(error)


def ffmpeg_column(ffmpeg, ffprobe, path, tmp, runs=None, input_flags=()):
    """ffmpeg's count at both stages: the default (it applies the format's trimming) and +skip_manual (the coded count, as the probe found for Opus).
    When ffmpeg exits with an error its partial count is recorded as an error and is never a figure."""
    code, out, _ = run_tool([ffprobe, "-v", "error", *input_flags, "-select_streams", "a:0", "-show_entries", "stream=channels,sample_rate,codec_name", "-of", "json", str(path)])
    try:
        stream = json.loads(out or b"{}").get("streams", [None])[0]
    except (json.JSONDecodeError, IndexError):
        stream = None
    column = {"stream": stream}
    if not stream or not stream.get("channels"):
        column["error"] = "no audio stream"
        return column
    for label, flags in runs or (("default", []), ("skipManual", ["-flags2", "+skip_manual"])):
        target = pathlib.Path(tmp) / ("ff-" + label + "-" + path.name + ".raw")
        code, _, err = run_tool([ffmpeg, "-v", "error", "-nostdin", "-y", *flags, *input_flags, "-i", str(path), "-map", "0:a:0", "-f", "f32le", str(target)])
        size = target.stat().st_size if target.exists() else None
        if target.exists():
            target.unlink()
        samples = size // (4 * stream["channels"]) if size is not None else None
        entry = {"exit": code, "errors": [re.sub(r" @ 0x[0-9a-f]+", "", l) for l in err.splitlines() if l.strip()][:5]}
        if code == 0 and samples is not None:
            entry["samples"] = samples
        else:
            entry["partialSamples"] = samples
        column[label] = entry
    return column


# ---- the comparison of one Ogg file ------------------------------------------------------------------------------------

def direct_total(path_result):
    return path_result.get("totalSamples") if path_result and path_result.get("stoppedBy") == "end" else None


def match_streams(record_streams, analyzer):
    """{harness stream index: analyzer stream} by serial number and the order in which a serial number occurs."""
    by_key = {(s["serial"], s["occurrence"]): s for s in analyzer["streams"]} if analyzer else {}
    seen, mapping = {}, {}
    for s in record_streams:
        occurrence = seen.get(s["serial"], 0)
        seen[s["serial"]] = occurrence + 1
        mapping[s["stream"]] = by_key.get((s["serial"], occurrence))
    return mapping


def sum_known(values):
    values = list(values)
    return None if not values or any(v is None for v in values) else sum(values)


def compare_ogg(record, analyzer, ffmpeg):
    """The figures, differences and unit verdicts of one record."""
    paths = record["paths"]
    direct = paths["direct"]
    mapping = match_streams(direct["streams"], analyzer)
    by_codec = {}
    for s in direct["streams"]:
        if s["codec"] in ("opus", "vorbis"):
            by_codec.setdefault(s["codec"], []).append(s)
    figures = []
    notes = []
    if direct.get("pagesAfterEndOfStream"):
        notes.append("%d page(s) after the end-of-stream page, fed" % direct["pagesAfterEndOfStream"])
    if analyzer is None:
        return {"figures": [], "differences": [], "units": [], "notes": notes + ["no analyzer report"]}

    def analyzer_streams(streams):
        return [mapping.get(s["stream"]) for s in streams]

    # coded stage: one figure per run of constant output format, and one per stream the decoder refuses
    runs = direct.get("runs", [])
    complete = direct.get("stoppedBy") == "end"
    fed = complete and direct.get("fedEveryPage")
    unfed = None
    if not complete:
        unfed = "the direct path did not reach the end (%s)" % direct.get("stoppedBy")
    elif not fed:
        unfed = "the direct path did not feed every unit (libogg dropped %d bytes, %d bytes left over, %d pages refused)" % (
            direct.get("skippedBytes") or 0, direct.get("trailingBytes") or 0, direct.get("refusedPages") or 0)
    coded = []
    for run in runs:
        members = [s for s in direct["streams"] if s["stream"] in run["streams"]]
        mine = analyzer_streams(members)
        a = sum_known(m["decodedSamples"] if m else None for m in mine)
        figure = {"stage": "coded", "analyzerFigure": "Decoded Samples", "streams": [analyzer_index(analyzer, m) for m in mine], "analyzer": a,
                  "reference": run["samples"] if unfed is None else None, "referencePath": "direct" if unfed is None else None,
                  "format": {"rate": run["rate"], "channels": run["channels"]}, "witnesses": {}, "kind": "run"}
        if unfed:
            figure["noReference"] = unfed
            figure["directTotal"] = run["samples"]
        coded.append(figure)
    if complete:
        for st in direct["streams"]:
            if st.get("refusal") and st["codec"] in ("opus", "vorbis"):
                m = mapping.get(st["stream"])
                coded.append({"stage": "coded", "analyzerFigure": "Decoded Samples", "streams": [analyzer_index(analyzer, m)], "analyzer": m["decodedSamples"] if m else None,
                              "reference": None, "referencePath": None, "witnesses": {}, "kind": "refused", "refused": st["refusal"],
                              "stream": st["stream"]})
    for number, figure in enumerate(coded):
        if len(coded) == 1:
            figure["id"] = "decodedSamples"
        elif figure["kind"] == "run":
            figure["id"] = "decodedSamples.run%d" % (number + 1)
        else:
            figure["id"] = "decodedSamples.stream%d" % figure.pop("stream")
        figure.pop("stream", None)
        figure.pop("kind")
        figures.append(figure)
    if not coded:
        figures.append({"id": "decodedSamples", "stage": "coded", "analyzerFigure": "Decoded Samples", "streams": [], "analyzer": None, "reference": None, "referencePath": None,
                        "witnesses": {}, "noReference": unfed or "the decoder decoded no packet"})
    coded_figures = list(figures)
    # ffmpeg decodes the first audio stream only, so its counts are set against a file of one logical stream; the column of the others is kept apart.
    # +skip_manual gives the coded count of Opus (the probe); Vorbis has no such count, so ffmpeg is a witness at the trimmed stage only.
    single = len(direct["streams"]) == 1 and direct["streams"][0]["codec"] in ("opus", "vorbis")
    codec_of_file = direct["streams"][0]["codec"] if single else None
    ff_coded = ffmpeg.get("skipManual", {}).get("samples") if ffmpeg else None
    ff_default = ffmpeg.get("default", {}).get("samples") if ffmpeg else None
    if ffmpeg and single and codec_of_file == "opus" and len(coded_figures) == 1:
        coded_figures[0]["witnesses"]["ffmpeg +skip_manual"] = ff_coded
        if ff_coded is None:
            coded_figures[0]["witnessErrors"] = {"ffmpeg +skip_manual": ffmpeg_failure(ffmpeg, "skipManual")}
    elif ffmpeg and by_codec and not single:
        notes.append("ffmpeg decodes the first audio stream only (%s samples at the trimmed stage, %s with +skip_manual); not set against a file of %d logical streams"
                     % (show(ff_default), show(ff_coded), len(direct["streams"])))

    # trimmed stage, one figure for the reader's codec
    reader = paths.get("reader")
    if reader:
        codec = "vorbis" if reader["tool"] == "oggdec" else "opus"
        members = by_codec.get(codec, [])
        mine = analyzer_streams(members)
        a = sum_known(m["samples"] if m else None for m in mine)
        coded_total = sum(m["samples"] for m in members) if complete else None
        lasts = [m["lastGranule"] for m in members]
        granule = sum(g or 0 for g in lasts)
        pre_skip = sum(m.get("preSkip", 0) for m in members) if codec == "opus" else 0
        reason = None
        total = reader.get("totalSamples")
        bounds = None
        if not reader.get("opened"):
            reason = "the reader did not open the file"
        elif reader["stoppedBy"] != "end":
            reason = "the reader stopped (%s)" % reader["stoppedBy"]
        elif total is None:
            reason = "the reader's samples could not be counted"
        elif not direct.get("fedEveryPage"):
            reason = "the direct path did not feed every page (libogg dropped %d bytes, %d bytes left over, %d pages refused)" % (direct.get("skippedBytes") or 0, direct.get("trailingBytes") or 0, direct.get("refusedPages") or 0)
        elif coded_total is None:
            reason = "the direct path did not reach the end"
        elif not members or any(g is None for g in lasts):
            reason = "the last granule position of a stream is unknown, so the reader's count cannot be checked"
        else:
            high = coded_total
            low = max(0, min(granule, coded_total) - pre_skip)
            bounds = [low, high]
            if not low <= total <= high:
                reason = "the reader's %d is outside %d to %d, what the last pages' granule positions (%d) and the coded total %d allow" % (total, low, high, granule, coded_total)
        figure = {
            "id": "samples", "stage": "trimmed", "analyzerFigure": "samples", "streams": [analyzer_index(analyzer, m) for m in mine], "analyzer": a,
            "reference": total if reason is None else None, "referencePath": reader["tool"] if reason is None else None,
            "format": {"rate": reader["formats"][0]["rate"], "channels": reader["formats"][0]["channels"]} if reader.get("formats") else None, "witnesses": {},
            "readerTotal": total, "readerTool": reader["tool"], "lastGranule": granule, "consistentRange": bounds,
        }
        if reason:
            figure["noReference"] = reason
        witness = paths.get("directGranule")
        if witness and direct_total(witness) is not None:
            figure["witnesses"]["libvorbis with granule positions"] = direct_total(witness)
        if ffmpeg and single:
            figure["witnesses"]["ffmpeg"] = ff_default
            if ff_default is None:
                figure["witnessErrors"] = {"ffmpeg": ffmpeg_failure(ffmpeg, "default")}
        figures.append(figure)
    elif by_codec:
        notes.append("no reader run")

    return {"figures": figures, "differences": figure_differences(figures), "units": unit_verdicts(direct, analyzer), "notes": notes}


def figure_differences(figures):
    """The differences of figures, never dropped: the analyzer against the reference, a witness against the reference, a reader that is not the reference, a stream the decoder refuses."""
    differences = []
    for figure in figures:
        a, r = figure["analyzer"], figure["reference"]
        if r is not None and a != r:
            differences.append({"figure": figure["id"], "decoder": figure["referencePath"], "value": r, "analyzer": a, "status": "pending", "kind": "analyzer-differs"})
        for name, value in figure["witnesses"].items():
            if value is not None and r is not None and value != r:
                differences.append({"figure": figure["id"], "decoder": name, "value": value, "analyzer": a, "reference": r, "status": "explained" if a == r else "pending",
                                    "kind": "witness-differs"})
        if figure.get("ttaNoCount"):
            differences.append({"figure": figure["id"], "decoder": "tta -d", "value": None, "analyzer": a, "status": "noted", "kind": "decoder-gives-no-count",
                                "reason": "tta -d accepts the header (exit 0) and writes %s; %s" % (
                                    "no data" if not figure.get("ttaDataBytes") else "%d data bytes" % figure["ttaDataBytes"], figure["ttaNoCount"])})
        if figure.get("refused"):
            differences.append({"figure": figure["id"], "decoder": "direct", "value": None, "analyzer": a, "status": "pending" if a else "noted", "kind": "decoder-refuses-stream",
                                "reason": figure["refused"]})
        if r is None and figure.get("readerTotal") is not None and figure["readerTotal"] != a:
            differences.append({"figure": figure["id"], "decoder": figure["readerTool"], "value": figure["readerTotal"], "analyzer": a, "status": "pending",
                                "kind": "reader-not-accepted", "reason": figure.get("noReference")})
    return differences


def ffmpeg_failure(ffmpeg, key):
    entry = ffmpeg.get(key) or {}
    return {"exit": entry.get("exit"), "error": (entry.get("errors") or [ffmpeg.get("error")])[0]}


def analyzer_index(analyzer, stream):
    return analyzer["streams"].index(stream) if stream is not None and analyzer else None


def unit_verdicts(direct, analyzer):
    """Every packet the decoder refuses and every packet that holds a finding about a unit, with the decoder's result and the analyzer's finding at the same bytes, and
    whether they agree; and every such finding that points into no packet. A finding that merges several packets points to the first only, so a refused packet with no
    finding in a file that has such findings is `noted`. A packet whose file ranges could not be worked out is listed as not located."""
    result = []
    findings = [f for f in analyzer["findings"] if UNIT_FINDING.match(f["code"]) and f["offset"] is not None]
    merged = any(f["count"] > 1 for f in findings)
    units = [u for u in direct.get("units", []) if "index" in u]
    located = [u for u in units if u.get("ranges")]
    empty_at = {u["ranges"][0][0] for u in located if sum(r[1] for r in u["ranges"]) == 0}

    def inside(unit, f):
        """A finding at an offset belongs to the packet that contains the byte; one of length 0 at the start of an empty packet belongs to that packet, not to the next."""
        if sum(r[1] for r in unit["ranges"]) == 0:
            return unit["ranges"][0][0] == f["offset"]
        if f["length"] == 0 and f["offset"] in empty_at:
            return False
        return any(start <= f["offset"] < start + l for start, l in unit["ranges"])

    matched = set()
    for u in units:
        refused = u["result"] == "error" or u["result"].startswith("not passed")
        if not u.get("ranges"):
            if refused:
                result.append({"index": u["index"], "stream": u["stream"], "offset": None, "length": None, "ranges": None, "decoder": u["result"], "finding": None, "findings": [],
                               "verdict": "unit not located: the decoder refuses a packet whose file ranges could not be worked out", "status": "noted"})
            continue
        mine = [f for f in findings if inside(u, f)]
        for f in mine:
            matched.add((f["code"], f["offset"]))
        if not refused and not mine:
            continue
        entry = {"index": u["index"], "stream": u["stream"], "offset": u["ranges"][0][0], "length": sum(r[1] for r in u["ranges"]), "ranges": u["ranges"],
                 "decoder": u["result"] + (" %d" % u["error"] if "error" in u else ""), "finding": mine[0]["code"] if mine else None, "findings": [f["code"] for f in mine]}
        if refused and mine:
            entry.update({"verdict": "decoder refuses, finding at the same bytes", "status": "agree"})
        elif refused:
            entry.update({"verdict": "decoder refuses, no finding" + (" (the file has findings that merge several packets: this may be a later one)" if merged else ""),
                          "status": "noted" if merged else "pending"})
        else:
            entry.update({"verdict": "finding, decoder decodes", "status": "noted"})
        result.append(entry)
    for f in findings:
        if (f["code"], f["offset"]) not in matched:
            result.append({"index": None, "stream": None, "offset": f["offset"], "length": f["length"], "ranges": None, "decoder": "no unit", "finding": f["code"], "findings": [f["code"]],
                           "verdict": "finding points into no packet the decoder was fed", "status": "noted"})
    return result


# ---- the comparison of one TTA file -------------------------------------------------------------------------------------

def tta_analyzer(report):
    """The TTA stream of a report with the figures `tta -d` speaks to, and the findings; None for a report that is not TTA."""
    if not any(c.get("name") == "TTA" for c in report["root"].get("children", [])):
        return None
    streams = [{"samples": i.get("samples"), "sampleRate": i.get("sampleRate"), "channels": i.get("channels"), "bitsPerSample": i.get("bitsPerSample")}
               for i in report.get("streams", []) if i.get("codec") == "TTA"]
    findings = [{"code": f["code"], "severity": f["severity"], "offset": f.get("offset"), "length": f.get("length"), "count": f.get("count", 1)} for f in report.get("findings", [])]
    return {"streams": streams, "findings": findings}


def load_tta_analyzer(path, encspect_reports):
    golden = path.with_name(path.stem + ".report.json")
    if golden.exists():
        return tta_analyzer(json.loads(golden.read_text(encoding="utf-8"))[0]), "golden"
    report = encspect_reports.get(path.name)
    return (tta_analyzer(report), "CLI") if report else (None, None)


def tta_stop_reason(reader):
    """Why `tta -d` gave no reference, in the tool's own words."""
    parts = []
    if reader["stoppedBy"] == "crash":
        parts.append("killed by %s" % reader["signal"])
    elif reader["stoppedBy"] == "timeout":
        parts.append("timeout")
    elif reader["stoppedBy"] == "error":
        parts.append("exit status %s" % reader["exit"])
        if not reader.get("outputFileLeft"):
            parts.append("no output file")
        if reader.get("stopReason"):
            parts.append(reader["stopReason"])
    elif reader.get("noSampleCount"):
        parts.append("exit status 0, " + reader["noSampleCount"])
    messages = [w for w in reader.get("warnings", []) if w.startswith("tta:")]
    if messages:
        parts.append('"%s"' % messages[0][len("tta: "):])
    return ", ".join(parts)


def compare_tta(record, analyzer, ffmpeg):
    """The figure and differences of one TTA record. tta -d gives per-file figures and no per-frame information, so there are no unit verdicts."""
    reader = record["paths"]["reader"]
    notes = []
    if "readerWithoutPassword" in record["paths"]:
        without = record["paths"]["readerWithoutPassword"]
        notes.append("without the password: %s" % (tta_stop_reason(without) if without["stoppedBy"] != "end" else "decodes"))
    if analyzer is None:
        return {"figures": [], "differences": [], "units": [], "notes": notes + ["no analyzer report"], "findings": []}
    stream = analyzer["streams"][0] if analyzer["streams"] else None
    figure = {"id": "samples", "stage": "coded", "analyzerFigure": "samples", "streams": [0] if stream else [], "analyzer": stream["samples"] if stream else None,
              "reference": None, "referencePath": None, "referenceKind": None, "witnesses": {}}
    ff = ffmpeg.get("default") if ffmpeg else None
    if reader["stoppedBy"] == "end" and reader.get("totalSamples") is not None:
        run = reader["runs"][0]
        figure.update({"reference": reader["totalSamples"], "referencePath": reader["tool"] + (" secret" if reader["tool"] == "tta -dp" else ""), "referenceKind": "official",
                       "format": {"rate": run["rate"], "channels": run["channels"], "bits": run["bits"]}})
        if ffmpeg:
            figure["witnesses"]["ffmpeg"] = ff.get("samples") if ff else None
            if ff and ff.get("samples") is None:
                figure["witnessErrors"] = {"ffmpeg": ffmpeg_failure(ffmpeg, "default")}
    else:
        figure["ttaStopped"] = {"stoppedBy": reader["stoppedBy"], "exit": reader["exit"], "signal": reader["signal"], "reason": tta_stop_reason(reader)}
        if reader.get("noSampleCount"):
            figure["ttaNoCount"] = reader["noSampleCount"]
            figure["ttaDataBytes"] = reader["runs"][0]["outputBytes"]
        if reader.get("partialSamples") is not None:
            figure["ttaPartialSamples"] = reader["partialSamples"]
        if ff and ff.get("samples") is not None:
            figure.update({"reference": ff["samples"], "referencePath": "ffmpeg", "referenceKind": "ffmpeg (fallback)"})
            figure["noOfficialReference"] = "tta -d gave none (%s); ffmpeg 8.0.1 is the reference as the design says" % figure["ttaStopped"]["reason"]
            if ff.get("errors"):
                figure["fallbackErrors"] = ff["errors"]
        else:
            why = "tta -d gave none (%s)" % figure["ttaStopped"]["reason"]
            if ffmpeg:
                failure = ffmpeg_failure(ffmpeg, "default")
                why += "; ffmpeg gave none (exit %s: %s)" % (failure["exit"], failure["error"])
                figure["witnessErrors"] = {"ffmpeg": failure}
            figure["noReference"] = why
    return {"figures": [figure], "differences": figure_differences([figure]), "units": [], "notes": notes,
            "findings": sorted({f["code"] for f in analyzer["findings"]})}


def render_tta(results, not_run, unmatched, environment, ffmpeg_version, warnings, args):
    out = ["Cross-checks by the official decoder: TTA (libtta-c 2.3 `tta -d`; ffmpeg as the fallback and the second column)",
           "run %s on %s (%s), ffmpeg %s, %s" % (environment.get("runId") or args.run_id or "-", environment.get("imageOS") or "-", environment.get("imageVersion") or "-",
                                                 (ffmpeg_version or ["-"])[0], environment.get("tta") or "-"),
           "tta -d gives one figure per file and no per-frame information, so only figures are compared; the analyzer's findings are listed beside them for reading"]
    out += ["WARNING: " + w for w in warnings]
    rows = []
    for r in results:
        for fig in r["figures"]:
            wit = "; ".join("%s %s" % (k, show(v)) for k, v in fig["witnesses"].items())
            note = fig.get("noReference") or fig.get("noOfficialReference") or ""
            if fig.get("ttaPartialSamples") is not None:
                note += " (tta wrote %s samples before it stopped, not a figure)" % show(fig["ttaPartialSamples"])
            if fig.get("fallbackErrors"):
                note += " [ffmpeg printed: %s]" % fig["fallbackErrors"][0]
            tag = "" if fig["analyzer"] == fig["reference"] or fig["reference"] is None else "  DIFFERS"
            rows.append([r["file"], r["sha256"][:8], show(fig["analyzer"]), show(fig["reference"]), fig["referenceKind"] or "-", wit, (note + tag).strip(), ",".join(r["findings"]) or "-"])
    out += ["", table(rows, ["file", "sha256", "analyzer", "reference", "kind", "other decoders", "notes", "analyzer findings"])]
    file_notes = [(r["file"], n) for r in results for n in r["notes"]]
    out += ["", "Notes per file"] + (["  %s: %s" % p for p in file_notes] or ["  none"])
    out += ["", "Differences"]
    diff_rows = [[r["file"], d["figure"], d["decoder"], show(d["value"]), show(d["analyzer"]), d["kind"], d["status"]] for r in results for d in r["differences"]]
    out.append(table(diff_rows, ["file", "figure", "decoder", "value", "analyzer", "kind", "status"]) if diff_rows else "none")
    out += ["", "Agreement (figures; committed encoder-written, committed hand-built and derived variants counted apart)"]
    groups = (("committed encoder-written", [r for r in results if not r["derived"] and r["file"].startswith("external-")]),
              ("committed hand-built", [r for r in results if not r["derived"] and not r["file"].startswith("external-")]),
              ("derived variants", [r for r in results if r["derived"]]))
    for label, subset in groups:
        figs = [f for r in subset for f in r["figures"]]
        official = [f for f in figs if f["referenceKind"] == "official"]
        fallback = [f for f in figs if f["referenceKind"] == "ffmpeg (fallback)"]
        none = [f for f in figs if f["reference"] is None]
        ff_same = [f for f in official if f["witnesses"].get("ffmpeg") == f["reference"]]
        out.append("  %-26s files %3d: tta -d reference %3d (analyzer equals it %3d; ffmpeg equals it %3d); ffmpeg fallback %3d (analyzer equals it %3d); no reference %3d" % (
            label, len(subset), len(official), sum(1 for f in official if f["analyzer"] == f["reference"]), len(ff_same), len(fallback),
            sum(1 for f in fallback if f["analyzer"] == f["reference"]), len(none)))
    stops = {}
    for r in results:
        for f in r["figures"]:
            if "ttaStopped" in f:
                stops.setdefault(re.sub(r"wrote \d+ (data )?bytes?, more than the \d+", "wrote more data bytes than the header declares, N", re.sub(r"wrote \d+ of the \d+ data bytes", "wrote fewer data bytes than", f["ttaStopped"]["reason"])), []).append(r["file"])
    out += ["", "Where tta -d gave no reference (its own message), files per reason"]
    out += ["  %3d  %s" % (len(v), k) for k, v in sorted(stops.items(), key=lambda kv: (-len(kv[1]), kv[0]))] or ["  none"]
    out += ["", "Derived variants by rule (analyzer equals the reference / differs / no reference; of those with a reference, how many are ffmpeg's fallback)"]
    rule_rows = []
    for rule in derive_variants.TTA_RULES:
        subset = [r for r in results if r["derived"] and r["derived"]["rule"] == rule]
        figs = [f for r in subset for f in r["figures"]]
        same = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] == f["reference"])
        differs = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] != f["reference"])
        rule_rows.append([rule, len(subset), "%d / %d / %d" % (same, differs, len(figs) - same - differs), sum(1 for f in figs if f["referenceKind"] == "ffmpeg (fallback)"),
                          sum(1 for f in figs if f["referenceKind"] == "official")])
    out.append(table(rule_rows, ["rule", "files", "analyzer = / differs / none", "ffmpeg fallback", "tta -d"]))
    twins = [r for r in results if r.get("sameBytesAs")]
    out += ["", "Variants with the same bytes as another (one row above stands for all of them; %d rows)" % len(twins)]
    out += ["  %s = %s" % (r["file"], ", ".join(r["sameBytesAs"])) for r in twins] or ["  none"]
    out += ["", "Not run (selected files without a record): " + (", ".join(not_run) if not_run else "none")]
    if unmatched:
        out.append("Records without a file here (another version of a fixture or variant): " + ", ".join(unmatched))
    return "\n".join(out) + "\n"


def run_tta_comparison(args, records, environments, ffmpeg_version, warnings, tmp):
    """The TTA section of a run: returns (text, JSON document, results); None when there are no TTA records."""
    tta_records = records.get("tta", {})
    if not tta_records:
        return None
    environment = next((e for e in environments if e.get("format") == "tta"), {})
    files, aliases = {}, {}
    for path in sorted(p for p in (FIXTURES / "tta").iterdir() if p.is_file() and p.suffix == ".tta"):
        files[sha256_of(path)] = (path, None)
    manifest = derive_variants.write_tta(tmp / "ttavariants")
    for entry in manifest:
        if entry["sha256"] in files:
            aliases.setdefault(entry["sha256"], [files[entry["sha256"]][0].name]).append(entry["name"])
            continue
        files[entry["sha256"]] = (tmp / "ttavariants" / entry["name"], entry)
    cli_reports = run_cli(args.encspect, tmp / "ttavariants") if args.encspect else {}

    def work(sha):
        path, derived = files[sha]
        record = tta_records.get(sha)
        if record is None:
            return None
        analyzer, source = load_tta_analyzer(path, cli_reports)
        column = None
        if not args.no_ffmpeg:
            column = ffmpeg_column(args.ffmpeg, args.ffprobe, path, tmp, runs=(("default", []),), input_flags=("-password", "secret") if "readerWithoutPassword" in record["paths"] else ())
        compared = compare_tta(record, analyzer, column)
        return {"file": path.name, "sha256": sha, "derived": derived and {"from": derived["source"], "rule": derived["rule"]}, "bytes": record["bytes"],
                "sameBytesAs": aliases.get(sha, [])[1:] if sha in aliases else [],
                "analyzerSource": source, "analyzerStreams": analyzer["streams"] if analyzer else None, "ffmpeg": column, "record": summarize(record), **compared}

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = {sha: pool.submit(work, sha) for sha in sorted(files, key=lambda x: files[x][0].name)}
    results, not_run = [], []
    for sha, future in futures.items():
        result = future.result()
        if result is None:
            not_run.append(files[sha][0].name)
        else:
            results.append(result)
    unmatched = sorted(r["file"] for sha, r in tta_records.items() if sha not in files)
    text = render_tta(results, not_run, unmatched, environment, ffmpeg_version, warnings, args)
    document = {"schema": SCHEMA, "environment": environment, "ffmpeg": ffmpeg_version, "warnings": warnings, "files": results, "notRun": not_run, "recordsWithoutFile": unmatched}
    return text, document, results, environment


# ---- the comparison of one AAC file -------------------------------------------------------------------------------------

AAC_CODECS = ("AAC", "HE-AAC", "xHE-AAC", "ER AAC")
FDK_UNSUPPORTED_PROFILES = ("AAC Main", "AAC LTP", "AAC SSR")
GAPLESS_FIGURES = (("Samples (iTunSMPB)", "samples.iTunSMPB"), ("Samples (Edit List)", "samples.editList"))
MP4_EXTRA = ("mp4.samples-overlap", "mp4.sample-outside-file", "mp4.sample-outside-mdat")


def aac_unit_finding(finding):
    return finding["code"].startswith("aac.") and finding["severity"] != "Info" and finding["offset"] is not None


def aac_analyzer(report):
    """The AAC streams of a report (all of an ADTS file's, the first stream of an MP4 file's, whose first sound track the unit list holds), the gapless figures of the first
    track and the findings."""
    mp4 = any(c.get("name") == "MP4" for c in report["root"].get("children", []))
    streams = [{"index": i, "codec": s["codec"], "samples": s.get("samples"), "sampleRate": s.get("sampleRate"), "channels": s.get("channels")}
               for i, s in enumerate(report.get("streams", [])) if s["codec"].startswith(AAC_CODECS)]
    if mp4:
        first = report["streams"][0] if report.get("streams") else None
        streams = [x for x in streams if first is not None and x["index"] == 0]
    gapless = find_node(report["root"], "Gapless")
    figures = {}
    if gapless:
        for node in gapless.get("children", []):
            if node.get("value") is not None:
                figures[node["name"]] = node["value"]
    findings = [{"code": f["code"], "severity": f["severity"], "offset": f.get("offset"), "length": f.get("length"), "count": f.get("count", 1)} for f in report.get("findings", [])]
    return {"streams": streams, "gapless": figures, "findings": findings}


def load_aac_analyzer(path, encspect_reports):
    golden = path.with_name(path.stem + ".report.json")
    if golden.exists():
        return aac_analyzer(json.loads(golden.read_text(encoding="utf-8"))[0]), "golden"
    report = encspect_reports.get(path.name)
    return (aac_analyzer(report), "CLI") if report else (None, None)


def analyzer_runs(streams):
    """Consecutive streams with the same rate and channels are one run, as the decoder's runs of constant output format are."""
    runs = []
    for s in streams:
        if runs and runs[-1]["key"] == (s["sampleRate"], s["channels"]):
            runs[-1]["streams"].append(s["index"])
            runs[-1]["samples"] = None if runs[-1]["samples"] is None or s["samples"] is None else runs[-1]["samples"] + s["samples"]
        else:
            runs.append({"key": (s["sampleRate"], s["channels"]), "streams": [s["index"]], "samples": s["samples"]})
    return runs


def aac_stop_reason(direct):
    parts = [direct["stoppedBy"]]
    if direct.get("signal"):
        parts.append("signal " + direct["signal"])
    parts += direct.get("notes", [])
    return ", ".join(parts)


def at_rate(samples, rate_from, rate_to):
    """samples counted at rate_from as a count at rate_to: the same duration (never a sum across rates), rounded to the nearest with halves up; (value, exact)."""
    if rate_from in (None, 0) or rate_to in (None, 0) or rate_from == rate_to:
        return samples, True
    exact = Fraction(samples * rate_to, rate_from)
    return int(exact + Fraction(1, 2)) if exact >= 0 else 0, exact.denominator == 1


def compare_aac(record, analyzer, ffmpeg):
    """The figures, differences and unit verdicts of one AAC record."""
    if record.get("notFed"):
        return {"figures": [], "differences": [], "units": [], "notes": ["not fed: " + record["notFed"]], "notFed": record["notFed"]}
    direct = record["paths"]["direct"]
    notes = []
    if analyzer is None:
        return {"figures": [], "differences": [], "units": [], "notes": ["no analyzer report"]}
    figures = []
    a_runs = analyzer_runs(analyzer["streams"])
    d_runs = direct.get("runs", [])
    complete = direct["stoppedBy"] == "end"
    # No output at all from a stream the analyzer counts no samples in (a file cut in its first sample) is a total of 0, which equals the analyzer's; no output from a stream
    # it counts samples in is a refusal.
    refused_all = complete and not d_runs and direct["unitCount"] > 0 and sum_known(r["samples"] for r in a_runs) != 0
    ff = None
    if ffmpeg:
        ff_entry = ffmpeg.get("coded") or ffmpeg.get("default") or {}
        ff = ff_entry.get("samples")
    all_streams = [i for r in a_runs for i in r["streams"]]
    base = {"stage": "coded", "analyzerFigure": "samples", "streams": all_streams, "analyzer": sum_known(r["samples"] for r in a_runs), "reference": None,
            "referencePath": None, "referenceKind": None, "witnesses": {"ffmpeg": ff} if ffmpeg else {}}
    if not a_runs:
        notes.append("the analyzer reports no AAC stream: its figure is 0")
        a_runs = [{"key": (None, None), "streams": [], "samples": 0}]
        base.update({"analyzer": 0})
    if refused_all:
        codes = sorted({c for c in direct["errorCodes"] if c not in ("0x0000", "0x1002")})
        figures.append({"id": "samples", **base, "refused": "FDK AAC outputs nothing for any of its %d units (%s)" % (direct["unitCount"], ", ".join(codes) if codes else "0x1002, it never has enough bits"),
                        "noReference": "FDK refuses the stream; the next decoder of the rule is the reference (Core Audio, rollout step 5), else ffmpeg"})
    elif not complete:
        figures.append({"id": "samples", **base, "noReference": "the direct path did not reach the end (%s); its partial count is %s" % (aac_stop_reason(direct), show(direct.get("partialSamples")))})
    elif direct.get("heldBack"):
        figures.append({"id": "samples", **base, "heldBack": True, "decoderSamples": direct.get("totalSamples"), "heldBytes": direct.get("heldBytes"),
                        "noReference": "FDK holds the last raw data blocks of the last frame (%s bytes fed and not used) when it answers 0x1002 and the drain discards them: its total of %s is short by those blocks" % (
                            show(direct.get("heldBytes")), show(direct.get("totalSamples")))})
    else:
        if not d_runs:  # the decoder output nothing and the analyzer counts nothing
            d_runs = [{"rate": None, "channels": None, "samples": 0, "aot": None, "extAot": None, "frameSize": None}]
            a_runs = [{"key": (None, None), "streams": all_streams, "samples": 0}]
        pairs = len(a_runs) == len(d_runs) and all(a["key"][1] == d["channels"] or a["key"][1] is None or d["channels"] is None for a, d in zip(a_runs, d_runs))
        if pairs:
            for n, (a, d) in enumerate(zip(a_runs, d_runs)):
                rate = a["key"][0]
                value, exact = at_rate(d["samples"], d["rate"], rate)
                concealed, _ = at_rate(d.get("concealedSamples", 0), d["rate"], rate)
                figure = {"id": "samples" if len(a_runs) == 1 else "samples.run%d" % (n + 1), **base, "streams": a["streams"], "analyzer": a["samples"], "reference": value,
                          "referencePath": "fdk direct", "referenceKind": "official", "format": {"rate": d["rate"], "channels": d["channels"]}, "concealedSamples": concealed,
                          "object": {"aot": d["aot"], "extAot": d["extAot"], "frameSize": d["frameSize"]}}
                if len(a_runs) > 1:
                    figure["witnesses"] = {}
                if d["rate"] != rate and rate is not None and d["rate"] is not None:
                    figure["rateDiffers"] = {"analyzerRate": rate, "decoderRate": d["rate"], "decoderSamples": d["samples"], "ratio": "%d/%d" % (d["rate"], rate)}
                    figure["referenceAtAnalyzerRate"] = {"samples": value, "exact": exact}
                figures.append(figure)
        else:
            rate = next((a["key"][0] for a in a_runs if a["key"][0]), None) or d_runs[0]["rate"]
            analyzer_total = exact_all = 0
            exact_all = True
            for a in a_runs:
                value, exact = at_rate(a["samples"], a["key"][0], rate)
                analyzer_total += value
                exact_all = exact_all and exact
            reference = concealed = 0
            for d in d_runs:
                value, exact = at_rate(d["samples"], d["rate"], rate)
                reference += value
                exact_all = exact_all and exact
                concealed += at_rate(d.get("concealedSamples", 0), d["rate"], rate)[0]
            figures.append({"id": "samples", **base, "analyzer": analyzer_total, "reference": reference, "referencePath": "fdk direct", "referenceKind": "official",
                            "format": {"rate": rate, "channels": d_runs[0]["channels"]}, "concealedSamples": concealed,
                            "unpaired": {"rate": rate, "exact": exact_all, "decoder": [[r["rate"], r["channels"], r["samples"]] for r in d_runs],
                                         "analyzer": [[r["key"][0], r["key"][1], r["samples"]] for r in a_runs]}})
            notes.append("the decoder has %d runs of constant output format and the analyzer %d: both are counted at %s Hz (the same durations) and compared as one figure" % (len(d_runs), len(a_runs), show(rate)))
    if ffmpeg and figures and "ffmpeg" in figures[0]["witnesses"] and figures[0]["witnesses"]["ffmpeg"] is None:
        figures[0]["witnessErrors"] = {"ffmpeg": ffmpeg_failure(ffmpeg, "coded" if "coded" in ffmpeg else "default")}
    if complete and direct.get("flushedSamples"):
        figures[0]["flushedSamples"] = direct["flushedSamples"]
    # the trimmed stage: Core Audio's ExtAudioFile is its reference (rollout step 5); the analyzer's gapless figures are listed as they stand
    for name, ident in GAPLESS_FIGURES:
        if name in analyzer["gapless"] and analyzer["streams"]:
            figures.append({"id": ident, "stage": "trimmed", "analyzerFigure": name, "streams": [analyzer["streams"][0]["index"]], "analyzer": analyzer["gapless"][name],
                            "reference": None, "referencePath": None, "referenceKind": None, "countsAt": "the core rate (the media timescale)",
                            "witnesses": {"ffmpeg": ffmpeg["default"].get("samples")} if ffmpeg and ffmpeg.get("default") else {},
                            "noReference": "the trimmed stage waits for Core Audio's ExtAudioFile (rollout step 5); FDK has no file reader"})
    codecs = [s["codec"] for s in analyzer["streams"]]
    units = aac_unit_verdicts(direct, analyzer)
    return {"figures": figures, "differences": aac_differences(figures, direct, codecs, analyzer, units), "units": units, "notes": notes, "codecs": codecs, "object": direct.get("object")}


def aac_differences(figures, direct, codecs, analyzer, units):
    differences = []
    capped = any(f["code"] == "aac.streams-limited" for f in analyzer["findings"])
    # units the analyzer flags and counts by their declared positions, for which the reference refuses or outputs nothing
    blocks = {u["index"]: u.get("blocks", 1) for u in direct.get("units", [])}
    missing = sum(blocks.get(u["index"], 1) for u in units if u["decoder"].split(" ")[0] in ("refused", "no") and u["status"] in ("agree", "noted") and u["index"] is not None)
    # The analyzer counts every unit by its declared raw data blocks, so one block is its samples over all the blocks: the shortfall is exactly the flagged units' blocks.
    total_blocks = sum(u.get("blocks", 1) for u in direct.get("units", []))
    for figure in figures:
        a, r = figure["analyzer"], figure["reference"]
        if figure["stage"] != "coded":
            continue
        if figure.get("refused"):
            if any(c in FDK_UNSUPPORTED_PROFILES for c in codecs):
                status, why = "explained", "AAC Main, LTP and SSR are not among the object types FDK AAC documents (its header names the AAC Low Complexity profile for predictors and no gain control; it also decodes HE-AAC, LD, ELD and USAC); the stream is %s" % ", ".join(sorted(set(codecs)))
            else:
                status, why = "noted", "FDK gives no reference; the next decoders of the rule (Core Audio, rollout step 5; Media Foundation, step 6) decide, and ffmpeg stays the reference where none decodes it"
            differences.append({"figure": figure["id"], "decoder": "fdk direct", "value": None, "analyzer": a, "status": status, "kind": "decoder-refuses-stream",
                                "reason": figure["refused"] + "; " + why})
            continue
        if figure.get("heldBack"):
            differences.append({"figure": figure["id"], "decoder": "fdk direct", "value": figure["decoderSamples"], "analyzer": a, "status": "noted", "kind": "decoder-holds-blocks",
                                "reason": figure["noReference"]})
            continue
        if figure.get("rateDiffers"):
            rd = figure["rateDiffers"]
            differences.append({"figure": figure["id"], "decoder": "fdk direct", "value": rd["decoderSamples"], "analyzer": a, "status": "pending", "kind": "rate-differs",
                                "reason": "FDK outputs %s samples at %s Hz where the analyzer counts %s at %s Hz (the rate ratio is %s: the same duration when the counts are in that ratio); the decoders disagree on whether SBR is applied" % (
                                    show(rd["decoderSamples"]), show(rd["decoderRate"]), show(a), show(rd["analyzerRate"]), rd["ratio"])})
        if r is not None and a != r:
            concealed = figure.get("concealedSamples", 0)
            if r > (a or 0) and capped:
                differences.append({"figure": figure["id"], "decoder": figure["referencePath"], "value": r, "analyzer": a, "status": "explained", "kind": "analyzer-differs",
                                    "reason": "the analyzer keeps 64 streams and counts no sample of the frames after them (aac.streams-limited, documented in aac.md); FDK decodes them all"})
            elif r > (a or 0) and r - a == concealed:
                differences.append({"figure": figure["id"], "decoder": figure["referencePath"], "value": r, "analyzer": a, "status": "explained", "kind": "analyzer-differs",
                                    "reason": "the excess of %s samples is exactly the samples of the frames FDK conceals; a future decoder leaves them as gaps" % show(r - a)})
            elif r < (a or 0) and missing and total_blocks and (a - r) * total_blocks == a * missing:
                differences.append({"figure": figure["id"], "decoder": figure["referencePath"], "value": r, "analyzer": a, "status": "explained", "kind": "analyzer-differs",
                                    "reason": "the shortfall of %s samples is %d frames of flagged units, which the analyzer counts by their declared positions and FDK refuses or outputs nothing for" % (show(a - r), missing)})
            else:
                differences.append({"figure": figure["id"], "decoder": figure["referencePath"], "value": r, "analyzer": a, "status": "pending", "kind": "analyzer-differs"})
        for name, value in figure["witnesses"].items():
            if value is not None and r is not None and value != r:
                differences.append({"figure": figure["id"], "decoder": name, "value": value, "analyzer": a, "reference": r, "status": "explained" if a == r else "noted",
                                    "kind": "witness-differs"})
    return differences


def aac_unit_verdicts(direct, analyzer):
    """Every unit FDK refuses, conceals or gives an error for, or outputs nothing for, and every unit that holds a finding about a unit (warnings and errors of the aac codes), with
    the decoder's result and the analyzer's finding at the same bytes; and every such finding that points into no unit. A unit's bytes are those fed with it, the gap in front of it
    included. The units of a stream FDK refuses entirely are listed only where they hold a finding: the figure carries the refusal. A finding that merges several cases points to the
    first only, so a flagged unit with no finding is `noted` in a file with such findings, in a file with sample-table findings of the MP4 (which name samples outside the file or
    the mdat) and inside the declared length of a damaged unit that has one (the library takes the declared bytes as the frame)."""
    result = []
    findings = [f for f in analyzer["findings"] if aac_unit_finding(f)]
    merged = any(f["count"] > 1 for f in findings) or any(f["code"] in MP4_EXTRA for f in analyzer["findings"])
    units = [u for u in direct.get("units", []) if direct.get("transport") != "TT_MP4_ADIF"]
    all_refused = direct.get("stoppedBy") == "end" and not direct.get("runs") and direct.get("unitCount", 0) > 0
    matched = set()
    damaged = []  # (offset, declared length, finding) of the units with a finding
    for u in units:
        start, end = u["fedFrom"], u["offset"] + max(u["received"], 1)
        mine = [f for f in findings if start <= f["offset"] < end]
        for f in mine:
            matched.add((f["code"], f["offset"]))
        flagged = u["result"] != "decoded" or "error" in u or "fill" in u
        swallowed = next((d for d in damaged if d[0] < u["offset"] < d[0] + d[1]), None)
        if mine:
            damaged.append((u["offset"], u["declared"], mine[0]["code"]))
        if not mine and (all_refused or not flagged):
            continue
        what = u["result"] + (" " + ", ".join(u["error"] if isinstance(u["error"], list) else [u["error"]]) if "error" in u else "")
        entry = {"index": u["index"], "offset": u["offset"], "length": u["received"], "ranges": [[start, end - start]], "decoder": what,
                 "samples": u["samples"], "finding": mine[0]["code"] if mine else None, "findings": [f["code"] for f in mine]}
        if flagged and mine:
            entry.update({"verdict": "decoder %s, finding at the same bytes" % u["result"], "status": "agree"})
        elif flagged and u["result"] == "decoded":
            entry.update({"verdict": "decoder decodes and reports an error (%s), no finding" % what.split(" ", 1)[1], "status": "noted"})
        elif flagged and swallowed:
            entry.update({"verdict": "decoder %s, inside the declared length of the damaged unit at %d (%s)" % (u["result"], swallowed[0], swallowed[2]), "status": "noted"})
        elif flagged and merged:
            entry.update({"verdict": "decoder %s, no finding at its bytes (the file has findings that merge several cases or name samples outside the file: this may be one of them)" % u["result"], "status": "noted"})
        elif flagged:
            entry.update({"verdict": "decoder %s, no finding" % u["result"], "status": "pending"})
        else:
            entry.update({"verdict": "finding, decoder decodes", "status": "noted"})
        result.append(entry)
    for f in findings:
        if (f["code"], f["offset"]) not in matched:
            result.append({"index": None, "offset": f["offset"], "length": f["length"], "ranges": None, "decoder": "no unit", "samples": None, "finding": f["code"], "findings": [f["code"]],
                           "verdict": "finding points into no unit the decoder was fed", "status": "noted"})
    return result


def render_aac(results, not_run, unmatched, environment, ffmpeg_version, warnings, args):
    out = ["Cross-checks by the official decoder: AAC (FDK AAC %s, concealment method 1; ffmpeg as the second column)" % ((environment.get("fdkAac") or {}).get("version") or "-"),
           "run %s on %s (%s), ffmpeg %s, FDK commit %s" % (environment.get("runId") or args.run_id or "-", environment.get("imageOS") or "-", environment.get("imageVersion") or "-",
                                                         (ffmpeg_version or ["-"])[0], ((environment.get("fdkAac") or {}).get("commit") or "-")[:7]),
           "the coded stage is compared (FDK direct against the stream's samples); the trimmed figures of MP4 files are listed with no reference until Core Audio (rollout step 5)"]
    out += ["WARNING: " + w for w in warnings]
    rows = []
    for r in results:
        if r.get("notFed"):
            continue
        for fig in r["figures"]:
            wit = "; ".join("%s %s" % (k, show(v)) for k, v in fig["witnesses"].items())
            note = fig.get("noReference") or ""
            if fig.get("refused"):
                note = fig["refused"]
            if fig.get("concealedSamples"):
                note += (" " if note else "") + "(%s concealed)" % show(fig["concealedSamples"])
            if fig.get("rateDiffers"):
                note += (" " if note else "") + "(FDK %s samples at %s Hz, the analyzer's rate %s Hz: ratio %s)" % (
                    show(fig["rateDiffers"]["decoderSamples"]), show(fig["rateDiffers"]["decoderRate"]), show(fig["rateDiffers"]["analyzerRate"]), fig["rateDiffers"]["ratio"])
            tag = "" if fig["analyzer"] == fig["reference"] or fig["reference"] is None else "  DIFFERS"
            rows.append([r["folder"], r["file"], r["sha256"][:8], fig["id"], fig["stage"], show(fig["analyzer"]), show(fig["reference"]), wit, (note + tag).strip()])
    out += ["", table(rows, ["folder", "file", "sha256", "figure", "stage", "analyzer", "fdk", "other decoders", "notes"])]
    agreeing = sum(1 for r in results for u in r["units"] if u["status"] == "agree")
    out += ["", "Notes per file"] + (["  %s/%s: %s" % (r["folder"], r["file"], n) for r in results for n in r["notes"]] or ["  none"])
    out += ["", "Units where a decoder's verdict differs from the analyzer's; %d more agree (the decoder refuses, conceals or errs and a finding is at the same bytes)" % agreeing]
    unit_rows = [[r["folder"], r["file"], show(u.get("offset")), show(u.get("length")), u["decoder"], u.get("finding") or "-", u["verdict"], u["status"]]
                 for r in results for u in r["units"] if u["status"] != "agree"]
    out.append(table(unit_rows, ["folder", "file", "offset", "length", "decoder", "finding", "verdict", "status"]) if unit_rows else "none")
    out += ["", "Differences"]
    diff_rows = [[r["folder"], r["file"], d["figure"], d["decoder"], show(d["value"]), show(d["analyzer"]), d["kind"], d["status"]] for r in results for d in r["differences"]]
    out.append(table(diff_rows, ["folder", "file", "figure", "decoder", "value", "analyzer", "kind", "status"]) if diff_rows else "none")
    out += ["", "Files not fed to the decoder (%d), with the reason" % sum(1 for r in results if r.get("notFed"))]
    reasons = {}
    for r in results:
        if r.get("notFed"):
            reasons.setdefault(r["notFed"][:110], []).append("%s/%s" % (r["folder"], r["file"]))
    out += ["  %3d  %s  (%s)" % (len(v), k, ", ".join(v[:4]) + (", ..." if len(v) > 4 else "")) for k, v in sorted(reasons.items(), key=lambda kv: -len(kv[1]))] or ["  none"]
    out += ["", "Agreement (coded figures with a reference; groups counted apart)"]
    groups = (("aac, apple (encoder-written)", [r for r in results if r["folder"] in ("aac", "apple")]),
              ("adts (hand-built)", [r for r in results if r["folder"] == "adts"]),
              ("mp4", [r for r in results if r["folder"] == "mp4"]),
              ("derived variants", [r for r in results if r["folder"] == "derived"]),
              ("downloaded", [r for r in results if r["folder"] == "downloaded"]))
    for label, subset in groups:
        figs = [f for r in subset for f in r["figures"] if f["stage"] == "coded"]
        withref = [f for f in figs if f["reference"] is not None]
        same = [f for f in withref if f["analyzer"] == f["reference"]]
        ff = [f for f in withref if f["witnesses"].get("ffmpeg") is not None]
        ff_same = [f for f in ff if f["witnesses"]["ffmpeg"] == f["reference"]]
        ff_analyzer = [f for f in ff if f["witnesses"]["ffmpeg"] == f["analyzer"]]
        out.append("  %-30s files %3d (not fed %3d): figures %3d, with a reference %3d, analyzer equals it %3d, refused or none %3d; ffmpeg equals the reference in %d of %d, the analyzer in %d" % (
            label, len(subset), sum(1 for r in subset if r.get("notFed")), len(figs), len(withref), len(same), len(figs) - len(withref), len(ff_same), len(ff), len(ff_analyzer)))
    out += ["", "Derived variants by rule (coded figures: analyzer equals the reference / differs / no reference)"]
    rule_rows = []
    for rule in derive_variants.AAC_RULES:
        subset = [r for r in results if r["folder"] == "derived" and r["derived"].get("rule") == rule]
        figs = [f for r in subset for f in r["figures"] if f["stage"] == "coded"]
        same = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] == f["reference"])
        differs = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] != f["reference"])
        rule_rows.append([rule, len(subset), "%d / %d / %d" % (same, differs, len(figs) - same - differs)])
    out.append(table(rule_rows, ["rule", "files", "analyzer = / differs / none"]))
    twins = [r for r in results if r.get("sameBytesAs")]
    out += ["", "Variants with the same bytes as another (one row above stands for all of them; %d rows)" % len(twins)]
    out += ["  %s = %s" % (r["file"], ", ".join(r["sameBytesAs"])) for r in twins] or ["  none"]
    out += ["", "Not run (selected files without a record): " + (", ".join(not_run) if not_run else "none")]
    if unmatched:
        out.append("Records without a file here (another version of a fixture or variant): " + ", ".join(unmatched))
    return "\n".join(out) + "\n"


def aac_ffmpeg(args, path, tmp, record):
    """ffmpeg's counts: the coded count (MP4: the edit list ignored and +skip_manual, so that no trimming is applied) and its default (what it trims by itself)."""
    if args.no_ffmpeg or record.get("notFed"):
        return None
    mp4 = record["unitList"]["kind"] == "mp4"
    column = ffmpeg_column(args.ffmpeg, args.ffprobe, path, tmp, runs=(("default", []),))
    if mp4 and column.get("stream"):
        coded = ffmpeg_column(args.ffmpeg, args.ffprobe, path, tmp, runs=(("coded", ["-flags2", "+skip_manual"]),), input_flags=("-ignore_editlist", "1"))
        column["coded"] = coded.get("coded")
    return column


def apply_reviewed(results, source):
    """Puts the statuses and notes a person set in the committed file onto the fresh differences and units whose values are unchanged, so that the text shows what the JSON will say."""
    if not source.exists():
        return
    previous = {}
    for f in json.loads(source.read_text(encoding="utf-8")).get("files", []):
        for d in f.get("differences", []) + f.get("units", []):
            previous[unit_key(f["path"], f["sha256"], d)] = d
    for r in results:
        for d in r["differences"] + r["units"]:
            old = previous.get(unit_key(r["file"], r["sha256"], d))
            if old and old.get("statusSource") == "reviewed" and all(old.get(k) == d.get(k) for k in ("value", "analyzer", "finding", "verdict", "reason")):
                d["status"], d["note"], d["statusSource"] = old["status"], old.get("note", ""), "reviewed"
                if "awaitingStep" in old:
                    d["awaitingStep"] = old["awaitingStep"]


def run_aac_comparison(args, records, environments, ffmpeg_version, warnings, tmp):
    """The AAC section of a run: returns (text, JSON document, results, environment); None when there are no AAC records."""
    aac_records = records.get("aac", {})
    if not aac_records:
        return None
    environment = next((e for e in environments if e.get("format") == "aac"), {})
    items = []  # (sha, path, derived entry or None, folder)
    for folder in driver.AAC_FOLDERS:
        for path in probe.files(folder, driver.AAC_EXTENSIONS):
            items.append((sha256_of(path), path, None, folder))
    committed = {sha for sha, *_ in items}
    manifest = derive_variants.write_aac(tmp / "aacvariants")
    seen, aliases = set(), {}
    for entry in manifest:
        if entry["sha256"] in committed:
            aliases.setdefault(entry["sha256"], []).append(entry["name"])
            continue
        if entry["sha256"] in seen:
            aliases.setdefault(entry["sha256"], []).append(entry["name"])
            continue
        seen.add(entry["sha256"])
        items.append((entry["sha256"], tmp / "aacvariants" / entry["name"], entry, "derived"))
    downloaded = sorted(p for p in pathlib.Path(args.downloaded).iterdir() if p.is_file() and p.suffix.lower() in driver.AAC_EXTENSIONS) if args.downloaded else []
    for path in downloaded:
        items.append((sha256_of(path), path, {"downloaded": True}, "downloaded"))
    cli_reports = {}
    if args.encspect:
        cli_reports.update(run_cli(args.encspect, tmp / "aacvariants"))
        if downloaded:
            cli_reports.update(run_cli(args.encspect, pathlib.Path(args.downloaded)))
        apple = tmp / "apple-media"
        apple.mkdir()
        for path in probe.files("apple", driver.AAC_EXTENSIONS):
            (apple / path.name).write_bytes(path.read_bytes())
        cli_reports.update(run_cli(args.encspect, apple))

    def work(item):
        sha, path, derived, folder = item
        record = aac_records.get(sha)
        if record is None:
            return None
        analyzer, source = load_aac_analyzer(path, cli_reports)
        column = aac_ffmpeg(args, path, tmp, record)
        compared = compare_aac(record, analyzer, column)
        return {"file": path.name, "sha256": sha, "folder": folder, "derived": derived and ({"from": derived["source"], "rule": derived["rule"]} if "rule" in derived else {"downloaded": True}),
                "bytes": record["bytes"], "sameBytesAs": aliases.get(sha, []), "analyzerSource": source, "analyzerStreams": analyzer["streams"] if analyzer else None,
                "analyzerGapless": analyzer["gapless"] if analyzer else None, "ffmpeg": column, "unitList": record["unitList"], "record": summarize(record) if record["paths"] else {},
                **compared}

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [(item, pool.submit(work, item)) for item in sorted(items, key=lambda i: (i[3], i[1].name))]
    results, not_run = [], []
    for item, future in futures:
        result = future.result()
        if result is None:
            not_run.append("%s/%s" % (item[3], item[1].name))
        else:
            results.append(result)
    for folder in ("aac", "adts", "mp4"):
        apply_reviewed([r for r in results if r["folder"] == folder and not r["derived"]], FIXTURES / folder / "official-decoders.json")
    known = {sha for sha, *_ in items}
    unmatched = sorted(r["file"] for sha, r in aac_records.items() if sha not in known)
    text = render_aac(results, not_run, unmatched, environment, ffmpeg_version, warnings, args)
    document = {"schema": SCHEMA, "environment": environment, "ffmpeg": ffmpeg_version, "warnings": warnings, "files": results, "notRun": not_run, "recordsWithoutFile": unmatched}
    return text, document, results, environment


# ---- reading records ---------------------------------------------------------------------------------------------------

def load_records(folders):
    records, environments = {}, []
    for folder in folders:
        folder = pathlib.Path(folder)
        for env in sorted(folder.rglob("environment.json")):
            content = json.loads(env.read_text(encoding="utf-8"))
            if "format" in content:  # not the probe's
                environments.append(content)
        for path in sorted(folder.rglob("records/*/*.json")):
            record = json.loads(path.read_text(encoding="utf-8"))
            if record.get("schema") != SCHEMA or "format" not in record:
                continue  # a record of the probe (rollout step 1)
            records.setdefault(record["format"], {})[record["sha256"]] = record
    return records, environments


def fixture_files(format_):
    folder = FIXTURES / format_
    return sorted(p for p in folder.iterdir() if p.is_file() and p.suffix in (".ogg", ".opus"))


# ---- text ------------------------------------------------------------------------------------------------------------

def show(value):
    return "-" if value is None else "{:,}".format(value) if isinstance(value, int) else str(value)


def table(rows, header):
    widths = [max(len(str(r[i])) for r in [header] + rows) for i in range(len(header))]
    return "\n".join("  ".join(str(c).ljust(widths[i]) for i, c in enumerate(r)).rstrip() for r in [header, ["-" * w for w in widths]] + rows)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folders", nargs="+")
    parser.add_argument("--encspect", help="the CLI, for the analyzer's figures of the derived variants")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--out", default=".")
    parser.add_argument("--run-id")
    parser.add_argument("--write-official", help="write tests/fixtures/ogg/official-decoders.json here")
    parser.add_argument("--write-official-tta", help="write tests/fixtures/tta/official-decoders.json here (the previous file given with --previous-tta, default the same file)")
    parser.add_argument("--write-official-aac", help="write official-decoders.<folder>.draft.json for the committed AAC folders into this directory (the previous files are those in tests/fixtures/<folder>)")
    parser.add_argument("--downloaded", help="the folder of the downloaded AAC samples (never committed), for the AAC section")
    parser.add_argument("--previous-tta", help="the committed tests/fixtures/tta/official-decoders.json whose reviewed statuses and notes are kept")
    parser.add_argument("--previous", help="the committed official-decoders.json whose reviewed statuses and notes are kept (default: the file --write-official names, if it exists)")
    parser.add_argument("--no-ffmpeg", action="store_true")
    args = parser.parse_args(argv)

    records, environments = load_records(args.folders)
    environment = next((e for e in environments if e.get("format") == "ogg"), {})
    ffmpeg_version = None if args.no_ffmpeg else run_tool([args.ffmpeg, "-version"])[1].decode("utf-8", "replace").splitlines()[:1]
    warnings = []
    if ffmpeg_version and "8.0.1" not in ffmpeg_version[0]:
        warnings.append("ffmpeg is %s; the design pins 8.0.1, so the ffmpeg column is not the one the committed results come from" % ffmpeg_version[0])
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="cod-"))
    files = {}   # sha256 -> (path, derived entry or None)
    for path in fixture_files("ogg"):
        files[sha256_of(path)] = (path, None)
    manifest = derive_variants.write_ogg(tmp / "variants")
    for entry in manifest:
        files[entry["sha256"]] = (tmp / "variants" / entry["name"], entry)
    cli_reports = run_cli(args.encspect, tmp / "variants") if args.encspect else {}

    selected = {sha: item for sha, item in files.items()}
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    ogg_records = records.get("ogg", {})
    results, not_run = [], []

    def work(sha):
        path, derived = selected[sha]
        record = ogg_records.get(sha)
        if record is None:
            return None
        analyzer, source = load_analyzer(path, cli_reports)
        column = None if args.no_ffmpeg else ffmpeg_column(args.ffmpeg, args.ffprobe, path, tmp)
        compared = compare_ogg(record, analyzer, column)
        return {"file": path.name, "sha256": sha, "derived": derived and {"from": derived["source"], "rule": derived["rule"]}, "bytes": record["bytes"],
                "analyzerSource": source, "analyzerStreams": analyzer["streams"] if analyzer else None, "ffmpeg": column, "record": summarize(record), **compared}

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = {sha: pool.submit(work, sha) for sha in sorted(selected, key=lambda s: selected[s][0].name)}
    other_files = []
    for sha, future in futures.items():
        result = future.result()
        if result is None:
            not_run.append(selected[sha][0].name)
        elif not any(s["codec"] in ("opus", "vorbis") for s in ogg_records[sha]["paths"]["direct"]["streams"]):
            other_files.append(result["file"])
        else:
            results.append(result)
    unmatched = sorted(r["file"] for sha, r in ogg_records.items() if sha not in selected)

    text = render(results, not_run, unmatched, other_files, environment, ffmpeg_version, warnings, args)
    (out / "comparison.txt").write_text(text, encoding="utf-8")
    (out / "comparison.json").write_text(json.dumps({"schema": SCHEMA, "environment": environment, "ffmpeg": ffmpeg_version, "warnings": warnings, "files": results,
                                                     "notRun": not_run, "recordsWithoutFile": unmatched, "filesWithoutOpusOrVorbis": other_files}, indent=1) + "\n", encoding="utf-8")
    if args.write_official:
        write_official(pathlib.Path(args.write_official), pathlib.Path(args.previous) if args.previous else None, results, environment, ffmpeg_version, args.run_id)
    print(text.split("\n\n")[0])
    print("comparison.txt: %d files compared, %d not run" % (len(results), len(not_run)))
    tta = run_tta_comparison(args, records, environments, ffmpeg_version, warnings, tmp)
    if tta:
        tta_text, tta_document, tta_results, tta_environment = tta
        (out / "comparison-tta.txt").write_text(tta_text, encoding="utf-8")
        (out / "comparison-tta.json").write_text(json.dumps(tta_document, indent=1) + "\n", encoding="utf-8")
        if args.write_official_tta:
            write_official(pathlib.Path(args.write_official_tta), pathlib.Path(args.previous_tta) if args.previous_tta else None, tta_results, tta_environment, ffmpeg_version,
                           args.run_id, "tta")
        print("comparison-tta.txt: %d files compared, %d not run" % (len(tta_results), len(tta_document["notRun"])))
    aac = run_aac_comparison(args, records, environments, ffmpeg_version, warnings, tmp)
    if aac:
        aac_text, aac_document, aac_results, aac_environment = aac
        (out / "comparison-aac.txt").write_text(aac_text, encoding="utf-8")
        (out / "comparison-aac.json").write_text(json.dumps(aac_document, indent=1) + "\n", encoding="utf-8")
        if args.write_official_aac:
            target = pathlib.Path(args.write_official_aac)
            target.mkdir(parents=True, exist_ok=True)
            for folder in ("aac", "adts", "mp4"):
                subset = [r for r in aac_results if r["folder"] == folder and not r["derived"]]
                write_official(target / ("official-decoders.%s.draft.json" % folder), FIXTURES / folder / "official-decoders.json", subset, aac_environment, ffmpeg_version, args.run_id, "aac")
        print("comparison-aac.txt: %d files compared, %d not run" % (len(aac_results), len(aac_document["notRun"])))
    return 0


def summarize(record):
    paths = {}
    for name, p in record["paths"].items():
        paths[name] = {"tool": p["tool"], "stoppedBy": p["stoppedBy"], "totalSamples": p.get("totalSamples"), "partialSamples": p.get("partialSamples"),
                       "warnings": p.get("warnings", [])[:3]}
    return paths


def render(results, not_run, unmatched, other_files, environment, ffmpeg_version, warnings, args):
    out = ["Cross-checks by the official decoders: Ogg (Opus by libopus, Vorbis by libvorbis)",
           "run %s on %s (%s), ffmpeg %s" % (environment.get("runId") or args.run_id or "-", environment.get("imageOS") or "-", environment.get("imageVersion") or "-", (ffmpeg_version or ["-"])[0]),
           "dpkg: " + ", ".join("%s %s" % (k, v) for k, v in sorted(environment.get("dpkg", {}).items()) if v)]
    out += ["WARNING: " + w for w in warnings]
    rows = []
    for r in results:
        for fig in r["figures"]:
            wit = "; ".join("%s %s" % (k, show(v)) for k, v in fig["witnesses"].items())
            note = fig.get("noReference") or ""
            if fig["stage"] == "trimmed" and fig.get("readerTotal") is not None and fig["reference"] is None:
                note = "reader %s: %s" % (show(fig["readerTotal"]), note)
            tag = "" if fig["analyzer"] == fig["reference"] or fig["reference"] is None else "  DIFFERS"
            rows.append([r["file"], r["sha256"][:8], fig["id"], fig["stage"], show(fig["analyzer"]), show(fig["reference"]), fig["referencePath"] or "-", wit, (note + tag).strip()])
    out.append("")
    out.append(table(rows, ["file", "sha256", "figure", "stage", "analyzer", "reference", "path", "other decoders", "notes"]))

    agreeing = sum(1 for r in results for u in r["units"] if u["status"] == "agree")
    file_notes = [(r["file"], n) for r in results for n in r["notes"]]
    out += ["", "Notes per file"] + (["  %s: %s" % p for p in file_notes] or ["  none"])
    out += ["", "Units where a decoder's verdict differs from the analyzer's (a finding that merges several packets points to the first only); %d more agree (the decoder refuses and a finding is at the same bytes)" % agreeing]
    unit_rows = []
    for r in results:
        for u in r["units"]:
            if u["status"] == "agree":
                continue
            unit_rows.append([r["file"], show(u.get("offset")), show(u.get("length")), u["decoder"], u.get("finding") or "-", u["verdict"], u["status"]])
    out.append(table(unit_rows, ["file", "offset", "length", "decoder", "finding", "verdict", "status"]) if unit_rows else "none")

    out += ["", "Differences"]
    diff_rows = []
    for r in results:
        for d in r["differences"]:
            diff_rows.append([r["file"], d["figure"], d["decoder"], show(d["value"]), show(d["analyzer"]), d["kind"], d["status"]])
    out.append(table(diff_rows, ["file", "figure", "decoder", "value", "analyzer", "kind", "status"]) if diff_rows else "none")

    out += ["", "Agreement (files with a reference figure; committed fixtures and derived variants counted apart)"]
    for label, subset in (("committed", [r for r in results if not r["derived"]]), ("derived variants", [r for r in results if r["derived"]])):
        for stage in ("trimmed", "coded"):
            figs = [f for r in subset for f in r["figures"] if f["stage"] == stage]
            withref = [f for f in figs if f["reference"] is not None]
            same = [f for f in withref if f["analyzer"] == f["reference"]]
            ff = [f for f in withref if any(k.startswith("ffmpeg") and v is not None for k, v in f["witnesses"].items())]
            ffsame = [f for f in ff if all(v == f["reference"] for k, v in f["witnesses"].items() if k.startswith("ffmpeg") and v is not None)]
            out.append("  %-16s %-8s figures %3d, with a reference %3d, analyzer equals it %3d, no reference %3d; ffmpeg equals the reference in %d of %d" % (
                label, stage, len(figs), len(withref), len(same), len(figs) - len(withref), len(ffsame), len(ff)))
    out += ["", "Variant groups, decided by the project lead on 2026-11-01 (recorded here and in the docs, not in the JSON)",
            "  (a) noted: a repeated page, which opusdec and oggdec skip while the analyzer and libogg count it (14 of 24 files); the analyzer equals libogg's own packet stream",
            "  (b) to be looked into in a follow-up, no conclusion yet: a repeated packet, where the analyzer is 38 to 128 samples below oggdec and ffmpeg agrees with oggdec (10 of 24 files)"]
    out += ["", "Derived variants by rule (figures: analyzer equals the reference / differs / no reference)"]
    rule_rows = []
    for rule in derive_variants.OGG_RULES:
        subset = [r for r in results if r["derived"] and r["derived"]["rule"] == rule]
        row = [rule, len(subset)]
        for stage in ("trimmed", "coded"):
            figs = [f for r in subset for f in r["figures"] if f["stage"] == stage]
            same = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] == f["reference"])
            differs = sum(1 for f in figs if f["reference"] is not None and f["analyzer"] != f["reference"])
            row.append("%d / %d / %d" % (same, differs, len(figs) - same - differs))
        rule_rows.append(row)
    out.append(table(rule_rows, ["rule", "files", "trimmed", "coded"]))
    out += ["", "Files with no Opus or Vorbis stream, counted apart (%d): %s" % (len(other_files), ", ".join(other_files) or "none")]
    out += ["", "Not run (selected files without a record): " + (", ".join(not_run) if not_run else "none")]
    if unmatched:
        out.append("Records without a file here (another version of a fixture or variant): " + ", ".join(unmatched))
    return "\n".join(out) + "\n"


# ---- the committed results ---------------------------------------------------------------------------------------------

def unit_key(path, sha, d):
    return (path, sha, d.get("figure") or "unit", d.get("decoder"), d.get("offset"), d.get("kind"), d.get("reference"))


def write_official(target, previous_file, results, environment, ffmpeg_version, run_id, format_="ogg"):
    previous, previous_figures, previous_run = {}, {}, {}
    source = previous_file or target
    if source.exists():
        old_doc = json.loads(source.read_text(encoding="utf-8"))
        previous_run = old_doc.get("run", {})
        for f in old_doc.get("files", []):
            for fig in f.get("figures", []):
                previous_figures[(f["path"], f["sha256"], fig["id"])] = fig
            for d in f.get("differences", []) + f.get("units", []):
                previous[unit_key(f["path"], f["sha256"], d)] = d
    # A figure that differs from the committed one for the same file bytes is `build-dependent` only when the decoder packages differ between the runs;
    # a different figure from the same packages (a changed harness or script) is listed for review and replaces the old one.
    if format_ == "ogg":
        tools = {"packages": environment.get("dpkg"), "oggdec": environment.get("oggdec"), "opusdec": environment.get("opusdec")}
    elif format_ == "aac":
        tools = {"fdkAac": environment.get("fdkAac"), "concealMethod": environment.get("concealMethod"), "gcc": environment.get("gcc")}
    else:
        tools = {"tta": environment.get("tta"), "ttaArchiveSha256": environment.get("ttaArchiveSha256"), "gcc": environment.get("gcc")}
    packages_differ = any(previous_run.get(k) != v for k, v in tools.items())
    files, changed = [], []
    for r in results:
        if r["derived"]:
            continue
        rel = r["file"]
        figures = [dict(f) for f in r["figures"]]
        extra = []
        for fig in figures:
            old = previous_figures.get((rel, r["sha256"], fig["id"]))
            if old and old.get("reference") is not None and fig["reference"] is not None and old["reference"] != fig["reference"]:
                if packages_differ:
                    extra.append({"figure": fig["id"], "decoder": fig["referencePath"], "value": fig["reference"], "analyzer": fig["analyzer"], "reference": old["reference"],
                                  "status": "build-dependent", "kind": "build-dependent", "previousRun": previous_run.get("id"), "previousImage": previous_run.get("imageVersion"),
                                  "image": environment.get("imageVersion")})
                    fig["reference"] = old["reference"]
                else:
                    changed.append("%s %s: %s -> %s" % (rel, fig["id"], old["reference"], fig["reference"]))
        entry = {"path": rel, "sha256": r["sha256"], "figures": figures, "differences": [], "units": []}
        if r.get("notFed"):
            entry["notFed"] = r["notFed"]
        if format_ == "aac":
            entry["unitList"] = {k: r["unitList"].get(k) for k in ("kind", "codec", "units", "cutUnits", "skippedRanges", "skippedBytes", "timescale") if r["unitList"].get(k) is not None}
            if r.get("codecs"):
                entry["codecs"] = r["codecs"]
        for kind, listing in (("differences", r["differences"] if format_ == "aac" else figure_differences(figures) + extra), ("units", r["units"])):
            for d in listing:
                old = previous.get(unit_key(rel, r["sha256"], d))
                same = old and all(old.get(k) == d.get(k) for k in ("value", "analyzer", "finding", "verdict", "reason"))
                if same and old.get("statusSource") == "reviewed":
                    d["status"], d["note"], d["statusSource"] = old["status"], old.get("note", ""), "reviewed"
                    if "awaitingStep" in old:
                        d["awaitingStep"] = old["awaitingStep"]
                else:
                    d["statusSource"] = "rule"
                entry[kind].append(d)
        files.append(entry)
    for line in changed:
        print("changed figure, same packages (review): " + line, file=sys.stderr)
    doc = {"schema": SCHEMA, "format": format_, "run": {"id": run_id or environment.get("runId"), "commit": environment.get("commit"), "imageOS": environment.get("imageOS"),
                                                         "imageVersion": environment.get("imageVersion"), "ffmpeg": (ffmpeg_version or [None])[0], **tools,
                                                         },
           "statuses": list(STATUSES), "changedFiguresForReview": changed, "files": files}
    target.write_text(json.dumps(doc, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
