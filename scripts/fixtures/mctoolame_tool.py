"""Builds mctoolame, a multichannel MPEG-2 Layer II encoder derived from the ISO reference software
(sourceforge.net/projects/mctoolame, 2003, LGPL), and runs it. Its source was never read here: it
builds with compiler flags alone (the ones of mctoolamed_tool.py), and everything below follows
from running it.

Four things the tarball does not give:
- Its table files. The program reads the ISO tables from the working directory (the analysis
  window "enwindow", the critical band and threshold tables "2cb0" and "2th0", "absthr_0" and the
  allocation tables), which the ISO software ships as a "tables" directory. toolame 0.2a, whose
  tarball is pinned here, ships the same files; only that directory is unpacked. mctoolame reads
  "enwindow" as four values a line (C[  0]= 0.000000000 ...) without the blank lines the ISO file
  has, so the file is written again in that layout. The 512 values are the ISO analysis window,
  Table 3-C.1: they equal the synthesis window of Table 3-B.3 divided by 32, which the polyphase
  stage of this repository's decoder holds as integers (checked against minimp3's impulse response).
- Its input format. It reads a 5 or 6 channel AIFF file written by the author's own tool, whose
  header fields are little endian (FORM and COMM chunk sizes, channel and frame counts, the sample
  rate field, the SSND chunk with 8 bytes of offset and block size and then little endian 16-bit
  samples). A standard big endian AIFF is read as stereo ("NO MULTICHANNEL CODING"). The file
  written here copies the header of the author's sample (mctoolame.sourceforge.net/test.aiff.bz2,
  44.1 kHz) with its own counts; other sample rates did not encode.
- A 32-bit build (-m32): built for x86-64 it takes the header's counts at the wrong width and encodes every file as stereo.
- Its output name: it appends ".mp2" to the name it is given.

What it does with options (tried on 44.1 kHz input): the configuration is given by -C (320, 310,
220, 210, 200: the channels in front and behind, as 3/2, 3/1, 2/2, 2/1 and 2/0) and -L (a
low frequency effects channel in addition), and the input must have exactly that many channels
(2/0 with LFE is a three channel file). Total bitrates above 192 kbps, a MPEG-1 bitrate (-B)
other than the total and other sample rates produce files that do not parse.
"""

import pathlib
import re
import shutil
import struct
import subprocess
import tarfile
import tempfile
import wave

import downloads

URL = "https://sourceforge.net/projects/mctoolame/files/mctoolame/mctoolame-01a/mctoolame-01a.tgz/download"
SHA256 = "e280e711a1402aaa1c45f8a9739b6f8d0ed5d590110579275c52c50c101ed905"
ADDRESSES = [URL, "https://downloads.sourceforge.net/project/mctoolame/mctoolame/mctoolame-01a/mctoolame-01a.tgz"]

TABLES_URL = "https://sourceforge.net/projects/toolame/files/toolame%200.2/02a/toolame-02a.tar.gz/download"
TABLES_SHA256 = "dda9f8ac514d82651970cc4b0e7879051842337ec8e2f9bd9bff6f3c7247eb8d"
TABLES_ADDRESSES = [TABLES_URL, "https://downloads.sourceforge.net/project/toolame/toolame%200.2/02a/toolame-02a.tar.gz"]

# The 80-bit sample rate field of the author's 44.1 kHz AIFF, as it stands in that file.
RATE_FIELD = bytes.fromhex("00800000442c07020028")

# The site's own sample, five channels, 2003: not redistributable (no license), so it is fetched at its hash.
SAMPLE_URL = "https://mctoolame.sourceforge.net/test.aiff-mpeg1-192.mp2"
SAMPLE_SHA256 = "991e17a1f0a7508cb903d7e53b2fe694da11cb4ddad7b6c10030c55f90bb2eae"
SAMPLE_ADDRESSES = [SAMPLE_URL]


def build():
    """Builds the encoder and unpacks its tables; returns the directory that holds both."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="mctoolame-"))
    archive = directory / "mctoolame.tgz"
    downloads.fetch(ADDRESSES, SHA256, archive)
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")
    source = directory / "mctoolame-01a"
    # The flags of mctoolamed_tool.py: the 2003 sources need the C of the time, and the Makefile compiles with CC_SWITCHES.
    flags = ("-O3 -DNDEBUG -DINLINE=inline -w -fcommon -std=gnu99 -Wno-error=implicit-function-declaration -Wno-error=implicit-int "
             "-Wno-error=int-conversion -Wno-error=incompatible-pointer-types")
    # As a 32-bit program (gcc-multilib): the 64-bit build reads the AIFF header's counts at the wrong width and takes every file for stereo.
    result = subprocess.run(["make", "-s", "CC=gcc -m32", f"CC_SWITCHES={flags}"], cwd=source, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"mctoolame does not build:\n{(result.stdout + result.stderr)[-3000:]}")
    tables_archive = directory / "toolame.tar.gz"
    downloads.fetch(TABLES_ADDRESSES, TABLES_SHA256, tables_archive)
    with tarfile.open(tables_archive) as tar:
        tar.extractall(directory, members=[m for m in tar.getmembers() if m.name.startswith("toolame-02a/tables/") and m.isfile()], filter="data")
    tables = directory / "toolame-02a" / "tables"
    window = re.findall(r"C\[\s*\d+\]=\s*(-?[\d.]+)", (tables / "enwindow").read_text())
    if len(window) != 512:
        raise SystemExit("the analysis window table does not hold 512 values")
    (tables / "enwindow").write_text("".join(" ".join(f"C[{i + j:3d}]= {window[i + j]}" for j in range(4)) + "\n" for i in range(0, 512, 4)))
    for table in tables.iterdir():
        if table.is_file():
            shutil.copy(table, source / table.name)
    return source / "mctoolame"


def write_aiff(wav_path, aiff_path):
    """Writes the 16-bit PCM of wav_path as the AIFF layout the encoder reads (see the module's text)."""
    with wave.open(str(wav_path)) as wav:
        channels, frames, data = wav.getnchannels(), wav.getnframes(), wav.readframes(wav.getnframes())
        if wav.getsampwidth() != 2 or wav.getframerate() != 44100:
            raise SystemExit("mctoolame is fed 16-bit PCM at 44100 Hz")
    comm = struct.pack("<HIH", channels, frames, 16) + RATE_FIELD
    ssnd = bytes(8) + data
    body = b"AIFF" + b"COMM" + struct.pack("<I", len(comm)) + comm + b"SSND" + struct.pack("<I", len(ssnd)) + ssnd
    pathlib.Path(aiff_path).write_bytes(b"FORM" + struct.pack("<I", len(body)) + body)


def encode(binary, arguments, source, path):
    """Encodes the WAVE file source to path."""
    binary = pathlib.Path(binary)
    with tempfile.TemporaryDirectory(prefix="mctoolame-run-") as temp:
        work = pathlib.Path(temp)
        for table in binary.parent.iterdir():
            if table.is_file() and table.name != "mctoolame" and "." not in table.name:
                shutil.copy(table, work / table.name)
        write_aiff(source, work / "in.aiff")
        result = subprocess.run([str(binary), *arguments, "in.aiff", "out"], cwd=work, capture_output=True, text=True, timeout=600)
        output = work / "out.mp2"
        if result.returncode != 0 or not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"mctoolame {' '.join(arguments)} failed: {(result.stdout + result.stderr)[-500:]}")
        shutil.move(output, path)
