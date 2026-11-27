"""A model of the measurements of docs/design/spectral-analysis.md (steps 1 and 2): the average spectrum, the spectral cutoff and the waveform grid
and the levels of a FLAC file, worked out in Python from the committed bytes with flac_model.py's decoder and this module's own FFT. Only the FFT, the
rows and the waveform are independent of the analyzer; the cutoff code below is transliterated from CutoffFinder.cs (the same rules in the
same integer arithmetic), so it checks the port, not the rules. make_measurement_fixtures.py and make_external_measurement_fixtures.py write its results into the
manifests (`<name>.expected.json`); MeasurementFixtureTests holds the analyzer to them.

The model follows the design's text:

- the frame size is the smallest power of two that is at least 0.085 s of samples, from 1,024 to 32,768 points; a periodic Hann window,
  hop half a frame; frames never cross a gap; a stream without a complete frame is measured as one frame padded with zeros;
- 512 rows of mean power (bins N / 1,024 a row), 0 dB a full-scale sine centered on a bin; the average level of each row in tenths of a dB,
  rounded to the nearest, halves up;
- the spectral cutoff: the largest step of the mean dB of a band of 1 kHz under a row against the mean above it, from 2 kHz to row 505, and
  the edge (walking up from the step's row while half of the frames with signal show an own step, the last such row less the band's width minus one), the start of the
  transition and the frames that show an own step of 15 dB at the step's row (the 1 % reference level and the ten levels of 3 dB below it),
  found where the step is 25 dB or more and half of the frames with signal show it.

- the spectral slope (step 7b): the top band of W rows that ends at row 506, found where the band under it is 5 dB above it, the run down from there
  while the band that ends at a row is 3 dB above the W rows over it, the fall between the band means and the fall per kilohertz to the middle of the top band;
- the spectral bandwidth (step 7): the floor of the band of W rows that ends at row 506, the run down while the band that ends at a row is within 3 dB of it,
  found where the band under the run is 20 dB above; the partial cutoff: a third counter, the frames whose band under a row stands 30 dB above the mean
  power of every row above it (up to row 506), the row with the most frames showing, the walk down while half of that maximum shows, found from a tenth of
  the frames with signal;
- the mid and side spectra (step 7): the rows of (left + right) / 2 and (left - right) / 2 from the pair's bins, each with its own cutoff counters;
- the inter-channel coherence (step 8): the magnitude-squared coherence of the pair per row from the stream's sums of the two power rows and the cross rows, the walk
  down from the top of the range (the holes') while the band of W rows under each row is at or over 85 % (20 |C|^2 >= 17 Sxx Syy), found where the run stops above
  0 Hz and the stream has at least 10 frames, with the frames of the run's first full band counted per frame and row in a counter set of the pair;
- the spectral holes (step 7): the share of the cells with signal in which a row stands at least 30 dB under both the W rows under it and the W rows over it,
  per row and over the cells from the first candidate to the cutoff's edge, else the bandwidth's, else row 505.

What is independent of the analyzer is the FFT, the rows (the window, the scale and the normalization), the waveform and the stream figures.
The cutoff's rules are checked against the construction of the signals (a brickwall at a known edge, none for noise, rolloff and mixed
content) and against the printed bands of the lossy files, not against a second reading of the rules.

The levels (levels_of) are worked out on Python integers from the decoded samples and the design's text alone, with no code shared with the
analyzer: the peak, RMS and DC offset from exact sums (the logarithm of a float only at the end), the clipping runs of three or more samples at
one extreme code, the digital silence of the sample frames that are 0 in every channel (and of each channel), the low bits that are never set, and
nothing counted across a gap. The model's tenths of a dB are worked out in 60 decimal digits, so they are exact; the analyzer's own series for the logarithm must give the same tenth
for every fixture; every count and the DC offset in millionths of a percent are exact too.

The loudness (loudness_of) and the true peak (true_peak_of) follow ITU-R BS.1770-5 and EBU Tech 3341 and 3342 as the design reads them, with no code shared with
the analyzer and the C# bins left out: the K-weighting by the Recommendation's 48 kHz coefficients (Tables 1 and 2) or, at other rates, derived with the C library's
tan and pow from the filters' analog parameters; granules of a fifth of 100 ms, gating blocks of 400 ms every 100 ms (the momentary loudness every granule), short-term
values of 3 s every 100 ms, the gates applied to the whole lists and the percentiles read from the sorted values (Tech 3342's reference code); the Annex 2 filter in plain
loops with the sample peak included. The analyzer's bins of 0.01 LU and its tenths of a LU are held to this within a tenth.

Everything is plain Python (the standard library). The FFT is a complex radix-2 transform of the full frame, apart from the analyzer's
real transform with its split step, so the two agree up to rounding (about 1e-12 relative), which the tests allow for.
"""

import decimal
import itertools
import json
import math
import re

import flac_model as m

ROWS = 512
TOP_ROW = 506
OWN_STEP_RATIO = 10 ** 1.5
MAX_CHANNELS = 32


def dumps(value):
    """JSON indented by one space, as the other manifests are, in which every array of plain values is on one line."""
    def write(v, depth):
        pad = " " * (depth + 1)
        if isinstance(v, dict):
            if not v:
                return "{}"
            items = [f"{pad}{json.dumps(k)}: {write(x, depth + 1)}" for k, x in v.items()]
            return "{\n" + ",\n".join(items) + "\n" + " " * depth + "}"
        if isinstance(v, list):
            if all(not isinstance(x, (dict, list)) for x in v):
                return json.dumps(v)
            items = [pad + write(x, depth + 1) for x in v]
            return "[\n" + ",\n".join(items) + "\n" + " " * depth + "]"
        return json.dumps(v)

    return write(value, 0) + "\n"


def fft_size(rate):
    need = (85 * rate + 999) // 1000
    size = 1024
    while size < need and size < 32768:
        size *= 2
    return size


def fft(values):
    """The DFT of a list of complex numbers (a power of two long), iterative radix-2."""
    n = len(values)
    bits = n.bit_length() - 1
    data = [values[int(format(i, f"0{bits}b")[::-1], 2)] for i in range(n)]
    size = 2
    while size <= n:
        half = size // 2
        twiddle = [complex(math.cos(2 * math.pi * k / size), -math.sin(2 * math.pi * k / size)) for k in range(half)]
        for start in range(0, n, size):
            for k in range(half):
                a = data[start + k]
                b = data[start + k + half] * twiddle[k]
                data[start + k] = a + b
                data[start + k + half] = a - b
        size *= 2
    return data


def hann(n):
    return [0.5 - 0.5 * math.cos(2 * math.pi * i / n) for i in range(n)]


def spectrum_of(samples, window):
    """The complex bins of one windowed frame of samples (in full scale +-1): the DFT of the whole frame."""
    return fft([complex(s * w, 0.0) for s, w in zip(samples, window)])


def rows_of(samples, window):
    """The 512 rows of mean power of one frame of samples (in full scale +-1), relative to a full-scale sine centered on a bin."""
    return rows_from_bins(spectrum_of(samples, window), len(window))


def rows_from_bins(spectrum, n):
    """The 512 rows of mean power of the bins of one frame (bins 0 to n / 2 - 1 are read)."""
    scale = 16.0 / (n * n)
    bins = n // (ROWS * 2)
    rows = []
    for r in range(ROWS):
        total = 0.0
        for k in range(r * bins, (r + 1) * bins):
            c = spectrum[k]
            total += (c.real * c.real + c.imag * c.imag) * scale
        rows.append(total / bins)
    return rows


def tenths(power):
    return math.floor(100 * math.log10(power) + 0.5)


def thousandths(power):
    """The level in thousandths of a dB: the searches below run on these, and a figure is rounded to tenths once, at the end."""
    return math.floor(10000 * math.log10(power) + 0.5)


def round_tenths(numerator, denominator):
    """A quotient in thousandths of a dB as tenths, to the nearest with halves up."""
    return (2 * numerator + 100 * denominator) // (200 * denominator)


def band_rows(rate, size):
    """(W, first candidate row, bins a row): 1 kHz in rows and the first row whose upper edge reaches 2 kHz."""
    bins = size // (ROWS * 2)
    w = max(1, (2000 * size + rate * bins) // (2 * rate * bins))
    first = -(-2000 * size // (rate * bins)) - 1
    return w, max(w - 1, first), bins


def hertz(boundary, rate, size, bins):
    return (2 * boundary * bins * rate + size) // (2 * size)


CEILING_RATIO = 1000.0


class Counters:
    def __init__(self):
        self.all = [[0] * 72 for _ in range(ROWS)]
        self.show = [[0] * 72 for _ in range(ROWS)]
        self.ceiling = [[0] * 72 for _ in range(ROWS)]
        self.holes = [[0] * 72 for _ in range(ROWS)]


HOLE_RATIO = 1000.0


def count_frame(rows, counters, w, first):
    suffix = [0.0] * (ROWS + 1)
    for r in range(TOP_ROW, -1, -1):
        suffix[r] = suffix[r + 1] + rows[r]
    for r in range(first, 506):
        under = suffix[r - w + 1] - suffix[r + 1]
        high = min(r + w, TOP_ROW)
        above_rows = high - r
        over = suffix[r + 1] - suffix[high + 1]
        mean = under / w
        exponent = math.frexp(mean)[1] - 1 if mean > 0 else -1023
        level = min(max(exponent, -64), 7) + 64
        counters.all[r][level] += 1
        lhs = under * above_rows
        if lhs > 0 and lhs >= OWN_STEP_RATIO * over * w:
            counters.show[r][level] += 1
        # The ceiling: the band under the row against the mean power of every row above it, up to the top of the search.
        if under > 0 and under * (TOP_ROW - r) >= CEILING_RATIO * w * suffix[r + 1]:
            counters.ceiling[r][level] += 1
        # A hole: the row's power at most a thousandth of the mean power of the W rows under it and of the rows over it (the W rows from r + 1 up, clipped at row 506), both with power.
        if r >= max(first, w):
            own = rows[r] * HOLE_RATIO
            below = suffix[r - w] - suffix[r]
            if below > 0 and over > 0 and own * w <= below and own * above_rows <= over:
                counters.holes[r][level] += 1


COHERENCE_NUMERATOR = 20.0
COHERENCE_DENOMINATOR = 17.0
MINIMUM_COHERENCE_FRAMES = 10


def is_coherent(cross_re, cross_im, left, right):
    """The test of the design: 20 |C|^2 >= 17 Sxx Syy, both powers over 0 (0.85)."""
    return left > 0 and right > 0 and COHERENCE_NUMERATOR * (cross_re * cross_re + cross_im * cross_im) >= COHERENCE_DENOMINATOR * left * right


def count_coherence(left, right, cross_re, cross_im, counters, w):
    """One frame's counters of the pair: for each row from the first full band, the level of the pair's mean power under it and whether that band is coherent."""
    sl = [0.0] * (ROWS + 1)
    sr = [0.0] * (ROWS + 1)
    sc = [0.0] * (ROWS + 1)
    si = [0.0] * (ROWS + 1)
    for r in range(TOP_ROW, -1, -1):
        sl[r] = sl[r + 1] + left[r]
        sr[r] = sr[r + 1] + right[r]
        sc[r] = sc[r + 1] + cross_re[r]
        si[r] = si[r + 1] + cross_im[r]
    for r in range(w - 1, 506):
        ul = sl[r - w + 1] - sl[r + 1]
        ur = sr[r - w + 1] - sr[r + 1]
        mean = (ul + ur) * 0.5 / w
        exponent = math.frexp(mean)[1] - 1 if mean > 0 else -1023
        level = min(max(exponent, -64), 7) + 64
        counters.all[r][level] += 1
        if is_coherent(sc[r - w + 1] - sc[r + 1], si[r - w + 1] - si[r + 1], ul, ur):
            counters.show[r][level] += 1


def coherence_tenths(cross_re, cross_im, left, right):
    if not (left > 0 and right > 0):
        return None
    return int(max(0.0, min(1000.0, math.floor(1000.0 * (((cross_re * cross_re + cross_im * cross_im) / left) / right) + 0.5))))


def row_of_upper_edge(h, rate, size, bins):
    for r in range(ROWS):
        edge = hertz(r + 1, rate, size, bins)
        if edge == h:
            return r
        if edge > h:
            break
    return -1


def find_coherence(sum_left, sum_right, sum_re, sum_im, counters, frames, cutoff, bandwidth, rate, size):
    """The inter-channel coherence of the pair (docs/design/spectral-analysis.md, Step 8) from the stream's sums and the pair's frame counters."""
    if rate < 8000:
        return {"is_searched": False}
    w, _, bins = band_rows(rate, size)
    rows = [coherence_tenths(sum_re[r], sum_im[r], sum_left[r], sum_right[r]) for r in range(ROWS)]
    sl = [0.0] * (ROWS + 1)
    sr = [0.0] * (ROWS + 1)
    sc = [0.0] * (ROWS + 1)
    si = [0.0] * (ROWS + 1)
    for r in range(TOP_ROW, -1, -1):
        sl[r] = sl[r + 1] + sum_left[r]
        sr[r] = sr[r + 1] + sum_right[r]
        sc[r] = sc[r + 1] + sum_re[r]
        si[r] = si[r + 1] + sum_im[r]

    def band(e):
        return sc[e - w + 1] - sc[e + 1], si[e - w + 1] - si[e + 1], sl[e - w + 1] - sl[e + 1], sr[e - w + 1] - sr[e + 1]

    last = 505
    if cutoff.get("is_found") and (edge := row_of_upper_edge(cutoff["frequency"], rate, size, bins)) >= 0:
        last = min(last, edge)
    elif bandwidth.get("is_found") and (content := row_of_upper_edge(bandwidth["frequency"], rate, size, bins)) >= 0:
        last = min(last, content)
    shares = [None] * ROWS
    first_row = w - 1
    for r in range(first_row, 506):
        with_signal, coherent = frames_at(counters, r, counters.show, counts_silence=False)
        if with_signal > 0:
            shares[r] = (2000 * coherent + with_signal) // (2 * with_signal)
    result = {"is_searched": True, "frames": frames, "rows": rows, "shares": shares, "is_found": False, "reason": "", "from_frequency": 0, "to_frequency": 0,
              "top_band_tenths": 0, "frames_showing": 0, "frames_with_signal": 0}
    if frames < MINIMUM_COHERENCE_FRAMES:
        result["reason"] = "tooFewFrames"
        return result
    if last < first_row:
        result["reason"] = "tooShort"
        return result
    top = coherence_tenths(*band(last))
    result["top_band_tenths"] = 0 if top is None else top
    if not is_coherent(*band(last)):
        result["reason"] = "notCoherent"
        return result
    end = last
    while end - 1 >= first_row and is_coherent(*band(end - 1)):
        end -= 1
    if end == first_row:
        result["reason"] = "reachesStart"
        return result
    with_signal, showing = frames_at(counters, end, counters.show, counts_silence=False)
    result.update(is_found=True, from_frequency=hertz(end - w + 1, rate, size, bins), to_frequency=hertz(last + 1, rate, size, bins),
                  frames_with_signal=with_signal, frames_showing=showing)
    return result


REPLICATED_MINIMUM_FRAMES = 10
REPLICATED_SIGNAL_FLOOR = 1e-10


class ReplicatedCounters:
    """The counters of the replicated bands (Step 9): per grid band and offset in rows, the frames with signal in both bands and those that match."""

    def __init__(self, w, rate):
        self.w = w
        self.step = max(1, w // 4)
        self.first_shift = max(1, (150 * 1024 + rate // 2) // rate)
        self.half = max(2, self.first_shift)
        self.count = 0
        while self.start(self.count) + w - 1 <= 505:
            self.count += 1
        self.frames = [[0] * ROWS for _ in range(self.count)]
        self.matches = [[0] * ROWS for _ in range(self.count)]
        self.used = 0

    def start(self, k):
        return self.w + k * self.step


def count_replicated(rows, counters):
    """One used frame: the fine structure x = ln P minus the mean of ln P within 150 Hz on each side (cut at the spectrum's ends), and for every band and offset whether
    both bands have signal and whether 4 S_ts^2 >= S_tt S_ss with S_ts > 0."""
    w = counters.w
    logs = [math.log(p if p >= 1e-30 else 1e-30) for p in rows]
    x = []
    for r in range(ROWS):
        low, high = max(0, r - counters.half), min(ROWS - 1, r + counters.half)
        x.append(logs[r] - sum(logs[low:high + 1]) / (high - low + 1))
    for k in range(counters.count):
        s = counters.start(k)
        target = x[s:s + w]
        power_t = sum(rows[s:s + w])
        if not power_t >= w * REPLICATED_SIGNAL_FLOOR:
            continue
        s_tt = sum(v * v for v in target)
        for d in range(counters.first_shift, s):
            source = x[s - d:s - d + w]
            if not sum(rows[s - d:s - d + w]) >= w * REPLICATED_SIGNAL_FLOOR:
                continue
            counters.frames[k][d] += 1
            s_ts = sum(a * b for a, b in zip(target, source))
            s_ss = sum(v * v for v in source)
            if s_ts > 0 and 4.0 * s_ts * s_ts >= s_tt * s_ss:
                counters.matches[k][d] += 1


def find_replicated(counters, rate, size, cutoff, bandwidth):
    """The replicated bands of the combined spectrum (docs/design/spectral-analysis.md, Step 9) from the counters of the stream, in integers."""
    if rate < 8000:
        return {"is_searched": False}
    w, _, bins = band_rows(rate, size)
    bands = []
    shifts = []
    max_frames = 0
    for k in range(counters.count):
        s = counters.start(k)
        best, best_matches = -1, -1
        for d in range(w, s):
            max_frames = max(max_frames, counters.frames[k][d])
            if counters.matches[k][d] > best_matches:
                best, best_matches = d, counters.matches[k][d]
        if best < 0:
            bands.append({"start_row": s, "offset": 0, "share": None, "frames_with_signal": 0, "is_periodic": False, "passes": False})
            shifts.append(0)
            continue
        n = counters.frames[k][best]
        periodic = best_matches > 0 and any(2 * counters.matches[k][d] >= best_matches for d in range(counters.first_shift, min(w, s)))
        shifts.append(best)
        bands.append({"start_row": s, "offset": hertz(best, rate, size, bins), "share": (2000 * best_matches + n) // (2 * n) if n > 0 else None,
                      "frames_with_signal": n, "is_periodic": periodic,
                      "passes": n >= REPLICATED_MINIMUM_FRAMES and 2 * best_matches >= n and not periodic})
    result = {"is_searched": True, "frames_used": counters.used, "is_found": False, "reason": "", "from_frequency": 0, "to_frequency": 0, "range_frequency": 0,
              "top_band_share": 0, "is_top_band_periodic": False, "stretches": [], "bands": bands}
    if counters.used < REPLICATED_MINIMUM_FRAMES:
        result["reason"] = "tooFewFrames"
        return result
    last = 505
    if cutoff.get("is_found") and (edge := row_of_upper_edge(cutoff["frequency"], rate, size, bins)) >= 0:
        last = min(last, edge)
    elif bandwidth.get("is_found") and (content := row_of_upper_edge(bandwidth["frequency"], rate, size, bins)) >= 0:
        last = min(last, content)
    top_band = -1
    for k in range(counters.count):
        if counters.start(k) + w - 1 <= last:
            top_band = k
    result["range_frequency"] = hertz(last + 1, rate, size, bins)
    if top_band < 0:
        result["reason"] = "tooShort"
        return result
    if max_frames < REPLICATED_MINIMUM_FRAMES:
        result["reason"] = "noSignal"
        return result
    top = bands[top_band]
    result["top_band_share"] = 0 if top["share"] is None else top["share"]
    result["is_top_band_periodic"] = top["is_periodic"]
    # The stretches: passing bands up to the range, joined across gaps shorter than half a band.
    groups = []
    for k in range(counters.count):
        s = counters.start(k)
        if s + w - 1 > last or not bands[k]["passes"]:
            continue
        if groups and 2 * (s - (groups[-1]["end"] + 1)) < w:
            groups[-1]["end"] = max(groups[-1]["end"], s + w - 1)
            groups[-1]["bands"].append(k)
        else:
            groups.append({"first": s, "end": s + w - 1, "bands": [k]})
    stretches = []
    for g in groups:
        runs = []
        for k in g["bands"]:
            s = counters.start(k)
            if runs and abs(shifts[k] - runs[-1]["shift"]) <= 1:
                runs[-1]["end"] = s + w - 1
                runs[-1]["count"] += 1
            else:
                runs.append({"first": s, "end": s + w - 1, "shift": shifts[k], "count": 1})
        i = 0
        while i < len(runs) and len(runs) > 1:
            if runs[i]["count"] > 1:
                i += 1
                continue
            if i > 0:
                runs[i - 1]["end"] = max(runs[i - 1]["end"], runs[i]["end"])
            else:
                runs[i + 1]["first"] = runs[i]["first"]
            del runs[i]
        shares = [bands[k]["share"] for k in g["bands"]]
        stretches.append({"from_frequency": hertz(g["first"], rate, size, bins), "to_frequency": hertz(g["end"] + 1, rate, size, bins), "end_row": g["end"],
                          "offsets": [{"offset": hertz(r["shift"], rate, size, bins), "from_frequency": hertz(r["first"], rate, size, bins),
                                       "to_frequency": hertz(r["end"] + 1, rate, size, bins)} for r in runs],
                          "lowest_share": min(shares), "highest_share": max(shares)})
    result["stretches"] = stretches
    if not stretches:
        result["reason"] = "notMatching"
    elif last - stretches[-1]["end_row"] > w:
        result["reason"] = "notAtTop"
    else:
        result.update(is_found=True, from_frequency=stretches[-1]["from_frequency"], to_frequency=stretches[-1]["to_frequency"])
    return result


def floor_div(a, b):
    return a // b


def frames_at(counters, r, shows=None, counts_silence=True):
    """(frames with signal, frames that show an own step) at a row: signal is within ten levels of the level 1 % of the frames reach. shows: the
    counter array to read the second figure from (the own steps by default; the ceilings for the partial cutoff)."""
    shows = counters.show if shows is None else shows
    total = sum(counters.all[r])
    with_signal = showing = 0
    if total:
        cumulative = 0
        reference = 0
        for l in range(71, -1, -1):
            cumulative += counters.all[r][l]
            if cumulative * 100 >= total:
                reference = l
                break
        if reference == 0 and not counts_silence:
            return 0, 0
        for l in range(max(0, reference - 10), 72):
            with_signal += counters.all[r][l]
            showing += shows[r][l]
    return with_signal, showing


def find_cutoff(level_tenths, counters, rate, size):
    """The spectral cutoff of a spectrum given as 512 thousandths of a dB (None for a row without power) and its counters."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    level = [-200000 if v is None else v for v in level_tenths]
    prefix = [0]
    for v in level:
        prefix.append(prefix[-1] + v)
    best = None
    for r in range(first, 506):
        high = min(r + w, TOP_ROW)
        above = high - r
        under = prefix[r + 1] - prefix[r - w + 1]
        over = prefix[high + 1] - prefix[r + 1]
        numerator = under * above - over * w
        denominator = w * above
        if best is None or numerator * best[2] > best[1] * denominator:
            best = (r, numerator, denominator)
    r, numerator, denominator = best
    step = round_tenths(numerator, denominator)
    with_signal, showing = frames_at(counters, r)
    result = {"is_searched": True, "is_found": False, "frequency": 0, "transition_start": 0, "step_frequency": hertz(r + 1, rate, size, bins),
              "step_tenths": step, "frames_showing": showing, "frames_with_signal": with_signal}
    if numerator >= 25000 * denominator and with_signal > 0 and 2 * showing >= with_signal:
        last = r
        while last < 505:
            signal_next, showing_next = frames_at(counters, last + 1)
            if signal_next == 0 or 2 * showing_next < signal_next:
                break
            last += 1
        edge = max(r, last - w + 1)
        under_sum = prefix[r + 1] - prefix[r - w + 1]
        start = r + 1
        i = r
        while i >= 0 and level[i] * w < under_sum - 3000 * w:
            start = i
            i -= 1
        result.update(is_found=True, frequency=hertz(edge + 1, rate, size, bins), transition_start=hertz(start, rate, size, bins))
    return result


def find_bandwidth(level_tenths, rate, size):
    """The spectral bandwidth of a spectrum given as 512 thousandths of a dB: the floor is the mean level of the band of W rows that ends at row 506;
    the run goes down while the band that ends at the row has a mean within 3 dB of the floor; found where the band of W rows under the run's
    lowest row (the lowest row of the lowest such band) is at least 20 dB above the floor. Sums of W integers are compared."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    level = [-200000 if v is None else v for v in level_tenths]
    prefix = [0]
    for v in level:
        prefix.append(prefix[-1] + v)
    floor_sum = prefix[TOP_ROW + 1] - prefix[TOP_ROW - w + 1]
    end = TOP_ROW
    while end >= w and abs(prefix[end] - prefix[end - w] - floor_sum) <= 3000 * w:
        end -= 1
    lowest = end - w + 1
    result = {"is_searched": True, "is_found": False, "frequency": 0, "floor_tenths": 0, "reason": "none"}
    if lowest < first or lowest < w:
        result["reason"] = "reaches_top"
    elif prefix[lowest] - prefix[lowest - w] < floor_sum + 20000 * w:
        result["reason"] = "slope"
    else:
        result.update(is_found=True, frequency=hertz(lowest, rate, size, bins), floor_tenths=round_tenths(floor_sum, w))
    return result


def find_slope(level_tenths, rate, size):
    """The spectral slope of a spectrum given as 512 thousandths of a dB, worked out in its own code: the top band is the band of W rows that ends at
    row 506; found where the band of W rows under it is at least 5 dB above it; the run goes down one row at a time while the band that ends at the
    row is at least 3 dB above the W rows over it, down to the first candidate. The fall is the mean of the band that ends at the run's lowest row
    less the top band's, the slope that fall over the distance from the row's upper edge to the middle of the top band (a row is rate / 1,024 Hz)."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    level = [-200000 if v is None else v for v in level_tenths]
    prefix = [0]
    for v in level:
        prefix.append(prefix[-1] + v)

    def band(end):
        return prefix[end + 1] - prefix[end - w + 1]

    top_sum = band(TOP_ROW)
    row = TOP_ROW - w
    top_step = round_tenths(band(row) - top_sum, w)
    result = {"is_searched": True, "is_found": False, "from_frequency": 0, "to_frequency": 0, "fall_tenths": 0, "tenths_per_kilohertz": 0, "top_step_tenths": top_step}
    if band(row) - top_sum < 5000 * w:
        return result
    fc = first
    while row - 1 >= fc and band(row - 1) - band(row - 1 + w) >= 3000 * w:
        row -= 1
    fall = band(row) - top_sum
    distance = 2 * (TOP_ROW - row) - w
    result.update(is_found=True, from_frequency=hertz(row + 1, rate, size, bins), to_frequency=hertz(TOP_ROW + 1, rate, size, bins),
                  fall_tenths=round_tenths(fall, w),
                  tenths_per_kilohertz=(2 * fall * 2048000 + 100 * w * distance * rate) // (200 * w * distance * rate))
    return result


def find_holes(counters, rate, size, cutoff, bandwidth):
    """The spectral holes: per row the share of the frames with signal in which the row is a hole (tenths of a percent, None for a row that is
    not tested or has no frame with signal), and the hole cells over the cells with signal from the first tested row to the last row of content:
    the cutoff's edge where found, else the bandwidth's lower edge less a row where found, else row 505."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    first = max(first, w)
    last = 505
    for figure in (cutoff, bandwidth):
        if figure["is_found"]:
            edge = [r for r in range(ROWS) if hertz(r + 1, rate, size, bins) == figure["frequency"]]
            if edge:
                last = min(last, edge[0])
            break
    shares = [None] * ROWS
    hole_cells = cells = 0
    for r in range(first, 506):
        with_signal, count = frames_at(counters, r, counters.holes, counts_silence=False)
        if with_signal:
            shares[r] = (2000 * count + with_signal) // (2 * with_signal)
        if r <= last:
            hole_cells += count
            cells += with_signal
    empty = last < first
    return {"is_searched": True, "hole_cells": hole_cells, "cells_with_signal": cells, "from_frequency": 0 if empty else hertz(first, rate, size, bins),
            "to_frequency": 0 if empty else hertz(last + 1, rate, size, bins), "row_shares": shares}


def find_partial(counters, rate, size):
    """The partial cutoff: the candidate row at which the most frames with signal show a ceiling (the lowest on a tie), then the lowest row below it
    whose count stays at least half of that maximum, which gives the frequency; the count is the maximum (at its own row); found where at least 10 % of the
    frames with signal at that row show one."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    showing = {r: frames_at(counters, r, counters.ceiling)[1] for r in range(first, 506)}
    best = first
    for r in range(first, 506):
        if showing[r] > showing[best]:
            best = r
    row = best
    while row - 1 >= first and showing[best] > 0 and 2 * showing[row - 1] >= showing[best]:
        row -= 1
    with_signal, shown = frames_at(counters, best, counters.ceiling)
    return {"is_searched": True, "is_found": with_signal > 0 and 10 * shown >= with_signal, "frequency": hertz(row + 1, rate, size, bins) if shown > 0 else 0,
            "frames_showing": shown, "frames_with_signal": with_signal}


decimal.getcontext().prec = 60


def tenths_of_ratio(ratio):
    """Tenths of a dB of an amplitude ratio, a decimal or None for 0: floor(200 log10 r + 0.5), halves up, the one rounding of every figure, worked
    out in 60 digits so that it is exact (a tie would need a ratio that is a power of ten to a half)."""
    return None if ratio == 0 else int((200 * ratio.log10() + decimal.Decimal("0.5")).to_integral_value(decimal.ROUND_FLOOR))


def tenths_of_power(power):
    return None if power == 0 else int((100 * power.log10() + decimal.Decimal("0.5")).to_integral_value(decimal.ROUND_FLOOR))


def tz(value):
    return (value & -value).bit_length() - 1


def silence_of(segments, minimum):
    """The digital silence of the zero flags of the segments between gaps (no stretch crosses a gap): a stretch is a maximal run of zero flags of at
    least minimum samples (None: the stream states no rate, nothing is measured but the total). Returns (start, end, total, stretches, samples, longest):
    the stretch that begins the first segment and the one that ends the last (0 where shorter than the minimum), all zero flags, and the stretches
    with the samples in them and the longest. Worked out from the runs of each segment, with no running state."""
    runs = [[(z, len(list(g))) for z, g in itertools.groupby(seg)] for seg in segments]
    total = sum(sum(seg) for seg in segments)
    if minimum is None:
        return 0, 0, total, 0, 0, 0
    long_runs = [n for segment in runs for z, n in segment if z and n >= minimum]
    first, last = runs[0][0], runs[-1][-1]
    start = first[1] if first[0] and first[1] >= minimum else 0
    end = last[1] if last[0] and last[1] >= minimum else 0
    return start, end, total, len(long_runs), sum(long_runs), max(long_runs, default=0)


def levels_of(runs, channels, bits, rate):
    """The levels of integer samples. runs: the segments of the stream in order, each a list of channel lists (the samples between two gaps).
    Returns the stream's figures and each channel's. Clipping is counted at the extremes of the bits in use: -2^(bits - 1), and 2^(bits - 1) less
    2^k for the k low bits that are 0 in every sample of every channel, from 8 bits in use on (the stream's OR is taken first, so there is no running
    state to get wrong). Digital silence is a stretch of zero frames of at least 10 ms: ceil(rate / 100) samples."""
    full = 1 << (bits - 1)
    stream_or = 0
    for seg in runs:
        for channel in seg:
            for v in channel:
                stream_or |= v
    unused = tz(stream_or) if stream_or else 0
    shift = unused if bits - unused >= 8 and stream_or else 0
    high, low = full - (1 << shift), -full
    minimum = (rate + 99) // 100 if rate > 0 else None
    count = sum(len(seg[0]) for seg in runs)

    def sums(c):
        segs = [seg[c] for seg in runs]
        flat = [v for seg in segs for v in seg]
        bit_or = 0
        for v in flat:
            bit_or |= v
        clip_runs = clipped = longest = 0
        for seg in segs:
            sign, length = 0, 0
            for v in seg:
                now = 1 if v == high else -1 if v == low else 0
                if now != 0 and now == sign:
                    length += 1
                else:
                    sign, length = now, 1 if now else 0
                if length >= 3:
                    clipped += 3 if length == 3 else 1
                    clip_runs += 1 if length == 3 else 0
                    longest = max(longest, length)
        return dict(total=sum(flat), squares=sum(v * v for v in flat), bit_or=bit_or, peak=max((abs(v) for v in flat), default=0),
                    runs=clip_runs, clipped=clipped, longest=longest, silence=silence_of([[v == 0 for v in seg] for seg in segs], minimum))

    def figures(f):
        denominator = count * full
        micro = 0
        if f["total"] != 0:
            micro = (2 * abs(f["total"]) * 10 ** 8 + denominator) // (2 * denominator)
            micro = micro if f["total"] > 0 else -micro
        return {
            "sample_peak_tenths": tenths_of_ratio(decimal.Decimal(f["peak"]) / full),
            "rms_tenths": tenths_of_power(decimal.Decimal(f["squares"]) / (count * full * full)),
            "dc_micropercent": micro,
            "dc_tenths": tenths_of_ratio(decimal.Decimal(abs(f["total"])) / (count * full)),
            "clipping_runs": f["runs"], "clipped_samples": f["clipped"], "longest_clipping_run": f["longest"],
            "silent_at_start": f["silence"][0], "silent_at_end": f["silence"][1], "zero_samples": f["silence"][2],
            "silent_stretches": f["silence"][3], "silent_samples": f["silence"][4], "longest_silent_stretch": f["silence"][5],
            "low_bits_unused": tz(f["bit_or"]) if f["bit_or"] else None, "highest_magnitude": f["peak"],
        }

    per = [sums(c) for c in range(channels)]
    result = [figures(f) for f in per]
    joint = silence_of([[all(seg[c][i] == 0 for c in range(channels)) for i in range(len(seg[0]))] for seg in runs], minimum)
    squares = sum(f["squares"] for f in per)
    bit_or = 0
    for f in per:
        bit_or |= f["bit_or"]
    biggest = max(range(channels), key=lambda c: abs(per[c]["total"]))
    stream = {
        "sample_peak_tenths": max((r["sample_peak_tenths"] for r in result if r["sample_peak_tenths"] is not None), default=None),
        "rms_tenths": tenths_of_power(decimal.Decimal(squares) / (count * channels * full * full)),
        "dc_micropercent": result[biggest]["dc_micropercent"], "dc_tenths": result[biggest]["dc_tenths"],
        "clipping_runs": sum(r["clipping_runs"] for r in result), "clipped_samples": sum(r["clipped_samples"] for r in result),
        "longest_clipping_run": max(r["longest_clipping_run"] for r in result),
        "silent_at_start": joint[0], "silent_at_end": joint[1], "zero_samples": joint[2],
        "silent_stretches": joint[3], "silent_samples": joint[4], "longest_silent_stretch": joint[5],
        "low_bits_unused": tz(bit_or) if bit_or else None, "highest_magnitude": max(f["peak"] for f in per),
    }
    return {"bits": bits, "bits_in_use": bits - shift, "minimum_silent_stretch": minimum, "stream": stream, "channels": result}


# ---- Loudness and true peak (step 5) ----

LUFS_OFFSET = -0.691
SPEAKER_SURROUND = {"side left", "side right"}
LFE_NAMES = {"LFE", "LFE 2"}
TRUE_PEAK_PHASES = [
    [0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000, -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750, 0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500],
    [-0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250, -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125, 0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375],
    [-0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000, -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500, 0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875],
    [-0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750, -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875, 0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750],
]


# The analog filter parameters and the bilinear transform for rates other than 48 kHz follow libebur128 v1.2.6 (ebur128_init_filter), Copyright (c) 2011 Jan Kokemüller,
# under the MIT license. The license text is in THIRD-PARTY-NOTICES.md.
def kweighting(rate):
    """(b0, b1, b2, a1, a2, h1, h2): the two sections; BS.1770-5 Tables 1 and 2 at 48 kHz, else the same analog filters at the rate."""
    if rate == 48000:
        return 1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585, -1.99004745483398, 0.99007225036621
    f0, gain, q = 1681.974450955533, 3.999843853973347, 0.7071752369554196
    k = math.tan(math.pi * f0 / rate)
    vh = 10 ** (gain / 20)
    vb = vh ** 0.4996667741545416
    a0 = 1 + k / q + k * k
    f1, q1 = 38.13547087602444, 0.5003270373238773
    k1 = math.tan(math.pi * f1 / rate)
    d = 1 + k1 / q1 + k1 * k1
    # The highpass numerator 1, -2, 1 gives the passband gain d; the gain of the 48 kHz filter (d48) is restored by scaling the shelf's numerator.
    k48 = math.tan(math.pi * f1 / 48000)
    g = (1 + k48 / q1 + k48 * k48) / d
    return (g * (vh + vb * k / q + k * k) / a0, g * 2 * (k * k - vh) / a0, g * (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0,
            2 * (k1 * k1 - 1) / d, (1 - k1 / q1 + k1 * k1) / d)


def loudness_weights(layout, measured):
    """The channel weights in hundredths, or (None, reason). BS.1770-5 Annex 3 Table 4: 1.41 for the surround positions, the LFE left out; the back pair is
    the rear pair of a 7.1 (1.0) beside a side pair, else the surround pair. Positions that are not stated: one channel, or two (left and right)."""
    if layout is None or len(layout) < measured or any(l == "discrete" for l in layout[:measured]):
        if measured <= 2:
            return [100] * measured, None
        return None, "the channel positions are not stated"
    names = layout[:measured]
    sides = "side left" in names and "side right" in names
    weights = []
    for name in names:
        if name in LFE_NAMES:
            weights.append(0)
        elif name in SPEAKER_SURROUND:
            weights.append(141)
        elif name in ("back left", "back right"):
            weights.append(100 if sides else 141)
        else:
            weights.append(100)
    if not any(weights):
        return None, "the stream holds no channel that loudness includes"
    return weights, None


def lufs(energy):
    return LUFS_OFFSET + 10 * math.log10(energy)


def tenths_of_lufs(energy):
    return int(math.floor(10 * lufs(energy) + 0.5))


def tenths_of_value(value):
    return int(math.floor(10 * value + 0.5))


def loudness_of(runs, measured, bits, rate, layout):
    """The loudness of integer samples. runs: the segments between gaps, each a list of channel lists. Returns the figures as tenths (None where there is
    none) and why a figure is missing: 'too_short' or 'below_gate'."""
    if rate < 8000:
        return {"not_measured": "the sample rate is not stated" if rate <= 0 else "loudness needs a sample rate of 8,000 Hz or more"}
    weights, reason = loudness_weights(layout, measured)
    if weights is None:
        return {"not_measured": reason}
    scale = 1.0 / (1 << (bits - 1))
    b0, b1, b2, a1, a2, h1, h2 = kweighting(rate)
    step = (rate + 5) // 10
    momentary_all, blocks, shorts, short_times = [], [], [], []
    position = 0
    for seg in runs:
        length = len(seg[0])
        squares = []
        for c in range(measured):
            if not weights[c]:
                continue
            s1 = s2 = t1 = t2 = 0.0
            sq = []
            for v in seg[c]:
                x = v * scale
                y = b0 * x + s1
                s1 = b1 * x - a1 * y + s2
                s2 = b2 * x - a2 * y
                z = y + t1
                t1 = -2.0 * y - h1 * z + t2
                t2 = y - h2 * z
                sq.append(z * z)
            squares.append((weights[c] / 100.0, sq))
        # The granule k ends at (k * step + 2) // 5 samples from the start of the run.
        granules = []
        k = 0
        while True:
            start, end = (k * step + 2) // 5, ((k + 1) * step + 2) // 5
            if end > length:
                break
            z = 0.0
            for w, sq in squares:
                z += w * sum(sq[start:end])
            granules.append((z, end - start, end))
            k += 1
        for g in range(19, len(granules)):
            window = granules[g - 19:g + 1]
            m = sum(w[0] for w in window) / sum(w[1] for w in window)
            momentary_all.append(m)
            if (g + 1) % 5 == 0:
                blocks.append(m)
                if g >= 149:
                    window = granules[g - 149:g + 1]
                    shorts.append(sum(w[0] for w in window) / sum(w[1] for w in window))
        position += length
    result = {"weights": weights, "blocks": len(blocks)}
    gate_abs = 10 ** ((-70 - LUFS_OFFSET) / 10)

    def figure(value, absence):
        return {"tenths": value, "absence": absence}

    result["momentary"] = figure(tenths_of_lufs(max(momentary_all)) if momentary_all and max(momentary_all) > 0 else None, "too_short" if not momentary_all else "none")
    result["short_term"] = figure(tenths_of_lufs(max(shorts)) if shorts and max(shorts) > 0 else None, "too_short" if not shorts else "none")
    above = [b for b in blocks if b > gate_abs]
    result["blocks_above_absolute_gate"] = len(above)
    if not blocks:
        result["integrated"] = figure(None, "too_short")
    elif not above:
        result["integrated"] = figure(None, "below_gate")
    else:
        gate = sum(above) / len(above) / 10
        rel = [b for b in above if b > gate]
        result["blocks_above_relative_gate"] = len(rel)
        result["integrated"] = figure(tenths_of_lufs(sum(rel) / len(rel)), "none")
    above = [v for v in shorts if v >= gate_abs]
    if not shorts:
        result["range"] = figure(None, "too_short")
    elif not above:
        result["range"] = figure(None, "below_gate")
    else:
        gate = sum(above) / len(above) / 100
        rel = sorted(v for v in above if v >= gate)
        n = len(rel)
        low = rel[int(math.floor((n - 1) * 10 / 100 + 0.5))]
        high = rel[int(math.floor((n - 1) * 95 / 100 + 0.5))]
        result["range"] = figure(tenths_of_value(lufs(high) - lufs(low)), "none")
        result["range_low_tenths"] = tenths_of_lufs(low)
        result["range_high_tenths"] = tenths_of_lufs(high)
    result["short_term_values"] = len(shorts)
    result["short_term_first_tenths"] = tenths_of_lufs(shorts[0]) if shorts and shorts[0] > 0 else None
    return result


def true_peak_of(runs, measured, bits, rate):
    """The true peak per channel in tenths of a dB (None for silence), by Annex 2's filter with the oversampling the rate gives, and the sample peak
    always included; the filter reads no sample from before the start of a run."""
    if rate <= 0:
        return {"not_measured": "the sample rate is not stated"}
    factor = 4 if rate < 96000 else 2 if rate < 192000 else 1
    phases = TRUE_PEAK_PHASES if factor == 4 else [TRUE_PEAK_PHASES[0], TRUE_PEAK_PHASES[2]] if factor == 2 else []
    scale = 1.0 / (1 << (bits - 1))
    peaks = []
    for c in range(measured):
        peak = 0.0
        for seg in runs:
            x = [v * scale for v in seg[c]]
            peak = max([peak] + [abs(v) for v in x])
            for n in range(len(x)):
                if not any(x[max(0, n - 11):n + 1]):
                    continue
                for phase in phases:
                    total = 0.0
                    for k in range(12):
                        if n - k >= 0:
                            total += phase[k] * x[n - k]
                    peak = max(peak, abs(total))
        peaks.append(None if peak == 0 else tenths_of_ratio(decimal.Decimal(peak)))
    return {"oversampling": factor, "channels": peaks, "stream": max((p for p in peaks if p is not None), default=None)}


def segments(stream):
    """The decoded blocks of a FLAC stream as (position, [channel lists], fails): fails is True where the frame's CRC-16 fails, and the measurements
    leave such a frame out as withheld samples (docs/design/damaged-units.md)."""
    out = []
    for f in stream.frames:
        if "channels" in f:
            out.append((f["first_sample"], f["channels"], "crc16" in f["status"]))
    return out


def find_pair(layout, measured):
    """The channels of the mid and side spectra: the first channels the layout names front left and front right, or channels 0 and 1 of a stream of
    two channels without a layout; None where there is no pair."""
    if not layout:
        return (0, 1) if measured == 2 else None
    names = list(layout)[:measured]
    if "front left" in names and "front right" in names:
        return names.index("front left"), names.index("front right")
    return None


def measure(data, layout=None):
    """The model's figures for a FLAC file: stream figures, the average spectrum rows (tenths of a dB, None for a row without power), the
    cutoff of the combined spectrum and of each channel, and the waveform's columns. layout: the speakers by channel (default: the
    format's; "LFE" is left out of the combined spectrum)."""
    stream = m.Stream(data)
    info = stream.info
    rate, bits, channels = info["sample_rate"], info["bits"], info["channels"]
    if layout is None:
        layout = m.LAYOUTS.get(channels)
    measured = min(channels, MAX_CHANNELS)
    combined_channels = [c for c in range(measured) if not layout or c >= len(layout) or layout[c] != "LFE"] or list(range(measured))
    size = fft_size(rate)
    hop = size // 2
    window = hann(size)
    scale = 1.0 / (1 << (bits - 1))
    w, first_candidate, bins = band_rows(rate, size)
    searched = rate >= 8000

    pair = find_pair(layout, measured)
    sets = measured + 1 + (2 if pair else 0)
    sums = [[0.0] * ROWS for _ in range(sets)]
    counters = [Counters() for _ in range(sets)]
    coherence_counters = Counters()
    replicated = ReplicatedCounters(w, rate) if searched else None
    cross_re = [0.0] * ROWS
    cross_im = [0.0] * ROWS
    frames = 0
    samples = lost = gaps = withheld = 0
    pending_withheld = 0
    first_position = None
    origin = 0
    expected = None
    mins, maxs = {}, {}
    buffers = [[] for _ in range(measured)]
    segment_start = 0
    runs = []

    def process(frame_samples):
        nonlocal frames
        bins = [spectrum_of([v * scale for v in frame_samples[c]], window) for c in range(measured)] if pair else None
        per = [rows_from_bins(bins[c], size) if pair else rows_of([v * scale for v in frame_samples[c]], window) for c in range(measured)]
        combined = [sum(per[c][r] for c in combined_channels) / len(combined_channels) for r in range(ROWS)]
        for c in range(measured):
            for r in range(ROWS):
                sums[c][r] += per[c][r]
            if searched:
                count_frame(per[c], counters[c], w, first_candidate)
        for r in range(ROWS):
            sums[measured][r] += combined[r]
        if searched:
            count_frame(combined, counters[measured], w, first_candidate)
            if ((segment_start - origin) // hop) % 2 == 0:
                count_replicated(combined, replicated)
                replicated.used += 1
        if pair:
            left, right = bins[pair[0]], bins[pair[1]]
            # The mid bin is (L + R) / 2 and the side bin (L - R) / 2, per bin, so identical channels give a side of exactly 0.
            for offset, sign in ((1, 1), (2, -1)):
                derived = [(a + sign * b) * 0.5 for a, b in zip(left[:size // 2], right[:size // 2])]
                derived_rows = rows_from_bins(derived, size)
                for r in range(ROWS):
                    sums[measured + offset][r] += derived_rows[r]
                if searched:
                    count_frame(derived_rows, counters[measured + offset], w, first_candidate)
            # The cross-spectrum rows: the sum over the bins of a row of L R*, scaled as the power rows are (16 / (n n bins)).
            scale_rows = 16.0 / (size * size)
            per_row = size // (ROWS * 2)
            frame_re = [0.0] * ROWS
            frame_im = [0.0] * ROWS
            for r in range(ROWS):
                total_re = total_im = 0.0
                for k in range(r * per_row, (r + 1) * per_row):
                    a, b = left[k], right[k]
                    total_re += a.real * b.real + a.imag * b.imag
                    total_im += a.imag * b.real - a.real * b.imag
                frame_re[r] = total_re * scale_rows / per_row
                frame_im[r] = total_im * scale_rows / per_row
                cross_re[r] += frame_re[r]
                cross_im[r] += frame_im[r]
            if searched:
                count_coherence(per[pair[0]], per[pair[1]], frame_re, frame_im, coherence_counters, w)
        frames += 1

    blocks = segments(stream)
    if blocks and blocks[-1][2]:
        raise ValueError("the model has no failed last frame")
    for position, block, fails in blocks:
        if fails:
            # Decoded and written, but left out of every figure; the next block follows a gap, so a run of failed frames is one gap.
            if expected is None:
                raise ValueError("the model has no failed first frame")
            withheld += len(block[0])
            pending_withheld += len(block[0])
            continue
        if expected is None:
            first_position = position
            origin = position // hop * hop
            expected = position
            segment_start = position
        if position > expected:
            lost += position - expected - pending_withheld
            gaps += 1
            expected = position
            buffers = [[] for _ in range(measured)]
            segment_start = position
            runs.append([[] for _ in range(measured)])
            pending_withheld = 0
        elif not runs:
            runs.append([[] for _ in range(measured)])
        count = len(block[0])
        for c in range(measured):
            runs[-1][c].extend(block[c][:count])
        for c in range(measured):
            buffers[c].extend(block[c])
            for i in range(count):
                v = block[c][i] * scale
                key = (c, (position + i - origin) // hop)
                if key in mins:
                    mins[key] = min(mins[key], v)
                    maxs[key] = max(maxs[key], v)
                else:
                    mins[key] = maxs[key] = v
        samples += count
        expected += count
        while len(buffers[0]) >= size:
            process([b[:size] for b in buffers])
            buffers = [b[hop:] for b in buffers]
            segment_start += hop
    if frames == 0 and buffers and buffers[0]:
        process([b + [0] * (size - len(b)) for b in buffers])

    levels = [[None if sums[c][r] <= 0 or frames == 0 else tenths(sums[c][r] / frames) for r in range(ROWS)] for c in range(sets)]
    fine = [[None if sums[c][r] <= 0 or frames == 0 else thousandths(sums[c][r] / frames) for r in range(ROWS)] for c in range(sets)]
    cutoffs = [find_cutoff(fine[c], counters[c], rate, size) for c in range(sets)]
    bandwidths = [find_bandwidth(fine[c], rate, size) for c in range(sets)]
    slopes = [find_slope(fine[c], rate, size) for c in range(sets)]
    partial = find_partial(counters[measured], rate, size)
    holes = find_holes(counters[measured], rate, size, cutoffs[measured], bandwidths[measured])
    replicated_bands = find_replicated(replicated, rate, size, cutoffs[measured], bandwidths[measured]) if searched else {"is_searched": False}
    mid_side = None
    if pair:
        mid_side = {"left_channel": pair[0], "right_channel": pair[1], "mid": levels[measured + 1], "side": levels[measured + 2],
                    "mid_cutoff": cutoffs[measured + 1], "side_cutoff": cutoffs[measured + 2],
                    "mid_holes": find_holes(counters[measured + 1], rate, size, cutoffs[measured + 1], bandwidths[measured + 1]),
                    "side_holes": find_holes(counters[measured + 2], rate, size, cutoffs[measured + 2], bandwidths[measured + 2])}

    coherence = None
    if pair:
        coherence = find_coherence(sums[pair[0]], sums[pair[1]], cross_re, cross_im, coherence_counters, frames, cutoffs[measured], bandwidths[measured], rate, size)
        coherence = {"left_channel": pair[0], "right_channel": pair[1], **coherence}

    last_hop = max(h for _, h in mins) if mins else 0
    shift = 0
    while (last_hop >> shift) >= 1024:
        shift += 1
    columns = (last_hop >> shift) + 1
    waveform = []
    for c in range(measured):
        low = [0] * columns
        high = [0] * columns
        seen = [False] * columns
        for (channel, h), v in mins.items():
            if channel != c:
                continue
            col = h >> shift
            lo = math.floor(v * 32768.0)
            hi = math.ceil(maxs[(c, h)] * 32768.0)
            if not seen[col]:
                low[col], high[col], seen[col] = lo, hi, True
            else:
                low[col], high[col] = min(low[col], lo), max(high[col], hi)
        waveform.append({"minimum": [max(-32768, min(32767, v)) for v in low], "maximum": [max(-32768, min(32767, v)) for v in high]})
    return {
        "sample_rate": rate, "channels": channels, "channels_measured": measured, "bits": bits, "samples": samples, "first_position": first_position or 0,
        "lost_samples": lost, **({"withheld_samples": withheld} if withheld else {}), "gaps": gaps, "fft_size": size, "frames": frames, "combined": levels[measured], "per_channel": levels[:measured],
        "cutoff": cutoffs[measured], "channel_cutoffs": cutoffs[:measured], "bandwidth": bandwidths[measured],
        "channel_bandwidths": bandwidths[:measured], "slope": slopes[measured], "channel_slopes": slopes[:measured], "partial_cutoff": partial, "holes": holes, "mid_side": mid_side, "coherence": coherence, "replicated_bands": replicated_bands, "levels": levels_of(runs, measured, bits, rate),
        "loudness": loudness_of(runs, measured, bits, rate, layout), "true_peak": true_peak_of(runs, measured, bits, rate), "spectrogram_columns": columns, "hops_per_column": 1 << shift, "waveform": waveform,
    }
