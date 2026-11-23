"""8hz-MP3 0.2b (04J), the ISO dist10 encoder that 8hz Productions sped up in 1998 and Jun Somekawa
carried on for Win32 in 1999, run with Wine.

The archive (8hz-mp3_02b.zip, from Rarewares' ReallyRareWares) holds the command-line program, whose
README says the programs "are available ... without any license fee or royalty on an 'as is' basis".
It reads WAV files (MPEG-1 Layer III only, 32, 44.1 and 48 kHz in its README; the 04J build also
takes raw PCM with -f) and takes -b <kbps>, -c and -o (copyright and original flags, which its
README says the stream does not carry), -q <0..30> and the L3Enc options -br, -crc, -hq and -dm. The
archive is checked against a pinned SHA-256 and unpacked into .cache/encoders. Wine's heap breaks
when TMPDIR is set, so it is removed.
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

ARCHIVE = "8hz-mp3_02b.zip"
SHA256 = "ce18892a6c15d3085baac26675b1506a479d5443341686b7574cb8875a0b7522"
ADDRESSES = ["https://www.rarewares.org/rrw/files/mpeg/8hz-mp3_02b.zip"]
BINARY_SHA256 = "41522ebe8b067af8c13a5309e4703ede7c5c32eb1ec81fac7e7005831d107aae"
BANNER = "for Win32(04J) 1998,1999 Jun Somekawa"


def fetch():
    """Downloads and checks the archive, unpacks 8hz-mp3.exe into .cache/encoders/8hz-mp3 and returns its path."""
    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    directory = CACHE / "8hz-mp3"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    with zipfile.ZipFile(archive) as files:
        data = files.read("8hz-mp3.exe")
    if hashlib.sha256(data).hexdigest() != BINARY_SHA256:
        raise SystemExit(f"8hz-mp3.exe has SHA-256 {hashlib.sha256(data).hexdigest()}, expected {BINARY_SHA256}")
    binary = directory / "8hz-mp3.exe"
    binary.write_bytes(data)
    result = run(binary, ["-h"], directory)
    banner = result.stdout + result.stderr
    if BANNER not in banner:
        raise SystemExit(f"{binary} is not 8hz-MP3 0.2b-04J: {banner[:200]}")
    return binary


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
            raise SystemExit(f"8hz-mp3 {' '.join(arguments)} failed: {(result.stdout + result.stderr)[-500:]}")
        shutil.move(output, path)
