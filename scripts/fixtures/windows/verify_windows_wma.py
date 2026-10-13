#!/usr/bin/env python3
"""Prints what ffprobe and ffmpeg make of the files make_windows_wma_fixtures.ps1 wrote, for the
workflow's log, and checks the lossless ones: ffmpeg's wmalossless decoder must give back the PCM
they were encoded from, bit for bit.

    python3 verify_windows_wma.py wma-out

Never fails the workflow on a file it cannot judge; it prints why. Exits 1 only when a lossless
file decodes to other samples than it was encoded from.
"""

import json
import pathlib
import subprocess
import sys
import wave


def run(*args):
    return subprocess.run(args, capture_output=True, text=True)


def pcm_of(path):
    with wave.open(str(path), "rb") as w:
        return w.getnchannels(), w.getsampwidth(), w.getframerate(), w.readframes(w.getnframes())


def main():
    out = pathlib.Path(sys.argv[1])
    entries = json.loads((out / "encodes.json").read_text(encoding="utf-8"))
    failed = False
    for entry in entries if isinstance(entries, list) else [entries]:
        if entry.get("status") != "ok":
            print(f"{entry['name']}: {entry.get('status')}")
            continue
        wma = out / entry["file"]
        probe = run("ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(wma))
        if probe.returncode != 0:
            print(f"{entry['name']}: ffprobe failed: {probe.stderr.strip()}")
            continue
        info = json.loads(probe.stdout)
        stream = info["streams"][0]
        print(f"{entry['name']}: {stream.get('codec_name')} tag {stream.get('codec_tag')} {stream.get('sample_rate')} Hz "
              f"{stream.get('channels')} channels {stream.get('channel_layout')} {stream.get('bits_per_sample')} bits "
              f"bit_rate {stream.get('bit_rate')} duration {info['format'].get('duration')} s, {entry['bytes']} bytes, "
              f"format \"{entry.get('formatDescription')}\"")
        if "lossless" not in entry["name"]:
            decode = run("ffmpeg", "-v", "error", "-i", str(wma), "-f", "null", "-")
            print(f"  ffmpeg decode: {'ok' if decode.returncode == 0 and not decode.stderr.strip() else decode.stderr.strip()[:300]}")
            continue
        bits = entry.get("inputBits") or 16
        decoded = out / "lossless-decoded.wav"
        codec = "pcm_s24le" if bits == 24 else "pcm_s16le"
        result = run("ffmpeg", "-v", "error", "-y", "-i", str(wma), "-c:a", codec, str(decoded))
        if result.returncode != 0:
            print(f"  ffmpeg decode failed: {result.stderr.strip()[:300]}")
            continue
        source = pcm_of(out / entry["pcm"])
        actual = pcm_of(decoded)
        # The decoded stream may be a little longer or shorter than the source at its ends.
        same = source[3] == actual[3][:len(source[3])] and len(actual[3]) - len(source[3]) < 8 * source[0] * source[1]
        print(f"  lossless decode ({bits}-bit): {'bit-exact' if same else 'DIFFERENT'}; "
              f"{len(actual[3])} bytes decoded, {len(source[3])} encoded")
        failed |= not same
        decoded.unlink()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
