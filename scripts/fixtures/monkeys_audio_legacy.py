"""Follows the Monkey's Audio SDK 13.44 (BSD 3-clause license, Copyright 2000-2026 Matthew T. Ashland; THIRD-PARTY-NOTICES.md).

An encoder for the coding of Monkey's Audio files from version 3.93 to 3.98, which the SDK 13.44 can only decode (it writes the current format), written
as the inverse of what its decoder does (NewPredictor.cpp, Old/NewPredictorOld.cpp, NNFilterGeneric.cpp, Old/UnBitArrayOld.cpp, CUnBitArray3891To3989) and of
the encoder of the current format (BitArray.cpp, Prepare.cpp) for everything the versions share.

The files it writes are held to the SDK, not to this script: `mac -V` decodes each of them with the SDK's own decoder of the version and compares every frame
CRC, so a file that verifies is valid by that decoder's reading, and `mac -d` gives the audio the analyzer's decoder must reproduce. Real files of these
versions are rare; the encoder makes the combinations of version, level, sample size and special frames that no public file has.

What a version changes (SDK 13.44):
  - 3.93 and 3.94: one adaptive filter of four taps over the last values, and neural network filters that update their deltas the old way; the two
    channels of a pair are predicted on their own, the first one's value coded first (APEDecompressCore.cpp, DecodeBlocksToFrameBuffer).
  - 3.95 to 3.97: the predictors of the current format with the old delta; the stereo pair is predicted as in the current format.
  - 3.98: the new delta, and the descriptor and header of the current format.
  - all: the range coder with the model of RANGE_TOTAL_1 and a value coded as a symbol and the bits of a width that follows the average.
"""

import struct
import zlib

import monkeys_audio_model as m

TOP = 1 << 31
BOTTOM = TOP >> 8
SHIFT_BITS = 23

RANGE_TOTAL = [0, 14824, 28224, 39348, 47855, 53994, 58171, 60926, 62682, 63786, 64463, 64878, 65126, 65276, 65365, 65419, 65450, 65469, 65480, 65487, 65491, 65493,
               65494, 65495] + list(range(65496, 65537))
RANGE_WIDTH = [14824, 13400, 11124, 8507, 6139, 4177, 2755, 1756, 1104, 677, 415, 248, 150, 89, 54, 31, 19, 11, 7, 4, 2] + [1] * 43
K_SUM_MIN = [0, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 8388608, 16777216, 33554432,
             67108864, 134217728, 268435456, 536870912, 1073741824, 2147483648, 0, 0, 0, 0]

FILTERS = {1000: [], 2000: [(16, 11)], 3000: [(64, 11)], 4000: [(256, 13), (32, 10)], 5000: [(1024 + 256, 15), (256, 13), (16, 11)]}


def w32(value):
    value &= 0xFFFFFFFF
    return value - (1 << 32) if value & 0x80000000 else value


def sat16(value):
    return value if -32768 <= value <= 32767 else (32767 if value > 0 else -32768)


def truncate_div(value, divisor):
    return value // divisor if value >= 0 else -((-value) // divisor)


class Filter:
    """A neural network filter in the direction of the encoder (CNNFilter::CompressGeneric)."""

    def __init__(self, order, shift, is_new_delta):
        self.order = order
        self.shift = shift
        self.round = 1 << (shift - 1)
        self.is_new_delta = is_new_delta
        self.flush()

    def flush(self):
        self.weights = [0] * self.order
        self.history = [0] * (self.order + 16)
        self.deltas = [0] * (self.order + 16)
        self.average = 0

    def compress(self, value):
        n = self.order
        dot = w32(sum(map(int.__mul__, self.history[-n:], self.weights)))
        out = w32(value - ((dot + self.round) >> self.shift))
        window = self.deltas[-n:]
        if out < 0:
            self.weights = [a + b for a, b in zip(self.weights, window)]
        elif out > 0:
            self.weights = [a - b for a, b in zip(self.weights, window)]
        d = self.deltas
        if self.is_new_delta:
            magnitude = abs(value)
            if magnitude > self.average * 3:
                d.append(((value >> 25) & 64) - 32)
            elif magnitude > truncate_div(self.average * 4, 3):
                d.append(((value >> 26) & 32) - 16)
            elif magnitude > 0:
                d.append(((value >> 27) & 16) - 8)
            else:
                d.append(0)
            self.average += truncate_div(magnitude - self.average, 16)
            d[-2] >>= 1
            d[-3] >>= 1
            d[-9] >>= 1
        else:
            d.append(0 if value == 0 else ((value >> 28) & 8) - 4)
            d[-5] >>= 1
            d[-9] >>= 1
        self.history.append(sat16(value))
        return out


def chain(filters, value):
    for f in filters:
        value = f.compress(value)
    return value


def sign(value):
    return (value < 0) - (value > 0)


class Predictor3950:
    """CPredictorDecompress3950toCurrent, run backwards: from the value to the residual the range coder stores."""

    def __init__(self, level, is_new_delta, bits):
        self.filters = [Filter(o, s, is_new_delta) for o, s in FILTERS[level]]
        self.bits = bits
        self.flush()

    def flush(self):
        for f in self.filters:
            f.flush()
        self.ma = [360, 317, -109, 98]
        self.mb = [0] * 5
        self.pa = [0] * 9
        self.pb = [0] * 9
        self.aa = [0] * 9
        self.ab = [0] * 9
        self.last_a = 0
        self.stage_a = 0
        self.stage_b = 0

    def compress(self, target, b):
        pa, pb, aa, ab = self.pa, self.pb, self.aa, self.ab
        pa.append(self.last_a)
        pa[-2] = pa[-1] - pa[-2]
        pb.append(w32(b - ((self.stage_b * 31) >> 5)))
        self.stage_b = b
        pb[-2] = pb[-1] - pb[-2]
        ma, mb = self.ma, self.mb
        prediction_a = w32(pa[-1] * ma[0] + pa[-2] * ma[1] + pa[-3] * ma[2] + pa[-4] * ma[3])
        prediction_b = w32(pb[-1] * mb[0] + pb[-2] * mb[1] + pb[-3] * mb[2] + pb[-4] * mb[3] + pb[-5] * mb[4])
        current = target - ((self.stage_a * 31) >> 5)
        a = w32(current - (w32(prediction_a + (prediction_b >> 1)) >> 10))
        aa.append(((pa[-1] >> 30) & 2) - 1 if pa[-1] else 0)
        aa[-2] = ((pa[-2] >> 30) & 2) - 1 if pa[-2] else 0
        ab.append(((pb[-1] >> 30) & 2) - 1 if pb[-1] else 0)
        ab[-2] = ((pb[-2] >> 30) & 2) - 1 if pb[-2] else 0
        direction = sign(a)
        for i in range(4):
            ma[i] += aa[-1 - i] * direction
        for i in range(5):
            mb[i] += ab[-1 - i] * direction
        self.stage_a = target
        self.last_a = current
        return chain(self.filters, a)


class Predictor3930:
    """CPredictorDecompress3930to3950, run backwards."""

    def __init__(self, level):
        if level > 4000:
            raise ValueError("no insane level before 3.95")
        self.filters = [Filter(o, s, False) for o, s in FILTERS[level]]
        self.flush()

    def flush(self):
        for f in self.filters:
            f.flush()
        self.m = [360, 317, -109, 98]
        self.buffer = [0] * 9
        self.last = 0

    def compress(self, target, b=0):
        buf, m = self.buffer, self.m
        p1 = buf[-1]
        p2 = buf[-1] - buf[-2]
        p3 = buf[-2] - buf[-3]
        p4 = buf[-3] - buf[-4]
        stage = w32(target - ((self.last * 31) >> 5))
        self.last = target
        value = w32(stage - (w32(p1 * m[0] + p2 * m[1] + p3 * m[2] + p4 * m[3]) >> 9))
        buf.append(stage)
        direction = sign(value)
        for i, p in enumerate((p1, p2, p3, p4)):
            m[i] += (((p >> 30) & 2) - 1) * direction
        return chain(self.filters, value)


class RangeEncoder:
    """CBitArray's range coder (BitArray.cpp), writing the stream bytes of a frame."""

    def __init__(self):
        self.out = bytearray()
        self.low = 0
        self.range = TOP
        self.buffer = 0
        self.help = 0

    def put32(self, value):
        self.out += struct.pack(">I", value & 0xFFFFFFFF)

    def _normalize(self):
        while self.range <= BOTTOM:
            if self.low < (0xFF << SHIFT_BITS):
                self.out.append(self.buffer & 0xFF)
                self.out += b"\xff" * self.help
                self.help = 0
                self.buffer = (self.low >> SHIFT_BITS) & 0xFF
            elif self.low & TOP:
                self.out.append((self.buffer + 1) & 0xFF)
                self.out += b"\0" * self.help
                self.help = 0
                self.buffer = (self.low >> SHIFT_BITS) & 0xFF
            else:
                self.help += 1
            self.low = (self.low << 8) & (TOP - 1)
            self.range = (self.range << 8) & 0xFFFFFFFF

    def fast(self, width, total, shift):
        self._normalize()
        t = self.range >> shift
        self.range = t * width
        self.low = (self.low + t * total) & 0xFFFFFFFF

    def direct(self, value, shift):
        self._normalize()
        self.range >>= shift
        self.low = (self.low + self.range * value) & 0xFFFFFFFF

    def finish(self):
        self._normalize()
        temp = (self.low >> SHIFT_BITS) + 1
        if temp > 0xFF:
            self.out.append((self.buffer + 1) & 0xFF)
            self.out += b"\0" * self.help
        else:
            self.out.append(self.buffer & 0xFF)
            self.out += b"\xff" * self.help
        self.out += bytes([temp & 0xFF, 0, 0, 0])
        return bytes(self.out)


class Channel:
    """The state that follows the size of the values of a channel through a frame (UNBIT_ARRAY_STATE)."""

    def __init__(self):
        self.k = 10
        self.ksum = (1 << 10) * 16


def encode_value(rc, state, residual, version):
    """CUnBitArray3891To3989::DecodeValueRange, backwards."""
    value = residual * 2 - 1 if residual > 0 else -residual * 2
    width = state.k - 1 if state.k >= 1 else 0
    symbol = value >> width
    if symbol < 63:
        rc.fast(RANGE_WIDTH[symbol], RANGE_TOTAL[symbol], 16)
        bits, count = value & ((1 << width) - 1), width
    else:
        rc.fast(RANGE_WIDTH[63], RANGE_TOTAL[63], 16)
        count = value.bit_length()
        rc.direct(count, 5)
        bits = value
    if count <= 16 or version < 3910:
        rc.direct(bits, count)
    else:
        rc.direct(bits & 0xFFFF, 16)
        rc.direct(bits >> 16, count - 16)
    state.ksum = (state.ksum + ((value + 1) // 2) - ((state.ksum + 16) >> 5)) & 0xFFFFFFFF
    if state.ksum < K_SUM_MIN[state.k]:
        state.k -= 1
    elif K_SUM_MIN[state.k + 1] and state.ksum >= K_SUM_MIN[state.k + 1]:
        state.k += 1


def prepare(samples, channels, has_special):
    """Prepare.cpp: the values the predictors see, and the special codes. samples are the frames' channel values (the first of a pair is what the SDK
    calls R)."""
    if channels == 2:
        y = [b - a for a, b in samples]
        x = [a + truncate_div(d, 2) for (a, _), d in zip(samples, y)]
        special = 0
        if has_special:
            if all(b == 0 for _, b in samples):
                special |= 1
            if all(a == 0 for a, _ in samples):
                special |= 2
            if all(d == 0 for d in y):
                special |= 4
        return [x, y], special
    values = [[s[c] for s in samples] for c in range(channels)]
    return values, (1 if has_special and channels == 1 and all(v == 0 for v in values[0]) else 0)


def encode_frame(samples, pcm, channels, bits, level, version, predictors):
    """The stream bytes of a frame: its CRC, its special codes where it has them, and the range-coded values. pcm is the frame's audio in WAV layout."""
    values, special = prepare(samples, channels, bits == 16)
    crc = zlib.crc32(pcm) >> 1
    rc = RangeEncoder()
    rc.put32(crc | (0x80000000 if special else 0))
    if special:
        rc.put32(special)
    for p in predictors:
        p.flush()
    states = [Channel() for _ in range(channels)]
    is_silent = (special & 3) == 3 if channels == 2 else bool(special & 1) if channels == 1 else False
    if not is_silent:
        n = len(samples)
        if channels == 2 and (special & 4) == 0 and version >= 3950:
            last_x = 0
            for i in range(n):
                x, y = values[0][i], values[1][i]
                ry = predictors[1].compress(y, last_x)
                rx = predictors[0].compress(x, y)
                last_x = x
                encode_value(rc, states[1], ry, version)
                encode_value(rc, states[0], rx, version)
        elif channels == 2 and (special & 4) == 0:
            for i in range(n):
                encode_value(rc, states[0], predictors[0].compress(values[0][i]), version)
                encode_value(rc, states[1], predictors[1].compress(values[1][i]), version)
        elif channels == 2:
            for i in range(n):
                encode_value(rc, states[0], predictors[0].compress(values[0][i], 0) if version >= 3950 else predictors[0].compress(values[0][i]), version)
        else:
            for i in range(n):
                for c in range(channels):
                    encode_value(rc, states[c], predictors[c].compress(values[c][i], 0), version)
    return rc.finish()


def make_predictors(version, level, channels, bits):
    if version >= 3950:
        return [Predictor3950(level, version >= 3980, bits) for _ in range(channels)]
    return [Predictor3930(level) for _ in range(channels)]


def blocks_per_frame(version, level):
    return m.old_blocks_per_frame(version, level)


def encode(samples, version, level, channels, bits, rate, pcm, flags=0, peak=None, header_data=b"", terminating=b""):
    """A file of the given version from samples (a list of frames of channel values) and their WAV-layout bytes. Files before 3.98 have the old header;
    3.98 has the descriptor. Returns (file bytes, frames as stream bytes, blocks of each frame)."""
    per_frame = blocks_per_frame(version, level)
    width = channels * bits // 8
    predictors = make_predictors(version, level, channels, bits)
    frames = []
    counts = []
    for start in range(0, len(samples), per_frame):
        chunk = samples[start:start + per_frame]
        frames.append(encode_frame(chunk, pcm[start * width:(start + len(chunk)) * width], channels, bits, level, version, predictors))
        counts.append(len(chunk))
    final = counts[-1]
    flags |= 2
    if bits == 8:
        flags |= 1
    elif bits == 24:
        flags |= 8
    if version >= 3980:
        data = m.build(frames, level, bits, channels, rate, per_frame, final, header_data=header_data, terminating=terminating, version=version, program=0,
                       flags=flags)
    else:
        data = m.build_old(frames, version, level, flags | (4 if peak is not None else 0), channels, rate, final, header_data=header_data, terminating=terminating, peak=peak)
    return data, frames, counts
