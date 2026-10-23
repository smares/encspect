"""The deterministic inputs the FLAC fixtures are encoded from: tones under a slow tremolo whose channels are alike (so stereo decorrelation
pays), then noise that is partly the same in every channel, as the WavPack fixtures use; and the variants that give each part of the format
its case: silence, loud noise, an opposed pair of full-scale channels for the 33-bit side channel, and samples whose low bits are zero.

The noise comes from a fixed linear congruential generator, so every run writes the same bytes.
"""

import math
import struct


def samples(kind, rate, channels, count, bits, shift=0):
    """count frames of channels integers each, in the range of bits (signed)."""
    scale = 1 << (bits - 1)
    state = 12345
    for i in range(count):
        t = i / rate
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        common = ((state >> 8) & 0x3FFF) - 0x2000
        frame = []
        for channel in range(channels):
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
            own = ((state >> 8) & 0x3FFF) - 0x2000
            envelope = 0.25 * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
            tone = envelope * (math.sin(2 * math.pi * 300 * t) + 0.3 * math.sin(2 * math.pi * (1100 + 300 * channel) * t + 0.3))
            if kind == "music":
                value = tone if i < count * 2 // 3 else (0.7 * common + 0.3 * own) / 32768
            elif kind == "loud":
                value = (state >> 3 & 0xFFFF) / 32768 - 1
            elif kind == "silent":
                value = tone if count // 3 <= i < count * 2 // 3 else 0
            elif kind == "opposed":
                value = 0.99 * math.sin(2 * math.pi * 200 * t) * (1 if channel == 0 else -1) if i < count * 2 // 3 else (0.99 if channel == 0 else -1.0) * ((common + 0x2000) / 0x4000 * 2 - 1)
            else:
                raise ValueError(kind)
            v = int(value * scale)
            v = max(-scale, min(scale - 1, v))
            if shift:
                v = (v >> shift) << shift
            frame.append(v)
        yield frame


def pcm(kind, rate, channels, count, bits, shift=0):
    out = bytearray()
    for frame in samples(kind, rate, channels, count, bits, shift):
        for value in frame:
            out += (value + 128 if bits == 8 else value).to_bytes(1 if bits == 8 else (bits + 7) // 8, "little", signed=bits != 8)
    return bytes(out)


def wav(kind, rate, channels, count, bits, valid_bits=None, mask=None, shift=0):
    """A WAVE file; with more than 2 channels or more than 16 bits, or a valid bit depth smaller than the container, WAVE_FORMAT_EXTENSIBLE."""
    width = (bits + 7) // 8 * 8
    body = pcm(kind, rate, channels, count, bits, shift)
    if width != bits:
        # Samples of fewer bits than their container are left-aligned in it.
        body = bytearray()
        for frame in samples(kind, rate, channels, count, bits, shift):
            for value in frame:
                body += (value << (width - bits)).to_bytes(width // 8, "little", signed=True)
        body = bytes(body)
    align = channels * width // 8
    if mask is None and channels <= 2 and width <= 16 and valid_bits is None:
        fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * align, align, width)
    else:
        if mask is None:
            mask = {1: 4, 2: 3, 3: 7, 4: 0x33, 5: 0x37, 6: 0x3F, 7: 0x13F, 8: 0x63F}.get(channels, 0)
        sub = struct.pack("<IHH", 1, 0, 0x10) + bytes([0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71])
        fmt = struct.pack("<HHIIHHHHI", 0xFFFE, channels, rate, rate * align, align, width, 22, valid_bits or bits, mask) + sub
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(body)) + body
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks
