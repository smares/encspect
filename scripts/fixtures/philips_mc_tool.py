"""Philips' multichannel MPEG-2 Layer II encoder 2.5 (pub_enc.exe, 16 March 1999), run with Wine.

Philips Consumer Electronics' Advanced Systems and Applications Laboratory published it with its
tools for the "MPEG-2 Multichannel" test suite, a 7.1 channel encoder and decoder built on the
ISO dist10 software (Rarewares keeps the archive, philipsmc.zip, and the table files the program
reads from a "tables" directory next to it). The archive's disclaimer.txt only provides the tools
"as is, for test purposes only, without any warranty", which is what the fixtures are used for;
it forbids nothing else. No source is read or needed.

What it does with options (tried here, 16-bit AIFF input): it reads a standard big endian AIFF
file (a WAVE file is taken for stereo) and takes the sample rate from it: 32, 44.1 and 48 kHz
encode, the rates of MPEG-2 LSF (24, 22.05, 16 kHz) fail on a missing table. The configuration is
given by -C (320, 310, 220, 210, 200, as 3/2, 3/1, 2/2, 2/1 and 2/0) and -L (a low frequency
effects channel in addition) and must match the number of channels in the file. The total
bitrate is -b, the MPEG-1 bitrate -B (both default to 384 kbps): with a total above the MPEG-1
bitrate, the multichannel extension goes to a second file, out.ext, instead of the base frames'
ancillary data. It appends .mpg to the output name.
"""

import os
import pathlib
import shutil
import struct
import subprocess
import tempfile
import wave
import zipfile
import array

import downloads
import xing_tool

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"

ARCHIVE = "philipsmc.zip"
SHA256 = "bd14ec2180ca3b7be1b5d54d0ece15c50a77b448bfa6652456f6aab611936924"
ADDRESSES = ["https://www.rarewares.org/rrw/files/mpeg/philipsmc.zip"]


def fetch():
    """Downloads and checks the archive, unpacks the encoder and its tables into .cache/encoders/philips-mc and returns the path of pub_enc.exe."""
    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    directory = CACHE / "philips-mc"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    with zipfile.ZipFile(archive) as files:
        for name in files.namelist():
            if name == "pub_enc.exe" or name == "disclaimer.txt" or (name.startswith("tables/") and not name.endswith("/")):
                target = directory / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(files.read(name))
    binary = directory / "pub_enc.exe"
    return binary


def run(binary, arguments, directory):
    """Runs the encoder from directory. TMPDIR must not be set: with it the program dies with a heap error before it writes anything."""
    environment = {k: v for k, v in os.environ.items() if k != "TMPDIR"} | {"WINEPREFIX": str(xing_tool.WINE_PREFIX), "WINEDEBUG": "-all"}
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True, text=True,
                          stdin=subprocess.DEVNULL, timeout=600, errors="replace")


def write_aiff(wav_path, aiff_path):
    """The 16-bit PCM of wav_path as a standard AIFF file."""
    with wave.open(str(wav_path)) as wav:
        channels, frames, rate, data = wav.getnchannels(), wav.getnframes(), wav.getframerate(), wav.readframes(wav.getnframes())
        if wav.getsampwidth() != 2:
            raise SystemExit("the Philips encoder is fed 16-bit PCM")
    samples = array.array("h")
    samples.frombytes(data)
    samples.byteswap()
    comm = struct.pack(">hIh", channels, frames, 16) + struct.pack(">HQ", 16383 + rate.bit_length() - 1, rate << (64 - rate.bit_length()))
    ssnd = struct.pack(">II", 0, 0) + samples.tobytes()
    body = b"AIFF" + b"COMM" + struct.pack(">I", len(comm)) + comm + b"SSND" + struct.pack(">I", len(ssnd)) + ssnd
    pathlib.Path(aiff_path).write_bytes(b"FORM" + struct.pack(">I", len(body)) + body)


def encode(binary, arguments, source, path):
    """Encodes the WAVE file source to path; an extension bitstream, if the encoder writes one, goes next to it with the suffix .ext."""
    binary = pathlib.Path(binary)
    with tempfile.TemporaryDirectory(prefix="philips-mc-") as temp:
        work = pathlib.Path(temp)
        shutil.copytree(binary.parent / "tables", work / "tables")
        shutil.copy(binary, work / binary.name)
        write_aiff(source, work / "in.aiff")
        output = work / "out.mpg"
        run(work / binary.name, [*arguments, "in.aiff", "out"], work)
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"Philips pub_enc {' '.join(arguments)} failed")
        shutil.move(output, path)
        if (work / "out.ext").exists():
            shutil.move(work / "out.ext", pathlib.Path(path).with_suffix(".ext"))
