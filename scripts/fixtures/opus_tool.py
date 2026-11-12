"""The Opus tools of the cross-checks, and the sources the decoder and the notices refer to.

- opus-1.5.2.tar.gz, pinned by SHA-256: libopus 1.5.2 (BSD-3-Clause, Copyright (c) 2001-2023 Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
  CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo, Mozilla, Amazon). The decoder follows its decoding code (the reference
  decoder of RFC 6716 as RFC 8251 updates it, and libopus where the RFC calls its repository current) and takes its tables as format data. The source is not
  redistributed; its COPYING is the text scripts/release/make_notices.py puts in THIRD-PARTY-NOTICES.md (--fetch checks the hash). build_libopus() builds it in a temporary
  directory with the default configuration (float decoding; OSCE, deep PLC and DRED off, as `configure.ac` leaves them) and without the SIMD intrinsics, so that
  the reference is the plain C code RFC 6716's decoder is: the intrinsics sum in another order and with fused multiply-adds, which moves the concealment of a mode change (a
  few 10^-3 of full scale in the 5 ms it covers) and nothing else.
- rfc6716.txt and rfc8251.txt: the specifications, pinned (the RFC's code is Simplified BSD under its copyright notice).
- opus_testvectors-rfc8251.tar.gz and opus_testvectors.tar.gz: the test vectors of RFC 8251 and of RFC 6716, downloaded and never committed (their license is unverified,
  docs/design/lossy-decoders.md): per vector the packets with the encoder's final range coder state (testvectorNN.bit, the opus_demo format) and the reference output
  (testvectorNN.dec, and the mono decode testvectorNNm.dec).
- opus_pcm.c (build_pcm, decode_ogg, decode_bit) is built against that libopus and decodes to floats; opus_enc.c (build_enc) is built against it and encodes the fixtures of make_external_vorbis_opus_fixtures.py that the other encoders cannot write.
"""

import json
import pathlib
import subprocess
import tarfile
import tempfile

import downloads

LIBOPUS_NAME = "opus-1.5.2.tar.gz"
LIBOPUS_SHA256 = "65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1"
LIBOPUS_ADDRESSES = [
    "https://github.com/xiph/opus/releases/download/v1.5.2/opus-1.5.2.tar.gz",
    "https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz",
]

RFC6716_NAME = "rfc6716.txt"
RFC6716_SHA256 = "41caac5240a4a22661efd0031d5b7aee48f3c0bde3b2cdcee8165932e485f98c"
RFC6716_ADDRESSES = ["https://www.rfc-editor.org/rfc/rfc6716.txt", downloads.archived("https://www.rfc-editor.org/rfc/rfc6716.txt", "2026")]

RFC8251_NAME = "rfc8251.txt"
RFC8251_SHA256 = "86abdb8c0fa1b0b6e1b9c1f324163ca152a8d2d9debd10461da3d56438489ecd"
RFC8251_ADDRESSES = ["https://www.rfc-editor.org/rfc/rfc8251.txt", downloads.archived("https://www.rfc-editor.org/rfc/rfc8251.txt", "2026")]

VECTORS_8251_NAME = "opus_testvectors-rfc8251.tar.gz"
VECTORS_8251_SHA256 = "6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55"
VECTORS_8251_ADDRESSES = [
    "https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz",
    downloads.archived("https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz", "2026"),
]

VECTORS_6716_NAME = "opus_testvectors.tar.gz"
VECTORS_6716_SHA256 = "94ac78ca4f74c4e43bc9fe4ec1ad0aa36f38ab90f45b0727c40dd1e96096e767"
VECTORS_6716_ADDRESSES = [
    "https://opus-codec.org/static/testvectors/opus_testvectors.tar.gz",
    downloads.archived("https://opus-codec.org/static/testvectors/opus_testvectors.tar.gz", "2026"),
]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield LIBOPUS_NAME, LIBOPUS_SHA256, LIBOPUS_ADDRESSES
    yield RFC6716_NAME, RFC6716_SHA256, RFC6716_ADDRESSES
    yield RFC8251_NAME, RFC8251_SHA256, RFC8251_ADDRESSES
    yield VECTORS_8251_NAME, VECTORS_8251_SHA256, VECTORS_8251_ADDRESSES
    yield VECTORS_6716_NAME, VECTORS_6716_SHA256, VECTORS_6716_ADDRESSES


def fetch(directory, name, digest, addresses):
    path = pathlib.Path(directory) / name
    downloads.fetch(addresses, digest, path)
    return path


def fetch_libopus(directory):
    return fetch(directory, LIBOPUS_NAME, LIBOPUS_SHA256, LIBOPUS_ADDRESSES)


def fetch_vectors(directory, which="8251"):
    """Downloads the test vectors and returns the directory that holds testvectorNN.bit and .dec (extracted below directory)."""
    name, digest, addresses = (VECTORS_8251_NAME, VECTORS_8251_SHA256, VECTORS_8251_ADDRESSES) if which == "8251" else (VECTORS_6716_NAME, VECTORS_6716_SHA256, VECTORS_6716_ADDRESSES)
    archive = fetch(directory, name, digest, addresses)
    target = pathlib.Path(directory) / ("vectors" + which)
    if not target.exists():
        target.mkdir()
        with tarfile.open(archive) as tar:
            tar.extractall(target, filter="data")
    folders = {path.parent for path in target.rglob("testvector*.bit")}
    return sorted(folders)[0]


def extract_libopus(directory):
    """Extracts the pinned libopus below directory and returns the folder of its source."""
    archive = fetch_libopus(directory)
    target = pathlib.Path(directory) / "libopus"
    if not target.exists():
        with tarfile.open(archive) as tar:
            tar.extractall(target, filter="data")
    return next(target.iterdir())


PCM_SOURCE = pathlib.Path(__file__).resolve().parent / "opus_pcm.c"


def build_libopus(directory):
    """Builds libopus 1.5.2 (float, default options) below directory and returns the folder that holds include/ and lib/."""
    source = extract_libopus(directory)
    build = pathlib.Path(directory) / "libopus-build"
    prefix = pathlib.Path(directory) / "libopus-install"
    if not (prefix / "lib" / "libopus.a").exists():
        build.mkdir(exist_ok=True)
        subprocess.run(["cmake", str(source), "-DCMAKE_BUILD_TYPE=Release", "-DOPUS_BUILD_PROGRAMS=OFF", "-DOPUS_BUILD_TESTING=OFF", "-DOPUS_DISABLE_INTRINSICS=ON", "-DCMAKE_C_FLAGS=-ffp-contract=off -O2",
                        f"-DCMAKE_INSTALL_PREFIX={prefix}"], cwd=build, check=True, capture_output=True)
        subprocess.run(["cmake", "--build", ".", "-j4"], cwd=build, check=True, capture_output=True)
        subprocess.run(["cmake", "--install", "."], cwd=build, check=True, capture_output=True)
    return prefix


def build_pcm(directory):
    """Compiles opus_pcm.c against the pinned libopus and returns the binary."""
    prefix = build_libopus(directory)
    binary = pathlib.Path(directory) / "opus_pcm"
    if not binary.exists():
        subprocess.run(["cc", "-O1", "-ffp-contract=off", f"-I{prefix / 'include'}", "-o", str(binary), str(PCM_SOURCE), str(prefix / "lib" / "libopus.a"), "-logg", "-lm"], check=True)
    return binary


ENC_SOURCE = pathlib.Path(__file__).resolve().parent / "opus_enc.c"


def build_enc(directory):
    """Compiles opus_enc.c (libopus 1.5.2's encoder, writing Ogg Opus files) and returns the binary."""
    prefix = build_libopus(directory)
    binary = pathlib.Path(directory) / "opus_enc"
    if not binary.exists():
        subprocess.run(["cc", "-O1", "-ffp-contract=off", f"-I{prefix / 'include'}", "-o", str(binary), str(ENC_SOURCE), str(prefix / "lib" / "libopus.a"), "-logg", "-lm"], check=True)
    return binary


def decode_pcm(binary, mode, arguments, output):
    """Runs opus_pcm (mode "ogg": [INPUT]; mode "bit": [CHANNELS, INPUT]) and returns (the packets, one dict each, and the stream's channels)."""
    result = subprocess.run([str(binary), mode, *[str(a) for a in arguments], str(output)], capture_output=True, text=True)
    if result.returncode != 0:
        return None
    lines = [json.loads(line) for line in result.stdout.splitlines()]
    return lines[:-1], lines[-1]


def work_directory():
    return tempfile.mkdtemp(prefix="opus-")
