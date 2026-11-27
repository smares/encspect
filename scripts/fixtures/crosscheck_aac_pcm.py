#!/usr/bin/env python3
"""Compares the PCM that Encspect's AAC LC decoder delivers with FFmpeg's, and with libxaac's test decoder where it is built
(docs/formats/aac.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "AAC decoding").

    crosscheck_aac_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--libxaac] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every ADTS, M4A and MP4 file of tests/fixtures/aac whose first stream the analyzer decodes. A stream the analyzer decodes as HE-AAC (SBR or parametric stereo; the stream's
codec in PcmDump's manifest) is left to crosscheck_he_aac_pcm.py: its output is not the core's, which is all FFmpeg's and libxaac's decoders are compared with here.

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
  decodes each file to 16 bits with its peak limiter off, and the analyzer's floats are rounded to nearest and compared within an RMS of 1 LSB and 32 LSB at the most (LIBXAAC_MAX_LSB), from the position
  the script finds (libxaac's output starts about 200 samples later than the coded stage, a figure of its own), the samples at full scale left out where it clips. A file with samples over full scale is not compared: libxaac's 16-bit output does not saturate them.

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
    "stereo.aac": "codes noise substitution",
    "loud-noise-pair.aac": "codes noise substitution",
    "intensity-noise.aac": "codes noise substitution",
    "apple-intel-passthru-m4a-from-faac-m4a.m4a": "codes noise substitution",
    "apple-intel-passthru-m4a-from-ffmpeg-adts.m4a": "codes noise substitution",
    "apple-passthru-adts-from-ffmpeg-adts.aac": "codes noise substitution",
    "apple-passthru-m4a-from-faac-m4a.m4a": "codes noise substitution",
    "apple-passthru-m4a-from-ffmpeg-adts.m4a": "codes noise substitution",
}

# libxaac computes in fixed point and interpolates its table of q^(4/3) linearly (at most about 12 LSB of the 16-bit output, at loud peaks): on the music files its RMS is 0.6 LSB and
# the largest difference of an unlisted file is 21 LSB, so the design's 2 LSB is widened to 32 for it (the RMS bound of 1 LSB stays).
LIBXAAC_MAX_LSB = 32

# The first block's left window half has its own shape in the standard (4.6.11.3.2: for the first raw_data_block to be decoded the left and right halves have the same shape),
# where FFmpeg's decoder and libxaac's start from the sine window; the analyzer follows the standard, so the first 1,024 samples (the first block's output) are left out of both comparisons.
FIRST_BLOCK = 1024

# Files where libxaac's test decoder differs from the analyzer beyond that, each with the reason, the blocks of 1,024 samples where it does (the positions at the coded stage) and the
# largest difference in LSB it is expected not to pass: a change of the analyzer on these files, or a difference elsewhere in them, fails the run. The analyzer computes the
# standard's arithmetic in each case, and FFmpeg's decoder agrees with it to about 10^-8 on the music files; four of the entries are one cause.
LIBXAAC_TNS = ("libxaac's 16-bit path for TNS filters in one or two channels (ixheaacd_tns_decode_coef, ixheaacd_tns_parcor_lpc_convert and ixheaacd_tns_ar_filter in "
               "ixheaacd_pns_js_thumb.c): the filter's coefficients are 16 bits, and near 0.996 the all-pole filter amplifies their rounding, where the analyzer filters with the "
               "standard's real values (a libxaac build with 32-bit TNS agrees within 22 LSB on all four files)")
LIBXAAC_ORDER = ("the order of the tools: libxaac generates noise after intensity stereo (ixheaacd_channel.c), so an intensity band over a noise band of the first channel copies zeros, where "
                 "Figure 4.2 and 4.6.8.2.3 scale the noise (FFmpeg's decoder gives the second channel the noise too)")
LIBXAAC_DIFFERENCES = {
    "faac-1.31.1-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.31.1.m4a": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40.m4a": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40-pink-equal-adts.aac": (LIBXAAC_ORDER, [3, 4, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86], 4723),
    "faac-1.40-pink-side-12-db-adts.aac": (LIBXAAC_ORDER, [8, 9, 12, 13, 14, 15, 16, 17, 18, 19, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 78, 79, 80, 81, 82, 83, 84, 85, 86], 2317),
    "faac-1.50-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.50.m4a": (LIBXAAC_ORDER, [1], 340),
    "apple-passthru-m4a-from-faac-m4a.m4a": (LIBXAAC_ORDER, [1], 340),
    "apple-intel-passthru-m4a-from-faac-m4a.m4a": (LIBXAAC_ORDER, [1], 340),
    "faac-1.31.1-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.31.1.m4a": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40.m4a": (LIBXAAC_ORDER, [1], 340),
    "faac-1.40-pink-equal-adts.aac": (LIBXAAC_ORDER, [3, 4, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86], 4723),
    "faac-1.40-pink-side-12-db-adts.aac": (LIBXAAC_ORDER, [8, 9, 12, 13, 14, 15, 16, 17, 18, 19, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 78, 79, 80, 81, 82, 83, 84, 85, 86], 2317),
    "faac-1.50-adts.aac": (LIBXAAC_ORDER, [1], 340),
    "faac-1.50.m4a": (LIBXAAC_ORDER, [1], 340),
    "apple-passthru-m4a-from-faac-m4a.m4a": (LIBXAAC_ORDER, [1], 340),
    "apple-intel-passthru-m4a-from-faac-m4a.m4a": (LIBXAAC_ORDER, [1], 340),
    "loud-noise-pair.aac": ("grouped short windows whose noise both channels share: libxaac reuses the first channel's seed variable for the second, which starts from the first channel's "
                            "last window of the group (ixheaacd_pns_js_thumb.c), so the second channel draws other noise there; the analyzer gives both the same noise in each window", [1, 2], 4862),
    "stereo-coding.aac": ("block 10, a pair without a common window: libxaac reads the mid/side flags only for a common window and never clears them (ixheaacd_read_ms_data), so its "
                          "intensity stereo inverts bands by the pair before's flags, where the standard transmits none and inverts nothing", [10], 8111),
    "apple-win-lc-m4a-q127-cbr128-tones.m4a": (LIBXAAC_TNS, [130], 57),
    "external-nero-rom-aac-22k-vbr-q050.mp4": (LIBXAAC_TNS, [146], 154),
    "apple-win-lc-m4a-q127-cbr64-clicks.m4a": (LIBXAAC_TNS + "; up to 30 % of full scale at the clicks", [72, 85, 86, 103, 117, 148, 207, 238], 9739),
    "loud-pulse-tns.aac": (LIBXAAC_TNS + "; block 3 holds filters, blocks 1 and 4 hold the overlap of filters (the pulses are applied alike; block 0, whose filters also differ, is left out with the first block)", [1, 3, 4], 165),
}

# Each entry says what differs: "positions", positions that only one of the two decodes (a block the analyzer refuses, or one FFmpeg refuses), and the samples the two share then
# differ too, since FFmpeg reads such a block its own way.
# Files whose output FFmpeg's decoder differs from the analyzer's, with the reason. The analyzer follows libxaac (docs/design/lossy-decoders.md, decision 1); the differences are
# in what the specification leaves to the decoder and the hand-built files exercise.
DIFFERENCES = {
    "stereo-tool-order.aac": ("samples", "intensity bands under ms_mask_present 2, which FFmpeg's decoder inverts where 4.6.8.2.3 inverts under mask 1 alone (block 1, up to 0.03 of full scale), and noise substitution, "
                                         "where each decoder draws its own noise (blocks 5 to 14); the second channel of the blocks with mid/side flags over the first channel's noise equals the one without them in both decoders"),
    "tns-overflow.aac": ("samples", "a TNS filter on the largest scalefactors: FFmpeg's float decoder overflows to infinity and NaN, the analyzer saturates the spectrum"),
    "stereo-coding-reserved.aac": ("samples", "ms_mask_present 3, which the specification reserves: FFmpeg refuses those blocks, the analyzer decodes them without mid/side stereo"),
    "corrupt-soft.aac": ("positions", "raw data blocks with more channel streams than the stream's configuration states: the analyzer does not decode them (decode.frame-invalid), FFmpeg decodes the channels it can"),
    "window-sequences-invalid.aac": ("samples", "five windows whose slopes do not match those of the window before: FFmpeg windows those transitions differently, in the blocks that hold them and the "
                                                "block after (a copy of the file 1,000 times as loud differs from FFmpeg's by up to 0.02 of full scale there and from libxaac's by 1 LSB at the most, so the analyzer follows libxaac; "
                                                "this file's own peak, -88 dBFS, is 1.3 LSB of 16 bits, too quiet for libxaac's output to show it)"),
    "sbr-in-one-block.aac": ("samples", "a raw data block with SBR data in a stream whose first block holds none, so that it stays AAC LC: FFmpeg reads the SBR data and its output differs in that block and the next "
                                        "by up to 0.06 of full scale, where the analyzer decodes the core (the first block decides, aac.sbr-data-sparse)"),
    "sbr-in-three-blocks.aac": ("samples", "raw data blocks with SBR data in a stream whose first block holds none, so that it stays AAC LC: FFmpeg reads the SBR data of the first of them (its output differs in that block and the next), where the analyzer decodes the core "
                                           "(the first block decides, aac.sbr-data-sparse)"),
    "sbr-in-all-but-first-block.aac": ("samples", "SBR data in every block but the first, which decides: the stream stays AAC LC at the core rate, and FFmpeg's output differs in every block that holds SBR data"),
    "sbr-in-frames-failing-crc.aac": ("samples", "SBR data in frames that all fail their CRC: the analyzer takes no evidence from them and stays AAC LC at the core rate; FFmpeg checks no CRC and decodes HE-AAC at twice "
                                                 "the rate, so it has twice the samples and the ones both have differ"),
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
    """The number of analyzer samples before the reference's first one (negative where the reference starts later than the coded stage, as libxaac's does): the candidate, or
    the search from -1,500 to 4,200, with the smallest difference over a stretch."""
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
    energy = sum(reference[middle + i] ** 2 for i in range(0, stretch, 3)) if middle + stretch <= len(reference) else 0.0
    if error(best) > 1e-4 * energy + 1e-9:
        best = min(range(-1500, 4200), key=error)
    return best


def read_wav(path):
    """The 16-bit samples of the data chunk of a WAVE file (xaacdec writes one, with a header of 44 bytes that are no samples), or None."""
    data = pathlib.Path(path).read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    at = 12
    while at + 8 <= len(data):
        size = int.from_bytes(data[at + 4:at + 8], "little")
        if data[at:at + 4] == b"data":
            body = data[at + 8:at + 8 + size]
            body = body[:len(body) // 2 * 2]
            values = array.array("h")
            values.frombytes(body)
            return values
        at += 8 + size + (size & 1)
    return None


def libxaac_difference(mine, values, channels, lag):
    """The blocks of 1,024 samples where the analyzer's floats, rounded to 16 bits, differ from libxaac's samples by more than LIBXAAC_MAX_LSB, and the largest difference over the file
    (the samples where either side is at full scale left out, as compare_pcm.py leaves them)."""
    blocks = set()
    largest = 0
    for position in range(max(lag, FIRST_BLOCK), len(mine) // channels):
        source = position - lag
        if source * channels + channels > len(values):
            break
        for c in range(channels):
            analyzer = mine[position * channels + c] * 32768.0
            reference = values[source * channels + c]
            if abs(analyzer) >= 32767.0 or abs(reference) >= 32767:
                continue
            difference = abs(math.floor(analyzer + 0.5) - reference)
            largest = max(largest, difference)
            if difference > LIBXAAC_MAX_LSB:
                blocks.add(position // 1024)
    return sorted(blocks), largest


def compare(manifest, reference_file, channels, start, extra=(), fmt="float32"):
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(reference_file), "--reference-format", fmt, "--reference-channels",
                             str(channels), "--reference-first-position", str(start), "--exclude", f"0:{FIRST_BLOCK}", "--json", *extra], capture_output=True, text=True)
    if output.returncode == 2:
        return None, output.stderr.strip()
    result = json.loads(output.stdout)
    if output.returncode == 0:
        return result, None
    worst = max(result["channels"], key=lambda c: c["max"])
    return result, f"outside the bound (largest difference {worst['max']:.3g}, RMS {worst['rms']:.3g}; {result['compared']} positions compared)"


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
    failed = compared = decoded = hand_over = 0
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
            if stream.get("streamCodec", "").startswith("HE-AAC"):
                # The analyzer decodes SBR (and parametric stereo) for this stream: its output is not the core's, and crosscheck_he_aac_pcm.py compares it.
                hand_over += 1
                print(f"ok   {path.name} (decoded as {stream['streamCodec']}: compared by crosscheck_he_aac_pcm.py)")
                continue
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
            peak = max(abs(v) for v in mine)
            # A hand-built file as quiet as -100 dBFS cannot fail the absolute bounds, so its bounds follow its level (FFmpeg's float tables agree within 3 x 10^-5 of the peak on loud-pulse-tns.aac, and within 5 x 10^-3 on the mixed channels of stereo-coding-elements.aac, whose peak is 22 steps of 16 bits).
            quiet = 0 < peak < 0.6
            bounds = ["--max-abs", str(min(2.0 ** -14, peak * 1e-2)), "--rms", str(min(2.0 ** -15 / math.sqrt(12.0), peak * 1e-3))] if quiet else []
            outcome, found = compare(manifest, raw, channels, start, [*extra, *bounds] if path.name not in NOISE else extra)
            compared += 1
            if path.name in NOISE:
                plain, plain_found = compare(manifest, raw, channels, start, bounds)
                if plain is not None and not plain_found:
                    problems.append("listed in NOISE, but the samples agree with FFmpeg's")
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
            owned = path.name in DIFFERENCES and DIFFERENCES[path.name][0] == "samples"
            if xaac is not None and (not problems or owned) and max(abs(v) for v in mine) >= 1.0:
                note = (note + "; " if note else "") + "libxaac not compared: the file has samples over full scale, which its 16-bit output does not saturate"
            elif xaac is not None and (not problems or owned) and channels > 2:
                note = (note + "; " if note else "") + "libxaac not compared: more than two channels"
            elif xaac is not None and (not problems or owned):
                wav = base / "xaac.wav"
                source = path
                if path.suffix != ".aac":
                    # xaacdec reads raw ADTS (its MP4 mode wants access units with a metadata file of its own): the access units are copied into ADTS, unchanged.
                    source = base / "xaac-input.aac"
                    subprocess.run([args.ffmpeg, "-v", "error", "-y", "-ignore_editlist", "1", "-i", str(path), "-c", "copy", "-f", "adts", str(source)], capture_output=True)
                wav.unlink(missing_ok=True)
                done = subprocess.run([str(xaac), f"-ifile:{source}", f"-ofile:{wav}", "-pcmsz:16", "-peak_limiter_off:1"], capture_output=True)
                values = read_wav(wav) if done.returncode == 0 and wav.exists() else None
                if values is None or len(values) == 0 or not source.exists():
                    note = (note + "; " if note else "") + "libxaac decodes nothing"
                else:
                    pcm = base / "xaac.pcm"
                    pcm.write_bytes(values.tobytes())
                    # Both on the scale of 16-bit steps; libxaac starts 220 samples after the coded stage on every file tried (its own delay).
                    lag = find_start([v * 32768.0 for v in mine], [float(v) for v in values], channels, (220, 0, 1024, 2112))
                    outcome2, found2 = compare(manifest, pcm, channels, lag, ["--skip-full-scale", "--max-lsb", str(LIBXAAC_MAX_LSB), "--rms-lsb", "1"], fmt="int16")
                    if path.name in LIBXAAC_DIFFERENCES:
                        reason, expected_blocks, expected_largest = LIBXAAC_DIFFERENCES[path.name]
                        blocks, largest = libxaac_difference(mine, values, channels, lag)
                        if found2 is None:
                            problems.append("listed in LIBXAAC_DIFFERENCES, but libxaac agrees")
                        elif blocks != expected_blocks or largest > expected_largest:
                            problems.append(f"libxaac differs otherwise than the entry says: blocks {blocks} (expected {expected_blocks}), largest {largest} LSB (at most {expected_largest})")
                        else:
                            note = (note + "; " if note else "") + f"libxaac differs in blocks {blocks} by {largest} LSB at the most: " + reason
                    elif found2 and "no position is in both" in found2:
                        # libxaac decodes only the first block of a damaged file, which the comparison leaves out (FIRST_BLOCK).
                        note = (note + "; " if note else "") + "libxaac not compared: it decodes only the first block, which is left out"
                    elif found2:
                        problems.append(f"libxaac: {found2}")
                    else:
                        note = (note + "; " if note else "") + f"libxaac agrees from sample {lag}"
            key = path.name
            seen.add(key)
            if key in DIFFERENCES:
                kind, reason = DIFFERENCES[key]
                one_sided = outcome is not None and (outcome["notComparedOnlyInReference"] or outcome["notComparedOnlyInAnalyzer"])
                if (kind == "positions" and one_sided) or (kind == "samples" and problems):
                    note = (note + "; " if note else "") + "differs: " + reason
                    problems = []
                elif not problems:
                    problems.append("listed in DIFFERENCES, but the decoders no longer differ on it")
                else:
                    problems.append(f"differs otherwise than the entry says ({kind})")
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
    print(f"{decoded} files decoded, {hand_over} of them as HE-AAC (compared by crosscheck_he_aac_pcm.py), {compared} compared with FFmpeg, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
