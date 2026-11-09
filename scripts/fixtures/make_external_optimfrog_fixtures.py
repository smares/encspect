#!/usr/bin/env python3
"""Runs OptimFROG 5.100's encoders (optimfrog_tool.py) over the project's signals and writes the files and a manifest of what they did.

    python3 scripts/fixtures/make_external_optimfrog_fixtures.py OUTPUT_DIRECTORY [--only name ...] [--tool-records]

It runs in CI only (.github/workflows/optimfrog-fixtures.yml): the binaries are never run on a development machine. OUTPUT_DIRECTORY gets:

- `fixtures/`: every file an encoder wrote (`.ofr`, `.off`, `.ofs`, the correction files `.ofc`), named `external-<case>.<extension>`;
- `manifest.json`: for each case the tool, its arguments, the input (kind, rate, channels, bits, samples, SHA-256), the exit statuses and
  the output of every run (encode twice, `--info`, `--verify`, `--decode`; for DualStream also the decode with and without the correction file),
  whether the two encodes are equal byte for byte, whether the decoded samples equal the input's, and optimfrog_model.py's reading of the file (the
  blocks, the samples of each COMP block);
- `tools/`: the output of `--help` and `--info` of each program and the SDK library's version, as records.

A case that fails is recorded with its output and does not stop the others; the exit status of this script is 1 only when a case in REQUIRED failed.
The cases are the design's matrix (docs/design/tak-optimfrog-headers.md, "Fixtures"): the presets, the 19 modes, several COMP blocks, the sample
formats, the HEAD and TAIL variants, `off`, and DualStream with and without its correction file. import_optimfrog_fixtures.py takes the artifact
into tests/fixtures/optimfrog.
"""

import hashlib
import json
import math
import os
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import flac_signal as sig
import optimfrog_model as model
import optimfrog_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]

PRESETS = [str(n) for n in range(11)] + ["max"]
MODES = ["fast", "normal", "high", "turbonew", "fastnew", "normalnew", "highnew-light", "extranew-light", "bestnew-light", "ultranew-light",
         "extra", "best", "ultra", "insane", "highnew", "extranew", "bestnew", "ultranew", "extrafast"]
REQUIRED = {"preset-2", "mode-normal", "float-32", "ofs-quality3", "ofs-quality3-correction"}


def quiet(rate, channels, count, bits):
    """A quiet, simple signal (a sine of a few hundred values' amplitude), which codes into few bytes however long it is."""
    scale = 1 << (bits - 1)
    out = bytearray()
    width = (bits + 7) // 8
    for i in range(count):
        value = int(300 * math.sin(2 * math.pi * 220 * i / rate) * scale / 32768) if bits > 8 else int(3 * math.sin(2 * math.pi * 220 * i / rate))
        for _ in range(channels):
            out += (value + 128 if bits == 8 else value).to_bytes(width, "little", signed=bits != 8)
    return bytes(out)


def wave(channels, rate, bits, body, tag=1, extensible=False, before=b"", after=b""):
    align = channels * bits // 8
    fmt = struct.pack("<HHIIHH", tag, channels, rate, rate * align, align, bits)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + before + b"data" + struct.pack("<I", len(body)) + body + (b"\0" if len(body) % 2 else b"") + after
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks


def signal(kind, rate, channels, bits, count):
    if kind == "quiet":
        return wave(channels, rate, bits, quiet(rate, channels, count, bits))
    return sig.wav(kind, rate, channels, count, bits)


def float_wav(rate, channels, count):
    body = bytearray()
    for frame in sig.samples("music", rate, channels, count, 16):
        for value in frame:
            body += struct.pack("<f", value / 32768)
    return wave(channels, rate, 32, bytes(body), tag=3)


def with_chunks(wav, before, after):
    """wav with a chunk before its data chunk and one after it."""
    at = wav.index(b"data")
    body = wav[at:]
    out = wav[12:at] + before + body + after
    return b"RIFF" + struct.pack("<I", 4 + len(out)) + b"WAVE" + out


def chunk(chunk_id, payload):
    return chunk_id + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) % 2 else b"")


def lossywav_input():
    return (ROOT / "tests" / "fixtures" / "wav" / "lossywav-standard.wav").read_bytes()


# Inputs: name -> (description, builder). The description is recorded; the builder returns the WAVE file.
def inputs():
    return {
        "music-16-stereo": ({"kind": "music", "rate": 44100, "channels": 2, "bits": 16, "samples": 30000}, lambda: sig.wav("music", 44100, 2, 30000, 16)),
        "music-16-mono": ({"kind": "music", "rate": 44100, "channels": 1, "bits": 16, "samples": 30000}, lambda: sig.wav("music", 44100, 1, 30000, 16)),
        "music-8-mono": ({"kind": "music", "rate": 22050, "channels": 1, "bits": 8, "samples": 20000}, lambda: sig.wav("music", 22050, 1, 20000, 8)),
        "music-24-stereo": ({"kind": "music", "rate": 48000, "channels": 2, "bits": 24, "samples": 30000}, lambda: sig.wav("music", 48000, 2, 30000, 24)),
        "music-32-stereo": ({"kind": "music", "rate": 44100, "channels": 2, "bits": 32, "samples": 20000}, lambda: sig.wav("music", 44100, 2, 20000, 32)),
        "float-32-stereo": ({"kind": "music-float", "rate": 44100, "channels": 2, "bits": 32, "samples": 30000, "format": "IEEE float"}, lambda: float_wav(44100, 2, 30000)),
        "chunks-16-stereo": ({"kind": "music", "rate": 44100, "channels": 2, "bits": 16, "samples": 30000, "extra": "LIST chunk before data, id3  chunk after"},
                             lambda: with_chunks(sig.wav("music", 44100, 2, 30000, 16), chunk(b"LIST", b"INFOISFT\x0e\0\0\0Encspect test\0\0"), chunk(b"id3 ", b"trailer bytes!"))),
        "quiet-8k-25s": ({"kind": "quiet", "rate": 8000, "channels": 1, "bits": 16, "samples": 200000}, lambda: signal("quiet", 8000, 1, 16, 200000)),
        "quiet-44k-41s": ({"kind": "quiet", "rate": 44100, "channels": 1, "bits": 16, "samples": 1808100}, lambda: signal("quiet", 44100, 1, 16, 1808100)),
        "lossywav": ({"kind": "lossyWAV output", "source": "tests/fixtures/wav/lossywav-standard.wav"}, lossywav_input),
    }


def case_list():
    cases = []
    for preset in PRESETS:
        cases.append({"name": f"preset-{preset}", "tool": "ofr", "input": "music-16-stereo", "args": ["--preset", preset]})
    for mode in MODES:
        cases.append({"name": f"mode-{mode}", "tool": "ofr", "input": "music-16-stereo", "args": ["--mode", mode]})
    for rate_input in ("quiet-8k-25s", "quiet-44k-41s"):
        for seek in ("min", "fast", "normal"):
            cases.append({"name": f"blocks-{rate_input}-seek-{seek}", "tool": "ofr", "input": rate_input, "args": ["--seek", seek]})
    cases += [
        {"name": "mono-16", "tool": "ofr", "input": "music-16-mono", "args": []},
        {"name": "8-mono", "tool": "ofr", "input": "music-8-mono", "args": []},
        {"name": "24-stereo", "tool": "ofr", "input": "music-24-stereo", "args": []},
        {"name": "32-stereo", "tool": "ofr", "input": "music-32-stereo", "args": []},
        {"name": "md5", "tool": "ofr", "input": "music-16-stereo", "args": ["--md5"]},
        {"name": "chunks", "tool": "ofr", "input": "chunks-16-stereo", "args": []},
        {"name": "lossywav", "tool": "ofr", "input": "lossywav", "args": []},
        {"name": "raw-headersize", "tool": "ofr", "input": "music-16-stereo", "raw": True,
         "args": ["--raw", "--channelconfig", "STEREO_LR", "--sampletype", "SINT16", "--rate", "44100", "--headersize", "@header"]},
        {"name": "raw-headersize-tailsize", "tool": "ofr", "input": "chunks-16-stereo", "raw": True,
         "args": ["--raw", "--channelconfig", "STEREO_LR", "--sampletype", "SINT16", "--rate", "44100", "--headersize", "@header", "--tailsize", "@tail"]},
        {"name": "float-32", "tool": "off", "input": "float-32-stereo", "args": []},
        {"name": "float-32-md5", "tool": "off", "input": "float-32-stereo", "args": ["--md5"]},
        {"name": "float-mantissa-16", "tool": "off", "input": "float-32-stereo", "args": ["--mantissabits", "16"]},
        {"name": "float-mantissa-8", "tool": "off", "input": "float-32-stereo", "args": ["--mantissabits", "8"]},
    ]
    for sample_type in ("FLOAT32_0", "FLOAT32_1", "FLOAT32_16", "FLOAT32_24"):
        cases.append({"name": f"float-sampletype-{sample_type.lower()}", "tool": "off", "input": "float-32-stereo", "args": ["--sampletype", sample_type]})
    cases.append({"name": "off-integer", "tool": "off", "input": "music-16-stereo", "args": []})
    for label, arguments in (("quality0", ["--quality", "0"]), ("quality3", ["--quality", "3"]), ("bitrate400", ["--bitrate", "400"])):
        for correction in (False, True):
            suffix = "-correction" if correction else ""
            cases.append({"name": f"ofs-{label}{suffix}", "tool": "ofs", "input": "music-16-stereo", "args": arguments + (["--correction"] if correction else []),
                          "dual": correction})
    for correction in (False, True):
        suffix = "-correction" if correction else ""
        cases.append({"name": f"ofs-mono-quality3{suffix}", "tool": "ofs", "input": "music-16-mono", "args": ["--quality", "3"] + (["--correction"] if correction else []),
                      "dual": correction})
        cases.append({"name": f"ofs-blocks-quality3{suffix}", "tool": "ofs", "input": "quiet-44k-41s", "args": ["--quality", "3"] + (["--correction"] if correction else []),
                      "dual": correction})
    cases.append({"name": "ofs-md5-correction", "tool": "ofs", "input": "music-16-stereo", "args": ["--quality", "3", "--md5", "--correction"], "dual": True})
    cases.append({"name": "ofs-mode-best", "tool": "ofs", "input": "music-16-stereo", "args": ["--quality", "3", "--mode", "best"]})
    cases.append({"name": "ofs-lossywav", "tool": "ofs", "input": "lossywav", "args": ["--quality", "3"]})
    return cases


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def run(command, cwd, timeout=900):
    """Runs a command and returns its record: exit status, and the first lines of its output."""
    try:
        result = subprocess.run([str(c) for c in command], cwd=cwd, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return {"exit": None, "stdout": "", "stderr": "timeout"}
    return {"exit": result.returncode, "stdout": result.stdout.decode("utf-8", "replace")[:4000], "stderr": result.stderr.decode("utf-8", "replace")[:4000]}


def data_of(wav):
    return wav[wav.index(b"data") + 8:]


def output_extension(tool):
    return {"ofr": ".ofr", "off": ".off", "ofs": ".ofs"}[tool]


def encode(package, case, wav, description, work, fixtures):
    """Encodes twice, records --info, --verify and --decode, and writes the encoder's files to fixtures."""
    tool = package / case["tool"]
    name = case["name"]
    header = wav.index(b"data") + 8
    tail = len(wav) - header - struct.unpack_from("<I", wav, header - 4)[0]
    case = {**case, "args": [{"@header": str(header), "@tail": str(tail)}.get(a, a) for a in case["args"]]}
    ext = output_extension(case["tool"])
    record = {"name": name, "tool": case["tool"], "arguments": case["args"], "input": {**description, "sha256": sha256(wav), "size": len(wav)}, "runs": {}}
    directory = work / name
    directory.mkdir()
    source = directory / "in.wav"
    source.write_bytes(wav)
    runs = []
    for attempt in (1, 2):
        out = directory / f"run{attempt}"
        out.mkdir()
        shutil.copy(source, out / "in.wav")
        command = [tool, "--encode", *case["args"], "in.wav", "--output", "out" + ext, "--overwrite"]
        record["runs"][f"encode{attempt}"] = {"command": [os.path.basename(str(c)) for c in command], **run(command, out)}
        runs.append(out)
    first, second = runs
    files = sorted(p.name for p in first.iterdir() if p.name not in ("in.wav",))
    record["files"] = files
    produced = first / ("out" + ext)
    if not produced.exists():
        record["status"] = "no output"
        return record
    equal = {}
    for fname in files:
        other = second / fname
        equal[fname] = other.exists() and other.read_bytes() == (first / fname).read_bytes()
    record["reproducible"] = equal
    data = produced.read_bytes()
    record["size"] = len(data)
    record["sha256"] = sha256(data)
    record["model"] = model.summary(data)
    # The files that are the fixtures: the encoded file and, for DualStream with a correction file, the .ofc beside it.
    dest = fixtures / f"external-{name}{ext}"
    shutil.copy(produced, dest)
    record["fixture"] = dest.name
    for fname in files:
        if fname != "out" + ext:
            suffix = pathlib.Path(fname).suffix
            shutil.copy(first / fname, fixtures / f"external-{name}{suffix}")
            record.setdefault("companions", {})[fname] = {"fixture": f"external-{name}{suffix}", "size": (first / fname).stat().st_size,
                                                          "sha256": sha256((first / fname).read_bytes()), "model": model.summary((first / fname).read_bytes())}
    for command in (["--info", "out" + ext], ["--verify", "out" + ext], ["--check", "out" + ext]):
        record["runs"][command[0].lstrip("-")] = {"command": command, **run([tool, *command], first)}
    wanted = data_of(wav) if not case.get("raw") else None
    for label, arguments, remove in (("decode", [], None), ("decode-correction", ["--correction"], None), ("decode-without-correction", [], "ofc")):
        if label != "decode" and not case.get("dual"):
            continue
        trial = directory / label
        trial.mkdir()
        for fname in files:
            if fname != "in.wav" and not (remove and fname.endswith("." + remove)):
                shutil.copy(first / fname, trial / fname)
        command = [tool, "--decode", *arguments, "out" + ext, "--output", "back.wav", "--overwrite"]
        record["runs"][label.replace("-", "_")] = {"command": command, **run(command, trial)}
        back = trial / "back.wav"
        if back.exists():
            body = data_of(back.read_bytes())
            record.setdefault("decoded", {})[label] = {"size": len(back.read_bytes()), "sha256": sha256(back.read_bytes()),
                                                         "equals_input": back.read_bytes() == wav if not case.get("raw") else None,
                                                         "samples_equal_input": body[:len(data_of(wav))] == data_of(wav) if not case.get("raw") else None}
    return record


def tool_records(package, out):
    """The programs' help and --info usage, and the SDK library's size, as records."""
    out.mkdir(parents=True, exist_ok=True)
    for tool in ("ofr", "off", "ofs"):
        for flag in ("--help", "-h", ""):
            result = run([package / tool, *( [flag] if flag else [])], package, timeout=60)
            (out / f"{tool}{flag.replace('-', '_') or '_noargs'}.txt").write_text(f"exit {result['exit']}\n--- stdout\n{result['stdout']}\n--- stderr\n{result['stderr']}\n", encoding="utf-8")
    library = optimfrog_tool.library(package)
    (out / "library.txt").write_text(f"{library.name} {library.stat().st_size} bytes sha256 {sha256(library.read_bytes())}\n", encoding="utf-8")


def main():
    arguments = sys.argv[1:]
    if not arguments:
        raise SystemExit(__doc__)
    out = pathlib.Path(arguments[0])
    only = set()
    if "--only" in arguments:
        only = set(arguments[arguments.index("--only") + 1:])
    package = optimfrog_tool.extract(tempfile.mkdtemp(prefix="optimfrog-"))
    fixtures = out / "fixtures"
    fixtures.mkdir(parents=True, exist_ok=True)
    tool_records(package, out / "tools")
    available = inputs()
    built = {}
    records = []
    with tempfile.TemporaryDirectory() as work_name:
        work = pathlib.Path(work_name)
        for case in case_list():
            if only and case["name"] not in only:
                continue
            description, builder = available[case["input"]]
            if case["input"] not in built:
                built[case["input"]] = builder()
            records.append(encode(package, case, built[case["input"]], description, work, fixtures))
            status = records[-1].get("status", "ok")
            print(case["name"], status, records[-1].get("size"), [r.get("exit") for k, r in records[-1]["runs"].items() if k.startswith("encode")], flush=True)
    manifest = {"tool": f"OptimFROG {optimfrog_tool.VERSION} for Linux x64 (SHA-256 {optimfrog_tool.SHA256})", "cases": records}
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    failed = [r["name"] for r in records if r["name"] in REQUIRED and r.get("status") == "no output"]
    print(f"{len(records)} cases, {sum(1 for r in records if r.get('status') != 'no output')} wrote a file; required failures: {failed}")
    raise SystemExit(1 if failed else 0)


if __name__ == "__main__":
    main()
