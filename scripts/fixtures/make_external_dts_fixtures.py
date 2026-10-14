#!/usr/bin/env python3
"""Writes the encoder-made DTS fixtures into tests/fixtures/dts (external-*): ffmpeg 6.1's dca
encoder (mono, stereo, 5.0 and 5.1 with the side surround pair, at 8 to 48 kHz, raw and in MP4).
Each encodes half a second of a deterministic signal written here: tones with short noise bursts.
The dca encoder is experimental (-strict -2), writes core frames of 512 samples only, and uses
neither extensions nor extension substreams, so those fixtures are hand-built (make_dts_fixtures.py).
ffmpeg's MP4 muxer writes DTS as an mp4a sample entry with an esds box of object type 0xA9 and no
ddts box, so the MP4 fixture checks the route through that object type.

The manifests hold what make_dts_fixtures.py's do, worked out from the files and, where ffmpeg
muxed MP4, from ffprobe's packets. The encoder, its version and the command are recorded too.

    python3 scripts/fixtures/make_external_dts_fixtures.py --manifests-only

rewrites the manifests from the committed files without the encoder, keeping the parts only the
tool gives. Needs ffmpeg 6.1 with the dca encoder otherwise.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "dts"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"

FC, FL, FR, BL, BR, LFE = "front center", "front left", "front right", "back left", "back right", "LFE"

# name, extension, input (rate, ffmpeg channel layout), bitrate, arrangement code, LFE present
FIXTURES = [
    ("external-ffmpeg-mono-8k-64k", ".dts", (8000, "mono"), "64k"),
    ("external-ffmpeg-stereo-22k-192k", ".dts", (22050, "stereo"), "192k"),
    ("external-ffmpeg-stereo-44k-256k", ".dts", (44100, "stereo"), "256k"),
    ("external-ffmpeg-5.0-16k-384k", ".dts", (16000, "5.0(side)"), "384k"),
    ("external-ffmpeg-5.1-11k-384k", ".dts", (11025, "5.1(side)"), "384k"),
    ("external-ffmpeg-5.1-48k-768k", ".dts", (48000, "5.1(side)"), "768k"),
    ("external-ffmpeg-mp4-stereo-22k-192k", ".mp4", (22050, "stereo"), "192k"),
]

LAYOUTS = {
    "mono": (0, 0, [FC]),
    "stereo": (2, 0, [FL, FR]),
    "5.0(side)": (9, 0, [FC, FL, FR, BL, BR]),
    "5.1(side)": (9, 1, [FC, FL, FR, BL, BR, LFE]),
}
MODES = {0: "1/0", 2: "2/0", 9: "3/2"}
SAMPLE_RATES = {8000: 1, 16000: 2, 32000: 3, 11025: 6, 22050: 7, 44100: 8, 12000: 11, 24000: 12, 48000: 13}
CHANNELS = {"mono": 1, "stereo": 2, "5.0(side)": 5, "5.1(side)": 6}


def signal(rate, channels, path):
    """Half a second of 16-bit PCM: per channel a tone with noise bursts."""
    frames = bytearray()
    seed = 12345
    for i in range(rate // 2):
        t = i / rate
        for ch in range(channels):
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
            noise = (seed / 0x7FFFFFFF) * 2 - 1
            burst = 0.3 if (i + ch * 1777) % 9000 < 400 else 0
            value = 0.35 * math.sin(2 * math.pi * (330 + 55 * ch) * t) + burst * noise
            frames += struct.pack("<h", int(max(-1, min(1, value)) * 32000))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))


def walk(data):
    """The core frames of a raw 16-bit big endian file as (offset, size, header fields)."""
    rows = []
    at = 0
    while at < len(data):
        if data[at:at + 4] != bytes.fromhex("7FFE8001"):
            raise SystemExit(f"No core frame at {at}.")
        value = int.from_bytes(data[at:at + 12], "big")
        fsize = (value >> 36) & 0x3FFF
        amode = (value >> 30) & 0x3F
        sfreq = (value >> 26) & 0xF
        blocks = ((value >> 50) & 0x7F) + 1
        rows.append((at, fsize + 1, {"amode": amode, "sfreq": sfreq, "blocks": blocks}))
        at += fsize + 1
    return rows


def ffprobe_packets(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return [[int(p["pos"]), int(p["size"])] for p in json.loads(result.stdout)["packets"]]


def main():
    only_manifests = "--manifests-only" in sys.argv
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        work = pathlib.Path(work)
        if not only_manifests:
            missing = [t for t in ("ffmpeg", "ffprobe") if shutil.which(t) is None]
            if missing:
                raise SystemExit(f"Missing {', '.join(missing)}; install ffmpeg 6.1, or pass --manifests-only.")
        for name, extension, (rate, layout), bitrate in FIXTURES:
            target = OUT / (name + extension)
            manifest_path = OUT / (name + ".expected.json")
            previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else None
            packets = None
            arguments = ["-c:a", "dca", "-strict", "-2", "-b:a", bitrate, "-af", f"aformat=channel_layouts={layout}"]
            if not only_manifests:
                source = work / f"{name}.wav"
                signal(rate, CHANNELS[layout], source)
                subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source)] + arguments + ["-fflags", "+bitexact", "-flags:a", "+bitexact"]
                               + (["-f", "dts"] if extension == ".dts" else []) + [str(target)], check=True)
                if extension == ".mp4":
                    packets = ffprobe_packets(target)
            data = target.read_bytes()
            amode, lfe, speakers = LAYOUTS[layout]
            if extension == ".mp4":
                rows = packets if packets is not None else previous["frames"]
                samples = len(rows) * 512
                sizes = {size for _, size in rows}
            else:
                walked = walk(data)
                rows = [[offset, size] for offset, size, _ in walked]
                samples = sum(h["blocks"] * 32 for _, _, h in walked)
                sizes = {size for _, size, _ in walked}
            result = {
                "description": f"Encoded by ffmpeg's dca encoder from {CHANNELS[layout]} channel{'s' if CHANNELS[layout] > 1 else ''} at {rate} Hz and {bitrate}bit/s.",
                "encoder": FFMPEG_VERSION,
                "command": ["ffmpeg"] + arguments,
                "length": len(data),
                "format": "MP4" if extension == ".mp4" else "DTS",
                "frames": rows,
                "gaps": [],
                "streams": [{"codec": "DTS", "sample_rate": rate, "channels": len(speakers), "channel_mode": MODES[amode], "channel_layout": speakers,
                             "samples": samples, "is_vbr": len(sizes) > 1}],
                "fields": {},
                "findings": [["mp4.moov-after-mdat", "not optimized for streaming"]] if extension == ".mp4" else [],
                "crosscheck": {"ffprobe": True, "decode": True, **({"libdca": True} if extension == ".dts" else {})},
            }
            if extension == ".mp4":
                result["crosscheck"]["skip"] = {}
            manifest_path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} manifests written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
