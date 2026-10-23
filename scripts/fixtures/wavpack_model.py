"""What a WavPack file holds, worked out from its bytes with the format document (WavPack 4 and 5
binary file/block format) and libwavpack 5.6.0's source, apart from the analyzer: the manifests the
fixture scripts write and the C# tests compare the analyzer's report with.

A block is found as libwavpack's read_next_header finds one (open_utils.c), with the minor version
left to a finding, and a header found after bytes that are no block counts only where its block ends
at the end of the data or at another header. The sub-blocks are walked as WavpackVerifySingleBlock
walks them, and the block checksum is worked out over the block's 16-bit little-endian words.
"""

import struct

SAMPLE_RATES = [6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000]
HEADER = 32
INITIAL, FINAL, MONO, HYBRID, JOINT, DSD, FALSE_STEREO = 0x800, 0x1000, 4, 8, 0x10, 0x80000000, 0x40000000
SUB_BLOCK_NAMES = {0x00: "Dummy", 0x01: "Encoder Info", 0x02: "Decorrelation Terms", 0x03: "Decorrelation Weights", 0x04: "Decorrelation Samples",
                   0x05: "Entropy Variables", 0x06: "Hybrid Profile", 0x07: "Shaping Weights", 0x08: "Float Info", 0x09: "Int32 Info",
                   0x0A: "WavPack Bitstream", 0x0B: "Correction Bitstream", 0x0C: "Extra Bitstream", 0x0D: "Channel Info", 0x0E: "DSD Block",
                   0x21: "RIFF Header", 0x22: "RIFF Trailer", 0x23: "Alternate Header", 0x24: "Alternate Trailer", 0x25: "Config Block",
                   0x26: "MD5 Checksum", 0x27: "Sample Rate", 0x28: "Alternate Extension", 0x29: "Alternate MD5 Checksum",
                   0x2A: "New Config Block", 0x2B: "Channel Identities", 0x2F: "Block Checksum"}


SPEAKERS = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center",
            "back center", "side left", "side right", "top center", "top front left", "top front center", "top front right", "top back left",
            "top back center", "top back right"]


def layout(channels, mask):
    """The speakers of the channels, the mask's in the order of its bits, then channels without one."""
    names = [SPEAKERS[bit] if bit < len(SPEAKERS) else "discrete" for bit in range(32) if mask >> bit & 1][:channels]
    return names + ["discrete"] * (channels - len(names))


def plausible(data, p):
    """libwavpack's header test (read_next_header), with the minor version left out."""
    b = data[p:p + HEADER]
    return (len(b) == HEADER and b[:4] == b"wvpk" and not b[4] & 1 and b[6] < 16 and b[7] == 0 and (b[6] or b[5] or b[4] >= 24)
            and b[9] == 4 and b[22] < 3 and b[23] == 0)


def header(data, p):
    size, version, index_high, total_high, total, index, samples, flags, crc = struct.unpack_from("<IHBBIIIII", data, p + 4)
    return {"size": size + 8, "version": version, "index": index | index_high << 32,
            "total": None if total == 0xFFFFFFFF else total + (total_high << 32) - total_high,
            "samples": samples, "flags": flags, "crc": crc}


def chained(data, p, end):
    """Whether the block at p ends at the end of the data or at another header."""
    stop = p + header(data, p)["size"]
    return stop == end or (stop < end and plausible(data, stop))


def find_chain(data, start, limit, end):
    for p in range(start, min(limit, end)):
        if data[p] == 0x77 and plausible(data, p) and chained(data, p, end):
            return p
    return -1


def checksum(block, length):
    total = 0xFFFFFFFF
    for i in range(0, length - 1, 2):
        total = (total * 3 + block[i] + (block[i + 1] << 8)) & 0xFFFFFFFF
    return total


def sub_blocks(block):
    """(id, offset, header length, size, data length, clipped) of each sub-block, and the bytes left."""
    found = []
    p = HEADER
    while len(block) - p >= 2:
        i, words = block[p], block[p + 1]
        size, hl = words << 1, 2
        if i & 0x80:
            if len(block) - p < 4:
                break
            size += (block[p + 2] << 9) + (block[p + 3] << 17)
            hl = 4
        clipped = len(block) - p - hl < size
        data = len(block) - p - hl if clipped else size - (1 if i & 0x40 else 0)
        found.append((i, p, hl, size, data, clipped))
        if clipped:
            return found, 0
        p += hl + size
    return found, len(block) - p


def verdict(block, found):
    """"valid", "invalid" or None without a checksum sub-block, as WavpackVerifySingleBlock sees it."""
    result = None
    for i, p, hl, size, length, clipped in found:
        if i & 0x3F != 0x2F or clipped:
            continue
        stored = block[p + hl:p + hl + size]
        if i & 0x40 or size not in (2, 4):
            return "invalid"
        total = checksum(block, p + hl - 2)
        if size == 4:
            ok = struct.unpack("<I", stored)[0] == total
        else:
            ok = struct.unpack("<H", stored)[0] == (total ^ (total >> 16)) & 0xFFFF
        if not ok:
            return "invalid"
        result = "valid"
    return result


def verifies(entry):
    """Whether libwavpack's WavpackVerifySingleBlock accepts the block: its sub-blocks end exactly at its
    end, no checksum sub-block fails, and one is there where the flags say so."""
    if entry["left"] or any(clipped for *_, clipped in entry["sub_blocks"]):
        return False
    if entry["checksum"] == "invalid":
        return False
    return not (entry["header"]["flags"] & 0x10000000 and entry["checksum"] is None)


def walk(data, start, end):
    """The entries of data[start:end]: blocks and skipped runs, in file order."""
    entries = []
    p = start
    skip = None
    while p < end:
        if not plausible(data, p):
            skip = p if skip is None else skip
            found = find_chain(data, p + 1, end, end)
            p = found if found >= 0 else end
            if found >= 0:
                entries.append({"kind": "skipped", "offset": skip, "length": p - skip})
                skip = None
            continue
        if skip is not None:
            entries.append({"kind": "skipped", "offset": skip, "length": p - skip})
            skip = None
        h = header(data, p)
        stated_end = p + h["size"]
        length, status = h["size"], []
        if stated_end <= end and (stated_end == end or plausible(data, stated_end)):
            pass
        else:
            q = find_chain(data, p + HEADER, min(stated_end, end), end)
            if q >= 0:
                length, status = q - p, ["oversized"]
            elif stated_end > end:
                length, status = end - p, ["truncated"]
        block = data[p:p + length]
        found, left = sub_blocks(block)
        entry = {"kind": "block", "offset": p, "length": length, "status": status, "header": h, "sub_blocks": found, "left": left,
                 "checksum": verdict(block, found)}
        entries.append(entry)
        p += length
    if skip is not None:
        entries.append({"kind": "skipped", "offset": skip, "length": end - skip})
    return entries


def sample_rate(entry, data):
    h = entry["header"]
    index = h["flags"] >> 23 & 15
    block = data[entry["offset"]:entry["offset"] + entry["length"]]
    for i, p, hl, size, length, clipped in entry["sub_blocks"]:
        if i & 0x3F == 0x27 and length in (3, 4) and not clipped:
            rate = int.from_bytes(block[p + hl:p + hl + 3], "little") | ((block[p + hl + 3] & 0x7F) << 24 if length == 4 else 0)
            # libwavpack falls back to the flags' index when config.sample_rate is 0 (open_utils.c 229-234).
            if rate:
                return rate
    return SAMPLE_RATES[index] if index < 15 else 44100


def channel_info(entry, data):
    """(channels, mask, streams) of the first channel info libwavpack takes with channels, or None. A count
    of 0 with an empty mask is accepted by read_channel_info and leaves the channels to the flags
    (open_utils.c 189-192), so it is no channel info; with a mask it is refused."""
    block = data[entry["offset"]:entry["offset"] + entry["length"]]
    for i, p, hl, size, length, clipped in entry["sub_blocks"]:
        if i & 0x3F == 0x0D and not clipped and 0 < length <= 7:
            d = block[p + hl:p + hl + length]
            if length >= 6:
                channels, streams = (d[0] | (d[2] & 0xF) << 8) + 1, (d[1] | (d[2] & 0xF0) << 4) + 1
                mask = int.from_bytes(d[3:], "little")
                if channels < streams:
                    return None
            else:
                channels, streams = d[0], 8
                mask = int.from_bytes(d[1:], "little")
            if channels > streams * 2 or bin(mask).count("1") > channels:
                return None
            if channels:
                return channels, mask, streams
    return None


def wrapper_bytes(entry):
    """The bytes of the wrapper sub-blocks (RIFF and alternate header and trailer) in the block, which the
    average bitrate leaves out."""
    return sum(hl + (size if not clipped else length) for i, p, hl, size, length, clipped in entry["sub_blocks"] if i & 0x3F in (0x21, 0x22, 0x23, 0x24))


def dsd_power(entry, data):
    block = data[entry["offset"]:entry["offset"] + entry["length"]]
    for i, p, hl, size, length, clipped in entry["sub_blocks"]:
        if i & 0x3F == 0x0E and length >= 2 and not clipped:
            return block[p + hl]
    return None


def has(entry, function):
    return any(i & 0x3F == function for i, *_ in entry["sub_blocks"])


def lossy(entries, data):
    """Whether a block lacks the extra bitstream it needs: an int32 info with bits sent, or a float info
    with shifted, sent or exceptional values, and no extra bitstream (libwavpack's lossy_blocks)."""
    for e in entries:
        if e["kind"] != "block" or not e["header"]["samples"] or has(e, 0x0C):
            continue
        block = data[e["offset"]:e["offset"] + e["length"]]
        for i, p, hl, size, length, clipped in e["sub_blocks"]:
            if clipped or length != 4:
                continue
            first = block[p + hl]
            if i & 0x3F == 0x09 and e["header"]["flags"] & 0x100 and first:
                return True
            if i & 0x3F == 0x08 and e["header"]["flags"] & 0x80 and first & 0x2E:
                return True
    return False


def sets(entries, max_streams=8):
    """The blocks with audio grouped into sets: from an initial block (or the first) to a final one, or to
    the stream limit of libwavpack (max_streams: 8, or the streams of a long channel info)."""
    result, current = [], None
    for e in entries:
        if e["kind"] != "block" or not e["header"]["samples"]:
            continue
        f = e["header"]["flags"]
        if f & INITIAL or current is None:
            current = {"entries": [], "closed": False}
            result.append(current)
        current["entries"].append(e)
        if f & FINAL or len(current["entries"]) >= max_streams:
            current = None
    return result


def stream_info(data, entries):
    """The figures of the stream info, or None without audio."""
    audio = [e for e in entries if e["kind"] == "block" and e["header"]["samples"]]
    if not audio:
        return None
    first = audio[0]
    h = first["header"]
    is_dsd = bool(h["flags"] & DSD)
    rate = sample_rate(first, data)
    power = dsd_power(first, data) if is_dsd else None
    # The rate of DSD is held to what 31 bits hold (Reader.MaxNativeRate).
    native = min(rate * (1 << power) * 8, 2**31 - 1) if is_dsd and power is not None and power <= 31 else rate
    # libwavpack settles the channels at the first audio block that verifies (its open loop skips a block that fails
    # WavpackVerifySingleBlock): only a channel info in the blocks that verify up to it counts.
    upto = []
    for e in entries:
        if e["kind"] == "block" and verifies(e):
            upto.append(e)
            if e["header"]["samples"]:
                break
    max_streams = 8
    for e in upto:
        if has(e, 0x0D) and (found := channel_info(e, data)):
            max_streams = found[2]
            break
    grouped = sets(entries, max_streams)
    counted = sum(g["entries"][0]["header"]["samples"] for g in grouped)
    samples = counted * (8 if is_dsd else 1)
    channels = None
    info = None
    for e in upto:
        if has(e, 0x0D) and (info := channel_info(e, data)):
            break
    set_channels = sum(1 if b["header"]["flags"] & MONO else 2 for b in grouped[0]["entries"])
    count = info[0] if info else set_channels
    mask = info[1] if info else {1: 4, 2: 3}.get(count, 0)
    # A shift that leaves no bits gives no bit depth (wavpack.flags-invalid).
    shifted = (h["flags"] & 3) * 8 + 8 - (h["flags"] >> 13 & 31)
    bits = 1 if is_dsd else 32 if h["flags"] & 0x80 else shifted if shifted > 0 else None
    set_bits = sum((e["length"] - wrapper_bytes(e)) * 8 for g in grouped for e in g["entries"])
    seconds = samples / native if native else 0
    hybrid = bool(h["flags"] & HYBRID) or any(e["header"]["flags"] & HYBRID for e in audio)
    correction = not any(has(e, 0x0A) or has(e, 0x0E) for e in audio) and any(has(e, 0x0B) or has(e, 0x0C) for e in audio)
    codec = "WavPack correction data" if correction else "WavPack hybrid" if hybrid else "WavPack lossy" if lossy(entries, data) else "WavPack lossless"
    return {"codec": codec, "layout": layout(count, mask), "sample_rate": native, "channels": count, "samples": samples, "duration": seconds, "bits_per_sample": bits,
            "average_bitrate": set_bits * native / samples / 1000 if samples else None, "hybrid": hybrid, "correction": correction, "lossless": not (hybrid or correction or lossy(entries, data)),
            "sets": len(grouped), "declared": h["total"] if h["index"] == 0 else None}


def manifest(data, start, end, extra=None):
    entries = walk(data, start, end)
    result = {"length": len(data), "blocks": [], "skipped": []}
    for e in entries:
        if e["kind"] == "skipped":
            result["skipped"].append([e["offset"], e["length"]])
            continue
        h = e["header"]
        result["blocks"].append({"offset": e["offset"], "length": e["length"], "index": h["index"], "samples": h["samples"],
                                 "flags": f"{h['flags']:08x}", "version": f"{h['version']:x}", "total": h["total"], "crc": f"{h['crc']:08x}", "verifies": verifies(e),
                                 "checksum": e["checksum"], "status": e["status"],
                                 "sub_blocks": [[e["offset"] + p, i, size] for i, p, hl, size, length, clipped in e["sub_blocks"]]})
    info = stream_info(data, entries)
    if info is None:
        result["stream_infos"] = []
    else:
        result["stream_infos"] = [info]
    expected = decoding(data, start, end) if info is not None else None
    if expected is not None:
        result.update(expected)
    result.update(extra or {})
    return result


def decoding(data, start, end):
    """The decoded samples, the MD5 of the canonical layout, the verification with and without decoding and the findings decoding adds,
    from wavpack_decode.py (the decoder worked out apart from the analyzer)."""
    import wavpack_decode

    expected = wavpack_decode.expectation(data, start, end)
    if expected is None:
        return None
    result = {}
    if expected["verify"]["decoding"] == "decoded":
        result["decoded_samples"] = expected["decoded_samples"]
        result["pcm_samples"] = expected["pcm_samples"]
        if expected["decoded_pcm_md5"] is not None:
            result["decoded_pcm_md5"] = expected["decoded_pcm_md5"]
        if expected["decoded_dsd_md5"] is not None:
            result["decoded_dsd_md5"] = expected["decoded_dsd_md5"]
    result["verification"] = {"default": expected["default"], "verify": expected["verify"]}
    result["decode_findings"] = expected["decode_findings"]
    return result
