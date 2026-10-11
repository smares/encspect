"""Xing Technology's command-line MPEG audio encoder, tompg 3.0 (1997), for encoder-written fixtures.

Xing's encoder never uses short blocks, so mp3guessenc names its Layer III streams "Xing (very
old)" or "Xing (old)"; its successor, the Helix MP3 Encoder, does use them. tompg is a 32-bit
Windows console program that encodes Layers I, II and III, run here with Wine. Rarewares keeps the
release, and the Wayback Machine a copy; both are checked against a pinned SHA-256. The package
carries no licence terms; the fixtures encode signals this project generates.
"""

import hashlib
import io
import os
import pathlib
import shutil
import subprocess
import tempfile
import zipfile

import downloads

URL = "https://www.rarewares.org/rrw/files/xing/tompg.zip"
SHA256 = "5127cf0fab9e9abac11108e6ecde0ff08542a502a818f52c361766100c4a8609"
ADDRESSES = [URL, downloads.archived(URL, "20260620075514")]
BINARY_SHA256 = "64cb19fbe7eda18dc25c7db9c55ceb58c998f77e7d98b50a0747cce91ba937fb"
VERSION = "file-file MPEG Layer I/II/III audio encode v3.0/32"


# One Wine prefix for every Wine tool and run, outside the repository: a new prefix takes over a
# gigabyte, and one links z: to /, which tools walking the tree would follow.
WINE_PREFIX = pathlib.Path(tempfile.gettempdir()) / "encspect-wine"


def fetch():
    """Downloads the release, checks it, and unpacks tompg.exe into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="tompg-"))
    path = directory / "tompg.zip"
    downloads.fetch(ADDRESSES, SHA256, path)
    with zipfile.ZipFile(io.BytesIO(path.read_bytes())) as archive:
        binary = directory / "tompg.exe"
        binary.write_bytes(archive.read("tompg.exe"))
    if hashlib.sha256(binary.read_bytes()).hexdigest() != BINARY_SHA256:
        raise SystemExit(f"tompg.exe has SHA-256 {hashlib.sha256(binary.read_bytes()).hexdigest()}, expected {BINARY_SHA256}")
    banner = run(binary, [], directory).stdout
    if VERSION not in banner:
        raise SystemExit(f"64cb19fbe7eda18dc25c7db9c55ceb58c998f77e7d98b50a0747cce91ba937fb is not {VERSION}: {banner[:200]}")
    return binary


def run(binary, arguments, directory):
    environment = dict(os.environ, WINEPREFIX=str(WINE_PREFIX), WINEDEBUG="-all")
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True, text=True,
                          stdin=subprocess.DEVNULL, timeout=600)


def encode(binary, arguments, source, path):
    """Encodes source to path; tompg reads and writes 8.3 names in its working directory."""
    with tempfile.TemporaryDirectory() as temp:
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        result = run(binary, ["in.wav", "out.mpa", *arguments], temp)
        output = pathlib.Path(temp) / "out.mpa"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"tompg {' '.join(arguments)} failed: {result.stdout[-500:]}")
        shutil.move(output, path)
