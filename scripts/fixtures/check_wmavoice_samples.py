#!/usr/bin/env python3
"""Checks tests/conformance/wmavoice-samples.json against real Windows Media Audio Voice files from the FFmpeg sample archive, which this
repository does not commit: ten ASF files of Microsoft's "Windows Media 9 Speech Codec" samples (4, 8 and 16 kbit/s, progressive and
streaming muxes, one cut to a length, one with a broken end, one whose superframes hold music) and an ASF video file with a Voice track.
The script downloads them into a cache directory and checks each file's SHA-256 against the manifest before using it.

They are the only real source of what Microsoft's own encoder (not the Windows Media Format SDK's writer of the committed fixtures) puts
in the superframes: frame types 0 to 16 in the proportions of speech, silence frames, shortened last superframes, the padding after a
superframe that a packet's spillover does not use, ASF packets that hold two blocks, and a file of music superframes
("WMAPro-in-WMAVoice") with junk in its codec data. No public specification describes the bitstream; FFmpeg's wmavoice.c is the reference, and
the analyzer reads Voice at header level until a clean-room rewrite, so these files are checked against the tools only.

Per file, ffprobe, ffmpeg and MediaInfo must agree with the manifest, with their words mapped to the analyzer's: ffprobe's codec name "wmavoice"
and bit rate (the container's nominal one), the samples ffmpeg decodes and
MediaInfo's Format "WMA" with the Format_Profile "Voice" and the CodecID "A", which are the format tag 0x000A; its BitRate is the nominal
bitrate and its SamplingCount the duration times the rate. The manifest's superframes, frames, frame types and findings are what the
removed frame-level reader reported; they stay as data for the clean-room rewrite.

Needs curl, ffmpeg, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_wmavoice_samples.py [cache directory]
"""

import json
import pathlib
import subprocess
import sys

import downloads
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "wmavoice-samples.json"


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def decode(path):
    """ffmpeg's decoded samples of the first audio stream and the errors it printed."""
    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", "s16le", "-"], capture_output=True)
    errors = "\n".join(line for line in result.stderr.decode("utf-8", "replace").splitlines() if "non monotonically increasing dts" not in line)
    return len(result.stdout) // 2, errors


def tool_problems(path, sample):
    problems = []
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels,bit_rate,extradata_size",
                                       "-of", "json", str(path)], capture_output=True, text=True, check=True).stdout)["streams"][0]
    expected = sample["ffprobe"]
    if probe["codec_name"] != "wmavoice" or int(probe["sample_rate"]) != sample["rate"] or int(probe["channels"]) != sample["channels"]:
        problems.append(f"ffprobe finds {probe['codec_name']} at {probe['sample_rate']} Hz with {probe['channels']} channels, the manifest {sample['rate']} and {sample['channels']}")
    if int(probe["bit_rate"]) != expected["bit_rate"] or int(probe["extradata_size"]) != 46:
        problems.append(f"ffprobe's bit rate is {probe['bit_rate']} and its extradata {probe['extradata_size']} bytes, the manifest {expected['bit_rate']} and 46")
    # ffprobe -count_frames decodes the stream: each superframe it decodes is a frame.
    counted = json.loads(subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-select_streams", "a:0", "-show_entries", "stream=nb_read_frames", "-of", "json", str(path)],
                                        capture_output=True, text=True, check=True).stdout)["streams"][0]["nb_read_frames"]
    if int(counted) != sample["superframes"]:
        problems.append(f"ffprobe decodes {counted} frames, the manifest {sample['superframes']} superframes")
    samples, errors = decode(path)
    if samples != sample["ffmpeg_samples"]:
        problems.append(f"ffmpeg decodes {samples} samples, the manifest {sample['ffmpeg_samples']}")
    for message in sample["ffmpeg_errors"]:
        if message not in errors:
            problems.append(f"ffmpeg does not report {message!r}")
    if not sample["ffmpeg_errors"] and errors.strip():
        problems.append(f"ffmpeg decoding: {errors.strip().splitlines()[0]}")

    info = mi.read_or_problem(path, problems, full=True)
    if info is None:
        return problems
    audio = info["Audio"][0] if info["Audio"] else {}
    for key, value in sample["mediainfo"].items():
        if audio.get(key) != value:
            problems.append(f"MediaInfo's {key} is {audio.get(key)!r}, expected {value!r}")
    # MediaInfo's SamplingCount is the container's duration times the rate.
    count = int(audio.get("SamplingCount", 0))
    if abs(count - sample["samples"]) > sample["rate"] // 100 and "sampling_count_differs" not in sample:
        problems.append(f"MediaInfo's sampling count is {count}, the manifest's samples {sample['samples']}")
    return problems


def main():
    arguments = sys.argv[1:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "wmavoice")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mi.require()
    tool_versions.require("ffmpeg", "ffprobe")
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        for problem in tool_problems(path, sample):
            problems += 1
            print(f"{sample['file']}: {problem}")
    print(f"Checked {len(manifest['samples'])} Windows Media Audio Voice samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
