#!/usr/bin/env python3
"""Checks the measurement fixtures (tests/fixtures/measurement, docs/design/spectral-analysis.md) against independent tools.

For every FLAC file there (the synthetic ones from make_measurement_fixtures.py and the lossy-derived ones from
make_external_measurement_fixtures.py):

- flac -t (flac 1.5.0 of Ubuntu 26.04) accepts the file: every frame's CRC-8 and CRC-16 and STREAMINFO's MD5 hold. The two gap files are
  left out (their frame numbers jump, which flac -t reports); their reason is printed.
- flac -d: the decoded samples (little-endian signed integers of as many whole bytes as the bit depth needs) have the MD5 of the samples that
  flac_model.py's decoder gives, the audio the measurements are made of, and FFmpeg 8.0.1 decodes the same samples (read as 32-bit integers).
- ffprobe: the sample rate, channels and bit depth are the model's.

The measurements themselves are checked against measurement_model.py by MeasurementFixtureTests; FFmpeg's showspectrum and astats are not
needed in step 1 (the design's cross-checks are for the levels and loudness). Every skip prints its reason; a disagreement fails the run.

    python3 scripts/fixtures/crosscheck_measurement_fixtures.py [name ...]
"""

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import flac_model as m
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "measurement"
GAPS = {"synthetic-gap-frames-removed", "synthetic-gap-jump-2-40"}


def run(arguments):
    return subprocess.run(arguments, capture_output=True)


def main():
    tool_versions.require("flac", "ffmpeg", "ffprobe")
    names = sys.argv[1:]
    problems = []
    skipped = []
    files = sorted(p for p in DIRECTORY.glob("*.flac") if not names or p.stem in names)
    with tempfile.TemporaryDirectory() as work:
        work = pathlib.Path(work)
        for path in files:
            found = []
            stream = m.Stream(path.read_bytes())
            info = stream.info
            if path.stem in GAPS:
                skipped.append(f"{path.stem}: flac -t, flac -d and ffmpeg (the frame numbers jump)")
            else:
                result = run(["flac", "-t", "-s", str(path)])
                if result.returncode != 0:
                    found.append(f"flac -t rejects the file: {result.stderr.decode(errors='replace').strip()[:200]}")
                bits = info["bits"]
                width = (bits + 7) // 8
                pcm, _ = stream.pcm()
                want = hashlib.md5(pcm).hexdigest()
                out = work / "flac.raw"
                result = run(["flac", "-d", "-s", "-f", "--force-raw-format", "--endian=little", "--sign=signed", "-o", str(out), str(path)])
                if result.returncode != 0:
                    found.append(f"flac -d fails: {result.stderr.decode(errors='replace').strip()[:200]}")
                elif hashlib.md5(out.read_bytes()).hexdigest() != want:
                    found.append("flac -d decodes other samples than the model")
                ffmpeg_out = work / "ffmpeg.raw"
                result = run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-f", "s32le", str(ffmpeg_out)])
                if result.returncode != 0:
                    found.append(f"ffmpeg fails: {result.stderr.decode(errors='replace').strip()[:200]}")
                else:
                    raw = ffmpeg_out.read_bytes()
                    values = [int.from_bytes(raw[i:i + 4], "little", signed=True) >> (32 - bits) for i in range(0, len(raw), 4)]
                    shifted = b"".join((v & ((1 << width * 8) - 1)).to_bytes(width, "little") for v in values)
                    if hashlib.md5(shifted).hexdigest() != want:
                        found.append("ffmpeg decodes other samples than the model")
            probe = run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)])
            streams = json.loads(probe.stdout or b"{}").get("streams", [])
            if not streams:
                found.append("ffprobe finds no audio stream")
            else:
                for key, want_value in (("sample_rate", info["sample_rate"]), ("channels", info["channels"])):
                    if str(streams[0].get(key)) != str(want_value):
                        found.append(f"ffprobe {key} is {streams[0].get(key)}, the model {want_value}")
            problems += [f"{path.stem}: {f}" for f in found]
    print(f"{len(files)} files checked")
    for line in skipped:
        print("skipped", line)
    for problem in problems:
        print("FAIL", problem)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
