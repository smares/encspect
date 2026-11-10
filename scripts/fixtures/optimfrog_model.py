"""A reader for OptimFROG 4.5xx and later files, written from the author's specification (SDK/Documentation/format.txt: the blocks, the main block, the
COMP block, appendices A to D) and from what the encoder-written fixtures show, apart from the analyzer. It works out the blocks of a file for the fixture
scripts and, in manifest(), each fixture's manifest.

What the files add to the specification:

- A COMP block's CRC-32 is the plain IEEE (zlib) CRC-32 over the block's data after the CRC field (every encoder-written fixture, OptimFROG 5.100).
- The 2-byte field "reserved for internal use" of a COMP block is shown, never judged.
"""

import struct
import zlib

SAMPLE_TYPES = ["UINT8", "SINT8", "UINT16", "SINT16", "UINT24", "SINT24", "UINT32", "SINT32", "FLOAT32_0", "FLOAT32_16", "FLOAT32_24"]
BITS = [8, 8, 16, 16, 24, 24, 32, 32, 32, 32, 32]
CHANNEL_CONFIGS = ["MONO", "STEREO_LR"]
CHANNELS = [1, 2]
MODES = ["fast", "normal", "high", "extra", "best", "ultra", "insane", "highnew", "extranew", "bestnew", "ultranew", "extrafast", "turbonew", "fastnew",
         "normalnew"]
SPEEDUPS = ["1x", "2x", "4x"]
SYSTEMS = ["Windows x86", "Linux x86", "Linux x64", "OS X ppc", "OS X x86", "OS X x64", "Windows x64", "FreeBSD x86", "FreeBSD x64"]
# "OFRX" is the main block of the files `off` 5.100 writes (format.txt does not define it); its fields are those of "OFR ".
MAIN_IDS = ("OFR ", "OFRX")
# "DETA" is a block `off` 5.100 writes after the data blocks, "CORR" a block of a correction file; format.txt defines neither.
KNOWN_IDS = {b"OFR ", b"OFRX", b"HEAD", b"COMP", b"TAIL", b"RECV", b"MD5 ", b"DETA", b"CORR"}


KNOWN_NAMES = {"OFR ", "OFRX", "HEAD", "COMP", "TAIL", "RECV", "MD5 ", "DETA", "CORR"}


def crc32(data):
    return zlib.crc32(data) & 0xFFFFFFFF


def printable(block_id):
    return all(0x20 <= b < 0x7F for b in block_id)


def trailing_tag_start(data, start=0):
    """Where the trailing tags the host looks for begin, where they validate structurally: an ID3v1 tag at the end, an APEv2 footer or a Lyrics3 v2 end marker
    before it. The end of the data where there is none."""
    end = len(data)
    if end - start > 128 + 32 and data[end - 128:end - 125] == b"TAG":
        end -= 128
    if end - start > 32 and data[end - 32:end - 24] == b"APETAGEX":
        size = struct.unpack_from("<I", data, end - 32 + 12)[0]
        flags = struct.unpack_from("<I", data, end - 32 + 20)[0]
        tag = end - size - (32 if flags & 0x80000000 else 0)
        if size >= 32 and tag >= start:
            return tag
    if end - start > 9 + 6 and data[end - 9:end] == b"LYRICS200" and data[end - 15:end - 9].isdigit():
        size = int(data[end - 15:end - 9])
        if size >= 11 and end - 15 - size >= start:
            return end - 15 - size
    return end


def walk(data, start=0):
    """The blocks of data from start as dicts {id, offset, length (of the data), end}, by the analyzer's rule: four printable bytes and a length that ends
    inside the data. Returns (blocks, end) where end is the offset after the last accepted block."""
    blocks = []
    pos = start
    tag = trailing_tag_start(data, start)
    while pos + 8 <= len(data):
        block_id = data[pos:pos + 4]
        length = struct.unpack_from("<I", data, pos + 4)[0]
        if not printable(block_id):
            break
        known = block_id in KNOWN_IDS
        end = pos + 8 + length
        if not known and (end > tag or pos >= tag or not (end == tag or (end + 8 <= len(data) and printable(data[end:end + 4])
                                                                          and end + 8 + struct.unpack_from("<I", data, end + 4)[0] <= tag))):
            break
        if known and end > len(data):
            # A known block the data cuts short owns the rest of the data and ends the walk.
            blocks.append({"id": block_id.decode("ascii"), "offset": pos, "length": length, "end": end})
            pos = end
            break
        blocks.append({"id": block_id.decode("ascii"), "offset": pos, "length": length, "end": pos + 8 + length})
        pos += 8 + length
    return blocks, pos


def main_block(data, block):
    d = block["offset"] + 8
    total = int.from_bytes(data[d:d + 6], "little")
    sample_type, config, rate, encoder, compression = struct.unpack_from("<BBIHB", data, d + 6)
    result = {"samples": total, "sample_type": sample_type, "channel_config": config, "sample_rate": rate, "encoder_id": encoder,
              "version": (encoder >> 4) + 4500, "system": encoder & 15, "compression": compression, "mode": compression >> 3, "speedup": compression & 7,
              "size": block["length"]}
    if block["length"] >= 17:
        result["min_decoder_version"] = struct.unpack_from("<H", data, d + 15)[0] + 4500
    return result


def comp_block(data, block):
    d = block["offset"] + 8
    crc, samples, sample_type, config, reserved, encoder = struct.unpack_from("<IIBBHH", data, d)
    return {"crc": crc, "crc_ok": crc32(data[d + 4:block["end"]]) == crc, "samples": samples, "sample_type": sample_type, "channel_config": config,
            "reserved": reserved, "encoder_id": encoder, "offset": block["offset"], "size": block["length"] + 8}


def summary(data):
    """The figures the fixture scripts record: the main block, the COMP blocks' sample counts and sizes, and whether all their CRCs hold."""
    blocks, end = walk(data)
    result = {"length": len(data), "ids": [b["id"] for b in blocks], "end": end}
    if blocks and blocks[0]["id"] in MAIN_IDS and blocks[0]["length"] >= 15:
        result["main"] = main_block(data, blocks[0])
    comps = [comp_block(data, b) for b in blocks if b["id"] == "COMP" and b["length"] >= 14]
    result["blocks"] = [{"samples": c["samples"], "size": c["size"], "crc_ok": c["crc_ok"]} for c in comps]
    return result


def riff_chunks(data, start, end, skip_header=True):
    """The (id, size, offset) of the RIFF chunks in data[start:end], as the analyzer lists them: from 12 bytes after a RIFF, RF64 or BW64 header, from the
    start otherwise, up to the data chunk or a chunk that does not fit; at most 64."""
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


def fmt_fields(data, start, end):
    """The format tag and channels of the fmt chunk of a RIFF header in data[start:end], or None."""
    for chunk in riff_chunks(data, start, end):
        if chunk["id"] == "fmt " and chunk["offset"] + 8 + 4 <= end:
            tag, channels = struct.unpack_from("<HH", data, chunk["offset"] + 8)
            return {"tag": tag, "channels": channels}
    return None


def manifest(data, extra=None):
    """The manifest of a fixture: the blocks, the main block, every data block with its CRC-32, the MD5, the stored header, the stream info, the
    verification and the findings the analyzer must raise, all worked out here from the bytes by the specification."""
    blocks, end = walk(data)
    result = {"length": len(data), "end": end, "blocks": [dict(b) for b in blocks]}
    findings = []
    main = main_block(data, blocks[0]) if blocks and blocks[0]["id"] in MAIN_IDS and blocks[0]["length"] >= 15 else None
    result["main"] = main
    comps = [comp_block(data, b) for b in blocks if b["id"] == "COMP" and b["length"] >= 14 and b["end"] <= len(data)]
    result["comp"] = comps
    md5 = next((data[b["offset"] + 8:b["end"]].hex() for b in blocks if b["id"] == "MD5 " and b["length"] == 16), None)
    result["md5"] = md5
    head = next((b for b in blocks if b["id"] == "HEAD"), None)
    is_riff = head is not None and data[head["offset"] + 8:head["offset"] + 12] in (b"RIFF", b"RF64", b"BW64")
    result["head"] = None if head is None else {"offset": head["offset"] + 8, "length": head["length"],
                                                 "chunks": [c["id"] for c in riff_chunks(data, head["offset"] + 8, head["end"])] if is_riff else [],
                                                 "fmt": fmt_fields(data, head["offset"] + 8, head["end"])}
    tail = next((b for b in blocks if b["id"] == "TAIL"), None)
    result["tail"] = None if tail is None else {"offset": tail["offset"] + 8, "length": tail["length"],
                                                 "chunks": [c["id"] for c in riff_chunks(data, tail["offset"] + 8, tail["end"], skip_header=False)] if is_riff else []}
    if main is not None:
        channels = CHANNELS[main["channel_config"]] if main["channel_config"] < 2 else 2
        samples = main["samples"] // channels
        bits = BITS[main["sample_type"]] if main["sample_type"] < len(BITS) else 0
        total = sum(c["samples"] for c in comps)
        failed = sum(1 for c in comps if not c["crc_ok"])
        result["stream_info"] = {"codec": "OptimFROG", "sample_rate": main["sample_rate"], "channels": channels, "bits_per_sample": bits, "samples": samples,
                                 "encoder": f"OptimFROG {main['version'] // 1000}.{main['version'] % 1000:03d}"}
        covers_all = len(comps) > 0 and total == main["samples"] and all(c["sample_type"] == main["sample_type"] and c["channel_config"] == main["channel_config"] and c["encoder_id"] == main["encoder_id"] for c in comps)
        result["verification"] = {"checksums": [{"name": "block CRC-32", "stored": len(comps), "checked": len(comps), "failed": failed, "covers_all": covers_all},
                                                {"name": "MD5", "stored": 1 if md5 else 0, "checked": 0, "failed": 0}]}
        if any(b["id"] not in KNOWN_NAMES for b in blocks):
            findings.append({"code": "optimfrog.block-unknown"})
        if total != main["samples"]:
            findings.append({"code": "optimfrog.samples-mismatch"})
        if failed:
            findings.append({"code": "optimfrog.block-crc-mismatch"})
        if head is None:
            findings.append({"code": "optimfrog.head-missing"})
        if tail is None:
            findings.append({"code": "optimfrog.tail-missing"})
    if blocks and blocks[0]["id"] == "CORR":
        corr = []
        for b in blocks:
            stored = struct.unpack_from("<I", data, b["offset"] + 8)[0]
            corr.append({"offset": b["offset"], "size": b["length"] + 8, "crc": stored, "crc_ok": crc32(data[b["offset"] + 12:b["end"]]) == stored})
        result["corr"] = corr
        result["stream_info"] = {"codec": "OptimFROG correction data"}
        result["verification"] = {"checksums": [{"name": "block CRC-32", "stored": len(corr), "checked": len(corr), "failed": sum(1 for c in corr if not c["crc_ok"])}]}
        findings.append({"code": "optimfrog.correction-file"})
        if any(not c["crc_ok"] for c in corr):
            findings.append({"code": "optimfrog.block-crc-mismatch"})
    result["lossy"] = bool(comps) and all(c["reserved"] & 0xFF == 1 for c in comps)
    result["findings"] = findings
    if extra:
        result.update(extra)
    return result
