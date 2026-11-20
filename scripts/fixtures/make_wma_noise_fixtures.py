#!/usr/bin/env python3
"""Two hand-built Windows Media Audio version 2 files that tell apart the two ways of placing a coded position under noise substitution
(docs/design/wma-standard-format.md, "Coefficients"): the committed ffmpeg wmav2 file external-ffmpeg-wmav2-stereo-44k-32k.wma (44.1 kHz stereo, one
block size, no bit reservoir, codebook pair 0, high bands of 65, 232, 324 and 424 lines from line 819) with the superframes 12 to 31 replaced by one
frame each: mid/side 0, channel 1 coded and channel 2 not, one escape-coded coefficient at position 829.

- flagged-band: the first high band is flagged for noise substitution, so the coefficient lies at line 894 (829 + 65), and a decoder puts a tone there.
- control: no band is flagged, so the coefficient lies at line 829.

The bit syntax is the description's; the Huffman codes are those of the project's Tables.cs. crosscheck_asf_fixtures.py decodes both with ffmpeg as a
program and finds the tone's line. Run by make_asf_fixtures.py, which writes the files and their manifests.
"""

import pathlib
import re

import asf_model

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASF = ROOT / "tests" / "fixtures" / "asf"
SOURCE = "external-ffmpeg-wmav2-stereo-44k-32k.wma"
FIRST, LAST = 12, 32
POSITION = 829
LEVEL = 4000
NAMES = {"flagged-band": "derived-ffmpeg-wmav2-noise-flagged-band", "control": "derived-ffmpeg-wmav2-noise-control"}
DESCRIPTIONS = {
    "flagged-band": "The ffmpeg wmav2 file external-ffmpeg-wmav2-stereo-44k-32k.wma with the superframes 12 to 31 replaced by frames of one escape-coded coefficient at position 829 of channel 1, the first of the four high bands flagged for noise substitution: the coefficient's line is 894, and ffmpeg decodes a tone there. Not an encoder's output.",
    "control": "The same file with the same frames and no band flagged: the coefficient's line is 829. Not an encoder's output.",
}


def table(name, text):
    body = re.search(r"public static readonly \w+\[\] " + name + r"\s*=\s*\[(.*?)\];", text, re.S).group(1)
    return [int(v, 16) if v.strip().lower().startswith("0x") else int(v) for v in re.findall(r"0[xX][0-9a-fA-F]+|\d+", re.sub(r"//[^\n]*", "", body))]


def tables():
    text = (ROOT / "src" / "Encspect.Formats.Wma" / "Tables.cs").read_text(encoding="utf-8")
    return {n: table(n, text) for n in ["CriticalFrequencies", "ExponentCodes", "ExponentLengths", "Coefficient0Codes", "Coefficient0Lengths"]}


class Bits:
    def __init__(self):
        self.bits = []

    def write(self, value, count):
        self.bits += [(value >> i) & 1 for i in range(count - 1, -1, -1)]

    def bytes(self, size):
        assert len(self.bits) <= size * 8, f"the frame needs {len(self.bits)} bits"
        out = bytearray(b"N" * size)
        out[:(len(self.bits) + 7) // 8] = bytes(0 for _ in range((len(self.bits) + 7) // 8))
        for i, bit in enumerate(self.bits):
            if bit:
                out[i // 8] |= 0x80 >> (i % 8)
        return bytes(out)


def band_widths(t, length=2048, rate=44100):
    widths, previous = [], 0
    for a in t["CriticalFrequencies"]:
        p = min(length, ((2 * length * a + 2 * rate) // (4 * rate)) * 4)
        if p > previous:
            widths.append(p - previous)
        previous = p
        if p >= length:
            break
    return widths


def frame(t, flagged):
    w = Bits()
    w.write(0, 1)  # mid/side
    w.write(1, 1)  # channel 1 coded
    w.write(0, 1)  # channel 2 not coded
    w.write(20, 7)  # total gain 21: escape levels are 12 bits
    for band in range(4):  # the high bands of channel 1
        w.write(1 if flagged and band == 0 else 0, 1)
    if flagged:
        w.write(0, 7)  # the noise level of the flagged band
    covered, band, widths = 0, 0, band_widths(t)
    while covered < 2048:  # exponents: a difference of 0 for every band
        w.write(t["ExponentCodes"][60], t["ExponentLengths"][60])
        width = widths[band] if band < len(widths) else 0
        covered += width if width > 0 else 4
        band += 1
    w.write(t["Coefficient0Codes"][0], t["Coefficient0Lengths"][0])  # an escape
    w.write(LEVEL, 12)
    w.write(POSITION, 11)
    w.write(0, 1)
    w.write(t["Coefficient0Codes"][1], t["Coefficient0Lengths"][1])  # the end of the block
    return w.bytes(185)


def build(variant):
    data = bytearray((ASF / SOURCE).read_bytes())
    objects = asf_model.parse(bytes(data))["media_objects"]
    crafted = frame(tables(), variant == "flagged-band")
    for _, _, size, _, ranges in objects[FIRST:LAST]:
        assert size == 185 and len(ranges) == 1
        data[ranges[0][0]:ranges[0][0] + 185] = crafted
    return bytes(data)
