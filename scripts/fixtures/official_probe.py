#!/usr/bin/env python3
"""The Linux and macOS driver of the official-decoder probe (docs/design/official-decoder-crosschecks.md, rollout step 1).

  official_probe.py linux  --out DIR --bin DIR --fdk NAME=LIBDIR... [--tta PROGRAM]
  (DIR holds ogg_decode and one fdk_decode-NAME per FDK version)
  official_probe.py macos  --out DIR --probe PROGRAM

Every decoder call is a child process of its own with a timeout, so that a crash or a stall is recorded and the run goes on.
The C harnesses write one JSON line per unit and flush it, so a killed child leaves what it did; this script keeps those lines,
the exit status or signal, the standard error and the elapsed time in one record per file and case:

  OUT/records/<case>/<file>.json    {case, file, path, bytes, sha256, runs: [{label, command, exit, signal, timedOut, elapsedMs, lines, stderr}]}
  OUT/environment.json              versions of everything that ran
  OUT/log.txt                       one line per record

Records hold no media: derived variants and decoded output are written to a temporary folder and deleted.
"""

import argparse
import concurrent.futures
import hashlib
import json
import os
import pathlib
import platform
import re
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time

HERE = pathlib.Path(__file__).resolve().parent
FIXTURES = HERE.parent.parent / "tests" / "fixtures"
TIMEOUT = 30
STDERR_LIMIT = 4000
LINE_LIMIT = 20000
LOG_LOCK = threading.Lock()

AAC_EXTENSIONS = {".aac", ".m4a", ".mp4", ".m4b", ".3gp"}
AC3_EXTENSIONS = {".ac3", ".eac3", ".mp4"}


def sha256(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def parse_lines(text):
    lines, truncated = [], False
    for raw in text.splitlines():
        try:
            lines.append(json.loads(raw))
        except json.JSONDecodeError:
            truncated = True
            lines.append({"type": "unparsed", "text": raw[:300]})
    return lines, truncated


def run_child(label, command, env=None, timeout=TIMEOUT, jsonl=True, stdin=None):
    """One child process: the exit status or the signal, a time-out, the elapsed time, standard output as JSON lines and standard error."""
    started = time.monotonic()
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, stdin=stdin, env={**os.environ, **(env or {})}, start_new_session=True)
    timed_out = False
    try:
        out, err = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        out, err = process.communicate()
    code = process.returncode
    record = {
        "label": label,
        "command": [str(c) for c in command],
        "exit": code if code >= 0 else None,
        "signal": signal.Signals(-code).name if code < 0 and not timed_out else None,
        "timedOut": timed_out,
        "elapsedMs": round((time.monotonic() - started) * 1000),
        "stderr": err.decode("utf-8", "replace")[-STDERR_LIMIT:],
    }
    text = out.decode("utf-8", "replace")
    if jsonl:
        lines, truncated = parse_lines(text)
        record["lineCount"] = len(lines)
        record["lines"] = lines if len(lines) <= LINE_LIMIT else lines[:LINE_LIMIT // 2] + lines[-LINE_LIMIT // 2:]
        record["linesElided"] = len(lines) > LINE_LIMIT
        record["lastLineTruncated"] = truncated
    else:
        record["stdout"] = text[-STDERR_LIMIT:]
    return record


class Output:
    def __init__(self, directory):
        self.directory = pathlib.Path(directory)
        (self.directory / "records").mkdir(parents=True, exist_ok=True)
        self.log = open(self.directory / "log.txt", "a", encoding="utf-8")

    def record(self, case, path, runs, **extra):
        path = pathlib.Path(path)
        data = path.read_bytes()
        name = path.name
        record = {"case": case, "file": name, "path": relative(path), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), **extra, "runs": runs}
        folder = self.directory / "records" / case
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / (name + ".json")
        if target.exists():
            target = folder / (name + "-" + record["sha256"][:8] + ".json")
        target.write_text(json.dumps(record) + "\n", encoding="utf-8")
        with LOG_LOCK:
            self.log.write("%s %s %s\n" % (case, name, " ".join("%s:%s" % (r["label"], "timeout" if r["timedOut"] else r["signal"] or r["exit"]) for r in runs)))
            self.log.flush()

    def environment(self, values):
        (self.directory / "environment.json").write_text(json.dumps(values, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def relative(path):
    try:
        return str(pathlib.Path(path).resolve().relative_to(FIXTURES.parent.parent)).replace("\\", "/")
    except ValueError:
        return str(path)


def files(folder, extensions):
    return sorted(p for p in (FIXTURES / folder).iterdir() if p.is_file() and p.suffix.lower() in extensions)


def tool_line(command):
    try:
        result = subprocess.run(command, capture_output=True, text=True, errors="replace", timeout=20)
        return (result.stdout + result.stderr).strip().splitlines()[0] if (result.stdout + result.stderr).strip() else ""
    except (OSError, subprocess.TimeoutExpired) as error:
        return "unavailable: %s" % error


def unit_list(path, directory):
    """Writes the unit list of path (unit_list.py) and returns (tsv path, parsed summary)."""
    sys.path.insert(0, str(HERE))
    import unit_list as extractor
    result = extractor.extract(str(path))
    tsv = pathlib.Path(directory) / (pathlib.Path(path).name + ".units.tsv")
    tsv.write_text(extractor.tsv(result), encoding="utf-8")
    cut = sum(1 for u in result["units"] if u[2] != u[1])
    return tsv, {"kind": result["kind"], "codec": result.get("codec"), "units": len(result["units"]), "cutUnits": cut, "skippedRanges": len(result["skipped"]),
                 "skippedBytes": sum(s[1] for s in result["skipped"]), "config": result.get("config"), "timescale": result.get("timescale"), "note": result.get("note")}


# ---- derived variants of Ogg files (no randomness) ---------------------------------------------------------------------------

def ogg_pages(data):
    pages, pos = [], 0
    while pos + 27 <= len(data) and data[pos:pos + 4] == b"OggS":
        segments = data[pos + 26]
        if pos + 27 + segments > len(data):
            break
        size = 27 + segments + sum(data[pos + 27:pos + 27 + segments])
        if pos + size > len(data):
            break
        pages.append((pos, size))
        pos += size
    return pages


def ogg_variants(path, directory):
    data = pathlib.Path(path).read_bytes()
    pages = ogg_pages(data)
    out = {}
    if len(pages) < 4:
        return out
    middle = pages[len(pages) - 2]  # the last page but one, so that audio packets come before and after
    out["cut-mid-page"] = data[:middle[0] + middle[1] // 2]
    flipped = bytearray(data)
    flipped[middle[0] + middle[1] - 1] ^= 0x55
    out["flip-byte"] = bytes(flipped)
    out["drop-page"] = data[:middle[0]] + data[middle[0] + middle[1]:]
    result = {}
    stem = pathlib.Path(path).stem
    suffix = pathlib.Path(path).suffix
    for name, content in out.items():
        target = pathlib.Path(directory) / ("%s.%s%s" % (stem, name, suffix))
        target.write_bytes(content)
        result[name] = target
    return result


# ---- Linux -----------------------------------------------------------------------------------------------------------

def wav_data_bytes(path):
    """(header bytes, data bytes written, data size the header declares, channels, bits) of a WAVE file as a decoder wrote it."""
    data = pathlib.Path(path).read_bytes()
    if data[:4] != b"RIFF":
        return None
    pos, channels, bits, declared = 12, None, None, None
    while pos + 8 <= len(data):
        kind, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        if kind == b"fmt ":
            channels, bits = struct.unpack_from("<H", data, pos + 10)[0], struct.unpack_from("<H", data, pos + 22)[0]
        elif kind == b"data":
            return {"headerBytes": pos + 8, "dataBytes": len(data) - pos - 8, "declaredDataBytes": size, "channels": channels, "bits": bits}
        pos += 8 + size + (size & 1)
    return {"headerBytes": None, "dataBytes": 0, "channels": channels, "bits": bits}


def stream_channels(path):
    data = pathlib.Path(path).read_bytes()[:65536]
    i = data.find(b"OpusHead")
    if i >= 0 and i + 10 <= len(data):
        return "opus", data[i + 9]
    i = data.find(b"\x01vorbis")
    if i >= 0 and i + 12 <= len(data):
        return "vorbis", data[i + 11]
    return None, None


def linux_reader_runs(path, tmp, tta):
    """The reader paths of one file: oggdec or opusdec for Ogg, tta -d for TTA. Output goes to a temporary raw file and only its size is kept."""
    runs = []
    suffix = path.suffix.lower()
    target = pathlib.Path(tmp) / ("out-" + path.name + ".raw")
    if suffix == ".ogg" or suffix == ".opus":
        codec, channels = stream_channels(path)
        if codec == "vorbis":
            run = run_child("oggdec", ["oggdec", "-R", "-b", "16", "-e", "0", "-s", "1", "-o", str(target), str(path)], jsonl=False)
            bytes_per_sample = 2
        elif codec == "opus":
            run = run_child("opusdec", ["opusdec", "--rate", "48000", str(path), str(target)], jsonl=False)
            bytes_per_sample = 2
        else:
            return runs
        written = target.stat().st_size if target.exists() else None
        run["outputBytes"] = written
        run["channels"] = channels
        run["samples"] = written // (bytes_per_sample * channels) if written is not None and channels else None
        run["remainder"] = written % (bytes_per_sample * channels) if written is not None and channels else None
        runs.append(run)
    elif suffix == ".tta" and tta:
        target = pathlib.Path(tmp) / ("out-" + path.name + ".wav")
        for label, arguments in (("tta", [tta, "-d"]), ("tta-password", [tta, "-dp", "secret"])):
            if label == "tta-password" and "password" not in path.name:
                continue
            if target.exists():
                target.unlink()
            run = run_child(label, [*arguments, str(path), str(target)], jsonl=False)
            run["wav"] = wav_data_bytes(target) if target.exists() else None
            if run["wav"] and run["wav"].get("channels") and run["wav"].get("bits"):
                frame = run["wav"]["channels"] * run["wav"]["bits"] // 8
                run["samples"] = run["wav"]["dataBytes"] // frame
                run["samplesFromHeader"] = run["wav"]["declaredDataBytes"] // frame if run["wav"].get("declaredDataBytes") is not None else None
            runs.append(run)
    if target.exists():
        target.unlink()
    return runs


def run_linux(args):
    out = Output(args.out)
    bins = pathlib.Path(args.bin)
    fdk = dict(item.split("=", 1) for item in args.fdk)
    tmp = tempfile.mkdtemp(prefix="odp-")
    started = time.time()
    env = {
        "platform": "linux",
        "python": sys.version,
        "kernel": platform.platform(),
        "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"),
        "gcc": tool_line(["gcc", "--version"]),
        "ffmpeg": tool_line(["ffmpeg", "-version"]), "ffprobe": tool_line(["ffprobe", "-version"]),
        "oggdec": tool_line(["oggdec", "--version"]), "opusdec": tool_line(["opusdec", "--version"]),
        "tta": tool_line([args.tta, "-h"]) if args.tta else None,
        "fdk": args.fdk,
        "dpkg": {},
    }
    for package in ("libogg0", "libogg-dev", "libopus0", "libopus-dev", "libopusfile0", "libvorbis0a", "libvorbisfile3", "libvorbis-dev", "opus-tools", "vorbis-tools", "ffmpeg"):
        env["dpkg"][package] = tool_line(["dpkg-query", "-W", "-f=${Version}", package])
    out.environment(env)
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=4)
    futures = []

    # AAC with FDK: every committed AAC file by every version (open question 6), the unit list made once per file.
    aac = [p for folder in ("aac", "adts", "apple", "mp4") for p in files(folder, AAC_EXTENSIONS | {".adts"})]
    lists = {}
    for p in aac + files("ac3", AC3_EXTENSIONS):
        lists[p] = unit_list(p, tmp)

    def fdk_case(case, version, path, mode, method):
        tsv, summary = lists[path]
        env_ = {"LD_LIBRARY_PATH": fdk[version]}
        run = run_child("%s-%s-method%d" % (mode, version, method), [bins / ("fdk_decode-" + version), mode, path, tsv, str(method)], env=env_)
        out.record(case, path, [run], unitList=summary, fdkVersion=version, concealMethod=method)

    for path in aac:
        summary = lists[path][1]
        if summary["codec"] != "aac" or summary["kind"] == "unknown":
            out.record("fdk-skipped", path, [], unitList=summary)
            continue
        if summary["kind"] == "adif":
            for version in fdk:
                futures.append(pool.submit(fdk_case, "fdk-adif", version, path, "adif", 1))
            continue
        for version in fdk:
            futures.append(pool.submit(fdk_case, "fdk-units", version, path, "units", 1))
    default = "v2.0.2" if "v2.0.2" in fdk else sorted(fdk)[0]
    # Concealment methods 0 and 2 on a few files, 1 is in fdk-units (open question 7).
    for name in ("adts/crc-stereo-damaged.aac", "adts/truncated.aac", "adts/crc-blocks-damaged.aac", "aac/exhale.m4a", "mp4/eld.m4a", "aac/fdkaac-he.m4a", "aac/fdkaac-lc.m4a"):
        p = FIXTURES / name
        if p in lists and lists[p][1]["codec"] == "aac":
            for method in (0, 2):
                futures.append(pool.submit(fdk_case, "fdk-conceal", default, p, "units", method))
    # The decoder's own byte counters against the unit list (open question 5).
    for name in ("adts/junk-around.aac", "adts/truncated.aac", "adts/damaged-header.aac", "adts/crc-stereo-damaged.aac", "adts/cbr.aac"):
        p = FIXTURES / name
        if p in lists:
            futures.append(pool.submit(fdk_case, "fdk-bulk", default, p, "bulk", 1))
    concurrent.futures.wait(futures)
    for f in futures:
        f.result()
    futures.clear()

    # Ogg: direct paths on every Opus and Vorbis file, readers on those and on derived variants (open questions 4 and 8).
    ogg = [p for p in files("ogg", {".ogg", ".opus"})]
    variants = {}
    for name in ("external-opusenc-96.opus", "external-oggenc-q5.ogg", "external-ffmpeg-libopus.opus", "external-ffmpeg-vorbis.ogg"):
        variants.update({p: (FIXTURES / "ogg" / name, kind) for kind, p in ogg_variants(FIXTURES / "ogg" / name, tmp).items()})

    def ogg_case(path, derived=None):
        codec, _ = stream_channels(path)
        runs = []
        if codec == "opus":
            runs.append(run_child("opus", [bins / "ogg_decode", "opus", path]))
        elif codec == "vorbis":
            runs.append(run_child("vorbis", [bins / "ogg_decode", "vorbis", path]))
            runs.append(run_child("vorbis-granule", [bins / "ogg_decode", "vorbis-granule", path]))
        runs.extend(linux_reader_runs(path, tmp, None))
        extra = {"derivedFrom": relative(derived[0]), "variant": derived[1]} if derived else {}
        out.record("ogg", path, runs, codec=codec, **extra)

    for p in ogg:
        futures.append(pool.submit(ogg_case, p))
    for p, derived in variants.items():
        futures.append(pool.submit(ogg_case, p, derived))

    # TTA readers on every file (open question 8).
    def tta_case(path):
        out.record("tta", path, linux_reader_runs(path, tmp, args.tta))

    for p in files("tta", {".tta"}):
        futures.append(pool.submit(tta_case, p))

    # ffmpeg's Opus coded stage (open question 4).
    def ffmpeg_case(path):
        runs = []
        probe = run_child("ffprobe-packets", ["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "packet=duration", "-of", "csv=p=0", str(path)], jsonl=False)
        durations = [int(l.split(",")[0]) for l in probe.get("stdout", "").splitlines() if l.split(",")[0].isdigit()]
        channels = stream_channels(path)[1]
        counted = []
        for label, flags in (("ffmpeg-default", []), ("ffmpeg-skip-manual", ["-flags2", "+skip_manual"])):
            target = pathlib.Path(tmp) / ("ffmpeg-" + label + "-" + path.name + ".raw")
            run = run_child(label + "-file", ["ffmpeg", "-v", "error", "-y", *flags, "-i", str(path), "-f", "f32le", str(target)], jsonl=False)
            run["outputBytes"] = target.stat().st_size if target.exists() else None
            run["samples"] = run["outputBytes"] // (4 * channels) if run["outputBytes"] is not None and channels else None
            if target.exists():
                target.unlink()
            counted.append(run)
        runs = [probe, *counted]
        out.record("ffmpeg-opus", path, runs, packetCount=len(durations), packetDurationSum=sum(durations))

    for name in ("external-opusenc-96.opus", "opus-granule-edges.opus", "external-ffmpeg-libopus.opus", "opus-multistream-51.opus"):
        futures.append(pool.submit(ffmpeg_case, FIXTURES / "ogg" / name))
    concurrent.futures.wait(futures)
    for f in futures:
        f.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    out.environment(env)
    return 0


# ---- macOS -----------------------------------------------------------------------------------------------------------

def plist_version(path):
    try:
        result = subprocess.run(["/usr/bin/plutil", "-extract", "CFBundleShortVersionString", "raw", "-o", "-", str(path)], capture_output=True, text=True, timeout=20)
        return result.stdout.strip() or result.stderr.strip()
    except (OSError, subprocess.TimeoutExpired) as error:
        return "unavailable: %s" % error


def run_macos(args):
    out = Output(args.out)
    probe = pathlib.Path(args.probe)
    tmp = tempfile.mkdtemp(prefix="odp-")
    started = time.time()
    env = {
        "platform": "macos",
        "machine": platform.machine(),
        "macOS": tool_line(["sw_vers", "-productVersion"]), "build": tool_line(["sw_vers", "-buildVersion"]),
        "imageOS": os.environ.get("ImageOS"), "imageVersion": os.environ.get("ImageVersion"),
        "python": sys.version,
        "clang": tool_line(["clang", "--version"]),
        "audioCodecs": plist_version("/System/Library/Components/AudioCodecs.component/Contents/Info.plist"),
        "audioToolbox": plist_version("/System/Library/Frameworks/AudioToolbox.framework/Resources/Info.plist"),
    }
    out.environment(env)
    inventory = run_child("inventory", [probe, "inventory"])
    (pathlib.Path(args.out) / "inventory.json").write_text(json.dumps(inventory) + "\n", encoding="utf-8")

    corpus = [p for folder in ("aac", "adts", "apple", "mp4") for p in files(folder, AAC_EXTENSIONS | {".adts"})] + files("ac3", AC3_EXTENSIONS)
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=3)

    def case(path):
        tsv, summary = unit_list(path, tmp)
        runs = [run_child("reader", [probe, "reader", path])]
        if summary["kind"] != "unknown" and summary["units"]:
            runs.append(run_child("convert-prime-default", [probe, "convert", path, tsv, "default"]))
            runs.append(run_child("convert-prime-none", [probe, "convert", path, tsv, "none"]))
        folder = "ac3" if path.parent.name == "ac3" else "aac"
        out.record("coreaudio-" + folder, path, runs, unitList=summary)

    futures = [pool.submit(case, p) for p in corpus]
    concurrent.futures.wait(futures)
    for f in futures:
        f.result()
    shutil.rmtree(tmp, ignore_errors=True)
    env["elapsedSeconds"] = round(time.time() - started)
    out.environment(env)
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="platform", required=True)
    linux = sub.add_parser("linux")
    linux.add_argument("--out", required=True)
    linux.add_argument("--bin", required=True)
    linux.add_argument("--fdk", nargs="+", required=True)
    linux.add_argument("--tta")
    mac = sub.add_parser("macos")
    mac.add_argument("--out", required=True)
    mac.add_argument("--probe", required=True)
    args = parser.parse_args(argv)
    return run_linux(args) if args.platform == "linux" else run_macos(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
