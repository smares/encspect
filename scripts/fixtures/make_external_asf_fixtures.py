#!/usr/bin/env python3
"""Writes the encoder-made WMA fixtures into tests/fixtures/asf (external-ffmpeg-*): ffmpeg 6.1's
wmav1 and wmav2 encoders on two seconds of the deterministic signal of wma_signals.py, mono and
stereo, at 22.05, 32 and 44.1 kHz and from 24 to 128 kbit/s. ffmpeg's encoder writes superframes of
the container's block alignment with no bit reservoir and no variable block lengths, pads them with
'N' bytes and sets the encode options to 1 (exponent VLC), so its files do not reach the reservoir,
the variable block sizes or the LSP exponents; Microsoft's encoder files (external-windows-*, see
import_windows_wma_fixtures.py) and the hand-built ones do.

Each external-*.expected.json holds what the tools say of the file: ffprobe's stream and packet count
and ffmpeg's decoded sample count, which the tests hold the analyzer's frame count to, plus the
encoder, its version and the command.

    python3 scripts/fixtures/make_external_asf_fixtures.py --manifests-only

rewrites the manifests from the committed files without the encoder or ffprobe, keeping what only
the tools give (the "ffprobe" part). Needs ffmpeg 6.1 and ffprobe otherwise.
"""

import argparse
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import wma_signals

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parents[1] / "tests" / "fixtures" / "asf"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
SECONDS = 2

# name, codec, channels, rate, bit rate
FIXTURES = [
    ("external-ffmpeg-wmav2-stereo-44k-128k", "wmav2", 2, 44100, 128000),
    ("external-ffmpeg-wmav2-stereo-44k-64k", "wmav2", 2, 44100, 64000),
    ("external-ffmpeg-wmav2-stereo-44k-32k", "wmav2", 2, 44100, 32000),
    ("external-ffmpeg-wmav2-mono-44k-48k", "wmav2", 1, 44100, 48000),
    ("external-ffmpeg-wmav2-stereo-22k-32k", "wmav2", 2, 22050, 32000),
    ("external-ffmpeg-wmav2-stereo-32k-64k", "wmav2", 2, 32000, 64000),
    ("external-ffmpeg-wmav1-stereo-44k-128k", "wmav1", 2, 44100, 128000),
    ("external-ffmpeg-wmav1-mono-22k-24k", "wmav1", 1, 22050, 24000),
    ("external-ffmpeg-wmav1-stereo-44k-48k", "wmav1", 2, 44100, 48000),
]


def run(command):
    return subprocess.run(command, check=True, capture_output=True, text=True).stdout


def probe(path):
    streams = json.loads(run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)]))
    stream = streams["streams"][0]
    packets = json.loads(run(["ffprobe", "-v", "error", "-select_streams", "0", "-show_packets", "-of", "json", str(path)]))["packets"]
    decoded = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"], check=True, capture_output=True).stdout
    channels = int(stream["channels"])
    return {
        "codec": stream["codec_name"],
        "channels": channels,
        "rate": int(stream["sample_rate"]),
        "block_align": int(stream["block_align"]) if "block_align" in stream else None,
        "bit_rate": int(stream["bit_rate"]),
        "extradata_size": int(stream.get("extradata_size", 0)),
        "packets": len(packets),
        "packet_sizes": sorted({int(p["size"]) for p in packets}),
        "decoded_samples": len(decoded) // (2 * channels),
        "file_bytes": pathlib.Path(path).stat().st_size,
    }


def manifest(name, codec, channels, rate, bit_rate, path, command, tools):
    return {
        "description": f"ffmpeg's {codec} encoder: {SECONDS} seconds of the test signal, {channels} channel{'s' if channels > 1 else ''}, {rate} Hz, {bit_rate // 1000} kbit/s.",
        "format": "ASF",
        "encoder": FFMPEG_VERSION,
        "command": command,
        "input": {"channels": channels, "rate": rate, "bits": 16, "seconds": SECONDS, "kind": "tones", "generator": "scripts/fixtures/wma_signals.py"},
        "sha256": hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest(),
        "reproducible": False,
        "ffprobe": tools,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifests-only", action="store_true")
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    for name, codec, channels, rate, bit_rate in FIXTURES:
        path = OUT / f"{name}.wma"
        command = ["ffmpeg", "-i", "tones.wav", "-c:a", codec, "-b:a", str(bit_rate), "-fflags", "+bitexact", "-flags:a", "+bitexact", f"{name}.wma"]
        if not args.manifests_only:
            with tempfile.TemporaryDirectory() as tmp:
                wav = pathlib.Path(tmp) / "tones.wav"
                wma_signals.write(wav, rate, channels, 16, SECONDS, "tones")
                run(["ffmpeg", "-v", "error", "-y", "-i", str(wav), "-c:a", codec, "-b:a", str(bit_rate), "-fflags", "+bitexact", "-flags:a", "+bitexact", str(path)])
        elif not path.exists():
            raise SystemExit(f"{path} is missing.")
        manifest_path = OUT / f"{name}.expected.json"
        if args.manifests_only and manifest_path.exists():
            # What only the tools give stays; the rest comes from the file.
            tools = json.loads(manifest_path.read_text(encoding="utf-8"))["ffprobe"]
            tools["file_bytes"] = path.stat().st_size
        else:
            tools = probe(path)
        manifest_path.write_text(json.dumps(manifest(name, codec, channels, rate, bit_rate, path, command, tools), indent=1) + "\n", encoding="utf-8")
        print(f"{name}: {path.stat().st_size} bytes")
    # Microsoft's encoder files (import_windows_wma_fixtures.py): the same figures, from the files
    # and the provenance record the importer wrote next to each.
    for provenance_path in sorted(OUT.glob("external-windows-*.provenance.json")):
        name = provenance_path.name[:-len(".provenance.json")]
        path = OUT / f"{name}.wma"
        provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
        manifest_path = OUT / f"{name}.expected.json"
        if manifest_path.exists() and args.manifests_only:
            tools = json.loads(manifest_path.read_text(encoding="utf-8"))["ffprobe"]
            tools["file_bytes"] = path.stat().st_size
        else:
            tools = probe(path)
        result = {
            "description": provenance["description"],
            "format": "ASF",
            "encoder": provenance["encoder"]["tool"],
            "encoder_path": provenance["encoder"]["path"],
            "mode": provenance["encoder"]["mode"],
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "reproducible": False,
            "ffprobe": tools,
        }
        manifest_path.write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
        print(f"{name}: {path.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
