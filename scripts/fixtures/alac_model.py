"""A model of ALAC streams for the fixtures, written apart from the analyzer from Apple's ALAC reference implementation (macosforge/alac at the commit
alac_tool.py pins: ALACMagicCookieDescription.txt, ALACDecoder.cpp, ag_dec.c, ag_enc.c, dp_dec.c, matrix_enc.c), Apache License 2.0, Copyright (c) 2011
Apple Inc. (THIRD-PARTY-NOTICES.md). It has three parts:

- A writer: a cookie, the elements of a frame (a single channel, LFE or channel pair element, compressed or uncompressed, with the shifted bytes of 24 and
  32-bit samples, a mixing of a pair, a partial frame, data stream and fill elements) and a frame, whose residuals are coded as ag_enc.c's dyn_comp codes them.
  The predictor is the decoder's loop run on the known samples (dp_dec.c, unpc_block, in the direction that gives the residual), so a frame it writes from
  samples decodes to them; the fixtures that Apple's alacconvert and FFmpeg decode back to their input show it.
- A walker: the reading of a frame by ALACDecoder.cpp and dyn_decomp, as a plain Python loop, which says for each frame what its elements are and where they
  end, or what is wrong with it. The analyzer's walk is checked against it.
- The manifest of a file (an MP4 file with an alac track, or a Matroska file with an A_ALAC track): the cookie, every frame, the stream's figures and the
  findings, in the form the tests read.

Nothing here imports the analyzer, and nothing in the analyzer reads this file.
"""

import json
import struct

import matroska_model as mkv

TAGS = {0: "single channel", 1: "channel pair", 2: "coupling channel", 3: "LFE", 4: "data stream", 5: "program config", 6: "fill", 7: "end"}
SCE, CPE, CCE, LFE, DSE, PCE, FIL, END = range(8)

QBSHIFT = 9
QB = 1 << QBSHIFT
MMULSHIFT = 2
MDENSHIFT = QBSHIFT - MMULSHIFT - 1
MOFF = 1 << (MDENSHIFT - 2)
BITOFF = 24
MAX_PREFIX = 9
MAX_RUN_BITS = 16
MEAN_CLAMP = 0xFFFF
MAX_FRAME_LENGTH = 1 << 20
MAX_ENTRIES = 1 << 21
MAX_DETAILED_FRAMES = 4096
MAX_DETAILED_ELEMENTS = 64

# The channel layout tags ALAC defines (ALACAudioTypes.h), by channel count.
LAYOUT_TAGS = {1: (100 << 16) | 1, 2: (101 << 16) | 2, 3: (113 << 16) | 3, 4: (116 << 16) | 4, 5: (120 << 16) | 5, 6: (124 << 16) | 6, 7: (142 << 16) | 7,
               8: (127 << 16) | 8}

M32 = 0xFFFFFFFF


def lead(x):
    """Leading zero bits of a 32-bit word (ag_dec.c, lead; 32 for 0)."""
    x &= M32
    return 32 - x.bit_length()


def lg3a(x):
    return 31 - lead(x + 3)


def wrap(value, bits):
    """value as a signed number of bits (the shifts by 32 - bits in dp_dec.c)."""
    mask = (1 << bits) - 1
    value &= mask
    return value - (1 << bits) if value >> (bits - 1) else value


def wrap32(value):
    return wrap(value, 32)


def sign(value):
    return (value > 0) - (value < 0)


class BitWriter:
    def __init__(self):
        self.bits = []

    def write(self, value, count):
        for i in range(count - 1, -1, -1):
            self.bits.append((value >> i) & 1)

    @property
    def position(self):
        return len(self.bits)

    def align(self):
        while len(self.bits) % 8:
            self.bits.append(0)

    def data(self):
        self.align()
        out = bytearray()
        for i in range(0, len(self.bits), 8):
            byte = 0
            for bit in self.bits[i:i + 8]:
                byte = (byte << 1) | bit
            out.append(byte)
        return bytes(out)


# --- The cookie --------------------------------------------------------------------------------------------------------------------------------------


def config(frame_length=4096, bit_depth=16, channels=2, rate=44100, pb=40, mb=10, kb=14, max_run=255, max_frame_bytes=0, average_bitrate=0, version=0):
    """The 24 bytes of the ALACSpecificConfig."""
    return struct.pack(">IBBBBBBHIII", frame_length, version, bit_depth, pb, mb, kb, channels, max_run, max_frame_bytes, average_bitrate, rate)


def layout_info(channels, tag=None, bitmap=0, descriptions=0, size=24, flags=0, kind=b"chan"):
    """The 24 bytes of the channel layout info."""
    return struct.pack(">I4sIIII", size, kind, flags, LAYOUT_TAGS[channels] if tag is None else tag, bitmap, descriptions)


def cookie(channels=2, layout=None, **fields):
    """The cookie Apple's encoder writes: the config, and for more than two channels the layout info (ALACEncoder.cpp, GetMagicCookie)."""
    out = config(channels=channels, **fields)
    if layout is None:
        layout = channels > 2
    return out + (layout_info(channels) if layout else b"")


def atom_cookie(inner, frma=False, terminator=False):
    """The older form: a format atom, an alac atom header of 36 bytes' size and a terminator atom around the cookie."""
    out = b""
    if frma:
        out += struct.pack(">I4s4s", 12, b"frma", b"alac")
    out += struct.pack(">I4sI", 12 + len(inner), b"alac", 0) + inner
    if terminator:
        out += struct.pack(">II", 8, 0)
    return out


def parse_cookie(data):
    """The cookie as ALACDecoder.cpp, Init reads it, with the optional parts the description names; the fields the report shows."""
    at = 0
    out = {"length": len(data), "format_atom": None, "config_atom": None, "layout": None, "terminator": None, "extra": 0}
    if len(data) >= at + 12 and data[at + 4:at + 8] == b"frma":
        out["format_atom"] = {"offset": at, "size": struct.unpack(">I", data[at:at + 4])[0], "format": data[at + 8:at + 12].decode("latin-1")}
        at += 12
    if len(data) >= at + 12 and data[at + 4:at + 8] == b"alac":
        out["config_atom"] = {"offset": at, "size": struct.unpack(">I", data[at:at + 4])[0], "flags": struct.unpack(">I", data[at + 8:at + 12])[0]}
        at += 12
    out["config_at"] = at
    if len(data) - at < 24:
        out["complete"] = False
        return out
    out["complete"] = True
    f = struct.unpack(">IBBBBBBHIII", data[at:at + 24])
    out.update(zip(["frame_length", "version", "bit_depth", "pb", "mb", "kb", "channels", "max_run", "max_frame_bytes", "average_bitrate", "rate"], f))
    at += 24
    if len(data) - at >= 24 and data[at + 4:at + 8] == b"chan":
        size, kind, flags, tag, bitmap, descriptions = struct.unpack(">I4sIIII", data[at:at + 24])
        out["layout"] = {"offset": at, "size": size, "flags": flags, "tag": tag, "bitmap": bitmap, "descriptions": descriptions}
        at += 24
    if len(data) - at >= 8 and data[at:at + 8] == struct.pack(">II", 8, 0):
        out["terminator"] = at
        at += 8
    out["extra"] = len(data) - at
    return out


# --- The writer --------------------------------------------------------------------------------------------------------------------------------------


def dyn_code(m, k, n):
    """ag_enc.c, dyn_code: the zero run count n as (number of bits, value)."""
    div = n // m
    if div >= MAX_PREFIX:
        return MAX_PREFIX + MAX_RUN_BITS, (((1 << MAX_PREFIX) - 1) << MAX_RUN_BITS) + n
    mod = n % m
    de = 1 if mod == 0 else 0
    bits = div + k + 1 - de
    value = (((1 << div) - 1) << (bits - div)) + mod + 1 - de
    if bits > MAX_PREFIX + MAX_RUN_BITS:
        return MAX_PREFIX + MAX_RUN_BITS, (((1 << MAX_PREFIX) - 1) << MAX_RUN_BITS) + n
    return bits, value


def encode_residuals(w, residuals, mb0, pb, kb, bit_size):
    """ag_enc.c, dyn_comp: the adaptive Golomb code of the residuals; returns the number of zero runs and of long codes."""
    wb = (1 << kb) - 1
    mb = mb0
    zmode = 0
    c = 0
    count = len(residuals)
    runs = 0
    longs = 0
    index = 0
    while c < count:
        m = mb >> QBSHIFT
        k = min(lg3a(m), kb)
        m = (1 << k) - 1
        delta = residuals[index]
        index += 1
        n = (abs(delta) << 1) - (1 if delta < 0 else 0) - zmode
        div = n // m if m else 0
        escape = False
        if m == 0 or div >= MAX_PREFIX:
            escape = True
        else:
            mod = n - m * div
            de = 1 if mod == 0 else 0
            bits = div + k + 1 - de
            value = (((1 << div) - 1) << (bits - div)) + mod + 1 - de
            if bits > 25:
                escape = True
        if escape:
            w.write((1 << MAX_PREFIX) - 1, MAX_PREFIX)
            w.write(n & ((1 << bit_size) - 1), bit_size)
            longs += 1
        else:
            w.write(value, bits)
        c += 1
        mb = (pb * (n + zmode) + mb - ((pb * mb) >> QBSHIFT)) & M32
        if n > MEAN_CLAMP:
            mb = MEAN_CLAMP
        zmode = 0
        if ((mb << MMULSHIFT) & M32) < QB and c < count:
            zmode = 1
            nz = 0
            while c < count and residuals[index] == 0:
                index += 1
                nz += 1
                c += 1
                if nz >= 65535:
                    zmode = 0
                    break
            k = lead(mb) - BITOFF + ((mb + MOFF) >> MDENSHIFT)
            mz = ((1 << k) - 1) & wb
            bits, value = dyn_code(mz, k, nz)
            w.write(value, bits)
            runs += 1
            mb = 0
    return runs, longs


def predict(samples, coefficients, shift, chan_bits):
    """The residuals of one channel for a predictor of the given initial coefficients: dp_dec.c's unpc_block run on the known samples."""
    order = len(coefficients)
    n = len(samples)
    if n == 0:
        return []
    residuals = [samples[0]] + [0] * (n - 1)
    if order == 0:
        return list(samples)
    for j in range(1, min(order, n - 1) + 1):
        residuals[j] = wrap(samples[j] - samples[j - 1], chan_bits)
    coefs = list(coefficients)
    lim = order + 1
    half = 1 << (shift - 1)
    for j in range(lim, n):
        top = samples[j - lim]
        pout = j - 1
        total = 0
        for k in range(order):
            total += coefs[k] * (samples[pout - k] - top)
        total = wrap32(total)
        predicted = top + ((total + half) >> shift)
        delta = wrap(samples[j] - predicted, chan_bits)
        residuals[j] = delta
        remaining = delta
        if delta > 0:
            for k in range(order - 1, -1, -1):
                dd = top - samples[pout - k]
                sgn = sign(dd)
                coefs[k] = wrap(coefs[k] - sgn, 16)
                remaining -= (order - k) * ((sgn * dd) >> shift)
                if remaining <= 0:
                    break
        elif delta < 0:
            for k in range(order - 1, -1, -1):
                dd = top - samples[pout - k]
                sgn = sign(dd)
                coefs[k] = wrap(coefs[k] + sgn, 16)
                remaining -= (order - k) * ((-sgn * dd) >> shift)
                if remaining >= 0:
                    break
    return residuals


def first_order(residuals, chan_bits):
    """The difference the decoder's "numActive == 31" pass undoes by summing: for the prediction types other than 0."""
    return [residuals[0]] + [wrap(residuals[i] - residuals[i - 1], chan_bits) for i in range(1, len(residuals))]


class Channel:
    """The parameters of one compressed channel: prediction type, shift, weight factor and the initial coefficients."""

    def __init__(self, coefficients=(), shift=9, weight=4, mode=0):
        self.coefficients = list(coefficients)
        self.shift = shift
        self.weight = weight
        self.mode = mode


def write_audio(w, tag, instance, channels, bit_depth, frame_length=4096, partial=None, count=None, shifted=0, escape=False, mix_bits=2, mix_res=0,
                parameters=None, reserved=0, pb=40, mb=10, kb=14, shift_flags=None):
    """One single channel, LFE or channel pair element: the tag, the header, and the parameters, shifted bytes and residuals or the samples as they are.
    channels: one list of samples (two for a pair), each of the element's sample count. count and partial replace what the lengths say; shift_flags
    replaces the 2 bits of the bytes shifted (to write a value the decoder refuses)."""
    pair = tag == CPE
    samples = len(channels[0])
    if partial is None:
        partial = samples != frame_length
    w.write(tag, 3)
    w.write(instance, 4)
    w.write(reserved, 12)
    flags = (1 if partial else 0) << 3 | (shifted if shift_flags is None else shift_flags) << 1 | (1 if escape else 0)
    w.write(flags, 4)
    if partial:
        w.write(samples if count is None else count, 32)
    if escape:
        bits = bit_depth if pair else bit_depth - shifted * 8
        for i in range(samples):
            for channel in channels:
                w.write(channel[i] & ((1 << bits) - 1), bits)
        return {"zero_runs": [0, 0], "long_codes": [0, 0]}
    shift_bits = shifted * 8
    chan_bits = bit_depth - shift_bits + (1 if pair else 0)
    low = []
    high = [list(c) for c in channels]
    if shifted:
        mask = (1 << shift_bits) - 1
        low = [[v & mask for v in c] for c in channels]
        high = [[v >> shift_bits for v in c] for c in channels]
    if pair and mix_res != 0:
        left, right = high
        mod = 1 << mix_bits
        u = [(mix_res * l + (mod - mix_res) * r) >> mix_bits for l, r in zip(left, right)]
        v = [l - r for l, r in zip(left, right)]
        high = [u, v]
    parameters = parameters or [Channel() for _ in channels]
    w.write(mix_bits, 8)
    w.write(mix_res & 0xFF, 8)
    residuals = []
    for channel, params in zip(high, parameters):
        w.write((params.mode << 4) | params.shift, 8)
        w.write((params.weight << 5) | len(params.coefficients), 8)
        for c in params.coefficients:
            w.write(c & 0xFFFF, 16)
        r = predict(channel, params.coefficients, params.shift, chan_bits)
        if params.mode:
            r = first_order(r, chan_bits)
        residuals.append(r)
    if shifted:
        for i in range(samples):
            for plane in low:
                w.write(plane[i], shift_bits)
    runs, longs = [], []
    for r, params in zip(residuals, parameters):
        n_runs, n_longs = encode_residuals(w, r, mb, pb * params.weight // 4, kb, chan_bits)
        runs.append(n_runs)
        longs.append(n_longs)
    return {"zero_runs": runs, "long_codes": longs}


def write_data_stream(w, instance, data, aligned=False):
    w.write(DSE, 3)
    w.write(instance, 4)
    w.write(1 if aligned else 0, 1)
    count = len(data)
    w.write(min(count, 255), 8)
    if count >= 255:
        w.write(count - 255, 8)
    if aligned:
        w.align()
    for byte in data:
        w.write(byte, 8)


def write_fill(w, count, byte=0):
    w.write(FIL, 3)
    w.write(min(count, 15), 4)
    if count >= 15:
        w.write(count - 14, 8)
    for _ in range(count):
        w.write(byte, 8)


def write_end(w):
    w.write(END, 3)
    w.align()


# The layouts of elements Apple's encoder writes for 1 to 8 channels (ALACEncoder.cpp, sChannelMaps).
LAYOUTS = {1: [SCE], 2: [CPE], 3: [SCE, CPE], 4: [SCE, CPE, SCE], 5: [SCE, CPE, CPE], 6: [SCE, CPE, CPE, SCE], 7: [SCE, CPE, CPE, SCE, SCE], 8: [SCE, CPE, CPE, CPE, SCE]}


def frame(planes, bit_depth, frame_length=4096, layout=None, options=None):
    """A frame: the planes (one list of samples per channel) in the elements of the layout, each element's options from options (a dict by element index),
    then the end tag. Returns the bytes."""
    layout = layout or LAYOUTS[len(planes)]
    options = options or {}
    w = BitWriter()
    channel = 0
    instances = {}
    for index, tag in enumerate(layout):
        used = 2 if tag == CPE else 1
        instance = instances.get(tag, 0)
        instances[tag] = instance + 1
        write_audio(w, tag, instance, planes[channel:channel + used], bit_depth, frame_length, **options.get(index, {}))
        channel += used
    write_end(w)
    return w.data()


# --- The walker --------------------------------------------------------------------------------------------------------------------------------------


class Reader:
    def __init__(self, data):
        self.data = data
        self.total = len(data) * 8

    def peek(self, position, count):
        value = 0
        for i in range(count):
            at = position + i
            bit = (self.data[at >> 3] >> (7 - (at & 7))) & 1 if at < self.total else 0
            value = (value << 1) | bit
        return value


def walk_residuals(reader, position, samples, max_bits, config_):
    """ag_dec.c, dyn_decomp: (new position, zero runs, long codes, fault) for the residuals of a channel; fault is None, "truncated" or "zero-run"."""
    total = reader.total
    mb = config_["mb"]
    pb = config_["weight"]
    kb = config_["kb"]
    wb = (1 << kb) - 1
    zmode = 0
    c = 0
    runs = longs = 0
    while c < samples:
        if position >= total:
            return total + 1, runs, longs, "truncated"
        m = mb >> QBSHIFT
        k = min(lg3a(m), kb)
        m = (1 << k) - 1
        window = reader.peek(position, 32)
        prefix = lead(~window & M32)
        if prefix >= MAX_PREFIX:
            n = reader.peek(position + MAX_PREFIX, max_bits)
            position += MAX_PREFIX + max_bits
            longs += 1
        else:
            position += prefix + 1
            if k != 1:
                v = (window << (prefix + 1) & M32) >> (32 - k)
                position += k - 1
                n = prefix * m
                if v >= 2:
                    n += v - 1
                    position += 1
            else:
                n = prefix
        c += 1
        mb = (pb * (n + zmode) + mb - ((pb * mb) >> QBSHIFT)) & M32
        if n > MEAN_CLAMP:
            mb = MEAN_CLAMP
        zmode = 0
        if ((mb << MMULSHIFT) & M32) < QB and c < samples:
            zmode = 1
            k = lead(mb) - BITOFF + ((mb + MOFF) >> MDENSHIFT)
            mz = ((1 << k) - 1) & wb
            window = reader.peek(position, 32)
            prefix = lead(~window & M32)
            if prefix >= MAX_PREFIX:
                run = reader.peek(position + MAX_PREFIX, MAX_RUN_BITS)
                position += MAX_PREFIX + MAX_RUN_BITS
            else:
                position += prefix + 1
                v = (window << (prefix + 1) & M32) >> (32 - k)
                position += k
                run = prefix * mz + v - 1
                if v < 2:
                    run -= v - 1
                    position -= 1
            if c + run > samples:
                return position, runs, longs, "zero-run"
            c += run
            runs += 1
            if run >= 65535:
                zmode = 0
            mb = 0
        if position > total:
            return position, runs, longs, "truncated"
    return position, runs, longs, None


def walk_frame(data, cookie_):
    """ALACDecoder.cpp, Decode, followed to the end of the frame: what the frame holds and how it ends, as a dict.

    "status" is "ok" (an end tag), "end-missing" (the channels of the cookie are read and the packet ends), or the fault; "walked" is the bytes through the
    end tag's padding; "elements" lists every element read with its bit positions."""
    reader = Reader(data)
    total = reader.total
    position = 0
    channels = 0
    elements = []
    frame_samples = None
    out = {"elements": elements, "status": None, "walked": 0, "samples": 0, "channels": 0, "flags": {}}
    differing = reserved = shifted_escape = too_long = exceeding = None
    while True:
        index = len(elements)
        if position + 3 > total:
            out["status"] = "end-missing" if channels >= cookie_["channels"] else "truncated"
            out["fault_element"] = index
            break
        start = position
        tag = reader.peek(position, 3)
        position += 3
        element = {"tag": tag, "name": TAGS[tag], "start": start}
        elements.append(element)
        fault = None
        if tag in (SCE, LFE, CPE):
            pair = tag == CPE
            used = 2 if pair else 1
            if position + 20 > total:
                position = total + 1
                fault = "truncated"
            else:
                element["instance"] = reader.peek(position, 4)
                element["reserved_zero"] = reader.peek(position + 4, 12) == 0
                header = reader.peek(position + 16, 4)
                position += 20
                partial = (header >> 3) & 1
                shifted = (header >> 1) & 3
                escape = header & 1
                element.update(partial=bool(partial), shifted=shifted, uncompressed=bool(escape))
                if shifted == 3:
                    fault = "invalid"
                else:
                    samples = cookie_["frame_length"]
                    if partial:
                        if position + 32 > total:
                            position = total + 1
                            fault = "truncated"
                        else:
                            samples = reader.peek(position, 32)
                            position += 32
                    if fault is None:
                        element["samples"] = samples
                        element["header_read"] = True
                        if frame_samples is None:
                            frame_samples = samples
                        elif samples != frame_samples and differing is None:
                            differing = index
                        if not element["reserved_zero"] and reserved is None:
                            reserved = index
                        if partial and samples > cookie_["frame_length"] and too_long is None:
                            too_long = (index, samples)
                        if escape and shifted and shifted_escape is None:
                            shifted_escape = index
                        width = cookie_["bit_depth"] - shifted * 8 + (1 if pair else 0)
                        if escape:
                            bits = cookie_["bit_depth"] if pair else cookie_["bit_depth"] - shifted * 8
                            if bits < 1 or bits > 32:
                                fault = "invalid"
                            else:
                                position += samples * bits * used
                                if position > total:
                                    fault = "truncated"
                            element["channels"] = []
                        elif width < 1 or width > 32:
                            fault = "invalid"
                        elif cookie_["kb"] == 0 or cookie_["kb"] > 31:
                            fault = "tuning"
                            element["channels"] = []
                        else:
                            fault = walk_compressed(reader, element, position, samples, used, shifted, width, cookie_)
                            position = element.pop("_position")
                        channels += used
                        if channels > cookie_["channels"] and exceeding is None:
                            exceeding = index
        elif tag == DSE:
            if position + 13 > total:
                position = total + 1
                fault = "truncated"
            else:
                element["instance"] = reader.peek(position, 4)
                element["aligned"] = bool(reader.peek(position + 4, 1))
                count = reader.peek(position + 5, 8)
                position += 13
                if count == 255:
                    if position + 8 > total:
                        position = total + 1
                        fault = "truncated"
                    else:
                        count += reader.peek(position, 8)
                        position += 8
                if fault is None:
                    if element["aligned"]:
                        position = (position + 7) // 8 * 8
                    element["bytes"] = count
                    position += count * 8
                    if position > total:
                        fault = "truncated"
        elif tag == FIL:
            if position + 4 > total:
                position = total + 1
                fault = "truncated"
            else:
                count = reader.peek(position, 4)
                position += 4
                if count == 15:
                    if position + 8 > total:
                        position = total + 1
                        fault = "truncated"
                    else:
                        count += reader.peek(position, 8) - 1
                        position += 8
                if fault is None:
                    element["bytes"] = count
                    position += count * 8
                    if position > total:
                        fault = "truncated"
        elif tag == END:
            pass
        else:
            fault = "unknown"
        element["end"] = start + 3 if fault == "unknown" else min(position, total)
        if fault:
            out["status"] = fault
            out["fault_element"] = index
            break
        if tag == END:
            out["status"] = "ok"
            out["walked"] = (position + 7) // 8
            break
    out["samples"] = frame_samples or 0
    out["channels"] = channels
    out["differing"] = differing
    out["reserved"] = reserved
    out["shifted_escape"] = shifted_escape
    out["too_long"] = too_long
    out["exceeding"] = exceeding
    return out


def walk_compressed(reader, element, position, samples, used, shifted, width, cookie_):
    """The parameters, shifted bits and residuals of a compressed element, from the bit at position; returns the fault and leaves the position in
    element["_position"]."""
    total = reader.total
    element["channels"] = []
    if position + 16 > total:
        element["_position"] = total + 1
        return "truncated"
    element["mix_bits"] = reader.peek(position, 8)
    mix_res = reader.peek(position + 8, 8)
    element["mix_res"] = mix_res - 256 if mix_res >= 128 else mix_res
    position += 16
    parameters = []
    for _ in range(used):
        if position + 16 > total:
            element["_position"] = total + 1
            return "truncated"
        first = reader.peek(position, 8)
        second = reader.peek(position + 8, 8)
        position += 16
        order = second & 31
        if position + order * 16 > total:
            element["_position"] = total + 1
            return "truncated"
        coefficients = [wrap(reader.peek(position + 16 * i, 16), 16) for i in range(order)]
        position += order * 16
        parameters.append({"type": first >> 4, "shift": first & 15, "weight": second >> 5, "order": order, "coefficients": coefficients})
    position += shifted * 8 * samples * used
    if position > total:
        element["_position"] = total + 1
        return "truncated"
    for params in parameters:
        weight = cookie_["pb"] * params["weight"] // 4
        position, runs, longs, fault = walk_residuals(reader, position, samples, width, dict(mb=cookie_["mb"], kb=cookie_["kb"], weight=weight))
        params["zero_runs"] = runs
        params["long_codes"] = longs
        params["residual_end"] = min(position, total)
        params["complete"] = fault is None
        element["channels"].append(params)
        if fault:
            element["_position"] = position
            return "truncated" if fault == "truncated" else "zero-run"
    element["_position"] = position
    return None


# --- Containers --------------------------------------------------------------------------------------------------------------------------------------


def boxes(data, start, end):
    """The boxes between start and end as (type, payload start, end)."""
    out = []
    while start + 8 <= end:
        size, kind = struct.unpack(">I4s", data[start:start + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", data[start + 8:start + 16])[0]
            header = 16
        elif size == 0:
            size = end - start
        if size < header or start + size > end:
            break
        out.append((kind, start + header, start + size))
        start += size
    return out


def find(data, start, end, path):
    for kind, payload, stop in boxes(data, start, end):
        if kind == path[0].encode():
            return (payload, stop) if len(path) == 1 else find(data, payload, stop, path[1:])
    return None


def m4a(data):
    """(cookie bytes or None, packets as (offset, size), facts) of the first track of an MP4 file with an alac sample entry."""
    stbl = None
    moov = find(data, 0, len(data), ["moov"])
    trak = find(data, *moov, ["trak"])
    mdia = find(data, *trak, ["mdia"])
    minf = find(data, *mdia, ["minf"])
    stbl = find(data, *minf, ["stbl"])
    mdhd = find(data, *mdia, ["mdhd"])
    timescale = struct.unpack(">I", data[mdhd[0] + 12:mdhd[0] + 16])[0]
    stsd = find(data, *stbl, ["stsd"])
    entry = boxes(data, stsd[0] + 8, stsd[1])[0]
    channels, bits, rate = struct.unpack(">HH", data[entry[1] + 16:entry[1] + 20]) + (struct.unpack(">I", data[entry[1] + 24:entry[1] + 28])[0] >> 16,)
    cookie_bytes = None
    for kind, payload, stop in boxes(data, entry[1] + 28, entry[2]):
        if kind == b"alac":
            cookie_bytes = data[payload + 4:stop]
    stsz = find(data, *stbl, ["stsz"])
    uniform, count = struct.unpack(">II", data[stsz[0] + 4:stsz[0] + 12])
    sizes = [uniform] * count if uniform else list(struct.unpack(f">{count}I", data[stsz[0] + 12:stsz[0] + 12 + 4 * count]))
    stsc = find(data, *stbl, ["stsc"])
    runs = struct.unpack(">I", data[stsc[0] + 4:stsc[0] + 8])[0]
    chunk_runs = [struct.unpack(">III", data[stsc[0] + 8 + 12 * i:stsc[0] + 20 + 12 * i]) for i in range(runs)]
    stco = find(data, *stbl, ["stco"])
    chunks = struct.unpack(">I", data[stco[0] + 4:stco[0] + 8])[0]
    offsets = struct.unpack(f">{chunks}I", data[stco[0] + 8:stco[0] + 8 + 4 * chunks])
    packets = []
    index = 0
    for chunk in range(chunks):
        per = next(r[1] for r in reversed(chunk_runs) if r[0] <= chunk + 1)
        at = offsets[chunk]
        for _ in range(per):
            if index >= count:
                break
            packets.append((at, sizes[index]))
            at += sizes[index]
            index += 1
    return cookie_bytes, packets, {"channels": channels, "bits": bits, "rate": rate, "timescale": timescale, "samples": count}


def container_findings(data):
    """The findings the MP4 container raises of a file with one ALAC track, as counts by code: the moov box after the mdat box, and an edit that plays
    media past the track's end (ISO/IEC 14496-12, 8.6.6). Worked out from the boxes."""
    counts = {}
    top = boxes(data, 0, len(data))
    kinds = [k for k, _, _ in top]
    if b"moov" in kinds and b"mdat" in kinds and kinds.index(b"mdat") < kinds.index(b"moov") and b"moof" not in kinds:
        counts["mp4.moov-after-mdat"] = 1
    moov = find(data, 0, len(data), ["moov"])
    mvhd = find(data, *moov, ["mvhd"])
    movie_scale = struct.unpack(">I", data[mvhd[0] + 12:mvhd[0] + 16])[0]
    trak = find(data, *moov, ["trak"])
    mdhd = find(data, *find(data, *trak, ["mdia"]), ["mdhd"])
    media_scale, media_duration = struct.unpack(">II", data[mdhd[0] + 12:mdhd[0] + 20])
    elst = find(data, *find(data, *trak, ["edts"]), ["elst"]) if find(data, *trak, ["edts"]) else None
    if elst and data[elst[0]] == 0:
        entries = struct.unpack(">I", data[elst[0] + 4:elst[0] + 8])[0]
        for i in range(entries):
            segment, media_time = struct.unpack(">Ii", data[elst[0] + 8 + 12 * i:elst[0] + 16 + 12 * i])
            if media_time >= 0 and media_time + segment * media_scale / movie_scale > media_duration + 1:
                counts["mp4.edit-beyond-media"] = 1
                break
    return counts


def mka(data, track=1):
    """(cookie bytes or None, packets as (offset, size), facts) of an A_ALAC track of a Matroska file."""
    r = mkv.read(data)
    entry = next(t for t in r.tracks if t["number"] == track)
    private = entry["private"]
    cookie_bytes = data[private[0]:private[0] + private[1]] if private else None
    packets = [tuple(f) for b in r.blocks if b.track == track and not b.problem for f in b.frames]
    return cookie_bytes, packets, {"channels": entry["channels"], "bits": entry["bits"], "rate": entry["rate"], "writers": mkv.writers_of(r.facts["segments"])}


# --- The manifest ------------------------------------------------------------------------------------------------------------------------------------

LAYOUT_NAMES = {1: ["front center"], 2: ["front left", "front right"]}
LAYOUT_SPEAKERS = {  # the speakers of ALAC's eight layouts, in the order of the tag's channels, as the report names them
    100: ["front center"], 101: ["front left", "front right"], 113: ["front center", "front left", "front right"],
    116: ["front center", "front left", "front right", "back center"], 120: ["front center", "front left", "front right", "back left", "back right"],
    124: ["front center", "front left", "front right", "back left", "back right", "LFE"],
    142: ["front center", "front left", "front right", "back left", "back right", "back center", "LFE"],
    127: ["front center", "front left of center", "front right of center", "front left", "front right", "back left", "back right", "LFE"],
}


def layout_of(c):
    """The speakers the cookie names (see the description: "If the channel layout is absent from the cookie")."""
    if c["layout"] is not None:
        names = LAYOUT_SPEAKERS.get(c["layout"]["tag"] >> 16)
        return names if names is not None and len(names) == c["channels"] and (c["layout"]["tag"] & 0xFFFF) == c["channels"] else []
    return LAYOUT_NAMES.get(c["channels"], [])


def cookie_findings(c):
    """The findings a cookie raises, as codes; the cookie's own and the consequences for reading frames."""
    out = []
    if not c["complete"]:
        return ["alac.cookie-too-short"], False
    can_walk = c["channels"] >= 1 and 1 <= c["bit_depth"] <= 32
    if c["version"] != 0:
        out.append("alac.compatible-version")
    if not 0 < c["frame_length"] <= MAX_FRAME_LENGTH:
        out.append("alac.frame-length-invalid")
    if c["bit_depth"] not in (16, 20, 24, 32):
        out.append("alac.bit-depth-invalid")
    if not 1 <= c["channels"] <= 8:
        out.append("alac.channels-invalid")
    if c["layout"] is not None and (c["layout"]["tag"] & 0xFFFF) != c["channels"]:
        out.append("alac.channel-layout-differs")
    if c["extra"] > 0:
        out.append("alac.cookie-extra")
    return out, can_walk


FAULT_CODES = {"truncated": "alac.frame-overrun", "unknown": "alac.element-unknown", "invalid": "alac.element-invalid", "zero-run": "alac.residual-invalid",
               "tuning": "alac.tuning-invalid"}


def element_record(e):
    """An element of a walked frame as the manifest has it."""
    out = {"tag": e["tag"], "name": e["name"], "start": e["start"], "end": e["end"]}
    for key in ("instance", "partial", "shifted", "uncompressed", "samples", "mix_bits", "mix_res", "bytes", "aligned"):
        if key in e:
            out[key] = e[key]
    if "reserved_zero" in e:
        out["reserved_zero"] = e["reserved_zero"]
    if "channels" in e:
        out["channels"] = [{k: v for k, v in c.items() if k != "complete"} for c in e["channels"]]
    return out


def manifest(data, kind, extra=None):
    """What the analyzer must show of an MP4 or Matroska file with an ALAC track: the cookie, every frame, the statistics, the stream and the findings."""
    cookie_bytes, packets, facts = (m4a if kind == "mp4" else mka)(data)
    result = {"container": kind, "length": len(data)}
    if extra:
        result.update(extra)
    findings = []
    counts = {}

    def add(code, n=1):
        counts[code] = counts.get(code, 0) + n

    if cookie_bytes is None:
        c = None
        add("alac.cookie-missing")
        can_walk = False
        result["cookie"] = None
    else:
        c = parse_cookie(cookie_bytes)
        names, can_walk = cookie_findings(c)
        for code in names:
            add(code)
        result["cookie"] = c
    frames = []
    tally = {"elements": [0] * 8, "compressed": 0, "uncompressed": 0, "pairs": 0, "mixed_pairs": 0, "channels": 0, "types": [0] * 16, "orders": [0] * 32,
             "shifts": [0] * 16, "weights": [0] * 8, "bytes_shifted": [0] * 3, "zero_runs": 0, "long_codes": 0, "fill_bytes": 0, "data_stream_bytes": 0}
    partial_frames = uncompressed_frames = unreadable_frames = 0
    total_samples = 0
    points = []
    pair_codes = []
    element_codes = []
    for index, (offset, size) in enumerate(packets):
        record = {"offset": offset, "size": size}
        if not can_walk:
            record["status"] = "not-walked"
            record["samples"] = 0
            frames.append(record)
            continue
        w = walk_frame(data[offset:offset + size], c)
        status = w["status"]
        record["status"] = status
        record["samples"] = w["samples"]
        record["walked"] = w["walked"]
        record["channels"] = w["channels"]
        record["element_count"] = len(w["elements"])
        record["elements"] = [element_record(e) for e in w["elements"][:MAX_DETAILED_ELEMENTS]] if index < MAX_DETAILED_FRAMES else []
        frames.append(record)
        fault_code = FAULT_CODES.get(status)
        elements = w["elements"]
        if fault_code:
            add(fault_code)
            unreadable_frames += 1
        if status == "end-missing":
            add("alac.end-missing")
        if status == "ok" and w["walked"] < size:
            add("alac.frame-underrun")
        if w["exceeding"] is not None:
            add("alac.channels-exceed")
        elif status == "ok" and w["channels"] < c["channels"]:
            add("alac.channels-missing")
        if w["reserved"] is not None:
            add("alac.element-reserved-bits")
        if w["shifted_escape"] is not None:
            add("alac.escape-shifted")
        if w["differing"] is not None:
            add("alac.element-samples-differ")
        if w["too_long"] is not None:
            add("alac.partial-frame-too-long")
        read = [e for e in elements if e.get("header_read")]
        if any(e["partial"] for e in read):
            partial_frames += 1
        if any(e["uncompressed"] for e in read):
            uncompressed_frames += 1
        frame_pairs = []
        frame_elements = []
        for e in elements:
            tally["elements"][e["tag"]] += 1
            if e["tag"] == DSE:
                tally["data_stream_bytes"] += e.get("bytes", 0)
            if e["tag"] == FIL:
                tally["fill_bytes"] += e.get("bytes", 0)
            if not e.get("header_read"):
                continue
            frame_elements.append(1 if e["uncompressed"] else 0)
            if e["uncompressed"]:
                tally["uncompressed"] += 1
            else:
                tally["compressed"] += 1
                tally["bytes_shifted"][min(e["shifted"], 2)] += 1
                for ch in e["channels"]:
                    if ch["complete"]:
                        tally["channels"] += 1
                        tally["types"][ch["type"]] += 1
                        tally["orders"][ch["order"]] += 1
                        tally["shifts"][ch["shift"]] += 1
                        tally["weights"][ch["weight"]] += 1
                        tally["zero_runs"] += ch["zero_runs"]
                        tally["long_codes"] += ch["long_codes"]
            if e["tag"] == CPE:
                tally["pairs"] += 1
                mixed = (not e["uncompressed"]) and e.get("mix_res", 0) != 0
                tally["mixed_pairs"] += 1 if mixed else 0
                frame_pairs.append(1 if mixed else 0)
        samples = w["samples"]
        total_samples += samples
        if samples > 0 and index < MAX_ENTRIES:
            points.append((offset, size * 8, samples))
            pair_codes.append(frame_pairs)
            element_codes.append(frame_elements)
    result["frames"] = frames
    result["frame_count"] = len(frames)
    result["statistics"] = {"frames": len(packets), "partial_frames": partial_frames, "uncompressed_frames": uncompressed_frames,
                            "unreadable_frames": unreadable_frames, **tally}
    if kind == "mp4":
        for code, n in container_findings(data).items():
            add(code, n)
    findings = [{"code": code, **({"count": n} if n > 1 else {})} for code, n in sorted(counts.items())]
    result["findings"] = findings
    rate = c["rate"] if c and c["complete"] else 0
    channels = c["channels"] if c and c["complete"] else 0
    has_stream = (c is not None and c["complete"]) or len(packets) > 0
    streams = []
    if has_stream and not (c is None and not packets):
        bits = sum(p[1] for p in points)
        covered = sum(p[2] for p in points)
        bitrates = [bits_ * rate / s / 1000 for _, bits_, s in points] if rate else []
        streams.append({
            "codec": "Apple Lossless", "sample_rate": rate, "channels": channels, "samples": total_samples, "duration": total_samples / rate if rate else 0.0,
            "bits_per_sample": c["bit_depth"] if c and c["complete"] and c["bit_depth"] > 0 else None,
            "average_bitrate": (bits * rate / covered / 1000) if covered and rate else None,
            "is_vbr": bool(bitrates) and max(bitrates) > min(bitrates),
            "layout": layout_of(c) if c and c["complete"] else [],
            "units": len(points), "pair_codes": pair_codes[:16], "element_codes": element_codes[:16],
        })
    # A track without a cookie gets the container's own stream, whose figures the model does not work out.
    result["stream_infos"] = streams if (c is not None and c["complete"]) else ([] if c is not None else None)
    result["verification"] = {"default": {"isVerified": None, "decoding": "noDecoder", "checksums": []}}
    return result


def dump(result):
    return json.dumps(result, indent=1, ensure_ascii=False) + "\n"


def ber(value):
    """A variable-length integer of the CAF packet table: 7 bits at a time, most significant first, the high bit set on all but the last."""
    groups = [value & 0x7F]
    value >>= 7
    while value:
        groups.append((value & 0x7F) | 0x80)
        value >>= 7
    return bytes(reversed(groups))


def caf(cookie_bytes, packets, rate, channels, bit_depth, frame_length, samples):
    """A CAF file of ALAC (Apple's Core Audio Format Specification; the 'desc' flags 1 to 4 are the source's 16, 20, 24 and 32 bits, as afconvert and
    alacconvert write them), for decoding a stream with alacconvert, which reads CAF files only."""
    def chunk(kind, body):
        return kind + struct.pack(">q", len(body)) + body

    flags = {16: 1, 20: 2, 24: 3, 32: 4}.get(bit_depth, 0)
    out = b"caff" + struct.pack(">HH", 1, 0)
    out += chunk(b"desc", struct.pack(">d4sIIIII", rate, b"alac", flags, 0, frame_length, channels, 0))
    out += chunk(b"kuki", cookie_bytes)
    if channels > 2:
        out += chunk(b"chan", struct.pack(">III", LAYOUT_TAGS[channels], 0, 0))
    sizes = b"".join(ber(len(p)) for p in packets)
    out += chunk(b"pakt", struct.pack(">qqii", len(packets), samples, 0, len(packets) * frame_length - samples) + sizes)
    out += chunk(b"data", struct.pack(">I", 0) + b"".join(packets))
    return out
