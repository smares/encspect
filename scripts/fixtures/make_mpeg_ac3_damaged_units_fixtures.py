#!/usr/bin/env python3
"""Writes the fixtures of tests/fixtures/mpeg-audio/damaged-units and tests/fixtures/ac3/damaged-units: encoder-written files with the stored CRC of one
frame changed, so that frame fails its per-unit check but its data is intact and it still decodes (docs/design/damaged-units.md):

- layer1-crc-changed.mp1: mpeg1-layer1-crc.mp1 with the CRC-16 of its third frame changed (Layer I frames are 384 samples, so the span after the frame covers two);
- external-twolame-crc-stereo-crc-changed.mp2: external-twolame-crc-stereo.mp2 (Layer II) with the CRC-16 of its tenth frame changed;
- external-lame-crc-crc-changed.mp3: external-lame-crc.mp3 (Layer III) with the CRC-16 of its tenth audio frame changed;
- external-ffmpeg-stereo-192k-crc1-crc2-changed.ac3: external-ffmpeg-stereo-192k.ac3 with the CRC1 of its fourth syncframe and the CRC2 of its tenth changed.
"""

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures"

BITRATES = {
    (1, 1): [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
    (1, 2): [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
    (1, 3): [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    (2, 1): [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
    (2, 2): [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    (2, 3): [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
}
RATES = {1: [44100, 48000, 32000], 2: [22050, 24000, 16000]}


def mpeg_frames(data):
    """The protected frames of the first run of frames: (offset, length), skipping a leading ID3v2 tag."""
    at = 0
    if data[:3] == b"ID3":
        at = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
    frames = []
    while at + 4 <= len(data) and data[at] == 0xFF and data[at + 1] & 0xE0 == 0xE0:
        version = 1 if data[at + 1] & 0x18 == 0x18 else 2
        layer = 4 - ((data[at + 1] >> 1) & 3)
        protected = data[at + 1] & 1 == 0
        bitrate = BITRATES[(version, layer)][data[at + 2] >> 4] * 1000
        rate = RATES[version][(data[at + 2] >> 2) & 3]
        padding = (data[at + 2] >> 1) & 1
        if layer == 1:
            length = (12 * bitrate // rate + padding) * 4
        elif layer == 2 or version == 1:
            length = 144 * bitrate // rate + padding
        else:
            length = 72 * bitrate // rate + padding
        frames.append((at, length, protected))
        at += length
    return [(o, n) for o, n, p in frames if p]


def mpeg_change(source, out, index):
    data = bytearray((FIXTURES / "mpeg-audio" / source).read_bytes())
    offset, _ = mpeg_frames(data)[index]
    data[offset + 4] ^= 0x55
    target = FIXTURES / "mpeg-audio" / "damaged-units"
    target.mkdir(exist_ok=True)
    (target / out).write_bytes(data)


def ac3_change(source, out, crc1_frame, crc2_frame):
    data = bytearray((FIXTURES / "ac3" / source).read_bytes())
    frames = json.loads((FIXTURES / "ac3" / (pathlib.Path(source).stem + ".expected.json")).read_text())["frames"]
    start, _ = frames[crc1_frame]
    assert data[start:start + 2] == b"\x0b\x77"
    data[start + 2] ^= 0x55
    start, size = frames[crc2_frame]
    assert data[start:start + 2] == b"\x0b\x77"
    data[start + size - 1] ^= 0x55
    target = FIXTURES / "ac3" / "damaged-units"
    target.mkdir(exist_ok=True)
    (target / out).write_bytes(data)


def main():
    mpeg_change("mpeg1-layer1-crc.mp1", "layer1-crc-changed.mp1", 2)
    mpeg_change("external-twolame-crc-stereo.mp2", "external-twolame-crc-stereo-crc-changed.mp2", 9)
    mpeg_change("external-lame-crc.mp3", "external-lame-crc-crc-changed.mp3", 9)
    ac3_change("external-ffmpeg-stereo-192k.ac3", "external-ffmpeg-stereo-192k-crc1-crc2-changed.ac3", 3, 9)


if __name__ == "__main__":
    main()
