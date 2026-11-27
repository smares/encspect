#!/usr/bin/env python3
"""Compares the PCM that Encspect's HE-AAC decoder (SBR and parametric stereo) delivers with FFmpeg's (docs/formats/aac.md, "Decoding"; docs/development.md, "AAC decoding").

    crosscheck_he_aac_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--max-ratio R] [FILE ...]

With no FILE the script takes every ADTS and M4A file of tests/fixtures/aac and the hand-built mp4/ps-34-bands.m4a and mp4/ps-mixed-bands.m4a (intensity differences in 34 bands, coherences in 20) and, downloaded and pinned, the two MPEG-4 conformance streams al_sbr_ps_04_new.mp4 and al_sbr_ps_06_new.mp4 (parametric stereo that starts in the second half) (parametric stereo in 34 bands, which no encoder's file uses; it agrees with FFmpeg to under 5e-5) whose first decoded stream the analyzer reports as HE-AAC (version 1 or 2, SBR or parametric stereo, signaled or implicit: PcmDump's "streamCodec"). A stream it reports as AAC LC, SBR data in some blocks included, is
crosscheck_aac_pcm.py's, which compares its core and lists what FFmpeg decodes otherwise. FFmpeg (a black box, as in the other cross-checks; its source is not read) decodes to 32-bit floats with the edit list ignored.

The two decoders cut their output differently (priming, and parametric stereo's delay of 6 QMF slots), so the script finds the shift between them by the cross-correlation of the
first channel over the whole file (the five highest peaks, the one with the smallest difference wins: a tonal file has many), and compares each channel from there. The bound is the
RMS of the difference relative to FFmpeg's RMS (MAX_RATIO, 6 %): both decoders reconstruct the high band from envelope data in single precision and differ by about 1 % for SBR and
up to 4 % where parametric stereo's decorrelators differ in the last details.

A mono core with SBR and no parametric stereo is compared with the first of FFmpeg's two (equal) channels, and a stream of more than two channels in FFmpeg's WAV order, mapped from the analyzer's layout as crosscheck_aac_pcm.py maps it. LAYOUTS names the layout of the one file whose PCM format carries none. Exit status 0 where every bound holds, 1 where one fails, 2 for a usage
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

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import check_sbr_samples  # noqa: E402
import downloads  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
EXTENSIONS = {".aac", ".m4a", ".mp4"}
MAX_RATIO = 0.06
# Samples left out at each end of the overlap (the two decoders' starts and ends differ), fewer in a file too short for that to leave an eighth of it.
TRIM = 4096

# The MPEG-4 audio parametric stereo conformance streams of FFmpeg's FATE suite (pinned by tests/conformance/sbr-samples.json, downloaded into .cache/sbr): a mono core with SBR whose PS data starts
# in the second half of the stream, so that the channels are equal until it does (ISO/IEC 14496-3 1.6.6.3). The signal repeats, so the correlation's peaks do not tell the alignment: both start at 0.
CONFORMANCE = ["al_sbr_ps_04_new.mp4", "al_sbr_ps_06_new.mp4"]
FIXED_SHIFT = {name: 0 for name in CONFORMANCE}


def conformance_files():
    manifest = json.loads(check_sbr_samples.MANIFEST.read_text())
    cache = ROOT / ".cache" / "sbr"
    cache.mkdir(parents=True, exist_ok=True)
    out = []
    for sample in manifest["samples"]:
        if sample["file"] in CONFORMANCE:
            downloads.fetch(check_sbr_samples.addresses(sample), sample["sha256"], cache / sample["file"])
            out.append(cache / sample["file"])
    return out


# Files that FFmpeg reads otherwise than the analyzer, each with the reason: the comparison runs and must fail (an entry that no longer differs fails the run, the stale-entry rule of
# docs/development.md).
DIFFERENCES = {
    "sbr-too-many-patches.aac": "the header's frequency tables need six patches, where 4.6.18.3.6 allows five: the analyzer reads none of the SBR data (aac.sbr-header-invalid) and upsamples the core, where FFmpeg builds the patches and "
                                "applies the data",
    "sbr-after-first-frame-failing-crc.aac": "the first frame fails its CRC and holds no SBR data: the analyzer takes no evidence from it and decodes the stream as HE-AAC at twice the core rate, "
                                              "where FFmpeg checks no CRC, finds no SBR data in its first block and decodes the core alone, at 22,050 Hz (docs/formats/aac.md, Reference for sample counts)",
}

# Files whose PCM format names no channel layout although the report does (a stream configured by a program config element in its blocks, not by an audio specific config or a
# channel configuration): the layout the report states, so that the channels are mapped to FFmpeg's order like any other.
LAYOUTS = {
    "ps-in-pce-multichannel.aac": ["front center", "front left", "front right"],
}


# FFmpeg's channel order for more than two channels is the WAV one.
WAV_ORDER = ["front left", "front right", "front center", "LFE", "back left", "back right", "front left of center", "front right of center", "back center", "side left", "side right"]


def to_ffmpeg_order(samples, layout):
    """The analyzer's channels in the order FFmpeg writes them, where the layout names every channel with a WAV position."""
    if samples.shape[1] <= 2 or not layout or any(name not in WAV_ORDER for name in layout):
        return samples
    order = sorted(range(samples.shape[1]), key=lambda c: WAV_ORDER.index(layout[c]))
    return samples[:, order]


# Files that use noise substitution in most of their channel streams: each decoder fills the substituted bands with its own noise, so they are compared by the energy of each channel in
# bands of the spectrum (a Hann-windowed average over frames of 2,048 samples with half overlap, which keeps the strong band's leakage out of the weak ones), at most BAND_DB apart; a band
# more than QUIET_DB below the channel's total is left out. Measured with FFmpeg 6.1.1: 1.24 dB for FAAC 2.2's file and 0.41 dB for 2.1's (libxaac's test decoder, the outlier, differs from both FFmpeg and
# the analyzer by about 8.5 dB on the 2.2 file).
NOISE_BANDS = ["faac-2.1-he-51-adts.aac", "faac-2.2-he-51-adts.aac"]
BAND_EDGES = [0, 500, 1000, 2000, 4000, 8000, 16000]
BAND_DB = 3.0
QUIET_DB = 50.0
FRAME = 2048


def band_energies(x, rate):
    window = np.hanning(FRAME)
    total = np.zeros(FRAME // 2 + 1)
    for at in range(0, len(x) - FRAME + 1, FRAME // 2):
        total += np.abs(np.fft.rfft(x[at:at + FRAME] * window)) ** 2
    freqs = np.fft.rfftfreq(FRAME, 1.0 / rate)
    return [float(total[(freqs >= low) & (freqs < high)].sum()) for low, high in zip(BAND_EDGES, BAND_EDGES[1:])]


def band_differences(a, b, shift, rate):
    """The largest difference in dB between the energies of a and b in the bands of BAND_EDGES, over the channels, from the samples both have."""
    start = max(0, -shift) + 4096
    stop = min(len(a), len(b) - shift) - 4096
    worst = 0.0
    for ch in range(a.shape[1]):
        x = band_energies(a[start:stop, ch], rate)
        y = band_energies(b[start + shift:stop + shift, ch], rate)
        loudest = max(max(x), max(y))
        for ex, ey in zip(x, y):
            if min(ex, ey) > 0 and max(ex, ey) > loudest * 10 ** (-QUIET_DB / 10):
                worst = max(worst, abs(10 * np.log10(ex / ey)))
    return worst


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
    start = max(0, -shift)
    stop = min(len(a), len(b) - shift)
    trim = min(TRIM, (stop - start) // 8)
    start += trim
    stop -= trim
    if stop - start < 2048:
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
    files = [pathlib.Path(f) for f in args.files] or sorted(p for p in (FIXTURES / "aac").rglob("*") if p.suffix in EXTENSIONS) + [FIXTURES / "mp4" / "ps-34-bands.m4a", FIXTURES / "mp4" / "ps-mixed-bands.m4a"] + conformance_files()
    failures = 0
    compared = 0
    listed = set()
    for path in files:
        with tempfile.TemporaryDirectory() as folder:
            dumped = analyzer_pcm(args.pcmdump, path, pathlib.Path(folder))
        if dumped is None:
            continue
        samples, fmt = dumped
        if not fmt.get("streamCodec", "").startswith("HE-AAC"):
            continue
        samples = to_ffmpeg_order(samples, fmt.get("channelLayout") or LAYOUTS.get(path.name))
        # A mono core with SBR is output on two channels by both decoders (equal where no parametric stereo data comes, ISO/IEC 14496-3 1.6.6.3); where the analyzer keeps one (PS signaled absent), it is compared with FFmpeg's first.
        reference = decode_ffmpeg(args.ffmpeg, path, ffmpeg_channels(path, samples.shape[1]))
        if reference is not None and samples.shape[1] == 1:
            reference = reference[:, :1]
        if reference is None or float(np.sqrt(np.mean(reference ** 2))) < 1e-4:
            continue
        if path.name in NOISE_BANDS:
            shift = best_shift(samples[:, :1], reference[:, :1])
            difference = band_differences(samples, reference, shift[0] if shift else 0, fmt["sampleRate"])
            passes = difference <= BAND_DB
            compared += 1
            print(f"{path.name}: noise substitution, band energies differ by at most {difference:.2f} dB{'' if passes else '  FAILS'}")
            failures += 0 if passes else 1
            continue
        found = best_shift(samples, reference) if path.name not in FIXED_SHIFT else (FIXED_SHIFT[path.name], ratios(samples, reference, FIXED_SHIFT[path.name]))
        if found is None and path.name in DIFFERENCES:
            listed.add(path.name)
            compared += 1
            print(f"{path.name}: differs as listed: {DIFFERENCES[path.name]}")
            continue
        if found is None:
            print(f"{path.name}: not compared, fewer than 2,048 samples remain after the ends are left out")
            continue
        passes = found is not None and max(found[1]) <= args.max_ratio
        compared += 1
        if path.name in DIFFERENCES:
            listed.add(path.name)
            print(f"{path.name}: {'listed in DIFFERENCES, but the decoders no longer differ on it  FAILS' if passes else 'differs as listed: ' + DIFFERENCES[path.name]}")
            failures += 1 if passes else 0
            continue
        print(f"{path.name}: shift {found[0] if found else None}, RMS of the difference to FFmpeg's {[round(r, 4) for r in found[1]] if found else None}{'' if passes else '  FAILS'}")
        failures += 0 if passes else 1
    if not args.files:
        for name in sorted(set(DIFFERENCES) - listed):
            print(f"{name}: listed in DIFFERENCES, but the analyzer decodes no such file as HE-AAC  FAILS")
            failures += 1
    print(f"{compared} files compared, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
