#!/usr/bin/env python3
"""The test signals the WMA fixtures are encoded from: deterministic PCM, so ffmpeg on Linux and
the Windows Media Format SDK on a Windows runner encode the same audio.

    python3 wma_signals.py out.wav --channels 2 --rate 44100 --bits 16 --seconds 3 --kind tones

- "tones": per channel a tone of 330 Hz plus 55 Hz per channel number, with noise bursts of 400
  samples every 9000, at about a third of full scale.
- "quiet": the same tone at a tenth, with faint noise throughout, for the lossless coding, which a
  loud noise burst would make large.

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


def write(path, rate, channels, bits, seconds, kind):
    if bits not in (16, 24):
        raise SystemExit("Bits are 16 or 24.")
    pack = (lambda v: struct.pack("<h", v)) if bits == 16 else (lambda v: struct.pack("<i", v)[:3])
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
    parser.add_argument("--kind", choices=("tones", "quiet"), default="tones")
    args = parser.parse_args()
    write(pathlib.Path(args.output), args.rate, args.channels, args.bits, args.seconds, args.kind)
    return 0


if __name__ == "__main__":
    sys.exit(main())
