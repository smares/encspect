#!/usr/bin/env python3
"""Compares the PCM that Encspect's decoder of Windows Media Audio versions 1 and 2 delivers with FFmpeg's, and where a Windows run exists with Microsoft's
(docs/formats/asf-wma.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "WMA versions 1 and 2 decoding").

    crosscheck_wma_pcm.py [--pcmdump CMD] [--microsoft DIR] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump (the default is its Release build in the repository). With no FILE the script takes the fixtures of versions 1 and 2 in tests/fixtures/asf:
the encoder files of ffmpeg, the Windows files, the remuxes and the derived noise substitution files.

For each file:

- PcmDump writes the samples of the first decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are at the coded stage: the
  output of the first frames is the start the stream's own syntax trims (two frames with the bit reservoir, one without it, as Microsoft's decoder does), so the first run begins at
  twice or once the frame length.
- FFmpeg (a black box, as in the other cross-checks) decodes the file to 32-bit floats. It drops the first two frames, so it starts at the analyzer's first sample where the stream has the bit reservoir, and one frame after it where it has not. It does not
  decode the frame the last superframe announces, and outputs the last overlap whole, complete or not, so the last frame of its output is left out of the comparison (the analyzer's
  tail holds the frame it did not decode, and the samples its blocks no longer change).
- Per channel the RMS of the difference must be at most 2^-15 / sqrt(12) of full scale and its largest magnitude 2^-14: floats against floats, nothing rounded to 16 bits. The
  fixtures agree to 3 x 10^-7, the rounding of single precision. Noise substitution draws the generator of docs/design/wma-standard-decoding.md, which FFmpeg's decoder draws too,
  so the files that code it agree sample by sample (Microsoft's generator is not known, see below).
- DIFFERENCES lists the files whose output FFmpeg's decoder differs from the analyzer's on purpose, each with the reason; an entry that no longer differs fails the run (the
  stale-entry rule of docs/development.md), as does a difference that has no entry. Eight committed fixtures have an entry where the analyzer follows Microsoft's decoder (the version 1 coefficient limit, the exponents of a channel not coded), and six of the
  sample kit files under asf/sample-kit have one for their line spectral pairs in short blocks; so do the files of the private sample kit that have them (the block's own angles, item 1 of "Points to settle against Microsoft's decoder" in docs/design/wma-standard-decoding.md); name them on the command line to see it.

With --microsoft DIR (the artifact of the Windows run: `wma-decode-probe wma-pcm`, `.github/workflows/windows-wma-decode.yml`, a folder of <name>.f32 and <name>.json) the same
files are compared with Microsoft's Source Reader output, which is the reference: the sample counts must be equal, and the RMS and the largest difference are printed for each file.
A file that codes noise substitution cannot match sample by sample (Microsoft's generator is not known): the figures are listed and are no failure. The comparison is
informational (a difference between decoders is a finding, not a failure) unless --strict is given.

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
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "asf"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

RMS_BOUND = 2.0 ** -15 / math.sqrt(12)
MAX_BOUND = 2.0 ** -14

PATTERNS = ["external-ffmpeg-wmav*.wma", "external-windows-wma92-[cv]*.wma", "external-remux-ffmpeg-wma92-*.wma", "derived-ffmpeg-wmav2-*.asf", "sample-kit/external-wme9-*.wma"]

# The files that code noise substitution, whose output Microsoft's decoder cannot reproduce sample by sample.
NOISE_PREFIXES = ("external-ffmpeg-wmav1-mono-22k", "external-ffmpeg-wmav2-stereo-44k-32k", "derived-ffmpeg-wmav2-noise", "external-windows-wma92-cbr-stereo-22k-20-sdk", "external-windows-wma92-cbr-stereo-44k-32-sdk",
                  "external-windows-wma92-cbr-stereo-44k-48-sdk", "external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk")

# Files whose output follows Microsoft's decoder where ffmpeg's differs (docs/design/wma-standard-decoding.md, "What Microsoft's output showed"), and the files of the private sample kit whose
# line spectral pair exponents sit in short blocks: ffmpeg evaluates the curve at the angles of a block as long as the frame.
LIMIT = "version 1 codes fewer positions (Microsoft's coefficient limit)"
CONVERSION = "a channel not coded when exponents are sent is converted (Microsoft's rule)"
DIFFERENCES = {
    "external-ffmpeg-wmav1-mono-22k-24k.wma": LIMIT,
    "external-ffmpeg-wmav1-stereo-44k-128k.wma": LIMIT,
    "external-ffmpeg-wmav1-stereo-44k-48k.wma": LIMIT,
    "external-remux-ffmpeg-wma92-cbr-stereo-22k-20-sdk.wma": CONVERSION,
    "external-remux-ffmpeg-wma92-cbr-stereo-22k-32.wma": CONVERSION,
    "external-windows-wma92-cbr-stereo-22k-20-sdk.wma": CONVERSION,
    "external-windows-wma92-cbr-stereo-22k-32.wma": CONVERSION,
    "external-windows-wma92-cbr-stereo-44k-32-sdk.wma": CONVERSION,
    "external-wme9-wma9-cbr-10k-16k-mono.wma": "line spectral pairs in short blocks",
    "external-wme9-wma9-cbr-12k-16k-mono.wma": "line spectral pairs in short blocks",
    "external-wme9-wma9-cbr-16k-16k-mono.wma": "line spectral pairs in short blocks",
    "external-wme9-wma9-cbr2pass-16k-16k-mono.wma": "line spectral pairs in short blocks",
    "external-wme9-wma9-cbr-16k-16k-stereo.wma": "line spectral pairs in short blocks",
    "external-wme9-wma9-cbr-20k-16k-stereo.wma": "line spectral pairs in short blocks",
    "music-16k-mono-wma9-cbr-10_16_1.wma": "line spectral pairs in short blocks",
    "music-16k-mono-wma9-cbr-12_16_1.wma": "line spectral pairs in short blocks",
    "music-16k-mono-wma9-cbr-16_16_1.wma": "line spectral pairs in short blocks",
    "music-16k-mono-wma9-cbr2pass-16_16_1.wma": "line spectral pairs in short blocks",
    "music-44k-stereo-wma9-cbr-16_16_2.wma": "line spectral pairs in short blocks",
    "music-44k-stereo-wma9-cbr-20_16_2.wma": "line spectral pairs in short blocks",
    "music-44k-stereo-moviemaker-16.wma": "line spectral pairs in short blocks",
}


def read_floats(path):
    values = array.array("f")
    values.frombytes(pathlib.Path(path).read_bytes())
    if sys.byteorder == "big":
        values.byteswap()
    return values


def channel_figures(analyzer, reference, channels, count, limit=None):
    """The RMS of the difference and its largest magnitude, per channel, over the first count samples of each channel (the first limit where given)."""
    count = count if limit is None else min(count, limit)
    figures = []
    for c in range(channels):
        total = 0.0
        largest = 0.0
        for i in range(count):
            d = analyzer[i * channels + c] - reference[i * channels + c]
            total += d * d
            if abs(d) > largest:
                largest = abs(d)
        figures.append((math.sqrt(total / max(count, 1)), largest))
    return figures


def dump(pcmdump, folder, path):
    subprocess.run([*shlex.split(pcmdump), str(folder), str(path)], check=True, capture_output=True)
    meta = json.loads(next(pathlib.Path(folder).glob("*.pcm.json")).read_text())
    stream = meta["streams"][0]
    return stream, read_floats(pathlib.Path(folder) / stream["pcm"])


def ffmpeg_pcm(path, out):
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", str(out)], check=True)
    return read_floats(out)


def frame_length(stream, path):
    rate = stream["sampleRate"]
    version1 = "wmav1" in path.name
    return 512 if rate <= 16000 else 1024 if rate <= 22050 or (version1 and rate <= 32000) else 2048


def check(path, args, work):
    folder = pathlib.Path(tempfile.mkdtemp(dir=work))
    stream, mine = dump(args.pcmdump, folder, path)
    channels = stream["channels"]
    frame = frame_length(stream, path)
    runs = stream["runs"]
    messages = []
    failed = False
    if len(runs) != 1 or runs[0][0] not in (frame, 2 * frame):
        return [f"the runs are {runs}, not one that starts at {frame} or {2 * frame}"], True
    reference = ffmpeg_pcm(path, folder / "ffmpeg.f32")

    # ffmpeg's output starts at twice the frame length; a stream without the bit reservoir starts one frame earlier here, and those samples are left out of this comparison.
    ahead = (2 * frame - runs[0][0]) * channels
    count = min(len(mine) - ahead, len(reference) - frame * channels) // channels
    figures = channel_figures(mine[ahead:], reference, channels, max(count, 0))
    rms = max(f[0] for f in figures)
    largest = max(f[1] for f in figures)
    holds = rms <= RMS_BOUND and largest <= MAX_BOUND
    known = DIFFERENCES.get(path.name)
    if known is not None:
        if holds:
            messages.append(f"is listed as different ({known}) and no longer differs")
            failed = True
    elif not holds:
        messages.append(f"differs from ffmpeg: RMS {rms:.2e}, largest {largest:.2e} (bounds {RMS_BOUND:.2e} and {MAX_BOUND:.2e})")
        failed = True
    status = "differs as listed" if known is not None and not holds else "ok" if holds else "differs"
    print(f"{path.name}: {count} samples against ffmpeg, RMS {rms:.2e}, largest {largest:.2e}: {status}")
    if args.microsoft:
        messages += microsoft(path, mine, channels, args, frame)
        failed |= any("failed" in m for m in messages)
    return messages, failed


def microsoft(path, mine, channels, args, frame):
    pcm = pathlib.Path(args.microsoft) / (path.name + ".f32")
    if not pcm.exists():
        return [f"has no record of Microsoft's decoder in {args.microsoft}"]
    theirs = read_floats(pcm)
    # The files written by ffmpeg carry no start time, so Microsoft's output begins one frame before the analyzer's first sample; the Windows files' begin with it.
    lead = min((0, frame), key=lambda n: sum(f[0] for f in channel_figures(mine, theirs[n * channels:], channels, min(len(mine), len(theirs) - n * channels) // channels, 4 * frame)))
    theirs = theirs[lead * channels:]
    note = []
    if lead:
        note.append(f"Microsoft's output starts {lead} samples before the analyzer's")
    if len(theirs) != len(mine):
        note.append(f"Microsoft's decoder gives {len(theirs) // channels} samples, the analyzer {len(mine) // channels}")
    count = min(len(theirs), len(mine)) // channels
    figures = channel_figures(mine, theirs, channels, count)
    rms = max(f[0] for f in figures)
    largest = max(f[1] for f in figures)
    isNoise = path.name.startswith(NOISE_PREFIXES)
    print(f"  against Microsoft's decoder: {count} samples, RMS {rms:.2e}, largest {largest:.2e}{' (noise substitution)' if isNoise else ''}")
    if args.strict and not isNoise and (rms > RMS_BOUND or largest > MAX_BOUND or note):
        note.append("failed: Microsoft's decoder differs beyond the bounds")
    return note


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--microsoft", help="the folder of a Windows run's PCM files")
    parser.add_argument("--strict", action="store_true", help="fail where Microsoft's decoder differs beyond the bounds")
    parser.add_argument("--keep", help="a folder to keep the decoded files in")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args(argv)
    paths = [pathlib.Path(f) for f in args.files] or sorted({p for pattern in PATTERNS for p in FIXTURES.glob(pattern)})
    if not paths:
        print("no fixtures found", file=sys.stderr)
        return 2
    failures = 0
    work = args.keep or tempfile.mkdtemp()
    pathlib.Path(work).mkdir(parents=True, exist_ok=True)
    for path in paths:
        messages, failed = check(path, args, work)
        for message in messages:
            print(f"  {path.name}: {message}")
        failures += failed
    print(f"{len(paths)} files, {failures} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
