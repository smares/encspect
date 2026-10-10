#!/usr/bin/env python3
"""Writes MPEG audio fixtures with real encoders: LAME 3.100, TwoLAME 0.4.0 and ffmpeg 6.1 with libmp3lame.

The hand-built fixtures from make_mpeg_fixtures.py have zero payloads. These come from encoders
fed with a known signal and known settings, so the parser is checked against real bit streams:
CBR, VBR and ABR, joint stereo, mono and dual channel, MPEG-1, 2 and 2.5, CRC protection, free
format, header flags, and LAME's Info/Xing frame.

Each <name>.expected.json manifest records the exact command, the settings handed to the encoder
(the expectations), and the frames ffprobe reports (offset and length of each packet) as a
reference from an independent decoder. Packets, not decoded frames: ffmpeg skips the encoder delay
when decoding, which drops whole MPEG-2 frames from its frame list. ffprobe does not list the
Info/Xing frame LAME writes first, so the manifest says whether there is one. ffmpeg does not read free format and libmad skips the first
frame of a LAME free-format stream ("bad audio data length"), so that fixture records the frame
count mpg123 decodes instead.

Where the first frame holds a VBR header (LAME's Xing/Info and LAME tag, or the one ffmpeg's muxer
writes with its own encoder string), "vbr_header" records its raw values as vbr_tags.py reads them,
"vbr_header_fields" the fields the analyzer reports for it, and "vbr_findings" the findings it
causes; crosscheck_mpeg_fixtures.py confirms the raw values with mp3guessenc, mpg123 and ffprobe.
"detected_encoder" is the encoder the analyzer must name: LAME from its tag (high confidence when
the tag CRC holds) or, without a tag, from the "LAME3.100" it writes into ancillary data (low);
ffmpeg's tag names ffmpeg, so the LAME behind it is again known from ancillary data (medium);
TwoLAME leaves no trace. "mp3guessenc_encoder_string" is the encoder string mp3guessenc finds.

For the statistics, "mp3guessenc_stream" records mp3guessenc's frame count, length, average bitrate
and bitrate histogram, and, where a LAME tag gives an encoder delay and padding, "decoded_samples"
the samples per channel ffmpeg and mpg123 output with their gapless trimming.

For Layer III, the manifest also records the side information of every frame as minimp3 reads it
("minimp3_side_info", see minimp3_side_info.c), and the statistics mp3guessenc prints about the
stream ("mp3guessenc": block usage, stereo modes, global gain, the largest main_data_begin,
ancillary data, scalefactor scaling and scfsi), both from frames that are not the Info frame. For
Layer II, "minimp3_side_info" holds every frame's allocation, scfsi and scalefactors.

The output is committed. CI does not run this script, because the result depends on the encoder
versions; rerun it by hand when a fixture needs to change:

    apt-get install lame twolame ffmpeg libmp3lame0 madplay mpg123 mp3guessenc
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

import minimp3_tool
import vbr_tags
from crosscheck_mpeg_fixtures import decoded_samples, guessenc_stream, guessenc_summary, guessenc_tag_lines

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"
LAME_VERSION = "LAME 64bits version 3.100"
CHANNELS = {"Mono": 1}
TWOLAME_VERSION = "TwoLAME version 0.4.0"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"


def write_tone(path, rate, channels, seconds=0.5, clicks=False):
    """A 440 Hz sine (the right channel at 660 Hz), 16-bit PCM; with clicks, a quiet sine with a loud click
    every 50 ms, whose transients make LAME switch to short blocks."""
    frames = int(rate * seconds)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(channels)
        out.setsampwidth(2)
        out.setframerate(rate)
        data = bytearray()
        for i in range(frames):
            for channel in range(channels):
                frequency = 440 if channel == 0 else 660
                sample = 12000 * math.sin(2 * math.pi * frequency * i / rate)
                if clicks:
                    sample = sample / 20 + (24000 if i % (rate // 20) < 8 else 0)
                data += struct.pack("<h", int(sample))
        out.writeframes(bytes(data))


def ffprobe_packets(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [[int(p["pos"]), int(p["size"])] for p in json.loads(result.stdout).get("packets", [])]


def mpg123_frame_count(path, samples_per_frame, channels):
    """Frames mpg123 decodes, from the length of its 16-bit PCM output."""
    pcm = subprocess.run(["mpg123", "-q", "--no-gapless", "-s", str(path)], capture_output=True, check=True).stdout
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
    ("external-lame-no-reservoir", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "--nores", "-b", "128", "-m", "j"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False)),
    ("external-lame-clicks", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, clicks=True)),
    ("external-lame-forced-ms", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "f"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False)),
    ("external-lame-free-format", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "--freeformat", "-b", "400", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=None, free_format=True, info_frame=False)),
    ("external-lame-vbr-old-mono", ".mp3", 44100, 1, ["lame", "--quiet", "--vbr-old", "-V", "4", "-m", "m"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=None, info_frame=True)),
    ("external-lame-v0-replaygain", ".mp3", 44100, 2, ["lame", "--quiet", "-V", "0", "--replaygain-accurate"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-lame-crc-tag", ".mp3", 44100, 2, ["lame", "--quiet", "-p", "-b", "128", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, protected=True, info_frame=True)),
    ("external-lame-mpeg2-vbr", ".mp3", 22050, 2, ["lame", "--quiet", "-V", "3"],
     dict(version="2", layer=3, sample_rate=22050, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-lame-mpeg25-vbr", ".mp3", 8000, 1, ["lame", "--quiet", "-V", "5", "-m", "m"],
     dict(version="2.5", layer=3, sample_rate=8000, channel_mode="Mono", bitrate=None, info_frame=True)),
    ("external-lame-resample", ".mp3", 48000, 2, ["lame", "--quiet", "-b", "128", "--resample", "44.1"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    # LAME marks a tag "unwise" for any source at 32 kHz or below.
    ("external-lame-unwise", ".mp3", 32000, 2, ["lame", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    ("external-lame-free-format-tag", ".mp3", 44100, 2, ["lame", "--quiet", "--freeformat", "-b", "400", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=None, free_format=True, info_frame=True)),
    ("external-lame-forced-ms-tag", ".mp3", 44100, 2, ["lame", "--quiet", "-b", "192", "-m", "f", "--noreplaygain"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=192, info_frame=True)),
    ("external-ffmpeg-cbr", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    ("external-ffmpeg-vbr", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-q:a", "2", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    # ffmpeg's tag CRC covers the first 190 bytes, which only fits an MPEG-1 stereo frame.
    ("external-ffmpeg-mono", ".mp3", 44100, 1, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-b:a", "64k", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=64, info_frame=True)),
    # A LAME file remuxed by ffmpeg: ffmpeg writes its own Xing frame and LAME tag, with the
    # source's encoder string but ffmpeg's fields.
    ("external-ffmpeg-remux", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{lame -V 2}", "-c", "copy", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-twolame-192-stereo", ".mp2", 48000, 2, ["twolame", "--quiet", "-b", "192", "-m", "s"],
     dict(version="1", layer=2, sample_rate=48000, channel_mode="Stereo", bitrate=192, info_frame=False)),
    # Stereo, because TwoLAME 0.4.0 with -m j picks the mode per frame (joint, then stereo).
    ("external-twolame-crc-stereo", ".mp2", 44100, 2, ["twolame", "--quiet", "-p", "-b", "256", "-m", "s"],
     dict(version="1", layer=2, sample_rate=44100, channel_mode="Stereo", bitrate=256, protected=True, info_frame=False)),
    ("external-twolame-mpeg2-mono", ".mp2", 24000, 1, ["twolame", "--quiet", "-b", "64", "-m", "m"],
     dict(version="2", layer=2, sample_rate=24000, channel_mode="Mono", bitrate=64, info_frame=False)),
]


# lame --nogap encodes consecutive files gaplessly and marks the boundary in both LAME tags.
NOGAP = [("external-lame-nogap-first", 44100), ("external-lame-nogap-second", 44100)]
NOGAP_COMMAND = ["lame", "--quiet", "--nogaptags", "-b", "128", "--nogap"]
NOGAP_SETTINGS = dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)


def check_versions():
    lame = subprocess.run(["lame", "--version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not lame.startswith(LAME_VERSION):
        sys.exit(f"{LAME_VERSION} is required, found {lame}")
    ffmpeg = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not ffmpeg.startswith(FFMPEG_VERSION):
        sys.exit(f"{FFMPEG_VERSION} is required, found {ffmpeg}")
    twolame = subprocess.run(["twolame", "--help"], capture_output=True, text=True)
    if TWOLAME_VERSION not in twolame.stdout + twolame.stderr:
        sys.exit(f"{TWOLAME_VERSION} is required")


def first_frame(data):
    """Offset and length of the first frame, after an ID3v2 tag; a free-format frame ends at the next sync."""
    start = 0
    if data[:3] == b"ID3":
        start = 10 + ((data[6] & 127) << 21 | (data[7] & 127) << 14 | (data[8] & 127) << 7 | (data[9] & 127))
    header = data[start : start + 4]
    mpeg1 = (header[1] >> 3) & 1
    rates = [44100, 48000, 32000] if mpeg1 else [22050, 24000, 16000]
    rate = rates[(header[2] >> 2) & 3] // (1 if (header[1] >> 4) & 1 else 2)
    index = header[2] >> 4
    if index == 0:
        end = start + 4
        while data[end] != 0xFF or data[end + 1] != header[1] or (data[end + 2] ^ header[2]) & 0xFC:
            end += 1
        return start, end - start
    bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320] if mpeg1 else [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
    return start, (144 if mpeg1 else 72) * bitrates[index] * 1000 // rate + ((header[2] >> 1) & 1)


def detected_encoder(command, tag):
    """TwoLAME leaves no trace. LAME's own tag names it; ffmpeg's tag names ffmpeg, or copies the
    encoder string of the file it remuxes; without a LAME string in the tag, LAME is known from the
    "LAME3.100" it writes into ancillary data."""
    if command[0] == "twolame":
        return None
    lame = tag.get("lame") if tag else None
    if lame is None:
        return {"name": "LAME 3.100", "confidence": "Low"}
    if not lame["encoder"].startswith("LAME"):
        return {"name": "LAME 3.100", "confidence": "Medium"}
    return {"name": "LAME 3.100", "confidence": "High" if lame["tag_crc"] == lame["tag_crc_computed"] else "Medium"}


def describe_tag(path, command, manifest):
    data = path.read_bytes()
    start, length = first_frame(data)
    end = len(data) - (128 if data[-128:-125] == b"TAG" else 0)
    tag = vbr_tags.read(data[start : start + length], data[start + length : end])
    if tag is not None:
        tag["frame_offset"] = start
        manifest["vbr_header_fields"] = vbr_tags.report_fields(tag)
        manifest["vbr_findings"] = [{"code": code, "contains": contains} for code, contains in vbr_tags.findings(tag, start)]
    manifest["vbr_header"] = tag
    manifest["detected_encoder"] = detected_encoder(command, tag)
    manifest["mp3guessenc_encoder_string"] = guessenc_tag_lines(path).get("Encoder string")


def encode(command, source, path, temp):
    if command[0] != "ffmpeg":
        subprocess.run(command + [str(source), str(path)], check=True)
        return
    arguments = []
    for argument in command:
        if argument == "{in}":
            argument = str(source)
        elif argument == "{out}":
            argument = str(path)
        elif argument.startswith("{lame "):
            encoded = pathlib.Path(temp) / f"{path.stem}.lame.mp3"
            subprocess.run(["lame", "--quiet", *argument[1:-1].split()[1:], str(source), str(encoded)], check=True)
            argument = str(encoded)
        arguments.append(argument)
    subprocess.run(arguments, check=True)


def write_manifest(name, path, rate, channels, command, settings, dumper):
    signal = "a quiet 440/660 Hz tone with a click every 50 ms" if settings.get("clicks") else "a 440/660 Hz tone"
    manifest = {
        "description": f"{' '.join(command)} on 0.5 s of {signal}, {rate} Hz, {channels} channels.",
        "crosscheck": True,
        "encoder": {"command": command, **settings},
    }
    if settings.get("free_format"):
        manifest["mpg123_frames"] = mpg123_frame_count(path, 1152, channels)
    else:
        manifest["ffprobe_packets"] = ffprobe_packets(path)
    if settings["layer"] in (2, 3):
        manifest["minimp3_side_info"] = minimp3_tool.side_info(dumper, path)
    if settings["layer"] == 3:
        manifest["mp3guessenc"] = guessenc_summary(path)
    describe_tag(path, command, manifest)
    manifest["mp3guessenc_stream"] = guessenc_stream(path)
    if manifest["vbr_header"] and manifest["vbr_header"].get("lame"):
        manifest["decoded_samples"] = decoded_samples(path, CHANNELS.get(settings["channel_mode"], 2))
    text = json.dumps(manifest, indent=1, ensure_ascii=False) + "\n"
    (OUT / f"{name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


def main():
    check_versions()
    OUT.mkdir(parents=True, exist_ok=True)
    dumper = minimp3_tool.build()
    with tempfile.TemporaryDirectory() as temp:
        for name, extension, rate, channels, command, settings in FIXTURES:
            source = pathlib.Path(temp) / f"{name}.wav"
            write_tone(source, rate, channels, clicks=settings.get("clicks", False))
            path = OUT / f"{name}{extension}"
            encode(command, source, path, temp)
            write_manifest(name, path, rate, channels, command, settings, dumper)

        sources = []
        for name, rate in NOGAP:
            sources.append(pathlib.Path(temp) / f"{name}.wav")
            write_tone(sources[-1], rate, 2)
        nogap = pathlib.Path(temp) / "nogap"
        nogap.mkdir()
        subprocess.run(NOGAP_COMMAND[:3] + ["--nogapout", str(nogap)] + NOGAP_COMMAND[3:] + [str(s) for s in sources], check=True)
        for (name, rate), source in zip(NOGAP, sources):
            path = OUT / f"{name}.mp3"
            (nogap / f"{name}.mp3").replace(path)
            write_manifest(name, path, rate, 2, NOGAP_COMMAND, NOGAP_SETTINGS, dumper)
    print(f"Wrote {len(FIXTURES) + len(NOGAP)} external fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
