"""Checks the Layer III "Coded Bandwidth" and "Intensity Stereo Bound" of every committed MPEG fixture report against minimp3.

minimp3's Huffman loop, patched to record where it ends (minimp3_coded.py), gives each granule's and channel's coded lines; the band widths it used place
a short block's lines on the 576-line scale (a line of window w in a band of width n at index i is at 3 * (start of the band / 3 + i)), and a frame's
bandwidth is the highest of its granules' edges, 576ths of half the sample rate, rounded half to even. The report's series must equal that at every frame it
has a value for.

The intensity stereo bound is the lowest of the granules' first bands that minimp3's L3_stereo_process takes the intensity branch for (patched to record them),
at 576ths of half the sample rate, half to even; the series has a point at a frame exactly where one does.

    python3 -I scripts/fixtures/crosscheck_mpeg_bandwidth.py [file.mp3 ...]
"""

import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import minimp3_coded  # noqa: E402

# Files whose frames minimp3 reads differently from the specification.
EXPLAINED = {
    "side-info-long-blocks.mp3": "the private bits are 111, which minimp3 reads as scfsi bits of the first granule, so its Huffman data of the second channel start 35 bits early",
}

FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mpeg-audio"


def top(granule):
    end = granule["end"]
    if end <= 0:
        return 0
    widths = granule["widths"]
    if granule["block_type"] != 2:
        return min(end, 576)
    long_lines = sum(widths[: granule["long_bands"]])
    if end <= long_lines:
        return end
    result, window_start, entry = long_lines, long_lines // 3, long_lines
    band = granule["long_bands"]
    while band + 2 < len(widths) and widths[band] != 0:
        width = widths[band]
        for window in range(3):
            start = entry + window * width
            if end > start:
                result = max(result, 3 * (window_start + min(width, end - start)))
        entry += 3 * width
        window_start += width
        band += 3
    return min(result, 576)


def half_even(numerator, denominator):
    quotient, remainder = divmod(numerator, denominator)
    if remainder * 2 > denominator or (remainder * 2 == denominator and quotient % 2):
        quotient += 1
    return quotient


def main(argv):
    binary = minimp3_coded.build()
    files = [pathlib.Path(a) for a in argv] or sorted(FIXTURES.rglob("*.mp3"))
    compared = compared_bounds = failed = 0
    for path in files:
        report = path.with_suffix(".report.json")
        if not report.exists():
            continue
        streams = json.loads(report.read_text())[0]["streams"]
        series = [s for stream in streams for s in stream["series"] if s["name"] == "Coded Bandwidth"]
        if not series:
            continue
        values = {x: v for s in series for x, v in zip(s["x"], s["values"])}
        mismatches = []
        bounds = {}
        for s in streams:
            for series_ in s["series"]:
                if series_["name"] == "Intensity Stereo Bound":
                    bounds.update(zip(series_["x"], series_["values"]))
        frames = minimp3_coded.run(binary, path)
        for frame in frames:
            if frame["offset"] not in values or not frame["granules"]:
                continue
            expected = half_even(max(top(g) for g in frame["granules"]) * frame["hz"], 1152)
            compared += 1
            if values[frame["offset"]] != expected:
                mismatches.append(f"frame at {frame['offset']}: {values[frame['offset']]} Hz, minimp3 gives {expected}")
        for frame in frames:
            if frame["offset"] not in values or not frame["granules"]:
                continue
            starts = [i for i in frame["intensity"] if i >= 0]
            expected = half_even(min(starts) * frame["hz"], 1152) if starts else None
            compared_bounds += 1
            if bounds.get(frame["offset"]) != expected:
                mismatches.append(f"frame at {frame['offset']}: intensity stereo bound {bounds.get(frame['offset'])} Hz, minimp3 gives {expected}")
        if mismatches and path.name in EXPLAINED:
            print(f"ok   {path.name}: {len(mismatches)} frames differ, listed: {EXPLAINED[path.name]}")
        elif mismatches:
            failed += 1
            print(f"FAIL {path.name}: {len(mismatches)} frames differ; the first: {mismatches[0]}")
    print(f"{compared} frames compared for the coded bandwidth, {compared_bounds} for the intensity stereo bound, {failed} files failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
