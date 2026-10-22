#!/usr/bin/env python3
"""Downloads FFmpeg 6.1.1's source release, pinned by its SHA-256, into a directory outside the
repository and extracts the files the format work is written from (the WMA, ASF, WavPack, DTS and MP4 parsers, and the phase 3
lossless codings the decoder design cites):

    python3 scripts/fixtures/ffmpeg_sources.py [directory]

- libavformat/asf.h, asf.c, asfdec_f.c and asfenc.c: the reference ASF demuxer and muxer.
- libavcodec/wma.c, wma.h, wmadec.c, wmadata.h, wma_common.c and wma_freqs.c: the Windows Media
  Audio versions 1 and 2 decoder and its tables.
- libavcodec/wmaprodec.c, wmalosslessdec.c, wmavoice.c and wmavoice_data.h: Windows Media Audio Professional,
  Lossless and Voice (the tables of the speech coder's gains, codebooks and LSP quantizers are in wmavoice_data.h; the analysis
  needs none of them, since it reads no audio, but they are pinned with the decoder they belong to). decode.c is the generic
  decode loop that wmavoice.c's return values are read by (an error drops the rest of the packet).
- libavcodec/aactab.c: the scalefactor codebook that WMA's exponents share with AAC.
- libavcodec/vlc.c: how a VLC is built from code lengths (ff_vlc_init_from_lengths).
- libavformat/wvdec.c, wv.c and wv.h and libavcodec/wavpack.c: the WavPack demuxer and decoder, which the WavPack cross-check explains
  ffprobe's readings with.
- libavcodec/wavpackenc.c, libavformat/wvenc.c and apetag.c: the WavPack encoder and muxer that write the FFmpeg fixture (stream version
  0x410, no new config block and no block checksums, and an APEv2 tag with an "encoder" item).
- libavcodec/dca.c, dca.h, dca_syncwords.h, dca_core.c, dca_core.h, dca_exss.c, dca_exss.h, dca_lbr.c, dca_xll.c, dca_parser.c,
  dca_sample_rate_tab.h, dcadata.c, dcadata.h and dcadec.c: the DTS core, extension substream and DTS-HD decoder and parser the DTS
  cross-check explains ffprobe's and ffmpeg's readings with; dcaenc.c and dcaenc.h, the encoder that writes the FFmpeg fixtures.
- libavformat/dtsdec.c, dtshddec.c and isom.c: the raw DTS probe, the DTS-HD file reader and the MP4 object type table (0xA9 for DTS).
- libavformat/mov.c, isom.h, movenccenc.c and libavutil/encryption_info.h: the MP4 demuxer's reading of the protection boxes (frma, schm, tenc,
  pssh, senc, saiz, saio, and the adrm box of Audible's files, mov_read_adrm) and the Common Encryption writer that makes the project's encrypted
  fixtures; the encryption info header says what the pattern's crypt and skip block counts are (16-byte blocks).

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
    "libavcodec/wmaenc.c", "libavcodec/wmaprodata.h", "libavcodec/wmavoice_data.h", "libavcodec/decode.c", "libavcodec/aactab.c", "libavcodec/vlc.c", "libavcodec/vlc.h",
    "libavformat/wv.h", "libavformat/wv.c", "libavformat/wvdec.c", "libavcodec/wavpack.c",
    "libavcodec/wavpackenc.c", "libavformat/wvenc.c", "libavformat/apetag.c",
    "libavcodec/dca.c", "libavcodec/dca.h", "libavcodec/dca_syncwords.h", "libavcodec/dca_core.c", "libavcodec/dca_core.h",
    "libavcodec/dca_exss.c", "libavcodec/dca_exss.h", "libavcodec/dca_lbr.c", "libavcodec/dca_xll.c", "libavcodec/dca_parser.c",
    "libavcodec/dca_sample_rate_tab.h", "libavcodec/dcadata.c", "libavcodec/dcadata.h", "libavcodec/dcadec.c",
    "libavcodec/dcaenc.c", "libavcodec/dcaenc.h", "libavformat/dtsdec.c", "libavformat/dtshddec.c", "libavformat/isom.c",
    "libavformat/mov.c", "libavformat/isom.h", "libavformat/movenccenc.c", "libavutil/encryption_info.h",
    "libavcodec/flac.c", "libavcodec/flac.h", "libavcodec/flacdec.c", "libavcodec/flacenc.c", "libavformat/flacdec.c",
    "libavcodec/apedec.c", "libavformat/ape.c",
    "libavcodec/tta.c", "libavcodec/ttadata.c", "libavcodec/ttadata.h", "libavcodec/ttaenc.c", "libavformat/tta.c",
    "libavcodec/mlp.c", "libavcodec/mlp.h", "libavcodec/mlp_parse.c", "libavcodec/mlp_parse.h", "libavcodec/mlpdec.c",
    "libavcodec/mlpdsp.c", "libavcodec/mlpenc.c", "libavcodec/mlp_parser.c",
    "libavcodec/alac.c", "libavcodec/alac_data.c", "libavcodec/alac_data.h",
    "libavformat/matroskadec.c",
]


def missing(archive):
    """The names in FILES that the archive at this path does not hold, so a pinned list cannot name a file the release lacks."""
    with tarfile.open(archive) as tar:
        names = set(tar.getnames())
    return [name for name in FILES if f"ffmpeg-{VERSION}/{name}" not in names]


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
