#!/usr/bin/env python3
"""Checks the measurement fixtures (tests/fixtures/measurement, docs/design/spectral-analysis.md) against independent tools.

For every FLAC file there (the synthetic ones from make_measurement_fixtures.py and the lossy-derived ones from
make_external_measurement_fixtures.py):

- flac -t (flac 1.5.0 of Ubuntu 26.04) accepts the file: every frame's CRC-8 and CRC-16 and STREAMINFO's MD5 hold. The two gap files are
  left out (their frame numbers jump, which flac -t reports); their reason is printed.
- flac -d: the decoded samples (little-endian signed integers of as many whole bytes as the bit depth needs) have the MD5 of the samples that
  flac_model.py's decoder gives, the audio the measurements are made of, and FFmpeg 8.0.1 decodes the same samples (read as 32-bit integers).
- ffprobe: the sample rate and the channels are the model's.

- FFmpeg's astats (docs/design/spectral-analysis.md, "Cross-checks"): the levels in the committed manifest (measurement_model.py's, which
  MeasurementFixtureTests holds the analyzer to) against what astats prints for each channel and for the file ("Overall"):
    - Peak level dB, RMS level dB and DC offset, to within a tenth of a dB (the rounding of the model's tenths) and 3e-6 of full scale, after the
      one convention that differs: astats divides the samples by 2^(B-1) - 1, the largest positive code, where Encspect divides by 2^(B-1) and counts
      the most negative code as full scale (B is 16 for samples of up to 16 bits and 32 above, the sample format FFmpeg's FLAC decoder writes,
      with the samples of fewer bits shifted up). So a stream whose most negative code is reached reads +0.000265 dB (16 bits) in astats
      and 0.0 dBFS in Encspect; the RMS level of a full-scale square wave is likewise +0.000133 dB there. Both use the full-scale square wave as 0 dB
      for the RMS level (a full-scale sine reads -3.010045 in astats), and Overall is all channels together for the RMS level and the DC offset of
      the channel with the largest one, as Encspect's stream figures are.
    - "Bit depth" prints four numbers; the third is the declared bits less the low bits that are never set (Low Bits Unused), the one that
      follows the OR of the samples (the others also change with the highest bit used and with the values' mean), observed on FFmpeg 6.1.1.
    - Number of samples, per channel.
  What astats cannot check: the clipping runs (its Peak count counts occasions at the smallest and largest value of the data, whatever they
  are, of any length), the digital silence (it has no figure for it) and the stream figure of the sample peak (Overall peak is the highest
  channel's, which is checked as the maximum).
- FFmpeg's silencedetect with a tolerance of one least significant bit (it compares an integer threshold, so exact zero needs more than 1.5e-5 and
  the file's samples of magnitude 1 may join a stretch): every stretch of 442 samples or more it prints for a file of up to 16 bits and up to 10 s
  (it prints six digits) lies between the stretch of exact zeros and the stretch of samples of magnitude 1 or less that the decoded samples have,
  in every channel at once, which checks the digital silence's positions and lengths where they are long.
- The PCM files (the WAV, Wave64 and AIFF twins and the fixtures only WAV can hold, step 3 of the rollout): FFmpeg decodes them to the MD5 of the canonical layout
  that pcm_model.py gives (the samples of the 16-bit twins, the floats, the NaN and the samples of 1e300 included); a twin's samples are also the FLAC
  file's, which MeasurementPcmTests holds the analyzer to. The header of 65,535 channels is skipped: FFmpeg refuses more channels than it has layouts for.
The measurements themselves are checked against measurement_model.py by MeasurementFixtureTests; FFmpeg's showspectrum is not needed. The tools' versions are pinned, so a field they do not print is a failure, as is a
file for which no figure was compared; the flac -t skip of the gap files is the one skip left. astats and silencedetect print their figures in the form of FFmpeg 6.1.1; the script reads them by
name and reports a field it cannot find as a failure.

    python3 scripts/fixtures/crosscheck_measurement_fixtures.py [name ...]
"""

import hashlib
import json
import math
import pathlib
import re
import subprocess
import sys
import tempfile

import aiff_model
import flac_model as m
import tool_versions
import wav_model

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "measurement"
GAPS = {"synthetic-gap-frames-removed", "synthetic-gap-jump-2-40", "synthetic-levels-gaps"}


def run(arguments):
    return subprocess.run(arguments, capture_output=True)


def astats(path):
    """(channels, overall): the dictionaries of what astats prints for each channel and for the file, by field name."""
    result = run(["ffmpeg", "-hide_banner", "-i", str(path), "-af", "astats=metadata=0", "-f", "null", "-"])
    channels, overall, current = [], {}, None
    for line in result.stderr.decode(errors="replace").splitlines():
        match = re.match(r"\[Parsed_astats_\d+ @ \w+\] (.*)$", line)
        if not match:
            continue
        text = match.group(1)
        if text.startswith("Channel:"):
            current = {}
            channels.append(current)
        elif text == "Overall":
            current = overall
        elif ": " in text and current is not None:
            key, value = text.split(": ", 1)
            current[key] = value
    return channels, overall


def number(value):
    """A printed figure as a float (NaN where it cannot be read, which no comparison accepts), None where it is not printed."""
    if value is None:
        return None
    try:
        return float(value)
    except ValueError:
        return math.nan


def tenths_to_db(tenths):
    return None if tenths is None else tenths / 10


def check_astats(path, expected, info, found):
    """The levels of the manifest against astats; returns the number of figures compared. Every figure that astats does not print, as the tools'
    versions are pinned, is a failure."""
    levels = expected["model"]["levels"]
    channels, overall = astats(path)
    if len(channels) != len(levels["channels"]) or not overall:
        found.append(f"astats printed {len(channels)} channels and {'an' if overall else 'no'} overall section for {len(levels['channels'])} channels")
        return 0
    container = 16 if info["bits"] <= 16 else 32
    scale = ((1 << (container - 1)) - 1) / (1 << (container - 1))
    correction = 20 * math.log10(scale)
    compared = 0

    def figure(got, key, name):
        value = got.get(key)
        if value is None:
            found.append(f"astats prints no {key} for {name}")
        return value

    def level(text):
        value = number(text)
        return None if value is None else (None if value == -math.inf else value + correction)

    def compare(what, text, tenths):
        nonlocal compared
        if text is None:
            return
        compared += 1
        got_db = level(text)
        if tenths is None:
            if got_db is not None:
                found.append(f"astats {what} is {got_db:.2f} dB, the model has none")
        elif got_db is None or not (abs(got_db * 10 - tenths) <= 1.0):
            found.append(f"astats {what} is {got_db} dB, the model {tenths / 10}")

    pairs = [(f"channel {i + 1}", channels[i], levels["channels"][i]) for i in range(len(channels))] + [("overall", overall, levels["stream"])]
    for name, got, want in pairs:
        compare(f"{name} peak", figure(got, "Peak level dB", name), want["sample_peak_tenths"])
        compare(f"{name} RMS", figure(got, "RMS level dB", name), want["rms_tenths"])
        dc = number(figure(got, "DC offset", name))
        if dc is not None:
            compared += 1
            if not (abs(dc * scale - want["dc_micropercent"] / 1e8) <= 3e-6):
                found.append(f"astats {name} DC offset is {dc}, the model {want['dc_micropercent'] / 1e8}")
    for name, got, want in pairs[:-1]:
        if want["low_bits_unused"] is None:
            continue
        depth = (figure(got, "Bit depth", name) or "").split("/")
        if len(depth) != 4:
            found.append(f"astats prints the bit depth of {name} as {got.get('Bit depth')!r}, not four numbers")
            continue
        compared += 1
        if not depth[2].isdigit() or int(depth[2]) != levels["bits"] - want["low_bits_unused"]:
            found.append(f"astats {name} bit depth is {got['Bit depth']}, the model {levels['bits']} less {want['low_bits_unused']} low bits unused")
    samples = number(figure(overall, "Number of samples", "overall"))
    if samples is not None:
        compared += 1
        if not math.isfinite(samples) or int(samples) != expected["model"]["samples"]:
            found.append(f"astats counts {int(samples)} samples, the model {expected['model']['samples']}")
    if compared == 0:
        found.append("astats: no figure compared")
    return compared


def silence_runs(pcm_channels, threshold):
    """The stretches (start, length) of at least 442 samples in which every channel's samples have a magnitude of at most threshold."""
    runs, start = [], None
    for i in range(len(pcm_channels[0])):
        quiet = all(abs(c[i]) <= threshold for c in pcm_channels)
        if quiet and start is None:
            start = i
        elif not quiet and start is not None:
            runs.append((start, i - start))
            start = None
    if start is not None:
        runs.append((start, len(pcm_channels[0]) - start))
    return [r for r in runs if r[1] >= 442]


def check_silence(path, stream, info, found):
    """silencedetect's stretches against the decoded samples' stretches of exact zeros (inside) and of magnitude 1 or less (outside)."""
    rate = info["sample_rate"]
    samples = [[] for _ in range(info["channels"])]
    for frame in stream.frames:
        if "channels" in frame:
            for c in range(info["channels"]):
                samples[c].extend(frame["channels"][c])
    result = run(["ffmpeg", "-hide_banner", "-i", str(path), "-af", "silencedetect=n=3.06e-5:d=0.01", "-f", "null", "-"])
    text = result.stderr.decode(errors="replace")
    if result.returncode != 0:
        found.append(f"silencedetect: ffmpeg fails: {text.strip()[-200:]}")
        return
    try:
        starts = [float(x) for x in re.findall(r"silence_start: (\S+)", text)]
        ends = [float(x) for x in re.findall(r"silence_end: (\S+)", text)]
    except ValueError as error:
        found.append(f"silencedetect prints a figure that cannot be read: {error}")
        return
    if len(starts) != len(ends):
        found.append(f"silencedetect printed {len(starts)} starts and {len(ends)} ends")
        return
    detected = [(round(a * rate), round(b * rate)) for a, b in zip(starts, ends)]
    inner, outer = silence_runs(samples, 0), silence_runs(samples, 1)
    for run_start, length in inner:
        end = run_start + length
        if not any(a - 1 <= run_start and end <= b + 1 for a, b in detected):
            found.append(f"silencedetect finds no stretch around the exact zeros {run_start} to {end}")
    for a, b in detected:
        if not any(s - 1 <= a and b <= s + n + 1 for s, n in outer):
            found.append(f"silencedetect prints {a} to {b}, which lies in no stretch of samples of magnitude 1 or less")


# The encoder FFmpeg writes the canonical layout of a decoded stream with, by pcm_model.py's format and bits.
FFMPEG_CANONICAL = {("integer", 8): "pcm_s8", ("integer", 16): "pcm_s16le", ("integer", 24): "pcm_s24le", ("integer", 32): "pcm_s32le",
                    ("float", 32): "pcm_f32le", ("float", 64): "pcm_f64le"}


def check_pcm_files(names, problems, skipped):
    """FFmpeg's decoded MD5 of each WAV, Wave64 and AIFF file against pcm_model.py's, for the files in the directory; returns how many were compared."""
    compared = 0
    for path in sorted(p for suffix in ("*.wav", "*.w64", "*.aiff") for p in DIRECTORY.glob(suffix) if not names or p.stem in names):
        data = path.read_bytes()
        pcm = ((aiff_model if path.suffix == ".aiff" else wav_model).manifest(data)["stream"] or {}).get("pcm")
        if pcm is None or "md5" not in pcm:
            problems.append(f"{path.name}: pcm_model.py gives no decoded samples for the file")
            continue
        if path.stem == "synthetic-pcm-channels-65535":
            skipped.append(f"{path.name}: ffmpeg (it refuses 65,535 channels)")
            continue
        result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-c:a", FFMPEG_CANONICAL[(pcm["format"], pcm["bits"])], "-f", "md5", "-"])
        found = result.stdout.decode(errors="replace").strip().removeprefix("MD5=")
        compared += 1
        if found != pcm["md5"]:
            problems.append(f"{path.name}: FFmpeg's decoded MD5 is {found or result.stderr.decode(errors='replace').strip()[:120]!r}, pcm_model.py's {pcm['md5']}")
    return compared


def main():
    tool_versions.require("flac", "ffmpeg", "ffprobe")
    names = sys.argv[1:]
    problems = []
    skipped = []
    files = sorted(p for p in DIRECTORY.glob("*.flac") if not names or p.stem in names)
    with tempfile.TemporaryDirectory() as work:
        work = pathlib.Path(work)
        for path in files:
            found = []
            stream = m.Stream(path.read_bytes())
            info = stream.info
            if path.stem in GAPS:
                skipped.append(f"{path.stem}: flac -t, flac -d and ffmpeg (the frame numbers jump)")
            else:
                result = run(["flac", "-t", "-s", str(path)])
                if result.returncode != 0:
                    found.append(f"flac -t rejects the file: {result.stderr.decode(errors='replace').strip()[:200]}")
                bits = info["bits"]
                width = (bits + 7) // 8
                pcm, _ = stream.pcm()
                want = hashlib.md5(pcm).hexdigest()
                out = work / "flac.raw"
                result = run(["flac", "-d", "-s", "-f", "--force-raw-format", "--endian=little", "--sign=signed", "-o", str(out), str(path)])
                if result.returncode != 0:
                    found.append(f"flac -d fails: {result.stderr.decode(errors='replace').strip()[:200]}")
                elif hashlib.md5(out.read_bytes()).hexdigest() != want:
                    found.append("flac -d decodes other samples than the model")
                ffmpeg_out = work / "ffmpeg.raw"
                result = run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-f", "s32le", str(ffmpeg_out)])
                if result.returncode != 0:
                    found.append(f"ffmpeg fails: {result.stderr.decode(errors='replace').strip()[:200]}")
                else:
                    raw = ffmpeg_out.read_bytes()
                    values = [int.from_bytes(raw[i:i + 4], "little", signed=True) >> (32 - bits) for i in range(0, len(raw), 4)]
                    shifted = b"".join((v & ((1 << width * 8) - 1)).to_bytes(width, "little") for v in values)
                    if hashlib.md5(shifted).hexdigest() != want:
                        found.append("ffmpeg decodes other samples than the model")
            expected = json.loads(path.with_suffix(".expected.json").read_text())
            if "levels" not in expected.get("model", {}):
                found.append("the manifest has no levels")
            else:
                check_astats(path, expected, info, found)
            if path.stem in GAPS and path.stem.startswith("synthetic-levels-"):
                skipped.append(f"{path.stem}: silencedetect (it follows the timestamps and counts a gap in the frame numbers as silence, so its stretches cross the gaps the levels keep apart)")
            elif path.stem.startswith("synthetic-levels-") and info["bits"] <= 16:
                check_silence(path, stream, info, found)
            probe = run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)])
            streams = json.loads(probe.stdout or b"{}").get("streams", [])
            if not streams:
                found.append("ffprobe finds no audio stream")
            else:
                for key, want_value in (("sample_rate", info["sample_rate"]), ("channels", info["channels"])):
                    if str(streams[0].get(key)) != str(want_value):
                        found.append(f"ffprobe {key} is {streams[0].get(key)}, the model {want_value}")
            problems += [f"{path.stem}: {f}" for f in found]
    pcm_files = check_pcm_files(names, problems, skipped)
    print(f"{len(files)} files checked, {pcm_files} PCM files compared with ffmpeg")
    for line in skipped:
        print("skipped", line)
    for problem in problems:
        print("FAIL", problem)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
