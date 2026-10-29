"""The deterministic inputs the Monkey's Audio fixtures are encoded from: two tones under a slow tremolo, then noise (as the WavPack and FLAC fixtures
use), and the variants that give the format its cases: digital silence (which the encoder stores as special frames), identical channels (pseudo
stereo), a silent left channel, a quiet tone for the frames of the highest level, and loud noise.

The noise comes from a fixed linear congruential generator, so every run writes the same bytes. The samples are also what the decoder must give
back, so the manifests' decoded_pcm_md5 is worked out from them and not from any decoder.
"""

import math
import struct


def samples(kind, rate, channels, count, bits):
    """count frames of channels integers each, in the range of bits (signed)."""
    scale = 1 << (bits - 1)
    state = 12345
    for i in range(count):
        t = i / rate
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        frame = []
        for channel in range(channels):
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
            noise = (((state >> 8) & 0x3FFF) - 0x2000) / 32768
            envelope = 0.25 * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
            tone = envelope * (math.sin(2 * math.pi * 300 * t) + 0.5 * math.sin(2 * math.pi * (1100 + 600 * channel) * t + 0.3))
            if kind == "music":
                value = tone if i < count * 2 // 3 else noise
            elif kind == "quiet":
                # A triangle wave that moves one step a sample: the predictors follow it almost exactly, so every sample costs about the least the
                # range coder spends on one (1.74 bits, Monkey's Audio SDK 13.44, UnBitArrayBase.h, RANGE_WIDTH_2), and frames of a million samples
                # code in a few hundred kilobytes.
                phase = (i + 500 * channel) % 2000
                value = ((phase if phase < 1000 else 2000 - phase) - 500) / scale
            elif kind == "silent":
                value = 0
            elif kind == "silent-then-tone":
                value = tone if i >= 80000 else 0
            elif kind == "pseudo":
                value = tone if channel == 0 else None
            elif kind == "left-silent":
                value = 0 if channel == 0 else tone
            elif kind == "loud-sine-noise":
                # A sine and a full-rate square wave at three tenths of full scale each, with a little noise: the first stage of the predictor sums
                # products of values the high-pass filter makes large with coefficients of hundreds, which at 24 bits overflow 32 bits.
                value = 0.3 * math.sin(2 * math.pi * (220 + 110 * channel) * t) + (0.3 if (i + channel) % 2 else -0.3) + noise * 0.1
            elif kind == "loud":
                value = (state >> 3 & 0xFFFF) / 32768 - 1
            else:
                raise ValueError(kind)
            if value is None:
                frame.append(frame[0])
                continue
            v = int(value * scale)
            frame.append(max(-scale, min(scale - 1, v)))
        yield frame


def pcm(kind, rate, channels, count, bits, big_endian=False):
    """Little-endian (or big-endian) signed PCM of bits; 8 bits are unsigned, as WAVE has them."""
    out = bytearray()
    for frame in samples(kind, rate, channels, count, bits):
        for value in frame:
            if bits == 8:
                out.append(value + 128)
            else:
                out += value.to_bytes(bits // 8, "big" if big_endian else "little", signed=True)
    return bytes(out)


def floats(kind, rate, channels, count):
    out = bytearray()
    for frame in samples(kind, rate, channels, count, 25):
        for value in frame:
            out += struct.pack("<f", value / (1 << 24))
    return bytes(out)


def canonical(kind, rate, channels, count, bits, is_float=False):
    """The decoded audio in the canonical layout (interleaved, signed, little-endian, whole bytes; floats as their bytes), which the manifests hash."""
    if is_float:
        return floats(kind, rate, channels, count)
    out = bytearray()
    for frame in samples(kind, rate, channels, count, bits):
        for value in frame:
            out += value.to_bytes(bits // 8, "little", signed=True)
    return bytes(out)


def chunk(name, payload):
    """A RIFF chunk, padded to an even length."""
    return name + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) % 2 else b"")


def wave_format(channels, rate, bits, mask, is_float=False):
    """The fmt chunk's data: the 16-byte format of tag 1 for the mask "plain" (or no mask with at most two channels of at most 16 bits), otherwise a
    WAVEFORMATEXTENSIBLE with the mask (the default one of mono and stereo, else 0)."""
    align = channels * bits // 8
    if mask == "plain" or (mask is None and channels <= 2 and bits <= 16 and not is_float):
        return struct.pack("<HHIIHH", 1, channels, rate, rate * align, align, bits)
    if mask is None:
        mask = {1: 4, 2: 3}.get(channels, 0)
    sub = struct.pack("<IHH", 3 if is_float else 1, 0, 0x10) + bytes([0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71])
    return struct.pack("<HHIIHHHHI", 0xFFFE, channels, rate, rate * align, align, bits, 22, bits, mask) + sub


def wav(kind, rate, channels, count, bits, mask=None, is_float=False, before=b"", after=b""):
    """A WAVE file; with a channel mask (or more than 2 channels, or more than 16 bits) the format is WAVE_FORMAT_EXTENSIBLE, except for the mask "plain",
    which writes the 16-byte format of tag 1 whatever the channel count. before and after are chunks put before the data chunk and after it."""
    body = floats(kind, rate, channels, count) if is_float else pcm(kind, rate, channels, count, bits)
    fmt = wave_format(channels, rate, bits, mask, is_float)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + before + b"data" + struct.pack("<I", len(body)) + body + (b"\0" if len(body) % 2 else b"") + after
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks


def bw64(kind, rate, channels, count, bits, mask=None):
    """A BW64 file (ITU-R BS.2088): the ds64 chunk first, with the sizes of the file and of the data, which the 32-bit fields of the BW64 and the data
    chunk leave at 0xFFFFFFFF."""
    body = pcm(kind, rate, channels, count, bits)
    fmt = wave_format(channels, rate, bits, mask)
    rest = chunk(b"fmt ", fmt) + b"data" + struct.pack("<I", 0xFFFFFFFF) + body
    ds64 = struct.pack("<QQQI", 4 + 8 + 28 + len(rest), len(body), 0, 0)
    return b"BW64" + struct.pack("<I", 0xFFFFFFFF) + b"WAVE" + chunk(b"ds64", ds64) + rest


W64_RIFF = bytes.fromhex("726966662E91CF11A5D628DB04C10000")
W64_WAVE = bytes.fromhex("77617665F3ACD3118CD100C04F8EDB8A")
W64_TEXT_TAIL = bytes.fromhex("F3ACD3118CD100C04F8EDB8A")


def w64_chunk(name, payload):
    """A Wave64 chunk: the GUID of a four-character name, the 64-bit size that counts the 24 bytes of header, the data, padded to a multiple of 8 bytes."""
    return name + W64_TEXT_TAIL + struct.pack("<Q", 24 + len(payload)) + payload + b"\0" * (-len(payload) % 8)


def w64(kind, rate, channels, count, bits, mask=None):
    """A Wave64 file (Sony): the riff GUID with the size of the whole file, the wave GUID, and the fmt and data chunks."""
    body = pcm(kind, rate, channels, count, bits)
    chunks = w64_chunk(b"fmt ", wave_format(channels, rate, bits, mask)) + w64_chunk(b"data", body)
    return W64_RIFF + struct.pack("<Q", 40 + len(chunks)) + W64_WAVE + chunks


def aiff(kind, rate, channels, count, bits):
    """An AIFF file: big-endian signed PCM, the rate as an 80-bit extended float."""
    body = pcm(kind, rate, channels, count, bits, big_endian=True)
    exponent = rate.bit_length() - 1
    extended = struct.pack(">HQ", 0x3FFF + exponent, rate << (63 - exponent))
    comm = struct.pack(">hIh", channels, count, bits) + extended
    chunks = b"COMM" + struct.pack(">I", len(comm)) + comm + b"SSND" + struct.pack(">I", 8 + len(body)) + b"\0" * 8 + body
    return b"FORM" + struct.pack(">I", 4 + len(chunks)) + b"AIFF" + chunks


def caf(kind, rate, channels, count, bits, layout_tag):
    """A Core Audio Format file with big-endian signed PCM and a channel layout."""
    body = pcm(kind, rate, channels, count, bits, big_endian=True)
    desc = struct.pack(">dIIIIII", float(rate), 0x6C70636D, 0, channels * bits // 8, 1, channels, bits)
    chan = struct.pack(">III", layout_tag, 0, 0)
    chunks = b"desc" + struct.pack(">q", len(desc)) + desc
    chunks += b"chan" + struct.pack(">q", len(chan)) + chan
    chunks += b"data" + struct.pack(">q", 4 + len(body)) + struct.pack(">I", 1) + body
    return b"caff" + struct.pack(">HH", 1, 0) + chunks
