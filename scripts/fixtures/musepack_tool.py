"""Builds the Musepack tools the fixtures and their cross-check need from pinned sources.

- mppenc 1.16, the last SV7 encoder, from its source release on files.musepack.net (pinned by SHA-256),
  for the SV7 fixtures. Modern GCC places its fast-math tables, which it declares const without an
  initializer and fills at startup, in read-only memory; -fcommon puts them back where the 2006 build
  had them. Nothing else is changed.
- mpcdec_frames, a small program (mpcdec_frames.c) over libmpcdec from musepack_src r495, the source
  Debian and Ubuntu build musepack-tools 0.1~r495 from (deb.debian.org, pinned by SHA-256), which
  prints what the decoder reads from each SV7 frame.

mppenc is under the LGPL 2.1 and libmpcdec under a BSD license; neither is redistributed.
"""

import pathlib
import subprocess
import sys
import tarfile
import tempfile

import downloads

MPPENC = ("mppenc-1.16.tar.bz2", "f90781856ca7b6ce1ec7033321fadbcd04fe264d0349c20b49ad05f7cb2655ad",
          ["https://files.musepack.net/source/mppenc-1.16.tar.bz2"])
LIBMPC = ("libmpc_0.1~r495.orig.tar.gz", "5182d7a30eec1b338cafc9c1706d663a6c8dc217b1d90187054512074f662202",
          ["https://deb.debian.org/debian/pool/main/libm/libmpc/libmpc_0.1~r495.orig.tar.gz"])
HERE = pathlib.Path(__file__).resolve().parent


def fetch(spec, directory):
    name, digest, addresses = spec
    path = directory / name
    downloads.fetch(addresses, digest, path)
    return path


def extract(archive, directory):
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")


def build_mppenc(directory=None):
    """The path of an mppenc 1.16 binary, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="mppenc-"))
    directory.mkdir(parents=True, exist_ok=True)
    extract(fetch(MPPENC, directory), directory)
    source = directory / "mppenc-1.16" / "src"
    binary = directory / "mppenc"
    subprocess.run(["gcc", "-O2", "-ffast-math", "-fomit-frame-pointer", "-fcommon", "-DMPP_ENCODER", "-DFAST_MATH", "-DCVD_FASTLOG", "-w",
                    "-o", str(binary), *sorted(str(p) for p in source.glob("*.c")), "-lm"], check=True)
    banner = subprocess.run([str(binary)], capture_output=True, text=True).stderr
    if "MPC Encoder  1.16" not in banner:
        raise SystemExit(f"{binary} is not mppenc 1.16")
    return binary


def build_frames(directory=None):
    """The path of mpcdec_frames, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libmpcdec-"))
    directory.mkdir(parents=True, exist_ok=True)
    extract(fetch(LIBMPC, directory), directory)
    source = directory / "musepack_src_r495"
    library = source / "libmpcdec"
    files = ["huffman.c", "mpc_bits_reader.c", "mpc_decoder.c", "mpc_demux.c", "mpc_reader.c", "requant.c", "streaminfo.c", "synth_filter.c"]
    binary = directory / "mpcdec_frames"
    subprocess.run(["gcc", "-O1", "-w", "-I", str(source / "include"), "-I", str(library), "-o", str(binary), str(HERE / "mpcdec_frames.c"),
                    *(str(library / f) for f in files), str(source / "common" / "crc32.c"), "-lm"], check=True)
    return binary


if __name__ == "__main__":
    print(build_mppenc() if sys.argv[1:] == ["mppenc"] else build_frames())
