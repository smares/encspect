#!/usr/bin/env python3
"""Codes lossyWAV's output, and the signal it was made from, with the coders that have no wasted bits in their syntax, and prints what each file shows
(docs/design/lossywav-detection.md, "The FLAC and WavPack statistics"; the format docs of TTA, ALAC and TrueHD record the result).

    check_lossywav_coders.py [--encspect CMD] [--tta PATH] [--alacconvert PATH] [--keep DIR] [INPUT ...]

INPUT is a noise-floor input of make_lossywav_fixtures.FLOORS whose lossyWAV output is committed as tests/fixtures/flac/external-lossyflac-<name>.flac
(floor-44k and floor-44k-high by default; 24 bits, stereo). The committed FLAC file is decoded with `flac -d` and is the lossyWAV output; the input is made
again by floor_signal at the same length. Both go through, as black boxes:

- libtta-c 2.3's `tta` (libtta_tool.py): TTA;
- Apple's `alacconvert` (alac_tool.py), the CAF copied into an M4A file by `ffmpeg -c copy`, and ffmpeg's ALAC encoder;
- ffmpeg's experimental TrueHD encoder (`-c:a truehd -strict -2`).

For each file the script prints the size in bytes and the figures the analyzer shows for it: the average bitrate, ALAC's Bytes Shifted, TrueHD's Output Shifts and
Largest Output Shift, and, where the stream has a decoder, the levels' Low Bits Unused and the changes of the low bits per span. The coders are not run on any
file lossyWAV did not write, and no FFmpeg or libtta source is read.
"""

import argparse
import collections
import json
import pathlib
import shlex
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import make_lossywav_fixtures as ml  # noqa: E402

ROOT = HERE.parents[1]
DEFAULT_ENCSPECT = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")


def plain_wav(path, frames, rate):
    """A 24-bit stereo WAVE file of plain PCM (no extensible header, which the coders take)."""
    body = b"".join(v.to_bytes(3, "little", signed=True) for frame in frames for v in frame)
    fmt = struct.pack("<HHIIHH", 1, 2, rate, rate * 6, 6, 24)
    chunks = b"fmt " + struct.pack("<I", 16) + fmt + b"data" + struct.pack("<I", len(body)) + body
    pathlib.Path(path).write_bytes(b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks)


def read_frames(path):
    data = pathlib.Path(path).read_bytes()
    at = data.index(b"data")
    size = struct.unpack("<I", data[at + 4:at + 8])[0]
    body = data[at + 8:at + 8 + size]
    return [(int.from_bytes(body[i:i + 3], "little", signed=True), int.from_bytes(body[i + 3:i + 6], "little", signed=True)) for i in range(0, size, 6)]


def walk(node):
    yield node
    for child in node.get("children", []):
        yield from walk(child)


def figures(encspect, path):
    result = subprocess.run([*shlex.split(encspect), "--json", "--measure", "levels", str(path)], capture_output=True, text=True)
    report = json.loads(result.stdout)[0]
    nodes = list(walk(report["root"]))
    values = collections.defaultdict(list)
    for node in nodes:
        if "value" in node and not isinstance(node["value"], dict):
            values[node["name"]].append(str(node["value"]))
    stream = report["streams"][0]
    levels = (stream.get("measurements") or {}).get("levels") or {}
    spans = levels.get("lowBitsPerSpan")
    found = {"averageBitrate": stream.get("averageBitrate"), "codec": stream.get("codec")}
    for name in ("Bytes Shifted", "Largest Output Shift", "Largest LSB Count"):
        if values.get(name):
            found[name] = dict(collections.Counter(values[name]).most_common(6))
    if values.get("Output Shifts"):
        found["Output Shifts"] = dict(collections.Counter(values["Output Shifts"]).most_common(6))
    if levels:
        found["lowBitsUnused"] = levels["bitDepth"]["lowBitsUnused"]
    if spans:
        found["changes"] = spans["changes"]["count"]
        found["grid"] = spans["grid"]["samples"]
    found["findings"] = sorted({f["code"] for f in report["findings"]})
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--encspect", default=DEFAULT_ENCSPECT)
    parser.add_argument("--tta")
    parser.add_argument("--alacconvert")
    parser.add_argument("--keep")
    parser.add_argument("inputs", nargs="*", default=["floor-44k", "floor-44k-high"])
    arguments = parser.parse_args()
    import alac_tool
    import libtta_tool
    tta = arguments.tta or libtta_tool.build()
    alacconvert = arguments.alacconvert or alac_tool.build()
    result = {}
    for name in arguments.inputs:
        rate, channels, bits, tone, floor = ml.FLOORS[name]
        if (channels, bits) != (2, 24):
            raise SystemExit(f"{name}: only 24-bit stereo inputs are coded")
        with tempfile.TemporaryDirectory() as directory:
            work = pathlib.Path(directory)
            committed = ROOT / "tests" / "fixtures" / "flac" / f"external-lossyflac-{'b512' if name == 'floor-44k-high' else name}.flac"
            subprocess.run(["flac", "-d", "-s", "-f", "-o", str(work / "decoded.wav"), str(committed)], check=True, capture_output=True)
            lossy = read_frames(work / "decoded.wav")
            source = ml.floor_signal(rate, channels, bits, len(lossy), tone, floor)
            plain_wav(work / "source.wav", source, rate)
            plain_wav(work / "lossy.wav", lossy, rate)
            result[name] = {"samples": len(lossy), "samplesChanged": sum(1 for a, b in zip(source, lossy) if tuple(a) != tuple(b))}
            for label in ("source", "lossy"):
                wav = work / f"{label}.wav"
                files = {}
                subprocess.run([str(tta), "-e", str(wav), str(work / f"{label}.tta")], check=True, capture_output=True)
                files["tta"] = work / f"{label}.tta"
                subprocess.run([str(alacconvert), str(wav), str(work / f"{label}.caf")], check=True, capture_output=True)
                subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(work / f"{label}.caf"), "-c", "copy", "-f", "ipod", str(work / f"{label}.alacconvert.m4a")], check=True)
                files["alacconvert"] = work / f"{label}.alacconvert.m4a"
                subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(wav), "-c:a", "alac", str(work / f"{label}.ffmpeg.m4a")], check=True)
                files["ffmpeg-alac"] = work / f"{label}.ffmpeg.m4a"
                subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(wav), "-c:a", "truehd", "-strict", "-2", str(work / f"{label}.thd")], check=True)
                files["ffmpeg-truehd"] = work / f"{label}.thd"
                for coder, path in files.items():
                    result[name].setdefault(coder, {})[label] = {"bytes": path.stat().st_size, **figures(arguments.encspect, path)}
                    if arguments.keep:
                        pathlib.Path(arguments.keep).mkdir(parents=True, exist_ok=True)
                        shutil.copy(path, pathlib.Path(arguments.keep) / f"{name}-{path.name}")
    print(json.dumps(result, indent=1))


if __name__ == "__main__":
    main()
