#!/usr/bin/env python3
"""Writes MPEG audio fixtures with real encoders: LAME 3.100, TwoLAME 0.4.0, Shine 3.1.1, ffmpeg 6.1 with
libmp3lame, libshine and its own MP2 encoder, the Helix MP3 Encoder 5.2.4 and 5.0 r11 (helix_tool.py),
Fraunhofer's MP3 Surround encoder 1.5 (fhg_tool.py), which also writes plain MP3, VBRI headers and
Original File Length (OFL) blocks, Xing's tompg 3.0 under Wine (xing_tool.py), the only external
source of Layer I here, BladeEnc 0.80 to 0.94.2 (blade_tool.py), LAME 3.90.1 to 3.94 beta 1, 3.98 beta 8, 3.99 alpha 12,
3.99.5, 3.101 and 4.0 built from source (old_lame_tool.py), the Windows builds of LAME 3.91 and HydrogenAudio's 3.90.3 under Wine
(lame_win_tool.py) and Gogo-no-coda 3.12 under Wine (gogo_tool.py). Besides a tone, some encode two seconds of tones, noise, clicks
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
frame of the untagged 400 kbps one ("bad audio data length": its frame 0 holds words that look like the next header), so the free-format
fixtures record the frame count mpg123 decodes instead.

Where the first frame holds a VBR header (LAME's Xing/Info and LAME tag, or the one ffmpeg's muxer
writes with its own encoder string), "vbr_header" records its raw values as vbr_tags.py reads them,
"vbr_header_fields" the fields the analyzer reports for it, and "vbr_findings" the findings it
causes; crosscheck_mpeg_fixtures.py confirms the raw values with mp3guessenc, mpg123 and ffprobe.
"detected_encoder" is the encoder the analyzer must name: LAME from its tag (high confidence when
the tag CRC holds) or, without a tag, from the "LAME3.100" it writes into ancillary data (low);
ffmpeg's tag names ffmpeg, so the LAME behind it is again known from ancillary data (medium);
TwoLAME leaves no trace. An ID3v2 TSSE frame, which LAME's front end and ffmpeg's muxer write,
names the encoder alone with low confidence and raises low-confidence evidence from the frames that
names the same encoder to medium. "mp3guessenc_encoder_string" is the encoder string mp3guessenc finds.
"detected_writer" is the writer the analyzer must name, present only where one is named: "FFmpeg" (low)
for an ID3v2 TSSE frame that is ffmpeg's muxer version (or "FFmpeg (Lavf...)" with it) and for a LAME tag laid out as ffmpeg's muxer
writes it (an encoder string of "Lavc..." or "Lavf...", or ffmpeg's tag CRC where LAME's would differ). It follows from the manifests' tags
and the committed files alone, so "--writers-only" recomputes it without any encoder, adding it where the rule names a writer and
removing it where it no longer does; CI's fixtures job runs that and fails on a difference.

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
the samples per channel ffmpeg and mpg123 output with their gapless trimming. "statistics" lists the
coding statistics the analyzer must report (channel modes, stereo coding, frame sizes, block usage,
global gain, ancillary data, padding, scfsi, scalefactor scaling and main_data_begin), computed by
stream_stats.py from the file's frames (file_frames), which does not need the encoders:

    python3 scripts/fixtures/make_external_mpeg_fixtures.py --statistics-only [name ...]

rewrites only that entry of the committed manifests.

For Layer III, the manifest also records the side information of every frame as minimp3 reads it
("minimp3_side_info", see minimp3_side_info.c), and the statistics mp3guessenc prints about the
stream ("mp3guessenc": block usage, stereo modes, global gain, the largest main_data_begin,
ancillary data, scalefactor scaling and scfsi), both from frames that are not the Info frame. For
Layer II, "minimp3_side_info" holds every frame's allocation, scfsi and scalefactors.

The output is committed. CI does not run this script, because the result depends on the encoder
versions; rerun it by hand when a fixture needs to change, naming the fixtures to write only those
(and build only the encoders they need):

    apt-get install lame twolame shineenc ffmpeg libmp3lame0 madplay mpg123 gcc-multilib wine p7zip-full
    python3 scripts/fixtures/make_external_mpeg_fixtures.py [name ...]
"""

import json
import math
import random
import pathlib
import re
import struct
import subprocess
import sys
import tempfile
import wave

import blade_tool
import fhg_tool
import gogo_tool
import lame_win_tool
import helix_tool
import minimp3_tool
import old_lame_tool
import stream_stats
import vbr_tags
import xing_tool
from crosscheck_mpeg_fixtures import (decoded_onset, decoded_samples, guessenc_guess, guessenc_ofl, guessenc_stream, guessenc_summary,
                                      guessenc_tag_lines)

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"
LAME_VERSION = "LAME 64bits version 3.100"
CHANNELS = {"Mono": 1}
TWOLAME_VERSION = "TwoLAME version 0.4.0"
# Provenance: the tool versions (Ubuntu 24.04) that wrote the committed files this script produced. The cross-check jobs run Ubuntu 26.04 and
# assert its versions (tool_versions.py); the committed files and goldens stay as written, so these strings stay as they are.
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
SHINE_VERSION = "3.1.1"


# Left, right, center, LFE, left surround, right surround.
SURROUND_FREQUENCIES = [440, 660, 550, 60, 330, 880]


def write_tone(path, rate, channels, seconds=0.5, signal="tone"):
    """16-bit PCM. "tone": a 440 Hz sine (the right channel at 660 Hz, 5.1 channels each at their
    own frequency). "clicks": a quiet sine with a loud click every 50 ms, whose transients make LAME
    switch to short blocks. "music": two seconds that give an encoder something to decide, tones,
    then noise with clicks, then a pause and a chord. "long-music": that, three times, in six seconds, with
    different content in each channel. "silence": all zeros."""
    loop = None
    if signal == "music":
        seconds = 2.0
    elif signal == "long-music":
        signal, seconds, loop = "music", 6.0, 2.0
    rng = random.Random(7)
    frames = int(rate * seconds)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(channels)
        out.setsampwidth(2)
        out.setframerate(rate)
        data = bytearray()
        for i in range(frames):
            t = i / rate if loop is None else (i / rate) % loop
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
    # 640 kbps is the highest rate LAME accepts for free format; the frames are about 2090 bytes.
    ("external-lame-free-format-640", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "--freeformat", "-b", "640", "-m", "s"],
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
    # With tags, LAME's front end writes an ID3v2 TSSE frame naming itself.
    ("external-lame-tagged", ".mp3", 44100, 2, ["lame", "--quiet", "--tt", "Encspect", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    ("external-lame-no-info-tagged", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "--tt", "Encspect", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False)),
    ("external-lame-forced-ms-tag", ".mp3", 44100, 2, ["lame", "--quiet", "-b", "192", "-m", "f", "--noreplaygain"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=192, info_frame=True)),
    ("external-ffmpeg-cbr", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True)),
    # Bit-exact: the stream's encoder metadata is "Lavc libmp3lame", which the muxer replaces with "Lavf lame" in the LAME tag it writes
    # (mp3enc.c:250-252), so the tag names the muxer and the encoder is told by LAME's own signature in the frames.
    ("external-ffmpeg-bitexact", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-fflags", "+bitexact", "-flags:a", "+bitexact", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"],
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
    # A mono LAME file remuxed by a bit-exact ffmpeg: its tag keeps the encoder string "LAME3.100" but gets ffmpeg's CRC over the frame's first 190
    # bytes, which for mono (a 175-byte range in LAME's) differs from LAME's: the tag is laid out as ffmpeg's muxer writes it, whatever its string says.
    ("external-ffmpeg-remux-lame-mono", ".mp3", 44100, 1, ["ffmpeg", "-v", "error", "-y", "-i", "{lame -m m -b 64}", "-fflags", "+bitexact", "-c", "copy", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Mono", bitrate=64, info_frame=True)),
    # A BladeEnc fixture remuxed by ffmpeg, which writes a LAME tag with "Lavf" when it knows no
    # encoder: a remux names none, so the frames still point to BladeEnc.
    ("external-ffmpeg-remux-blade", ".mp3", 44100, 2, ["ffmpeg", "-v", "error", "-y", "-i", "{fixture external-blade-stereo.mp3}", "-c", "copy", "{out}"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=True, signal="music")),
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
    # One encode per older release that writes other bytes. 0.80 and 0.81 drain the bit reservoir
    # into ancillary data mid-stream, as the ISO dist10 encoder does, so neither the analyzer nor
    # mp3guessenc names the encoder of this one.
    ("external-blade080-stereo", ".mp3", 44100, 2, ["bladeenc-0.80", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade082-stereo", ".mp3", 44100, 2, ["bladeenc-0.82", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade084-stereo", ".mp3", 44100, 2, ["bladeenc-0.84", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade0920-stereo", ".mp3", 44100, 2, ["bladeenc-0.92.0", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade0927-stereo", ".mp3", 44100, 2, ["bladeenc-0.92.7", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-blade0936-stereo", ".mp3", 44100, 2, ["bladeenc-0.93.6", "-128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="music")),
    # LAME 3.90 to 3.92 have Dibrom's --alt-presets but no preset field in the tag; 3.93 added it.
    ("external-lame390-alt-standard", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "standard"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame390-alt-extreme", ".mp3", 48000, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "extreme"],
     dict(version="1", layer=3, sample_rate=48000, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame390-alt-insane", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "insane"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=320, info_frame=True, signal="music")),
    ("external-lame390-alt-abr", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame391-cbr", ".mp3", 44100, 2, ["lame-3.91", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame392-alt-fast-standard", ".mp3", 44100, 2, ["lame-3.92", "--quiet", "--alt-preset", "fast", "standard"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame393-alt-standard", ".mp3", 44100, 2, ["lame-3.93.1", "--quiet", "--alt-preset", "standard"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    # HydrogenAudio's 3.90.3 and the Windows build of 3.91 write other preset settings than 3.90 and 3.92;
    # 3.90.3's version does not fit the tag, which holds "LAME3.90.", and stands whole in the padding.
    ("external-lame3903-alt-standard", ".mp3", 44100, 2, ["lame-win-3.90.3", "--quiet", "--alt-preset", "standard"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame3903-alt-extreme", ".mp3", 44100, 2, ["lame-win-3.90.3", "--quiet", "--alt-preset", "extreme"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame3903-alt-insane", ".mp3", 44100, 2, ["lame-win-3.90.3", "--quiet", "--alt-preset", "insane"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=320, info_frame=True, signal="music")),
    ("external-lame391w-alt-extreme", ".mp3", 44100, 2, ["lame-win-3.91", "--quiet", "--alt-preset", "extreme"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame390-r3mix", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--r3mix"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame390-preset-cd", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "cd"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=192, info_frame=True, signal="music")),
    # The other presets of LAME 3.90 to 3.92 whose tag settings differ from the encodes above; the analyzer names a
    # preset from these settings, and the Windows build of 3.91 writes some lowpasses 100 Hz higher
    # (docs/formats/mpeg.md).
    ("external-lame390-alt-fast-extreme", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "fast", "extreme"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame390-alt-cbr", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--alt-preset", "cbr", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame390-preset-studio", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "studio"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=256, info_frame=True, signal="music")),
    ("external-lame390-preset-hifi", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "hifi"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=160, info_frame=True, signal="music")),
    ("external-lame390-preset-tape", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "tape"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame390-preset-radio", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "radio"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame390-preset-fm", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "fm"],
     dict(version="1", layer=3, sample_rate=32000, channel_mode="Joint stereo", bitrate=112, info_frame=True, signal="music")),
    ("external-lame390-preset-mw-us", ".mp3", 44100, 2, ["lame-3.90.1", "--quiet", "--preset", "mw-us"],
     dict(version="2", layer=3, sample_rate=16000, channel_mode="Mono", bitrate=40, info_frame=True, signal="music")),
    ("external-lame391w-alt-fast-extreme", ".mp3", 44100, 2, ["lame-win-3.91", "--quiet", "--alt-preset", "fast", "extreme"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame391w-r3mix", ".mp3", 44100, 2, ["lame-win-3.91", "--quiet", "--r3mix"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame391w-preset-mw-us", ".mp3", 44100, 2, ["lame-win-3.91", "--quiet", "--preset", "mw-us"],
     dict(version="2", layer=3, sample_rate=16000, channel_mode="Mono", bitrate=40, info_frame=True, signal="music")),
    # LAME 3.94 beta 1 computes ReplayGain against 83 dB.
    ("external-lame394b-vbr", ".mp3", 44100, 2, ["lame-3.94b1", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame394b-no-info", ".mp3", 44100, 2, ["lame-3.94b1", "--quiet", "-t", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    # LAME writes its version into the tag ("LAME3.98b", "L3.99a12", "LAME3.99r") and whole into the
    # padding it drains into ancillary data ("3.98 (beta)", "3.99 (alpha 12)", "3.99.5").
    ("external-lame398b-vbr", ".mp3", 44100, 2, ["lame-3.98b8", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame398b-cbr", ".mp3", 44100, 2, ["lame-3.98b8", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame399a-vbr", ".mp3", 44100, 2, ["lame-3.99a12", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame399a-cbr", ".mp3", 44100, 2, ["lame-3.99a12", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame3995-vbr", ".mp3", 44100, 2, ["lame-3.99.5", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame3995-cbr", ".mp3", 44100, 2, ["lame-3.99.5", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    # LAME 3.101 writes "LAME3.101", with no room left for the release marker; 4.0 bumped the
    # major version to make room and writes "LAME4.0 " and a NUL byte.
    ("external-lame3101-vbr", ".mp3", 44100, 2, ["lame-3.101", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame3101-cbr", ".mp3", 44100, 2, ["lame-3.101", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    ("external-lame40-vbr", ".mp3", 44100, 2, ["lame-4.0", "--quiet", "-V", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-lame40-cbr", ".mp3", 44100, 2, ["lame-4.0", "--quiet", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=True, signal="music")),
    # Gogo fills unused bits with "GOGO" and writes it where the Xing header's quality belongs.
    ("external-gogo-cbr", ".mp3", 44100, 2, ["gogo", "-b", "128"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="music")),
    ("external-gogo-vbr", ".mp3", 44100, 2, ["gogo", "-v", "2"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=None, info_frame=True, signal="music")),
    ("external-gogo-mpeg2-mono", ".mp3", 22050, 1, ["gogo", "-b", "64", "-m", "m"],
     dict(version="2", layer=3, sample_rate=22050, channel_mode="Mono", bitrate=64, info_frame=False, signal="music")),
    # Six seconds of different content in each channel, from LAME at one constant bitrate in dual channel, joint stereo and left/right
    # stereo, and from TwoLAME in dual channel, for the Channel Bit Share series; the goldens leave the frames out.
    ("external-lame-dual-music", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "d"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Dual channel", bitrate=128, info_frame=False, signal="long-music", golden_hides=["frames"])),
    ("external-lame-joint-music", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "j"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Joint stereo", bitrate=128, info_frame=False, signal="long-music", golden_hides=["frames"])),
    ("external-lame-stereo-music", ".mp3", 44100, 2, ["lame", "--quiet", "-t", "-b", "128", "-m", "s"],
     dict(version="1", layer=3, sample_rate=44100, channel_mode="Stereo", bitrate=128, info_frame=False, signal="long-music", golden_hides=["frames"])),
    ("external-twolame-dual-music", ".mp2", 44100, 2, ["twolame", "--quiet", "-b", "192", "-m", "d"],
     dict(version="1", layer=2, sample_rate=44100, channel_mode="Dual channel", bitrate=192, info_frame=False, signal="long-music", golden_hides=["frames"])),
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


def file_frames(path, manifest):
    """The frames of an encoder-written file as stream_stats.FrameInfo: from the first frame after an
    ID3v2 tag, each where the one before ends, until no header of the stream follows, before an
    ID3v1 tag. A last frame longer than the data left is truncated; the first frame holds the VBR
    header the manifest records. A free-format frame is as long as the first plus its padding. The
    frames must be the ones ffprobe listed, or as many as mpg123 decoded."""
    data = path.read_bytes()
    end = len(data) - (128 if data[-128:-125] == b"TAG" else 0)
    offset, first_length = first_frame(data)
    free = first_length - ((data[offset + 2] >> 1) & 1)
    stream = data[offset + 1], data[offset + 2] & 0x0C
    frames = []
    while offset + 4 <= end and data[offset] == 0xFF and (data[offset + 1], data[offset + 2] & 0x0C) == stream:
        header = data[offset : offset + 4]
        length = frame_length(header)
        if length is None:
            length = free + (((header[2] >> 1) & 1) * (4 if (header[1] >> 1) & 3 == 3 else 1))
        available = min(length, end - offset)
        coding = stream_stats.read_frame(data, offset, available)
        bitrate = stream_stats.BITRATES[("1" if coding["version"] == "MPEG-1" else "2", coding["layer"])][coding["bitrate_index"]]
        frames.append(stream_stats.FrameInfo(offset, available, coding["version"], ["I", "II", "III"][coding["layer"] - 1], coding["rate"], bitrate,
                                             header=manifest["vbr_header"] if not frames and manifest.get("vbr_header") else None,
                                             truncated=available < length, coding=coding))
        offset += length
    audio = [[f.offset, f.length] for f in frames if f.header is None]
    if "ffprobe_packets" in manifest:
        assert audio == manifest["ffprobe_packets"], (path.name, audio[:3], manifest["ffprobe_packets"][:3])
    else:
        assert len(audio) == manifest["mpg123_frames"], (path.name, len(audio), manifest["mpg123_frames"])
    return frames


def coding_statistics(path, manifest):
    """The coding statistics of the file's single MPEG Audio region (stream_stats.coding_fields)."""
    frames = file_frames(path, manifest)
    audio = [f for f in frames if f.is_audio]
    return stream_stats.coding_fields(frames, audio, sum(f.ticks for f in audio))


def truncated_last(path, packets):
    """Whether the last frame ffprobe lists is shorter than its header says, as Shine 3.1.1 writes
    the last frame of a stereo stream two bytes short."""
    data = path.read_bytes()
    offset, length = packets[-1]
    expected = frame_length(data[offset : offset + 4])
    return expected is not None and length < expected


def main_data_findings(path, packets, rows):
    """Where Layer III main data does not fit the bit reservoir (ISO/IEC 11172-3 2.4.2.7): main data
    that starts before the stream, inside the previous frame's (overlap) or runs past the end of its
    own frame (overflow), as BladeEnc 0.80 and 0.81 write it; and main_data_begin beyond the decoder
    buffer, which holds one frame at bitrate_index 14 at the stream's sample rate, as LAME writes it
    at high bitrates since 3.81. main_data_begin and part2_3_length are read here, since minimp3
    rejects the side information of some of those frames. The frames lie back to back: ffprobe lists
    each but a Xing or Info frame, which minimp3 lists and which takes its place in the reservoir
    without main data; a truncated last frame cannot overflow."""
    data = path.read_bytes()
    sizes = dict(map(tuple, packets))
    offsets = sorted(set(sizes) | {row["offset"] for row in rows})
    findings = []
    position = previous_end = 0
    for k, offset in enumerate(offsets):
        header = data[offset : offset + 4]
        size = sizes.get(offset, offsets[k + 1] - offset if k + 1 < len(offsets) else 0)
        main_data_begin, part2_3, side = side_info_lengths(data, offset)
        area = size - 4 - (0 if header[1] & 1 else 2) - side
        start = position
        if offset in sizes:
            if main_data_begin > start:
                findings.append("mpeg.main-data-before-stream")
            elif main_data_begin > start - previous_end:
                findings.append("mpeg.main-data-overlap")
            top = frame_length(header[:2] + bytes([(header[2] & 0x0D) | 0xE0]) + header[3:])
            # ISO/IEC 11172-3's decoder buffer, checked for MPEG-1 only (version bits 11).
            if (header[1] >> 3) & 3 == 3 and 0 < main_data_begin and main_data_begin + frame_length(header) > top:
                findings.append("mpeg.main-data-begin-exceeds-buffer")
        end = start - main_data_begin + (part2_3 + 7) // 8
        truncated = k == len(offsets) - 1 and truncated_last(path, packets)
        if offset in sizes and end > start + area and not truncated:
            findings.append("mpeg.main-data-overflow")
        previous_end = max(previous_end, min(end, start + area)) if offset in sizes else previous_end
        position = start + area
    return sorted(set(findings))


def last_frame_ancillary(path, packets):
    """The bytes after the last frame's main data, rounded up to a byte, to the end of its data."""
    data = path.read_bytes()
    offset, size = packets[-1]
    main_data_begin, part2_3, side = side_info_lengths(data, offset)
    area = size - 4 - (0 if data[offset + 1] & 1 else 2) - side
    return max(area - ((part2_3 + 7) // 8 - main_data_begin), 0)


def side_info_lengths(data, offset):
    """main_data_begin, the sum of part2_3_length and the side information's size of the Layer III
    frame at offset (ISO/IEC 11172-3 2.4.1.7, 13818-3 2.4.1.7)."""
    header = data[offset : offset + 4]
    mpeg1 = (header[1] >> 3) & 3 == 3
    channels = 1 if header[3] >> 6 == 3 else 2
    size = (17 if channels == 1 else 32) if mpeg1 else (9 if channels == 1 else 17)
    start = offset + 4 + (0 if header[1] & 1 else 2)
    bits = int.from_bytes(data[start : start + size], "big")
    position = 0

    def read(count):
        nonlocal position
        position += count
        return (bits >> (size * 8 - position)) & ((1 << count) - 1)

    main_data_begin = read(9 if mpeg1 else 8)
    read((5 if channels == 1 else 3) if mpeg1 else channels)
    read(4 * channels if mpeg1 else 0)
    total = 0
    for _ in range((2 if mpeg1 else 1) * channels):
        total += read(12)
        read(47 if mpeg1 else 51)
    return main_data_begin, total, size


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
    frame, as mp3guessenc counts it (low). Old LAME releases are named by their tag as LAME 3.100
    is, with the version spelled out ("LAME 3.98 (beta)", "LAME 3.99.x") and completed from the one
    mp3guessenc finds in ancillary data where the two agree, or without a tag by that one (low). Gogo's
    "GOGO", which mp3guessenc finds, names it: with its Xing header, which holds "GOGO" as the
    quality, medium, from ancillary data alone low."""
    if command[0] == "gogo":
        if guessenc_tag_lines(path).get("Encoder string") != "GOGO":
            return None
        return {"name": "Gogo", "confidence": "Medium" if tag is not None and tag.get("quality") == int.from_bytes(b"GOGO", "big") else "Low"}
    if command[0].startswith("lame-"):
        lame = tag.get("lame") if tag else None
        string = guessenc_tag_lines(path).get("Encoder string")
        if lame is None:
            return {"name": vbr_tags.lame_name(string), "confidence": "Low"} if string and string.startswith("LAME") else None
        # A version cut off in the tag ("LAME3.90.", "LAME3.99r") stands whole in the padding, where mp3guessenc finds it.
        name, _, _ = vbr_tags.tag_and_ancillary_name(lame["encoder"], string)
        return {"name": name, "confidence": "High" if lame["tag_crc"] == lame["tag_crc_computed"] else "Medium"}
    if command[0] in ("twolame", "tompg") and settings["layer"] == 2:
        return {"name": "TwoLAME or Xing", "confidence": "Low"} if uses_index63(rows) else None
    if command[0] == "tompg":
        found = granules(rows)
        original = all(path.read_bytes()[offset + 3] & 4 for offset, _ in ffprobe_packets(path))
        if settings["layer"] == 3 and not any(g["block_type"] for g in found) and not any(g.get("scfsi") for g in found) and original:
            return {"name": "Xing", "confidence": "Low"}
        return None
    if command[0].startswith("bladeenc"):
        found = granules(rows)
        # mp3guessenc's total ancillary bytes must all be the last frame's (BladeEnc 0.80 and 0.81
        # drain the reservoir into one segment mid-stream).
        ancillary = guessenc_summary(path)["ancillary"]
        if (any(g["block_type"] for g in found) and not any(g["scalefac_scale"] for g in found)
                and ancillary[0] == last_frame_ancillary(path, ffprobe_packets(path))):
            return {"name": "BladeEnc", "confidence": "Low"}
        return None
    if command[0] == "shineenc":
        return {"name": "Shine", "confidence": "Low"}
    if command[0] == "ffmpeg" and "libshine" in command:
        return {"name": "Shine", "confidence": "Medium"}
    if command[0] == "ffmpeg" and "mp2" in command:
        return None
    remuxed = next((a[len("{fixture ") : -1] for a in command if a.startswith("{fixture ")), None)
    if remuxed is not None:
        # ffmpeg writes "Lavf" for a stream whose encoder it does not know, which names none.
        assert tag["lame"]["encoder"].startswith("Lavf")
        return json.loads((OUT / f"{pathlib.Path(remuxed).stem}.expected.json").read_text(encoding="utf-8"))["detected_encoder"]
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
    if lame["encoder"].startswith("Lavf"):
        # The muxer wrote "Lavf lame" in place of the bit-exact "Lavc libmp3lame" (mp3enc.c:250-252): it names no encoder and
        # supports none, so LAME's own signature in the frames stands alone.
        return {"name": "LAME 3.100", "confidence": "Low"}
    if not lame["encoder"].startswith("LAME"):
        return {"name": "LAME 3.100", "confidence": "Medium"}
    return {"name": "LAME 3.100", "confidence": "High" if lame["tag_crc"] == lame["tag_crc_computed"] else "Medium"}


def tsse(path):
    """The text of the TSSE frame in the ID3v2.3 or 2.4 tag at the start of the file, or None."""
    data = path.read_bytes()
    if data[:3] != b"ID3" or data[3] not in (3, 4):
        return None
    end = 10 + ((data[6] & 127) << 21 | (data[7] & 127) << 14 | (data[8] & 127) << 7 | (data[9] & 127))
    position = 10
    while position + 10 <= end and data[position] != 0:
        size = int.from_bytes(data[position + 4 : position + 8], "big")
        if data[3] == 4:
            size = (size >> 3 & 0xFE00000) | (size >> 2 & 0x1FC000) | (size >> 1 & 0x3F80) | (size & 0x7F)
        if data[position : position + 4] == b"TSSE":
            body = data[position + 10 : position + 10 + size]
            encoding = {0: "latin-1", 1: "utf-16", 2: "utf-16-be", 3: "utf-8"}[body[0]]
            return body[1:].decode(encoding).rstrip("\x00").strip()
        position += 10 + size
    return None


def weigh_tsse(encoder, claim):
    """The encoder the analyzer must name once the TSSE claim is weighed: alone it names the encoder
    (LAME's front end as "LAME" and its version, ffmpeg's version as the muxer, low confidence);
    agreeing with low-confidence evidence from the frames it raises that to medium."""
    if claim is None:
        return encoder
    if encoder is None:
        # ffmpeg's muxer writes its own version whatever encoded the stream, which names no encoder.
        if claim.startswith(("Lavf", "Lavc")):
            return None
        lame = re.search(r"(?<![a-z])LAME(?![a-z])(?:\s*\d+bits)?(?:\s*version)?\s*(\d+\.\d+[\w.]*)", claim, re.IGNORECASE)
        return {"name": f"LAME {lame.group(1)}" if lame else claim, "confidence": "Low"}
    if encoder["confidence"] == "Low" and re.search(r"(?<![a-z])lame(?![a-z])", claim.lower()) and encoder["name"].startswith("LAME"):
        return {**encoder, "confidence": "Medium"}
    return encoder


def detected_writer(tag, path):
    """The writer the analyzer must name, by a rule of its own: a TSSE frame of ffmpeg's muxer ("Lavf60.16.100") names it with its version; without
    one, a LAME tag laid out as ffmpeg's mp3 muxer writes it (libavformat/mp3enc.c writes the whole tag) names "FFmpeg": an encoder string of "Lavc"
    or "Lavf", or a tag CRC that is not LAME's but is the one over the frame's first 190 bytes. Both are low confidence; None names no writer."""
    text = tsse(path)
    if text and text.startswith("Lavf"):
        return {"name": f"FFmpeg ({text})", "confidence": "Low"}
    lame = tag.get("lame") if tag else None
    if lame and (lame["encoder"].startswith(("Lavc", "Lavf")) or (lame["tag_crc"] != lame["tag_crc_computed"] and lame["tag_crc"] == lame["tag_crc_ffmpeg"])):
        return {"name": "FFmpeg", "confidence": "Low"}
    return None


def add_writers():
    """Adds "detected_writer" to the manifests of the committed files whose writer is named, from the files and the tags the manifests hold, with no encoder."""
    for path in sorted(OUT.glob("*.expected.json")):
        manifest = json.loads(path.read_text(encoding="utf-8"))
        data = path.with_name(path.name[: -len(".expected.json")] + ".mp3")
        if "vbr_header" not in manifest or "encoder" not in manifest or not data.exists():
            continue
        writer = detected_writer(manifest["vbr_header"], data)
        if manifest.get("detected_writer") != writer:
            ordered = {}
            for key, value in manifest.items():
                if key != "detected_writer":
                    ordered[key] = value
                if key == "mp3guessenc_encoder_string" and writer is not None:
                    ordered["detected_writer"] = writer
            if writer is not None:
                ordered.setdefault("detected_writer", writer)
            path.write_text(json.dumps(ordered, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def describe_tag(path, command, manifest):
    data = path.read_bytes()
    start, length = first_frame(data)
    end = len(data) - (128 if data[-128:-125] == b"TAG" else 0)
    tag = vbr_tags.read(data[start : start + length], data[start + length : end])
    if tag is not None:
        tag["frame_offset"] = start
        manifest["vbr_header_fields"] = vbr_tags.report_fields(tag)
        manifest["vbr_findings"] = [{"code": code, "contains": contains} for code, contains in vbr_tags.findings(tag, start)]
        # ffprobe lists the frames after the header; LAME 3.94 beta 1 counts the header too.
        packets = manifest.get("ffprobe_packets")
        if tag.get("lame") and packets and tag.get("frames") == len(packets) + 1:
            samples = 1152 if manifest["encoder"]["version"] == "1" else 576
            manifest["vbr_findings"].append({"code": "mpeg.vbr-frame-count-includes-header", "contains": f"play {samples} samples more"})
        if tag.get("lame") and vbr_tags.tag_and_ancillary_name(tag["lame"]["encoder"], guessenc_tag_lines(path).get("Encoder string"))[1]:
            manifest["vbr_findings"].append({"code": "mpeg.lame-version-differs", "contains": "the version LAME wrote into the ancillary data"})
    manifest["vbr_header"] = tag
    manifest["detected_encoder"] = weigh_tsse(detected_encoder(command, tag, manifest["encoder"], manifest.get("minimp3_side_info", []), path), tsse(path))
    manifest["mp3guessenc_encoder_string"] = guessenc_tag_lines(path).get("Encoder string")
    if (writer := detected_writer(tag, path)) is not None:
        manifest["detected_writer"] = writer


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
    if command[0].startswith("bladeenc"):
        version = command[0][len("bladeenc-"):] or "0.94.2"
        blade_tool.encode(BLADE_ENCODERS[version], version, command[1:], source, path)
        return
    if command[0] == "gogo":
        gogo_tool.encode(GOGO_ENCODER[0], command[1:], source, path)
        return
    if command[0].startswith("lame-win-"):
        lame_win_tool.encode(WIN_LAME[command[0][len("lame-win-"):]], command[1:], source, path)
        return
    if command[0].startswith("lame-"):
        subprocess.run([str(OLD_LAME[command[0][len("lame-"):]]), *command[1:], str(source), str(path)], check=True)
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
        elif argument.startswith("{fixture "):
            argument = str(OUT / argument[len("{fixture ") : -1])
        elif argument.startswith("{lame "):
            encoded = pathlib.Path(temp) / f"{path.stem}.lame.mp3"
            subprocess.run(["lame", "--quiet", *argument[1:-1].split()[1:], str(source), str(encoded)], check=True)
            argument = str(encoded)
        arguments.append(argument)
    subprocess.run(arguments, check=True)


def write_manifest(name, path, rate, channels, command, settings, dumper):
    signal = {"clicks": "0.5 s of a quiet 440/660 Hz tone with a click every 50 ms", "music": "2 s of tones, noise with clicks and a chord",
              "long-music": "6 s of tones, noise with clicks and a chord, three times, different in each channel", "silence": "0.5 s of silence"}.get(settings.get("signal"), "0.5 s of a 440/660 Hz tone")
    if channels == 6:
        signal = f"0.5 s of a 5.1 tone of {'/'.join(map(str, SURROUND_FREQUENCIES))} Hz"
    manifest = {
        "description": f"{' '.join(command)} on {signal}, {rate} Hz, {channels} channels.",
        "crosscheck": True,
        "encoder": {"command": command, **{k: v for k, v in settings.items() if k != "golden_hides"}},
    }
    if settings.get("golden_hides"):
        manifest["golden_hides"] = settings["golden_hides"]
    if settings.get("free_format"):
        manifest["mpg123_frames"] = mpg123_frame_count(path, 1152, channels)
    else:
        manifest["ffprobe_packets"] = ffprobe_packets(path)
        # Findings beyond the VBR header's that the analyzer must report.
        manifest["findings"] = ["mpeg.frame-truncated"] if truncated_last(path, manifest["ffprobe_packets"]) else []
        if settings["layer"] == 1:
            manifest["findings"] += layer1_findings(path, manifest["ffprobe_packets"])
    if "+bitexact" in command and "findings" in manifest:
        # A bit-exact ffmpeg writes no encoder tag, so the ID3v2 tag it still writes holds no frame.
        manifest["findings"].append("id3v2.no-frames")
    if settings["layer"] in (2, 3):
        manifest["minimp3_side_info"] = minimp3_tool.side_info(dumper, path)
    if settings["layer"] == 3 and settings["version"] == "1" and scalefactors_exceed(manifest["minimp3_side_info"], channels):
        manifest.setdefault("findings", []).append("mpeg.scalefactors-exceed-granule")
    if settings["layer"] == 3 and "ffprobe_packets" in manifest:
        manifest["findings"] += main_data_findings(path, manifest["ffprobe_packets"], manifest["minimp3_side_info"])
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
    manifest["statistics"] = coding_statistics(path, manifest)
    text = json.dumps(manifest, indent=1, ensure_ascii=False) + "\n"
    (OUT / f"{name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


FHG_ENCODER = []
HELIX_ENCODER = []
HELIX_R11_ENCODER = []
XING_ENCODER = []
BLADE_ENCODERS = {}
GOGO_ENCODER = []
OLD_LAME = {}
WIN_LAME = {}


def write_statistics(names):
    """Rewrites the "statistics" of the committed manifests, or of the named ones, from their files."""
    for name, extension, *_ in [f for f in FIXTURES if not names or f[0] in names] + [(n, ".mp3") for n, _ in NOGAP if not names or n in names]:
        manifest_path = OUT / f"{name}.expected.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["statistics"] = coding_statistics(OUT / f"{name}{extension}", manifest)
        manifest_path.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def main(names):
    """Writes every fixture, or only the named ones."""
    check_versions()
    OUT.mkdir(parents=True, exist_ok=True)
    known = {f[0] for f in FIXTURES} | {name for name, _ in NOGAP}
    if set(names) - known:
        sys.exit(f"Unknown fixtures: {', '.join(sorted(set(names) - known))}")
    fixtures = [f for f in FIXTURES if not names or f[0] in names]
    nogap = not names or any(name in names for name, _ in NOGAP)
    heads = {command[0] for _, _, _, _, command, _ in fixtures}
    dumper = minimp3_tool.build()
    if "mp3sEncoder" in heads:
        FHG_ENCODER.append(fhg_tool.fetch())
    if "hmp3" in heads:
        HELIX_ENCODER.append(helix_tool.build())
    if "tomp3" in heads:
        HELIX_R11_ENCODER.append(helix_tool.build_r11())
    if "tompg" in heads:
        XING_ENCODER.append(xing_tool.fetch())
    for version in blade_tool.RELEASES:
        if ("bladeenc-" + version if version != "0.94.2" else "bladeenc") in heads:
            BLADE_ENCODERS[version] = blade_tool.build(version)
    if "gogo" in heads:
        GOGO_ENCODER.append(gogo_tool.fetch())
    for version in ("3.90.1", "3.91", "3.92", "3.93.1", "3.94b1", "3.98b8", "3.99a12", "3.99.5", "3.101", "4.0"):
        if "lame-" + version in heads:
            OLD_LAME[version] = old_lame_tool.build(version)
    for version in lame_win_tool.BUILDS:
        if "lame-win-" + version in heads:
            WIN_LAME[version] = lame_win_tool.fetch(version)
    with tempfile.TemporaryDirectory() as temp:
        for name, extension, rate, channels, command, settings in fixtures:
            source = pathlib.Path(temp) / f"{name}.wav"
            write_tone(source, rate, channels, signal=settings.get("signal", "tone"))
            path = OUT / f"{name}{extension}"
            encode(command, source, path, temp)
            write_manifest(name, path, rate, channels, command, settings, dumper)

        sources = []
        for name, rate in NOGAP if nogap else []:
            sources.append(pathlib.Path(temp) / f"{name}.wav")
            write_tone(sources[-1], rate, 2)
        if nogap:
            directory = pathlib.Path(temp) / "nogap"
            directory.mkdir()
            subprocess.run(NOGAP_COMMAND[:3] + ["--nogapout", str(directory)] + NOGAP_COMMAND[3:] + [str(s) for s in sources], check=True)
            for (name, rate), source in zip(NOGAP, sources):
                path = OUT / f"{name}.mp3"
                (directory / f"{name}.mp3").replace(path)
                write_manifest(name, path, rate, 2, NOGAP_COMMAND, NOGAP_SETTINGS, dumper)
    print(f"Wrote {len(fixtures) + (len(NOGAP) if nogap else 0)} external fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    if sys.argv[1:2] == ["--writers-only"]:
        add_writers()
    elif sys.argv[1:2] == ["--statistics-only"]:
        write_statistics(sys.argv[2:])
    else:
        main(sys.argv[1:])
