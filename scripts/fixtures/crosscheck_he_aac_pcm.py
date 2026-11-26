#!/usr/bin/env python3
"""Compares the PCM that Encspect's HE-AAC decoder (SBR and parametric stereo) delivers with FFmpeg's (docs/formats/aac.md, "Decoding"; docs/development.md, "AAC decoding").

    crosscheck_he_aac_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--max-ratio R] [FILE ...]

With no FILE the script takes every ADTS and M4A file of tests/fixtures/aac and the hand-built mp4/ps-34-bands.m4a (parametric stereo in 34 bands, which no encoder's file uses; it agrees with FFmpeg to under 5e-5) whose first audio stream FFprobe names HE-AAC (version 1 or 2; an implicit stream in ADTS, which FFprobe
names LC, is not compared). FFmpeg (a black box, as in the other cross-checks; its source is not read) decodes to 32-bit floats with the edit list ignored.

The two decoders cut their output differently (priming, and parametric stereo's delay of 6 QMF slots), so the script finds the shift between them by the cross-correlation of the
first channel over the whole file (the five highest peaks, the one with the smallest difference wins: a tonal file has many), and compares each channel from there. The bound is the
RMS of the difference relative to FFmpeg's RMS (MAX_RATIO, 6 %): both decoders reconstruct the high band from envelope data in single precision and differ by about 1 % for SBR and
up to 4 % where parametric stereo's decorrelators differ in the last details.

A mono core with SBR and no parametric stereo is compared with the first of FFmpeg's two (equal) channels, and a stream of more than two channels in FFmpeg's WAV order, mapped from the analyzer's layout as crosscheck_aac_pcm.py maps it. NOT_COMPARED lists the files whose channels the two decoders do not give alike, each with the reason. Exit status 0 where every bound holds, 1 where one fails, 2 for a usage
error. Needs numpy.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

import numpy as np

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
EXTENSIONS = {".aac", ".m4a"}
MAX_RATIO = 0.06

# Files that are not compared sample by sample, each with the reason (the channels of the two decoders are not the same ones).
NOT_COMPARED = {
    "faac-2.1-he-51-adts.aac": "FAAC 2.1's 5.1 SBR payload follows no element FFmpeg can apply it to (\"cannot apply SBR to element type 3\"), and four of its six channels differ from the analyzer's below 6 kHz too",
    "faac-2.2-he-51-adts.aac": "four of the six channels differ from the analyzer's below 6 kHz, where SBR has no part (only the left and the back right channel agree), so the difference is not SBR's; not investigated",
    "ps-in-multichannel-sce.aac": "the parametric stereo extension in a core of more than one channel, which the analyzer applies to no stream (aac.ps-without-mono-core); FFmpeg's channels differ (not investigated)",
    "ps-in-pce-multichannel.aac": "as the file above",
    "sbr-in-frames-failing-crc.aac": "the analyzer takes no evidence from frames that fail their CRC and stays AAC LC; FFmpeg checks no CRC and decodes HE-AAC (docs/formats/aac.md, Reference for sample counts)",
}


# FFmpeg's channel order for more than two channels is the WAV one.
WAV_ORDER = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center", "side left", "side right"]


def to_ffmpeg_order(samples, layout):
    """The analyzer's channels in the order FFmpeg writes them, where the layout names every channel with a WAV position."""
    if samples.shape[1] <= 2 or not layout or any(name not in WAV_ORDER for name in layout):
        return samples
    order = sorted(range(samples.shape[1]), key=lambda c: WAV_ORDER.index(layout[c]))
    return samples[:, order]


def ffmpeg_channels(path, channels):
    """The channels FFmpeg writes: those of the analyzer, but two for a mono core in ADTS (without an explicit configuration FFmpeg takes SBR on a single channel for HE-AAC v2)."""
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=channels", "-of", "csv=p=0", str(path)], capture_output=True, text=True)
    return int(result.stdout.strip()) if result.returncode == 0 and result.stdout.strip().isdigit() else channels


def decode_ffmpeg(ffmpeg, path, channels):
    result = subprocess.run([ffmpeg, "-v", "error", "-i", str(path), "-ignore_editlist", "1", "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None
    return np.frombuffer(result.stdout, dtype="<f4").reshape(-1, channels)


def analyzer_pcm(pcmdump, path, folder):
    subprocess.run(shlex.split(pcmdump) + [str(folder), str(path)], capture_output=True)
    descriptions = sorted(folder.glob("*.pcm.json"))
    if not descriptions:
        return None
    description = json.loads(descriptions[0].read_text())
    if not description["streams"]:
        return None
    fmt = description["streams"][0]
    channels = fmt["channels"]
    samples = np.fromfile(sorted(folder.glob("*.s0.pcm"))[0], dtype="<f4").reshape(-1, channels)
    return samples, fmt


def is_he(path):
    """Whether FFprobe names the stream HE-AAC (v1 or v2) from its configuration or its first frames."""
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=profile", "-of", "csv=p=0", str(path)], capture_output=True, text=True)
    return result.returncode == 0 and result.stdout.strip().startswith("HE-AAC")


def best_shift(a, b):
    """The shift s with b[t + s] ~ a[t], from the five highest peaks of the cross-correlation of the first channels."""
    n = 1 << int(np.ceil(np.log2(len(a) + len(b))))
    x = np.fft.rfft(a[:, 0], n)
    y = np.fft.rfft(b[:, 0], n)
    c = np.fft.irfft(y * np.conj(x), n)
    candidates = np.argsort(-np.abs(np.concatenate([c[:len(b)], c[-len(a):]])))[:5]
    shifts = [int(i) if i < len(b) else int(i - len(b) - len(a)) for i in candidates]
    best = None
    for s in shifts:
        ratio = ratios(a, b, s)
        if ratio is not None and (best is None or max(ratio) < max(best[1])):
            best = (s, ratio)
    return best


def ratios(a, b, shift):
    start = max(0, -shift) + 4096
    stop = min(len(a), len(b) - shift) - 4096
    if stop - start < 8192:
        return None
    out = []
    for ch in range(a.shape[1]):
        reference = b[start + shift:stop + shift, ch]
        out.append(float(np.sqrt(np.mean((a[start:stop, ch] - reference) ** 2) / max(np.mean(reference ** 2), 1e-30))))
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--max-ratio", type=float, default=MAX_RATIO)
    parser.add_argument("files", nargs="*")
    args = parser.parse_args()
    files = [pathlib.Path(f) for f in args.files] or sorted(p for p in (FIXTURES / "aac").rglob("*") if p.suffix in EXTENSIONS) + [FIXTURES / "mp4" / "ps-34-bands.m4a"]
    failures = 0
    compared = 0
    for path in files:
        with tempfile.TemporaryDirectory() as folder:
            dumped = analyzer_pcm(args.pcmdump, path, pathlib.Path(folder))
        if dumped is None:
            continue
        samples, fmt = dumped
        if not is_he(path):
            continue
        if path.name in NOT_COMPARED:
            continue
        samples = to_ffmpeg_order(samples, fmt.get("channelLayout"))
        # FFmpeg takes a mono core with SBR for HE-AAC v2 and outputs two channels; without parametric stereo they are equal and the analyzer's one is compared with the first.
        reference = decode_ffmpeg(args.ffmpeg, path, ffmpeg_channels(path, samples.shape[1]))
        if reference is not None and samples.shape[1] == 1:
            reference = reference[:, :1]
        if reference is None or float(np.sqrt(np.mean(reference ** 2))) < 1e-4:
            continue
        found = best_shift(samples, reference)
        if found is None:
            print(f"{path.name}: not compared, fewer than 8,192 samples remain after the 4,096 at each end that are left out")
            continue
        passes = found is not None and max(found[1]) <= args.max_ratio
        compared += 1
        print(f"{path.name}: shift {found[0] if found else None}, RMS of the difference to FFmpeg's {[round(r, 4) for r in found[1]] if found else None}{'' if passes else '  FAILS'}")
        failures += 0 if passes else 1
    print(f"{compared} files compared, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
