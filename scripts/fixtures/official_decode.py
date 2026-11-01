#!/usr/bin/env python3
"""The Linux driver of the official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, "Records and the comparison").

  official_decode.py ogg --out DIR --bin DIR     (DIR holds ogg_decode)
  official_decode.py tta --out DIR --tta PROGRAM (PROGRAM is libtta-c's `tta`)

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
    args = parser.parse_args(argv)
    return run_ogg(args) if args.format == "ogg" else run_tta(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
