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
    return {"codec": codec, "layout": layout(count, mask), "sample_rate": native, "channels": count, "samples": samples, "lost_samples": 0, "duration": seconds, "bits_per_sample": bits,
            "average_bitrate": set_bits * native / samples / 1000 if samples else None, "hybrid": hybrid, "correction": correction, "lossless": not (hybrid or correction or lossy(entries, data)),
            "sets": len(grouped), "declared": h["total"] if h["index"] == 0 else None}


# The header bits that state the stream, not how a block is coded (Pairing.StreamFlags): the sample size, mono, float, 32-bit integer and false stereo
# flags, the shift, the rate and DSD.
STREAM_FLAGS = 0x3 | 0x4 | 0x80 | 0x100 | (0x1F << 13) | (0xF << 23) | FALSE_STEREO | DSD
CONFIG_CREATE_WVC = 0x80000

# What a hybrid file's lookup of its correction file found, where it is not bytes: nothing beside the file.
MISSING = "missing"


def hybrid_blocks(entries):
    """The blocks of a hybrid main file that take a correction block: with audio and the hybrid flag."""
    return [e for e in entries if e["kind"] == "block" and e["header"]["samples"] and e["header"]["flags"] & HYBRID]


def is_correction(entries):
    """Whether the blocks with audio hold correction data only: a correction or extra bitstream and no bitstream of their own."""
    audio = [e for e in entries if e["kind"] == "block" and e["header"]["samples"]]
    return bool(audio) and not any(has(e, 0x0A) or has(e, 0x0E) for e in audio) and any(has(e, 0x0B) or has(e, 0x0C) for e in audio)


def match(main, other):
    """libwavpack's match_wvc_header (open_utils.c): 0 the correction block is the main block's, 1 it comes before it (the next one is read), -1 the main
    block has none."""
    if main["index"] == other["index"] and main["samples"] == other["samples"]:
        if main["flags"] == other["flags"]:
            return 0
        in_main = (-1 if main["flags"] & INITIAL else 0) + (1 if main["flags"] & FINAL else 0)
        in_other = (-1 if other["flags"] & INITIAL else 0) + (1 if other["flags"] & FINAL else 0)
        return 1 if in_other - in_main < 0 else -1
    return 1 if (other["index"] - main["index"]) & 0x8000000000 else -1


def applied(main_entries, entries):
    """How libwavpack's read_wvc_block takes the correction file's blocks for the main file's hybrid blocks (open_utils.c): the entries of the correction blocks the
    main blocks use (a matching block that is whole and whose checksum holds), and how many hybrid blocks have none."""
    audio = [e for e in entries if e["kind"] == "block" and e["header"]["samples"]]
    used, missing, j = [], 0, 0
    for block in hybrid_blocks(main_entries):
        while True:
            if j >= len(audio):
                missing += 1
                break
            result = match(block["header"], audio[j]["header"])
            if result == -1:
                missing += 1
                break
            j += 1
            if result == 0:
                if audio[j - 1]["status"] or not verifies(audio[j - 1]):
                    missing += 1
                else:
                    used.append(audio[j - 1])
                break
    return used, missing


_TOOL = []


def libwavpack_pair(data, companion):
    """libwavpack's pass over the pair, as wavpack_blocks prints it (wavpack_blocks.c, decode_pair): whether the correction file is in use, the samples unpacked,
    the errors counted and the MD5 of the samples (as the container's bytes, little-endian, channels interleaved), which the manifest holds as the pair's audio."""
    import pathlib
    import subprocess
    import tempfile

    import wavpack_tool

    if not _TOOL:
        _TOOL.append(wavpack_tool.build_blocks())
    with tempfile.TemporaryDirectory() as work:
        (pathlib.Path(work) / "x.wv").write_bytes(data)
        (pathlib.Path(work) / "x.wvc").write_bytes(companion)
        lines = subprocess.run([str(_TOOL[0]), str(pathlib.Path(work) / "x.wv"), str(pathlib.Path(work) / "x.wvc")], capture_output=True, text=True, check=True).stdout.splitlines()
    facts = dict(line.split(" ", 1) for line in lines if line.startswith("pair_"))
    return {"mode_wvc": facts["pair_mode_wvc"] == "1", "samples": int(facts["pair_samples"]), "errors": int(facts["pair_errors"]), "md5": facts["pair_md5"]}


def config_flags(data, entries):
    """The config flags of the first config block that holds them (read_config_info), or None."""
    for e in entries:
        if e["kind"] != "block":
            continue
        for i, p, hl, size, length, clipped in e["sub_blocks"]:
            if i & 0x3F == 0x25 and not clipped and length >= 3:
                d = data[e["offset"] + p + hl:e["offset"] + p + hl + length]
                return d[0] << 8 | d[1] << 16 | d[2] << 24
    return None


def pairing(main_entries, other_data):
    """How a hybrid file's blocks stand with the file given as its correction file (docs/design/correction-files.md, "WavPack"): {"status", "problems",
    "companion"}, each problem a kind and a count, in the order the analyzer reports them. The blocks are matched as libwavpack matches them; a companion that
    states another stream is not matched at all (one cause); the checksums and the damage are the companion's own."""
    entries = walk(other_data, 0, len(other_data))
    if not is_correction(entries):
        return {"status": "inconsistent", "problems": [{"kind": "notCorrectionData", "count": 1}], "entries": entries}
    mains = hybrid_blocks(main_entries)
    audio = [e for e in entries if e["kind"] == "block" and e["header"]["samples"]]
    problems = []
    matched = 0
    first = audio[0]["header"]
    if mains and (mains[0]["header"]["version"] != first["version"] or mains[0]["header"]["total"] != first["total"] or mains[0]["header"]["index"] != first["index"]
                  or (mains[0]["header"]["flags"] ^ first["flags"]) & STREAM_FLAGS):
        problems.append({"kind": "streamDiffers", "count": 1})
    else:
        missing = unmatched = 0
        j = 0
        for m in mains:
            while True:
                if j >= len(audio):
                    missing += 1
                    break
                result = match(m["header"], audio[j]["header"])
                if result == -1:
                    missing += 1
                    break
                j += 1
                if result == 0:
                    matched += 1
                    break
                unmatched += 1
        unmatched += len(audio) - j
        if missing:
            problems.append({"kind": "blocksMissing", "count": missing})
        if unmatched:
            problems.append({"kind": "blocksUnmatched", "count": unmatched})
    failed = cut = 0
    for e in entries:
        if e["kind"] == "skipped":
            cut += 1
        elif e["header"]["samples"]:
            if e["status"]:
                cut += 1
            elif not verifies(e):
                failed += 1
    if failed:
        problems.append({"kind": "checksumsFail", "count": failed})
    if cut:
        problems.append({"kind": "cut", "count": cut})
    return {"status": "inconsistent" if problems else "consistent", "problems": problems, "entries": entries, "matched": matched}


FINDING_TEXT = {"notCorrectionData": "holds no correction data", "streamDiffers": "states another stream", "blocksMissing": "lacks",
                "blocksUnmatched": "that no block of the file matches", "checksumsFail": "checksum", "cut": "is cut"}


def correction(data, entries, info, companion):
    """The pairing of a hybrid main file that has a correction file to look for, the findings it raises and the tally the verification gains, or None for a
    file without the concept. companion is the bytes of the file the lookup finds, MISSING where it finds none, or None where nothing was looked for."""
    if info is None or not info["hybrid"] or info["correction"]:
        return None
    findings, tally, pair = [], None, None
    if companion is None:
        result = {"status": "unchecked", "problems": []}
        findings.append({"code": "correction.unchecked", "contains": "was not checked"})
    elif companion is MISSING:
        flags = config_flags(data, entries)
        if flags is not None and not flags & CONFIG_CREATE_WVC:
            result = {"status": "notWritten", "problems": []}
        else:
            result = {"status": "missing", "problems": []}
            findings.append({"code": "correction.missing", "contains": "was found beside the file"})
    else:
        found = pairing(entries, companion)
        result = {"status": found["status"], "problems": found["problems"]}
        findings += [{"code": "correction.inconsistent", "contains": FINDING_TEXT[p["kind"]]} for p in found["problems"]]
        kinds = {p["kind"] for p in found["problems"]}
        # The companion's checksums are the stream's only where its blocks are used: it holds correction data of the same stream and a block of it matches one of the file.
        if is_correction(found["entries"]) and not kinds & {"notCorrectionData", "streamDiffers"} and found.get("matched", 0) > 0:
            blocks = [e for e in found["entries"] if e["kind"] == "block"]
            missing = sum(1 for e in blocks if e["checksum"] is None and e["header"]["flags"] & 0x10000000 and "truncated" not in e["status"])
            failed = sum(1 for e in blocks if e["checksum"] == "invalid") + missing
            stored = sum(1 for e in blocks if e["checksum"] is not None) + missing
            tally = {"name": "correction block checksum", "covers": "codedData", "coversAllAudio": False, "stored": stored, "checked": stored, "failed": failed}
        if not kinds & {"notCorrectionData", "streamDiffers"}:
            pair = {"entries": found["entries"], "bytes": companion}
    return result, findings, tally, pair


def manifest(data, start, end, extra=None, companion=None):
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
    found = correction(data, entries, info, companion)
    if found is not None:
        status, findings, tally, pair = found
        result["correction"] = status
        result["findings"] = list(result.get("findings", [])) + findings
        if tally is not None and expected is not None:
            for mode in result["verification"].values():
                mode["checksums"].insert(1, dict(tally))
                if tally["failed"]:
                    # A checksum that fails, in either file, makes the verdict no.
                    mode["isVerified"] = False
        if isinstance(companion, bytes):
            passed = libwavpack_pair(data, companion)
            result["crosscheck"] = {**result.get("crosscheck", {}), "pair": {"opens": True, "mode_wvc": passed["mode_wvc"], "samples": passed["samples"],
                                                                           "errors": passed["errors"], "md5": passed["md5"]}}
            if pair is not None and expected is not None:
                decoded_with_correction(result, entries, pair, passed)
    return result


def decoded_with_correction(result, entries, pair, passed):
    """The decoding of a main file with the correction file that belongs to it (docs/design/correction-files.md, "Version 4 and 5"): the audio is libwavpack's
    pass over the pair, and the verification is that of the decoding without the correction file with what the correction changes: the checksums of the blocks
    that take a correction block are of the lossless audio (the CRC of the correction block's header, the extra bits' CRC, which only a correction block can
    hold), the MD5 is compared only where every hybrid block has one, and the stream is the lossy part only where none has. A block libwavpack counts an error
    for, and that has its correction block, is one whose CRC fails."""
    used, missing = applied(entries, pair["entries"])
    hybrid = len(hybrid_blocks(entries))
    failed = max(0, passed["errors"] - missing)
    result["decoded_samples"] = result["pcm_samples"] = passed["samples"]
    result["decoded_pcm_md5"] = passed["md5"]
    extra_stored = sum(1 for e in entries if e["kind"] == "block" and has(e, 0x0C)) + sum(1 for e in pair["entries"] if e["kind"] == "block" and has(e, 0x0C))
    extra_used = sum(1 for e in used if has(e, 0x0C))
    for mode, tallies in result["verification"].items():
        by_name = {t["name"]: t for t in tallies["checksums"]}
        by_name["extended CRC"]["stored"] = extra_stored
        tallies["withCorrection"] = True
        tallies.pop("isLossy", None)
        if mode == "verify":
            by_name["block header CRC"]["failed"] = failed
            by_name["extended CRC"]["checked"] = extra_used
            md5 = by_name["MD5"]
            if md5["stored"] and not missing and md5["checked"] == 0 and tallies["decoding"] == "decoded":
                md5["checked"], md5["failed"] = 1, 1 if failed else 0
            if not used:
                tallies["isLossy"] = True
                tallies["withCorrection"] = False
    # The findings: the lossless audio's MD5 is compared (or not) where the decoding says so, and a block whose CRC fails is one finding for each block.
    verify = result["verification"]["verify"]
    findings = [f for f in result["decode_findings"] if f["code"] not in ("wavpack.md5-needs-correction-file", "wavpack.audio-crc-mismatch", "wavpack.md5-mismatch")]
    by_name = {t["name"]: t for t in verify["checksums"]}
    if failed:
        findings.append({"code": "wavpack.audio-crc-mismatch", "count": failed})
    if by_name["MD5"]["stored"] and missing and verify["decoding"] == "decoded":
        findings.append({"code": "wavpack.md5-needs-correction-file", "count": 1})
    if by_name["MD5"]["failed"]:
        findings.append({"code": "wavpack.md5-mismatch", "count": 1})
    result["decode_findings"] = sorted(findings, key=lambda f: f["code"])
    import wavpack_decode

    for mode, tallies in result["verification"].items():
        fresh = wavpack_decode.verification(tallies["decoding"], tallies["checksums"], tallies.get("isLossy", False))
        tallies["isVerified"] = fresh["isVerified"]


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
