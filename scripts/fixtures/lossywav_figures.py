#!/usr/bin/env python3
"""The prototype of the low-bit figures of docs/design/lossywav-detection.md, and the writer of the false-positive
variants the design measured. Standard library only; it reads files as data and never runs lossyWAV.

The measurement's figures, per stream, over integer PCM in a WAV file (8, 16, 24 and 32 bits, any rate and channel count):
- spans of 32 samples per channel, anchored at the stream's position 0; a span's count is the number of low bits that
  are zero in all its samples (the trailing zeros of their OR within the declared width), and its magnitude the largest
  bit length of its samples beside the sign (of v for v >= 0, of -v - 1 for v < 0: the bits a two's-complement sample
  needs besides its sign bit);
- a span whose samples are all 0 is silent; one whose magnitude is fewer than THIN bits above its count is thin (few
  significant bits above the zeroed ones: small samples, but also a constant power of two or full-scale negative
  samples); one of fewer than 32 samples (the last of the stream) is short; the silent spans are counted apart, and
  none of the three takes part in the changes;
- Low Bits Unused: the smallest count over the stream (the levels' existing figure);
- the distribution of the counts over the spans that are not silent, all channels together;
- the changes: the spans, neither silent, thin nor short, whose count differs from the same channel's last such span;
- the moments: the span positions at which some channel's count changes (a change in every channel at one position is one moment);
- the grid of the changes: the largest power of two from 256 to 65,536 samples such that at least 95% of the moments
  fall within one span of the phase with the most moments (hits * 20 >= moments * 19, in integers), given at least 20 moments
  (the lead's amendment of decision 6, 2026-11-08: one switch of bit usage in 20 or more channels is one moment);
- the times (span positions at which no channel is silent) at which all channels have the same count.

The coded counterpart, over a FLAC file's subframes as `flac -a` lists them (--flac): the wasted bits of each subframe,
the changes between consecutive frames of each channel, and the behavior rule of the design.

Usage: lossywav_figures.py [--thin K] FILE.wav [...]       one JSON line of figures per file
       lossywav_figures.py --flac FILE.flac [...]            one JSON line of wasted-bit figures per file (needs flac)
       lossywav_figures.py --variants OUT SRC16.wav SRC24.wav LOSSY24.wav LOSSY16.wav LOSSY24B.wav
                                                            writes the false-positive variants into OUT
"""

import json
import pathlib
import random
import struct
import subprocess
import sys
import tempfile

SPAN = 32
BINS = 2048
MIN_CHANGES = 20
MIN_GRID = 256
THIN = 4


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


def spans(wav):
    """Per channel, the (count, magnitude, whether it is short) of each span, or None for a silent span."""
    channels, values, frames = wav["channels"], wav["values"], wav["frames"]
    mask = (1 << wav["bits"]) - 1
    out = [[] for _ in range(channels)]
    for start in range(0, frames, SPAN):
        end = min(frames, start + SPAN)
        for channel in range(channels):
            o = m = 0
            for i in range(start, end):
                v = values[i * channels + channel]
                o |= v & mask
                m |= v if v >= 0 else ~v
            out[channel].append((trailing_zeros(o), m.bit_length(), end - start < SPAN) if o else None)
    return out


def grid_of(phase, changes, unit=SPAN, bins=BINS, smallest=MIN_GRID):
    """The largest power-of-two grid, in samples, at the phase with the most moments, within one unit of which at least 95% of the moments fall."""
    if changes < MIN_CHANGES:
        return None, None
    length = bins * unit
    while length >= max(smallest, unit):
        n = length // unit
        folded = [sum(phase[k::n]) for k in range(n)]
        m = max(range(n), key=folded.__getitem__)
        hits = folded[m] + (folded[(m - 1) % n] + folded[(m + 1) % n] if n > 2 else folded[(m + 1) % n] if n == 2 else 0)
        if hits * 20 >= changes * 19:
            return length, hits
        length //= 2
    return None, None


def figures(path, thin=THIN):
    return figures_of(read_wav(path), path, thin)


def pcm_of(channels, rate, bits):
    """The samples of lists of integers (one list a channel) as the dictionary figures_of reads, as read_wav returns it."""
    frames = len(channels[0])
    values = [channels[c][i] for i in range(frames) for c in range(len(channels))]
    return {"rate": rate, "channels": len(channels), "bits": bits, "frames": frames, "values": values, "chunks": []}


def figures_of(wav, path="", thin=THIN):
    channels, bits = wav["channels"], wav["bits"]
    cells = spans(wav)
    histogram = [0] * (bits + 1)
    phase = [0] * BINS
    moved = set()
    changes = thin_spans = silent = short_spans = 0
    smallest = None
    for channel in range(channels):
        last = None
        for index, cell in enumerate(cells[channel]):
            if cell is None:
                silent += 1
                continue
            count, magnitude, short = cell
            histogram[count] += 1
            short_spans += short
            smallest = count if smallest is None else min(smallest, count)
            if magnitude - count < thin:
                thin_spans += 1
                continue
            if short:
                continue
            if last is not None and last != count:
                changes += 1
                moved.add(index)
            last = count
    agree = times = 0
    if channels > 1:
        for row in zip(*cells):
            if None not in row:
                times += 1
                agree += len({c for c, _, _ in row}) == 1
    for index in moved:
        phase[index % BINS] += 1
    grid, on_grid = grid_of(phase, len(moved))
    total = sum(histogram)
    return {"file": str(path), "rate": wav["rate"], "bits": bits, "channels": channels, "chunks": wav["chunks"],
            "frames": wav["frames"], "lowBitsUnused": smallest, "spans": total, "silentSpans": silent, "thinSpans": thin_spans, "shortSpans": short_spans,
            "distribution": {z: n for z, n in enumerate(histogram) if n},
            "spansAboveLowBitsUnused": sum(histogram[smallest + 1:]) if smallest is not None else 0,
            "changes": changes, "changeMoments": len(moved), "grid": grid, "momentsOnGrid": on_grid, "changesOnGrid": on_grid,  # the old name of momentsOnGrid, kept for the importer and the manifests
           
            "timesWithEqualCounts": agree if channels > 1 else None, "times": times if channels > 1 else None}


# lossyWAV's block length at the rates the design tested (its 8 kHz runs crashed); the FLAC behavior rule applies at these rates only.
TESTED_GRIDS = {22050: 256, 32000: 256, 44100: 512, 48000: 512, 88200: 1024, 96000: 1024, 176400: 2048, 192000: 2048}


def flac_units(path):
    """The wasted bits of each subframe of a FLAC file, from `flac -a`, and the design's behavior rule over them (PR 3): a constant subframe of 0 is digital
    silence and has no count; the claim needs a tested rate, 20 moments (frame positions at which some channel's count changes), the greatest common divisor
    of the distances between the changes' positions equal to lossyWAV's block, and more than half of the counted subframes above the smallest count. Only a
    source channel coded as itself is compared (left in left/right and left/side, right in left/right and side/right): a mid or side subframe states the
    coding's own count, so it is in the distribution only."""
    import math
    with tempfile.TemporaryDirectory() as work:
        analysis = pathlib.Path(work) / "a.ana"
        result = subprocess.run(["flac", "-a", "-s", "-f", "-o", str(analysis), str(path)], capture_output=True)
        if result.returncode != 0 or not analysis.exists():
            return {"file": str(path), "error": result.stderr.decode("utf-8", "replace").strip()[-200:]}
        frames, rate, sizes, starts, assignments = [], None, [], [], []
        position = 0
        with analysis.open() as lines:
            for line in lines:
                if line.startswith("frame="):
                    fields = dict(f.split("=", 1) for f in line.split())
                    rate = int(fields["sample_rate"])
                    sizes.append(int(fields["blocksize"]))
                    starts.append(position)
                    position += sizes[-1]
                    frames.append([])
                    assignments.append(fields.get("channel_assignment", "INDEPENDENT").upper())
                elif line.startswith("\tsubframe=") and frames:
                    silent = "type=CONSTANT" in line and "value=0" in line.split("type=CONSTANT")[1].split()
                    frames[-1].append(None if silent else int(line.split("wasted_bits=")[1].split()[0]))
    if not frames:
        return {"file": str(path), "error": "no frames"}
    histogram, compared, changes, moments, silent_units = {}, {}, 0, set(), 0
    grid = TESTED_GRIDS.get(rate)
    divisor, first_change = 0, None
    for channel in range(max(len(f) for f in frames)):
        last = None
        for index, frame in enumerate(frames):
            if channel >= len(frame):
                continue
            count = frame[channel]
            if count is None:
                silent_units += 1
                continue
            histogram[count] = histogram.get(count, 0) + 1
            kind = assignments[index]
            if kind.startswith("MID") or (kind.startswith("LEFT_SIDE") and channel == 1) or (kind.startswith("RIGHT_SIDE") and channel == 0):
                continue
            compared[count] = compared.get(count, 0) + 1
            if last is not None and count != last:
                changes += 1
                moments.add(starts[index])
                first_change = starts[index] if first_change is None else first_change
                divisor = math.gcd(divisor, abs(starts[index] - first_change))
            last = count
    smallest = min(compared) if compared else 0
    above = sum(n for c, n in compared.items() if c > smallest)
    units = sum(histogram.values())
    claim = grid is not None and len(moments) >= MIN_CHANGES and divisor == grid and above * 2 > sum(compared.values())
    return {"file": str(path), "rate": rate, "frameLength": max(sizes), "subframes": units, "silentSubframes": silent_units, "distribution": dict(sorted(histogram.items())),
            "changes": changes, "moments": len(moments), "gridOfChanges": divisor, "subframesAboveSmallest": above, "claim": claim}


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


def faded(frames, start, length):
    """A linear fade to digital silence over length frames from start, as an editor applies it, rounded to the nearest."""
    out = [list(f) for f in frames]
    for i in range(start, min(len(out), start + length)):
        gain_num, gain_den = start + length - 1 - i, length - 1
        out[i] = [(2 * v * gain_num + gain_den) // (2 * gain_den) for v in out[i]]
    for i in range(start + length, len(out)):
        out[i] = [0] * len(out[i])
    return out


def variants(out, src16, src24, lossy24, lossy16, lossy24b):
    """The false positives of the design, from files the design names; a fixed seed, so every run writes the same bytes.
    lossy24 and lossy24b are two lossyWAV outputs of the same rate and width (the 24-bit recording at standard and portable)."""
    out = pathlib.Path(out)
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(1)
    a16, a24, l24, l16, l24b = (read_wav(p) for p in (src16, src24, lossy24, lossy16, lossy24b))
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
    # Edits after lossyWAV: two outputs joined off the grid (the first cut 100 samples after a grid line), a fade to
    # digital silence over the last 5 s, and a fade out, 1 s of digital silence and the output again.
    g24b = frames_of(l24b)
    write_wav(out / "lossy-joined.wav", g24[:15 * 44100 + 100] + g24b[15 * 44100:], 2, 44100, 24)
    write_wav(out / "lossy-fade-end.wav", faded(g24, len(g24) - 5 * 44100, 5 * 44100), 2, 44100, 24)
    write_wav(out / "lossy-fade-silence.wav", faded(g24[:10 * 44100], 7 * 44100, 3 * 44100) + [[0, 0]] * 44100 + g24[:10 * 44100], 2, 44100, 24)


def main():
    args = sys.argv[1:]
    if args[:1] == ["--variants"]:
        variants(*args[1:7])
        return
    if args[:1] == ["--flac"]:
        for path in args[1:]:
            print(json.dumps(flac_units(path)))
        return
    thin = THIN
    if args[:1] == ["--thin"]:
        thin, args = int(args[1]), args[2:]
    for path in args:
        print(json.dumps(figures(path, thin)))


if __name__ == "__main__":
    main()
