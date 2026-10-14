#!/usr/bin/env python3
"""Checks tests/conformance/sbr-samples.json against HE-AAC and HE-AAC v2 files that other encoders wrote.

The analyzer names FAAC 2.x from the layout of the SBR headers and data (FaacSbr.cs): FAAC's writer is
fixed in ways that other encoders' are not, and that only means something where other encoders lay SBR out
differently, which their own files show. These are 13 files from FFmpeg's FATE suite: MPEG-4 audio SBR and
parametric stereo conformance streams (al_sbr_*) and Coding Technologies' aacPlus decoder check streams
(CT_DecoderCheck, File1 to File7). They are downloaded into a cache directory and checked against their
SHA-256 rather than committed.

The manifest is ours. Per file it records who wrote it, what ffprobe reads from its first audio stream
(profile, sample rate, channels), the codec the analyzer reports, the encoder it must report (none: none
of these files may be named FAAC) and its warnings and errors ("findings").

Without --analyzer, ffprobe's reading of every file is compared with the manifest. With --analyzer, the
analyzer's report on every file (encspect --ndjson) must show the same codec, no encoder and the same
findings.

Needs curl and, without --analyzer, ffprobe. Usage:

    python3 scripts/fixtures/check_sbr_samples.py [cache directory]
    python3 scripts/fixtures/check_sbr_samples.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import subprocess
import sys

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "sbr-samples.json"


def addresses(sample):
    """The sample's own address: FFmpeg's FATE server, the only one that serves it."""
    return [sample["url"]]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def tool_problems(path, sample):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=profile,sample_rate,channels", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    stream = json.loads(result.stdout)["streams"][0]
    found = {"profile": stream["profile"], "sample_rate": int(stream["sample_rate"]), "channels": stream["channels"]}
    return [] if found == sample["ffprobe"] else [f"ffprobe reads {found}, the manifest {sample['ffprobe']}"]


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    streams = report.get("streams", [])
    codec = streams[0]["codec"] if streams else None
    if codec != sample["analyzer_codec"]:
        problems.append(f"the analyzer reports the codec {codec}, the manifest {sample['analyzer_codec']}")
    encoder = streams[0].get("encoder") if streams else None
    if encoder != sample["analyzer_encoder"]:
        problems.append(f"the analyzer names the encoder {encoder}, the manifest {sample['analyzer_encoder']}")
    serious = sorted(f["code"] for f in report["findings"] if f["severity"] != "Info")
    if serious != sample["findings"]:
        problems.append(f"the analyzer reports {serious}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "sbr")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if not manifest["samples"]:
        raise SystemExit(f"{MANIFEST} lists no samples")
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} SBR samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
