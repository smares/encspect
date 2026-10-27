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

What is independent of the analyzer is the FFT, the rows (the window, the scale and the normalization), the waveform and the stream figures.
The cutoff's rules are checked against the construction of the signals (a brickwall at a known edge, none for noise, rolloff and mixed
content) and against the printed bands of the lossy files, not against a second reading of the rules.

The levels (levels_of) are worked out on Python integers from the decoded samples and the design's text alone, with no code shared with the
analyzer: the peak, RMS and DC offset from exact sums (the logarithm of a float only at the end), the clipping runs of three or more samples at
one extreme code, the digital silence of the sample frames that are 0 in every channel (and of each channel), the low bits that are never set, and
nothing counted across a gap. The analyzer's logarithm is its own series and the model's is the C library's, so the model's tenths of a dB are
compared to within one tenth; every count and the DC offset in millionths of a percent are exact.

Everything is plain Python (the standard library). The FFT is a complex radix-2 transform of the full frame, apart from the analyzer's
real transform with its split step, so the two agree up to rounding (about 1e-12 relative), which the tests allow for.
"""

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


def rows_of(samples, window):
    """The 512 rows of mean power of one frame of samples (in full scale +-1), relative to a full-scale sine centered on a bin."""
    n = len(window)
    spectrum = fft([complex(s * w, 0.0) for s, w in zip(samples, window)])
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


def band_rows(rate, size):
    """(W, first candidate row, bins a row): 1 kHz in rows and the first row whose upper edge reaches 2 kHz."""
    bins = size // (ROWS * 2)
    w = max(1, (2000 * size + rate * bins) // (2 * rate * bins))
    first = -(-2000 * size // (rate * bins)) - 1
    return w, max(w - 1, first), bins


def hertz(boundary, rate, size, bins):
    return (2 * boundary * bins * rate + size) // (2 * size)


class Counters:
    def __init__(self):
        self.all = [[0] * 72 for _ in range(ROWS)]
        self.show = [[0] * 72 for _ in range(ROWS)]


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


def floor_div(a, b):
    return a // b


def frames_at(counters, r):
    """(frames with signal, frames that show an own step) at a row: signal is within ten levels of the level 1 % of the frames reach."""
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
        for l in range(max(0, reference - 10), 72):
            with_signal += counters.all[r][l]
            showing += counters.show[r][l]
    return with_signal, showing


def find_cutoff(level_tenths, counters, rate, size):
    """The spectral cutoff of a spectrum given as 512 tenths of a dB (None for a row without power) and its counters."""
    if rate < 8000:
        return {"is_searched": False}
    w, first, bins = band_rows(rate, size)
    level = [-2000 if v is None else v for v in level_tenths]
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
    step = floor_div(2 * numerator + denominator, 2 * denominator)
    with_signal, showing = frames_at(counters, r)
    result = {"is_searched": True, "is_found": False, "frequency": 0, "transition_start": 0, "step_frequency": hertz(r + 1, rate, size, bins),
              "step_tenths": step, "frames_showing": showing, "frames_with_signal": with_signal}
    if step >= 250 and with_signal > 0 and 2 * showing >= with_signal:
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
        while i >= 0 and level[i] * w < under_sum - 30 * w:
            start = i
            i -= 1
        result.update(is_found=True, frequency=hertz(edge + 1, rate, size, bins), transition_start=hertz(start, rate, size, bins))
    return result


def tenths_of_ratio(ratio):
    """Tenths of a dB of an amplitude ratio (None for 0): floor(200 log10 r + 0.5), halves up, the one rounding of every figure."""
    return None if ratio == 0 else math.floor(200 * math.log10(ratio) + 0.5)


def tz(value):
    return (value & -value).bit_length() - 1


def silence_of(segments):
    """(start, end, total) of the zero flags of the segments between gaps: the start is the zeros before the first non-zero sample of the first
    segment, the end the zeros after the last non-zero sample of the last, and the total all zeros; no stretch crosses a gap."""
    first, last = segments[0], segments[-1]
    return (next((i for i, z in enumerate(first) if not z), len(first)),
            next((i for i, z in enumerate(reversed(last)) if not z), len(last)),
            sum(sum(seg) for seg in segments))


def levels_of(runs, channels, bits):
    """The levels of integer samples. runs: the segments of the stream in order, each a list of channel lists (the samples between two gaps).
    Returns the stream's figures and each channel's."""
    full = 1 << (bits - 1)
    high, low = full - 1, -full
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
                    runs=clip_runs, clipped=clipped, longest=longest, silence=silence_of([[v == 0 for v in seg] for seg in segs]))

    def figures(f):
        denominator = count * full
        micro = 0
        if f["total"] != 0:
            micro = (2 * abs(f["total"]) * 10 ** 8 + denominator) // (2 * denominator)
            micro = micro if f["total"] > 0 else -micro
        return {
            "sample_peak_tenths": tenths_of_ratio(f["peak"] / full),
            "rms_tenths": None if f["squares"] == 0 else math.floor(100 * math.log10(f["squares"] / (count * full * full)) + 0.5),
            "dc_micropercent": micro,
            "dc_tenths": tenths_of_ratio(abs(f["total"]) / count / full),
            "clipping_runs": f["runs"], "clipped_samples": f["clipped"], "longest_clipping_run": f["longest"],
            "silent_at_start": f["silence"][0], "silent_at_end": f["silence"][1], "silent_samples": f["silence"][2],
            "low_bits_unused": tz(f["bit_or"]) if f["bit_or"] else None, "highest_magnitude": f["peak"],
        }

    per = [sums(c) for c in range(channels)]
    result = [figures(f) for f in per]
    joint = silence_of([[all(seg[c][i] == 0 for c in range(channels)) for i in range(len(seg[0]))] for seg in runs])
    squares = sum(f["squares"] for f in per)
    bit_or = 0
    for f in per:
        bit_or |= f["bit_or"]
    biggest = max(range(channels), key=lambda c: abs(result[c]["dc_micropercent"]))
    stream = {
        "sample_peak_tenths": max((r["sample_peak_tenths"] for r in result if r["sample_peak_tenths"] is not None), default=None),
        "rms_tenths": None if squares == 0 else math.floor(100 * math.log10(squares / (count * channels * full * full)) + 0.5),
        "dc_micropercent": result[biggest]["dc_micropercent"], "dc_tenths": result[biggest]["dc_tenths"],
        "clipping_runs": sum(r["clipping_runs"] for r in result), "clipped_samples": sum(r["clipped_samples"] for r in result),
        "longest_clipping_run": max(r["longest_clipping_run"] for r in result),
        "silent_at_start": joint[0], "silent_at_end": joint[1], "silent_samples": joint[2],
        "low_bits_unused": tz(bit_or) if bit_or else None, "highest_magnitude": max(f["peak"] for f in per),
    }
    return {"bits": bits, "stream": stream, "channels": result}


def segments(stream):
    """The decoded blocks of a FLAC stream as (position, [channel lists])."""
    out = []
    for f in stream.frames:
        if "channels" in f:
            out.append((f["first_sample"], f["channels"]))
    return out


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

    sums = [[0.0] * ROWS for _ in range(measured + 1)]
    counters = [Counters() for _ in range(measured + 1)]
    frames = 0
    samples = lost = gaps = 0
    first_position = None
    origin = 0
    expected = None
    mins, maxs = {}, {}
    buffers = [[] for _ in range(measured)]
    segment_start = 0
    runs = []

    def process(frame_samples):
        nonlocal frames
        per = [rows_of([v * scale for v in frame_samples[c]], window) for c in range(measured)]
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
        frames += 1

    for position, block in segments(stream):
        if expected is None:
            first_position = position
            origin = position // hop * hop
            expected = position
            segment_start = position
        if position > expected:
            lost += position - expected
            gaps += 1
            expected = position
            buffers = [[] for _ in range(measured)]
            segment_start = position
            runs.append([[] for _ in range(measured)])
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

    levels = [[None if sums[c][r] <= 0 or frames == 0 else tenths(sums[c][r] / frames) for r in range(ROWS)] for c in range(measured + 1)]
    cutoffs = [find_cutoff(levels[c], counters[c], rate, size) for c in range(measured + 1)]

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
        "lost_samples": lost, "gaps": gaps, "fft_size": size, "frames": frames, "combined": levels[measured], "per_channel": levels[:measured],
        "cutoff": cutoffs[measured], "channel_cutoffs": cutoffs[:measured], "levels": levels_of(runs, measured, bits), "spectrogram_columns": columns, "hops_per_column": 1 << shift, "waveform": waveform,
    }
