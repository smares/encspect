#!/usr/bin/env python3
"""Downloads the specifications the AC-3 parser, its descriptions and its articles are written from,
pinned by their SHA-256, into a directory outside the repository, which may not hold them:

- ATSC A/52:2018, Digital Audio Compression (AC-3, E-AC-3) Standard, with Annex D (alternate bit
  stream syntax) and Annex E (E-AC-3).
- ETSI TS 102 366 V1.4.1 (2017-09), Digital Audio Compression (AC-3, Enhanced AC-3) Standard, for
  Annex F: the AC-3 and E-AC-3 sample entries and the dac3 and dec3 boxes in MP4, and Annex H, the
  EMDF container.
- ETSI TS 103 420 V1.2.1 (2018-10), Backwards-compatible object audio carriage using Enhanced AC-3,
  for the OAMD and JOC payloads, the addbsi extension (8.3) and dec3's extension (Annex C).

    python3 scripts/fixtures/ac3_specs.py [directory]

ETSI's and ATSC's servers refuse curl's own user agent; downloads.py names a browser's for them.
"""

import pathlib
import sys

import downloads

SPECS = [
    ("A52-2018.pdf", "4580b631f5ac1aafdd31034f28d5fc9c29bce72a3d3f6367e3d9746906e0ffa1",
     ["https://www.atsc.org/wp-content/uploads/2015/03/A52-2018.pdf"]),
    ("ts_102366v010401p.pdf", "0229e151dfd9f8cec427f234798cac679a66fdec096feecc4d5ce455bbe3cadf",
     ["https://www.etsi.org/deliver/etsi_ts/102300_102399/102366/01.04.01_60/ts_102366v010401p.pdf"]),
    ("ts_103420v010201p.pdf", "e532bfc4f8be4a97c7c9cdd9f6bcc40634ecf8ef93a1dc490fcb15c16265daa7",
     ["https://www.etsi.org/deliver/etsi_ts/103400_103499/103420/01.02.01_60/ts_103420v010201p.pdf"]),
]


def main():
    target = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "ac3-specs")
    target.mkdir(parents=True, exist_ok=True)
    for name, digest, addresses in SPECS:
        downloads.fetch(addresses, digest, target / name)
        print(f"{target / name}: SHA-256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
