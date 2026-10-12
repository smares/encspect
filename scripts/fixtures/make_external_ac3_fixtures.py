#!/usr/bin/env python3
"""Writes the encoder-made AC-3 and E-AC-3 fixtures into tests/fixtures/ac3 (external-*): ffmpeg 6.1's
ac3, ac3_fixed and eac3 encoders (mono, stereo, 3/2 + LFE, 44.1 and 32 kHz, Annex D's alternate bit
stream syntax, raw and in MP4) and Aften (git 2010-01-05, Ubuntu's aften package) with and without
block switching. Each encodes half a second of a deterministic signal written here: tones with
short noise bursts and, for block switching, clicks after quiet. ffmpeg's eac3 encoder writes no
dependent substreams (it encodes 7.1 input as 5.0), so the substream fixtures are hand-built
(make_ac3_fixtures.py).

The manifests hold what make_ac3_fixtures.py's do, worked out from the files (ac3_model.py) and,
where ffmpeg muxed MP4, from ffprobe's packets; for AC-3, "block_ends" holds where liba52 ends each
audio block (a52_blocks.c), so the tests hold the analyzer's bit allocation to liba52's. The encoder,
its version and the command are recorded too.

    python3 scripts/fixtures/make_external_ac3_fixtures.py --manifests-only

rewrites the manifests from the committed files without the encoders, keeping the parts only the
tools give. Needs ffmpeg 6.1, aften, liba52-0.7.4-dev and a C compiler otherwise.
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

import ac3_model as m

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parents[1] / "tests" / "fixtures" / "ac3"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
AFTEN_VERSION = "aften 0.0.8+git20100105-0ubuntu4"

# name, extension, tool, input (rate, channels, signal), arguments, extra manifest findings, fields
FIXTURES = [
    ("external-ffmpeg-mono-96k", ".ac3", "ffmpeg", (48000, 1, "tones"), ["-c:a", "ac3", "-b:a", "96k"], [], {}),
    ("external-ffmpeg-stereo-192k", ".ac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "ac3", "-b:a", "192k"], [], {}),
    ("external-ffmpeg-5.1-448k", ".ac3", "ffmpeg", (48000, 6, "tones"), ["-c:a", "ac3", "-b:a", "448k"], [], {}),
    ("external-ffmpeg-stereo-44k-64k", ".ac3", "ffmpeg", (44100, 2, "tones"), ["-c:a", "ac3", "-b:a", "64k"], [], {}),
    ("external-ffmpeg-stereo-32k-320k", ".ac3", "ffmpeg", (32000, 2, "tones"), ["-c:a", "ac3", "-b:a", "320k"], [], {}),
    ("external-ffmpeg-fixed-stereo-128k", ".ac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "ac3_fixed", "-b:a", "128k"], [], {}),
    ("external-ffmpeg-annex-d-5.1", ".ac3", "ffmpeg", (48000, 6, "tones"),
     ["-c:a", "ac3", "-b:a", "384k", "-dmix_mode", "2", "-ltrt_cmixlev", "0.595", "-loro_surmixlev", "0.5", "-dsurex_mode", "2", "-ad_conv_type", "1",
      "-room_type", "2", "-mixing_level", "95", "-dialnorm", "-24"], [],
     {"AC-3/Frame/Bit Stream Information/Bit Stream ID": "6 (AC-3 with the alternate bit stream syntax of Annex D)",
      "AC-3/Frame/Bit Stream Information/Extended Information 1/Preferred Downmix": "2 (Lo/Ro downmix preferred)"}),
    ("external-ffmpeg-eac3-mono-48k", ".eac3", "ffmpeg", (48000, 1, "tones"), ["-c:a", "eac3", "-b:a", "48k"], [], {}),
    ("external-ffmpeg-eac3-stereo-96k", ".eac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "eac3", "-b:a", "96k"], [], {}),
    ("external-ffmpeg-eac3-5.1-256k", ".eac3", "ffmpeg", (48000, 6, "tones"), ["-c:a", "eac3", "-b:a", "256k"], [], {}),
    ("external-aften-stereo-192k-blocksw", ".ac3", "aften", (48000, 2, "clicks"), ["-b", "192", "-s", "1"], [], {}),
    ("external-aften-5.1-448k", ".ac3", "aften", (48000, 6, "tones"), ["-b", "448"], [], {}),
    ("external-ffmpeg-mp4-5.1-448k", ".mp4", "ffmpeg", (48000, 6, "tones"), ["-c:a", "ac3", "-b:a", "448k"], [("mp4.moov-after-mdat", "not optimized for streaming")], {}),
    ("external-ffmpeg-mp4-eac3-stereo-96k", ".mp4", "ffmpeg", (48000, 2, "tones"), ["-c:a", "eac3", "-b:a", "96k"], [("mp4.moov-after-mdat", "not optimized for streaming")], {}),
]


def signal(rate, channels, kind, path):
    """Half a second of 16-bit PCM: per channel a tone with noise bursts, or quiet tones with clicks of 2 ms."""
    count = rate // 2
    frames = bytearray()
    seed = 12345
    for i in range(count):
        t = i / rate
        for ch in range(channels):
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
            noise = (seed / 0x7FFFFFFF) * 2 - 1
            if kind == "clicks":
                value = 0.05 * math.sin(2 * math.pi * (440 + 110 * ch) * t) + (0.9 if (i + ch * 3000) % 12000 < 96 else 0)
            else:
                burst = 0.3 if (i + ch * 1777) % 9000 < 400 else 0
                value = 0.35 * math.sin(2 * math.pi * (330 + 55 * ch) * t) + burst * noise
            frames += struct.pack("<h", int(max(-1, min(1, value)) * 32000))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))


def liba52_block_ends(tool, path):
    ends = {}
    for line in subprocess.run([tool, str(path)], capture_output=True, text=True, check=True).stdout.splitlines():
        parts = line.split()
        if len(parts) == 8:
            ends[parts[0]] = [int(p) for p in parts[2:]]
    return ends


def ffprobe_packets(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return [[int(p["pos"]), int(p["size"])] for p in json.loads(result.stdout)["packets"]]


def streams_of(data):
    frames = m.walk(data)
    first = frames[0][1]
    samples = sum(h["blocks"] * 256 for _, h in frames if not h["dependent"] and h["substream"] == 0)
    codec = "E-AC-3" if first["eac3"] else "AC-3"
    vbr = len({h["length"] for _, h in frames}) > 1 if first["eac3"] else len({h["bitrate_code"] for _, h in frames}) > 1
    layout = m.speakers(first["acmod"], first["lfeon"])
    return frames, [{"codec": codec, "sample_rate": first["rate"], "channels": len(layout), "channel_mode": m.CODING_MODES[first["acmod"]],
                     "channel_layout": layout, "samples": samples, "is_vbr": vbr}]


def manifest(name, extension, tool, settings, arguments, findings, fields, data, previous, block_ends, packets):
    if extension == ".mp4":
        frames = packets if packets is not None else previous["frames"]
        sample = b""
        stream = previous["streams"] if previous else None
        if stream is None:
            raise SystemExit("MP4 manifests need the encoder run once.")
        rows = frames
    else:
        rows_with, stream = streams_of(data)
        rows = [[offset, h["length"]] for offset, h in rows_with]
    result = {
        "description": f"Encoded by {tool} from {settings[1]} channels at {settings[0]} Hz ({settings[2]}).",
        "encoder": AFTEN_VERSION if tool == "aften" else FFMPEG_VERSION,
        "command": [tool] + arguments,
        "length": len(data),
        "format": "MP4" if extension == ".mp4" else "AC-3",
        "frames": rows,
        "gaps": [],
        "streams": stream,
        "fields": fields,
        "findings": [list(f) for f in findings],
        "crosscheck": {"ffprobe": True, "decode": True, **({"liba52": True} if extension == ".ac3" else {}), **({"skip_insertion": True} if extension == ".eac3" else {})},
    }
    if block_ends is not None:
        result["block_ends"] = block_ends
    elif previous and "block_ends" in previous:
        result["block_ends"] = previous["block_ends"]
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        work = pathlib.Path(work)
        tool_path = None
        if not only_manifests:
            missing = [t for t in ("ffmpeg", "ffprobe", "aften", "cc") if shutil.which(t) is None]
            if missing:
                raise SystemExit(f"Missing {', '.join(missing)}; install ffmpeg, aften, liba52-0.7.4-dev and a C compiler, or pass --manifests-only.")
            tool_path = work / "a52_blocks"
            subprocess.run(["cc", "-O2", "-o", str(tool_path), str(ROOT / "a52_blocks.c"), "-la52", "-lm"], check=True)
        for name, extension, tool, settings, arguments, findings, fields in FIXTURES:
            target = OUT / (name + extension)
            manifest_path = OUT / (name + ".expected.json")
            previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else None
            block_ends = None
            packets = None
            if not only_manifests:
                source = work / f"{name}.wav"
                signal(*settings, source)
                if tool == "aften":
                    subprocess.run(["aften"] + arguments + [str(source), str(target)], check=True, capture_output=True)
                else:
                    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source)] + arguments + ["-fflags", "+bitexact", "-flags:a", "+bitexact", str(target)],
                                   check=True)
                if extension == ".ac3":
                    block_ends = liba52_block_ends(tool_path, target)
                if extension == ".mp4":
                    packets = ffprobe_packets(target)
                    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(target)], capture_output=True, text=True,
                                                      check=True).stdout)["streams"][0]
                    eac3 = probe["codec_name"] == "eac3"
                    acmod = {1: 1, 2: 2, 6: 7}[settings[1]]
                    lfeon = 1 if settings[1] == 6 else 0
                    layout = m.speakers(acmod, lfeon)
                    previous = {"streams": [{"codec": "E-AC-3" if eac3 else "AC-3", "sample_rate": settings[0], "channels": len(layout),
                                             "channel_mode": m.CODING_MODES[acmod], "channel_layout": layout, "samples": len(packets) * 1536, "is_vbr": False}]}
            data = target.read_bytes()
            result = manifest(name, extension, tool, settings, arguments, findings, fields, data, previous, block_ends, packets)
            manifest_path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES)} manifests written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
