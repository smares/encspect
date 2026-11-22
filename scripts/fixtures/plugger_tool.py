"""Plugger+ 0.4 (Alberto Demichelis, 1998), a Windows MPEG-1 Layer III command-line encoder, run with Wine.

The archive (plugger.zip, from Rarewares' ReallyRareWares) holds plugger.exe and plugger-pro.exe,
which print "Plugger+ v0.4" and "Plugger+ Pro v0.4". The program's text says "It's freeware"; no
other license text comes with it, and the original site (members.xoom.com/mp3nkoder) is gone. It
takes -b <kbps> with a space, -dm (downmix to mono), -c, -y and -o (CRC, copyright and original
flags), and the example in its help text names -p and -e. The archive is checked against a pinned
SHA-256 and unpacked into .cache/encoders. Wine's heap breaks when TMPDIR is set, so it is removed.
"""

import hashlib
import os
import pathlib
import shutil
import subprocess
import tempfile
import zipfile

import downloads
import xing_tool

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"

ARCHIVE = "plugger.zip"
SHA256 = "c2fcd5b77603b0b3c5cdc8cd39693ea189c00fd29d383b9c2a46b9246c588c5c"
ADDRESSES = ["https://www.rarewares.org/rrw/files/mpeg/plugger.zip"]
BINARIES = {
    "plugger": ("plugger.exe", "Plugger+ v0.4", "10d8d415d2e3ce0cd4729b0fd478f32cfb18b1ff15eedd16ebe717a542343f33"),
    "plugger-pro": ("plugger-pro.exe", "Plugger+ Pro v0.4", "503c74a24e0adc3e177782002bf87ffa0d6230e7175051d76bd1d374fb242681"),
}


def fetch():
    """Downloads and checks the archive, unpacks both programs into .cache/encoders/plugger and returns
    their paths by name (plugger, plugger-pro)."""
    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    directory = CACHE / "plugger"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    binaries = {}
    with zipfile.ZipFile(archive) as files:
        for name, (member, banner, digest) in BINARIES.items():
            data = files.read(member)
            if hashlib.sha256(data).hexdigest() != digest:
                raise SystemExit(f"{member} has SHA-256 {hashlib.sha256(data).hexdigest()}, expected {digest}")
            (directory / member).write_bytes(data)
            binaries[name] = directory / member
            result = run(binaries[name], [], directory)
            if banner not in result.stdout + result.stderr:
                raise SystemExit(f"{member} is not {banner}: {(result.stdout + result.stderr)[:200]}")
    return binaries


def run(binary, arguments, directory):
    environment = {k: v for k, v in os.environ.items() if k != "TMPDIR"} | {"WINEPREFIX": str(xing_tool.WINE_PREFIX), "WINEDEBUG": "-all"}
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True, text=True,
                          stdin=subprocess.DEVNULL, timeout=600, errors="replace")


def encode(binary, arguments, source, path):
    """Encodes source to path from a temporary directory, where Wine reads and writes plain names."""
    with tempfile.TemporaryDirectory() as temp:
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        result = run(binary, [*arguments, "in.wav", "out.mp3"], temp)
        output = pathlib.Path(temp) / "out.mp3"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"{binary.name} {' '.join(arguments)} failed: {(result.stdout + result.stderr)[-500:]}")
        shutil.move(output, path)
