# Derived from libwavpack 5.6.0 (unpack.c, read_words.c, entropy_utils.c, decorr_utils.c, open_utils.c, unpack_floats.c, unpack_dsd.c), Copyright (c) 1998 - 2022 David Bryant,
# under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.
"""A WavPack decoder worked out from libwavpack 5.6.0's source (unpack.c, read_words.c, entropy_utils.c, decorr_utils.c, open_utils.c,
unpack_floats.c, unpack_dsd.c, unpack_utils.c and cli/wvunpack.c), apart from the analyzer: the decoded samples and the verdicts of the
checks over them that the manifests of tests/fixtures/wavpack hold (decoded_samples, decoded_pcm_md5, verification), which the C# decoder
must reproduce and crosscheck_wavpack_fixtures.py confirms with wvunpack and FFmpeg.

It decodes the lossless and hybrid streams the way wvunpack does without a correction file (hybrid lossy), floats, integers above 24
bits and DSD, and works out what the analyzer's decoder does with the blocks of a file: which it hands over, how it gathers them into
sets (the channels of the same samples), which it cannot decode, and what the CRC of each block, the CRC of the extra bits and the MD5
say (docs/design/decoders.md, "Data flows beyond FLAC", "Checksums and verdicts"). Only the standard library is used.
"""

import hashlib
import struct

import wavpack_model as m

MONO_FLAG, HYBRID_FLAG, JOINT, CROSS, FLOAT, INT32 = 4, 8, 0x10, 0x20, 0x80, 0x100
HYBRID_BITRATE, HYBRID_BALANCE, FALSE_STEREO = 0x200, 0x400, 0x40000000
MONO_DATA = MONO_FLAG | FALSE_STEREO
MASK32 = 0xFFFFFFFF
MAX_TERM = 8
MAX_SET_SAMPLES = 1 << 24
LIMIT_ONES = 16
SLS, SLO = 8, 128

# libwavpack's exp2_table and log2_table (entropy_utils.c), the tables of the 8-bit logarithms the sub-blocks store.
EXP2 = bytes.fromhex(
    "00010102030304050606070808090a0b0b0c0d0e0e0f10101112131314151616"
    "171819191a1b1c1d1d1e1f202021222324242526272828292a2b2c2c2d2e2f30"
    "30313233343535363738393a3a3b3c3d3e3f4041414243444546474848494a4b"
    "4c4d4e4f50515152535455565758595a5b5c5d5e5e5f60616263646566676869"
    "6a6b6c6d6e6f707172737475767778797a7b7c7d7e7f8081828384858788898a"
    "8b8c8d8e8f9091929395969798999a9b9c9d9fa0a1a2a3a4a5a6a8a9aaabacad"
    "afb0b1b2b3b4b6b7b8b9babcbdbebfc0c2c3c4c5c6c8c9cacbcdcecfd0d2d3d4"
    "d6d7d8d9dbdcdddee0e1e2e4e5e6e8e9eaecedeef0f1f2f4f5f6f8f9fafcfdff"
)
LOG2 = bytes.fromhex(
    "000103040607090a0b0d0e10111214151618191a1c1d1e20212224252628292a"
    "2c2d2e2f31323334363738393b3c3d3e3f41424344454748494a4b4d4e4f5051"
    "525455565758595a5c5d5e5f6061626364666768696a6b6c6d6e6f7071727475"
    "767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f909192939495"
    "969798999a9b9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9a9aaabacadaeafb0b1b2b2"
    "b3b4b5b6b7b8b9b9babbbcbdbebfc0c0c1c2c3c4c5c6c6c7c8c9cacbcbcccdce"
    "cfd0d0d1d2d3d4d4d5d6d7d8d8d9dadbdcdcdddedfe0e0e1e2e3e4e4e5e6e7e7"
    "e8e9eaeaebecedeeeeeff0f1f1f2f3f4f4f5f6f7f7f8f9f9fafbfcfcfdfeffff"
)


def s32(x):
    return ((x + 0x80000000) & MASK32) - 0x80000000


class Refused(Exception):
    """libwavpack refuses the block (a sub-block whose size or content it does not take, no bitstream)."""


def exp2s(log):
    if log < 0:
        return -exp2s(-log)
    value = EXP2[log & 0xFF] | 0x100
    log >>= 8
    return value >> (9 - log) if log <= 9 else (value << ((log - 9) & 0x1F)) & MASK32


def log2_of(value):
    """wp_log2 of an unsigned 32-bit value."""
    value = (value + (value >> 9)) & MASK32
    if value < 256:
        bits = value.bit_length()
        return (bits << 8) + LOG2[(value << (9 - bits)) & 0xFF]
    bits = (value >> 8).bit_length() + 8 if value < 1 << 16 else (value >> 16).bit_length() + 16 if value < 1 << 24 else (value >> 24).bit_length() + 24
    return (bits << 8) + LOG2[(value >> (bits - 9)) & 0xFF]


def restore_weight(stored):
    stored = stored - 256 if stored > 127 else stored
    result = stored * 8
    return result + ((result + 64) >> 7) if result > 0 else result


def read_sub(block, position):
    """read_metadata_buff: (id, function, data start, data length, next position) or None where the block holds no further sub-block."""
    if len(block) - position < 2:
        return None
    ident = block[position]
    length = block[position + 1] << 1
    at = position + 2
    if ident & 0x80:
        if len(block) - at < 2:
            return None
        length += (block[at] << 9) + (block[at + 1] << 17)
        at += 2
    if ident & 0x40:
        if length == 0:
            return None
        length -= 1
    padded = length + (length & 1)
    if length and len(block) - at < padded:
        return None
    return ident, ident & 0x3F, at, length, at + (padded if length else 0)


class Contents:
    """What unpack_init and process_metadata take from a block's sub-blocks."""

    def __init__(self, block, flags, samples, version, channels_known):
        self.terms, self.deltas, self.weights_a, self.weights_b = [], [], [], []
        self.samples_a, self.samples_b = [], []
        self.medians = [0] * 6
        self.slow = [0, 0]
        self.acc = [0, 0]
        self.delta = [0, 0]
        self.bitstream = None
        self.extra = None
        self.extra_crc = 0
        self.dsd = None
        self.correction = False
        self.channel_info = None
        self.sample_rate = None
        self.new_config = None
        self.md5 = None
        self.float_info = (0, 0, 0, 0)
        self.int32_info = (0, 0, 0, 0)
        mono = bool(flags & MONO_DATA)
        if flags & MONO_DATA == MONO_DATA:
            raise Refused("the mono flag and the false stereo flag are both set")
        position = m.HEADER
        while (sub := read_sub(block, position)) is not None:
            ident, function, start, length, position = sub
            data = block[start:start + length]
            self.process(function, ident, start, length, data, flags, samples, version, mono, channels_known, block)
        if samples and (self.dsd is None if flags & m.DSD else self.bitstream is None):
            raise Refused("holds only correction data" if self.correction else "counts samples but holds no bitstream")

    def process(self, function, ident, start, length, data, flags, samples, version, mono, channels_known, block):
        if function == 0x00:
            return
        if function == 0x02:
            if len(data) > 16:
                raise Refused("too many decorrelation passes")
            terms = []
            for byte in data:
                term = (byte & 0x1F) - 5
                if term == 0 or term < -3 or 8 < term < 17 or term > 18 or (mono and term < 0):
                    raise Refused("invalid decorrelation term")
                terms.append((term, byte >> 5))
            terms.reverse()
            self.terms = [t for t, d in terms]
            self.deltas = [d for t, d in terms]
            self.weights_a = [0] * len(terms)
            self.weights_b = [0] * len(terms)
            self.samples_a = [[0] * MAX_TERM for _ in terms]
            self.samples_b = [[0] * MAX_TERM for _ in terms]
        elif function == 0x03:
            count = len(data) if mono else len(data) // 2
            if count > len(self.terms):
                raise Refused("too many weights")
            self.weights_a = [0] * len(self.terms)
            self.weights_b = [0] * len(self.terms)
            at = 0
            for pass_ in range(len(self.terms) - 1, -1, -1):
                if count <= 0:
                    break
                count -= 1
                self.weights_a[pass_] = restore_weight(data[at])
                at += 1
                if not mono:
                    self.weights_b[pass_] = restore_weight(data[at])
                    at += 1
        elif function == 0x04:
            self.samples_a = [[0] * MAX_TERM for _ in self.terms]
            self.samples_b = [[0] * MAX_TERM for _ in self.terms]
            at = 0
            if version == 0x402 and flags & HYBRID_FLAG:
                if len(data) < (2 if mono else 4):
                    raise Refused("short decorrelation samples")
                at = 2 if mono else 4
            for pass_ in range(len(self.terms) - 1, -1, -1):
                if at >= len(data):
                    break
                term = self.terms[pass_]
                if term > MAX_TERM:
                    if len(data) - at < (4 if mono else 8):
                        raise Refused("short decorrelation samples")
                    self.samples_a[pass_][0] = exp2s(struct.unpack_from("<h", data, at)[0])
                    self.samples_a[pass_][1] = exp2s(struct.unpack_from("<h", data, at + 2)[0])
                    at += 4
                    if not mono:
                        self.samples_b[pass_][0] = exp2s(struct.unpack_from("<h", data, at)[0])
                        self.samples_b[pass_][1] = exp2s(struct.unpack_from("<h", data, at + 2)[0])
                        at += 4
                elif term < 0:
                    if len(data) - at < 4:
                        raise Refused("short decorrelation samples")
                    self.samples_a[pass_][0] = exp2s(struct.unpack_from("<h", data, at)[0])
                    self.samples_b[pass_][0] = exp2s(struct.unpack_from("<h", data, at + 2)[0])
                    at += 4
                else:
                    for k in range(term):
                        if len(data) - at < (2 if mono else 4):
                            raise Refused("short decorrelation samples")
                        self.samples_a[pass_][k] = exp2s(struct.unpack_from("<h", data, at)[0])
                        at += 2
                        if not mono:
                            self.samples_b[pass_][k] = exp2s(struct.unpack_from("<h", data, at)[0])
                            at += 2
            if at != len(data):
                raise Refused("decorrelation samples that do not end exactly")
        elif function == 0x05:
            if len(data) != (6 if mono else 12):
                raise Refused("entropy variables of the wrong size")
            for i in range(len(data) // 2):
                self.medians[i] = exp2s(data[2 * i] + (data[2 * i + 1] << 8))
        elif function == 0x06:
            self.profile(data, flags, mono)
        elif function == 0x07:
            if len(data) == 2:
                pass
            elif len(data) >= (4 if mono else 8):
                pass
            else:
                raise Refused("shaping weights of the wrong size")
        elif function == 0x08:
            if len(data) != 4:
                raise Refused("float info of the wrong size")
            self.float_info = tuple(data)
        elif function == 0x09:
            if len(data) != 4:
                raise Refused("int32 info of the wrong size")
            self.int32_info = tuple(data)
        elif function == 0x0A:
            if not length or length & 1:
                raise Refused("a bitstream of an odd or empty size")
            self.bitstream = data
        elif function == 0x0B:
            if not length or length & 1:
                raise Refused("a correction bitstream of an odd or empty size")
            self.correction = True
        elif function == 0x0C:
            if length <= 4 or length & 1:
                raise Refused("an extra bitstream of an odd size or without a CRC")
            self.extra_crc = struct.unpack_from("<I", data)[0]
            self.extra = data[4:]
        elif function == 0x0D:
            if not 0 < length <= 7:
                raise Refused("a channel info of the wrong size")
            if not channels_known:
                info = channel_info_of(data)
                if info is None:
                    raise Refused("a channel info libwavpack refuses")
                if info[0]:
                    self.channel_info = info
        elif function == 0x0E:
            self.read_dsd(data, samples, mono, flags)
        elif function == 0x2B:
            if not length or 0 in data:
                raise Refused("channel identities that are empty or hold a 0")
        elif function == 0x2A:
            self.read_new_config(data)
        elif function == 0x27:
            if length in (3, 4):
                rate = data[0] | data[1] << 8 | data[2] << 16 | ((data[3] & 0x7F) << 24 if length == 4 else 0)
                self.sample_rate = rate
        elif function in (0x26, 0x29):
            if length == 16:
                self.md5 = (bytes(data), function == 0x29, start - (4 if ident & 0x80 else 2), (4 if ident & 0x80 else 2) + length + (length & 1))
        elif function in (0x21, 0x22, 0x23, 0x24, 0x25, 0x28, 0x2F):
            return
        elif not function & 0x20:
            raise Refused(f"a sub-block of function 0x{function:02X}, which is not optional")

    def profile(self, data, flags, mono):
        channels = 1 if mono else 2
        at = 0
        if flags & HYBRID_BITRATE:
            if len(data) < 2 * channels:
                raise Refused("short hybrid profile")
            for c in range(channels):
                self.slow[c] = exp2s(data[at] + (data[at + 1] << 8))
                at += 2
        if len(data) - at < 2 * channels:
            raise Refused("short hybrid profile")
        for c in range(channels):
            self.acc[c] = ((data[at] + (data[at + 1] << 8)) << 16) & MASK32
            at += 2
        if at < len(data):
            if len(data) - at < 2 * channels:
                raise Refused("short hybrid profile")
            for c in range(channels):
                self.delta[c] = exp2s(struct.unpack_from("<h", data, at)[0]) & MASK32
                at += 2
            if at < len(data):
                raise Refused("long hybrid profile")
        else:
            self.delta = [0, 0]

    def read_new_config(self, data):
        self.new_config = {"qmode": 0, "layout_channels": 0, "reordering": None}
        if len(data) < 2:
            return
        self.new_config["qmode"] = data[1]
        if len(data) >= 4:
            count = data[3]
            self.new_config["layout_channels"] = count
            if len(data) > 4:
                given = len(data) - 4
                if given > count:
                    raise Refused("a reordering longer than the layout's channels")
                order = []
                for i in range(count):
                    value = data[4 + i] if i < given else i
                    order.append(0 if value >= count else value)
                self.new_config["reordering"] = order

    def read_dsd(self, data, samples, mono, flags):
        """init_dsd_block's checks of the size and the mode, and the parser's (Reader.DsdChecks); the tables are read when the block is decoded."""
        if len(data) < 2 or data[0] > 31:
            raise Refused("a DSD block that is too short or has a rate power above 31")
        mode = data[1]
        if mode == 0:
            if len(data) - 2 != samples * (1 if mono else 2):
                raise Refused("a stored DSD block of the wrong size")
        elif mode == 1:
            if len(data) < 3 or data[2] > 5 or len(data) == 3:
                raise Refused("a DSD block in the fast mode without history bits or probabilities")
        elif mode == 3:
            if len(data) - 2 < (13 if mono else 20) or data[3] != 20:
                raise Refused("a DSD block in the high mode that is too short or has another rate step")
        else:
            raise Refused("a DSD block of an unknown mode")
        self.dsd = data


def channel_info_of(data):
    """(channels, mask, streams) of a channel info libwavpack takes as the first, or None where it refuses it."""
    if len(data) >= 6:
        channels, streams = (data[0] | (data[2] & 0xF) << 8) + 1, (data[1] | (data[2] & 0xF0) << 4) + 1
        mask = int.from_bytes(data[3:], "little")
        if channels < streams:
            return None
    else:
        channels, streams = data[0], 8
        mask = int.from_bytes(data[1:], "little")
    if channels > streams * 2 or bin(mask).count("1") > channels:
        return None
    return channels, mask, streams


# ---- entropy decoding ----

class Bits:
    """Bits least significant first; past the end they read as zero and the overrun is noted."""

    def __init__(self, data):
        self.data = bytes(data)
        self.pos = 0
        self.size = len(self.data) * 8

    @property
    def overrun(self):
        return self.pos > self.size

    def bit(self):
        p = self.pos
        self.pos += 1
        return (self.data[p >> 3] >> (p & 7)) & 1 if p < self.size else 0

    def read(self, n):
        if n == 0:
            return 0
        p = self.pos
        self.pos += n
        offset = p & 7
        chunk = int.from_bytes(self.data[p >> 3:(p + n + 7) >> 3], "little")
        return (chunk >> offset) & ((1 << n) - 1)

    def ones(self, limit):
        """Counts one bits up to a zero, which is consumed, or to the limit."""
        total = 0
        while total < limit:
            if self.pos >= self.size:
                self.pos = max(self.pos, self.size + 1)
                return total
            if not self.bit():
                return total
            total += 1
        return total


def read_code(bits, maxcode):
    if maxcode < 2:
        return bits.bit() if maxcode else 0
    count = maxcode.bit_length()
    extras = (1 << count) - maxcode - 1
    code = bits.read(count - 1)
    if code >= extras:
        code = (code << 1) - extras + bits.bit()
    return code


class Words:
    def __init__(self, contents, flags):
        self.flags = flags
        self.median = list(contents.medians)
        self.slow = list(contents.slow)
        self.limit = [0, 0]
        self.acc = list(contents.acc)
        self.delta = list(contents.delta)
        self.holding_one = 0
        self.holding_zero = False
        self.zeros = 0

    def update_limit(self):
        f = self.flags
        self.acc[0] = (self.acc[0] + self.delta[0]) & MASK32
        rate0 = self.acc[0] >> 16
        if f & MONO_DATA:
            if f & HYBRID_BITRATE:
                slow0 = (self.slow[0] + SLO) >> SLS
                self.limit[0] = exp2s(slow0 - rate0 + 0x100) if slow0 - rate0 > -0x100 else 0
            else:
                self.limit[0] = exp2s(rate0)
            return
        self.acc[1] = (self.acc[1] + self.delta[1]) & MASK32
        rate1 = self.acc[1] >> 16
        if f & HYBRID_BITRATE:
            slow0 = (self.slow[0] + SLO) >> SLS
            slow1 = (self.slow[1] + SLO) >> SLS
            if f & HYBRID_BALANCE:
                balance = (slow1 - slow0 + rate1 + 1) >> 1
                if balance > rate0:
                    rate1, rate0 = rate0 * 2, 0
                elif -balance > rate0:
                    rate0, rate1 = rate0 * 2, 0
                else:
                    rate1, rate0 = rate0 + balance, rate0 - balance
            self.limit[0] = exp2s(slow0 - rate0 + 0x100) if slow0 - rate0 > -0x100 else 0
            self.limit[1] = exp2s(slow1 - rate1 + 0x100) if slow1 - rate1 > -0x100 else 0
        else:
            self.limit[0] = exp2s(rate0)
            self.limit[1] = exp2s(rate1)

    def word(self, bits, chan):
        """get_word: the next value of the channel, or None where the bitstream ends."""
        m_ = self.median
        base = 3 * chan
        if m_[0] < 2 and not self.holding_zero and not self.holding_one and m_[3] < 2:
            if self.zeros:
                self.zeros -= 1
                if self.zeros:
                    self.slow[chan] = (self.slow[chan] - ((self.slow[chan] + SLO) >> SLS)) & MASK32
                    return 0
            else:
                cbits = bits.ones(33)
                if cbits == 33:
                    return None
                self.zeros = cbits if cbits < 2 else bits.read(cbits - 1) | (1 << (cbits - 1))
                if self.zeros:
                    self.slow[chan] = (self.slow[chan] - ((self.slow[chan] + SLO) >> SLS)) & MASK32
                    self.median = m_ = [0] * 6
                    return 0
        if self.holding_zero:
            ones = 0
            self.holding_zero = False
        else:
            ones = bits.ones(LIMIT_ONES + 1)
            if ones > LIMIT_ONES:
                return None
            if ones == LIMIT_ONES:
                cbits = bits.ones(33)
                if cbits == 33:
                    return None
                ones = (cbits if cbits < 2 else bits.read(cbits - 1) | (1 << (cbits - 1))) + LIMIT_ONES
            if self.holding_one:
                self.holding_one = ones & 1
                ones = (ones >> 1) + 1
            else:
                self.holding_one = ones & 1
                ones >>= 1
            self.holding_zero = not self.holding_one
        if self.flags & HYBRID_FLAG and chan == 0:
            self.update_limit()

        def med(k):
            return (m_[base + k] >> 4) + 1

        if ones == 0:
            low, high = 0, med(0) - 1
            m_[base] = (m_[base] - ((m_[base] + 126) // 128) * 2) & MASK32
        else:
            low = med(0)
            m_[base] = (m_[base] + ((m_[base] + 128) // 128) * 5) & MASK32
            if ones == 1:
                high = low + med(1) - 1
                m_[base + 1] = (m_[base + 1] - ((m_[base + 1] + 62) // 64) * 2) & MASK32
            else:
                low += med(1)
                m_[base + 1] = (m_[base + 1] + ((m_[base + 1] + 64) // 64) * 5) & MASK32
                if ones == 2:
                    high = low + med(2) - 1
                    m_[base + 2] = (m_[base + 2] - ((m_[base + 2] + 30) // 32) * 2) & MASK32
                else:
                    low = (low + (ones - 2) * med(2)) & MASK32
                    high = (low + med(2) - 1) & MASK32
                    m_[base + 2] = (m_[base + 2] + ((m_[base + 2] + 32) // 32) * 5) & MASK32
        low &= 0x7FFFFFFF
        high &= 0x7FFFFFFF
        if low > high:
            high = low
        mid = (high + low + 1) >> 1
        limit = self.limit[chan]
        if not limit:
            mid = read_code(bits, high - low) + low
        else:
            while (high - low) & MASK32 > limit:
                if bits.overrun:
                    return None
                if bits.bit():
                    low = mid
                    mid = (high + low + 1) >> 1
                else:
                    high = (mid - 1) & MASK32
                    mid = (high + low + 1) >> 1
        sign = bits.bit()
        if self.flags & HYBRID_BITRATE:
            self.slow[chan] = (self.slow[chan] - ((self.slow[chan] + SLO) >> SLS) + log2_of(mid)) & MASK32
        return s32(~mid if sign else mid)


def apply_weight(weight, sample):
    if sample != s16(sample):
        return s32((((((sample & 0xFFFF) * weight) >> 9) + (((sample & ~0xFFFF) >> 9) * weight) + 1) >> 1))
    return s32((weight * sample + 512) >> 10)


def s16(x):
    return ((x + 0x8000) & 0xFFFF) - 0x8000


def update(weight, delta, source, result):
    if source and result:
        s = -1 if (source ^ result) < 0 else 0
        return s32((delta ^ s) + (weight - s))
    return weight


def update_clip(weight, delta, source, result):
    if source and result:
        s = -1 if (source ^ result) < 0 else 0
        weight = s32((weight ^ s) + (delta - s))
        if weight > 1024:
            weight = 1024
        weight = s32((weight ^ s) - s)
    return weight


def mono_pass(buf, c, p):
    term, delta, w = c.terms[p], c.deltas[p], c.weights_a[p]
    a = c.samples_a[p]
    if term in (17, 18):
        for i, x in enumerate(buf):
            sam = s32(2 * a[0] - a[1]) if term == 17 else s32(3 * a[0] - a[1]) >> 1
            a[1] = a[0]
            a[0] = s32(apply_weight(w, sam) + x)
            w = update(w, delta, sam, x)
            buf[i] = a[0]
    else:
        m_, k = 0, term & 7
        for i, x in enumerate(buf):
            sam = a[m_]
            a[k] = s32(apply_weight(w, sam) + x)
            w = update(w, delta, sam, x)
            buf[i] = a[k]
            m_, k = (m_ + 1) & 7, (k + 1) & 7
    c.weights_a[p] = w


def stereo_pass(buf, c, p):
    term, delta = c.terms[p], c.deltas[p]
    wa, wb = c.weights_a[p], c.weights_b[p]
    a, b = c.samples_a[p], c.samples_b[p]
    n = len(buf) // 2
    if term in (17, 18):
        for i in range(n):
            for ch, w in ((0, wa), (1, wb)):
                s = a if ch == 0 else b
                sam = s32(2 * s[0] - s[1]) if term == 17 else s32(s[0] + ((s[0] - s[1]) >> 1))
                s[1] = s[0]
                x = buf[2 * i + ch]
                s[0] = s32(apply_weight(w, sam) + x)
                w = update(w, delta, sam, x)
                buf[2 * i + ch] = s[0]
                if ch == 0:
                    wa = w
                else:
                    wb = w
    elif term > 0:
        m_, k = 0, term & 7
        for i in range(n):
            x = buf[2 * i]
            sam = a[m_]
            a[k] = s32(apply_weight(wa, sam) + x)
            wa = update(wa, delta, sam, x)
            buf[2 * i] = a[k]
            x = buf[2 * i + 1]
            sam = b[m_]
            b[k] = s32(apply_weight(wb, sam) + x)
            wb = update(wb, delta, sam, x)
            buf[2 * i + 1] = b[k]
            m_, k = (m_ + 1) & 7, (k + 1) & 7
    elif term == -1:
        for i in range(n):
            x0, x1 = buf[2 * i], buf[2 * i + 1]
            sam = s32(x0 + apply_weight(wa, a[0]))
            wa = update_clip(wa, delta, a[0], x0)
            buf[2 * i] = sam
            a[0] = s32(x1 + apply_weight(wb, sam))
            wb = update_clip(wb, delta, sam, x1)
            buf[2 * i + 1] = a[0]
    elif term == -2:
        for i in range(n):
            x0, x1 = buf[2 * i], buf[2 * i + 1]
            sam = s32(x1 + apply_weight(wb, b[0]))
            wb = update_clip(wb, delta, b[0], x1)
            buf[2 * i + 1] = sam
            b[0] = s32(x0 + apply_weight(wa, sam))
            wa = update_clip(wa, delta, sam, x0)
            buf[2 * i] = b[0]
    else:
        for i in range(n):
            x0, x1 = buf[2 * i], buf[2 * i + 1]
            sam_a = s32(x0 + apply_weight(wa, a[0]))
            wa = update_clip(wa, delta, a[0], x0)
            sam_b = s32(x1 + apply_weight(wb, b[0]))
            wb = update_clip(wb, delta, b[0], x1)
            buf[2 * i] = b[0] = sam_a
            buf[2 * i + 1] = a[0] = sam_b
    c.weights_a[p], c.weights_b[p] = wa, wb


def limit_of(flags, magnitude_bits):
    limit = s32((1 << magnitude_bits) + 2)
    if flags & HYBRID_FLAG:
        limit = s32(limit * 2 + 128)
    return limit


def abs32(v):
    return -v if v < 0 else v


def unpack_pcm(block, h, c, count):
    """unpack_samples for a main file: (interleaved samples, muted, crc, extra crc or None)."""
    flags = h["flags"]
    mono = bool(flags & MONO_DATA)
    width = 1 if mono else 2
    mute_limit = limit_of(flags, flags >> 18 & 0x1F)
    bits = Bits(c.bitstream)
    words = Words(c, flags)
    buf = [0] * (count * width)
    muted = False
    crc = MASK32
    complete = True
    for i in range(count):
        for ch in range(width):
            v = words.word(bits, ch)
            if v is None or bits.overrun:
                complete = False
                break
            buf[i * width + ch] = v
        if not complete:
            break
    if complete:
        for p in range(len(c.terms)):
            (mono_pass if mono else stereo_pass)(buf, c, p)
        stopped = count
        if mono:
            for i, x in enumerate(buf):
                if abs32(x) > mute_limit:
                    stopped = i
                    break
                crc = (crc * 3 + x) & MASK32
        else:
            joint = flags & JOINT
            for i in range(count):
                if joint:
                    buf[2 * i + 1] = s32(buf[2 * i + 1] - (buf[2 * i] >> 1))
                    buf[2 * i] = s32(buf[2 * i] + buf[2 * i + 1])
                crc = (crc + (crc << 3) + (buf[2 * i] << 1) + buf[2 * i] + buf[2 * i + 1]) & MASK32
            for i in range(0, 2 * count, 16):
                if abs32(buf[i]) > mute_limit or abs32(buf[i + 1]) > mute_limit:
                    stopped = i // 2
                    break
        complete = stopped == count
    if not complete:
        buf = [0] * (count * width)
        muted = True
    extra_crc = None
    buf, extra_crc = fixup(buf, h, c, muted)
    return buf, muted, crc, extra_crc


def fixup(buf, h, c, muted):
    flags = h["flags"]
    lossy = bool(flags & HYBRID_FLAG)
    shift = flags >> 13 & 0x1F
    extra = None if c.extra is None or muted else Bits(c.extra)
    crc_x = MASK32
    if flags & FLOAT:
        return restore_floats(buf, c, extra)
    if flags & INT32:
        sent, zeros, ones, dups = (v & 0x1F for v in c.int32_info)
        mask = (1 << sent) - 1
        if extra is not None:
            for i, v in enumerate(buf):
                data = extra.read(sent)
                v = s32(((v << sent) & MASK32) | (data & mask))
                if zeros:
                    v = s32((v << zeros) & MASK32)
                elif ones:
                    v = s32((((v + 1) & MASK32) << ones & MASK32) - 1)
                elif dups:
                    v = s32((((v + (v & 1)) & MASK32) << dups & MASK32) - (v & 1))
                buf[i] = v
                crc_x = (crc_x * 9 + (v & 0xFFFF) * 3 + ((v >> 16) & 0xFFFF)) & MASK32
            return finish_shift(buf, flags, lossy, shift), crc_x
        if not sent and (zeros + ones + dups):
            while lossy and flags & 3 == 3 and shift < 8:
                if zeros:
                    zeros -= 1
                elif ones:
                    ones -= 1
                elif dups:
                    dups -= 1
                else:
                    break
                shift += 1
            for i, v in enumerate(buf):
                if zeros:
                    v = s32((v << zeros) & MASK32)
                elif ones:
                    v = s32((((v + 1) & MASK32) << ones & MASK32) - 1)
                elif dups:
                    v = s32((((v + (v & 1)) & MASK32) << dups & MASK32) - (v & 1))
                buf[i] = v
        else:
            shift += zeros + sent + ones + dups
    return finish_shift(buf, flags, lossy, shift), (crc_x if extra is not None else None)


def finish_shift(buf, flags, lossy, shift):
    shift &= 0x1F
    if lossy:
        size = flags & 3
        lo, hi = ((-128, 127), (-32768, 32767), (-8388608, 8388607), (-2**31, 2**31 - 1))[size]
        lo >>= shift
        hi >>= shift
        lo_s, hi_s = s32(lo << shift), s32(hi << shift)
        return [lo_s if v < lo else hi_s if v > hi else s32((v << shift) & MASK32) for v in buf]
    if shift:
        return [s32((v << shift) & MASK32) for v in buf]
    return buf


def restore_floats(buf, c, extra):
    flags_f, shift_f, max_exp, _ = c.float_info
    shift_f &= 0x1F
    out = []
    crc = MASK32
    for value in buf:
        shift_count = 0
        exp = max_exp
        sign = mant = expo = 0
        if extra is None:
            if value:
                value = s32((value << shift_f) & MASK32)
                if value < 0:
                    value = s32(-value)
                    sign = 1
                if value >= 0x1000000:
                    while value & 0xF000000:
                        value >>= 1
                        exp += 1
                elif exp:
                    while not value & 0x800000:
                        exp -= 1
                        if not exp:
                            break
                        shift_count += 1
                        value = s32((value << 1) & MASK32)
                    shift_count &= 0x1F
                    if shift_count and flags_f & 1:
                        value |= (1 << shift_count) - 1
                mant = value & 0x7FFFFF
                expo = exp & 0xFF
            out.append(s32(sign << 31 | expo << 23 | mant))
            continue
        if value == 0:
            if flags_f & 8:
                if extra.bit():
                    mant = extra.read(23)
                    if exp >= 25:
                        expo = extra.read(8)
                    sign = extra.bit()
                elif flags_f & 0x10:
                    sign = extra.bit()
        else:
            value = s32((value << shift_f) & MASK32)
            if value < 0:
                value = s32(-value)
                sign = 1
            if value == 0x1000000:
                if extra.bit():
                    mant = extra.read(23)
                expo = 255
            else:
                if exp:
                    while not value & 0x800000:
                        exp -= 1
                        if not exp:
                            break
                        shift_count += 1
                        value = s32((value << 1) & MASK32)
                shift_count &= 0x1F
                if shift_count:
                    if flags_f & 1 or (flags_f & 2 and extra.bit()):
                        value |= (1 << shift_count) - 1
                    elif flags_f & 4:
                        value |= extra.read(shift_count) & ((1 << shift_count) - 1)
                mant = value & 0x7FFFFF
                expo = exp & 0xFF
        crc = (crc * 27 + mant * 9 + expo * 3 + sign) & MASK32
        out.append(s32(sign << 31 | expo << 23 | mant))
    return out, (crc if extra is not None else None)


# ---- DSD ----

def fast_tables(data):
    at = 2
    if at == len(data):
        raise Refused("a DSD block in the fast mode that ends before its history bits")
    history = data[at]
    at += 1
    if at == len(data) or history > 5:
        raise Refused("a DSD block in the fast mode without probabilities")
    bins = 1 << history
    max_probability = data[at]
    at += 1
    size = bins * 256
    probabilities = bytearray(size)
    if max_probability < 0xFF:
        out = 0
        while out < size and at < len(data):
            code = data[at]
            at += 1
            if code > max_probability:
                out = min(size, out + code - max_probability)
            elif code:
                probabilities[out] = code
                out += 1
            else:
                break
        if out < size:
            raise Refused("probabilities that end early")
        if at < len(data):
            last = data[at]
            at += 1
            if last:
                raise Refused("probabilities that do not end in a zero")
    elif len(data) - at > size:
        probabilities[:] = data[at:at + size]
        at += size
    else:
        raise Refused("probabilities that are missing")
    summed, lookup, total = [], [], 0
    for b in range(bins):
        row = probabilities[b * 256:(b + 1) * 256]
        run, sums = 0, []
        for v in row:
            run += v
            sums.append(run)
        summed.append(sums)
        total += run
        if total > bins * 1280:
            raise Refused("probabilities that sum to too much")
        values = []
        if run:
            for i, v in enumerate(row):
                values.extend([i] * v)
        lookup.append(values)
    if len(data) - at < 4:
        raise Refused("a DSD block in the fast mode without a range coder")
    return {"bins": bins, "probabilities": probabilities, "summed": summed, "lookup": lookup, "value": struct.unpack_from(">I", data, at)[0], "at": at + 4}


def decode_fast(data, t, count, mono):
    value, at = t["value"], t["at"]
    bins, summed, lookup, prob = t["bins"], t["summed"], t["lookup"], t["probabilities"]
    low, high = 0, MASK32
    p0 = p1 = 0
    out = []
    crc = MASK32
    for _ in range(count * (1 if mono else 2)):
        total = summed[p0][255]
        if not total:
            return None, crc
        mult = (high - low) // total
        if not mult:
            if len(data) - at >= 4:
                value = struct.unpack_from(">I", data, at)[0]
                at += 4
            low, high = 0, MASK32
            mult = high // total
            if not mult:
                return None, crc
        index = (value - low) // mult
        if index >= total:
            return None, crc
        code = lookup[p0][index]
        out.append(code)
        if code:
            low = (low + summed[p0][code - 1] * mult) & MASK32
        high = (low + prob[p0 * 256 + code] * mult - 1) & MASK32
        crc = (crc + (crc << 1) + code) & MASK32
        if mono:
            p0 = code & (bins - 1)
        else:
            p0, p1 = p1, code & (bins - 1)
        while not (high ^ low) & 0xFF000000 and at < len(data):
            value = ((value << 8) | data[at]) & MASK32
            at += 1
            high = ((high << 8) | 0xFF) & MASK32
            low = (low << 8) & MASK32
    return out, crc


def high_tables(data, mono):
    at = 2
    if len(data) - at < (13 if mono else 20):
        raise Refused("a DSD block in the high mode that is too short")
    rate_i, rate_s = data[at], data[at + 1]
    at += 2
    if rate_s != 20:
        raise Refused("a DSD block in the high mode with another rate step")
    table = [0] * 256
    value = 0x808000
    rate = rate_i << 8
    for _ in range((rate + 128) >> 8):
        value += (0x10000 - value) >> 8
    for i in range(128):
        table[i] = value
        table[255 - i] = 0x100FFFF - value
        if value > 0x10000:
            rate = s32(rate + ((rate * 20 + 128) >> 8))
            for _ in range((rate + 64) >> 7):
                step = (0x10000 - value) >> 8
                if not step:
                    break
                value += step
    filters = []
    for _ in range(1 if mono else 2):
        f = {"f1": data[at] << 12, "f2": data[at + 1] << 12, "f3": data[at + 2] << 12, "f4": data[at + 3] << 12, "f5": data[at + 4] << 12, "f6": 0}
        factor = data[at + 5] | data[at + 6] << 8
        f["factor"] = factor - 65536 if factor > 32767 else factor
        at += 7
        filters.append(f)
    return {"table": table, "filters": filters, "value": struct.unpack_from(">I", data, at)[0], "at": at + 4}


def decode_high(data, t, count, mono):
    table = list(t["table"])
    filters = [dict(f) for f in t["filters"]]
    value, at = t["value"], t["at"]
    low, high = 0, MASK32
    out = []
    crc = MASK32
    n = len(data)
    for _ in range(count):
        for f in filters:
            f["value"] = s32(f["f1"] - f["f5"] + ((f["f6"] * f["factor"]) >> 2))
            f["byte"] = 0
        for _bit in range(8):
            for f in filters:
                idx = (f["value"] >> 8) & 255
                p = table[idx]
                split = (low + (((high - low) & MASK32) >> 8) * (p >> 16)) & MASK32
                if value <= split:
                    high = split
                    table[idx] = s32(p + ((0x010000FE - p) >> 8))
                    f["f0"] = -1
                else:
                    low = (split + 1) & MASK32
                    table[idx] = s32(p + ((0x10000 - p) >> 8))
                    f["f0"] = 0
                while not (high ^ low) & 0xFF000000 and at < n:
                    value = ((value << 8) | data[at]) & MASK32
                    at += 1
                    high = ((high << 8) | 0xFF) & MASK32
                    low = (low << 8) & MASK32
                f["value"] = s32(f["value"] + f["f6"] * 8)
                f["byte"] = ((f["byte"] << 1) | (f["f0"] & 1)) & MASK32
                f["factor"] = s32(f["factor"] + ((((f["value"] ^ f["f0"]) >> 31) | 1) & ((f["value"] ^ s32(f["value"] - f["f6"] * 16)) >> 31)))
                f["f1"] = s32(f["f1"] + (((f["f0"] & (1 << 20)) - f["f1"]) >> 6))
                f["f2"] = s32(f["f2"] + (((f["f0"] & (1 << 20)) - f["f2"]) >> 4))
                f["f3"] = s32(f["f3"] + ((f["f2"] - f["f3"]) >> 4))
                f["f4"] = s32(f["f4"] + ((f["f3"] - f["f4"]) >> 4))
                f["value"] = s32((f["f4"] - f["f5"]) >> 4)
                f["f5"] = s32(f["f5"] + f["value"])
                f["f6"] = s32(f["f6"] + ((f["value"] - f["f6"]) >> 3))
                f["value"] = s32(f["f1"] - f["f5"] + ((f["f6"] * f["factor"]) >> 2))
        for f in filters:
            byte = f["byte"] & 0xFF
            out.append(byte)
            crc = (crc + (crc << 1) + byte) & MASK32
            f["factor"] = s32(f["factor"] - ((f["factor"] + 512) >> 10))
    return out, crc


def unpack_dsd(h, c, count):
    flags = h["flags"]
    mono = bool(flags & MONO_DATA)
    data = c.dsd
    mode = data[1]
    width = 1 if mono else 2
    if mode == 0:
        out = list(data[2:2 + count * width])
        crc = MASK32
        for b in out:
            crc = (crc + (crc << 1) + b) & MASK32
        return out, False, crc
    result = decode_fast(data, fast_tables(data), count, mono) if mode == 1 else decode_high(data, high_tables(data, mono), count, mono)
    out, crc = result
    if out is None:
        return [0x55] * (count * width), True, crc
    return out, False, crc


# ---- MD5 layouts ----

def digest_bytes(planes, samples, channels, bytes_per_sample, qmode, order):
    """store_samples over the channel-interleaved samples (wvunpack unpack_audio), after the channels are put back in the source's order."""
    big = qmode & 1
    unsigned = qmode & 4 or (bytes_per_sample == 1 and not qmode & 2)
    out = bytearray()
    for i in range(samples):
        for ch in range(channels):
            v = planes[order[ch] if order else ch][i]
            if unsigned:
                v = s32(v + (0x80 << (8 * (bytes_per_sample - 1))))
            raw = (v & ((1 << 8 * bytes_per_sample) - 1)).to_bytes(bytes_per_sample, "big" if big else "little")
            out += raw
    return bytes(out)


def reverse_bits(b):
    return int(f"{b:08b}"[::-1], 2)


class Digest:
    def __init__(self, channels, bytes_per_sample, qmode, is_dsd, order):
        self.md5 = hashlib.md5()
        self.channels, self.bps, self.qmode, self.is_dsd, self.order = channels, bytes_per_sample, qmode, is_dsd, order
        self.block = [bytearray(4096) for _ in range(channels)] if is_dsd and qmode & 0x40 else None
        self.fill = 0

    def append(self, planes, samples):
        if not self.is_dsd:
            self.md5.update(digest_bytes(planes, samples, self.channels, self.bps, self.qmode, self.order))
            return
        if self.block is None:
            out = bytearray()
            for i in range(samples):
                for ch in range(self.channels):
                    out.append(planes[self.order[ch] if self.order else ch][i] & 0xFF)
            self.md5.update(out)
            return
        for i in range(samples):
            for ch in range(self.channels):
                v = planes[self.order[ch] if self.order else ch][i] & 0xFF
                self.block[ch][self.fill] = reverse_bits(v) if self.qmode & 0x10 else v
            self.fill += 1
            if self.fill == 4096:
                self.md5.update(b"".join(bytes(b) for b in self.block))
                self.fill = 0

    def hexdigest(self):
        if self.block is not None and self.fill:
            for b in self.block:
                b[self.fill:] = bytes(4096 - self.fill)
            self.md5.update(b"".join(bytes(b) for b in self.block))
            self.fill = 0
        return self.md5.hexdigest()


# ---- the analyzer's decoding of a file ----

MAX_HELD = 16
MAX_HELD_BYTES = 1 << 16
DEFAULT_STREAMS = 8


class FrameInvalid(Exception):
    """The decoder could not decode a frame (InvalidDataException): decode.frame-invalid, and the next frame is decoded."""


def block_bytes(data, e):
    return data[e["offset"]:e["offset"] + e["length"]]


def declared_total(entries):
    """The sample count the file declares as the block reader finds it (Reader.Declare): in the first block of index 0 with a count when it
    holds no audio, else in the first block with audio, where an index other than 0 or an unknown count leaves it unknown."""
    for e in entries:
        if e["kind"] != "block":
            continue
        h = e["header"]
        if h["samples"]:
            return h["total"] if h["index"] == 0 and h["total"] is not None else None
        if h["index"] == 0 and h["total"]:
            return h["total"]
    return None


def lossy_blocks(entries, data):
    return m.lossy(entries, data)


def facts(entries, data):
    """What the block reader works out about each block and the file, in file order (Reader.cs): which blocks verify, which it marks as
    refused or unreadable, the channel info and the block that settles the stream, and the sets."""
    blocks = [e for e in entries if e["kind"] == "block"]
    channel = None
    configured = None
    sets_state = {"open": False, "final": False, "samples": 0, "index": 0, "blocks": 0, "channels": 0}
    counted = broken = sets = first_channels = 0
    next_index = -1
    has_gap = False
    gap_before = set()
    first_index = None

    def close():
        nonlocal counted, broken, sets, first_channels, next_index
        st = sets_state
        if not st["open"]:
            return
        st["open"] = False
        if not st["final"]:
            broken += 1
        if sets == 0 or first_channels == 0:
            first_channels = st["channels"]
        if st["channels"] != first_channels:
            broken += 1
        sets += 1
        counted += st["samples"]
        next_index = st["index"] + st["samples"]

    for e in blocks:
        h = e["header"]
        f = h["flags"]
        block = block_bytes(data, e)
        e["verifies_p"] = m.verifies(e) and "truncated" not in e["status"]
        known = configured is not None or channel is not None
        try:
            if h["version"] < 0x402 or h["version"] > 0x410 or f & MONO_DATA == MONO_DATA:
                raise Refused("version or flags")
            Contents(block, f, h["samples"], h["version"], known)
            e["refused"] = False
        except Refused:
            e["refused"] = True
        e["unreadable"] = "truncated" in e["status"] or bool(e["left"]) or any(clipped for *_, clipped in e["sub_blocks"]) or e["refused"]
        if configured is None and channel is None and e["verifies_p"] and m.has(e, 0x0D):
            found = m.channel_info(e, data)
            if found:
                channel = found
        if configured is None and e["verifies_p"] and h["samples"]:
            configured = e
            e["channel_at_config"] = channel
        if not h["samples"]:
            continue
        streams = channel[2] if channel else DEFAULT_STREAMS
        st = sets_state
        if f & m.INITIAL or not st["open"]:
            close()
            if not f & m.INITIAL:
                broken += 1
            st.update(open=True, final=False, samples=h["samples"], index=h["index"], blocks=0, channels=0)
            if first_index is None:
                first_index = h["index"]
                sets = 0
            if next_index >= 0 and h["index"] != next_index:
                has_gap = True
                gap_before.add(e["offset"])
        elif h["index"] != st["index"] or h["samples"] != st["samples"]:
            broken += 1
        st["blocks"] += 1
        st["channels"] += 1 if f & MONO_FLAG else 2
        if f & m.FINAL:
            st["final"] = True
            close()
        elif st["blocks"] >= streams:
            close()
    close()
    return {"blocks": blocks, "configured": configured, "channel": channel, "counted": counted, "broken": broken, "has_gap": has_gap,
            "gap_before": gap_before, "first_index": first_index if first_index is not None else -1}


def expectation(data, start, end):
    """What the analyzer reports for the file with and without decoding: None without a stream, else a dictionary with the verification
    of both ('default', 'verify'), the decoder's counts and the decode findings."""
    entries = m.walk(data, start, end)
    info = m.stream_info(data, entries)
    if info is None:
        return None
    fx = facts(entries, data)
    blocks, configured = fx["blocks"], fx["configured"]
    audio = [e for e in blocks if e["header"]["samples"]]
    declared = declared_total(entries)
    correction = info["correction"]
    unread = sum(e["unreadable"] for e in audio)
    matches = declared is None or declared == fx["counted"]
    covers_all = not correction and bool(audio) and not fx["has_gap"] and matches and unread == 0 and fx["broken"] == 0
    valid = sum(e["checksum"] == "valid" for e in blocks)
    invalid = sum(e["checksum"] == "invalid" for e in blocks)
    missing = sum(e["checksum"] is None and bool(e["header"]["flags"] & 0x10000000) and "truncated" not in e["status"] for e in blocks)
    checksummed = sum(e["checksum"] is not None for e in audio)
    wvx = sum(1 for e in blocks if m.has(e, 0x0C))
    md5_stored = 0
    for e in blocks:
        for i, p, hl, size, length, clipped in e["sub_blocks"]:
            if i & 0x3F in (0x26, 0x29) and not clipped and length == 16:
                md5_stored = 1
    stored = valid + invalid + missing
    tallies = [
        {"name": "block checksum", "covers": "codedData", "coversAllAudio": covers_all and checksummed == len(audio), "stored": stored, "checked": stored, "failed": invalid + missing},
        {"name": "block header CRC", "covers": "decodedAudio", "coversAllAudio": covers_all, "stored": len(audio), "checked": 0, "failed": 0},
        {"name": "extended CRC", "covers": "decodedAudio", "coversAllAudio": False, "stored": wvx, "checked": 0, "failed": 0},
        {"name": "MD5", "covers": "decodedAudio", "coversAllAudio": True, "stored": md5_stored, "checked": 0, "failed": 0},
    ]
    upto = blocks[:blocks.index(configured) + 1] if configured is not None else []
    is_lossy = configured is not None and (bool(configured["header"]["flags"] & HYBRID_FLAG) or lossy_blocks(upto, data))
    default = verification("notRequested", [dict(t) for t in tallies], is_lossy)
    outcome = run_decoder(data, fx, info, [dict(t) for t in tallies], declared, is_lossy)
    verify = verification(outcome["decoding"], outcome["tallies"], is_lossy)
    result = {"default": default, "verify": verify, "decode_findings": outcome["findings"], "decoded_samples": outcome["decoded"], "pcm_samples": outcome["pcm_samples"],
              "decoded_pcm_md5": outcome["pcm_md5"], "decoded_dsd_md5": outcome["dsd_md5"]}
    return result


def verification(decoding, tallies, is_lossy):
    """The verdict by the rule of docs/design/decoders.md (StreamVerification.IsVerified)."""
    if any(t["failed"] for t in tallies):
        verified = False
    elif any(t["stored"] - t["checked"] > 0 for t in tallies):
        verified = None
    elif any(t["coversAllAudio"] and t["checked"] > 0 for t in tallies):
        verified = True
    else:
        verified = None
    result = {"isVerified": verified, "decoding": decoding, "checksums": tallies}
    if is_lossy:
        result["isLossy"] = True
    return result


def run_decoder(data, fx, info, tallies, declared, is_lossy):
    blocks, configured = fx["blocks"], fx["configured"]
    out = {"decoding": "notPossible", "tallies": tallies, "decoded": 0, "pcm_samples": 0, "pcm_md5": None, "dsd_md5": None, "findings": [{"code": "decode.not-possible", "count": 1}]}
    if configured is None:
        return out
    h0 = configured["header"]
    block0 = block_bytes(data, configured)
    if h0["version"] < 0x402 or h0["version"] > 0x410:
        return out
    try:
        c0 = Contents(block0, h0["flags"], h0["samples"], h0["version"], False)
        if h0["flags"] & m.DSD and c0.dsd[1] == 1:
            fast_tables(c0.dsd)
        elif h0["flags"] & m.DSD and c0.dsd[1] == 3:
            high_tables(c0.dsd, bool(h0["flags"] & MONO_DATA))
    except Refused:
        return out
    channel = configured["channel_at_config"]
    channels = channel[0] if channel else (1 if h0["flags"] & MONO_FLAG else 2)
    run = Run(data, fx, info, channels, h0, declared, is_lossy)
    run.go()
    return run.report(tallies)


class Run:
    """What the analyzer reports of a stream it decodes, worked out from libwavpack's WavpackUnpackSamples (unpack_utils.c), unpack.c and open_utils.c.

    The host's work budget, which ends the decoding of a crafted file of blocks that never complete, is not modeled: no fixture reaches it."""

    def __init__(self, data, fx, info, channels, h0, declared, is_lossy):
        self.data, self.fx, self.channels, self.declared, self.is_lossy = data, fx, channels, declared, is_lossy
        self.is_dsd = bool(h0["flags"] & m.DSD)
        self.is_float = bool(h0["flags"] & FLOAT)
        self.bps = 1 if self.is_dsd else (h0["flags"] & 3) + 1
        self.info = None
        self.max_streams = DEFAULT_STREAMS
        self.qmode = 0
        self.layout_channels = 0
        self.reordering = None
        self.frozen = False
        self.md5 = None
        self.saw_hybrid = False
        self.crc = [0, 0]
        self.extra = [0, 0]
        self.digest_incomplete = False
        self.streams = None
        self.digest = None
        self.findings = {}
        self.decoded = 0
        self.not_decoded = 0
        self.saw_gap = False
        self.after_unreadable = False
        self.pcm = bytearray()
        self.dsd = bytearray()
        self.pcm_samples = 0
        self.oversize = False
        self.has_audio = False

    def add(self, code, count=1):
        self.findings[code] = self.findings.get(code, 0) + count

    def go(self):
        blocks, configured = self.fx["blocks"], self.fx["configured"]
        held, held_bytes, started = [], 0, False
        gap_next = False
        for e in blocks:
            audio = bool(e["header"]["samples"])
            if audio and e["offset"] in self.fx["gap_before"]:
                gap_next = True
            follows_gap = audio and gap_next
            if audio:
                gap_next = False
            if not started:
                if not audio:
                    if len(held) < MAX_HELD and held_bytes + e["length"] <= MAX_HELD_BYTES:
                        held.append(e)
                        held_bytes += e["length"]
                    continue
                if e is not configured:
                    continue
                started = True
                for h in held:
                    self.hand(h, False, True)
                held = []
            self.hand(e, follows_gap, False)

    def hand(self, e, follows_gap, is_held):
        unreadable = e["unreadable"]
        if self.after_unreadable:
            follows_gap = True
            self.after_unreadable = False
        self.saw_gap |= follows_gap
        if unreadable:
            self.not_decoded += 1
            self.after_unreadable = True
            return
        try:
            self.decode(e, follows_gap)
        except FrameInvalid:
            self.not_decoded += 1
            self.add("decode.frame-invalid")

    def abandon(self):
        # A set whose final block never comes is read on past by WavpackUnpackSamples (unpack_utils.c 132-133 loop to the next
        # block that starts a set); its samples are missing.
        if self.streams is not None:
            self.streams = None
            self.digest_incomplete = True

    def decode(self, e, follows_gap):
        h = e["header"]
        flags = h["flags"]
        block = block_bytes(self.data, e)
        if follows_gap:
            self.abandon()
        if h["version"] < 0x402 or h["version"] > 0x410:
            raise FrameInvalid
        try:
            c = Contents(block, flags, h["samples"], h["version"], self.info is not None)
        except Refused:
            raise FrameInvalid
        if self.info is None and c.channel_info:
            self.info = c.channel_info
            self.max_streams = c.channel_info[2]
        if not self.frozen and c.new_config:
            self.qmode = c.new_config["qmode"]
            self.layout_channels = c.new_config["layout_channels"]
            self.reordering = c.new_config["reordering"]
        if c.md5:
            self.md5 = c.md5
        if h["samples"]:
            self.saw_hybrid |= bool(flags & HYBRID_FLAG)
        if not h["samples"]:
            return
        self.frozen = True

        # WavpackUnpackSamples (unpack_utils.c 132-133) goes on to the next block when this one does not start a set; a block
        # that does (INITIAL_BLOCK) starts one whose samples_to_unpack is its own block_samples (unpack_utils.c 169).
        if flags & m.INITIAL:
            self.abandon()
            self.streams = {"index": h["index"], "samples_to_unpack": h["samples"], "offset": 0, "current_stream": 0, "failed": False,
                            "too_large": False, "planes": []}
        elif self.streams is None:
            return
        wpc = self.streams
        num_channels = self.channels
        wpc["current_stream"] += 1
        block_channels = 1 if flags & MONO_FLAG else 2

        # A block of another kind than the first one, or a stereo block that leaves no room for two channels: libwavpack copies one
        # channel and counts a crc_errors (unpack_utils.c 293-301) or unpacks DSD as PCM; this decoder refuses the block.
        if bool(flags & m.DSD) != self.is_dsd or wpc["offset"] + block_channels > num_channels:
            wpc["failed"] = True
            raise FrameInvalid
        sample_count = wpc["samples_to_unpack"]

        # unpack.c 89 and unpack_dsd.c 74 mute the block when GET_BLOCK_INDEX (wphdr) > sample_index || block_samples < sample_count.
        # unpack_init sets sample_index from every block's own header (open_utils.c 357), so for a block of a set the first term never
        # holds, and a block is muted when it has fewer samples than the set's first.
        mute_error = h["samples"] < sample_count
        if (wpc["offset"] + block_channels) * sample_count > MAX_SET_SAMPLES:
            wpc["too_large"] = True
        width = 1 if flags & MONO_DATA else 2
        has_extra, extra_ok = False, True
        try:
            if mute_error:
                # unpack.c 91-100: the buffer is zeroed (0x55 for DSD, unpack_dsd.c 93-104) and sample_index advanced; check_crc_error
                # then compares the CRC the muted block was left with (0xffffffff, open_utils.c 301 initial value) with the header's.
                crc_ok = MASK32 == h["crc"]
                decoded = [0x55 if self.is_dsd else 0] * (sample_count * width)
            elif self.is_dsd:
                decoded, _, crc = unpack_dsd(h, c, sample_count)
                crc_ok = crc == h["crc"]
            else:
                decoded, muted_now, crc, extra_crc = unpack_pcm(block, h, c, sample_count)
                crc_ok = crc == h["crc"]
                has_extra = c.extra is not None and not muted_now
                extra_ok = extra_crc == c.extra_crc if has_extra else True
        except Refused:
            wpc["failed"] = True
            raise FrameInvalid
        # check_crc_error (unpack_utils.c 376-399) at the end of every block; libwavpack then zeroes the block, this decoder does not.
        self.crc[0] += 1
        if not crc_ok:
            self.crc[1] += 1
            self.add("wavpack.audio-crc-mismatch")
        if has_extra:
            self.extra[0] += 1
            if not extra_ok:
                self.extra[1] += 1
                self.add("wavpack.audio-crc-mismatch")
        if not wpc["too_large"]:
            if width == 1:
                wpc["planes"].append(list(decoded[:sample_count]))
                if block_channels == 2:
                    wpc["planes"].append(list(decoded[:sample_count]))
            else:
                wpc["planes"].append(decoded[0::2][:sample_count])
                wpc["planes"].append(decoded[1::2][:sample_count])
        wpc["offset"] += block_channels
        # unpack_utils.c 318: the set ends at a FINAL_BLOCK, at the last stream max_streams allows, or when the channels are all there.
        if not flags & m.FINAL and wpc["current_stream"] != self.max_streams and wpc["offset"] != num_channels:
            return
        self.complete()

    def complete(self):
        wpc = self.streams
        self.streams = None
        samples = wpc["samples_to_unpack"]
        if wpc["failed"]:
            self.digest_incomplete = True
            return
        # unpack_utils.c 326-337: the channels did not add up, so the buffer is muted and an error counted.
        if wpc["offset"] != self.channels:
            self.digest_incomplete = True
            raise FrameInvalid
        if wpc["too_large"]:
            self.digest_incomplete = True
            self.oversize = True
            self.decoded += samples
            return
        if self.is_dsd:
            # The bytes wvunpack --raw writes for DSD: the channels' bytes interleaved.
            for i in range(samples):
                for plane in wpc["planes"]:
                    self.dsd.append(plane[i] & 0xFF)
        else:
            self.pcm_samples += samples
            self.pcm += self.canonical(wpc["planes"], samples)
        if not self.is_lossy and not self.saw_hybrid:
            if self.digest is None:
                order = self.reordering if self.qmode & 8 and self.reordering and self.layout_channels <= self.channels else None
                self.digest = Digest(self.channels, self.bps, self.qmode, self.is_dsd, order)
            self.digest.append(wpc["planes"], samples)
        self.decoded += samples

    def canonical(self, planes, samples):
        """The canonical layout of docs/design/decoders.md: interleaved, signed, little-endian, the container's bytes per sample."""
        bps = 4 if self.is_float else self.bps
        out = bytearray()
        mask = (1 << 8 * bps) - 1
        for i in range(samples):
            for plane in planes:
                out += (plane[i] & mask).to_bytes(bps, "little")
        return bytes(out)

    def report(self, tallies):
        for t in tallies:
            if t["name"] == "block header CRC":
                t["checked"], t["failed"] = self.crc
            elif t["name"] == "extended CRC" and self.extra[0]:
                t["checked"], t["failed"] = self.extra
        self.abandon()
        complete = not self.not_decoded and not self.saw_gap and (self.declared is None or self.declared == self.decoded)
        if self.md5 is not None:
            if self.oversize:
                self.add("wavpack.md5-set-too-large")
            if self.is_lossy or self.saw_hybrid:
                if complete:
                    self.add("wavpack.md5-needs-correction-file")
            elif complete and not self.digest_incomplete and self.digest is not None:
                computed = bytes.fromhex(self.digest.hexdigest())
                for t in tallies:
                    if t["name"] == "MD5":
                        t["checked"] = 1
                        t["failed"] = 0 if computed == self.md5[0] else 1
                if computed != self.md5[0]:
                    self.add("wavpack.md5-mismatch")
        pcm_md5 = None if self.is_dsd else hashlib.md5(bytes(self.pcm)).hexdigest()
        dsd_md5 = hashlib.md5(bytes(self.dsd)).hexdigest() if self.is_dsd and self.dsd else None
        return {"decoding": "decoded", "tallies": tallies, "decoded": self.decoded, "pcm_samples": 0 if self.is_dsd else self.pcm_samples, "pcm_md5": pcm_md5, "dsd_md5": dsd_md5,
                "findings": [{"code": code, "count": count} for code, count in sorted(self.findings.items())]}
