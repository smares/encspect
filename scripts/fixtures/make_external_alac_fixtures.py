#!/usr/bin/env python3
"""Writes the encoder-made ALAC fixtures in tests/fixtures/alac (external-*.m4a and external-*.mka) and the manifests of Apple's afconvert files.

Three encoders write them, all run as black boxes (their sources are not read for FFmpeg, docs/design/decoders.md, decision 10):

- FFmpeg 6.1.1's ALAC encoder and MP4 muxer (the ffmpeg package of Ubuntu 24.04), files named external-ffmpeg-<name>.m4a: 16 and 24 bits, 1 to 8 channels,
  the compression levels 0 (every frame uncompressed) and 1, a stream that ends part way into a frame, one that fills whole frames, one frame, and a
  stream whose loud part is stored uncompressed (escape frames) beside frames that are predicted. The encoder writes no 20-bit or 32-bit stream (a 32-bit
  input gives a 24-bit stream), which the hand-built fixtures of make_alac_fixtures.py cover.
- Apple's own reference encoder, `alacconvert` built unpatched from the pinned source by alac_tool.py, files named external-alacconvert-<name>.m4a: it
  writes CAF files, which MP4Box (GPAC) copies into an MP4 file without touching a frame (`-add`, the muxer is the only other tool; it rounds the media's
  duration up to whole frames). 16, 24 and 32 bits,
  1, 2, 3, 6 and 8 channels, streams that end part way into a frame.
- mkvmerge (MKVToolNix) copies an MP4 file of ALAC into Matroska, files named external-mkvmerge-<name>.mka.

Each file is decoded again with alacconvert and with ffmpeg and must give back the input's samples; the manifest records the hashes of the input's channels,
which crosscheck_alac_fixtures.py compares again with the pinned tools. The inputs are deterministic signals written by flac_signal.py.

The files of Apple's afconvert (apple-alac-m4a-*.m4a, made on a macOS runner by the apple-fixtures workflow) get manifests here too.

The encoders' output depends on their version, so the files are committed and --manifests-only recomputes the manifests from them without the encoders. Each
manifest is worked out by alac_model.py from the file (a reading of Apple's source apart from the analyzer), plus the encoder and its settings.

    python3 scripts/fixtures/make_external_alac_fixtures.py [--manifests-only] [name ...]
"""

import hashlib
import json
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import alac_model as m
import alac_tool
import flac_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "alac"

WIDTH = {16: 2, 24: 3, 32: 4}

# name: (input (kind, rate, channels, bits, samples), arguments before the output). A kind "mixed" is music with a stretch of loud noise in the middle.
# FFmpeg 6.1.1's encoder takes 1, 2, 4, 5, 6 and 7 channels and mixes any other input down (3 to stereo, 8 to 7): the layouts named below are given to the input
# (-ch_layout), so that no channel is mixed. alacconvert writes 3 and 8 channels.
LAYOUTS = {4: "4.0", 5: "5.0", 6: "5.1", 7: "6.1(back)"}
FFMPEG = {
    "16-stereo": (("music", 44100, 2, 16, 100000), []),
    "16-mono": (("music", 44100, 1, 16, 100000), []),
    "24-stereo": (("music", 48000, 2, 24, 60000), []),
    "24-mono": (("music", 48000, 1, 24, 30000), []),
    "16-4ch": (("music", 22050, 4, 16, 30000), []),
    "16-5ch": (("music", 22050, 5, 16, 30000), []),
    "16-6ch": (("music", 22050, 6, 16, 30000), []),
    "16-7ch": (("music", 22050, 7, 16, 30000), []),
    "24-6ch": (("music", 48000, 6, 24, 20000), []),
    "16-stereo-last-frame-one-sample": (("music", 44100, 2, 16, 8193), []),
    "16-stereo-whole-frames": (("music", 44100, 2, 16, 16384), []),
    "16-stereo-one-frame": (("music", 44100, 2, 16, 3000), []),
    "16-stereo-escape-frames": (("mixed", 44100, 2, 16, 20000), []),
    "16-stereo-level-0": (("music", 44100, 2, 16, 30000), ["-compression_level", "0"]),
    "16-stereo-level-1": (("music", 44100, 2, 16, 30000), ["-compression_level", "1"]),
    "16-stereo-order-20": (("music", 44100, 2, 16, 30000), ["-min_prediction_order", "20", "-max_prediction_order", "20"]),
    "16-silent": (("silent", 44100, 2, 16, 30000), []),
    "16-loud-noise": (("loud", 22050, 2, 16, 20000), []),
}

# name: (input (kind, rate, channels, bits, samples)), written by alacconvert as external-alacconvert-<name>.m4a.
ALACCONVERT = {
    "16-stereo": ("music", 44100, 2, 16, 50000),
    "16-mono": ("music", 44100, 1, 16, 50000),
    "24-stereo": ("music", 48000, 2, 24, 30000),
    "32-stereo": ("music", 48000, 2, 32, 30000),
    "16-3ch": ("music", 22050, 3, 16, 20000),
    "16-6ch": ("music", 22050, 6, 16, 20000),
    "16-8ch": ("music", 22050, 8, 16, 20000),
    "16-stereo-last-frame-one-sample": ("music", 44100, 2, 16, 8193),
    "16-silent": ("silent", 44100, 2, 16, 20000),
    "16-loud-noise": ("loud", 22050, 2, 16, 20000),
}

# name: the MP4 fixture mkvmerge copies (its stem), written as external-mkvmerge-<name>.mka. MP4Box writes a btrt box beside the alac box of its sample entry, which mkvmerge
# takes into the CodecPrivate after the 24 bytes of the configuration (`alac.cookie-extra`); the files of FFmpeg's muxer have none.
MKVMERGE = {
    "16-stereo": "external-ffmpeg-16-stereo",
    "24-stereo": "external-ffmpeg-24-stereo",
    "16-6ch": "external-ffmpeg-16-6ch",
    "16-stereo-btrt-in-cookie": "external-alacconvert-16-stereo",
}


def tool_version(command):
    result = subprocess.run(command, capture_output=True, text=True)
    return (result.stdout or result.stderr).splitlines()[0].strip()


def describe(spec):
    kind, rate, channels, bits, count = spec
    return {"kind": kind, "rate": rate, "channels": channels, "bits": bits, "samples": count}


def frames_of(spec):
    kind, rate, channels, bits, count = spec
    if kind == "mixed":
        part = count // 4
        return (list(sig.samples("music", rate, channels, part * 2, bits)) + list(sig.samples("loud", rate, channels, part, bits))
                + list(sig.samples("music", rate, channels, count - part * 3, bits)))
    return list(sig.samples(kind, rate, channels, count, bits))


def wav_of(spec):
    """A plain PCM WAVE file (alacconvert reads no WAVE_FORMAT_EXTENSIBLE header)."""
    kind, rate, channels, bits, count = spec
    width = WIDTH[bits]
    body = b"".join(v.to_bytes(width, "little", signed=True) for frame in frames_of(spec) for v in frame)
    fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * channels * width, channels * width, bits)
    return b"RIFF" + struct.pack("<I", 36 + len(body)) + b"WAVEfmt " + struct.pack("<I", 16) + fmt + b"data" + struct.pack("<I", len(body)) + body


def input_of(spec):
    """What a decoder must give back: the channels' hashes of the little-endian samples, as make_alac_fixtures.py records them."""
    kind, rate, channels, bits, count = spec
    frames = frames_of(spec)
    width = WIDTH[bits]
    return {"channels": channels, "bits": bits, "rate": rate, "samples": count, "width": width, "left_shift": 0, "decoders": ["ffmpeg", "alacconvert"],
            "channel_md5": [hashlib.md5(b"".join(f[c].to_bytes(width, "little", signed=True) for f in frames)).hexdigest() for c in range(channels)]}


def run(command, what):
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"{what} failed: {result.stderr[-400:]}")
    return result


def encode_ffmpeg(name, spec, arguments, work):
    target = OUT / f"external-ffmpeg-{name}.m4a"
    (work / "in.wav").write_bytes(wav_of(spec))
    out = work / "out.m4a"
    out.unlink(missing_ok=True)
    layout = ["-ch_layout", LAYOUTS[spec[2]]] if spec[2] in LAYOUTS else []
    run(["ffmpeg", "-y", "-loglevel", "error", *layout, "-i", str(work / "in.wav"), "-c:a", "alac", *arguments, "-fflags", "+bitexact", "-flags:a", "+bitexact", str(out)],
        f"ffmpeg {name}")
    shutil.copy(out, target)
    return target, {"tool": f"{tool_version(['ffmpeg', '-version'])} (ffmpeg package of Ubuntu 24.04)", "arguments": [*layout, *arguments] if layout else arguments, "input": describe(spec)}


def encode_alacconvert(name, spec, program, work):
    target = OUT / f"external-alacconvert-{name}.m4a"
    (work / "in.wav").write_bytes(wav_of(spec))
    caf = work / "out.caf"
    alac_tool.encode(program, work / "in.wav", caf)
    out = work / "out.m4a"
    out.unlink(missing_ok=True)
    run(["MP4Box", "-add", f"{caf}:name=", "-new", str(out)], f"MP4Box {name}")
    shutil.copy(out, target)
    tool = (f"alacconvert (Apple's ALAC reference implementation, commit {alac_tool.COMMIT}, built by alac_tool.py), "
            f"CAF copied into MP4 by {tool_version(['MP4Box', '-version'])}")
    return target, {"tool": tool, "arguments": ["alacconvert in.wav out.caf", "MP4Box -add out.caf:name= -new out.m4a"], "input": describe(spec)}


def encode_mkvmerge(name, source, work):
    target = OUT / f"external-mkvmerge-{name}.mka"
    out = work / "out.mka"
    out.unlink(missing_ok=True)
    result = subprocess.run(["mkvmerge", "--deterministic", "encspect", "-o", str(out), str(OUT / f"{source}.m4a")], capture_output=True, text=True)
    if result.returncode > 1 or not out.exists():
        raise SystemExit(f"mkvmerge {name} failed: {result.stdout[-400:]}")
    shutil.copy(out, target)
    return target, {"tool": f"{tool_version(['mkvmerge', '--version'])} (mkvtoolnix package of Ubuntu 24.04)",
                    "arguments": ["--deterministic", "encspect", f"{source}.m4a"], "source": f"{source}.m4a"}


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def provenance_encoder(path):
    document = json.loads(path.with_name(path.name.rsplit(".", 1)[0] + ".provenance.json").read_text(encoding="utf-8"))
    return {"tool": document["encoder"]["tool"], "command": document["encoder"]["command"], "provenance": path.name.rsplit(".", 1)[0] + ".provenance.json"}


def write_manifest(path, encoder, input_=None):
    extra = {"encoder": encoder}
    if input_:
        extra["input"] = input_
    result = m.manifest(path.read_bytes(), "mp4" if path.suffix == ".m4a" else "mka", extra)
    manifest_path(path).write_text(m.dump(result), encoding="utf-8")


def previous(path):
    if not path.exists() or not manifest_path(path).exists():
        raise SystemExit(f"No fixture or manifest for {path.name} to take the encoder from; run the script without --manifests-only.")
    return json.loads(manifest_path(path).read_text(encoding="utf-8"))


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]

    def wanted(label):
        return not only_manifests and (not chosen or label in chosen)

    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    program = alac_tool.build(pathlib.Path(tempfile.gettempdir()) / "alac-build") if any(wanted(f"alacconvert-{n}") for n in ALACCONVERT) else None
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        for name, (spec, arguments) in FFMPEG.items():
            if wanted(f"ffmpeg-{name}"):
                path, encoder = encode_ffmpeg(name, spec, arguments, work)
            else:
                path = OUT / f"external-ffmpeg-{name}.m4a"
                encoder = previous(path)["encoder"]
            write_manifest(path, encoder, input_of(spec))
            count += 1
        for name, spec in ALACCONVERT.items():
            if wanted(f"alacconvert-{name}"):
                path, encoder = encode_alacconvert(name, spec, program, work)
            else:
                path = OUT / f"external-alacconvert-{name}.m4a"
                encoder = previous(path)["encoder"]
            write_manifest(path, encoder, input_of(spec))
            count += 1
        for name, source in MKVMERGE.items():
            if wanted(f"mkvmerge-{name}"):
                path, encoder = encode_mkvmerge(name, source, work)
            else:
                path = OUT / f"external-mkvmerge-{name}.mka"
                encoder = previous(path)["encoder"]
            write_manifest(path, encoder, input_of(FFMPEG[source[len("external-ffmpeg-"):]][0] if source.startswith("external-ffmpeg-") else ALACCONVERT[source[len("external-alacconvert-"):]]))
            count += 1
        for path in sorted(OUT.glob("apple-alac-m4a-*.m4a")):
            write_manifest(path, provenance_encoder(path))
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
