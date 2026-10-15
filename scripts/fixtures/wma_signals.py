#!/usr/bin/env python3
"""The test signals the WMA fixtures are encoded from: deterministic PCM, so ffmpeg on Linux and
the Windows Media Format SDK on a Windows runner encode the same audio.

    python3 wma_signals.py out.wav --channels 2 --rate 44100 --bits 16 --seconds 3 --kind tones

- "tones": per channel a tone of 330 Hz plus 55 Hz per channel number, with noise bursts of 400
  samples every 9000, at about a third of full scale.
- "quiet": the same tone at a tenth, with faint noise throughout, for the lossless coding, which a
  loud noise burst would make large.
- "speech": for the speech coder, a second that repeats: a vowel of a pulse train (110 Hz gliding
  to 150 Hz) through three resonances, a pause with faint noise, a fricative of noise, a second
  vowel with other resonances (the pulses 130 Hz gliding to 100 Hz) and a pause. The same for every
  channel.

Bits are 16 or 24. The noise is a linear congruential generator, so the files do not depend on the
interpreter. The WAV is a plain PCM one (the wave module), which the Windows script reads.
"""

import argparse
import math
import pathlib
import struct
import sys
import wave


def samples(rate, channels, bits, seconds, kind):
    """Yields the interleaved samples as integers."""
    full = (1 << (bits - 1)) - 1
    seed = 12345
    for i in range(int(rate * seconds)):
        t = i / rate
        for ch in range(channels):
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
            noise = (seed / 0x7FFFFFFF) * 2 - 1
            tone = math.sin(2 * math.pi * (330 + 55 * ch) * t)
            if kind == "quiet":
                value = 0.1 * tone + 0.004 * noise
            else:
                burst = 0.3 if (i + ch * 1777) % 9000 < 400 else 0
                value = 0.35 * tone + burst * noise
            yield int(max(-1, min(1, value)) * full * 0.98)


# The speech signal: (start, end) in seconds of a one-second cycle, then its kind and the formants (centre Hz, bandwidth Hz).
SPEECH = [
    (0.00, 0.30, "vowel", ((730, 90), (1090, 110), (2440, 160)), (110, 150)),
    (0.30, 0.42, "pause", (), ()),
    (0.42, 0.58, "fricative", (), ()),
    (0.58, 0.88, "vowel", ((270, 60), (2290, 120), (3010, 200)), (130, 100)),
    (0.88, 1.00, "pause", (), ()),
]


def speech(rate, seconds):
    """The speech signal as floats in [-1, 1], one per sample."""
    seed = 54321
    state = {}
    phase = 0.0
    for i in range(int(rate * seconds)):
        t = i / rate
        cycle = t % 1.0
        seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
        noise = (seed / 0x7FFFFFFF) * 2 - 1
        for start, end, kind, formants, pitch in SPEECH:
            if start <= cycle < end:
                break
        if kind == "vowel":
            f0 = pitch[0] + (pitch[1] - pitch[0]) * (cycle - start) / (end - start)
            phase += f0 / rate
            pulse = 1.0 if phase >= 1.0 else 0.0
            phase %= 1.0
            value = 0.0
            for n, (centre, bandwidth) in enumerate(formants):
                radius = math.exp(-math.pi * bandwidth / rate)
                a1 = 2 * radius * math.cos(2 * math.pi * centre / rate)
                a2 = -radius * radius
                y1, y2 = state.get((kind, start, n), (0.0, 0.0))
                y = pulse + a1 * y1 + a2 * y2
                state[(kind, start, n)] = (y, y1)
                value += y * (1 - 0.25 * n)
            envelope = min(1.0, (cycle - start) / 0.02, (end - cycle) / 0.02)
            result = 0.12 * value * envelope
        elif kind == "fricative":
            result = 0.12 * noise
        else:
            result = 0.002 * noise
        yield max(-1.0, min(1.0, result))


def write(path, rate, channels, bits, seconds, kind):
    if bits not in (16, 24):
        raise SystemExit("Bits are 16 or 24.")
    pack = (lambda v: struct.pack("<h", v)) if bits == 16 else (lambda v: struct.pack("<i", v)[:3])
    if kind == "speech":
        full = (1 << (bits - 1)) - 1
        data = b"".join(pack(int(value * full * 0.98)) * channels for value in speech(rate, seconds))
    else:
        data = b"".join(pack(v) for v in samples(rate, channels, bits, seconds, kind))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(bits // 8)
        w.setframerate(rate)
        w.writeframes(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output")
    parser.add_argument("--channels", type=int, default=2)
    parser.add_argument("--rate", type=int, default=44100)
    parser.add_argument("--bits", type=int, default=16)
    parser.add_argument("--seconds", type=float, default=3)
    parser.add_argument("--kind", choices=("tones", "quiet", "speech"), default="tones")
    args = parser.parse_args()
    write(pathlib.Path(args.output), args.rate, args.channels, args.bits, args.seconds, args.kind)
    return 0


if __name__ == "__main__":
    sys.exit(main())
