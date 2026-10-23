"""The deterministic inputs the WavPack fixtures are encoded from: two tones under a slow tremolo,
then noise, as the Musepack fixtures use, written as WAVE (integer, float and extensible), AIFF,
Core Audio Format and DSF files.

The noise comes from a fixed linear congruential generator, so every run writes the same bytes. The
tones give the decorrelation passes something to predict and the noise something they cannot, so
blocks differ in size.
"""

import math
import struct


def samples(rate, channels, count, scale):
    """count frames of channels values each, integers in [-scale, scale - 1]."""
    state = 12345
    for i in range(count):
        t = i / rate
        noise = i >= count * 2 // 3
        if noise:
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        frame = []
        for channel in range(channels):
            if noise:
                value = (((state >> (8 + channel)) & 0x3FFF) - 0x2000) / 32768
            else:
                envelope = 0.25 * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
                value = envelope * (math.sin(2 * math.pi * 300 * t) + 0.5 * math.sin(2 * math.pi * (1100 + 600 * channel) * t + 0.3))
            frame.append(max(-scale, min(scale - 1, int(value * scale))))
        yield frame


def pcm(rate, channels, count, bits, big_endian=False):
    """Little-endian (or big-endian) signed PCM of bits (8 is unsigned, as WAVE has it)."""
    out = bytearray()
    for frame in samples(rate, channels, count, 1 << (bits - 1)):
        for value in frame:
            if bits == 8:
                out.append(value + 128)
            else:
                out += value.to_bytes(bits // 8, "big" if big_endian else "little", signed=True)
    return bytes(out)


def pcm_identical(rate, count, bits):
    """Stereo PCM whose two channels are the same, the first channel of the signal twice."""
    out = bytearray()
    for (value,) in samples(rate, 1, count, 1 << (bits - 1)):
        out += (value + 128 if bits == 8 else value).to_bytes(1 if bits == 8 else bits // 8, "little", signed=bits != 8) * 2
    return bytes(out)


def floats(rate, channels, count):
    out = bytearray()
    for frame in samples(rate, channels, count, 1 << 24):
        for value in frame:
            out += struct.pack("<f", value / (1 << 24))
    return bytes(out)


def wav_extremes(rate, channels, count, bits):
    """WAVE whose every sample is the lowest or the highest value of the sample size, chosen by the fixed generator."""
    top = (1 << (bits - 1)) - 1
    state = 777
    body = bytearray()
    for i in range(count):
        for channel in range(channels):
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
            body += (top if (state >> 16) & 1 else -top - 1).to_bytes(bits // 8, "little", signed=True)
    width = bits // 8
    fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * channels * width, channels * width, bits)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(body)) + bytes(body)
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks


def wav(rate, channels, count, bits, mask=None, is_float=False, before=b"", after=b""):
    """A WAVE file; with a channel mask (or more than 2 channels, or more than 16 bits) the format is
    WAVE_FORMAT_EXTENSIBLE. before and after are chunks put before the data chunk and after it."""
    body = floats(rate, channels, count) if is_float else pcm(rate, channels, count, bits)
    align = channels * bits // 8
    if mask is None and channels <= 2 and bits <= 16:
        fmt = struct.pack("<HHIIHH", 3 if is_float else 1, channels, rate, rate * align, align, bits)
    else:
        if mask is None:
            # Mono and stereo files take the common positions: front center, and front left and right.
            mask = {1: 4, 2: 3}.get(channels, 0)
        sub = struct.pack("<IHH", 3 if is_float else 1, 0, 0x10) + bytes([0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71])
        fmt = struct.pack("<HHIIHHHHI", 0xFFFE, channels, rate, rate * align, align, bits, 22, bits, mask) + sub
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + before + b"data" + struct.pack("<I", len(body)) + body + after
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks


def chunk(name, payload):
    """A RIFF chunk, padded to an even length."""
    return name + struct.pack("<I", len(payload)) + payload + (b"\0" if len(payload) % 2 else b"")


def aiff(rate, channels, count, bits):
    """An AIFF file: big-endian signed PCM, the rate as an 80-bit extended float."""
    body = pcm(rate, channels, count, bits, big_endian=True)
    exponent = rate.bit_length() - 1
    extended = struct.pack(">HQ", 0x3FFF + exponent, rate << (63 - exponent))
    comm = struct.pack(">hIh", channels, count, bits) + extended
    chunks = b"COMM" + struct.pack(">I", len(comm)) + comm + b"SSND" + struct.pack(">I", 8 + len(body)) + b"\0" * 8 + body
    return b"FORM" + struct.pack(">I", 4 + len(chunks)) + b"AIFF" + chunks


def caf(rate, channels, count, bits, layout_tag):
    """A Core Audio Format file with big-endian signed PCM and a channel layout."""
    body = pcm(rate, channels, count, bits, big_endian=True)
    desc = struct.pack(">dIIIIII", float(rate), 0x6C70636D, 0, channels * bits // 8, 1, channels, bits)
    chan = struct.pack(">III", layout_tag, 0, 0)
    chunks = b"desc" + struct.pack(">q", len(desc)) + desc
    chunks += b"chan" + struct.pack(">q", len(chan)) + chan
    chunks += b"data" + struct.pack(">q", 4 + len(body)) + struct.pack(">I", 1) + body
    return b"caff" + struct.pack(">HH", 1, 0) + chunks


def dsf(rate, channels, bytes_per_channel):
    """A DSF file: DSD bytes least significant bit first, in blocks of 4096 bytes per channel. The
    bits come from a first-order sigma-delta modulator over the tones, so the bytes are not noise."""
    block = 4096
    blocks = -(-bytes_per_channel // block)
    integrator = [0.0] * channels
    bits = [[] for _ in range(channels)]
    for i in range(bytes_per_channel * 8):
        t = i / rate
        for c in range(channels):
            target = 0.4 * math.sin(2 * math.pi * (300 + 300 * c) * t) * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
            bit = 1 if integrator[c] >= 0 else 0
            integrator[c] += target - (1.0 if bit else -1.0)
            bits[c].append(bit)
    data = bytearray()
    for b in range(blocks):
        for c in range(channels):
            for byte in range(block):
                index = (b * block + byte) * 8
                data.append(sum(bit << k for k, bit in enumerate(bits[c][index:index + 8])))
    fmt = struct.pack("<IIIIIIQII", 1, 0, 2 if channels == 2 else 1, channels, rate, 1, bytes_per_channel * 8, block, 0)
    fmt_chunk = b"fmt " + struct.pack("<Q", 12 + len(fmt)) + fmt
    data_chunk = b"data" + struct.pack("<Q", 12 + len(data)) + bytes(data)
    total = 28 + len(fmt_chunk) + len(data_chunk)
    return b"DSD " + struct.pack("<QQQ", 28, total, 0) + fmt_chunk + data_chunk
