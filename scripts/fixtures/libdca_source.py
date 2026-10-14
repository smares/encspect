#!/usr/bin/env python3
"""Downloads libdca 0.0.7's source release (the DTS decoder behind dcadec, dtsdec and extract_dca), pinned
by its SHA-256, into a directory outside the repository, and extracts the files the DTS work is written
from:

    python3 scripts/fixtures/libdca_source.py [directory]

- libdca/parse.c and dca_internal.h: the core frame header, the audio header and the subframes as libdca
  reads them, and the frame size of the 14-bit forms, which ETSI TS 102 114 does not describe.
- libdca/bitstream.c and bitstream.h: how the four forms of the stream are put in order for the bit reader.
- include/dca.h: the decoder's flags and the arrangements it reports.

The release is the original source of the libdca-utils package of Ubuntu 24.04 (0.0.7-2build1), whose
dcadec the DTS cross-check runs.
"""

import pathlib
import sys
import tarfile

import downloads

VERSION = "0.0.7"
ARCHIVE = f"libdca_{VERSION}.orig.tar.bz2"
SHA256 = "3a0b13815f582c661d2388ffcabc2f1ea82f471783c400f765f2ec6c81065f6a"
ADDRESSES = [
    f"https://deb.debian.org/debian/pool/main/libd/libdca/{ARCHIVE}",
    f"http://archive.ubuntu.com/ubuntu/pool/universe/libd/libdca/{ARCHIVE}",
    f"https://download.videolan.org/pub/videolan/libdca/{VERSION}/libdca-{VERSION}.tar.bz2",
]

FILES = ["libdca/parse.c", "libdca/dca_internal.h", "libdca/bitstream.c", "libdca/bitstream.h", "include/dca.h"]


def fetch(directory):
    """Returns the directory that holds the extracted files, under their paths in the release."""
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    target = directory / f"libdca-{VERSION}"
    with tarfile.open(archive) as tar:
        for name in FILES:
            path = target / name
            if not path.exists():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(tar.extractfile(f"libdca-{VERSION}/{name}").read())
    return target


def main():
    target = fetch(sys.argv[1] if len(sys.argv) > 1 else "libdca-source")
    print(f"{target}: libdca {VERSION}, SHA-256 {SHA256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
