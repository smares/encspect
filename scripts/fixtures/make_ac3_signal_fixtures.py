#!/usr/bin/env python3
"""Hand-built 2/0 AC-3 files with audible content (every bin has mantissa bits), for the paths the encoders' files do not take (docs/formats/ac3.md, "Decoding"):

- ac3-signal-unequal-bandwidth.ac3: no coupling, the two channels with different bandwidths (chbwcod 20 and 50) and all four rematrixing flags set, so that rematrixing
  applies only up to the lower bandwidth (A/52 7.5.4);
- ac3-signal-phase-flags.ac3: coupling with phase flags in use (phsflginu 1, flags set), then a new coupling range with phsflginu 0, whose bands must not be inverted
  (A/52 7.4.1, 5.4.3.10); the coordinates are sent in every block that needs them.

The bit allocation of A/52 7.2.2 is run here (the tables are read from the analyzer's Tables.cs, which carries them as specification data), so each mantissa gets the
bits the parametric allocation gives it; the codes are seeded random values of their quantizers, the dither flags are 0. liba52 decodes the files, and
crosscheck_ac3_pcm.py compares them with it and with FFmpeg's decoder.

    make_ac3_signal_fixtures.py [--check]
"""

import pathlib
import random
import re
import sys

import ac3_model as m
from mp4_boxes import Bits

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "ac3"
TABLES = (ROOT / "src" / "Encspect.Formats.Ac3" / "Tables.cs").read_text(encoding="utf-8")


def table(name):
    body = re.search(r"%s =\s*\[(.*?)\];" % name, TABLES, re.S).group(1)
    body = re.sub(r"//.*", "", body)
    return [int(x, 0) for x in re.findall(r"-?0x[0-9A-Fa-f]+|-?\d+", body)]


MASKTAB, BANDSTART, LOGADD, HEARING48, BAPTAB = (table(n) for n in ("MaskTab", "BandStart", "LogAdd", "Hearing48", "BapTab"))
SDCYCOD, FDCYCOD, SGAINCOD, DBPBCOD, FLOORCOD, FGAINCOD = 2, 1, 1, 2, 7, 4
SLOWDECAY, FASTDECAY, SLOWGAIN, DBPERBIT, FLOOR, FASTGAIN = 0x13, 0x53, 0x4D8, 0x900, -2048, 0x280


def log_add(a, b):
    c = a - b
    address = min(abs(c) >> 1, 255)
    return a + LOGADD[address] if c >= 0 else b + LOGADD[address]


def allocate(exp, start, end, snr, fastleak=0, slowleak=0, coupling=False):
    """7.2.2: the bit allocation pointers of bins start..end-1 for a channel (full bandwidth or coupling) with the given SNR offset (already (csnr-15)*16+fsnr)*4)."""
    psd = {b: 3072 - (exp[b] << 7) for b in range(start, end)}
    band_psd = {}
    j, k = start, MASKTAB[start]
    while True:
        last = min(BANDSTART[k + 1], end)
        band_psd[k] = psd[j]
        j += 1
        for _ in range(j, last):
            band_psd[k] = log_add(band_psd[k], psd[j])
            j += 1
        k += 1
        if end <= last:
            break
    band_start, band_end = MASKTAB[start], MASKTAB[end - 1] + 1
    excite = {}
    if band_start == 0:
        raise NotImplementedError("channels start in band 0 only below the coupling; see low_comp")
    begin, fast, slow = band_start, fastleak, slowleak
    for b in range(begin, band_end):
        fast = max(fast - FASTDECAY, band_psd[b] - FASTGAIN)
        slow = max(slow - SLOWDECAY, band_psd[b] - SLOWGAIN)
        excite[b] = max(fast, slow)
    mask = {}
    for b in range(band_start, band_end):
        if band_psd[b] < DBPERBIT:
            excite[b] += (DBPERBIT - band_psd[b]) >> 2
        mask[b] = max(excite[b], HEARING48[b])
    bap = {}
    i, j = start, MASKTAB[start]
    while True:
        last = min(BANDSTART[j + 1], end)
        mm = mask[j] - snr - FLOOR
        mm = max(mm, 0) & 0x1FE0
        mm += FLOOR
        while i < last:
            address = min(max((psd[i] - mm) >> 5, 0), 63)
            bap[i] = BAPTAB[address]
            i += 1
        j += 1
        if end <= last:
            break
    return bap


def low_comp(a, b0, b1, bin_):
    if bin_ < 7:
        if b0 + 256 == b1:
            a = 384
        elif b0 > b1:
            a = max(0, a - 64)
    elif bin_ < 20:
        if b0 + 256 == b1:
            a = 320
        elif b0 > b1:
            a = max(0, a - 64)
    else:
        a = max(0, a - 128)
    return a


def allocate_full(exp, end, snr):
    """The allocation of a full bandwidth channel (bins 0..end-1), whose excitation starts in band 0 (7.2.2.4)."""
    psd = {b: 3072 - (exp[b] << 7) for b in range(end)}
    band_psd = {}
    j, k = 0, 0
    while True:
        last = min(BANDSTART[k + 1], end)
        band_psd[k] = psd[j]
        j += 1
        for _ in range(j, last):
            band_psd[k] = log_add(band_psd[k], psd[j])
            j += 1
        k += 1
        if end <= last:
            break
    band_end = MASKTAB[end - 1] + 1
    excite = {}
    lc = 0
    lc = low_comp(lc, band_psd[0], band_psd[1], 0)
    excite[0] = band_psd[0] - FASTGAIN - lc
    lc = low_comp(lc, band_psd[1], band_psd[2], 1)
    excite[1] = band_psd[1] - FASTGAIN - lc
    begin, fast, slow = 7, 0, 0
    for b in range(2, 7):
        lc = low_comp(lc, band_psd[b], band_psd[b + 1], b)
        fast = band_psd[b] - FASTGAIN
        slow = band_psd[b] - SLOWGAIN
        excite[b] = fast - lc
        if band_psd[b] <= band_psd[b + 1]:
            begin = b + 1
            break
    for b in range(begin, min(band_end, 22)):
        lc = low_comp(lc, band_psd[b], band_psd[b + 1], b)
        fast = max(fast - FASTDECAY, band_psd[b] - FASTGAIN)
        slow = max(slow - SLOWDECAY, band_psd[b] - SLOWGAIN)
        excite[b] = max(fast - lc, slow)
    for b in range(22, band_end):
        fast = max(fast - FASTDECAY, band_psd[b] - FASTGAIN)
        slow = max(slow - SLOWDECAY, band_psd[b] - SLOWGAIN)
        excite[b] = max(fast, slow)
    mask = {}
    for b in range(band_end):
        if band_psd[b] < DBPERBIT:
            excite[b] += (DBPERBIT - band_psd[b]) >> 2
        mask[b] = max(excite[b], HEARING48[b])
    bap = {}
    i, j = 0, 0
    while True:
        last = min(BANDSTART[j + 1], end)
        mm = max(mask[j] - snr - FLOOR, 0) & 0x1FE0
        mm += FLOOR
        while i < last:
            bap[i] = BAPTAB[min(max((psd[i] - mm) >> 5, 0), 63)]
            i += 1
        j += 1
        if end <= last:
            break
    return bap


def exponent_set(rng, bins, base, shift=0):
    """Slowly varying exponents of `bins` bins for D45 coding: the first one and differences of -2..2 per group of four bins; `shift` raises all of them (6 dB each)."""
    first = base + shift
    deltas = [rng.choice([-1, 0, 0, 1]) for _ in range(bins)]
    exp, groups, value = [first], [], first
    for g in range(0, (bins - 1 + 9) // 12 * 3, 3):
        triple = []
        for d in deltas[g:g + 3]:
            value = min(max(value + d, 3 + shift), 12 + shift)
            triple.append(value)
        groups.append(triple)
    previous, codes = first, []
    exp = [first]
    for triple in groups:
        ds = []
        for v in triple:
            ds.append(v - previous)
            previous = v
            exp += [v] * 4
        codes.append(25 * (ds[0] + 2) + 5 * (ds[1] + 2) + (ds[2] + 2))
    return first, codes, exp[:256]


QUANT = {1: 3, 2: 5, 3: 7, 4: 11, 5: 15}


def write_mantissas(bits, rng, channels):
    """channels: [(bins, bap)] in stream order; groups of bap 1, 2 and 4 are shared across them (7.3.5)."""
    pending = {1: [], 2: [], 4: []}
    sizes = {1: (3, 5, 3), 2: (3, 7, 5), 4: (2, 7, 11)}
    slots = {1: [], 2: [], 4: []}
    plan = []
    for bins, bap in channels:
        for b in bins:
            plan.append(bap[b])
    # first pass: which codes the groups hold, in stream order
    emitted = []
    open_group = {1: None, 2: None, 4: None}
    for pointer in plan:
        if pointer == 0:
            continue
        if pointer in (1, 2, 4):
            count, width, base = sizes[pointer]
            g = open_group[pointer]
            if g is None or len(g[1]) == count:
                g = [None, []]
                open_group[pointer] = g
                emitted.append(["group", pointer, g])
            g[1].append(rng.randrange(QUANT[pointer]))
        elif pointer in (3, 5):
            emitted.append(["fixed", rng.randrange(QUANT[pointer]), 3 if pointer == 3 else 4])
        else:
            width = {14: 14, 15: 16}.get(pointer, pointer - 1)
            emitted.append(["fixed", rng.randrange(1 << width), width])
    for item in emitted:
        if item[0] == "fixed":
            bits.put(item[1], item[2])
        else:
            pointer, (_, codes) = item[1], item[2]
            count, width, base = sizes[pointer]
            codes = codes + [0] * (count - len(codes))
            value = 0
            for c in codes:
                value = value * base + c
            bits.put(value, width)


CSNROFFST = 22


def frame(kind, seed, compr=None, shift=0):
    rng = random.Random(seed)
    fscod, frmsizecod = 0, 34  # 48 kHz, 1,152 words: 448 kbps
    words = m.WORDS[fscod][frmsizecod]
    b = Bits()
    b.put(m.SYNC, 16).put(0, 16).put(fscod, 2).put(frmsizecod, 6)
    b.put(8, 5).put(0, 3).put(2, 3).put(0, 2).put(0, 1)  # bsid 8, bsmod 0, acmod 2, dsurmod 0, lfeon 0
    b.put(27, 5).put(compr is not None, 1)  # dialnorm, compre
    if compr is not None:
        b.put(compr, 8)  # compr: X (4 bits, two's complement) and Y (4 bits), A/52 7.7.2
    b.put(0, 1).put(0, 1)  # langcode, audprodie
    b.put(0, 1).put(1, 1).put(0, 1).put(0, 1).put(0, 1)  # copyrightb, origbs, timecod1e, timecod2e, addbsie
    widths = [20, 50] if kind == "unequal" else [None, None]
    # (cplbegf, cplendf, phsflginu, phase flags) of a coupling strategy sent in the block; None sends none
    plan = [None] * 6 if kind == "unequal" else [(4, 9, 1, 0b101010), None, (6, 11, 0, 0b111111), None, (3, 8, 1, 0b010101), None]
    cplstate = None
    snr = ((CSNROFFST - 15) << 4) << 2
    for block in range(6):
        spec = plan[block]
        b.put(0, 2).put(0, 2).put(0, 1)  # blksw, dithflag, dynrnge
        if kind == "unequal":
            b.put(block == 0, 1)
            if block == 0:
                b.put(0, 1)
        else:
            b.put(spec is not None, 1)
            if spec is not None:
                b.put(1, 1).put(3, 2)  # cplinu, chincpl of both channels
                b.put(spec[2], 1).put(spec[0], 4).put(spec[1], 4)
                b.put(0, 3 + spec[1] - spec[0] - 1)  # every sub-band its own band
                cplstate = spec
        nb = 0 if cplstate is None else 3 + cplstate[1] - cplstate[0]
        if cplstate:
            for _ in range(2):
                b.put(1, 1).put(1, 2)  # cplcoe, mstrcplco
                for _ in range(nb):
                    b.put(rng.randrange(0, 6), 4).put(rng.randrange(16), 4)
            if cplstate[2]:
                b.put(cplstate[3] & ((1 << nb) - 1), nb)
        width = 4 if not cplstate else (2 if cplstate[0] == 0 else 3 if cplstate[0] < 3 else 4)
        b.put(1, 1)
        b.put((0b1111 if kind == "unequal" else 0b0101) & ((1 << width) - 1), width)
        if cplstate:
            b.put(3, 2)
        b.put(3, 2).put(3, 2)
        if not cplstate:
            for w in widths:
                b.put(w, 6)
        exps = {}
        if cplstate:
            start, end = 37 + 12 * cplstate[0], 37 + 12 * (cplstate[1] + 3)
            first = rng.randrange(3, 6)
            b.put(first, 4)
            value, exp, bin_ = first * 2, {}, start
            for _ in range((end - start) // 12):
                ds = []
                for _ in range(3):
                    new = min(max(value + rng.choice([-1, 0, 1]), 3), 20)
                    ds.append(new - value)
                    value = new
                b.put(25 * (ds[0] + 2) + 5 * (ds[1] + 2) + (ds[2] + 2), 7)
                value -= sum(ds)
                for d in ds:
                    value += d
                    for _ in range(4):
                        exp[bin_] = value
                        bin_ += 1
            exps["cpl"] = (exp, start, end)
        for ch in range(2):
            end = 37 + 12 * cplstate[0] if cplstate else (widths[ch] + 12) * 3 + 37
            first, codes, exp = exponent_set(rng, end, rng.randrange(6, 10), shift)
            b.put(first, 4)
            for c in codes:
                b.put(c, 7)
            b.put(0, 2)  # gainrng
            exps[ch] = (dict(enumerate(exp)), 0, end)
        b.put(block == 0, 1)
        if block == 0:
            b.put(SDCYCOD, 2).put(FDCYCOD, 2).put(SGAINCOD, 2).put(DBPBCOD, 2).put(FLOORCOD, 3)
        b.put(1, 1).put(CSNROFFST, 6)
        for _ in range((1 if cplstate else 0) + 2):
            b.put(0, 4).put(4, 3)
        if cplstate:
            b.put(1, 1).put(0, 3).put(0, 3)  # cplleake, cplfleak, cplsleak
        b.put(0, 1).put(0, 1)  # deltbaie, skiple
        channels = []
        for ch in range(2):
            exp, start, end = exps[ch]
            channels.append((range(start, end), allocate_full(exp, end, snr)))
            if cplstate and ch == 0:
                cexp, cstart, cend = exps["cpl"]
                channels.append((range(cstart, cend), allocate(cexp, cstart, cend, snr, 768, 768, True)))
        write_mantissas(b, rng, channels)
    assert b.count <= words * 16 - 16, (b.count, words * 16)
    b.put(0, words * 16 - 16 - b.count)
    b.put(0, 16)
    return m.fix_crc2(m.fix_crc1(b.bytes()))


COMPR_SHIFT = 6  # exponents 6 higher (36 dB quieter): the peak is about -23 dBFS, so that the +13.6 dB of a decoder's RF mode (+7 dB for the dialogue level, +6.02 dB compr, 0.27 dB) stays under full scale


def build(kind, seed, compr=False, switch=False):
    # +6.02 dB (X = 1, Y = 0: 2^(1 + 1) * 0.5) in the first six frames, -6.02 dB (X = -1, Y = 0) in the last six; the switch variant has compre 1 with 0 dB (X = 0, Y = 0) in the first six
    # frames and compre 0 in the last six
    def word(i):
        if switch:
            return 0x00 if i < 6 else None
        return (0x10 if i < 6 else 0xF0) if compr else None
    return b"".join(frame(kind, seed + i, word(i), COMPR_SHIFT if compr or switch else 0) for i in range(12))


DESCRIPTIONS = {
    "ac3-signal-compr-switch.ac3": "The audio of ac3-signal-compr (exponents 6 higher than ac3-signal-unequal-bandwidth's, so 36 dB quieter) with compre set and a compr word of 0 dB (0x00) in the first six frames and compre 0 "
                                   "in the last six, dialnorm 27: to see whether a decoder that picks its output mode by the presence of compr changes its gain at the step, with no compr gain to confuse the steps.",
    "ac3-signal-compr.ac3": "The frames of ac3-signal-unequal-bandwidth with the exponents 6 higher (36 dB quieter, so that the gain of a decoder's RF mode stays under full scale), compre set and a compr word that is +6.02 dB (0x10) in the first six frames and -6.02 dB (0xF0) in the last six, dialnorm 27 "
                            "(A/52 5.4.2.9, 5.4.2.10 and 7.7.2): to see whether a decoder that picks its output mode by the presence of compr changes its gain at the step.",
    "ac3-signal-unequal-bandwidth.ac3": "Twelve 2/0 frames at 48 kHz whose two channels have different bandwidths (chbwcod 20 and 50), all four rematrixing flags set and mantissas in every bin, so that "
                                       "rematrixing must stop at the lower bandwidth (A/52 7.5.4).",
    "ac3-signal-phase-flags.ac3": "Twelve 2/0 frames at 48 kHz with coupling, phase flags in use in two coupling ranges and not in use in a third (phsflginu 0 after a block that had flags), "
                                  "mantissas in every bin (A/52 7.4.1).",
}


def manifest(name, data):
    import json
    frames, at = [], 0
    while at < len(data):
        h = m.header(data, at)
        frames.append([at, h["length"]])
        at += h["length"]
    return {
        "description": DESCRIPTIONS[name], "length": len(data), "format": "AC-3", "frames": frames, "gaps": [],
        "streams": [{"codec": "AC-3", "sample_rate": 48000, "channels": 2, "channel_mode": "2/0", "channel_layout": ["front left", "front right"],
                     "samples": 1536 * len(frames), "is_vbr": False}],
        "fields": {}, "findings": [],
        "crosscheck": {"ffprobe": True, "decode": True, "liba52": True, "skip_insertion": False,
                       "skip": {"skip_insertion": "The check rewrites E-AC-3 frames with skip fields."}},
    }


def main(argv):
    import json
    files = {"ac3-signal-unequal-bandwidth.ac3": build("unequal", 100), "ac3-signal-phase-flags.ac3": build("phase", 200), "ac3-signal-compr.ac3": build("unequal", 100, compr=True),
             "ac3-signal-compr-switch.ac3": build("unequal", 100, switch=True)}
    check = "--check" in argv
    for name, data in files.items():
        path = OUT / name
        manifest_path = OUT / (path.stem + ".expected.json")
        text = json.dumps(manifest(name, data), indent=1) + "\n"
        if check:
            if not path.exists() or path.read_bytes() != data or manifest_path.read_text(encoding="utf-8") != text:
                print(f"{name} differs")
                return 1
        else:
            path.write_bytes(data)
            manifest_path.write_text(text, encoding="utf-8")
            print(name, len(data))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
