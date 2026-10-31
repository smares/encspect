"""A second reading of Monkey's Audio files, written from the SDK 13.44's description of the format (MACLib.h, APEHeader.cpp, APECompressCreate.cpp,
APEDecompress.cpp) and not from the analyzer, and the writer that assembles a file from its parts, which the hand-built fixtures use.

A file is a descriptor, a header, a seek table, the WAV header data, the frames and the terminating data. The frames are one stream of bytes that the
encoder writes in 32-bit words with the bytes of each word reversed (BitArray.cpp, APE_PUTC; APECompressCreate.cpp, FixupFrame), and the seek table says where in
that stream, counted in the file, every frame starts. parse() cuts the stream into frames; build() puts frames back into words, writes the seek table and
the MD5, so a frame that is changed or removed gives a file whose structure is right and only the changed thing differs.
"""

import hashlib
import json
import struct

DESCRIPTOR = 52
HEADER = 24


def u16(data, offset):
    return struct.unpack_from("<H", data, offset)[0]


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def find_descriptor(data):
    """Where the descriptor starts: the first 'MAC ' or 'MACF' in the first MiB (APEHeader.cpp, FindDescriptor), or -1."""
    best = -1
    for tag in (b"MAC ", b"MACF"):
        at = data.find(tag, 0, (1 << 20) + 4)
        if at >= 0 and (best < 0 or at < best):
            best = at
    return best


def stream_offset(start, index):
    """The file offset of the byte at index of the stream that begins at start: the bytes of every aligned word are reversed."""
    return start + ((index >> 2) << 2) + 3 - (index & 3)


def ranges(start, first, last):
    """The file ranges [(offset, length)] that hold stream bytes [first, last), in file order, merged where they touch."""
    out = []
    if last <= first:
        return out
    for word in range(first >> 2, ((last - 1) >> 2) + 1):
        low = max(first, word << 2) & 3
        high = (min(last, (word + 1) << 2) - 1) & 3
        offset, length = start + (word << 2) + 3 - high, high - low + 1
        if out and out[-1][0] + out[-1][1] == offset:
            out[-1] = (out[-1][0], out[-1][1] + length)
        else:
            out.append((offset, length))
    return out


def extent(rs):
    """(offset, length) of a node over the ranges."""
    return (rs[0][0], rs[-1][0] + rs[-1][1] - rs[0][0]) if rs else (0, 0)


def stream_bytes(data, start, first, last):
    return bytes(data[stream_offset(start, j)] for j in range(first, last) if stream_offset(start, j) < len(data))


SPEAKERS = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center",
            "side left", "side right", "top center", "top front left", "top front center", "top front right", "top back left", "top back center",
            "top back right"]


def mask_layout(mask, channels):
    """The speakers of a WAVE channel mask for channels: the set bits in order (bits from 18 up have no position), cut or filled to the count (Microsoft,
    "Multiple channel audio data and WAVE files", "Details about dwChannelMask")."""
    names = [SPEAKERS[bit] if bit < 18 else "discrete" for bit in range(32) if mask >> bit & 1]
    return (names + ["discrete"] * channels)[:channels]


W64_RIFF = bytes.fromhex("726966662E91CF11A5D628DB04C10000")
W64_WAVE = bytes.fromhex("77617665F3ACD3118CD100C04F8EDB8A")
W64_TEXT_TAIL = bytes.fromhex("F3ACD3118CD100C04F8EDB8A")


def wav_header(header_data, channels):
    """What the analyzer reads of the stored header data, worked out from the WAVE chunk layout (Multimedia Programming Interface and Data Specifications
    1.0, "WAVE Format Chunk"; Microsoft, WAVEFORMATEXTENSIBLE; for Wave64 the layout of docs/formats/wav.md and the SDK's CW64InputSource): the layout the
    channel mask gives (None without a nonzero mask) and the findings about the header data as [{"code", "contains"}]. RIFF, RF64 and BW64 headers
    are walked by 32-bit sizes (the ds64 chunk is one more chunk before the fmt chunk), Wave64 by GUIDs and 64-bit sizes that count the chunk's
    24 bytes of header, chunks aligned to 8 bytes."""
    data = header_data
    is_w64 = len(data) >= 40 and data[:16] == W64_RIFF and data[24:40] == W64_WAVE
    if not is_w64 and (len(data) < 12 or data[:4] not in (b"RIFF", b"RF64", b"BW64") or data[8:12] != b"WAVE"):
        return None, []
    found = []

    def problem(contains):
        found.append({"code": "monkeys-audio.wav-header-damaged", "contains": contains})

    position = 40 if is_w64 else 12
    head = 24 if is_w64 else 8
    fmt = None
    for _ in range(64):
        if position >= len(data):
            problem("holds no fmt chunk before its end")
            break
        if len(data) - position < head:
            problem("ends inside a chunk header")
            break
        if is_w64:
            guid, size = data[position:position + 16], int.from_bytes(data[position + 16:position + 24], "little")
            if size < 24:
                problem("under 24")
                break
            name = guid[:4] if guid[4:] == W64_TEXT_TAIL and all(0x20 <= c <= 0x7E for c in guid[:4]) else None
            size -= 24
        else:
            name, size = data[position:position + 4], u32(data, position + 4)
        body = position + head
        if name == b"data":
            problem("holds no fmt chunk before its data chunk")
            break
        if name == b"fmt ":
            fmt = (position, body, size)
            break
        if body + size > len(data):
            problem(f"ends inside the {name.decode('latin-1').strip()} chunk" if name else "ends inside the")
            break
        position = body + size + (-(size + 24) % 8 if is_w64 else size & 1)
    else:
        problem("more than 64 chunks")
    if fmt is None:
        return None, found
    _, body, size = fmt
    held = min(size, len(data) - body)
    if size > held:
        problem("ends inside the fmt chunk")
    elif size < 16:
        problem("fewer than the 16 of the common fields")
    if held < 4:
        return None, found
    tag, count = u16(data, body), u16(data, body + 2)
    layout = None
    mask = None
    if tag == 0xFFFE and held >= 18:
        stated = u16(data, body + 16)
        if min(stated, held - 18) >= 22:
            mask = u32(data, body + 20)
        else:
            found.append({"code": "monkeys-audio.wav-extensible-incomplete", "contains": f"states {stated} byte"})
    elif tag == 0xFFFE and held >= 16:
        found.append({"code": "monkeys-audio.wav-extensible-incomplete", "contains": "holds no cbSize"})
    if count != channels and channels > 0:
        found.append({"code": "monkeys-audio.wav-channels-differ", "contains": f"states {count} channel"})
    if mask:
        if 1 <= channels <= 32:
            layout = mask_layout(mask, channels)
        ones = bin(mask).count("1")
        if mask & 0x80000000 == 0 and ones != channels:
            found.append({"code": "monkeys-audio.channel-mask-mismatch", "contains": f"names {ones} speaker"})
    return layout, found


class Ape:
    """What parse() found."""


def parse(data):
    """Reads a file of the current format (version 3.98 and later)."""
    a = Ape()
    a.junk = find_descriptor(data)
    j = a.junk
    a.signature = data[j:j + 4]
    a.version = u16(data, j + 4)
    a.program = u16(data, j + 6)
    a.descriptor_bytes, a.header_bytes, a.seek_bytes, a.header_data_bytes = (u32(data, j + o) for o in (8, 12, 16, 20))
    a.frame_data_bytes = u32(data, j + 24) | (u32(data, j + 28) << 32)
    a.terminating_bytes = u32(data, j + 32)
    a.md5 = data[j + 36:j + 52]
    h = j + max(DESCRIPTOR, a.descriptor_bytes)
    (a.level, a.flags, a.blocks_per_frame, a.final_blocks, a.total_frames, a.bits, a.channels, a.rate) = struct.unpack_from("<HHIIIHHI", data, h)
    a.header_at = h
    a.seek_at = h + max(HEADER, a.header_bytes)
    a.header_data_at = a.seek_at + a.seek_bytes
    a.data_start = a.header_data_at + a.header_data_bytes
    a.data_end = a.data_start + a.frame_data_bytes
    a.terminating_end = a.data_end + a.terminating_bytes
    entries = min(a.seek_bytes // 4, max(0, len(data) - a.seek_at) // 4)
    a.seek = []
    add = previous = 0
    for i in range(entries):
        current = u32(data, a.seek_at + 4 * i)
        if current < previous:
            add += 1 << 32
        a.seek.append(add + current)
        previous = current
    count = min(a.total_frames, entries)
    # The SDK takes only the phase of the 32-bit words of the frames from the first seek entry ((seek[n] - seek[0]) % 4, APEDecompress.cpp), so the words
    # start at the first byte at or after the descriptor's data start that has that phase.
    a.word_start = a.data_start + (a.seek[0] + j - a.data_start) % 4 if count > 0 else a.data_start
    span = a.data_end - a.word_start
    a.frames = []
    for f in range(count):
        s = a.seek[f] + j - a.word_start
        e = a.seek[f + 1] + j - a.word_start if f + 1 < count else span
        blocks = a.final_blocks if f == a.total_frames - 1 else a.blocks_per_frame
        a.frames.append({"start": s, "end": e, "blocks": blocks})
    a.header_data = data[a.header_data_at:a.data_start]
    a.terminating = data[a.data_end:a.terminating_end]
    return a


def frame_data(data, a, f):
    """The stream bytes of frame f, as the encoder wrote them."""
    frame = a.frames[f]
    return stream_bytes(data, a.word_start, frame["start"], frame["end"])


def compute_md5(data, a):
    md5 = hashlib.md5()
    md5.update(data[a.header_data_at:a.header_data_at + a.header_data_bytes])
    md5.update(data[a.data_start:a.data_start + a.frame_data_bytes + a.terminating_bytes])
    md5.update(data[a.header_at:a.header_at + a.header_bytes])
    md5.update(data[a.seek_at:a.seek_at + a.seek_bytes])
    return md5.digest()


def start_fields(data, a, f, version_has_special=True):
    """(stored CRC of 31 bits, special codes or None) of frame f."""
    frame = a.frames[f]
    first = stream_bytes(data, a.word_start, frame["start"], frame["start"] + 8)
    if len(first) < 4:
        return None, None
    word = struct.unpack(">I", first[:4])[0]
    if word & 0x80000000 and len(first) >= 8:
        return word & 0x7FFFFFFF, struct.unpack(">I", first[4:8])[0]
    return word & 0x7FFFFFFF, None


def build(frames, level, bits, channels, rate, blocks_per_frame, final_blocks, header_data=b"", terminating=b"", junk=b"", version=3990, program=13420,  # 13420: the program version (13.42) these hand-built files imitate
          flags=0, signature=b"MAC ", total_frames=None, seek=None, descriptor_bytes=DESCRIPTOR, header_bytes=HEADER, md5=None, frame_data_bytes=None,
          terminating_bytes=None, header_data_bytes=None, seek_bytes=None, seek_pad=0):
    """Assembles a file. frames are the stream bytes of each frame (the last one including the padding to a whole word); the optional arguments
    override a field the writer would work out, to build files whose descriptor or header says something the file does not hold."""
    stream = b"".join(frames)
    padded = stream + b"\0" * (-len(stream) % 4)
    body = bytearray(len(padded))
    for w in range(0, len(padded), 4):
        body[w:w + 4] = padded[w:w + 4][::-1]
    n = len(frames)
    total_frames = n if total_frames is None else total_frames
    hd_bytes = len(header_data) if header_data_bytes is None else header_data_bytes
    seek_size = (4 * n if seek is None else 4 * len(seek)) + seek_pad if seek_bytes is None else seek_bytes
    descriptor_pad = b"\0" * (descriptor_bytes - DESCRIPTOR) if descriptor_bytes > DESCRIPTOR else b""
    header_pad = b"\0" * (header_bytes - HEADER) if header_bytes > HEADER else b""
    fixed = max(DESCRIPTOR, descriptor_bytes) + max(HEADER, header_bytes)
    seek_count = n if seek is None else len(seek)
    data_start = fixed + 4 * seek_count + seek_pad + len(header_data)
    if seek is None:
        seek = []
        position = 0
        for frame in frames:
            seek.append((data_start + position) & 0xFFFFFFFF)
            position += len(frame)
    seek_blob = b"".join(struct.pack("<I", value & 0xFFFFFFFF) for value in seek) + b"\0" * seek_pad
    header = struct.pack("<HHIIIHHI", level, flags, blocks_per_frame, final_blocks, total_frames, bits, channels, rate) + header_pad
    seek_for_md5 = seek_blob
    hasher = hashlib.md5()
    hasher.update(header_data)
    hasher.update(bytes(body))
    hasher.update(terminating)
    hasher.update(header[:header_bytes] if header_bytes <= len(header) else header)
    hasher.update(seek_for_md5)
    digest = hasher.digest() if md5 is None else md5
    fd = len(body) if frame_data_bytes is None else frame_data_bytes
    tb = len(terminating) if terminating_bytes is None else terminating_bytes
    descriptor = (signature + struct.pack("<HHIIIIIII", version, program, descriptor_bytes, header_bytes, seek_size, hd_bytes, fd & 0xFFFFFFFF, fd >> 32, tb) + digest)
    return bytes(junk) + descriptor + descriptor_pad + header + seek_blob + header_data + bytes(body) + terminating


def frame_blocks(a, f):
    return a.final_blocks if f == a.total_frames - 1 else a.blocks_per_frame


def verdict(tallies):
    """StreamVerification.IsVerified: false where a stored checksum differs; true where every stored checksum was compared and holds and one that was
    compared covers all of the audio; null otherwise."""
    if any(t["failed"] > 0 for t in tallies):
        return False
    if any(t["stored"] - t["checked"] > 0 for t in tallies):
        return None
    return True if any(t["coversAllAudio"] and t["checked"] > 0 for t in tallies) else None


def tallies(a, data, frames_complete, stored_crcs, md5_checked, md5_failed, crc_checked=0, crc_failed=0):
    return [
        {"name": "descriptor MD5", "covers": "codedData", "coversAllAudio": bool(md5_checked and frames_complete), "stored": 1, "checked": md5_checked, "failed": md5_failed},
        {"name": "frame CRC-32", "covers": "decodedAudio", "coversAllAudio": bool(frames_complete and stored_crcs == len(a.frames)), "stored": stored_crcs,
         "checked": crc_checked, "failed": crc_failed},
    ]


def manifest(data, a, regions, encoder, findings, pcm, decode, frame_limit=64, frames_complete=None, stored_crcs=None, md5_checked=None, md5_failed=None,
             stream=True, level_known=True, seek_present=None, placed=None, rated=None):
    """The expected report of a file. pcm is {"samples", "md5"} of the audio the decoder writes, or None; decode is {"status", "checked", "failed",
    "samples", "findings"}: what decoding does."""
    computed = compute_md5(data, a) if a.terminating_end <= len(data) else None
    if md5_checked is None:
        md5_checked = 1 if computed is not None else 0
    if md5_failed is None:
        md5_failed = 1 if computed is not None and computed != a.md5 else 0
    n = len(a.frames)
    if frames_complete is None:
        frames_complete = n == a.total_frames > 0 and a.terminating_end <= len(data) and all(0 <= f["start"] <= f["end"] for f in a.frames)
    if stored_crcs is None:
        stored_crcs = n
    placed = len(a.frames) if placed is None else placed
    avail = max(0, min(len(data), a.data_end) - a.word_start) // 4 * 4
    entries = []
    for index, frame in enumerate(a.frames[:min(frame_limit, placed)]):
        end = min(frame["end"], avail)
        rs = ranges(a.word_start, min(frame["start"], end), end)
        offset, length = extent(rs) if rs else (min(a.data_start, len(data)), 0)
        crc, special = start_fields(data, a, index)
        entries.append({"index": index, "offset": offset, "length": length, "samples": frame["blocks"], "crc": None if crc is None else f"0x{crc:08X}",
                        "special": None if special is None else f"0x{special:08X}", "start": frame["start"], "end": frame["end"]})
    rated = len(a.frames) if rated is None else rated
    bits = sum((f["end"] - f["start"]) * 8 for f in a.frames[:rated])
    coded = sum(f["blocks"] for f in a.frames[:rated])
    samples = 0 if a.total_frames == 0 else (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    default = tallies(a, data, frames_complete, stored_crcs, md5_checked, md5_failed)
    verify = tallies(a, data, frames_complete, stored_crcs, md5_checked, md5_failed, decode.get("checked", 0), decode.get("failed", 0))
    wav_layout, wav_findings = wav_header(a.header_data, a.channels)
    findings = [*findings, *wav_findings]
    info = {
        "codec": "Monkey's Audio",
        "layout": wav_layout or {1: ["front center"], 2: ["front left", "front right"]}.get(a.channels),
        "sample_rate": a.rate,
        "channels": a.channels,
        "channel_mode": {0: None, 1: "Mono", 2: "Mid/side stereo"}.get(a.channels, "Multichannel"),
        "samples": samples,
        "duration": samples / a.rate if a.rate else 0,
        "bits_per_sample": a.bits,
        "average_bitrate": bits * a.rate / coded / 1000 if coded and a.rate else None,
        "encoder": f"Monkey's Audio {a.program // 1000}.{a.program % 1000 // 10:02d}" if a.program else f"Monkey's Audio (file version {a.version // 1000}.{a.version % 1000 // 10:02d})",
    }
    result = {
        "length": len(data),
        "descriptor": {
            "version": a.version, "program": a.program, "descriptor_bytes": a.descriptor_bytes, "header_bytes": a.header_bytes, "seek_table_bytes": a.seek_bytes,
            "header_data_bytes": a.header_data_bytes, "frame_data_bytes": a.frame_data_bytes, "terminating_bytes": a.terminating_bytes, "md5": a.md5.hex(),
            "md5_computed": computed.hex() if computed else None,
        },
        "header": {"level": a.level, "flags": a.flags, "blocks_per_frame": a.blocks_per_frame, "final_frame_blocks": a.final_blocks, "total_frames": a.total_frames,
                   "bits_per_sample": a.bits, "channels": a.channels, "sample_rate": a.rate},
        "seek_entries": len(a.frames),
        "frames": entries,
        "stream_infos": [info] if stream else [],
        "decoded_samples": decode.get("samples", 0),
        "pcm_samples": decode.get("pcm_samples", decode.get("samples", 0)),
        "verification": {
            "default": {"isVerified": verdict(default), "decoding": "notRequested", "checksums": default},
            "verify": {"isVerified": verdict(verify), "decoding": decode["status"], "checksums": verify},
        },
        "decode_findings": decode.get("findings", []),
        "regions": regions,
        "encoder": encoder,
        "findings": findings,
    }
    if pcm is not None:
        result["decoded_pcm_md5"] = pcm["md5"]
    return result


def dump(manifest_dict):
    return json.dumps(manifest_dict, indent=1, ensure_ascii=False) + "\n"


def old_blocks_per_frame(version, level):
    """The blocks in a frame of a file with the old header (APEHeader.cpp, AnalyzeOld)."""
    return 73728 * 4 if version >= 3950 else 73728 if version >= 3900 or (version >= 3800 and level == 4000) else 9216


class OldApe:
    """What parse_old found."""


def parse_old(data):
    """Reads a file of the format before version 3.98 (one header of 32 bytes, optional fields, WAV header data, seek table, frames, terminating data)."""
    a = OldApe()
    a.junk = 0
    a.version = u16(data, 4)
    a.level = u16(data, 6)
    a.flags = u16(data, 8)
    a.channels = u16(data, 10)
    a.rate = u32(data, 12)
    a.header_data_bytes = u32(data, 16)
    a.terminating_bytes = u32(data, 20)
    a.total_frames = u32(data, 24)
    a.final_blocks = u32(data, 28)
    at = 32
    a.peak = None
    if a.flags & 4:
        a.peak = u32(data, at)
        at += 4
    elements = a.total_frames
    if a.flags & 16:
        elements = u32(data, at)
        at += 4
    a.header_bytes = at
    a.header_data = data[at:at + a.header_data_bytes]
    a.bits = 8 if a.flags & 1 else 24 if a.flags & 8 else 16
    a.blocks_per_frame = old_blocks_per_frame(a.version, a.level)
    a.seek_at = at + a.header_data_bytes
    a.data_start = a.seek_at + 4 * elements + (elements if a.version <= 3800 else 0)
    a.data_end = len(data) - a.terminating_bytes
    a.seek_entries = elements
    a.has_crc = bool(a.flags & 2) or a.version >= 3930
    a.has_special = a.has_crc and a.version > 3820
    seek = [u32(data, a.seek_at + 4 * i) for i in range(elements)]
    bits_at = a.seek_at + 4 * elements
    a.start_bits = list(data[bits_at:bits_at + elements]) if a.version <= 3800 else [0] * elements
    a.word_start = a.data_start + (seek[0] - a.data_start) % 4 if a.total_frames > 0 and elements > 0 else a.data_start
    a.frames = []
    for f in range(a.total_frames):
        start = seek[f] - a.word_start
        end = seek[f + 1] - a.word_start if f + 1 < a.total_frames else a.data_end - a.word_start
        a.frames.append({"start": start, "end": end, "blocks": a.final_blocks if f == a.total_frames - 1 else a.blocks_per_frame})
    return a


def old_frame_entries(data, a):
    """The manifest's entry for each frame of a file with the old header: where it lies, its stored CRC and special codes where the version has them (all
    32 bits are the CRC up to version 3.82, and a file without the CRC flag has a Rice-coded checksum instead, which the analyzer does not read). In a file of
    version 3.80 a frame starts at a bit of the word its seek entry names."""
    entries = []
    available = max(0, min(len(data), a.data_end) - a.word_start) // 4 * 4
    for index, frame in enumerate(a.frames):
        rs = ranges(a.word_start, frame["start"], min(frame["end"], available))
        offset, length = extent(rs)
        crc = special = None
        bit = a.start_bits[index] if index < len(a.start_bits) else 0
        first = stream_bytes(data, a.word_start, frame["start"], frame["start"] + 12)
        if a.has_crc and len(first) >= 4:
            window = int.from_bytes(first.ljust(12, b"\0"), "big")
            word = (window >> (96 - bit - 32)) & 0xFFFFFFFF
            if not a.has_special:
                crc = word
            else:
                crc = word & 0x7FFFFFFF
                if word & 0x80000000 and len(first) >= 8:
                    special = (window >> (96 - bit - 64)) & 0xFFFFFFFF
        entries.append({"index": index, "offset": offset, "length": length, "samples": frame["blocks"], "crc": None if crc is None else f"0x{crc:08X}",
                        "special": None if special is None else f"0x{special:08X}", "start": frame["start"], "end": frame["end"]})
    return entries


def old_stream_info(a, entries_bits, samples):
    layout = wav_header(a.header_data, a.channels)[0] or {1: ["front center"], 2: ["front left", "front right"]}.get(a.channels)
    return {"codec": "Monkey's Audio", "layout": layout, "sample_rate": a.rate, "channels": a.channels,
            "channel_mode": {0: None, 1: "Mono", 2: "Mid/side stereo"}.get(a.channels, "Multichannel"), "samples": samples,
            "duration": samples / a.rate if a.rate else 0, "bits_per_sample": a.bits,
            "average_bitrate": entries_bits * a.rate / samples / 1000 if samples and a.rate else None,
            "encoder": f"Monkey's Audio (file version {a.version // 1000}.{a.version % 1000 // 10:02d})"}


def build_old(frames, version, level, flags, channels, rate, final_blocks, header_data=b"", terminating=b"", total_frames=None, peak=None):
    """A file of the format before version 3.98: one header of 32 bytes, an optional peak level and seek element count, the WAV header data, the seek table
    (offsets from the start of the file) and the frames (APEHeader.cpp, AnalyzeOld). It has no MD5."""
    stream = b"".join(frames)
    padded = stream + b"\0" * (-len(stream) % 4)
    body = bytearray(len(padded))
    for w in range(0, len(padded), 4):
        body[w:w + 4] = padded[w:w + 4][::-1]
    n = len(frames)
    total_frames = n if total_frames is None else total_frames
    extra = (struct.pack("<i", peak) if peak is not None else b"")
    fixed = 32 + len(extra) + len(header_data) + 4 * n + (n if version <= 3800 else 0)
    seek = []
    position = 0
    for frame in frames:
        seek.append(fixed + position)
        position += len(frame)
    header = (b"MAC " + struct.pack("<HHHHIIIII", version, level, flags, channels, rate, len(header_data), len(terminating), total_frames, final_blocks))
    seek_blob = b"".join(struct.pack("<I", value) for value in seek) + (b"\0" * n if version <= 3800 else b"")
    return header + extra + header_data + seek_blob + bytes(body) + terminating


def regions_of(data, a, end=None):
    """The top-level nodes of the stream: the stream itself up to the end of the frame data, then the terminating data and what follows it (the
    analyzer's reading of a file whose tags are cut off at end)."""
    end = len(data) if end is None else end
    frame_end = min(a.data_end, end)
    stream_end = max(frame_end, min(a.data_start, end))
    out = []
    if a.junk > 0:
        out.append({"name": "Junk", "offset": 0, "length": a.junk})
    out.append({"name": "Monkey's Audio", "offset": a.junk, "length": max(0, stream_end - a.junk)})
    if frame_end < end and a.terminating_bytes > 0 and frame_end + a.terminating_bytes <= end:
        out.append({"name": "WAV Terminating Data", "offset": frame_end, "length": a.terminating_bytes})
        after = frame_end + a.terminating_bytes
        if after < end:
            out.append({"name": "Trailing Data", "offset": after, "length": end - after})
    elif a.terminating_bytes == 0:
        if frame_end < end:
            out.append({"name": "Trailing Data", "offset": frame_end, "length": end - frame_end})
    elif frame_end < end:
        out.append({"name": "WAV Terminating Data", "offset": frame_end, "length": end - frame_end})
    return out
