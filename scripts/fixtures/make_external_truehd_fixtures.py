#!/usr/bin/env python3
"""Writes the encoder-made Dolby TrueHD and MLP fixtures in tests/fixtures/truehd (external-*.thd and external-*.mlp).

They come from FFmpeg 6.1.1's TrueHD and MLP encoders (-c:a truehd, -c:a mlp, -strict -2: experimental in FFmpeg, which writes one substream for
MLP and for TrueHD of one or two channels, and two for more), from the deterministic inputs flac_signal.py writes: every sampling rate of the
encoders, 16 and 24 bits, every channel layout they take, silence, loud noise, a stream that ends inside an access unit (so that the end of
stream marker drops samples), and some of the encoders' options. The real files of tests/conformance/truehd-samples.json are downloaded, not
committed.

The encoders' output depends on their versions, so the files are committed and --manifests-only recomputes the manifests from them without
FFmpeg. Each manifest is worked out by truehd_model.py from the file (the checks, the major syncs and the restart headers, apart from the
analyzer) plus what FFmpeg's decoder gives: the samples and the MD5 of the decoded audio in the canonical layout, with its channels in the
analyzer's order. crosscheck_truehd_fixtures.py confirms them with ffprobe, ffmpeg and MediaInfo.
"""

import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import channel_layouts as layouts
import flac_signal as sig
import truehd_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "truehd"

# The WAVE channel masks of the layouts the encoders take.
MASKS = {"mono": 0x4, "stereo": 0x3, "2.1": 0xB, "3.0": 0x7, "3.1": 0xF, "4.0": 0x107, "4.1": 0x10F, "5.0": 0x37, "5.0(side)": 0x607, "5.1": 0x3F,
         "5.1(side)": 0x60F, "3.0(back)": 0x103, "quad": 0x33}
CHANNELS = {"mono": 1, "stereo": 2, "2.1": 3, "3.0": 3, "3.1": 4, "4.0": 4, "4.1": 5, "5.0": 5, "5.0(side)": 5, "5.1": 6, "5.1(side)": 6, "3.0(back)": 3, "quad": 4}

# name: (codec, rate, layout, bits, kind, samples, extra arguments of ffmpeg)
FIXTURES = {
    "truehd-mono-48k-16": ("truehd", 48000, "mono", 16, "music", 7200, []),
    "truehd-stereo-44k-16": ("truehd", 44100, "stereo", 16, "music", 6600, []),
    "truehd-stereo-48k-24": ("truehd", 48000, "stereo", 24, "music", 9600, []),
    "truehd-stereo-88k-24": ("truehd", 88200, "stereo", 24, "music", 8800, []),
    "truehd-stereo-96k-24": ("truehd", 96000, "stereo", 24, "music", 9600, []),
    "truehd-stereo-176k-16": ("truehd", 176400, "stereo", 16, "music", 8800, []),
    "truehd-stereo-192k-24": ("truehd", 192000, "stereo", 24, "music", 9600, []),
    "truehd-21-48k-16": ("truehd", 48000, "2.1", 16, "music", 4800, []),
    "truehd-31-48k-24": ("truehd", 48000, "3.1", 24, "music", 4800, []),
    "truehd-40-48k-16": ("truehd", 48000, "4.0", 16, "music", 4800, []),
    "truehd-41-48k-24": ("truehd", 48000, "4.1", 24, "music", 4800, []),
    "truehd-50-48k-24": ("truehd", 48000, "5.0(side)", 24, "music", 4800, []),
    "truehd-51-48k-24": ("truehd", 48000, "5.1(side)", 24, "music", 9600, []),
    "truehd-51-96k-24": ("truehd", 96000, "5.1(side)", 24, "music", 4800, []),
    "truehd-51-48k-16-loud": ("truehd", 48000, "5.1(side)", 16, "loud", 3200, []),
    "truehd-stereo-48k-24-silent": ("truehd", 48000, "stereo", 24, "silent", 9600, []),
    "truehd-stereo-48k-24-odd-length": ("truehd", 48000, "stereo", 24, "music", 7213, []),
    "truehd-stereo-48k-24-interval-8": ("truehd", 48000, "stereo", 24, "music", 9600, ["-max_interval", "8"]),
    "truehd-stereo-48k-24-search": ("truehd", 48000, "stereo", 24, "music", 9600, ["-prediction_order", "search", "-codebook_search", "20"]),
    "truehd-51-48k-24-cholesky": ("truehd", 48000, "5.1(side)", 24, "music", 4800, ["-lpc_type", "cholesky", "-rematrix_precision", "10"]),
    "mlp-mono-44k-16": ("mlp", 44100, "mono", 16, "music", 6600, []),
    "mlp-stereo-48k-24": ("mlp", 48000, "stereo", 24, "music", 9600, []),
    "mlp-stereo-88k-16": ("mlp", 88200, "stereo", 16, "music", 8800, []),
    "mlp-stereo-96k-16": ("mlp", 96000, "stereo", 16, "music", 9600, []),
    "mlp-stereo-176k-24": ("mlp", 176400, "stereo", 24, "music", 8800, []),
    "mlp-stereo-192k-24": ("mlp", 192000, "stereo", 24, "music", 9600, []),
    "mlp-30-back-48k-16": ("mlp", 48000, "3.0(back)", 16, "music", 4800, []),
    "mlp-quad-48k-24": ("mlp", 48000, "quad", 24, "music", 4800, []),
    "mlp-21-48k-16": ("mlp", 48000, "2.1", 16, "music", 4800, []),
    "mlp-30-48k-24": ("mlp", 48000, "3.0", 24, "music", 4800, []),
    "mlp-31-48k-16": ("mlp", 48000, "3.1", 16, "music", 4800, []),
    "mlp-40-48k-24": ("mlp", 48000, "4.0", 24, "music", 4800, []),
    "mlp-41-48k-16": ("mlp", 48000, "4.1", 16, "music", 4800, []),
    "mlp-50-48k-24": ("mlp", 48000, "5.0", 24, "music", 4800, []),
    "mlp-51-48k-24": ("mlp", 48000, "5.1", 24, "music", 9600, []),
    "mlp-51-44k-16-loud": ("mlp", 44100, "5.1", 16, "loud", 3300, []),
    "mlp-stereo-44k-16-odd-length": ("mlp", 44100, "stereo", 16, "music", 6613, []),
    "mlp-stereo-48k-24-interval-128": ("mlp", 48000, "stereo", 24, "music", 9600, ["-max_interval", "128"]),
}


def tool_version(command):
    result = subprocess.run(command, capture_output=True, text=True)
    return (result.stdout or result.stderr).splitlines()[0].strip()


def encode(name, spec, work):
    codec, rate, layout, bits, kind, count, extra = spec
    extension = "thd" if codec == "truehd" else "mlp"
    target = OUT / f"external-{name}.{extension}"
    wav = sig.wav(kind, rate, CHANNELS[layout], count, bits, None, MASKS[layout], 0)
    source = work / "in.wav"
    source.write_bytes(wav)
    out = work / f"out.{extension}"
    out.unlink(missing_ok=True)
    result = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(source), "-c:a", codec, "-strict", "-2", *extra, "-f", codec, str(out)],
                            capture_output=True, text=True)
    if result.returncode != 0 or not out.exists():
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr}")
    shutil.copy(out, target)
    # "Ubuntu 24.04" is the provenance of the committed files this FFmpeg wrote; the cross-check job runs Ubuntu 26.04 (tool_versions.py).
    return target, {"tool": f"{tool_version(['ffmpeg', '-version'])} (ffmpeg package of Ubuntu 24.04)", "codec": codec, "arguments": extra,
                    "input": {"kind": kind, "rate": rate, "layout": layout, "bits": bits, "samples": count}}


def ffmpeg_pcm(path):
    """FFmpeg's decoding of the file as 32-bit integers, with the channel layout it reports: (samples as rows, layout name, rate)."""
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-f", "truehd" if path.suffix == ".thd" else "mlp", "-select_streams", "a:0", "-show_entries",
                                       "stream=channel_layout,channels,sample_rate,bits_per_raw_sample", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    raw = subprocess.run(["ffmpeg", "-v", "error", "-f", "truehd" if path.suffix == ".thd" else "mlp", "-i", str(path), "-f", "s32le", "-"],
                         capture_output=True, check=True).stdout
    channels = int(probe["channels"])
    import struct
    values = struct.unpack(f"<{len(raw) // 4}i", raw)
    return values, channels, probe.get("channel_layout")


def same_pair(names):
    """FFmpeg names a TrueHD 5.1's surround pair 'side', the analyzer 'back' where no rear pair exists: one name for both."""
    if "side left" not in names and "back left" in names:
        return ["side left" if n == "back left" else "side right" if n == "back right" else n for n in names]
    return list(names)


def canonical(values, channels, ff_layout, ours, bits):
    """The samples in the analyzer's channel order, as little-endian integers of whole bytes (the canonical layout), and their MD5."""
    theirs = same_pair(layouts.FFMPEG[ff_layout])
    wanted = same_pair(ours)
    index = [theirs.index(n) for n in wanted]
    width = (bits + 7) // 8
    out = bytearray()
    for frame in range(len(values) // channels):
        for c in index:
            out += (values[frame * channels + c] >> (32 - bits)).to_bytes(width, "little", signed=True)
    return len(values) // channels, hashlib.md5(bytes(out)).hexdigest()


def manifest_for(path, encoder):
    data = path.read_bytes()
    units = m.split_units(data)
    assert sum(l for _, l in units) == len(data), f"{path.name}: the units do not end at the end of the file"
    values, channels, ff_layout = ffmpeg_pcm(path)
    first = m.Unit(data, units[0][0], units[0][1])
    n = first.sync.unit_samples
    total = len(values) // channels
    counts = [n] * (len(units) - 1) + [total - n * (len(units) - 1)]
    result = m.manifest(data, [("unit", o, l) for o, l in units], counts, average=True)
    info = result["stream_infos"][0]
    samples, digest = canonical(values, channels, ff_layout, info["layout"], info["bits_per_sample"])
    assert samples == total == result["decoded_samples"], f"{path.name}: the model counts {result['decoded_samples']} samples, FFmpeg {total}"
    return {**result, "encoder": encoder, "decoded_pcm_md5": digest, "regions": [{"name": "TrueHD", "offset": 0, "length": len(data)}], "findings": [],
            "sampling": {"unit_samples": n}, "crosscheck": {"ffmpeg": True}}


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def write_manifest(fixture, manifest):
    """Writes the manifest of a fixture, one unit to a line, so that a manifest of thousands of units stays readable and short."""
    path = manifest_path(fixture)
    lines = ["{"]
    keys = list(manifest)
    for i, key in enumerate(keys):
        value = manifest[key]
        comma = "," if i < len(keys) - 1 else ""
        if isinstance(value, list) and value and key in ("entries", "syncs", "restarts", "failures"):
            lines.append(f' "{key}": [')
            lines += [f"  {json.dumps(item, ensure_ascii=False)}{',' if j < len(value) - 1 else ''}" for j, item in enumerate(value)]
            lines.append(f" ]{comma}")
        else:
            lines.append(f" {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}{comma}")
    lines.append("}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not only_manifests and shutil.which("ffmpeg") is None:
        raise SystemExit("Missing ffmpeg; install it, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    with tempfile.TemporaryDirectory() as work:
        for name, spec in FIXTURES.items():
            path = OUT / f"external-{name}.{'thd' if spec[0] == 'truehd' else 'mlp'}"
            if only_manifests or (chosen and name not in chosen):
                if not path.exists() or not manifest_path(path).exists():
                    raise SystemExit(f"No fixture or manifest for {name} to take the encoder from; run the script without --manifests-only.")
                encoder = json.loads(manifest_path(path).read_text(encoding="utf-8"))["encoder"]
            else:
                path, encoder = encode(name, spec, pathlib.Path(work))
            write_manifest(path, manifest_for(path, encoder))
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
