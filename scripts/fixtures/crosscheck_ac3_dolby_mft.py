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
  a different gain on the three real files from Dolby's encoder (buggy_aht_5.1_384, csi_miami_stereo_128_spx, casablanca_aht_mono_64), which carry compr in every frame: -20 dBFS as the
  dialogue level plus the frame's compr (docs/development.md, "Dolby's decoder"). They are pinned.
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
    "casablanca_aht_mono_64": [(None, 0, "these files carry compr (compre) in every frame, and Dolby's gain follows it: -20 dBFS as the dialogue level (dialnorm -27 gives +7 dB, not the -4 dB of the dialnorm rule) plus "
                                         "the frame's compr within 0.35 dB, 6.66 to 12.35 dB over the file (docs/development.md, \"Dolby's decoder\"), where the analyzer applies no dynamic range control")],
    "csi_miami_stereo_128_spx": [(None, 0, "these files carry compr (compre) in every frame, and Dolby's gain follows it: -20 dBFS as the dialogue level (dialnorm -27 gives +7 dB, not the -4 dB of the dialnorm rule) plus "
                                           "the frame's compr within 0.35 dB, 6.15 to 8.23 dB over the file (docs/development.md, \"Dolby's decoder\"), where the analyzer applies no dynamic range control")],
    "buggy_aht_5.1_384": [(None, 0, "these files carry compr (compre) in every frame, and Dolby's gain follows it: -20 dBFS as the dialogue level (dialnorm 31 gives +11 dB, not 0) plus the frame's compr "
                                    "within 0.35 dB, 10.98 to 17.31 dB over the file (docs/development.md, \"Dolby's decoder\"), where the analyzer applies no dynamic range control")],
    "eac3-signal-ecpl": [((1,), 6400, "enhanced coupling's chaos values scale random values added to each bin's angle (A/52 Annex E 3.5.5), which each decoder draws as it likes; channel 0, whose "
                                      "amplitudes the chaos values change alike, agrees")],
    "eac3-signal-transient": [((0,), 8000, "Dolby applies transient pre-noise processing at 5 of the first 10 transients of channel 0 (those whose location, 656 to 1,636 samples, lies late in the frame or in the next one) "
                                           "and none of channel 1; where it does, with the analyzer's windows and with the locations 256 samples (one block) later the output equals Dolby's to -59 to -77 dB. "
                                           "A/52 3.7.2 counts the location from the first decoded PCM sample of the frame and says nothing of a delay, so this is not taken over (docs/development.md)")],
}
c.PINS = {**c.PINS, **PINS}
c.FIRST_POSITION = 0


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
