#!/usr/bin/env python3
"""Builds MP4Box from GPAC 2.2.1's source release, pinned by its SHA-256, for the MP4 cross-checks. Ubuntu 26.04 no longer
packages GPAC, and the cross-checks are written against GPAC 2.2 (the dump format of MP4Box -diso and -dtsx), so the version
24.04 shipped is built here:

    python3 scripts/fixtures/gpac_tool.py [directory]

- The build is a minimal one: `--isomedia-only` (the ISO base media file format with its parsers, importers, exporters and
  dumps, DASH and the timed text formats), with logging, encryption (`MP4Box -crypt`), the MPEG-4 descriptor dump (the `esds` bitrates and the
  UsacConfig that -diso lists) and the Ogg feature (which the Opus box `dOps` belongs to) enabled, and every optional library left out.
  The dumps of the committed fixtures were compared with the 24.04 package's, which builds everything. It links statically, so the binary needs nothing from the system but the C library.
- The binary is put in the directory (default .cache/tools/gpac, git-ignored) as `MP4Box`, and the sources are removed. Put the
  directory on the PATH: the cross-check scripts call `MP4Box`. CI caches it like the traced faad2 (faad_trace_tool.py).
- GPAC is under the LGPL; it is built for checking this project's fixtures, not redistributed.
"""

import os
import pathlib
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

import downloads

VERSION = "2.2.1"
ARCHIVE = f"gpac_{VERSION}+dfsg1.orig.tar.xz"
SHA256 = "28bebf095d82cc641c126c934c54690def60090f13a3ca6cdb17f671f1fd91f6"
ADDRESSES = [
    f"http://archive.ubuntu.com/ubuntu/pool/universe/g/gpac/{ARCHIVE}",
    f"https://launchpad.net/ubuntu/+archive/primary/+sourcefiles/gpac/{VERSION}+dfsg1-3.1build2/{ARCHIVE}",
]
ROOT = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "tools" / "gpac"
CONFIGURE = ["--isomedia-only", "--enable-log", "--enable-crypto", "--enable-ogg", "--enable-odf", "--enable-od-parse", "--enable-od-dump", "--use-freetype=no", "--static-bin"]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield ARCHIVE, SHA256, ADDRESSES


def build(directory=ROOT):
    """Returns the MP4Box binary in directory, building it once."""
    directory = pathlib.Path(directory)
    binary = directory / "MP4Box"
    if binary.exists():
        return binary
    directory.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="gpac-") as temp:
        temp = pathlib.Path(temp)
        archive = temp / ARCHIVE
        downloads.fetch(ADDRESSES, SHA256, archive)
        with tarfile.open(archive) as tar:
            tar.extractall(temp, filter="data")
        source = temp / f"gpac-{VERSION}"
        for command in (["./configure", f"--prefix={temp / 'install'}", *CONFIGURE], ["make", f"-j{os.cpu_count() or 1}"]):
            result = subprocess.run(command, cwd=source, capture_output=True, text=True)
            if result.returncode != 0:
                sys.exit(f"{' '.join(command[:1])} failed:\n{(result.stdout + result.stderr)[-3000:]}")
        shutil.copyfile(source / "bin" / "gcc" / "MP4Box", binary)
        binary.chmod(0o755)
    return binary


def main():
    started = time.monotonic()
    binary = build(sys.argv[1] if len(sys.argv) > 1 else ROOT)
    print(f"{binary}: GPAC {VERSION}, SHA-256 of the source {SHA256}, {time.monotonic() - started:.0f} s", file=sys.stderr)
    print(binary.parent)
    return 0


if __name__ == "__main__":
    sys.exit(main())
