#!/usr/bin/env python3
"""Varied, longer test signals for the window shape question (does Apple's AAC encoder use sine windows only?),
written with the standard library only, deterministic (fixed seed, own generator, no interpreter-dependent
random numbers; sine values may differ in the last bit between CPU architectures, which the sources' SHA-256 records show).

Used by macos_aac_support.py, which writes the WAVs: source(name) returns the 16-bit PCM and the channel count of one of
NAMES, 44.1 kHz, all at a peak of 0.9 of full scale.

Each source runs 8 seconds, so an encode holds some 340 AAC frames (170 for HE-AAC's 2,048-sample frames).
The sources are named without the prefix the encodes carry (`win-<codec>-...-<name>`); the first two are mono.

- speech: formant-filtered pulse trains (three vowels in turn, a gliding pitch), fricative noise between the syllables, pauses.
- mix: mono music-like mix of the plucks, the clicks and the chords.
- pluck: Karplus-Strong plucked strings playing a pentatonic line, panned note by note.
- clicks: castanet-like clicks, a ringing 2 to 3 kHz tick with accents, and single-sample impulses.
- chords: a chord sequence (A minor, F, C, G) of sawtooth-like partials with a 2 ms attack and a decay, each chord struck at once.
- white, pink: noise, uncorrelated between the channels.
- tones: pure tones from 100 Hz to 15 kHz, 1 s each, then two tones together.
- sweep: a logarithmic sweep from 30 Hz to 20 kHz over 4 s, then a linear one from 200 Hz to 16 kHz.
- transient: digital silence, then noise bursts, a click, a very quiet tone and a full-scale square burst.
- wide: left and right with other chords and other noise (a wide image).
- narrow: nearly the same in both channels: a mono mix plus a faint difference signal (a narrow image).
"""

import math
import struct

RATE = 44100
SECONDS = 8
SEED = 20260615
NAMES = ["speech", "mix", "pluck", "clicks", "chords", "white", "pink", "tones", "sweep", "transient", "wide", "narrow"]
MONO = {"speech", "mix"}


class Random:
    """A 32-bit xorshift generator: the same numbers on every interpreter."""

    def __init__(self, seed):
        self.state = seed & 0xFFFFFFFF or 1

    def next(self):
        x = self.state
        x ^= (x << 13) & 0xFFFFFFFF
        x ^= x >> 17
        x ^= (x << 5) & 0xFFFFFFFF
        self.state = x
        return x

    def uniform(self):
        """-1 up to 1."""
        return self.next() / 2147483648.0 - 1.0

    def below(self, n):
        return self.next() % n


def silence(count=None):
    return [0.0] * (count if count is not None else RATE * SECONDS)


def mix_into(target, source, start, gain=1.0):
    end = min(len(target), start + len(source))
    for i in range(start, end):
        target[i] += source[i - start] * gain


def resonator(signal, frequency, bandwidth):
    """A two-pole resonator, gain normalized to about 1 at the center."""
    r = math.exp(-math.pi * bandwidth / RATE)
    c1 = 2 * r * math.cos(2 * math.pi * frequency / RATE)
    c2 = -r * r
    gain = 1 - r
    y1 = y2 = 0.0
    out = []
    for x in signal:
        y = gain * x + c1 * y1 + c2 * y2
        y2, y1 = y1, y
        out.append(y)
    return out


def speech(rng):
    vowels = [(730, 1090, 2440), (270, 2290, 3010), (300, 870, 2240), (570, 840, 2410)]  # a, i, u, o
    total = RATE * SECONDS
    syllables = []  # start, length, vowel, pitch in Hz
    position = 0
    count = 0
    while position < total:
        if count % 7 == 6:
            position += int(RATE * (0.35 + 0.05 * rng.below(5)))  # a pause
        else:
            length = int(RATE * (0.16 + 0.04 * rng.below(5)))
            syllables.append((position, length, rng.below(len(vowels)), 95 + 12 * rng.below(8)))
            position += length + int(RATE * 0.05)  # room for the fricative
        count += 1
    voiced = silence(total)
    noise = silence(total)
    envelope = silence(total)
    gate = [[0.0] * total for _ in vowels]
    phase = 0.0
    for start, length, vowel, f0 in syllables:
        for i in range(min(length, total - start)):
            t = i / length
            phase += f0 * (1 + 0.15 * (t - 0.5)) / RATE
            if phase >= 1:
                phase -= 1
                voiced[start + i] = 1.0
            envelope[start + i] = math.sin(math.pi * t) ** 0.7
            gate[vowel][start + i] = 1.0
        fricative = int(RATE * 0.05)
        for i in range(min(fricative, total - start - length)):
            noise[start + length + i] = rng.uniform() * 0.25 * math.sin(math.pi * i / fricative)
    out = silence(total)
    # the pulse train runs through every vowel's three formants and each result is kept where its vowel is gated in
    for vowel, formants in enumerate(vowels):
        for formant, weight, bandwidth in zip(formants, (1.0, 0.6, 0.3), (90, 110, 160)):
            filtered = resonator(voiced, formant, bandwidth)
            for i in range(total):
                if gate[vowel][i]:
                    out[i] += filtered[i] * weight * envelope[i]
    hiss = resonator(noise, 5500, 2500)
    return [o * 6 + h * 4 for o, h in zip(out, hiss)]


def pluck_note(frequency, seconds, rng, decay=0.996):
    length = max(2, int(RATE / frequency))
    line = [rng.uniform() for _ in range(length)]
    out = []
    index = 0
    for _ in range(int(RATE * seconds)):
        nxt = (index + 1) % length
        value = line[index]
        line[index] = decay * 0.5 * (line[index] + line[nxt])
        index = nxt
        out.append(value)
    return out


def pluck(rng):
    notes = [220.0, 261.63, 293.66, 329.63, 392.0, 440.0, 392.0, 329.63]
    left, right = silence(), silence()
    position = 0
    n = 0
    while position < RATE * SECONDS:
        frequency = notes[rng.below(len(notes))] * (2 if n % 5 == 4 else 1)
        sound = pluck_note(frequency, 1.0, rng)
        pan = 0.5 + 0.45 * math.sin(n * 1.7)
        mix_into(left, sound, position, math.cos(pan * math.pi / 2))
        mix_into(right, sound, position, math.sin(pan * math.pi / 2))
        position += int(RATE * (0.25 if n % 3 else 0.5))
        n += 1
    return left, right


def clicks(rng):
    left, right = silence(), silence()
    position = 0
    n = 0
    while position < RATE * SECONDS:
        accent = 1.0 if n % 4 == 0 else 0.45 + 0.1 * rng.below(3)
        length = int(RATE * 0.02)
        burst = [rng.uniform() * math.exp(-i / (RATE * 0.0015)) for i in range(length)]
        ring = resonator(burst, 2000 + 150 * rng.below(5), 400)
        sound = [(b * 0.4 + r * 6) * accent for b, r in zip(burst, ring)]
        pan = 0.5 + 0.4 * math.sin(n * 2.3)
        mix_into(left, sound, position, math.cos(pan * math.pi / 2))
        mix_into(right, sound, position, math.sin(pan * math.pi / 2))
        if n % 9 == 8 and position + int(RATE * 0.115) < len(left):
            left[position + int(RATE * 0.11)] += 0.9  # a single-sample impulse
            right[position + int(RATE * 0.115)] -= 0.9
        position += int(RATE * (0.18 if n % 8 < 5 else 0.09))
        n += 1
    return left, right


def chord_voice(frequency, seconds, partials=7):
    count = int(RATE * seconds)
    out = []
    for i in range(count):
        t = i / RATE
        attack = min(1.0, t / 0.002)
        value = 0.0
        for k in range(1, partials + 1):
            value += math.sin(2 * math.pi * frequency * k * t) / k
        out.append(value * attack * math.exp(-t * 2.2))
    return out


def chords(rng, detune=1.0, progression=0):
    sequences = [
        [(220.0, 261.63, 329.63), (174.61, 220.0, 261.63), (261.63, 329.63, 392.0), (196.0, 246.94, 293.66)],
        [(196.0, 246.94, 293.66), (261.63, 329.63, 392.0), (174.61, 220.0, 261.63), (220.0, 277.18, 329.63)],
    ]
    chord_list = sequences[progression]
    left, right = silence(), silence()
    n = 0
    position = 0
    while position < RATE * SECONDS:
        for frequency in chord_list[n % len(chord_list)]:
            mix_into(left, chord_voice(frequency, 1.0), position, 0.33)
            mix_into(right, chord_voice(frequency * detune, 1.0), position, 0.33)
        position += RATE
        n += 1
    return left, right


def white(rng):
    return [rng.uniform() * 0.4 for _ in range(RATE * SECONDS)]


def pink(rng):
    b0 = b1 = b2 = b3 = b4 = b5 = b6 = 0.0
    out = []
    for _ in range(RATE * SECONDS):
        w = rng.uniform()
        b0 = 0.99886 * b0 + w * 0.0555179
        b1 = 0.99332 * b1 + w * 0.0750759
        b2 = 0.96900 * b2 + w * 0.1538520
        b3 = 0.86650 * b3 + w * 0.3104856
        b4 = 0.55000 * b4 + w * 0.5329522
        b5 = -0.7616 * b5 - w * 0.0168980
        out.append((b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11)
        b6 = w * 0.115926
    return out


def tones():
    out = []
    plan = [(100,), (440,), (1000,), (3000,), (8000,), (15000,), (440, 660), (1000, 1500)]
    for frequencies in plan:
        for i in range(RATE):
            t = i / RATE
            fade = min(1.0, t / 0.01, (1 - t) / 0.01)
            out.append(sum(math.sin(2 * math.pi * f * t) for f in frequencies) * fade * 0.45 / len(frequencies))
    return out


def sweep():
    out = []
    phase = 0.0
    first = RATE * 4
    for i in range(first):
        frequency = 30 * (20000 / 30) ** (i / first)
        phase += 2 * math.pi * frequency / RATE
        out.append(math.sin(phase) * 0.5)
    for i in range(RATE * SECONDS - first):
        frequency = 200 + (16000 - 200) * i / (RATE * SECONDS - first)
        phase += 2 * math.pi * frequency / RATE
        out.append(math.sin(phase) * 0.5)
    return out


def transient(rng):
    left, right = silence(), silence()
    events = [
        (0.50, "noise", 0.05, 0.9), (1.30, "click", 0, 0.9), (1.80, "tone", 0.5, 0.01), (2.60, "square", 0.02, 0.9),
        (3.10, "noise", 0.005, 0.9), (3.70, "noise", 0.3, 0.05), (4.40, "click", 0, 0.9), (4.41, "click", 0, -0.9),
        (5.00, "square", 0.1, 0.5), (5.80, "noise", 0.02, 0.9), (6.20, "tone", 0.4, 0.8), (7.20, "noise", 0.1, 0.9),
    ]
    for start, kind, length, level in events:
        s = int(start * RATE)
        n = int(length * RATE)
        for channel in (left, right):
            if kind == "click":
                channel[s] += level
            else:
                for i in range(n):
                    if kind == "noise":
                        channel[s + i] += rng.uniform() * level
                    elif kind == "tone":
                        channel[s + i] += math.sin(2 * math.pi * 1000 * i / RATE) * level
                    else:
                        channel[s + i] += level if (i // 50) % 2 == 0 else -level
    return left, right


def mono_of(pair):
    return [(a + b) * 0.5 for a, b in zip(*pair)]


def normalized(channels, peak=0.9):
    top = max(max(abs(v) for v in channel) for channel in channels) or 1.0
    scale = peak / top
    return [[v * scale for v in channel] for channel in channels]


def build(name):
    rng = Random(SEED + NAMES.index(name))
    if name == "speech":
        return [speech(rng)]
    if name == "mix":
        a, b, c = mono_of(pluck(rng)), mono_of(clicks(rng)), mono_of(chords(rng))
        return [[x * 0.6 + y * 0.5 + z * 0.6 for x, y, z in zip(a, b, c)]]
    if name == "pluck":
        return list(pluck(rng))
    if name == "clicks":
        return list(clicks(rng))
    if name == "chords":
        return list(chords(rng, 1.003))
    if name == "white":
        return [white(rng), white(rng)]
    if name == "pink":
        return [pink(rng), pink(rng)]
    if name == "tones":
        t = tones()
        return [t, t]
    if name == "sweep":
        s = sweep()
        return [s, s]
    if name == "transient":
        return list(transient(rng))
    if name == "wide":
        left, _ = chords(rng, 1.0, 0)
        _, right = chords(rng, 1.0, 1)
        return [[x + 0.15 * y for x, y in zip(left, pink(rng))], [x + 0.15 * y for x, y in zip(right, pink(rng))]]
    if name == "narrow":
        m = [a * 0.5 + b * 0.5 for a, b in zip(mono_of(chords(rng)), mono_of(clicks(rng)))]
        side = resonator([rng.uniform() for _ in m], 1500, 1500)
        return [[x + 0.04 * y for x, y in zip(m, side)], [x - 0.04 * y for x, y in zip(m, side)]]
    raise SystemExit(f"unknown source {name}")


def pcm16(channels):
    """Interleaved little-endian 16-bit samples of the (already normalized) channels."""
    frames = []
    for values in zip(*channels):
        for v in values:
            frames.append(max(-32768, min(32767, int(round(v * 32767)))))
    return struct.pack(f"<{len(frames)}h", *frames)


def source(name):
    """The 16-bit interleaved PCM of one source, and its channel count."""
    channels = normalized(build(name))
    return pcm16(channels), len(channels)
