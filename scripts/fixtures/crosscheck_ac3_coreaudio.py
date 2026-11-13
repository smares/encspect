#!/usr/bin/env python3
"""Compares the PCM that Encspect's AC-3 and E-AC-3 decoders deliver with Core Audio's, on macOS (docs/development.md, "AC-3 decoding"; rollout step 5 of
docs/design/official-decoder-crosschecks.md), and decodes the damage variants of derive_variants.py ac3 with it.

    crosscheck_ac3_coreaudio.py --probe BUILD/audiotoolbox_probe [--pcmdump CMD] [--encspect CMD] [--keep DIR] [FILE ...]

The probe's `pcm` mode reads a file with ExtAudioFile as interleaved floats. As crosscheck_ac3_pcm.py does, each encoder-written file is compared twice with
compare_pcm.py: sample by sample on a copy with its dither flags cleared (the design's bounds), and, if that fails, by the energy of bands on the file repeated
(whatever Core Audio's dither is, bands of noise are compared by energy). The first run found Core Audio's output at the coded stage's position 256, in the coded channel order
at gain 1, so that alignment is fixed (FIRST_POSITION) and both passes must hold; where one fails, the lines add the correlation diagnostics, the blocks that miss most (strict) or the
bands (dithered), so that a difference at one block, such as FFmpeg's in block 47 of the block switching file, shows as that block's errors. A file is ok only if every pass holds. The variants (cut, flipped, dropped, repeated frames) are only decoded and their frame counts
listed beside the analyzer's: Core Audio conceals damage, the analyzer does not, so no bound applies (the lead's rule of 2026-11-01). The encoder-written E-AC-3 files (`external-*.eac3`) are
compared by the energy of bands on the file repeated only (their dither flags are mostly absent, so there is no strict pass); the first run records what Core Audio does with them, as the AC-3
run did (its alignment, channel order and gain).

Exit status 0 where every file passes, 1 where one fails, 2 for a usage error.
"""

import argparse
import array
import json
import os
import math
import pathlib
import shlex
import subprocess
import sys
import tempfile
import traceback

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_ac3_pcm as x  # noqa: E402
import derive_variants  # noqa: E402

# (name, channel order of the reference's output in the coded order: reference channel i is coded channel ORDER[i] ... as indexes into the reference's channels
# for each coded channel, first position of the reference's samples at the coded stage). Core Audio's 5.1 is L R C LFE Ls Rs like FFmpeg's, or the coded order.
ORDERS = {
    1: {"coded": [0]},
    2: {"coded": [0, 1]},
    3: {"coded": [0, 1, 2]},
    4: {"coded": [0, 1, 2, 3]},
    5: {"coded": [0, 1, 2, 3, 4], "smpte": [0, 2, 1, 3, 4]},
    6: {"coded": [0, 1, 2, 3, 4, 5], "smpte": [0, 2, 1, 4, 5, 3]},
}
FIRST_POSITIONS = (288, 0)


def candidates(channels):
    """[(label, order, first position)] to try: every order known for the channel count at every first position."""
    return [(f"{name} order, first position {first}", order, first) for name, order in ORDERS.get(channels, {"coded": list(range(channels))}).items() for first in FIRST_POSITIONS]


def reorder(raw, channels, order):
    """The interleaved floats with the channels moved so that output channel c is the reference's channel order[c]."""
    values = array.array("f")
    values.frombytes(raw)
    out = array.array("f")
    for n in range(len(values) // channels):
        for c in order:
            out.append(values[n * channels + c])
    return out.tobytes()


# AudioToolbox's kAudioChannelLabel_* values, as the names the analyzer's layouts use (docs/formats/ac3.md, "Channel layout").
APPLE_LABELS = {1: "front left", 2: "front right", 3: "front center", 4: "LFE", 5: "side left", 6: "side right", 7: "front left of center", 8: "front right of center",
                9: "back center", 10: "side left direct", 11: "side right direct", 12: "top center", 13: "top front left", 14: "top front center", 15: "top front right",
                16: "top back left", 17: "top back center", 18: "top back right", 33: "back left", 34: "back right", 35: "front wide left", 36: "front wide right", 37: "LFE2"}


def map_reference(raw, info, dump):
    """Core Audio's channels placed by speaker, where its channel count is not the analyzer's: the reference's channels that the analyzer's stream names, in the analyzer's order,
    with the lines that print the labels Core Audio gives, and what the channels the analyzer's stream does not have carry. None where a speaker is not found."""
    names = json.loads(dump.read_text(encoding="utf-8"))["streams"][0].get("channelLayout") or []
    labels = info.get("labels") or []
    lines = [f"  Core Audio's channels ({info.get('layoutName') or 'no name'}, tag {info.get('layoutTag')}): " + (", ".join(f"{label} = {APPLE_LABELS.get(label, 'unknown')}" for label in labels) or "no labels"),
             f"  the analyzer's channels: {', '.join(names) or 'no layout'}"]
    reference_names = [APPLE_LABELS.get(label, f"label {label}") for label in labels]
    if not names or len(reference_names) != info["channels"] or any(name not in reference_names for name in names):
        lines.append("  no mapping by speaker: a speaker of the analyzer's stream is not among Core Audio's labels")
        return None, lines
    picks = [reference_names.index(name) for name in names]
    values = floats(raw)
    count = info["channels"]
    reduced = array.array("f")
    for n in range(len(values) // count):
        for pick in picks:
            reduced.append(values[n * count + pick])
    for j in range(count):
        if j not in picks:
            column = values[j::count]
            rms = (sum(v * v for v in column) / max(1, len(column))) ** 0.5
            lines.append(f"  Core Audio's {reference_names[j]} (channel {j}) is not in the analyzer's stream: RMS {rms:.3g} of full scale")
    return reduced.tobytes(), lines


def subset_dump(dump, keep):
    """The analyzer's dump cut down to the channels in keep (their indexes), written beside it."""
    manifest = json.loads(dump.read_text(encoding="utf-8"))
    stream = manifest["streams"][0]
    names = stream.get("channelLayout") or []
    our_count = stream["channels"]
    mine = floats((dump.parent / stream["pcm"]).read_bytes())
    cut = array.array("f")
    for n in range(len(mine) // our_count):
        for i in keep:
            cut.append(mine[n * our_count + i])
    (dump.parent / "common.s0.pcm").write_bytes(cut.tobytes())
    manifest["streams"][0] = {**stream, "channels": len(keep), "channelLayout": [names[i] for i in keep] if names else [], "pcm": "common.s0.pcm"}
    common = dump.parent / "common.pcm.json"
    common.write_text(json.dumps(manifest), encoding="utf-8")
    return common


def common_speakers(raw, info, dump):
    """Where Core Audio's labels name only some of the analyzer's speakers (an AC-3 core whose dependent substreams it does not decode comes back as the core's channels): the
    analyzer's dump cut down to the speakers both have, and Core Audio's output for them in the same order, with the lines that say which speakers are missing; None where none is shared."""
    manifest = json.loads(dump.read_text(encoding="utf-8"))
    stream = manifest["streams"][0]
    names = stream.get("channelLayout") or []
    reference_names = [APPLE_LABELS.get(label, f"label {label}") for label in info.get("labels") or []]
    keep = [i for i, name in enumerate(names) if name in reference_names]
    if not keep or len(keep) == len(names) or len(reference_names) != info["channels"]:
        return None
    count = info["channels"]
    common = subset_dump(dump, keep)
    values = floats(raw)
    picks = [reference_names.index(names[i]) for i in keep]
    reduced = array.array("f")
    for n in range(len(values) // count):
        for pick in picks:
            reduced.append(values[n * count + pick])
    lines = [f"  Core Audio's output has only {', '.join(reference_names)}: the analyzer's {', '.join(names[i] for i in range(len(names)) if i not in keep)} are not in it, so only {', '.join(names[i] for i in keep)} are compared"]
    return common, reduced.tobytes(), lines


def correlation_map(ours, our_channels, reference, reference_channels, first, window=4096):
    """For each of the analyzer's channels the reference channel it correlates with most at the aligned position, where Core Audio gives no usable labels: (the picks, the
    lines that print the matrix and the choice), or None where a channel correlates with nothing (below 0.3) or two take the same reference channel."""
    frames = len(ours) // our_channels
    start = max(first, frames // 2 - window)
    lines, picks = [], []
    for i in range(our_channels):
        x = [ours[(start + n) * our_channels + i] for n in range(window)]
        xx = sum(v * v for v in x)
        row = []
        for j in range(reference_channels):
            at = start - first
            if at < 0 or (at + window) * reference_channels > len(reference):
                row.append(0.0)
                continue
            y = [reference[(at + n) * reference_channels + j] for n in range(window)]
            yy = sum(v * v for v in y)
            row.append(sum(p * q for p, q in zip(x, y)) / (xx * yy) ** 0.5 if xx and yy else 0.0)
        best = max(range(reference_channels), key=lambda j: abs(row[j]))
        lines.append(f"  analyzer channel {i} against Core Audio's channels: " + " ".join(f"{v:5.2f}" for v in row) + f"  -> {best}")
        picks.append(best if abs(row[best]) >= 0.3 else None)
    if None in picks or len(set(picks)) != len(picks):
        lines.append("  no mapping by correlation: a channel correlates with nothing or two share a reference channel")
        return None, lines
    return picks, lines


def energy_map(ours, our_channels, reference, reference_channels):
    """For a file of dither alone: the analyzer's channels and Core Audio's paired by their level in turn (the loudest with the loudest, a silent channel with a silent one), with the
    lines that say so; the comparison then holds the level of every band of every channel, which is all such a file has."""
    def levels(values, count):
        frames = len(values) // count
        return [(sum(v * v for v in values[i:frames * count:count]) / max(1, frames)) ** 0.5 for i in range(count)]
    mine, theirs = levels(ours, our_channels), levels(reference, reference_channels)
    picks = [0] * our_channels
    for i, j in zip(sorted(range(our_channels), key=lambda k: -mine[k]), sorted(range(reference_channels), key=lambda k: -theirs[k])):
        picks[i] = j
    lines = ["  channel RMS, analyzer: " + " ".join(f"{v:.3g}" for v in mine), "  channel RMS, Core Audio: " + " ".join(f"{v:.3g}" for v in theirs),
             f"  a file of dither alone: channels paired by level, analyzer channel i = Core Audio channel {picks}"]
    return (picks if our_channels == reference_channels else None), lines


def partial_map(ours, our_channels, reference, reference_channels, first, window=4096):
    """Where Core Audio gives no labels and does not decode some of the analyzer's channels (the channels of dependent substreams: its output has them silent): the analyzer's channels
    that correlate (0.3 or more) with a reference channel each, as (their indexes, the reference channels), or None where none or all do; with the lines that say which are left out."""
    picks, lines = correlation_map(ours, our_channels, reference, reference_channels, first, window)
    if picks is not None:
        return None
    frames = len(ours) // our_channels
    start = max(first, frames // 2 - window)
    keep, chosen = [], []
    for i in range(our_channels):
        x = [ours[(start + n) * our_channels + i] for n in range(window)]
        xx = sum(v * v for v in x)
        best, best_j = 0.0, None
        for j in range(reference_channels):
            at = start - first
            if at < 0 or (at + window) * reference_channels > len(reference):
                continue
            y = [reference[(at + n) * reference_channels + j] for n in range(window)]
            yy = sum(v * v for v in y)
            value = abs(sum(p * q for p, q in zip(x, y)) / (xx * yy) ** 0.5) if xx and yy else 0.0
            if value > best:
                best, best_j = value, j
        if best >= 0.3 and best_j not in chosen:
            keep.append(i)
            chosen.append(best_j)
    if not keep and 0 < reference_channels < our_channels:
        # Content that is mostly dither (the coding-* files) correlates with nothing, so no channel can be found by correlation. A program with dependent substreams lists the independent
        # substream's channels first in coded order and the dependents add to them (A/52 Annex E 3.8.2: the channel map names the channels a dependent substream carries), so the first
        # channels of the analyzer's are the ones Core Audio, which does not decode the dependents, gives.
        keep = list(range(reference_channels))
        chosen = list(range(reference_channels))
        lines = lines + [f"  nothing correlates (dither-dominated content): Core Audio's {reference_channels} channels are taken as the first {reference_channels} of the analyzer's, the independent substream's, in coded order"]
    if not keep or len(keep) == our_channels:
        return None
    left = [i for i in range(our_channels) if i not in keep]
    return keep, chosen, lines + [f"  Core Audio's output correlates with analyzer channels {keep} (as its channels {chosen}) and has nothing of channels {left} (the dependent substreams': not decoded by Core Audio): only the first are compared"]


def pick_channels(raw, count, picks):
    values = floats(raw)
    reduced = array.array("f")
    for n in range(len(values) // count):
        for pick in picks:
            reduced.append(values[n * count + pick])
    return reduced.tobytes()


def first_passing(attempts):
    """attempts: [(label, problems)] in order. Returns (label of the first with no problems or None, the report lines of all)."""
    lines = []
    for label, problems in attempts:
        lines.append(f"    {label}: {'passes' if not problems else '; '.join(problems)}")
        if not problems:
            return label, lines
    return None, lines


def decode(probe, path, out):
    run = subprocess.run([probe, "pcm", str(path), str(out)], capture_output=True, text=True)
    line = next((l for l in run.stdout.splitlines() if l.startswith("{")), "{}")
    info = json.loads(line)
    if run.returncode != 0 or info.get("status") != 0:
        return None, info
    return out.read_bytes(), info


def floats(raw):
    values = array.array("f")
    values.frombytes(raw)
    return values


def estimate(ours, reference, channels, window=1024, reach=2400):
    """Where the reference's samples lie against the analyzer's, and with what gain, from channel 0 over one window of loud samples: (p0, gain, correlation) where the
    reference's sample k is the analyzer's sample p0 + k times about 1/gain; None where a window cannot be taken. Core Audio's priming (288 samples at the coded stage) and any gain
    it applies by default (dynamic range control, dialogue normalization) show here; a correlation near 1 says the signals are the same and only aligned and scaled."""
    a = ours[0::channels]
    r = reference[0::channels]
    start = max(0, min(len(a) // 2, len(a) - window - reach))
    if len(a) < window + reach or len(r) < window + 2 * reach:
        return None
    x = a[start:start + window]
    energy = sum(v * v for v in x)
    if energy == 0:
        return None
    best = None
    for lag in range(-reach, reach + 1):
        origin = start + lag
        if origin < 0 or origin + window > len(r):
            continue
        y = r[origin:origin + window]
        dot = sum(p * q for p, q in zip(x, y))
        if best is None or dot > best[0]:
            best = (dot, lag, sum(q * q for q in y))
    if best is None or best[2] == 0:
        return None
    dot, lag, ry = best
    return -lag, dot / ry, dot / (energy * ry) ** 0.5


def correlations(ours, reference, channels, first, window=2048):
    """Matrix of correlation coefficients, the analyzer's channel i against the reference's channel j at the aligned position: which reference channel is which."""
    start = max(0, len(ours) // channels // 2 - window)
    if (start + window) * channels > len(ours):
        return []
    rows = []
    for i in range(channels):
        x = [ours[(start + n) * channels + i] for n in range(window)]
        row = []
        for j in range(channels):
            at = start - first
            y = [reference[(at + n) * channels + j] for n in range(window)] if at >= 0 and (at + window) * channels <= len(reference) else None
            if y is None:
                row.append(float("nan"))
                continue
            xx, yy = sum(v * v for v in x), sum(v * v for v in y)
            row.append(sum(p * q for p, q in zip(x, y)) / (xx * yy) ** 0.5 if xx and yy else 0.0)
        rows.append(" ".join(f"{v:5.2f}" for v in row))
    return rows


# Core Audio's output starts 256 samples into the coded stage (one block: the first block's overlap), in the coded channel order, at the coded gain: the first
# run's diagnostics found every file at position 256, gain 1.0000, correlation 1.0000 (the 288 of the converter's totals is a different path's figure).
FIRST_POSITION = 256
# Core Audio's output of these files is compared with the analyzer's of the twin file, which has no transient pre-noise processing data: the processing (A/52 Annex E 3.7) is applied
# by the analyzer, which the comparison of the file itself found Core Audio does not match (correlation 0.5 to 0.7 with the file, the 0.73 to 0.83 the analyzer's own output with and without
# it has); the analyzer's output with the processing is held by its own tests (PreNoiseTests, the pcmSha256 of eac3-signal-transient).
OURS_WITHOUT = {"eac3-signal-transient": "eac3-signal-transient-off"}
# The bands of the E-AC-3 comparison: those of the other checks, with edges where the tools of the files change (5,700 Hz and 12,500 Hz: enhanced coupling's begin and end bins of
# eac3-signal-ecpl; 10,200 Hz: the coupling begin of buggy_aht_5.1_384; 8,000 Hz between), so that a band that misses says on which side of an edge.
# Bands whose energy is below FLOOR_DB (dBFS) in either output are not compared: 16-bit audio has a noise floor near -98 dBFS, and Core Audio's level there (the +2 dB of buggy_aht_5.1_384 band 4
# at -90 to -99 dB) says nothing about the decoder.
FLOOR_DB = -90.0
FINE_BANDS = "400,800,1600,3200,5700,6400,8000,10200,12500,12800,20000"
CURRENT = {"stem": ""}
# Bands that are not compared, each with its measured reason: (the analyzer's channels or None for all, the lowest edge in Hz of the bands, the reason). A pin is a statement about
# what Core Audio does, not a tolerance: the reason says how it was found.
PINS = {
    "buggy_aht_5.1_384": [((0, 2), 10200, "from the coupling begin (bin 109, 10.2 kHz) up, the channels coupled with an adaptive hybrid transform coupling channel (IsCouplingAht in 149 of the first 195 frames): Core Audio "
                                            "differs from the analyzer's output by waveform (error -7 dB, correlation 0.89 and 0.85) where the analyzer and FFmpeg correlate 0.993 and 0.994 there and Core Audio "
                                            "0.95 with each; every band below agrees to 0.00 dB over the whole file")],
    "external-ffmpeg-eac3-5.1-256k": [(None, 12500, "above the coded bandwidth (12,469 Hz) only the transform's leakage is left, at about -50 dB; every band below agrees to 0.00 dB over the whole file")],
    "csi_miami_stereo_128_spx": [(None, 12800, "spectral extension's noise region (extension begin and end in the stream; the notch and the blend are in use): Core Audio's band is 1.5 dB louder than the analyzer's, "
                                               "which FFmpeg matches to 0.0 dB in every band; the noise is unit variance and scaled by the RMS of the translated coefficients before the notch (A/52 Annex E 3.6.4.2.4), "
                                               "no reading of those steps that was tried gives 1.5 dB, so the cause is unresolved")],
    "eac3-signal-spx-noise": [(None, 6400, "spectral extension's noise: A/52 Annex E 3.6.4.2.4 asks for noise() from a zero-mean, unity-variance generator and does not define the generator or "
                                          "its seed, so each decoder draws its own; the signs of the differences change with the channel (-2.2 to +1.3 dB)")],
}
# Files Core Audio does not open: the status it gives, recorded and not counted as a failure of the analyzer.
UNOPENABLE = {"casablanca_aht_mono_64": "!dat (560226676) when Core Audio opens the file"}
# Files of dither alone (no signal): the channels have no identity to find by correlation, so Core Audio's are paired with the analyzer's by level.
DITHER_ONLY_FILES = {"substreams"}
DITHER_ONLY_DB = -70.0
DITHER_RANGE_DB = (-3.6, 0.6)


def block_errors(ours, reference, channels, first, count=5):
    """The blocks of 256 samples where the reference differs most from the analyzer after alignment: [(block, [largest error of each channel])], worst first."""
    rows = []
    blocks = (len(reference) // channels) // 256
    for block in range(blocks):
        worst = []
        for c in range(channels):
            error = 0.0
            for n in range(256):
                k = first + block * 256 + n
                if k * channels + c >= len(ours):
                    break
                error = max(error, abs(ours[k * channels + c] - reference[(block * 256 + n) * channels + c]))
            worst.append(error)
        rows.append((block + first // 256, worst))
    rows.sort(key=lambda r: -max(r[1]))
    return rows[:count]


def clicks_source(rate=48000, channels=2):
    """The signal make_external_ac3_fixtures.py encoded for the block switching file (half a second of quiet tones with clicks of 96 samples), as the 16-bit values it wrote, in full scale."""
    import math
    out, seed = [], 12345
    for i in range(rate // 2):
        t = i / rate
        for ch in range(channels):
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
            value = 0.05 * math.sin(2 * math.pi * (440 + 110 * ch) * t) + (0.9 if (i + ch * 3000) % 12000 < 96 else 0)
            out.append(int(max(-1.0, min(1.0, value)) * 32000) / 32768)
    return out


SOURCES = {"external-aften-stereo-192k-blocksw": clicks_source}

# Blocks Core Audio decodes differently from the analyzer, liba52 and FFmpeg, by file (run 43367433212): in external-aften-stereo-192k-blocksw.ac3 the two blocks of its clicks that
# send new rematrixing flags and bit allocation parameters, block 48 (left channel, 0.1117 of full scale) and block 36 (right channel, 0.0072), where the analyzer, liba52 and FFmpeg
# agree to 2.4e-7 and Core Audio agrees with them to 2e-6 everywhere else (block 47, the file's one block switched block, included). Against the signal the encoder was given the
# analyzer's and Core Audio's largest errors are equal in every one of these blocks (0.2612 and 0.1289: the codec's own error at a click), so Core Audio is not closer and
# nothing says the analyzer is wrong; the cause on Core Audio's side is not known. Both blocks (every block of that number in the repeated file, both channels) are left out of the
# comparison and listed in its output.
PINNED_BLOCKS = {"external-aften-stereo-192k-blocksw": [36, 48]}


def source_errors(ours, reference, channels, blocks, name):
    """For the blocks that miss, the largest error of the analyzer and of the reference against the signal the encoder was given (the codec's own error at a click is large, so only
    which of the two is closer says anything): [(block, channel, analyzer error, reference error)]."""
    if name not in SOURCES:
        return []
    source = SOURCES[name]()
    rows = []
    for block, worst in blocks:
        for c in range(channels):
            a = r = sa = sr = 0.0
            count = 0
            for n in range(256):
                k = (block - 1) * 256 + n  # the encoder's delay is one block: the output at position p is the source at p - 256, and the reference starts at 256
                if k < 0 or (k * channels + c) >= len(source) or ((k + 256) * channels + c) >= len(ours) or (k * channels + c) >= len(reference):
                    break
                ea = ours[(k + 256) * channels + c] - source[k * channels + c]
                er = reference[k * channels + c] - source[k * channels + c]
                a, r = max(a, abs(ea)), max(r, abs(er))
                sa, sr, count = sa + ea * ea, sr + er * er, count + 1
            rows.append((block, c, a, r, (sa / count) ** 0.5 if count else 0.0, (sr / count) ** 0.5 if count else 0.0))
    return rows


def attempt(dump, raw, channels, work, tag, extra, name=""):
    """The comparison at the fixed alignment; where it fails the lines also say where the signals are and which blocks and bands miss."""
    sample = json.loads(dump.read_text(encoding="utf-8"))["streams"][0]
    ours = floats((dump.parent / sample["pcm"]).read_bytes())
    reference = floats(raw)
    coded = ORDERS.get(channels, {"coded": list(range(channels))})["coded"]
    problems, result = compare(dump, reorder(raw, channels, coded), channels, f"{tag}-fixed", FIRST_POSITION, extra)
    results = [(f"coded order, first position {FIRST_POSITION}", problems)]
    if problems:
        found = estimate(ours, reference, channels)
        if found:
            p0, gain, correlation = found
            results.append((f"diagnostic: correlation puts the reference at position {p0} with gain {gain:.4f} (correlation {correlation:.4f}; a tonal file can be ambiguous)", ["not a comparison"]))
        for line in correlations(ours, reference, channels, FIRST_POSITION):
            results.append((f"channel correlations (analyzer rows, reference columns) {line}", ["not a comparison"]))
        if "--no-sample-bounds" not in extra:
            missing = block_errors(ours, reference, channels, FIRST_POSITION)
            for block, worst in missing:
                results.append((f"block {block} (a file of 96 blocks repeats): largest errors per channel {' '.join(f'{w:.4g}' for w in worst)}", ["not a comparison"]))
            for block, c, a, r, arms, rrms in source_errors(ours, reference, channels, missing, name):
                results.append((f"block {block} channel {c} against the encoder's input: largest error analyzer {a:.4f}, Core Audio {r:.4f}; RMS analyzer {arms:.5f}, Core Audio {rrms:.5f}", ["not a comparison"]))
        elif result and "bands" in result:
            for number, channel in enumerate(result["bands"]["channels"]):
                for band in channel["bands"]:
                    if band["status"] == "outside":
                        sign = f", reference {band['signedDifferenceDb']:+} dB (analyzer {band['analyzerDb']} dB, Core Audio {band['referenceDb']} dB)" if "signedDifferenceDb" in band else ""
                        results.append((f"channel {number} band {band['band']} ({extra[extra.index('--bands') + 1].split(',')[band['band']]} Hz up): {band['differenceDb']} dB over {band['windowFrames']} frames{sign}", ["outside"]))
    return results


def compare(dump, reference, channels, label, first, extra):
    raw = dump.parent / f"{label}.f32"
    raw.write_bytes(reference)
    out = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(dump), "--reference", str(raw), "--reference-format", "float32", "--reference-channels", str(channels),
                          "--reference-first-position", str(first), "--json", *extra], capture_output=True, text=True)
    if out.returncode == 2:
        return [out.stderr.strip()], None
    result = json.loads(out.stdout)
    if out.returncode == 0:
        return [], result
    if "--no-sample-bounds" in extra and result and "bands" in result:
        # A band whose level is that of the dither alone (below DITHER_ONLY_DB) holds noise that each decoder draws as it likes: 7.3.4 allows a scaling of 0.5 to 0.75 where 0.707 is
        # the optimum, so a reference between -3.0 dB (0.5) and +0.5 dB (0.75) of ours is within the specification, with 0.6 dB for the measurement.
        outside = 0
        for number, channel in enumerate(result["bands"]["channels"]):
            for band in channel["bands"]:
                if band["status"] != "outside":
                    continue
                edges = [int(e) for e in extra[extra.index("--bands") + 1].split(",")]
                if band.get("analyzerDb", 0) < DITHER_ONLY_DB and DITHER_RANGE_DB[0] <= band.get("signedDifferenceDb", 99) <= DITHER_RANGE_DB[1]:
                    band["status"] = "ok (dither range)"
                elif any((channels is None or number in channels) and edges[band["band"]] >= low for channels, low, _ in PINS.get(CURRENT["stem"], [])):
                    band["status"] = "ok (pinned)"
                else:
                    outside += 1
        if outside == 0:
            return [], result
    if "--no-sample-bounds" in extra:
        return ["a band outside the tolerance, or none comparable"], result
    worst = max(result["channels"], key=lambda c: c["max"])
    return [f"largest {worst['max']:.3g}, RMS {worst['rms']:.3g}"], result


def dump_with_dither(command, path, folder, scale):
    """The analyzer's PCM of a file with its dither at scale (the test-only hook of tests/Encspect.PcmDump)."""
    environment = {**os.environ, "ENCSPECT_DITHER_SCALE": str(scale)}
    subprocess.run([*shlex.split(command), str(folder), str(path)], check=True, capture_output=True, env=environment)
    return sorted(folder.glob("*.pcm.json"))[0]


def judge(dump, raw, info, work, variant, extra, name):
    """One comparison of the analyzer's PCM with Core Audio's, by the channel mapping that fits: (the label that passed or None, the lines of the misses, the lines that say how the
    channels were placed)."""
    lines = []
    reference_channels = info["channels"]
    sample = json.loads(dump.read_text(encoding="utf-8"))["streams"][0]
    if sample["channels"] != reference_channels:
        mapped, mapping_lines = map_reference(raw, info, dump)
        lines += mapping_lines
        if mapped is not None:
            raw, reference_channels = mapped, sample["channels"]
        elif info.get("labels"):
            common = common_speakers(raw, info, dump)
            if common is not None:
                dump, raw, common_lines = common
                lines += common_lines
                sample = json.loads(dump.read_text(encoding="utf-8"))["streams"][0]
                reference_channels = sample["channels"]
    peak = max((abs(v) for v in floats((dump.parent / sample["pcm"]).read_bytes())), default=0.0)
    if peak > 1.0:
        # The analyzer's float output is not clipped; Core Audio's may be, which no comparison of levels or correlations survives.
        lines.append(f"  note: the analyzer's output peaks at {peak:.1f} of full scale; a reference that clips or saturates differs by gain and correlation whatever the decoders do")
    label, detail = first_passing(attempt(dump, raw, reference_channels, work, f"ca-{variant}", extra, name))
    if label is None and (reference_channels > 2 or reference_channels < sample["channels"]):
        # Core Audio's channel order is not known (the labels are missing): place its channels by correlation and try again.
        ours = floats((dump.parent / sample["pcm"]).read_bytes())
        picks, mapping_lines = correlation_map(ours, sample["channels"], floats(raw), reference_channels, FIRST_POSITION)
        if picks is None and name in DITHER_ONLY_FILES:
            picks, level_lines = energy_map(ours, sample["channels"], floats(raw), reference_channels)
            mapping_lines += level_lines
        lines += mapping_lines
        if picks is None:
            partial = partial_map(ours, sample["channels"], floats(raw), reference_channels, FIRST_POSITION)
            if partial is not None:
                keep, chosen, partial_lines = partial
                lines += partial_lines[-1:]
                cut = subset_dump(dump, keep)
                label, detail = first_passing(attempt(cut, pick_channels(raw, reference_channels, chosen), len(keep), work, f"ca-{variant}-partial", extra, name))
                return label, detail, lines
        if picks is not None:
            lines.append(f"  mapped by correlation: analyzer channel i = Core Audio channel {picks}")
            mapped_label, mapped_detail = first_passing(attempt(dump, pick_channels(raw, reference_channels, picks), len(picks), work, f"ca-{variant}-mapped", extra, name))
            if mapped_label is not None:
                label, detail = mapped_label, mapped_detail
            else:
                detail = detail + mapped_detail
    return label, detail, lines


def check_file(args, path, work):
    data = path.read_bytes()
    manifest_path = x.FIXTURES / (path.stem + ".expected.json")
    if manifest_path.exists():
        frames = x.manifest_frames(json.loads(manifest_path.read_text(encoding="utf-8")))
    else:
        # A real file from check_eac3_samples.py has no manifest here: its frames are those the headers give, from the start of the file.
        frames, at = [], 0
        while at + 8 <= len(data):
            header = x.m.header(data, at)
            if header is None:
                at += 1  # junk before or between frames: the frames are those that follow it
                continue
            frames.append([at, header["length"]])
            at += header["length"]
    # An AC-3 core with E-AC-3 dependent frames is compared as E-AC-3 is (by band energy, no dither-free copy: the tool that clears the flags rewrites AC-3 frames only).
    mixed = any(x.m.header(data, offset).get("strmtyp") == 1 for offset, _ in frames) and path.suffix == ".ac3"
    is_eac3 = path.suffix == ".eac3" or mixed
    if mixed:
        samples = 6 * 256 * sum(1 for offset, _ in frames if x.m.header(data, offset).get("strmtyp") != 1)
    else:
        samples = sum(x.m.header(data, offset)["blocks"] * 256 for offset, _ in frames) if is_eac3 else 6 * 256 * len(frames)
    stripped = work / (path.stem + ".nodither.ac3")
    if not is_eac3:
        x.clear_dither(args.encspect, path, frames, stripped)
    lines = [f"  pinned, not compared from {low} Hz{'' if channels is None else ' in channels ' + str(channels)}: {reason}" for channels, low, reason in PINS.get(path.stem, [])]
    long_data, long_frames = x.repeated(data, frames, max(2, x.REPEATED_SAMPLES // samples))
    long_path = work / (path.stem + ".repeated" + path.suffix)
    long_path.write_bytes(long_data)
    # The analyzer's output the comparison uses: the file's own, or where Core Audio does not apply a processing the analyzer does, that of the twin file without it.
    ours_path = long_path
    twin = OURS_WITHOUT.get(path.stem)
    if twin:
        twin_data = (x.FIXTURES / (twin + path.suffix)).read_bytes()
        twin_frames = x.manifest_frames(json.loads((x.FIXTURES / (twin + ".expected.json")).read_text(encoding="utf-8")))
        twin_long, _ = x.repeated(twin_data, twin_frames, max(2, x.REPEATED_SAMPLES // samples))
        ours_path = work / (twin + ".repeated" + path.suffix)
        ours_path.write_bytes(twin_long)
    holds = True
    pinned = PINNED_BLOCKS.get(path.stem, [])
    if pinned:
        lines.append(f"  pinned, left out of both passes: blocks {pinned} (see PINNED_BLOCKS)")
    passes = (("dithered", long_path, ["--bands", FINE_BANDS, "--no-sample-bounds", "--band-floor-db", str(FLOOR_DB)]),) if is_eac3 else (("strict", stripped, []), ("dithered", long_path, ["--bands", x.BANDS, "--no-sample-bounds"]))
    CURRENT["stem"] = path.stem
    for variant, source, extra in passes:
        folder = work / variant
        folder.mkdir()
        copies = 1 if variant == "strict" else max(2, x.REPEATED_SAMPLES // samples)
        for copy in range(copies):
            for block in pinned:
                first = (block + copy * 96) * 256
                extra = [*extra, "--exclude", f"{first}:{first + 256}"]
        dump = x.pcmdump(args.pcmdump, ours_path if source == long_path else source, folder)
        raw, info = decode(args.probe, source, folder / "coreaudio.f32")
        if raw is None:
            lines.append(f"  {variant}: Core Audio does not decode the file: {info}")
            if path.stem in UNOPENABLE and info.get("stage") == "open":
                lines.append(f"  recorded: {UNOPENABLE[path.stem]}; not a comparison, not a failure")
                continue
            holds = False
            continue
        label, detail, pre = judge(dump, raw, info, work, variant, extra, path.stem)
        lines += pre
        status = ""
        if label is None:
            # Core Audio's dither may be scaled 0.5, which 7.3.4 allows as well as the optimum 0.707 the analyzer uses: the same comparison against the analyzer's output with its
            # dither at 0.5 (a test-only hook of the PCM dump, ENCSPECT_DITHER_SCALE) says whether that is all that differs.
            folder05 = folder / "dither05"
            folder05.mkdir()
            dump05 = dump_with_dither(args.pcmdump, ours_path if source == long_path else source, folder05, 0.5)
            label05, detail05, pre05 = judge(dump05, raw, info, work, variant + "-d05", extra, path.stem)
            status = f"; with the analyzer's dither at 0.5 (7.3.4, also acceptable): {'ok' if label05 else 'still outside'}"
            if label05 is not None:
                label = label05
                status += " (the file passes only against that output)"
            else:
                detail = detail + ["    with the analyzer's dither at 0.5:"] + detail05
        lines.append(f"  {variant}: {'ok' if label else 'OUTSIDE THE BOUND'} ({info['frames']} frames, layout tag {info.get('layoutTag')}, drc status {info.get('drcStatus')}){status}")
        if label is None:
            lines += detail
            holds = False
    return holds, lines


def variants_report(args, root):
    out = root / "variants"
    entries = derive_variants.write_ac3(out)
    lines = []
    for entry in entries:
        path = out / entry["name"]
        raw, info = decode(args.probe, path, root / "variant.f32")
        lines.append(f"  {entry['name']}: " + (f"{info['frames']} frames" if raw is not None else f"not decoded ({info})"))
    return lines


CORE_AUDIO_SIGNAL_FILES = ["eac3-signal-aht.eac3", "eac3-signal-aht-hebap4.eac3", "eac3-signal-ecpl.eac3", "eac3-signal-ecpl-flat.eac3", "eac3-signal-ecpl-quiet.eac3", "eac3-signal-ecpl-angle.eac3", "eac3-signal-ecpl-flat-quiet.eac3", "eac3-signal-ecpl-flat-explicit.eac3", "eac3-signal-ecpl-flat-new.eac3", "eac3-signal-spx.eac3", "eac3-signal-spx-noise.eac3", "eac3-signal-transient.eac3", "eac3-signal-dependent.eac3", "substreams.eac3", "coding-ac3-core-dependent.ac3"]


# Files whose analyzer streams follow one another (a program frame with dependent frames, one without): each stream is cut out as a file of its own and compared with Core Audio's
# decode of that file, since one run of Core Audio over a file that changes its configuration says nothing about which of the streams a figure belongs to. (The streams of
# substreams.eac3 play at the same time and are not split.)
SEQUENTIAL_STREAMS = {"coding-ac3-core-dependent"}


def split_streams(path, work):
    """The files of the streams of a file whose streams follow one another: the program frames (an independent frame and its dependent frames) that share a configuration."""
    data = path.read_bytes()
    manifest = json.loads((x.FIXTURES / (path.stem + ".expected.json")).read_text(encoding="utf-8"))
    units, current = [], None
    for offset, length in x.manifest_frames(manifest):
        header = x.m.header(data, offset)
        if header.get("strmtyp") != 1 or current is None:
            current = {"frames": [], "key": [(header.get("bsid"), header.get("fscod"), header["acmod"], header["lfeon"])]}
            units.append(current)
        else:
            current["key"].append((header.get("substream"), header["acmod"], header["lfeon"]))
        current["frames"].append((offset, length))
    segments = []
    for unit in units:
        if segments and segments[-1]["key"] == unit["key"]:
            segments[-1]["frames"] += unit["frames"]
        else:
            segments.append({"key": unit["key"], "frames": list(unit["frames"])})
    paths = []
    for number, segment in enumerate(segments, 1):
        target = work / f"{path.stem}.s{number}{path.suffix}"
        target.write_bytes(b"".join(data[o:o + n] for o, n in segment["frames"]))
        paths.append(target)
    return paths


def save_decodes(args, path, folder):
    """Core Audio's decode of the file itself (not repeated) and the analyzer's, with the analyzer's at the dither 0.5 as well, kept for analysis off the runner."""
    folder.mkdir(parents=True, exist_ok=True)
    raw, info = decode(args.probe, path, folder / "coreaudio.f32")
    (folder / "coreaudio.json").write_text(json.dumps(info), encoding="utf-8")
    for tag, scale in (("analyzer", None), ("analyzer-dither05", 0.5)):
        target = folder / tag
        target.mkdir(exist_ok=True)
        if scale is None:
            x.pcmdump(args.pcmdump, path, target)
        else:
            dump_with_dither(args.pcmdump, path, target, scale)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--probe", required=True)
    parser.add_argument("--pcmdump", default=x.DEFAULT_PCMDUMP)
    parser.add_argument("--encspect", default=x.DEFAULT_ENCSPECT)
    parser.add_argument("--keep", type=pathlib.Path)
    parser.add_argument("--save", type=pathlib.Path, help="a directory: Core Audio's decode (float32) of each E-AC-3 file as it is, the probe's line and the analyzer's PCM with its manifest, for analysis off the runner")
    parser.add_argument("--real-samples", type=pathlib.Path, help="a cache directory: the real E-AC-3 files of tests/conformance/eac3-samples.json are fetched into it and compared as well")
    args = parser.parse_args(argv)
    # The AC-3 files with a liba52 reference and the encoder-written E-AC-3 files, and the hand-built E-AC-3 files that use what FFmpeg does not decode or decodes differently
    # (the adaptive hybrid transform with hebap 4, enhanced coupling, spectral extension, transient pre-noise processing, dependent substreams): Core Audio is the independent decoder
    # that can say who is right. The three real files of check_eac3_samples.py can be given on the command line.
    files = args.files or [p for p in x.default_files() if p.name.startswith("external-")] + [x.FIXTURES / name for name in CORE_AUDIO_SIGNAL_FILES]
    if args.real_samples and not args.files:
        import check_eac3_samples
        manifest = json.loads(check_eac3_samples.MANIFEST.read_text(encoding="utf-8"))
        check_eac3_samples.fetch(manifest, args.real_samples)
        files = [*files, *[args.real_samples / s["file"] for s in manifest["samples"] if (args.real_samples / s["file"]).suffix in (".eac3", ".ac3")]]
    failures = []
    with tempfile.TemporaryDirectory() as temporary:
        root = args.keep or pathlib.Path(temporary)
        root.mkdir(parents=True, exist_ok=True)
        for path in files:
            work = root / path.stem
            work.mkdir(exist_ok=True)
            if args.save and path.suffix in (".eac3", ".ac3"):
                try:
                    save_decodes(args, path, args.save / path.stem)
                except Exception as error:  # the saved files are for looking at, not part of the verdict
                    print(f"{path.name}: not saved: {type(error).__name__}: {error}")
            try:
                if path.stem in SEQUENTIAL_STREAMS:
                    ok, lines = True, []
                    for number, part in enumerate(split_streams(path, work), 1):
                        part_work = work / f"s{number}"
                        part_work.mkdir(exist_ok=True)
                        part_ok, part_lines = check_file(args, part, part_work)
                        lines += [f"  stream {number} (frames of {part.name}): {'ok' if part_ok else 'FAILS'}", *part_lines]
                        ok = ok and part_ok
                else:
                    ok, lines = check_file(args, path, work)
            except Exception as error:  # one file's failure must not stop the rest, the real files last of all
                ok, lines = False, [f"  ERROR: {type(error).__name__}: {error}", *["  " + t for t in traceback.format_exc().splitlines()[-8:]]]
            print(f"{path.name}: {'ok' if ok else 'FAILS'}")
            print("\n".join(lines))
            if not ok:
                failures.append(path.name)
        print("damage variants decoded by Core Audio (frame counts, no bound):")
        print("\n".join(variants_report(args, root)))
    print(f"{len(files)} files, {len(failures)} failing: {', '.join(failures) if failures else 'none'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
