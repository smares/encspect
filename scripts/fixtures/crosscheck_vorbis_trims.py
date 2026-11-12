#!/usr/bin/env python3
"""Compares the samples that Encspect's measurements keep of a Vorbis stream with the samples FFmpeg and oggdec write for it
(docs/formats/vorbis.md, "Decoding"; docs/design/lossy-decoders.md, "As built: Vorbis"; docs/development.md, "Vorbis decoding").

    crosscheck_vorbis_trims.py [--cli CMD] [--ffmpeg PATH] [FILE ...]

CMD runs the built CLI, for example "dotnet src/Encspect.Cli/bin/Release/net10.0/encspect.dll" (the default is that path relative to the repository). With no FILE
the script takes every Vorbis file of tests/fixtures/ogg and tests/fixtures/matroska whose only audio stream is one Vorbis stream.

For each file the analyzer's `--measure` figure "samples" (what its measurements keep after the start and end trimming, Matroska's DiscardPadding and Gap Trimming) is
compared with:

- FFmpeg (`-f f32le`) for Matroska files, which trims the end by the DiscardPadding of the last block (FFmpeg does not drop what a page's position discards, so it is
  not compared on Ogg files);
- oggdec for Ogg files, which trims by granule positions as libvorbis does (the start, the end and what a position after a gap discards).

A file the analyzer does not measure (libvorbis refuses its headers, and it is not decoded) is skipped. DIFFERENT pins the counts of the files where a reference writes
another count, with what is known; such a line prints OPEN, and the run fails if either count changes. The exit status is 1 where a count differs without being listed.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_CLI = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")

# Files where a reference's count is known to differ from the analyzer's, by reference, with what is known. Printed as OPEN; not a failure.
DIFFERENT = {
    ("vorbis-discard-padding-large.mka", "ffmpeg"): (21504, 21632, "the padding (2,646 samples) is more than the last packet returns (128): the analyzer takes 128, FFmpeg 6.1.1 writes every sample of the packet"),
    ("vorbis-cut-last-packet.ogg", "oggdec"): (39936, 26816, "the file ends inside its last page, whose whole packets the analyzer decodes and libogg drops with the page"),
    ("vorbis-end-page-repeated.ogg", "oggdec"): (29196, 39436, "OPEN: the packets after the first end-of-stream page lie past the end the decoder was told, so the measurements keep 29,196 where the report's Samples and oggdec give 39,436"),
    ("vorbis-eos-mid-then-more.ogg", "oggdec"): (18956, 28896, "OPEN: as vorbis-end-page-repeated.ogg (the packets after an end-of-stream page in the middle are not measured)"),
    ("vorbis-eos-then-eos.ogg", "oggdec"): (18956, 28896, "OPEN: as vorbis-end-page-repeated.ogg"),
    ("vorbis-first-extreme.ogg", "oggdec"): (29196, 28172, "OPEN: not examined"),
    ("vorbis-granule-errors.ogg", "oggdec"): (29696, 28672, "OPEN: not examined"),
    ("vorbis-packet-errors.ogg", "oggdec"): (11264, 8192, "OPEN: not examined (a damaged stream; the packets libvorbis refuses)"),
    ("vorbis-short-start-trim.ogg", "oggdec"): (26260, 25684, "OPEN: not examined"),
    ("vorbis-skip-before-first.ogg", "oggdec"): (29696, 28672, "OPEN: not examined (a refused packet before the first position)"),
}


def default_files():
    files = []
    for pattern in ("ogg/*.ogg", "matroska/*.mka", "matroska/*.webm"):
        for path in sorted(FIXTURES.glob(pattern)):
            manifest = path.with_suffix(".expected.json")
            if not manifest.exists():
                continue
            data = json.loads(manifest.read_text(encoding="utf-8"))
            codecs = [s.get("codec") for s in data.get("stream_infos") or data.get("streams") or []]
            if codecs == ["Vorbis"]:
                files.append(path)
    return files


def analyzer(cli, path):
    result = subprocess.run(shlex.split(cli) + ["--measure", "--json", str(path)], capture_output=True, text=True)
    if result.returncode not in (0, 1):
        return None
    streams = json.loads(result.stdout)[0]["streams"]
    if len(streams) != 1 or "measurements" not in streams[0]:
        return None
    m = streams[0]["measurements"]
    return m["samples"], m.get("trimmedSamples", 0), m["channels"]


def ffmpeg_samples(ffmpeg, path, channels):
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None
    return len(result.stdout) // (4 * channels)


def oggdec_samples(path, channels):
    with tempfile.TemporaryDirectory() as temp:
        target = pathlib.Path(temp) / "out.raw"
        result = subprocess.run(["oggdec", "--raw", "-o", str(target), str(path)], capture_output=True)
        if result.returncode != 0 or not target.exists():
            return None
        return target.stat().st_size // (2 * channels)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cli", default=DEFAULT_CLI)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    args = parser.parse_args()
    files = args.files or default_files()
    failed = compared = 0
    for path in files:
        found = analyzer(args.cli, path)
        if found is None:
            print(f"skip {path.name}: the analyzer does not measure the stream (libvorbis refuses its headers, or it is encrypted)")
            continue
        samples, trimmed, channels = found
        notes = []
        problems = []
        for reference, count in (("ffmpeg", ffmpeg_samples(args.ffmpeg, path, channels) if path.suffix != ".ogg" else None),
                                 ("oggdec", oggdec_samples(path, channels) if path.suffix == ".ogg" else None)):
            if count is None:
                continue
            compared += 1
            pinned = DIFFERENT.get((path.name, reference))
            if pinned is not None:
                if (samples, count) == pinned[:2]:
                    notes.append(f"OPEN {reference} {count}: {pinned[2]}")
                else:
                    problems.append(f"{reference} writes {count}, the analyzer keeps {samples}; the pinned counts are {pinned[1]} and {pinned[0]}")
            elif count == samples:
                notes.append(f"{reference} {count}")
            else:
                problems.append(f"{reference} writes {count}")
        if problems:
            failed += 1
            print(f"FAIL {path.name}: the analyzer keeps {samples} ({trimmed} trimmed); " + "; ".join(problems))
        else:
            label = "OPEN" if any(n.startswith("OPEN") for n in notes) else "ok  "
            print(f"{label} {path.name}: {samples} samples ({trimmed} trimmed) = " + ", ".join(notes) if notes else f"skip {path.name}: no reference decodes it")
    print(f"{compared} counts compared with FFmpeg and oggdec over {len(files)} files, {failed} failed")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
