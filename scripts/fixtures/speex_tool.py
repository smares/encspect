"""The Speex source the Speex parser and the notices refer to: libspeex 1.2.1 (the version of the speex package of
Ubuntu 24.04, whose speexenc writes the fixtures), pinned by SHA-256.

- speex-1.2.1.tar.gz from downloads.xiph.org. Debian's and Ubuntu's orig tarballs of the package are the same file,
  so either serves as a fallback. The source is not redistributed; its BSD license allows it.
- Its COPYING is the text scripts/release/make_notices.py puts in THIRD-PARTY-NOTICES.md (--fetch checks the hash).
"""

import pathlib

import downloads

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
