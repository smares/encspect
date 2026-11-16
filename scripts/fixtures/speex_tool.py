"""The Speex source the Speex parser and the notices refer to: libspeex 1.2.1 (the version of the speex package of
Ubuntu 24.04, whose speexenc writes the fixtures), pinned by SHA-256.

- speex-1.2.1.tar.gz from downloads.xiph.org. Debian's and Ubuntu's orig tarballs of the package are the same file,
  so either serves as a fallback. The source is not redistributed; its BSD license allows it.
- Its COPYING is the text scripts/release/make_notices.py puts in THIRD-PARTY-NOTICES.md (--fetch checks the hash).
- build_pcm() builds speex_pcm (speex_pcm.c over the libspeex of that archive, compiled with FIXED_POINT: the integer decoder the analyzer's Speex decoder
  follows and equals bit for bit, scripts/fixtures/crosscheck_speex_pcm.py). It needs libogg and its headers (libogg-dev), as speexdec does.
"""

import pathlib
import subprocess
import tarfile
import tempfile

import downloads

HERE = pathlib.Path(__file__).resolve().parent

# The files of libspeex's Makefile.am that the decoder and the header reader need (the encoder's are compiled too, since the library's own files call them).
LIBRARY = ["cb_search.c", "exc_10_32_table.c", "exc_8_128_table.c", "filters.c", "gain_table.c", "hexc_table.c", "high_lsp_tables.c", "lsp.c", "ltp.c", "speex.c",
           "stereo.c", "vbr.c", "vq.c", "bits.c", "exc_10_16_table.c", "exc_20_32_table.c", "exc_5_256_table.c", "exc_5_64_table.c", "gain_table_lbr.c",
           "hexc_10_32_table.c", "lpc.c", "lsp_tables_nb.c", "modes.c", "modes_wb.c", "nb_celp.c", "quant_lsp.c", "sb_celp.c", "speex_callbacks.c", "speex_header.c", "window.c"]

NAME = "speex-1.2.1.tar.gz"
SHA256 = "4b44d4f2b38a370a2d98a78329fefc56a0cf93d1c1be70029217baae6628feea"
ADDRESSES = [
    "https://downloads.xiph.org/releases/speex/speex-1.2.1.tar.gz",
    "https://deb.debian.org/debian/pool/main/s/speex/speex_1.2.1.orig.tar.gz",
    "http://archive.ubuntu.com/ubuntu/pool/main/s/speex/speex_1.2.1.orig.tar.gz",
]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES


def fetch(directory):
    path = pathlib.Path(directory) / NAME
    downloads.fetch(ADDRESSES, SHA256, path)
    return path


def build_pcm(directory=None, program="speex_pcm"):
    """The path of speex_pcm (or of another program of this folder over the same library, such as speex_convergence), built in directory (a new temporary one by default) from the pinned libspeex 1.2.1 with FIXED_POINT. Signed overflow wraps (-fwrapv): the
    fixed-point macros rely on it."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libspeex-pcm-"))
    directory.mkdir(parents=True, exist_ok=True)
    archive = fetch(directory)
    with tarfile.open(archive) as tar:
        tar.extractall(directory, filter="data")
    source = directory / "speex-1.2.1"
    include = source / "include" / "speex"
    (include / "speex_config_types.h").write_text(
        "#ifndef __SPEEX_TYPES_H__\n#define __SPEEX_TYPES_H__\n#include <stdint.h>\n"
        "typedef int16_t spx_int16_t;\ntypedef uint16_t spx_uint16_t;\ntypedef int32_t spx_int32_t;\ntypedef uint32_t spx_uint32_t;\n#endif\n")
    binary = directory / program
    library = source / "libspeex"
    subprocess.run(["gcc", "-O2", "-w", "-fwrapv", "-DFIXED_POINT", "-DUSE_ALLOCA", "-DEXPORT=", "-I", str(source / "include"), "-I", str(library), "-o", str(binary),
                    str(HERE / (program + ".c")), *(str(library / f) for f in LIBRARY), "-logg", "-lm"], check=True)
    return binary


if __name__ == "__main__":
    import sys
    print(build_pcm(sys.argv[1] if len(sys.argv) > 1 else None))
