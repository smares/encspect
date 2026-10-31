#!/usr/bin/env python3
"""Checks tests/conformance/wma-samples.json against real Windows Media Audio Professional and Lossless files from the FFmpeg sample archive
and FATE suite, which this repository does not commit: 24 Professional files (stereo, 5.1 and 8 channels at 16 to 96 kHz from Windows Media
Format SDK 9, 10 and 11 and Windows Media Audio 10 Professional, among them three whose frames have no length prefix, three cut inside a media
object, one whose audio is in the extended header of its subframes and files of frames of 512, 1,024 and 4,096 samples) and 6 Lossless files
(WMFSDK 9 and 12, one of 24 bits with 8 zero low bits, one of raw samples, two cut inside a media object). The script downloads them into a cache
directory and checks each file's SHA-256 against the manifest before using it.

They exercise what the committed fixtures, all of one encoder build and signal, do not: the packets of 8,192 and 16,384 bytes, the frames
without the length prefix, a frame length adjustment, subframe counts of 4 and 16, the extended header, the 24-bit and zero low bits files of
Lossless, and the files cut inside a media object. The readers were written from the format descriptions in docs/design and never compared
with FFmpeg's code, so ffmpeg's decoder is the one outside check of the frame boundaries: the samples it decodes and the frames it outputs.

Per file, ffprobe, ffmpeg and MediaInfo must agree with the manifest, with their words mapped to the analyzer's: ffprobe's codec names "wmapro"
and "wmalossless" and the container's nominal bit rate, the samples and the frames (framecrc) ffmpeg decodes, and MediaInfo's Format "WMA" with
the Format_Profile "Pro" or "Lossless" and the CodecID "162" or "163". With --analyzer, the analyzer's report (encspect --ndjson --hide frames)
must match instead: the first stream, the samples (equal to ffmpeg's), the frames and packets, and exactly the codes of the findings.

Needs curl, ffmpeg, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_wma_samples.py [--analyzer path/to/encspect] [cache directory]
    python3 scripts/fixtures/check_wma_samples.py --pin path/to/encspect [cache directory]

The second form rewrites the measured fields of the manifest (what the analyzer and the tools report) from the files.
"""

import json
import pathlib
import re
import subprocess
import sys

import downloads
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "wma-samples.json"
CODECS = {"wma-pro": ("wmapro", "Windows Media Audio Professional", "Pro", "162"), "wma-lossless": ("wmalossless", "Windows Media Audio Lossless", "Lossless", "163")}


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def ffmpeg_frames(path):
    """ffmpeg's decoded samples, the frames it outputs (framecrc's lines) and the errors it printed."""
    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", "framecrc", "-"], capture_output=True, text=True)
    durations = [int(line.split(",")[3]) for line in result.stdout.splitlines() if line and not line.startswith("#")]
    # ffmpeg names its contexts by address ("[wmapro @ 0x55c0...]"), which differs from run to run.
    errors = sorted({re.sub(r" @ 0x[0-9a-f]+", "", line.strip()) for line in result.stderr.splitlines() if line.strip() and "non monotonically increasing dts" not in line})
    return sum(durations), len(durations), errors


def probe(path):
    return json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,bit_rate",
                                      "-of", "json", str(path)], capture_output=True, text=True, check=True).stdout)["streams"][0]


def tool_problems(path, sample):
    problems = []
    name, _, profile, codec_id = CODECS[sample["codec"]]
    stream = probe(path)
    if stream["codec_name"] != name or int(stream["sample_rate"]) != sample["rate"] or int(stream["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {stream['codec_name']} at {stream['sample_rate']} Hz with {stream['channels']} channels, the manifest {sample['rate']} and {sample['channels']}")
    if int(stream["bit_rate"]) != sample["ffprobe"]["bit_rate"]:
        problems.append(f"ffprobe's bit rate is {stream['bit_rate']}, the manifest {sample['ffprobe']['bit_rate']}")
    samples, frames, errors = ffmpeg_frames(path)
    if samples != sample["ffmpeg_samples"] or frames != sample["ffmpeg_frames"]:
        problems.append(f"ffmpeg outputs {frames} frames of {samples} samples, the manifest {sample['ffmpeg_frames']} of {sample['ffmpeg_samples']}")
    if errors != sample["ffmpeg_errors"]:
        problems.append(f"ffmpeg prints {errors}, the manifest {sample['ffmpeg_errors']}")
    info = mi.read_or_problem(path, problems, full=True)
    if info is None:
        return problems
    audio = info["Audio"][0] if info["Audio"] else {}
    expected = {"Format": "WMA", "Format_Profile": profile, "CodecID": codec_id, "Channels": str(sample["channels"]), "SamplingRate": str(sample["rate"])}
    for key, value in {**expected, **sample["mediainfo"]}.items():
        if audio.get(key) != value:
            problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
    return problems


def nodes_of(node):
    yield node
    for child in node.get("children", []):
        yield from nodes_of(child)


def analyze(analyzer, path):
    """What the analyzer reports of the file: its stream, the frames and packets, and the codes of its findings."""
    report = json.loads(subprocess.run([analyzer, "--ndjson", "--hide", "frames", str(path)], capture_output=True, text=True, check=True).stdout)
    stream = report["streams"][0]
    codec = next(n for n in nodes_of(report["root"]) if n["name"].startswith("Windows Media Audio") and n.get("kind") == "Stream")
    statistics = {c["name"]: c.get("value") for n in codec.get("children", []) if n["name"] == "Statistics" for c in n["children"]}
    return {
        "samples": stream["samples"],
        "frames": int(str(codec["value"]).split()[0]),
        "packets": statistics.get("Packets"),
        "findings": sorted({f["code"] for f in report["findings"] if f["severity"] != "Info"}),
        "info_findings": sorted({f["code"] for f in report["findings"] if f["severity"] == "Info"}),
    }


def analyzer_problems(analyzer, path, sample):
    problems = []
    found = analyze(analyzer, path)
    for key in ("samples", "frames", "packets", "findings", "info_findings"):
        if found[key] != sample[key]:
            problems.append(f"the analyzer's {key} are {found[key]}, the manifest {sample[key]}")
    if found["samples"] != sample["ffmpeg_samples"] and "samples_differ" not in sample:
        problems.append(f"the analyzer's samples are {found['samples']}, ffmpeg decodes {sample['ffmpeg_samples']}")
    return problems


def pin(analyzer, manifest, cache):
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        stream = probe(path)
        samples, frames, errors = ffmpeg_frames(path)
        sample["ffprobe"] = {"bit_rate": int(stream["bit_rate"])}
        sample["ffmpeg_samples"], sample["ffmpeg_frames"], sample["ffmpeg_errors"] = samples, frames, errors
        sample.update(analyze(analyzer, path))
    MANIFEST.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    arguments = sys.argv[1:]
    analyzer = None
    is_pin = False
    if arguments[:1] in (["--analyzer"], ["--pin"]):
        is_pin = arguments[0] == "--pin"
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "wma")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if not is_pin:
        mi.require()
        tool_versions.require("ffmpeg", "ffprobe")
    fetch(manifest, cache)
    if is_pin:
        pin(analyzer, manifest, cache)
        print(f"Pinned {len(manifest['samples'])} samples.", file=sys.stderr)
        return
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} Windows Media Audio Professional and Lossless samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
