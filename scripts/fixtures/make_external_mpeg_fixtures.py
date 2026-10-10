#!/usr/bin/env python3
"""Writes MPEG audio fixtures with real encoders: LAME 3.100 and TwoLAME 0.4.0.

The hand-built fixtures from make_mpeg_fixtures.py have zero payloads. These come from encoders
fed with a known signal and known settings, so the parser is checked against real bit streams:
CBR, VBR and ABR, joint stereo, mono and dual channel, MPEG-1, 2 and 2.5, CRC protection, free
format, header flags, and LAME's Info/Xing frame.

Each <name>.expected.json manifest records the exact command, the settings handed to the encoder
(the expectations), and the frames ffprobe reports (offset and length of each) as a reference
from an independent decoder. ffprobe does not list the Info/Xing frame LAME writes first, so the
manifest says whether there is one. ffmpeg does not read free format and libmad skips the first
frame of a LAME free-format stream ("bad audio data length"), so that fixture records the frame
count mpg123 decodes instead.

The output is committed. CI does not run this script, because the result depends on the encoder
versions; rerun it by hand when a fixture needs to change:

    apt-get install lame twolame ffmpeg madplay mpg123
    python3 scripts/fixtures/make_external_mpeg_fixtures.py
"""

import json
import math
import pathlib
import struct
import subprocess
import sys
import tempfile
import wave

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"
LAME_VERSION = "LAME 64bits version 3.100"
TWOLAME_VERSION = "TwoLAME version 0.4.0"


def write_tone(path, rate, channels, seconds=0.5):
    """A 440 Hz sine (the right channel at 660 Hz), 16-bit PCM."""
    frames = int(rate * seconds)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(channels)
        out.setsampwidth(2)
        out.setframerate(rate)
        data = bytearray()
        for i in range(frames):
            for channel in range(channels):
                frequency = 440 if channel == 0 else 660
                data += struct.pack("<h", int(12000 * math.sin(2 * math.pi * frequency * i / rate)))
        out.writeframes(bytes(data))


def ffprobe_frames(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "frame=pkt_pos,pkt_size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [[int(f["pkt_pos"]), int(f["pkt_size"])] for f in json.loads(result.stdout).get("frames", [])]


def mpg123_frame_count(path, samples_per_frame, channels):
    """Frames mpg123 decodes, from the length of its 16-bit PCM output."""
    pcm = subprocess.run(["mpg123", "-q", "-s", str(path)], capture_output=True, check=True).stdout
    return len(pcm) // (samples_per_frame * channels * 2)


FIXTURES = [
    # name, extension, input rate, input channels, command (input and output appended), settings
    ("external-lame-cbr128-joint", ".mp3", 44100, 2, ["lame", "--quiet", "-b", "128", "-m", "j"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    ("external-lame-cbr128-no-info", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False)),
    ("external-lame-vbr-v2", ".mp3", 44100, 2, ["lame", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-lame-abr160", ".mp3", 48000, 2, ["lame", "--quiet", "--abr", "160"],
     dict(version="1", layer=3, sample_rate=48000, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-lame-crc", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-p", "-b", "160", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=160, protected=True, info_frame=False)),
    ("external-lame-dual", ".mp3", 32000, 2, ["lame", "--quiet", "-t", "-b", "96", "-m", "d"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Dual channel", bitrate=96, info_frame=False)),
    ("external-lame-flags", ".mp3", 44100, 1, ["lame", "--quiet", "-t", "-b", "64", "-m", "m", "-c", "-o", "-e", "c"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=64, info_frame=False,
          copyright=True, original=False, emphasis="CCITT J.17")),
    ("external-lame-mpeg2-mono", ".mp3", 22050, 1, ["lame", "--quiet", "-t", "-b", "32", "-m", "m"],
     dict(version="2", layer=3, sample_rate=22050, channel_mode="Mono", bitrate=32, info_frame=False)),
    ("external-lame-mpeg25-mono", ".mp3", 8000, 1, ["lame", "--quiet", "-t", "-b", "16", "-m", "m"],
     dict(version="2.5", layer=3, sample_rate=8000, channel_mode="Mono", bitrate=16, info_frame=False)),
    ("external-lame-free-format", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "--freeformat", "-b", "400", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=None, free_format=True, info_frame=False)),
    ("external-twolame-192-stereo", ".mp2", 48000, 2, ["twolame", "--quiet", "-b", "192", "-m", "s"],
     dict(version="1", layer=2, sample_rate=48000, channel_mode="Stereo", bitrate=192, info_frame=False)),
    # Stereo, because TwoLAME 0.4.0 with -m j picks the mode per frame (joint, then stereo).
    ("external-twolame-crc-stereo", ".mp2", 44100, 2, ["twolame", "--quiet", "-p", "-b", "256", "-m", "s"],
     dict(version="1", layer=2, sample_rate=44100, channel_mode="Stereo", bitrate=256, protected=True, info_frame=False)),
    ("external-twolame-mpeg2-mono", ".mp2", 24000, 1, ["twolame", "--quiet", "-b", "64", "-m", "m"],
     dict(version="2", layer=2, sample_rate=24000, channel_mode="Mono", bitrate=64, info_frame=False)),
]


def check_versions():
    lame = subprocess.run(["lame", "--version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not lame.startswith(LAME_VERSION):
        sys.exit(f"{LAME_VERSION} is required, found {lame}")
    twolame = subprocess.run(["twolame", "--help"], capture_output=True, text=True)
    if TWOLAME_VERSION not in twolame.stdout + twolame.stderr:
        sys.exit(f"{TWOLAME_VERSION} is required")


def main():
    check_versions()
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as temp:
        for name, extension, rate, channels, command, settings in FIXTURES:
            source = pathlib.Path(temp) / f"{name}.wav"
            write_tone(source, rate, channels)
            path = OUT / f"{name}{extension}"
            subprocess.run(command + [str(source), str(path)], check=True)

            manifest = {
                "description": f"{' '.join(command)} on a 0.5 s, {rate} Hz, {channels}-channel 440/660 Hz tone.",
                "crosscheck": True,
                "encoder": {"command": command, **settings},
            }
            if settings.get("free_format"):
                manifest["mpg123_frames"] = mpg123_frame_count(path, 1152, channels)
            else:
                manifest["ffprobe_frames"] = ffprobe_frames(path)

            text = json.dumps(manifest, indent=1, ensure_ascii=False) + "\n"
            (OUT / f"{name}.expected.json").write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote {len(FIXTURES)} external fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
