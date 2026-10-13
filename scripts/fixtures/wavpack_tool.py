"""The WavPack source the fixtures' checks are built from: libwavpack 5.6.0, the version of the wavpack
package of Ubuntu 24.04 that writes the fixtures, pinned by SHA-256.

- wavpack-5.6.0.tar.bz2 from wavpack.com (David Bryant's site). Debian's orig tarball of the package
  (deb.debian.org) and the Wayback Machine's copy of the site's are the same file, so either serves as a
  fallback. The source is not redistributed; its BSD license allows it.
- wavpack_blocks, a small program (wavpack_blocks.c) linked with libwavpack's own sources, which prints
  what the library reads from a file: its figures, and every block read_next_header finds with the verdict
  of WavpackVerifySingleBlock. The reference for the format is in the same tree: doc/WavPack5FileFormat.pdf.
"""

import pathlib
import subprocess
import tarfile
import tempfile

import downloads

HERE = pathlib.Path(__file__).resolve().parent

WAVPACK = ("wavpack-5.6.0.tar.bz2", "8cbfa15927d29bcf953db35c0cfca7424344ff43ebe4083daf161577fb839cc1",
           ["https://www.wavpack.com/wavpack-5.6.0.tar.bz2",
            "https://deb.debian.org/debian/pool/main/w/wavpack/wavpack_5.6.0.orig.tar.bz2",
            downloads.archived("https://www.wavpack.com/wavpack-5.6.0.tar.bz2", "2023")])

# The library's sources as CMakeLists.txt lists them, with DSD (WAVPACK_ENABLE_DSD, on by default) and
# without the legacy decoder and the assembly.
LIBRARY = ["common_utils.c", "decorr_utils.c", "entropy_utils.c", "extra1.c", "extra2.c", "open_utils.c", "open_filename.c", "open_legacy.c", "open_raw.c",
           "pack.c", "pack_dns.c", "pack_floats.c", "pack_utils.c", "read_words.c", "tags.c", "tag_utils.c", "unpack.c", "unpack_floats.c", "unpack_seek.c",
           "unpack_utils.c", "write_words.c", "pack_dsd.c", "unpack_dsd.c"]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    name, digest, addresses = WAVPACK
    yield name, digest, addresses


def fetch(directory):
    name, digest, addresses = WAVPACK
    path = pathlib.Path(directory) / name
    downloads.fetch(addresses, digest, path)
    return path


def build_blocks(directory=None):
    """The path of wavpack_blocks, built in directory (a new temporary one by default)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="wavpack-"))
    directory.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fetch(directory)) as tar:
        tar.extractall(directory, filter="data")
    source = directory / "wavpack-5.6.0"
    binary = directory / "wavpack_blocks"
    subprocess.run(["gcc", "-O2", "-w", "-DENABLE_DSD", "-DHAVE___BUILTIN_CLZ", "-DHAVE_FSEEKO", "-I", str(source / "include"), "-I", str(source / "src"),
                    "-o", str(binary), str(HERE / "wavpack_blocks.c"), *[str(source / "src" / name) for name in LIBRARY], "-lm"], check=True)
    return binary
