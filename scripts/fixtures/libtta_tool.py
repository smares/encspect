"""The TTA authors' encoder, built from source as a black box for the TTA fixtures: libtta-c 2.3, whose `tta` console program encodes and decodes
TTA1 files (LGPL, not redistributed), pinned by SHA-256.

- libtta-c-2.3.tar.gz from the TTA project's SourceForge downloads (tta.sourceforge.net is the project's site), by the two addresses SourceForge
  serves it from. The archive builds with its own configure script and make; nothing is patched.
- The source is not read for the parser (docs/design/decoders.md, decision 10): the program is run on inputs the scripts write and what it
  writes is read with the TTA authors' format description. Its usage text is the only documentation used (`tta -h`):
  `tta [-hebd][p password] input_file output_file`, with -e to encode, -d to decode and -ep or -dp to use a password. It encoded the 16 and 24 bit WAVE
  files of one to six channels tried; a 32-bit and an 8-bit file are refused ("not compatible file format").
"""

import pathlib
import subprocess
import tarfile
import tempfile

import downloads

NAME = "libtta-c-2.3.tar.gz"
SHA256 = "68ba6db2d494dcd4aa6832a40952b88a3d50ca1174ab0ef1e091db16992303c7"
ADDRESSES = [
    "https://downloads.sourceforge.net/project/tta/tta/libtta/libtta-c-2.3.tar.gz",
    "https://sourceforge.net/projects/tta/files/tta/libtta/libtta-c-2.3.tar.gz/download",
]
VERSION_LINE = "TTA1 lossless audio encoder/decoder, version 2.3"


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES


def build(directory=None):
    """The path of the `tta` program, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libtta-"))
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / NAME
    downloads.fetch(ADDRESSES, SHA256, archive)
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")
    source = directory / "libtta-c-2.3"
    subprocess.run(["./configure"], cwd=source, check=True, capture_output=True)
    subprocess.run(["make"], cwd=source, check=True, capture_output=True)
    program = source / "console" / "tta"
    usage = subprocess.run([str(program), "-h"], capture_output=True, text=True)
    if VERSION_LINE not in usage.stdout + usage.stderr:
        raise SystemExit(f"{program} does not print \"{VERSION_LINE}\".")
    return program


def encode(program, wav, target, password=None):
    """Encodes the WAVE file wav to target, with the password where one is given."""
    arguments = [str(program), "-ep", password] if password else [str(program), "-e"]
    result = subprocess.run([*arguments, str(wav), str(target)], capture_output=True, text=True)
    if result.returncode != 0 or not pathlib.Path(target).exists():
        raise SystemExit(f"tta failed for {wav}: {result.stdout}{result.stderr}")


def decode(program, tta, target, password=None):
    """Decodes the TTA file to the WAVE file target."""
    arguments = [str(program), "-dp", password] if password else [str(program), "-d"]
    result = subprocess.run([*arguments, str(tta), str(target)], capture_output=True, text=True)
    if result.returncode != 0 or not pathlib.Path(target).exists():
        raise SystemExit(f"tta failed to decode {tta}: {result.stdout}{result.stderr}")
