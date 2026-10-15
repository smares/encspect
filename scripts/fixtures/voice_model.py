"""A writer of Windows Media Audio Voice streams for the fixtures, bit by bit, from FFmpeg 6.1.1's wmavoice.c (the only
description of the codec there is; Microsoft has published none). Standard library only.

It writes what a decoder reads: the 46 bytes of codec-specific data, superframes (a speech bit, an optional sample count,
residual LSPs, three frames, optional additional data) and packets (a sequence number, the residual LSPs flag, the count of
superframes, the spillover, the end of the superframe before and the superframes). The fields a decoder reads and does
not report (LSPs, pulse positions, gains) are random bits from a seeded generator, so a file decodes without error in FFmpeg
whatever it says. Only the widths matter to the analysis, and the writer works them out as the decoder does: the pitch of
a frame decides, for a pitch-adaptive window frame, how wide one field is.

The tables below are those of wmavoice.c; wma_tables.py --check fails when they differ from the source.
"""

import random
import struct

# wmavoice.c: frame_descs: blocks per frame, its base-2 logarithm, adaptive codebook (0 none, 1 asymmetric window with a pitch
# per frame, 2 Hamming window with a pitch per block), fixed codebook (0 silence, 1 hardcoded, 2 pitch-adaptive window
# pulses, 3 pulse sets) and the pulse vectors that hold a pair.
FRAME_DESCRIPTORS = [
    (1, 0, 0, 0, 0), (2, 1, 0, 1, 0), (2, 1, 1, 2, 0), (2, 1, 1, 3, 2), (2, 1, 1, 3, 5), (4, 2, 1, 3, 0), (4, 2, 1, 3, 2), (4, 2, 1, 3, 5),
    (2, 1, 2, 3, 0), (2, 1, 2, 3, 2), (2, 1, 2, 3, 5), (4, 2, 2, 3, 0), (4, 2, 2, 3, 2), (4, 2, 2, 3, 5), (8, 3, 2, 3, 0), (8, 3, 2, 3, 2), (8, 3, 2, 3, 5),
]

# wmavoice.c: wmavoice_init_static_data: the code lengths of the frame type VLC.
CODE_LENGTHS = [2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14, 14]

# wmavoice.c: aw_parse_coords: start_offset.
START_OFFSETS = [
    -11, -9, -7, -5, -3, -1, 1, 3, 5, 7, 9, 11, 13, 15, 18, 17, 19, 20, 21, 22, 23, 24, 25, 26,
    27, 28, 29, 30, 31, 32, 33, 35, 37, 39, 41, 43, 45, 47, 49, 51, 53, 55, 57, 59, 61, 63, 65, 67,
    69, 71, 73, 75, 77, 79, 81, 83, 85, 87, 89, 91, 93, 95, 97, 99, 101, 103, 105, 107, 109, 111, 113, 115,
    117, 119, 121, 123, 125, 127, 129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149, 151, 153, 155, 157, 159,
]

FRAME_SAMPLES = 160
SUPERFRAME_SAMPLES = 480

# The frame type table of a typical file: the groups of the 17 frame types (the 8 kHz file of the Windows Media Format SDK's
# writer), 3 bits each; a type in group g is sent with a code of 2 * (g + 1) bits (14 for groups 6 and 7).
DEFAULT_GROUPS = [1, 1, 0, 0, 0, 1, 2, 2, 4, 4, 5, 2, 3, 3, 5, 3, 4]


def ceil_log2(value):
    return 0 if value <= 1 else (value - 1).bit_length()


class BitWriter:
    """Bits, most significant first."""

    def __init__(self):
        self.bits = []

    def __len__(self):
        return len(self.bits)

    def write(self, value, count):
        for i in range(count - 1, -1, -1):
            self.bits.append((value >> i) & 1)
        return self

    def random(self, rng, count):
        return self.write(rng.getrandbits(count) if count else 0, count)

    def append(self, other, start=0, end=None):
        self.bits.extend(other.bits[start:end])
        return self

    def to_bytes(self, size=None):
        data = bytearray((len(self.bits) + 7) // 8)
        for i, bit in enumerate(self.bits):
            if bit:
                data[i >> 3] |= 0x80 >> (i & 7)
        if size is not None:
            if len(data) > size:
                raise ValueError(f"{len(self.bits)} bits do not fit {size} bytes")
            data += bytes(size - len(data))
        return bytes(data)


def set_bits(data, position, count, value):
    """The data with the count bits at the bit position set to value (most significant first)."""
    data = bytearray(data)
    for i in range(count):
        at = position + i
        mask = 0x80 >> (at & 7)
        if (value >> (count - 1 - i)) & 1:
            data[at >> 3] |= mask
        else:
            data[at >> 3] &= ~mask & 0xFF
    return bytes(data)


class Config:
    """The codec-specific data and what follows from it and the stream's rate and block alignment."""

    def __init__(self, rate, align, groups=None, postfilter=True, denoise=5, tilt=False, dc_level=3, lsp16=False, q_mode=0, default_mode=0, residual=True,
                 header=None, flags_override=None):
        self.rate = rate
        self.align = align
        self.groups = list(DEFAULT_GROUPS if groups is None else groups)
        self.flags = flags_override if flags_override is not None else (
            (1 if postfilter else 0) | (denoise << 2) | (0x40 if tilt else 0) | (dc_level << 7) | (0x1000 if lsp16 else 0) | (0x2000 if q_mode else 0) | (0x4000 if default_mode else 0))
        self.lsp16 = (self.flags & 0x1000) != 0
        self.residual = residual
        # Bytes 0 to 17 are laid out as Professional's: the sample size, the channel mask (front center), zeros.
        self.header = header if header is not None else struct.pack("<HI", 16, 4) + bytes(12)
        self.spillover_bits = 3 + ceil_log2(align)
        # wmavoice_decode_init
        self.min_pitch = (((rate << 8) // 400) + 50) >> 8
        self.max_pitch = ((((rate << 8) * 37) // 2000) + 50) >> 8
        pitch_range = self.max_pitch - self.min_pitch
        self.pitch_bits = ceil_log2(pitch_range)
        self.conversion = [self.min_pitch, (pitch_range * 25) >> 6, (pitch_range * 44) >> 6, self.max_pitch - 1]
        self.half_range = (pitch_range >> 3) & ~0xF
        self.delta_bits = 1 + ceil_log2(self.half_range)
        self.block_range = self.conversion[2] + self.conversion[3] + 1 + 2 * (self.conversion[1] - 2 * self.min_pitch)
        self.block_pitch_bits = ceil_log2(self.block_range)
        # decode_vbmtree
        self.tree = [-1] * 25
        counts = [0] * 8
        for frame_type, group in enumerate(self.groups):
            self.tree[group * 3 + counts[group]] = frame_type
            counts[group] += 1
        self.slot_of = {}
        for slot in range(len(CODE_LENGTHS)):
            if self.tree[slot] >= 0:
                self.slot_of[self.tree[slot]] = slot
        code = 0
        self.codes = []
        for length in CODE_LENGTHS:
            self.codes.append((code >> (32 - length), length))
            code += 1 << (32 - length)

    @property
    def lsp_count(self):
        return 16 if self.lsp16 else 10

    @property
    def independent_lsp_bits(self):
        return 34 if self.lsp16 else 24

    @property
    def residual_lsp_bits(self):
        return self.independent_lsp_bits + (26 if self.lsp16 else 24)

    def codec_data(self):
        table = BitWriter()
        for group in self.groups:
            table.write(group, 3)
        return self.header + struct.pack("<I", self.flags) + table.to_bytes(24)

    def wave_format(self, rate=None, align=None, extra=None, tag=0x000A, channels=1):
        """The stream's WAVEFORMATEX; rate, align and extra override what the configuration gives (for damaged files)."""
        from asf_model import wave_format
        rate = self.rate if rate is None else rate
        return wave_format(tag, channels, rate, rate // 8, self.align if align is None else align, 16, self.codec_data() if extra is None else extra)


class Writer:
    """Writes frames and superframes, keeping the pitch that carries from one frame to the next."""

    def __init__(self, config, seed=1):
        self.c = config
        self.rng = random.Random(seed)
        self.last_pitch = 40
        self.last_acb = 0

    def frame(self, frame_type, residual=None):
        """One frame as the decoder reads it. Returns its bits and what the analysis reports of it."""
        c = self.c
        residual = c.residual if residual is None else residual
        rng = self.rng
        w = BitWriter()
        if not residual:
            w.random(rng, c.independent_lsp_bits)
        slot = c.slot_of[frame_type]
        w.write(*c.codes[slot])
        blocks, log_blocks, acb, fcb, pairs = FRAME_DESCRIPTORS[frame_type]
        pitch = [0] * 8
        current = 0
        info = {"type": frame_type, "blocks": blocks}
        if acb == 1:
            code = rng.getrandbits(c.pitch_bits)
            w.write(code, c.pitch_bits)
            current = min(c.min_pitch + code, c.max_pitch - 1)
            if self.last_acb == 0 or 20 * abs(current - self.last_pitch) > current + self.last_pitch:
                self.last_pitch = current
            for n in range(blocks):
                factor = n * 2 + 1
                pitch[n] = (factor * current + (2 * blocks - factor) * self.last_pitch + blocks) >> (log_blocks + 1)
            info["pitch"] = current
        extended = False
        windows = 0
        if fcb == 0:
            info["parameter"] = rng.getrandbits(8)
            w.write(info["parameter"], 8)
        elif fcb == 2:
            index = rng.getrandbits(6)
            w.write(index, 6)
            if index >= 54:
                extended = True
                low = rng.getrandbits(2)
                w.write(low, 2)
                index += (index - 54) * 3 + low
            offset = START_OFFSETS[index]
            while offset < 0:
                offset += pitch[0]
            # C's division truncates toward zero.
            numerator = pitch[0] - 1 + FRAME_SAMPLES // 2 - offset
            windows = int(numerator / pitch[0])
            info["window_index"] = index
        last_block = 0
        for n in range(blocks):
            if acb == 2:
                if n == 0:
                    block_pitch = rng.getrandbits(c.block_pitch_bits)
                    w.write(block_pitch, c.block_pitch_bits)
                else:
                    delta = rng.getrandbits(c.delta_bits)
                    w.write(delta, c.delta_bits)
                    block_pitch = last_block - c.half_range + delta
                low, high = c.half_range, c.block_range - c.half_range
                last_block = low if block_pitch < low else high if block_pitch > high else block_pitch
                t1 = (c.conversion[1] - c.conversion[0]) << 2
                t2 = (c.conversion[2] - c.conversion[1]) << 1
                t3 = c.conversion[3] - c.conversion[2] + 1
                if block_pitch < t1:
                    doubled = (c.conversion[0] << 2) + block_pitch
                else:
                    block_pitch -= t1
                    if block_pitch < t2:
                        doubled = (c.conversion[1] << 2) + (block_pitch << 1)
                    else:
                        block_pitch -= t2
                        doubled = ((c.conversion[2] + block_pitch) << 2) if block_pitch < t3 else (c.conversion[3] << 2)
                pitch[n] = doubled >> 2
            if acb == 0:
                if fcb == 1:
                    w.random(rng, 8 + 6)
                continue
            if fcb == 2:
                w.random(rng, 12 - (2 if extended and n == 0 else 0))
                w.random(rng, (5 - 2 * n) if windows > 0 else 4)
                w.random(rng, 1)
            else:
                position_bits = 5 - log_blocks
                w.random(rng, 5 + 5 * position_bits + pairs * position_bits)
            w.random(rng, 7)
        self.last_acb = acb
        self.last_pitch = 0 if acb == 0 else current if acb == 1 else pitch[blocks - 1]
        if acb == 2:
            info["pitch"] = pitch[blocks - 1]
        info["bits"] = len(w)
        return w, info

    def superframe(self, types, samples=None, additional=0, residual=None, speech=True):
        """A superframe of three frames of the given types. samples is the sample count it states (None: not stated), additional
        the groups of 10 bits of additional data (0: none)."""
        c = self.c
        residual = c.residual if residual is None else residual
        w = BitWriter()
        w.write(1 if speech else 0, 1)
        if samples is None:
            w.write(0, 1)
        else:
            w.write(1, 1).write(samples, 12)
        if residual:
            w.random(self.rng, c.residual_lsp_bits)
        frames = []
        for frame_type in types:
            bits, info = self.frame(frame_type, residual)
            w.append(bits)
            frames.append(info)
        if additional:
            w.write(1, 1).write(additional - 1, 4).random(self.rng, 10 * additional)
        else:
            w.write(0, 1)
        return w, {"types": list(types), "frames": frames, "samples": SUPERFRAME_SAMPLES if samples is None else samples, "bits": len(w), "additional": additional}
