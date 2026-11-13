#!/usr/bin/env python3
"""Hand-built 2/0 E-AC-3 files with audible content (every bin has the mantissa bits the bit allocation gives it), for the tools that encoders do not write or write in few
variants (docs/formats/ac3.md, "Decoding"):

- eac3-signal-aht.eac3: the adaptive hybrid transform in both channels, with the four gain adaptive quantization modes in turn, vector quantization (hebap 1 to 7) and gain adaptive
  quantization with the gains 1, 2 and 4 (hebap 8 to 19), tags and large values (ATSC A/52:2018 Annex E 3.4);
- eac3-signal-spx.eac3 and eac3-signal-spx-noise.eac3: spectral extension with an explicit band structure in half of the frames and the default one in the others, new coordinates
  in some blocks, the attenuation filter in one channel, and rematrixing below the extension (Annex E 3.6); the first with the blend 31 (no noise, so that it is comparable sample by
  sample), the second with random blends;
- eac3-signal-ecpl.eac3: enhanced coupling with amplitudes, angles and chaos values, an explicit band structure in half of the frames and new parameters in some blocks (Annex E 3.5).

The bit allocation of A/52 7.2.2 is run here, with the high efficiency table of Annex E for the adaptive hybrid transform; the codes are seeded random values of their quantizers,
the dither flags are 0. FFmpeg decodes all but the last (it does not decode enhanced coupling), and crosscheck_ac3_pcm.py compares the first two with it.

    make_eac3_signal_fixtures.py [--check]
"""

import json
import pathlib
import random
import sys

import ac3_model as m
import make_ac3_signal_fixtures as sig
from mp4_boxes import Bits

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "ac3"
HEBAPTAB = sig.table("HebapTab")
BITS_OF_HEBAP = dict(zip(range(8, 20), [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16]))
VECTOR_BITS = dict(zip(range(1, 8), [2, 3, 4, 5, 7, 8, 9]))
WORDS = 2048
FRAMES = 12
CSNR, FSNR = 24, 8
SNR = (((CSNR - 15) << 4) + FSNR) << 2


def allocate_hebap(exp, end, snr=SNR):
    """The high efficiency pointers of bins 0..end-1 of a full bandwidth channel (Annex E 3.4.3)."""
    saved = sig.BAPTAB
    sig.BAPTAB = HEBAPTAB
    try:
        return sig.allocate_full(exp, end, snr)
    finally:
        sig.BAPTAB = saved


def d15(rng, end, low, high):
    """The first exponent and the 7-bit groups of a D15 exponent set of bins 0..end-1, exponents wandering between low and high; exp indexed by bin."""
    previous = rng.randrange(low, high)
    first, groups, exp = previous, [], {0: previous}
    bin_ = 1
    for _ in range((end - 1) // 3):
        deltas = []
        for _ in range(3):
            new = min(max(previous + rng.choice([-2, -1, -1, 0, 0, 1, 1, 2]), low), high)
            deltas.append(new - previous)
            previous = new
            exp[bin_] = new
            bin_ += 1
        groups.append(25 * (deltas[0] + 2) + 5 * (deltas[1] + 2) + (deltas[2] + 2))
    return first, groups, exp


def d45(rng, start, end, low, high, coupling=False):
    """A D45 exponent set of bins start..end-1: the first value and the groups of 4 bins; for the coupling channel the absolute exponent is doubled (A/52 5.4.3.25)."""
    first = rng.randrange(low // 2 + 1, high // 2) if coupling else rng.randrange(low, high)
    previous = first * 2 if coupling else first
    exp, groups = {}, []
    bin_ = start
    if not coupling:
        exp[bin_] = first
        bin_ += 1
        count = (end - start - 1 + 9) // 12
    else:
        count = (end - start) // 12
    for _ in range(count):
        deltas = []
        for _ in range(3):
            new = min(max(previous + rng.choice([-1, 0, 0, 1]), low), high)
            deltas.append(new - previous)
            previous = new
            for _ in range(4):
                if bin_ < end:
                    exp[bin_] = new
                    bin_ += 1
        groups.append(25 * (deltas[0] + 2) + 5 * (deltas[1] + 2) + (deltas[2] + 2))
    return first, groups, exp


def twos(value, width):
    return value & ((1 << width) - 1)


def write_aht_channel(b, rng, hebap, end, mode):
    """gaqmod, the gains and the vectors and values of one channel (Annex E 2.2.4, 3.4.2, 3.4.4)."""
    b.put(mode, 2)
    endbap = 12 if mode < 2 else 17
    active = [bin_ for bin_ in range(end) if 7 < hebap[bin_] < endbap]
    gains = {}
    if mode in (1, 2):
        for bin_ in active:
            gains[bin_] = rng.choice([1, 2 if mode == 1 else 4])
            b.put(gains[bin_] != 1, 1)
    elif mode == 3:
        values = [rng.choice([1, 2, 4]) for _ in active]
        gains = dict(zip(active, values))
        index = {1: 0, 2: 1, 4: 2}
        padded = [index[g] for g in values] + [0] * (-len(values) % 3)
        for i in range(0, len(padded), 3):
            b.put(9 * padded[i] + 3 * padded[i + 1] + padded[i + 2], 5)
    for bin_ in range(end):
        h = hebap[bin_]
        if h == 0:
            continue
        if h < 8:
            b.put(rng.randrange(1 << VECTOR_BITS[h]), VECTOR_BITS[h])
            continue
        mbits = BITS_OF_HEBAP[h]
        g = gains.get(bin_, 1)
        for _ in range(6):
            if g == 1:
                b.put(twos(rng.randrange(-(1 << (mbits - 1)) + 1, 1 << (mbits - 1)), mbits), mbits)
                continue
            tag_bits = mbits - 1 if g == 2 else mbits - 2
            if rng.random() < 0.6 and tag_bits >= 2:
                limit = (1 << (tag_bits - 1)) - 1
                b.put(twos(rng.randrange(-limit, limit + 1), tag_bits), tag_bits)
            else:
                b.put(1 << (tag_bits - 1), tag_bits)
                b.put(rng.randrange(1 << (mbits - 1 if g == 2 else mbits)), mbits - 1 if g == 2 else mbits)
    return gains


def bsi(b, strmtyp=0, chanmap=None):
    b.put(m.SYNC, 16).put(strmtyp, 2).put(0, 3).put(WORDS - 1, 11).put(0, 2).put(3, 2).put(2, 3).put(0, 1).put(16, 5)
    b.put(27, 5).put(0, 1)  # dialnorm, compre
    if strmtyp == 1:
        b.put(chanmap is not None, 1)  # chanmape
        if chanmap is not None:
            b.put(chanmap, 16)
    b.put(0, 1).put(0, 1)  # mixmdate, infomdate
    b.put(0, 1)  # addbsie (strmtyp 0 with six blocks has no convsync)


def audfrm_head(b, ahte=0, spxattene=0, transproce=0):
    b.put(1, 1).put(ahte, 1).put(0, 2)  # expstre, ahte, snroffststr 0
    b.put(transproce, 1).put(0, 1).put(1, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(spxattene, 1)  # transproce, blkswe, dithflage, bamode, frmfgaincode, dbaflde, skipflde, spxattene


def finish(b):
    assert b.count <= WORDS * 16 - 16, (b.count, WORDS * 16)
    b.put(0, WORDS * 16 - 16 - b.count)
    b.put(0, 16)
    return m.fix_crc2(b.bytes())


def aht_frame(seed, mode, csnr=CSNR, narrow=False):
    """One frame; seeds whose exponents give a bin the pointer 4 are skipped, whose vectors FFmpeg 6.1.1 reads from the next row of Table E.3.4 (docs/development.md, "E-AC-3 decoding")."""
    while True:
        frame = aht_frame_of(seed, mode, csnr, narrow)
        if frame is not None:
            return frame
        seed += 1000


def aht_frame_of(seed, mode, csnr, narrow):
    rng = random.Random(seed)
    snr = (((csnr - 15) << 4) + FSNR) << 2
    b = Bits()
    bsi(b)
    audfrm_head(b, ahte=1)
    b.put(0, 1)  # cplinu of block 0
    for _ in range(5):
        b.put(0, 1)  # cplstre of blocks 1 to 5
    for block in range(6):
        for _ in range(2):
            b.put(1 if block == 0 else 0, 2)  # chexpstr: D15 in block 0, reuse after
    for _ in range(2):
        b.put(0, 5)  # converter exponent strategies
    b.put(1, 1).put(1, 1)  # chahtinu of both channels, each of which has one exponent set
    b.put(csnr, 6).put(FSNR, 4)
    b.put(0, 1)  # blkstrtinfoe
    widths = [rng.randrange(30, 40), rng.randrange(30, 40)] if narrow else [rng.randrange(40, 61), rng.randrange(40, 61)]
    sets = []
    for block in range(6):
        b.put(0, 2).put(0, 1)  # dithflags, dynrnge
        b.put(0, 1)  # spxinu of block 0, spxstre of the others
        b.put(0, 4) if block == 0 else b.put(0, 1)  # rematflg of the four bands, rematstr
        if block == 0:
            for w in widths:
                b.put(w, 6)
            for ch in range(2):
                end = 37 + 3 * (widths[ch] + 12)
                first, groups, exp = d15(rng, end, 0, 16)
                b.put(first, 4)
                for g in groups:
                    b.put(g, 7)
                b.put(0, 2)
                sets.append((exp, end))
        b.put(0, 1)  # convsnroffste
        if block == 0:
            pointers = [allocate_hebap(exp, end, snr) for exp, end in sets]
            if any(4 in p.values() for p in pointers):
                return None
            for (exp, end), hebap in zip(sets, pointers):
                write_aht_channel(b, rng, hebap, end, mode)
    return finish(b)


def spx_frame(seed, explicit_structure, attenuation, new_coordinates, blend=None, strmtyp=0, chanmap=None, transients=None):
    rng = random.Random(seed)
    b = Bits()
    bsi(b, strmtyp, chanmap)
    audfrm_head(b, spxattene=1, transproce=int(transients is not None))
    b.put(0, 1)
    for _ in range(5):
        b.put(0, 1)
    for block in range(6):
        for _ in range(2):
            b.put(3 if block == 0 else 0, 2)  # D45 in block 0
    if strmtyp == 0:
        for _ in range(2):
            b.put(0, 5)
    b.put(CSNR, 6).put(FSNR, 4)
    for transient in transients or ():  # chintransproc, transprocloc and transproclen of each channel
        if transient is None:
            b.put(0, 1)
        else:
            b.put(1, 1).put(transient[0], 10).put(transient[1], 8)
    b.put(0, 1).put(1, 1).put(attenuation, 5)  # chinspxatten of channel 0 off and of channel 1 on, with its code
    b.put(0, 1)  # blkstrtinfoe
    spxbegf, spxendf, spxstrtf = 2, 4, 1  # begin sub-band 4 (bin 73), end sub-band 11 (bin 157), copy from bin 37
    begin_band, end_band = spxbegf + 2, spxendf + 5 if spxendf < 3 else spxendf * 2 + 3
    end = 25 + 12 * begin_band
    structure = [int(i % 2 == 0) for i in range(end_band - begin_band - 1)] if explicit_structure else m.DEFAULT_SPX_BANDS[begin_band + 1:end_band]
    bands = end_band - begin_band - sum(structure)
    sets = []
    for block in range(6):
        b.put(0, 2).put(0, 1)  # dithflags, dynrnge
        if block == 0:
            b.put(1, 1).put(3, 2)  # spxinu, chinspx of both channels
            b.put(spxstrtf, 2).put(spxbegf, 3).put(spxendf, 3).put(explicit_structure, 1)
            if explicit_structure:
                for bit in structure:
                    b.put(bit, 1)
        else:
            b.put(0, 1)  # spxstre
        for ch in range(2):
            fresh = block == 0 or (new_coordinates and block == 3)
            if block != 0:
                b.put(fresh, 1)
            if fresh:
                b.put(rng.randrange(0, 20) if blend is None else blend, 5).put(rng.randrange(0, 3), 2)
                for _ in range(bands):
                    b.put(rng.randrange(1, 12), 4).put(rng.randrange(4), 2)
        if block == 0:
            b.put(0b1010, 4)  # rematflg: four bands, those from bin 61 on stop at the extension
        else:
            b.put(0, 1)
        if block == 0:
            for ch in range(2):
                first, groups, exp = d45(rng, 0, end, 2, 12)
                b.put(first, 4)
                for g in groups:
                    b.put(g, 7)
                b.put(0, 2)
                sets.append(exp)
        if strmtyp == 0:
            b.put(0, 1)  # convsnroffste
        channels = [(range(end), sig.allocate_full(exp, end, SNR)) for exp in sets]
        sig.write_mantissas(b, rng, channels)
    return finish(b)


def ecpl_frame(seed, explicit_structure, new_parameters):
    rng = random.Random(seed)
    b = Bits()
    bsi(b)
    audfrm_head(b)
    b.put(1, 1)  # cplinu of block 0
    for _ in range(5):
        b.put(0, 1)
    for block in range(6):
        b.put(3 if block == 0 else 0, 2)  # cplexpstr
        for _ in range(2):
            b.put(3 if block == 0 else 0, 2)
    for _ in range(2):
        b.put(0, 5)
    b.put(CSNR, 6).put(FSNR, 4)
    b.put(0, 1)
    ecplbegf, ecplendf = 4, 5  # sub-band 6 (bin 61) to 12 (bin 133)
    begin, end_sub = ecplbegf + 2, ecplendf + 7
    low, high = m.ECPL_BINS[begin], m.ECPL_BINS[end_sub]
    first_flag = max(9, begin + 1)
    structure = {sub: 0 for sub in range(first_flag)}
    if explicit_structure:
        for sub in range(first_flag, end_sub):
            structure[sub] = int(sub % 2 == 1)
    else:
        for sub in range(end_sub):
            structure[sub] = m.DEFAULT_ECPL_BANDS[sub] if sub >= first_flag else 0
    nb = sum(1 for sub in range(begin, end_sub) if sub == begin or structure[sub] == 0)
    sets = {}
    for block in range(6):
        b.put(0, 2).put(0, 1)  # dithflags, dynrnge
        b.put(0, 1)  # spxinu / spxstre
        if block == 0:
            b.put(1, 1)  # ecplinu
            b.put(ecplbegf, 4).put(ecplendf, 4).put(explicit_structure, 1)
            if explicit_structure:
                for sub in range(first_flag, end_sub):
                    b.put(structure[sub], 1)
        b.put(0, 1)  # ecplangleintrp
        fresh = block == 0 or (new_parameters and block == 3)
        for ch in range(2):
            if block != 0:
                b.put(fresh, 1)  # ecplparam1e
                if ch > 0:
                    b.put(fresh, 1)  # ecplparam2e
            if fresh:
                for _ in range(nb):
                    b.put(rng.randrange(0, 30), 5)
                if ch > 0:
                    for _ in range(nb):
                        b.put(rng.randrange(64), 6).put(rng.randrange(8), 3)
            if ch > 0:
                b.put(rng.randrange(2) if fresh else 0, 1)  # ecpltrans
        if block == 0:
            b.put(0b101, 3)  # rematflg: three bands for ecplbegf 4
        else:
            b.put(0, 1)
        if block == 0:
            first, groups, exp = d45(rng, low, high, 4, 14, coupling=True)
            b.put(first, 4)
            for g in groups:
                b.put(g, 7)
            sets["cpl"] = exp
            for ch in range(2):
                first, groups, exp = d45(rng, 0, low, 2, 12)
                b.put(first, 4)
                for g in groups:
                    b.put(g, 7)
                b.put(0, 2)
                sets[ch] = exp
        b.put(0, 1)  # convsnroffste
        if block == 0:
            b.put(0, 3).put(0, 3)  # cplfleak, cplsleak
        else:
            b.put(0, 1)  # cplleake
        channels = [(range(low), sig.allocate_full(sets[0], low, SNR)),
                    (range(low, high), sig.allocate(sets["cpl"], low, high, SNR, 768, 768, True)),
                    (range(low), sig.allocate_full(sets[1], low, SNR))]
        sig.write_mantissas(b, rng, channels)
    return finish(b)


def transients(i):
    """Frame i's transient pre-noise data of the two channels: a location in steps of 4 samples (a few lie in the next frame) and a length."""
    first = ((150 + (47 * i)) % 230 + 150, (20 + (11 * i)) % 200) if i % 5 != 4 else (400 + i, 90)
    return [first, ((260 + (13 * i)) % 120 + 130, 60 + i) if i % 2 == 1 else None]


FILES = {
    "eac3-signal-aht.eac3": lambda: b"".join(aht_frame(300 + i, i % 4, 28 if i % 4 == 3 else CSNR, i % 4 == 3) for i in range(FRAMES)),
    "eac3-signal-spx.eac3": lambda: b"".join(spx_frame(400 + i, i % 2 == 1, (7 * i) % 32, i % 3 == 2, blend=31) for i in range(FRAMES)),
    "eac3-signal-dependent.eac3": lambda: b"".join(spx_frame(700 + i, i % 2 == 1, (3 * i) % 32, False, blend=31) + spx_frame(800 + i, False, 5, False, blend=31, strmtyp=1, chanmap=0x0200)
                                                    for i in range(FRAMES)),
    "eac3-signal-transient.eac3": lambda: b"".join(spx_frame(900 + i, i % 2 == 1, (2 * i) % 32, False, blend=31, transients=transients(i)) for i in range(FRAMES)),
    "eac3-signal-spx-noise.eac3": lambda: b"".join(spx_frame(600 + i, i % 2 == 1, (5 * i) % 32, i % 3 == 2) for i in range(FRAMES)),
    "eac3-signal-ecpl.eac3": lambda: b"".join(ecpl_frame(500 + i, i % 2 == 1, i % 3 == 2) for i in range(FRAMES)),
}

DESCRIPTIONS = {
    "eac3-signal-aht.eac3": "Twelve 2/0 E-AC-3 frames at 48 kHz whose two channels use the adaptive hybrid transform with every gain adaptive quantization mode in turn, vector quantization, "
                            "gains of 1, 2 and 4, tags and large values, and mantissas in every bin (A/52 Annex E 3.4).",
    "eac3-signal-spx.eac3": "Twelve 2/0 E-AC-3 frames at 48 kHz with spectral extension from bin 73 to 157 in both channels, the default band structure or an explicit one, new coordinates in "
                            "block 3 of some frames, the attenuation filter in the right channel and rematrixing below the extension, with mantissas in every bin and the blend 31, so that "
                            "only the copy and its filter and scaling remain and the decoded samples are comparable one by one (A/52 Annex E 3.6).",
    "eac3-signal-dependent.eac3": "Twelve access units of a 2/0 independent substream (spectral extension, no noise) and a dependent substream with the channel map of the back left and back right pair, also 2/0: "
                                  "a program of four channels, which decoders that read dependent substreams give in the order front left, front right, back left, back right (A/52 Annex E 3.8).",
    "eac3-signal-transient.eac3": "Twelve 2/0 E-AC-3 frames at 48 kHz like the spectral extension ones, with transient pre-noise processing data for the left channel of every frame and the right channel of every second one, "
                                  "some transients in the next frame (A/52 Annex E 3.7).",
    "eac3-signal-spx-noise.eac3": "The same twelve 2/0 E-AC-3 frames with other seeds and a random blend in every set of coordinates, so that the extension blends in noise (A/52 Annex E 3.6.4.2).",
    "eac3-signal-ecpl.eac3": "Twelve 2/0 E-AC-3 frames at 48 kHz with enhanced coupling from bin 61 to 133, the default band structure or an explicit one, new amplitudes, angles and chaos "
                             "values in block 3 of some frames, and mantissas in every bin (A/52 Annex E 3.5).",
}


NO_REFERENCE = {
    "eac3-signal-ecpl.eac3": "FFmpeg does not decode enhanced coupling.",
    "eac3-signal-transient.eac3": "FFmpeg does not apply transient pre-noise processing, whose cross-fade windows the specification leaves open.",
    "eac3-signal-spx-noise.eac3": "The noise of spectral extension comes from a generator of each decoder, so the samples differ; the level of the noise equals FFmpeg's (docs/development.md, \"E-AC-3 decoding\").",
}


def manifest(name, data):
    frames, at = [], 0
    while at < len(data):
        h = m.header(data, at)
        frames.append([at, h["length"]])
        at += h["length"]
    decodes = name in ("eac3-signal-aht.eac3", "eac3-signal-spx.eac3")
    if name == "eac3-signal-dependent.eac3":
        independent = [f for f in frames if m.header(data, f[0])["strmtyp"] == 0]
        return {
            "description": DESCRIPTIONS[name], "length": len(data), "format": "AC-3", "frames": frames, "gaps": [],
            "streams": [{"codec": "E-AC-3", "sample_rate": 48000, "channels": 4, "channel_mode": "2/0", "channel_layout": ["front left", "front right", "back left", "back right"],
                         "samples": 1536 * len(independent), "is_vbr": False}],
            "fields": {}, "findings": [],
            "crosscheck": {"ffprobe": True, "ffprobe_fields": "channels", "ffprobe_bitrate": False, "decode": False, "skip_insertion": False,
                           "skip": {"decode": "FFmpeg decodes the independent substream only (its 2 channels); Core Audio's output is the reference for the four (macOS job).",
                                    "ffprobe_fields": "FFmpeg reports the independent substream's channels.", "ffprobe_bitrate": "FFmpeg gives no bitrate for frames it cannot decode.",
                                    "skip_insertion": "The check rewrites E-AC-3 frames with skip fields."}},
        }
    return {
        "description": DESCRIPTIONS[name], "length": len(data), "format": "AC-3", "frames": frames, "gaps": [],
        "streams": [{"codec": "E-AC-3", "sample_rate": 48000, "channels": 2, "channel_mode": "2/0", "channel_layout": ["front left", "front right"],
                     "samples": 1536 * len(frames), "is_vbr": False}],
        "fields": {}, "findings": [],
        "crosscheck": {"ffprobe": True, "ffprobe_bitrate": name != "eac3-signal-ecpl.eac3", "decode": decodes, "skip_insertion": False,
                       "skip": {"skip_insertion": "The check rewrites E-AC-3 frames with skip fields.",
                                **({} if decodes else {"decode": NO_REFERENCE[name]}),
                                **({"ffprobe_bitrate": "FFmpeg gives no bitrate for frames it cannot decode."} if name == "eac3-signal-ecpl.eac3" else {})}},
    }


def main(argv):
    check = "--check" in argv
    for name, build in FILES.items():
        data = build()
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
