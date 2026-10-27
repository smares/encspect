#!/usr/bin/env python3
"""Writes the synthetic measurement fixtures in tests/fixtures/measurement (synthetic-*.flac) and their manifests (*.expected.json).

The signals are made here with the standard library (docs/design/spectral-analysis.md, "Synthetic fixtures") and written as FLAC files with
flac_model.py's writer, so the files are reproducible byte for byte on every machine and CI regenerates and compares them. Step 1 of the
rollout has FLAC files only; the WAV twins come with the PCM route (step 3).

    - sines at a bin center and between bins, a logarithmic sweep, and sines of 8, 24 and 32 bits;
    - noise band-limited exactly at 11, 16, 19 and 20.5 kHz at 44.1 kHz and at 16 kHz also at 48 kHz (lines at bin centers with fixed random phases, repeating
      with the period of the frame, so the edge is as sharp as the bins), the 16 kHz one with TPDF dither and quiet (-30, -45 and -60 dB,
      with dither), and at 8, 11.025 and 192 kHz; white noise at 2 kHz (the cutoff is not searched under 8 kHz);
    - natural rolloff: white noise through one, two and four cascaded biquad lowpass sections at 16 kHz;
    - mixed content (half band-limited, half wideband), band-limited noise followed by digital silence, an impulse then silence;
    - 5.1 with a wideband LFE that must not set the cutoff;
    - a stream shorter than one frame, and one of more than 1,024 hops (the grids merge columns);
    - gaps: frames removed, and a jump of 2^40 samples; and the same audio at two block sizes;
    - levels (step 2): a full-scale sine, the most negative code (8 bits), a full-scale square wave, DC offsets, clipping runs of 1, 2, 3, 5, 100 and
      across block boundaries, leading, trailing and middle silence (mono and stereo), 16-bit audio in 24-bit samples, an all-silent stream, runs
      and silence that a gap must keep apart, and 32-bit samples at full scale (sums wider than 64 bits).

Each manifest is worked out from the file by measurement_model.py (its own FFT, spectrum and waveform; its cutoff method is transliterated from the analyzer), plus what the signal
was built to have: the cutoff in hertz, or none. MeasurementFixtureTests holds the analyzer to the model and the construction.
"""

import hashlib
import json
import math
import pathlib
import struct

import flac_model as m
import measurement_model as mm

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "measurement"


class Lcg:
    """A fixed linear congruential generator, so every run writes the same bytes."""

    def __init__(self, seed):
        self.state = seed & 0x7FFFFFFF

    def next(self):
        self.state = (self.state * 1103515245 + 12345) & 0x7FFFFFFF
        return self.state

    def uniform(self):
        """A number in [0, 1)."""
        return (self.next() >> 7) / 16777216.0

    def signed(self):
        return self.uniform() * 2 - 1


def quantize(values, bits, dither_seed=None, level=1.0):
    """Integers of bits bits: round(v * scale), with TPDF dither of one least significant bit before rounding where a seed is given."""
    scale = 1 << (bits - 1)
    low, high = -scale, scale - 1
    rng = Lcg(dither_seed) if dither_seed is not None else None
    out = []
    for v in values:
        x = v * level * scale
        if rng is not None:
            x += rng.uniform() + rng.uniform() - 1.0
        out.append(max(low, min(high, math.floor(x + 0.5))))
    return out


def lines(rate, highest_hz, rms_db, seed, length, lowest_hz=40.0, slope=None):
    """Lines at the centers of the bins of the frame the analyzer uses at this rate, from lowest_hz to highest_hz, with fixed random
    phases, repeated to length samples, at the given RMS level in dB relative to full scale. slope=(pass_hz, stop_db): the lines above pass_hz
    fall linearly in dB to stop_db at highest_hz (a sloped lowpass, as a long FIR filter makes); none lie above highest_hz."""
    n = mm.fft_size(rate)
    low = math.ceil(lowest_hz * n / rate)
    high = math.floor(highest_hz * n / rate)
    rng = Lcg(seed)
    spectrum = [0j] * n
    for k in range(low, high + 1):
        phase = rng.uniform() * 2 * math.pi
        gain = 1.0
        if slope is not None and k * rate / n > slope[0]:
            gain = 10 ** (slope[1] * ((k * rate / n) - slope[0]) / (highest_hz - slope[0]) / 20)
        spectrum[k] = gain * complex(math.cos(phase), math.sin(phase))
        spectrum[n - k] = spectrum[k].conjugate()
    period = [v.real for v in mm.fft([c.conjugate() for c in spectrum])]
    rms = math.sqrt(sum(v * v for v in period) / n)
    gain = 10 ** (rms_db / 20) / rms
    return [period[i % n] * gain for i in range(length)]


def lines_q(rate, highest_hz, rms_db, seed, length, bits=16, dither=True):
    """The lines as integers, with TPDF dither of one least significant bit unless dither is off: a signal that repeats with the frame's
    period has quantization error that repeats too and so is itself a set of lines, which does not average away over frames as the floor of a
    recording does."""
    return quantize(lines(rate, highest_hz, rms_db, seed, length), bits, dither_seed=seed + 1 if dither else None)


def white(length, seed, rms_db):
    rng = Lcg(seed)
    gain = 10 ** (rms_db / 20) * math.sqrt(3)
    return [rng.signed() * gain for _ in range(length)]


def pink(length, seed, rms_db, tone_hz=330.0, tone_db=-30.0, rate=44100):
    """Pink noise (Paul Kellet's three-pole filter on white noise) at the given RMS level, with a sine of tone_hz added."""
    rng = Lcg(seed)
    b0 = b1 = b2 = 0.0
    out = []
    for _ in range(length):
        x = rng.signed()
        b0 = 0.99765 * b0 + x * 0.0990460
        b1 = 0.96300 * b1 + x * 0.2965164
        b2 = 0.57000 * b2 + x * 1.0526913
        out.append(b0 + b1 + b2 + x * 0.1848)
    rms = math.sqrt(sum(v * v for v in out) / length)
    gain = 10 ** (rms_db / 20) / rms
    tone = 10 ** (tone_db / 20) * math.sqrt(2)
    return [v * gain + tone * math.sin(2 * math.pi * ((tone_hz * 10 * i / rate) % 10) / 10) for i, v in enumerate(out)]


def biquad_lowpass(values, rate, hertz, sections):
    w0 = 2 * math.pi * hertz / rate
    alpha = math.sin(w0) / (2 * 0.7071067811865476)
    c = math.cos(w0)
    b0, b1, b2 = (1 - c) / 2, 1 - c, (1 - c) / 2
    a0, a1, a2 = 1 + alpha, -2 * c, 1 - alpha
    for _ in range(sections):
        x1 = x2 = y1 = y2 = 0.0
        out = []
        for x in values:
            y = (b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0
            x2, x1, y2, y1 = x1, x, y1, y
            out.append(y)
        values = out
    return values


def sine_bin(rate, bin_numerator, bin_denominator, length, amplitude):
    """A sine at bin_numerator / bin_denominator bins of the frame, with an integer phase."""
    n = mm.fft_size(rate)
    period = n * bin_denominator
    return [amplitude * math.sin(2 * math.pi * ((bin_numerator * i) % period) / period) for i in range(length)]


def sweep(rate, seconds, f0, f1, amplitude):
    length = int(rate * seconds)
    ratio = f1 / f0
    k = 2 * math.pi * f0 * seconds / math.log(ratio)
    return [amplitude * math.sin(k * (ratio ** (i / (rate * seconds)) - 1)) for i in range(length)]


# ---- The FLAC writer ----

def best_fixed(samples):
    """The fixed predictor order 0 to 4 with the smallest sum of residual magnitudes."""
    best = None
    for order in range(5):
        total = 0
        for n in range(order, len(samples)):
            if order == 0:
                p = 0
            elif order == 1:
                p = samples[n - 1]
            elif order == 2:
                p = 2 * samples[n - 1] - samples[n - 2]
            elif order == 3:
                p = 3 * samples[n - 1] - 3 * samples[n - 2] + samples[n - 3]
            else:
                p = 4 * samples[n - 1] - 6 * samples[n - 2] + 4 * samples[n - 3] - samples[n - 4]
            total += abs(samples[n] - p)
        if best is None or total < best[0]:
            best = (total, order)
    return best[1]


def subframe_writer(samples, bits, wasted=False, verbatim=False):
    if all(s == samples[0] for s in samples):
        return lambda w: m.write_subframe(w, samples, "constant", bits)
    if verbatim:
        return lambda w: m.write_subframe(w, samples, "verbatim", bits)
    shift = 0
    if wasted:
        combined = 0
        for sample in samples:
            combined |= sample
        shift = (combined & -combined).bit_length() - 1 if combined else 0
    order = best_fixed(samples)
    partition_order = 0
    while partition_order < 5 and len(samples) % (1 << (partition_order + 1)) == 0 and (len(samples) >> (partition_order + 1)) > order + 16:
        partition_order += 1
    return lambda w: m.write_subframe(w, samples, "fixed", bits, wasted=shift, order=order, partition_order=partition_order, method=1 if bits > 16 else 0)


def flac_file(channels, rate, bits, block=4096, numbers=None, drop=(), md5_of=None, wasted=False, verbatim=False):
    """A FLAC file of the channels (lists of integers). The frames are fixed-size blocks numbered from 0 (numbers: the frame number
    of each block instead), and the blocks whose index is in drop are left out."""
    length = len(channels[0])
    blocks = [(i, range(start, min(start + block, length))) for i, start in enumerate(range(0, length, block))]
    frames = []
    kept = []
    for i, span in blocks:
        if i in drop:
            continue
        part = [c[span.start:span.stop] for c in channels]
        number = numbers[i] if numbers else i
        header = m.frame_header(number, len(span), rate, len(channels) - 1, bits)
        frames.append(m.write_frame(header, [subframe_writer(s, bits, wasted, verbatim) for s in part]))
        kept.append(part)
    width = (bits + 7) // 8
    pcm = bytearray()
    total = 0
    for part in kept:
        for k in range(len(part[0])):
            for ch in part:
                pcm += (ch[k] & ((1 << width * 8) - 1)).to_bytes(width, "little")
        total += len(part[0])
    md5 = hashlib.md5(pcm).digest()
    sizes = [len(f) for f in frames]
    info = m.streaminfo_body(min(block, length), block, min(sizes), max(sizes), rate, len(channels), bits, total, md5)
    return b"fLaC" + m.metadata_block(0, info, last=True) + b"".join(frames)


def manifest(data, description, construction, layout=None):
    result = {"description": description, "length": len(data), "construction": construction, "model": mm.measure(data, layout)}
    return result


FIXTURES = {}


def add(name, description, channels, rate, bits, construction, block=4096, layout=None, **options):
    FIXTURES[name] = (description, channels, rate, bits, construction, block, layout, options)


def build():
    rate44, rate48 = 44100, 48000
    seconds = 1.0
    one = rate44 // 2
    n44, n48 = int(rate44 * seconds), int(rate48 * seconds)

    # Sines: a bin center and a quarter between two bins, at -6 dBFS, in 8, 16, 24 and 32 bits.
    add("sine-bin-center", "A sine at the center of bin 400 of the 4,096-point frame (about 4,306.6 Hz), amplitude 0.5.",
        [quantize(sine_bin(rate44, 400, 1, one, 0.5), 16)], rate44, 16, {"cutoff_hz": None, "unspecified": True, "tone_bin": 400, "tone_dbfs": -6.0})
    add("sine-between-bins", "A sine halfway between bins 400 and 401, amplitude 0.5.",
        [quantize(sine_bin(rate44, 801, 2, one, 0.5), 16)], rate44, 16, {"cutoff_hz": None, "unspecified": True})
    add("sine-8-bits", "The sine of bin 400 in 8 bits.", [quantize(sine_bin(rate44, 400, 1, one, 0.5), 8)], rate44, 8, {"cutoff_hz": None, "unspecified": True, "tone_bin": 400})
    add("sine-24-bits", "The sine of bin 400 in 24 bits.", [quantize(sine_bin(rate44, 400, 1, one, 0.5), 24)], rate44, 24, {"cutoff_hz": None, "unspecified": True, "tone_bin": 400})
    add("sine-32-bits", "The sine of bin 400 in 32 bits.", [quantize(sine_bin(rate44, 400, 1, one, 0.5), 32)], rate44, 32, {"cutoff_hz": None, "unspecified": True, "tone_bin": 400})
    add("sweep", "A logarithmic sweep from 100 Hz to 20 kHz in 2 s at amplitude 0.5.", [quantize(sweep(rate44, 2.0, 100.0, 20000.0, 0.5), 16)], rate44, 16,
        {"cutoff_hz": None, "unspecified": True, "sweep": [100, 20000, 2.0]})

    # Noise band-limited exactly: lines at bin centers.
    for rate, count in ((rate44, n44), (rate48, n48)):
        # 44.1 kHz has the four edges; 48 kHz only the 16 kHz one, to keep the files few.
        for edge in ((11000, 16000, 19000, 20500) if rate == rate44 else (16000,)):
            add(f"lines-{edge}-{rate}", f"Lines at the bin centers from 40 Hz to {edge} Hz at {rate} Hz with fixed random phases, -20 dBFS RMS{', with TPDF dither of one least significant bit' if not (rate == rate44 and edge == 16000) else ', undithered'}: a brickwall at {edge} Hz.",
                [lines_q(rate, edge, -20.0, edge + rate, count, dither=not (rate == rate44 and edge == 16000))], rate, 16, {"cutoff_hz": edge})
    sloped = quantize(lines(rate44, 17000, -20.0, 21, n44, slope=(15000, -90.0)), 16, dither_seed=22)
    add("lines-slope-15000-17000", "Lines at the bin centers from 40 Hz to 17 kHz, flat to 15 kHz and falling linearly to -90 dB at 17 kHz (a sloped lowpass), with TPDF dither of one least significant bit.",
        [sloped], rate44, 16, {"cutoff_hz": None, "pass_hz": 15000, "stop_hz": 17000})
    base = lines(rate44, 16000, -20.0, 7, n44)
    add("lines-16000-44100-dithered", "The 16 kHz lines with TPDF dither of one least significant bit.", [quantize(base, 16, dither_seed=11)], rate44, 16, {"cutoff_hz": 16000})
    for db in (30, 45, 60):
        add(f"lines-16000-44100-quiet-{db}", f"The 16 kHz lines lowered by {db} dB, with TPDF dither of one least significant bit.",
            [quantize(base, 16, dither_seed=11, level=10 ** (-db / 20))], rate44, 16, {"cutoff_hz": 16000, "level_db": -db, "hidden_by_dither": db == 60})
    add("lines-16000-44100-b1152", "The audio of lines-16000-44100 in blocks of 1,152 samples.", [quantize(lines(rate44, 16000, -20.0, 16000 + rate44, n44), 16)], rate44, 16,
        {"cutoff_hz": 16000, "same_audio_as": "synthetic-lines-16000-44100"}, block=1152)

    # Other rates.
    add("lines-3200-8000", "Lines up to 3,200 Hz at 8,000 Hz (the frame is 1,024 points).", [lines_q(8000, 3200, -20.0, 5, 16000)], 8000, 16, {"cutoff_hz": 3200})
    add("lines-4400-11025", "Lines up to 4,400 Hz at 11,025 Hz.", [lines_q(11025, 4400, -20.0, 6, 22050)], 11025, 16, {"cutoff_hz": 4400})
    add("lines-8000-22050", "Lines up to 8,000 Hz at 22,050 Hz (the frame is 2,048 points).", [lines_q(22050, 8000, -20.0, 8, 44100)], 22050, 16, {"cutoff_hz": 8000})
    add("lines-20000-192000", "Lines up to 20 kHz at 192 kHz (the frame is 16,384 points), -40 dBFS RMS, with TPDF dither of one least significant bit.", [lines_q(192000, 20000, -40.0, 9, 57600, bits=24)], 192000, 24,
        {"cutoff_hz": 20000})
    add("white-2000", "White noise at 2,000 Hz: under 8,000 Hz no cutoff is searched.", [quantize(white(4000, 3, -20.0), 16)], 2000, 16, {"cutoff_hz": None, "no_cutoff": True, "is_searched": False})
    add("white-44100", "White noise, -20 dBFS RMS: no cutoff.", [quantize(white(n44, 4, -20.0), 16)], rate44, 16, {"cutoff_hz": None, "no_cutoff": True})

    # Natural rolloff.
    noise = pink(n44, 21, -20.0)
    for sections in (1, 2, 4):
        add(f"rolloff-{sections}", f"Pink noise with a 330 Hz tone through {sections} cascaded second-order lowpass sections at 16 kHz: a gradual rolloff, no cutoff.",
            [quantize(biquad_lowpass(noise, rate44, 16000.0, sections), 16)], rate44, 16, {"cutoff_hz": None, "no_cutoff": True})

    # Mixed content.
    half = n44 // 2
    mixed = lines_q(rate44, 16000, -20.0, 31, half) + quantize(white(n44 - half, 32, -20.0), 16)
    add("mixed-half-and-half", "Half a second of 16 kHz lines, then as much white noise at the same level: part of the stream has the edge.", [mixed], rate44, 16,
        {"cutoff_hz": None, "no_cutoff": True})
    with_silence = lines_q(rate44, 16000, -20.0, 33, int(rate44 * 0.5)) + [0] * int(rate44 * 0.5)
    add("lines-16000-then-silence", "Half a second of 16 kHz lines and as much digital silence: the silent frames count as without signal.", [with_silence], rate44, 16,
        {"cutoff_hz": 16000})

    # Silence after an impulse.
    for rate in (rate44, rate48):
        samples = [0] * int(rate * 1.5)
        samples[1000] = 30000
        add(f"impulse-silence-{rate}", "An impulse of 30,000 at sample 1,000, then digital silence.", [samples], rate, 16, {"cutoff_hz": None, "no_cutoff": True})

    # 5.1: five channels with a 8 kHz brickwall, the LFE wideband.
    rate22 = 22050
    channels = [lines_q(rate22, 8000, -30.0, 40 + c, 22050) for c in (0, 1, 2)]
    lfe = quantize(white(22050, 50, -30.0), 16)
    surrounds = [lines_q(rate22, 8000, -30.0, 43 + c, 22050) for c in (0, 1)]
    add("surround-5-1-wideband-lfe", "Six channels at 22,050 Hz: five with lines up to 8 kHz and a wideband LFE, which the combined spectrum leaves out.",
        channels + [lfe] + surrounds, rate22, 16, {"cutoff_hz": 8000, "lfe_channel": 3})

    # Short and long streams.
    add("short-1000-samples", "A stream of 1,000 samples, shorter than one frame: measured as one frame padded with zeros.", [quantize(white(1000, 61, -20.0), 16)], rate44, 16,
        {"cutoff_hz": None, "no_cutoff": True})
    long_samples = [0] * (rate44 * 48)
    burst = quantize(white(8192, 62, -10.0), 16)
    for second in (10, 25, 40):
        long_samples[rate44 * second:rate44 * second + 8192] = burst
    add("long-bursts-48-seconds", "48 s of digital silence with a burst of white noise at 10, 25 and 40 s: more than 1,024 hops, so the grid merges its columns.", [long_samples], rate44, 16,
        {"cutoff_hz": None, "no_cutoff": True, "bursts_at_seconds": [10, 25, 40]})

    # Gaps.
    gap_audio = lines_q(rate44, 16000, -20.0, 71, int(rate44 * 1.0))
    add("gap-frames-removed", "The 16 kHz lines with the fourth and fifth frames removed: the positions jump by 8,192 samples.", [gap_audio], rate44, 16,
        {"cutoff_hz": 16000, "lost_samples": 8192}, drop=(3, 4))
    add("gap-jump-2-40", "Two blocks, then two more numbered 2^28 frames later: the positions jump by about 2^40 samples.", [gap_audio[:16384]], rate44, 16,
        {"cutoff_hz": None, "unspecified": True, "jump": 1 << 40}, numbers=[0, 1, 1 << 28, (1 << 28) + 1])

    add_levels()


def small_noise(length, seed, amplitude=300, nonzero=False):
    """Integers in [-amplitude, amplitude] from the fixed generator; with nonzero, never 0 (so silence is only where it is put)."""
    rng = Lcg(seed)
    out = []
    for _ in range(length):
        v = math.floor(rng.signed() * amplitude + 0.5)
        out.append(v if v != 0 or not nonzero else 1)
    return out


def add_levels():
    """The fixtures of the levels (docs/design/spectral-analysis.md, "Levels"). Each carries what it was built to have in construction["levels"]
    (stream and channels), exact where the construction fixes a figure; the model's figures are checked against the analyzer as for every fixture."""
    rate = 44100
    unspecified = {"cutoff_hz": None, "unspecified": True}

    sine = [max(-32768, min(32767, math.floor(32768 * math.sin(2 * math.pi * (i % 441) / 441) + 0.5))) for i in range(8820)]
    add("levels-sine-full-scale", "A sine of amplitude 1.0 with a period of 441 samples in 16 bits: its peak is clamped to 32,767.", [sine], rate, 16,
        dict(unspecified, levels={"stream": {"highest_magnitude": 32768 if min(sine) == -32768 else 32767}}))

    low = small_noise(4096, 3, 60)
    low[1000:1004] = [-128] * 4
    low[2000] = 127
    add("levels-most-negative-8-bits", "8-bit noise with four samples of the most negative code, -128, and one of +127: the most negative code counts as full scale.", [low], rate, 8,
        dict(unspecified, levels={"stream": {"highest_magnitude": 128, "sample_peak_tenths": 0, "clipping_runs": 1, "clipped_samples": 4, "longest_clipping_run": 4}}))

    square = [32767 if (i // 50) % 2 == 0 else -32768 for i in range(5000)]
    add("levels-square-full-scale", "A square wave between +32,767 and -32,768 with a half period of 50 samples: RMS and peak read 0 dBFS, every half period is a run.", [square],
        rate, 16, dict(unspecified, levels={"stream": {"sample_peak_tenths": 0, "rms_tenths": 0, "clipping_runs": 100, "clipped_samples": 5000, "longest_clipping_run": 50}}))

    left = [1000 + (200 if i % 2 == 0 else -200) for i in range(8192)]
    right = [-300 + (100 if i % 2 == 0 else -100) for i in range(8192)]
    add("levels-dc-offset", "Two channels alternating around a constant: 1,000 (left) and -300 (right) in 16 bits, so the means are exact.", [left, right], rate, 16,
        dict(unspecified, levels={"stream": {"dc_micropercent": 3051758}, "channels": [{"dc_micropercent": 3051758, "highest_magnitude": 1200}, {"dc_micropercent": -915527, "highest_magnitude": 400}]}))

    runs = small_noise(8192, 5, 300)
    runs[0:3] = [32767] * 3
    runs[100] = 32767
    runs[200:202] = [32767] * 2
    runs[300:303] = [32767] * 3
    runs[400:403] = [-32768] * 3
    runs[500:600] = [32767] * 100
    runs[700:704] = [32767, 32767, -32768, -32768]
    runs[4094:4099] = [-32768] * 5
    runs[8188:8192] = [32767] * 4
    add("levels-clipping-runs", "Noise with runs at the extreme codes: 1 and 2 samples (not runs), 3 at the start, 3 at each extreme, 100, two of 2 at opposite extremes side by side, 5 across a block boundary and 4 at the end.",
        [runs], rate, 16, dict(unspecified, levels={"stream": {"clipping_runs": 6, "clipped_samples": 118, "longest_clipping_run": 100}}))

    mono_silence = [0] * 1000 + small_noise(4000, 7, 299, nonzero=True) + [0] * 500 + small_noise(3000, 8, 299, nonzero=True) + [0] * 2000
    add("levels-silence-positions", "Noise that never reaches 0 with 1,000 samples of digital silence at the start, 500 in the middle and 2,000 at the end.", [mono_silence], rate, 16,
        dict(unspecified, levels={"stream": {"silent_at_start": 1000, "silent_at_end": 2000, "zero_samples": 3500, "low_bits_unused": 0}}))

    first = [0] * 3000 + small_noise(5192, 9, 299, nonzero=True)
    second = [0] * 1000 + small_noise(6692, 10, 299, nonzero=True) + [0] * 500
    add("levels-silence-stereo", "Two channels of noise that never reaches 0: the left silent for its first 3,000 samples, the right for its first 1,000 and last 500; the sample frames silent in both are the first 1,000.",
        [first, second], rate, 16, dict(unspecified, levels={"stream": {"silent_at_start": 1000, "silent_at_end": 0, "zero_samples": 1000},
                                                             "channels": [{"silent_at_start": 3000, "silent_at_end": 0, "zero_samples": 3000}, {"silent_at_start": 1000, "silent_at_end": 500, "zero_samples": 1500}]}))

    audio = small_noise(8192, 11, 20000)
    add("levels-low-bits-24-in-16", "16-bit noise as 24-bit samples shifted left by 8 (left) and by 12 (right): the low bits that are never set.",
        [[v << 8 for v in audio], [v << 12 for v in small_noise(8192, 12, 2000)]], rate, 24,
        dict(unspecified, levels={"stream": {"low_bits_unused": 8}, "channels": [{"low_bits_unused": 8}, {"low_bits_unused": 12}]}), wasted=True)

    add("levels-silent", "8,192 samples of digital silence: no peak, no level, no bit in use.", [[0] * 8192], rate, 16,
        dict(unspecified, levels={"stream": {"sample_peak_tenths": None, "rms_tenths": None, "dc_micropercent": 0, "silent_at_start": 8192, "silent_at_end": 8192, "zero_samples": 8192, "low_bits_unused": None}}))

    gap_audio = small_noise(8192, 13, 299, nonzero=True)
    gap_audio[0:1024] = [0] * 1024
    gap_audio[2048:2248] = [0] * 200
    gap_audio[3070:3072] = [32767] * 2
    gap_audio[4096] = 32767
    gap_audio[4820:5120] = [0] * 300
    gap_audio[6144:8192] = [0] * 2048
    add("levels-gaps", "Blocks of 1,024 samples with the second, fourth and sixth removed: the first block and the start of the third are silent (the stretch at the start ends at the gap), two samples at +32,767 end the third block and one starts the fifth (no run), 300 zeros end the fifth block, and the last two blocks are silent (the stretch at the end starts after the gap).",
        [gap_audio], rate, 16, dict(unspecified, levels={"stream": {"clipping_runs": 0, "silent_at_start": 1024, "silent_at_end": 2048, "zero_samples": 3572}}), block=1024, drop=(1, 3, 5))

    wide = [2147483647 if i % 128 < 96 else -2147483648 for i in range(4096)]
    add("levels-full-scale-32-bits", "A 32-bit square wave between +2,147,483,647 and -2,147,483,648, high for 96 and low for 32 samples of every 128: the sum of squares passes 2^64 within two samples.",
        [wide], rate, 32, dict(unspecified, levels={"stream": {"sample_peak_tenths": 0, "clipping_runs": 64, "clipped_samples": 4096, "longest_clipping_run": 96, "highest_magnitude": 2147483648}}), verbatim=True)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    build()
    for name, (description, channels, rate, bits, construction, block, layout, options) in FIXTURES.items():
        data = flac_file(channels, rate, bits, block=block, numbers=options.get("numbers"), drop=options.get("drop", ()), wasted=options.get("wasted", False),
                         verbatim=options.get("verbatim", False))
        stem = OUT / f"synthetic-{name}"
        stem.with_suffix(".flac").write_bytes(data)
        text = mm.dumps(manifest(data, description, construction, layout))
        stem.with_suffix(".expected.json").write_text(text)
        print(name, len(data))


if __name__ == "__main__":
    main()
