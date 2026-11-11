"""The Vorbis tools of the cross-checks, and the libvorbis source the decoder and the notices refer to.

- vorbis_pcm.c (build_pcm, decode_pcm) is built against the system's libvorbis (libvorbis-dev, libogg-dev, 1.3.7 on Ubuntu 24.04) and decodes to floats.
- libvorbis-1.3.7.tar.xz, pinned by SHA-256, is the source the decoder follows where the Vorbis I specification leaves open (BSD-3-Clause, Copyright (c) 2002-2020 Xiph.org
  Foundation). The release asset on GitHub is the file downloads.xiph.org serves. The source is not redistributed; its license allows reading it. Its COPYING is the text
  scripts/release/make_notices.py puts in THIRD-PARTY-NOTICES.md (--fetch checks the hash).
"""

import json
import pathlib
import subprocess
import tempfile

import downloads

NAME = "libvorbis-1.3.7.tar.xz"
SHA256 = "b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b"
ADDRESSES = [
    "https://github.com/xiph/vorbis/releases/download/v1.3.7/libvorbis-1.3.7.tar.xz",
    "https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz",
]


SPEC_NAME = "Vorbis_I_spec.html"
SPEC_SHA256 = "f0ffafdedc352d2a42674b01255793838a7ad6a8592db37ce3ef6df4cc8beb95"
SPEC_ADDRESSES = [
    "https://xiph.org/vorbis/doc/Vorbis_I_spec.html",
    downloads.archived("https://xiph.org/vorbis/doc/Vorbis_I_spec.html", "2026"),
]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES
    yield SPEC_NAME, SPEC_SHA256, SPEC_ADDRESSES


def fetch_spec(directory):
    path = pathlib.Path(directory) / SPEC_NAME
    downloads.fetch(SPEC_ADDRESSES, SPEC_SHA256, path)
    return path


def fetch(directory):
    path = pathlib.Path(directory) / NAME
    downloads.fetch(ADDRESSES, SHA256, path)
    return path

PCM_SOURCE = pathlib.Path(__file__).resolve().parent / "vorbis_pcm.c"


def build_pcm():
    """Compiles vorbis_pcm.c (the libvorbis decoder of the PCM cross-check) into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="vorbis-"))
    binary = directory / "vorbis_pcm"
    subprocess.run(["cc", "-O1", "-ffp-contract=off", "-o", str(binary), str(PCM_SOURCE), "-lvorbis", "-logg", "-lm"], check=True)
    return binary


def decode_pcm(binary, path, output):
    """Decodes path with libvorbis to the file output (interleaved float32) and returns (the packets, one dict each, and the stream's channels and rate)."""
    result = subprocess.run([str(binary), str(path), str(output)], capture_output=True, text=True)
    if result.returncode != 0:
        return None
    lines = [json.loads(line) for line in result.stdout.splitlines()]
    return lines[:-1], lines[-1]
