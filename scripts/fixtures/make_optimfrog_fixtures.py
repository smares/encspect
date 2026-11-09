#!/usr/bin/env python3
"""Writes the hand-built OptimFROG fixtures in tests/fixtures/optimfrog (built-*.ofr) from the committed external-chunks.ofr, with tags after the blocks and tag-like
bytes inside a data block (docs/design/tak-optimfrog-headers.md, "Hand-built"). The blocks are the encoder's; only the bytes around them and the payload of one
data block are written here. The manifests come from optimfrog_model.py, which reads the specification and not the analyzer.

    python3 scripts/fixtures/make_optimfrog_fixtures.py
"""

import json
import pathlib
import struct
import zlib

import optimfrog_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "optimfrog"
SOURCE = OUT / "external-chunks.ofr"


def ape_footer_only():
    """An APEv2 tag without a header: one item, and the 32-byte footer whose size counts the items and the footer."""
    item = struct.pack("<II", 4, 0) + b"Title\0" + b"Song"
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(item) + 32, 1, 0) + bytes(8)
    return item + footer


def ape_with_header():
    item = struct.pack("<II", 4, 0) + b"Title\0" + b"Song"
    size = len(item) + 32
    header = b"APETAGEX" + struct.pack("<IIII", 2000, size, 1, 0xA0000000) + bytes(8)
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, size, 1, 0x80000000) + bytes(8)
    return header + item + footer


def id3v1(title=b"Song", artist=b"Artist"):
    return b"TAG" + title.ljust(30, b"\0") + artist.ljust(30, b"\0") + bytes(30) + bytes(4) + bytes(30) + b"\xff"


def lyrics3():
    fields = b"LYR" + b"00004" + b"abcd"
    return b"LYRICSBEGIN" + fields + f"{len(fields) + 11:06d}".encode() + b"LYRICS200"


def block(block_id, data):
    return block_id + struct.pack("<I", len(data)) + data


def with_tag_bytes_in_comp(base):
    """The file with its data block's payload replaced by bytes that hold an APEv2 footer's signature and an ID3v1 tag's, and the CRC-32 recomputed."""
    blocks, _ = m.walk(base)
    parts = []
    for b in blocks:
        data = base[b["offset"] + 8:b["end"]]
        if b["id"] == "COMP":
            head = data[4:14]
            payload = bytes(range(64)) + b"APETAGEX" + struct.pack("<IIII", 2000, 32, 0, 0) + bytes(8) + id3v1() + bytes(range(64))
            body = head + payload
            data = struct.pack("<I", zlib.crc32(body) & 0xFFFFFFFF) + body
        parts.append(block(b["id"].encode(), data))
    return b"".join(parts)


def bad_ape_footer():
    """An APEv2 footer whose tag size and item count fit no tag: the host's search for trailing tags finds the signature at the end of the data and rejects it."""
    return b"APETAGEX" + struct.pack("<IIII", 2000, 4000, 9, 0) + bytes(8)


FILES = {
    "built-tag-like-bytes-after-blocks.ofr": (lambda b: b + bad_ape_footer(), ["optimfrog.data-after-blocks", "tag.near-miss"]),
    "built-tag-ape-footer-only.ofr": (lambda b: b + ape_footer_only(), []),
    "built-tag-ape-with-header.ofr": (lambda b: b + ape_with_header(), []),
    "built-tag-ape-and-id3v1.ofr": (lambda b: b + ape_footer_only() + id3v1(), []),
    "built-tag-lyrics3-and-id3v1.ofr": (lambda b: b + lyrics3() + id3v1(), []),
    "built-tag-id3v1-short-title.ofr": (lambda b: b + id3v1(b"A", b"B"), []),
    "built-tag-like-bytes-in-data-block.ofr": (with_tag_bytes_in_comp, []),
}


def main():
    base = SOURCE.read_bytes()
    for name, (build, findings) in FILES.items():
        data = build(base)
        (OUT / name).write_bytes(data)
        # The model walks the blocks; the tag after them is the host's, so the manifest's blocks end where the encoder's end.
        manifest = m.manifest(data, {"encoder": {"built": "make_optimfrog_fixtures.py from external-chunks.ofr"}})
        manifest["findings"] += [{"code": code} for code in findings]
        (OUT / name.replace(".ofr", ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print("wrote", name, len(data))


if __name__ == "__main__":
    main()
