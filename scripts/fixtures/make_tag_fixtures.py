#!/usr/bin/env python3
"""Writes the byte-exact tag fixtures in tests/fixtures/tags.

Each fixture is a <name>.mp3 file plus a <name>.expected.json manifest that states what the
analyzer must report:

- "regions": the top-level regions in file order, tags and the untagged "Data" between them,
  with offset, length, version, confidence and selected field values. A field path is a
  sequence of node names separated by "/", relative to the tag node; "Name[i]" picks the i-th
  sibling with that name. A list value means "all nodes matching the last segment".
- "findings": the findings the analyzer must produce, as a code plus a string the message has
  to contain. "format.unrecognized" is left out because no format parser exists yet.

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ. The "Data" filler stands in for audio and never contains a tag
signature.
"""

import json
import pathlib
import struct
import sys

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "tags"

SIGNATURES = (b"TAG", b"ID3", b"3DI", b"APETAGEX", b"LYRICS")


# --- Helpers ----------------------------------------------------------------------------------


class Rng:
    """xorshift32, so the bytes do not depend on the Python version."""

    def __init__(self, seed):
        self.state = seed

    def next(self):
        x = self.state
        x ^= (x << 13) & 0xFFFFFFFF
        x ^= x >> 17
        x ^= (x << 5) & 0xFFFFFFFF
        self.state = x
        return x

    def bytes(self, count):
        return bytes(self.next() & 0xFF for _ in range(count))


def filler(length, seed=1):
    data = bytes(((i * 37) + seed * 11 + (i >> 8)) % 251 for i in range(length))
    for sig in SIGNATURES:
        assert sig not in data, sig
    return data


def syncsafe(value, width=4):
    assert 0 <= value < 1 << (7 * width)
    return bytes((value >> (7 * (width - 1 - i))) & 0x7F for i in range(width))


def unsynchronize(data):
    """ID3v2.4 section 6.1: insert $00 after every $FF that precedes $00 or %111xxxxx."""
    out = bytearray()
    for i, b in enumerate(data):
        out.append(b)
        if b == 0xFF and (i + 1 == len(data) or data[i + 1] == 0x00 or data[i + 1] >= 0xE0):
            out.append(0x00)
    return bytes(out)


LATIN1, UTF16, UTF16BE, UTF8 = 0, 1, 2, 3
ENCODING_NAMES = {LATIN1: "ISO-8859-1", UTF16: "UTF-16", UTF16BE: "UTF-16BE", UTF8: "UTF-8"}


def encode(encoding, text, big_endian_bom=False):
    if encoding == LATIN1:
        return text.encode("latin-1")
    if encoding == UTF16:
        if big_endian_bom:
            return b"\xfe\xff" + text.encode("utf-16-be")
        return b"\xff\xfe" + text.encode("utf-16-le")
    if encoding == UTF16BE:
        return text.encode("utf-16-be")
    return text.encode("utf-8")


def terminator(encoding):
    return b"\x00\x00" if encoding in (UTF16, UTF16BE) else b"\x00"


# --- ID3v2 ------------------------------------------------------------------------------------


def text_frame_data(encoding, values, big_endian_bom=False):
    parts = [encode(encoding, v, big_endian_bom) for v in values]
    return bytes([encoding]) + terminator(encoding).join(parts)


def txxx_data(encoding, description, value):
    return bytes([encoding]) + encode(encoding, description) + terminator(encoding) + encode(encoding, value)


def comm_data(encoding, language, description, text):
    return (
        bytes([encoding])
        + language.encode("latin-1")
        + encode(encoding, description)
        + terminator(encoding)
        + encode(encoding, text)
    )


def apic_data(encoding, mime, picture_type, description, picture):
    return (
        bytes([encoding])
        + mime.encode("latin-1")
        + b"\x00"
        + bytes([picture_type])
        + encode(encoding, description)
        + terminator(encoding)
        + picture
    )


def pic_data(encoding, image_format, picture_type, description, picture):
    return (
        bytes([encoding])
        + image_format.encode("latin-1")
        + bytes([picture_type])
        + encode(encoding, description)
        + terminator(encoding)
        + picture
    )


def wxxx_data(encoding, description, url):
    return bytes([encoding]) + encode(encoding, description) + terminator(encoding) + url.encode("latin-1")


def frame22(frame_id, data):
    assert len(frame_id) == 3
    return frame_id.encode("ascii") + len(data).to_bytes(3, "big") + data


def frame23(frame_id, data, flags=0):
    return frame_id.encode("ascii") + struct.pack(">IH", len(data), flags) + data


def frame24(frame_id, data, status=0, unsync=False, data_length_indicator=False, plain_size=False):
    """An ID3v2.4 frame. With unsync the data is unsynchronized and the flag set (section 4.1.2)."""
    fmt = 0
    body = data
    if unsync:
        fmt |= 0x02
        body = unsynchronize(data)
    if data_length_indicator:
        fmt |= 0x01
        body = syncsafe(len(data)) + body
    size = len(body).to_bytes(4, "big") if plain_size else syncsafe(len(body))
    return frame_id.encode("ascii") + size + bytes([status, fmt]) + body


def id3v2(major, frames, flags=0, padding=0, extended_header=b"", footer=False, unsync=False, size_override=None):
    """Header (section 3.1), optional extended header, frames, padding and optional footer.

    For 2.2 and 2.3, unsynchronization applies to everything after the header.
    """
    body = extended_header + b"".join(frames) + b"\x00" * padding
    if extended_header:
        flags |= 0x40
    if unsync:
        flags |= 0x80
        if major in (2, 3):
            body = unsynchronize(body)
    if footer:
        flags |= 0x10
    size = len(body) if size_override is None else size_override
    header = b"ID3" + bytes([major, 0, flags]) + syncsafe(size)
    tag = header + body
    if footer:
        tag += b"3DI" + bytes([major, 0, flags]) + syncsafe(size)
    return tag


def ext_header23(padding_size, crc=None):
    flags = 0x8000 if crc is not None else 0
    size = 10 if crc is not None else 6
    data = struct.pack(">IHI", size, flags, padding_size)
    if crc is not None:
        data += struct.pack(">I", crc)
    return data


def ext_header24(update=False, crc=None, restrictions=None):
    flags = 0
    data = b""
    if update:
        flags |= 0x40
        data += b"\x00"
    if crc is not None:
        flags |= 0x20
        data += b"\x05" + syncsafe(crc, 5)
    if restrictions is not None:
        flags |= 0x10
        data += b"\x01" + bytes([restrictions])
    body = b"\x01" + bytes([flags]) + data
    return syncsafe(4 + len(body)) + body


# --- ID3v1 ------------------------------------------------------------------------------------


def pad(text, width, fill=b"\x00"):
    data = text.encode("latin-1")
    assert len(data) <= width
    return data + fill * (width - len(data))


def id3v1(title="", artist="", album="", year="", comment="", track=None, genre=255):
    if track is None:
        comment_bytes = pad(comment, 30)
    else:
        comment_bytes = pad(comment, 28) + b"\x00" + bytes([track])
    return b"TAG" + pad(title, 30) + pad(artist, 30) + pad(album, 30) + pad(year, 4) + comment_bytes + bytes([genre])


def enhanced_tag(title="", artist="", album="", speed=0, genre="", start="", end=""):
    data = (
        b"TAG+"
        + pad(title, 60)
        + pad(artist, 60)
        + pad(album, 60)
        + bytes([speed])
        + pad(genre, 30)
        + pad(start, 6)
        + pad(end, 6)
    )
    assert len(data) == 227
    return data


# --- APE --------------------------------------------------------------------------------------

APE_TEXT, APE_BINARY, APE_EXTERNAL = 0, 1, 2


def ape_item(key, value, item_type=APE_TEXT, read_only=False):
    if isinstance(value, str):
        value = value.encode("utf-8")
    elif isinstance(value, list):
        value = b"\x00".join(v.encode("utf-8") for v in value)
    flags = (item_type << 1) | (1 if read_only else 0)
    return struct.pack("<II", len(value), flags) + key.encode("ascii") + b"\x00" + value


def ape_tag(items, version=2000, header=True, footer_overrides=None, header_overrides=None):
    """APE tag: optional header, items, footer (APEv2 specification, "APE Tag Header/Footer")."""
    body = b"".join(items)
    size = len(body) + 32
    count = len(items)
    has_header = header and version == 2000
    base_flags = (1 << 31) if has_header else 0

    def block(is_header, overrides):
        fields = {
            "version": version,
            "size": size,
            "count": count,
            "flags": base_flags | ((1 << 29) if is_header else 0),
        }
        fields.update(overrides or {})
        return b"APETAGEX" + struct.pack("<IIII", fields["version"], fields["size"], fields["count"], fields["flags"]) + b"\x00" * 8

    tag = body + block(False, footer_overrides)
    if has_header:
        tag = block(True, header_overrides) + tag
    return tag


# --- Lyrics3 ----------------------------------------------------------------------------------


def lyrics3v1(lyrics):
    return b"LYRICSBEGIN" + lyrics.encode("latin-1") + b"LYRICSEND"


def lyrics3v2(fields, size_override=None):
    body = b"LYRICSBEGIN"
    for field_id, value in fields:
        data = value.encode("latin-1")
        body += field_id.encode("ascii") + b"%05d" % len(data) + data
    size = len(body) if size_override is None else size_override
    return body + b"%06d" % size + b"LYRICS200"


# --- Fixture assembly -------------------------------------------------------------------------


class Fixture:
    def __init__(self, name, description):
        self.name = name
        self.description = description
        self.data = bytearray()
        self.regions = []
        self.findings = []

    def add(self, data, region=None):
        if region is not None:
            region = dict(region)
            region["offset"] = len(self.data)
            region["length"] = len(data)
            self.regions.append(region)
        self.data += data
        return self

    def tag(self, name, version, data, confidence, fields=None):
        region = {"name": name, "version": version, "confidence": confidence}
        if fields:
            region["fields"] = fields
        return self.add(data, region)

    def audio(self, length=512, seed=1):
        return self.add(filler(length, seed), {"name": "Data"})

    def raw(self, data):
        return self.add(data)

    def finding(self, code, contains):
        self.findings.append({"code": code, "contains": contains})
        return self

    @property
    def size(self):
        return len(self.data)

    def write(self):
        (OUT / f"{self.name}.mp3").write_bytes(bytes(self.data))
        manifest = {
            "description": self.description,
            "length": len(self.data),
            "regions": self.regions,
            "findings": self.findings,
        }
        text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
        (OUT / f"{self.name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


def hex_offset(value):
    return f"0x{value:X}"


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


# Common tag contents, so several fixtures can stack the same tags.

def basic_id3v1():
    return id3v1("Song Title", "Artist Name", "Album Name", "2001", "A comment", track=7, genre=17)


BASIC_ID3V1_FIELDS = {
    "Title": "Song Title",
    "Artist": "Artist Name",
    "Album": "Album Name",
    "Year": "2001",
    "Comment": "A comment",
    "Track": "7",
    "Genre": "17 (Rock)",
}


def basic_ape():
    return ape_tag([ape_item("Title", "APE Title"), ape_item("Artist", "APE Artist")])


BASIC_APE_FIELDS = {"Items/Title/Value": "APE Title", "Items/Artist/Value": "APE Artist"}


def basic_lyrics3v2():
    return lyrics3v2([("IND", "10"), ("LYR", "First line\r\nSecond line"), ("ETT", "Extended Title")])


BASIC_LYRICS3V2_FIELDS = {"LYR/Data": "First line\r\nSecond line", "ETT/Data": "Extended Title"}


def basic_id3v23():
    return id3v2(3, [frame23("TIT2", text_frame_data(LATIN1, ["ID3 Title"]))], padding=16)


BASIC_ID3V23_FIELDS = {"TIT2/Text": "ID3 Title"}


# --- ID3v1 fixtures ---------------------------------------------------------------------------


@fixture
def id3v1_basic():
    f = Fixture("id3v1-basic", "ID3v1.0 tag with every field filled, after untagged data.")
    f.audio()
    f.tag(
        "ID3v1",
        "1.0",
        id3v1("Title", "Artist", "Album", "1999", "Thirty character comment field", genre=13),
        "Medium",
        {"Title": "Title", "Comment": "Thirty character comment field", "Genre": "13 (Pop)"},
    )
    return f


@fixture
def id3v11_track():
    f = Fixture("id3v11-track", "ID3v1.1 tag: zero byte at comment offset 28, track number in the last comment byte.")
    f.audio()
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def id3v1_enhanced():
    f = Fixture("id3v1-enhanced", "Enhanced TAG (TAG+, 227 bytes) directly before an ID3v1.1 tag; both form one region.")
    f.audio()
    data = enhanced_tag("Extended title text", "Extended artist", "Extended album", 3, "Progressive Psytrance", "001:05", "004:30")
    data += basic_id3v1()
    fields = dict(BASIC_ID3V1_FIELDS)
    fields.update(
        {
            "Enhanced TAG/Title": "Extended title text",
            "Enhanced TAG/Speed": "3 (fast)",
            "Enhanced TAG/Genre": "Progressive Psytrance",
            "Enhanced TAG/Start Time": "001:05",
            "Enhanced TAG/End Time": "004:30",
        }
    )
    f.tag("ID3v1", "1.1", data, "Medium", fields)
    return f


@fixture
def id3v1_control_bytes():
    f = Fixture("id3v1-control-bytes", "TAG at EOF-128 followed by binary data: still an ID3v1 tag by position, but low confidence.")
    f.audio()
    data = b"TAG" + bytes(range(1, 126))
    f.tag("ID3v1", "1.0", data, "Low")
    f.finding("id3v1.not-text", "Title")
    f.finding("id3v1.year-not-numeric", "[\\]^")
    return f


@fixture
def id3v1_too_short():
    f = Fixture("id3v1-too-short", "A 100-byte file that starts with TAG: too short for ID3v1, nothing is detected.")
    f.add(b"TAG" + filler(97), {"name": "Data"})
    return f


@fixture
def empty_file():
    return Fixture("empty", "Zero-byte file.").finding("file.empty", "empty")


# --- ID3v2 fixtures ---------------------------------------------------------------------------


@fixture
def id3v22_basic():
    f = Fixture("id3v22-basic", "ID3v2.2 tag with three-character frame IDs, UTF-16 text and a PIC frame.")
    frames = [
        frame22("TT2", text_frame_data(LATIN1, ["v2.2 Title"])),
        frame22("TP1", text_frame_data(UTF16, ["Ärtist"])),
        frame22("COM", comm_data(LATIN1, "eng", "", "v2.2 comment")),
        frame22("PIC", pic_data(LATIN1, "PNG", 3, "cover", b"\x89PNG\r\n\x1a\nfake")),
    ]
    f.tag(
        "ID3v2",
        "2.2.0",
        id3v2(2, frames, padding=32),
        "High",
        {
            "TT2/Text": "v2.2 Title",
            "TP1/Encoding": "UTF-16",
            "TP1/Text": "Ärtist",
            "COM/Language": "eng",
            "COM/Text": "v2.2 comment",
            "PIC/Image Format": "PNG",
            "PIC/Picture Type": "3 (Cover (front))",
            "PIC/Picture Data": "12 bytes",
            "Padding": "32 bytes",
        },
    )
    f.audio()
    return f


@fixture
def id3v23_basic():
    f = Fixture("id3v23-basic", "ID3v2.3 tag with common frame types, both UTF-16 byte orders and padding.")
    frames = [
        frame23("TIT2", text_frame_data(LATIN1, ["Latin-1 Title"])),
        frame23("TPE1", text_frame_data(UTF16, ["Künstler"])),
        frame23("TALB", text_frame_data(UTF16, ["Big-endian Album"], big_endian_bom=True)),
        frame23("TRCK", text_frame_data(LATIN1, ["3/12"])),
        frame23("COMM", comm_data(UTF16, "deu", "Beschreibung", "Kommentar")),
        frame23("TXXX", txxx_data(LATIN1, "REPLAYGAIN_TRACK_GAIN", "-6.50 dB")),
        frame23("WXXX", wxxx_data(LATIN1, "Homepage", "https://example.com/")),
        frame23("WOAR", b"https://example.com/artist"),
        frame23("APIC", apic_data(LATIN1, "image/jpeg", 4, "back", b"\xff\xd8\xff\xe0JFIF\x00\xff\xd9")),
        frame23("UFID", b"http://example.com/id\x00ABC-123"),
        frame23("PRIV", b"com.example.private\x00\x01\x02\x03\x04"),
        frame23("POPM", b"rater@example.com\x00\xc4\x00\x00\x01\x00"),
        frame23("PCNT", b"\x00\x00\x00\x2a"),
    ]
    f.tag(
        "ID3v2",
        "2.3.0",
        id3v2(3, frames, padding=64),
        "High",
        {
            "Header/Size": str(sum(len(x) for x in frames) + 64),
            "TIT2/Encoding": "ISO-8859-1",
            "TIT2/Text": "Latin-1 Title",
            "TPE1/Text": "Künstler",
            "TALB/Text": "Big-endian Album",
            "TRCK/Text": "3/12",
            "COMM/Language": "deu",
            "COMM/Description": "Beschreibung",
            "COMM/Text": "Kommentar",
            "TXXX/Description": "REPLAYGAIN_TRACK_GAIN",
            "TXXX/Text": "-6.50 dB",
            "WXXX/Description": "Homepage",
            "WXXX/URL": "https://example.com/",
            "WOAR/URL": "https://example.com/artist",
            "APIC/MIME Type": "image/jpeg",
            "APIC/Picture Type": "4 (Cover (back))",
            "APIC/Description": "back",
            "APIC/Picture Data": "11 bytes",
            "UFID/Owner": "http://example.com/id",
            "UFID/Identifier": "ABC-123",
            "PRIV/Owner": "com.example.private",
            "PRIV/Data": "4 bytes",
            "POPM/Email": "rater@example.com",
            "POPM/Rating": "196",
            "POPM/Counter": "256",
            "PCNT/Counter": "42",
            "Padding": "64 bytes",
        },
    )
    f.audio()
    return f


@fixture
def id3v23_unsync():
    f = Fixture(
        "id3v23-unsync",
        "ID3v2.3 tag with tag-level unsynchronization and an extended header with CRC; the APIC data contains $FF $E0 and $FF $00.",
    )
    picture = b"\xff\xd8\xff\xe0\x00\x10JFIF\xff\x00\xff"
    frames = [
        frame23("TIT2", text_frame_data(LATIN1, ["Unsynchronized"])),
        frame23("APIC", apic_data(LATIN1, "image/jpeg", 3, "", picture)),
        frame23("TPE1", text_frame_data(UTF16, ["ÿÿ Artist"])),
    ]
    data = id3v2(3, frames, padding=8, extended_header=ext_header23(8, crc=0x12345678), unsync=True)
    f.tag(
        "ID3v2",
        "2.3.0",
        data,
        "High",
        {
            "Header/Flags/IsUnsynchronized": "true",
            "Header/Flags/HasExtendedHeader": "true",
            "Extended Header/Padding Size": "8",
            "Extended Header/CRC": "0x12345678",
            "TIT2/Text": "Unsynchronized",
            "APIC/Picture Data": f"{len(picture)} bytes",
            "TPE1/Text": "ÿÿ Artist",
            "Padding": "8 bytes",
        },
    )
    f.audio()
    return f


@fixture
def id3v24_basic():
    f = Fixture(
        "id3v24-basic",
        "ID3v2.4 tag with UTF-8 and UTF-16BE text, multiple values, a data length indicator, per-frame unsynchronization and an extended header.",
    )
    frames = [
        frame24("TIT2", text_frame_data(UTF8, ["Ünïcödé Title"])),
        frame24("TPE1", text_frame_data(UTF8, ["Artist One", "Artist Two"])),
        frame24("TALB", text_frame_data(UTF16BE, ["UTF-16BE Album"])),
        frame24("TDRC", text_frame_data(LATIN1, ["2024-05-01"])),
        frame24("TCON", text_frame_data(UTF16, ["Rock", "Pop"])),
        frame24("TXXX", txxx_data(UTF8, "MusicBrainz Album Id", "0f0e0d0c"), data_length_indicator=True),
        frame24("APIC", apic_data(UTF8, "image/png", 3, "Front", b"\x89PNG\xff\xe2\xff\x00end"), unsync=True, data_length_indicator=True),
        frame24("TCOP", text_frame_data(LATIN1, ["Read-only"]), status=0x10),
    ]
    restrictions = 0b01_1_10_1_01
    ext = ext_header24(update=True, crc=0x0ABCDEF12, restrictions=restrictions)
    f.tag(
        "ID3v2",
        "2.4.0",
        id3v2(4, frames, padding=20, extended_header=ext),
        "High",
        {
            "Extended Header/Flags/IsUpdate": "true",
            "Extended Header/Flags/HasCrc": "true",
            "Extended Header/Flags/HasRestrictions": "true",
            "Extended Header/CRC": "0xABCDEF12",
            "Extended Header/Restrictions/Tag Size": "No more than 64 frames and 128 KB",
            "Extended Header/Restrictions/Text Encoding": "ISO-8859-1 or UTF-8 only",
            "Extended Header/Restrictions/Text Size": "No more than 128 characters",
            "Extended Header/Restrictions/Image Encoding": "PNG or JPEG only",
            "Extended Header/Restrictions/Image Size": "256x256 pixels or less",
            "TIT2/Encoding": "UTF-8",
            "TIT2/Text": "Ünïcödé Title",
            "TPE1/Text": ["Artist One", "Artist Two"],
            "TALB/Encoding": "UTF-16BE",
            "TALB/Text": "UTF-16BE Album",
            "TDRC/Text": "2024-05-01",
            "TCON/Text": ["Rock", "Pop"],
            "TXXX/Header/Flags/HasDataLengthIndicator": "true",
            "TXXX/Header/Data Length": str(len(txxx_data(UTF8, "MusicBrainz Album Id", "0f0e0d0c"))),
            "TXXX/Description": "MusicBrainz Album Id",
            "TXXX/Text": "0f0e0d0c",
            "APIC/Header/Flags/IsUnsynchronized": "true",
            "APIC/Description": "Front",
            "APIC/Picture Data": "11 bytes",
            "TCOP/Header/Flags/IsReadOnly": "true",
            "Padding": "20 bytes",
        },
    )
    f.audio()
    return f


@fixture
def id3v24_footer_appended():
    f = Fixture("id3v24-footer-appended", "ID3v2.4 tag with a footer appended after the audio and before ID3v1, found through its 3DI footer.")
    f.audio()
    tag = id3v2(4, [frame24("TIT2", text_frame_data(UTF8, ["Appended"]))], footer=True)
    f.tag("ID3v2", "2.4.0", tag, "High", {"Header/Flags/HasFooter": "true", "TIT2/Text": "Appended", "Footer/Identifier": "3DI"})
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def id3v24_plain_frame_sizes():
    f = Fixture(
        "id3v24-plain-frame-sizes",
        "ID3v2.4 tag whose frame size is written as a plain 32-bit integer (an old iTunes bug); the byte $80 cannot be syncsafe.",
    )
    long_text = "x" * 200
    frames = [
        frame24("TIT2", text_frame_data(LATIN1, [long_text]), plain_size=True),
        frame24("TPE1", text_frame_data(LATIN1, ["After"])),
    ]
    f.tag("ID3v2", "2.4.0", id3v2(4, frames), "Medium", {"TIT2/Text": long_text, "TPE1/Text": "After"})
    f.finding("id3v2.frame-size-not-syncsafe", "TIT2")
    f.audio()
    return f


@fixture
def id3v2_two_leading():
    f = Fixture("id3v2-two-leading", "Two ID3v2 tags back to back at the start of the file.")
    f.tag("ID3v2", "2.3.0", basic_id3v23(), "High", BASIC_ID3V23_FIELDS)
    f.tag("ID3v2", "2.4.0", id3v2(4, [frame24("TIT2", text_frame_data(UTF8, ["Second"]))]), "High", {"TIT2/Text": "Second"})
    f.audio()
    return f


@fixture
def id3v2_bad_frame():
    f = Fixture(
        "id3v2-bad-frame",
        "ID3v2.3 tag where garbage follows the first frame: the tag keeps its declared size, the rest is reported as unparsed.",
    )
    frames = [frame23("TIT2", text_frame_data(LATIN1, ["Good"])), b"t!t2" + b"\x01\x02\x03\x04\x05\x06\x07\x08"]
    f.tag("ID3v2", "2.3.0", id3v2(3, frames), "Medium", {"TIT2/Text": "Good", "Unparsed": "12 bytes"})
    f.finding("id3v2.frame-id-invalid", "t!t2")
    f.audio()
    return f


@fixture
def id3v2_padding_garbage():
    f = Fixture("id3v2-padding-garbage", "ID3v2.3 tag whose padding contains non-zero bytes.")
    tag = bytearray(id3v2(3, [frame23("TIT2", text_frame_data(LATIN1, ["Padded"]))], padding=40))
    tag[-5] = 0x41
    f.tag("ID3v2", "2.3.0", bytes(tag), "Medium", {"TIT2/Text": "Padded", "Padding": "40 bytes"})
    f.finding("id3v2.padding-not-zero", "Padding")
    f.audio()
    return f


@fixture
def id3v2_truncated():
    f = Fixture("id3v2-truncated", "ID3v2.3 header that declares more bytes than the file has; the tag is clipped at EOF.")
    tag = id3v2(3, [frame23("TIT2", text_frame_data(LATIN1, ["Cut"]))], size_override=5000)
    f.tag("ID3v2", "2.3.0", tag, "Low", {"TIT2/Text": "Cut", "Header/Size": "5000"})
    f.finding("id3v2.truncated", "5000")
    f.finding("layout.tags-only", "")
    return f


@fixture
def id3v2_invalid_header():
    f = Fixture("id3v2-invalid-header", "ID3 at offset 0 with a size byte of $80: not syncsafe, so no tag; the bytes stay data.")
    f.add(b"ID3\x03\x00\x00\x00\x00\x80\x10" + filler(500), {"name": "Data"})
    f.finding("tag.rejected", "size")
    return f


@fixture
def id3v2_unknown_flags():
    f = Fixture("id3v2-unknown-flags", "ID3v2.3 header with an undefined flag bit set: rejected.")
    tag = bytearray(basic_id3v23())
    tag[5] = 0x01
    f.add(bytes(tag) + filler(300), {"name": "Data"})
    f.finding("tag.rejected", "flag")
    return f


# --- APE fixtures -----------------------------------------------------------------------------


@fixture
def ape_v2_full():
    f = Fixture("ape-v2-full", "APEv2 tag with header, text, multi-value, binary, external and read-only items, before ID3v1.")
    items = [
        ape_item("Title", "Ünïcödé APE Title"),
        ape_item("Artist", ["First Artist", "Second Artist"]),
        ape_item("Cover Art (Front)", b"front.jpg\x00\xff\xd8\xff\xe0data", APE_BINARY),
        ape_item("Related", "https://example.com/related", APE_EXTERNAL),
        ape_item("Year", "2010", read_only=True),
    ]
    f.audio()
    f.tag(
        "APE",
        "2.0",
        ape_tag(items),
        "High",
        {
            "Header/Version": "2000",
            "Header/Item Count": "5",
            "Header/Flags/IsHeader": "true",
            "Footer/Flags/HasHeader": "true",
            "Footer/Flags/IsHeader": "false",
            "Items/Title/Value": "Ünïcödé APE Title",
            "Items/Artist/Value": ["First Artist", "Second Artist"],
            "Items/Cover Art (Front)/Flags/Item Type": "Binary",
            "Items/Cover Art (Front)/Value": "18 bytes",
            "Items/Related/Flags/Item Type": "External",
            "Items/Related/Value": "https://example.com/related",
            "Items/Year/Flags/IsReadOnly": "true",
        },
    )
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def ape_v1():
    f = Fixture("ape-v1", "APEv1 tag: footer only, version 1000, at the very end of the file.")
    f.audio()
    tag = ape_tag([ape_item("Title", "Version One"), ape_item("Track", "5")], version=1000)
    f.tag("APE", "1.0", tag, "High", {"Footer/Version": "1000", "Items/Title/Value": "Version One", "Items/Track/Value": "5"})
    return f


@fixture
def ape_131_collision():
    f = Fixture(
        "ape-131-collision",
        "131-byte APEv2 tag (32 header, 67 items, 32 footer) ending the file: TAG inside APETAGEX lands at EOF-128. "
        "Expect the APE tag only, plus a near-miss finding for the ID3v1 signature.",
    )
    f.audio()
    item = ape_item("Title", "x" * 53)
    assert len(item) == 67
    tag = ape_tag([item])
    assert len(tag) == 131
    f.tag("APE", "2.0", tag, "High", {"Items/Title/Value": "x" * 53})
    assert f.data[-128:-125] == b"TAG"
    f.finding("tag.near-miss", hex_offset(f.size - 128))
    return f


@fixture
def ape_then_id3v1():
    f = Fixture("ape-then-id3v1", "APEv2 tag directly followed by a real ID3v1 tag.")
    f.audio()
    f.tag("APE", "2.0", basic_ape(), "High", BASIC_APE_FIELDS)
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def id3v1_then_ape():
    f = Fixture("id3v1-then-ape", "ID3v1 tag followed by an APEv2 tag: both are found, ID3v1 not being last is reported.")
    f.audio()
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    f.tag("APE", "2.0", basic_ape(), "High", BASIC_APE_FIELDS)
    f.finding("layout.id3v1-not-last", "ID3v1")
    return f


@fixture
def ape_header_mismatch():
    f = Fixture("ape-header-mismatch", "APEv2 tag whose header declares a different item count than the footer: rejected.")
    f.add(filler(400) + ape_tag([ape_item("Title", "Mismatch")], header_overrides={"count": 2}), {"name": "Data"})
    f.finding("tag.rejected", "header")
    return f


@fixture
def ape_items_overrun():
    f = Fixture("ape-items-overrun", "APEv2 footer whose item count does not match the items: items do not end at the footer, rejected.")
    f.add(filler(400) + ape_tag([ape_item("Title", "One"), ape_item("Album", "Two")], footer_overrides={"count": 1}, header=False), {"name": "Data"})
    f.finding("tag.rejected", "items")
    return f


@fixture
def ape_size_too_large():
    f = Fixture("ape-size-too-large", "APE footer declaring a tag larger than the file: rejected.")
    f.add(filler(100) + ape_tag([ape_item("Title", "Big")], header=False, footer_overrides={"size": 100000}), {"name": "Data"})
    f.finding("tag.rejected", "100000")
    return f


# --- Lyrics3 fixtures -------------------------------------------------------------------------


@fixture
def lyrics3v1_basic():
    f = Fixture("lyrics3v1-basic", "Lyrics3 v1 block before ID3v1.")
    f.audio()
    f.tag("Lyrics3", "1.00", lyrics3v1("Some lyrics\r\nMore lyrics"), "Medium", {"Lyrics": "Some lyrics\r\nMore lyrics"})
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def lyrics3v2_basic():
    f = Fixture("lyrics3v2-basic", "Lyrics3 v2 tag with every field type before ID3v1.")
    f.audio()
    fields = [
        ("IND", "110"),
        ("LYR", "[00:01]Timed line\r\n[00:05]Another"),
        ("INF", "Information"),
        ("AUT", "Lyrics Author"),
        ("EAL", "Extended Album Name"),
        ("EAR", "Extended Artist Name"),
        ("ETT", "Extended Title Name"),
        ("IMG", "cover.jpg||00:00"),
    ]
    tag = lyrics3v2(fields)
    f.tag(
        "Lyrics3",
        "2.00",
        tag,
        "High",
        {
            "IND/HasLyrics": "true",
            "IND/HasTimestamps": "true",
            "IND/IsRandomSelectionInhibited": "false",
            "LYR/Data": "[00:01]Timed line\r\n[00:05]Another",
            "AUT/Data": "Lyrics Author",
            "EAL/Data": "Extended Album Name",
            "IMG/Data": "cover.jpg||00:00",
            "Size": str(len(tag) - 15),
        },
    )
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def lyrics3v2_bad_size():
    f = Fixture("lyrics3v2-bad-size", "Lyrics3 v2 tag whose size field does not point at LYRICSBEGIN: rejected, ID3v1 still found.")
    f.audio()
    f.add(lyrics3v2([("LYR", "Words")], size_override=7), {"name": "Data"})
    f.regions[-2]["length"] += f.regions[-1]["length"]
    f.regions.pop()
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    f.finding("tag.rejected", "LYRICSBEGIN")
    return f


@fixture
def lyrics3_without_id3v1():
    f = Fixture("lyrics3-without-id3v1", "Lyrics3 v2 tag at the very end without the ID3v1 tag its specification requires.")
    f.audio()
    f.tag("Lyrics3", "2.00", basic_lyrics3v2(), "High", BASIC_LYRICS3V2_FIELDS)
    f.finding("layout.lyrics3-without-id3v1", "Lyrics3")
    return f


# --- Stacking orders --------------------------------------------------------------------------

TRAILING = {
    "APE": lambda: ("APE", "2.0", basic_ape(), "High", BASIC_APE_FIELDS),
    "Lyrics3": lambda: ("Lyrics3", "2.00", basic_lyrics3v2(), "High", BASIC_LYRICS3V2_FIELDS),
    "ID3v1": lambda: ("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS),
}

STACKING_ORDERS = [
    ("APE", "Lyrics3", "ID3v1"),
    ("Lyrics3", "APE", "ID3v1"),
    ("APE", "ID3v1", "Lyrics3"),
    ("Lyrics3", "ID3v1", "APE"),
    ("ID3v1", "APE", "Lyrics3"),
    ("ID3v1", "Lyrics3", "APE"),
]


def make_stack(order):
    def build():
        name = "stack-id3v2-" + "-".join(t.lower() for t in order)
        f = Fixture(name, f"ID3v2.3 at the start, then data, then {', '.join(order)}.")
        f.tag("ID3v2", "2.3.0", basic_id3v23(), "High", BASIC_ID3V23_FIELDS)
        f.audio()
        for tag_name in order:
            f.tag(*TRAILING[tag_name]())
        for i, tag_name in enumerate(order):
            following = order[i + 1] if i + 1 < len(order) else None
            if tag_name == "Lyrics3" and following != "ID3v1":
                f.finding("layout.lyrics3-without-id3v1", "Lyrics3")
            if tag_name == "ID3v1" and following is not None:
                f.finding("layout.id3v1-not-last", "ID3v1")
        return f

    return build


for _order in STACKING_ORDERS:
    FIXTURES.append(make_stack(_order))


# --- Embedded signatures, random data, truncation, concatenation -----------------------------


def cover_art_fixture(name, trailer, trailer_name, near_miss_offset_from_end):
    f = Fixture(
        name,
        f"ID3v2.3 tag that is the whole file; its APIC picture data ends with a valid {trailer_name} tag. "
        "The trailing candidate lies inside the ID3v2 tag and is rejected as a near miss.",
    )
    picture = b"\xff\xd8\xff\xe0JFIF" + filler(200) + trailer
    frames = [frame23("TIT2", text_frame_data(LATIN1, ["Has Cover"])), frame23("APIC", apic_data(LATIN1, "image/jpeg", 3, "", picture))]
    f.tag("ID3v2", "2.3.0", id3v2(3, frames), "High", {"TIT2/Text": "Has Cover", "APIC/Picture Data": f"{len(picture)} bytes"})
    f.finding("tag.near-miss", hex_offset(f.size - near_miss_offset_from_end))
    f.finding("layout.tags-only", "")
    return f


@fixture
def cover_art_embedded_id3v1():
    return cover_art_fixture("cover-art-embedded-id3v1", basic_id3v1(), "ID3v1", 128)


@fixture
def cover_art_embedded_ape():
    return cover_art_fixture("cover-art-embedded-ape", basic_ape(), "APEv2", 32)


@fixture
def random_seeded():
    f = Fixture(
        "random-seeded",
        "64 KiB of pseudo-random data with TAG, ID3, 3DI, APETAGEX and LYRICS signatures planted away from every anchor: nothing is detected.",
    )
    rng = Rng(0x5EED1234)
    data = bytearray(rng.bytes(65536))
    plants = [b"TAG", b"ID3\x03\x00\x00\x00\x00\x01\x00", b"APETAGEX\xd0\x07\x00\x00", b"LYRICSBEGIN", b"LYRICS200", b"LYRICSEND", b"3DI\x04\x00", b"TAG+"]
    for _ in range(64):
        sig = plants[rng.next() % len(plants)]
        offset = 1 + rng.next() % (len(data) - 400)
        data[offset : offset + len(sig)] = sig
    # Keep every anchor free of signatures: offset 0 and the trailing anchor offsets.
    assert data[0:3] != b"ID3"
    assert data[-128:-125] != b"TAG"
    assert data[-32:-24] != b"APETAGEX"
    assert data[-9:-3] != b"LYRICS"
    assert data[-10:-7] != b"3DI"
    f.add(bytes(data), {"name": "Data"})
    return f


@fixture
def truncated_tags():
    f = Fixture(
        "truncated-tags",
        "Data ending in the first 40 bytes of an APEv2 tag and half an ID3v1 tag: neither is detected.",
    )
    f.add(filler(300) + basic_ape()[:40] + basic_id3v1()[:64], {"name": "Data"})
    return f


@fixture
def concatenated_files():
    f = Fixture(
        "concatenated-files",
        "Two tagged files concatenated. Only the first file's ID3v2 and the second file's ID3v1 are at anchors; "
        "the inner ID3v1 and ID3v2 are inside the data and are left to the format parser.",
    )
    f.tag("ID3v2", "2.3.0", basic_id3v23(), "High", BASIC_ID3V23_FIELDS)
    inner = filler(400, 2) + basic_id3v1() + basic_id3v23() + filler(400, 3)
    f.add(inner, {"name": "Data"})
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    return f


@fixture
def tags_only():
    f = Fixture("tags-only", "ID3v2.4 tag immediately followed by an ID3v1 tag, no data between them.")
    f.tag("ID3v2", "2.4.0", id3v2(4, [frame24("TIT2", text_frame_data(UTF8, ["Only Tags"]))]), "High", {"TIT2/Text": "Only Tags"})
    f.tag("ID3v1", "1.1", basic_id3v1(), "Medium", BASIC_ID3V1_FIELDS)
    f.finding("layout.tags-only", "")
    return f


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for path in OUT.glob("*"):
        if not path.name.startswith("external-") and path.suffix in (".mp3", ".json") and not path.name.endswith(".report.json"):
            path.unlink()
    names = set()
    for build in FIXTURES:
        f = build()
        assert f.name not in names, f.name
        names.add(f.name)
        covered = sum(r["length"] for r in f.regions)
        assert covered == f.size, (f.name, covered, f.size)
        f.write()
    print(f"Wrote {len(names)} fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
