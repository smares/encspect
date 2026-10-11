"""Gogo-no-coda 3.12 (2003, LGPL), a fork of LAME 3.88 with its slowest parts in x86 assembler, for
encoder-written fixtures.

Gogo writes no LAME tag. It fills the bits no frame uses with "GOGO" over and over, and in VBR
writes a Xing header with "GOGO" where the quality belongs, which is how mp3guessenc recognizes it.
Rarewares keeps the Windows release (command-line encoder and DLL), which runs here with Wine; it is
checked against a pinned SHA-256. Gogo picks its assembler routines by the processor's features
(MMX, 3DNow!, SSE, SSE2), so another processor may write other bytes; the fixtures are committed.
The original sites (marinecat.net, nifty.com) are gone.
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
import xing_tool

URL = "https://www.rarewares.org/rrw/files/gogo/gogo3.12.zip"
SHA256 = "aa3c76fc72a796923e8425ad88ecdacd98778e55e83eb6c23269a134cf2e1cef"
ADDRESSES = [URL]
FILES = {"gogo_8hz.exe": "e48dccad4beec1ae03b762c0bc67f675f7b7778e90ac6f7789274ad601a597a6",
         "gogo.dll": "3d18bf40e5b6a49b03f25ed6daa46ecf2f289baa77112c72c75db3d6e2104d36"}
VERSION = "GOGO-no-coda ver. 3.12"


def fetch():
    """Downloads the release, checks it, and unpacks the encoder and its DLL into a temporary
    directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="gogo-"))
    path = directory / "gogo3.12.zip"
    downloads.fetch(ADDRESSES, SHA256, path)
    with zipfile.ZipFile(io.BytesIO(path.read_bytes())) as archive:
        for name, digest in FILES.items():
            data = archive.read(name)
            if hashlib.sha256(data).hexdigest() != digest:
                raise SystemExit(f"{name} has SHA-256 {hashlib.sha256(data).hexdigest()}, expected {digest}")
            (directory / name).write_bytes(data)
    binary = directory / "gogo_8hz.exe"
    result = run(binary, [], directory)
    banner = result.stdout + result.stderr
    if VERSION not in banner:
        raise SystemExit(f"{binary} is not {VERSION}: {banner[:200]}")
    return binary


def run(binary, arguments, directory):
    environment = dict(os.environ, WINEPREFIX=str(xing_tool.WINE_PREFIX), WINEDEBUG="-all")
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True,
                          stdin=subprocess.DEVNULL, timeout=600, encoding="shift_jis", errors="replace")


def encode(binary, arguments, source, path):
    """Encodes source to path, from a copy in a temporary directory next to the DLL."""
    with tempfile.TemporaryDirectory() as temp:
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        for name in FILES:
            shutil.copy(binary.parent / name, pathlib.Path(temp) / name)
        result = run(pathlib.Path(temp) / binary.name, ["in.wav", "out.mp3", *arguments, "-silent"], temp)
        output = pathlib.Path(temp) / "out.mp3"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"gogo {' '.join(arguments)} failed: {result.stdout[-500:]}")
        shutil.move(output, path)
