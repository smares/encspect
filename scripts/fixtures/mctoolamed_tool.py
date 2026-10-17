"""Builds mctoolamed, an MPEG-2 multichannel Layer II decoder derived from the ISO reference
software (sourceforge.net/projects/mctoolame, LGPL), and runs it.

Its channel files are only named for 5 and 6 channels, so it stops on 3 and 4; the build gives
every channel a numbered file instead. Nothing else in the decoder is changed. On the ISO
conformance streams 01, 08, 13 and 21 (3/2 with LFE, dynamic crosstalk, phantom center,
prediction and extension bitstreams), its output matches ISO's reference output to within one
least significant bit.
"""

import pathlib
import re
import shutil
import subprocess
import tarfile
import tempfile

import downloads

URL = "https://sourceforge.net/projects/mctoolame/files/mctoolamed/mctoolamed-01a/mctoolamed-01a.tgz/download"
SHA256 = "654b7e20e905946dc4a7e58262e81f6cf1d0a171455eeeaeeaf0cc223e0c052e"
# SourceForge's mirror network, directly; the Wayback Machine has no copy.
ADDRESSES = [URL, "https://downloads.sourceforge.net/project/mctoolame/mctoolamed/mctoolamed-01a/mctoolamed-01a.tgz"]

NAMING = '''  numchannels = numchan; // Set the global var for later on.
  if (numchan == 5)
    type = 0;
  if (numchan == 6)
    type = 1;
  if (soundfile>=0) {'''
GENERIC = '''  numchannels = numchan; // Set the global var for later on.
  type = 0;
  for (i = 0; i < numchan; i++)
    sprintf(soundfile[type][i], "channel%d.wav", i);
  if (soundfile>=0) {'''


def build():
    directory = pathlib.Path(tempfile.mkdtemp(prefix="mctoolamed-"))
    archive = directory / "mctoolamed.tgz"
    downloads.fetch(ADDRESSES, SHA256, archive)
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")
    source = directory / "mctoolamed-01a"
    output = source / "audio_write.c"
    text = output.read_text(encoding="latin-1")
    if NAMING not in text:
        raise SystemExit("mctoolamed's audio_write.c is not the expected version")
    output.write_text(text.replace(NAMING, GENERIC), encoding="latin-1")
    # The 2001 sources call gets() without declaring it and use old-style declarations: GCC 14 and later make an implicit function declaration, an
    # implicit int and mismatched pointer types errors, and GCC 15 reads () as (void) by default (C23). -std=gnu99 keeps the C of the time, and the
    # permerrors are warnings again (-w hides them). The Makefile compiles with CC_SWITCHES, not CFLAGS, so the flags replace those.
    flags = ("-O3 -DNDEBUG -DINLINE=inline -w -fcommon -std=gnu99 -Wno-error=implicit-function-declaration -Wno-error=implicit-int "
             "-Wno-error=int-conversion -Wno-error=incompatible-pointer-types")
    result = subprocess.run(["make", "-s", f"CC_SWITCHES={flags}"], cwd=source, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"mctoolamed does not build:\n{(result.stdout + result.stderr)[-3000:]}")
    return source / "mctoolamed"


def decode(binary, path, extension=None):
    """Decodes path, with its extension bitstream if any; returns the number of multichannel CRC
    errors, whether every channel is silent, and how many channels it wrote."""
    errors, silent, channels, _ = decode_all(binary, path, extension)
    return errors, silent, channels


def decode_all(binary, path, extension=None):
    """As decode, and the number of multilingual channels the decoder reports, which it writes to a
    file of their own, apart from the channel files."""
    work = pathlib.Path(tempfile.mkdtemp(prefix="mctoolamed-run-"))
    (work / "stream.mp2").write_bytes(pathlib.Path(path).read_bytes())
    if extension is not None:
        (work / "stream.ext").write_bytes(pathlib.Path(extension).read_bytes())
    result = subprocess.run([str(binary), "-a", "stream", "stream.dec"], cwd=work, capture_output=True, text=True, timeout=300)
    output = result.stdout + result.stderr
    channels = sorted(work.glob("channel*.wav")) + sorted(work.glob("*_lfe.dec"))
    # The channel files start with a 44-byte header.
    silent = bool(channels) and all(not any(f.read_bytes()[44 if f.suffix == ".wav" else 0 :]) for f in channels)
    multilingual = re.search(r"There are (\d+) multilingual channels", output)
    shutil.rmtree(work, ignore_errors=True)
    return output.count("ERROR in MC-CRC"), silent, len(channels), int(multilingual.group(1)) if multilingual else 0
