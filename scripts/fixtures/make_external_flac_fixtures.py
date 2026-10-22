#!/usr/bin/env python3
"""Writes the encoder-made FLAC fixtures in tests/fixtures/flac (external-*.flac, and external-*-ogg.* for Ogg FLAC).

They come from flac 1.4.3 of Ubuntu 24.04 (the flac package, libFLAC 1.4.3) and FFmpeg 6.1.1's native FLAC encoder: every compression
level, block sizes the 4-bit code names and sizes it does not (stated in 8 and in 16 bits), every channel assignment, 1 to 8 channels, bit
depths of 8, 16, 20, 24 and 32 bits (among them the 33-bit side channel of a 32-bit stream), the sample rates the header codes and those it
states, silence, loud noise, samples with wasted bits, seek points, tags, a cue sheet and a picture, and Ogg FLAC. FFmpeg's encoder writes
a Vorbis comment of its own, and, to a pipe, a STREAMINFO block that does not know the MD5 or the length. The inputs are deterministic
signals written by flac_signal.py.

The encoders' output depends on their versions, so the files are committed and --manifests-only recomputes the manifests from them without
the encoders. Each manifest is worked out by flac_model.py from the file (its own parser and decoder, written from RFC 9639 apart from the
analyzer), plus the encoder and its settings. crosscheck_flac_fixtures.py confirms them with flac, metaflac, ffprobe, MediaInfo and FFmpeg.
"""

import json
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

import flac_model as m
import flac_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "flac"

SHORT = 24000

# name: (input, arguments of flac). An input is (kind, rate, channels, bits, samples, options); options may hold valid_bits and shift.
FLAC_FIXTURES = {
    "16-stereo-l0": (("music", 44100, 2, 16, 8000), ["-0"]),
    "16-stereo-l5": (("music", 44100, 2, 16, SHORT), ["-5"]),
    "16-stereo-l8": (("music", 44100, 2, 16, SHORT), ["-8"]),
    "16-stereo-l8-exhaustive": (("music", 44100, 2, 16, SHORT), ["-8", "-e"]),
    "16-stereo-adaptive-mid-side": (("music", 44100, 2, 16, SHORT), ["-8", "-M"]),
    "16-stereo-independent": (("music", 44100, 2, 16, SHORT), ["-8", "--no-mid-side"]),
    "16-stereo-b576": (("music", 44100, 2, 16, 6000), ["-5", "-b", "576"]),
    "16-stereo-b1152": (("music", 44100, 2, 16, 6000), ["-5", "-b", "1152"]),
    "16-stereo-b4608": (("music", 44100, 2, 16, SHORT), ["-5", "-b", "4608"]),
    "16-stereo-b100": (("music", 44100, 2, 16, 2400), ["-5", "-b", "100"]),
    "16-stereo-b5000": (("music", 44100, 2, 16, 15000), ["-5", "-b", "5000", "--lax"]),
    "16-stereo-b16384": (("music", 44100, 2, 16, 40000), ["-5", "-b", "16384", "--lax"]),
    "8-mono-b65535": (("music", 22050, 1, 8, 70000), ["-5", "-b", "65535", "--lax"]),
    "16-stereo-seekpoints": (("music", 44100, 2, 16, SHORT), ["-5", "-S", "0.1s"]),
    "16-stereo-no-padding": (("music", 44100, 2, 16, SHORT), ["-5", "-P", "0"]),
    "16-stereo-tags": (("music", 44100, 2, 16, SHORT), ["-5", "-T", "ARTIST=Fixture artist", "-T", "TITLE=Fixture title", "-T", "ARTIST=Second artist"]),
    "16-stereo-cuesheet-picture": (("music", 44100, 2, 16, SHORT), ["-5", "--cuesheet=cue.cue", "--picture=3|image/png|Front cover|1x1x24|cover.png", "-T", "ALBUM=Fixture album"]),
    "16-mono": (("music", 44100, 1, 16, SHORT), ["-5"]),
    "8-stereo": (("music", 44100, 2, 8, SHORT), ["-5"]),
    "20-stereo": (("music", 44100, 2, 20, SHORT, {"valid_bits": 20}), ["-5"]),
    "24-stereo": (("music", 48000, 2, 24, SHORT), ["-5"]),
    "32-stereo": (("music", 44100, 2, 32, SHORT), ["-5", "--lax"]),
    "32-opposed": (("opposed", 44100, 2, 32, 12000), ["-8", "--lax"]),
    "16-3ch": (("music", 44100, 3, 16, 8000), ["-5"]),
    "16-4ch": (("music", 44100, 4, 16, 8000), ["-5"]),
    "16-5ch": (("music", 48000, 5, 16, 8000), ["-5"]),
    "16-6ch": (("music", 48000, 6, 16, 8000), ["-5"]),
    "24-8ch": (("music", 48000, 8, 24, 8000), ["-5"]),
    "8000-mono": (("music", 8000, 1, 16, 8000), ["-5"]),
    "11025-stereo": (("music", 11025, 2, 16, 11025), ["-5"]),
    "12000-stereo": (("music", 12000, 2, 16, 12000), ["-5"]),
    "22050-stereo": (("music", 22050, 2, 16, 12000), ["-5"]),
    "32000-stereo": (("music", 32000, 2, 16, 12000), ["-5"]),
    "88200-stereo": (("music", 88200, 2, 24, 20000), ["-5"]),
    "96000-stereo": (("music", 96000, 2, 24, 20000), ["-5"]),
    "176400-stereo": (("music", 176400, 2, 24, 20000), ["-5"]),
    "192000-stereo": (("music", 192000, 2, 24, 20000), ["-5"]),
    "640000-mono": (("music", 640000, 1, 24, 20000), ["-5", "--lax"]),
    "16-silent": (("silent", 44100, 2, 16, SHORT), ["-5"]),
    "16-loud-noise": (("loud", 44100, 2, 16, 12000), ["-5"]),
    "16-wasted-bits": (("music", 44100, 2, 16, SHORT, {"shift": 3}), ["-5"]),
    "ogg-16-stereo": (("music", 44100, 2, 16, SHORT), ["-5", "--ogg"]),
    "ogg-16-mono-l8": (("music", 44100, 1, 16, SHORT), ["-8", "--ogg"]),
    "ogg-24-stereo-spanning": (("loud", 48000, 2, 24, 40000), ["-5", "--ogg", "-b", "16384", "--lax"]),
    "ogg-24-8ch-tags": (("music", 48000, 8, 24, 8000), ["-5", "--ogg", "-T", "TITLE=Ogg fixture"]),
}

# name: (input, arguments before the output). ffmpeg's native encoder; Lavf writes the vendor string and Lavc the ENCODER comment.
FFMPEG_FIXTURES = {
    "ffmpeg-16-stereo": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac"], "flac"),
    "ffmpeg-24-stereo": (("music", 48000, 2, 24, SHORT), ["-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"], "flac"),
    "ffmpeg-8ch": (("music", 48000, 8, 16, 8000), ["-c:a", "flac"], "flac"),
    "ffmpeg-mono": (("music", 48000, 1, 16, SHORT), ["-c:a", "flac"], "flac"),
    "ffmpeg-indep": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-ch_mode", "indep"], "flac"),
    "ffmpeg-left-side": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-ch_mode", "left_side"], "flac"),
    "ffmpeg-right-side": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-ch_mode", "right_side"], "flac"),
    "ffmpeg-mid-side": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-ch_mode", "mid_side"], "flac"),
    "ffmpeg-level0-fixed": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-compression_level", "0"], "flac"),
    "ffmpeg-pipe": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac"], "pipe"),
    "ogg-ffmpeg-16-stereo": (("music", 44100, 2, 16, SHORT), ["-c:a", "flac", "-f", "ogg"], "ogg"),
}

CUE = 'FILE "in.wav" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:00:20\n'


def png():
    """A one-pixel PNG, the picture of the fixture with a picture block."""
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"\x00\xc8\x64\x32", 9)) + chunk(b"IEND", b"")


def make_input(spec):
    kind, rate, channels, bits, count, *rest = spec
    options = rest[0] if rest else {}
    return sig.wav(kind, rate, channels, count, bits, options.get("valid_bits"), None, options.get("shift", 0))


def describe(spec):
    kind, rate, channels, bits, count, *rest = spec
    return {"kind": kind, "rate": rate, "channels": channels, "bits": bits, "samples": count, **(rest[0] if rest else {})}


def tool_version(command):
    result = subprocess.run(command, capture_output=True, text=True)
    return (result.stdout or result.stderr).splitlines()[0].strip()


def encode_flac(name, spec, arguments, work):
    target = OUT / f"external-{name}.{'ogg' if '--ogg' in arguments else 'flac'}"
    for old in OUT.glob(f"external-{name}.*"):
        old.unlink()
    (work / "in.wav").write_bytes(make_input(spec))
    (work / "cue.cue").write_text(CUE)
    (work / "cover.png").write_bytes(png())
    out = work / "out.flac"
    out.unlink(missing_ok=True)
    result = subprocess.run(["flac", "-s", "-f", *arguments, "in.wav", "-o", str(out)], cwd=work, capture_output=True, text=True)
    if result.returncode != 0 or not out.exists():
        raise SystemExit(f"flac failed for {name}: {result.stderr}")
    shutil.copy(out, target)
    # "Ubuntu 24.04" is the provenance of the committed files these tools wrote; the cross-check job runs Ubuntu 26.04 (tool_versions.py).
    return target, {"tool": f"{tool_version(['flac', '--version'])} (flac package of Ubuntu 24.04)", "arguments": arguments, "input": describe(spec)}


def encode_ffmpeg(name, spec, arguments, mode, work):
    suffix = "ogg" if mode == "ogg" else "flac"
    target = OUT / f"external-{name}.{'ogg' if mode == 'ogg' else 'flac'}"
    for old in OUT.glob(f"external-{name}.*"):
        old.unlink()
    (work / "in.wav").write_bytes(make_input(spec))
    out = work / f"out.{suffix}"
    out.unlink(missing_ok=True)
    if mode == "pipe":
        result = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(work / "in.wav"), *arguments, "-f", "flac", "pipe:1"], capture_output=True)
        out.write_bytes(result.stdout)
    else:
        result = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(work / "in.wav"), *arguments, str(out)], capture_output=True)
    if result.returncode != 0 or not out.exists() or out.stat().st_size == 0:
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr}")
    shutil.copy(out, target)
    return target, {"tool": f"{tool_version(['ffmpeg', '-version'])} (ffmpeg package of Ubuntu 24.04)", "arguments": arguments, "input": describe(spec)}


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def build_manifest(path, encoder):
    data = path.read_bytes()
    is_ogg = data[:4] == b"OggS"
    if is_ogg:
        return ogg_manifest(path, data, encoder)
    result = m.manifest(data, 0, len(data), {"regions": [{"name": "FLAC", "offset": 0, "length": len(data)}], "encoder": encoder})
    result["crosscheck"] = {"flac_test": True}
    return result


def ogg_manifest(path, data, encoder):
    """The FLAC stream of an Ogg file: the packets the pages hold, as the Ogg mapping lays them out (RFC 9639, section 10.1), read as a
    native stream is from a copy whose packets are joined; each metadata block and frame then takes the place of its packet in the file."""
    import ogg_model as o
    pages, _ = o.read_pages(data)
    packets = next(iter(o.stream_packets(data, pages).values()))["packets"]
    first = packets[0]["data"]
    blocks = [packets[0]]
    index = 1
    last = bool(first[13] & 0x80)
    while not last and index < len(packets):
        blocks.append(packets[index])
        last = bool(packets[index]["data"][0] & 0x80)
        index += 1
    native = b"fLaC" + first[13:] + b"".join(p["data"] for p in packets[1:])
    result = m.manifest(native, 0, len(native), {"encoder": encoder})

    def place(ranges):
        return ranges[0][0], ranges[-1][0] + ranges[-1][1] - ranges[0][0]

    # The metadata blocks and frames of the native copy, in the order of the packets that hold them.
    metadata = result["metadata"]
    frames = result["frames"]
    result["metadata"] = []
    for k, block in enumerate(metadata):
        packet = packets[k]
        ranges = [(r[0] + 13, r[1] - 13) for r in packet["ranges"][:1]] if k == 0 else packet["ranges"]
        offset, length = place(ranges)
        result["metadata"].append({**block, "offset": offset, "length": length})
    result["frames"] = []
    for k, frame in enumerate(frames):
        packet = packets[len(metadata) + k]
        offset, length = place(packet["ranges"])
        result["frames"].append({**frame, "offset": offset, "length": length, "ranges": [list(r) for r in packet["ranges"]]})
    result["length"] = len(data)
    result["skipped"] = []
    result["regions"] = [{"name": "Ogg", "offset": 0, "length": len(data)}]
    result["ogg"] = {"header_packets": (first[7] << 8) | first[8], "packets": len(packets), "last_granule": pages[-1]["granule"]}
    result["crosscheck"] = {"flac_test": True}
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not only_manifests:
        missing = [tool for tool in ("flac", "ffmpeg") if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install flac and ffmpeg, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    everything = {**{n: ("flac", s, a, None) for n, (s, a) in FLAC_FIXTURES.items()}, **{n: ("ffmpeg", s, a, mode) for n, (s, a, mode) in FFMPEG_FIXTURES.items()}}
    with tempfile.TemporaryDirectory() as work:
        for name, (tool, spec, arguments, mode) in everything.items():
            ogg = "--ogg" in arguments or mode == "ogg"
            path = OUT / f"external-{name}.{'ogg' if ogg else 'flac'}"
            if only_manifests or (chosen and name not in chosen):
                if not path.exists() or not manifest_path(path).exists():
                    raise SystemExit(f"No fixture or manifest for {name} to take the encoder from; run the script without --manifests-only.")
                encoder = json.loads(manifest_path(path).read_text(encoding="utf-8"))["encoder"]
            elif tool == "flac":
                path, encoder = encode_flac(name, spec, arguments, pathlib.Path(work))
            else:
                path, encoder = encode_ffmpeg(name, spec, arguments, mode, pathlib.Path(work))
            manifest_path(path).write_text(json.dumps(build_manifest(path, encoder), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
