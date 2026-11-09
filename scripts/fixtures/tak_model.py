"""A reader for TAK files, written from takStream.pas (TAK 2.3.3's SDK, Sdk/Source/takStream.pas: the author's own source of the container) and from what the
files Takc.exe wrote show, apart from the analyzer. It works out the metadata objects of a file for the fixture scripts and, in manifest(), each fixture's manifest.

takStream.pas is read here and not copied, and nothing about TAK is read from anywhere else (docs/design/tak-optimfrog-headers.md, "Sources and licenses").
This reader is not an official release of TAK.

Where the files depart from takStream.pas (the design's list; the files win):

- An object's size counts its CRC-24: the data is the size less 3 bytes.
- Wave data has one CRC-24, at the end, over the header and footer sizes (3 bytes each) and the header and footer bytes.
- The End object (ID 0) is a header with size 0 and no CRC.
- The padding object (ID 5, "unused space") has no CRC: its size counts only its bytes, all zero (pipe encoding without -ihs, run 42719547527).
"""

import struct

CODECS = ["Integer 24 bit (TAK 1.0)", "Experimental!", "Integer 24 bit (TAK 2.0)", "LossyWav (TAK 2.1 Beta)", "Integer 24 bit MC (TAK 2.2)"]
FRAME_SIZE_QUANTS = [3, 4, 6, 8, 4096, 8192, 16384, 512, 1024, 2048]
OBJECT_NAMES = {0: "End", 1: "Stream Info", 2: "Seek Table", 3: "Wave Data", 4: "Encoder Info", 5: "Padding", 6: "MD5", 7: "Last Frame Info"}
SYNC = b"\xff\xa0"
CRC24_INIT = 0xB704CE
CRC24_POLY = 0x864CFB
SPEAKERS = ["none", "front left", "front right", "front center", "low frequency", "back left", "back right", "front left of center", "front right of center",
            "back center", "side left", "side right", "top center", "top front left", "top front center", "top front right", "top back left", "top back center",
            "top back right"]


def crc24(data):
    """CRC-24 of RFC 2440 (OpenPGP): initial value 0xB704CE, polynomial 0x864CFB, most significant bit first."""
    crc = CRC24_INIT
    for byte in data:
        crc ^= byte << 16
        for _ in range(8):
            crc <<= 1
            if crc & 0x1000000:
                crc ^= CRC24_POLY
    return crc & 0xFFFFFF


class Bits:
    """The bit structures of takStream.pas: items start at the least significant bits, bytes in order."""

    def __init__(self, data):
        self.data = data
        self.pos = 0

    def get(self, count):
        value = 0
        for i in range(count):
            at = self.pos + i
            if at // 8 >= len(self.data):
                self.overflow = True
                continue
            value |= ((self.data[at // 8] >> (at % 8)) & 1) << i
        self.pos += count
        return value

    overflow = False

    def align(self):
        rest = (-self.pos) % 8
        return self.get(rest)


def frame_size(rate, frame_type):
    """(samples, valid) by tak_str_CalcFrameSizeFromType: a time type that comes to more than 16384 samples, or a size type of more than 250 ms, is invalid."""
    if frame_type > 9:
        return 0, False
    quant = FRAME_SIZE_QUANTS[frame_type]
    if frame_type <= 3:
        size = (rate * quant) >> 5
        return size, size <= 16384
    return quant, quant <= (rate * 8) >> 5


def stream_info(bits):
    """The stream info fields (tak_str_DecodeStreamInfo) from a bit reader."""
    info = {"codec": bits.get(6), "profile": bits.get(4), "frame_size_type": bits.get(4)}
    info["samples"] = bits.get(24) | (bits.get(11) << 24)
    info["data_type"] = bits.get(3)
    info["sample_rate"] = bits.get(18) + 6000
    info["bits_per_sample"] = bits.get(5) + 8
    info["channels"] = bits.get(4) + 1
    info["has_extension"] = bool(bits.get(1))
    if info["has_extension"]:
        info["valid_bits"] = bits.get(5) + 1
        info["speakers"] = None
        if bits.get(1):
            info["speakers"] = [bits.get(6) for _ in range(info["channels"])]
    info["frame_length"], info["frame_size_valid"] = frame_size(info["sample_rate"], info["frame_size_type"])
    return info


def frame_header(data, pos):
    """The frame header at pos (tak_str_DecodeFrameHeader) as a dict with its length including the CRC-24, or None where it does not decode or its CRC-24 fails."""
    if data[pos:pos + 2] != SYNC:
        return None
    bits = Bits(data[pos:pos + 64])
    bits.get(16)
    header = {"last": bool(bits.get(1)), "info": bool(bits.get(1)), "metadata": bool(bits.get(1)), "number": bits.get(21)}
    if header["last"]:
        header["samples"] = bits.get(14) + 1
        bits.get(2)
    if header["metadata"]:
        return None
    flags = 0
    if header["info"]:
        header["stream_info"] = stream_info(bits)
        header["has_prev_distance"] = bool(bits.get(1))
        flags = bits.get(5)
        if header["has_prev_distance"]:
            header["prev_distance"] = bits.get(25)
        flags |= bits.align()
    length = (bits.pos + 7) // 8
    stored_bits = Bits(data[pos + length:pos + length + 3])
    if len(data) < pos + length + 3 or bits.overflow:
        return None
    stored = int.from_bytes(data[pos + length:pos + length + 3], "little")
    if flags != 0 or crc24(data[pos:pos + length]) != stored:
        return None
    header["length"] = length + 3
    return header


def walk(data, start=0):
    """The objects after the stream ID at start: dicts {id, offset, reserved, size, end, crc_ok (None where the object has no CRC)} up to the End object or the
    first frame whose header CRC-24 holds, and the offset where the audio starts (None where the data ends first)."""
    objects = []
    pos = start + 4
    audio = None
    while pos + 4 <= len(data):
        if frame_header(data, pos) is not None:
            audio = pos
            break
        header = struct.unpack_from("<I", data, pos)[0]
        object_id, reserved, size = header & 127, (header >> 7) & 1, header >> 8
        entry = {"id": object_id, "offset": pos, "reserved": reserved, "size": size, "end": pos + 4 + size, "crc_ok": None}
        if object_id == 0:
            entry["end"] = pos + 4
            objects.append(entry)
            audio = pos + 4
            break
        data_end = pos + 4 + size
        if data_end > len(data):
            entry["cut"] = True
            objects.append(entry)
            pos = len(data)
            break
        if object_id in (1, 2, 3, 4, 6, 7) and size >= 3:
            stored = int.from_bytes(data[data_end - 3:data_end], "little")
            entry["crc"] = stored
            entry["crc_ok"] = crc24(data[pos + 4:data_end - 3]) == stored
        objects.append(entry)
        pos = data_end
    return objects, audio


def last_frame(data, entry, audio):
    pos_value = int.from_bytes(data[entry["offset"] + 4:entry["offset"] + 9], "little")
    size = int.from_bytes(data[entry["offset"] + 9:entry["offset"] + 12], "little")
    return {"position": pos_value, "size": size, "end": None if audio is None else audio + pos_value + size}


def riff_chunks(data, start, end, skip_header=True):
    position = start + 12 if skip_header and data[start:start + 4] in (b"RIFF", b"RF64", b"BW64") else start
    chunks = []
    while len(chunks) < 64 and end - position >= 8:
        chunk_id = data[position:position + 4].decode("latin-1")
        size = struct.unpack_from("<I", data, position + 4)[0]
        chunks.append({"id": chunk_id, "size": size, "offset": position})
        if position + 8 + size > end or chunk_id == "data":
            break
        position += 8 + size + (size & 1)
    return chunks


def preset_name(preset, evaluation):
    return f"-p{preset}" + {0: "", 1: "e", 2: "m"}.get(evaluation, f"?{evaluation}")


def manifest(data, extra=None):
    """The manifest of a fixture: the objects, the stream info, the encoder info, the wave data, the last frame info, the end of the stream and the
    findings the analyzer must raise, all worked out here from the bytes."""
    result = {"length": len(data)}
    findings = []
    if data[:4] != b"tBaK":
        result["findings"] = [{"code": "tak.no-signature"}]
        return result
    objects, audio = walk(data)
    result["objects"] = [dict(o) for o in objects]
    result["audio_start"] = audio
    if audio is None:
        findings.append({"code": "tak.metadata-cut"})
    elif not objects or objects[-1]["id"] != 0:
        findings.append({"code": "tak.end-missing"})
    by_id = {}
    for o in objects:
        by_id.setdefault(o["id"], o)
    result["stream_info_first"] = bool(objects) and objects[0]["id"] == 1
    if objects and objects[0]["id"] != 1:
        findings.append({"code": "tak.stream-info-not-first"})
    info = by_id.get(1)
    stream = None
    if info is not None and not info.get("cut") and info["size"] >= 13:
        stream = stream_info(Bits(data[info["offset"] + 4:info["end"] - 3]))
        result["stream_info"] = stream
    encoder = by_id.get(4)
    enc = None
    if encoder is not None and not encoder.get("cut") and encoder["size"] == 7:
        raw = int.from_bytes(data[encoder["offset"] + 4:encoder["offset"] + 7], "little")
        byte3 = data[encoder["offset"] + 7]
        enc = {"version": f"{(raw >> 16) & 255}.{(raw >> 8) & 255}.{raw & 255}", "preset": byte3 & 15, "evaluation": (byte3 >> 4) & 3}
        enc["name"] = preset_name(enc["preset"], enc["evaluation"])
    result["encoder_info"] = enc
    wave = by_id.get(3)
    result["wave_data"] = None
    if wave is not None and not wave.get("cut") and wave["size"] >= 9:
        head = int.from_bytes(data[wave["offset"] + 4:wave["offset"] + 7], "little")
        tail = int.from_bytes(data[wave["offset"] + 7:wave["offset"] + 10], "little")
        base = wave["offset"] + 10
        result["wave_data"] = {"header_size": head, "footer_size": tail, "size_fits": wave["size"] == 6 + head + tail + 3,
                               "header_chunks": [c["id"] for c in riff_chunks(data, base, base + head)] if data[base:base + 4] in (b"RIFF", b"RF64", b"BW64") else [],
                               "footer_chunks": [c["id"] for c in riff_chunks(data, base + head, base + head + tail, skip_header=False)] if tail else []}
    md5 = by_id.get(6)
    result["md5"] = data[md5["offset"] + 4:md5["offset"] + 20].hex() if md5 is not None and md5["size"] == 19 and not md5.get("cut") else None
    last = by_id.get(7)
    result["last_frame"] = None
    if last is not None and last["size"] == 11 and not last.get("cut"):
        result["last_frame"] = last_frame(data, last, audio)
        result["last_frame"]["crc_ok"] = last["crc_ok"]
    failed = sum(1 for o in objects if o["crc_ok"] is False)
    compared = sum(1 for o in objects if o["crc_ok"] is not None)
    result["checksums"] = {"compared": compared, "failed": failed}
    if failed:
        findings.append({"code": "tak.object-crc-mismatch"})
    first = frame_header(data, audio) if audio is not None else None
    result["first_frame"] = None if first is None else {"info": first["info"], "last": first["last"], "length": first["length"]}
    end = len(data)
    if result["last_frame"] and result["last_frame"]["crc_ok"] and result["last_frame"]["end"] is not None:
        end = result["last_frame"]["end"]
        if end > len(data):
            findings.append({"code": "tak.audio-cut"})
    result["end"] = min(end, len(data))
    result["declared_end"] = end if result["last_frame"] and result["last_frame"]["crc_ok"] else None
    if stream is not None:
        rate = stream["sample_rate"]
        result["summary"] = {"codec": stream["codec"], "codec_name": CODECS[stream["codec"]] if stream["codec"] < len(CODECS) else None, "profile": stream["profile"],
                             "frame_length": stream["frame_length"], "sample_rate": rate, "channels": stream["channels"], "bits_per_sample": stream["bits_per_sample"],
                             "samples": stream["samples"], "encoder": f"TAK {enc['version']}" if enc else "TAK 1.0",
                             "preset": enc["name"] if enc else f"-p{stream['profile']}"}
    result["findings"] = findings
    if extra:
        result.update(extra)
    return result
