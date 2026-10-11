#!/usr/bin/env python3
"""Writes the byte-exact MPEG audio fixtures in tests/fixtures/mpeg.

Every frame has a zero payload, which every decoder treats as a valid silent frame, so the
headers, CRCs and the layout of frames, gaps and tags are fully under control. Each fixture is a
.mp1, .mp2 or .mp3 file plus a <name>.expected.json manifest:

- "regions": the top-level regions in file order: tags, and one "MPEG Audio" region for each
  stretch of data between tags.
- "frames": every frame in file order, with its offset, length and field values. A field path
  is a sequence of node names separated by "/", relative to the frame node.
- "gaps": bytes inside an "MPEG Audio" region that are not frames (junk, lost sync, trailing
  data).
- "findings": the findings the analyzer must produce, as a code plus a string the message has to
  contain. Findings of one kind are reported once per stream, not once per frame.
- "crosscheck": whether crosscheck_mpeg_fixtures.py must find exactly these frames with ffprobe
  (false where junk or damage leaves room for a decoder's own resynchronization choices), or
  "madplay" or "mpg123" to compare that decoder's frame count where ffmpeg cannot read the stream
  or resyncs differently.

A header only counts as the start of a stream when two more consistent frames (same version,
layer and sample rate) follow it, or when its frames end exactly at the end of the data.

Header layout, tables and frame lengths follow ISO/IEC 11172-3 and 13818-3 as summarized in
"MPEG Audio Frame Header" (datavoyage.com/mpgscript/mpeghdr.htm); MPEG 2.5 is the Fraunhofer
extension. The CRC is CRC-16 (polynomial 0x8005, initial value 0xFFFF) over the last two header
bytes and, for Layer III, the side information, or for Layer I the bit allocation; libmad
(madplay) confirms both. Layer II frames are left unprotected here, because their CRC coverage
depends on the allocation tables; the twolame fixtures cover Layer II CRCs.

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ.
"""

import json
import pathlib
import struct
import sys

import stream_stats

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg"

VERSION_BITS = {"1": 0b11, "2": 0b10, "2.5": 0b00}
VERSION_NAMES = {"1": "MPEG-1", "2": "MPEG-2", "2.5": "MPEG-2.5"}
LAYER_BITS = {1: 0b11, 2: 0b10, 3: 0b01}
LAYER_NAMES = {1: "I", 2: "II", 3: "III"}
BITRATES = {
    ("1", 1): [None, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
    ("1", 2): [None, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
    ("1", 3): [None, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    ("2", 1): [None, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
    ("2", 2): [None, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
}
BITRATES[("2", 3)] = BITRATES[("2", 2)]
SAMPLE_RATES = {"1": [44100, 48000, 32000], "2": [22050, 24000, 16000], "2.5": [11025, 12000, 8000]}
MODES = {"Stereo": 0, "Joint stereo": 1, "Dual channel": 2, "Mono": 3}
EMPHASIS = ["None", "50/15 µs", "Reserved", "CCITT J.17"]
EXTENSIONS = {".mp1": 1, ".mp2": 2, ".mp3": 3}


# --- Frames -----------------------------------------------------------------------------------


def bitrate_table(version, layer):
    return BITRATES[("1" if version == "1" else "2", layer)]


def frame_length(version, layer, bitrate, rate, padding):
    if layer == 1:
        return (12 * bitrate * 1000 // rate + padding) * 4
    # Layer III in MPEG 2 and 2.5 has 576 samples per frame instead of 1152.
    coefficient = 72 if layer == 3 and version != "1" else 144
    return coefficient * bitrate * 1000 // rate + padding


def side_info_size(version, mode):
    if version == "1":
        return 17 if mode == "Mono" else 32
    return 9 if mode == "Mono" else 17


def layer1_allocation_bytes(mode, mode_ext):
    if mode == "Mono":
        return 16
    bound = (mode_ext + 1) * 4 if mode == "Joint stereo" else 32
    return (bound * 2 + (32 - bound)) * 4 // 8


def crc16(data, crc=0xFFFF):
    for byte in data:
        for i in range(7, -1, -1):
            top = (crc >> 15) & 1
            crc = (crc << 1) & 0xFFFF
            if top ^ ((byte >> i) & 1):
                crc ^= 0x8005
    return crc


class Layer1Audio:
    """Layer I audio data (ISO/IEC 11172-3 2.4.1.5): allocation[ch][sb] (one row above the joint
    stereo bound), a scalefactor per allocated subband and channel, and twelve samples of
    allocation + 1 bits coded as zero, then ancillary bytes. Allocation 15 is forbidden; mpg123
    reads it as 16-bit samples, which the analyzer follows."""

    def __init__(self, allocation, scalefactors=None, ancillary=b""):
        self.allocation, self.scalefactors, self.ancillary = allocation, scalefactors or {}, ancillary

    def payload(self, mode, mode_ext):
        channels = 1 if mode == "Mono" else 2
        bound = (mode_ext + 1) * 4 if mode == "Joint stereo" else 32
        bits, fields = [], {}

        def put(value, count):
            bits.extend((value >> (count - 1 - i)) & 1 for i in range(count))

        def alloc(ch, sb):
            return self.allocation[ch if sb < bound else 0][sb]

        for sb in range(32):
            coded = channels if sb < bound else 1
            for ch in range(coded):
                put(alloc(ch, sb), 4)
                value = alloc(ch, sb)
                name = "Channels 0 and 1" if coded < channels else f"Channel {ch}"
                fields[f"Bit Allocation/Subband {sb}/{name}"] = "None" if value == 0 else "Forbidden" if value == 15 else f"{value + 1} bits per sample"
        for sb in range(32):
            for ch in range(channels):
                if alloc(ch, sb):
                    value = self.scalefactors.get((ch, sb), (sb * 5 + ch * 3) % 63)
                    put(value, 6)
                    fields[f"Scalefactors/Subband {sb}/Channel {ch}"] = str(value)
        sample_start = len(bits)
        for _ in range(12):
            for sb in range(32):
                for ch in range(channels if sb < bound else 1):
                    n = alloc(ch, sb)
                    if n:
                        put((1 << n) - 1, n + 1)
        fields["Samples"] = f"{len(bits) - sample_start} bits"
        bits.extend([0] * (-len(bits) % 8))
        data = bytes(int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8))
        return data, fields


def mode_extension_name(layer, mode, mode_ext):
    if mode != "Joint stereo":
        return None
    if layer == 3:
        return ["None", "Intensity stereo", "MS stereo", "MS and intensity stereo"][mode_ext]
    return f"Bands {(mode_ext + 1) * 4} to 31"


class Frame:
    """One frame with a zero payload; free-format frames give their length explicitly."""

    def __init__(self, version="1", layer=3, bitrate=128, rate=44100, padding=0, mode="Joint stereo", mode_ext=0,
                 protected=False, private=0, copyright=0, original=0, emphasis=0, bad_crc=False, free_length=None,
                 payload=None):
        self.version, self.layer, self.bitrate, self.rate = version, layer, bitrate, rate
        self.padding, self.mode, self.mode_ext, self.protected = padding, mode, mode_ext, protected
        self.private, self.copyright, self.original, self.emphasis = private, copyright, original, emphasis
        self.bad_crc, self.free_length, self.payload = bad_crc, free_length, payload
        self.audio_fields = {}

    def header(self):
        index = 0 if self.bitrate is None else bitrate_table(self.version, self.layer).index(self.bitrate)
        value = (0x7FF << 21) | (VERSION_BITS[self.version] << 19) | (LAYER_BITS[self.layer] << 17)
        value |= (0 if self.protected else 1) << 16
        value |= index << 12 | SAMPLE_RATES[self.version].index(self.rate) << 10 | self.padding << 9 | self.private << 8
        value |= MODES[self.mode] << 6 | self.mode_ext << 4 | self.copyright << 3 | self.original << 2 | self.emphasis
        return struct.pack(">I", value)

    def length(self):
        if self.bitrate is None:
            return self.free_length + (self.padding * (4 if self.layer == 1 else 1))
        return frame_length(self.version, self.layer, self.bitrate, self.rate, self.padding)

    def crc_coverage(self):
        if self.layer == 3:
            return side_info_size(self.version, self.mode)
        if self.layer == 1:
            return layer1_allocation_bytes(self.mode, self.mode_ext)
        raise ValueError("Layer II CRCs are not built here")

    def bytes(self):
        header = self.header()
        body = bytearray(self.length() - 4 - (2 if self.protected else 0))
        if isinstance(self.payload, Layer1Audio):
            # Everything after the samples is ancillary data.
            data, self.audio_fields = self.payload.payload(self.mode, self.mode_ext)
            if len(body) > len(data):
                self.audio_fields["Ancillary Data"] = f"{len(body) - len(data)} bytes"
            data = (data + self.payload.ancillary)[: len(body)]
            body[: len(data)] = data
        elif self.payload is not None:
            offset, data = self.payload
            body[offset : offset + len(data)] = data
        if not self.protected:
            return header + bytes(body)
        crc = crc16(header[2:4] + bytes(body[: self.crc_coverage()]))
        self.crc = crc ^ (0x0001 if self.bad_crc else 0)
        return header + struct.pack(">H", self.crc) + bytes(body)

    def fields(self):
        fields = {
            "Header/Version": VERSION_NAMES[self.version],
            "Header/Layer": LAYER_NAMES[self.layer],
            "Header/Bitrate": "free" if self.bitrate is None else f"{self.bitrate} kbps",
            "Header/Sample Rate": f"{self.rate} Hz",
            "Header/Channel Mode": self.mode,
            "Header/HasPadding": "true" if self.padding else "false",
            "Header/IsProtected": "true" if self.protected else "false",
        }
        extension = mode_extension_name(self.layer, self.mode, self.mode_ext)
        if extension is not None:
            fields["Header/Mode Extension"] = extension
        if self.private:
            fields["Header/IsPrivate"] = "true"
        if self.copyright:
            fields["Header/IsCopyrighted"] = "true"
        if self.original:
            fields["Header/IsOriginal"] = "true"
        if self.emphasis:
            fields["Header/Emphasis"] = EMPHASIS[self.emphasis]
        if self.protected:
            fields["CRC"] = f"0x{self.crc:04X}"
            fields["CRC/Computed"] = f"0x{self.crc ^ (0x0001 if self.bad_crc else 0):04X}"
        fields.update(self.audio_fields)
        return fields


def cbr_padding(count, bitrate, rate, layer=3):
    """Padding pattern an encoder uses to hold an exact bitrate when frames are fractional."""
    slots, unit = (12, 4) if layer == 1 else (144, 1)
    exact = slots * bitrate * 1000 / rate
    pattern, owed = [], 0.0
    for _ in range(count):
        owed += exact - int(exact)
        if owed >= 1:
            pattern.append(1)
            owed -= 1
        else:
            pattern.append(0)
    return pattern


def stream(count, **kwargs):
    if kwargs.get("bitrate", 128) is not None and "padding" not in kwargs:
        layer, bitrate, rate = kwargs.get("layer", 3), kwargs.get("bitrate", 128), kwargs.get("rate", 44100)
        return [Frame(padding=p, **kwargs) for p in cbr_padding(count, bitrate, rate, layer)]
    return [Frame(**kwargs) for _ in range(count)]


# --- Tags and junk ----------------------------------------------------------------------------


def syncsafe(value):
    return bytes((value >> (7 * (3 - i))) & 0x7F for i in range(4))


def id3v2(title, major=3, footer=False, extra_frames=b""):
    data = title.encode("latin-1")
    size_bytes = len(data) + 1
    size = syncsafe(size_bytes) if major == 4 else struct.pack(">I", size_bytes)
    frames = b"TIT2" + size + b"\x00\x00" + b"\x00" + data + extra_frames
    flags = 0x10 if footer else 0
    tag = b"ID3" + bytes([major, 0, flags]) + syncsafe(len(frames)) + frames
    if footer:
        tag += b"3DI" + bytes([major, 0, flags]) + syncsafe(len(frames))
    return tag


def tsse_frame(text, major=3):
    """A TSSE text frame in ISO-8859-1, "the used audio encoder and its settings" (ID3v2.3 4.2.1)."""
    data = b"\x00" + text.encode("latin-1")
    size = syncsafe(len(data)) if major == 4 else struct.pack(">I", len(data))
    return b"TSSE" + size + b"\x00\x00" + data


def id3v22(frames):
    """An ID3v2.2 tag of (three-letter ID, text) frames in ISO-8859-1."""
    body = b"".join(fid.encode("ascii") + struct.pack(">I", len(text) + 1)[1:] + b"\x00" + text.encode("latin-1") for fid, text in frames)
    return b"ID3" + bytes([2, 0, 0]) + syncsafe(len(body)) + body


def seek_frame(offset):
    return b"SEEK" + syncsafe(4) + b"\x00\x00" + struct.pack(">I", offset)


def id3v1(title):
    return b"TAG" + title.encode("latin-1").ljust(30, b"\x00") + b"\x00" * 94 + b"\xff"


def ape(title):
    value = title.encode("utf-8")
    item = struct.pack("<II", len(value), 0) + b"Title\x00" + value
    size = len(item) + 32
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, size, 1, 0) + b"\x00" * 8
    return item + footer


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


def junk(length, seed):
    """Pseudo-random bytes without $FF, so they never hold a frame header by accident."""
    rng = Rng(seed)
    return bytes(rng.next() % 255 for _ in range(length))


LONE_HEADER = Frame(version="1", layer=3, bitrate=128, rate=44100, mode="Stereo").header()


# --- Fixture assembly -------------------------------------------------------------------------


class Fixture:
    out = OUT

    def __init__(self, name, extension, description, crosscheck=True):
        self.name, self.extension, self.description, self.crosscheck = name, extension, description, crosscheck
        self.data = bytearray()
        self.regions, self.frames, self.gaps, self.findings = [], [], [], []
        self.audio_start = None
        # Per frame, what the statistics need beyond the manifest entry; "header" is set for a
        # frame that holds a VBR header.
        self.layout = []
        # The encoder the analyzer must name, when the fixture states one.
        self.encoder = None

    def tag(self, name, data):
        self.end_audio()
        self.regions.append({"name": name, "offset": len(self.data), "length": len(data)})
        self.data += data
        return self

    def begin_audio(self):
        if self.audio_start is None:
            self.audio_start = len(self.data)

    def end_audio(self):
        if self.audio_start is not None and len(self.data) > self.audio_start:
            self.regions.append({"name": "MPEG Audio", "offset": self.audio_start, "length": len(self.data) - self.audio_start})
        self.audio_start = None

    def add_frames(self, frames, truncate_last=None):
        """Adds frames; a frame with a cut_to attribute keeps only that many bytes, where the next
        frame starts."""
        self.begin_audio()
        for i, frame in enumerate(frames):
            data = frame.bytes()
            entry = {"offset": len(self.data), "length": len(data), "fields": frame.fields()}
            cut = truncate_last if truncate_last is not None and i == len(frames) - 1 else getattr(frame, "cut_to", None)
            if cut is not None:
                data = data[:cut]
                entry["length"] = len(data)
                entry["truncated"] = True
            self.frames.append(entry)
            self.layout.append(frame_layout(frame, len(data)))
            self.data += data
        return self

    def gap(self, data):
        self.begin_audio()
        self.gaps.append({"offset": len(self.data), "length": len(data)})
        self.data += data
        return self

    def finding(self, code, contains):
        self.findings.append({"code": code, "contains": contains})
        return self

    def statistics(self):
        """Adds each MPEG Audio region's statistics (stream_stats.py) and the findings about VBR
        header counts; the reservoir model must agree with any fill the fixture states itself."""
        fills = stream_stats.reservoir_fills(self.layout)
        infos = []
        for entry, layout, fill in zip(self.frames, self.layout, fills):
            stated = entry["fields"].get("Side Info/Reservoir Fill")
            assert stated is None or stated == f"{fill} bytes", (self.name, entry["offset"], stated, fill)
            fields = entry["fields"]
            bitrate = fields["Header/Bitrate"]
            infos.append(stream_stats.FrameInfo(
                entry["offset"], entry["length"], fields["Header/Version"], fields["Header/Layer"],
                int(fields["Header/Sample Rate"].split()[0]), None if bitrate == "free" else int(bitrate.split()[0]),
                header=layout.get("header"), truncated=entry.get("truncated", False), fill=fill))
        for region in self.regions:
            if region["name"] != "MPEG Audio":
                continue
            inside = [i for i in infos if region["offset"] <= i.offset < region["offset"] + region["length"]]
            fields = stream_stats.region_fields(inside, infos)
            if fields:
                region["statistics"] = fields
        for info in infos:
            if info.header is not None:
                for code, contains in stream_stats.header_findings(info, infos):
                    if not any(f["code"] == code and f["contains"] == contains for f in self.findings):
                        self.finding(code, contains)

    def manifest(self):
        self.statistics()
        return {
            "description": self.description,
            "length": len(self.data),
            "crosscheck": self.crosscheck,
            "regions": self.regions,
            "frames": self.frames,
            "gaps": self.gaps,
            "findings": self.findings,
            **({"detected_encoder": self.encoder} if self.encoder else {}),
        }

    def write(self):
        self.end_audio()
        path = self.out / f"{self.name}{self.extension}"
        path.write_bytes(bytes(self.data))
        text = json.dumps(self.manifest(), indent=1, ensure_ascii=False) + "\n"
        (self.out / f"{self.name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


def frame_layout(frame, available):
    """What the reservoir model needs of a frame: whether its side information is there, its data
    area after the side information, and where its main data lies (none for a zero frame)."""
    side = side_info_size(frame.version, frame.mode) if frame.layer == 3 else 0
    head = 4 + (2 if frame.protected else 0) + side
    return {
        "layer3": frame.layer == 3 and available >= head,
        "stream": (VERSION_NAMES[frame.version], frame.rate),
        "area": available - head,
        "main_data_begin": getattr(frame, "main_data_begin", 0),
        "main_data_length": frame.main_data_length() if hasattr(frame, "main_data_length") else 0,
    }


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


# --- Versions, layers, bitrates and header fields ---------------------------------------------

BASIC = [
    ("mpeg1-layer1-stereo", ".mp1", dict(version="1", layer=1, bitrate=384, rate=44100, mode="Stereo")),
    ("mpeg1-layer2-stereo", ".mp2", dict(version="1", layer=2, bitrate=192, rate=48000, mode="Stereo")),
    ("mpeg1-layer3-joint", ".mp3", dict(version="1", layer=3, bitrate=128, rate=44100, mode="Joint stereo", mode_ext=2)),
    ("mpeg1-layer3-mono", ".mp3", dict(version="1", layer=3, bitrate=64, rate=32000, mode="Mono")),
    ("mpeg1-layer3-dual", ".mp3", dict(version="1", layer=3, bitrate=192, rate=48000, mode="Dual channel")),
    ("mpeg2-layer1-stereo", ".mp1", dict(version="2", layer=1, bitrate=128, rate=16000, mode="Stereo")),
    ("mpeg2-layer2-mono", ".mp2", dict(version="2", layer=2, bitrate=64, rate=24000, mode="Mono")),
    ("mpeg2-layer3-stereo", ".mp3", dict(version="2", layer=3, bitrate=64, rate=22050, mode="Stereo")),
    ("mpeg25-layer3-mono", ".mp3", dict(version="2.5", layer=3, bitrate=32, rate=8000, mode="Mono")),
    ("mpeg25-layer3-stereo", ".mp3", dict(version="2.5", layer=3, bitrate=64, rate=11025, mode="Joint stereo", mode_ext=3)),
]


def make_basic(name, extension, params):
    def build():
        f = Fixture(name, extension, f"Ten frames: {VERSION_NAMES[params['version']]} Layer {LAYER_NAMES[params['layer']]}, "
                    f"{params['bitrate']} kbps, {params['rate']} Hz, {params['mode']}.")
        return f.add_frames(stream(10, **params))

    return build


for _name, _ext, _params in BASIC:
    FIXTURES.append(make_basic(_name, _ext, _params))


@fixture
def every_bitrate():
    f = Fixture("mpeg1-layer3-every-bitrate", ".mp3", "One MPEG-1 Layer III frame for each of the 14 bitrate indexes, at 44100 Hz (a VBR stream).")
    frames = [Frame(bitrate=b, mode="Joint stereo") for b in bitrate_table("1", 3)[1:]]
    return f.add_frames(frames)


@fixture
def header_flags():
    f = Fixture(
        "mpeg1-layer3-header-flags",
        ".mp3",
        "Frames with the private, copyright and original bits, each emphasis value that is defined, and every Layer III "
        "mode extension. ffmpeg only syncs once those bits stop changing, so the cross-check counts libmad's frames.",
        crosscheck="madplay",
    )
    frames = [
        Frame(private=1),
        Frame(copyright=1, original=1),
        Frame(emphasis=1),
        Frame(emphasis=3),
        Frame(mode_ext=0),
        Frame(mode_ext=1),
        Frame(mode_ext=2),
        Frame(mode_ext=3),
    ]
    return f.add_frames(frames)


@fixture
def layer1_mode_extensions():
    f = Fixture("mpeg1-layer1-mode-extensions", ".mp1", "Layer I joint stereo frames with each intensity stereo bound (bands 4, 8, 12 and 16 to 31).")
    return f.add_frames([Frame(layer=1, bitrate=256, rate=48000, mode="Joint stereo", mode_ext=e) for e in range(4)] * 2)


@fixture
def reserved_emphasis():
    f = Fixture("mpeg1-layer3-reserved-emphasis", ".mp3", "A stream whose third frame uses the reserved emphasis value: still a frame, reported.")
    frames = stream(6)
    frames[2].emphasis = 2
    f.add_frames(frames)
    return f.finding("mpeg.emphasis-reserved", "reserved")


@fixture
def layer2_mode_not_allowed():
    f = Fixture(
        "mpeg1-layer2-mode-not-allowed",
        ".mp2",
        "MPEG-1 Layer II at 32 kbps in stereo, a combination the standard allows only for single channel: frames are "
        "kept, reported once.",
    )
    f.add_frames(stream(6, version="1", layer=2, bitrate=32, rate=48000, mode="Stereo"))
    return f.finding("mpeg.bitrate-mode-not-allowed", "32 kbps")


@fixture
def free_format():
    f = Fixture(
        "mpeg1-layer3-free-format",
        ".mp3",
        "Free-format MPEG-1 Layer III at 48000 Hz with 1440-byte frames (480 kbps). ffmpeg does not read free format, so "
        "the cross-check counts the frames mpg123 decodes.",
        crosscheck="mpg123",
    )
    f.add_frames([Frame(bitrate=None, rate=48000, free_length=1440) for _ in range(6)])
    return f.finding("mpeg.free-format", "1440")


@fixture
def mpeg25_layer2():
    f = Fixture(
        "mpeg25-layer2-mono",
        ".mp2",
        "MPEG-2.5 Layer II mono at 8000 Hz and 64 kbps. MPEG 2.5 is Fraunhofer's extension of Layer III only, so no "
        "standard defines these frames; decoders play them, and they are reported.",
        crosscheck="madplay",
    )
    f.add_frames(stream(6, version="2.5", layer=2, bitrate=64, rate=8000, mode="Mono"))
    return f.finding("mpeg.mpeg25-layer", "Layer II")


@fixture
def free_format_period():
    f = Fixture(
        "free-format-period-4",
        ".mp3",
        "The free-format header FF FB 00 00 repeated 1000 times. The next header is 4 bytes on, too close for a "
        "frame's header and side information, so there is no frame, as mpg123, libmad and ffmpeg find.",
        crosscheck=False,
    )
    f.data += bytes.fromhex("FFFB0000") * 1000
    f.regions.append({"name": "Data", "offset": 0, "length": len(f.data)})
    return f.finding("format.unrecognized", "No format parser")


# --- Layer I audio data ------------------------------------------------------------------------


@fixture
def layer1_audio_data():
    f = Fixture(
        "mpeg1-layer1-audio-data",
        ".mp1",
        "MPEG-1 Layer I joint stereo at 48000 Hz and 256 kbps (bound 8) with CRC, allocations below and above the "
        "bound, scalefactors, zero-coded samples and ancillary text; the third frame uses scalefactor index 63, which "
        "Table B.1 does not define.",
    )
    frames = []
    for i in range(6):
        allocation = [[(sb * 3 + i + ch) % 6 if sb < 20 else 0 for sb in range(32)] for ch in range(2)]
        scalefactors = {(1, 2): 63} if i == 2 else {}
        frames.append(Frame(layer=1, bitrate=256, rate=48000, mode="Joint stereo", mode_ext=1, protected=True,
                            payload=Layer1Audio(allocation, scalefactors, b"Layer I ancillary data.")))
    f.add_frames(frames)
    return f.finding("mpeg.scalefactor-undefined", "1 frame")


@fixture
def layer1_forbidden_allocation():
    f = Fixture(
        "mpeg1-layer1-forbidden-allocation",
        ".mp1",
        "MPEG-1 Layer I mono at 44100 Hz and 128 kbps with CRC whose subband 0 has the forbidden bit allocation 15 in "
        "every frame. libmad rejects these frames; mpg123 reads 16-bit samples, as the analyzer does.",
        crosscheck=False,
    )
    allocation = [[15] + [0] * 31]
    f.add_frames([Frame(layer=1, bitrate=128, rate=44100, mode="Mono", protected=True, payload=Layer1Audio(allocation))
                  for _ in range(6)])
    return f.finding("mpeg.allocation-forbidden", "6 frames")


@fixture
def layer1_overflow():
    f = Fixture(
        "mpeg1-layer1-overflow",
        ".mp1",
        "MPEG-1 Layer I mono at 44100 Hz and 128 kbps whose allocations need far more bits than the 136-byte frames "
        "hold.",
        crosscheck=False,
    )
    frames = [Frame(layer=1, bitrate=128, rate=44100, mode="Mono", payload=Layer1Audio([[14] * 32])) for _ in range(6)]
    f.add_frames(frames)
    for entry in f.frames:
        entry["fields"]["Samples"] = f"{(136 - 4) * 8 - 128 - 32 * 6} bits"
    return f.finding("mpeg.audio-data-overflow", "Layer I")


# --- CRC ---------------------------------------------------------------------------------------


@fixture
def layer3_crc():
    f = Fixture("mpeg1-layer3-crc", ".mp3", "MPEG-1 Layer III stereo frames protected by CRC-16 over the header and the 32 bytes of side information.")
    return f.add_frames(stream(6, mode="Stereo", protected=True))


@fixture
def layer3_crc_mono_lsf():
    f = Fixture("mpeg2-layer3-crc-mono", ".mp3", "MPEG-2 Layer III mono frames with CRC over the header and 9 bytes of side information.")
    return f.add_frames(stream(6, version="2", bitrate=32, rate=22050, mode="Mono", protected=True))


@fixture
def layer1_crc():
    f = Fixture("mpeg1-layer1-crc", ".mp1", "MPEG-1 Layer I joint stereo frames (bound 8) with CRC over the header and the bit allocation.")
    return f.add_frames(stream(6, layer=1, bitrate=256, rate=48000, mode="Joint stereo", mode_ext=1, protected=True))


@fixture
def crc_mismatch():
    f = Fixture("mpeg1-layer3-crc-mismatch", ".mp3", "MPEG-1 Layer III frames with CRC; the third frame's CRC is wrong.")
    frames = stream(6, mode="Stereo", protected=True)
    frames[2].bad_crc = True
    f.add_frames(frames)
    return f.finding("mpeg.crc-mismatch", "1 frame")


# --- Sync, junk and damage ---------------------------------------------------------------------


@fixture
def leading_junk():
    f = Fixture(
        "leading-junk",
        ".mp3",
        "300 bytes of junk before the first frame. The junk holds one valid-looking header that no chain of frames "
        "follows, so it is not a frame.",
        crosscheck=False,
    )
    data = bytearray(junk(300, 11))
    data[120:124] = LONE_HEADER
    f.gap(bytes(data))
    f.add_frames(stream(8))
    return f.finding("mpeg.leading-junk", "300 bytes")


@fixture
def leading_zeros():
    f = Fixture("leading-zeros", ".mp3", "ID3v2.3 tag, then 512 zero bytes, then the frames: the zeros are junk before the first frame.")
    f.tag("ID3v2", id3v2("Zero Padded"))
    f.gap(bytes(512))
    f.add_frames(stream(8))
    return f.finding("mpeg.leading-junk", "zero")


@fixture
def two_header_false_chain():
    f = Fixture(
        "false-chain-of-two",
        ".mp3",
        "Junk with two valid headers at the right distance but no third frame after them: two frames are not a chain, "
        "so the real stream starts later.",
        crosscheck=False,
    )
    fake = Frame(mode="Stereo").bytes()
    data = bytearray(junk(50, 21)) + fake + fake + junk(80, 22)
    f.gap(bytes(data))
    f.add_frames(stream(8))
    return f.finding("mpeg.leading-junk", f"{len(data)} bytes")


@fixture
def sync_lost():
    f = Fixture("sync-lost", ".mp3", "Five frames, 37 bytes of garbage, five more frames: sync is lost and found again.", crosscheck=False)
    f.add_frames(stream(5))
    f.gap(junk(37, 31))
    f.add_frames(stream(5))
    return f.finding("mpeg.sync-lost", "37 bytes")


@fixture
def false_sync_in_payload():
    f = Fixture(
        "false-sync-in-payload",
        ".mp3",
        "Frames whose payload holds bytes that look like a frame header. Frames are followed by their length, so the "
        "payload is never searched.",
    )
    frames = stream(8)
    for frame in frames:
        frame.payload = (100, LONE_HEADER * 3)
    return f.add_frames(frames)


@fixture
def truncated_last_frame():
    f = Fixture("truncated-last-frame", ".mp3", "Six frames; the file ends 100 bytes into the sixth.", crosscheck=False)
    f.add_frames(stream(6), truncate_last=100)
    return f.finding("mpeg.frame-truncated", "100 of")


@fixture
def trailing_garbage():
    f = Fixture("trailing-garbage", ".mp3", "Eight frames followed by 50 bytes that are neither a frame nor a tag.", crosscheck=False)
    f.add_frames(stream(8))
    f.gap(junk(50, 41))
    return f.finding("mpeg.trailing-data", "50 bytes")


@fixture
def short_stream():
    f = Fixture("two-frames", ".mp3", "A file of exactly two frames: the chain is short but ends exactly at the end of the data.")
    return f.add_frames(stream(2))


@fixture
def no_frames():
    f = Fixture("no-frames", ".mp3", "Junk with three lone valid headers and no chain: no MPEG audio is recognized.", crosscheck=False)
    data = bytearray(junk(2000, 51))
    for offset in (100, 700, 1500):
        data[offset : offset + 4] = LONE_HEADER
    f.data += data
    f.regions.append({"name": "Data", "offset": 0, "length": len(data)})
    return f.finding("format.unrecognized", "No format parser")


@fixture
def cut_frame():
    f = Fixture(
        "cut-frame-stream-change",
        ".mp3",
        "Five frames at 44100 Hz, the first 200 bytes of a sixth, then ten frames at 48000 Hz: the cut frame ends where "
        "the new stream starts, so no frame is lost.",
        crosscheck=False,
    )
    f.add_frames(stream(5))
    cut = Frame(mode="Joint stereo")
    cut.cut_to = 200
    f.add_frames([cut])
    f.add_frames(stream(10, rate=48000))
    f.finding("mpeg.frame-cut", "200 of its 417 bytes")
    return f.finding("mpeg.stream-changed", "48000 Hz")


@fixture
def damaged_bitrate():
    f = Fixture(
        "damaged-bitrate",
        ".mp3",
        "Eight frames at 128 kbps; the fourth frame's bitrate index is damaged to 192 kbps, which would make it 627 bytes "
        "long. The fifth frame starts 418 bytes in, so the damaged frame ends there and no frame is lost.",
        crosscheck=False,
    )
    frames = stream(8)
    real = frames[3].length()
    frames[3].bitrate = 192
    frames[3].cut_to = real
    f.add_frames(frames)
    return f.finding("mpeg.frame-cut", f"{real} of its {frames[3].length()} bytes")


@fixture
def free_format_length_change():
    f = Fixture(
        "free-format-length-change",
        ".mp3",
        "Five free-format MPEG-1 Layer III frames of 500 bytes, then five of 600 bytes: the same stream with another "
        "frame length, which ISO/IEC 11172-3 does not allow, reported.",
        crosscheck=False,
    )
    f.add_frames([Frame(bitrate=None, free_length=500) for _ in range(5)])
    f.add_frames([Frame(bitrate=None, free_length=600) for _ in range(5)])
    f.finding("mpeg.free-format", "500 bytes")
    return f.finding("mpeg.free-format-length-changed", "from 500 to 600 bytes")


# --- Stream changes ----------------------------------------------------------------------------


@fixture
def sample_rate_change():
    f = Fixture("sample-rate-change", ".mp3", "Five frames at 44100 Hz directly followed by five at 48000 Hz: a new stream starts, no bytes are lost.")
    f.add_frames(stream(5))
    f.add_frames(stream(5, rate=48000))
    return f.finding("mpeg.stream-changed", "48000 Hz")


@fixture
def layer_change():
    f = Fixture("layer-change", ".mp3", "Five MPEG-1 Layer III frames directly followed by five Layer II frames.")
    f.add_frames(stream(5, rate=48000))
    f.add_frames(stream(5, layer=2, bitrate=192, rate=48000, mode="Stereo"))
    return f.finding("mpeg.stream-changed", "Layer II")


@fixture
def stream_change_after_gap():
    f = Fixture(
        "stream-change-after-gap",
        ".mp3",
        "Five MPEG-1 Layer III frames at 44100 Hz, 37 bytes of junk, then five MPEG-2 Layer II mono frames at 24000 Hz: "
        "sync is lost, and the stream found again is a different one.",
        crosscheck=False,
    )
    f.add_frames(stream(5))
    f.gap(junk(37, 61))
    f.add_frames(stream(5, version="2", layer=2, bitrate=64, rate=24000, mode="Mono"))
    f.finding("mpeg.sync-lost", "37 bytes")
    return f.finding("mpeg.stream-changed", "MPEG-2 Layer II, 24000 Hz")


@fixture
def free_to_fixed():
    f = Fixture(
        "free-format-to-fixed",
        ".mp3",
        "Five free-format MPEG-1 Layer III frames of 500 bytes at 44100 Hz directly followed by five at 128 kbps: the "
        "same version, layer and rate, but a new stream, which the message names as free format.",
        crosscheck=False,
    )
    f.add_frames([Frame(bitrate=None, free_length=500) for _ in range(5)])
    f.add_frames(stream(5))
    f.finding("mpeg.free-format", "500 bytes")
    return f.finding("mpeg.stream-changed", "44100 Hz, free format to MPEG-1 Layer III, 44100 Hz at")


@fixture
def channel_mode_change():
    f = Fixture("channel-mode-change", ".mp3", "Joint stereo frames followed by mono frames within one stream.")
    f.add_frames(stream(4))
    f.add_frames(stream(4, mode="Mono"))
    return f.finding("mpeg.channel-mode-changed", "Mono")


# --- Streams with tags ------------------------------------------------------------------------


@fixture
def tss_names_encoder():
    f = Fixture(
        "id3v22-tss-names-encoder",
        ".mp3",
        "ID3v2.2 tag whose TSS frame says \"Xing MP3 Encoder 1.5\" before frames that show no encoder's habits: the tag "
        "alone names the encoder, with low confidence.",
    )
    f.tag("ID3v2", id3v22([("TT2", "Claimed"), ("TSS", "Xing MP3 Encoder 1.5")]))
    f.add_frames(stream(8))
    f.encoder = {"name": "Xing MP3 Encoder 1.5", "confidence": "Low"}
    return f


@fixture
def tsse_names_ffmpeg():
    f = Fixture(
        "id3v24-tsse-ffmpeg",
        ".mp3",
        "ID3v2.4 tag whose TSSE frame holds ffmpeg's version, as ffmpeg's muxer writes it, before frames that show no "
        "encoder's habits: ffmpeg is named, with low confidence.",
    )
    f.tag("ID3v2", id3v2("Muxed", major=4, extra_frames=tsse_frame("Lavf58.76.100", major=4)))
    f.add_frames(stream(8))
    f.encoder = {"name": "FFmpeg (Lavf58.76.100)", "confidence": "Low"}
    return f


@fixture
def tagged_stream():
    f = Fixture("tagged-stream", ".mp3", "ID3v2.3 tag, eight frames, an APEv2 tag and an ID3v1 tag: the frames fill the data between the tags.")
    f.tag("ID3v2", id3v2("Tagged"))
    f.add_frames(stream(8))
    f.tag("APE", ape("Tagged"))
    f.tag("ID3v1", id3v1("Tagged"))
    return f


@fixture
def seek_split_stream():
    f = Fixture(
        "seek-split-stream",
        ".mp3",
        "ID3v2.4 tag whose SEEK frame points at a second ID3v2.4 tag between frames; the stream continues after it, so "
        "there are two MPEG Audio regions. ffmpeg loses a frame resyncing after the tag, so the cross-check counts "
        "libmad's frames.",
        crosscheck="madplay",
    )
    first = stream(5)
    first_length = sum(len(frame.bytes()) for frame in first)
    f.tag("ID3v2", id3v2("Lead", major=4, extra_frames=seek_frame(first_length)))
    f.add_frames(first)
    f.tag("ID3v2", id3v2("Middle", major=4))
    f.add_frames(stream(5))
    return f


@fixture
def concatenated_files():
    f = Fixture(
        "concatenated-files",
        ".mp3",
        "Two tagged files concatenated. The first file's ID3v1 tag and the second file's ID3v2 tag sit between the "
        "streams, away from any anchor, so they are a gap in the frames.",
        crosscheck=False,
    )
    f.tag("ID3v2", id3v2("First"))
    f.add_frames(stream(5))
    f.gap(id3v1("First") + id3v2("Second"))
    f.add_frames(stream(5))
    f.tag("ID3v1", id3v1("Second"))
    return f.finding("mpeg.sync-lost", "bytes")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for path in OUT.glob("*"):
        if path.name.startswith("external-") or path.name.endswith(".report.json"):
            continue
        path.unlink()
    names = set()
    for build in FIXTURES:
        f = build()
        assert f.name not in names, f.name
        names.add(f.name)
        f.write()
    print(f"Wrote {len(names)} fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
