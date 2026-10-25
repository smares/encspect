#!/usr/bin/env python3
"""Writes the encoder-made WAV fixtures in tests/fixtures/wav (external-*.wav).

They come from FFmpeg 6.1.1 of Ubuntu 24.04 (its wav muxer and the encoders that feed it): PCM of 8, 16, 24 and 32 bits, floating point of 32 and
64 bits, A-law and mu-law, mono, stereo, 5.1 and 7.1 with their channel masks, a quadraphonic and a three-channel layout, RF64 and the
JUNK chunk that reserves space for a ds64 chunk, a Broadcast Wave bext chunk, INFO tags, a peak envelope chunk, a file written to a pipe (whose sizes
are placeholders), Wave64 files (the w64 muxer), and MPEG audio Layers II and III, AC-3, E-AC-3, DTS, Microsoft ADPCM, IMA ADPCM and GSM 6.10 as the muxer tags them. The
inputs are deterministic signals written by wav_model.py.

The muxer's output depends on its version, so the files are committed and --manifests-only recomputes the manifests from them without FFmpeg.
Each manifest is worked out by wav_model.py from the file (its own reader, written from the specifications apart from the analyzer), plus the
tool and its settings. crosscheck_wav_fixtures.py confirms them with ffprobe and MediaInfo.

    python3 scripts/fixtures/make_external_wav_fixtures.py [--manifests-only]
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import wav_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "wav"

SHORT = 0.25

TAGS = ["-metadata", "title=Fixture title", "-metadata", "artist=Fixture artist", "-metadata", "album=Fixture album", "-metadata", "date=2026",
        "-metadata", "comment=A comment", "-metadata", "genre=Test", "-metadata", "track=3", "-metadata", "copyright=Public domain"]
BEXT = ["-write_bext", "1", "-metadata", "description=A description of the sound", "-metadata", "originator=Fixture originator",
        "-metadata", "originator_reference=REF-0001", "-metadata", "origination_date=2026-10-24", "-metadata", "origination_time=12:34:56",
        "-metadata", "time_reference=48000", "-metadata", "coding_history=A=PCM,F=48000,W=16,M=stereo,T=Fixture"]

# name: (signal (kind, rate, channels, seconds, raw format), arguments before the output, the layout of the input or None, findings the analyzer must raise,
# the codec name the analyzer gives a coding it has no parser for, or None).
FIXTURES = {
    "pcm-u8-mono-8000": (("sine", 8000, 1, SHORT, "u8"), ["-c:a", "pcm_u8"], None, [], None),
    "pcm-s16-stereo-44100": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le"], None, [], None),
    "pcm-s16-mono-22050": (("sine", 22050, 1, SHORT, "s16le"), ["-c:a", "pcm_s16le"], None, [], None),
    "pcm-s16-stereo-192000": (("sine", 192000, 2, 0.05, "s16le"), ["-c:a", "pcm_s16le"], None, [], None),
    "pcm-s24-stereo-48000": (("sine", 48000, 2, SHORT, "s24le"), ["-c:a", "pcm_s24le"], None, [], None),
    "pcm-s32-stereo-96000": (("sine", 96000, 2, 0.1, "s32le"), ["-c:a", "pcm_s32le"], None, [], None),
    "pcm-f32-stereo-44100": (("sine", 44100, 2, SHORT, "f32le"), ["-c:a", "pcm_f32le"], None, [], None),
    "pcm-f64-mono-48000": (("sine", 48000, 1, SHORT, "f64le"), ["-c:a", "pcm_f64le"], None, [], None),
    "alaw-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_alaw"], None, [], None),
    "mulaw-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_mulaw"], None, [], None),
    "pcm-s16-3ch-48000": (("sine", 48000, 3, 0.1, "s16le"), ["-c:a", "pcm_s16le"], "3.0", [], None),
    "pcm-s16-quad-48000": (("sine", 48000, 4, 0.1, "s16le"), ["-c:a", "pcm_s16le"], "quad", [], None),
    "pcm-s16-5.1-48000": (("sine", 48000, 6, 0.1, "s16le"), ["-c:a", "pcm_s16le"], "5.1", [], None),
    "pcm-s24-7.1-96000": (("sine", 96000, 8, 0.05, "s24le"), ["-c:a", "pcm_s24le"], "7.1", [], None),
    "pcm-s16-stereo-bwf": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le", *BEXT, *TAGS], None, [], None),
    "pcm-s16-stereo-info": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le", *TAGS], None, [], None),
    "pcm-s16-stereo-peak": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le", "-write_peak", "on"], None, [], None),
    "pcm-s24-stereo-rf64": (("sine", 48000, 2, SHORT, "s24le"), ["-c:a", "pcm_s24le", "-rf64", "always"], None, [], None),
    "pcm-s16-stereo-rf64-auto": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le", "-rf64", "auto"], None, [], None),
    "pcm-s16-stereo-rf64-bwf": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le", "-rf64", "always", *BEXT], None, [], None),
    "mp2-in-wav": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "mp2", "-b:a", "192k"], None, [], None),
    "mp3-in-wav-stereo": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "libmp3lame", "-b:a", "128k"], None, [], None),
    "mp3-in-wav-mono": (("sine", 22050, 1, SHORT, "s16le"), ["-c:a", "libmp3lame", "-b:a", "48k"], None, [], None),
    "ac3-in-wav": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "ac3", "-b:a", "192k"], None, [], None),
    "eac3-in-wav": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "eac3", "-b:a", "192k"], None, [], None),
    "dts-in-wav": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "dca", "-strict", "-2", "-b:a", "768k"], None, [], None),
    "adpcm-ms": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "adpcm_ms"], None, ["wav.coding-not-analyzed"], "Microsoft ADPCM"),
    "adpcm-ima": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "adpcm_ima_wav"], None, ["wav.coding-not-analyzed"], "IMA ADPCM"),
    "gsm-ms": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "libgsm_ms"], None, ["wav.coding-not-analyzed"], "GSM 6.10"),
}

# Wave64 files from FFmpeg's w64 muxer (.w64), written the same way.
W64_FIXTURES = {
    "w64-pcm-u8-mono-8000": (("sine", 8000, 1, SHORT, "u8"), ["-c:a", "pcm_u8"], None, [], None),
    "w64-pcm-s16-stereo-44100": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le"], None, [], None),
    "w64-pcm-s24-5.1-48000": (("sine", 48000, 6, 0.1, "s24le"), ["-c:a", "pcm_s24le"], "5.1", [], None),
    "w64-pcm-s32-stereo-96000": (("sine", 96000, 2, 0.1, "s32le"), ["-c:a", "pcm_s32le"], None, [], None),
    "w64-pcm-f32-stereo-48000": (("sine", 48000, 2, SHORT, "f32le"), ["-c:a", "pcm_f32le"], None, [], None),
    "w64-alaw-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_alaw"], None, [], None),
    "w64-mp2": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "mp2", "-b:a", "192k"], None, [], None),
    "w64-ac3": (("sine", 48000, 2, SHORT, "s16le"), ["-c:a", "ac3", "-b:a", "192k"], None, [], None),
    "w64-adpcm-ima": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "adpcm_ima_wav"], None, ["wav.coding-not-analyzed"], "IMA ADPCM"),
}

# IEC 61937 bursts in the samples of a WAVE file, as S/PDIF passthrough is captured: the signal is encoded (FFmpeg's encoder for the arguments), the
# stream is written as IEC 61937 by the spdif muxer (used as a black box; its source was not read), and the bytes of that are the samples of a 16-bit
# stereo PCM WAVE file at the link rate. name: (signal, encoder arguments, file extension of the stream, link rate in Hz, (codec, rate, channels) the
# codec parser reads, findings the analyzer must raise).
IEC_FIXTURES = {
    "iec61937-ac3-stereo-192k": (("sine", 48000, 2, 0.1, "s16le"), ["-c:a", "ac3", "-b:a", "192k"], "ac3", 48000, ("ac-3", 48000, 2), ["wav.iec61937-in-pcm"]),
    "iec61937-ac3-5.1-448k": (("sine", 44100, 6, 0.1, "s16le"), ["-c:a", "ac3", "-b:a", "448k"], "ac3", 44100, ("ac-3", 44100, 6), ["wav.iec61937-in-pcm"]),
    "iec61937-eac3-stereo-192k": (("sine", 48000, 2, 0.1, "s16le"), ["-c:a", "eac3", "-b:a", "192k"], "eac3", 192000, ("e-ac-3", 48000, 2), ["wav.iec61937-in-pcm"]),
    "iec61937-dts-stereo-768k": (("sine", 48000, 2, 0.1, "s16le"), ["-c:a", "dca", "-strict", "-2", "-b:a", "768k"], "dts", 48000, ("dts", 48000, 2), ["wav.iec61937-in-pcm"]),
    "iec61937-mp2-stereo-192k": (("sine", 48000, 2, 0.1, "s16le"), ["-c:a", "mp2", "-b:a", "192k"], "mp2", 48000, None, ["wav.iec61937-payload-not-analyzed"]),
    "iec61937-mp2-lsf-24000": (("sine", 24000, 2, 0.1, "s16le"), ["-c:a", "mp2", "-b:a", "64k"], "mp2", 48000, None, ["wav.iec61937-payload-not-analyzed"]),
    "iec61937-mp3-stereo-128k": (("sine", 44100, 2, 0.1, "s16le"), ["-c:a", "libmp3lame", "-b:a", "128k"], "mp3", 44100, None, ["wav.iec61937-payload-not-analyzed"]),
    "iec61937-aac-stereo-128k": (("sine", 48000, 2, 0.1, "s16le"), ["-c:a", "aac", "-b:a", "128k"], "aac", 48000, None, ["wav.iec61937-payload-not-analyzed"]),
}

# name: (signal, arguments, findings). Written to a pipe: the muxer cannot go back to fill in the sizes.
PIPE_FIXTURES = {
    "pcm-s16-stereo-pipe": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le"], ["wav.riff-size-placeholder", "wav.data-size-placeholder"]),
}

RAW = {"u8": (1, "u8"), "s16le": (2, "s16le"), "s24le": (3, "s24le"), "s32le": (4, "s32le"), "f32le": (4, "f32le"), "f64le": (8, "f64le")}


def signal(spec):
    kind, rate, channels, seconds, raw = spec
    count = round(rate * seconds)
    if raw.startswith("f"):
        width = 4 if raw == "f32le" else 8
        out = bytearray()
        for i in range(count):
            for c in range(channels):
                v = 0.4 * math.sin(2 * math.pi * 220 * (c + 1) * i / rate)
                out += struct.pack("<f" if width == 4 else "<d", v)
        return bytes(out)
    bits = RAW[raw][0] * 8
    return m.pcm(kind, rate, channels, count, bits)


def tool_version():
    result = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True)
    return result.stdout.splitlines()[0].split(" Copyright")[0].strip()


def encode(name, spec, arguments, layout, work):
    kind, rate, channels, seconds, raw = spec
    suffix = ".w64" if name.startswith("w64-") else ".wav"
    target = OUT / f"external-{name}{suffix}"
    command = ["ffmpeg", "-v", "error", "-y", "-f", RAW[raw][1], "-ar", str(rate), "-ac", str(channels)]
    if layout:
        command += ["-channel_layout", layout]
    command += ["-i", "-", *arguments, str(work / f"out{suffix}")]
    result = subprocess.run(command, input=signal(spec), capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr.decode(errors='replace')}")
    shutil.copy(work / f"out{suffix}", target)
    return target


def encode_iec(name, spec, arguments, extension, link_rate, work):
    kind, rate, channels, seconds, raw = spec
    target = OUT / f"external-{name}.wav"
    stream = work / f"stream.{extension}"
    command = ["ffmpeg", "-v", "error", "-y", "-f", RAW[raw][1], "-ar", str(rate), "-ac", str(channels), "-i", "-", *arguments, str(stream)]
    result = subprocess.run(command, input=signal(spec), capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr.decode(errors='replace')}")
    spdif = work / "stream.spdif"
    result = subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(stream), "-c", "copy", "-f", "spdif", str(spdif)], capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg spdif failed for {name}: {result.stderr.decode(errors='replace')}")
    result = subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "s16le", "-ar", str(link_rate), "-ac", "2", "-i", str(spdif), "-c:a", "pcm_s16le", str(target)], capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg wav failed for {name}: {result.stderr.decode(errors='replace')}")
    return target


def encode_pipe(name, spec, arguments):
    kind, rate, channels, seconds, raw = spec
    target = OUT / f"external-{name}.wav"
    command = ["ffmpeg", "-v", "error", "-y", "-f", RAW[raw][1], "-ar", str(rate), "-ac", str(channels), "-i", "-", *arguments, "-f", "wav", "-"]
    result = subprocess.run(command, input=signal(spec), capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr.decode(errors='replace')}")
    target.write_bytes(result.stdout)
    return target


def write_manifest(path, spec, arguments, findings, codec, layout=None, carried=None):
    data = path.read_bytes()
    manifest = m.manifest(data, codec, 0, carried)
    manifest["findings"] = [{"code": code} for code in findings]
    manifest["tool"] = f"{tool_version()} (ffmpeg package of Ubuntu 24.04)"
    manifest["arguments"] = arguments
    manifest["input"] = {"kind": spec[0], "rate": spec[1], "channels": spec[2], "seconds": spec[3], "format": spec[4], **({"layout": layout} if layout else {})}
    path.with_suffix(".expected.json").write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manifests_only = "--manifests-only" in sys.argv
    # --only PREFIX writes just the fixtures whose names start with it, for a file added later (a full run writes every file again, and files whose
    # bytes depend on the time they were made, such as the peak envelope, would change).
    only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else ""
    if manifests_only:
        # The committed files are the evidence; the manifests are recomputed from them and from the settings the script lists.
        for name, (spec, arguments, layout, findings, codec) in {**FIXTURES, **W64_FIXTURES}.items():
            write_manifest_keep_tool(OUT / f"external-{name}.{'w64' if name.startswith('w64-') else 'wav'}", spec, arguments, findings, codec, layout)
        for name, (spec, arguments, findings) in PIPE_FIXTURES.items():
            write_manifest_keep_tool(OUT / f"external-{name}.wav", spec, ["-f", "wav", "-", *arguments], findings, None, None)
        for name, (spec, arguments, extension, link_rate, carried, findings) in IEC_FIXTURES.items():
            write_manifest_keep_tool(OUT / f"external-{name}.wav", spec, ["-f", "spdif", *arguments], findings, None, None, carried)
        return
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for name, (spec, arguments, layout, findings, codec) in {**FIXTURES, **W64_FIXTURES}.items():
            if not name.startswith(only):
                continue
            path = encode(name, spec, arguments, layout, work)
            write_manifest(path, spec, arguments, findings, codec, layout)
        for name, (spec, arguments, findings) in PIPE_FIXTURES.items():
            if not name.startswith(only):
                continue
            path = encode_pipe(name, spec, arguments)
            write_manifest(path, spec, ["-f", "wav", "-", *arguments], findings, None)
        for name, (spec, arguments, extension, link_rate, carried, findings) in IEC_FIXTURES.items():
            if not name.startswith(only):
                continue
            path = encode_iec(name, spec, arguments, extension, link_rate, work)
            write_manifest(path, spec, ["-f", "spdif", *arguments], findings, None, None, carried)


def write_manifest_keep_tool(path, spec, arguments, findings, codec, layout, carried=None):
    """The manifest of a committed file: the tool that wrote the file stays as the last full run recorded it."""
    tool = "FFmpeg 6.1.1 (ffmpeg package of Ubuntu 24.04)"
    old = path.with_suffix(".expected.json")
    if old.exists():
        tool = json.loads(old.read_text(encoding="utf-8"))["tool"]
    data = path.read_bytes()
    manifest = m.manifest(data, codec, 0, carried)
    manifest["findings"] = [{"code": code} for code in findings]
    manifest["tool"] = tool
    manifest["arguments"] = arguments
    manifest["input"] = {"kind": spec[0], "rate": spec[1], "channels": spec[2], "seconds": spec[3], "format": spec[4], **({"layout": layout} if layout else {})}
    old.write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
