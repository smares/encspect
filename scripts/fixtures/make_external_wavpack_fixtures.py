#!/usr/bin/env python3
"""Writes the encoder-made WavPack fixtures in tests/fixtures/wavpack (external-*.wv, and *.wvc for the
hybrid ones that have a correction file).

They come from wavpack 5.6.0 of Ubuntu 24.04 (the wavpack package, libwavpack 5.6.0, the version of
the pinned source wavpack_tool.py lists): lossless 8, 16, 24 and 32-bit integer and 32-bit floating
point, mono, stereo and multichannel with a channel mask, hybrid with and without a correction file at
several bitrates (among them targets below the lowest the encoder stores), every speed mode and extra processing level, joint stereo and left/right, high and
non-standard sample rates, DSD from a DSF file, an APEv2 tag, an MD5 digest, a RIFF header with chunks
before and after the audio, and AIFF and CAF sources. The inputs are deterministic signals written by
wavpack_signal.py.

FFmpeg 6.1's native WavPack encoder and its wv muxer write the others (FFMPEG_FIXTURES): a stream of version
0x410 with neither a new config block nor block checksums, and an APEv2 tag whose "encoder" item names
Lavf, which the analyzer weighs against the blocks.

The encoder's output depends on its version, so the files are committed and --manifests-only
recomputes the manifests from them without the encoder. Each manifest is worked out by
wavpack_model.py from the file, plus the encoder and its settings and the findings the analyzer must
report. crosscheck_wavpack_fixtures.py confirms them with wvunpack, ffprobe and libwavpack's own
reading of every block.
"""

import json
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import wavpack_model as m
import wavpack_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "wavpack"

SHORT = 24000
LIST_CHUNK = sig.chunk(b"LIST", b"INFOISFT" + (12).to_bytes(4, "little") + b"Lavf60.16.100\0" + b"\0")
BEXT = sig.chunk(b"bext", bytes(602))

# name: (input, wavpack arguments, files); an input is (kind, parameters). files lists what the encoder
# writes: the main file always, and the correction file when it creates one.
FIXTURES = {
    "lossless-16-stereo": (("wav", 44100, 2, 16, SHORT), [], ["wv"]),
    "lossless-16-stereo-blocks": (("wav", 44100, 2, 16, SHORT), ["--blocksize=2048"], ["wv"]),
    "lossless-16-stereo-small-blocks": (("wav", 44100, 2, 16, SHORT), ["--blocksize=1000"], ["wv"]),
    "lossless-16-mono": (("wav", 44100, 1, 16, SHORT), [], ["wv"]),
    "lossless-8-stereo": (("wav", 44100, 2, 8, SHORT), [], ["wv"]),
    "lossless-24-stereo": (("wav", 44100, 2, 24, SHORT), [], ["wv"]),
    "lossless-32-stereo": (("wav", 44100, 2, 32, SHORT), [], ["wv"]),
    "lossless-float-stereo": (("float", 44100, 2, SHORT), [], ["wv"]),
    "lossless-float-mono-48k": (("float", 48000, 1, SHORT), [], ["wv"]),
    "lossless-51-16": (("wav", 48000, 6, 16, SHORT, 0x3F), [], ["wv"]),
    "lossless-71-16": (("wav", 48000, 8, 16, 12000, 0x63F), [], ["wv"]),
    "lossless-3ch-unassigned": (("wav", 44100, 3, 16, SHORT, 0), [], ["wv"]),
    "lossless-4ch-two-unassigned": (("wav", 44100, 4, 16, 12000, 0), ["--channel-order=FL,FR,..."], ["wv"]),
    "lossless-16-stereo-md5": (("wav", 44100, 2, 16, SHORT), ["-m"], ["wv"]),
    "lossless-16-stereo-chunks": (("wav", 44100, 2, 16, SHORT, None, BEXT, LIST_CHUNK), [], ["wv"]),
    "lossless-16-stereo-no-wrapper": (("wav", 44100, 2, 16, SHORT), ["-r"], ["wv"]),
    "hybrid-lossy-b4": (("wav", 44100, 2, 16, SHORT), ["-b4"], ["wv"]),
    "hybrid-b4-correction": (("wav", 44100, 2, 16, SHORT), ["-b4", "-c"], ["wv", "wvc"]),
    "hybrid-mono-b3": (("wav", 44100, 1, 16, SHORT), ["-b3"], ["wv"]),
    "hybrid-mono-b3-correction": (("wav", 44100, 1, 16, SHORT), ["-b3", "-c"], ["wv", "wvc"]),
    "hybrid-kbps-b256": (("wav", 44100, 2, 16, SHORT), ["-b256"], ["wv"]),
    "hybrid-24-b8-correction": (("wav", 44100, 2, 24, SHORT), ["-b8", "-c"], ["wv", "wvc"]),
    "hybrid-32-b12-correction": (("wav", 44100, 2, 32, SHORT), ["-b12", "-c"], ["wv", "wvc"]),
    "hybrid-32-b12": (("wav", 44100, 2, 32, SHORT), ["-b12"], ["wv"]),
    "hybrid-float-b8-correction": (("float", 44100, 2, SHORT), ["-b8", "-c"], ["wv", "wvc"]),
    "hybrid-51-b3-correction": (("wav", 48000, 6, 16, SHORT, 0x3F), ["-b3", "-c"], ["wv", "wvc"]),
    "hybrid-b4-maximum": (("wav", 44100, 2, 16, SHORT), ["-b4", "-cc"], ["wv", "wvc"]),
    "hybrid-b2": (("wav", 44100, 2, 16, SHORT), ["-b2"], ["wv"]),
    "hybrid-false-stereo-b2": (("wav-identical", 44100, 16, SHORT), ["-b2"], ["wv"]),
    "hybrid-false-stereo-b4": (("wav-identical", 44100, 16, SHORT), ["-b4"], ["wv"]),
    "hybrid-b22": (("wav", 44100, 2, 16, SHORT), ["-b2.2"], ["wv"]),
    "hybrid-b4-flat-noise": (("wav", 44100, 2, 16, SHORT), ["-b4", "-s0"], ["wv"]),
    "hybrid-b4-left-right": (("wav", 44100, 2, 16, SHORT), ["-b4", "-j0", "-c"], ["wv", "wvc"]),
    "mode-fast": (("wav", 44100, 2, 16, SHORT), ["-f"], ["wv"]),
    "mode-high": (("wav", 44100, 2, 16, SHORT), ["-h"], ["wv"]),
    "mode-very-high": (("wav", 44100, 2, 16, SHORT), ["-hh"], ["wv"]),
    "mode-extra-1": (("wav", 44100, 2, 16, SHORT), ["-x"], ["wv"]),
    "mode-extra-3": (("wav", 44100, 2, 16, SHORT), ["-hx3"], ["wv"]),
    "mode-extra-6": (("wav", 44100, 2, 16, SHORT), ["-hhx6"], ["wv"]),
    "joint-stereo-off": (("wav", 44100, 2, 16, SHORT), ["-j0"], ["wv"]),
    "joint-stereo-on": (("wav", 44100, 2, 16, SHORT), ["-j1"], ["wv"]),
    "rate-96000-mono": (("wav", 96000, 1, 24, SHORT), [], ["wv"]),
    "rate-192000-mono": (("wav", 192000, 1, 24, SHORT), [], ["wv"]),
    "rate-12345": (("wav", 12345, 2, 16, 12000), [], ["wv"]),
    "rate-8000-mono-8": (("wav", 8000, 1, 8, 8000), [], ["wv"]),
    "dsd-64": (("dsf", 2822400, 2, 65536), ["--blocksize=8192"], ["wv"]),
    "dsd-64-high": (("dsf", 2822400, 2, 65536), ["-h", "--blocksize=8192"], ["wv"]),
    "dsd-64-mono": (("dsf", 2822400, 1, 32768), [], ["wv"]),
    "dsd-128": (("dsf", 5644800, 2, 32768), [], ["wv"]),
    "apev2-tag": (("wav", 44100, 2, 16, SHORT), ["-w", "Artist=Fixture artist", "-w", "Title=Fixture title", "-w", "Encoder", "-w", "Settings"], ["wv"]),
    "from-aiff": (("aiff", 44100, 2, SHORT, 16), [], ["wv"]),
    "from-caf": (("caf", 44100, 2, SHORT, 16, (100 << 16) | 2), [], ["wv"]),
    "from-caf-51": (("caf", 48000, 6, 12000, 16, (121 << 16) | 6), [], ["wv"]),
}


# name: (input, files); the encoder is ffmpeg with its defaults, which stamps the "encoder" item.
FFMPEG_FIXTURES = {
    "ffmpeg-apev2-tag": (("wav", 44100, 2, 16, SHORT), ["wv"]),
}


def make_input(spec, work):
    kind, *p = spec
    if kind == "wav":
        rate, channels, bits, count, *rest = p
        mask = rest[0] if rest else None
        before, after = (rest[1], rest[2]) if len(rest) > 2 else (b"", b"")
        data = sig.wav(rate, channels, count, bits, mask, False, before, after)
        name = "in.wav"
    elif kind == "wav-identical":
        # Stereo with the same samples in both channels, which the encoder codes once as a false stereo block.
        rate, bits, count = p
        body = sig.pcm_identical(rate, count, bits)
        fmt = struct.pack("<HHIIHH", 1, 2, rate, rate * 2 * bits // 8, 2 * bits // 8, bits)
        chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(body)) + body
        data = b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks
        name = "in.wav"
    elif kind == "float":
        rate, channels, count = p
        data = sig.wav(rate, channels, count, 32, None, True)
        name = "in.wav"
    elif kind == "aiff":
        data = sig.aiff(*p)
        name = "in.aif"
    elif kind == "caf":
        data = sig.caf(*p)
        name = "in.caf"
    else:
        data = sig.dsf(*p)
        name = "in.dsf"
    path = work / name
    path.write_bytes(data)
    return path


def encoder_version():
    return subprocess.run(["wavpack", "--version"], capture_output=True, text=True).stdout.strip()


def encode(name, spec, arguments, files, work):
    for old in OUT.glob(f"external-{name}.*"):
        old.unlink()
    source = make_input(spec, work)
    for old in work.glob("out.*"):
        old.unlink()
    result = subprocess.run(["wavpack", "-y", "-q", *arguments, str(source), "-o", str(work / "out.wv")], capture_output=True, text=True)
    if result.returncode != 0 or not (work / "out.wv").exists():
        raise SystemExit(f"wavpack failed for {name}: {result.stderr}")
    shutil.copy(work / "out.wv", OUT / f"external-{name}.wv")
    if "wvc" in files:
        shutil.copy(work / "out.wvc", OUT / f"external-{name}-wvc.wvc")
    # "Ubuntu 24.04" is the provenance of the committed files these tools wrote; the cross-check jobs run Ubuntu 26.04 (tool_versions.py).
    return {"tool": f"{encoder_version()} (wavpack package of Ubuntu 24.04)", "arguments": arguments, "input": describe(spec)}


def ffmpeg_version():
    return subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]


def encode_ffmpeg(name, spec, work):
    for old in OUT.glob(f"external-{name}.*"):
        old.unlink()
    source = make_input(spec, work)
    target = work / "ffmpeg.wv"
    target.unlink(missing_ok=True)
    result = subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(source), "-c:a", "wavpack", str(target)], capture_output=True, text=True)
    if result.returncode != 0 or not target.exists():
        raise SystemExit(f"ffmpeg failed for {name}: {result.stderr}")
    shutil.copy(target, OUT / f"external-{name}.wv")
    # "Ubuntu 24.04" is the provenance of the committed files these tools wrote; the cross-check jobs run Ubuntu 26.04 (tool_versions.py).
    return {"tool": f"{ffmpeg_version()} (ffmpeg package of Ubuntu 24.04)", "arguments": ["-c:a", "wavpack"], "input": describe(spec)}


def describe(spec):
    kind, *p = spec
    names = {"wav": ["rate", "channels", "bits", "samples", "channel_mask"], "wav-identical": ["rate", "bits", "samples"], "float": ["rate", "channels", "samples"], "aiff": ["rate", "channels", "samples", "bits"],
             "caf": ["rate", "channels", "samples", "bits", "layout_tag"], "dsf": ["rate", "channels", "bytes_per_channel"]}[kind]
    result = {"kind": kind}
    for key, value in zip(names, p):
        if isinstance(value, int):
            result[key] = value
    return result


def regions(data, path, apev2):
    """The top-level nodes: the WavPack stream and, after it, an APEv2 tag."""
    if apev2:
        tag = data.rindex(b"APETAGEX", 0, len(data) - 32)
        return tag, [{"name": "WavPack", "offset": 0, "length": tag}, {"name": "APE", "offset": tag, "length": len(data) - tag}]
    return len(data), [{"name": "WavPack", "offset": 0, "length": len(data)}]


def decode_expectation(path, result):
    """What libwavpack's decoder does with the file (crosscheck_wavpack_fixtures.py runs it): a correction file is refused alone, any other
    opens and unpacks every sample of its sets, counted as the decoder counts them (DSD as bytes, 8 to the bit samples the report counts),
    without an error."""
    if path.suffix == ".wvc":
        return {"opens": False}
    info = result["stream_infos"][0]
    is_dsd = int(result["blocks"][0]["flags"], 16) & 0x80000000 != 0
    return {"opens": True, "samples": info["samples"] // (8 if is_dsd else 1), "errors": 0}


def build_manifest(path, encoder, findings=()):
    data = path.read_bytes()
    end, top = regions(data, path, "apev2" in path.name)
    result = m.manifest(data, 0, end, {"regions": top, "encoder": encoder, "findings": [{"code": c, "contains": t} for c, t in findings]})
    result["crosscheck"] = {"valid": True, "decode": decode_expectation(path, result)}
    return result


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def main():
    only_manifests = "--manifests-only" in sys.argv
    # Names given on the command line limit which fixtures the encoders write again.
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not only_manifests:
        missing = [tool for tool in ("wavpack", "ffmpeg") if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install wavpack and ffmpeg, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    everything = {**{n: (s, a, f, False) for n, (s, a, f) in FIXTURES.items()}, **{n: (s, [], f, True) for n, (s, f) in FFMPEG_FIXTURES.items()}}
    with tempfile.TemporaryDirectory() as work:
        for name, (spec, arguments, files, is_ffmpeg) in everything.items():
            produced = {"wv": OUT / f"external-{name}.wv", "wvc": OUT / f"external-{name}-wvc.wvc"}
            if only_manifests or (chosen and name not in chosen):
                encoder = json.loads(manifest_path(produced["wv"]).read_text(encoding="utf-8"))["encoder"] if produced["wv"].exists() and manifest_path(produced["wv"]).exists() else None
                if encoder is None:
                    raise SystemExit(f"No manifest for {name} to take the encoder from; run the script without --manifests-only.")
            elif is_ffmpeg:
                encoder = encode_ffmpeg(name, spec, pathlib.Path(work))
            else:
                encoder = encode(name, spec, arguments, files, pathlib.Path(work))
            for kind in files:
                path = produced[kind]
                note = dict(encoder, file="main file" if kind == "wv" else "correction file written next to " + produced["wv"].name)
                findings = [("wavpack.correction-file", "correction data only")] if kind == "wvc" else []
                if name == "apev2-tag":
                    # wavpack writes the items in the order given, not sorted by size.
                    findings.append(("ape.items-unsorted", "is smaller than the item before it"))
                manifest_path(path).write_text(json.dumps(build_manifest(path, note, findings), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
                count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
