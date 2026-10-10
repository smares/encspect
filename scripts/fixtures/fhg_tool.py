"""Fraunhofer IIS MP3 Surround Commandline Encoder 1.5 (mp3sEncoder, 2008-05-30), for encoder-written fixtures.

It encodes mono, stereo and 5.1 input: plain MP3 from mono and stereo, MP3 Surround (a stereo
downmix with spatial data in the ancillary data) from 5.1, with an optional VBRI header (-vbri)
and Original File Length block (-ofl). Fraunhofer no longer distributes it; Rarewares keeps the
release, which is downloaded here at a pinned SHA-256 and unpacked to its static 64-bit Linux build.

The encoder asks for acceptance of Fraunhofer's and Agere's limited-use licence (personal,
non-commercial evaluation use) on every run. The project's owner accepted it on 2026-10-10 for
this personal, non-commercial, open-source project, so encode() answers the prompts. The fixtures
encode signals this project generates, not third-party audio.
"""

import hashlib
import io
import pathlib
import subprocess
import tempfile
import zipfile

import downloads

URL = "https://www.rarewares.org/rrw/files/fhg/mp3sconsv15.zip"
SHA256 = "340ad76d932bfbef2152a9698d8e7e0c33a6cc95fe89d5d82ff8a6cf10fd9883"
INNER = "mp3sEnc_Lin64static_FCRv15_20080530.zip"
BINARY_SHA256 = "693f27d5224bdff2520d74349b6fdd732386e81949b11996fb23ef3dbdf9fa38"
VERSION = "Fraunhofer IIS MP3 Surround Commandline Encoder V1.5"
# The Wayback Machine lists a capture of the archive but answers every request for it with an
# error, so Rarewares is the only source; the fixtures it writes are committed.
ADDRESSES = [URL]

# The licence pages through "Press <Return>" prompts before asking for "yes".
ACCEPTANCE = "\n" * 40 + "yes\n"


def fetch():
    """Downloads the release, checks it, and unpacks the encoder into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="mp3sencoder-"))
    path = directory / "mp3sconsv15.zip"
    downloads.fetch(ADDRESSES, SHA256, path)
    archive = path.read_bytes()
    with zipfile.ZipFile(io.BytesIO(archive)) as outer, zipfile.ZipFile(io.BytesIO(outer.read(INNER))) as inner:
        binary = directory / "mp3sEncoder"
        binary.write_bytes(inner.read("mp3sEncoder"))
    if hashlib.sha256(binary.read_bytes()).hexdigest() != BINARY_SHA256:
        raise SystemExit(f"mp3sEncoder has SHA-256 {hashlib.sha256(binary.read_bytes()).hexdigest()}, expected {BINARY_SHA256}")
    binary.chmod(0o755)
    return binary


def encode(binary, arguments, source, path):
    result = subprocess.run([str(binary), "-if", str(source), "-of", str(path), *arguments], input=ACCEPTANCE,
                            capture_output=True, text=True)
    if result.returncode != 0 or not path.exists():
        raise SystemExit(f"mp3sEncoder {' '.join(arguments)} failed: {result.stdout[-500:]}")
