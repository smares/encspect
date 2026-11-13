#!/usr/bin/env python3
"""An optional, local third decoder for the E-AC-3 hand-built fixtures: oxideav-ac3 (MIT, Rust), used as a black box beside FFmpeg and Core Audio.

    python3 scripts/fixtures/crosscheck_ac3_oxideav.py [--pcmdump CMD] [--keep DIR] [FIXTURE ...]

Not part of CI (it needs a Rust toolchain and a clone of github.com/OxideAV/oxideav-ac3); docs/development.md ("oxideav-ac3") says what it is for and where it is
not a reference. The clone is made into the download cache (.cache/oxideav-ac3, or ENCSPECT_DOWNLOAD_CACHE) and checked out at COMMIT, which is its own pin: a
commit ID names its content, so a moved branch or a rewritten history cannot change what runs (downloads.py pins files by SHA-256; a git checkout has no
single file to pin, and GitHub's archive of a commit is not byte-stable). None of its code is used here: `oxideav_dump_pcm.rs` is this repository's own harness,
which calls the crate's public decode function and writes 16-bit samples, so no notice is needed.

Each fixture is repeated to about a million samples and compared with the analyzer's PCM by the energy of bands (compare_pcm.py --bands, the bands and the floor
of crosscheck_ac3_coreaudio.py). The channels of both are in the coded order.
"""

import argparse
import array
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_ac3_pcm as x  # noqa: E402

REPOSITORY = "https://github.com/OxideAV/oxideav-ac3.git"
COMMIT = "7c28fcd75e95a00a80ae8930e4a7ee4995856fcb"
BANDS = "400,800,1600,3200,5700,6400,8000,10200,12500,12800,20000"
FLOOR_DB = -90
# The fixtures that hold what oxideav-ac3 decodes: the transform, spectral extension, enhanced coupling. Its rematrixing reads differently from the analyzer's and from
# Core Audio's in bins 25 to 72 (about 0.5 to 1.7 kHz), so a file with rematrixing there is not a reference for that range.
DEFAULT = ["eac3-signal-aht", "eac3-signal-aht-hebap4", "eac3-signal-spx", "eac3-signal-ecpl", "eac3-signal-ecpl-flat", "eac3-signal-ecpl-quiet", "eac3-signal-ecpl-flat-quiet"]


def checkout():
    cache = pathlib.Path(os.environ.get("ENCSPECT_DOWNLOAD_CACHE") or HERE.parents[1] / ".cache") / "oxideav-ac3"
    if not (cache / ".git").exists():
        cache.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "--quiet", REPOSITORY, str(cache)], check=True)
    if subprocess.run(["git", "-C", str(cache), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip() != COMMIT:
        subprocess.run(["git", "-C", str(cache), "fetch", "--quiet", "origin", COMMIT], check=True)
        subprocess.run(["git", "-C", str(cache), "checkout", "--quiet", "--detach", COMMIT], check=True)
    examples = cache / "examples"
    examples.mkdir(exist_ok=True)
    shutil.copy(HERE / "oxideav_dump_pcm.rs", examples / "dump_pcm.rs")
    subprocess.run(["cargo", "build", "--quiet", "--release", "--example", "dump_pcm"], cwd=cache, check=True)
    return cache / "target" / "release" / "examples" / "dump_pcm"


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--pcmdump", default=x.DEFAULT_PCMDUMP)
    parser.add_argument("--keep", type=pathlib.Path)
    parser.add_argument("fixtures", nargs="*", default=DEFAULT)
    args = parser.parse_args()
    tool = checkout()
    failed = 0
    with tempfile.TemporaryDirectory() as temporary:
        root = args.keep or pathlib.Path(temporary)
        for name in args.fixtures:
            path = x.FIXTURES / f"{name}.eac3"
            data = path.read_bytes()
            manifest = json.loads((x.FIXTURES / f"{name}.expected.json").read_text(encoding="utf-8"))
            frames = x.manifest_frames(manifest)
            samples = sum(x.m.header(data, offset)["blocks"] * 256 for offset, _ in frames)
            long_data, _ = x.repeated(data, frames, max(2, x.REPEATED_SAMPLES // samples))
            work = root / name
            work.mkdir(parents=True, exist_ok=True)
            long_path = work / f"{name}.repeated.eac3"
            long_path.write_bytes(long_data)
            dump = x.pcmdump(args.pcmdump, long_path, work)
            raw = work / "oxideav.s16"
            output = subprocess.run([str(tool), str(long_path), str(raw)], capture_output=True, text=True, check=True).stdout.strip()
            channels = int(output.split("channels=")[1].split()[0])
            pcm = array.array("h")
            pcm.frombytes(raw.read_bytes())
            reference = array.array("f", (v / 32768.0 for v in pcm)).tobytes()
            problems, result = x.compare(dump, reference, channels, "oxideav", ["--bands", BANDS, "--no-sample-bounds", "--band-floor-db", str(FLOOR_DB)])
            print(f"{name}: {output}: {'agrees by band energy within 0.5 dB' if not problems else 'differs: ' + '; '.join(problems)}")
            if result and "bands" in result:
                for number, channel in enumerate(result["bands"]["channels"]):
                    for band in channel["bands"]:
                        if band["status"] == "outside":
                            print(f"  channel {number} band {band['band']}: {band['differenceDb']} dB")
            failed += bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
