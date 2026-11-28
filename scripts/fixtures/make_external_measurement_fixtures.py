#!/usr/bin/env python3
"""Writes the lossy-derived measurement fixtures in tests/fixtures/measurement (lossy-*.flac) and their manifests (*.expected.json).

A pinned input (generated here: 1 s of mono pink noise with a 330 Hz tone at -20 dBFS RMS, 16 bits, from fixed generators) is encoded with
LAME, oggenc, FFmpeg's aac encoder and opusenc, decoded again with the encoder's own decoder where it has one (lame --decode, oggdec, opusdec;
FFmpeg for aac), and written as a FLAC file by flac -8: a lossless file made from a lossy encode, which is the use case of the spectral cutoff
(docs/design/spectral-analysis.md, "Lossless files from lossy encodes"). Most files are 1 s of mono, for the size of the repository; the stereo ones (`lossy-stereo-*`, 12 files) are 2 s of pink noise with a side signal 12 dB under the mid or as loud
as it (the mid/side spectra and the holes). Some files are not lossy encodes alone: the resampled ones (`resampled-ffmpeg-*`, the input resampled by FFmpeg),
and `lossy-lame-b64-then-source` (2 s: the first second encoded, the second the input). LAME -m j at -b 64 decodes at 24,000 Hz (LAME's own resampling), not at the input's rate.

The files are committed because the encoders' output depends on their versions (LAME 3.100, vorbis-tools 1.4.2 with libvorbis, FFmpeg 6.1.1
of Ubuntu 24.04, opus-tools 0.2 with libopus 1.4, flac 1.4.3: the tools that wrote the committed files; CI's Ubuntu 26.04 has newer ones and
does not run them). `--manifests-only` recomputes the manifests from the committed files without the encoders, which is what CI runs:

    python3 scripts/fixtures/make_external_measurement_fixtures.py [--manifests-only] [name ...]

Each manifest has the model's figures (measurement_model.py, worked out from the FLAC file), the exact commands, and what the encoder states about
its lowpass: for LAME, the Lowpass byte of its tag and the transition band that `lame --verbose` prints. For oggenc, FFmpeg's aac and opus no
lowpass is stated here (no source was read for it). "expect_cutoff" is what the file must give: a cutoff where the encoder applies a lowpass,
none for LAME -V 0 and oggenc -q 8, which leave none in these files.
"""

import json
import math
import os
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

import measurement_model as mm
import r8brain_tool
import tool_versions
from make_measurement_fixtures import NYQUIST_SOURCES, Lcg, nyquist_source, pink, quantize

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "measurement"

TOOLS = "LAME 3.100, vorbis-tools 1.4.2 (oggenc, oggdec), FFmpeg 6.1.1-3ubuntu5 (aac), opus-tools 0.2 with libopus 1.4 (opusenc, opusdec), flac 1.4.3 (Ubuntu 24.04)"

# The files of TwoLAME 0.4.0 name it too, and FFmpeg 6.1.1-3ubuntu5 as the decoder of its streams, which no other file used.
# The Nyquist edge files (Step 10) name what made them: the converter (and the compiler of r8brain's front end) and the FLAC writer.
NYQUIST_TOOLS = {
    "SoX": "SoX 14.4.2 (Ubuntu 24.04; sox --version is checked), flac 1.4.3 (Ubuntu 24.04)",
    "r8brain-free-src": f"r8brain-free-src {r8brain_tool.COMMIT[:7]} (r8brain_resample.cpp built with g++ 13.3.0 -O2 -ffp-contract=off, Ubuntu 24.04), flac 1.4.3 (Ubuntu 24.04)",
    "FFmpeg swresample": "FFmpeg 6.1.1-3ubuntu5 (libswresample and the pcm_s16le writer), flac 1.4.3 (Ubuntu 24.04)",
    "FFmpeg swresample with libsoxr": "FFmpeg 6.1.1-3ubuntu5 (libswresample with libsoxr, resampler=soxr, and the pcm_s16le writer), flac 1.4.3 (Ubuntu 24.04)",
    "FFmpeg wmav2": "FFmpeg 6.1.1-3ubuntu5 (the wmav2 encoder and decoder), flac 1.4.3 (Ubuntu 24.04)",
}
TWOLAME_TOOLS = "TwoLAME 0.4.0, FFmpeg 6.1.1-3ubuntu5 (the decoder of the MPEG audio streams), flac 1.4.3 (Ubuntu 24.04)"
SECONDS = 1

# The stereo files (docs/design/spectral-analysis.md, Step 7 (c) and (d)): 2 s of a pink mid signal and a pink side signal 12 dB lower, from two generators, as left = mid + side
# and right = mid - side, encoded and decoded by the same tools; "stereo-<tool>" in the name. Which of them shows a side cutoff under the mid's or holes is what the
# manifests record; "expect_cutoff" is "any" for them.
STEREO_SECONDS = 2
STEREO = {
    "stereo-lame-j-b64": ("lame", 44100, "-m j -b 64", ["-m", "j", "-b", "64"]),
    "stereo-lame-j-b128": ("lame", 44100, "-m j -b 128", ["-m", "j", "-b", "128"]),
    "stereo-lame-s-b128": ("lame", 44100, "-m s -b 128", ["-m", "s", "-b", "128"]),
    "stereo-oggenc-q0": ("oggenc", 44100, "-q 0", ["0"]),
    "stereo-aac-64k": ("aac", 44100, "-b:a 64k", ["64k"]),
    "stereo-opus-48k": ("opus", 48000, "--bitrate 48", ["48"]),
    # Step 8 (inter-channel coherence): TwoLAME 0.4.0 with intensity stereo at two bitrates and the stereo counterpart, opusenc at a bitrate under the 48 kbps one, and FFmpeg's aac with
    # perceptual noise substitution off. The "equal" set of each is the source with channels that are uncorrelated (a mid and a side signal of equal power).
    "stereo-twolame-j-b128": ("twolame", 44100, "-m j -b 128", ["-m", "j", "-b", "128"]),
    "stereo-twolame-j-b192": ("twolame", 44100, "-m j -b 192", ["-m", "j", "-b", "192"]),
    "stereo-twolame-s-b128": ("twolame", 44100, "-m s -b 128", ["-m", "s", "-b", "128"]),
    "stereo-opus-24k": ("opus", 48000, "--bitrate 24", ["24"]),
    "stereo-aac-64k-nopns": ("aac", 44100, "-b:a 64k -aac_pns 0", ["64k", "-aac_pns", "0"]),
}
# The same six runs with a side signal as loud as the mid ("stereo-equal-<tool>"): a pink mid and a pink side, both at -20 dBFS RMS, from two generators. The first set's side is
# 12 dB lower; the two together show what a side cutoff under the mid's and the holes follow (the coder or the side's level).
SIDE_DB = {"stereo-": -32.0, "stereo-equal-": -20.0}
STEREO = {**STEREO, **{k.replace("stereo-", "stereo-equal-", 1): v for k, v in STEREO.items()}}


def side_db_of(name):
    return SIDE_DB["stereo-equal-"] if name.startswith("stereo-equal-") else SIDE_DB["stereo-"]

# name: (input rate, settings text, arguments, expected: "found" or "none"). The bitrates are those of a mono stream: half the stereo ones the
# design's table used (-b 64 for its -b 128), so a channel gets the same bits.
LAME = {
    "lame-b64": (44100, "-b 64", ["-b", "64"], "found"),
    "lame-b96": (44100, "-b 96", ["-b", "96"], "found"),
    "lame-b160": (44100, "-b 160", ["-b", "160"], "found"),
    "lame-v2": (44100, "-V 2", ["-V", "2"], "found"),
    "lame-v5": (44100, "-V 5", ["-V", "5"], "found"),
    "lame-v0": (44100, "-V 0", ["-V", "0"], "none"),
    "lame-b64-48000": (48000, "-b 64", ["-b", "64"], "found"),
    "lame-b32-22050": (22050, "-b 32", ["-b", "32"], "found"),
}
VORBIS = {"oggenc-q2": ("-q 2", "2", "found"), "oggenc-q5": ("-q 5", "5", "found"), "oggenc-q8": ("-q 8", "8", "none")}
AAC = {"aac-48k": "48k", "aac-64k": "64k", "aac-96k": "96k"}
OPUS = {"opus-48k": "48"}


def write_input(path, rate, seconds=SECONDS):
    """Mono pink noise with a 330 Hz tone, 16 bits."""
    values = pink(rate * seconds, 100, -20.0, rate=rate)
    samples = [max(-32768, min(32767, math.floor(v * 32768 + 0.5))) for v in values]
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", v) for v in samples))


def write_stereo_input(path, rate, side_db=-32.0, seconds=STEREO_SECONDS):
    """Stereo pink noise, 16 bits: left is mid + side and right mid - side, the mid signal at -20 dBFS RMS and the side signal at side_db (12 dB lower, or equal), from two generators."""
    count = rate * seconds
    mid = pink(count, 201, -20.0, tone_db=-200.0, rate=rate)
    side = pink(count, 202, side_db, tone_db=-200.0, rate=rate)
    clamp = lambda v: max(-32768, min(32767, math.floor(v * 32768 + 0.5)))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<hh", clamp(a + b), clamp(a - b)) for a, b in zip(mid, side)))


def run(*args):
    return subprocess.run([str(a) for a in args], check=True, capture_output=True, text=True)


def read_samples(path):
    with wave.open(str(path), "rb") as w:
        return list(struct.unpack(f"<{w.getnframes()}h", w.readframes(w.getnframes())))


def write_samples(path, rate, samples):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", v) for v in samples))


def flac_of(wav, out):
    run("flac", "-8", "-s", "-f", "-P", "0", "-o", out, wav)


def lame_info(mp3, verbose_text):
    data = mp3.read_bytes()
    at = data.find(b"LAME3.")
    band = re.search(r"transition band: (\d+) Hz - (\d+) Hz", verbose_text)
    return {
        "tag_lowpass_hz": data[at + 10] * 100 if at >= 0 else None,
        "transition_band_hz": [int(band.group(1)), int(band.group(2))] if band else None,
    }


# The files that are not a lossy encode on their own: pink noise resampled by FFmpeg's default resampler (swr; the upsampled content that the step
# finder misses, docs/design/spectral-analysis.md, Step 7 (a)), and a mixed one (LAME for the first half of 2 s, the source for the rest, Step 7 (b)).
# Step 7b adds the other direction, a target above the old Nyquist rate and the soxr engine (the figures and the expected cutoff of each are in the manifests).
RESAMPLED = {
    "ffmpeg-44100-to-48000": (44100, 48000, [], "none"),
    "ffmpeg-48000-to-44100": (48000, 44100, [], "none"),
    "ffmpeg-44100-to-96000": (44100, 96000, [], "found"),
    "ffmpeg-soxr-44100-to-48000": (44100, 48000, ["-af", "aresample=resampler=soxr"], "found"),
}

# Step 10 (docs/design/spectral-analysis.md, the Nyquist edge): the three sources (make_measurement_fixtures.py, NYQUIST_SOURCES) at the source's rate, 1 s, quantized to 16
# bits with TPDF dither, converted by SoX 14.4.2 (`rate`, run as a black box; its 32-bit float output is quantized here) or r8brain-free-src (r8brain_tool.py), and quantized to the
# file's 16 or 24 bits with TPDF dither. The name is `resampled-<converter>-<source>-<from>-to-<to>-<bits>`. The converters: SoX's `rate` presets, and r8brain's transition band
# in percent with its attenuation in dB. `expect` is the lower rates (a rate whose half is the edge) the model must find; None where the figure does not say.
SOX = {"sox-h": ["-h"], "sox-v": ["-v"], "sox-v-s": ["-v", "-s"], "sox-l": ["-l"], "sox-q": ["-q"]}
R8BRAIN = {"r8brain-tb05": (0.5, 206.91), "r8brain-tb2": (2.0, 206.91), "r8brain-tb4": (4.0, 136.0), "r8brain-tb10": (10.0, 100.0)}
FOUND, NONE = "found", "none"


def nyquist_resampled():
    """name: (source, rate in, rate out, bits, converter, expect), the converter a key of SOX or R8BRAIN or "ffmpeg" (its default resampler)."""
    entries = {}

    def add(source, rate_in, rate_out, bits, converter, expect):
        entries[f"{converter}-{source}-{rate_in}-to-{rate_out}-{bits}"] = (source, rate_in, rate_out, bits, converter, expect)

    # Every setting on pink noise from 44.1 to 48 kHz, in 16 bits and (the settings of the design's table) in 24 bits.
    # The settings whose stopband starts at the Nyquist frequency are found; SoX's `-l` and `-q` are not; r8brain at 10 % is at the edge of the window (found at its lowest boundary here).
    for converter in (*SOX, *R8BRAIN):
        add("pink", 44100, 48000, 16, converter, None if converter == "r8brain-tb10" else FOUND if converter in ("sox-h", "sox-v", "sox-v-s", "r8brain-tb05", "r8brain-tb2", "r8brain-tb4") else NONE)
    for converter in ("sox-h", "sox-v", "sox-v-s", "r8brain-tb05", "r8brain-tb2", "r8brain-tb4", "r8brain-tb10"):
        add("pink", 44100, 48000, 24, converter, FOUND if converter != "r8brain-tb10" else None)
    # The other sources, and the other rate pairs, at the settings most used.
    # The sweeps' frame test rests on one or two frames, so they claim nothing; the music files and the 48 to 88.2 kHz pair are found in 7 to 20 frames.
    for source in ("sweep", "music"):
        for converter in ("sox-v", "r8brain-tb2"):
            add(source, 44100, 48000, 16, converter, FOUND if source == "music" else None)
            add(source, 44100, 96000, 24, converter, FOUND if source == "music" else None)
    for source in ("pink", "music"):
        for converter in ("sox-v", "r8brain-tb2"):
            add(source, 48000, 88200, 24, converter, FOUND)
    for converter in ("sox-v", "r8brain-tb2"):
        add("pink", 44100, 96000, 24, converter, FOUND)
    for rate_in, rate_out in ((22050, 44100), (32000, 48000), (8000, 44100)):
        for converter in ("sox-v", "r8brain-tb2"):
            add("pink", rate_in, rate_out, 16, converter, FOUND)
        add("pink", rate_in, rate_out, 16, "ffmpeg", NONE)
    return entries


NYQUIST_LOOKALIKES = {
    "wmav2-b128-48000": ("FFmpeg 6.1.1's wmav2 encoder at 128 kbit/s on pink noise at 48,000 Hz, decoded with FFmpeg: its lowpass lies at 21,891 Hz, under the Nyquist frequency of 44,100 Hz.", [44100]),
    "oggenc-q0-48000": ("oggenc (vorbis-tools 1.4.2) -q 0 on pink noise at 48,000 Hz, decoded with oggdec: its lowpass lies at 15,844 Hz, under the Nyquist frequency of 32,000 Hz.", [32000]),
}

MIXED = {"lame-b64-then-source": ("-b 64", ["-b", "64"])}


def prefix_of(name):
    return "resampled" if name in RESAMPLED or name in nyquist_resampled() else "lossy"


def write_wav_bits(path, rate, bits, samples):
    """A mono WAV file of 16 or 24 bit integers."""
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(bits // 8)
        w.setframerate(rate)
        if bits == 16:
            w.writeframes(b"".join(struct.pack("<h", v) for v in samples))
        else:
            w.writeframes(b"".join(struct.pack("<i", v)[:3] for v in samples))


def resample_nyquist(name, entry, temp, tools):
    """The converted source as integers of the entry's bits, and the entry for the manifest."""
    source, rate_in, rate_out, bits, converter, expect = entry
    if converter in SOX and tool_versions.problems("sox"):
        sys.exit("The SoX fixtures are made with " + "; ".join(tool_versions.problems("sox")))
    # The source: 16 bits with TPDF dither at its own rate, as the audio of a 16-bit file would be.
    original = quantize(nyquist_source(source, rate_in), 16, dither_seed=rate_in + 1 + len(source))
    wav = temp / f"{name}-in.wav"
    write_wav_bits(wav, rate_in, 16, original)
    seed = rate_out + bits + len(name)
    if converter == "ffmpeg":
        decoded = temp / f"{name}.wav"
        run("ffmpeg", "-v", "error", "-y", "-i", wav, "-ar", rate_out, "-c:a", "pcm_s16le", decoded)
        samples = read_samples(decoded)
        settings, version, description_tool = f"-ar {rate_out}", "6.1.1", "FFmpeg 6.1.1's default resampler (libswresample), 16 bits, no dither option"
        command = f"ffmpeg -i in.wav -ar {rate_out} -c:a pcm_s16le out.wav"
        name_tool = "FFmpeg swresample"
    else:
        if converter in SOX:
            raw = temp / f"{name}.raw"
            run("sox", wav, "-t", "raw", "-e", "floating-point", "-b", "32", raw, "rate", *SOX[converter], rate_out)
            data = raw.read_bytes()
            floats = struct.unpack(f"<{len(data) // 4}f", data)
            settings, version = f"rate {' '.join(SOX[converter])}", "14.4.2"
            description_tool = f"SoX 14.4.2's `rate {' '.join(SOX[converter])}` (run as a black box, 32-bit floating point output)"
            command = f"sox in.wav -t raw -e floating-point -b 32 out.raw rate {' '.join(SOX[converter])} {rate_out}"
            name_tool = "SoX"
        else:
            band, attenuation = R8BRAIN[converter]
            result = subprocess.run([str(tools["r8brain"]), str(rate_in), str(rate_out), str(band), str(attenuation)], input=struct.pack(f"<{len(original)}d", *[v / 32768 for v in original]),
                                    capture_output=True, check=True)
            floats = struct.unpack(f"<{len(result.stdout) // 8}d", result.stdout)
            settings, version = f"transition band {band:g} %, attenuation {attenuation:g} dB", f"r8brain-free-src {r8brain_tool.COMMIT[:7]}"
            description_tool = f"r8brain-free-src {r8brain_tool.COMMIT[:7]} (MIT) with a transition band of {band:g} % and an attenuation of {attenuation:g} dB (run as a black box, doubles)"
            command = f"r8brain_resample {rate_in} {rate_out} {band:g} {attenuation:g} < in.f64 > out.f64"
            name_tool = "r8brain-free-src"
        samples = quantize(floats, bits, dither_seed=seed)
    entry_out = {
        "description": f"{NYQUIST_SOURCES[source]}, 1 s at {rate_in:,} Hz (16 bits, TPDF dither of one least significant bit), converted to {rate_out:,} Hz by {description_tool}"
                       + ("" if converter == "ffmpeg" else f" and quantized to {bits} bits with TPDF dither of one least significant bit") + ".",
        "encoder": {"name": name_tool, "version": version, "settings": settings, "command": command}, "stated": None,
        "expect_cutoff": "any", "expect_nyquist": None if expect is None else ([rate_in] if expect == FOUND else []),
    }
    return samples, bits, rate_out, entry_out


def encode_all(names):
    made = {}
    with tempfile.TemporaryDirectory() as temp:
        temp = pathlib.Path(temp)
        inputs = {}
        for rate in (22050, 44100, 48000):
            inputs[rate] = temp / f"in-{rate}.wav"
            write_input(inputs[rate], rate)

        def finish(name, decoded, entry):
            flac_of(decoded, OUT / f"{prefix_of(name)}-{name}.flac")
            made[name] = entry

        for name, (rate, settings, args, expect) in LAME.items():
            if names and name not in names:
                continue
            mp3, decoded = temp / f"{name}.mp3", temp / f"{name}.wav"
            result = run("lame", "--verbose", *args, inputs[rate], mp3)
            run("lame", "--decode", mp3, decoded)
            finish(name, decoded, {
                "description": f"LAME 3.100 {settings} on pink noise at {rate} Hz, decoded with lame --decode.",
                "encoder": {"name": "LAME", "version": "3.100", "settings": settings, "command": f"lame --verbose {settings} in.wav out.mp3; lame --decode out.mp3 out.wav"},
                "stated": lame_info(mp3, result.stderr + result.stdout), "expect_cutoff": expect})
        for name, (settings, quality, expect) in VORBIS.items():
            if names and name not in names:
                continue
            ogg, decoded = temp / f"{name}.ogg", temp / f"{name}.wav"
            run("oggenc", "-Q", "-q", quality, "-o", ogg, inputs[44100])
            run("oggdec", "-Q", "-b", "16", "-e", "0", "-s", "1", "-o", decoded, ogg)
            finish(name, decoded, {
                "description": f"oggenc (vorbis-tools 1.4.2) {settings} on pink noise at 44,100 Hz, decoded with oggdec.",
                "encoder": {"name": "oggenc", "version": "vorbis-tools 1.4.2", "settings": settings, "command": f"oggenc -Q {settings} in.wav -o out.ogg; oggdec -b 16 out.ogg"},
                "stated": None, "expect_cutoff": expect})
        for name, bitrate in AAC.items():
            if names and name not in names:
                continue
            m4a, decoded = temp / f"{name}.m4a", temp / f"{name}.wav"
            run("ffmpeg", "-v", "error", "-y", "-i", inputs[44100], "-c:a", "aac", "-b:a", bitrate, m4a)
            run("ffmpeg", "-v", "error", "-y", "-i", m4a, "-c:a", "pcm_s16le", decoded)
            finish(name, decoded, {
                "description": f"FFmpeg 6.1.1's aac encoder at {bitrate}bps on pink noise at 44,100 Hz, decoded with FFmpeg.",
                "encoder": {"name": "FFmpeg aac", "version": "6.1.1", "settings": f"-b:a {bitrate}", "command": f"ffmpeg -i in.wav -c:a aac -b:a {bitrate} out.m4a; ffmpeg -i out.m4a -c:a pcm_s16le out.wav"},
                "stated": None, "expect_cutoff": "found"})
        for name, (source, target, extra, expect) in RESAMPLED.items():
            if names and name not in names:
                continue
            decoded = temp / f"{name}.wav"
            run("ffmpeg", "-v", "error", "-y", "-i", inputs[source], *extra, "-ar", target, "-c:a", "pcm_s16le", decoded)
            engine = "soxr resampler (libsoxr, -af aresample=resampler=soxr)" if extra else "default resampler (libswresample)"
            settings = f"{' '.join(extra)} -ar {target}".strip()
            finish(name, decoded, {
                "description": f"The pink noise at {source} Hz resampled to {target} Hz by FFmpeg 6.1.1's {engine}, 16 bits, no dither option.",
                "encoder": {"name": "FFmpeg swresample", "version": "6.1.1", "settings": settings, "command": f"ffmpeg -i in.wav {settings} -c:a pcm_s16le out.wav"},
                "stated": None, "expect_cutoff": expect})
        tools = {}
        for name, entry in nyquist_resampled().items():
            if names and name not in names:
                continue
            if entry[4] in R8BRAIN and "r8brain" not in tools:
                tools["r8brain"] = r8brain_tool.build(temp / "r8brain")
            samples, bits, rate_out, entry_out = resample_nyquist(name, entry, temp, tools)
            decoded = temp / f"{name}-out.wav"
            write_wav_bits(decoded, rate_out, bits, samples)
            finish(name, decoded, entry_out)
        for name, (description, expect) in NYQUIST_LOOKALIKES.items():
            if names and name not in names:
                continue
            decoded = temp / f"{name}.wav"
            if name.startswith("wmav2"):
                asf = temp / f"{name}.wma"
                run("ffmpeg", "-v", "error", "-y", "-i", inputs[48000], "-c:a", "wmav2", "-b:a", "128k", asf)
                run("ffmpeg", "-v", "error", "-y", "-i", asf, "-c:a", "pcm_s16le", decoded)
                encoder = {"name": "FFmpeg wmav2", "version": "6.1.1", "settings": "-b:a 128k", "command": "ffmpeg -i in.wav -c:a wmav2 -b:a 128k out.wma; ffmpeg -i out.wma -c:a pcm_s16le out.wav"}
            else:
                ogg = temp / f"{name}.ogg"
                run("oggenc", "-Q", "-q", "0", "-o", ogg, inputs[48000])
                run("oggdec", "-Q", "-b", "16", "-e", "0", "-s", "1", "-o", decoded, ogg)
                encoder = {"name": "oggenc", "version": "vorbis-tools 1.4.2", "settings": "-q 0", "command": "oggenc -Q -q 0 in.wav -o out.ogg; oggdec -b 16 out.ogg"}
            finish(name, decoded, {"description": description, "encoder": encoder, "stated": None, "expect_cutoff": "any", "expect_nyquist": expect})
        for name, (settings, args) in MIXED.items():
            if names and name not in names:
                continue
            long_input = temp / "in-44100-2s.wav"
            write_input(long_input, 44100, 2)
            mp3, wav_part, joined = temp / f"{name}.mp3", temp / f"{name}-part.wav", temp / f"{name}.wav"
            run("lame", "--verbose", *args, long_input, mp3)
            run("lame", "--decode", mp3, wav_part)
            half = 44100
            lossy, source = read_samples(wav_part), read_samples(long_input)
            if len(lossy) < half:
                sys.exit(f"{name}: the decoded part has {len(lossy)} samples")
            write_samples(joined, 44100, lossy[:half] + source[half:])
            finish(name, joined, {
                "description": f"Pink noise at 44,100 Hz, 2 s: the first second encoded with LAME 3.100 {settings} and decoded with lame --decode, the second the source itself: half of the stream is from a lossy encode.",
                "encoder": {"name": "LAME", "version": "3.100", "settings": settings, "command": f"lame {settings} in.wav out.mp3; lame --decode out.mp3 part.wav; the first 44,100 samples of part.wav then the last 44,100 of in.wav"},
                "stated": None, "expect_cutoff": "none"})
        stereo_inputs = {}
        for name, (tool, rate, settings, args) in STEREO.items():
            if names and name not in names:
                continue
            side_db = side_db_of(name)
            if (rate, side_db) not in stereo_inputs:
                stereo_inputs[(rate, side_db)] = temp / f"stereo-in-{rate}-{side_db:g}.wav"
                write_stereo_input(stereo_inputs[(rate, side_db)], rate, side_db)
            source, decoded = stereo_inputs[(rate, side_db)], temp / f"{name}.wav"
            level = "a side signal 12 dB lower" if side_db < -20 else "a side signal as loud as the mid"
            if tool == "lame":
                mp3 = temp / f"{name}.mp3"
                result = run("lame", "--verbose", *args, source, mp3)
                run("lame", "--decode", mp3, decoded)
                with wave.open(str(decoded), "rb") as w:
                    decoded_rate = w.getframerate()
                at = f" at {decoded_rate} Hz" if decoded_rate != rate else ""
                entry = {"description": f"LAME 3.100 {settings} on 2 s of stereo pink noise (a mid signal and {level}) at {rate} Hz, decoded with lame --decode{at}.",
                         "encoder": {"name": "LAME", "version": "3.100", "settings": settings, "command": f"lame --verbose {settings} in.wav out.mp3; lame --decode out.mp3 out.wav"},
                         "stated": lame_info(mp3, result.stderr + result.stdout)}
            elif tool == "oggenc":
                ogg = temp / f"{name}.ogg"
                run("oggenc", "-Q", "-q", args[0], "-o", ogg, source)
                run("oggdec", "-Q", "-b", "16", "-e", "0", "-s", "1", "-o", decoded, ogg)
                entry = {"description": f"oggenc (vorbis-tools 1.4.2) {settings} on 2 s of stereo pink noise (a mid signal and {level}) at {rate} Hz, decoded with oggdec.",
                         "encoder": {"name": "oggenc", "version": "vorbis-tools 1.4.2", "settings": settings, "command": f"oggenc -Q {settings} in.wav -o out.ogg; oggdec -b 16 out.ogg"},
                         "stated": None}
            elif tool == "aac":
                m4a = temp / f"{name}.m4a"
                run("ffmpeg", "-v", "error", "-y", "-i", source, "-c:a", "aac", "-b:a", args[0], *args[1:], m4a)
                run("ffmpeg", "-v", "error", "-y", "-i", m4a, "-c:a", "pcm_s16le", decoded)
                entry = {"description": f"FFmpeg 6.1.1's aac encoder at {args[0]}bps{' with perceptual noise substitution off' if len(args) > 1 else ''} on 2 s of stereo pink noise (a mid signal and {level}) at {rate} Hz, decoded with FFmpeg.",
                         "encoder": {"name": "FFmpeg aac", "version": "6.1.1", "settings": settings, "command": f"ffmpeg -i in.wav -c:a aac {settings} out.m4a; ffmpeg -i out.m4a -c:a pcm_s16le out.wav"},
                         "stated": None}
            elif tool == "twolame":
                mp2 = temp / f"{name}.mp2"
                run("twolame", "--quiet", *args, source, mp2)
                run("ffmpeg", "-v", "error", "-y", "-i", mp2, "-c:a", "pcm_s16le", decoded)
                entry = {"description": f"TwoLAME 0.4.0 {settings} on 2 s of stereo pink noise (a mid signal and {level}) at {rate} Hz, decoded with FFmpeg.",
                         "encoder": {"name": "TwoLAME", "version": "0.4.0", "settings": settings, "command": f"twolame --quiet {settings} in.wav out.mp2; ffmpeg -i out.mp2 -c:a pcm_s16le out.wav"},
                         "stated": None}
            else:
                opus = temp / f"{name}.opus"
                run("opusenc", "--quiet", "--bitrate", args[0], source, opus)
                run("opusdec", "--quiet", "--rate", str(rate), opus, decoded)
                entry = {"description": f"opusenc (opus-tools 0.2, libopus 1.4) at {args[0]} kbit/s on 2 s of stereo pink noise (a mid signal and {level}) at {rate} Hz, decoded with opusdec.",
                         "encoder": {"name": "opusenc", "version": "opus-tools 0.2, libopus 1.4", "settings": settings, "command": f"opusenc {settings} in.wav out.opus; opusdec --rate {rate} out.opus out.wav"},
                         "stated": None}
            entry["expect_cutoff"] = "any"
            finish(name, decoded, entry)
        for name, kbps in OPUS.items():
            if names and name not in names:
                continue
            opus, decoded = temp / f"{name}.opus", temp / f"{name}.wav"
            run("opusenc", "--quiet", "--bitrate", kbps, inputs[48000], opus)
            run("opusdec", "--quiet", "--rate", "48000", opus, decoded)
            finish(name, decoded, {
                "description": f"opusenc (opus-tools 0.2, libopus 1.4) at {kbps} kbit/s on pink noise at 48,000 Hz, decoded with opusdec.",
                "encoder": {"name": "opusenc", "version": "opus-tools 0.2, libopus 1.4", "settings": f"--bitrate {kbps}", "command": f"opusenc --bitrate {kbps} in.wav out.opus; opusdec --rate 48000 out.opus out.wav"},
                "stated": None, "expect_cutoff": "found"})
    return made


def write_manifest(name, entry):
    prefix = prefix_of(name)
    data = (OUT / f"{prefix}-{name}.flac").read_bytes()
    entry = dict(entry)
    encoder_name = entry.get("encoder", {}).get("name")
    if encoder_name == "FFmpeg swresample" and "soxr" in entry["encoder"].get("settings", ""):
        encoder_name = "FFmpeg swresample with libsoxr"
    entry["tools"] = NYQUIST_TOOLS.get(encoder_name, TWOLAME_TOOLS if encoder_name == "TwoLAME" else TOOLS)
    entry["length"] = len(data)
    entry["model"] = mm.measure(data)
    found = entry["model"]["cutoff"]["is_found"]
    expected_edges = entry.get("expect_nyquist")
    if expected_edges is not None and [e["lower_rate"] for e in entry["model"]["nyquist_edges"]["edges"]] != expected_edges:
        message = f"{name}: expected the Nyquist edges of the rates {expected_edges}, the model says {entry['model']['nyquist_edges']['edges']}"
        if os.environ.get("ENCSPECT_REPORT_ONLY"):
            print("MISMATCH", message)
        else:
            sys.exit(message)
    if entry["expect_cutoff"] != "any" and (entry["expect_cutoff"] == "found") != found:
        sys.exit(f"{name}: expected {entry['expect_cutoff']}, the model says {'found' if found else 'none'}")
    (OUT / f"{prefix}-{name}.expected.json").write_text(mm.dumps(entry))
    cutoff = entry["model"]["cutoff"]
    print(name, len(data), cutoff.get("frequency"), cutoff.get("step_tenths"), cutoff.get("frames_showing"), cutoff.get("frames_with_signal"), entry["stated"],
          [(e["lower_rate"], e["step_frequency"], e["step_tenths"], e["above_tenths"]) for e in entry["model"]["nyquist_edges"]["edges"]])
    side = entry["model"].get("mid_side")
    if side:
        print("   mid", side["mid_cutoff"].get("frequency"), side["mid_cutoff"]["step_tenths"], "side", side["side_cutoff"].get("frequency"), side["side_cutoff"]["step_tenths"],
              "holes", entry["model"]["holes"]["hole_cells"], entry["model"]["holes"]["cells_with_signal"], side["mid_holes"]["hole_cells"], side["side_holes"]["hole_cells"])


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    OUT.mkdir(parents=True, exist_ok=True)
    if "--manifests-only" in sys.argv:
        for path in sorted([*OUT.glob("lossy-*.expected.json"), *OUT.glob("resampled-*.expected.json")]):
            name = path.name.split("-", 1)[1][:-len(".expected.json")]
            if not args or name in args:
                entry = json.loads(path.read_text())
                write_manifest(name, {k: v for k, v in entry.items() if k not in ("model", "length", "tools")})
        return
    for name, entry in encode_all(set(args)).items():
        write_manifest(name, entry)


if __name__ == "__main__":
    main()
