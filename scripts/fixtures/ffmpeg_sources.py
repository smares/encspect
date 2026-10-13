#!/usr/bin/env python3
"""Downloads FFmpeg 6.1.1's source release, pinned by its SHA-256, into a directory outside the
repository and extracts the files the WMA and ASF work is written from:

    python3 scripts/fixtures/ffmpeg_sources.py [directory]

- libavformat/asf.h, asf.c, asfdec_f.c and asfenc.c: the reference ASF demuxer and muxer.
- libavcodec/wma.c, wma.h, wmadec.c, wmadata.h, wma_common.c and wma_freqs.c: the Windows Media
  Audio versions 1 and 2 decoder and its tables.
- libavcodec/wmaprodec.c, wmalosslessdec.c and wmavoice.c: Windows Media Audio Professional,
  Lossless and Voice.
- libavcodec/aactab.c: the scalefactor codebook that WMA's exponents share with AAC.
- libavcodec/vlc.c: how a VLC is built from code lengths (ff_vlc_init_from_lengths).

The release is the one Ubuntu 24.04's ffmpeg 6.1.1 is built from, which the cross-checks run.
"""

import pathlib
import sys
import tarfile

import downloads

VERSION = "6.1.1"
ARCHIVE = f"ffmpeg-{VERSION}.tar.xz"
SHA256 = "8684f4b00f94b85461884c3719382f1261f0d9eb3d59640a1f4ac0873616f968"
ADDRESSES = [f"https://ffmpeg.org/releases/{ARCHIVE}"]

FILES = [
    "libavformat/asf.h", "libavformat/asf.c", "libavformat/asfdec_f.c", "libavformat/asfenc.c", "libavformat/asfcrypt.c",
    "libavcodec/wma.c", "libavcodec/wma.h", "libavcodec/wmadec.c", "libavcodec/wmadata.h", "libavcodec/wma_common.c",
    "libavcodec/wma_freqs.c", "libavcodec/wmaprodec.c", "libavcodec/wmalosslessdec.c", "libavcodec/wmavoice.c",
    "libavcodec/wmaenc.c", "libavcodec/aactab.c", "libavcodec/vlc.c", "libavcodec/vlc.h",
]


def fetch(directory):
    """Returns the directory that holds the extracted files, under their paths in the release."""
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    target = directory / f"ffmpeg-{VERSION}"
    with tarfile.open(archive) as tar:
        for name in FILES:
            member = f"ffmpeg-{VERSION}/{name}"
            path = target / name
            if not path.exists():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(tar.extractfile(member).read())
    return target


def main():
    target = fetch(sys.argv[1] if len(sys.argv) > 1 else "ffmpeg-sources")
    print(f"{target}: FFmpeg {VERSION}, SHA-256 {SHA256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
