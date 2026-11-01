#!/usr/bin/env python3
"""The one unit extractor of the official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, decision 9).

It is not the analyzer and shares no code with it. For one AAC, AC-3 or E-AC-3 file it lists the units a decoder is fed
(an ADTS frame, an MP4 sample, an AC-3 syncframe or E-AC-3 access unit) with the file offset, the declared length and the
bytes the file holds of it, the byte ranges it skipped, and the configuration (AudioSpecificConfig, dac3 or dec3).
Every direct path of every platform reads this list, so differences between decoders are not differences between readers.

  unit_list.py [--tsv] file...      one JSON object per line, or a flat text form for the C harnesses
  unit_list.py --tsv-dir DIR file-or-folder...  writes DIR/<sha256 of the file>.units.tsv for each file (the Windows probe finds the lists by hash)

The flat form:
      F <sha256> <kind>              kind: adts, adif, mp4, ac3, eac3, unknown
      T <codec> <timescale>          codec: aac, ac-3, ec-3
      C <hex>                        configuration, when the container has one
      H <profile> <sfi> <channels>   the first ADTS header's fields
      U <offset> <declared> <received>
      S <offset> <length>            skipped bytes
      N <text>                       a note (why the file is skipped)
"""

import hashlib
import json
import os
import struct
import sys

MAX_DEPTH = 8
WORDS = {
    0: [64, 64, 80, 80, 96, 96, 112, 112, 128, 128, 160, 160, 192, 192, 224, 224, 256, 256, 320, 320, 384, 384, 448, 448, 512, 512, 640, 640, 768, 768,
        896, 896, 1024, 1024, 1152, 1152, 1280, 1280],
    1: [69, 70, 87, 88, 104, 105, 121, 122, 139, 140, 174, 175, 208, 209, 243, 244, 278, 279, 348, 349, 417, 418, 487, 488, 557, 558, 696, 697, 835, 836,
        975, 976, 1114, 1115, 1253, 1254, 1393, 1394],
    2: [96, 96, 120, 120, 144, 144, 168, 168, 192, 192, 240, 240, 288, 288, 336, 336, 384, 384, 480, 480, 576, 576, 672, 672, 768, 768, 960, 960, 1152, 1152,
        1344, 1344, 1536, 1536, 1728, 1728, 1920, 1920],
}


def boxes(data, start, end, depth=0):
    """(type, payload start, payload end, box start) of the boxes in data[start:end]; stops at the first box that does not fit."""
    pos = start
    while pos + 8 <= end and depth <= MAX_DEPTH:
        size, kind = struct.unpack_from(">I4s", data, pos)
        header = 8
        if size == 1:
            if pos + 16 > end:
                return
            size = struct.unpack_from(">Q", data, pos + 8)[0]
            header = 16
        elif size == 0:
            size = end - pos
        if size < header:
            return
        stop = min(pos + size, end)
        yield kind, pos + header, stop, pos
        pos += size


def find(data, start, end, path):
    for kind, s, e, _ in boxes(data, start, end):
        if kind == path[0]:
            if len(path) == 1:
                return s, e
            found = find(data, s, e, path[1:])
            if found:
                return found
    return None


def descriptor_config(data, start, end):
    """The DecoderSpecificInfo (tag 5) inside an esds payload (after its version and flags)."""
    pos = start + 4

    def read_len(p):
        n = 0
        for _ in range(4):
            if p >= end:
                return None, p
            b = data[p]
            p += 1
            n = (n << 7) | (b & 0x7F)
            if not b & 0x80:
                return n, p
        return n, p

    while pos < end:
        tag = data[pos]
        length, body = read_len(pos + 1)
        if length is None:
            return None
        if tag == 3:
            pos = body + 3
            flags = data[body + 2] if body + 2 < end else 0
            if flags & 0x80:
                pos += 2
            if flags & 0x40:
                pos += 1 + (data[pos] if pos < end else 0)
            if flags & 0x20:
                pos += 2
        elif tag == 4:
            pos = body + 13
        elif tag == 5:
            return data[body:body + length]
        else:
            pos = body + length
    return None


def mp4_units(data, result):
    size = len(data)
    moov = find(data, 0, size, [b"moov"])
    if not moov:
        result["note"] = "no moov box"
        return
    track = None
    for kind, s, e, _ in boxes(data, *moov):
        if kind != b"trak":
            continue
        hdlr = find(data, s, e, [b"mdia", b"hdlr"])
        if not hdlr or data[hdlr[0] + 8:hdlr[0] + 12] != b"soun":
            continue
        track = (s, e)
        break
    if track is None:
        result["note"] = "no audio track"
        return
    s, e = track
    tkhd = find(data, s, e, [b"tkhd"])
    track_id = struct.unpack_from(">I", data, tkhd[0] + (20 if data[tkhd[0]] == 1 else 12))[0] if tkhd else 1
    mdhd = find(data, s, e, [b"mdia", b"mdhd"])
    if mdhd:
        result["timescale"] = struct.unpack_from(">I", data, mdhd[0] + (20 if data[mdhd[0]] == 1 else 12))[0]
    stbl = find(data, s, e, [b"mdia", b"minf", b"stbl"])
    if not stbl:
        result["note"] = "no stbl"
        return
    stsd = find(data, stbl[0], stbl[1], [b"stsd"])
    if not stsd:
        result["note"] = "no stsd"
        return
    entry_start = stsd[0] + 8
    for kind, es, ee, _ in boxes(data, entry_start, stsd[1]):
        codec = {b"mp4a": "aac", b"ac-3": "ac-3", b"ec-3": "ec-3"}.get(kind)
        if codec is None:
            result["note"] = "sample entry " + kind.decode("latin-1")
            return
        result["codec"] = codec
        # AudioSampleEntry: 28 bytes, version 1 adds 16 and version 2 adds 36 before the child boxes.
        version = struct.unpack_from(">H", data, es + 8)[0]
        child = es + 28 + {0: 0, 1: 16, 2: 36}.get(version, 0)
        for ck, cs, ce, _ in boxes(data, child, ee):
            if codec == "aac" and ck == b"esds":
                config = descriptor_config(data, cs, ce)
                if config is not None:
                    result["config"] = config.hex()
            elif ck in (b"dac3", b"dec3"):
                result["config"] = data[cs:ce].hex()
            elif ck == b"wave" and codec == "aac":
                inner = find(data, cs, ce, [b"esds"])
                if inner:
                    config = descriptor_config(data, *inner)
                    if config is not None:
                        result["config"] = config.hex()
        break

    units = []
    stsz = find(data, stbl[0], stbl[1], [b"stsz"])
    stco = find(data, stbl[0], stbl[1], [b"stco"])
    co64 = find(data, stbl[0], stbl[1], [b"co64"])
    stsc = find(data, stbl[0], stbl[1], [b"stsc"])
    if stsz and (stco or co64) and stsc:
        fixed, count = struct.unpack_from(">II", data, stsz[0] + 4)
        sizes = []
        for i in range(count):
            p = stsz[0] + 12 + 4 * i
            if fixed:
                sizes.append(fixed)
            elif p + 4 <= stsz[1]:
                sizes.append(struct.unpack_from(">I", data, p)[0])
            else:
                break
        chunk_box, wide = (co64, True) if co64 else (stco, False)
        chunks = struct.unpack_from(">I", data, chunk_box[0] + 4)[0]
        offsets = []
        for i in range(chunks):
            p = chunk_box[0] + 8 + (8 if wide else 4) * i
            if p + (8 if wide else 4) > chunk_box[1]:
                break
            offsets.append(struct.unpack_from(">Q" if wide else ">I", data, p)[0])
        runs = []
        for i in range(struct.unpack_from(">I", data, stsc[0] + 4)[0]):
            p = stsc[0] + 8 + 12 * i
            if p + 12 > stsc[1]:
                break
            runs.append(struct.unpack_from(">III", data, p))
        index = 0
        for r, (first, per, _) in enumerate(runs):
            last = runs[r + 1][0] - 1 if r + 1 < len(runs) else len(offsets)
            for chunk in range(first, last + 1):
                if chunk - 1 >= len(offsets):
                    break
                pos = offsets[chunk - 1]
                for _ in range(per):
                    if index >= len(sizes):
                        break
                    units.append((pos, sizes[index]))
                    pos += sizes[index]
                    index += 1
    for kind, s2, e2, moof_start in boxes(data, 0, size):
        if kind != b"moof":
            continue
        for tk, ts, te, _ in boxes(data, s2, e2):
            if tk != b"traf":
                continue
            tfhd = find(data, ts, te, [b"tfhd"])
            if not tfhd:
                continue
            flags = struct.unpack_from(">I", data, tfhd[0])[0] & 0xFFFFFF
            tid = struct.unpack_from(">I", data, tfhd[0] + 4)[0]
            if tid != track_id:
                continue
            p = tfhd[0] + 8
            base = moof_start
            if flags & 1:
                base = struct.unpack_from(">Q", data, p)[0]
                p += 8
            if flags & 2:
                p += 4
            if flags & 8:
                p += 4
            default_size = 0
            if flags & 0x10:
                default_size = struct.unpack_from(">I", data, p)[0]
            for rk, rs, re_, _ in boxes(data, ts, te):
                if rk != b"trun":
                    continue
                tf = struct.unpack_from(">I", data, rs)[0] & 0xFFFFFF
                n = struct.unpack_from(">I", data, rs + 4)[0]
                p = rs + 8
                pos = base
                if tf & 1:
                    pos = base + struct.unpack_from(">i", data, p)[0]
                    p += 4
                if tf & 4:
                    p += 4
                for _ in range(n):
                    if p > re_:
                        break
                    if tf & 0x100:
                        p += 4
                    length = default_size
                    if tf & 0x200:
                        length = struct.unpack_from(">I", data, p)[0]
                        p += 4
                    if tf & 0x400:
                        p += 4
                    if tf & 0x800:
                        p += 4
                    units.append((pos, length))
                    pos += length
    for pos, length in sorted(set(units)):
        result["units"].append((pos, length, max(0, min(length, size - pos))))
    if not result["units"]:
        result["note"] = "no samples in the sample tables or fragments"


ADTS_RATES = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350]


def adts_header(data, pos):
    if pos + 7 > len(data) or data[pos] != 0xFF or data[pos + 1] & 0xF6 != 0xF0:
        return None
    sfi = (data[pos + 2] >> 2) & 15
    if sfi > 12:
        return None
    length = ((data[pos + 3] & 3) << 11) | (data[pos + 4] << 3) | (data[pos + 5] >> 5)
    minimum = 7 if data[pos + 1] & 1 else 9
    if length < minimum:
        return None
    return (data[pos + 2] >> 6, sfi, ((data[pos + 2] & 1) << 2) | (data[pos + 3] >> 6), length)


def adts_units(data, result):
    size = len(data)
    pos = 0
    skip_from = None
    while pos < size:
        h = adts_header(data, pos)
        valid = False
        if h:
            nxt = pos + h[3]
            # A header counts when the frame is followed by another header or by the end of the file, or is cut by it; a frame
            # followed by bytes that start no later frame (trailing junk) counts too, since a decoder reads it.
            valid = nxt >= size or adts_header(data, nxt) is not None or not any(adts_header(data, i) for i in range(nxt, size - 6))
        if valid:
            if skip_from is not None:
                result["skipped"].append((skip_from, pos - skip_from))
                skip_from = None
            if "adts" not in result:
                result["adts"] = h[:3]
            result["units"].append((pos, h[3], min(h[3], size - pos)))
            pos += h[3]
        else:
            if skip_from is None:
                skip_from = pos
            pos += 1
    if skip_from is not None:
        result["skipped"].append((skip_from, size - skip_from))


def ac3_units(data, result):
    size = len(data)
    pos = 0
    skip_from = None
    eac3 = None
    current = None
    while pos < size:
        length = None
        frame_eac3 = None
        independent = True
        if pos + 6 <= size and data[pos] == 0x0B and data[pos + 1] == 0x77:
            bsid = data[pos + 5] >> 3
            if bsid <= 8:
                fscod, code = data[pos + 4] >> 6, data[pos + 4] & 0x3F
                if fscod < 3 and code < 38:
                    length, frame_eac3 = WORDS[fscod][code] * 2, False
            elif 11 <= bsid <= 16:
                strmtyp, substream = data[pos + 2] >> 6, (data[pos + 2] >> 3) & 7
                length, frame_eac3 = (((data[pos + 2] & 7) << 8 | data[pos + 3]) + 1) * 2, True
                independent = not (strmtyp == 1 or substream != 0)
                if strmtyp == 3 or length < 8:
                    length = None
        if length is not None:
            if skip_from is not None:
                result["skipped"].append((skip_from, pos - skip_from))
                skip_from = None
            eac3 = frame_eac3 if eac3 is None else eac3
            received = min(length, size - pos)
            # An E-AC-3 access unit is the independent frame of substream 0 and the frames that follow it
            # until the next one (dependent frames and other substreams), as an MP4 sample holds them.
            if frame_eac3 and not independent and current is not None:
                u = result["units"][current]
                result["units"][current] = (u[0], pos + length - u[0], min(pos + length, size) - u[0])
            else:
                result["units"].append((pos, length, received))
                current = len(result["units"]) - 1
            pos += length
        else:
            if skip_from is None:
                skip_from = pos
            pos += 1
    if skip_from is not None:
        result["skipped"].append((skip_from, size - skip_from))
    result["kind"] = "eac3" if eac3 else "ac3"
    result["codec"] = "ec-3" if eac3 else "ac-3"


def extract(path):
    data = open(path, "rb").read()
    result = {"file": path, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "kind": "unknown", "units": [], "skipped": []}
    try:
        if data[4:8] == b"ftyp" or data[4:8] in (b"moov", b"mdat", b"free", b"skip"):
            result["kind"] = "mp4"
            mp4_units(data, result)
        elif data[:4] == b"ADIF":
            result["kind"] = "adif"
            result["codec"] = "aac"
            result["note"] = "ADIF has no frame lengths; the harness uses FDK's own transport"
        elif data[:2] == b"\x0b\x77":
            ac3_units(data, result)
        elif adts_header(data, 0):
            result["kind"] = "adts"
            result["codec"] = "aac"
            adts_units(data, result)
        else:
            # Junk before the first frame: ADTS if a valid chain starts within the first 64 KiB.
            adts_units(data, result)
            if result["units"]:
                result["kind"] = "adts"
                result["codec"] = "aac"
            else:
                result["skipped"] = []
                ac3_units(data, result)
                if not result["units"]:
                    result["kind"] = "unknown"
                    result["skipped"] = []
                    result["note"] = "neither ADTS, MP4, AC-3 nor E-AC-3 frames found"
    except (struct.error, IndexError, ValueError) as error:
        result["units"] = []
        result["note"] = "extractor failed: %s" % error
    return result


def tsv(result):
    lines = ["F %s %s" % (result["sha256"], result["kind"])]
    if "codec" in result:
        lines.append("T %s %d" % (result["codec"], result.get("timescale", 0)))
    if result.get("config"):
        lines.append("C " + result["config"])
    if "adts" in result:
        lines.append("H %d %d %d" % tuple(result["adts"]))
    for u in result["units"]:
        lines.append("U %d %d %d" % u)
    for s in result["skipped"]:
        lines.append("S %d %d" % s)
    if "note" in result:
        lines.append("N " + result["note"])
    return "\n".join(lines) + "\n"


def main(argv):
    if "--tsv-dir" in argv:
        i = argv.index("--tsv-dir")
        directory = argv[i + 1]
        paths = []
        for item in argv[:i] + argv[i + 2:]:
            if os.path.isdir(item):
                paths.extend(sorted(os.path.join(item, n) for n in os.listdir(item) if os.path.isfile(os.path.join(item, n))))
            else:
                paths.append(item)
        for path in paths:
            result = extract(path)
            with open("%s/%s.units.tsv" % (directory, result["sha256"]), "w", encoding="utf-8", newline="\n") as target:
                target.write(tsv(result))
        return 0
    flat = "--tsv" in argv
    for path in [a for a in argv if a != "--tsv"]:
        result = extract(path)
        if flat:
            sys.stdout.write(tsv(result))
        else:
            print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
