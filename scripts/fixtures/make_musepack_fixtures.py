#!/usr/bin/env python3
"""Writes the hand-built Musepack fixtures in tests/fixtures/musepack.

Damaged and odd SV7 and SV8 files are made from the committed encoder-made fixtures
(make_external_musepack_fixtures.py) by changing, cutting or adding bytes: a stream header whose CRC
fails, a reserved sample rate, a sample count the audio packets do not reach, a missing stream end,
a cut audio packet, keys the specification does not reserve or does not allow, a seek table that
disagrees with the audio packets, data after the stream end, a stream end of the wrong size, no
ReplayGain packet; an SV7 header with fields libmpcdec refuses, a cut stream, a damaged frame length,
data after the frames, a header cut short; and both versions with ID3v2 before and APE and ID3v1
after, and with "TAG" inside the audio where an ID3v1 tag would sit; an SV5 stream whose frame is
too short for its subband resolutions. Streams of version 6, of which no encoder was found, are
written from their header layout (libmpcdec 1.2.6), with frames of zero bits. Each has a
manifest worked out by musepack_model.py plus the findings the analyzer must report.

Standard library only and deterministic, so CI reruns it and fails when the committed files differ.
"""

import json
import pathlib
import struct
import zlib

import make_tag_fixtures as tags
import musepack_model as m

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "musepack"


def external(name):
    return (OUT / f"external-{name}.mpc").read_bytes()


def encode_size(value, width=1):
    """A size field (SV8 specification), at least width bytes long."""
    groups = []
    while value or len(groups) < width:
        groups.append(value & 0x7F)
        value >>= 7
    groups.reverse()
    return bytes((g | 0x80) if i < len(groups) - 1 else g for i, g in enumerate(groups))


def packet(key, payload):
    size = 2 + len(payload) + 1
    if size > 127:
        size += 1
    return key + encode_size(size) + payload


def packets(data):
    found, _ = m.packets(data)
    return found


def find(data, key, nth=0):
    return [p for p in packets(data) if p[2] == key][nth]


def with_sh(data, change):
    """data with its stream header's payload changed by change(bytearray) and its CRC recomputed."""
    offset, length, _, _, header = find(data, "SH")
    payload = bytearray(data[offset + header:offset + length])
    change(payload)
    payload[:4] = struct.pack(">I", zlib.crc32(bytes(payload[4:])))
    return data[:offset + header] + bytes(payload) + data[offset + length:]


def insert(data, offset, extra):
    return data[:offset] + extra + data[offset:]


def sv8_bad_crc():
    data = bytearray(external("mpcenc-standard"))
    offset, _, _, _, header = find(data, "SH")
    data[offset + header] ^= 0x01
    return bytes(data)


def sv8_rate_reserved():
    def change(p):
        at = 5
        for _ in range(2):
            while p[at] & 0x80:
                at += 1
            at += 1
        p[at] = (p[at] & 0x1F) | (5 << 5)
    return with_sh(external("mpcenc-standard"), change)


def sv8_samples_beyond():
    """The stream header's sample count 23040 larger: 20 frames, 5 audio packets of 4, more than the stream holds."""
    data = external("mpcenc-packets")
    offset, length, _, _, header = find(data, "SH")
    payload = data[offset + header:offset + length]
    samples, after = m.size(payload, 5)
    old = payload[5:after]
    new = encode_size(samples + 23040, len(old))
    assert len(new) == len(old)
    return with_sh(data, lambda p: p.__setitem__(slice(5, after), new))


def sv8_no_end():
    data = external("mpcenc-standard")
    return data[:find(data, "SE")[0]]


def sv8_truncated():
    data = external("mpcenc-packets")
    return data[:len(data) * 3 // 5]


def before_end(data, extra):
    return insert(data, find(data, "SE")[0], extra)


def sv8_unknown_key():
    return before_end(external("mpcenc-standard"), packet(b"XY", b"\x01\x02"))


def sv8_invalid_key():
    return before_end(external("mpcenc-standard"), packet(b"ab", b"\x01\x02"))


def sv8_seek_mismatch():
    """An unknown packet between the first two audio packets, with the seek table offset moved along,
    so the seek table's entries after the first point 5 bytes early."""
    data = external("mpcenc-packets")
    extra = packet(b"XY", b"\x01\x02")
    second = find(data, "AP", 1)[0]
    data = insert(data, second, extra)
    offset, length, _, _, header = find(data, "SO")
    value, after = m.size(data, offset + header)
    new = encode_size(value + len(extra), after - offset - header)
    return data[:offset + header] + new + data[offset + header + len(new):]


def sv8_data_after_end():
    return external("mpcenc-standard") + bytes(range(1, 17))


def sv8_end_size():
    data = external("mpcenc-standard")
    return data[:find(data, "SE")[0]] + b"SE\x04\x00"


def sv8_no_replaygain():
    data = external("mpcenc-no-ei-no-st")
    offset, length, _, _, _ = find(data, "RG")
    return data[:offset] + data[offset + length:]


def id3v2_tag():
    return tags.id3v2(3, [tags.frame23("TIT2", tags.text_frame_data(0, ["Musepack fixture"]))])


def trailing_tags():
    return tags.ape_tag([tags.ape_item("Title", "Musepack fixture")]) + tags.id3v1(title="Musepack fixture")


def sv8_tags():
    return id3v2_tag() + external("mpcenc-standard") + trailing_tags()


def tag_inside(data):
    data = bytearray(data)
    data[len(data) - 128:len(data) - 125] = b"TAG"
    return bytes(data)


def sv8_tag_inside():
    return tag_inside(external("mpcenc-standard"))


def sv7_truncated():
    data = external("mppenc-standard")
    return data[:len(data) * 3 // 5]


def write_bits(words, bit, count, value):
    """Writes value into count bits at bit of 32-bit words read from their most significant bit."""
    for i in range(count):
        position = bit + i
        mask = 1 << (31 - (position & 31))
        word = position >> 5
        words[word] = (words[word] | mask) if (value >> (count - 1 - i)) & 1 else (words[word] & ~mask)


def sv7_damaged_length():
    """The fourth frame's length 64 bits shorter, so the frames after it are read from the wrong bits."""
    data = bytearray(external("mppenc-standard"))
    w = m.Words(bytes(data))
    frame = m.sv7(bytes(data))["frames"][3]
    bit = frame["bit"]
    words = list(struct.unpack(f"<{len(data) // 4}I", data))
    write_bits(words, bit, 20, frame["length"] - 64)
    assert w.read(bit, 20) == frame["length"]
    return struct.pack(f"<{len(words)}I", *words)


def sv5_band_overrun():
    """The fourth frame's length cut to 100 bits and the fifth's lengthened by as much, so the units
    still end where the encoder ends them, but the fourth frame's subband resolutions run past its
    length."""
    data = external("mppsv5-vbr")
    w = m.Words(data)
    words = list(struct.unpack(f"<{len(data) // 4}I", data))
    bit = 64
    for _ in range(3):
        bit += 20 + w.read(bit, 20)
    fourth = w.read(bit, 20)
    fifth = w.read(bit + 20 + fourth, 20)
    write_bits(words, bit, 20, 100)
    write_bits(words, bit + 120, 20, fifth + fourth - 100)
    return struct.pack(f"<{len(words)}I", *words)


def sv7_trailing_data():
    return external("mppenc-standard") + bytes([0x55] * 40)


def sv7_header_fields():
    """The former intensity stereo flag set and the max band 0, which libmpcdec refuses, and unused bits set."""
    data = bytearray(external("mppenc-standard"))
    word = struct.unpack("<I", data[8:12])[0]
    word = (word | (1 << 31)) & ~(0x3F << 24)
    data[8:12] = struct.pack("<I", word)
    word = struct.unpack("<I", data[20:24])[0] | 1
    data[20:24] = struct.pack("<I", word)
    return bytes(data)


def sv7_header_truncated():
    return external("mppenc-standard")[:20]


def sv7_tags():
    return id3v2_tag() + external("mppenc-standard") + trailing_tags()


def sv7_tag_inside():
    return tag_inside(external("mppenc-standard"))


def old(version, bitrate=0, frames=(40, 75, 13)):
    """A stream of version 6 (libmpcdec 1.2.6 streaminfo_read_header_sv6): the header word, the frame
    count, then frames of a 20-bit length and that many zero bits, and the 11 bits after the last;
    written into 32-bit little-endian words from their most significant bit."""
    bits = format(bitrate, "09b") + "0" + "1" + format(version, "010b") + format(27, "05b") + format(1, "06b")
    bits += format(len(frames), "032b")
    for length in frames:
        bits += format(length, "020b") + "0" * length
    bits += format(500, "011b")
    bits += "0" * (-len(bits) % 32)
    return b"".join(struct.pack("<I", int(bits[i:i + 32], 2)) for i in range(0, len(bits), 32))


SV8 = "Musepack SV8"
SV7 = "Musepack SV7"

# name: (bytes, regions as (name, start, end) with None for the end of the file, findings as (code, text))
FIXTURES = {
    "sv8-bad-crc": (sv8_bad_crc, None, [("musepack.header-crc-mismatch", "the header is damaged")]),
    "sv8-rate-reserved": (sv8_rate_reserved, None, [("musepack.sample-rate-reserved", "index is 5")]),
    "sv8-samples-beyond-packets": (sv8_samples_beyond, None, [("musepack.audio-packets-missing", "need 78 frames in 20 audio packets; the stream holds 15")]),
    "sv8-no-end": (sv8_no_end, None, [("musepack.end-missing", "no stream end packet")]),
    "sv8-truncated": (sv8_truncated, None, [("musepack.packet-truncated", "the stream is cut short"), ("musepack.end-missing", "no stream end packet"),
                                            ("musepack.seek-offset-invalid", "no seek table packet starts")]),
    "sv8-unknown-key": (sv8_unknown_key, None, [("musepack.key-unknown", "first XY at")]),
    "sv8-invalid-key": (sv8_invalid_key, None, [("musepack.key-invalid", "first ab at")]),
    "sv8-seek-mismatch": (sv8_seek_mismatch, None, [("musepack.key-unknown", "first XY at"),
                                                    ("musepack.seek-table-mismatch", "14 of the seek table's 15 entries")]),
    "sv8-data-after-end": (sv8_data_after_end, None, [("musepack.data-after-end", "16 bytes after the stream end packet")]),
    "sv8-end-size": (sv8_end_size, None, [("musepack.end-size", "4 bytes long")]),
    "sv8-no-replaygain": (sv8_no_replaygain, None, [("musepack.replaygain-missing", "no ReplayGain packet")]),
    "sv8-tags": (sv8_tags, "tags", []),
    "sv8-tag-inside": (sv8_tag_inside, None, [("tag.near-miss", "Musepack")]),
    "sv7-truncated": (sv7_truncated, None, [("musepack.frame-truncated", "the stream is cut short"), ("musepack.frames-missing", "counts 58 frames")]),
    "sv7-damaged-length": (sv7_damaged_length, None, None),
    "sv7-trailing-data": (sv7_trailing_data, None, [("musepack.trailing-data", "40 bytes after the stream's last frame")]),
    "sv7-header-fields": (sv7_header_fields, None, [("musepack.max-band-invalid", "max band is 0"), ("musepack.intensity-stereo-flag", "flag is set"),
                                                    ("musepack.unused-bits-set", "not 0")]),
    "sv7-header-truncated": (sv7_header_truncated, None, [("musepack.header-truncated", "the data holds 20")]),
    "sv7-tags": (sv7_tags, "tags", []),
    "sv7-tag-inside": (sv7_tag_inside, None, [("tag.near-miss", "Musepack")]),
    "sv5-band-overrun": (sv5_band_overrun, None, [("musepack.band-header-overrun", "1 frame's subband resolutions run past"),
                                                  ("musepack.last-frame-unwritten", "bits past the end of the data")]),
    "sv6": (lambda: old(6), None, [("musepack.frames-not-read", "no SV6 encoder")]),
    "sv6-constant-bitrate": (lambda: old(6, bitrate=128), None, [("musepack.frames-not-read", "no SV6 encoder")]),
}

# The damaged frame length makes the frames after it be read from the wrong bits, which still decode
# as resolutions, until a length runs past the end of the data.
DAMAGED = [("musepack.frame-truncated", "the stream is cut short"), ("musepack.frames-missing", "counts 58 frames")]


def name_of(data):
    if data[:4] == b"MPCK":
        return SV8
    if data[:3] == b"MP+":
        return SV7
    return f"Musepack SV{m.Words(data).read(11, 10)}"


def main():
    for name, (build, layout, findings) in FIXTURES.items():
        data = build()
        if layout == "tags":
            start = len(id3v2_tag())
            end = len(data) - len(trailing_tags())
            ape = len(tags.ape_tag([tags.ape_item("Title", "Musepack fixture")]))
            regions = [{"name": "ID3v2", "offset": 0, "length": start}, {"name": name_of(data[start:]), "offset": start, "length": end - start},
                       {"name": "APE", "offset": end, "length": ape}, {"name": "ID3v1", "offset": end + ape, "length": 128}]
        else:
            start, end = 0, len(data)
            regions = [{"name": name_of(data), "offset": 0, "length": len(data)}]
        expected = DAMAGED if findings is None else findings
        result = m.manifest(data, start, end, {"regions": regions, "findings": [{"code": c, "contains": t} for c, t in expected],
                                               "crosscheck": {"valid": False}})
        (OUT / f"{name}.mpc").write_bytes(data)
        (OUT / f"{name}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
