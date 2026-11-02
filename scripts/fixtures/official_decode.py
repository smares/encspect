#!/usr/bin/env python3
"""The Linux driver of the official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, "Records and the comparison").

  official_decode.py ogg --out DIR --bin DIR     (DIR holds ogg_decode)
  official_decode.py tta --out DIR --tta PROGRAM (PROGRAM is libtta-c's `tta`)
  official_decode.py aac --out DIR --fdk-decode PROGRAM --fdk-lib DIR [--downloaded DIR] [--fdk-commit SHA] [--fdk-version TAG]
                                                 (PROGRAM is fdk_decode built against FDK AAC, DIR holds its libfdk-aac.so)

Every decoder call is a child process of its own with a timeout, so that a crash or a stall is recorded and the run goes on. The C harness writes
one JSON line per packet and flushes it, so a killed child leaves what it did; this script keeps those lines and assembles the file's record:

  OUT/records/ogg/<file>.json   one record per file: the file's path, size and SHA-256 and per path (reader, direct, directGranule) its result
  OUT/variants.json             the derived variants of derive_variants.py with their SHA-256
  OUT/environment.json          versions of everything that ran, written before the first file and again at the end
  OUT/log.txt                   one line per record

A path's result holds: whether it opened (and the failure), the output formats it reports, one total per run of constant output format, the units
(the packets: file ranges, bytes, result code, samples), what libogg dropped, the standard error of the tools apart from how the path stopped, and
stoppedBy (end, error, timeout or crash); totalSamples only for end, partialSamples otherwise. Records hold no media: derived variants and decoded
output are written to a temporary folder and deleted.

AAC has one path, the direct path: fdk_decode feeds Fraunhofer's FDK AAC decoder one unit of the unit list (unit_list.py) at a time, concealment method 1 for every
object type, and drains ceil(outputDelay / frameSize) frames at the end, counted apart (flushedSamples). The units of the record are the extractor's, each with the
decoder's result (decoded, concealed, refused, no output), the samples it output and where the library's byte counter stood after it.

Core Audio (macOS) has two paths per file (rollout step 5): the reader, ExtAudioFile on the file (which applies the packet table: iTunSMPB or the edit list), and the converter,
AudioConverter fed the units of the unit list one at a time with the file's stream description and magic cookie; the records are `coreaudio-aac` and `coreaudio-ac3`:

  official_decode.py coreaudio --out DIR --probe PROGRAM [--downloaded DIR]   (PROGRAM is macos/audiotoolbox_probe built with clang)

TTA has one path, the reader `tta -d` (`tta -dp secret` for the file written with that password, beside a run without the password): TTA has no direct path
(decision 6). Its samples are counted from the bytes written after the WAVE header with the channel count and bits per sample of the WAVE header it wrote, never
from the declared size, which only checks completeness: fewer data bytes than the header declares is a stop. A nonzero exit status, a crash, a timeout or a missing output file is a stop with its reason, and the sample count of what a stopped run
left is `partialSamples`, never a figure.
"""

import argparse
import concurrent.futures
import json
import os
import pathlib
import platform
import re
import shutil
import struct
import sys
import tempfile
import threading
import time

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import derive_variants  # noqa: E402
import official_probe as probe  # noqa: E402

SCHEMA = 1
FIXTURES = probe.FIXTURES
LOG_LOCK = threading.Lock()
UNIT_LIMIT = 20000
AAC_UNIT_LIMIT = 100000  # the long fragmented files (about 41,000 units) are read whole; two lines per unit


# ---- records ---------------------------------------------------------------------------------------------------------

class Records:
    def __init__(self, directory):
        self.directory = pathlib.Path(directory)
        self.log = open(self.directory / "log.txt", "a", encoding="utf-8")
        self.count = 0

    def write(self, format_, path, record):
        folder = self.directory / "records" / format_
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / (record["file"] + ".json")
        if target.exists():
            target = folder / ("%s-%s.json" % (record["file"], record["sha256"][:8]))
        target.write_text(json.dumps(record, separators=(",", ":")) + "\n", encoding="utf-8")
        with LOG_LOCK:
            self.count += 1
            self.log.write("%s %s %s\n" % (format_, record["file"], " ".join("%s:%s" % (k, v["stoppedBy"]) for k, v in record["paths"].items())))
            self.log.flush()


def file_record(path, format_, derived=None):
    data = pathlib.Path(path).read_bytes()
    record = {"schema": SCHEMA, "format": format_, "file": pathlib.Path(path).name, "bytes": len(data), "sha256": probe.hashlib.sha256(data).hexdigest()}
    if derived:
        record["derived"] = derived
    else:
        record["path"] = probe.relative(path)
    return record


# ---- Ogg: the direct paths -------------------------------------------------------------------------------------------

def stopped_by(child, end_line):
    if child["timedOut"]:
        return "timeout"
    if child["signal"]:
        return "crash"
    if end_line is None or child["exit"] != 0:
        return "error"
    return end_line.get("stoppedBy", "end")


def direct_result(child, mode):
    """The record of one run of ogg_decode: streams, units, totals per run of constant output format, what libogg dropped."""
    lines = child["lines"]
    end = next((l for l in lines if l.get("type") == "end"), None)
    env = next((l for l in lines if l.get("type") == "env"), {})
    streams, units, dropped = {}, [], []
    holes = refused = 0
    for l in lines:
        kind = l.get("type")
        if kind == "stream":
            streams[l["stream"]] = {"stream": l["stream"], "serial": l["serial"], "offset": l["offset"], "codec": None, "lastGranule": None, "samples": 0}
        elif kind == "skip":
            dropped.append({"offset": l["offset"], "bytes": l["bytes"]})
        elif kind == "hole":
            holes += 1
            units.append({"result": "hole", "stream": l["stream"], "pageOffset": l["pageOffset"]})
        elif kind == "pagein":
            refused += 1
        elif kind == "packet":
            s = streams.get(l["stream"])
            if s is None:
                continue
            if "codec" in l:
                s["codec"] = l["codec"]
            result = l.get("result")
            if result == "head":
                s["channels"], s["family"], s["preSkip"], s["created"] = l["channels"], l["family"], l["preSkip"], l["created"]
            elif result == "header":
                s.setdefault("headerPackets", []).append({"index": l["index"], "error": l["error"], "initError": l.get("initError", 0)})
            elif result == "tags":
                s["tagsIndex"] = l["index"]
            else:
                if l["granulepos"] >= 0 and result != "other codec":
                    s["lastGranule"] = l["granulepos"]  # the last audio page's, in file order; header pages carry 0 and are not audio
                unit = {"index": l["index"], "stream": l["stream"], "ranges": l["ranges"], "pageOffset": l["pageOffset"], "bytes": l["bytes"], "result": result}
                for key in ("samples", "tocSamples", "error", "granulepos", "eos"):
                    if key in l and (key not in ("granulepos", "eos") or l[key] not in (-1, 0)):
                        unit[key] = l[key]
                if result == "decoded":
                    s["samples"] += l["samples"]
                    s["rate"], s["channels"] = l["rate"], l["channels"]
                    s["decodedUnits"] = s.get("decodedUnits", 0) + 1
                elif result == "not an OpusHead":
                    s["refusal"] = "not passed to libopus: the first packet is not an OpusHead (the harness's check, RFC 7845 section 5.1)"
                elif result == "not a Vorbis identification header":
                    s["refusal"] = "libvorbis is not given the stream: the first packet is not a Vorbis identification header (the harness's check)"
                elif (result or "").startswith("not decoded"):
                    why = result[len("not decoded: "):]
                    s["refusal"] = ("libopus refuses the harness's family-0 reading (1 stream, uncoupled, identity mapping) of a header with %s (code %d)" % ("more than 2 channels", l["created"]))\
                        if why.startswith("family 0 with more") else ("libopus refuses the OpusHead (%s, code %d)" % (why, l["created"])) if l.get("by") == "libopus" else \
                        "not passed to libopus: the harness's OpusHead check fails (%s; RFC 7845 section 5.1)" % why
                units.append(unit)
    for s in streams.values():
        if s["codec"] == "vorbis" and "refusal" not in s:
            packets = s.get("headerPackets", [])
            bad = next((i for i, h in enumerate(packets) if h["error"] != 0), None)
            if bad is not None:
                s["refusal"] = "libvorbis refuses header packet %d (code %d)" % (bad + 1, packets[bad]["error"])
            elif any(h["initError"] != 0 for h in packets):
                s["refusal"] = "libvorbis accepts the three headers but vorbis_synthesis_init fails (code %d)" % next(h["initError"] for h in packets if h["initError"] != 0)
            elif len(packets) < 3:
                s["refusal"] = "libvorbis is given %d of the 3 header packets" % len(packets)
        if s["codec"] == "opus" and "refusal" not in s and s.get("created", 0) != 0:
            s["refusal"] = "libopus does not create a decoder from the OpusHead (code %d)" % s["created"]
        if s["codec"] == "opus" and "refusal" not in s and "channels" not in s:
            s["refusal"] = "no OpusHead"
    # one total per run of constant output format: consecutive streams with the same rate and channels
    runs = []
    for s in sorted(streams.values(), key=lambda x: x["stream"]):
        if "decodedUnits" not in s:
            continue
        key = (s["rate"], s["channels"], s["codec"])
        if runs and runs[-1]["_key"] == key:
            runs[-1]["samples"] += s["samples"]
            runs[-1]["units"] += s["decodedUnits"]
            runs[-1]["streams"].append(s["stream"])
        else:
            runs.append({"_key": key, "rate": s["rate"], "channels": s["channels"], "samples": s["samples"], "units": s["decodedUnits"], "streams": [s["stream"]]})
    for run in runs:
        del run["_key"]
    stopped = stopped_by(child, end)
    notes = []
    incomplete = child.get("linesElided") or child.get("lastLineTruncated") or any(l.get("type") == "unparsed" for l in lines)
    if incomplete and stopped == "end":
        stopped = "limit"
        notes.append("the output was cut (lines elided, truncated or not parseable); the per-stream figures are partial")
    total = end["codedSamples"] if end and not incomplete else sum(r["samples"] for r in runs)
    if end and not incomplete and total != sum(r["samples"] for r in runs):
        stopped = "limit"
        notes.append("the harness counted %d samples and the lines add up to %d" % (total, sum(r["samples"] for r in runs)))
    result = {
        "tool": "ogg_decode " + mode,
        "decoder": {"libvorbis": env.get("libvorbis"), "libopus": env.get("libopus")},
        "opened": True,
        "streams": list(streams.values()),
        "runs": runs,
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "elapsedMs": child["elapsedMs"],
        "unitCount": len(units),
        "units": units if len(units) <= UNIT_LIMIT else units[:UNIT_LIMIT],
        "dropped": dropped,
        "holes": holes,
        "refusedPages": refused,
        "pagesAfterEndOfStream": end.get("pagesAfterEndOfStream", 0) if end else None,
        "skippedBytes": end["skippedBytes"] if end else None,
        "trailingBytes": end["trailingBytes"] if end else None,
        "pages": end["pages"] if end else None,
        "fedEveryPage": bool(end) and end["skippedBytes"] == 0 and end["trailingBytes"] == 0 and refused == 0,
        "warnings": [l for l in child["stderr"].splitlines() if l.strip()],
    }
    if len(units) > UNIT_LIMIT:
        result["unitsElided"] = True
        notes.append("units elided above %d" % UNIT_LIMIT)
    result["totalSamples" if stopped == "end" else "partialSamples"] = total
    if end and end.get("pagesAfterEndOfStream"):
        notes.append("%d page(s) after the end-of-stream page, fed (libogg accepts them)" % end["pagesAfterEndOfStream"])
    if holes or dropped:
        notes.append("libogg dropped %d bytes in %d places and reported %d lost packets; the decoder never saw them" % (sum(d["bytes"] for d in dropped), len(dropped), holes))
    if notes:
        result["notes"] = notes
    return result


# ---- Ogg: the readers ------------------------------------------------------------------------------------------------

PROGRESS = re.compile(r"\[[^\]]*\]\s*(\d\d:\d\d:\d\d)?")
OPUSDEC_CHATTER = re.compile(r"^(Decoding to \d+ Hz|Decoding complete|Encoded with |[A-Za-z_]+=)")


def wav_info(path):
    """What a decoder's WAVE output says about itself: rate, channels, bits, the offset of the sample bytes and how many follow (never the declared size)."""
    data = pathlib.Path(path).read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    pos, rate, channels, bits = 12, None, None, None
    while pos + 8 <= len(data):
        kind, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        if kind == b"fmt " and pos + 24 <= len(data):
            channels, rate, bits = struct.unpack_from("<H", data, pos + 10)[0], struct.unpack_from("<I", data, pos + 12)[0], struct.unpack_from("<H", data, pos + 22)[0]
        elif kind == b"data":
            return {"rate": rate, "channels": channels, "bits": bits, "headerBytes": pos + 8, "dataBytes": len(data) - pos - 8, "declaredDataBytes": size}
        pos += 8 + size + (size & 1)
    return {"rate": rate, "channels": channels, "bits": bits, "headerBytes": None, "dataBytes": 0}


def reader_result(codec, path, tmp, decoder_versions):
    """oggdec for Vorbis, opusdec for Opus; the samples are counted from the bytes written, with the channel count from the tool's own output."""
    stem = pathlib.Path(tmp) / ("out-" + pathlib.Path(path).name)
    if codec == "vorbis":
        target = stem.with_suffix(stem.suffix + ".wav")
        child = probe.run_child("oggdec", ["oggdec", "-b", "16", "-e", "0", "-s", "1", "-o", str(target), str(path)], jsonl=False)
    else:
        target = stem.with_suffix(stem.suffix + ".raw")
        child = probe.run_child("opusdec", ["opusdec", "--rate", "48000", str(path), str(target)], jsonl=False)
    text = child["stderr"].replace("\r", "\n").replace(str(path), pathlib.Path(path).name)
    lines = [l.strip() for l in text.splitlines() if l.strip()]
    stated = re.search(r"Decoding to (\d+) Hz \((\d+) channels?\)", text)
    warnings = []
    for line in lines:
        line = PROGRESS.sub("", line).strip() if "%]" in line or re.match(r"^\[.\]", line) else line
        if not line or line.startswith("oggdec from") or line.startswith('Decoding "') or OPUSDEC_CHATTER.match(line):
            continue
        warnings.append(line)
    written = target.stat().st_size if target.exists() else None
    info = wav_info(target) if codec == "vorbis" and written else None
    if codec == "vorbis":
        rate, channels = (info["rate"], info["channels"]) if info else (None, None)
        data_bytes = info["dataBytes"] if info else (0 if written is not None else None)
        bits = info["bits"] if info else 16
    else:
        rate, channels = (int(stated.group(1)), int(stated.group(2))) if stated else (None, None)
        data_bytes, bits = written, 16
    frame = (bits // 8) * channels if channels and bits else None
    stopped = "end"
    if child["timedOut"]:
        stopped = "timeout"
    elif child["signal"]:
        stopped = "crash"
    elif child["exit"] != 0 or written is None or any(w.startswith(("Failed to open", "Decoding error", "Logical bitstreams with changing parameters")) for w in warnings):
        stopped = "error"
    result = {
        "tool": "oggdec" if codec == "vorbis" else "opusdec",
        "decoder": decoder_versions,
        "opened": not any(w.startswith("Failed to open") for w in warnings) and written is not None,
        "formats": [{"rate": rate, "channels": channels, "bits": bits}] if channels else [],
        "runs": [{"rate": rate, "channels": channels, "samples": data_bytes // frame, "outputBytes": data_bytes, "remainder": data_bytes % frame}] if frame and data_bytes is not None else [],
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "timedOut": child["timedOut"], "elapsedMs": child["elapsedMs"],
        "warnings": warnings[:40],
    }
    if codec == "opus" and stated is None and written:
        result["notes"] = ["opusdec printed no channel count; the samples are not counted"]
    samples = result["runs"][0]["samples"] if result["runs"] else None
    if samples is not None:
        result["totalSamples" if stopped == "end" else "partialSamples"] = samples
    if target.exists():
        target.unlink()
    return result


# ---- the Ogg run -----------------------------------------------------------------------------------------------------

def tool_versions():
    env = {"gcc": probe.tool_line(["gcc", "--version"]), "oggdec": probe.tool_line(["oggdec", "--version"]), "opusdec": probe.tool_line(["opusdec", "--version"]), "dpkg": {}}
    for package in ("libogg0", "libogg-dev", "libopus0", "libopus-dev", "libopusfile0", "libvorbis0a", "libvorbisfile3", "libvorbis-dev", "opus-tools", "vorbis-tools"):
        env["dpkg"][package] = probe.tool_line(["dpkg-query", "-W", "-f=${Version}", package])
    return env


def run_ogg(args):
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "records").mkdir(exist_ok=True)
    records = Records(out)
    bins = pathlib.Path(args.bin)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="odd-"))
    started = time.time()
    env = {"platform": "linux", "python": sys.version, "kernel": platform.platform(), "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"),
           "runId": os.environ.get("GITHUB_RUN_ID"), "commit": os.environ.get("GITHUB_SHA"), "format": "ogg", **tool_versions()}
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    manifest = derive_variants.write_ogg(tmp / "variants")
    (out / "variants.json").write_text(json.dumps({"format": "ogg", "rules": list(derive_variants.OGG_RULES), "variants": manifest}, indent=1) + "\n", encoding="utf-8")
    by_name = {m["name"]: m for m in manifest}
    committed = sorted(p for p in (FIXTURES / "ogg").iterdir() if p.is_file() and p.suffix in (".ogg", ".opus"))
    work = [(p, None) for p in committed] + [(tmp / "variants" / m["name"], {"from": m["source"], "fromSha256": m["sourceSha256"], "rule": m["rule"]}) for m in manifest]

    def case(item):
        path, derived = item
        record = file_record(path, "ogg", derived)
        direct = probe.run_child("direct", [bins / "ogg_decode", "direct", path])
        paths = {"direct": direct_result(direct, "direct")}
        codecs = {s["codec"] for s in paths["direct"]["streams"]}
        versions = paths["direct"]["decoder"]
        if "vorbis" in codecs:
            granule = probe.run_child("directGranule", [bins / "ogg_decode", "granule", path])
            paths["directGranule"] = direct_result(granule, "granule")
        # A file with both codecs gets the reader of the first stream's codec; none is in the corpus, and the record says if one appears.
        first = next((s["codec"] for s in paths["direct"]["streams"] if s["codec"] in ("opus", "vorbis")), None)
        if first:
            paths["reader"] = reader_result(first, path, tmp, {"libvorbis": versions["libvorbis"]} if first == "vorbis" else {"libopus": versions["libopus"]})
            if len(codecs & {"opus", "vorbis"}) > 1:
                paths["reader"].setdefault("notes", []).append("the file has both codecs; only the first stream's codec has a reader run")
        else:
            record["note"] = "no Opus or Vorbis stream"
        record["paths"] = paths
        records.write("ogg", path, record)

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for future in [pool.submit(case, item) for item in work]:
            future.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    env["records"] = records.count
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


# ---- the TTA run -----------------------------------------------------------------------------------------------------

TTA_PASSWORD = "secret"  # the password make_external_tta_fixtures.py wrote external-libtta-16-stereo-password.tta with
TTA_PASSWORD_FILES = {"external-libtta-16-stereo-password.tta"}  # the files `tta -dp secret` decodes: this one and its derived variants
TTA_CHATTER = re.compile(r"^(TTA1 lossless audio encoder/decoder, version [\d.]+|Decoding: .*|Progress: \d+%.*|Time: [\d.]+ sec\.)$")


def tta_wav(path):
    """What a WAVE file `tta` wrote says about itself: format tag, channels, rate, bits, block align, where the data chunk's bytes begin, the size it declares and how many
    bytes were written for it (to the end of the file, or to the end of the declared size when a chunk follows it); None when the file is no WAVE file."""
    data = pathlib.Path(path).read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    pos, info = 12, {}
    while pos + 8 <= len(data):
        kind, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        if kind == b"fmt " and pos + 24 <= len(data):
            info["formatTag"], info["channels"], info["rate"], _, info["blockAlign"], info["bits"] = struct.unpack_from("<HHIIHH", data, pos + 8)
        elif kind == b"data":
            written = len(data) - pos - 8
            extra = max(0, written - size)
            # Bytes past the declared size are a chunk (an id of four ASCII letters, digits or spaces and a size that fits) or an overrun, which is a stop.
            end = pos + 8 + size
            chunk = extra >= 8 and all(c == 32 or 48 <= c <= 57 or 65 <= c <= 90 or 97 <= c <= 122 for c in data[end:end + 4]) and struct.unpack_from("<I", data, end + 4)[0] <= extra - 8
            info.update({"headerBytes": pos + 8, "declaredDataBytes": size, "writtenBytes": written, "trailingBytes": extra, "overrun": extra > 0 and not chunk})
            info["dataBytes"] = size if chunk else written
            return info
        pos += 8 + size + (size & 1)
    info.update({"headerBytes": None, "declaredDataBytes": None, "writtenBytes": 0, "dataBytes": 0, "trailingBytes": 0, "overrun": False})
    return info


def tta_result(program, path, tmp, password):
    """One run of `tta -d` (or `-dp password`): the samples counted from the PCM bytes in the WAVE file it wrote, with its own channel count and block align. Fewer data
    bytes than its WAVE header declares is a stop (the tool exits 0 and says nothing); the declared size is a check of completeness, never the count."""
    target = pathlib.Path(tmp) / ("out-" + pathlib.Path(path).name + ".wav")
    if target.exists():
        target.unlink()
    arguments = [program, "-dp", password] if password else [program, "-d"]
    child = probe.run_child("tta -dp" if password else "tta -d", [*arguments, str(path), str(target)], jsonl=False)
    text = (child["stderr"] + child.get("stdout", "")).replace("\r", "\n").replace(str(path), pathlib.Path(path).name).replace(str(target), "OUTPUT").replace(str(program), "tta")
    lines = [l.strip() for l in text.splitlines() if l.strip()]
    warnings = [l for l in lines if not TTA_CHATTER.match(l)]
    left = target.exists()
    info = tta_wav(target) if left and target.stat().st_size else None
    has_header = info is not None and info["headerBytes"] is not None
    container = (info["bits"] + 7) // 8 if has_header and info.get("bits") else None
    # The frame is the WAVE header's block align, valid only when it is channels x the bytes of one container; 0 channels leaves no frame to count by.
    frame = info["blockAlign"] if has_header and container and info.get("channels") and info["blockAlign"] == info["channels"] * container else None
    no_channels = has_header and info.get("channels") == 0
    short = has_header and info["declaredDataBytes"] is not None and info["dataBytes"] < info["declaredDataBytes"]
    stopped = "end"
    reason = None
    overrun = has_header and info["overrun"]
    if child["timedOut"]:
        stopped = "timeout"
    elif child["signal"]:
        stopped = "crash"
    elif child["exit"] != 0 or not left or not has_header:
        stopped = "error"
    elif short:
        stopped = "error"
        reason = "wrote %d of the %d data bytes its WAVE header declares" % (info["dataBytes"], info["declaredDataBytes"])
    elif overrun:
        stopped = "error"
        reason = "wrote %d data bytes, more than the %d its WAVE header declares" % (info["writtenBytes"], info["declaredDataBytes"])
    elif not frame and not no_channels:
        stopped = "error"
        reason = "the WAVE header's block align %s does not fit %s channels of %s bits" % (info.get("blockAlign"), info.get("channels"), info.get("bits"))
    result = {
        "tool": "tta -dp" if password else "tta -d",
        "decoder": {"libtta-c": "2.3"},
        "wroteWaveHeader": has_header,
        "formats": [{"rate": info["rate"], "channels": info["channels"], "bits": info["bits"], "formatTag": info["formatTag"], "blockAlign": info["blockAlign"]}] if has_header and "formatTag" in info else [],
        "runs": [],
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "timedOut": child["timedOut"], "elapsedMs": child["elapsedMs"],
        "outputFileLeft": left,
        "warnings": warnings[:40],
        "stderrLines": len(lines), "droppedLines": len(lines) - len(warnings), "stderrTail": "\n".join(lines)[-600:],
    }
    if reason:
        result["stopReason"] = reason
    notes = []
    if has_header and (frame or no_channels):
        run = {"rate": info["rate"], "channels": info["channels"], "bits": info["bits"], "headerBytes": info["headerBytes"], "writtenBytes": info["writtenBytes"], "outputBytes": info["dataBytes"],
               "declaredDataBytes": info["declaredDataBytes"]}
        if frame:
            run.update({"samples": info["dataBytes"] // frame, "remainder": info["dataBytes"] % frame})
            result["runs"] = [run]
        else:
            result["runs"] = [run]
            result["noSampleCount"] = "0 channels: the bytes written give no sample count"
            notes.append("the WAVE header states 0 channels and %d data bytes follow: any sample count writes none, so the bytes give no count" % info["dataBytes"])
    if stopped == "end" and result["runs"]:
        run = result["runs"][0]
        if run.get("remainder"):
            notes.append("%d bytes after the last whole sample" % run["remainder"])
        if info["trailingBytes"]:
            notes.append("%d bytes of another chunk follow the data chunk's declared size and are not counted" % info["trailingBytes"])
    elif child["signal"]:
        notes.append("killed by %s; no reference" % child["signal"])
    elif child["exit"] not in (0, None):
        notes.append("exit status %d%s; no reference" % (child["exit"], "" if left else " and no output file"))
    elif reason:
        notes.append(reason + "; exit 0 and no message; the count is partial and no reference")
    if notes:
        result["notes"] = notes
    samples = result["runs"][0].get("samples") if result["runs"] else None
    if samples is not None:
        result["totalSamples" if stopped == "end" else "partialSamples"] = samples
    if left:
        target.unlink()
    return result


def run_tta(args):
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "records").mkdir(exist_ok=True)
    records = Records(out)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="odd-"))
    started = time.time()
    env = {"platform": "linux", "python": sys.version, "kernel": platform.platform(), "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"),
           "runId": os.environ.get("GITHUB_RUN_ID"), "commit": os.environ.get("GITHUB_SHA"), "format": "tta", "gcc": probe.tool_line(["gcc", "--version"]),
           "tta": next((l.strip() for l in probe.run_child("tta -h", [args.tta, "-h"], jsonl=False)["stderr"].replace("\r", "\n").splitlines() if "version" in l), None),
           "ttaArchiveSha256": args.archive_sha256}
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    manifest = derive_variants.write_tta(tmp / "variants")
    (out / "variants.json").write_text(json.dumps({"format": "tta", "rules": list(derive_variants.TTA_RULES), "variants": manifest}, indent=1) + "\n", encoding="utf-8")
    committed = sorted(p for p in (FIXTURES / "tta").iterdir() if p.is_file() and p.suffix == ".tta")
    work = [(p, None) for p in committed] + [(tmp / "variants" / m["name"], {"from": m["source"], "fromSha256": m["sourceSha256"], "rule": m["rule"]}) for m in manifest]

    def case(item):
        path, derived = item
        record = file_record(path, "tta", derived)
        if path.name in TTA_PASSWORD_FILES or (derived and pathlib.Path(derived["from"]).name in TTA_PASSWORD_FILES):
            record["paths"] = {"reader": tta_result(args.tta, path, tmp, TTA_PASSWORD), "readerWithoutPassword": tta_result(args.tta, path, tmp, None)}
        else:
            record["paths"] = {"reader": tta_result(args.tta, path, tmp, None)}
        records.write("tta", path, record)

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for future in [pool.submit(case, item) for item in work]:
            future.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    env["records"] = records.count
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


# ---- the AAC run -----------------------------------------------------------------------------------------------------

AAC_EXTENSIONS = probe.AAC_EXTENSIONS | {".adts", ".mov"}
AAC_FOLDERS = ("aac", "adts", "apple", "mp4")
NEEDS_MORE_BITS = "0x1002"


SAMPLE_ENTRIES = {
    "enca": "encrypted audio (an enca sample entry, a key the project does not have): no reference",
    "aavd": "Audible's aavd sample entry (encrypted): no reference",
    "drms": "a drms sample entry (DRM-protected): no reference",
    "alac": "Apple Lossless (an alac sample entry), not AAC",
}


def aac_skip_reason(summary):
    """Why a file is not fed to the decoder, or None. The extractor's own note says it for most of them."""
    note = summary.get("note") or ""
    if note.startswith("sample entry ") and note[len("sample entry "):] in SAMPLE_ENTRIES:
        return SAMPLE_ENTRIES[note[len("sample entry "):]]
    if summary["encrypted"]:
        return "encrypted track (a key the project does not have): no reference"
    if summary["kind"] == "unknown":
        return summary.get("note") or "the extractor finds neither ADTS, ADIF nor an MP4 sound track"
    if summary["codec"] not in ("aac", None) or (summary["kind"] in ("mp4", "ac3", "eac3") and summary["codec"] != "aac"):
        return summary.get("note") or "the sound track is %s, not AAC" % summary["codec"]
    if not summary["units"] and summary["kind"] != "adif":
        return summary.get("note") or "the extractor finds no unit"
    return None


def fdk_result(child, summary, mode, version, path):
    """The record of one run of fdk_decode: its units (the extractor's, with the decoder's result), totals per run of constant output format, the drain."""
    lines = child["lines"]
    end = next((l for l in lines if l.get("type") == "end"), None)
    env = next((l for l in lines if l.get("type") == "env"), {})
    drain = next((l for l in lines if l.get("type") == "drain"), None)
    calls = [l for l in lines if l.get("type") == "unit"]
    flushes = [l for l in lines if l.get("type") == "flush"]
    conceal = [l for l in lines if l.get("type") == "conceal"]
    stuck = [l for l in lines if l.get("type") == "stuck"]
    held = next((l for l in lines if l.get("type") == "held"), None)
    # The units of the list, from the first line of each fill. An ADTS output is placed at the unit that holds the last byte the library had used when it gave the output
    # (numTotalBytes): a frame of several raw data blocks gives its outputs over several calls, and the first of them can come with the fill of the next frame.
    table = {}
    for l in calls:
        table.setdefault(l["index"], (l["offset"], l["received"], l["declared"], l["fedFrom"]))
    adts = env.get("kind") == "adts"
    data = pathlib.Path(path).read_bytes() if adts else b""
    spans = sorted((offset, offset + received, index) for index, (offset, received, _, _) in table.items())

    def placed(l):
        if not adts or l["totalBytes"] <= 0 or (not l["outputValid"] and l["error"] in ("0x0000", NEEDS_MORE_BITS)):
            return l["index"]
        last = l["totalBytes"] - 1
        for start, stop, index in spans:
            if start <= last < stop:
                return index
        return l["index"]

    by_unit = {index: [] for index in table}
    for l in calls:
        by_unit[placed(l)].append(l)
    units, runs = [], []
    codes = {}
    for index in sorted(by_unit):
        group = by_unit[index]
        offset, received, declared, fed_from = table[index]
        first = group[0] if group else {"fill": "0x0000"}
        outputs = [l for l in group if l["outputValid"]]
        errors = sorted({l["error"] for l in group if l["error"] not in ("0x0000", NEEDS_MORE_BITS)})
        fills = sorted({l["fill"] for l in group if l["fill"] != "0x0000"})
        concealed_codes = sorted({l["error"] for l in outputs if l["error"] != "0x0000"})
        if outputs:
            result = "concealed" if concealed_codes else "decoded"
        elif errors:
            result = "refused"
        else:
            result = "no output"
        samples = sum(l["samples"] for l in outputs)
        unit = {"index": index, "offset": offset, "declared": declared, "received": received, "fedFrom": fed_from, "result": result,
                "samples": samples, "usedBytes": group[-1]["totalBytes"] if group else None, "calls": len(group)}
        if adts and index < summary["units"] and offset + 6 < len(data) and data[offset] == 0xFF and data[offset + 1] & 0xF0 == 0xF0:
            unit["blocks"] = (data[offset + 6] & 3) + 1  # number_of_raw_data_blocks_in_frame + 1 of the frame's header (frames of the unit list only)
        if outputs:
            unit["rate"] = outputs[0]["sampleRate"]
        if errors:
            unit["error"] = errors[0] if len(errors) == 1 else errors
        if fills:
            unit["fill"] = fills[0] if len(fills) == 1 else fills
        if len(outputs) > 1:
            unit["outputs"] = len(outputs)
        units.append(unit)
        for l in group:
            codes[l["error"]] = codes.get(l["error"], 0) + 1
        for l in outputs:
            key = (l["sampleRate"], l["channels"])
            if runs and runs[-1]["_key"] == key:
                runs[-1]["samples"] += l["samples"]
                runs[-1]["outputs"] += 1
            else:
                runs.append({"_key": key, "rate": l["sampleRate"], "channels": l["channels"], "samples": l["samples"], "outputs": 1, "firstUnit": index,
                             "frameSize": l["frameSize"], "aot": l["aot"], "extAot": l["extAot"]})
            if l["error"] != "0x0000":
                runs[-1]["concealedSamples"] = runs[-1].get("concealedSamples", 0) + l["samples"]
    for run in runs:
        del run["_key"]
    # Bytes fed that the library has not used when it answers 0x1002 with a whole frame fed last: it keeps the last raw data blocks of a frame of several until more bits come,
    # and the drain discards them, so the total is short by those blocks.
    # The last frame of the unit list, whole, with several raw data blocks: the library keeps the last blocks until more bits come and the drain discards them. The blocks held are
    # those the frame declares less those it output (a block it refused is not held).
    listed = [u for u in units if u["index"] < summary["units"]]
    last_unit = listed[-1] if listed else None
    held_blocks = 0
    if last_unit is not None and last_unit.get("blocks", 1) > 1 and last_unit["received"] == last_unit["declared"] and runs and runs[-1]["frameSize"]:
        held_blocks = max(0, last_unit["blocks"] - last_unit["samples"] // runs[-1]["frameSize"])
        if last_unit["result"] != "decoded" and last_unit["samples"] == 0:
            held_blocks = 0  # a frame the library refused as a whole is no held frame
    held_back = bool(held) and held_blocks > 0 and held["heldBytes"] > 0 and env.get("kind") in ("adts", "adif")
    stopped = stopped_by(child, end)
    notes = []
    incomplete = child.get("linesElided") or child.get("lastLineTruncated") or any(l.get("type") == "unparsed" for l in lines)
    if incomplete and stopped == "end":
        stopped = "limit"
        notes.append("the output was cut (lines elided, truncated or not parseable); the figures are partial")
    total = end["codedSamples"] if end and not incomplete else sum(r["samples"] for r in runs)
    if end and not incomplete and total != sum(r["samples"] for r in runs):
        stopped = "limit"
        notes.append("the harness counted %d samples and the lines add up to %d" % (total, sum(r["samples"] for r in runs)))
    if stuck:
        notes.append("no progress in numTotalBytes for eight calls in a row at unit %d" % stuck[0]["index"])
    configured = env.get("configRaw")
    result = {
        "tool": "fdk_decode " + mode,
        "decoder": {"fdk-aac": version, "module": env.get("fdkVersion")},
        "opened": bool(env) and configured in (None, "0x0000"),
        "transport": {"adts": "TT_MP4_ADTS", "adif": "TT_MP4_ADIF", "mp4": "TT_MP4_RAW"}.get(env.get("kind"), env.get("kind")),
        "concealMethod": env.get("concealMethod"),
        "concealStatus": {"beforeConfiguration": env.get("setConceal"), "afterConfiguration": (conceal[0]["status"] if conceal else env.get("setConcealAfterConfig"))},
        "runs": runs,
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "elapsedMs": child["elapsedMs"],
        "unitCount": len(units),
        "units": units if len(units) <= AAC_UNIT_LIMIT else units[:AAC_UNIT_LIMIT],
        "errorCodes": dict(sorted(codes.items())),
        "heldBytes": held["heldBytes"] if held and env.get("kind") in ("adts", "adif") else None,
        "heldBlocks": held_blocks,
        "heldBack": held_back,
        "drain": {"outputDelay": drain["outputDelay"], "frameSize": drain["frameSize"], "requests": drain["requests"], "flushedSamples": end.get("flushedSamples") if end else None,
                  "results": [f["error"] for f in flushes]} if drain else None,
        "flushedSamples": end.get("flushedSamples") if end else None,
        "warnings": [l for l in child["stderr"].splitlines() if l.strip()],
    }
    if held_back:
        notes.append("the last frame has %d raw data blocks and the library output %d of them; it holds the rest when it answers 0x1002 (%d bytes fed and not used) and the drain discards them" % (
            last_unit["blocks"], last_unit["blocks"] - held_blocks, held["heldBytes"]))
    if configured is not None:
        result["configRaw"] = {"status": configured, "bytes": env.get("configBytes")}
    if len(units) > AAC_UNIT_LIMIT:
        result["unitsElided"] = True
        notes.append("units elided above %d" % AAC_UNIT_LIMIT)
    if runs:
        result["object"] = {k: runs[0][k] for k in ("aot", "extAot", "frameSize")}
    result["totalSamples" if stopped == "end" else "partialSamples"] = total
    if notes:
        result["notes"] = notes
    return result


def run_aac(args):
    probe.LINE_LIMIT = 4 * AAC_UNIT_LIMIT
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "records").mkdir(exist_ok=True)
    records = Records(out)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="odd-"))
    started = time.time()
    fdk_env = {"LD_LIBRARY_PATH": args.fdk_lib}
    version = "%s %s" % (args.fdk_version or "unknown", (args.fdk_commit or "")[:7]) if args.fdk_commit else (args.fdk_version or "unknown")
    env = {"platform": "linux", "python": sys.version, "kernel": platform.platform(), "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"),
           "runId": os.environ.get("GITHUB_RUN_ID"), "commit": os.environ.get("GITHUB_SHA"), "format": "aac", "gcc": probe.tool_line(["gcc", "--version"]),
           "fdkAac": {"version": args.fdk_version, "commit": args.fdk_commit}, "concealMethod": 1}
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    manifest = derive_variants.write_aac(tmp / "variants")
    (out / "variants.json").write_text(json.dumps({"format": "aac", "rules": list(derive_variants.AAC_RULES), "variants": manifest}, indent=1) + "\n", encoding="utf-8")
    committed = [p for folder in AAC_FOLDERS for p in probe.files(folder, AAC_EXTENSIONS)]
    downloaded = sorted(p for p in pathlib.Path(args.downloaded).iterdir() if p.is_file() and p.suffix.lower() in AAC_EXTENSIONS) if args.downloaded else []
    work = [(p, None) for p in committed] + [(tmp / "variants" / m["name"], {"from": m["source"], "fromSha256": m["sourceSha256"], "rule": m["rule"]}) for m in manifest] \
        + [(p, {"downloaded": True}) for p in downloaded]

    def case(item):
        path, derived = item
        record = file_record(path, "aac", derived)
        record["folder"] = "derived" if derived and "rule" in derived else "downloaded" if derived else path.parent.name
        tsv, summary = probe.unit_list(path, tmp)
        record["unitList"] = summary
        reason = aac_skip_reason(summary)
        if reason:
            record["notFed"] = reason
            record["paths"] = {}
            records.write("aac", path, record)
            return
        mode = "adif" if summary["kind"] == "adif" else "units"
        child = probe.run_child("direct", [args.fdk_decode, mode, path, tsv, "1"], env=fdk_env)
        record["paths"] = {"direct": fdk_result(child, summary, mode, version, path)}
        records.write("aac", path, record)

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for future in [pool.submit(case, item) for item in work]:
            future.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    env["records"] = records.count
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


# ---- Core Audio (macOS) ------------------------------------------------------------------------------------------------------

# The undamaged files whose derived variants macOS runs (the design's subset of at most 20 per format: one per encoder and object type); the other variants run on Linux only.
MAC_VARIANT_SOURCES = (
    "apple-lc-adts-cbr-st44.aac", "apple-lc-m4a-cbr-st44.m4a", "apple-lc-m4a-cbr-mono44.m4a", "apple-he-m4a-cbr-st44.m4a", "apple-hev2-m4a-cbr-st44.m4a",
    "faac-1.40-adts.aac", "faac-2.2.m4a", "faac-2.2-he-adts.aac", "faac-2.2-he.m4a",
    "fdkaac-lc.m4a", "fdkaac-he.m4a", "fdkaac-he-v2.m4a", "fdkaac-he-adts.aac", "fdkaac-crc-mono.aac",
    "ffmpeg-adts.aac", "ffmpeg-lc.m4a", "ffmpeg-mono-faststart.m4a", "mp4box-mux.m4a", "exhale.m4a",
)
AC3_FOLDERS = ("ac3",)


def whole_rate(rate):
    return int(rate) if isinstance(rate, float) and rate == int(rate) else rate


def os_status(status):
    """An OSStatus as the hex code the headers and the logs show (0 stays 0)."""
    return status if status in (0, None) else "0x%08X" % (status & 0xFFFFFFFF)


def coreaudio_format(line):
    f = line.get("format") or {}
    return {"formatID": f.get("formatID"), "sampleRate": whole_rate(f.get("sampleRate")), "channels": f.get("channels"), "framesPerPacket": f.get("framesPerPacket")}


def coreaudio_reader_result(child):
    """The record of one run of `audiotoolbox_probe reader`: ExtAudioFile opens the file, the client format is the file's rate with 16-bit samples, and it reads to the end."""
    lines = child["lines"]
    by_type = {}
    for l in lines:
        by_type.setdefault(l.get("type"), []).append(l)
    first = lambda kind: (by_type.get(kind) or [None])[0]  # noqa: E731
    end = first("end")
    opened = first("open")
    file_format, client, packets, table = first("fileFormat"), first("clientFormat"), first("packetCount"), first("packetTableInfo")
    reads = by_type.get("read", [])
    total = sum(r["frames"] for r in reads)
    failed = [r for r in reads if r["status"] != 0]
    incomplete = child.get("linesElided") or child.get("lastLineTruncated") or any(l.get("type") == "unparsed" for l in lines)
    if child["timedOut"]:
        stopped = "timeout"
    elif child["signal"]:
        stopped = "crash"
    elif incomplete:
        stopped = "limit"
    elif end is None or child["exit"] != 0:
        stopped = "error"
    else:
        stopped = end["stoppedBy"]
    ok = bool(opened) and opened["status"] == 0 and bool(client) and client["status"] == 0
    result = {
        "tool": "audiotoolbox_probe reader",
        "opened": bool(opened) and opened["status"] == 0,
        "openStatus": os_status(opened["status"]) if opened else None,
        "fileFormat": coreaudio_format(file_format) if file_format and file_format["status"] == 0 else None,
        "fileLengthFrames": (first("fileLengthFrames") or {}).get("frames"),
        "packetTable": {"validFrames": table["validFrames"], "primingFrames": table["primingFrames"], "remainderFrames": table["remainderFrames"]} if table and table["status"] == 0 else None,
        "packetCount": packets["packets"] if packets and packets["status"] == 0 else None,
        "clientFormat": {**coreaudio_format(client), "status": os_status(client["status"])} if client else None,
        "runs": [{"rate": whole_rate(client["format"]["sampleRate"]), "channels": client["format"]["channels"], "samples": total, "outputs": len(reads)}] if ok and total else [],
        "readCount": len(reads),
        "failedReads": [{"index": r["index"], "status": os_status(r["status"]), "frames": r["frames"]} for r in failed][:8],
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "elapsedMs": child["elapsedMs"],
        "warnings": [l for l in child["stderr"].splitlines() if l.strip()][:8],
    }
    if stopped == "end" and not ok:
        result["stoppedBy"] = stopped = "error"
    result["totalSamples" if stopped == "end" else "partialSamples"] = total
    return result


def coreaudio_converter_result(child):
    """The record of one run of `audiotoolbox_probe convert`: AudioConverter fed one unit of the unit list per call (a cut unit from its first byte to the last byte the file
    holds, never padded), then the end of the stream. A unit is `refused` when the call returns an error other than 'nomo' (the input has no further unit), `decoded` when
    the call returns frames and `no output` otherwise; the converter holds frames back, so the frames of a call need not be the frames of its unit (the AC-3 converter
    outputs 1,248 frames for its first 1,536)."""
    lines = child["lines"]
    by_type = {}
    for l in lines:
        by_type.setdefault(l.get("type"), []).append(l)
    first = lambda kind: (by_type.get(kind) or [None])[0]  # noqa: E731
    setup, made, end = first("setup"), first("converter"), first("end")
    calls = by_type.get("unit", [])
    drains = by_type.get("drain", [])
    units, decoded, nothing = [], 0, 0
    not_fed = 0
    for l in calls:
        if l.get("notFed"):
            result = "not fed"
            not_fed += 1
        elif l["status"] not in (0,) and not l["noMoreData"]:
            result = "refused"
        elif l["frames"] > 0:
            result = "decoded"
            decoded += 1
        else:
            result = "no output"
            nothing += 1
        if result != "decoded":
            units.append({"index": l["index"], "offset": l["offset"], "declared": l["declared"], "received": l["received"], "result": result, "frames": l["frames"],
                          **({"status": os_status(l["status"])} if l["status"] not in (0,) and not l["noMoreData"] else {}), "consumed": l["consumed"]})
    frames_units = sum(l["frames"] for l in calls)
    frames_drain = sum(l["frames"] for l in drains)
    total = frames_units + frames_drain
    incomplete = child.get("linesElided") or child.get("lastLineTruncated") or any(l.get("type") == "unparsed" for l in lines)
    if child["timedOut"]:
        stopped = "timeout"
    elif child["signal"]:
        stopped = "crash"
    elif incomplete:
        stopped = "limit"
    elif end is None or child["exit"] != 0:
        stopped = "error"
    else:
        stopped = end["stoppedBy"]
    created = bool(made) and made["status"] == 0
    if stopped == "end" and not created:
        stopped = "error"
    output = made["output"] if made else None
    prime = [l for l in by_type.get("primeMethod", [])]
    prime_info = first("primeInfo")
    result = {
        "tool": "audiotoolbox_probe convert",
        "opened": created,
        "setup": {k: setup[k] for k in ("source", "audioFileOpen", "dataFormat", "formatListEntries", "firstPlayable", "firstPlayableIndex", "formatList", "formatInfo", "syncframe", "cookieBytes") if k in setup} if setup else None,
        "input": {k: whole_rate(setup["input"].get(k)) for k in ("formatID", "sampleRate", "channels", "framesPerPacket")} if setup and "input" in setup else None,
        "converterStatus": os_status(made["status"]) if made else None,
        "primeMethod": [{"when": p["when"], "status": os_status(p["status"]), "method": p.get("method")} for p in prime],
        "primeInfo": {"status": os_status(prime_info["status"]), "leadingFrames": prime_info["leadingFrames"], "trailingFrames": prime_info["trailingFrames"]} if prime_info else None,
        "runs": [{"rate": whole_rate(output["sampleRate"]), "channels": output["channels"], "samples": total, "outputs": decoded}] if created and output and total else [],
        "unitCount": len(calls),
        "decodedUnits": decoded, "noOutputUnits": nothing, "notFedUnits": not_fed, "refusedUnits": sum(1 for u in units if u["result"] == "refused"),
        "units": units,
        "unitFrames": frames_units, "drainFrames": frames_drain,
        "failedCalls": end.get("failedCalls") if end else None,
        "stoppedBy": stopped,
        "exit": child["exit"], "signal": child["signal"], "elapsedMs": child["elapsedMs"],
        "warnings": [l for l in child["stderr"].splitlines() if l.strip()][:8],
    }
    if end and not incomplete and created and end.get("codedFrames") != total:
        result["stoppedBy"] = stopped = "limit"
        result["notes"] = ["the harness counted %s frames and the lines add up to %s" % (end.get("codedFrames"), total)]
    result["totalSamples" if stopped == "end" else "partialSamples"] = total
    return result


def coreaudio_skip_reason(summary, ac3):
    """Why a file is not given to Core Audio, or None. AAC files follow the FDK run's rules; an AC-3 or E-AC-3 file needs units of that codec."""
    if ac3:
        if summary["encrypted"]:
            return "encrypted track (a key the project does not have): no reference"
        if summary["codec"] not in ("ac-3", "ec-3"):
            return summary.get("note") or "the extractor finds no AC-3 or E-AC-3 syncframe in a chain or an MP4 sound track of those codecs"
        if not summary["units"]:
            return summary.get("note") or "the extractor finds no unit"
        return None
    return aac_skip_reason(summary)


def run_coreaudio(args):
    probe.LINE_LIMIT = 4 * AAC_UNIT_LIMIT
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "records").mkdir(exist_ok=True)
    records = Records(out)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="odm-"))
    started = time.time()
    program = str(pathlib.Path(args.probe).resolve())
    env = {"platform": "macos", "format": "coreaudio", "machine": platform.machine(), "macOS": probe.tool_line(["sw_vers", "-productVersion"]), "build": probe.tool_line(["sw_vers", "-buildVersion"]),
           "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"), "runId": os.environ.get("GITHUB_RUN_ID"), "commit": os.environ.get("GITHUB_SHA"),
           "python": sys.version, "clang": probe.tool_line(["clang", "--version"]),
           "audioCodecs": probe.plist_version("/System/Library/Components/AudioCodecs.component/Contents/Info.plist"),
           "audioToolbox": probe.plist_version("/System/Library/Frameworks/AudioToolbox.framework/Resources/Info.plist"),
           "primeMethod": "default (kAudioConverterPrimeMethod can be neither read nor set for the AAC decoder, see the records)"}
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    manifest = [m for m in derive_variants.write_aac(tmp / "variants") if pathlib.Path(m["source"]).name in MAC_VARIANT_SOURCES]
    (out / "variants.json").write_text(json.dumps({"format": "aac", "rules": list(derive_variants.AAC_RULES), "sources": list(MAC_VARIANT_SOURCES), "variants": manifest}, indent=1) + "\n",
                                       encoding="utf-8")
    committed = [(p, False) for folder in AAC_FOLDERS for p in probe.files(folder, AAC_EXTENSIONS)] + [(p, True) for folder in AC3_FOLDERS for p in probe.files(folder, probe.AC3_EXTENSIONS)]
    downloaded = sorted(p for p in pathlib.Path(args.downloaded).iterdir() if p.is_file() and p.suffix.lower() in AAC_EXTENSIONS) if args.downloaded else []
    work = [(p, None, ac3) for p, ac3 in committed] + [(tmp / "variants" / m["name"], {"from": m["source"], "fromSha256": m["sourceSha256"], "rule": m["rule"]}, False) for m in manifest] \
        + [(p, {"downloaded": True}, False) for p in downloaded]

    def case(item):
        path, derived, ac3 = item
        format_ = "coreaudio-ac3" if ac3 else "coreaudio-aac"
        record = file_record(path, format_, derived)
        record["folder"] = "derived" if derived and "rule" in derived else "downloaded" if derived else path.parent.name
        tsv, summary = probe.unit_list(path, tmp)
        record["unitList"] = summary
        reason = coreaudio_skip_reason(summary, ac3)
        if reason:
            record["notFed"] = reason
            record["paths"] = {}
            records.write(format_, path, record)
            return
        paths = {"reader": coreaudio_reader_result(probe.run_child("reader", [program, "reader", str(path)]))}
        if summary["units"]:
            paths["converter"] = coreaudio_converter_result(probe.run_child("converter", [program, "convert", str(path), str(tsv), "default"]))
        record["paths"] = paths
        records.write(format_, path, record)

    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        for future in [pool.submit(case, item) for item in work]:
            future.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    env["records"] = records.count
    (out / "environment.json").write_text(json.dumps(env, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="format", required=True)
    ogg = sub.add_parser("ogg")
    ogg.add_argument("--out", required=True)
    ogg.add_argument("--bin", required=True)
    tta = sub.add_parser("tta")
    tta.add_argument("--out", required=True)
    tta.add_argument("--tta", required=True)
    tta.add_argument("--archive-sha256")
    aac = sub.add_parser("aac")
    aac.add_argument("--out", required=True)
    aac.add_argument("--fdk-decode", required=True)
    aac.add_argument("--fdk-lib", required=True)
    aac.add_argument("--fdk-commit")
    aac.add_argument("--fdk-version")
    aac.add_argument("--downloaded")
    mac = sub.add_parser("coreaudio")
    mac.add_argument("--out", required=True)
    mac.add_argument("--probe", required=True)
    mac.add_argument("--downloaded")
    args = parser.parse_args(argv)
    return {"ogg": run_ogg, "tta": run_tta, "aac": run_aac, "coreaudio": run_coreaudio}[args.format](args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
