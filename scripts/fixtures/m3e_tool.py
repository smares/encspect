"""M3E 0.99a (Wata-Ken, 2000-03-04), the Japanese Layer III encoder with its own VBR method, run with Wine.

The archive (M3E_B099a.zip, from Rarewares' ReallyRareWares) holds the GUI and the command-line
encoder m3ec.exe (the GUI's DLL is not needed), whose help text is in Shift-JIS (Rarewares' m3ec-transl.txt translates it). Its
Japanese help page calls the version a beta, forbids redistributing the program and disclaims
responsibility; it says nothing of what the encoder writes. The command line takes -m<s|j|m>, -b
<kbps>, -q <0..3>, -v <0..4> (variable bitrate) and -a1, -c, -f, -i, -r and -s1 (the translation
lists them). The archive is checked against a pinned SHA-256 and unpacked into .cache/encoders.
Wine's heap breaks when TMPDIR is set, so it is removed.
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

ARCHIVE = "M3E_B099a.zip"
SHA256 = "7a45fc29c5b73418d3adf858a919c7f1a5ed6cbd9b5ea80cc0223739e5d8d761"
ADDRESSES = ["https://www.rarewares.org/rrw/files/mpeg/M3E_B099a.zip"]
BINARY_SHA256 = "1b15bb5139e21c8c2425ee8d757316390a869044c95522c31986aeb2460dbd91"
BANNER = "m3ec [-m<mode>][-b <br>][-q [0|1|2]] [-v1] <WAV-file> [<MP3-file>]"


def fetch():
    """Downloads and checks the archive, unpacks m3ec.exe into .cache/encoders/m3e and returns the path of m3ec.exe."""
    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    directory = CACHE / "m3e"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    with zipfile.ZipFile(archive) as files:
        data = files.read("m3ec.exe")
    if hashlib.sha256(data).hexdigest() != BINARY_SHA256:
        raise SystemExit(f"m3ec.exe has SHA-256 {hashlib.sha256(data).hexdigest()}, expected {BINARY_SHA256}")
    binary = directory / "m3ec.exe"
    binary.write_bytes(data)
    result = run(binary, ["-h"], directory)
    banner = result.stdout + result.stderr
    if BANNER not in banner:
        raise SystemExit(f"{binary} is not M3E 0.99a's m3ec: {banner[:200]}")
    return binary


def run(binary, arguments, directory):
    environment = {k: v for k, v in os.environ.items() if k != "TMPDIR"} | {"WINEPREFIX": str(xing_tool.WINE_PREFIX), "WINEDEBUG": "-all"}
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True,
                          stdin=subprocess.DEVNULL, timeout=600, text=True, encoding="shift_jis", errors="replace")


def encode(binary, arguments, source, path):
    """Encodes source to path from a temporary directory, where Wine reads and writes plain names."""
    with tempfile.TemporaryDirectory() as temp:
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        result = run(binary, [*arguments, "in.wav", "out.mp3"], temp)
        output = pathlib.Path(temp) / "out.mp3"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"m3ec {' '.join(arguments)} failed: {(result.stdout + result.stderr)[-500:]}")
        shutil.move(output, path)
