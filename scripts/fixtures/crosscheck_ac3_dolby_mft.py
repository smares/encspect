#!/usr/bin/env python3
"""Rollout step 7 (docs/design/official-decoder-crosschecks.md): the AC-3 and E-AC-3 decoders' PCM against Dolby's decoder on a Windows machine that has it, by hand.

    py -3 scripts/fixtures/crosscheck_ac3_dolby_mft.py --probe WmaDecodeProbe.exe --pcmdump "dotnet ...Encspect.PcmDump.dll" --encspect "...encspect.exe"

The probe's `ac3-pcm` mode (Ac3Mft.cs, `eac3-pcm` for the `.eac3` files) feeds each syncframe to "Microsoft Dolby Digital Plus Decoder MFT" and writes floats; the comparison is
crosscheck_ac3_coreaudio.py's (strict on a copy with the dither flags cleared, bands on the file repeated otherwise), with the three rules below that the first run measured
(docs/development.md, "Dolby's decoder") applied to what the probe writes, and PINS where Dolby differs from the analyzer for a reason found. The record to keep is the output with
the machine's Windows build and DolbyDecMFT.dll version.

How Dolby's output is read (decode() below):

- It starts DELAY samples after the analyzer's: they are dropped, so the first position is 0.
- It fills the 5.1 layout it was asked for (WAVE order: left, right, center, LFE, left surround, right surround); the channels of the input's layout are taken from it in the
  coded order, and a channel that is all zero is left out unless every channel is (a silent input). It does not decode a dependent substream's channels into that layout.
- It applies dialogue normalization (A/52 5.4.2.8): the output is 2^((dialnorm - 31) / 6) times the decoded audio, which is undone with the first frame's dialnorm. It also applies
  more gain than that on the two real files with dynamic range words (csi_miami_stereo_128_spx, casablanca_aht_mono_64), which is not understood: they are pinned.
"""

import array
import json
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_ac3_coreaudio as c  # noqa: E402
import crosscheck_ac3_pcm as x  # noqa: E402
import unit_list  # noqa: E402

DELAY = 32
# The slots of the 5.1 layout the MFT writes (WAVE order) for each channel of an input layout, in the coded order (A/52 Table 5.8); a single surround channel (acmod 4 and 5) and
# dual mono (0) are not placed, so such an input keeps what is not silent in slot order.
SLOTS = {1: [2], 2: [0, 1], 3: [0, 2, 1], 6: [0, 1, 4, 5], 7: [0, 2, 1, 4, 5]}
LFE_SLOT = 3

# Bands that are not compared, each with its measured reason (the structure of crosscheck_ac3_coreaudio.PINS).
PINS = {
    "casablanca_aht_mono_64": [(None, 0, "Dolby's output is 9.67 dB louder than the analyzer's (dialnorm -27 alone would be 4 dB quieter), constant over the first 48 blocks and between 6.6 and 12.4 dB over "
                                         "the frames, with a correlation of 0.998 after the best constant gain; the cause is not found (the stream's dynamic range gains are 1.26 to 2.77 dB)")],
    "csi_miami_stereo_128_spx": [(None, 0, "Dolby's output is 6.96 dB louder than the analyzer's (dialnorm -27 alone would be 4 dB quieter), constant over the blocks to 0.03 dB, with a correlation of 0.9997; "
                                           "the stream's dynamic range gains are only -0.28 to 1.49 dB, and the cause is not found")],
    "eac3-signal-ecpl": [((1,), 6400, "enhanced coupling's chaos values scale random values added to each bin's angle (A/52 Annex E 3.5.5), which each decoder draws as it likes; channel 0, whose "
                                      "amplitudes the chaos values change alike, agrees")],
    "eac3-signal-transient": [((0,), 8000, "Dolby applies transient pre-noise processing, which the analyzer's twin without it leaves out: in channel 0 it changes 6 runs of 650 to 725 samples "
                                           "where the analyzer's changes 8, five of them the same runs 256 samples later; channel 1, with the same data in every second frame, is not changed "
                                           "(equal to the twin to -109 dB)")],
}
c.PINS = {**c.PINS, **PINS}
c.FIRST_POSITION = 0
# Dolby's dither lies 3.0 to 3.7 dB under the analyzer's (the bands of substreams.eac3 and the channels' RMS): a scaling of 0.5, which 7.3.4 allows, is 3.01 dB under 0.707; the two bands
# that the range of the Core Audio script (-3.6) missed are at -3.63 and -3.66 dB.
c.DITHER_RANGE_DB = (-3.8, c.DITHER_RANGE_DB[1])


def first_frame(data):
    """The offset and header of the first frame of the file."""
    for at in range(len(data) - 8):
        if data[at] == 0x0B and data[at + 1] == 0x77:
            header = x.m.header(data, at)
            if header:
                return at, header
    return None, None


def dialnorm(data):
    """The first frame's dialnorm (5.4.2.8; 0 is read as 31)."""
    at, header = first_frame(data)
    if header is None:
        return 31
    bits = int.from_bytes(data[at + 2:at + 12], "big")
    position = 0

    def take(count):
        nonlocal position
        position += count
        return (bits >> (80 - position)) & ((1 << count) - 1)

    if header["eac3"]:
        for count in (2, 3, 11, 2, 2, 3, 1, 5):
            take(count)
    else:
        for count in (16, 2, 6, 5, 3):
            take(count)
        acmod = take(3)
        if acmod & 1 and acmod != 1:
            take(2)
        if acmod & 4:
            take(2)
        if acmod == 2:
            take(2)
        take(1)
    return take(5) or 31


CHUNK_FRAMES = 1 << 20


def chunks(path, channels, skip):
    """The MFT's output as arrays of whole frames, without the first `skip` frames: the files run to hundreds of millions of samples, so they are not held whole."""
    with open(path, "rb") as stream:
        stream.seek(skip * channels * 4)
        while True:
            block = stream.read(CHUNK_FRAMES * channels * 4)
            if not block:
                return
            values = array.array("f")
            values.frombytes(block[:len(block) // (channels * 4) * channels * 4])
            yield values


def slots_of(path, channels, header):
    """The slots of the MFT's output that the input's layout has, in the coded order (silent ones left out unless all are silent)."""
    layout = SLOTS.get(header["acmod"])
    if channels == 6 and layout is not None:
        slots = layout + ([LFE_SLOT] if header["lfeon"] else [])
    else:
        slots = list(range(channels))
    loud = set()
    for values in chunks(path, channels, DELAY):
        loud.update(slot for slot in slots if slot not in loud and any(values[slot::channels]))
    return [slot for slot in slots if slot in loud] or slots[:2]


def decode(probe, path, out):
    data = pathlib.Path(path).read_bytes()
    tsv = out.with_suffix(".units.tsv")
    tsv.write_text(unit_list.tsv(unit_list.extract_bytes(data)), encoding="utf-8")
    mode = "eac3-pcm" if pathlib.Path(path).suffix == ".eac3" else "ac3-pcm"
    run = subprocess.run([probe, mode, str(path), str(tsv), str(out)], capture_output=True, text=True)
    line = next((l for l in run.stdout.splitlines() if l.startswith("{")), "{}")
    info = json.loads(line)
    if run.returncode != 0 or "error" in info or not out.exists():
        return None, info
    channels = info["channels"]
    _, header = first_frame(data)
    slots = slots_of(out, channels, header or {"acmod": 2, "lfeon": 0})
    gain = 2.0 ** ((31 - dialnorm(data)) / 6.0)
    reduced = array.array("f")
    for values in chunks(out, channels, DELAY):
        block = array.array("f", bytes(4 * (len(values) // channels) * len(slots)))
        for index, slot in enumerate(slots):
            column = values[slot::channels]
            block[index::len(slots)] = array.array("f", (v * gain for v in column)) if gain != 1.0 else column
        reduced.extend(block)
    return reduced.tobytes(), {"channels": len(slots), "frames": len(reduced) // len(slots), "layoutTag": json.dumps({**(info.get("output") or {}), "slots": slots, "dialnormGain": gain}), "status": 0}


c.decode = decode

if __name__ == "__main__":
    sys.exit(c.main())
