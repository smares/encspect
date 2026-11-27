#!/usr/bin/env python3
"""Holds the analyzer's AAC Main and AAC LTP output to the ISO/IEC 14496-26 reference waveforms of tests/conformance/aac-conformance.json
(docs/design/aac-main-ltp-er.md, "The cross-checks"; docs/development.md, "AAC Main, LTP and ER conformance").

The streams and their reference waveforms (24-bit WAV, one file for each channel where the reference is several) are downloaded into a cache directory and checked
against their SHA-256; the address list is ISO's publicly available standards site, then the private repository's copy. PcmDump (tests/Encspect.PcmDump) writes what the
decoder delivers, and compare_pcm.py compares it by position with the reference read as floats, within the bound of the float cross-checks: per channel the RMS of the
difference at most 2^-15 / sqrt(12) of full scale and its largest magnitude at most 2^-14 (docs/design/lossy-decoders.md, "The PCM cross-checks").

    python3 scripts/fixtures/check_aac_conformance.py [--pcmdump CMD] [cache directory]

Exit status 0 where every stream is within the bound, 1 where one is not or one cannot be fetched.
"""

import array
import json
import pathlib
import shlex
import struct
import subprocess
import sys
import tempfile
import urllib.parse

import downloads

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
MANIFEST = ROOT / "tests" / "conformance" / "aac-conformance.json"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")


def addresses(entry):
    return [entry["url"], downloads.private(urllib.parse.quote(entry["private"]))]


def wav_samples(path):
    """The samples of a mono or multichannel PCM WAV file as floats at full scale 1.0, and its channel count (16 or 24 bits)."""
    data = path.read_bytes()
    position = 12
    channels = bits = 0
    while position + 8 <= len(data):
        tag, size = data[position:position + 4], struct.unpack("<I", data[position + 4:position + 8])[0]
        if tag == b"fmt ":
            channels, bits = struct.unpack("<H", data[position + 10:position + 12])[0], struct.unpack("<H", data[position + 22:position + 24])[0]
        if tag == b"data":
            body = data[position + 8:position + 8 + size]
            break
        position += 8 + size + (size & 1)
    else:
        raise SystemExit(f"{path}: no data chunk")
    width = bits // 8
    count = len(body) // width
    out = array.array("f", [0.0]) * count
    scale = 1.0 / (1 << (bits - 1))
    for i in range(count):
        out[i] = int.from_bytes(body[i * width:(i + 1) * width], "little", signed=True) * scale
    return out, channels


def reference_floats(files):
    """The reference as interleaved floats: the one file's samples, or the files of the channels interleaved in the order the manifest lists them."""
    channels = [wav_samples(f) for f in files]
    if len(channels) == 1:
        return channels[0][0], channels[0][1]
    length = min(len(c[0]) for c in channels)
    out = array.array("f", [0.0]) * (length * len(channels))
    for c, (samples, _) in enumerate(channels):
        out[c::len(channels)] = samples[:length]
    return out, len(channels)


def main(argv):
    arguments = list(argv)
    pcmdump = DEFAULT_PCMDUMP
    if arguments[:1] == ["--pcmdump"]:
        pcmdump = arguments[1]
        arguments = arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "aac-conformance")
    cache.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    failed = 0
    with tempfile.TemporaryDirectory() as temporary:
        for entry in manifest["samples"]:
            name = entry["file"]
            try:
                downloads.fetch(addresses(entry), entry["sha256"], cache / name)
                for reference in entry["references"]:
                    downloads.fetch(addresses(reference), reference["sha256"], cache / reference["file"])
            except SystemExit as error:
                failed += 1
                print(f"FAIL {name}: {error}")
                continue
            dump = pathlib.Path(temporary) / name
            dump.mkdir()
            result = subprocess.run([*shlex.split(pcmdump), str(dump), str(cache / name)], capture_output=True, text=True)
            if result.returncode != 0:
                failed += 1
                print(f"FAIL {name}: PcmDump exits {result.returncode}: {result.stderr[-300:]}")
                continue
            reference, channels = reference_floats([cache / r["file"] for r in entry["references"]])
            raw = dump / "reference.f32"
            raw.write_bytes(reference.tobytes())
            description = next(dump.glob("*.pcm.json"))
            outcome = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(description), "--reference", str(raw), "--reference-format", "float32", "--reference-channels", str(channels),
                                      "--json"], capture_output=True, text=True)
            if outcome.returncode == 2:
                failed += 1
                print(f"FAIL {name}: {outcome.stderr.strip()}")
                continue
            figures = json.loads(outcome.stdout)
            worst = max(figures["channels"], key=lambda c: c["max"])
            lsb = 32768.0
            text = f"RMS {max(c['rms'] for c in figures['channels']) * lsb:.4f} and largest {worst['max'] * lsb:.3f} LSB of 16 bits over {figures['compared']} positions"
            if outcome.returncode != 0:
                failed += 1
                print(f"FAIL {name}: outside the bound ({text})")
            else:
                print(f"ok   {name}: {text}")
    print(f"{len(manifest['samples'])} streams, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
