#!/usr/bin/env python3
"""Checks tests/conformance/mpeg2-audio.json against the ISO/IEC 13818-4 audio conformance streams.

ISO publishes 35 MPEG-2 audio conformance test sets, each a bitstream, an optional extension
bitstream, a description and the reference decoder's output. Their licence lets them be used,
unmodified, for the purpose of the standard, but not redistributed, so they are not in this
repository: this script downloads them into a cache directory and checks each archive's SHA-256
against the manifest before using it.

The manifest is ours. Per stream it records the version, layer, sample rate and frame count, and
for the 22 MPEG-1 Layer II streams with the MPEG-2 multichannel extension, what the analyzer must
report: the multichannel header (configuration, center, surround, LFE, extension bitstream,
dematrix procedure, multilingual channels), whether any frame uses prediction or dynamic
crosstalk, and how many frames have a multichannel CRC that holds and how many continue their
multichannel side information in the extension bitstream, a separate file the analyzer does not
read. The header values match ISO's description of every stream. This script confirms the
manifest with independent tools:

- ffprobe finds the listed number of frames.
- mp3guessenc reports the listed multichannel parameters; it verifies the multichannel CRC
  before it reports any.
- mctoolamed, a decoder derived from the ISO reference software, decodes every multichannel
  stream, with its extension bitstream, without a multichannel CRC error.

Needs curl, ffprobe, mp3guessenc and a C compiler. Usage:

    python3 scripts/fixtures/conformance_mpeg_audio.py [cache directory]
"""

import hashlib
import json
import pathlib
import re
import subprocess
import sys
import tarfile

import mctoolamed_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "mpeg2-audio.json"


def fetch(manifest, cache):
    """Downloads and unpacks every archive the manifest lists, checking its SHA-256."""
    cache.mkdir(parents=True, exist_ok=True)
    for stream in manifest["streams"]:
        archive = cache / f"{stream['name']}.tar.gz"
        if not archive.exists() or hashlib.sha256(archive.read_bytes()).hexdigest() != stream["archive_sha256"]:
            subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", str(archive), manifest["source"] + archive.name], check=True)
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        if digest != stream["archive_sha256"]:
            raise SystemExit(f"{archive.name} has SHA-256 {digest}, expected {stream['archive_sha256']}")
        target = cache / stream["name"]
        if not (target / stream["file"]).exists():
            with tarfile.open(archive) as tar:
                tar.extractall(target, filter="data")


def guessenc(path):
    output = subprocess.run(["mp3guessenc", "-e", str(path)], capture_output=True, text=True).stdout
    if "Multichannel parameters" not in output:
        return None
    field = lambda key: re.search(rf"{key}\s*:\s*(.+)", output).group(1).strip()
    return {"extension": field("Audio bitstream extension"), "lfe": field("LFE"), "channels": field("Channels"),
            "ml": field("Multi lingual channels"), "config": field("Channel configuration")}


def ffprobe_frames(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "frame=pkt_pos", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return len(json.loads(result.stdout).get("frames", []))


def main():
    cache = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / ".cache" / "conformance")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    fetch(manifest, cache)
    decoder = mctoolamed_tool.build()
    problems = 0
    for stream in manifest["streams"]:
        directory = cache / stream["name"]
        path = directory / stream["file"]
        found = []
        if ffprobe_frames(path) != stream["frames"]:
            found.append(f"ffprobe finds {ffprobe_frames(path)} frames, the manifest lists {stream['frames']}")
        if guessenc(path) != stream.get("mp3guessenc"):
            found.append(f"mp3guessenc reports {guessenc(path)}, the manifest {stream.get('mp3guessenc')}")
        if "multichannel" in stream and stream["layer"] == "II":
            extension = directory / stream["extension_file"] if stream["extension_file"] else None
            errors, _, channels = mctoolamed_tool.decode(decoder, path, extension)
            if errors or not channels:
                found.append(f"mctoolamed reports {errors} multichannel CRC errors and writes {channels} channels")
        for problem in found:
            problems += 1
            print(f"{stream['name']}: {problem}")
    print(f"Checked {len(manifest['streams'])} conformance streams, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
