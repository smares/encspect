#!/usr/bin/env python3
"""Checks tests/conformance/dts-hd-samples.json against real DTS-HD Master Audio files from the FFmpeg sample archive, which this repository
does not commit: a 7.1 stream of two lossless channel sets, a 96 kHz 5.0 stream and a 16-bit stereo stream. The script downloads them into
a cache directory and checks each file's SHA-256 against the manifest before using it.

They are the only real source of the lossless extension's channel set sub-headers and navigation index (ETSI TS 102 114 8.3.2, 8.4.2): that
every CRC16 of the headers holds, that the channel masks are in the order of Table 6-22 (the 7.1 file's sets are 0x627 and 0x180), and that
the downmix coefficients are counted as FFmpeg's dca_xll.c counts them (12 and 18) are what the real data settles. No real DTS:X or .dtshd file
could be found at a public address, so those have hand-built fixtures only.

Per file, ffprobe and MediaInfo must agree with the manifest, with their words mapped to the analyzer's (ffprobe's profile "DTS-HD MA" and
channel layout names, channel_layouts.py; MediaInfo's channels, sampling rate, bit depth, compression mode and commercial name). With --analyzer, the
analyzer's report (encspect --ndjson --hide frames) must match instead: the first stream, the lossless statistics, the DTS:X flags and exactly
the warnings "findings" and the infos "info_findings" list.

Needs curl, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_dts_hd_samples.py [cache directory]
    python3 scripts/fixtures/check_dts_hd_samples.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import subprocess
import sys

import channel_layouts
import downloads
import mediainfo_tool as mi

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "dts-hd-samples.json"


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def normalized(speakers):
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def tool_problems(path, sample):
    problems = []
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-f", "dts", "-select_streams", "a:0", "-show_entries",
                                       "stream=codec_name,profile,sample_rate,channels,channel_layout,bits_per_raw_sample", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    expected = sample["ffprobe"]
    if probe["codec_name"] != "dts" or probe.get("profile") != expected["profile"]:
        problems.append(f"ffprobe finds {probe['codec_name']} with the profile {probe.get('profile')!r}, expected dts, {expected['profile']!r}")
    if int(probe["sample_rate"]) != sample["sample_rate"] or int(probe["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {probe['sample_rate']} Hz and {probe['channels']} channels, the manifest {sample['sample_rate']} and {sample['channels']}")
    if probe.get("channel_layout") != expected["channel_layout"]:
        problems.append(f"ffprobe's channel layout is {probe.get('channel_layout')!r}, expected {expected['channel_layout']!r}")
    known = channel_layouts.FFMPEG.get(probe.get("channel_layout"))
    if known is None or normalized(known) != normalized(sample["layout"]):
        problems.append(f"ffprobe's channel layout {probe.get('channel_layout')} does not feed the manifest's speakers {sample['layout']}")
    if int(probe.get("bits_per_raw_sample", 0)) != sample["storage_width"]:
        problems.append(f"ffprobe's bits per raw sample {probe.get('bits_per_raw_sample')} is not the storage width {sample['storage_width']}")
    info = mi.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    for key, value in sample["mediainfo"].items():
        if audio.get(key) != value:
            problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
    return problems


def statistic(report, name):
    return next((n["value"] for n in mi.walk(report["root"]) if n["name"] == name and n.get("kind") != "Frame"), None)


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", "--hide", "frames", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    if report["root"].get("value") != sample["format"]:
        problems.append(f"the analyzer's format is {report['root'].get('value')}, the manifest {sample['format']}")
    stream = report["streams"][0]
    for key, expected in (("codec", sample["codec"]), ("sampleRate", sample["sample_rate"]), ("channels", sample["channels"]), ("channelLayout", sample["layout"]),
                          ("bitsPerSample", sample["bits_per_sample"])):
        if stream.get(key) != expected:
            problems.append(f"the analyzer's {key} is {stream.get(key)}, the manifest {expected}")
    for name, expected in (("Lossless Channel Sets", sample["channel_sets"]), ("Lossless Channels", sample["channels"]), ("Lossless Sampling Frequency", sample["sample_rate"]),
                           ("Lossless Source PCM Resolution", sample["bits_per_sample"]), ("Lossless Storage Bit Width", sample["storage_width"])):
        if statistic(report, name) != expected:
            problems.append(f"the analyzer's {name} is {statistic(report, name)}, the manifest {expected}")
    for name in ("HasDtsX", "HasDtsXImax"):
        if statistic(report, name) is not sample["has_dts_x"]:
            problems.append(f"the analyzer's {name} is {statistic(report, name)}, the manifest {sample['has_dts_x']}")
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
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "dts-hd")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mi.require()
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} DTS-HD Master Audio samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
