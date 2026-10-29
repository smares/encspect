#!/usr/bin/env python3
"""Checks tests/conformance/monkeys-audio-samples.json against real Monkey's Audio files of version 3.97 from the FFmpeg sample archive, which this
repository does not commit. The script downloads them into a cache directory and checks each file's SHA-256 against the manifest before using it.

The SDK 13.44 decodes the versions before 3.99 and no longer writes them, so a file an encoder of the time wrote is the one reference for the coding
of these versions that does not come from monkeys_audio_legacy.py (whose files are held to the SDK's decoder, not to an encoder). Per file:

- mac -V (built from the pinned SDK, monkeys_audio_tool.py) decodes every frame with the SDK's decoder of the version and compares its CRC, and the audio
  of mac -d has the MD5 of the manifest's decoded_pcm_md5;
- ffmpeg decodes the same samples, and ffprobe and MediaInfo give the codec, the version, the sample rate, the channels, the bit depth and the duration
  (MediaInfo's sampling count is the duration times the rate, within a frame of the analyzer's samples);
- with --analyzer, the analyzer's report (encspect --verify --ndjson --hide frames) must match the manifest instead: the stream, the verification (every
  frame CRC compared and holding) and exactly the warnings "findings" and the infos "info_findings" list. The audio the analyzer's decoder writes is
  confirmed by the frame CRCs, which are the CRC-32 of the samples mac -d writes.

Needs curl, cmake and a C++ compiler for mac, ffmpeg, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_monkeys_audio_samples.py [cache directory]
    python3 scripts/fixtures/check_monkeys_audio_samples.py --analyzer path/to/encspect [cache directory]
"""

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import downloads
import mediainfo_tool as mi
import monkeys_audio_tool
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "monkeys-audio-samples.json"


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def wav_data(raw):
    """The data chunk of a RIFF/WAVE file, found by walking its chunks."""
    at = 12
    while at + 8 <= len(raw):
        size = int.from_bytes(raw[at + 4:at + 8], "little")
        if raw[at:at + 4] == b"data":
            return raw[at + 8:at + 8 + size] if size != 0xFFFFFFFF else raw[at + 8:]
        at += 8 + size + (size & 1)
    raise ValueError("the WAVE file has no data chunk")


def tool_problems(path, sample, mac, work):
    problems = []
    code, text = monkeys_audio_tool.run(mac, path, "-V")
    if code != 0:
        problems.append(f"mac -V fails: {text.strip().splitlines()[-1] if text.strip() else code}")
    out = work / "mac.wav"
    out.unlink(missing_ok=True)
    code, text = monkeys_audio_tool.run(mac, path, out, "-d")
    if code != 0 or not out.exists():
        problems.append(f"mac -d fails: {text.strip().splitlines()[-1] if text.strip() else code}")
    else:
        digest = hashlib.md5(wav_data(out.read_bytes())).hexdigest()
        if digest != sample["decoded_pcm_md5"]:
            problems.append(f"mac -d decodes to {digest}, the manifest says {sample['decoded_pcm_md5']}")

    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", f"s{sample['bits']}le", "-"], capture_output=True)
    if result.returncode != 0:
        problems.append(f"ffmpeg fails: {result.stderr.decode(errors='replace').strip()[:200]}")
    elif hashlib.md5(result.stdout).hexdigest() != sample["decoded_pcm_md5"]:
        problems.append("ffmpeg decodes samples that differ from the manifest's")
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    if probe["codec_name"] != "ape" or int(probe["sample_rate"]) != sample["rate"] or int(probe["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {probe['codec_name']} at {probe['sample_rate']} Hz with {probe['channels']} channels")
    if abs(float(probe["duration"]) - sample["samples"] / sample["rate"]) > 0.002:
        problems.append(f"ffprobe's duration is {probe['duration']}, the manifest's samples last {sample['samples'] / sample['rate']}")

    info = mi.read_or_problem(path, problems, full=True)
    if info is not None:
        audio = info["Audio"][0] if info["Audio"] else {}
        for key, value in sample["mediainfo"].items():
            if audio.get(key) != value:
                problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
        if int(audio.get("SamplingCount", 0)) != sample["mediainfo_sampling_count"]:
            problems.append(f"MediaInfo's sampling count is {audio.get('SamplingCount')}, expected {sample['mediainfo_sampling_count']}")
    return problems


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", "--verify", "--hide", "frames", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    stream = report["streams"][0]
    for key, expected in (("codec", "Monkey's Audio"), ("sampleRate", sample["rate"]), ("channels", sample["channels"]), ("bitsPerSample", sample["bits"]), ("samples", sample["samples"]),
                          ("encoder", f"Monkey's Audio (file version {sample['version'] // 1000}.{sample['version'] % 1000 // 10:02d})")):
        if stream.get(key) != expected:
            problems.append(f"the analyzer's {key} is {stream.get(key)}, the manifest {expected}")
    verification = stream.get("verification") or {}
    crc = next((c for c in verification.get("checksums", []) if c["name"] == "frame CRC-32"), None)
    if verification.get("isVerified") is not True or crc is None or (crc["stored"], crc["checked"], crc["failed"]) != (sample["frames"], sample["frames"], 0):
        problems.append(f"the analyzer's verification is {verification}, expected every one of {sample['frames']} frame CRCs compared and holding")
    if verification.get("decodedSamples") != sample["samples"]:
        problems.append(f"the analyzer decodes {verification.get('decodedSamples')} samples, the manifest {sample['samples']}")
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
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "monkeys-audio")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if analyzer is None:
        mi.require()
        tool_versions.require("ffmpeg", "ffprobe")
    fetch(manifest, cache)
    mac = None if analyzer else monkeys_audio_tool.build_mac(pathlib.Path(tempfile.gettempdir()) / "monkeys-audio-sdk")
    problems = 0
    with tempfile.TemporaryDirectory() as folder:
        for sample in manifest["samples"]:
            path = cache / sample["file"]
            found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample, mac, pathlib.Path(folder))
            for problem in found:
                problems += 1
                print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} Monkey's Audio samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
