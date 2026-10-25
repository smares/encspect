"""An independent reading of RIFF/WAVE files, for the manifests of the WAV fixtures and their cross-check.

Written from Microsoft's "Multimedia Programming Interface and Data Specifications 1.0" (RIFF, WAVE, LIST/INFO, cue, plst, adtl), the
"Multimedia Data Standards Update" of April 1994 (fact, inst, smpl, the extra fields of the WAVE types), mmreg.h and ksmedia.h (format tags,
sub-format GUIDs and the channel mask), the sub-format GUIDs of ksuuids.h and mfapi.h that Microsoft's "Multiple channel audio data and WAVE files" and
"Audio Subtype GUIDs" name, EBU Tech 3285 (bext) and Tech 3306-2009 (RF64, ds64), ITU-R BS.2088 (BW64, ds64, chna, axml), apart from the analyzer: the chunk tree, the fmt
fields and the figures of the stream that a manifest states come from here. The signal generators and the byte builders the fixture scripts use
live here too.

    read(data)          the chunk tree of a file: {"magic", "size", "form", "end", "chunks": [...], "ds64", ...}; a Wave64 file (the layout
                        the MultimediaWiki gives and FFmpeg's w64 files show) is read by read_w64 with the same result
    manifest(data)      what the analyzer must report about a file that is well formed enough to have a stream: the length, the chunks, the stream
"""

import math
import struct
import uuid

SPEAKERS = [
    "front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center",
    "side left", "side right", "top center", "top front left", "top front center", "top front right", "top back left", "top back center", "top back right",
]

BASE_GUID = bytes.fromhex("00001000" "800000aa00389b71")  # the twelve bytes after the format tag in a sub-format GUID (ksmedia.h)

LINEAR = (0x0001, 0x0003, 0x0006, 0x0007)
ROUTED = {0x0050: "mpeg-audio", 0x0055: "mpeg-audio", 0x0008: "dts", 0x2000: "ac-3", 0x2001: "dts"}
# Sub-format GUIDs that are no format tag but are named in Microsoft's headers (as stored in the file: the first three groups little-endian):
# MEDIASUBTYPE_DOLBY_AC3 and MEDIASUBTYPE_MPEG2_AUDIO (ksuuids.h), MFAudioFormat_Dolby_DDPlus (mfapi.h).
NAMED_GUIDS = {
    uuid.UUID("e06d802c-db46-11cf-b4d1-00805f6cbbea").bytes_le: "ac-3",
    uuid.UUID("a7fb87af-2d02-42fb-a4d4-05cd93843bdd").bytes_le: "e-ac-3",
    uuid.UUID("e06d802b-db46-11cf-b4d1-00805f6cbbea").bytes_le: "mpeg-audio",
}
LIST_TYPES = (b"INFO", b"adtl", b"wavl")


def u16(data, at, big=False):
    return struct.unpack_from(">H" if big else "<H", data, at)[0]


def u32(data, at, big=False):
    return struct.unpack_from(">I" if big else "<I", data, at)[0]


def u64(data, at):
    return struct.unpack_from("<Q", data, at)[0]


def is_id(raw):
    return len(raw) == 4 and raw[0] != 0x20 and all(0x20 <= b <= 0x7E for b in raw)


def read_chunks(data, at, end, big, ds64=None, top=False, rf64=False, depth=0):
    """The chunks in [at, end): each {"id", "offset", "size" (as stated, resolved through ds64), "length" (the bytes the file holds), "pad", "children"?}.
    Reading stops at a chunk that runs past the end, a chunk without a plausible ID, and a data chunk whose size is a placeholder."""
    chunks = []
    stop = at
    while at + 8 <= end and len(chunks) < 2048:
        raw = data[at:at + 4]
        if not is_id(raw):
            break
        stated = u32(data, at + 4, big)
        cid = raw.decode("latin1")
        body = at + 8
        resolved = stated
        if rf64 and top and stated == 0xFFFFFFFF and ds64 is not None:
            resolved = ds64["data_size"] if cid == "data" else ds64["table"].get(cid, stated)
        remaining = end - body
        placeholder = cid == "data" and ((stated == 0xFFFFFFFF and resolved == stated) or (stated == 0 and remaining > 0 and not (body + 8 <= end and is_id(data[body:body + 4]))))
        if placeholder:
            length = remaining
        else:
            length = min(resolved, remaining)
        chunk = {"id": cid, "offset": at, "size": resolved, "length": length, "stated": stated, "placeholder": placeholder, "pad": False}
        body_end = body + length
        nxt = body_end
        if length % 2 == 1 and resolved <= remaining and not placeholder:
            padded = body_end + 1 + 8 <= end and is_id(data[body_end + 1:body_end + 5])
            unpadded = body_end + 8 <= end and is_id(data[body_end:body_end + 4])
            if body_end < end and not (not padded and unpadded):
                chunk["pad"] = True
                nxt = body_end + 1
        chunk["end"] = nxt
        if cid == "LIST" and length >= 4 and depth < 8:
            kind = data[body:body + 4]
            chunk["list_type"] = kind.decode("latin1")
            chunk["children"], _ = read_chunks(data, body + 4, body_end, big, depth=depth + 1)
        chunks.append(chunk)
        stop = nxt
        if resolved > remaining or placeholder:
            break
        at = nxt
    return chunks, stop


W64_RIFF = uuid.UUID("66666972-912E-11CF-A5D6-28DB04C10000").bytes_le
W64_WAVE = uuid.UUID("65766177-ACF3-11D3-8CD1-00C04F8EDB8A").bytes_le
W64_LIST = uuid.UUID("7473696C-912F-11CF-A5D6-28DB04C10000").bytes_le
W64_TAIL = uuid.UUID("00000000-ACF3-11D3-8CD1-00C04F8EDB8A").bytes_le[4:]


def w64_guid(guid):
    """A chunk GUID as the analyzer shows it: the text of the GUID and, in brackets, the four characters that name it."""
    text = str(uuid.UUID(bytes_le=bytes(guid)))
    if guid == W64_RIFF:
        return f"{text} (riff)"
    if guid == W64_WAVE:
        return f"{text} (wave)"
    if guid == W64_LIST:
        return f"{text} (LIST)"
    if guid[4:] == W64_TAIL and is_id(guid[:4]):
        return f"{text} ({guid[:4].decode('latin1')})"
    return text


def w64_key(guid):
    """The RIFF ID a Wave64 chunk stands for: fmt, fact, data and the other four-character names, LIST, or the GUID text."""
    if guid == W64_LIST:
        return "LIST"
    if guid[4:] == W64_TAIL and is_id(guid[:4]):
        return guid[:4].decode("latin1")
    return str(uuid.UUID(bytes_le=bytes(guid)))


def read_w64(data):
    """A Wave64 file: the riff GUID, the size of the whole file, the wave GUID, then chunks of a GUID, a 64-bit size that counts these 24 bytes and
    the data, each padded to a multiple of 8 bytes from the start of the file."""
    if data[:16] != W64_RIFF or data[24:40] != W64_WAVE:
        raise ValueError("not a Wave64 file")
    size = u64(data, 16)
    placeholder = size < 40 or size > len(data) * 4 + (1 << 40)
    end = len(data) if placeholder else min(size, len(data))
    chunks = []
    at = 40
    stop = at
    while at + 24 <= end and len(chunks) < 2048:
        stated = u64(data, at + 16)
        if stated < 24:
            break
        past = stated > end - at
        length = end - at - 24 if past else stated - 24
        nxt = end if past else min(at + ((stated + 7) & ~7), end)
        guid = data[at:at + 16]
        chunks.append({"id": w64_guid(guid), "key": w64_key(guid), "offset": at, "end": nxt, "size": stated - 24, "length": length, "placeholder": False, "stated": stated})
        stop = nxt
        if past:
            break
        at = nxt
    result = {"magic": "Wave64", "size": size, "form": "WAVE", "big": False, "end": end, "placeholder": placeholder, "declared_end": None if placeholder else size,
              "chunks": chunks, "ds64": None, "stop": stop}
    for chunk in chunks:
        key = chunk["key"]
        if key == "fmt " and "fmt" not in result:
            result["fmt"] = parse_fmt(data, {"offset": chunk["offset"] + 16, "length": chunk["length"]}, False)
        elif key == "fact" and "fact" not in result and chunk["length"] >= 4:
            result["fact"] = u32(data, chunk["offset"] + 24)
        elif key == "data" and "data" not in result:
            result["data"] = {**chunk, "offset": chunk["offset"] + 16}
    return result


def read(data):
    """The RIFF chunk of a file: its header, the chunks, and the figures of the fmt, fact, data and ds64 chunks."""
    if data[:16] == W64_RIFF:
        return read_w64(data)
    magic = data[:4].decode("latin1")
    if magic not in ("RIFF", "RIFX", "RF64", "BW64") or data[8:12] != b"WAVE":
        raise ValueError("not a WAVE file")
    big = magic == "RIFX"
    size = u32(data, 4, big)
    ds64 = None
    if magic in ("RF64", "BW64") and data[12:16] == b"ds64" and u32(data, 16) >= 24:
        table = {}
        count = u32(data, 12 + 8 + 24) if u32(data, 16) >= 28 else 0
        for i in range(min(count, (u32(data, 16) - 28) // 12, 1024)):
            entry = 12 + 8 + 28 + 12 * i
            if is_id(data[entry:entry + 4]):
                table.setdefault(data[entry:entry + 4].decode("latin1"), u32(data, entry + 4) | (u32(data, entry + 8) << 32))
        ds64 = {"riff_size": u64(data, 20), "data_size": u64(data, 28), "sample_count": u64(data, 36), "table": table}
    placeholder = False
    if magic in ("RF64", "BW64") and size == 0xFFFFFFFF:
        total = ds64["riff_size"] if ds64 else None
    elif size in (0, 0xFFFFFFFF) or size < 4:
        total = None
    else:
        total = size
    if total is None:
        placeholder = True
        end = len(data)
    else:
        end = min(8 + total, len(data))
    chunks, stop = read_chunks(data, 12, end, big, ds64, True, magic in ("RF64", "BW64"))
    result = {"magic": magic, "size": size, "form": "WAVE", "big": big, "end": end, "placeholder": placeholder, "declared_end": None if total is None else 8 + total, "chunks": chunks, "ds64": ds64, "stop": stop}
    for chunk in chunks:
        if chunk["id"] == "fmt " and "fmt" not in result:
            result["fmt"] = parse_fmt(data, chunk, big)
        elif chunk["id"] == "fact" and "fact" not in result and chunk["length"] >= 4:
            samples = u32(data, chunk["offset"] + 8, big)
            result["fact"] = ds64["sample_count"] if magic == "RF64" and samples == 0xFFFFFFFF and ds64 else samples
        elif chunk["id"] == "data" and "data" not in result:
            result["data"] = chunk
    return result


def parse_fmt(data, chunk, big):
    at = chunk["offset"] + 8
    length = chunk["length"]
    if length < 14:
        return None
    # The common fields of WAVEFORMAT are 14 bytes; wBitsPerSample follows them in a chunk of 16.
    fmt = {"tag": u16(data, at, big), "channels": u16(data, at + 2, big), "rate": u32(data, at + 4, big), "average": u32(data, at + 8, big),
           "align": u16(data, at + 12, big), "bits": u16(data, at + 14, big) if length >= 16 else 0, "short": length < 16, "valid": None, "mask": None,
           "coding": None, "named": None}
    fmt["coding"] = fmt["tag"]
    if length >= 18:
        fmt["cb"] = u16(data, at + 16, big)
        if fmt["tag"] == 0xFFFE and fmt["cb"] >= 22 and length >= 40:
            fmt["valid"] = u16(data, at + 18, big)
            fmt["mask"] = u32(data, at + 20, big)
            guid = data[at + 24:at + 40]
            if guid[4:] == BASE_GUID:
                fmt["coding"] = struct.unpack("<I", guid[:4])[0]
            else:
                fmt["named"] = NAMED_GUIDS.get(guid)
    return fmt


def layout(fmt):
    """The speakers the stream's channels feed, as the analyzer words them."""
    channels = fmt["channels"]
    mask = fmt["mask"]
    if not 1 <= channels <= 64:
        return []
    if fmt["tag"] == 0xFFFE and mask:
        # Microsoft, "Multiple channel audio data and WAVE files": bits past the channels are ignored, channels past the bits have no speaker.
        names = [SPEAKERS[bit] if bit < 18 else "discrete" for bit in range(32) if mask >> bit & 1][:channels]
        return names + ["discrete"] * (channels - len(names))
    return {1: ["front center"], 2: ["front left", "front right"]}.get(channels, [])


CODECS = {0x0001: "PCM", 0x0003: "PCM (floating-point)", 0x0006: "A-law", 0x0007: "Mu-law"}


def data_info(riff):
    """The first data chunk in document order and the bytes and silent samples of the audio it stands for: a data chunk of a wavl list is followed by the
    other data chunks and the silence of the list, which add to the audio."""
    first = None
    extra = 0
    silent = 0
    def walk(chunks, in_wavl):
        nonlocal first, extra, silent
        for c in chunks:
            if c.get("key", c["id"]) == "data":
                if first is None:
                    first = (c, in_wavl)
                elif in_wavl and first[1]:
                    extra += c["length"]
            elif c["id"] == "slnt" and in_wavl and c["length"] >= 4:
                silent += u32(riff["_data"], c["offset"] + 8)
            elif c["id"] == "LIST" and c.get("list_type") == "wavl":
                walk(c["children"], True)
    walk(riff["chunks"], False)
    return (first[0] if first else None), extra, silent


# IEC 61937 (S/PDIF passthrough) bursts in 16-bit stereo samples, from the freely available previews of IEC 61937-1:2021 (the burst-preamble, 6.1.6 to
# 6.1.8), IEC 61937-2:2021 (Table 2, the data types) and IEC 61937-3:2017 (AC-3 and E-AC-3).
IEC_SYNC = b"\x72\xf8\x1f\x4e"  # Pa = F872h, Pb = 4E1Fh as the 16-bit samples they are, little-endian
# data type (bits 0 to 6 of Pc): (name, repetition period in IEC 60958 frames, unit of Pd, codec route)
IEC_TYPES = {
    1: ("AC-3", 1536, "bits", "ac-3"), 4: ("MPEG-1 Layer I", 384, "bits", None),
    5: ("MPEG-1 Layer II or III, or MPEG-2 without extension", 1152, "bits", None), 6: ("MPEG-2 with extension", 1152, "bits", None),
    7: ("MPEG-2 AAC", 1024, "bits", None), 8: ("MPEG-2 Layer I low sampling frequency", 768, "bits", None),
    9: ("MPEG-2 Layer II low sampling frequency", 2304, "bits", None), 10: ("MPEG-2 Layer III low sampling frequency", 1152, "bits", None),
    11: ("DTS type I", 512, "bits", "dts"), 12: ("DTS type II", 1024, "bits", "dts"), 13: ("DTS type III", 2048, "bits", "dts"),
    14: ("ATRAC", 512, "bits", None), 15: ("ATRAC 2/3", 1024, "bits", None), 16: ("ATRAC-X", 2048, "bits", None),
    48: ("ATRAC-X low latency", 512, "bits", None), 80: ("ATRAC-X low latency", 256, "bits", None), 112: ("ATRAC-X low latency", 128, "bits", None),
    18: ("WMA Professional type I", 2048, "bits", None), 50: ("WMA Professional type II", 2048, "bits", None),
    82: ("WMA Professional type III", 1024, "bits", None), 114: ("WMA Professional type IV", 512, "bits", None),
    19: ("MPEG-2 AAC low sampling frequency", 2048, "bits", None), 51: ("MPEG-2 AAC low sampling frequency", 4096, "bits", None),
    20: ("MPEG-4 AAC", 1024, "bits", None), 52: ("MPEG-4 AAC", 2048, "bits", None), 84: ("MPEG-4 AAC", 4096, "bits", None),
    116: ("MPEG-4 AAC", 512, "bits", None), 21: ("E-AC-3", 6144, "bytes", "e-ac-3"), 22: ("MAT", 15360, "bytes", None),
}
IEC_CHAIN = 3


def iec_header(data, at):
    """(pc, pd) of the burst preamble at at, or None: the sync words are there and the data type is one of the table, null data (0) or pause (3)."""
    if at + 8 > len(data) or data[at:at + 4] != IEC_SYNC:
        return None
    pc, pd = struct.unpack_from("<HH", data, at + 4)
    return (pc, pd) if (pc & 0x7F) in IEC_TYPES or (pc & 0x7F) in (0, 3) else None


def iec_length(kind, pd, unit):
    return {"bits": (pd + 7) // 8, "bytes": pd, "8bytes": pd * 8}[unit]


def iec_chain(data):
    """The chain at the start of the data: after the zero frames (at most 1 MiB of them), IEC_CHAIN bursts of one audio data type and bitstream number at
    the repetition period, each with a payload that fits the period and the data. (first, data type) or None."""
    at = 0
    limit = min(len(data), 1 << 20)
    while at < limit and data[at:at + 4] == b"\0\0\0\0":
        at += 4
    first = iec_header(data, at)
    if first is None or (first[0] & 0x7F) not in IEC_TYPES:
        return None
    kind = first[0] & 0x7F
    name, repetition, unit, codec = IEC_TYPES[kind]
    period = repetition * 4
    for i in range(IEC_CHAIN):
        header = iec_header(data, at + i * period)
        if header is None or (header[0] & 0x7F) != kind or header[0] >> 13 != first[0] >> 13:
            return None
        length = iec_length(kind, header[1], unit)
        if length == 0 or length > period - 8 or at + i * period + 8 + length > len(data):
            return None
    return at, kind


def iec_walk(data, first, kind):
    """The bursts of the chain's data type and bitstream number, and the pause and null bursts, from the first one: each is (offset, header, length of its
    payload read), found at the repetition period; where none starts, the next valid preamble is looked for, and a burst of another type is skipped.
    Returns {"bursts": [...], "pauses": n, "findings": {code: count}}."""
    name, repetition, unit, codec = IEC_TYPES[kind]
    period = repetition * 4
    bitstream = iec_header(data, first)[0] >> 13
    found = {"bursts": [], "pauses": 0, "findings": {}, "payload_bytes": 0, "nodes": []}

    def note(code):
        found["findings"][code] = found["findings"].get(code, 0) + 1

    def next_header(frm):
        at = frm + (-(frm) % 4)
        while at + 8 <= len(data):
            if data[at:at + 2] == b"\x72\xf8" and iec_header(data, at):
                return at
            at += 4
        return None

    position = first
    expected = first
    search = first
    while True:
        header = iec_header(data, position)
        if header is None:
            nxt = next_header(search)
            if nxt is None:
                if any(data[search:]):
                    note("wav.iec61937-data-after-bursts")
                return found
            if expected >= 0:
                note("wav.iec61937-burst-spacing" if nxt < expected else "wav.iec61937-burst-missing")
            position, expected = nxt, -1
            continue
        pc, pd = header
        found["nodes"].append(position)
        if (pc & 0x7F) != kind or pc >> 13 != bitstream:
            if (pc & 0x7F) in (0, 3):
                found["pauses"] += 1
            else:
                note("wav.iec61937-type-changed")
            search = position + 8
            position = search
            expected = -1
            continue
        length = iec_length(kind, pd, unit)
        room = period - 8
        start = position + 8
        too_long = length > room
        cut = start + min(length, room) > len(data)
        payload = min(length, room, len(data) - start)
        if too_long or cut or payload == 0:
            note("wav.iec61937-length-invalid")
        if pc & 0x80:
            note("wav.iec61937-error-flag")
        found["bursts"].append((position, header, payload))
        found["payload_bytes"] += payload
        expected = position + period
        search = start + (-(-payload // 4) * 4)
        position = expected


def shift(items, base):
    for item in items:
        item["offset"] += base
        item["end"] += base
        if "children" in item:
            shift(item["children"], base)


def manifest(data, extra_codec=None, base=0, carried=None):
    """The manifest of a file with a stream: the chunks and, for the codings that store samples one after another (and others with a fact chunk), the
    figures of the stream. carried is (codec, sample rate, channels) of a stream the PCM samples hold (a DTS stream, as on a DTS music CD), whose
    figures are the codec parser's and are stated by the script that built the file."""
    riff = read(data[base:])
    riff["_data"] = data[base:]
    result = {"length": len(data), "container": {"id": riff["magic"], "size": riff["size"], "end": riff["end"] + base}, "chunks": tree(riff["chunks"]), "stream": None}
    shift(result["chunks"], base)
    fmt = riff.get("fmt")
    chunk, wavl_bytes, silent = data_info(riff)
    if fmt is None or chunk is None or not fmt["channels"] or not fmt["rate"]:
        return result
    coding = fmt["coding"]
    routed = fmt["named"] or ROUTED.get(coding)
    if coding in (1, 0x92) and not riff["big"] and (coding == 0x92 or (fmt["bits"] == 16 and fmt["channels"] == 2)):
        start = base + chunk["offset"] + 8
        body = data[start:start + chunk["length"]]
        chain = iec_chain(body)
        if chain is not None:
            name, repetition, unit, codec = IEC_TYPES[chain[1]]
            walked = iec_walk(body, *chain)
            bursts = walked["bursts"]
            result["iec61937"] = {"first": start + chain[0], "data_type": chain[1], "name": name, "bursts": len(bursts), "pauses": walked["pauses"],
                                  "listed": [start + o for o in walked["nodes"][:1024]], "findings": walked["findings"],
                                  "duration": len(bursts) * repetition / fmt["rate"], "link_rate": fmt["rate"]}
            if codec is not None:
                result["stream"] = {"routed": codec, "carried": True, "sample_rate": carried[1] if carried else 0, "channels": carried[2] if carried else 0}
            else:
                duration = len(bursts) * repetition / fmt["rate"]
                result["stream"] = {"codec": name, "sample_rate": 0, "channels": 0, "bits": None, "samples": 0, "duration": duration, "layout": [],
                                    "average_bitrate": walked["payload_bytes"] * 8 / duration / 1000 if duration > 0 else None}
            return result
    if carried is not None:
        result["stream"] = {"routed": carried[0], "carried": True, "sample_rate": carried[1], "channels": carried[2]}
        return result
    if routed:
        # The codec parser describes the audio (its samples include the padding of the last frame), so only what the fmt chunk states is expected.
        result["stream"] = {"routed": routed, "sample_rate": fmt["rate"], "channels": fmt["channels"]}
        return result
    if coding in LINEAR:
        align = fmt["align"] or fmt["channels"] * ((fmt["bits"] + 7) // 8)
        samples = (chunk["length"] + wavl_bytes) // align + silent
        bits = (fmt["valid"] if fmt["tag"] == 0xFFFE and fmt["valid"] and fmt["valid"] <= fmt["bits"] else fmt["bits"]) or None
        codec = CODECS[coding]
    else:
        samples = riff.get("fact", 0)
        bits = None
        codec = extra_codec
    duration = samples / fmt["rate"]
    result["stream"] = {
        "codec": codec,
        "sample_rate": fmt["rate"],
        "channels": fmt["channels"],
        "bits": bits,
        "samples": samples,
        "duration": duration,
        "layout": layout(fmt),
        "average_bitrate": (chunk["length"] + wavl_bytes) * 8 / duration / 1000 if duration > 0 else (fmt["average"] * 8 / 1000 if fmt["average"] else None),
    }
    return result


def tree(chunks):
    items = []
    for chunk in chunks:
        item = {"id": chunk["id"], "offset": chunk["offset"], "end": chunk["end"], "size": chunk["size"], "length": chunk["length"]}
        if "children" in chunk:
            item["list_type"] = chunk["list_type"]
            item["children"] = tree(chunk["children"])
        items.append(item)
    return items


# Builders for the hand-built fixtures.

def chunk(cid, body, pad=True, size=None):
    """A chunk: the ID, the size (the body's unless given), the body and, if its size is odd, the pad byte."""
    cid = cid if isinstance(cid, bytes) else cid.encode("latin1")
    out = cid + struct.pack("<I", len(body) if size is None else size) + body
    if pad and len(body) % 2:
        out += b"\x00"
    return out


def riff(chunks, magic=b"RIFF", size=None, form=b"WAVE", big=False):
    body = form + b"".join(chunks)
    fmt = ">I" if big else "<I"
    return magic + struct.pack(fmt, len(body) if size is None else size) + body


def fmt_body(tag=1, channels=2, rate=44100, bits=16, align=None, average=None, extra=None):
    align = (channels * ((bits + 7) // 8)) if align is None else align
    average = rate * align if average is None else average
    body = struct.pack("<HHIIHH", tag, channels, rate, average, align, bits)
    if extra is not None:
        body += struct.pack("<H", len(extra)) + extra
    return body


def extensible(channels=2, rate=44100, bits=16, valid=None, mask=3, coding=1, container=None, align=None):
    guid = struct.pack("<I", coding) + BASE_GUID
    extra = struct.pack("<HI", bits if valid is None else valid, mask) + guid
    return fmt_body(0xFFFE, channels, rate, container or bits, align, None, extra)


def pcm(kind, rate, channels, count, bits, seed=0):
    """Deterministic samples as little-endian signed integers of ceil(bits / 8) bytes (unsigned for 8 bits), interleaved."""
    width = (bits + 7) // 8
    out = bytearray()
    amplitude = (1 << (bits - 1)) - 1
    state = 12345 + seed
    for i in range(count):
        for c in range(channels):
            if kind == "silent":
                v = 0
            elif kind == "noise":
                state = (state * 1103515245 + 12345) & 0x7FFFFFFF
                v = (state >> 8) % (2 * amplitude + 1) - amplitude
            else:
                v = round(0.4 * amplitude * math.sin(2 * math.pi * (220 * (c + 1)) * i / rate))
            if bits == 8:
                out.append((v >> 0) + 128 & 0xFF if kind != "silent" else 128)
            else:
                v = v << (width * 8 - bits)
                out += (v & ((1 << (8 * width)) - 1)).to_bytes(width, "little")
    return bytes(out)
