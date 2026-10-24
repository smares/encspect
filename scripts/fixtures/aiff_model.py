"""An independent reading of AIFF and AIFF-C files, for the manifests of the AIFF fixtures and their cross-check.

Written from Apple's "Audio Interchange File Format AIFF-C" draft of 8/26/91 and "Audio Interchange File Format: AIFF, version 1.3" (chunks,
Common Chunk, Sound Data Chunk, markers, comments, instrument, text chunks, and in the AIFF-C draft's appendix C the sample frames of ACE and MACE),
for the compression types they do not list Apple's QuickTime File Format Specification (2001-03-01, "Sound Codec Formats Supported") and, for the
packet of ima4, Apple's Core Audio Format Specification ("Audio Description Chunk"), and for "GSM " FFmpeg 6.1.1's libavformat/aiff.c, apart from the
analyzer: the chunk tree and the stream a manifest states come from here. The byte builders of the fixture scripts live here too.

    read(data)          the chunk tree of a file
    manifest(data)      the length, the chunks and the stream the analyzer must report
"""

import math
import struct

# compression type: (codec, bytes per sample for the codings that store samples one after another, or None, whether a sample's width follows the
# sample size, bits fixed by the type or None).
TYPES = {
    "NONE": ("PCM", None, True, None),
    "twos": ("PCM", None, True, None),
    "sowt": ("PCM", None, True, None),
    "in24": ("PCM", 3, True, 24),
    "in32": ("PCM", 4, True, 32),
    "raw ": ("PCM", 1, True, 8),
    "fl32": ("PCM (floating-point)", 4, True, 32),
    "fl64": ("PCM (floating-point)", 8, True, 64),
    "alaw": ("A-law", 1, False, None),
    "ulaw": ("Mu-law", 1, False, None),
    "ima4": ("IMA ADPCM", None, False, None),
    "GSM ": ("GSM 6.10", None, False, None),
    "ACE2": ("ACE 2-to-1", None, False, None),
    "ACE8": ("ACE 8-to-3", None, False, None),
    "MAC3": ("MACE 3-to-1", None, False, None),
    "MAC6": ("MACE 6-to-1", None, False, None),
}

# compression type: (bytes of one compressed sample frame of one channel, samples of one channel it holds), for the codings that count compressed
# sample frames in the Common Chunk (AIFF-C draft, section 4.0 and appendix C; Core Audio Format Specification for ima4).
PACKETS = {"ima4": (34, 64), "ACE2": (1, 2), "ACE8": (3, 8), "MAC3": (2, 6), "MAC6": (1, 6)}

LAYOUTS = {1: ["front center"], 2: ["front left", "front right"], 3: ["front left", "front right", "front center"]}


def u16(data, at):
    return struct.unpack_from(">H", data, at)[0]


def u32(data, at):
    return struct.unpack_from(">I", data, at)[0]


def is_id(raw):
    """Four printable characters, no space before a printing character (AIFF-C draft, section 1.0), and the first is not a space."""
    return len(raw) == 4 and raw[0] != 0x20 and all(0x20 <= b <= 0x7E for b in raw) and b" " not in raw.rstrip(b" ")


def extended(raw):
    """The value of an 80-bit extended number (sign, 15-bit exponent with bias 16383, 64-bit mantissa with an explicit leading bit), or None if it is infinite or not a number."""
    sign = raw[0] >> 7
    exponent = ((raw[0] & 0x7F) << 8) | raw[1]
    mantissa = int.from_bytes(raw[2:10], "big")
    if exponent == 0x7FFF:
        return None
    value = math.ldexp(mantissa, (exponent or 1) - 16383 - 63)
    return -value if sign else value


def read_chunks(data, at, end):
    chunks = []
    while at + 8 <= end and len(chunks) < 2048:
        raw = data[at:at + 4]
        if not is_id(raw):
            break
        cid = raw.decode("latin1")
        stated = u32(data, at + 4)
        body = at + 8
        remaining = end - body
        placeholder = cid == "SSND" and stated == 0xFFFFFFFF
        length = remaining if placeholder else min(stated, remaining)
        chunk = {"id": cid, "offset": at, "size": stated, "length": length, "placeholder": placeholder, "pad": False}
        body_end = body + length
        nxt = body_end
        if length % 2 == 1 and stated <= remaining and not placeholder:
            padded = body_end + 1 + 8 <= end and is_id(data[body_end + 1:body_end + 5])
            unpadded = body_end + 8 <= end and is_id(data[body_end:body_end + 4])
            if body_end < end and not (not padded and unpadded):
                chunk["pad"] = True
                nxt = body_end + 1
        chunk["end"] = nxt
        chunks.append(chunk)
        if stated > remaining or placeholder:
            break
        at = nxt
    return chunks


def read(data):
    if data[:4] != b"FORM" or data[8:12] not in (b"AIFF", b"AIFC"):
        raise ValueError("not an AIFF file")
    size = u32(data, 4)
    declared = None if size in (0, 0xFFFFFFFF) or size < 4 else 8 + size
    end = len(data) if declared is None else min(declared, len(data))
    chunks = read_chunks(data, 12, end)
    result = {"form": data[8:12].decode("latin1"), "size": size, "declared_end": declared, "end": end, "chunks": chunks}
    for c in chunks:
        if c["id"] == "COMM" and "comm" not in result:
            result["comm"] = parse_comm(data, c, result["form"] == "AIFC")
        elif c["id"] == "SSND" and "ssnd" not in result and c["length"] >= 8:
            result["ssnd"] = {"chunk": c, "offset": u32(data, c["offset"] + 8), "block": u32(data, c["offset"] + 12)}
    return result


def parse_comm(data, c, aifc):
    at = c["offset"] + 8
    if c["length"] < 18:
        return None
    comm = {"channels": u16(data, at), "frames": u32(data, at + 2), "size": u16(data, at + 6), "rate": extended(data[at + 8:at + 18]), "type": "NONE" if not aifc else None, "name": None}
    if aifc and c["length"] >= 22:
        comm["type"] = data[at + 18:at + 22].decode("latin1")
        count = data[at + 22] if c["length"] > 22 else 0
        comm["name"] = data[at + 23:at + 23 + count].decode("latin1")
    elif aifc:
        comm["type"] = "NONE"
    return comm


def tree(chunks):
    return [{"id": c["id"], "offset": c["offset"], "end": c["end"], "size": c["size"], "length": c["length"]} for c in chunks]


def manifest(data, base=0):
    aiff = read(data[base:])
    chunks = tree(aiff["chunks"])
    for c in chunks:
        c["offset"] += base
        c["end"] += base
    result = {"length": len(data), "container": {"id": "FORM", "form": aiff["form"], "size": aiff["size"], "end": aiff["end"] + base}, "chunks": chunks, "stream": None}
    comm = aiff.get("comm")
    if comm is None or not comm["channels"]:
        return result
    rate = comm["rate"]
    valid_rate = rate is not None and 1 <= rate <= 2**31 - 1
    kind = TYPES.get(comm["type"])
    codec = kind[0] if kind else "Other"
    frame = None
    if kind:
        if kind[1] is not None:
            frame = kind[1] * comm["channels"]
        elif kind[2]:
            frame = ((comm["size"] + 7) // 8) * comm["channels"]
    frames = comm["frames"]
    ssnd = aiff.get("ssnd")
    data_bytes = 0
    if ssnd is not None:
        data_bytes = max(0, ssnd["chunk"]["length"] - 8 - ssnd["offset"])
        if frame:
            held = data_bytes // frame
            if frames == 0 and held > 0:
                frames = held
            elif held < frames:
                frames = held
    length_known = True
    if frame is None:
        if comm["type"] in PACKETS:
            size, samples = PACKETS[comm["type"]]
            held = frames if ssnd is None else data_bytes // (size * comm["channels"])
            frames = min(frames, held) * samples
        else:
            frames = 0
            length_known = False
    duration = frames / rate if valid_rate else 0
    linear = bool(kind and kind[2])
    bits = None
    if linear:
        bits = kind[3] or (comm["size"] if 1 <= comm["size"] <= 32 else None)
    if duration > 0 and ssnd is not None and length_known:
        bitrate = (frames * frame * 8 if frame else data_bytes * 8) / duration / 1000
    else:
        bitrate = None
    result["stream"] = {
        "codec": codec,
        "sample_rate": round(rate) if valid_rate else 0,
        "channels": comm["channels"],
        "bits": bits,
        "samples": frames,
        "duration": duration,
        "layout": LAYOUTS.get(comm["channels"], []),
        "average_bitrate": bitrate,
        "type": comm["type"],
    }
    return result


# Builders for the hand-built fixtures.

def chunk(cid, body, pad=True, size=None):
    cid = cid if isinstance(cid, bytes) else cid.encode("latin1")
    out = cid + struct.pack(">I", len(body) if size is None else size) + body
    if pad and len(body) % 2:
        out += b"\x00"
    return out


def form(chunks, kind=b"AIFF", size=None):
    body = kind + b"".join(chunks)
    return b"FORM" + struct.pack(">I", len(body) if size is None else size) + body


def ext80(value):
    """An 80-bit extended number of a non-negative value."""
    if value == 0:
        return bytes(10)
    mantissa, exponent = math.frexp(value)
    exponent += 16382
    m = int(mantissa * 2**64)
    return struct.pack(">H", exponent) + m.to_bytes(8, "big")


def pstring(text):
    raw = text.encode("latin1")
    out = bytes([len(raw)]) + raw
    return out + (b"\0" if len(out) % 2 else b"")


def comm(channels=2, frames=50, size=16, rate=44100, kind=None, name=""):
    body = struct.pack(">HIH", channels, frames, size) + (rate if isinstance(rate, bytes) else ext80(rate))
    if kind is not None:
        body += kind.encode("latin1") + pstring(name)
    return body


def ssnd(sound, offset=0, block=0):
    return struct.pack(">II", offset, block) + sound


def pcm(rate, channels, count, bits):
    """Deterministic big-endian signed samples of ceil(bits / 8) bytes, interleaved."""
    width = (bits + 7) // 8
    out = bytearray()
    amplitude = (1 << (bits - 1)) - 1
    for i in range(count):
        for c in range(channels):
            v = round(0.4 * amplitude * math.sin(2 * math.pi * 220 * (c + 1) * i / rate)) << (width * 8 - bits)
            out += (v & ((1 << (8 * width)) - 1)).to_bytes(width, "big")
    return bytes(out)
