#!/usr/bin/env python3
"""Variants of the Windows-written WMA Professional fixtures, to ask Microsoft's decoder one question at a time.

    python3 make_wma_pro_variants.py <fixtures folder> <output folder>

Each variant changes one thing in a copy of one of three base files (two stereo files at 44.1 kHz and a 5.1 file at
48 kHz, see BASES) and nothing else; the decoder under test is a black box, so only what it outputs is read. The kinds:

  cut-<k>              the stream ends after frame k: its trailer bit is cleared, the bits after it in its packet are
                       zeroed and the later packets are removed (cut-open-<k>: the trailer bit is left as it is, so the
                       frame says another follows and the next length prefix is 0)
  cut-<k>-end-<T>      the same, with an end trim of T samples written into frame k (14 bits are inserted)
  end-trim-<T>         the value of the last frame's end trim set to T (T below 4096, the field is 12 bits)
  end-trim-flag-off    the last frame states no end trim (12 bits removed)
  trim-off-last        the last frame has no trim field content at all (its trim flag is 0)
  start-trim-<T>       the value of the first frame's start trim set to T
  start-trim-flag-off  the first frame states no start trim
  trim-off-first       the first frame has no trim field content at all
  play-<ms>, preroll-<ms>   the ASF File Properties Object's play duration or preroll, in milliseconds

The frames are found by their length prefixes (docs/design/wma-pro-format.md, "Packets" and "Frame"); the trim fields
by reading each tile header. Writes <output folder>/<name>.wma and manifest.json, which holds for each variant what the
comparison needs: the frame count, the frame size F, the trims of the first and last frame and the size of the last
subframe of each channel of the last frame. Nothing is guessed: a variant whose bits do not read as expected is
reported and skipped.
"""

import json
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
import asf_model  # noqa: E402

BASES = {
    "stereo-128": "external-windows-wma10pro-cbr-stereo-44k-128.wma",
    "stereo-64": "external-windows-wma10pro-cbr-stereo-44k-64.wma",
    "surround-384": "external-windows-wma10pro-cbr-5.1-48k-384.wma",
}
FILE_PROPERTIES = bytes.fromhex("a1dcab8c47a9cf118ee400c00c205365")
DATA_OBJECT = bytes.fromhex("3626b2758e66cf11a6d900aa0062ce6c")


class Unreadable(Exception):
    pass


def frame_size_log2(rate, flags):
    b = 9 if rate <= 16000 else 10 if rate <= 22050 else 11 if rate <= 48000 else 12 if rate <= 96000 else 13
    return b + {0: 0, 1: 1, 2: -1, 3: -2}[(flags >> 1) & 3]


class Bits:
    """A payload as a string of '0' and '1', most significant bit first."""

    def __init__(self, raw):
        self.s = "".join(f"{byte:08b}" for byte in raw)

    def get(self, pos, n):
        if pos + n > len(self.s):
            raise Unreadable("read past the payload")
        return int(self.s[pos:pos + n], 2) if n else 0

    def put(self, pos, n, value):
        self.s = self.s[:pos] + format(value, f"0{n}b") + self.s[pos + n:]

    def to_bytes(self):
        return int(self.s, 2).to_bytes(len(self.s) // 8, "big")


class ProFile:
    def __init__(self, data):
        self.data = bytearray(data)
        parsed = asf_model.parse(bytes(data))
        stream = next(s for s in parsed["streams"] if s.get("tag") == 0x162)
        codec = bytes.fromhex(stream["codec_data"])
        self.block_align = stream["block_align"]
        self.rate = stream["rate"]
        self.flags = codec[14] | codec[15] << 8
        mask = int.from_bytes(codec[2:6], "little")
        self.channels = bin(mask).count("1") if mask else stream["channels"]
        self.log2_m = (self.flags >> 3) & 7
        self.has_prefix = bool(self.flags & 0x40)
        self.has_gain = bool(self.flags & 0x80)
        self.f_log2 = frame_size_log2(self.rate, self.flags)
        self.f = 1 << self.f_log2
        self.w = self.block_align.bit_length() - 1 + 4
        if not self.has_prefix:
            raise Unreadable("no length prefix")
        objects = [m for m in parsed["media_objects"] if m[0] == stream["number"]]
        self.payloads = []
        for obj in objects:
            if len(obj[4]) != 1 or obj[4][0][1] != self.block_align:
                raise Unreadable("a media object that is not one whole packet")
            self.payloads.append(tuple(obj[4][0]))
        self.packets = parsed["packets"]
        header_size = struct.unpack_from("<Q", self.data, 16)[0]
        self.data_offset = header_size
        if bytes(self.data[header_size:header_size + 16]) != DATA_OBJECT:
            raise Unreadable("no Data Object after the header")
        self.file_properties = self.find_object(FILE_PROPERTIES, 30, header_size)

    def find_object(self, guid, start, end):
        at = start
        while at + 24 <= end:
            size = struct.unpack_from("<Q", self.data, at + 16)[0]
            if bytes(self.data[at:at + 16]) == guid:
                return at
            at += size
        raise Unreadable("object not found")

    def packet_bits(self, index):
        offset, length = self.payloads[index]
        return Bits(bytes(self.data[offset:offset + length]))

    def walk(self):
        """The frames: (start packet, start bit, length in bits, end packet, end bit), the end bit after the trailer bit."""
        frames = []
        carry = None
        w = self.w
        for index, (offset, length) in enumerate(self.payloads):
            bits = Bits(bytes(self.data[offset:offset + length]))
            total = length * 8
            count = bits.get(6, w)
            pos = 6 + w + count
            if carry is not None:
                s_index, s_pos, s_len = carry
                remaining = s_pos + s_len - self.payloads[s_index][1] * 8
                pos = 6 + w + remaining
                frames.append((s_index, s_pos, s_len, index, pos))
                carry = None
                if count >= total - 6 - w:
                    continue  # a count that covers the payload: the whole payload belongs to the frame before
            while total - pos > w:
                flen = bits.get(pos, w)
                if flen == 0:
                    break
                if pos + flen <= total:
                    frames.append((index, pos, flen, index, pos + flen))
                    trailer = bits.get(pos + flen - 1, 1)
                    pos += flen
                    if not trailer:
                        break
                else:
                    carry = (index, pos, flen)
                    break
        return frames

    def frame_info(self, frame):
        """The trim fields and the last subframe size of each channel of a frame that lies in one packet."""
        s_index, s_pos = frame[0], frame[1]
        bits = self.packet_bits(s_index)
        pos = s_pos + self.w
        c = self.channels
        m_count = 1 << self.log2_m
        smallest = self.f // m_count
        aligned = 1 if m_count == 1 else bits.get(pos, 1)
        pos += 0 if m_count == 1 else 1
        width = (self.log2_m.bit_length() - 1) + 1 if self.log2_m else 0
        sums = [0] * c
        sizes = [[] for _ in range(c)]
        while min(sums) < self.f:
            s = min(sums)
            candidates = [i for i in range(c) if sums[i] == s]
            taking = []
            for i in candidates:
                if aligned or len(candidates) == 1 or s == self.f - smallest:
                    taking.append(i)
                else:
                    if bits.get(pos, 1):
                        taking.append(i)
                    pos += 1
            if s == self.f - smallest:
                length = smallest
            elif m_count in (4, 16):
                first = bits.get(pos, 1)
                pos += 1
                if first == 0:
                    length = self.f
                else:
                    v = bits.get(pos, width - 1)
                    pos += width - 1
                    length = self.f >> (v + 1)
            else:
                v = bits.get(pos, width)
                pos += width
                length = self.f >> v
            for i in taking:
                sums[i] += length
                sizes[i].append(length)
            if any(x > self.f for x in sums):
                raise Unreadable("tile header overruns the frame")
        if c > 1:
            if bits.get(pos, 1):
                raise Unreadable("postprocessing transform")
            pos += 1
        if self.has_gain:
            pos += 8
        info = {"trim_pos": pos, "last_sizes": [x[-1] for x in sizes], "first_sizes": [x[0] for x in sizes], "start": 0, "end": 0,
                "start_flag_pos": None, "start_pos": None, "end_flag_pos": None, "end_pos": None}
        width_t = self.f_log2 + 1
        flag = bits.get(pos, 1)
        pos += 1
        if flag:
            info["start_flag_pos"] = pos
            if bits.get(pos, 1):
                info["start_pos"] = pos + 1
                info["start"] = bits.get(pos + 1, width_t)
                pos += 1 + width_t
            else:
                pos += 1
            info["end_flag_pos"] = pos
            if bits.get(pos, 1):
                info["end_pos"] = pos + 1
                info["end"] = bits.get(pos + 1, width_t)
                pos += 1 + width_t
            else:
                pos += 1
        info["trim_end"] = pos
        info["flag"] = flag
        return info


def edit_frame(pro, frames, k, replace_from, replace_to, new_bits):
    """Replaces the bits [replace_from, replace_to) of frame k by new_bits; the rest of its packet moves, the frame's length
    prefix follows. Only for a frame that lies in one packet with nothing but whole frames after it in that packet."""
    s_index, s_pos, s_len, e_index, e_pos = frames[k]
    if s_index != e_index:
        raise Unreadable("frame spans packets")
    if any(f[0] == s_index and f[3] != s_index for f in frames):
        raise Unreadable("a frame crosses out of the packet")
    bits = pro.packet_bits(s_index)
    delta = len(new_bits) - (replace_to - replace_from)
    if delta > 0 and "1" in bits.s[len(bits.s) - delta:]:
        raise Unreadable("no room in the packet")
    s = bits.s[:replace_from] + new_bits + bits.s[replace_to:]
    s = s[:len(bits.s)] if delta > 0 else s + "0" * (-delta)
    bits.s = s
    bits.put(s_pos, pro.w, s_len + delta)
    offset, length = pro.payloads[s_index]
    pro.data[offset:offset + length] = bits.to_bytes()
    return delta


def fix_counts(pro, packets, size_after):
    """Header fields that count or measure the file: Data Object size and packet count, File Properties file size and packets."""
    d = pro.data_offset
    struct.pack_into("<Q", pro.data, d + 16, size_after - d)
    struct.pack_into("<Q", pro.data, d + 40, packets)
    f = pro.file_properties
    struct.pack_into("<Q", pro.data, f + 40, size_after)
    struct.pack_into("<Q", pro.data, f + 56, packets)


def cut_after(pro, frames, k, clear_trailer=True):
    """Zeroes everything after frame k in its packet, clears its trailer bit (the frame says no frame follows) and removes
    the later packets."""
    s_index, s_pos, s_len, e_index, e_pos = frames[k]
    bits = pro.packet_bits(e_index)
    bits.s = bits.s[:e_pos] + "0" * (len(bits.s) - e_pos)
    if clear_trailer:
        bits.put(e_pos - 1, 1, 0)
    offset, length = pro.payloads[e_index]
    pro.data[offset:offset + length] = bits.to_bytes()
    packet_start, packet_size, _ = pro.packets[e_index]
    end = packet_start + packet_size
    del pro.data[end:]
    fix_counts(pro, e_index + 1, end)


def summarize(data):
    """What the comparison needs, read back from the variant itself."""
    pro = ProFile(bytes(data))
    frames = pro.walk()
    first = pro.frame_info(frames[0])
    try:
        last = pro.frame_info(frames[-1])
    except Unreadable:
        last = None
    prev = None
    if len(frames) > 1:
        try:
            prev = pro.frame_info(frames[-2])["last_sizes"]
        except Unreadable:
            prev = None
    return {
        "frames": len(frames), "frameSize": pro.f, "channels": pro.channels, "rate": pro.rate,
        "startTrim": first["start"], "startStated": first["start_pos"] is not None, "firstSizes": first["first_sizes"],
        "prevLastSizes": prev, "firstEndTrim": first["end"],
        "endTrim": last["end"] if last else None, "lastStartTrim": last["start"] if last else None,
        "lastSizes": last["last_sizes"] if last else None, "packets": len(pro.payloads),
    }


def variants_of(base_name, raw):
    pro = ProFile(raw)
    frames = pro.walk()
    last_k = len(frames) - 1
    out = []  # (kind, bytes)
    seen = {}

    def add(kind, build):
        try:
            p = ProFile(raw)
            fr = p.walk()
            build(p, fr)
            out.append((kind, bytes(p.data)))
        except Unreadable as error:
            seen[kind] = str(error)

    def set_value(which, value):
        def build(p, fr):
            k = last_k if which == "end" else 0
            info = p.frame_info(fr[k])
            pos = info[f"{which}_pos"]
            if pos is None:
                raise Unreadable(f"no {which} trim to set")
            bits = p.packet_bits(fr[k][0])
            bits.put(pos, p.f_log2 + 1, value)
            offset, length = p.payloads[fr[k][0]]
            p.data[offset:offset + length] = bits.to_bytes()
        return build

    def drop(which, whole):
        def build(p, fr):
            k = last_k if which == "end" else 0
            info = p.frame_info(fr[k])
            s_pos = fr[k][1]
            if whole:
                edit_frame(p, fr, k, info["trim_pos"], info["trim_end"], "0")
            else:
                flag_pos, value_pos = info[f"{which}_flag_pos"], info[f"{which}_pos"]
                if value_pos is None:
                    raise Unreadable(f"no {which} trim to remove")
                edit_frame(p, fr, k, flag_pos, value_pos + p.f_log2 + 1, "0")
        return build

    def cut(k, end_trim=None, clear_trailer=True):
        def build(p, fr):
            cut_after(p, fr, k, clear_trailer)
            if end_trim is not None:
                q = ProFile(bytes(p.data))
                fr2 = q.walk()
                info = q.frame_info(fr2[k])
                if info["flag"]:
                    raise Unreadable("the frame already has a trim")
                width = q.f_log2 + 1
                edit_frame(q, fr2, k, info["trim_pos"], info["trim_end"], "1" + "0" + "1" + format(end_trim, f"0{width}b"))
                p.data = q.data
        return build

    for k in range(len(frames)):
        if base_name != "stereo-64" or k in (0, last_k - 2, last_k - 1, last_k):
            add(f"cut-{k}", cut(k))
        if k % 8 == 3 and base_name != "stereo-64":
            add(f"cut-open-{k}", cut(k, clear_trailer=False))
    ends = [0, 1, 256, 512, 820, 1024, 1331, 1536, 2000, 2047, 2048, 2049, 2100, 2300, 2500, 2559, 2560, 2561, 3000, 4095]
    for value in ends:
        add(f"end-trim-{value}", set_value("end", value))
    add("end-trim-flag-off", drop("end", False))
    add("trim-off-last", drop("end", True))
    for value in [0, 1, 512, 1024, 2047, 2049, 3000, 4095]:
        add(f"start-trim-{value}", set_value("start", value))
    add("start-trim-flag-off", drop("start", False))
    add("trim-off-first", drop("start", True))
    # end trims on cut frames, for frames of different last subframe sizes
    infos = {}
    for k in range(len(frames)):
        try:
            infos[k] = pro.frame_info(frames[k])
        except Unreadable:
            continue
    by_size = {}
    for k, info in infos.items():
        if k not in (0, last_k) and not info["flag"]:
            by_size.setdefault(max(info["last_sizes"]), []).append(k)
    for size, ks in sorted(by_size.items()):
        for k in ks[:2]:
            for value in (500, 1500):
                add(f"cut-{k}-end-{value}", cut(k, value))

    def from_packet(j):
        def build(p, fr):
            if not any(f[0] == j and f[1] == 6 + p.w for f in fr):
                raise Unreadable("a frame does not start at the packet's first bit")
            start = p.packets[0][0]
            cut_at = p.packets[j][0]
            del p.data[start:cut_at]
            fix_counts(p, len(p.payloads) - j, len(p.data))
        return build

    for j in range(1, len(pro.payloads)):
        add(f"from-{j}", from_packet(j))

    def header(field, ms):
        def build(p, fr):
            offset = p.file_properties + (64 if field == "play" else 80)
            value = ms * 10000 if field == "play" else ms
            struct.pack_into("<Q", p.data, offset, value)
        return build

    for ms in (0, 1500, 3000, 6033, 9000):
        add(f"play-{ms}", header("play", ms))
    for ms in (0, 1500, 6000):
        add(f"preroll-{ms}", header("preroll", ms))
    return out, seen


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    fixtures, output = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    output.mkdir(parents=True, exist_ok=True)
    manifest = {}
    skipped = {}
    for base, name in BASES.items():
        raw = (fixtures / name).read_bytes()
        variants, seen = variants_of(base, raw)
        manifest_base = summarize(raw)
        manifest[f"pro-{base}-base.wma"] = dict(manifest_base, base=base, kind="base")
        (output / f"pro-{base}-base.wma").write_bytes(raw)
        for kind, data in variants:
            file = f"pro-{base}-{kind}.wma"
            (output / file).write_bytes(data)
            try:
                manifest[file] = dict(summarize(data), base=base, kind=kind)
            except Unreadable as error:
                manifest[file] = {"base": base, "kind": kind, "summaryError": str(error)}
        skipped[base] = seen
    (output / "manifest.json").write_text(json.dumps({"files": manifest, "skipped": skipped}, indent=1) + "\n", encoding="utf-8")
    print(f"{len(manifest)} files, skipped: " + json.dumps(skipped))
    return 0


if __name__ == "__main__":
    sys.exit(main())
