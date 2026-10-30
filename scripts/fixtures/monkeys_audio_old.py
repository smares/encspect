"""Follows the Monkey's Audio SDK 13.44 (BSD 3-clause license, Copyright 2000-2026 Matthew T. Ashland; THIRD-PARTY-NOTICES.md).

An encoder for the coding of Monkey's Audio files from version 3.80 to 3.92, which the SDK 13.44 can only decode (through Old/UnMACOld.cpp; it writes the
current format), written as the inverse of what that decoder does (Old/UnMACOld.cpp, Old/APEDecompressCoreOld.cpp, Old/UnBitArrayOld.cpp,
Old/AntiPredictorFastOld.cpp, Old/AntiPredictorNormalOld.cpp, Old/AntiPredictorHighOld.cpp, Old/AntiPredictorExtraHighOld.cpp, UnBitArrayBase.cpp). The coding of
3.93 and later is monkeys_audio_legacy.py's.

The files it writes are held to the SDK, not to this script: `mac -V` decodes each of them with the SDK's own decoder of the version and compares every frame CRC
or checksum, so a file that verifies is valid by that decoder's reading, and `mac -d` gives the audio the analyzer's decoder must reproduce. Real files of these
versions are rare; the encoder makes the combinations of version, level, sample size and special frames that no public file has.

What the versions change (SDK 13.44):
  - Bit coding. Up to 3.85 (GenerateArrayOld): the first five values of a channel's array coded with Rice parameter 10, the next 59 with a parameter that follows the
    mean so far, the rest with one that follows a sum over the last 64 values. 3.86 to 3.89 (GenerateArrayRice): one parameter that follows a running sum, above version
    3880 with the overflow capped at 16 (each 16 more add 4 to the parameter). 3.90 to 3.92: the range coder of 3.93 and later, an array at a time.
  - Frames. Up to 3.80 frames follow each other bit by bit and the file holds the bit where each starts (a table of bytes beside the table of byte offsets); from 3.81
    a frame starts on a byte boundary. The channel's whole array is coded, then the next channel's (the mid channel X, then the side channel Y).
  - The word in front of a frame. With the CRC flag: the CRC of the audio in all 32 bits up to 3.82; from 3.83 31 bits, the top bit flagging 32 bits of special
    codes (silent channels, pseudo stereo). Without the flag: the sum of |left| + |right| (|X| for mono) over the frame, Rice-coded with parameter 30; 0 means silence.
  - 8-bit audio. Up to 3.83 the predictors see the unsigned bytes; from 3.84 the signed values (the byte minus 128).
  - Predictors (all levels but insane, which these versions do not have). Fast: two stages of order 2 and 1 over the values (AntiPredictorFastOld, from 3.32); normal:
    three adaptive stages over the last values, then a stage on the output (3800To3929); high: a 16-tap filter in front of that; extra high: a filter in front of that
    of 128 taps up to 3.82 and 256 taps from 3.83, and from 3.83 a filter of 8 taps in front of that.
"""

import struct
import zlib

import monkeys_audio_legacy as legacy
import monkeys_audio_model as m

M32 = 0xFFFFFFFF
LEVELS = (1000, 2000, 3000, 4000)

K_SUM_MIN = legacy.K_SUM_MIN
K_SUM_MIN_OLD = [0] + [1 << p for p in range(7, 32)] + [0] * 6
K_SUM_MAX_OLD = K_SUM_MIN_OLD[1:] + [0]


def k_sum_max(k):
    return K_SUM_MIN[k + 1] if k + 1 < len(K_SUM_MIN) else 0


def w32(value):
    value &= M32
    return value - (1 << 32) if value & 0x80000000 else value


def w16(value):
    return ((value + 32768) & 0xFFFF) - 32768


def truncate_div(value, divisor):
    return value // divisor if value >= 0 else -((-value) // divisor)


def sign_factor(value):
    """((value >> 30) & 2) - 1: +1 for a negative value, -1 otherwise."""
    return ((value >> 30) & 2) - 1


# ---------------------------------------------------------------------------------------------------------------------------------------------------------------
# Predictors, in the direction of the encoder: from the values the decoder gives back to the residuals it reads.
# ---------------------------------------------------------------------------------------------------------------------------------------------------------------

def fast(out):
    """CAntiPredictorFast3320To3930, backwards."""
    n = len(out)
    if n < 3:
        return list(out)
    res = [out[0], out[1]]
    mm = 375
    ip2, ip3, op1 = out[1], out[0], out[1]
    for q in range(2, n):
        p = w32(ip2 + ip2 - ip3)
        t = w32(out[q] - op1)
        r = w32(t - (w32(p * mm) >> 9))
        mm += 1 if (r ^ p) > 0 else -1
        ip3, ip2, op1 = ip2, t, out[q]
        res.append(r)
    return res


class ThreeStages:
    """The three stages every predictor from 3.80 ends with (the end of CAntiPredictorNormal3800To3929::AntiPredict, whose variables the others share)."""

    def __init__(self, raw, first, shift_c):
        self.m2, self.m3, self.m4, self.m5, self.m6 = 64, 115, 64, 740, 0
        self.p4 = raw[first - 1]
        self.p3 = w32((raw[first - 1] - raw[first - 2]) << 1)
        self.p2 = w32(raw[first - 1] + ((raw[first - 3] - raw[first - 2]) << 3))
        self.ipp2 = raw[first - 2]
        self.p7 = w32(2 * raw[first - 1] - raw[first - 2])
        self.opp = raw[first - 1]
        self.shift_c = shift_c

    def back(self, previous_out, target):
        """The value the first stage's input must have for the decoder to give target after previous_out (the stages run in reverse), and the update of the state
        once the residual `i` that stage reads is known: returns (i, finish) where finish(i) must be called with it."""
        o2 = w32(target - ((previous_out * 31) >> 5))
        g = w32(w32(self.p7 * self.m5) - w32(self.opp * self.m6)) >> self.shift_c
        o1 = w32(o2 - g)
        f = w32(self.p2 * self.m2 + self.p3 * self.m3 + self.p4 * self.m4) >> 11
        return w32(o1 - f), o1, o2

    def update(self, i, o1, o2):
        if i > 0:
            self.m2 -= sign_factor(self.p2)
            self.m3 -= ((self.p3 >> 28) & 8) - 4
            self.m4 -= ((self.p4 >> 28) & 8) - 4
        elif i < 0:
            self.m2 += sign_factor(self.p2)
            self.m3 += ((self.p3 >> 28) & 8) - 4
            self.m4 += ((self.p4 >> 28) & 8) - 4
        self.p2 = w32(o1 + ((self.ipp2 - self.p4) << 3))
        self.p3 = w32((o1 - self.p4) << 1)
        self.ipp2 = self.p4
        self.p4 = o1
        if self.p4 > 0:
            self.m5 -= ((self.p7 >> 29) & 4) - 2
            self.m6 += ((self.opp >> 30) & 2) - 1
        elif self.p4 < 0:
            self.m5 += ((self.p7 >> 29) & 4) - 2
            self.m6 -= ((self.opp >> 30) & 2) - 1
        self.p7 = w32(2 * o2 - self.opp)
        self.opp = o2


def initial(out, first):
    """The first values of an array, which the decoder copies and then sums (the 'undo the initial prediction stuff' loop): the residuals they come from."""
    return [out[0]] + [w32(out[q] - out[q - 1]) for q in range(1, first)]


def normal(out):
    """CAntiPredictorNormal3800To3929, backwards."""
    n = len(out)
    first = 4
    if n < 8:
        return list(out)
    res = initial(out, first)
    stages = ThreeStages(res, first, 10)
    for q in range(first, n):
        i, o1, o2 = stages.back(out[q - 1], out[q])
        stages.update(i, o1, o2)
        res.append(i)
    return res


def high(out):
    """CAntiPredictorHigh3800To3929, backwards."""
    n = len(out)
    first = 16
    if n < 20:
        return list(out)
    res = initial(out, first)
    stages = ThreeStages(res, first, 10)
    bm = [0] * first
    history = list(res)
    for q in range(first, n):
        a, o1, o2 = stages.back(out[q - 1], out[q])
        window = history[q - first:q]
        dot = w32(sum(p * b for p, b in zip(window, bm)))
        r = w32(a + (dot >> 9))
        if r > 0:
            bm = [w32(b + sign_factor(p)) for p, b in zip(window, bm)]
        elif r < 0:
            bm = [w32(b - sign_factor(p)) for p, b in zip(window, bm)]
        stages.update(a, o1, o2)
        history.append(a)
        res.append(r)
    return res


def extra_high(out, version):
    """CAntiPredictorExtraHigh3800To3929, backwards."""
    n = len(out)
    old = version < 3830
    taps = 128 if old else 256
    shift = 11 if old else 12
    first = taps
    if n < (134 if old else 262):
        return list(out)
    res = initial(out, first)
    stages = ThreeStages(res, first, 10 if old else 11)
    bm = [0] * taps
    shorts = [w16(v) for v in res]
    adapt = [sign_factor(v) for v in res]
    fm = [0] * 9
    fp = [0] * 9
    for q in range(first, n):
        a2, o1, o2 = stages.back(out[q - 1], out[q])
        window = shorts[q - first:q]
        dot = w32(sum(p * b for p, b in zip(window, bm)))
        a1 = w32(a2 + (dot >> shift))
        if a1 > 0:
            bm = [w16(b + s) for b, s in zip(bm, adapt[q - first:q])]
        elif a1 < 0:
            bm = [w16(b - s) for b, s in zip(bm, adapt[q - first:q])]
        shorts.append(w16(a2))
        adapt.append(sign_factor(a2))
        stages.update(a2, o1, o2)
        if old:
            res.append(a1)
            continue
        dot8 = w32(sum(fp[j] * fm[j] for j in range(1, 9)))
        r0 = w32(a1 + (dot8 >> 9))
        if r0 > 0:
            for j in range(1, 9):
                fm[j] = w32(fm[j] + sign_factor(fp[j]))
        elif r0 < 0:
            for j in range(1, 9):
                fm[j] = w32(fm[j] - sign_factor(fp[j]))
        fp[1:] = [r0] + fp[1:8]
        res.append(r0)
    return res


def predict(level, version, out):
    if level == 1000:
        return fast(out)
    if level == 2000:
        return normal(out)
    if level == 3000:
        return high(out)
    if level == 4000:
        return extra_high(out, version)
    raise ValueError("no insane level before 3.93")


# ---------------------------------------------------------------------------------------------------------------------------------------------------------------
# Bit coding.
# ---------------------------------------------------------------------------------------------------------------------------------------------------------------

class BitWriter:
    """Bits most significant first, as the decoder's bit array reads the stream bytes."""

    def __init__(self):
        self.out = bytearray()
        self.acc = 0
        self.count = 0

    def put(self, value, bits):
        while bits > 24:
            bits -= 24
            self.put((value >> bits) & 0xFFFFFF, 24)
        if bits == 0:
            return
        self.acc = (self.acc << bits) | (value & ((1 << bits) - 1))
        self.count += bits
        while self.count >= 8:
            self.count -= 8
            self.out.append((self.acc >> self.count) & 0xFF)
        self.acc &= (1 << self.count) - 1

    def unary(self, zeros):
        """zeros zero bits and a one."""
        while zeros > 24:
            self.put(0, 24)
            zeros -= 24
        self.put(1, zeros + 1)

    def align(self):
        if self.count:
            self.put(0, 8 - self.count)

    def bits(self):
        return len(self.out) * 8 + self.count

    def extend(self, data):
        """Appends whole bytes at a byte boundary."""
        assert self.count == 0
        self.out += data


def unsigned(value):
    return 2 * value - 1 if value > 0 else -2 * value


def put_rice(w, value, k):
    w.unary(value >> k)
    if k:
        w.put(value, k)


def get_k(x):
    return x.bit_length()


def encode_array_up_to_385(w, values):
    """CUnBitArrayOld::GenerateArrayOld, backwards."""
    n = len(values)
    u = [unsigned(v) for v in values]
    for q in range(min(n, 5)):
        put_rice(w, u[q], 10)
    if n <= 5:
        return
    ksum = sum(u[:5]) & M32
    k = get_k(ksum // 10)
    for q in range(5, min(n, 64)):
        put_rice(w, u[q], k)
        ksum = (ksum + u[q]) & M32
        k = get_k(ksum // (q + 1) // 2)
    if n <= 64:
        return
    k = get_k(ksum >> 7)
    kmin, kmax = K_SUM_MIN_OLD[k], K_SUM_MAX_OLD[k]
    for q in range(64, n):
        put_rice(w, u[q], k)
        ksum = (ksum + u[q] - u[q - 64]) & M32
        if ksum < kmin or ksum >= kmax:
            if ksum < kmin:
                k -= 1
                while ksum < K_SUM_MIN_OLD[k]:
                    k -= 1
            else:
                while K_SUM_MAX_OLD[k + 1]:
                    k += 1
                    if ksum < K_SUM_MAX_OLD[k]:
                        break
            kmax, kmin = K_SUM_MAX_OLD[k], K_SUM_MIN_OLD[k]


def encode_array_rice(w, values, is_capped, forced=None):
    """CUnBitArrayOld::GenerateArrayRice with DecodeValueNew, backwards. forced maps the index of a value to a number of steps of 4 that the parameter is raised by
    although the value is small enough for the parameter it has: the overflow is then exactly 16 times the steps (16, 32), which this encoder otherwise never writes
    (its overflow is the quotient of 16 or more that is left plus 16 times the steps), and which the decoder reads as the steps and a zero overflow."""
    k = 10
    ksum = 1024 * 16
    for index, value in enumerate(values):
        u = unsigned(value)
        if is_capped:
            extra = 0
            if forced and index in forced:
                extra = forced[index]
                assert u >> (k + 4 * extra) == 0 and k + 4 * extra <= 31, "the value does not fit the parameter forced"
            else:
                while (u >> (k + 4 * extra)) >= 16 and k + 4 * extra < 31:
                    extra += 1
            k += 4 * extra
            w.unary((u >> k) + 16 * extra)
        else:
            w.unary(u >> k)
        if k:
            w.put(u, k)
        ksum = (ksum + u - ((ksum + 8) >> 4)) & M32
        if k > 31:
            k = 31
        if ksum < K_SUM_MIN[k]:
            k -= 1
        elif k_sum_max(k) and ksum >= k_sum_max(k):
            k += 1


class CountingRange(legacy.RangeEncoder):
    """The range coder of one array of 3.90 to 3.92 (BitArray.cpp), counting the bytes its normalization shifts out: the decoder has read FIRST_READ_BYTES more
    than that when it has decoded the array."""

    def __init__(self):
        super().__init__()
        self.shifts = 0

    def _normalize(self):
        while self.range <= legacy.BOTTOM:
            self.shifts += 1
            if self.low < (0xFF << legacy.SHIFT_BITS):
                self.out.append(self.buffer & 0xFF)
                self.out += b"\xff" * self.help
                self.help = 0
                self.buffer = (self.low >> legacy.SHIFT_BITS) & 0xFF
            elif self.low & legacy.TOP:
                self.out.append((self.buffer + 1) & 0xFF)
                self.out += b"\0" * self.help
                self.help = 0
                self.buffer = (self.low >> legacy.SHIFT_BITS) & 0xFF
            else:
                self.help += 1
            self.low = (self.low << 8) & (legacy.TOP - 1)
            self.range = (self.range << 8) & M32


# Where the decoder of 3.90 to 3.92 leaves its position when it has decoded an array (CUnBitArray3891To3989, FlushBitArray, GenerateArrayRange, Finalize), which is
# where the next array or frame starts: it starts with a range of 2^7 where the encoder has 2^31, so its first normalization reads three bytes and it has read five
# more than the encoder has shifted out by the time it has the first value, one more for each shift after that; Finalize then steps over one byte for each shift
# the range still needs to be above BOTTOM_VALUE, and back over two. (Checked with the SDK: the files that place the next array here verify with `mac -V`.)
FIRST_READ_BYTES = 5
FINALIZE_BACK_BYTES = 2


def encode_array_range(values, version):
    """(the bytes of one array of 3.90 to 3.92, ending where the decoder's position will be after it) for the values of a channel."""
    rc = CountingRange()
    state = legacy.Channel()
    for value in values:
        legacy.encode_value(rc, state, value, version)
    r, extra = rc.range, 0
    while r <= legacy.BOTTOM:
        extra += 1
        r = (r << 8) & M32
        if r == 0:
            raise ValueError("the range ran out")
    end = FIRST_READ_BYTES + rc.shifts + extra - FINALIZE_BACK_BYTES
    return rc.finish()[:end]


# ---------------------------------------------------------------------------------------------------------------------------------------------------------------
# Frames and files.
# ---------------------------------------------------------------------------------------------------------------------------------------------------------------

def prepare(samples, channels, bits, version):
    """Prepare.cpp as the old decoder undoes it (Old/UnMACOld.cpp, UnprepareMono, UnprepareStereo): the arrays X (and Y) of a frame. Up to 3.83 the 8-bit
    samples are unsigned bytes."""
    offset = 128 if bits == 8 and version <= 3830 else 0
    if channels == 2:
        y = [(b + offset) - (a + offset) for a, b in samples]
        x = [a + offset + truncate_div(d, 2) for (a, _), d in zip(samples, y)]
        return [x, y]
    return [[s[0] + offset for s in samples]]


def old_checksum(arrays):
    """CUnMACOld::CalculateOldChecksum."""
    if len(arrays) == 2:
        x, y = arrays
        total = 0
        for xv, yv in zip(x, y):
            r = xv - truncate_div(yv, 2)
            total += abs(r) + abs(r + yv)
        return total & M32
    return sum(abs(v) for v in arrays[0]) & M32


def special_codes(arrays, channels):
    """The codes a frame needs: 1 and 2 where a channel is silent, 4 where both are the same."""
    if channels == 2:
        x, y = arrays
        a = [xv - truncate_div(yv, 2) for xv, yv in zip(x, y)]
        b = [av + yv for av, yv in zip(a, y)]
        return (1 if all(v == 0 for v in b) else 0) | (2 if all(v == 0 for v in a) else 0) | (4 if all(v == 0 for v in y) else 0)
    return 1 if all(v == 0 for v in arrays[0]) else 0


def encode_frame(samples, pcm, channels, bits, level, version, has_crc, special_override=None, forced=None):
    """(the stream bytes of a frame, their length in bits). pcm is the frame's audio in WAV layout. Up to 3.80 the bits run on into the next frame."""
    arrays = prepare(samples, channels, bits, version)
    special = 0
    silent = False
    if has_crc:
        crc = zlib.crc32(pcm)
        if version > 3820:
            special = special_codes(arrays, channels) if special_override is None else special_override
            silent = (special & 3) == 3 if channels == 2 else bool(special & 1)
            word = (crc >> 1) | (0x80000000 if special else 0)
        else:
            word = crc
    else:
        checksum = old_checksum(arrays)
        silent = checksum == 0
        special = 3 if silent and channels == 2 else 1 if silent else 0
    if silent:
        arrays = [[0] * len(a) for a in arrays]
    coded = [] if silent else arrays[:1] if (special & 4 and channels == 2) else arrays
    residuals = [predict(level, version, a) for a in coded]
    w = BitWriter()
    if has_crc:
        w.put(word, 32)
        if version > 3820 and special:
            w.put(special, 32)
    else:
        put_rice(w, old_checksum(arrays), 30)
    if version >= 3900:
        w.align()
        out = bytearray(w.out)
        for r in residuals:
            out += encode_array_range(r, version)
        return bytes(out), len(out) * 8
    for r in residuals:
        if version < 3860:
            encode_array_up_to_385(w, r)
        else:
            encode_array_rice(w, r, version > 3880, forced)
    if version > 3800:
        w.align()
        return bytes(w.out), len(w.out) * 8
    return bytes(w.out) + (bytes([(w.acc << (8 - w.count)) & 0xFF]) if w.count else b""), w.bits()


def blocks_per_frame(version, level):
    return m.old_blocks_per_frame(version, level)


def build_3800(frames, level, flags, channels, rate, final_blocks, header_data=b"", terminating=b"", peak=None, version=3800):
    """A file of version 3.80, whose frames follow each other bit by bit: the header, the optional peak level, the WAV header data, the seek table of byte offsets
    (of the word each frame starts in) and the table of bytes that holds the bit of that word it starts at (APEHeader.cpp, AnalyzeOld)."""
    w = BitWriter()
    starts = []
    for data, nbits in frames:
        starts.append(w.bits())
        for byte in data[:nbits // 8]:
            w.put(byte, 8)
        if nbits % 8:
            w.put(data[nbits // 8] >> (8 - nbits % 8), nbits % 8)
    stream = bytes(w.out) + (bytes([(w.acc << (8 - w.count)) & 0xFF]) if w.count else b"")
    stream += b"\0" * (-len(stream) % 4 + 4)
    body = bytearray(len(stream))
    for at in range(0, len(stream), 4):
        body[at:at + 4] = stream[at:at + 4][::-1]
    n = len(frames)
    extra = struct.pack("<i", peak) if peak is not None else b""
    fixed = 32 + len(extra) + len(header_data) + 5 * n
    seek = b"".join(struct.pack("<I", fixed + (start // 32) * 4) for start in starts)
    table = bytes(start % 32 for start in starts)
    header = b"MAC " + struct.pack("<HHHHIIIII", version, level, flags | (4 if peak is not None else 0), channels, rate, len(header_data), len(terminating), n, final_blocks)
    return header + extra + header_data + seek + table + bytes(body) + terminating


def encode(samples, version, level, channels, bits, rate, pcm, flags=0, peak=None, header_data=b"", terminating=b"", has_crc=True, per_frame=None, padded=True, forced=None):
    """A file of the given version from samples (a list of frames of channel values) and their WAV-layout bytes. Returns (file bytes, frames, blocks of
    each frame). padded=False leaves off the four zero bytes the last frame of 3.81 and later gets after its coded data (3.80 keeps its padding, which closes
    the table of bits): the file then ends where the coded data does, up to the word it ends in, and the decoder of 3.90 to 3.92 reads past it."""
    per_frame = per_frame or blocks_per_frame(version, level)
    width = channels * bits // 8
    frames = []
    counts = []
    for start in range(0, len(samples), per_frame):
        chunk = samples[start:start + per_frame]
        frames.append(encode_frame(chunk, pcm[start * width:(start + len(chunk)) * width], channels, bits, level, version, has_crc, forced=forced))
        counts.append(len(chunk))
    flags |= 2 if has_crc else 0
    if bits == 8:
        flags |= 1
    elif bits == 24:
        flags |= 8
    if version <= 3800:
        data = build_3800(frames, level, flags, channels, rate, counts[-1], header_data, terminating, peak, version)
    else:
        streams = [f[0] for f in frames]
        streams[-1] += b"\0" * 4 if padded else b""
        data = m.build_old(streams, version, level, flags | (4 if peak is not None else 0), channels, rate, counts[-1], header_data=header_data, terminating=terminating, peak=peak)
    return data, frames, counts
