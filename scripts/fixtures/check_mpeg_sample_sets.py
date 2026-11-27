#!/usr/bin/env python3
"""Checks tests/conformance/thomson-mp3-samples.json and vbspot-mp3-samples.json against two private sets of MP3 files.

- Thomson's "mp3 Tips & Tricks for MP3 Decoder Implementations" (2008): nine files built to trip a decoder up, among them a
  51 MB ID3v2.3 tag, appended ID3v2.4 tags with a footer, VBR with and without a Xing or VBRI header, and 30 MB of raw PCM in the audio.
- The test samples of VBSpot, the lead's first audio analyzer (about 2008): two ID3v2 tags, junk after a VBRI header frame, headerless
  VBR, an APEv2 tag that looks like ID3v1, a cut file with APEv2, Lyrics3 v2 and ID3v1, and two mp3PRO files.

The files are Thomson's material or commercial recordings, which this repository must not redistribute, so the only address is the
private repository (downloads.private, which needs ENCSPECT_PRIVATE_TOKEN; without it a file that is not in the cache is skipped, as
for a pull request from a fork). Each file's SHA-256 is checked before it is used.

The manifests are ours. Per file they record the frame count, the regions (tags with their kind and version, the stream) with their
offsets and lengths, the gaps, the VBR header kind, the stream's figures, the encoder the analyzer names, and the findings. Each value was
checked against the file by hand before it went in. Without --analyzer the script confirms the download, the size, and with ffprobe the
codec, sample rate and channels; with --analyzer the analyzer's report on every file (encspect --ndjson) must equal the manifest.

Usage:

    python3 scripts/fixtures/check_mpeg_sample_sets.py [cache directory]
    python3 scripts/fixtures/check_mpeg_sample_sets.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import subprocess
import sys
import urllib.parse

import downloads
import tool_versions
from conformance_mpeg_audio import child, walk

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFESTS = ["thomson-mp3-samples.json", "vbspot-mp3-samples.json"]


def addresses(manifest, sample):
    """The private repository's copy, the only one."""
    return [downloads.private(f"mirrors/samples/{manifest['private_folder']}/{urllib.parse.quote(sample['file'])}")]


def load():
    return [json.loads((ROOT / "tests" / "conformance" / name).read_text(encoding="utf-8")) for name in MANIFESTS]


def tool_problems(path, sample):
    problems = []
    if path.stat().st_size != sample["size"]:
        problems.append(f"the file is {path.stat().st_size} bytes, the manifest lists {sample['size']}")
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels",
                             "-of", "json", str(path)], capture_output=True, text=True, check=True)
    stream = json.loads(result.stdout)["streams"][0]
    expected = {"codec_name": "mp3", "sample_rate": str(sample["stream"]["sample_rate"]), "channels": sample["stream"]["channels"]}
    if stream != expected:
        problems.append(f"ffprobe finds {stream}, the manifest lists {expected}")
    return problems


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")

    def differs(what, actual, expected):
        if actual != expected:
            problems.append(f"{what}: the analyzer reports {actual}, the manifest {expected}")

    frames = list(walk(report["root"], "Frame"))
    differs("frames", len(frames), sample["frames"])
    regions = [{"name": c["name"], "kind": c["kind"], "value": c.get("value"), "offset": c["offset"], "length": c["length"]} for c in report["root"]["children"]]
    differs("regions", regions, sample["regions"])
    stream_nodes = [c for c in report["root"]["children"] if c["kind"] == "Stream"]
    gaps = [{"offset": g["offset"], "length": g["length"]} for s in stream_nodes for g in s.get("children", []) if g.get("kind") == "Gap"]
    differs("gaps", gaps, sample["gaps"])
    header = lame = None
    if frames:
        header = next((n for n in ("Xing", "Info", "VBRI") if child(frames[0], n)), None)
        lame = child(frames[0], "LAME Tag") is not None
    differs("VBR header", header, sample["vbr_header"])
    differs("LAME tag", lame, sample["lame_tag"])
    streams = report["streams"]
    differs("streams", len(streams), 1)
    if streams:
        info = streams[0]
        differs("stream", {"codec": info["codec"], "sample_rate": info["sampleRate"], "channels": info["channels"], "channel_mode": info["channelMode"],
                           "samples": info["samples"], "is_vbr": info["isVbr"]}, sample["stream"])
        encoder = {"name": info["encoder"], "confidence": info["encoderConfidence"]} if info.get("encoder") else None
        differs("encoder", encoder, sample["encoder"])
    enhancement = next((c["value"] for s in stream_nodes for c in s.get("children", []) if c["name"] == "Enhancement"), None)
    differs("enhancement", enhancement, sample.get("enhancement"))
    differs("findings", sorted(f["code"] for f in report["findings"]), sample["findings"])
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "mpeg-samples")
    if analyzer is None:
        tool_versions.require("ffprobe")
    problems = checked = 0
    for manifest in load():
        folder = cache / manifest["private_folder"]
        folder.mkdir(parents=True, exist_ok=True)
        for sample in manifest["samples"]:
            path = folder / sample["file"]
            address = addresses(manifest, sample)
            if not downloads.has_token(address[0]) and not path.exists():
                print(f"{sample['file']}: skipped, the private repository needs ENCSPECT_PRIVATE_TOKEN", file=sys.stderr)
                continue
            downloads.fetch(address, sample["sha256"], path)
            checked += 1
            for problem in analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample):
                problems += 1
                print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {checked} private MPEG samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
