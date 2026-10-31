#!/usr/bin/env python3
"""A writer of Windows Media Audio Voice streams for the hand-built fixtures (make_voice_fixtures.py).

It writes the 46 bytes of codec data and superframes bit by bit as docs/design/wma-voice-format.md describes them (the
sections "Codec data", "Frame type codes", "Superframe", "Frame", "Pitch state" and "Writing valid streams"). Every field
that carries no structure is drawn at random from one generator per stream, in the order that document lists, so a given
seed always writes the same bits. Standard library only.
"""

import random
import struct

import asf_model

FRAME_SAMPLES = 160
SUPERFRAME_SAMPLES = 480
FRAME_TYPES = 17
DEFAULT_GROUPS = [1, 1, 0, 0, 0, 1, 2, 2, 4, 4, 5, 2, 3, 3, 5, 3, 4]
INITIAL_PITCH = 40

# Per frame type: blocks, log2 of blocks, adaptive codebook (0 none, 1 pitch per frame, 2 pitch per block), fixed codebook
# (0 silence, 1 hardcoded, 2 pitch-adaptive window pulses, 3 pulse sets) and the pulse vectors that hold a pair.
FRAME_DESCRIPTORS = [
    (1, 0, 0, 0, 0), (2, 1, 0, 1, 0), (2, 1, 1, 2, 0), (2, 1, 1, 3, 2), (2, 1, 1, 3, 5), (4, 2, 1, 3, 0),
    (4, 2, 1, 3, 2), (4, 2, 1, 3, 5), (2, 1, 2, 3, 0), (2, 1, 2, 3, 2), (2, 1, 2, 3, 5), (4, 2, 2, 3, 0),
    (4, 2, 2, 3, 2), (4, 2, 2, 3, 5), (8, 3, 2, 3, 0), (8, 3, 2, 3, 2), (8, 3, 2, 3, 5),
]

# The code lengths of the 22 slots of the frame type code.
CODE_LENGTHS = [2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14, 14]

# The offset of the first window pulse of a pitch-adaptive window frame, by its window index.
START_OFFSETS = [
    -11, -9, -7, -5, -3, -1, 1, 3, 5, 7, 9, 11, 13, 15, 18, 17, 19, 20, 21, 22, 23, 24, 25, 26,
    27, 28, 29, 30, 31, 32, 33, 35, 37, 39, 41, 43, 45, 47, 49, 51, 53, 55, 57, 59, 61, 63, 65, 67,
    69, 71, 73, 75, 77, 79, 81, 83, 85, 87, 89, 91, 93, 95, 97, 99, 101, 103, 105, 107, 109, 111, 113, 115,
    117, 119, 121, 123, 125, 127, 129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149, 151, 153, 155, 157, 159,
]


def ceil_log2(value):
    return 0 if value <= 1 else (value - 1).bit_length()


class BitWriter:
    """Bits written most significant first."""

    def __init__(self):
        self.value = 0
        self.count = 0

    def __len__(self):
        return self.count

    def write(self, value, count):
        if count:
            self.value = (self.value << count) | (value & ((1 << count) - 1))
            self.count += count
        return self

    def random(self, rng, count):
        """One draw of count bits (none for a width of 0)."""
        if count:
            self.write(rng.getrandbits(count), count)
        return self

    def append(self, other, start=0, end=None):
        end = other.count if end is None else end
        width = end - start
        if width > 0:
            self.write((other.value >> (other.count - end)) & ((1 << width) - 1), width)
        return self

    def to_bytes(self, size=None):
        size = (self.count + 7) // 8 if size is None else size
        if size * 8 < self.count:
            raise ValueError(f"{self.count} bits do not fit {size} bytes")
        return (self.value << (size * 8 - self.count)).to_bytes(size, "big")


def set_bits(data, position, count, value):
    """data with count bits from the bit position (most significant first) replaced by value."""
    number = int.from_bytes(data, "big")
    shift = len(data) * 8 - position - count
    mask = ((1 << count) - 1) << shift
    return ((number & ~mask) | ((value << shift) & mask)).to_bytes(len(data), "big")


def slot_table(groups):
    """The frame type whose code each of the slots holds (None for none): type t takes slot 3 * its group + the number of earlier types of
    the group, and a later type replaces an earlier one in a slot."""
    taken = {}
    holder = {}
    for t, g in enumerate(groups):
        slot = 3 * g + taken.get(g, 0)
        taken[g] = taken.get(g, 0) + 1
        holder[slot] = t
    return holder


def slots_of(groups):
    """For each frame type its slot, or None where no slot from 0 to 21 holds it."""
    holder = slot_table(groups)
    return [next((s for s, t in holder.items() if t == type_ and s < len(CODE_LENGTHS)), None) for type_ in range(len(groups))]


class Config:
    """The configuration of a stream: what the WAVEFORMATEX and its 46 bytes of codec data state. It records and packs; it checks nothing."""

    def __init__(self, rate, align, groups=None, postfilter=True, denoise=5, tilt=False, dc_level=3, lsp16=False, q_mode=0, default_mode=0,
                 residual=True, header=None, flags_override=None):
        self.rate = rate
        self.align = align
        self.groups = list(DEFAULT_GROUPS if groups is None else groups)
        self.postfilter = postfilter
        self.denoise = denoise
        self.tilt = tilt
        self.dc_level = dc_level
        self.lsp16 = lsp16
        self.q_mode = q_mode
        self.default_mode = default_mode
        self.residual = residual
        self.header = header if header is not None else bytes([0x10, 0, 0x04, 0, 0, 0]) + bytes(12)
        self.flags_override = flags_override

        self.spillover_bits = 3 + ceil_log2(align) if align > 0 else 0
        self.lsp_count = 16 if lsp16 else 10
        self.independent_lsp_bits = 34 if lsp16 else 24
        self.residual_lsp_bits = 60 if lsp16 else 48
        self.min_pitch = ((256 * rate // 400) + 50) // 256
        self.max_pitch = ((256 * 37 * rate // 2000) + 50) // 256
        span = self.max_pitch - self.min_pitch
        self.pitch_bits = ceil_log2(span)
        self.anchors = (self.min_pitch, 25 * span // 64, 44 * span // 64, self.max_pitch - 1)
        half = 16 * ((span // 8) // 16)
        self.half_step = half
        self.delta_bits = 1 + ceil_log2(half)
        self.block_pitch_range = self.anchors[2] + self.anchors[3] + 1 + 2 * (self.anchors[1] - 2 * self.min_pitch)
        self.block_pitch_bits = ceil_log2(self.block_pitch_range)
        self.slots = slots_of(self.groups)

    def flags(self):
        if self.flags_override is not None:
            return self.flags_override
        return ((1 if self.postfilter else 0) | (self.denoise << 2) | (0x40 if self.tilt else 0) | (self.dc_level << 7)
                | (0x1000 if self.lsp16 else 0) | (0x2000 if self.q_mode else 0) | (0x4000 if self.default_mode else 0))

    def codec_data(self):
        table = BitWriter()
        for g in self.groups:
            table.write(g, 3)
        table.write(0, 24 * 8 - len(table))
        return bytes(self.header) + struct.pack("<I", self.flags()) + table.to_bytes()

    def wave_format(self, rate=None, align=None, extra=None, tag=0x000A, channels=1):
        rate = self.rate if rate is None else rate
        align = self.align if align is None else align
        data = self.codec_data() if extra is None else extra
        return asf_model.wave_format(tag, channels, rate, rate // 8, align, 16, data)


class Writer:
    """Writes the superframes of one stream. The pitch state carries through all of them."""

    def __init__(self, config, seed):
        self.config = config
        self.rng = random.Random(seed)
        self.pitch = INITIAL_PITCH
        self.kind = 0

    def superframe(self, types, samples=None, additional=0, residual=None, speech=True):
        """The bits of one superframe of three frames of the given types, and what was written."""
        config = self.config
        residual = config.residual if residual is None else residual
        bits = BitWriter().write(1 if speech else 0, 1)
        bits.write(0 if samples is None else 1, 1)
        if samples is not None:
            bits.write(samples, 12)
        if residual:
            bits.random(self.rng, config.residual_lsp_bits)
        frames = [self.frame(t, bits, not residual) for t in types]
        if additional:
            bits.write(1, 1).write(additional - 1, 4).random(self.rng, 10 * additional)
        else:
            bits.write(0, 1)
        info = {"types": list(types), "frames": frames, "samples": SUPERFRAME_SAMPLES if samples is None else samples,
                "bits": len(bits), "additional": additional}
        return bits, info

    def frame(self, type_, out, own_lsps):
        config = self.config
        rng = self.rng
        blocks_count, _, adaptive, fixed, pairs = FRAME_DESCRIPTORS[type_]
        slot = config.slots[type_]
        if slot is None:
            raise ValueError(f"frame type {type_} has no code")
        start = len(out)
        if own_lsps:
            out.random(rng, config.independent_lsp_bits)
        if slot < 18:
            for _ in range(slot // 3):
                out.write(3, 2)
            out.write(slot % 3, 2)
        else:
            for _ in range(6):
                out.write(3, 2)
            out.write(slot - 18, 2)

        record = {"type": type_}
        pitch = self.pitch
        frame_pitch = None
        if adaptive == 1:
            field = rng.getrandbits(config.pitch_bits) if config.pitch_bits else 0
            out.write(field, config.pitch_bits)
            frame_pitch = min(config.min_pitch + field, config.max_pitch - 1)
            if self.kind == 0 or 20 * abs(frame_pitch - pitch) > frame_pitch + pitch:
                pitch = frame_pitch
            record["pitch"] = frame_pitch
        if fixed == 0:
            gain = rng.getrandbits(8)
            out.write(gain, 8)
            record["parameter"] = gain

        window_index = None
        extended = False
        if fixed == 2:
            value = rng.getrandbits(6)
            window_index = value
            out.write(value, 6)
            if value >= 54:
                extra = rng.getrandbits(2)
                out.write(extra, 2)
                window_index = 54 + 4 * (value - 54) + extra
                extended = True
            record["window_index"] = window_index

        # The width of each block as it is read, for the record.
        widths = []
        block_value = 0
        last_pitch = None
        for n in range(blocks_count):
            block_start = len(out)
            if adaptive == 2:
                if n == 0:
                    block_value = rng.getrandbits(config.block_pitch_bits)
                    out.write(block_value, config.block_pitch_bits)
                else:
                    limited = min(max(block_value, config.half_step), config.block_pitch_range - config.half_step)
                    delta = rng.getrandbits(config.delta_bits)
                    out.write(delta, config.delta_bits)
                    block_value = limited - config.half_step + delta
                last_pitch = self.block_pitch(block_value)
            if fixed == 1:
                out.random(rng, 14)
            elif fixed == 2:
                lag = self.window_has_lag(window_index, frame_pitch, pitch, blocks_count)
                set_bits_ = 10 if extended and n == 0 else 12
                index_bits = (5 if n == 0 else 3) if lag else 4
                out.random(rng, set_bits_).random(rng, index_bits).random(rng, 1).random(rng, 7)
            elif fixed == 3:
                q = 5 - FRAME_DESCRIPTORS[type_][1]
                out.random(rng, 5 + (5 + pairs) * q)
                out.random(rng, 7)
            widths.append(len(out) - block_start)

        if adaptive == 1:
            self.pitch = frame_pitch
        elif adaptive == 2:
            self.pitch = last_pitch
            record["pitch"] = last_pitch
        else:
            self.pitch = 0
        self.kind = adaptive
        record["blocks"] = widths
        record["bits"] = len(out) - start
        return record

    def window_has_lag(self, index, c, pitch, blocks):
        """Whether the first block of a pitch-adaptive window frame has pulses at the pitch lag; pitch is the last pitch after the frame's update."""
        first = ((c) + (2 * blocks - 1) * pitch + blocks) // (2 * blocks)
        offset = START_OFFSETS[index]
        while offset < 0:
            offset += first
        return offset < FRAME_SAMPLES // 2

    def block_pitch(self, value):
        a0, a1, a2, a3 = self.config.anchors
        s1 = 4 * (a1 - a0)
        s2 = 2 * (a2 - a1)
        s3 = a3 - a2 + 1
        if value < s1:
            return a0 + value // 4
        value -= s1
        if value < s2:
            return a1 + value // 2
        value -= s2
        if value < s3:
            return a2 + value
        return a3
