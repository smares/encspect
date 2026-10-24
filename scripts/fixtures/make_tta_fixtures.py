#!/usr/bin/env python3
"""Writes the hand-built TTA fixtures in tests/fixtures/tta (built-*.tta): streams written here with tta_model.py's writer, whose frames hold
bytes in place of coded samples (the analyzer compares CRC-32s and does not decode), for each case the format and the damage allow: every
field of the header, the length of a frame, a seek table that is damaged or ends past the data, a file that ends in each part, frames whose
CRC-32 fails, and the tags around a stream.

Each manifest has the same parts as an encoder-made one (make_external_tta_fixtures.py), worked out by tta_model.py from the file, plus a
description and the findings the model does not work out.
"""

import json
import pathlib
import struct

import tta_model as m
from make_wavpack_fixtures import ape_tag, id3v1, id3v2, id3v2_seek, id3v2_title

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "tta"

CASES = {}


def case(name, description, data, start=0, end=None, tags=(), extra=()):
    if end is None:
        # The stream ends where the first tag after it begins.
        end = min([offset for _, offset, _ in tags if offset > start] + [len(data)])
    CASES[name] = {"description": description, "data": data, "start": start, "end": end, "tags": list(tags), "extra": list(extra)}


def pieces(data):
    """(head, table entries, [frames]): the header, the seek table's sizes and each frame of a stream written by tta_model.stream."""
    s = m.Stream(data)
    frames = [data[x["offset"]:x["offset"] + x["size"]] for x in s.frames]
    return data[:m.HEADER_LENGTH], s.table["entries"], frames


def put(head, sizes, frames, **table):
    return head + m.table(sizes, **table) + b"".join(frames)


def flip(data, index, mask=1):
    out = bytearray(data)
    out[index] ^= mask
    return bytes(out)


def damage():
    base = m.stream(44100, 2, 16, 100000)
    head, sizes, frames = pieces(base)
    assert sizes == [204, 304, 404]
    table_end = m.HEADER_LENGTH + 4 * 3 + 4
    first_end = table_end + sizes[0]
    second_end = first_end + sizes[1]
    third = len(base)

    case("three-frames", "Three frames of 44,100 Hz stereo: 46,080 samples in each of the first two and 7,840 in the last.", base)
    case("minimal-mono-8", "One frame of 8-bit mono at 8,000 Hz: 8,359 samples, a whole frame.", m.stream(8000, 1, 8, 8359, [50]))
    case("six-channels-24", "24-bit audio in six channels at 48,000 Hz, two frames.", m.stream(48000, 6, 24, 100000))
    case("last-frame-one-sample", "At 32,000 Hz a frame holds 33,436 samples (33,436.7 truncated): 66,873 samples are two whole frames and one sample.", m.stream(32000, 2, 16, 66873))
    case("whole-frames", "Two whole frames: the samples are a multiple of the frame length.", m.stream(44100, 2, 16, 92160))
    case("crc-only-frames", "Frames of 4 bytes, the CRC-32 of no coded bytes (0).", m.stream(44100, 2, 16, 100000, [0, 0, 0]))
    case("many-small-frames", "1,500 frames of 4 bytes each, 8,359 samples apiece: more than the report lists in one block of children.", m.stream(8000, 1, 16, 8359 * 1500, [0] * 1500))

    # The header.
    case("header-crc-fails", "The CRC-32 of the header differs by one bit; the fields are intact.", flip(base, 21))
    case("header-samples-too-many", "The header counts 300,000 samples, with its CRC-32 right: the seek table is read as seven entries, which take bytes of the first frames, and the data ends before the frames they give.",
         m.header(1, 2, 16, 44100, 300000) + base[m.HEADER_LENGTH:])
    case("audio-format-0", "Audio format 0, which the description does not define and FFmpeg reads as it reads 1.", m.header(0, 2, 16, 44100, 100000) + base[m.HEADER_LENGTH:])
    case("audio-format-2", "Audio format 2, which FFmpeg reads as an encrypted stream; the CRC-32s hold.", m.header(2, 2, 16, 44100, 100000) + base[m.HEADER_LENGTH:])
    case("audio-format-3", "Audio format 3, which FFmpeg refuses.", m.header(3, 2, 16, 44100, 100000) + base[m.HEADER_LENGTH:])
    case("channels-zero", "A header of 0 channels.", m.header(1, 0, 16, 44100, 100000) + base[m.HEADER_LENGTH:])
    case("bits-12", "A header of 12 bits per sample, which the description does not name.", m.header(1, 2, 12, 44100, 100000) + base[m.HEADER_LENGTH:])
    case("sample-rate-zero", "A sample rate of 0: the length of a frame, and so the size of the seek table, is undefined; the rest is not read.", m.header(1, 2, 16, 0, 100000) + base[m.HEADER_LENGTH:])
    case("no-samples", "A header that counts no samples, and a seek table of its CRC-32 alone.", m.header(1, 2, 16, 44100, 0) + m.table([]))
    case("huge-sample-count", "A header that counts 4,294,967,295 samples (93,206 frames) over a file of a few frames: the seek table is cut off after the entries the data holds.",
         m.header(1, 2, 16, 44100, 0xFFFFFFFF) + base[m.HEADER_LENGTH:])
    # The layout the description gives for TTA2 (in development): signature, channels, bits, rate, channel mask, samples (8 bytes), data size (8 bytes), CRC-32.
    tta2 = b"TTA2" + struct.pack("<HHIIQQ", 2, 16, 44100, 3, 100000, len(base) - m.HEADER_LENGTH - 16)
    case("tta2", "A stream that begins with the signature TTA2, a header laid out as the format description gives it and the frames of the first version: the second version, which is not read.",
         tta2 + struct.pack("<I", m.crc32(tta2)) + base[m.HEADER_LENGTH:])

    # The seek table.
    case("seek-table-crc-fails", "The CRC-32 of the seek table differs by one bit; the entries are intact.", flip(base, m.HEADER_LENGTH + 12))
    case("seek-entry-too-large", "The last entry of the seek table is 1,000 bytes too large, with the table's CRC-32 right: the last frame is cut by the end of the data.",
         put(head, [*sizes[:2], sizes[2] + 1000], frames))
    case("seek-table-past-end", "Every entry of the seek table is 1,000,000 bytes, with the table's CRC-32 right: the first frame is cut and the others lie past the end.",
         put(head, [1000000] * 3, frames))
    case("seek-entry-too-small", "The second entry of the seek table is 3 bytes, less than a frame's CRC-32, with the table's CRC-32 right.", put(head, [sizes[0], 3, sizes[2]], frames))
    case("seek-table-short-of-data", "The last entry of the seek table is 50 bytes too small, with the table's CRC-32 right: the last frame ends early, and 50 bytes follow it.",
         put(head, [*sizes[:2], sizes[2] - 50], frames))

    # The frames.
    bad = flip(frames[1], 10)
    case("frame-crc-fails", "One bit of the second frame's coded bytes differs, so its CRC-32 fails.", put(head, sizes, [frames[0], bad, frames[2]]))
    case("frame-crc-fails-every-frame", "The stored CRC-32 of every frame differs by one bit.", put(head, sizes, [flip(f, len(f) - 1) for f in frames]))

    # Where the data ends.
    case("cut-mid-frame", "The file ends in the middle of the second frame.", base[:first_end + 100])
    case("cut-after-frame", "The file ends after the first frame, so two frames the seek table lists are missing.", base[:first_end])
    case("cut-after-table", "The file ends after the seek table.", base[:table_end])
    case("cut-in-table-crc", "The file ends two bytes into the seek table's CRC-32.", base[:table_end - 2])
    case("cut-in-table", "The file ends in the second entry of the seek table.", base[:m.HEADER_LENGTH + 6])
    case("cut-in-header", "The file ends ten bytes into the header.", base[:10])
    case("signature-only", "The file is the signature TTA1.", base[:4])

    # Bytes around the stream.
    case("trailing-junk", "Twenty bytes after the last frame that are no tag.", base + b"\x55" * 20)
    case("trailing-id3v1", "An ID3v1 tag after the last frame.", base + id3v1(), tags=[("ID3v1", third, 128)])
    ape = ape_tag(("Title", "Hand-built title"), ("Artist", "Hand-built artist"))
    case("trailing-ape", "An APEv2 tag after the last frame.", base + ape, tags=[("APE", third, len(ape))])
    case("trailing-ape-and-id3v1", "An APEv2 tag and an ID3v1 tag after the last frame.", base + ape + id3v1(), tags=[("APE", third, len(ape)), ("ID3v1", third + len(ape), 128)])
    lead = id3v2()
    case("leading-id3v2", "An ID3v2 tag before the header.", lead + base, start=len(lead), tags=[("ID3v2", 0, len(lead))])
    case("leading-id3v2-trailing-ape", "An ID3v2 tag before the header and an APEv2 tag after the last frame.", lead + base + ape, start=len(lead), end=len(lead) + third,
         tags=[("ID3v2", 0, len(lead)), ("APE", len(lead) + third, len(ape))])

    # An ID3v1 look-alike inside the last frame, 128 bytes before the end of the data: the stream states where it ends, so the bytes are its own.
    payload = bytearray(m.payload(2, 300))
    payload[300 - 124:300 - 121] = b"TAG"
    last = m.frame(bytes(payload))
    case("id3v1-lookalike-in-last-frame", "The bytes 128 from the end, inside the last frame, are \"TAG\" and 125 more: the seek table says the frame holds them, so they are no ID3v1 tag.",
         put(head, [sizes[0], sizes[1], len(last)], [frames[0], frames[1], last]), extra=[{"code": "tag.near-miss"}])

    # An ID3v2.4 tag whose SEEK frame points at a second ID3v2 tag after the second frame. The seek table says the frames' bytes, and so the second tag,
    # are the stream's own, unless the table is damaged and the stream states no end: then the stream is read up to the second tag.
    title = id3v2_title()
    seek = id3v2_seek(second_end)
    first = len(seek) + second_end
    inside = seek + base[:second_end] + title + base[second_end:]
    case("seek-tag-inside-stream", "An ID3v2.4 tag whose SEEK frame points at a second ID3v2 tag after the second frame. The seek table puts the second tag inside the stream, whose frames after it are then misplaced, so the host does not look for a tag there.",
         inside, start=len(seek), end=len(inside), tags=[("ID3v2", 0, len(seek))], extra=[{"code": "tag.next-in-container"}])
    damaged = inside[:len(seek) + m.HEADER_LENGTH + 12] + bytes([inside[len(seek) + m.HEADER_LENGTH + 12] ^ 1]) + inside[len(seek) + m.HEADER_LENGTH + 13:]
    case("seek-tag-between-frames", "As seek-tag-inside-stream, with the seek table's CRC-32 damaged, so the stream states no end: the second tag is found and the stream is read up to it, and the bytes after it are not read.",
         damaged, start=len(seek), end=first, tags=[("ID3v2", 0, len(seek)), ("ID3v2", first, len(title)), ("Data", first + len(title), third - second_end)],
         extra=[{"code": "tta.data-after-tag"}])


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    damage()
    for name, c in CASES.items():
        data = c["data"]
        end = c["end"]
        extra = {"description": c["description"]}
        result = m.manifest(data, c["start"], end, c["tags"], extra)
        result["findings"] += c["extra"]
        (OUT / f"built-{name}.tta").write_bytes(data)
        (OUT / f"built-{name}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(CASES)} hand-built fixtures to {OUT}")


if __name__ == "__main__":
    main()
