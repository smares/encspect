#!/usr/bin/env python3
"""Compares the PCM that Encspect's AAC LC decoder delivers with FFmpeg's, and with libxaac's test decoder where it is built
(docs/formats/aac.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "AAC decoding").

    crosscheck_aac_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--libxaac] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every ADTS, M4A and MP4 file of tests/fixtures/aac whose first stream the analyzer decodes.

For each file:

- PcmDump writes the samples of the first decoded stream (floats at full scale 1.0, interleaved, in the coded order of the channels) with the runs of positions they cover.
  Positions are untrimmed, at the coded stage: the first block's output is the first 1,024 samples.
- FFmpeg (a black box, as in the other cross-checks; its source is not read) decodes the file to 32-bit floats with the edit list ignored. It drops a priming of its own at the
  start of a container's stream, so the script finds the number of analyzer samples before its first sample (0 for ADTS, 1,024 or 2,112 for M4A and MP4 files, else a search over the
  first 4,200) and compares from there. Its channels of more than two come in WAV order, which the script maps from the analyzer's layout.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its largest magnitude at
  most 2^-14): floats against floats, nothing rounded to 16 bits.
- Frames that use noise substitution cannot match sample by sample (each decoder draws its own noise): NOISE lists the files, with the edges of their scalefactor bands, and for
  them compare_pcm.py compares the energy of each band over windows of frames, within 0.5 dB.
- DIFFERENCES lists the files whose output FFmpeg's decoder differs from the analyzer's on purpose or by what the file is, each with the reason. An entry that no longer differs
  fails the run (the stale-entry rule of docs/development.md), as does a difference that has no entry.
- With --libxaac the test decoder of libxaac (github.com/ittiam-systems/libxaac, the commit libxaac_tool.py pins, built with its default CMake options and no fused multiply-add)
  decodes each file to 16 bits with its peak limiter off, and the analyzer's floats are rounded to nearest and compared within 2 LSB and an RMS of 1 LSB, from the position
  the script finds (libxaac's output starts later than the coded stage by a figure of its own).

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import array
import json
import math
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
EXTENSIONS = {".aac", ".m4a", ".mp4"}

import aac_tables  # noqa: E402

RATES = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350]


def band_edges(rate):
    """The edges, in hertz, of the long scalefactor bands at the sample rate (ISO/IEC 14496-3 Table 4.110, aac_tables.SWB_LONG), without the first and the last."""
    index = min(range(len(RATES)), key=lambda i: abs(RATES[i] - rate))
    offsets = aac_tables.SWB_LONG[index]
    return ",".join(f"{o * rate / 2.0 / 1024:.1f}" for o in offsets[1:-1])


# The files that code noise substitution (each decoder draws its own noise, so the samples cannot match): compared by the energy of each band over windows of frames.
NOISE = {
    "faac-1.31.1-adts.aac": "codes noise substitution",
    "faac-1.31.1.m4a": "codes noise substitution",
    "faac-1.40-adts.aac": "codes noise substitution",
    "faac-1.40-pink-equal-adts.aac": "codes noise substitution",
    "faac-1.40-pink-side-12-db-adts.aac": "codes noise substitution",
    "faac-1.40.m4a": "codes noise substitution",
    "faac-1.50-adts.aac": "codes noise substitution",
    "faac-1.50.m4a": "codes noise substitution",
    "faac-2.2-adts.aac": "codes noise substitution",
    "faac-2.2-tags.m4a": "codes noise substitution",
    "faac-2.2.m4a": "codes noise substitution",
    "faac-adts.aac": "codes noise substitution",
    "faac.m4a": "codes noise substitution",
    "ffmpeg-adts.aac": "codes noise substitution",
    "ffmpeg-fast.aac": "codes noise substitution",
    "ffmpeg-fragmented.mp4": "codes noise substitution",
    "ffmpeg-lc-vbr.m4a": "codes noise substitution",
    "ffmpeg-lc.m4a": "codes noise substitution",
    "ffmpeg-mono-faststart.m4a": "codes noise substitution",
    "mp4box-mux.m4a": "codes noise substitution",
    "stereo-coding.aac": "codes noise substitution",
    "stereo.aac": "codes noise substitution",
    "windows.aac": "codes noise substitution",
    "apple-intel-passthru-m4a-from-faac-m4a.m4a": "codes noise substitution",
    "apple-intel-passthru-m4a-from-ffmpeg-adts.m4a": "codes noise substitution",
    "apple-lc-m4a-cbr-six48-lout.m4a": "codes noise substitution",
    "apple-passthru-adts-from-ffmpeg-adts.aac": "codes noise substitution",
    "apple-passthru-m4a-from-faac-m4a.m4a": "codes noise substitution",
    "apple-passthru-m4a-from-ffmpeg-adts.m4a": "codes noise substitution",
}

# Files whose output FFmpeg's decoder differs from the analyzer's, with the reason. The analyzer follows libxaac (docs/design/lossy-decoders.md, decision 1); the differences are
# in what the specification leaves to the decoder and the hand-built files exercise.
DIFFERENCES = {
    "corrupt-soft.aac": "raw data blocks with more channel streams than the stream's configuration states: the analyzer does not decode them (decode.frame-invalid), FFmpeg decodes the channels it can",
    "sbr-in-one-block.aac": "a raw data block with SBR data in a stream that signals none: the analyzer does not decode the block (HE-AAC comes with its own decoder), FFmpeg decodes its core",
    "sbr-in-three-blocks.aac": "raw data blocks with SBR data in a stream that signals none: the analyzer does not decode them (HE-AAC comes with its own decoder), FFmpeg decodes their core",
}


def default_files():
    return sorted(p for p in (FIXTURES / "aac").rglob("*") if p.suffix in EXTENSIONS)


def decode_ffmpeg(ffmpeg, path):
    result = subprocess.run([ffmpeg, "-v", "error", "-i", str(path), "-ignore_editlist", "1", "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    return result.stdout if result.returncode == 0 and result.stdout else None


# FFmpeg's channel order for more than two channels is the WAV one.
WAV_ORDER = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center",
             "side left", "side right"]


def to_ffmpeg_order(samples, layout):
    channels = len(layout)
    if channels <= 2 or any(name not in WAV_ORDER for name in layout):
        return samples
    order = sorted(range(channels), key=lambda c: WAV_ORDER.index(layout[c]))
    mapped = array.array("f", [0.0]) * len(samples)
    for frame in range(len(samples) // channels):
        for target, source in enumerate(order):
            mapped[frame * channels + target] = samples[frame * channels + source]
    return mapped


def find_start(analyzer, reference, channels, candidates=(0, 1024, 2112)):
    """The number of analyzer samples before the reference's first one: the candidate (or the search over the first 4,200) with the smallest difference over a stretch."""
    count = min(len(analyzer), len(reference)) // channels
    stretch = min(2048, count // 4) * channels
    middle = (count // 2) * channels
    middle -= middle % channels

    def error(lag):
        start = middle + lag * channels
        if start + stretch > len(analyzer) or middle + stretch > len(reference):
            return math.inf
        return sum((analyzer[start + i] - reference[middle + i]) ** 2 for i in range(0, stretch, 3))

    best = min(candidates, key=error)
    if error(best) > 1e-9:
        best = min(range(0, 4200), key=error)
    return best


def compare(manifest, reference_file, channels, start, extra=(), fmt="float32"):
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(reference_file), "--reference-format", fmt, "--reference-channels",
                             str(channels), "--reference-first-position", str(start), "--json", *extra], capture_output=True, text=True)
    if output.returncode == 2:
        return None, output.stderr.strip()
    return json.loads(output.stdout), (None if output.returncode == 0 else "outside the bound")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--libxaac", action="store_true", help="also compare with libxaac's test decoder (built from the pinned commit)")
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = args.files or default_files()
    xaac = None
    failed = compared = decoded = 0
    seen = set()
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        if args.libxaac:
            import libxaac_tool
            xaac = libxaac_tool.build(base)
        dump = base / "dump"
        result = subprocess.run(shlex.split(args.pcmdump) + [str(dump)] + [str(f) for f in files], capture_output=True)
        if result.returncode not in (0, 1):
            print(f"PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
            return 2
        for number, path in enumerate(files):
            manifest = dump / f"{number:04d}-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            if not streams:
                continue
            decoded += 1
            stream = streams[0]
            channels = stream["channels"]
            mine = array.array("f")
            mine.frombytes((dump / stream["pcm"]).read_bytes())
            problems = []
            note = ""
            reference_bytes = decode_ffmpeg(args.ffmpeg, path)
            if reference_bytes is None:
                print(f"ok   {path.name} (ffmpeg decodes nothing)")
                continue
            reference = array.array("f")
            reference.frombytes(reference_bytes)
            if len(reference) // channels == 0:
                continue
            start = find_start(to_ffmpeg_order(mine, stream.get("channelLayout") or [""] * channels), reference, channels)
            raw = base / "ffmpeg.f32"
            raw.write_bytes(reference_bytes)
            # The manifest's samples are in the analyzer's order; compare_pcm.py takes the reference in the same order, so the reference is mapped back.
            if channels > 2 and stream.get("channelLayout") and all(n in WAV_ORDER for n in stream["channelLayout"]):
                order = sorted(range(channels), key=lambda c: WAV_ORDER.index(stream["channelLayout"][c]))
                back = array.array("f", [0.0]) * len(reference)
                for frame in range(len(reference) // channels):
                    for target, source in enumerate(order):
                        back[frame * channels + source] = reference[frame * channels + target]
                raw.write_bytes(back.tobytes())
            extra = []
            if path.name in NOISE:
                extra = ["--bands", band_edges(stream["sampleRate"]), "--no-sample-bounds", "--band-frame", "2048"]
                note = f"noise substitution: bands compared ({NOISE[path.name]})"
            outcome, found = compare(manifest, raw, channels, start, extra)
            compared += 1
            if path.name in NOISE and outcome is not None:
                # No sample bounds: the bands decide, and a file too short for a band to show 0.5 dB compares none (inconclusive, as compare_pcm.py says), which is no failure.
                bands = outcome["bands"]["channels"]
                outside = sum(b["bandsOutside"] for b in bands)
                found = f"{outside} bands differ by more than {outcome['bands']['toleranceDb']} dB" if outside else None
                if not outside and not sum(b["bandsCompared"] for b in bands):
                    note += "; too few frames for a band to show 0.5 dB (inconclusive)"
            if found:
                problems.append(f"ffmpeg: {found}")
            elif outcome is not None and path.name not in NOISE and outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"] and start == 0 and not outcome["notComparedOnlyInReference"]:
                note = (note + "; " if note else "") + f"positions only the analyzer has: {outcome['notComparedOnlyInAnalyzer']}"
            if xaac is not None and not problems:
                pcm = base / "xaac.pcm"
                done = subprocess.run([str(xaac), f"-ifile:{path}", f"-ofile:{pcm}", "-pcmsz:16", "-peak_limiter_off:1"], capture_output=True)
                if done.returncode == 0 and pcm.exists() and pcm.stat().st_size > 0 and channels <= 2:
                    values = array.array("h")
                    values.frombytes(pcm.read_bytes())
                    lag = find_start([v / 32768.0 for v in mine], [v / 32768.0 for v in values], channels, (0, 1024, 2112, 209))
                    outcome2, found2 = compare(manifest, pcm, channels, lag, extra, fmt="int16")
                    if found2:
                        problems.append(f"libxaac: {found2}")
            key = path.name
            seen.add(key)
            if key in DIFFERENCES:
                if problems:
                    note = (note + "; " if note else "") + "differs: " + DIFFERENCES[key]
                    problems = []
                else:
                    problems.append("listed in DIFFERENCES, but the decoders no longer differ on it")
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                print(f"ok   {path.name}" + (f" ({note})" if note else ""))
    if args.files == [] or not args.files:
        for key in DIFFERENCES:
            if key not in seen:
                failed += 1
                print(f"FAIL {key}: listed in DIFFERENCES, but the analyzer decodes no such file")
    print(f"{decoded} files decoded, {compared} compared with FFmpeg, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
