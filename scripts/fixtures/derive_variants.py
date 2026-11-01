#!/usr/bin/env python3
"""Derived damage variants of the encoder-written fixtures (docs/design/official-decoder-crosschecks.md, "Fixtures").

  derive_variants.py ogg --out DIR [--manifest FILE] [FILE...]

One script writes the variants, by fixed rules and without randomness, so that every job of the official-decoder workflow (Linux, the
comparison, later macOS and Windows) decodes the same bytes. Each variant's SHA-256 is recorded in the manifest, and the comparison matches
records by SHA-256. The variants are never committed and never uploaded: artifacts and caches hold records only.

Ogg (Opus and Vorbis files named external-*, which an encoder wrote). The audio pages are the pages from the first one whose granule position is
above 0 (the header pages carry 0 or -1); a file with fewer than two has no variants. With A the audio pages, first = A[0],
middle = A[len(A) // 2] and last = A[-1] (which is the middle page when there are two):

  cut-first-page    the file cut in the middle of the first audio page
  cut-middle-page   the file cut in the middle of the middle audio page
  cut-last-page     the file cut in the middle of the last audio page
  flip-byte         the byte in the middle of the middle audio page's body changed (XOR 0x55), so the page fails its CRC
  drop-page         the middle audio page removed
  repeat-page       the middle audio page written twice in a row
  drop-packet       the packet in the middle of those the middle audio page holds whole removed, the page's lacing and CRC rewritten
  repeat-packet     that packet written twice in a row, the page's lacing and CRC rewritten

A variant that equals an earlier one (the middle page is the last one) is not written. The packet rules leave the granule positions as they were.
"""

import argparse
import hashlib
import json
import pathlib
import struct
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
OGG_DIRECTORY = ROOT / "tests" / "fixtures" / "ogg"
OGG_RULES = ("cut-first-page", "cut-middle-page", "cut-last-page", "flip-byte", "drop-page", "repeat-page", "drop-packet", "repeat-packet")


def ogg_pages(data):
    """(offset, size, header size, granule position as a signed integer) of every whole page from the start of data, while pages follow each other."""
    pages, pos = [], 0
    while pos + 27 <= len(data) and data[pos:pos + 4] == b"OggS":
        segments = data[pos + 26]
        if pos + 27 + segments > len(data):
            break
        header = 27 + segments
        size = header + sum(data[pos + 27:pos + header])
        if pos + size > len(data):
            break
        pages.append((pos, size, header, struct.unpack_from("<q", data, pos + 6)[0]))
        pos += size
    return pages


def crc_table():
    table = []
    for i in range(256):
        r = i << 24
        for _ in range(8):
            r = ((r << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if r & 0x80000000 else (r << 1) & 0xFFFFFFFF
        table.append(r)
    return table


CRC_TABLE = crc_table()


def ogg_crc(page):
    """The CRC of a page whose CRC field holds 0 (RFC 3533, section 6)."""
    crc = 0
    for byte in page:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ CRC_TABLE[(crc >> 24) ^ byte]
    return crc


def rewrite_packet(data, page, repeat):
    """The page with its middle whole packet removed (repeat False) or written twice (repeat True), lacing and CRC rewritten; None when no packet is whole in the page or the lacing would exceed 255 segments."""
    offset, size, header_size, _ = page
    segments = list(data[offset + 27:offset + header_size])
    body = data[offset + header_size:offset + size]
    continued = data[offset + 5] & 1
    # packets as (first segment, last segment + 1) that end in this page, and whether each began in it
    packets, start = [], 0
    for i, lace in enumerate(segments):
        if lace < 255:
            packets.append((start, i + 1, start > 0 or not continued))
            start = i + 1
    whole = [(a, b) for a, b, owned in packets if owned]
    if not whole:
        return None
    a, b = whole[len(whole) // 2]
    taken = sum(segments[:a])
    length = sum(segments[a:b])
    if repeat:
        if len(segments) + (b - a) > 255:
            return None
        new_segments = segments[:b] + segments[a:b] + segments[b:]
        new_body = body[:taken + length] + body[taken:taken + length] + body[taken + length:]
    else:
        new_segments = segments[:a] + segments[b:]
        new_body = body[:taken] + body[taken + length:]
    page_bytes = bytearray(data[offset:offset + 26]) + bytes([len(new_segments)]) + bytes(new_segments) + new_body
    page_bytes[22:26] = b"\0\0\0\0"
    page_bytes[22:26] = struct.pack("<I", ogg_crc(page_bytes))
    return bytes(page_bytes)


def ogg_variants(data):
    """{rule: bytes} for the bytes of one Ogg file; empty when the file has fewer than three audio pages."""
    pages = ogg_pages(data)
    first_audio = next((i for i, p in enumerate(pages) if p[3] > 0), None)
    if first_audio is None:
        return {}
    audio = pages[first_audio:]
    if len(audio) < 2:
        return {}
    first, middle, last = audio[0], audio[len(audio) // 2], audio[-1]
    flipped = bytearray(data)
    flipped[middle[0] + middle[2] + (middle[1] - middle[2]) // 2] ^= 0x55
    page = data[middle[0]:middle[0] + middle[1]]
    variants = {
        "cut-first-page": data[:first[0] + first[1] // 2],
        "cut-middle-page": data[:middle[0] + middle[1] // 2],
        "cut-last-page": data[:last[0] + last[1] // 2],
        "flip-byte": bytes(flipped),
        "drop-page": data[:middle[0]] + data[middle[0] + middle[1]:],
        "repeat-page": data[:middle[0] + middle[1]] + page + data[middle[0] + middle[1]:],
    }
    for rule, repeat in (("drop-packet", False), ("repeat-packet", True)):
        rewritten = rewrite_packet(data, middle, repeat)
        if rewritten is not None:
            variants[rule] = data[:middle[0]] + rewritten + data[middle[0] + middle[1]:]
    seen, unique = set(), {}
    for rule, content in variants.items():
        if content not in seen:
            seen.add(content)
            unique[rule] = content
    return unique


def ogg_sources():
    """The fixtures the rules apply to: Opus and Vorbis files an encoder wrote."""
    return sorted(p for p in OGG_DIRECTORY.iterdir() if p.name.startswith("external-") and p.suffix in (".ogg", ".opus"))


def write_ogg(out, sources=None):
    """Writes the variants into out; returns the manifest entries (name, file written, source path and SHA-256, rule, SHA-256, size)."""
    out = pathlib.Path(out)
    out.mkdir(parents=True, exist_ok=True)
    entries = []
    for source in sources or ogg_sources():
        data = pathlib.Path(source).read_bytes()
        for rule, content in ogg_variants(data).items():
            name = "%s.%s%s" % (pathlib.Path(source).stem, rule, pathlib.Path(source).suffix)
            (out / name).write_bytes(content)
            try:
                shown = str(pathlib.Path(source).resolve().relative_to(ROOT)).replace("\\", "/")
            except ValueError:
                shown = str(source)
            entries.append({"name": name, "source": shown, "sourceSha256": hashlib.sha256(data).hexdigest(), "rule": rule,
                            "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)})
    return entries


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("format", choices=["ogg"])
    parser.add_argument("--out", required=True)
    parser.add_argument("--manifest")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args(argv)
    entries = write_ogg(args.out, [pathlib.Path(f) for f in args.files] or None)
    manifest = json.dumps({"format": args.format, "rules": list(OGG_RULES), "variants": entries}, indent=1) + "\n"
    if args.manifest:
        pathlib.Path(args.manifest).write_text(manifest, encoding="utf-8")
    print("%d variants of %d files" % (len(entries), len({e["source"] for e in entries})))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
