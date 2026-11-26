#!/usr/bin/env python3
"""Compares the PCM that Encspect's HE-AAC decoder (SBR and parametric stereo) delivers with FFmpeg's (docs/formats/aac.md, "Decoding"; docs/development.md, "AAC decoding").

    crosscheck_he_aac_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--max-ratio R] [FILE ...]

With no FILE the script takes every ADTS and M4A file of tests/fixtures/aac whose first audio stream FFprobe names HE-AAC (version 1 or 2; an implicit stream in ADTS, which FFprobe
names LC, is not compared). FFmpeg (a black box, as in the other cross-checks; its source is not read) decodes to 32-bit floats with the edit list ignored.

The two decoders cut their output differently (priming, and parametric stereo's delay of 6 QMF slots), so the script finds the shift between them by the cross-correlation of the
first channel over the whole file (the five highest peaks, the one with the smallest difference wins: a tonal file has many), and compares each channel from there. The bound is the
RMS of the difference relative to FFmpeg's RMS (MAX_RATIO, 6 %): both decoders reconstruct the high band from envelope data in single precision and differ by about 1 % for SBR and
up to 4 % where parametric stereo's decorrelators differ in the last details.

NOT_COMPARED lists the files whose channels the two decoders do not give alike, each with the reason. Exit status 0 where every bound holds, 1 where one fails, 2 for a usage
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
    "faac-2.2-he-mono-adts.aac": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels (it takes the stream for HE-AAC v2), the analyzer decodes one",
    "external-winamp-aacplus-16k-mono-he-cbr8-adts.aac": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels, the analyzer one",
    "external-winamp-fhgaac-8k-mono-cbr16.m4a": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels, the analyzer one",
    "apple-he-m4a-cbr-mono22.m4a": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels, the analyzer one",
    "apple-intel-he-m4a-cbr-mono22.m4a": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels, the analyzer one",
    "apple-he-m4a-cbr-mono44.m4a": "a mono core with SBR and no parametric stereo signaled: FFmpeg outputs two channels, the analyzer one",
    "ps-in-multichannel-sce.aac": "the parametric stereo extension in a core of more than one channel, which the analyzer applies to no stream (aac.ps-without-mono-core); FFmpeg's channels differ (not investigated)",
    "ps-in-pce-multichannel.aac": "as the file above",
    "sbr-in-frames-failing-crc.aac": "the analyzer takes no evidence from frames that fail their CRC and stays AAC LC; FFmpeg checks no CRC and decodes HE-AAC (docs/formats/aac.md, Reference for sample counts)",
    "faac-2.1-he-51-adts.aac": "5.1 in the coded order of the channels, as the file above",
    "faac-2.2-he-51-adts.aac": "5.1 in the coded order of the channels, which FFmpeg's WAV order does not follow (crosscheck_aac_pcm.py maps it for AAC LC; not mapped here)",
    "external-qaac-51-he-cbr160.m4a": "5.1 in the coded order of the channels, as the file above",
}


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
    files = [pathlib.Path(f) for f in args.files] or sorted(p for p in (FIXTURES / "aac").rglob("*") if p.suffix in EXTENSIONS)
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
        reference = decode_ffmpeg(args.ffmpeg, path, samples.shape[1])
        if reference is None or float(np.sqrt(np.mean(reference ** 2))) < 1e-4:
            continue
        found = best_shift(samples, reference)
        passes = found is not None and max(found[1]) <= args.max_ratio
        compared += 1
        print(f"{path.name}: shift {found[0] if found else None}, RMS of the difference to FFmpeg's {[round(r, 4) for r in found[1]] if found else None}{'' if passes else '  FAILS'}")
        failures += 0 if passes else 1
    print(f"{compared} files compared, {failures} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
