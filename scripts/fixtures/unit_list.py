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
    """(DecoderSpecificInfo (tag 5), objectTypeIndication) inside an esds payload (after its version and flags); either may be None."""
    pos = start + 4
    oti = None

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
            return None, oti
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
            oti = data[body] if body < end else None
            pos = body + 13
        elif tag == 5:
            return data[body:body + length], oti
        else:
            pos = body + length
    return None, oti


def protected(data, trak_start, trak_end):
    """Whether the track carries a protection scheme: a sinf, schm or tenc box among the children of trak, mdia, minf, stbl and of each
    sample entry, where encryption puts them (or misplaces them, as `cenc-sinf-in-trak.m4a` does)."""
    marks = (b"sinf", b"schm", b"tenc")
    levels = [(trak_start, trak_end)]
    for path in ([b"mdia"], [b"mdia", b"minf"], [b"mdia", b"minf", b"stbl"]):
        found = find(data, trak_start, trak_end, path)
        if found:
            levels.append(found)
    for s, e in levels:
        if any(kind in marks for kind, _, _, _ in boxes(data, s, e)):
            return True
    stsd = find(data, trak_start, trak_end, [b"mdia", b"minf", b"stbl", b"stsd"])
    if stsd:
        for kind, es, ee, _ in boxes(data, stsd[0] + 8, stsd[1]):
            if kind in (b"enca", b"encv"):
                return True
            version = struct.unpack_from(">H", data, es + 8)[0] if es + 10 <= ee else 0
            child = es + 28 + {0: 0, 1: 16, 2: 36}.get(version, 0)
            if any(k in marks for k, _, _, _ in boxes(data, child, ee)):
                return True
    return False


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
                config, oti = descriptor_config(data, cs, ce)
                result["oti"] = oti
                if config is not None:
                    result["config"] = config.hex()
            elif ck in (b"dac3", b"dec3"):
                result["config"] = data[cs:ce].hex()
            elif ck == b"wave" and codec == "aac":
                inner = find(data, cs, ce, [b"esds"])
                if inner:
                    config, oti = descriptor_config(data, *inner)
                    result["oti"] = oti
                    if config is not None:
                        result["config"] = config.hex()
        break

    # The track is encrypted when it carries a protection scheme box; the search is for the four-character codes in the track's bytes.
    if protected(data, s, e):
        result["note"] = "encrypted track (sinf or tenc box): no reference"
        result["encrypted"] = True
        return
    if result.get("codec") == "aac" and result.get("oti") not in (0x40, 0x66, 0x67, 0x68):
        result["note"] = "mp4a with object type indication %s is not AAC" % (hex(result["oti"]) if result.get("oti") is not None else "none")
        result["codec"] = "other"
        return

    units = []
    stsz = find(data, stbl[0], stbl[1], [b"stsz"])
    stz2 = find(data, stbl[0], stbl[1], [b"stz2"]) if not stsz else None
    stco = find(data, stbl[0], stbl[1], [b"stco"])
    co64 = find(data, stbl[0], stbl[1], [b"co64"])
    stsc = find(data, stbl[0], stbl[1], [b"stsc"])
    if stz2 and (stco or co64) and stsc:
        # stz2, the compact sample size box: 4, 8 or 16 bits per sample (ISO/IEC 14496-12, 8.7.3.3)
        field = data[stz2[0] + 7]
        count = struct.unpack_from(">I", data, stz2[0] + 8)[0]
        count = min(count, (stz2[1] - stz2[0] - 12) * 8 // max(field, 4))
        sizes = []
        for i in range(count):
            if field == 16:
                sizes.append(struct.unpack_from(">H", data, stz2[0] + 12 + 2 * i)[0])
            elif field == 8:
                sizes.append(data[stz2[0] + 12 + i])
            elif field == 4:
                b = data[stz2[0] + 12 + i // 2]
                sizes.append(b >> 4 if i % 2 == 0 else b & 15)
        stsz = (0, 0)
        fixed_sizes = sizes
    else:
        fixed_sizes = None
    if (stsz or fixed_sizes is not None) and (stco or co64) and stsc:
        fixed, count = (0, 0) if fixed_sizes is not None else struct.unpack_from(">II", data, stsz[0] + 4)
        # A declared count never allocates by itself: with a table it is bounded by the table, with a fixed size by the file.
        if fixed_sizes is None:
            count = min(count, (stsz[1] - stsz[0] - 12) // 4) if not fixed else min(count, size // max(fixed, 1) + 1)
        sizes = []
        if fixed_sizes is not None:
            sizes = fixed_sizes
            count = 0
        for i in range(count):
            p = stsz[0] + 12 + 4 * i
            if fixed:
                sizes.append(fixed)
            elif p + 4 <= stsz[1]:
                sizes.append(struct.unpack_from(">I", data, p)[0])
            else:
                break
        chunk_box, wide = (co64, True) if co64 else (stco, False)
        chunks = min(struct.unpack_from(">I", data, chunk_box[0] + 4)[0], (chunk_box[1] - chunk_box[0] - 8) // (8 if wide else 4))
        offsets = []
        for i in range(chunks):
            p = chunk_box[0] + 8 + (8 if wide else 4) * i
            if p + (8 if wide else 4) > chunk_box[1]:
                break
            offsets.append(struct.unpack_from(">Q" if wide else ">I", data, p)[0])
        runs = []
        for i in range(min(struct.unpack_from(">I", data, stsc[0] + 4)[0], (stsc[1] - stsc[0] - 8) // 12)):
            p = stsc[0] + 8 + 12 * i
            if p + 12 > stsc[1]:
                break
            runs.append(struct.unpack_from(">III", data, p))
        index = 0
        for r, (first, per, _) in enumerate(runs):
            last = runs[r + 1][0] - 1 if r + 1 < len(runs) else len(offsets)
            if first < 1:
                result["note"] = "stsc names chunk 0"
                continue
            for chunk in range(first, min(last, len(offsets)) + 1):
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
            cursor = base
            for rk, rs, re_, _ in boxes(data, ts, te):
                if rk != b"trun":
                    continue
                tf = struct.unpack_from(">I", data, rs)[0] & 0xFFFFFF
                n = struct.unpack_from(">I", data, rs + 4)[0]
                p = rs + 8
                per_sample = 4 * sum(1 for bit in (0x100, 0x200, 0x400, 0x800) if tf & bit)
                n = min(n, (re_ - p) // per_sample if per_sample else size // max(default_size, 1))
                pos = cursor
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
                cursor = pos
    for pos, length in sorted(units, key=lambda u: u[0]):
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
    """ADTS frames in a chain: a frame counts when it starts where the last one ended with a header of the same profile, rate and
    channels, or when the next header follows it at its end with the same fields; a run of one frame counts only when the file is that
    one frame. The bytes between are the skipped ranges."""
    size = len(data)
    pos = 0
    skip_from = None
    previous = None
    frames = []
    while pos < size:
        h = adts_header(data, pos)
        valid = False
        if h:
            nxt = pos + h[3]
            same_before = previous is not None and previous[0] == pos and previous[1] == h[:3]
            h2 = adts_header(data, nxt) if nxt < size else None
            valid = same_before or (h2 is not None and h2[:3] == h[:3]) or (nxt >= size and pos == 0 and size - pos == h[3])
        if valid:
            if skip_from is not None:
                result["skipped"].append((skip_from, pos - skip_from))
                skip_from = None
            if "adts" not in result:
                result["adts"] = h[:3]
            # A unit ends where the next frame of the same stream begins: a header inside the declared length that starts a chain closes
            # the frame there (a cut or wrongly sized frame), and the unit keeps only the bytes up to it.
            end = min(pos + h[3], size)
            for q in range(pos + 1, end):
                hq = adts_header(data, q)
                if hq and hq[:3] == h[:3]:
                    nq = q + hq[3]
                    hn = adts_header(data, nq) if nq < size else None
                    if nq == size or (hn is not None and hn[:3] == h[:3]):
                        end = q
                        break
            frames.append((pos, h[3], end - pos))
            previous = (end, h[:3])
            pos = end if end < pos + h[3] else pos + h[3]
        else:
            if skip_from is None:
                skip_from = pos
            pos += 1
    if skip_from is not None:
        result["skipped"].append((skip_from, size - skip_from))
    result["units"] = frames


def crc16(data):
    """The CRC of A/52 (x^16 + x^15 + x^2 + 1, start value 0): over a whole syncframe after its sync word it comes to zero."""
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x8005) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def ac3_header(data, pos):
    """(length, is_eac3, independent, key) of the AC-3 or E-AC-3 syncframe at pos, or None; bsid 9 and 10 are not read."""
    if pos + 8 > len(data) or data[pos] != 0x0B or data[pos + 1] != 0x77:
        return None
    bsid = data[pos + 5] >> 3
    if bsid <= 8:
        fscod, code = data[pos + 4] >> 6, data[pos + 4] & 0x3F
        if fscod < 3 and code < 38:
            return WORDS[fscod][code] * 2, False, True, (False, fscod)
    elif 11 <= bsid <= 16:
        strmtyp, substream = data[pos + 2] >> 6, (data[pos + 2] >> 3) & 7
        length = (((data[pos + 2] & 7) << 8 | data[pos + 3]) + 1) * 2
        if strmtyp != 3 and length >= 8:
            return length, True, not (strmtyp == 1 or substream != 0), (True, (data[pos + 4] >> 6))
    return None


def ac3_units(data, result):
    """AC-3 and E-AC-3 syncframes in a chain (the next syncframe at the end of the frame, or the end of the file; a frame after a
    skipped range counts when the next one follows); an E-AC-3 access unit is the independent frame of substream 0 and the frames up
    to the next one."""
    size = len(data)
    pos = 0
    skip_from = None
    previous_end = None
    current = None
    units = []
    eac3 = None
    while pos < size:
        h = ac3_header(data, pos)
        valid = False
        if h:
            nxt = pos + h[0]
            valid = previous_end == pos or nxt >= size or ac3_header(data, nxt) is not None
        if valid:
            if skip_from is not None:
                result["skipped"].append((skip_from, pos - skip_from))
                skip_from = None
            eac3 = h[1] if eac3 is None else eac3
            # As for ADTS, a frame ends where the next syncframe of the same kind begins inside its declared length.
            end = min(pos + h[0], size)
            for q in range(pos + 1, end):
                hq = ac3_header(data, q)
                if hq and hq[3] == h[3]:
                    nq = q + hq[0]
                    if nq == size or ac3_header(data, nq) is not None:
                        end = q
                        break
            result["frameCount"] = result.get("frameCount", 0) + 1
            if end == min(pos + h[0], size) and end - pos == h[0] and crc16(data[pos + 2:end]) == 0:
                result["crcOk"] = result.get("crcOk", 0) + 1
            if h[1] and not h[2] and current is not None:
                u = units[current]
                units[current] = (u[0], pos + h[0] - u[0], end - u[0])
            else:
                units.append((pos, h[0], end - pos))
                current = len(units) - 1
            previous_end = end
            pos = end
        else:
            if skip_from is None:
                skip_from = pos
            pos += 1
    if skip_from is not None:
        result["skipped"].append((skip_from, size - skip_from))
    result["units"] = units
    result["eac3"] = bool(eac3)


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
        elif len(data) >= 6 and data[:2] == b"\x0b\x77" and data[5] >> 3 in (9, 10):
            result["codec"] = "ac-3"
            result["note"] = "bsid %d (not read by this extractor)" % (data[5] >> 3)
        else:
            # Neither ADIF nor MP4: the format whose chain of frames covers more bytes of the file decides, and the record says what the other covered.
            candidates = {}
            for name, function in (("adts", adts_units), ("ac3", ac3_units)):
                trial = {"units": [], "skipped": []}
                function(data, trial)
                candidates[name] = (sum(u[2] for u in trial["units"]), trial)
            best = max(candidates, key=lambda k: candidates[k][0])
            # The format whose chain begins first decides when both have a chain of two frames or more: look-alike headers inside the frames of
            # the other (`junk-adts-in-frames.eac3`) begin later, and cover less or equally.
            chains = {k: v[1]["units"] for k, v in candidates.items() if len(v[1]["units"]) >= 2}
            if len(chains) == 2:
                best = min(chains, key=lambda k: (chains[k][0][0], -candidates[k][0]))
                # Look-alike ADTS headers inside AC-3 frames cannot pass an AC-3 CRC: when the AC-3 chain has more than half of its syncframes (dependent ones included)
                # CRC-clean it decides, wherever the chains begin.
                ac3_ok = candidates["ac3"][1].get("crcOk", 0)
                if ac3_ok * 2 > candidates["ac3"][1].get("frameCount", 0):
                    best = "ac3"
            other = "ac3" if best == "adts" else "adts"
            if candidates[best][0] == 0 or len(candidates[best][1]["units"]) < 2 and len(data) > candidates[best][0] + 64:
                result["note"] = "neither ADTS nor AC-3 frames in a chain (ADTS %d bytes, AC-3 %d bytes)" % (candidates["adts"][0], candidates["ac3"][0])
            else:
                trial = candidates[best][1]
                result["units"], result["skipped"] = trial["units"], trial["skipped"]
                if best == "adts":
                    result["kind"], result["codec"] = "adts", "aac"
                    if trial["units"]:
                        h = adts_header(data, trial["units"][0][0])
                        result["adts"] = h[:3]
                else:
                    result["kind"] = "eac3" if trial["eac3"] else "ac3"
                    result["codec"] = "ec-3" if trial["eac3"] else "ac-3"
                result["detected"] = "%s covers %d bytes, %s %d" % (best, candidates[best][0], other, candidates[other][0])
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
