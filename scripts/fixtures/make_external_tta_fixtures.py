#!/usr/bin/env python3
"""Writes the encoder-made TTA fixtures in tests/fixtures/tta (external-*.tta).

Two encoders write them, both run as black boxes (their sources are not read, docs/design/decoders.md, decision 10):

- FFmpeg 6.1.1's TTA encoder and muxer (the ffmpeg package of Ubuntu 24.04): 8, 16 and 24 bits, 1 to 8 channels, sample rates from 8,000 to
  192,000 Hz, silence and loud noise, a stream that ends one sample into a frame (which fixes how the length of a frame is rounded), one whose
  samples fill whole frames, one frame, tags, and one without the ENCODER tag FFmpeg's muxer writes. The files are named external-<name>.tta.
- libtta-c 2.3's `tta` console program, the TTA authors' own encoder, built from its pinned source by libtta_tool.py: 16 and 24 bits, mono,
  stereo and six channels, two sample rates (32,000 and 24,000 Hz) whose frame length is not a whole number of samples and whose streams end one
  sample into a frame, one (44,100 Hz) whose frame length is whole, and a stream encoded with a password, which has audio format 2. Each file is
  decoded again by the same program, with the password where there is one, and must give back the input's samples. The program refuses 8-bit
  and 32-bit WAVE files, and writes no tag. The files are named external-libtta-<name>.tta.

The inputs are deterministic signals written by flac_signal.py.

The encoder's output depends on its version, so the files are committed and --manifests-only recomputes the manifests from them without the
encoders. Each manifest is worked out by tta_model.py from the file (written from the TTA authors' format description apart from the analyzer),
plus the encoder and its settings. crosscheck_tta_fixtures.py confirms them with ffprobe and MediaInfo.

    python3 scripts/fixtures/make_external_tta_fixtures.py [--manifests-only] [name ...]
"""

import json
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import flac_signal as sig
import libtta_tool
import tta_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "tta"

# name: (input (kind, rate, channels, bits, samples), arguments before the output).
FIXTURES = {
    "16-stereo": (("music", 44100, 2, 16, 100000), []),
    "16-mono": (("music", 44100, 1, 16, 100000), []),
    "8-stereo": (("music", 44100, 2, 8, 100000), ["-sample_fmt", "u8"]),
    "24-stereo": (("music", 48000, 2, 24, 60000), ["-sample_fmt", "s32"]),
    "16-3ch": (("music", 22050, 3, 16, 50000), []),
    "16-6ch": (("music", 22050, 6, 16, 50000), []),
    "16-8ch": (("music", 22050, 8, 16, 30000), []),
    "8000-mono": (("music", 8000, 1, 16, 20000), []),
    "11025-stereo": (("music", 11025, 2, 16, 24000), []),
    "22050-stereo": (("music", 22050, 2, 16, 50000), []),
    "32000-stereo-last-frame-one-sample": (("music", 32000, 2, 16, 66873), []),
    "96000-mono": (("music", 96000, 1, 24, 210000), ["-sample_fmt", "s32"]),
    "192000-mono": (("music", 192000, 1, 24, 220000), ["-sample_fmt", "s32"]),
    "16-stereo-whole-frames": (("music", 44100, 2, 16, 92160), []),
    "16-stereo-one-frame": (("music", 44100, 2, 16, 5000), []),
    "16-silent": (("silent", 44100, 2, 16, 100000), []),
    "16-loud-noise": (("loud", 22050, 2, 16, 50000), []),
    "16-stereo-tags": (("music", 44100, 2, 16, 50000), ["-metadata", "title=Fixture title", "-metadata", "artist=Fixture artist", "-metadata", "album=Fixture album"]),
    "16-stereo-bitexact": (("music", 44100, 2, 16, 50000), ["-fflags", "+bitexact", "-flags:a", "+bitexact"]),
}


# name: (input (kind, rate, channels, bits, samples), password or None), written by libtta-c 2.3 as external-libtta-<name>.tta. 32,000 Hz (frames of
# 33,436.7 samples) and 24,000 Hz (25,077.55) end one sample into a frame, so the frame count tells truncation from rounding; at 44,100 Hz a frame
# is 46,080 samples, a whole number. The password is a test value and no secret.
LIBTTA = {
    "16-stereo-32000-last-frame-one-sample": (("music", 32000, 2, 16, 66873), None),
    "16-mono-24000-last-frame-one-sample": (("music", 24000, 1, 16, 50155), None),
    "16-mono-44100-whole-frames": (("music", 44100, 1, 16, 92160), None),
    "24-stereo": (("music", 48000, 2, 24, 60000), None),
    "16-6ch": (("music", 22050, 6, 16, 50000), None),
    "16-loud-noise": (("loud", 22050, 2, 16, 50000), None),
    "16-stereo-password": (("music", 22050, 2, 16, 50000), "secret"),
}


# Findings about the APEv2 tag, which the model does not work out: FFmpeg writes its items in the order of the metadata, not sorted by size.
TAG_FINDINGS = {"16-stereo-tags": [{"code": "ape.items-unsorted"}]}


def tool_version(command):
    result = subprocess.run(command, capture_output=True, text=True)
    return (result.stdout or result.stderr).splitlines()[0].strip()


def describe(spec):
    kind, rate, channels, bits, count = spec
    return {"kind": kind, "rate": rate, "channels": channels, "bits": bits, "samples": count}


def encode(name, spec, arguments, work):
    target = OUT / f"external-{name}.tta"
    kind, rate, channels, bits, count = spec
    (work / "in.wav").write_bytes(sig.wav(kind, rate, channels, count, bits))
    out = work / "out.tta"
    out.unlink(missing_ok=True)
    result = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(work / "in.wav"), "-c:a", "tta", *arguments, str(out)], capture_output=True)
    if result.returncode != 0 or not out.exists() or out.stat().st_size == 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr}")
    shutil.copy(out, target)
    # "Ubuntu 24.04" is the provenance of the committed files this tool wrote; the cross-check job runs Ubuntu 26.04 (tool_versions.py).
    return target, {"tool": f"{tool_version(['ffmpeg', '-version'])} (ffmpeg package of Ubuntu 24.04)", "arguments": arguments, "input": describe(spec)}


def data_chunk(wav):
    """The samples of a WAVE file: the bytes after the header of its data chunk."""
    return wav[wav.index(b"data") + 8:]


def encode_libtta(name, spec, password, program, work):
    target = OUT / f"external-libtta-{name}.tta"
    kind, rate, channels, bits, count = spec
    wav = work / "in.wav"
    wav.write_bytes(sig.wav(kind, rate, channels, count, bits))
    out = work / "out.tta"
    out.unlink(missing_ok=True)
    libtta_tool.encode(program, wav, out, password)
    back = work / "back.wav"
    back.unlink(missing_ok=True)
    libtta_tool.decode(program, out, back, password)
    if data_chunk(back.read_bytes()) != data_chunk(wav.read_bytes()):
        raise SystemExit(f"libtta does not decode its own file for {name} to the input's samples.")
    shutil.copy(out, target)
    tool = f"libtta-c 2.3 (tta console program built from libtta-c-2.3.tar.gz, SHA-256 {libtta_tool.SHA256})"
    return target, {"tool": tool, "arguments": ["-ep", password] if password else ["-e"], "input": describe(spec)}


def ape_footer(data, end):
    """The APEv2 tag that ends data[:end], as (offset, length), or None: its footer is 32 bytes whose size counts the items and the footer, not the header."""
    if end < 32 or data[end - 32:end - 24] != b"APETAGEX":
        return None
    size = struct.unpack("<I", data[end - 20:end - 16])[0]
    flags = struct.unpack("<I", data[end - 12:end - 8])[0]
    length = size + (32 if flags & 0x80000000 else 0)
    return end - length, length


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def build_manifest(path, encoder, name):
    data = path.read_bytes()
    tag = ape_footer(data, len(data))
    end = tag[0] if tag else len(data)
    tags = [("APE", tag[0], tag[1])] if tag else []
    result = m.manifest(data, 0, end, tags, {"encoder": encoder})
    result["findings"] += TAG_FINDINGS.get(name, [])
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    ffmpeg_names = [n for n in FIXTURES if not only_manifests and (not chosen or n in chosen)]
    libtta_names = [n for n in LIBTTA if not only_manifests and (not chosen or f"libtta-{n}" in chosen)]
    if ffmpeg_names and shutil.which("ffmpeg") is None:
        raise SystemExit("Missing ffmpeg; install it, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    program = libtta_tool.build() if libtta_names else None
    with tempfile.TemporaryDirectory() as work:
        for name, (spec, arguments) in FIXTURES.items():
            path = OUT / f"external-{name}.tta"
            if name not in ffmpeg_names:
                if not path.exists() or not manifest_path(path).exists():
                    raise SystemExit(f"No fixture or manifest for {name} to take the encoder from; run the script without --manifests-only.")
                encoder = json.loads(manifest_path(path).read_text(encoding="utf-8"))["encoder"]
            else:
                path, encoder = encode(name, spec, arguments, pathlib.Path(work))
            manifest_path(path).write_text(json.dumps(build_manifest(path, encoder, name), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            count += 1
        for name, (spec, password) in LIBTTA.items():
            path = OUT / f"external-libtta-{name}.tta"
            if name not in libtta_names:
                if not path.exists() or not manifest_path(path).exists():
                    raise SystemExit(f"No fixture or manifest for libtta-{name} to take the encoder from; run the script without --manifests-only.")
                encoder = json.loads(manifest_path(path).read_text(encoding="utf-8"))["encoder"]
            else:
                path, encoder = encode_libtta(name, spec, password, program, pathlib.Path(work))
            manifest_path(path).write_text(json.dumps(build_manifest(path, encoder, name), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
