#!/usr/bin/env python3
"""Derived damage variants of the encoder-written fixtures (docs/design/official-decoder-crosschecks.md, "Fixtures").

  derive_variants.py ogg|tta|aac --out DIR [--manifest FILE] [FILE...]

One script writes the variants, by fixed rules and without randomness, so that every job of the official-decoder workflow (Linux, the
comparison, later macOS and Windows) decodes the same bytes. Each variant's SHA-256 is recorded in the manifest, and the comparison matches
records by SHA-256. The variants are never committed and never uploaded: artifacts and caches hold records only.

Ogg (Opus and Vorbis files named external-*, which an encoder wrote). The audio pages are the pages from the first one whose granule position is
above 0 (the header pages carry 0 or -1); a file with fewer than two has no variants. With A the audio pages, first = A[0], last = A[-1], and
middle the page in the middle of A[1:-1] when A has three or more pages (so that audio comes before and after it and it is not the end-of-stream page);
a file with two audio pages has no mid-stream page, and its middle is the last page:

  cut-first-page    the file cut in the middle of the first audio page
  cut-middle-page   the file cut in the middle of the middle audio page
  cut-last-page     the file cut in the middle of the last audio page
  flip-byte         the byte in the middle of the middle audio page's body changed (XOR 0x55), so the page fails its CRC
  drop-page         the middle audio page removed
  repeat-page       the middle audio page written twice in a row
  drop-packet       the packet in the middle of those the middle audio page holds whole removed, the page's lacing and CRC rewritten
  repeat-packet     that packet written twice in a row, the page's lacing and CRC rewritten

A variant that equals an earlier one (a two-page file's middle page is its last) is not written. The packet rules leave the granule positions as they were.

TTA (files named external-*, which an encoder wrote). The frames are those the header and the seek table give (the description: a frame holds the sample rate times
256/245 samples, truncated; an entry is a frame's length with its CRC-32); a file whose layout does not fit its length has no variants. With F the frames, first = F[0],
last = F[-1] and middle = F[len(F) // 2]:

  cut-first-frame        the file cut in the middle of the first frame
  cut-middle-frame       the file cut in the middle of the middle frame
  cut-last-frame         the file cut in the middle of the last frame
  flip-byte              the byte in the middle of the middle frame's coded bytes changed (XOR 0x55), so the frame fails its CRC-32
  flip-crc               the last byte of the middle frame (its CRC-32) changed (XOR 0x55), so the coded bytes are intact and the frame fails its CRC-32
  drop-frame             the middle frame removed from the data, the seek table as it was (a file of two or more frames)
  drop-frame-table       the middle frame removed, its entry removed from the seek table and the table's CRC-32 rewritten, the header as it was (two or more frames): the table
                         is 4 bytes shorter than the header calls for, so a reader takes the recomputed CRC-32 as the last entry and the next 4 bytes as the table's CRC-32,
                         which fails
  repeat-frame           the middle frame written twice in a row, the seek table as it was (two or more frames)
  samples-plus-one       the header's samples one more, its CRC-32 rewritten
  samples-minus-one      the header's samples one fewer, its CRC-32 rewritten

A variant that equals an earlier one (a one-frame file's first, middle and last frame are the same frame) is not written.

AAC (the files of tests/fixtures/aac and tests/fixtures/aac/apple, which encoders wrote). The units are those of unit_list.py, the one extractor of the direct paths, so a
variant is cut where the decoders will be fed. A file with fewer than three units has no variants. With U the units, first = U[0], last = U[-1] and middle = U[len(U) // 2].

ADTS files (the frames are the extractor's; the header is 7 bytes, 9 with a CRC):

  cut-first-frame    the file cut in the middle of the first frame
  cut-middle-frame   the file cut in the middle of the middle frame
  cut-last-frame     the file cut in the middle of the last frame
  flip-byte          the byte in the middle of the middle frame's payload changed (XOR 0x55)
  flip-crc           the first CRC byte of the middle frame changed (XOR 0x55), the payload intact (a file whose middle frame has no CRC has no such variant)
  drop-frame         the middle frame removed
  repeat-frame       the middle frame written twice in a row

MP4 files with one AAC sound track (the samples are the extractor's, in file order):

  flip-byte          the byte in the middle of the middle sample changed (XOR 0x55), the tables as they were
  cut-first-sample, cut-middle-sample, cut-last-sample
                     the file cut in the middle of the first, middle or last sample, only for a file whose box tables come before its samples and whose last
                     sample ends at the end of the file (so that the cut leaves the tables intact and the samples are what is cut)

A variant that equals an earlier one is not written.
"""

import argparse
import hashlib
import json
import pathlib
import struct
import sys
import zlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import unit_list  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
OGG_DIRECTORY = ROOT / "tests" / "fixtures" / "ogg"
TTA_DIRECTORY = ROOT / "tests" / "fixtures" / "tta"
AAC_DIRECTORIES = (ROOT / "tests" / "fixtures" / "aac", ROOT / "tests" / "fixtures" / "aac" / "apple")
AAC_EXTENSIONS = (".aac", ".m4a", ".mp4", ".m4b", ".3gp")
TTA_RULES = ("cut-first-frame", "cut-middle-frame", "cut-last-frame", "flip-byte", "flip-crc", "drop-frame", "drop-frame-table", "repeat-frame", "samples-plus-one", "samples-minus-one")
AAC_RULES = ("cut-first-frame", "cut-middle-frame", "cut-last-frame", "flip-byte", "flip-crc", "drop-frame", "repeat-frame", "cut-first-sample", "cut-middle-sample", "cut-last-sample")
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
    """{rule: bytes} for the bytes of one Ogg file; empty when the file has fewer than two audio pages."""
    pages = ogg_pages(data)
    first_audio = next((i for i, p in enumerate(pages) if p[3] > 0), None)
    if first_audio is None:
        return {}
    audio = pages[first_audio:]
    if len(audio) < 2:
        return {}
    inner = audio[1:-1]
    first, middle, last = audio[0], inner[len(inner) // 2] if inner else audio[-1], audio[-1]
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


def tta_layout(data):
    """(header offset, seek table offset, frames as (offset, size)) of a TTA file whose header, seek table and frames fit its length; None otherwise. A leading ID3v2 tag is skipped."""
    start = 0
    if data[:3] == b"ID3" and len(data) >= 10:
        start = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
    if data[start:start + 4] != b"TTA1" or len(data) < start + 22:
        return None
    rate, samples = struct.unpack_from("<II", data, start + 10)
    frame_samples = rate * 256 // 245
    if frame_samples == 0 or samples == 0:
        return None
    count = -(-samples // frame_samples)
    table = start + 22
    position = table + 4 * count + 4
    if position > len(data):
        return None
    frames = []
    for i in range(count):
        size = struct.unpack_from("<I", data, table + 4 * i)[0]
        if size < 4 or position + size > len(data):
            return None
        frames.append((position, size))
        position += size
    return start, table, frames


def tta_variants(data):
    """{rule: bytes} for the bytes of one TTA file; empty when its layout does not fit its length."""
    layout = tta_layout(data)
    if layout is None:
        return {}
    header, table, frames = layout
    first, middle, last = frames[0], frames[len(frames) // 2], frames[-1]
    flipped = bytearray(data)
    flipped[middle[0] + (middle[1] - 4) // 2] ^= 0x55
    crc_flipped = bytearray(data)
    crc_flipped[middle[0] + middle[1] - 1] ^= 0x55
    variants = {
        "cut-first-frame": data[:first[0] + first[1] // 2],
        "cut-middle-frame": data[:middle[0] + middle[1] // 2],
        "cut-last-frame": data[:last[0] + last[1] // 2],
        "flip-byte": bytes(flipped),
        "flip-crc": bytes(crc_flipped),
    }
    if len(frames) >= 2:
        index = len(frames) // 2
        variants["drop-frame"] = data[:middle[0]] + data[middle[0] + middle[1]:]
        entries = data[table:table + 4 * len(frames)]
        shorter = entries[:4 * index] + entries[4 * index + 4:]
        variants["drop-frame-table"] = data[:table] + shorter + struct.pack("<I", zlib.crc32(shorter)) + data[table + 4 * len(frames) + 4:middle[0]] + data[middle[0] + middle[1]:]
        variants["repeat-frame"] = data[:middle[0] + middle[1]] + data[middle[0]:middle[0] + middle[1]] + data[middle[0] + middle[1]:]
    for rule, delta in (("samples-plus-one", 1), ("samples-minus-one", -1)):
        changed = bytearray(data)
        samples = struct.unpack_from("<I", data, header + 14)[0] + delta
        changed[header + 14:header + 18] = struct.pack("<I", samples)
        changed[header + 18:header + 22] = struct.pack("<I", zlib.crc32(bytes(changed[header:header + 18])))
        variants[rule] = bytes(changed)
    seen, unique = set(), {}
    for rule, content in variants.items():
        if content not in seen:
            seen.add(content)
            unique[rule] = content
    return unique


def tta_sources():
    """The fixtures the rules apply to: TTA files an encoder wrote."""
    return sorted(p for p in TTA_DIRECTORY.iterdir() if p.name.startswith("external-") and p.suffix == ".tta")


def aac_variants(data):
    """{rule: bytes} for the bytes of one AAC file (ADTS or one AAC sound track in MP4); empty when the extractor finds fewer than three units."""
    found = unit_list.extract_bytes(data)
    units = found["units"]
    if found.get("codec") != "aac" or found.get("encrypted") or len(units) < 3:
        return {}
    first, middle, last = units[0], units[len(units) // 2], units[-1]
    variants = {}
    flipped = bytearray(data)
    if found["kind"] == "adts":
        header = 7 if data[middle[0] + 1] & 1 else 9
        if middle[2] <= header:
            return {}
        flipped[middle[0] + header + (middle[2] - header) // 2] ^= 0x55
        variants["cut-first-frame"] = data[:first[0] + first[2] // 2]
        variants["cut-middle-frame"] = data[:middle[0] + middle[2] // 2]
        variants["cut-last-frame"] = data[:last[0] + last[2] // 2]
        variants["flip-byte"] = bytes(flipped)
        if header == 9:
            crc = bytearray(data)
            crc[middle[0] + 7] ^= 0x55
            variants["flip-crc"] = bytes(crc)
        variants["drop-frame"] = data[:middle[0]] + data[middle[0] + middle[2]:]
        variants["repeat-frame"] = data[:middle[0] + middle[2]] + data[middle[0]:middle[0] + middle[2]] + data[middle[0] + middle[2]:]
    elif found["kind"] == "mp4":
        if middle[2] < 1:
            return {}
        flipped[middle[0] + middle[2] // 2] ^= 0x55
        variants["flip-byte"] = bytes(flipped)
        moov = next((start for kind, _, _, start in unit_list.boxes(data, 0, len(data)) if kind == b"moov"), None)
        ordered = sorted(units)
        if moov is not None and moov < ordered[0][0] and ordered[-1][0] + ordered[-1][2] == len(data) and all(u[2] == u[1] for u in ordered):
            variants["cut-first-sample"] = data[:ordered[0][0] + ordered[0][2] // 2]
            variants["cut-middle-sample"] = data[:ordered[len(ordered) // 2][0] + ordered[len(ordered) // 2][2] // 2]
            variants["cut-last-sample"] = data[:ordered[-1][0] + ordered[-1][2] // 2]
    seen, unique = set(), {}
    for rule, content in variants.items():
        # A variant the extractor finds no unit in (a file cut in its first frame) gives the decoders nothing to be fed and is not written.
        if content not in seen and content != data and unit_list.extract_bytes(content)["units"]:
            seen.add(content)
            unique[rule] = content
    return unique


def aac_sources():
    """The fixtures the rules apply to: the AAC files of tests/fixtures/aac and tests/fixtures/aac/apple, which encoders wrote."""
    return sorted((p for directory in AAC_DIRECTORIES for p in directory.iterdir() if p.is_file() and p.suffix.lower() in AAC_EXTENSIONS), key=lambda p: (p.name, str(p)))


def write_variants(out, sources, variants):
    """Writes the variants into out; returns the manifest entries (name, file written, source path and SHA-256, rule, SHA-256, size)."""
    out = pathlib.Path(out)
    out.mkdir(parents=True, exist_ok=True)
    entries = []
    for source in sources:
        data = pathlib.Path(source).read_bytes()
        for rule, content in variants(data).items():
            name = "%s.%s%s" % (pathlib.Path(source).stem, rule, pathlib.Path(source).suffix)
            (out / name).write_bytes(content)
            try:
                shown = str(pathlib.Path(source).resolve().relative_to(ROOT)).replace("\\", "/")
            except ValueError:
                shown = str(source)
            entries.append({"name": name, "source": shown, "sourceSha256": hashlib.sha256(data).hexdigest(), "rule": rule,
                            "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)})
    return entries


def write_ogg(out, sources=None):
    return write_variants(out, sources or ogg_sources(), ogg_variants)


def write_tta(out, sources=None):
    return write_variants(out, sources or tta_sources(), tta_variants)


def write_aac(out, sources=None):
    return write_variants(out, sources or aac_sources(), aac_variants)


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("format", choices=["ogg", "tta", "aac"])
    parser.add_argument("--out", required=True)
    parser.add_argument("--manifest")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args(argv)
    write = {"ogg": write_ogg, "tta": write_tta, "aac": write_aac}[args.format]
    entries = write(args.out, [pathlib.Path(f) for f in args.files] or None)
    manifest = json.dumps({"format": args.format, "rules": list({"ogg": OGG_RULES, "tta": TTA_RULES, "aac": AAC_RULES}[args.format]), "variants": entries}, indent=1) + "\n"
    if args.manifest:
        pathlib.Path(args.manifest).write_text(manifest, encoding="utf-8")
    print("%d variants of %d files" % (len(entries), len({e["source"] for e in entries})))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
