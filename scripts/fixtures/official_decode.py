#!/usr/bin/env python3
"""The Linux driver of the official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, "Records and the comparison").

  official_decode.py ogg --out DIR --bin DIR     (DIR holds ogg_decode)

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
    streams, units, dropped, runs = {}, [], [], []
    holes = refused = 0
    for l in lines:
        kind = l.get("type")
        if kind == "stream":
            streams[l["stream"]] = {"stream": l["stream"], "serial": l["serial"], "offset": l["offset"], "codec": None, "lastGranule": None}
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
            if l["granulepos"] >= 0 and (s["lastGranule"] is None or l["granulepos"] > s["lastGranule"]):
                s["lastGranule"] = l["granulepos"]
            result = l.get("result")
            if result == "head":
                s["channels"], s["family"], s["preSkip"] = l["channels"], l["family"], l["preSkip"]
            elif result == "header":
                s.setdefault("headerPackets", []).append({"index": l["index"], "error": l["error"]})
            elif result == "tags":
                s["tagsIndex"] = l["index"]
            else:
                unit = {"index": l["index"], "stream": l["stream"], "ranges": l["ranges"], "pageOffset": l["pageOffset"], "bytes": l["bytes"], "result": result}
                for key in ("samples", "tocSamples", "error", "granulepos", "eos"):
                    if key in l and (key not in ("granulepos", "eos") or l[key] not in (-1, 0)):
                        unit[key] = l[key]
                if result == "decoded":
                    key = (l["rate"], l["channels"])
                    if not runs or runs[-1]["_key"] != key:
                        runs.append({"_key": key, "rate": key[0], "channels": key[1], "samples": 0, "units": 0, "streams": []})
                    runs[-1]["samples"] += l["samples"]
                    runs[-1]["units"] += 1
                    if l["stream"] not in runs[-1]["streams"]:
                        runs[-1]["streams"].append(l["stream"])
                    s["rate"], s["channels"] = l["rate"], l["channels"]
                units.append(unit)
    for run in runs:
        del run["_key"]
    stopped = stopped_by(child, end)
    total = sum(r["samples"] for r in runs)
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
        "skippedBytes": end["skippedBytes"] if end else None,
        "trailingBytes": end["trailingBytes"] if end else None,
        "pages": end["pages"] if end else None,
        "fedEveryPage": bool(end) and end["skippedBytes"] == 0 and end["trailingBytes"] == 0 and refused == 0,
        "warnings": [l for l in child["stderr"].splitlines() if l.strip()],
    }
    if len(units) > UNIT_LIMIT:
        result["unitsElided"] = True
    result["totalSamples" if stopped == "end" else "partialSamples"] = total
    if holes or dropped:
        result["notes"] = ["libogg dropped %d bytes in %d places and reported %d lost packets; the decoder never saw them" % (sum(d["bytes"] for d in dropped), len(dropped), holes)]
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
    env = {"gcc": probe.tool_line(["gcc", "--version"]), "oggdec": probe.tool_line(["oggdec", "--version"]), "opusdec": probe.tool_line(["opusdec", "--version"]),
           "ffmpeg": probe.tool_line(["ffmpeg", "-version"]), "dpkg": {}}
    for package in ("libogg0", "libogg-dev", "libopus0", "libopus-dev", "libopusfile0", "libvorbis0a", "libvorbisfile3", "libvorbis-dev", "opus-tools", "vorbis-tools", "ffmpeg"):
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


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="format", required=True)
    ogg = sub.add_parser("ogg")
    ogg.add_argument("--out", required=True)
    ogg.add_argument("--bin", required=True)
    args = parser.parse_args(argv)
    return run_ogg(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
