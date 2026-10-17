"""Matroska and WebM files, written and read for the Matroska fixtures.

Writing: E builds one EBML element (RFC 8794) and lays out a tree of them with every element's offset, so
a fixture's expectations are positions the writer knows; the helpers build the EBML Header, Info, Tracks,
Clusters with SimpleBlocks and BlockGroups, every lacing (RFC 9559 section 10.3), Cues, SeekHead, Tags,
CRC-32 and Void elements, and the damage the corrupt fixtures need (sizes that run past the parent,
unknown sizes, invalid headers).

Reading: read() lists a file's elements the way the analyzer's tree has them (an independent reading of
RFC 8794 and RFC 9559, worked out from the bytes alone) with every block's track, timestamp, flags and
frames; manifest() turns that into the expectations the fixtures test and the cross-check compares with
ffprobe, mkvinfo and MediaInfo. The names, types and masters of the elements come from the table in
src/Encspect.Formats.Matroska/Elements.cs, which matroska_tables.py generates from the RFCs.

Standard library only, so the hand-built fixtures regenerate identically in CI.
"""

import math
import pathlib
import re
import struct
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[2]

# --- The schema ---------------------------------------------------------------------------------

_ELEMENT = re.compile(r'new\((0x[0-9A-F]+), "([^"]+)", ElementType\.(\w+), "((?:[^"\\]|\\.)*)".*?(?:defaultValue: "([^"]*)")?\),$', re.M)
MASTER, UNSIGNED, SIGNED, FLOAT, STRING, UTF8, DATE, BINARY = "Master", "Unsigned", "Signed", "Float", "String", "Utf8", "Date", "Binary"


DEFAULTS = {}
RECURSIVE = set()


def _load():
    text = (ROOT / "src" / "Encspect.Formats.Matroska" / "Elements.cs").read_text(encoding="utf-8")
    table = {}
    for m in _ELEMENT.finditer(text):
        path = m.group(4).replace("\\\\", "\\").replace("+", "")
        table[int(m.group(1), 16)] = (m.group(2), m.group(3), path)
        if m.group(5) is not None:
            DEFAULTS[int(m.group(1), 16)] = m.group(5)
        if m.group(4).endswith("\\\\+" + m.group(2)):
            RECURSIVE.add(int(m.group(1), 16))
    return table


SCHEMA = _load()
NAMES = {v[0]: k for k, v in SCHEMA.items()}

EBML, SEGMENT, SEEKHEAD, INFO, CLUSTER, TRACKS, CUES, ATTACHMENTS, CHAPTERS, TAGS = (
    0x1A45DFA3, 0x18538067, 0x114D9B74, 0x1549A966, 0x1F43B675, 0x1654AE6B, 0x1C53BB6B, 0x1941A469, 0x1043A770, 0x1254C367)
CRC32, VOID = 0xBF, 0xEC
SIMPLEBLOCK, BLOCKGROUP, BLOCK, TIMESTAMP = 0xA3, 0xA0, 0xA1, 0xE7


def name_of(ident):
    return SCHEMA[ident][0] if ident in SCHEMA else "Unknown Element"


def type_of(ident):
    return SCHEMA[ident][1] if ident in SCHEMA else None


def path_of(ident):
    return SCHEMA[ident][2] if ident in SCHEMA else None


def is_top_level(ident):
    """A direct child of the Segment."""
    path = path_of(ident)
    return path is not None and path.startswith("\\Segment\\") and path.count("\\") == 2


def is_placed(ident, parent):
    """Whether the schema places the element in its parent (None: the root of the document): the CRC-32 and Void elements fit any master, a
    recursive element fits in itself, any other by its path (RFC 8794 section 11.1.6.2)."""
    if ident in (CRC32, VOID):
        return True
    path = path_of(ident)
    if path is None:
        return False
    if parent is None:
        return path.count("\\") == 1
    return path.rsplit("\\", 1)[0] == path_of(parent) or (parent == ident and ident in RECURSIVE)


def is_deprecated(ident):
    return False


# --- Variable-size integers (RFC 8794 section 4) ---------------------------------------------------


def width_of(first):
    """The octets of a variable-size integer from its first octet, 0 for a first octet of zero bits."""
    return 0 if first == 0 else 8 - first.bit_length() + 1


def id_bytes(ident):
    return ident.to_bytes((ident.bit_length() + 7) // 8, "big")


def unknown_size(width=1):
    """The reserved Element Data Size that says the size is unknown: all the VINT_DATA bits set (section 6.2)."""
    return bytes([(0x80 >> (width - 1)) | (0xFF >> width)]) + b"\xff" * (width - 1)


def size_bytes(size, width=None):
    """An Element Data Size; width forces the number of octets. None is the unknown size."""
    if size is None:
        return unknown_size(width or 1)
    if width is None:
        width = 1
        while size >= (1 << (7 * width)) - 1:
            width += 1
    if size >= (1 << (7 * width)) - 1:
        raise ValueError("size does not fit")
    return ((1 << (7 * width)) | size).to_bytes(width, "big")


def uint_bytes(value, width=None):
    if width is None:
        width = max(1, (value.bit_length() + 7) // 8)
    return value.to_bytes(width, "big")


# --- Writing ---------------------------------------------------------------------------------------


class E:
    """One element: its ID and either data or children (a Master Element). `size` overrides the size the header states: a number, or
    "unknown"; `size_width` forces the octets of the size; `id_raw` the ID's bytes. render() lays the tree out and sets every
    element's offset, header_length and data_length (what the file holds, whatever size the header states)."""

    def __init__(self, ident, data=b"", children=None, size=None, size_width=None, id_raw=None):
        self.id = ident
        self.data = bytes(data)
        self.children = children
        self.size = size
        self.size_width = size_width
        self.id_raw = id_raw
        self.offset = self.header_length = self.data_length = None
        self._bytes = None

    def build(self):
        if self.children is not None:
            parts = []
            for index, child in enumerate(self.children):
                parts.append(None if isinstance(child, Crc) else child.build())
            for index, child in enumerate(self.children):
                if isinstance(child, Crc):
                    rest = b"".join(p for p in parts[index + 1:] if p is not None)
                    parts[index] = child.build_over(rest)
            body = b"".join(parts)
        else:
            body = self.data
        ident = self.id_raw if self.id_raw is not None else id_bytes(self.id)
        if self.size == "unknown":
            field = unknown_size(self.size_width or 1)
        elif self.size is not None:
            field = size_bytes(self.size, self.size_width)
        else:
            field = size_bytes(len(body), self.size_width)
        self.header_length = len(ident) + len(field)
        self.data_length = len(body)
        self._bytes = ident + field + body
        return self._bytes

    def place(self, offset):
        self.offset = offset
        if self.children is not None:
            position = offset + self.header_length
            for child in self.children:
                child.place(position)
                position += child.header_length + child.data_length

    def render(self, offset=0):
        data = self.build()
        self.place(offset)
        return data

    @property
    def length(self):
        return self.header_length + self.data_length

    @property
    def end(self):
        return self.offset + self.length


class Crc(E):
    """A CRC-32 element over the siblings after it. `value` replaces the computed CRC; `size` the data length."""

    def __init__(self, value=None, size=4):
        super().__init__(CRC32, bytes(size))
        self.value = value
        self.crc_size = size

    def build_over(self, rest):
        computed = zlib.crc32(rest) & 0xFFFFFFFF
        value = computed if self.value is None else self.value
        self.data = struct.pack("<I", value) + bytes(max(0, self.crc_size - 4)) if self.crc_size >= 4 else struct.pack("<I", value)[: self.crc_size]
        return self.build()


def uint(ident, value, width=None):
    return E(ident, uint_bytes(value, width))


def sint(ident, value, width=None):
    if width is None:
        width = 1
        while not -(1 << (8 * width - 1)) <= value < (1 << (8 * width - 1)):
            width += 1
    return E(ident, value.to_bytes(width, "big", signed=True))


def flt(ident, value, width=8):
    return E(ident, struct.pack(">d" if width == 8 else ">f", value))


def text(ident, value):
    return E(ident, value.encode("utf-8"))


def raw(ident, data):
    return E(ident, data)


def master(ident, *children, **kw):
    return E(ident, children=list(children), **kw)


def void(length):
    """A Void element that takes `length` bytes in all."""
    if length < 2:
        raise ValueError("a Void element takes at least two bytes")
    return E(VOID, bytes(length - 2)) if length - 2 < 127 else E(VOID, bytes(length - 3), size_width=2)



# --- Elements the fixtures use ---------------------------------------------------------------------

ID = {
    "EBMLVersion": 0x4286, "EBMLReadVersion": 0x42F7, "EBMLMaxIDLength": 0x42F2, "EBMLMaxSizeLength": 0x42F3, "DocType": 0x4282,
    "DocTypeVersion": 0x4287, "DocTypeReadVersion": 0x4285, "Seek": 0x4DBB, "SeekID": 0x53AB, "SeekPosition": 0x53AC,
    "TimestampScale": 0x2AD7B1, "Duration": 0x4489, "DateUTC": 0x4461, "Title": 0x7BA9, "MuxingApp": 0x4D80, "WritingApp": 0x5741,
    "SegmentUUID": 0x73A4, "TrackEntry": 0xAE, "TrackNumber": 0xD7, "TrackUID": 0x73C5, "TrackType": 0x83, "FlagEnabled": 0xB9,
    "FlagDefault": 0x88, "FlagLacing": 0x9C, "CodecID": 0x86, "CodecPrivate": 0x63A2, "CodecDelay": 0x56AA, "SeekPreRoll": 0x56BB,
    "DefaultDuration": 0x23E383, "Name": 0x536E, "Language": 0x22B59C, "Audio": 0xE1, "SamplingFrequency": 0xB5,
    "OutputSamplingFrequency": 0x78B5, "Channels": 0x9F, "BitDepth": 0x6264, "Video": 0xE0, "PixelWidth": 0xB0, "PixelHeight": 0xBA,
    "ContentEncodings": 0x6D80, "ContentEncoding": 0x6240, "ContentEncodingOrder": 0x5031, "ContentEncodingScope": 0x5032,
    "ContentEncodingType": 0x5033, "ContentCompression": 0x5034, "ContentCompAlgo": 0x4254, "ContentCompSettings": 0x4255,
    "ContentEncryption": 0x5035, "ContentEncAlgo": 0x47E1, "ContentEncKeyID": 0x47E2, "ContentEncAESSettings": 0x47E7,
    "AESSettingsCipherMode": 0x47E8, "Timestamp": 0xE7, "Position": 0xA7, "PrevSize": 0xAB, "SimpleBlock": 0xA3, "BlockGroup": 0xA0,
    "Block": 0xA1, "BlockDuration": 0x9B, "ReferenceBlock": 0xFB, "ReferencePriority": 0xFA, "DiscardPadding": 0x75A2,
    "BlockAdditions": 0x75A1, "BlockMore": 0xA6, "BlockAddID": 0xEE, "BlockAdditional": 0xA5, "CodecState": 0xA4,
    "CuePoint": 0xBB, "CueTime": 0xB3, "CueTrackPositions": 0xB7, "CueTrack": 0xF7, "CueClusterPosition": 0xF1, "CueRelativePosition": 0xF0,
    "Tag": 0x7373, "Targets": 0x63C0, "TargetTypeValue": 0x68CA, "SimpleTag": 0x67C8, "TagName": 0x45A3, "TagString": 0x4487,
    "EditionEntry": 0x45B9, "ChapterAtom": 0xB6, "ChapterUID": 0x73C4, "ChapterTimeStart": 0x91, "ChapterDisplay": 0x80, "ChapString": 0x85,
    "AttachedFile": 0x61A7, "FileName": 0x466E, "FileMediaType": 0x4660, "FileData": 0x465C, "FileUID": 0x46AE,
    "SilentTracks": 0x5854, "SilentTrackNumber": 0x58D7,
}

for _name, _ident in NAMES.items():
    ID.setdefault(_name, _ident)


def ebml_header(doctype="matroska", doctype_version=4, read_version=2, max_id=4, max_size=8, extra=()):
    return master(EBML, uint(ID["EBMLVersion"], 1), uint(ID["EBMLReadVersion"], 1), uint(ID["EBMLMaxIDLength"], max_id),
                  uint(ID["EBMLMaxSizeLength"], max_size), text(ID["DocType"], doctype), uint(ID["DocTypeVersion"], doctype_version),
                  uint(ID["DocTypeReadVersion"], read_version), *extra)


def info(scale=1_000_000, duration=None, muxing="encspect fixtures", writing="encspect fixtures", title=None, uuid=None, extra=()):
    children = []
    if uuid is not None:
        children.append(raw(ID["SegmentUUID"], uuid))
    children.append(uint(ID["TimestampScale"], scale))
    if duration is not None:
        children.append(flt(ID["Duration"], duration))
    if title is not None:
        children.append(text(ID["Title"], title))
    if muxing is not None:
        children.append(text(ID["MuxingApp"], muxing))
    if writing is not None:
        children.append(text(ID["WritingApp"], writing))
    return master(INFO, *children, *extra)


def audio_track(number=1, codec="A_PCM/INT/LIT", channels=2, rate=48000.0, bits=None, private=None, uid=None, lacing=True, delay=None, preroll=None,
                default_duration=None, name=None, language=None, encodings=None, extra=(), audio_extra=(), enabled=True):
    children = [uint(ID["TrackNumber"], number), uint(ID["TrackUID"], uid if uid is not None else 0x1000 + number), uint(ID["TrackType"], 2)]
    if not enabled:
        children.append(uint(ID["FlagEnabled"], 0))
    if not lacing:
        children.append(uint(ID["FlagLacing"], 0))
    if default_duration is not None:
        children.append(uint(ID["DefaultDuration"], default_duration))
    if language is not None:
        children.append(text(ID["Language"], language))
    if name is not None:
        children.append(text(ID["Name"], name))
    if codec is not None:
        children.append(text(ID["CodecID"], codec))
    if private is not None:
        children.append(raw(ID["CodecPrivate"], private))
    if delay is not None:
        children.append(uint(ID["CodecDelay"], delay))
    if preroll is not None:
        children.append(uint(ID["SeekPreRoll"], preroll))
    if encodings is not None:
        children.append(encodings)
    audio = [flt(ID["SamplingFrequency"], rate)]
    if channels is not None:
        audio.append(uint(ID["Channels"], channels))
    if bits is not None:
        audio.append(uint(ID["BitDepth"], bits))
    children.append(master(ID["Audio"], *audio, *audio_extra))
    return master(ID["TrackEntry"], *children, *extra)


def video_track(number=1, codec="V_VP8", width=64, height=48, uid=None, private=None):
    children = [uint(ID["TrackNumber"], number), uint(ID["TrackUID"], uid if uid is not None else 0x2000 + number), uint(ID["TrackType"], 1),
                text(ID["CodecID"], codec)]
    if private is not None:
        children.append(raw(ID["CodecPrivate"], private))
    children.append(master(ID["Video"], uint(ID["PixelWidth"], width), uint(ID["PixelHeight"], height)))
    return master(ID["TrackEntry"], *children)


def content_encoding(order=0, scope=1, compression=None, settings=None, encryption=None, cipher=None, key_id=None, stray_encryption=None, missing_encryption=False,
                     no_algo=False, kind=None, omit=()):
    """A ContentEncoding: compression=0 zlib, 1 bzlib, 2 lzo1x, 3 header stripping; or encryption=the ContentEncAlgo (cipher: the AES cipher
    mode, which RFC 9559 allows for AES only). stray_encryption adds a ContentEncryption with that ContentEncAlgo to a compression, where a
    reader ignores it; missing_encryption makes an encryption (type 1) without the ContentEncryption element; no_algo makes one whose ContentEncryption
    has no ContentEncAlgo, which RFC 9559 gives the default 0 (not encrypted). kind writes another ContentEncodingType than the body's, and omit
    names the elements left out ("order", "scope", "type"), which have their defaults."""
    encrypted_type = 1
    if missing_encryption:
        extra = []
    elif no_algo:
        extra = [master(ID["ContentEncryption"], *([raw(ID["ContentEncKeyID"], key_id)] if key_id is not None else []))]
    elif encryption is not None:
        enc = [uint(ID["ContentEncAlgo"], encryption)]
        if key_id is not None:
            enc.append(raw(ID["ContentEncKeyID"], key_id))
        if cipher is not None:
            enc.append(master(ID["ContentEncAESSettings"], uint(ID["AESSettingsCipherMode"], cipher)))
        extra = [master(ID["ContentEncryption"], *enc)]
    else:
        encrypted_type = 0
        comp = [uint(ID["ContentCompAlgo"], compression)]
        if settings is not None:
            comp.append(raw(ID["ContentCompSettings"], settings))
        extra = [master(ID["ContentCompression"], *comp)]
        if stray_encryption is not None:
            extra.append(master(ID["ContentEncryption"], uint(ID["ContentEncAlgo"], stray_encryption)))
    body = [uint(ID["ContentEncodingOrder"], order), uint(ID["ContentEncodingScope"], scope), uint(ID["ContentEncodingType"], encrypted_type if kind is None else kind)]
    left_out = {"order": 0, "scope": 1, "type": 2}
    body = [e for i, e in enumerate(body) if not any(left_out[o] == i for o in omit)]
    return master(ID["ContentEncoding"], *body, *extra)


def content_encodings(*encodings):
    return master(ID["ContentEncodings"], *encodings)


def tracks(*entries, crc=False):
    return master(TRACKS, *([Crc()] if crc else []), *entries)


def track_vint(number, width=None):
    width = width or max(1, (number.bit_length() + 6) // 7)
    return ((1 << (7 * width)) | number).to_bytes(width, "big")


def xiph_sizes(sizes):
    out = bytearray()
    for size in sizes:
        out += bytes([255] * (size // 255)) + bytes([size % 255])
    return bytes(out)


def ebml_sizes(sizes):
    """The sizes of the frames except the last as EBML lacing codes them (RFC 9559 section 10.3.3)."""
    out = bytearray()
    out += size_bytes(sizes[0])
    for previous, size in zip(sizes, sizes[1:]):
        diff = size - previous
        width = 1
        while not -((1 << (7 * width - 1)) - 1) <= diff <= (1 << (7 * width - 1)):
            width += 1
        out += ((1 << (7 * width)) | (diff + (1 << (7 * width - 1)) - 1)).to_bytes(width, "big")
    return bytes(out)


def block_payload(track, timestamp, frames, lacing="none", flags=0, track_width=None, key=False, invisible=False, discardable=False, is_block=False,
                  lace_count=None, lace_head=None):
    """The data of a SimpleBlock or Block: the track, the timestamp, the flags and the frames, laced as `lacing` says. lace_head
    replaces the lacing header (the count and sizes) for the damaged fixtures."""
    mode = {"none": 0, "xiph": 1, "fixed": 2, "ebml": 3}[lacing]
    flag = flags | (mode << 1) | (0x08 if invisible else 0)
    if not is_block:
        flag |= (0x80 if key else 0) | (0x01 if discardable else 0)
    out = bytearray(track_vint(track, track_width)) + struct.pack(">h", timestamp) + bytes([flag])
    if lacing == "none":
        out += frames[0]
        return bytes(out)
    sizes = [len(f) for f in frames]
    if lace_head is not None:
        out += lace_head
    else:
        out += bytes([(lace_count if lace_count is not None else len(frames)) - 1])
        if lacing == "xiph":
            out += xiph_sizes(sizes[:-1])
        elif lacing == "ebml":
            out += ebml_sizes(sizes[:-1]) if len(frames) > 1 else b""
    for f in frames:
        out += f
    return bytes(out)


def simple_block(track, timestamp, frames, **kw):
    frames = [frames] if isinstance(frames, (bytes, bytearray)) else frames
    return E(SIMPLEBLOCK, block_payload(track, timestamp, frames, **kw))


def block_group(track, timestamp, frames, duration=None, references=(), discard_padding=None, additions=None, extra=(), lacing="none", **kw):
    frames = [frames] if isinstance(frames, (bytes, bytearray)) else frames
    children = [E(BLOCK, block_payload(track, timestamp, frames, lacing=lacing, is_block=True, **kw))]
    if additions is not None:
        children.append(master(ID["BlockAdditions"], *[master(ID["BlockMore"], uint(ID["BlockAddID"], i), raw(ID["BlockAdditional"], data))
                                                      for i, data in additions]))
    if duration is not None:
        children.append(uint(ID["BlockDuration"], duration))
    for ref in references:
        children.append(sint(ID["ReferenceBlock"], ref))
    if discard_padding is not None:
        children.append(sint(ID["DiscardPadding"], discard_padding))
    return master(BLOCKGROUP, *children, *extra)


def cluster(timestamp, *items, crc=False, unknown=False, extra_first=(), size=None):
    first = ([Crc()] if crc else []) + [uint(TIMESTAMP, timestamp)] + list(extra_first)
    return master(CLUSTER, *first, *items, size="unknown" if unknown else size)


def segment(*children, unknown=False, size=None):
    return master(SEGMENT, *children, size="unknown" if unknown else size)


def seek_head(*entries, crc=False):
    """entries: (element id, position) pairs; positions are from the start of the Segment's data."""
    return master(SEEKHEAD, *([Crc()] if crc else []),
                  *[master(ID["Seek"], raw(ID["SeekID"], id_bytes(i)), uint(ID["SeekPosition"], p, 4)) for i, p in entries])


def cues(*points, crc=False):
    """points: (time, track, cluster position) triples."""
    return master(CUES, *([Crc()] if crc else []),
                  *[master(ID["CuePoint"], uint(ID["CueTime"], t), master(ID["CueTrackPositions"], uint(ID["CueTrack"], tr), uint(ID["CueClusterPosition"], p)))
                    for t, tr, p in points])


def tags(*simple, crc=False):
    return master(TAGS, *([Crc()] if crc else []), master(ID["Tag"], master(ID["Targets"], uint(ID["TargetTypeValue"], 50)),
                                                         *[master(ID["SimpleTag"], text(ID["TagName"], n), text(ID["TagString"], v)) for n, v in simple]))


def file_of(*elements):
    """Renders top-level elements one after another; the elements' offsets are set."""
    out = bytearray()
    for element in elements:
        out += element.render(len(out))
    return bytes(out)


# --- Reading ---------------------------------------------------------------------------------------

MAX_DEPTH = 40
LACINGS = ["none", "xiph", "fixed", "ebml"]


class Problem(Exception):
    """An element header that cannot be read: truncated, not-integer or invalid-id."""


def header(data, position, limit, max_id=4, max_size=8):
    """(id, id_length, size_length, size or None for unknown, is_long_size) at position, or raises Problem."""
    available = min(limit, len(data)) - position
    if available < 2 or position < 0:
        raise Problem("truncated")
    id_length = width_of(data[position])
    if id_length == 0:
        raise Problem("not-integer")
    if id_length > max_id or id_length > 8:
        raise Problem("invalid-id")
    if available < id_length + 1:
        raise Problem("truncated")
    ident = int.from_bytes(data[position:position + id_length], "big")
    mask = (1 << (7 * id_length)) - 1
    if ident & mask == mask:
        raise Problem("invalid-id")
    size_length = width_of(data[position + id_length])
    if size_length == 0:
        raise Problem("not-integer")
    if available < id_length + size_length:
        raise Problem("truncated")
    value = int.from_bytes(data[position + id_length:position + id_length + size_length], "big") & ((1 << (7 * size_length)) - 1)
    unknown = value == (1 << (7 * size_length)) - 1
    return ident, id_length, size_length, None if unknown else value, size_length > max_size


class Element:
    """An element as read: name, offset and length (the header included, clamped to its parent), children for a Master."""

    def __init__(self, name, offset, length, children=None, ident=None, value=None):
        self.name, self.offset, self.length, self.children, self.id, self.value = name, offset, length, children, ident, value

    def list(self):
        entry = [self.name, self.offset, self.length]
        if self.children is not None and self.children:
            entry.append([c.list() for c in self.children])
        return entry


class Block:
    def __init__(self, name, offset, length, in_group):
        self.name, self.offset, self.length, self.in_group = name, offset, length, in_group
        self.track = self.timestamp = self.flags = None
        self.lacing = "none"
        self.frames = []
        self.problem = None
        self.cluster_timestamp = 0

    def dict(self):
        out = {"name": self.name, "offset": self.offset, "length": self.length, "track": self.track, "timestamp": self.timestamp,
               "cluster_timestamp": self.cluster_timestamp, "flags": self.flags, "lacing": self.lacing, "frames": [list(f) for f in self.frames]}
        if self.in_group:
            out["in_group"] = True
        if self.problem:
            out["problem"] = self.problem
        return out


MAX_BLOCK = 1 << 26


class Reader:
    def __init__(self, data):
        self.data = data
        self.max_id, self.max_size = 4, 8
        self.facts = {"ebml": {}, "segments": []}
        self.blocks = []
        self.tracks = []
        self.track = None
        self.encoding = None
        self.top = []
        # How many elements that the schema does not place where they stand enclose the one being read: their values are shown, never used.
        self.displaced = 0

    # -- blocks

    def read_block(self, h_off, h_len, end, name, in_group, cluster_ts):
        block = Block(name, h_off, end - h_off, in_group)
        block.cluster_timestamp = cluster_ts
        data = self.data
        start = h_off + h_len
        length = end - start
        self.blocks.append(block)
        if length > MAX_BLOCK:
            block.problem = "too-large"
            return block
        if length == 0 or width_of(data[start]) == 0 or length < width_of(data[start]) + 3:
            block.problem = "short"
            width = width_of(data[start]) if length else 0
            block.track = int.from_bytes(bytes([data[start] & (0xFF >> width)]) + data[start + 1:start + width], "big") if width and length >= width else 0
            return block
        width = width_of(data[start])
        block.track = int.from_bytes(bytes([data[start] & (0xFF >> width)]) + data[start + 1:start + width], "big")
        block.timestamp = struct.unpack(">h", data[start + width:start + width + 2])[0]
        block.flags = data[start + width + 2]
        position = start + width + 3
        remaining = end - position
        mode = (block.flags >> 1) & 3
        block.lacing = LACINGS[mode]
        if mode == 0:
            block.frames = [(position, remaining)]
            return block
        sizes, lace_bytes, why = self.lace(mode, position, remaining)
        if sizes is None:
            block.problem = "lace-invalid"
            return block
        if len(sizes) == 1:
            block.problem = "lace-single"
        frames_at = position + lace_bytes
        for size in sizes:
            block.frames.append((frames_at, size))
            frames_at += size
        return block

    def lace(self, mode, position, remaining):
        data = self.data
        if remaining < 1:
            return None, 0, "short"
        count = data[position] + 1
        lace_bytes = 1
        total = 0
        sizes = []
        if mode == 2:
            if (remaining - 1) % count:
                return None, 0, "fixed"
            return [(remaining - 1) // count] * count, 1, None
        if mode == 1:
            for _ in range(count - 1):
                size = 0
                while True:
                    if lace_bytes >= remaining:
                        return None, 0, "ends"
                    value = data[position + lace_bytes]
                    lace_bytes += 1
                    size += value
                    if value != 255:
                        break
                total += size
                if total > remaining - lace_bytes:
                    return None, 0, "large"
                sizes.append(size)
        else:
            previous = 0
            for index in range(count - 1):
                if lace_bytes >= remaining:
                    return None, 0, "ends"
                first = data[position + lace_bytes]
                width = width_of(first)
                if width == 0 or lace_bytes + width > remaining:
                    return None, 0, "vint"
                raw = int.from_bytes(bytes([first & (0xFF >> width)]) + data[position + lace_bytes + 1:position + lace_bytes + width], "big")
                lace_bytes += width
                size = raw if index == 0 else previous + (raw - ((1 << (7 * width - 1)) - 1))
                if size < 0 or size > remaining:
                    return None, 0, "negative"
                total += size
                if total > remaining - lace_bytes:
                    return None, 0, "large"
                sizes.append(size)
                previous = size
        last = remaining - lace_bytes - total
        if last < 0:
            return None, 0, "large"
        sizes.append(last)
        return sizes, lace_bytes, None

    # -- elements

    def element(self, h, h_off, parent_def_id, parent_end, depth, segment=None):
        """Reads the element at h_off whose header is h; returns (Element, next position, is_complete)."""
        ident, id_length, size_length, size, _ = h
        data_off = h_off + id_length + size_length
        declared = parent_end if size is None else data_off + size
        end = min(declared, parent_end)
        complete = declared <= parent_end
        name = name_of(ident)
        node = Element(name, h_off, end - h_off, ident=ident)
        kind = type_of(ident)
        placed = is_placed(ident, parent_def_id) and self.displaced == 0
        if kind == MASTER:
            node.children = []
            if placed:
                self.begin(ident, node)
            else:
                self.displaced += 1
            if depth < MAX_DEPTH:
                self.children(node, data_off, end, depth + 1, complete)
            if placed:
                self.finish(ident, node)
            else:
                self.displaced -= 1
        elif kind is not None:
            node.value = self.leaf(ident, kind, data_off, end, placed)
        return node, end, complete

    def children(self, node, start, end, depth, complete):
        position = start
        while position < end:
            try:
                h = header(self.data, position, end, self.max_id, self.max_size)
            except Problem:
                node.children.append(Element("Unparsed Data", position, end - position))
                return
            child, next_position, _ = self.element(h, position, node.id, end, depth)
            node.children.append(child)
            if h[0] == CRC32 and position == start and complete and h[3] == 4 and position + h[1] + h[2] + 4 <= end:
                child.children = [Element("Computed", position + h[1] + h[2], 4)]
            position = next_position

    def leaf(self, ident, kind, start, end, placed=True):
        data = self.data[start:end]
        value = None
        if not data and ident in DEFAULTS:
            # An empty element has its default value (RFC 8794, section 6.1).
            default = DEFAULTS[ident]
            value = int(default) if kind in (UNSIGNED, SIGNED) else float(default) if kind == FLOAT else default if kind in (STRING, UTF8) else None
        elif kind == UNSIGNED and len(data) <= 8:
            value = int.from_bytes(data, "big")
        elif kind == SIGNED and len(data) <= 8:
            value = int.from_bytes(data, "big", signed=True) if data else 0
        elif kind == FLOAT and len(data) in (0, 4, 8):
            value = 0.0 if not data else struct.unpack(">f" if len(data) == 4 else ">d", data)[0]
        elif kind in (STRING, UTF8):
            # The value ends at the first null octet (RFC 8794 section 13).
            value = data.split(b"\0", 1)[0].decode("utf-8", "replace")
        if placed:
            self.collect(ident, value, start, end - start)
        return value

    # -- what the analysis keeps

    def begin(self, ident, node):
        if ident == 0xAE:
            self.track = {"number": None, "uid": None, "type": None, "codec": None, "private": None, "channels": None, "rate": None, "bits": None,
                          "delay": 0, "encodings": [], "lacing": True, "node": node}
            self.tracks.append(self.track)
        elif ident == 0x6240 and self.track is not None:
            self.encoding = {"order": 0, "scope": 1, "type": 0, "compression": None, "settings": None, "encryption": None, "cipher": None, "has_encryption": False}
        elif ident == 0x5035 and self.encoding is not None:
            self.encoding["has_encryption"] = True

    def finish(self, ident, node):
        if ident == 0x6240 and self.encoding is not None and self.track is not None:
            self.track["encodings"].append(self.encoding)
            self.encoding = None
        elif ident == 0xAE:
            self.track = None

    def collect(self, ident, value, start, length):
        facts = self.facts
        ebml = facts["ebml"]
        names = {0x4282: "doctype", 0x4287: "doctype_version", 0x4285: "doctype_read_version", 0x4286: "version", 0x42F7: "read_version"}
        if ident in names and self.track is None:
            ebml[names[ident]] = value
        if ident == 0x42F2 and value is not None:
            # The Body is read with IDs of at least four octets whatever the header says (RFC 9559 section 4.4).
            self.max_id = max(4, min(value, 8))
            ebml["max_id_length"] = value
        if ident == 0x42F3 and value is not None:
            self.max_size = min(value, 8)
            ebml["max_size_length"] = value
        seg = facts["segments"][-1] if facts["segments"] else None
        if seg is not None:
            seg_names = {0x2AD7B1: "timestamp_scale", 0x4489: "duration", 0x7BA9: "title", 0x4D80: "muxing_app", 0x5741: "writing_app"}
            if ident in seg_names and "info" not in seg.get("_done", ()):
                seg.setdefault("info", {}).setdefault(seg_names[ident], value)
        t = self.track
        if t is not None:
            if ident == 0xD7:
                t["number"] = value
            elif ident == 0x73C5:
                t["uid"] = value
            elif ident == 0x83:
                t["type"] = value
            elif ident == 0x86:
                t["codec"] = value
            elif ident == 0x63A2:
                t["private"] = [start, length]
            elif ident == 0x9F:
                t["channels"] = value
            elif ident == 0xB5:
                # JSON has no NaN or infinity: a rate that is none is a rate the track does not state.
                t["rate"] = value if value is not None and math.isfinite(value) else None
            elif ident == 0x78B5:
                t["output_rate"] = value if value is not None and math.isfinite(value) else None
            elif ident == 0x6264:
                t["bits"] = value
            elif ident == 0x56AA:
                t["delay"] = value
            elif ident == 0x9C:
                t["lacing"] = value != 0
            elif ident == 0x23314F and value is not None:
                # Only a track that states a TrackTimestampScale has one in its facts (the default is 1.0).
                t["timestamp_scale"] = value
        e = self.encoding
        if e is not None:
            if ident == 0x5031:
                e["order"] = value
            elif ident == 0x5032:
                e["scope"] = value
            elif ident == 0x5033:
                e["type"] = value
            elif ident == 0x4254:
                e["compression"] = value
            elif ident == 0x4255:
                e["settings"] = [start, length]
            elif ident == 0x47E1:
                e["encryption"] = value
            elif ident == 0x47E8:
                e["cipher"] = value

    # -- clusters and segments

    def cluster(self, h, h_off, segment_end, segment):
        ident, id_length, size_length, size, _ = h
        data_off = h_off + id_length + size_length
        unknown = size is None
        declared = segment_end if unknown else data_off + size
        end = min(declared, segment_end)
        complete = declared <= segment_end
        items = []
        timestamp = 0
        has_timestamp = False
        position = data_off
        seen = 0
        timestamps = 0
        crc = None
        after_crc = False
        while position < end:
            try:
                ch = header(self.data, position, end, self.max_id, self.max_size)
            except Problem:
                items.append(Element("Unparsed Data", position, end - position))
                position = end
                break
            cid, cil, csl, csize, _ = ch
            if unknown and (cid in (EBML, SEGMENT) or is_top_level(cid)):
                end = position
                break
            c_data = position + cil + csl
            child_end = end if csize is None else min(c_data + csize, end)
            c_len = child_end - position
            if cid == SIMPLEBLOCK:
                self.read_block(position, cil + csl, child_end, "SimpleBlock", False, timestamp)
                items.append(Element("SimpleBlock", position, c_len, ident=cid))
            elif cid == BLOCKGROUP:
                group = Element("BlockGroup", position, c_len, children=[], ident=cid)
                self.group(group, c_data, child_end, 0, timestamp)
                items.append(group)
            elif cid == TIMESTAMP:
                length = child_end - c_data
                if length <= 8:
                    if timestamps == 0:
                        timestamp = int.from_bytes(self.data[c_data:child_end], "big")
                        has_timestamp = True
                    timestamps += 1
                else:
                    timestamps += 1
                items.append(Element("Timestamp", position, c_len, ident=cid))
            elif cid == CRC32:
                node = Element("CRC-32", position, c_len, ident=cid)
                if seen == 0 and csize == 4 and complete and c_data + 4 <= child_end:
                    node.children = [Element("Computed", c_data, 4)]
                items.append(node)
            else:
                items.append(Element(name_of(cid), position, c_len, ident=cid))
            seen += 1
            position = child_end
        node = Element("Cluster", h_off, end - h_off, children=items, ident=ident)
        segment["clusters"].append({"offset": h_off, "length": end - h_off, "timestamp": timestamp if has_timestamp else None,
                                    "size_unknown": unknown, "blocks": sum(1 for _ in self._cluster_blocks(items))})
        return node, end

    def _cluster_blocks(self, items):
        for item in items:
            if item.name == "SimpleBlock":
                yield item
            elif item.name == "BlockGroup":
                for child in item.children:
                    if child.name == "Block":
                        yield child

    def group(self, group, start, end, depth, timestamp):
        position = start
        while position < end:
            try:
                ch = header(self.data, position, end, self.max_id, self.max_size)
            except Problem:
                group.children.append(Element("Unparsed Data", position, end - position))
                return
            cid, cil, csl, csize, _ = ch
            c_data = position + cil + csl
            child_end = end if csize is None else min(c_data + csize, end)
            child = Element(name_of(cid), position, child_end - position, ident=cid)
            if cid == BLOCK and depth == 0:
                self.read_block(position, cil + csl, child_end, "Block", True, timestamp)
            if (cid == 0x75A1 and depth == 0) or (cid == 0xA6 and depth == 1):
                child.children = []
                self.group(child, c_data, child_end, depth + 1, timestamp)
            group.children.append(child)
            position = child_end

    def segment(self, h, h_off, header_missing):
        ident, id_length, size_length, size, _ = h
        data = self.data
        info = {"offset": h_off, "clusters": [], "size_unknown": size is None and not header_missing, "header_missing": header_missing}
        self.facts["segments"].append(info)
        start = h_off if header_missing else h_off + id_length + size_length
        unknown = size is None and not header_missing
        declared = len(data) if unknown or header_missing else start + size
        end = min(declared, len(data))
        children = []
        position = start
        while position < end:
            try:
                ch = header(data, position, end, self.max_id, self.max_size)
            except Problem:
                children.append(Element("Unparsed Data", position, end - position))
                position = end
                break
            if unknown and ch[0] in (EBML, SEGMENT):
                end = position
                break
            if ch[0] == CLUSTER:
                node, position = self.cluster(ch, position, end, info)
                children.append(node)
                continue
            node, position, complete = self.element(ch, position, SEGMENT, end, 2)
            children.append(node)
            if ch[0] == CRC32 and node.offset == start and complete and ch[3] == 4 and node.offset + ch[1] + ch[2] + 4 <= end and not unknown and declared <= len(data):
                node.children = [Element("Computed", node.offset + ch[1] + ch[2], 4)]
        info["length"] = end - h_off
        info["declared_end"] = declared
        node = Element("Segment", h_off, end - h_off, children=children, ident=SEGMENT)
        return node, end

    def run(self):
        data = self.data
        position = 0
        segment_seen = False
        while position < len(data):
            try:
                h = header(data, position, len(data), self.max_id, self.max_size)
            except Problem:
                self.top.append(Element("Unparsed Data", position, len(data) - position))
                break
            if h[0] == SEGMENT:
                segment_seen = True
                node, position = self.segment(h, position, False)
            elif not segment_seen and is_top_level(h[0]):
                segment_seen = True
                node, position = self.segment(h, position, True)
            else:
                node, position, _ = self.element(h, position, None, len(data), 1)
            self.top.append(node)
        return self


def read(data):
    return Reader(bytes(data)).run()


def writers_of(segments):
    """The writer each Segment's streams are named by, worked out from the documented rule rather than from the analyzer's code.

    RFC 9559 gives a Segment's Info two apps: the WritingApp, "the application that wrote this file", and the MuxingApp, "the muxing
    application or library". Both are claims of the writer role at low confidence, the application first, so it names the writer. Two
    exceptions: a text that is a libavcodec encoder ID ("Lavc60.31.102 libopus": "Lavc" and a digit or a space) names the encoder, not the
    writer; and a MuxingApp that names the Matroska libraries (mkvtoolnix: "libebml ... + libmatroska ...") or is ffmpeg's muxer version
    under the WritingApp of other software is the library under the application and only evidence. ffmpeg's muxer version ("Lavf...") shows as "FFmpeg (<text>)". Streams of one Segment share the
    result; a Segment with no claim has none (None)."""
    result = []
    for segment in segments:
        info = segment.get("info", {})
        writing, muxing = info.get("writing_app") or "", info.get("muxing_app") or ""
        # The texts as stored: ffmpeg's muxer version may follow white space ("^\s*Lavf" not followed by a letter), its encoder ID may not
        # ("^Lavc" and a digit or a space).
        # matroskaenc.c (FFmpeg 6.1.1) writes LIBAVFORMAT_IDENT as the MuxingApp always and the encoding_tool metadata as the WritingApp when there is
        # one, so a "Lavf" MuxingApp under the WritingApp of other software (neither names FFmpeg) is the library under it as well.
        muxer = re.match(r"^\s*Lavf(?![A-Za-z])", muxing) and not re.search(r"(?<![a-z])(ffmpeg|lavf|lavc|libav)(?![a-z])", writing.lower())
        library = (muxing.startswith(("libebml", "libmatroska")) or muxer) and writing.strip()
        candidates = [writing] + ([] if library else [muxing])
        text = next((t for t in candidates if t.strip() and not re.match(r"^Lavc(?:\d| )", t)), None)
        if text is None:
            result.append(None)
            continue
        shown = text.strip()
        if shown == "ffmpeg":
            name = "FFmpeg"
        elif re.match(r"^\s*Lavf(?![A-Za-z])", text):
            name = f"FFmpeg ({shown[:64]})"
        else:
            name = shown[:64]
        result.append({"name": name, "confidence": "Low"})
    return result


def manifest(data):
    """What the analyzer must show of a file, worked out from its bytes."""
    r = read(data)
    doctype = r.facts["ebml"].get("doctype")
    first = None
    for track in r.tracks:
        track.pop("node", None)
    return {
        "length": len(data),
        "format": "WebM" if doctype == "webm" else "Matroska",
        "ebml": r.facts["ebml"],
        "segments": [{k: v for k, v in s.items() if not k.startswith("_")} for s in r.facts["segments"]],
        "tree": [e.list() for e in r.top],
        "blocks": [b.dict() for b in r.blocks],
        "tracks": r.tracks,
        "writers": writers_of(r.facts["segments"]),
    }
