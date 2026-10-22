"""What a FLAC stream holds, worked out from its bytes with RFC 9639 apart from the analyzer: the manifests the fixture scripts write and
the C# tests compare the analyzer's report with, and a decoder whose output is the reference for the fixtures' decoded audio. It also holds
the writer that builds the hand-made fixtures (FlacWriter).

Frames are found as the analyzer finds them (the rule is in the plan, "Tag detection and false positives", and CLAUDE.md): in sequence from
the end of the metadata, and after damage by a search for a header that reads, agrees with STREAMINFO, has a CRC-16 that holds over the
frame its subframes walk to, and is followed by the end of the data or another such header. The model is a second reading of the
specification, written separately from the analyzer's C#; crosscheck_flac_fixtures.py confirms both against flac, ffprobe and FFmpeg.
"""

import hashlib
import struct

RATES = {1: 88200, 2: 176400, 3: 192000, 4: 8000, 5: 16000, 6: 22050, 7: 24000, 8: 32000, 9: 44100, 10: 48000, 11: 96000}
SIZES = {1: 8, 2: 12, 4: 16, 5: 20, 6: 24, 7: 32}
BLOCK_TYPES = {0: "STREAMINFO", 1: "PADDING", 2: "APPLICATION", 3: "SEEKTABLE", 4: "VORBIS_COMMENT", 5: "CUESHEET", 6: "PICTURE", 127: "Forbidden"}
MAX_FRAME = (1 << 24) - 1

# docs/formats/flac.md, "Frames and resync": the search past damage reads at most 64 MiB plus four times the file's length in candidates that did not
# hold. The model counts the bytes every candidate walk reads, with a header that does not read or agree costing the shortest header.
PROBE_ALLOWANCE = 64 << 20
MIN_HEADER = 6
ASSIGNMENTS = {8: "left/side", 9: "side/right", 10: "mid/side"}
LAYOUTS = {
    1: ["front center"],
    2: ["front left", "front right"],
    3: ["front left", "front right", "front center"],
    4: ["front left", "front right", "back left", "back right"],
    5: ["front left", "front right", "front center", "back left", "back right"],
    6: ["front left", "front right", "front center", "LFE", "back left", "back right"],
    7: ["front left", "front right", "front center", "LFE", "back center", "side left", "side right"],
    8: ["front left", "front right", "front center", "LFE", "back left", "back right", "side left", "side right"],
}


def _table8():
    table = []
    for i in range(256):
        crc = i
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
        table.append(crc)
    return table


def _table16():
    table = []
    for i in range(256):
        crc = i << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x8005) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
        table.append(crc)
    return table


T8, T16 = _table8(), _table16()


def crc8(data):
    crc = 0
    for b in data:
        crc = T8[crc ^ b]
    return crc


def crc16(data):
    crc = 0
    for b in data:
        crc = T16[(crc >> 8) ^ b] ^ ((crc << 8) & 0xFFFF)
    return crc


class Cut(Exception):
    """The data ends inside what is being read."""


class Fault(Exception):
    """A structure that cannot be read; the argument names why."""


class Bits:
    """Reads bits most significant first; reading past the end raises Cut."""

    def __init__(self, data, byte=0):
        self.data = data
        self.pos = byte * 8

    def read(self, n):
        if n == 0:
            return 0
        end = self.pos + n
        if end > len(self.data) * 8:
            self.pos = end
            raise Cut()
        first, last = self.pos >> 3, (end - 1) >> 3
        value = int.from_bytes(self.data[first:last + 1], "big")
        value >>= (last + 1) * 8 - end
        self.pos = end
        return value & ((1 << n) - 1)

    def signed(self, n):
        value = self.read(n)
        return value - (1 << n) if value >> (n - 1) else value

    def unary(self):
        """Zero bits up to the next one bit, which is consumed."""
        count = 0
        while True:
            if self.pos >= len(self.data) * 8:
                self.pos += 1
                raise Cut()
            bit = self.data[self.pos >> 3] >> (7 - (self.pos & 7)) & 1
            self.pos += 1
            if bit:
                return count
            count += 1


def streaminfo(b):
    v = int.from_bytes(b[10:18], "big")
    return {"min_block": b[0] << 8 | b[1], "max_block": b[2] << 8 | b[3], "min_frame": int.from_bytes(b[4:7], "big"), "max_frame": int.from_bytes(b[7:10], "big"),
            "sample_rate": v >> 44, "channels": (v >> 41 & 7) + 1, "bits": (v >> 36 & 31) + 1, "total": v & (1 << 36) - 1, "md5": b[18:34].hex()}


def read_number(data, p, variable):
    first = data[p]
    if first < 0x80:
        count, value = 1, first
    elif first & 0xE0 == 0xC0:
        count, value = 2, first & 0x1F
    elif first & 0xF0 == 0xE0:
        count, value = 3, first & 0x0F
    elif first & 0xF8 == 0xF0:
        count, value = 4, first & 7
    elif first & 0xFC == 0xF8:
        count, value = 5, first & 3
    elif first & 0xFE == 0xFC:
        count, value = 6, first & 1
    elif first == 0xFE:
        count, value = 7, 0
    else:
        raise Fault("number")
    if p + count > len(data):
        raise Cut()
    for i in range(1, count):
        if data[p + i] & 0xC0 != 0x80:
            raise Fault("number")
        value = value << 6 | data[p + i] & 0x3F
    if count > 1 and value < (0, 0, 0x80, 0x800, 0x10000, 0x200000, 0x4000000, 0x80000000)[count]:
        raise Fault("number")
    if not variable and value > 0x7FFFFFFF:
        raise Fault("number")
    return value, count


def header(data, p):
    """(header, None), or (None, fault) as the analyzer's FrameHeader.TryRead: the fields are returned with the fault "crc8"."""
    d = data[p:p + 16]
    if (len(d) >= 1 and d[0] != 0xFF) or (len(d) >= 2 and d[1] & 0xFC != 0xF8):
        return None, "sync"
    if len(d) < 4:
        return None, "truncated"
    if d[1] & 2 or d[3] & 1:
        return None, "reserved"
    variable = bool(d[1] & 1)
    bs_code, rate_code, channel_code, size_code = d[2] >> 4, d[2] & 15, d[3] >> 4, d[3] >> 1 & 7
    if bs_code == 0:
        return None, "blocksize"
    if rate_code == 15:
        return None, "samplerate"
    if channel_code > 10:
        return None, "assignment"
    if size_code == 3:
        return None, "samplesize"
    try:
        number, count = read_number(d, 4, variable)
    except Cut:
        return None, "truncated"
    except Fault:
        return None, "number"
    at = 4 + count
    try:
        if bs_code == 1:
            block = 192
        elif bs_code == 6:
            block, at = d[at] + 1, at + 1
        elif bs_code == 7:
            block, at = (d[at] << 8 | d[at + 1]) + 1, at + 2
            if block > 65535:
                return None, "blocksize"
        elif bs_code < 6:
            block = 576 << bs_code - 2
        else:
            block = 256 << bs_code - 8
        if rate_code == 12:
            rate, at = d[at] * 1000, at + 1
        elif rate_code == 13:
            rate, at = d[at] << 8 | d[at + 1], at + 2
        elif rate_code == 14:
            rate, at = (d[at] << 8 | d[at + 1]) * 10, at + 2
        else:
            rate = RATES.get(rate_code, 0)
        stored = d[at]
    except IndexError:
        return None, "truncated"
    h = {"length": at + 1, "variable": variable, "block_size_code": bs_code, "block_size": block, "rate_code": rate_code, "sample_rate": rate,
         "channel_code": channel_code, "channels": channel_code + 1 if channel_code < 8 else 2, "size_code": size_code, "bits": SIZES.get(size_code, 0),
         "number": number, "number_length": count, "crc8": stored, "computed_crc8": crc8(d[:at])}
    return h, None if stored == h["computed_crc8"] else "crc8"


def subframe(bits, block, frame_bits, is_side):
    """Reads one subframe and returns (description, samples); raises Fault with the name of the fault, or Cut."""
    sample_bits = frame_bits + (1 if is_side else 0)
    start = bits.pos
    if bits.read(1):
        raise Fault("ReservedBit")
    code = bits.read(6)
    if code == 0:
        kind, order = "constant", 0
    elif code == 1:
        kind, order = "verbatim", 0
    elif 8 <= code <= 12:
        kind, order = "fixed", code - 8
    elif code >= 32:
        kind, order = "lpc", (code & 31) + 1
    else:
        raise Fault("ReservedType")
    wasted = 0
    if bits.read(1):
        wasted = bits.unary() + 1
        if wasted >= sample_bits:
            raise Fault("WastedBits")
    sample_bits -= wasted
    d = {"type": kind, "order": order, "wasted": wasted, "start": start, "sample_bits": sample_bits}
    if kind == "constant":
        samples = [bits.signed(sample_bits)] * block
        d["constant"] = samples[0]
    elif kind == "verbatim":
        samples = [bits.signed(sample_bits) for _ in range(block)]
    else:
        if order > block:
            raise Fault("Order")
        samples = [bits.signed(sample_bits) for _ in range(order)]
        if kind == "lpc":
            precision = bits.read(4)
            if precision == 15:
                raise Fault("Precision")
            precision += 1
            shift = bits.signed(5)
            if shift < 0:
                raise Fault("Shift")
            coefficients = [bits.signed(precision) for _ in range(order)]
            d.update({"precision": precision, "shift": shift, "coefficients": coefficients})
        d["residual_start"] = bits.pos
        residual = read_residual(bits, d, block, order)
        samples = restore(samples, residual, d)
    d["end"] = bits.pos
    if wasted:
        samples = [s << wasted for s in samples]
    return d, samples


def read_residual(bits, d, block, order):
    method = bits.read(2)
    if method > 1:
        raise Fault("ResidualMethod")
    partition_order = bits.read(4)
    partitions = 1 << partition_order
    if block % partitions or block >> partition_order < order:
        raise Fault("PartitionOrder")
    width = 4 if method == 0 else 5
    escape = (1 << width) - 1
    # Section 9.2.7: the samples of a partition must outnumber the predictor order, and the residual -2^31 is excluded; both are decoded and reported.
    d["tight"] = block >> partition_order == order
    values = []
    parameters = []
    escaped = 0
    for p in range(partitions):
        count = (block >> partition_order) - (order if p == 0 else 0)
        start = bits.pos
        parameter = bits.read(width)
        if parameter == escape:
            escaped += 1
            n = bits.read(5)
            values.extend(bits.signed(n) if n else 0 for _ in range(count))
            parameters.append((start, n, True))
        else:
            parameters.append((start, parameter, False))
            for _ in range(count):
                q = bits.unary()
                folded = q << parameter | bits.read(parameter)
                if "remainder_lsb" not in d and parameter:
                    d["remainder_lsb"] = bits.pos - 1
                if folded >> 32:
                    raise Fault("ResidualOverflow")
                if folded == 0xFFFFFFFF:
                    d["min_residual"] = True
                values.append(folded >> 1 ^ -(folded & 1))
    rice = [p for _, p, e in parameters if not e]
    d.update({"method": method, "partition_order": partition_order, "partitions": parameters, "escaped": escaped,
              "min_parameter": min(rice) if rice else -1, "max_parameter": max(rice) if rice else -1})
    return values


def restore(warm, residual, d):
    out = list(warm)
    order = d["order"]
    if d["type"] == "fixed":
        for r in residual:
            n = len(out)
            if order == 0:
                p = 0
            elif order == 1:
                p = out[n - 1]
            elif order == 2:
                p = 2 * out[n - 1] - out[n - 2]
            elif order == 3:
                p = 3 * out[n - 1] - 3 * out[n - 2] + out[n - 3]
            else:
                p = 4 * out[n - 1] - 6 * out[n - 2] + 4 * out[n - 3] - out[n - 4]
            out.append(r + p)
    else:
        c, shift = d["coefficients"], d["shift"]
        for r in residual:
            n = len(out)
            out.append(r + (sum(c[j] * out[n - 1 - j] for j in range(order)) >> shift))
    return out


def walk_frame(data, p, h):
    """Walks the frame at p whose header is h (with bits resolved). Returns a dict: complete, cut, fault, fault_channel, length, crc16 values,
    the subframes' descriptions and the decoded channels (left, right etc. as the frame assigns them)."""
    bits = Bits(data, p)
    bits.pos += h["length"] * 8
    result = {"complete": False, "cut": False, "fault": None, "fault_channel": 0, "length": 0, "subframes": [], "padding_set": False}
    channels = []
    try:
        for c in range(h["channels"]):
            side = (h["channel_code"] in (8, 10) and c == 1) or (h["channel_code"] == 9 and c == 0)
            try:
                d, samples = subframe(bits, h["block_size"], h["bits"], side)
            except Fault as fault:
                result["fault"], result["fault_channel"] = str(fault), c
                return result
            result["subframes"].append(d)
            channels.append(samples)
        end_bit = bits.pos
        size = (end_bit + 7 >> 3) - p
        if p + size + 2 > len(data):
            result["cut"] = True
            return result
        if end_bit & 7:
            result["padding_set"] = bool(data[p + size - 1] & (0xFF >> (end_bit & 7)))
        result.update({"complete": True, "length": size + 2, "crc16": data[p + size] << 8 | data[p + size + 1], "computed_crc16": crc16(data[p:p + size])})
        result["channels"] = decorrelate(h, channels)
        return result
    except Cut:
        result["cut"] = True
        return result
    finally:
        result["bytes_read"] = max(0, ((bits.pos + 7) >> 3) - p)


def decorrelate(h, ch):
    code = h["channel_code"]
    if code < 8:
        return ch
    a, b = ch
    if code == 8:
        return [a, [x - y for x, y in zip(a, b)]]
    if code == 9:
        return [[x + y for x, y in zip(a, b)], b]
    mid = [(m << 1) | (s & 1) for m, s in zip(a, b)]
    return [[(m + s) >> 1 for m, s in zip(mid, b)], [(m - s) >> 1 for m, s in zip(mid, b)]]


class Stream:
    """Reads a data range as the analyzer does: the marker and metadata blocks, then the frames."""

    def __init__(self, data, start=0, end=None):
        self.data = data
        self.start = start
        self.end = len(data) if end is None else end
        self.info = None
        self.blocks = []
        self.frames = []
        self.skipped = []
        self.findings = []
        self.probed = 0
        self.budget = PROBE_ALLOWANCE + 4 * (self.end - self.start)
        self._parse()

    def finding(self, code, **extra):
        self.findings.append({"code": code, **extra})

    def resolve(self, h):
        h = dict(h)
        if self.info and h["rate_code"] == 0:
            h["sample_rate"] = self.info["sample_rate"]
        if self.info and not h["bits"]:
            h["bits"] = self.info["bits"]
        return h

    def consistent(self, h):
        i = self.info
        if not i:
            return True
        return (h["channels"] == i["channels"] and (h["rate_code"] == 0 or h["sample_rate"] in (0, i["sample_rate"]))
                and (h["size_code"] == 0 or h["bits"] == i["bits"]))

    def header_at(self, p):
        h, fault = header(self.data, p)
        return h if fault is None and self.consistent(self.resolve(h)) else None

    def attempt(self, p):
        h, fault = header(self.data, p)
        if h is None:
            return None, fault, None
        h = self.resolve(h)
        if not h["bits"]:
            return None, "samplesize", None
        cap = min(self.end, p + MAX_FRAME)
        walk = walk_frame(self.data[:cap], p, h)
        if walk["cut"] and cap - p >= MAX_FRAME and cap < self.end:
            walk["cut"], walk["fault"] = False, "TooLong"
        return h, fault, walk

    def chained(self, p):
        h, fault = header(self.data, p)
        if fault is not None or h is None or not self.consistent(self.resolve(h)) or not self.resolve(h)["bits"]:
            self.probed += MIN_HEADER
            return False
        h, fault, walk = self.attempt(p)
        self.probed += max(walk["bytes_read"], MIN_HEADER)
        if not walk["complete"]:
            return False
        if walk["crc16"] != walk["computed_crc16"]:
            return False
        end = p + walk["length"]
        return end == self.end or self.header_at(end) is not None

    def find_next(self, frm, limit):
        limit = min(limit, self.end - 1)
        p = max(frm, self.start)
        while p < limit and self.probed <= self.budget:
            q = self.data.find(b"\xff", p, limit)
            if q < 0:
                return -1
            if self.data[q + 1] & 0xFC == 0xF8 and self.chained(q):
                return q
            p = q + 1
        return -1

    def _parse(self):
        data, end = self.data, self.end
        p = self.start
        if data[p:p + 4] == b"fLaC":
            p = self._metadata(p + 4)
        else:
            self.finding("flac.no-marker")
        self._frames(p)

    def _metadata(self, p):
        data, end = self.data, self.end
        index = 0
        while p < end:
            if end - p < 4:
                self.finding("flac.metadata-truncated")
                return end
            if data[p] == 0xFF and data[p + 1] & 0xFC == 0xF8:
                self.finding("flac.metadata-no-last-block")
                return p
            last, kind = bool(data[p] & 0x80), data[p] & 0x7F
            length = int.from_bytes(data[p + 1:p + 4], "big")
            if kind == 127:
                self.finding("flac.metadata-forbidden-type")
                return p
            body_end = p + 4 + length
            self.blocks.append({"offset": p, "length": 4 + min(length, end - p - 4), "type": kind, "last": last, "body": length})
            if kind == 5 and body_end <= end:
                self._cuesheet(data[p + 4:body_end])
            if kind == 0 and index == 0 and length >= 34 and p + 38 <= end:
                self.info = streaminfo(data[p + 4:p + 38])
            if body_end > end:
                self.finding("flac.metadata-truncated")
                return end
            p = body_end
            index += 1
            if last:
                break
        if self.info is None:
            self.finding("flac.no-streaminfo")
        return p

    def _cuesheet(self, body):
        """RFC 9639, section 8.7: the lead-out is the last track, 170 for a compact disc and 255 otherwise. Only a cue sheet whose tracks fill the block is
        judged; the number is information when it is the other medium's."""
        if len(body) < 396:
            return
        compact_disc = bool(body[136] & 0x80)
        tracks, at, last = body[395], 396, None
        for _ in range(tracks):
            if at + 36 > len(body):
                return
            last = body[at + 8]
            at += 36 + 12 * body[at + 35]
            if at > len(body):
                return
        if at == len(body) and tracks and last in (170, 255) and last != (170 if compact_disc else 255):
            self.finding("flac.cuesheet-lead-out-medium")

    def _frames(self, p):
        data, end = self.data, self.end
        skip = -1
        fixed_size = None
        previous_end = None
        first = True
        strategy = None
        while p < end:
            h, fault, walk = self.attempt(p)
            length, status = -1, set()
            if h is not None:
                intact = fault is None
                if walk["complete"]:
                    frame_end = p + walk["length"]
                    if walk["crc16"] == walk["computed_crc16"] or frame_end == end or self.header_at(frame_end):
                        length = walk["length"]
                    elif intact and (nxt := self.find_next(p + 1, p + MAX_FRAME)) >= 0:
                        length, status = nxt - p, {"extent"}
                elif walk["cut"] and intact:
                    length, status = end - p, {"cut"}
                elif walk["fault"] and intact and (nxt := self.find_next(p + 1, p + MAX_FRAME)) >= 0:
                    length, status = nxt - p, {"unreadable", "extent"}
            if length > 0:
                if skip >= 0:
                    self.skipped.append([skip, p - skip])
                    skip = -1
                self._add(p, length, h, walk, status, fault)
                p += length
                continue
            skip = p if skip < 0 else skip
            nxt = self.find_next(p + 1, end)
            p = end if nxt < 0 else nxt
        if skip >= 0:
            self.skipped.append([skip, end - skip])
        if self.skipped:
            self.finding("flac.data-skipped", count=len(self.skipped))
        if self.probed > self.budget:
            self.finding("flac.resync-limit")
        self._finish()

    def _add(self, p, length, h, walk, status, fault):
        i = self.info
        frame = {"offset": p, "length": length, "header": h, "status": sorted(status), "crc8_ok": fault is None}
        cut = "cut" in status
        if fault == "crc8":
            frame["status"].append("crc8")
        number_size = (i["max_block"] if i and i["max_block"] else h["block_size"])
        if not self.frames:
            self.fixed_size = number_size
            self.strategy = h["variable"]
        first = h["number"] if h["variable"] else h["number"] * self.fixed_size
        frame["first_sample"] = first
        if not self.frames:
            if first != 0:
                self.finding("flac.first-sample-not-zero")
        elif first != self.next_sample:
            frame["gap"] = True
            self.finding("flac.sample-number-discontinuity")
        if h["variable"] != self.strategy:
            self.finding("flac.blocking-strategy-changes")
        self.next_sample = first + h["block_size"]
        if h["rate_code"] >= 12 and h["sample_rate"] == 0:
            self.finding("flac.sample-rate-zero")
        if self.frames and i and self.frames[-1]["header"]["block_size"] < i["min_block"] and not cut:
            self.finding("flac.block-size-out-of-range")
        if i and i["max_block"] >= 16 and h["block_size"] > i["max_block"]:
            self.finding("flac.block-size-out-of-range")
        if i and not cut and ((i["max_frame"] and length > i["max_frame"]) or (i["min_frame"] and length < i["min_frame"])):
            self.finding("flac.frame-size-out-of-range")
        if i:
            if h["rate_code"] and h["sample_rate"] and h["sample_rate"] != i["sample_rate"]:
                self.finding("flac.frame-differs-from-streaminfo", problem="sample rate")
            if h["size_code"] and h["bits"] != i["bits"]:
                self.finding("flac.frame-differs-from-streaminfo", problem="bits per sample")
            if h["channels"] != i["channels"]:
                self.finding("flac.frame-differs-from-streaminfo", problem="channels")
        data = self.data
        if not cut and length >= h["length"] + 2:
            stored = data[p + length - 2] << 8 | data[p + length - 1]
            computed = walk["computed_crc16"] if walk["complete"] and walk["length"] == length else crc16(data[p:p + length - 2])
            frame["crc16"], frame["computed_crc16"] = stored, computed
            if stored != computed:
                frame["status"].append("crc16")
        elif not cut:
            frame["status"].append("cut")
            cut = True
        if cut:
            self.finding("flac.frame-cut")
        if walk and walk["complete"] and "unreadable" not in status:
            frame["subframes"] = walk["subframes"]
            # The decoder refuses a frame whose channels or bits differ from STREAMINFO's: the samples would not fit the output it declared.
            if not i or (h["channels"] == i["channels"] and h["bits"] == i["bits"]):
                frame["channels"] = walk["channels"]
        if walk and walk["complete"] and not ({"extent", "unreadable"} & status):
            if walk["padding_set"]:
                self.finding("flac.padding-bits-set")
            for sub in walk["subframes"]:
                if sub.get("tight"):
                    self.finding("flac.partition-order-too-high")
                if sub.get("min_residual"):
                    self.finding("flac.residual-out-of-range")
        if "crc8" in frame["status"]:
            self.finding("flac.header-crc-mismatch")
        if "crc16" in frame["status"]:
            self.finding("flac.crc16-mismatch")
        if "unreadable" in status:
            frame["fault"] = walk["fault"]
            self.finding("flac.subframe-invalid", problem=walk["fault"])
        self.frames.append(frame)

    def _finish(self):
        i = self.info
        frames = self.frames
        self.samples = sum(f["header"]["block_size"] for f in frames if "cut" not in f["status"])
        if not frames:
            self.finding("flac.no-frames")
        if i is None:
            return
        if i["total"] == 0:
            if frames:
                self.finding("flac.total-samples-unknown")
        elif i["total"] != self.samples and frames:
            self.finding("flac.total-samples-mismatch")
        if i["md5"] == "00" * 16:
            self.finding("flac.md5-not-stored")

    # The decoded audio.
    def pcm(self):
        """Interleaved little-endian bytes of the decoded samples of the frames that decode, in the layout of STREAMINFO's MD5, and the
        number of samples per channel."""
        i = self.info
        width = (i["bits"] + 7) // 8
        out = bytearray()
        total = 0
        for f in self.frames:
            if "channels" not in f:
                continue
            for k in range(f["header"]["block_size"]):
                for ch in f["channels"]:
                    out += (ch[k] & ((1 << width * 8) - 1)).to_bytes(width, "little")
            total += f["header"]["block_size"]
        return bytes(out), total

    def is_clean(self):
        return not self.skipped and all(not f["status"] for f in self.frames)


def decode_md5(stream):
    pcm, samples = stream.pcm()
    return {"decoded_samples": samples, "decoded_pcm_md5": hashlib.md5(pcm).hexdigest()}


def md5_of_pcm(pcm):
    return hashlib.md5(pcm).hexdigest()


def verification(stream, decoded):
    """The verification the analyzer reports, without --verify (decoded False) and with it, by the rule of docs/design/decoders.md."""
    i = stream.info
    frames = stream.frames
    cut = sum("cut" in f["status"] for f in frames)
    has_gap = any(f.get("gap") for f in frames) or bool(frames and frames[0]["first_sample"] != 0)
    unreadable = sum("unreadable" in f["status"] for f in frames)
    matches = i is None or i["total"] == 0 or i["total"] == stream.samples
    crc8_failed = sum("crc8" in f["status"] for f in frames)
    crc16_failed = sum("crc16" in f["status"] for f in frames)
    md5_stored = 1 if i and i["md5"] != "00" * 16 else 0
    tallies = [
        {"name": "frame header CRC-8", "covers": "header", "coversAllAudio": False, "stored": len(frames), "checked": len(frames), "failed": crc8_failed},
        {"name": "frame CRC-16", "covers": "codedData", "coversAllAudio": not has_gap and matches and cut == 0 and unreadable == 0, "stored": len(frames), "checked": len(frames) - cut, "failed": crc16_failed},
        {"name": "STREAMINFO MD5", "covers": "decodedAudio", "coversAllAudio": True, "stored": md5_stored, "checked": 0, "failed": 0},
    ]
    status = "notRequested"
    if decoded and (i is None or i["bits"] < 4):
        status = "notPossible"
    elif decoded:
        status = "decoded"
        pcm, samples = stream.pcm()
        not_decoded = sum(1 for f in frames if "channels" not in f)
        complete = not_decoded == 0 and not has_gap and (i is None or i["total"] == 0 or i["total"] == samples)
        if md5_stored and complete:
            tallies[2]["checked"] = 1
            tallies[2]["failed"] = 0 if md5_of_pcm(pcm) == i["md5"] else 1
    if any(t["failed"] for t in tallies):
        verified = False
    elif any(t["stored"] - t["checked"] > 0 for t in tallies):
        verified = None
    elif any(t["coversAllAudio"] and t["checked"] > 0 for t in tallies):
        verified = True
    else:
        verified = None
    return {"isVerified": verified, "decoding": status, "checksums": tallies}


# What the analyzer reports as the stream.
def stream_info(stream):
    i = stream.info
    frames = [f for f in stream.frames if "cut" not in f["status"]]
    if i is None and not stream.frames:
        return None
    first = stream.frames[0]["header"] if stream.frames else None
    rate = i["sample_rate"] if i and i["sample_rate"] else first["sample_rate"]
    channels = i["channels"] if i else first["channels"]
    bits = i["bits"] if i else first["bits"]
    samples = stream.samples
    bits_total = sum(f["length"] * 8 for f in frames)
    return {"codec": "FLAC", "sample_rate": rate, "channels": channels, "samples": samples, "duration": samples / rate if rate else 0.0, "bits_per_sample": bits,
            "average_bitrate": bits_total * rate / samples / 1000 if samples else None, "layout": LAYOUTS.get(channels, [])}


def manifest(data, start=0, end=None, extra=None):
    stream = Stream(data, start, end)
    result = {"length": len(data), "metadata": [], "frames": [], "skipped": stream.skipped}
    for b in stream.blocks:
        result["metadata"].append({"offset": b["offset"], "length": b["length"], "type": BLOCK_TYPES.get(b["type"], "Reserved"), "is_last": b["last"], "body_length": b["body"]})
    if stream.info:
        result["stream_info"] = stream.info
    for f in stream.frames:
        h = f["header"]
        entry = {"offset": f["offset"], "length": f["length"], "variable": h["variable"], "block_size": h["block_size"], "sample_rate": h["sample_rate"],
                 "channel_code": h["channel_code"], "bits": h["bits"], "number": h["number"], "crc8": f"{h['crc8']:02x}", "status": f["status"],
                 "crc16": None if "crc16" not in f else f"{f['crc16']:04x}"}
        if "subframes" in f:
            entry["subframes"] = [[s["type"], s["order"], s["wasted"]] + ([s["partition_order"]] if "partition_order" in s else []) for s in f["subframes"]]
        result["frames"].append(entry)
    result["stream_infos"] = [stream_info(stream)] if stream_info(stream) else []
    result["findings"] = [{k: v for k, v in item.items()} for item in stream.findings]
    result.update(decode_md5(stream) if stream.info and stream.info["bits"] >= 4 else {})
    result["verification"] = {"default": verification(stream, False), "verify": verification(stream, True)}
    result.update(extra or {})
    return result


# The writer of the hand-made fixtures.
class BitWriter:
    def __init__(self):
        self.bits = []

    def write(self, value, n):
        for k in range(n - 1, -1, -1):
            self.bits.append(value >> k & 1)

    def signed(self, value, n):
        self.write(value & ((1 << n) - 1), n)

    def unary(self, q):
        self.bits.extend([0] * q + [1])

    def align(self):
        self.bits.extend([0] * (-len(self.bits) % 8))

    def bytes(self):
        out = bytearray()
        for i in range(0, len(self.bits), 8):
            byte = 0
            for b in self.bits[i:i + 8]:
                byte = byte << 1 | b
            out.append(byte)
        return bytes(out)


def utf8_number(n):
    """n as the frame header codes a frame or sample number: UTF-8 style, up to seven bytes."""
    if n < 0x80:
        return bytes([n])
    count = next(c for c, limit in ((2, 0x800), (3, 0x10000), (4, 0x200000), (5, 0x4000000), (6, 0x80000000), (7, 1 << 36)) if n < limit)
    out = []
    for _ in range(count - 1):
        out.append(0x80 | n & 0x3F)
        n >>= 6
    out.append((0xFF << (8 - count)) & 0xFF | n)
    return bytes(reversed(out))


def frame_header(number, block, rate, channel_code, bits, variable=False, rate_code=None, block_code=None, size_code=None):
    """A frame header with its CRC-8. block, rate and bits take the shortest code that names them unless a code is given."""
    if block_code is None:
        table = {192: 1, 576: 2, 1152: 3, 2304: 4, 4608: 5, 256: 8, 512: 9, 1024: 10, 2048: 11, 4096: 12, 8192: 13, 16384: 14, 32768: 15}
        block_code = table.get(block, 6 if block <= 256 else 7)
    if rate_code is None:
        rate_code = {v: k for k, v in RATES.items()}.get(rate)
        if rate_code is None:
            rate_code = 12 if rate % 1000 == 0 and rate < 256000 else 13 if rate < 65536 else 14
    if size_code is None:
        size_code = {8: 1, 12: 2, 16: 4, 20: 5, 24: 6, 32: 7}[bits]
    out = bytearray([0xFF, 0xF8 | (1 if variable else 0), block_code << 4 | rate_code, channel_code << 4 | size_code << 1])
    out += utf8_number(number)
    if block_code == 6:
        out.append(block - 1)
    elif block_code == 7:
        out += (block - 1).to_bytes(2, "big")
    if rate_code == 12:
        out.append(rate // 1000)
    elif rate_code == 13:
        out += rate.to_bytes(2, "big")
    elif rate_code == 14:
        out += (rate // 10).to_bytes(2, "big")
    out.append(crc8(out))
    return bytes(out)


def fold(v):
    return v << 1 if v >= 0 else (-v << 1) - 1


def write_residual(w, residual, block, order, partition_order=0, method=0, escape=None):
    """A residual in partitions; escape maps a partition index to the bits of its fixed-width samples (None: Rice with the best parameter)."""
    w.write(method, 2)
    w.write(partition_order, 4)
    escape = escape or {}
    n = block >> partition_order
    at = 0
    for p in range(1 << partition_order):
        count = n - (order if p == 0 else 0)
        part = residual[at:at + count]
        at += count
        width = 4 if method == 0 else 5
        if p in escape:
            w.write((1 << width) - 1, width)
            w.write(escape[p], 5)
            for v in part:
                w.signed(v, escape[p]) if escape[p] else None
            continue
        folded = [fold(v) for v in part]
        k = 0
        while k < (1 << width) - 2 and sum(f >> k for f in folded) > sum(f >> (k + 1) for f in folded) + len(folded):
            k += 1
        w.write(k, width)
        for f in folded:
            w.unary(f >> k)
            w.write(f & ((1 << k) - 1), k)


def write_subframe(w, samples, kind, bits, wasted=0, order=0, coefficients=None, precision=0, shift=0, partition_order=0, method=0, escape=None):
    """Writes a subframe of samples (already without the wasted bits' zeros removed: they are shifted down here)."""
    block = len(samples)
    samples = [s >> wasted for s in samples]
    sample_bits = bits - wasted
    w.write(0, 1)
    if kind == "constant":
        w.write(0, 6)
    elif kind == "verbatim":
        w.write(1, 6)
    elif kind == "fixed":
        w.write(8 + order, 6)
    else:
        w.write(32 + order - 1, 6)
    if wasted:
        w.write(1, 1)
        w.unary(wasted - 1)
    else:
        w.write(0, 1)
    if kind == "constant":
        w.signed(samples[0], sample_bits)
        return
    if kind == "verbatim":
        for s in samples:
            w.signed(s, sample_bits)
        return
    for s in samples[:order]:
        w.signed(s, sample_bits)
    residual = []
    for n in range(order, block):
        if kind == "fixed":
            if order == 0:
                p = 0
            elif order == 1:
                p = samples[n - 1]
            elif order == 2:
                p = 2 * samples[n - 1] - samples[n - 2]
            elif order == 3:
                p = 3 * samples[n - 1] - 3 * samples[n - 2] + samples[n - 3]
            else:
                p = 4 * samples[n - 1] - 6 * samples[n - 2] + 4 * samples[n - 3] - samples[n - 4]
        else:
            p = sum(coefficients[j] * samples[n - 1 - j] for j in range(order)) >> shift
        residual.append(samples[n] - p)
    if kind == "lpc":
        w.write(precision - 1, 4)
        w.signed(shift, 5)
        for c in coefficients:
            w.signed(c, precision)
    write_residual(w, residual, block, order, partition_order, method, escape)


def write_frame(header_bytes, subframes):
    """header_bytes and the subframes (each a function that writes into a BitWriter), then the padding and CRC-16."""
    w = BitWriter()
    for write in subframes:
        write(w)
    w.align()
    body = header_bytes + w.bytes()
    return body + crc16(body).to_bytes(2, "big")


def metadata_block(kind, body, last=False):
    return bytes([(0x80 if last else 0) | kind]) + len(body).to_bytes(3, "big") + body


def streaminfo_body(min_block, max_block, min_frame, max_frame, rate, channels, bits, total, md5):
    v = rate << 44 | (channels - 1) << 41 | (bits - 1) << 36 | total
    return struct.pack(">HH", min_block, max_block) + min_frame.to_bytes(3, "big") + max_frame.to_bytes(3, "big") + v.to_bytes(8, "big") + md5
