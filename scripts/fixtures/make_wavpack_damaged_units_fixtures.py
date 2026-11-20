#!/usr/bin/env python3
"""Writes the fixture of tests/fixtures/wavpack/damaged-units: external-lossless-3ch-unassigned.wv (wavpack 5.6.0, three blocks to a set) with
the stored checksum of the middle block of its first set changed, so that a block that is not the set's last fails and the whole set must be
withheld from the measurements (docs/design/damaged-units.md). The block's data is intact, so it still decodes.
"""

import pathlib
import struct

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tests" / "fixtures" / "wavpack" / "external-lossless-3ch-unassigned.wv"
OUT = ROOT / "tests" / "fixtures" / "wavpack" / "damaged-units"


def blocks(data):
    at = 0
    while at + 32 <= len(data) and data[at:at + 4] == b"wvpk":
        size = struct.unpack_from("<I", data, at + 4)[0] + 8
        yield at, size
        at += size


def main():
    data = bytearray(SOURCE.read_bytes())
    found = list(blocks(data))
    # The first set is the blocks that share the first block's index; the checksum is the last sub-block of a block, 4 data bytes after its 2-byte header.
    index = struct.unpack_from("<I", data, found[0][0] + 16)[0]
    first_set = [b for b in found if struct.unpack_from("<I", data, b[0] + 16)[0] == index and b[0] <= found[2][0]]
    assert len(first_set) == 3, first_set
    start, size = first_set[1]
    data[start + size - 3] ^= 0x55
    OUT.mkdir(exist_ok=True)
    (OUT / "set-block-checksum-changed.wv").write_bytes(data)


if __name__ == "__main__":
    main()
