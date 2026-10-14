#!/usr/bin/env python3
"""Downloads the specification the DTS parser, its descriptions and its articles are written from,
pinned by its SHA-256, into a directory outside the repository, which may not hold it:

- ETSI TS 102 114 V1.6.1 (2019-08), DTS Coherent Acoustics; Core and Extensions with Additional
  Profiles: the core (clause 5), its extensions XCh, X96 and XXCH (clause 6), the extension
  substream (clause 7), XBR, XLL and LBR, and Annex E: the DTS formats in MP4 (the ddts box).

    python3 scripts/fixtures/dts_specs.py [directory]

The clause and table numbers in the DTS parser's descriptions and articles are its. Versions 1.3.1
to 1.6.1 all list 960 kbit/s for RATE 16, as the 1.6.1 text does; FFmpeg and libdca list 896.
"""

import pathlib
import sys

import downloads

NAME = "ts_102114v010601p.pdf"
SHA256 = "29f9e50575e48bfe55222c5f004088af379bc9a6c65274ed975a81dca10647ba"
ADDRESSES = ["https://www.etsi.org/deliver/etsi_ts/102100_102199/102114/01.06.01_60/ts_102114v010601p.pdf"]


def fetch(directory):
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    downloads.fetch(ADDRESSES, SHA256, directory / NAME)
    return directory / NAME


def main():
    path = fetch(sys.argv[1] if len(sys.argv) > 1 else "dts-specs")
    print(f"{path}: SHA-256 {SHA256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
