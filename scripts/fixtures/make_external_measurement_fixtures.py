#!/usr/bin/env python3
"""Writes the lossy-derived measurement fixtures in tests/fixtures/measurement (lossy-*.flac) and their manifests (*.expected.json).

A pinned input (generated here: 1 s of mono pink noise with a 330 Hz tone at -20 dBFS RMS, 16 bits, from fixed generators) is encoded with
LAME, oggenc, FFmpeg's aac encoder and opusenc, decoded again with the encoder's own decoder where it has one (lame --decode, oggdec, opusdec;
FFmpeg for aac), and written as a FLAC file by flac -8: a lossless file made from a lossy encode, which is the use case of the spectral cutoff
(docs/design/spectral-analysis.md, "Lossless files from lossy encodes"). The files are 1 s of mono, for the size of the repository. Two files are not
lossy encodes alone: `resampled-ffmpeg-44100-to-48000` (the input resampled by FFmpeg) and `lossy-lame-b64-then-source` (2 s: the first second encoded, the second the input).

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
import pathlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

import measurement_model as mm
from make_measurement_fixtures import Lcg, pink

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "measurement"

TOOLS = "LAME 3.100, vorbis-tools 1.4.2 (oggenc, oggdec), FFmpeg 6.1.1-3ubuntu5 (aac), opus-tools 0.2 with libopus 1.4 (opusenc, opusdec), flac 1.4.3 (Ubuntu 24.04)"
SECONDS = 1

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
RESAMPLED = {"ffmpeg-44100-to-48000": (44100, 48000)}
MIXED = {"lame-b64-then-source": ("-b 64", ["-b", "64"])}


def prefix_of(name):
    return "resampled" if name in RESAMPLED else "lossy"


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
        for name, (source, target) in RESAMPLED.items():
            if names and name not in names:
                continue
            decoded = temp / f"{name}.wav"
            run("ffmpeg", "-v", "error", "-y", "-i", inputs[source], "-ar", target, "-c:a", "pcm_s16le", decoded)
            finish(name, decoded, {
                "description": f"The pink noise at {source} Hz resampled to {target} Hz by FFmpeg 6.1.1's default resampler (libswresample), 16 bits, no dither option.",
                "encoder": {"name": "FFmpeg swresample", "version": "6.1.1", "settings": f"-ar {target}", "command": f"ffmpeg -i in.wav -ar {target} -c:a pcm_s16le out.wav"},
                "stated": None, "expect_cutoff": "none"})
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
    entry["tools"] = TOOLS
    entry["length"] = len(data)
    entry["model"] = mm.measure(data)
    found = entry["model"]["cutoff"]["is_found"]
    if (entry["expect_cutoff"] == "found") != found:
        sys.exit(f"{name}: expected {entry['expect_cutoff']}, the model says {'found' if found else 'none'}")
    (OUT / f"{prefix}-{name}.expected.json").write_text(mm.dumps(entry))
    cutoff = entry["model"]["cutoff"]
    print(name, len(data), cutoff.get("frequency"), cutoff.get("step_tenths"), cutoff.get("frames_showing"), cutoff.get("frames_with_signal"), entry["stated"])


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
