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
too short for its subband resolutions. Further ones cover each finding the others do not: SV8
stream headers with 3 channels, 32 bands, a sample count of 0, a CRC of 0 or none at all, audio
before the header, audio packets past the sample count, packets after the audio, a cut or unknown
ReplayGain packet, a packet size that cannot be followed, seek tables with too many or too few
entries, 4,100 unknown packets before the header, with or without two of an invalid key after them; SV7 streams without the gapless frame but with an
APEv2 tag after them, whose last frame's samples differ or are invalid, without frames, of another
version, with an invalid resolution, whose ID3v2 tag names another encoder, broken by a tag a SEEK
frame finds; and an SV5 stream followed by such a tag.

No SV6 encoder was found, so the SV6 fixtures are derived: the SV5 encoder's variable bitrate streams,
with and without mid/side, relabeled as version 6 (derived-*-as-sv6, not an encoder's output). SV5
decoders drop the last counted frame, which the SV5 encoder leaves cut short, while SV6 decoders
(libmpcdec 1.2.6, the Portable Musepack decoder) decode it and read 11 bits after it; so the frame
count is lowered by one, the 11 bits after the new last frame give its samples (0, meaning all 1,152,
and 300, so both of the decoders' ways of ending the output are taken), and the stream ends in the
word that holds them. From them come an SV6 stream whose frame is too short for its subband
resolutions, one followed by a tag a SEEK frame finds and one whose 11 bits give more than a frame;
one more SV6 stream with a constant bitrate is written from the header layout. Each fixture has a
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


def band_overrun(data):
    """The fourth frame's length cut to 100 bits and the fifth's lengthened by as much, so the units
    still end where the encoder ends them, but the fourth frame's subband resolutions run past its
    length."""
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


def sv5_band_overrun():
    return band_overrun(external("mppsv5-vbr"))


def as_sv6(name, final):
    """The SV5 encoder's stream relabeled as version 6: the frame count lowered by one, so the frame
    the SV5 encoder leaves cut short, which SV5 decoders drop, is no longer counted; 11 bits after the
    new last frame that give its samples; the rest of their word zero, and the stream ending there."""
    data = external(name)
    w = m.Words(data)
    h = m.old_header(w)
    assert h["version"] == 5 and h["block"] == 1
    words = list(struct.unpack(f"<{len(data) // 4}I", data))
    write_bits(words, 11, 10, 6)
    write_bits(words, 32, 32, h["frames"] - 1)
    bit = h["data"]
    for _ in range(h["frames"] - 1):
        bit += 20 + w.read(bit, 20)
    end = (bit + 11 + 31) // 32 * 32
    write_bits(words, bit, 11, final)
    write_bits(words, bit + 11, end - bit - 11, 0)
    return struct.pack(f"<{end // 32}I", *words[:end // 32])


def sv6_ms():
    return as_sv6("mppsv5-vbr", 0)


def sv6_band_overrun():
    return band_overrun(sv6_ms())


def sv6_seek_tag():
    """A SEEK frame in a leading ID3v2.4 tag points to an ID3v2.4 tag right after an SV6 stream."""
    data = sv6_ms()
    return seek_tag(len(data), "Leading") + data + inner_tag("After")


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


def set_bits(data, bit, count, value):
    words = list(struct.unpack(f"<{len(data) // 4}I", data))
    write_bits(words, bit, count, value)
    return struct.pack(f"<{len(words)}I", *words)


def counted_end(data):
    """The byte after the word that holds the last of the frames the SV7 header counts."""
    s = m.sv7(data)
    last = s["frames"][s["header"]["frames"] - 1]
    return ((last["bit"] + 20 + last["length"] + 11 + 31) // 32) * 4


def sv7_gapless_frame_missing_ape():
    """The frame mppenc writes after the counted ones cut off and an APEv2 tag with a large binary
    item after them: read as a length, the tag's first word points inside the file."""
    data = external("mppenc-extra-frame")
    cover = tags.ape_item("Cover Art (Front)", b"cover.jpg\x00" + bytes(i % 251 for i in range(45000)), tags.APE_BINARY)
    return data[:counted_end(data)] + tags.ape_tag([cover])


def sv7_last_frame_samples_differ():
    """The header's last frame samples 500; the 11 bits after the last frame still give 927."""
    return set_bits(external("mppenc-extra-frame"), 161, 11, 500)


def sv7_last_frame_samples_invalid():
    return set_bits(external("mppenc-standard"), 161, 11, 1500)


def sv7_no_frames():
    return set_bits(external("mppenc-standard"), 32, 32, 0)


def sv7_version_unknown():
    data = bytearray(external("mppenc-standard"))
    data[3] = (data[3] & 0xF0) | 6
    return bytes(data)


def sv7_resolution_invalid():
    """The first frame's resolutions rewritten: 15 in subband 0, then 2 more in each of the next two,
    up to 19, past libmpcdec's 17."""
    data = external("mppenc-standard")
    bit = 220
    for count, value in ((4, 15), (4, 15), (1, 0), (6, 0b010110), (6, 0b010110), (1, 0), (6, 0b010110), (6, 0b010110)):
        data = set_bits(data, bit, count, value)
        bit += count
    return data


def sv7_encoder_claim():
    tag = tags.id3v2(3, [tags.frame23("TSSE", tags.text_frame_data(0, ["LAME 3.100"]))])
    return tag + external("mppenc-standard")


def seek_tag(target, title):
    """An ID3v2.4 tag whose SEEK frame points target bytes past its end (ID3v2.4.0 frames 4.29)."""
    return tags.id3v2(4, [tags.frame24("TIT2", tags.text_frame_data(3, [title])), tags.frame24("SEEK", struct.pack(">I", target))])


def inner_tag(title):
    return tags.id3v2(4, [tags.frame24("TIT2", tags.text_frame_data(3, [title]))])


def sv7_data_after_tag():
    """A SEEK frame in a leading ID3v2.4 tag points to another ID3v2.4 tag in the middle of the stream."""
    data = external("mppenc-standard")
    middle = len(data) // 2 // 4 * 4
    return seek_tag(middle, "Leading") + data[:middle] + inner_tag("Inside") + data[middle:]


def sv5_seek_tag():
    """A SEEK frame in a leading ID3v2.4 tag points to an ID3v2.4 tag right after an SV5 stream."""
    data = external("mppsv5-vbr")
    return seek_tag(len(data), "Leading") + data + inner_tag("After")


def replace(data, key, new, nth=0):
    offset, length, _, _, _ = find(data, key, nth)
    return data[:offset] + new + data[offset + length:]


def sh_payload(version=8, samples=None, silence=0, rate=None, bands=None, channels=None, ms=None, power=None, base=None, extra=b""):
    """A stream header payload with its CRC, from base's fields where none is given."""
    offset, length, _, _, header = find(base, "SH")
    p = base[offset + header:offset + length]
    base_samples, at = m.size(p, 5)
    _, at = m.size(p, at)
    first, second = p[at], p[at + 1]
    body = bytes([version]) + encode_size(base_samples if samples is None else samples) + encode_size(silence)
    body += bytes([((first >> 5 if rate is None else rate) << 5) | ((first & 0x1F) if bands is None else bands - 1),
                   (((second >> 4) if channels is None else channels - 1) << 4) | ((second & 0x08) if ms is None else ms << 3)
                   | ((second & 0x07) if power is None else power)]) + extra
    return struct.pack(">I", zlib.crc32(body)) + body


def plain():
    return external("mpcenc-no-ei-no-st")


def sv8_header_changed(**fields):
    return lambda: replace(plain(), "SH", packet(b"SH", sh_payload(base=plain(), **fields)))


def forge_crc_zero(body):
    """Four bytes that, appended to body, make its CRC-32 0: CRC-32 is affine in them over GF(2)."""
    base = zlib.crc32(body + bytes(4))
    columns = [zlib.crc32(body + (1 << i).to_bytes(4, "little")) ^ base for i in range(32)]
    rows, pivots = list(columns), []
    solution_basis = [1 << i for i in range(32)]
    target, picked = base, 0
    for bit in range(31, -1, -1):
        index = next((i for i in range(len(rows)) if i not in pivots and rows[i] >> bit & 1), None)
        if index is None:
            continue
        pivots.append(index)
        for j in range(len(rows)):
            if j != index and rows[j] >> bit & 1:
                rows[j] ^= rows[index]
                solution_basis[j] ^= solution_basis[index]
        if target >> bit & 1:
            target ^= rows[index]
            picked ^= solution_basis[index]
    assert target == 0
    extra = picked.to_bytes(4, "little")
    assert zlib.crc32(body + extra) == 0
    return extra


def sv8_header_crc_zero():
    """A stream header whose bytes give a CRC of 0, through four bytes after its fields."""
    body = sh_payload(base=plain())[4:]
    return replace(plain(), "SH", packet(b"SH", bytes(4) + body + forge_crc_zero(body)))


def sv8_header_missing():
    return replace(plain(), "SH", b"")


def first_audio(data):
    return find(data, "AP")[0]


def sv8_audio_before_header():
    data = plain()
    offset, length, _, _, _ = find(data, "AP")
    sh = find(data, "SH")[0]
    return insert(data, sh, data[offset:offset + length])


def sv8_audio_packets_extra():
    data = plain()
    offset, length, _, _, _ = find(data, "AP")
    return before_end(data, data[offset:offset + length])


def sv8_after_audio():
    """A ReplayGain packet and a second stream header after the audio packets."""
    data = plain()
    rg_offset, rg_length, _, _, _ = find(data, "RG")
    sh_offset, sh_length, _, _, _ = find(data, "SH")
    return before_end(data, data[rg_offset:rg_offset + rg_length] + data[sh_offset:sh_offset + sh_length])


def sv8_replaygain_truncated():
    return replace(plain(), "RG", packet(b"RG", b"\x01\x00"))


def sv8_replaygain_version():
    data = plain()
    offset, length, _, _, header = find(data, "RG")
    return data[:offset + header] + b"\x02" + data[offset + header + 1:]


def sv8_packet_size_invalid():
    return before_end(plain(), b"XY\x02")


def sv8_seek_table(change):
    """mpcenc's seek table over 15 audio packets with its entry count changed, keeping the count's width."""
    data = external("mpcenc-packets")
    offset, length, _, _, header = find(data, "ST")
    count, after = m.size(data, offset + header)
    new = encode_size(change(count), after - offset - header)
    assert len(new) == after - offset - header
    return data[:offset + header] + new + data[after:]


def sv8_invalid_key_in_run():
    """4,100 empty unknown packets, then two of a key outside A to Z, which join the run of packets
    the analyzer no longer details; the findings point at the run as it ends (a fuzzer find)."""
    data = plain()
    return data[:4] + b"XX\x03" * 4100 + b"ab\x03" * 2 + data[4:]


def sv8_many_unknown_first():
    """4,100 empty unknown packets before the stream header, past the 4,096 packets the analyzer
    details: the stream header and the packets after it are still read."""
    data = plain()
    return data[:4] + b"XX\x03" * 4100 + data[4:]


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
    "sv7-gapless-frame-missing-ape": (sv7_gapless_frame_missing_ape, lambda d: (0, counted_end(d), [region(SV7, 0, counted_end(d)), region("APE", counted_end(d), len(d))]), [("musepack.gapless-frame-missing", "ends 256 samples early")]),
    "sv7-last-frame-samples-differ": (sv7_last_frame_samples_differ, None, [("musepack.last-frame-samples-differ", "give 927 samples, the header 500")]),
    "sv7-last-frame-samples-invalid": (sv7_last_frame_samples_invalid, None, [("musepack.last-frame-samples-invalid", "1500 samples"), ("musepack.last-frame-samples-differ", "the header 1500")]),
    "sv7-no-frames": (sv7_no_frames, None, [("musepack.no-frames", "counts no frames"), ("musepack.trailing-data", "26212 bytes")]),
    "sv7-version-unknown": (sv7_version_unknown, None, [("musepack.version-unknown", "version is 6")]),
    "sv7-resolution-invalid": (sv7_resolution_invalid, None, [("musepack.resolution-invalid", "1 frame codes")]),
    "sv7-encoder-claim": (sv7_encoder_claim, lambda d: (tag_end(d), len(d), [region("ID3v2", 0, tag_end(d)), region(SV7, tag_end(d), len(d))]), [("musepack.encoder-claim-differs", "LAME 3.100")]),
    "sv7-data-after-tag": (sv7_data_after_tag, lambda d: seek_layout(d, SV7), [("musepack.frames-missing", "holds 38 whole ones"), ("musepack.frame-truncated", "cut short"), ("musepack.data-after-tag", "13120 bytes")]),
    "sv5-seek-tag": (sv5_seek_tag, lambda d: seek_layout(d, "Musepack SV5"), [("musepack.last-frame-unwritten", "29 bits past")]),
    "sv8-channels-unsupported": (sv8_header_changed(channels=3), None, [("musepack.channels-unsupported", "3 channels")]),
    "sv8-max-band-32": (sv8_header_changed(bands=32), None, [("musepack.max-band-invalid", "32 used bands")]),
    "sv8-sample-count-unknown": (sv8_header_changed(samples=0), None, [("musepack.sample-count-unknown", "64 frames")]),
    "sv8-header-crc-zero": (sv8_header_crc_zero, None, [("musepack.header-crc-zero", "CRC is 0"), ("musepack.padding-not-zero", "SH packet")]),
    "sv8-header-missing": (sv8_header_missing, None, [("musepack.header-missing", "no stream header"), ("musepack.audio-before-header", "at 0x10")]),
    "sv8-audio-before-header": (sv8_audio_before_header, None, [("musepack.audio-packets-extra", "holds 2"), ("musepack.audio-before-header", "at 0x4"), ("musepack.packet-after-audio", "RG packet")]),
    "sv8-audio-packets-extra": (sv8_audio_packets_extra, None, [("musepack.audio-packets-extra", "holds 2")]),
    "sv8-after-audio": (sv8_after_audio, None, [("musepack.packet-after-audio", "RG packet"), ("musepack.stream-header-repeated", "2 stream header packets")]),
    "sv8-replaygain-truncated": (sv8_replaygain_truncated, None, [("musepack.packet-fields-truncated", "RG packet")]),
    "sv8-replaygain-version": (sv8_replaygain_version, None, [("musepack.replaygain-version", "version is 2")]),
    "sv8-packet-size-invalid": (sv8_packet_size_invalid, None, [("musepack.end-missing", "no stream end"), ("musepack.packet-size-invalid", "smaller than its key")]),
    "sv8-seek-table-truncated": (lambda: sv8_seek_table(lambda n: n + 1), None, [("musepack.seek-table-truncated", "counts 16 entries")]),
    "sv8-seek-table-short": (lambda: sv8_seek_table(lambda n: n - 1), None, [("musepack.seek-table-short", "holds 14 entries")]),
    "sv8-invalid-key-in-run": (sv8_invalid_key_in_run, None, [("musepack.key-unknown", "4100 packets"), ("musepack.key-invalid", "2 packets' keys")]),
    "sv8-many-unknown-first": (sv8_many_unknown_first, None, [("musepack.key-unknown", "4100 packets")]),
    "sv5-band-overrun": (sv5_band_overrun, None, [("musepack.band-header-overrun", "1 frame's subband resolutions run past"),
                                                  ("musepack.last-frame-unwritten", "bits past the end of the data")]),
    "derived-mppsv5-vbr-as-sv6": (sv6_ms, None, []),
    "derived-mppsv5-left-right-vbr-as-sv6": (lambda: as_sv6("mppsv5-left-right-vbr", 300), None, []),
    "sv6-band-overrun": (sv6_band_overrun, None, [("musepack.band-header-overrun", "1 frame's subband resolutions run past")]),
    "sv6-seek-tag": (sv6_seek_tag, lambda d: seek_layout(d, "Musepack SV6"), []),
    "sv6-last-frame-samples-invalid": (lambda: as_sv6("mppsv5-vbr", 1900), None, [("musepack.last-frame-samples-invalid", "give 1900 samples")]),
    "sv6-constant-bitrate": (lambda: old(6, bitrate=128), None, [("musepack.frames-not-read", "have a constant bitrate (128 kbps)")]),
}

# The SV5 encoder's streams relabeled as version 6, which the cross-check reads as valid streams, with
# the frames mppdec (musepack_tool.py, under Wine) decoded from them when they were first written.
DERIVED = {
    "derived-mppsv5-vbr-as-sv6": ("external-mppsv5-vbr.mpc", 28),
    "derived-mppsv5-left-right-vbr-as-sv6": ("external-mppsv5-left-right-vbr.mpc", 28),
}

# The damaged frame length makes the frames after it be read from the wrong bits, which still decode
# as resolutions, until a length runs past the end of the data.
DAMAGED = [("musepack.frame-truncated", "the stream is cut short"), ("musepack.frames-missing", "counts 58 frames")]


def region(name, start, end):
    return {"name": name, "offset": start, "length": end - start}


def tag_end(data):
    """The end of the ID3v2 tag at the start of data (ID3v2.4.0 structure 3.1)."""
    return 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])


def seek_layout(data, name):
    """A leading ID3v2.4 tag, the stream up to the tag its SEEK frame points to, that tag, and the
    bytes after it, which no format reads."""
    start = tag_end(data)
    inner = data.index(b"ID3\x04", start)
    after = inner + tag_end(data[inner:])
    regions = [region("ID3v2", 0, start), region(name, start, inner), region("ID3v2", inner, after)]
    if after < len(data):
        regions.append(region("Data", after, len(data)))
    return start, inner, regions


def name_of(data):
    if data[:4] == b"MPCK":
        return SV8
    if data[:3] == b"MP+":
        return SV7
    return f"Musepack SV{m.Words(data).read(11, 10)}"


def main():
    for name, (build, layout, findings) in FIXTURES.items():
        data = build()
        if callable(layout):
            start, end, regions = layout(data)
        elif layout == "tags":
            start = len(id3v2_tag())
            end = len(data) - len(trailing_tags())
            ape = len(tags.ape_tag([tags.ape_item("Title", "Musepack fixture")]))
            regions = [{"name": "ID3v2", "offset": 0, "length": start}, {"name": name_of(data[start:]), "offset": start, "length": end - start},
                       {"name": "APE", "offset": end, "length": ape}, {"name": "ID3v1", "offset": end + ape, "length": 128}]
        else:
            start, end = 0, len(data)
            regions = [{"name": name_of(data), "offset": 0, "length": len(data)}]
        expected = DAMAGED if findings is None else findings
        extra = {"regions": regions, "findings": [{"code": c, "contains": t} for c, t in expected], "crosscheck": {"valid": name in DERIVED}}
        if name in DERIVED:
            extra["derived"] = {"from": DERIVED[name][0], "mppdec_frames": DERIVED[name][1], "note": "Not an encoder's output: no SV6 encoder was found. The SV5 encoder's stream relabeled as version 6, "
                                "its frame count lowered by one and 11 bits written after the new last frame."}
        result = m.manifest(data, start, end, extra)
        (OUT / f"{name}.mpc").write_bytes(data)
        (OUT / f"{name}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
