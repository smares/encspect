"""Reads Musepack streams the way the fixtures' manifests describe them, from the sources the parser
cites: the SV8 specification for packets, libmpcdec r495 for SV7 headers, frames and sample counts,
libmpcdec 1.2.6 for versions 4 to 6. Written apart from the analyzer, so the tests compare two
readings; crosscheck_musepack_fixtures.py confirms this one with mpcdec, ffprobe and the decoders' own
frame reading (mpcdec_frames.c, also built over libmpcdec 1.2.6 and its C++ ancestor).
"""

import struct
import zlib

FRAME = 1152
DELAY = 481
RATES = [44100, 48000, 37800, 32000]
PROFILES = ["n.a.", "Unstable/Experimental", "n.a.", "n.a.", "n.a.", "quality 0", "quality 1", "Telephone", "Thumb", "Radio", "Standard",
            "Extreme", "Insane", "BrainDead", "quality 9", "quality 10"]
# libmpcdec r495 huffman.c mpc_table_HuffHdr: (code, length, value).
HUFF_HDR = [(0x8000, 1, 0), (0x6000, 3, 1), (0x5E00, 7, -4), (0x5D80, 9, 3), (0x5D00, 9, 4), (0x5C00, 8, -5), (0x5800, 6, 2), (0x5000, 5, -3),
            (0x4000, 4, -2), (0x0, 2, -1)]


class Words:
    """A bitstream of 32-bit little-endian words read from their most significant bit."""

    def __init__(self, data):
        padded = data + bytes(-len(data) % 4) + bytes(8)
        self.bits = len(data) * 8
        self.words = struct.unpack(f"<{len(padded) // 4}I", padded)

    def read(self, bit, count):
        if count == 0:
            return 0
        word = bit >> 5
        both = (self.words[word] << 32) | self.words[word + 1] if word + 1 < len(self.words) else 0
        return ((both << (bit & 31)) & ((1 << 64) - 1)) >> (64 - count)


def sv7_header(w):
    return {"version": w.read(4, 4), "pns": w.read(0, 4), "frames": w.read(32, 32), "is": w.read(64, 1), "ms": w.read(65, 1),
            "max_band": w.read(66, 6), "profile": w.read(72, 4), "link": w.read(76, 2), "rate": w.read(78, 2), "max_level": w.read(80, 16),
            "title_gain": signed16(w.read(96, 16)), "title_peak": w.read(112, 16), "album_gain": signed16(w.read(128, 16)),
            "album_peak": w.read(144, 16), "gapless": w.read(160, 1), "last": w.read(161, 11), "fast_seek": w.read(172, 1),
            "unused": w.read(173, 19), "encoder": w.read(192, 8)}


def signed16(value):
    return value - 65536 if value >= 32768 else value


def bands(w, bit, end, max_band, ms):
    """(used subbands, mid/side mask, noise channels, overrun, invalid) of the frame data at bit."""
    left, right = w.read(bit, 4), w.read(bit + 4, 4)
    position = bit + 8
    used = mask = noise = 0
    invalid = False
    for band in range(max_band + 1):
        if band:
            values = []
            for previous in (left, right):
                code = w.read(position, 16)
                for entry, length, value in HUFF_HDR:
                    if code >= entry:
                        position += length
                        if value == 4:
                            values.append(w.read(position, 4))
                            position += 4
                        else:
                            values.append(previous + value)
                        break
            left, right = values
        invalid |= not (-1 <= left <= 17 and -1 <= right <= 17)
        noise += (left == -1) + (right == -1)
        if left or right:
            if ms:
                if w.read(position, 1):
                    mask |= 1 << band
                position += 1
            used = band + 1
        if position > end:
            return used, mask, noise, True, invalid
    return used, mask, noise, position > end, invalid


def sv7(data):
    """The SV7 stream at the start of data: header, whole frames, where it ends, the output samples.
    libmpcdec r495 takes the last frame's samples from the 11 bits after it with true gapless
    (mpc_decoder_decode_frame) and then decodes one more frame where they and the filter's delay pass
    a frame; mppenc writes that frame right after, so a tag at the next word, or resolutions that run
    past the length or out of range, mean it is not there."""
    w = Words(data)
    h = sv7_header(w)
    bit, frames, truncated, extra_missing, final = 200, [], False, False, None
    for i in range(h["frames"] + 1):
        if i == h["frames"]:
            last = (final if h["gapless"] and final is not None else h["last"]) or FRAME
            needed = h["gapless"] and h["frames"] and last + DELAY > FRAME
            if not needed:
                break
            word = (bit + 31) // 32 * 4
            tag = any(data[word:word + len(t)] == t for t in (b"APETAGEX", b"ID3", b"TAG"))
            if bit + 20 > w.bits or tag or any(bands(w, bit + 20, min(bit + 20 + w.read(bit, 20), w.bits), min(h["max_band"], 31), h["ms"])[3:]):
                extra_missing = True
                break
        if bit + 20 > w.bits:
            truncated = bit < w.bits
            break
        length = w.read(bit, 20)
        data_end = bit + 20 + length
        end = data_end + (11 if i == h["frames"] - 1 else 0)
        if end > w.bits:
            truncated = True
            break
        used, mask, noise, overrun, invalid = bands(w, bit + 20, min(data_end, w.bits), min(h["max_band"], 31), h["ms"])
        frames.append({"bit": bit, "length": length, "used": used, "ms": mask, "noise": noise, "overrun": overrun, "invalid": invalid})
        if i == h["frames"] - 1:
            final = w.read(data_end, 11)
        bit = end
    stream_end = len(data) if truncated else ((bit + 31) // 32) * 4
    last = (final if h["gapless"] and final is not None else h["last"]) or FRAME
    announced = 0 if not h["frames"] else h["frames"] * FRAME - FRAME + last if h["gapless"] else h["frames"] * FRAME - DELAY
    samples = max(0, min(announced, len(frames) * FRAME - DELAY))
    return {"header": h, "frames": frames, "end": stream_end, "truncated": truncated, "extra_missing": extra_missing, "final": final,
            "samples": samples, "rate": RATES[h["rate"]], "frame_bits": sum(f["length"] + 20 for f in frames) + (11 if final is not None else 0)}


# libmpcdec 1.2.6 huffsv46.c mpc_table_Region_A, B, C: (code, length, index into Q_res) for subbands
# 0 to 10, 11 to 22 and 23 up, codes left-aligned in 32 bits; mpc_decoder.c Q_res: the resolutions.
REGION_A = [(2147483648, 1, 1), (2013265920, 5, 3), (1946157056, 6, 4), (1912602624, 7, 7), (1895825408, 8, 8), (1887436800, 9, 9),
            (1883242496, 10, 10), (1881145344, 11, 11), (1880096768, 12, 12), (1879572480, 13, 13), (1879310336, 14, 14), (1879048192, 14, 15),
            (1744830464, 5, 5), (1610612736, 5, 6), (1073741824, 3, 0), (0, 2, 2)]
REGION_B = [(2147483648, 1, 1), (1073741824, 2, 0), (536870912, 3, 2), (268435456, 4, 3), (134217728, 5, 4), (67108864, 6, 5), (33554432, 7, 6),
            (0, 7, 7)]
REGION_C = [(2147483648, 1, 0), (1073741824, 2, 1), (536870912, 3, 2), (0, 3, 3)]
Q_RES_A = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 17]
Q_RES_B = [0, 1, 2, 3, 4, 5, 6, 17]
Q_RES_C = [0, 1, 2, 17]


def old_header(w):
    """libmpcdec 1.2.6 streaminfo_read_header_sv6."""
    version = w.read(11, 10)
    return {"bitrate": w.read(0, 9), "is": w.read(9, 1), "ms": w.read(10, 1), "version": version, "max_band": w.read(21, 5),
            "block": w.read(26, 6), "frames": w.read(32, 16) if version == 4 else w.read(32, 32), "data": 48 if version == 4 else 64}


def old_resolution(w, position, band):
    table, resolutions = (REGION_A, Q_RES_A) if band < 11 else (REGION_B, Q_RES_B) if band <= 22 else (REGION_C, Q_RES_C)
    code = w.read(position, 32)
    for entry, length, index in table:
        if code >= entry:
            return position + length, resolutions[index]


def last_frame_output(given):
    """The samples libmpcdec 1.2.6 (mpc_decoder_decode_internal) outputs for the last frame of version
    6, from the 11 bits after it: 0 stands for 1,152, and the synthesis filter's delay follows,
    wrapping at a frame."""
    samples = given or FRAME
    decay = (samples + DELAY) % FRAME
    return FRAME + decay if samples + DELAY >= FRAME else decay


def old(data):
    """A stream of version 4 to 6 with a variable bitrate, no intensity stereo and one frame per length,
    as libmpcdec 1.2.6 reads it: mpc_decoder_read_bitstream_sv6 decodes each frame's resolutions (and
    mid/side flags) by subband. In versions 4 and 5 the decoder stops before the last counted frame,
    which the encoders leave unfinished; in version 6 it decodes it too and takes its samples from the
    11 bits after it."""
    w = Words(data)
    h = old_header(w)
    readable = h["version"] in (4, 5, 6) and h["bitrate"] == 0 and not h["is"] and h["block"] == 1
    keeps_last = h["version"] == 6
    frames, bit = [], h["data"]
    for i in range((h["frames"] if keeps_last else h["frames"] - 1) if readable else 0):
        length = w.read(bit, 20)
        position, used, mask = bit + 20, 0, 0
        for band in range(min(h["max_band"], 31) + 1):
            position, left = old_resolution(w, position, band)
            flag = w.read(position, 1) if h["ms"] else 0
            position += 1 if h["ms"] else 0
            position, right = old_resolution(w, position, band)
            if left or right:
                used = band + 1
                mask |= flag << band
            if position > bit + 20 + length:
                break
        frames.append({"length": length, "used": used, "ms": mask, "overrun": position > bit + 20 + length})
        bit += 20 + length
    final = w.read(bit, 11) if readable and keeps_last and frames else None
    last_end = bit + 20 + w.read(bit, 20) if readable and not keeps_last else None
    decoded = len(frames) if readable else max(0, h["frames"] - 1) if h["version"] in (4, 5) else 0
    samples = (decoded - 1) * FRAME + last_frame_output(final) - DELAY if final is not None else max(0, decoded * FRAME - DELAY)
    return {"header": h, "readable": readable, "frames": frames, "last_runs_past": last_end - w.bits if last_end is not None else None,
            "final": final, "samples": samples, "frame_bits": sum(f["length"] + 20 for f in frames) + (11 if final is not None else 0)}


def size(data, position, limit=None):
    """A size field (SV8 specification): (value, position after it), or None."""
    limit = len(data) if limit is None else limit
    value = 0
    for n in range(9):
        if position + n >= limit:
            return None
        b = data[position + n]
        value = (value << 7) | (b & 0x7F)
        if not b & 0x80:
            return value, position + n + 1
    return None


def packets(data):
    """The SV8 packets after the magic number: (offset, length, key, declared size, header length), and
    the offset where reading stopped."""
    position, found = 4, []
    while position < len(data):
        if position + 3 > len(data):
            break
        read = size(data, position + 2)
        if read is None:
            break
        declared, after = read
        header = after - position
        if declared < header:
            break
        length = min(declared, len(data) - position)
        found.append((position, length, data[position:position + 2].decode("latin-1"), declared, header))
        position += length
        if data[position - length:position - length + 2] == b"SE":
            break
    return found, position


def sv8(data):
    """The SV8 stream at the start of data: packets, the stream header's fields, the samples."""
    found, stop = packets(data)
    sh = None
    for offset, length, key, declared, header in found:
        if key == "SH" and sh is None:
            p = data[offset + header:offset + length]
            if len(p) < 4:
                continue
            crc = struct.unpack(">I", p[:4])[0]
            # A header cut before its two coding bytes keeps the sample count and the silence as far as
            # they were read, and reports no rate or channels (and counts nothing: the analyzer
            # takes the sample count only from a header it reads whole).
            sh = {"crc": crc, "crc_ok": zlib.crc32(p[4:]) == crc, "version": p[4] if len(p) > 4 else None, "samples": None, "silence": None,
                  "complete": False}
            read = size(p, 5) if len(p) > 5 else None
            if read:
                sh["samples"], at = read
                read = size(p, at)
                if read:
                    sh["silence"], at = read
                    if at + 2 <= len(p):
                        sh.update({"complete": True, "rate": p[at] >> 5, "bands": (p[at] & 31) + 1, "channels": (p[at + 1] >> 4) + 1,
                                   "ms": (p[at + 1] >> 3) & 1, "power": p[at + 1] & 7})
    complete = sh is not None and sh["complete"]
    audio = [(o, l) for o, l, k, d, h in found if k == "AP" and d == l]
    frames, audio_bytes = 0, 0
    if sh:
        per = 4 ** sh["power"] if complete else 64
        expected = (sh["samples"] + DELAY + FRAME - 1) // FRAME if complete and sh["samples"] else None
        for i, (_, length) in enumerate(audio):
            left = expected - i * per if expected is not None and i == len(audio) - 1 else per
            count = left if 0 < left <= per else per
            # Decoders stop at the sample count, so the packets after it hold no frames they decode.
            if expected is not None:
                count = min(count, max(0, expected - frames))
            frames += count
            audio_bytes += length if count else 0
    samples = max(0, min(sh["samples"], frames * FRAME - DELAY) - sh["silence"]) if complete else 0
    return {"packets": found, "stop": stop, "sh": sh, "complete": complete, "frames": frames, "samples": samples,
            "rate": RATES[sh["rate"]] if complete and sh["rate"] < 4 else 0, "audio_bytes": audio_bytes}


def ei_name(major, minor, build):
    """libmpcdec r495 mpc_get_encoder_string, as the analyzer spells it."""
    version = major * 100 + minor
    if version <= 116:
        return libmpcdec_name(version)
    return f"{'stable' if minor % 2 == 0 else 'unstable'} {major}.{minor}.{build}"


def sv7_name(version):
    """libmpcdec r495's name for the SV7 header's version byte: above 116 its else branch reads major,
    minor and build from the bits above the byte, all 0 (only SV8 recomputes the version first)."""
    return libmpcdec_name(version) if version <= 116 else "stable 0.0.0"


def libmpcdec_name(version):
    if version == 0:
        return "Buschmann 1.7.0 to 1.7.9 or Klemm 0.90 to 1.05"
    if version % 10 == 0:
        return f"Release {version // 100}.{version // 10 % 10}"
    if version % 2 == 0:
        return f"Beta {version // 100}.{version % 100:02d}"
    return f"Alpha {version // 100}.{version % 100:02d}"


def manifest(data, start, end, extra=None):
    """The manifest of the Musepack stream in data[start:end]: its kind, header fields, frames or
    packets, and the stream info; extra adds regions, findings and cross-check notes."""
    stream = data[start:end]
    result = {"length": len(data)}
    if stream[:4] == b"MPCK":
        s = sv8(stream)
        sh = s["sh"]
        result["version"] = 8
        # Once 4,096 packets that are no audio are detailed, runs of packets that do not carry audio are
        # one node each, with their count, except the stream end packet and the first 8 each of the
        # stream header, ReplayGain and encoder info packets read after them.
        detailed, listed, runs, late = 0, [], [], {"SH": 0, "RG": 0, "EI": 0}
        for o, l, k, d, h in s["packets"]:
            folded = k not in ("AP", "SE") and d == l and detailed >= 4096
            if folded and k in late and late[k] < 8:
                late[k] += 1
                folded = False
            if folded:
                if runs and runs[-1][0] + runs[-1][1] == start + o and runs[-1][3] == len(listed):
                    runs[-1][1] += l
                    runs[-1][2] += 1
                else:
                    runs.append([start + o, l, 1, len(listed)])
                continue
            detailed += k != "AP"
            listed.append([start + o, l, k])
        result["packets"] = listed
        if runs:
            result["packet_runs"] = [r[:3] for r in runs]
        coded = s["frames"] * FRAME
        info = {"codec": "Musepack SV8", "sample_rate": s["rate"], "channels": sh["channels"] if s["complete"] else 0, "samples": s["samples"],
                "average_bitrate": s["audio_bytes"] * 8 * s["rate"] / coded / 1000 if coded and s["rate"] else None}
        if sh:
            header = {}
            if sh["samples"] is not None:
                header["Sample Count"] = f"{sh['samples']} samples"
            if sh["silence"] is not None:
                header["Beginning Silence"] = f"{sh['silence']} samples"
            if s["complete"]:
                header.update({"Channels": str(sh["channels"]), "Max Used Bands": str(sh["bands"]), "UsesMidSideStereo": "true" if sh["ms"] else "false",
                               "Audio Block Frames": f"{4 ** sh['power']} frames"})
            result["header"] = header
        result["frames"] = s["frames"]
    elif stream[:3] == b"MP+" and len(stream) < 28:
        result["version"] = 7
        info = {"codec": "Musepack SV7", "sample_rate": 0, "channels": 0, "samples": 0, "average_bitrate": None}
    elif stream[:3] == b"MP+":
        s = sv7(stream)
        h = s["header"]
        result["version"] = 7
        result["header"] = {"Frame Count": f"{h['frames']} frame{'s' if h['frames'] != 1 else ''}", "Max Band": str(h["max_band"]),
                            "Profile": str(h["profile"]), "Sample Rate": f"{RATES[h['rate']]} Hz", "IsTrueGapless": "true" if h["gapless"] else "false",
                            "Last Frame Samples": f"{h['last']} sample{'s' if h['last'] != 1 else ''}", "UsesMidSideStereo": "true" if h["ms"] else "false",
                            "Encoder Version": str(h["encoder"])}
        result["frames"] = [[f["length"], f["used"], f"{f['ms']:x}", f["noise"]] for f in s["frames"]]
        result["stream_end"] = start + s["end"]
        coded = len(s["frames"]) * FRAME
        info = {"codec": "Musepack SV7", "sample_rate": s["rate"], "channels": 2, "samples": s["samples"],
                "average_bitrate": s["frame_bits"] * s["rate"] / coded / 1000 if coded else None}
    else:
        s = old(stream)
        h = s["header"]
        result["version"] = h["version"]
        result["header"] = {"Bitrate": f"{h['bitrate']} kbps", "UsesIntensityStereo": "true" if h["is"] else "false",
                            "UsesMidSideStereo": "true" if h["ms"] else "false", "Stream Version": str(h["version"]), "Max Band": str(h["max_band"]),
                            "Block Size": f"{h['block']} frame{'s' if h['block'] != 1 else ''}",
                            "Frame Count": f"{h['frames']} frame{'s' if h['frames'] != 1 else ''}"}
        result["frames"] = [[f["length"], f["used"], f"{f['ms']:x}", 0] for f in s["frames"]]
        if s["final"] is not None:
            result["last_frame_samples"] = s["final"]
        coded = len(s["frames"]) * FRAME
        info = {"codec": f"Musepack SV{h['version']}", "sample_rate": 44100, "channels": 2, "samples": s["samples"],
                "average_bitrate": s["frame_bits"] * 44100 / coded / 1000 if s["readable"] and coded else None}
    info["duration"] = info["samples"] / info["sample_rate"] if info["sample_rate"] else 0
    result["stream_infos"] = [info]
    result.update(extra or {})
    return result
