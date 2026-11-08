#!/usr/bin/env python3
"""Compares the PCM that Encspect's decoder delivered with a reference decoder's, by position
(docs/design/lossy-decoders.md, "The PCM cross-checks"; the bounds and their basis are in docs/development.md, "Comparing decoded PCM").

    python3 scripts/fixtures/compare_pcm.py DUMP.pcm.json --reference FILE --reference-format FORMAT --reference-channels N [options]

DUMP.pcm.json is the manifest tests/Encspect.PcmDump writes next to the analyzer's samples (<name>.s<k>.pcm, interleaved, little-endian). The reference is a raw
file of interleaved little-endian samples (float32, int16, int24 or int32) at the coded stage: its first sample is at --reference-first-position (default 0),
never at a position a trim has moved. Samples are compared where both sides have a position; the positions that only one side has are listed and left out:
those the analyzer withheld or lost, those a reference drops or conceals (--exclude FIRST:END, end exclusive), and with --skip-full-scale the samples where
either side reaches full scale and the reference clips (any 16-bit output).

Bounds, per channel:

  float reference (float32):   RMS of the difference at most 2^-15 / sqrt(12) of full scale, largest magnitude at most 2^-14 (--max-abs, --rms override).
  fixed reference (int16 ...): the analyzer's floats rounded to nearest (halves up) and clipped to the reference's width; then the largest difference at most
                               2 LSB and the RMS at most 1 LSB (--max-lsb, --rms-lsb override).

Frames that use random noise (AAC noise substitution, AC-3 dither, WMA noise coding) cannot match sample by sample. --bands EDGES (hertz, comma separated, the
edges of the scalefactor or exponent bands) compares the energy of each band, averaged over windows of frames of --band-frame samples (Hann window), within --band-tolerance
dB (0.5), for the bands whose energy on both sides is above --band-floor-db; use it with --no-sample-bounds to leave the sample bounds out. Two independent noise fills
of equal energy differ in a single frame by several dB, so only the mean over many frames can show 0.5 dB: a band whose estimate has a standard deviation above a
third of the tolerance (4.34 * sqrt(2) * 1.5 / sqrt(frames * bins) dB) is reported as inconclusive and not compared (--band-sigmas); the window is the fewest frames that makes it conclusive (z of at least --band-sigmas, more with many windows so that equal noise
fails with a probability of 0.001 a band),
so that frames that are off are not diluted by the rest of the stream, and the worst window counts. A channel with no band compared fails.

Exit status: 0 where every bound holds, 1 where one fails, 2 for a usage error or input the script cannot read. --json prints the result as JSON.
"""

import argparse
import array
import cmath
import json
import math
import pathlib
import sys

FLOAT_RMS = 2.0 ** -15 / math.sqrt(12.0)
FLOAT_MAX = 2.0 ** -14
FIXED_MAX_LSB = 2.0
FIXED_RMS_LSB = 1.0
BAND_TOLERANCE_DB = 0.5
BAND_FLOOR_DB = -100.0
FORMATS = {"float32": (4, None), "int16": (2, 16), "int24": (3, 24), "int32": (4, 32)}


class UsageError(Exception):
    pass


def read_reference(path, fmt, channels):
    """The reference's samples as one list per channel: floats for float32, integers otherwise."""
    width, _ = FORMATS[fmt]
    data = pathlib.Path(path).read_bytes()
    frame = width * channels
    count = len(data) // frame
    if count == 0:
        raise UsageError(f"{path} holds no whole sample frame")
    data = data[: count * frame]
    if fmt == "float32":
        values = array.array("f")
        values.frombytes(data)
        if sys.byteorder == "big":
            values.byteswap()
    elif fmt in ("int16", "int32"):
        values = array.array("h" if fmt == "int16" else "i")
        values.frombytes(data)
        if sys.byteorder == "big":
            values.byteswap()
    else:
        values = [int.from_bytes(data[i:i + 3], "little", signed=True) for i in range(0, len(data), 3)]
    return [list(values[c::channels]) for c in range(channels)]


def read_analyzer(manifest_path, stream):
    """The analyzer's stream: (entry, runs as [position, samples], samples per channel as lists)."""
    manifest = json.loads(pathlib.Path(manifest_path).read_text(encoding="utf-8"))
    entries = manifest["streams"]
    entry = next((e for e in entries if e["index"] == stream), None) if stream is not None else (entries[0] if entries else None)
    if entry is None:
        raise UsageError(f"{manifest_path} holds no stream {stream if stream is not None else 0}")
    fmt = {"float32": "float32", "float64": None, "integer": {16: "int16", 24: "int24", 32: "int32"}.get(entry["bitsPerSample"])}[entry["format"]]
    if fmt is None:
        raise UsageError(f"stream {entry['index']} writes {entry['format']} samples of {entry['bitsPerSample']} bits, which the script does not read")
    channels = entry["channels"]
    samples = read_reference(pathlib.Path(manifest_path).parent / entry["pcm"], fmt, channels)
    expected = sum(n for _, n in entry["runs"])
    if len(samples[0]) != expected:
        raise UsageError(f"{entry['pcm']} holds {len(samples[0])} samples per channel, the manifest says {expected}")
    if FORMATS[fmt][1] is not None:
        # Integer output (the lossless decoders, Speex's fixed-point one) is compared as the floats it stands for.
        scale = float(1 << (FORMATS[fmt][1] - 1))
        samples = [[v / scale for v in channel] for channel in samples]
    return entry, entry["runs"], samples, fmt


def ranges_text(ranges):
    return ", ".join(f"[{a}, {b})" for a, b in ranges) if ranges else "none"


def subtract(ranges, removed):
    """The parts of the sorted, disjoint half-open ranges that the sorted, disjoint removed ranges do not cover."""
    out = []
    for a, b in ranges:
        cursor = a
        for ra, rb in removed:
            if rb <= cursor or ra >= b:
                continue
            if ra > cursor:
                out.append((cursor, ra))
            cursor = max(cursor, rb)
            if cursor >= b:
                break
        if cursor < b:
            out.append((cursor, b))
    return out


def intersect(first, second):
    out = []
    i = j = 0
    while i < len(first) and j < len(second):
        a, b = max(first[i][0], second[j][0]), min(first[i][1], second[j][1])
        if a < b:
            out.append((a, b))
        if first[i][1] < second[j][1]:
            i += 1
        else:
            j += 1
    return out


def merge(ranges):
    out = []
    for a, b in sorted(ranges):
        if out and a <= out[-1][1]:
            out[-1] = (out[-1][0], max(out[-1][1], b))
        else:
            out.append((a, b))
    return out


def align(runs, analyzer_count, reference_first, reference_count, excluded):
    """The position ranges both sides have, less the excluded ones, and the ranges only one side has."""
    analyzer = [(p, p + n) for p, n in runs]
    reference = [(reference_first, reference_first + reference_count)]
    both = intersect(analyzer, reference)
    kept = subtract(both, merge(excluded))
    return kept, subtract(reference, analyzer), subtract(analyzer, reference), subtract(both, kept)


def channel_samples(analyzer_samples, runs, reference_samples, reference_first, kept):
    """For each channel the analyzer's and the reference's samples at the kept positions, in order, and the positions."""
    starts = []
    offset = 0
    for p, n in runs:
        starts.append((p, p + n, offset))
        offset += n
    a_out = [[] for _ in analyzer_samples]
    r_out = [[] for _ in reference_samples]
    positions = []
    run = 0
    for a, b in kept:
        while starts[run][1] <= a:
            run += 1
        position = a
        while position < b:
            first, end, base = starts[run]
            stop = min(b, end)
            for c, channel in enumerate(analyzer_samples):
                a_out[c].extend(channel[base + position - first:base + stop - first])
            for c, channel in enumerate(reference_samples):
                r_out[c].extend(channel[position - reference_first:stop - reference_first])
            positions.append((position, stop))
            position = stop
            if position >= end and position < b:
                run += 1
    return a_out, r_out, positions


def round_to_width(value, bits):
    """Round to nearest, halves up, and clip to the signed range of bits."""
    scale = 1 << (bits - 1)
    code = math.floor(value * scale + 0.5)
    return max(-scale, min(scale - 1, code))


def statistics(analyzer, reference, fmt, skip_full_scale):
    """Per channel: samples compared, RMS and largest magnitude of the difference (in full scale for floats, LSB for fixed), and the samples left out."""
    result = []
    bits = FORMATS[fmt][1]
    for a_channel, r_channel in zip(analyzer, reference):
        total = 0.0
        largest = 0.0
        used = 0
        skipped = 0
        for a, r in zip(a_channel, r_channel):
            if bits is None:
                if skip_full_scale and (abs(a) >= 1.0 or abs(r) >= 1.0):
                    skipped += 1
                    continue
                d = abs(a - r)
            else:
                top = (1 << (bits - 1)) - 1
                if skip_full_scale and (abs(a) >= 1.0 or r >= top or r <= -top - 1):
                    skipped += 1
                    continue
                d = abs(round_to_width(a, bits) - r)
            total += d * d
            largest = max(largest, d)
            used += 1
        result.append({"samples": used, "skippedAtFullScale": skipped, "rms": math.sqrt(total / used) if used else 0.0, "max": largest})
    return result


def fft(values):
    """Radix-2 FFT of a list whose length is a power of two."""
    n = len(values)
    if n == 1:
        return list(values)
    even = fft(values[0::2])
    odd = fft(values[1::2])
    out = [0j] * n
    for k in range(n // 2):
        t = cmath.exp(-2j * math.pi * k / n) * odd[k]
        out[k] = even[k] + t
        out[k + n // 2] = even[k] - t
    return out


def band_powers(samples, rate, frame, edges):
    """Per frame, the mean-square power of each band (Hann window, linear), for the frames of `frame` samples that lie in `samples`, and the bins of each band."""
    window = [0.5 - 0.5 * math.cos(2 * math.pi * i / frame) for i in range(frame)]
    norm = sum(w * w for w in window) * frame
    bins = [min(frame // 2, round(e * frame / rate)) for e in edges]
    frames = []
    for start in range(0, len(samples) - frame + 1, frame):
        spectrum = fft([samples[start + i] * window[i] for i in range(frame)])
        frames.append([sum(abs(spectrum[k]) ** 2 for k in range(lo, hi)) * 2 / norm for lo, hi in zip(bins, bins[1:])])
    return frames, [hi - lo for lo, hi in zip(bins, bins[1:])]


def db(power):
    return 10 * math.log10(power) if power > 0 else -math.inf


def band_sigma_db(frames, bins):
    """The standard deviation, in dB, of the difference of two band energies, each averaged over `frames` frames of `bins` bins of independent noise.

    The power of a bin of noise is exponentially distributed, so the mean of n of them has a relative spread of 1 / sqrt(n), 4.34 / sqrt(n) dB; the
    difference of two independent estimates has sqrt(2) times that. Hann-windowed bins of neighboring frequencies are correlated, which the factor
    of 1.5 covers (the test measures the real spread on noise and holds it under this value)."""
    n = frames * bins
    return math.inf if n == 0 else 4.343 * math.sqrt(2) * 1.5 / math.sqrt(n)


FALSE_FAILURE = 1e-3


def window_frames(bins, tolerance, sigmas, count=None):
    """The fewest frames whose mean band energy has a spread that fits z times in the tolerance, for a band of `bins` bins.

    The worst of all the windows counts, so z grows with their number: z = max(sigmas, sqrt(2 ln(windows / FALSE_FAILURE))) is the normal quantile
    (Bonferroni) at which equal noise fails the band with a probability of FALSE_FAILURE. The windows are counted for the window of `sigmas`, which
    is the most there can be, so z is never too small."""
    z = sigmas
    if count:
        windows = max(1, count // window_frames(bins, tolerance, sigmas))
        z = max(sigmas, math.sqrt(2 * math.log(windows / FALSE_FAILURE)))
    needed = (z * 4.343 * math.sqrt(2) * 1.5 / tolerance) ** 2
    return max(1, math.ceil(needed / max(1, bins)))


def compare_bands(analyzer, reference, fmt, positions, rate, frame, edges, tolerance, floor, sigmas, window=None):
    """Compares the energy of each band over windows of frames; a frame never spans a jump.

    Two decoders that fill a band with independent noise (AAC noise substitution, AC-3 dither, WMA noise coding) agree on the band's energy only on
    average, not frame by frame, so the figure compared is the mean over a window of frames: the shortest window whose spread fits z times in
    the tolerance (z grows with the number of windows, see window_frames) (or `window` frames), so that a stretch of frames that is off is not diluted by the rest of a long stream. The frames are split
    into as many windows of that length or more as fit, and the worst window counts. A band with fewer frames than one window cannot show a
    difference of the tolerance and is inconclusive, not compared."""
    bits = FORMATS[fmt][1]
    scale = 1.0 if bits is None else float(1 << (bits - 1))
    result = []
    for a_channel, r_channel in zip(analyzer, reference):
        rows_a, rows_r, bins = [], [], []
        offset = 0
        for first, end in merge_adjacent(positions):
            n = end - first
            fa, bins = band_powers(a_channel[offset:offset + n], rate, frame, edges)
            fr, _ = band_powers([r / scale for r in r_channel[offset:offset + n]], rate, frame, edges)
            offset += n
            rows_a += fa
            rows_r += fr
        count = len(rows_a)
        bands = []
        compared = outside = inconclusive = 0
        largest = 0.0
        for i, nbins in enumerate(bins):
            size = window if window else window_frames(nbins, tolerance, sigmas, count)
            windows = count // size
            sigma = band_sigma_db(min(count, size), nbins)
            entry = {"band": i, "frames": count, "windowFrames": size, "windows": windows, "sigmaDb": round(sigma, 3)}
            if windows == 0:
                entry["status"] = "inconclusive"
                inconclusive += 1
            else:
                worst = 0.0
                low = False
                for w in range(windows):
                    lo, hi = w * count // windows, (w + 1) * count // windows
                    ea = db(sum(r[i] for r in rows_a[lo:hi]) / (hi - lo))
                    er = db(sum(r[i] for r in rows_r[lo:hi]) / (hi - lo))
                    if ea < floor or er < floor:
                        low = True
                        continue
                    worst = max(worst, abs(ea - er))
                if low and worst == 0.0:
                    entry["status"] = "below the floor"
                else:
                    entry["status"] = "outside" if worst > tolerance else "ok"
                    entry["differenceDb"] = round(worst, 3)
                    compared += 1
                    largest = max(largest, worst)
                    outside += worst > tolerance
            bands.append(entry)
        result.append({"bandsCompared": compared, "bandsOutside": outside, "bandsInconclusive": inconclusive, "largestDifferenceDb": round(largest, 3), "bands": bands})
    return result


def merge_adjacent(ranges):
    out = []
    for a, b in ranges:
        if out and out[-1][1] == a:
            out[-1] = (out[-1][0], b)
        else:
            out.append((a, b))
    return out


def parse_ranges(texts):
    out = []
    for text in texts:
        try:
            first, end = (int(x) for x in text.split(":"))
        except ValueError:
            raise UsageError(f"--exclude wants FIRST:END, not {text}")
        out.append((first, end))
    return out


def run(args):
    entry, runs, analyzer, analyzer_format = read_analyzer(args.manifest, args.stream)
    channels = entry["channels"]
    if args.reference_channels != channels:
        raise UsageError(f"the analyzer's stream has {channels} channels, the reference {args.reference_channels}")
    reference = read_reference(args.reference, args.reference_format, channels)
    kept, only_reference, only_analyzer, excluded = align(runs, len(analyzer[0]), args.reference_first_position, len(reference[0]), parse_ranges(args.exclude))
    if not kept:
        raise UsageError("no position is in both the analyzer's samples and the reference")
    a_samples, r_samples, positions = channel_samples(analyzer, runs, reference, args.reference_first_position, kept)
    is_float = args.reference_format == "float32"
    if is_float:
        bound_max = args.max_abs if args.max_abs is not None else FLOAT_MAX
        bound_rms = args.rms if args.rms is not None else FLOAT_RMS
    else:
        bound_max = args.max_lsb if args.max_lsb is not None else FIXED_MAX_LSB
        bound_rms = args.rms_lsb if args.rms_lsb is not None else FIXED_RMS_LSB
    figures = statistics(a_samples, r_samples, args.reference_format, args.skip_full_scale)
    ok = True
    for figure in figures:
        figure["passes"] = figure["samples"] > 0 and figure["max"] <= bound_max and figure["rms"] <= bound_rms
        if not args.no_sample_bounds:
            ok &= figure["passes"]
    result = {
        "stream": entry["index"],
        "unit": "full scale" if is_float else "LSB",
        "bounds": {"max": bound_max, "rms": bound_rms},
        "compared": sum(b - a for a, b in kept),
        "channels": figures,
        "notComparedOnlyInReference": [list(r) for r in only_reference],
        "notComparedOnlyInAnalyzer": [list(r) for r in only_analyzer],
        "excluded": [list(r) for r in excluded],
    }
    if args.no_sample_bounds:
        for figure in figures:
            del figure["passes"]
    if args.bands:
        edges = [float(x) for x in args.bands.split(",")]
        bands = compare_bands(a_samples, r_samples, args.reference_format, positions, entry["sampleRate"], args.band_frame, edges, args.band_tolerance, args.band_floor_db, args.band_sigmas, args.band_window)
        result["bands"] = {"toleranceDb": args.band_tolerance, "floorDb": args.band_floor_db, "channels": bands}
        ok &= all(b["bandsOutside"] == 0 and b["bandsCompared"] > 0 for b in bands)
    result["passes"] = ok
    return result, ok


def report(result):
    lines = [f"stream {result['stream']}: {result['compared']} samples per channel compared, bounds max {result['bounds']['max']:.6g} and RMS {result['bounds']['rms']:.6g} ({result['unit']})"]
    for c, figure in enumerate(result["channels"]):
        verdict = "" if "passes" not in figure else ("  ok" if figure["passes"] else "  OUTSIDE THE BOUND")
        lines.append(f"  channel {c}: max {figure['max']:.6g}, RMS {figure['rms']:.6g}, {figure['skippedAtFullScale']} at full scale left out{verdict}")
    lines.append(f"  only in the reference (withheld, lost or not output by the analyzer): {ranges_text(result['notComparedOnlyInReference'])}")
    lines.append(f"  only in the analyzer (a reference that drops them): {ranges_text(result['notComparedOnlyInAnalyzer'])}")
    lines.append(f"  excluded: {ranges_text(result['excluded'])}")
    if "bands" in result:
        for c, band in enumerate(result["bands"]["channels"]):
            lines.append(f"  channel {c} bands: {band['bandsCompared']} compared, {band['bandsOutside']} outside {result['bands']['toleranceDb']} dB, {band['bandsInconclusive']} inconclusive (too few samples), largest {band['largestDifferenceDb']} dB")
    lines.append("passes" if result["passes"] else "FAILS")
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("manifest", help="the .pcm.json tests/Encspect.PcmDump wrote")
    parser.add_argument("--stream", type=int, help="the report's stream index (default: the manifest's first stream)")
    parser.add_argument("--reference", required=True, help="the reference's raw samples")
    parser.add_argument("--reference-format", required=True, choices=sorted(FORMATS))
    parser.add_argument("--reference-channels", required=True, type=int)
    parser.add_argument("--reference-first-position", type=int, default=0, help="the position of the reference's first sample at the coded stage (default 0)")
    parser.add_argument("--exclude", action="append", default=[], metavar="FIRST:END", help="positions a reference conceals or drops (repeatable)")
    parser.add_argument("--skip-full-scale", action="store_true", help="leave out samples where either side reaches full scale and the reference clips")
    parser.add_argument("--max-abs", type=float, help="float reference: the largest magnitude, in full scale (default 2^-14)")
    parser.add_argument("--rms", type=float, help="float reference: the RMS, in full scale (default 2^-15 / sqrt(12))")
    parser.add_argument("--max-lsb", type=float, help="fixed reference: the largest difference in LSB (default 2)")
    parser.add_argument("--rms-lsb", type=float, help="fixed reference: the RMS in LSB (default 1)")
    parser.add_argument("--bands", help="hertz edges of the bands whose energy is compared, comma separated")
    parser.add_argument("--band-frame", type=int, default=1024, help="samples per frame of the band comparison, a power of two (default 1024)")
    parser.add_argument("--band-tolerance", type=float, default=BAND_TOLERANCE_DB)
    parser.add_argument("--band-floor-db", type=float, default=BAND_FLOOR_DB)
    parser.add_argument("--band-window", type=int, help="frames in a window of the band comparison (default: the fewest whose spread fits --band-sigmas in the tolerance)")
    parser.add_argument("--band-sigmas", type=float, default=3.0, help="a band is compared only if this many standard deviations of its estimate fit in the tolerance (default 3)")
    parser.add_argument("--no-sample-bounds", action="store_true", help="compare the bands only")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    if args.band_frame < 2 or args.band_frame & (args.band_frame - 1):
        print("compare_pcm.py: --band-frame must be a power of two", file=sys.stderr)
        return 2
    try:
        result, ok = run(args)
    except (UsageError, OSError, KeyError, json.JSONDecodeError) as error:
        print(f"compare_pcm.py: {error}", file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2) if args.json else report(result))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
