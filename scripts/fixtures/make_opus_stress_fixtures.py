#!/usr/bin/env python3
"""Writes the Opus streams that stress the entropy pass and the decoder (malformed frames), into tests/fixtures/fuzz as .bin files that FuzzRegressionTests analyzes within 20 s
each and crosscheck_opus_pcm.py compares with libopus:

- opus-random-frames-every-config.bin: for each of the 32 configurations and for mono and stereo, frames of seeded random bytes of 1, 2, 7, 60 and 400 bytes (the range
  coder reads what it finds: most frames end in an overrun, which libopus refuses, or in odd symbols);
- opus-extreme-frames.bin: frames of 0xFF and of 0x00 bytes of every length from 1 to 20 and of 1,275, in the configurations of each mode (large pulses, gains and spreads);
- opus-random-frames-51.bin: six channels in mapping family 1 (two coupled and two single streams) of random frames in the self-delimited framing the streams but the last use.

    make_opus_stress_fixtures.py [--check]
"""

import pathlib
import random
import sys

import ogg_model
import vorbis_opus_model as v

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "fuzz"


def pages(serial, packets):
    granules = [0, 0] + [960 * (i + 1) for i in range(len(packets) - 2)]
    cuts = [1, 1] + [40] * (len(packets) // 40 + 1)
    return b"".join(ogg_model.page(p.serial, p.sequence, p.granule, p.flags, p.lacing, p.body)[0] for p in ogg_model.pages_for(serial, packets, granules, cuts))


def stream(channels, packets, **head):
    return pages(11, [v.opus_head(channels, **head), v.opus_tags(b"stress", [])] + packets)


def random_frames():
    rng = random.Random(6716)
    packets = []
    for stereo in (False, True):
        for config in range(32):
            for length in (1, 2, 7, 60, 400):
                body = bytes(rng.randrange(256) for _ in range(length))
                packets.append(bytes([(config << 3) | (4 if stereo else 0)]) + body)
    return stream(2, packets)


def extreme_frames():
    packets = []
    for fill in (0xFF, 0x00):
        for config in (0, 4, 8, 12, 14, 16, 20, 24, 28, 31):
            for length in list(range(1, 21)) + [1275]:
                packets.append(v.opus_packet(config, True, [length], fill=fill))
    return stream(2, packets)


def surround():
    rng = random.Random(8251)
    packets = []
    for _ in range(300):
        config = rng.randrange(32)
        parts = []
        for index in range(4):
            length = rng.randrange(1, 90)
            body = bytes(rng.randrange(256) for _ in range(length))
            toc = bytes([(config << 3) | (4 if index < 2 else 0)])
            parts.append(toc + v.frame_length(length) + body if index < 3 else toc + body)
        packets.append(b"".join(parts))
    return stream(6, packets, family=1, streams=4, coupled=2, mapping=[0, 4, 1, 2, 3, 5])


FILES = {
    "opus-random-frames-every-config.bin": random_frames,
    "opus-extreme-frames.bin": extreme_frames,
    "opus-random-frames-51.bin": surround,
}


def main(argv):
    check = argv[1:] == ["--check"]
    for name, build in FILES.items():
        data = build()
        path = OUT / name
        if check:
            if not path.exists() or path.read_bytes() != data:
                print(f"{name} differs from what the script writes")
                return 1
        else:
            path.write_bytes(data)
            print(f"{name}: {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
