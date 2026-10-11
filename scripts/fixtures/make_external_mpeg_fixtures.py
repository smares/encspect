#!/usr/bin/env python3
"""Writes MPEG audio fixtures with real encoders: LAME 3.100, TwoLAME 0.4.0, Shine 3.1.1, ffmpeg 6.1 with
libmp3lame, libshine and its own MP2 encoder, the Helix MP3 Encoder 5.2.4 and 5.0 r11 (helix_tool.py),
Fraunhofer's MP3 Surround encoder 1.5 (fhg_tool.py), which also writes plain MP3, VBRI headers and
Original File Length (OFL) blocks, Xing's tompg 3.0 under Wine (xing_tool.py), the only external
source of Layer I here, and BladeEnc 0.94.2 (blade_tool.py). Besides a tone, some encode two seconds of tones, noise, clicks
and a chord, which give each encoder's habits room to show, or silence.

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

"mp3guessenc_guess" is the encoder mp3guessenc guesses, and for a stream with an OFL block,
"mp3guessenc_ofl" the delay and original length mp3guessenc reads from it and "decoded_onset" the
sample at which the encoded tone starts in ffmpeg's output, which shows what the delay counts.
"enhancement" names the extension the analyzer must find in the ancillary data, or is null.

"findings" lists findings beyond the VBR header's that the file must cause: Shine 3.1.1 writes the
last frame of a stereo stream two bytes short, and Xing's tompg 3.0 writes Layer I frames with the
forbidden bit allocation 15, which libmad rejects, and with scalefactor index 63; Helix 5.0 r11
writes granules whose scalefactors are longer than their part2_3_length.

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

    apt-get install lame twolame shineenc ffmpeg libmp3lame0 madplay mpg123
    python3 scripts/fixtures/make_external_mpeg_fixtures.py
"""

import json
import math
import random
import pathlib
import struct
import subprocess
import sys
import tempfile
import wave

import blade_tool
import fhg_tool
import helix_tool
import minimp3_tool
import vbr_tags
import xing_tool
from crosscheck_mpeg_fixtures import (decoded_onset, decoded_samples, guessenc_guess, guessenc_ofl, guessenc_stream, guessenc_summary,
                                      guessenc_tag_lines)

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"
LAME_VERSION = "LAME 64bits version 3.100"
CHANNELS = {"Mono": 1}
TWOLAME_VERSION = "TwoLAME version 0.4.0"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
SHINE_VERSION = "3.1.1"


# Left, right, center, LFE, left surround, right surround.
SURROUND_FREQUENCIES = [440, 660, 550, 60, 330, 880]


def write_tone(path, rate, channels, seconds=0.5, signal="tone"):
    """16-bit PCM. "tone": a 440 Hz sine (the right channel at 660 Hz, 5.1 channels each at their
    own frequency). "clicks": a quiet sine with a loud click every 50 ms, whose transients make LAME
    switch to short blocks. "music": two seconds that give an encoder something to decide, tones,
    then noise with clicks, then a pause and a chord. "silence": all zeros."""
    if signal == "music":
        seconds = 2.0
    rng = random.Random(7)
    frames = int(rate * seconds)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(channels)
        out.setsampwidth(2)
        out.setframerate(rate)
        data = bytearray()
        for i in range(frames):
            t = i / rate
            for channel in range(channels):
                frequency = SURROUND_FREQUENCIES[channel] if channels == 6 else 440 if channel == 0 else 660
                sample = 12000 * math.sin(2 * math.pi * frequency * i / rate)
                if signal == "clicks":
                    sample = sample / 20 + (24000 if i % (rate // 20) < 8 else 0)
                elif signal == "music":
                    if t < 0.7:
                        sample = 8000 * math.sin(2 * math.pi * (440 if channel == 0 else 554) * t) + 3000 * math.sin(2 * math.pi * 3300 * t)
                    elif t < 1.4:
                        sample = rng.uniform(-4000, 4000) + (20000 if i % (rate // 8) < 20 else 0)
                    else:
                        chord = sum(math.sin(2 * math.pi * f * t + channel * k) for k, f in enumerate((262, 330, 392)))
                        sample = 0 if t < 1.6 else 6000 * chord
                elif signal == "silence":
                    sample = 0
                data += struct.pack("<h", max(-32768, min(32767, int(sample))))
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
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="clicks")),
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
    ("external-fhg-surround-cbr", ".mp3", 44100, 6, ["mp3sEncoder", "-br", "192000"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=192, info_frame=False, enhancement="MP3 Surround")),
    ("external-fhg-surround-ofl", ".mp3", 44100, 6, ["mp3sEncoder", "-br", "192000", "-ofl"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=192, info_frame=False, enhancement="MP3 Surround")),
    ("external-fhg-surround-vbr", ".mp3", 44100, 6, ["mp3sEncoder", "-br", "0", "-m", "2", "-vbri"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, enhancement="MP3 Surround")),
    ("external-fhg-surround-vbr-ofl", ".mp3", 44100, 6, ["mp3sEncoder", "-br", "0", "-m", "2", "-vbri", "-ofl"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, enhancement="MP3 Surround")),
    ("external-fhg-stereo-cbr", ".mp3", 44100, 2, ["mp3sEncoder", "-br", "128000"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False)),
    ("external-fhg-stereo-ofl", ".mp3", 44100, 2, ["mp3sEncoder", "-br", "128000", "-ofl"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False)),
    ("external-fhg-stereo-vbr", ".mp3", 44100, 2, ["mp3sEncoder", "-br", "0", "-m", "3", "-vbri"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-fhg-stereo-vbr-ofl", ".mp3", 44100, 2, ["mp3sEncoder", "-br", "0", "-m", "3", "-vbri", "-ofl"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True)),
    ("external-fhg-mono-cbr", ".mp3", 44100, 1, ["mp3sEncoder", "-br", "64000"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=64, info_frame=False)),
    ("external-fhg-music-cbr", ".mp3", 44100, 2, ["mp3sEncoder", "-br", "128000"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    # Shine 3.1.1 never uses the bit reservoir, short blocks or scalefactor scaling, and leaves no
    # ancillary data; it writes no tag.
    ("external-shine-music", ".mp3", 44100, 2, ["shineenc", "-q", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-shine-joint", ".mp3", 44100, 2, ["shineenc", "-q", "-j", "-b", "160"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=160, info_frame=False, signal="music")),
    ("external-shine-mono", ".mp3", 48000, 1, ["shineenc", "-q", "-b", "64"],
     dict(version="1", layer=3, sample_rate=48000, channel_mode="Mono", bitrate=64, info_frame=False, signal="music")),
    ("external-shine-mpeg2", ".mp3", 22050, 2, ["shineenc", "-q", "-b", "64"],
     dict(version="2", layer=3, sample_rate=22050, channel_mode="Stereo", bitrate=64, info_frame=False, signal="music")),
    ("external-shine-silence", ".mp3", 44100, 2, ["shineenc", "-q", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="silence")),
    ("external-ffmpeg-shine", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libshine", "-b:a", "128k", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=True, signal="music")),
    # The Helix encoder writes a Xing header and a LAME tag whose encoder string is "LAMEH5.24";
    # without it (-X0), it sets the copyright and original bits in every frame.
    ("external-helix-cbr", ".mp3", 44100, 2, ["hmp3", "-B64"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-helix-vbr", ".mp3", 44100, 2, ["hmp3", "-V60"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-helix-no-tag", ".mp3", 44100, 2, ["hmp3", "-B64", "-X0"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-helix-no-tag-mono", ".mp3", 32000, 1, ["hmp3", "-B48", "-X0"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Mono", bitrate=48, info_frame=False, signal="music")),
    # Helix 5.0 r11 writes no Xing header in any mode, so only the frames point to it.
    ("external-helix-r11-cbr", ".mp3", 44100, 2, ["tomp3", "-B64"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-helix-r11-vbr", ".mp3", 44100, 2, ["tomp3", "-V60"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=False, signal="music")),
    ("external-helix-r11-mono", ".mp3", 32000, 1, ["tomp3", "-B48", "-M3"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Mono", bitrate=48, info_frame=False, signal="music")),
    # Xing's tompg takes the bitrate per channel. It never switches to short blocks, never uses
    # scfsi and sets the original bit in every frame; in joint stereo it codes intensity stereo.
    ("external-xing-joint", ".mp3", 44100, 2, ["tompg", "-L3", "-B64", "-M1"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-xing-stereo", ".mp3", 48000, 2, ["tompg", "-L3", "-B56", "-M0"],
     dict(version="1", layer=3, sample_rate=48000, channel_mode="Stereo", bitrate=112, info_frame=False, signal="music")),
    ("external-xing-mono", ".mp3", 44100, 1, ["tompg", "-L3", "-B56", "-M3"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=56, info_frame=False, signal="music")),
    ("external-xing-mpeg2", ".mp3", 22050, 2, ["tompg", "-L3", "-B32", "-M1"],
     dict(version="2", layer=3, sample_rate=22050, channel_mode="Joint stereo", bitrate=64, info_frame=False, signal="music")),
    ("external-xing-layer2", ".mp2", 44100, 2, ["tompg", "-L2", "-B96", "-M0"],
     dict(version="1", layer=2, sample_rate=44100, channel_mode="Stereo", bitrate=192, info_frame=False, signal="music")),
    ("external-xing-layer1", ".mp1", 44100, 2, ["tompg", "-L1", "-B192", "-M0"],
     dict(version="1", layer=1, sample_rate=44100, channel_mode="Stereo", bitrate=384, info_frame=False, signal="music")),
    # BladeEnc codes plain stereo or mono, switches to short blocks, never uses scalefactor
    # scaling and leaves ancillary data at most in its last frame.
    ("external-blade-stereo", ".mp3", 44100, 2, ["bladeenc", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade-mono", ".mp3", 48000, 1, ["bladeenc"],
     dict(version="1", layer=3, sample_rate=48000, channel_mode="Mono", bitrate=64, info_frame=False, signal="music")),
    ("external-blade-crc", ".mp3", 32000, 2, ["bladeenc", "-96", "-crc"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Stereo", bitrate=96, protected=True, info_frame=False, signal="music")),
    ("external-blade-flags", ".mp3", 44100, 2, ["bladeenc", "-320", "-p", "-c", "-copy"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=320, info_frame=False, signal="music")),
    ("external-twolame-music", ".mp2", 48000, 2, ["twolame", "--quiet", "-b", "192", "-m", "s"],
     dict(version="1", layer=2, sample_rate=48000, channel_mode="Stereo", bitrate=192, info_frame=False, signal="music")),
    ("external-ffmpeg-mp2", ".mp2", 48000, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "mp2", "-b:a", "192k", "{out}"],
     dict(version="1", layer=2, sample_rate=48000, channel_mode="Stereo", bitrate=192, info_frame=False, signal="music")),
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
    shine = subprocess.run(["dpkg-query", "-W", "-f", "${Version}", "shineenc"], capture_output=True, text=True).stdout
    if not shine.startswith(SHINE_VERSION):
        sys.exit(f"shineenc {SHINE_VERSION} is required, found {shine or 'none'}")


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


def frame_length(header):
    """The length a frame header gives (ISO/IEC 11172-3 2.4.3.1), or None for free format."""
    mpeg1 = (header[1] >> 3) & 1
    layer = 4 - ((header[1] >> 1) & 3)
    rates = [44100, 48000, 32000] if mpeg1 else [22050, 24000, 16000]
    rate = rates[(header[2] >> 2) & 3] // (1 if (header[1] >> 4) & 1 else 2)
    if layer == 1:
        bitrates = ([0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448] if mpeg1
                    else [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256])
        index = header[2] >> 4
        return None if index == 0 else (12 * bitrates[index] * 1000 // rate + ((header[2] >> 1) & 1)) * 4
    if mpeg1 and layer == 2:
        bitrates = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384]
    elif mpeg1:
        bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
    else:
        bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
    index = header[2] >> 4
    if index == 0:
        return None
    coefficient = 72 if layer == 3 and not mpeg1 else 144
    return coefficient * bitrates[index] * 1000 // rate + ((header[2] >> 1) & 1)


def truncated_last(path, packets):
    """Whether the last frame ffprobe lists is shorter than its header says, as Shine 3.1.1 writes
    the last frame of a stereo stream two bytes short."""
    data = path.read_bytes()
    offset, length = packets[-1]
    expected = frame_length(data[offset : offset + 4])
    return expected is not None and length < expected


def layer1_findings(path, packets):
    """What breaks ISO/IEC 11172-3 in the Layer I audio data (2.4.1.5), read here independently of
    the analyzer: the forbidden allocation 15 and scalefactor index 63. libmad must reject the
    frames with allocation 15, which confirms the reading."""
    data = path.read_bytes()
    forbidden = undefined = 0
    for offset, length in packets:
        frame = data[offset : offset + length]
        bits = "".join(f"{b:08b}" for b in frame)
        position = 32 + (0 if frame[1] & 1 else 16)
        mode, extension = frame[3] >> 6, (frame[3] >> 4) & 3
        channels = 1 if mode == 3 else 2
        bound = (extension + 1) * 4 if mode == 1 else 32
        allocation = [[0] * 32 for _ in range(channels)]
        for sb in range(32):
            for ch in range(channels if sb < bound else 1):
                allocation[ch][sb] = int(bits[position : position + 4], 2)
                position += 4
            if sb >= bound and channels == 2:
                allocation[1][sb] = allocation[0][sb]
        scalefactors = []
        for sb in range(32):
            for ch in range(channels):
                if allocation[ch][sb]:
                    scalefactors.append(int(bits[position : position + 6], 2))
                    position += 6
        forbidden += any(15 in row for row in allocation)
        undefined += 63 in scalefactors
    rejected = "forbidden bit allocation value" in subprocess.run(["madplay", "-o", "null:/dev/null", str(path)], capture_output=True,
                                                                  text=True).stderr
    if rejected != (forbidden > 0):
        raise SystemExit(f"{path.name}: libmad {'rejects' if rejected else 'accepts'} the allocations, {forbidden} frames use 15")
    return (["mpeg.allocation-forbidden"] if forbidden else []) + (["mpeg.scalefactor-undefined"] if undefined else [])


SLEN1 = [0, 0, 0, 0, 3, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4]
SLEN2 = [0, 1, 2, 3, 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 2, 3]


def scalefactors_exceed(rows, channels):
    """Whether an MPEG-1 granule's scalefactors (part2, ISO/IEC 11172-3 2.4.2.7: slen1 and slen2
    from scalefac_compress, groups of 6, 5, 5 and 5 bands that scfsi reuses in granule 1, 18 or
    17 short-block bands of each) take more bits than its part2_3_length, from minimp3's reading.
    Helix 5.0 r11 writes such granules with a part2_3_length of 0, in its official Windows build
    as in the build here."""
    for row in rows:
        for i, g in enumerate(row.get("granules", [])):
            s1, s2 = SLEN1[g["scalefac_compress"]], SLEN2[g["scalefac_compress"]]
            if g["block_type"] == 2:
                part2 = (17 if g.get("mixed_block_flag") else 18) * s1 + 18 * s2
            else:
                scfsi = g.get("scfsi", 0) if i >= channels else 0
                part2 = sum(n * s for k, (n, s) in enumerate([(6, s1), (5, s1), (5, s2), (5, s2)]) if not scfsi >> (3 - k) & 1)
            if part2 > g["part2_3_length"]:
                return True
    return False


def float32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def minimp3_scalefactor(cls, index):
    """minimp3's dequantization step for a Layer II class and scalefactor index (g_deq_L12), in
    single precision as minimp3 prints it."""
    divisors = [3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9]
    bases = [float32(9.53674316e-07), float32(7.56931807e-07), float32(6.00777173e-07)]
    return float32(float32(bases[index % 3] / divisors[cls - 2]) * float32(1 << 21 >> (index // 3)))


def uses_index63(rows):
    """Whether minimp3 dequantizes any Layer II scalefactor with index 63, which Table B.1 leaves undefined."""
    return any(float.fromhex(sf) == minimp3_scalefactor(ch["class"], 63)
               for row in rows if "subbands" in row for band in row["subbands"] for ch in band
               if ch["class"] > 0 for sf in ch["scalefactors"])


def granules(rows):
    return [g for row in rows if "granules" in row for g in row["granules"]]


def detected_encoder(command, tag, settings, rows, path):
    """The encoder the analyzer must name, from what each encoder is known to write.

    LAME's own tag names it; ffmpeg's tag names ffmpeg, or copies the encoder string of the file it
    remuxes; without a LAME string in the tag, LAME is known from the "LAME3.100" it writes into
    ancillary data. Fraunhofer's encoder is known from its VBRI header (high confidence), the MP3
    Surround data in every frame or its OFL block (medium), and otherwise, with low confidence, from
    using scalefactor scaling and short blocks but never scfsi, as minimp3 reads the side information. Helix's tag
    names it in a LAME tag ("LAMEH5.24"); without the tag, the copyright and original bits it sets
    in every frame together with scfsi and scalefactor scaling point to it (low). Shine is known
    from its frames alone (low), and from them behind ffmpeg's tag (medium). TwoLAME and Xing's
    encoder both write scalefactor index 63 into Layer II, which names them together (low); ffmpeg's
    MP2 encoder leaves no trace. Xing's Layer III frames never switch to short blocks or use scfsi
    and all set the original bit (low). BladeEnc switches to short blocks without scalefactor
    scaling, codes plain stereo or mono, and leaves ancillary data at most in its last
    frame, as mp3guessenc counts it (low)."""
    if command[0] in ("twolame", "tompg") and settings["layer"] == 2:
        return {"name": "TwoLAME or Xing", "confidence": "Low"} if uses_index63(rows) else None
    if command[0] == "tompg":
        found = granules(rows)
        original = all(path.read_bytes()[offset + 3] & 4 for offset, _ in ffprobe_packets(path))
        if settings["layer"] == 3 and not any(g["block_type"] for g in found) and not any(g.get("scfsi") for g in found) and original:
            return {"name": "Xing", "confidence": "Low"}
        return None
    if command[0] == "bladeenc":
        found = granules(rows)
        ancillary = guessenc_summary(path)["ancillary"]
        if (any(g["block_type"] for g in found) and not any(g["scalefac_scale"] for g in found)
                and ancillary[0] == ancillary[2]):
            return {"name": "BladeEnc", "confidence": "Low"}
        return None
    if command[0] == "shineenc":
        return {"name": "Shine", "confidence": "Low"}
    if command[0] == "ffmpeg" and "libshine" in command:
        return {"name": "Shine", "confidence": "Medium"}
    if command[0] == "ffmpeg" and "mp2" in command:
        return None
    if command[0] == "tomp3":
        return {"name": "Helix MP3 Encoder", "confidence": "Low"}
    if command[0] == "hmp3":
        lame = tag.get("lame") if tag else None
        if lame is None:
            return {"name": "Helix MP3 Encoder", "confidence": "Low"}
        version = lame["encoder"][len("LAMEH"):]
        return {"name": f"Helix MP3 Encoder {version}", "confidence": "High" if lame["tag_crc"] == lame["tag_crc_computed"] else "Medium"}
    if command[0] == "mp3sEncoder":
        surround = settings.get("enhancement") == "MP3 Surround"
        name = "Fraunhofer MP3 Surround encoder" if surround else "Fraunhofer"
        if tag is not None and tag["type"] == "VBRI":
            return {"name": name, "confidence": "High"}
        if surround or "-ofl" in command:
            return {"name": name, "confidence": "Medium"}
        found = granules(rows)
        if (settings["version"] == "1" and any(g["scalefac_scale"] for g in found) and not any(g.get("scfsi") for g in found)
                and any(g["block_type"] for g in found)):
            return {"name": "Fraunhofer", "confidence": "Low"}
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
    manifest["detected_encoder"] = detected_encoder(command, tag, manifest["encoder"], manifest.get("minimp3_side_info", []), path)
    manifest["mp3guessenc_encoder_string"] = guessenc_tag_lines(path).get("Encoder string")


def encode(command, source, path, temp):
    if command[0] == "mp3sEncoder":
        fhg_tool.encode(FHG_ENCODER[0], command[1:], source, path)
        return
    if command[0] == "hmp3":
        helix_tool.encode(HELIX_ENCODER[0], command[1:], source, path)
        return
    if command[0] == "tomp3":
        helix_tool.encode_r11(HELIX_R11_ENCODER[0], command[1:], source, path)
        return
    if command[0] == "tompg":
        xing_tool.encode(XING_ENCODER[0], command[1:], source, path)
        return
    if command[0] == "bladeenc":
        blade_tool.encode(BLADE_ENCODER[0], command[1:], source, path)
        return
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
    signal = {"clicks": "0.5 s of a quiet 440/660 Hz tone with a click every 50 ms", "music": "2 s of tones, noise with clicks and a chord",
              "silence": "0.5 s of silence"}.get(settings.get("signal"), "0.5 s of a 440/660 Hz tone")
    if channels == 6:
        signal = f"0.5 s of a 5.1 tone of {'/'.join(map(str, SURROUND_FREQUENCIES))} Hz"
    manifest = {
        "description": f"{' '.join(command)} on {signal}, {rate} Hz, {channels} channels.",
        "crosscheck": True,
        "encoder": {"command": command, **settings},
    }
    if settings.get("free_format"):
        manifest["mpg123_frames"] = mpg123_frame_count(path, 1152, channels)
    else:
        manifest["ffprobe_packets"] = ffprobe_packets(path)
        # Findings beyond the VBR header's that the analyzer must report.
        manifest["findings"] = ["mpeg.frame-truncated"] if truncated_last(path, manifest["ffprobe_packets"]) else []
        if settings["layer"] == 1:
            manifest["findings"] += layer1_findings(path, manifest["ffprobe_packets"])
    if settings["layer"] in (2, 3):
        manifest["minimp3_side_info"] = minimp3_tool.side_info(dumper, path)
    if settings["layer"] == 3 and settings["version"] == "1" and scalefactors_exceed(manifest["minimp3_side_info"], channels):
        manifest.setdefault("findings", []).append("mpeg.scalefactors-exceed-granule")
    if settings["layer"] == 3:
        manifest["mp3guessenc"] = guessenc_summary(path)
    describe_tag(path, command, manifest)
    manifest["mp3guessenc_stream"] = guessenc_stream(path)
    manifest["mp3guessenc_guess"] = guessenc_guess(path)
    manifest["enhancement"] = settings.get("enhancement")
    ofl = guessenc_ofl(path)
    if ofl is not None:
        manifest["mp3guessenc_ofl"] = ofl
        manifest["decoded_onset"] = decoded_onset(path)
    if manifest["vbr_header"] and manifest["vbr_header"].get("lame"):
        manifest["decoded_samples"] = decoded_samples(path, CHANNELS.get(settings["channel_mode"], 2))
    text = json.dumps(manifest, indent=1, ensure_ascii=False) + "\n"
    (OUT / f"{name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


FHG_ENCODER = []
HELIX_ENCODER = []
HELIX_R11_ENCODER = []
XING_ENCODER = []
BLADE_ENCODER = []


def main():
    check_versions()
    OUT.mkdir(parents=True, exist_ok=True)
    dumper = minimp3_tool.build()
    FHG_ENCODER.append(fhg_tool.fetch())
    HELIX_ENCODER.append(helix_tool.build())
    HELIX_R11_ENCODER.append(helix_tool.build_r11())
    XING_ENCODER.append(xing_tool.fetch())
    BLADE_ENCODER.append(blade_tool.build())
    with tempfile.TemporaryDirectory() as temp:
        for name, extension, rate, channels, command, settings in FIXTURES:
            source = pathlib.Path(temp) / f"{name}.wav"
            write_tone(source, rate, channels, signal=settings.get("signal", "tone"))
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
