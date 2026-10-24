#!/usr/bin/env python3
"""Checks tests/conformance/truehd-samples.json against real Dolby TrueHD and MLP files from the FFmpeg sample archive, which this repository
does not commit: a commercial 5.1 stream of two substreams that begins with 13 units before its first major sync, and two MLP stereo files, one
of them with IIR filters that carry state. The script downloads them into a cache directory and checks each file's SHA-256 against the manifest
before using it.

They are the real data the format description was checked against (docs/design/truehd-format.md, "Sources"): the lossless check of every
presentation holds over the whole of each file. Per file, ffprobe and MediaInfo must agree with the manifest, with their words mapped to the analyzer's
(ffprobe's codec and channel layout names, channel_layouts.py; MediaInfo's format, channels, sampling rate, bit depth and compression mode). With
--analyzer, the analyzer's report (encspect --ndjson --verify --hide frames) must match instead: the first stream, its samples, a verification that
holds with decoding, and exactly the warnings "findings" and the infos "info_findings" list.

Needs curl, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_truehd_samples.py [cache directory]
    python3 scripts/fixtures/check_truehd_samples.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import subprocess
import sys

import channel_layouts
import downloads
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "truehd-samples.json"


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def normalized(speakers):
    """FFmpeg's 5.1(side) puts the surrounds at the side; the analyzer names a TrueHD surround pair back, as for AC-3 and DTS."""
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def tool_problems(path, sample):
    problems = []
    forced = "truehd" if sample["codec"] == "Dolby TrueHD" else "mlp"
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-f", forced, "-select_streams", "a:0", "-show_entries",
                                       "stream=codec_name,sample_rate,channels,channel_layout,bits_per_raw_sample", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    expected = sample["ffprobe"]
    if probe["codec_name"] != expected["codec_name"]:
        problems.append(f"ffprobe finds {probe['codec_name']}, expected {expected['codec_name']}")
    if int(probe["sample_rate"]) != sample["sample_rate"] or int(probe["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {probe['sample_rate']} Hz and {probe['channels']} channels, the manifest {sample['sample_rate']} and {sample['channels']}")
    if probe.get("channel_layout") != expected["channel_layout"]:
        problems.append(f"ffprobe's channel layout is {probe.get('channel_layout')!r}, expected {expected['channel_layout']!r}")
    known = channel_layouts.FFMPEG.get(probe.get("channel_layout"))
    if known is None or normalized(known) != normalized(sample["layout"]):
        problems.append(f"ffprobe's channel layout {probe.get('channel_layout')} does not feed the manifest's speakers {sample['layout']}")
    if int(probe.get("bits_per_raw_sample", 0)) != expected["bits_per_raw_sample"]:
        problems.append(f"ffprobe's bits per raw sample {probe.get('bits_per_raw_sample')} is not {expected['bits_per_raw_sample']}")
    info = mi.read_or_problem(path, problems, full=True)
    if info is None:
        return problems
    audio = info["Audio"][0] if info["Audio"] else {}
    for key, value in sample["mediainfo"].items():
        if audio.get(key) != value:
            problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
    return problems


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", "--verify", "--hide", "frames", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    if report["root"].get("value") != sample["format"]:
        problems.append(f"the analyzer's format is {report['root'].get('value')}, the manifest {sample['format']}")
    stream = report["streams"][0]
    for key, expected in (("codec", sample["codec"]), ("sampleRate", sample["sample_rate"]), ("channels", sample["channels"]), ("channelLayout", sample["layout"]),
                          ("bitsPerSample", sample["bits_per_sample"]), ("samples", sample["samples"])):
        if stream.get(key) != expected:
            problems.append(f"the analyzer's {key} is {stream.get(key)}, the manifest {expected}")
    verification = stream.get("verification") or {}
    if verification.get("isVerified") is not True or verification.get("decoding") != "decoded" or verification.get("decodedSamples") != sample["samples"]:
        problems.append(f"the analyzer's verification is {verification}, expected a verified stream decoded to {sample['samples']} samples")
    serious = sorted({f["code"] for f in report["findings"] if f["severity"] != "Info"})
    if serious != sorted(sample["findings"]):
        problems.append(f"the analyzer reports {serious}, the manifest {sample['findings']}")
    infos = sorted({f["code"] for f in report["findings"] if f["severity"] == "Info"})
    if infos != sorted(sample["info_findings"]):
        problems.append(f"the analyzer reports the infos {infos}, the manifest {sample['info_findings']}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "truehd")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mi.require()
    if analyzer is None:
        tool_versions.require("ffprobe")
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} Dolby TrueHD and MLP samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
