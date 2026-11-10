#!/usr/bin/env python3
"""Checks tests/conformance/tak-samples.json against real TAK files from the FFmpeg sample archive, which this repository does not commit. The script downloads them into a
cache directory and checks each file's SHA-256 against the manifest before using it.

They are the TAK 1.0 and 2.2.0 files the design walked (docs/design/tak-optimfrog-headers.md, "Fixtures"): no other version can be downloaded from the author. Per file:

- the objects, read by tak_model.py from takStream.pas and the departures (every CRC-24 holds, the ID, size and order of each object, where the audio starts and, for 2.2.0,
  the last frame info and the end it gives, which is the file's end);
- ffprobe and MediaInfo give the codec, the sample rate, the channels, the bit depth and the duration (MediaInfo also the encoder, for 2.2.0); ffmpeg decodes the TAK 2.0 codec
  (2.2.0) without a CRC error to the PCM of the manifest's MD5, and refuses the TAK 1.0 codec ("Not yet implemented in FFmpeg");
- with --analyzer, the analyzer's report (encspect --ndjson --hide frames) must match the manifest instead: the stream, the encoder and its confidence, the header
  checksums compared and holding, and exactly the findings listed.

Needs curl, ffmpeg, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_tak_samples.py [cache directory]
    python3 scripts/fixtures/check_tak_samples.py --analyzer path/to/encspect [cache directory]
"""

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import downloads
import mediainfo_tool as mi
import tak_model
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "tak-samples.json"


def addresses(sample):
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def model_problems(path, sample):
    data = path.read_bytes()
    result = tak_model.manifest(data)
    problems = []
    if [(o["id"], o["size"]) for o in result["objects"]] != [tuple(o) for o in sample["objects"]]:
        problems.append(f"the objects are {[(o['id'], o['size']) for o in result['objects']]}, the manifest {sample['objects']}")
    if any(o["crc_ok"] is False for o in result["objects"]):
        problems.append("a CRC-24 of the metadata fails under the reading of tak_model.py")
    if result["audio_start"] != sample["audio_start"]:
        problems.append(f"the audio starts at {result['audio_start']}, the manifest {sample['audio_start']}")
    if (result["last_frame"] is not None) != sample["has_last_frame_info"]:
        problems.append("the last frame info is there or missing against the manifest")
    elif result["last_frame"] is not None:
        last = result["last_frame"]
        if (last["position"], last["size"]) != (sample["last_frame"]["position"], sample["last_frame"]["size"]) or last["end"] != len(data):
            problems.append(f"the last frame info says {last}, the manifest {sample['last_frame']} and the file ends at {len(data)}")
    summary = result["summary"]
    for key in ("codec", "profile", "channels", "samples", "frame_length", "encoder", "preset"):
        if summary[key] != sample[key]:
            problems.append(f"the stream info gives {key} {summary[key]}, the manifest {sample[key]}")
    return problems


def tool_problems(path, sample, work):
    problems = []
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,duration", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    if probe["codec_name"] != "tak" or int(probe["sample_rate"]) != sample["rate"] or int(probe["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {probe['codec_name']} at {probe['sample_rate']} Hz with {probe['channels']} channels")
    if abs(float(probe["duration"]) - sample["samples"] / sample["rate"]) > 0.002:
        problems.append(f"ffprobe's duration is {probe['duration']}, the manifest's samples last {sample['samples'] / sample['rate']}")
    result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", f"s{sample['bits']}le", "-"], capture_output=True)
    if sample["ffmpeg_decodes"]:
        if result.returncode != 0 or result.stderr.strip():
            problems.append(f"ffmpeg does not decode the file cleanly: {result.stderr.decode(errors='replace').strip()[:200]}")
        elif hashlib.md5(result.stdout).hexdigest() != sample["decoded_pcm_md5"]:
            problems.append("ffmpeg decodes samples that differ from the manifest's")
    elif result.returncode == 0:
        problems.append("ffmpeg is expected to refuse the TAK 1.0 codec (6.1.1: \"Not yet implemented in FFmpeg\"), but it decodes the file")
    info = mi.read_or_problem(path, problems)
    if info is not None:
        audio = info["Audio"][0] if info["Audio"] else {}
        for key, value in sample["mediainfo"].items():
            if audio.get(key) != value:
                problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
        if abs(float(audio.get("Duration", -1)) - sample["samples"] / sample["rate"]) > 0.002:
            problems.append(f"MediaInfo's duration is {audio.get('Duration')}, the samples last {sample['samples'] / sample['rate']}")
    return problems


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", "--hide", "frames", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    stream = report["streams"][0]
    for key, expected in (("codec", "TAK"), ("sampleRate", sample["rate"]), ("channels", sample["channels"]), ("bitsPerSample", sample["bits"]), ("samples", sample["samples"]),
                          ("encoder", sample["encoder"]), ("encoderConfidence", sample["encoder_confidence"])):
        if stream.get(key) != expected:
            problems.append(f"the analyzer's {key} is {stream.get(key)}, the manifest {expected}")
    verification = stream.get("verification") or {}
    crc = next((c for c in verification.get("checksums", []) if c["name"] == "metadata CRC-24"), None)
    compared = sum(1 for o in sample["objects"] if o[0] in (1, 2, 3, 4, 6, 7))
    if crc is None or (crc["stored"], crc["checked"], crc["failed"]) != (compared, compared, 0):
        problems.append(f"the analyzer's metadata CRC-24 tally is {crc}, expected {compared} objects compared and holding")
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
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "tak")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if analyzer is None:
        mi.require()
        tool_versions.require("ffmpeg", "ffprobe")
    fetch(manifest, cache)
    problems = 0
    with tempfile.TemporaryDirectory() as folder:
        for sample in manifest["samples"]:
            path = cache / sample["file"]
            found = analyzer_problems(analyzer, path, sample) if analyzer else [*model_problems(path, sample), *tool_problems(path, sample, pathlib.Path(folder))]
            for problem in found:
                problems += 1
                print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} TAK samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
