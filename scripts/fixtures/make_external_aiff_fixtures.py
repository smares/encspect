#!/usr/bin/env python3
"""Writes the encoder-made AIFF fixtures in tests/fixtures/aiff (external-*.aiff, external-*.aifc).

They come from FFmpeg 6.1.1 of Ubuntu 24.04 (its aiff muxer and the encoders that feed it): PCM of 8, 16, 24 and 32 bits, floating point of 32 and
64 bits, the AIFF-C types sowt, raw, fl32, fl64, alaw, ulaw and ima4, mono, stereo and three channels, the sample rates 8,000 to 96,000 Hz, the NAME,
AUTH, (c) and ANNO text chunks, and an ID3v2 tag in an ID3 chunk, in versions 3 and 4. The inputs are deterministic signals written by wav_model.py.

The muxer's output depends on its version, so the files are committed and --manifests-only recomputes the manifests from them without FFmpeg.
Each manifest is worked out by aiff_model.py from the file (its own reader, written from the specifications apart from the analyzer), plus the
tool and its settings. crosscheck_wav_fixtures.py confirms them with ffprobe and MediaInfo.

    python3 scripts/fixtures/make_external_aiff_fixtures.py [--manifests-only]
"""

import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import aiff_model as m
import make_external_wav_fixtures as w

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "aiff"

SHORT = w.SHORT
TAGS = ["-metadata", "title=Fixture title", "-metadata", "author=Fixture author", "-metadata", "copyright=Public domain", "-metadata", "comment=A comment"]
ID3 = ["-write_id3v2", "1", "-metadata", "title=Fixture title", "-metadata", "artist=Fixture artist", "-metadata", "album=Fixture album", "-metadata", "date=2026"]

# name: (signal (kind, rate, channels, seconds, raw format), arguments before the output, the extension, the layout of the input or None, the findings
# the analyzer must raise).
FIXTURES = {
    "pcm-s16-stereo-44100": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16be"], "aiff", None, []),
    "pcm-s16-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_s16be"], "aiff", None, []),
    "pcm-s16-stereo-96000": (("sine", 96000, 2, 0.1, "s16le"), ["-c:a", "pcm_s16be"], "aiff", None, []),
    "pcm-s16-3ch-48000": (("sine", 48000, 3, 0.1, "s16le"), ["-c:a", "pcm_s16be"], "aiff", "3.0", []),
    "pcm-s24-stereo-48000": (("sine", 48000, 2, SHORT, "s24le"), ["-c:a", "pcm_s24be"], "aiff", None, []),
    "pcm-s32-stereo-96000": (("sine", 96000, 2, 0.1, "s32le"), ["-c:a", "pcm_s32be"], "aiff", None, []),
    "raw-u8-mono-22050": (("sine", 22050, 1, SHORT, "u8"), ["-c:a", "pcm_u8"], "aifc", None, []),
    "fl32-stereo-44100": (("sine", 44100, 2, SHORT, "f32le"), ["-c:a", "pcm_f32be"], "aifc", None, []),
    "fl64-mono-48000": (("sine", 48000, 1, SHORT, "f64le"), ["-c:a", "pcm_f64be"], "aifc", None, []),
    "sowt-stereo-44100": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16le"], "aifc", None, []),
    "alaw-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_alaw"], "aifc", None, []),
    "ulaw-mono-8000": (("sine", 8000, 1, SHORT, "s16le"), ["-c:a", "pcm_mulaw"], "aifc", None, []),
    "ima4-stereo-44100": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "adpcm_ima_qt"], "aifc", None, []),
    "pcm-s16-stereo-text": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16be", *TAGS], "aiff", None, []),
    "pcm-s16-stereo-id3-v4": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16be", *ID3], "aiff", None, []),
    "pcm-s16-stereo-id3-v3": (("sine", 44100, 2, SHORT, "s16le"), ["-c:a", "pcm_s16be", *ID3, "-id3v2_version", "3"], "aiff", None, []),
}


def tool_version():
    return w.tool_version()


def encode(name, spec, arguments, extension, layout, work):
    kind, rate, channels, seconds, raw = spec
    target = OUT / f"external-{name}.{extension}"
    for old in (OUT / f"external-{name}.aiff", OUT / f"external-{name}.aifc"):
        old.unlink(missing_ok=True)
    command = ["ffmpeg", "-v", "error", "-y", "-f", w.RAW[raw][1], "-ar", str(rate), "-ac", str(channels)]
    if layout:
        command += ["-channel_layout", layout]
    command += ["-i", "-", *arguments, "-f", "aiff", str(work / "out.aiff")]
    result = subprocess.run(command, input=w.signal(spec), capture_output=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr.decode(errors='replace')}")
    shutil.copy(work / "out.aiff", target)
    return target


def write_manifest(path, spec, arguments, layout, findings, tool):
    manifest = m.manifest(path.read_bytes())
    manifest["findings"] = [{"code": code} for code in findings]
    manifest["tool"] = tool
    manifest["arguments"] = arguments
    manifest["input"] = {"kind": spec[0], "rate": spec[1], "channels": spec[2], "seconds": spec[3], "format": spec[4], **({"layout": layout} if layout else {})}
    path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json").write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manifests_only = "--manifests-only" in sys.argv
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for name, (spec, arguments, extension, layout, findings) in FIXTURES.items():
            if manifests_only:
                path = OUT / f"external-{name}.{extension}"
                old = path.with_name(f"external-{name}.expected.json")
                tool = json.loads(old.read_text(encoding="utf-8"))["tool"] if old.exists() else "FFmpeg 6.1.1 (ffmpeg package of Ubuntu 24.04)"
            else:
                path = encode(name, spec, arguments, extension, layout, work)
                tool = f"{tool_version()} (ffmpeg package of Ubuntu 24.04)"
            write_manifest(path, spec, arguments, layout, findings, tool)


if __name__ == "__main__":
    main()
