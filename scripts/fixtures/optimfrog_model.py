"""A reader for OptimFROG 4.5xx and later files, written from the author's specification (SDK/Documentation/format.txt: the blocks, the main block, the
COMP block, appendices A to D) and from what the encoder-written fixtures show, apart from the analyzer. It works out the blocks of a file for the fixture
scripts and, in manifest(), each fixture's manifest.

What the files add to the specification:

- A COMP block's CRC-32 is the plain IEEE (zlib) CRC-32 over the block's data after the CRC field (luckynight.ofr, OptimFROG 4.509; every encoder-written
  fixture).
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
KNOWN_IDS = {b"OFR ", b"HEAD", b"COMP", b"TAIL", b"RECV", b"MD5 "}


def crc32(data):
    return zlib.crc32(data) & 0xFFFFFFFF


def printable(block_id):
    return all(0x20 <= b < 0x7F for b in block_id)


def walk(data, start=0):
    """The blocks of data from start as dicts {id, offset, length (of the data), end}, by the analyzer's rule: four printable bytes and a length that ends
    inside the data. Returns (blocks, end) where end is the offset after the last accepted block."""
    blocks = []
    pos = start
    while pos + 8 <= len(data):
        block_id = data[pos:pos + 4]
        length = struct.unpack_from("<I", data, pos + 4)[0]
        if not printable(block_id) or pos + 8 + length > len(data):
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
    if blocks and blocks[0]["id"] == "OFR " and blocks[0]["length"] >= 15:
        result["main"] = main_block(data, blocks[0])
    comps = [comp_block(data, b) for b in blocks if b["id"] == "COMP" and b["length"] >= 14]
    result["blocks"] = [{"samples": c["samples"], "size": c["size"], "crc_ok": c["crc_ok"]} for c in comps]
    return result
