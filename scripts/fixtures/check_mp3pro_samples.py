#!/usr/bin/env python3
"""Checks tests/conformance/mp3pro-samples.json against mp3PRO files from the FFmpeg sample archive.

No mp3PRO encoder runs today, so real mp3PRO files are the only source; the archive's are commercial
music, which this repository must not redistribute. This script downloads them into a cache
directory and checks each file's SHA-256 against the manifest before using it.

The manifest is ours. Per file it records the frame count, version and sample rate, and the
ancillary data mp3PRO keeps its spectral band replication data in: the bytes and the rate. This
script confirms the manifest with independent tools:

- ffprobe finds the listed number of frames, version and sample rate.
- mp3guessenc counts the same ancillary bytes and rate, and guesses Fraunhofer's mp3PRO encoder
  from the signature those bytes carry.

With --analyzer, the analyzer's report on every file (encspect --ndjson) must match the manifest
instead: frame count, version and sample rate, the mp3PRO enhancement with its ancillary bytes and
rate, the mp3PRO encoder, and no warning or error.

Needs curl, ffprobe and mp3guessenc. Usage:

    python3 scripts/fixtures/check_mp3pro_samples.py [cache directory]
    python3 scripts/fixtures/check_mp3pro_samples.py --analyzer path/to/encspect [cache directory]
"""

import hashlib
import json
import pathlib
import re
import subprocess
import sys
import urllib.parse

from conformance_mpeg_audio import child, walk
from crosscheck_mpeg_fixtures import guessenc_guess

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "mp3pro-samples.json"
VERSIONS = {"1": "MPEG-1", "2": "MPEG-2", "2.5": "MPEG-2.5"}


def fetch(manifest, cache):
    """Downloads every file the manifest lists, checking its SHA-256."""
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != sample["sha256"]:
            url = manifest["source"] + urllib.parse.quote(sample["file"])
            subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", str(path), url], check=True)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != sample["sha256"]:
            raise SystemExit(f"{sample['file']} has SHA-256 {digest}, expected {sample['sha256']}")


def tool_problems(path, sample):
    problems = []
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-count_packets", "-show_entries",
                             "stream=sample_rate,nb_read_packets", "-of", "json", str(path)], capture_output=True, text=True, check=True)
    stream = json.loads(result.stdout)["streams"][0]
    if (int(stream["nb_read_packets"]), int(stream["sample_rate"])) != (sample["frames"], sample["sample_rate"]):
        problems.append(f"ffprobe finds {stream['nb_read_packets']} frames at {stream['sample_rate']} Hz")
    output = subprocess.run(["mp3guessenc", str(path)], capture_output=True, text=True).stdout
    version = re.search(r"Detected MPEG stream version (\S+)", output).group(1)
    if VERSIONS[version] != sample["version"]:
        problems.append(f"mp3guessenc finds MPEG version {version}")
    total = re.search(r"Total amount\s*:\s*(\d+) bytes", output).group(1)
    rate = re.search(r"Bitrate\s*:\s*([\d.]+ kbps)", output).group(1)
    if (int(total), rate) != (sample["ancillary_bytes"], sample["ancillary_rate"]):
        problems.append(f"mp3guessenc counts {total} bytes of ancillary data at {rate}")
    if guessenc_guess(path) != sample["mp3guessenc_guess"]:
        problems.append(f"mp3guessenc guesses {guessenc_guess(path)!r}")
    return problems


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    frames = list(walk(report["root"], "Frame"))
    problems = []
    if len(frames) != sample["frames"]:
        problems.append(f"the analyzer finds {len(frames)} frames, the manifest lists {sample['frames']}")
    header = {c["name"]: c.get("value") for c in child(frames[0], "Header")["children"]} if frames else {}
    if (header.get("Version"), header.get("Sample Rate")) != (sample["version"], f"{sample['sample_rate']} Hz"):
        problems.append(f"the first frame is {header.get('Version')} at {header.get('Sample Rate')}")
    region = child(report["root"], "MPEG Audio")
    enhancement = child(region, "Enhancement") if region else None
    actual = None
    if enhancement:
        fields = {c["name"]: c.get("value") for c in enhancement["children"]}
        actual = (enhancement["value"], fields.get("Ancillary Data"), fields.get("Data Rate"))
    expected = (sample["enhancement"], f"{sample['ancillary_bytes']} bytes", sample["ancillary_rate"])
    if actual != expected:
        problems.append(f"the analyzer reports the enhancement {actual}, the manifest {expected}")
    encoder = child(region, "Encoder") if region else None
    if encoder is None or encoder["value"] != "mp3PRO encoder":
        problems.append(f"the analyzer names the encoder {encoder and encoder['value']}")
    serious = sorted(f["code"] for f in report["findings"] if f["severity"] != "Info")
    if serious:
        problems.append(f"the analyzer reports {serious}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "mp3pro")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} mp3PRO samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
