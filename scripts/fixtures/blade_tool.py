"""Builds BladeEnc 0.94.2 (Tord Jansson, 2001, LGPL) from its source release, for encoder-written fixtures.

BladeEnc encodes MPEG-1 Layer III in plain stereo or mono only, without scalefactor scaling, and
leaves ancillary data at most in its last frame, which is how mp3guessenc recognizes it. Its site,
bladeenc.mp3.no, is gone; the Wayback Machine keeps the source release, which is checked against a
pinned SHA-256. The build here writes the same bytes as the Windows build Rarewares keeps
(blade0942.zip), checked on stereo, mono and CRC-protected encodes.
"""

import pathlib
import subprocess
import tarfile
import tempfile

import downloads

URL = "http://bladeenc.mp3.no:80/source/bladeenc-0.94.2-src-stable.tar.gz"
SHA256 = "c5e9dd180f28d1865599d0755e36fa1a4c146bb5e1b7ba9ad099955261fc1164"
ADDRESSES = [downloads.archived(URL, "20010611193234")]
VERSION = "BladeEnc 0.94.2"


def build():
    """Downloads and checks the source, and compiles the encoder in a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="bladeenc-"))
    path = directory / "bladeenc.tar.gz"
    downloads.fetch(ADDRESSES, SHA256, path)
    with tarfile.open(path) as archive:
        archive.extractall(directory, filter="data")
    sources = sorted((directory / "bladeenc-0.94.2" / "bladeenc").glob("*.c"))
    binary = directory / "bladeenc"
    # The sources predate GCC 10's -fno-common default.
    subprocess.run(["gcc", "-O2", "-w", "-fcommon", "-o", str(binary), *map(str, sources), "-lm"], check=True)
    banner = subprocess.run([str(binary), "-quit"], capture_output=True, text=True, stdin=subprocess.DEVNULL).stdout
    if VERSION not in banner:
        raise SystemExit(f"{binary} is not {VERSION}")
    return binary


def encode(binary, arguments, source, path):
    result = subprocess.run([str(binary), "-quit", "-quiet", "-nocfg", *arguments, str(source), str(path)], capture_output=True, text=True,
                            stdin=subprocess.DEVNULL)
    if result.returncode != 0 or not path.exists():
        raise SystemExit(f"BladeEnc {' '.join(arguments)} failed: {result.stdout[-500:]}")
