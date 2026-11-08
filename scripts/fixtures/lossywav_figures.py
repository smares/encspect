#!/usr/bin/env python3
"""The prototype of the per-block low-bit figures of docs/design/lossywav-detection.md, over integer PCM in WAV files,
and the writer of the false-positive variants the design measured. Standard library only; it reads files as data.

The figures, per stream, in one pass and bounded memory:
- blocks of 32 samples per channel, anchored at the stream's first sample; a block's count is the number of low bits
  that are zero in all its samples, and a block whose samples are all 0 has none;
- Low Bits Unused: the smallest count over the stream (the levels' figure: the trailing zeros of the OR of every sample);
- the distribution of the counts over the blocks of every channel;
- the changes: the blocks whose count differs from the channel's last block with a count;
- the block grid: the largest power of two from 256 to 65,536 samples such that at least 95% of the changes fall within
  one block of 32 samples of one phase of it, given at least 20 changes; else none;
- the share of times (blocks with a count in every channel) at which all channels have the same count.

Usage: lossywav_figures.py FILE.wav [...]                prints one JSON line of figures per file
       lossywav_figures.py --variants OUT SRC16.wav SRC24.wav LOSSY24.wav LOSSY16.wav
                                                          writes the false-positive variants into OUT
"""

import json
import pathlib
import random
import struct
import sys

CELL = 32
BINS = 2048
MIN_CHANGES = 20
MIN_GRID = 256
SHARE = 0.95


def read_wav(path):
    data = pathlib.Path(path).read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"{path}: not a RIFF WAVE file")
    position, fmt, body, chunks = 12, None, None, []
    while position + 8 <= len(data):
        cid, size = data[position:position + 4], struct.unpack_from("<I", data, position + 4)[0]
        chunks.append((cid.decode("latin-1"), size))
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", data, position + 8)
        elif cid == b"data":
            body = data[position + 8:position + 8 + size]
        position += 8 + size + (size & 1)
    _, channels, rate, _, align, bits = fmt
    width = align // channels
    frames = len(body) // align
    if width == 1:
        values = [b - 128 for b in body[:frames * align]]
    elif width == 3:
        values = [int.from_bytes(body[i:i + 3], "little", signed=True) for i in range(0, frames * align, 3)]
    else:
        values = list(struct.unpack(f"<{frames * channels}{'h' if width == 2 else 'i'}", body[:frames * align]))
    return {"rate": rate, "channels": channels, "bits": bits, "frames": frames, "values": values, "chunks": chunks}


def trailing_zeros(value):
    return (value & -value).bit_length() - 1


def figures(path):
    wav = read_wav(path)
    channels, values, bits = wav["channels"], wav["values"], wav["bits"]
    mask = (1 << bits) - 1
    frames = wav["frames"]
    histogram = [0] * (bits + 1)
    phase = [0] * BINS
    changes, whole, agree, rows = 0, 0, 0, 0
    last = [None] * channels
    for cell in range(0, (frames + CELL - 1) // CELL):
        counts = []
        for channel in range(channels):
            o = 0
            for i in range(cell * CELL, min(frames, (cell + 1) * CELL)):
                o |= values[i * channels + channel] & mask
            whole |= o
            if o == 0:
                counts.append(None)
                continue
            z = trailing_zeros(o)
            counts.append(z)
            histogram[z] += 1
            if last[channel] is not None and last[channel] != z:
                changes += 1
                phase[cell % BINS] += 1
            last[channel] = z
        if channels > 1 and None not in counts:
            rows += 1
            agree += len(set(counts)) == 1
    grid, on_grid = None, None
    if changes >= MIN_CHANGES:
        length = BINS * CELL
        while length >= MIN_GRID:
            n = length // CELL
            folded = [sum(phase[k::n]) for k in range(n)]
            m = max(range(n), key=folded.__getitem__)
            hits = folded[m] + folded[(m - 1) % n] + folded[(m + 1) % n]
            if hits >= SHARE * changes:
                grid, on_grid = length, hits
                break
            length //= 2
    return {"file": str(path), "rate": wav["rate"], "bits": bits, "channels": channels, "chunks": wav["chunks"],
            "lowBitsUnused": trailing_zeros(whole) if whole else None,
            "blocks": sum(histogram), "distribution": {z: n for z, n in enumerate(histogram) if n},
            "changes": changes, "grid": grid, "changesOnGrid": on_grid,
            "timesWithEqualCounts": agree if channels > 1 else None, "times": rows if channels > 1 else None}


def write_wav(path, frames, channels, rate, bits):
    lo, hi = -(1 << (bits - 1)), (1 << (bits - 1)) - 1
    width = (bits + 7) // 8
    body = bytearray()
    for frame in frames:
        for v in frame:
            body += max(lo, min(hi, v)).to_bytes(width, "little", signed=True)
    if channels <= 2 and bits <= 16:
        fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * channels * width, channels * width, bits)
    else:
        sub = struct.pack("<IHH", 1, 0, 0x10) + bytes([0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71])
        fmt = struct.pack("<HHIIHHHHI", 0xFFFE, channels, rate, rate * channels * width, channels * width, bits, 22, bits, 3) + sub
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(body)) + bytes(body)
    pathlib.Path(path).write_bytes(b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks)


def frames_of(wav):
    c = wav["channels"]
    v = wav["values"]
    return [v[i * c:(i + 1) * c] for i in range(wav["frames"])]


def variants(out, src16, src24, lossy24, lossy16):
    """The false positives of the design, from files the design names; a fixed seed, so every run writes the same bytes."""
    out = pathlib.Path(out)
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(1)
    a16, a24, l24, l16 = (read_wav(p) for p in (src16, src24, lossy24, lossy16))
    s24 = frames_of(a24)
    write_wav(out / "trunc14.wav", [[(v >> 10) << 10 for v in f] for f in s24], 2, 44100, 24)
    tpdf = [[((v + rng.randrange(1024) - rng.randrange(1024) + 512) >> 10) << 10 for v in f] for f in s24]
    write_wav(out / "trunc14-dither.wav", tpdf, 2, 44100, 24)
    write_wav(out / "pad16in24.wav", [[v << 8 for v in f] for f in frames_of(a16)], 2, 44100, 24)
    half = len(s24) // 2
    write_wav(out / "mixed-16-24.wav", [[(v >> 8) << 8 for v in f] for f in s24[:half]] + s24[half:], 2, 44100, 24)
    # Another tool that zeroes a count per 4,096-sample block and channel, from 6 to 12 by the block's peak.
    other = [list(f) for f in s24]
    for start in range(0, len(other), 4096):
        for c in range(2):
            peak = max(1, max(abs(f[c]) for f in other[start:start + 4096]))
            k = max(6, min(12, peak.bit_length() - 11))
            for f in other[start:start + 4096]:
                f[c] = (f[c] >> k) << k
    write_wav(out / "other-4096.wav", other, 2, 44100, 24)
    g24 = frames_of(l24)
    gain = 10 ** (-0.5 / 20)
    write_wav(out / "lossy-gain.wav", [[int(round(v * gain)) for v in f] for f in g24], 2, 44100, 24)
    write_wav(out / "lossy16-dither.wav", [[v + rng.randrange(2) - rng.randrange(2) for v in f] for f in frames_of(l16)], 2, 44100, 16)
    write_wav(out / "lossy-cut100.wav", g24[100:], 2, 44100, 24)
    s16 = frames_of(a16)
    fade = [list(f) for f in s16[:5 * 44100]]
    for i in range(3 * 44100, 5 * 44100):
        fade[i] = [int(round(v * (1 - (i - 3 * 44100) / (2 * 44100 - 1)))) for v in fade[i]]
    write_wav(out / "fade-silence.wav", fade + [[0, 0]] * 44100 + s16[:44100], 2, 44100, 16)


def main():
    if sys.argv[1:2] == ["--variants"]:
        variants(*sys.argv[2:7])
        return
    for path in sys.argv[1:]:
        print(json.dumps(figures(path)))


if __name__ == "__main__":
    main()
