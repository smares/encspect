#!/usr/bin/env python3
"""Compares the PCM that Encspect's AC-3 decoder delivers with liba52's and FFmpeg's, by position
(docs/formats/ac3.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "AC-3 decoding").

    crosscheck_ac3_pcm.py [--pcmdump CMD] [--encspect CMD] [--ffmpeg PATH] [--keep DIR] [FILE ...]

CMD of --pcmdump runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll"; --encspect runs the built command
line ("dotnet src/Encspect.Cli/bin/Release/net10.0/encspect.dll"), which finds where the dither flags are. With no FILE the script takes every fixture of tests/fixtures/ac3 whose
manifest says liba52 decodes it ("crosscheck": {"liba52": true}) and that is one AC-3 stream.

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved, the coded channel order) with the runs of positions they cover.
- liba52 (the system's liba52-0.7.4, called as a library through ctypes, no source read) decodes the frames the manifest lists, one by one, with its dynamic range control off and
  no downmix (the flags the frame itself gives); its LFE comes first and is moved last. liba52 and the analyzer both dither the mantissas of zero bits, with generators of
  their own, so the file is compared twice:
    strict:   a copy of the file with every dither flag cleared (and the CRCs written again) is compared sample by sample within the design's bounds (per channel the RMS of the
              difference at most 2^-15 / sqrt(12) of full scale, its largest magnitude at most 2^-14);
    dithered: the file as it is, by the energy of bands, within 0.5 dB of the mean over all frames (compare_pcm.py --bands), where the dither is noise of equal energy.
- FFmpeg's float decoder (its ac3 decoder, dynamic range control off with -drc_scale 0) is the second reference, in the same two ways, its channels moved into the coded order.
  The one known difference: a block in which a channel is block switched and the channel it is rematrixed with is not (external-aften-stereo-192k-blocksw.ac3), where FFmpeg
  6.1.1 decodes the long channel differently from liba52 and the analyzer (FFMPEG_DIFFERS pins it).
- Positions only one side has are listed and compared by compare_pcm.py's rules; a file outside its bound fails the run.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import array
import ctypes
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import ac3_model as m  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "ac3"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
DEFAULT_ENCSPECT = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")

# The edges, in hertz, of the bands of the energy comparison: octave-wide steps above 400 Hz, which the exponent bands of A/52 7.2.2.3 are about as wide as.
BANDS = "400,800,1600,3200,6400,12800,20000"

# Files FFmpeg decodes differently from liba52 and the analyzer, by file: what is known. Not an explanation of the cause (FFmpeg's source is not read): the blocks are the ones the
# analyzer's tree names, block 47 of the file below (its left channel is block switched, its right is not, and the rematrixing flags of block 42 are in force), where FFmpeg's
# right channel differs by up to 0.025 of full scale and its left channel agrees with the others to 2e-6.
FFMPEG_DIFFERS = {
    "external-aften-stereo-192k-blocksw.ac3": "block 47: a block switched left channel and a long right channel with rematrixing flags in force",
}

LIBA52_FLAGS = {0: 2, 1: 1, 2: 2, 3: 3, 4: 3, 5: 4, 6: 4, 7: 5}
A52_LFE = 0x10


def load_liba52():
    lib = ctypes.CDLL("liba52-0.7.4.so")
    lib.a52_init.restype = ctypes.c_void_p
    lib.a52_init.argtypes = [ctypes.c_uint32]
    lib.a52_syncinfo.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int)]
    lib.a52_frame.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_float), ctypes.c_float]
    lib.a52_block.argtypes = [ctypes.c_void_p]
    lib.a52_samples.argtypes = [ctypes.c_void_p]
    lib.a52_samples.restype = ctypes.POINTER(ctypes.c_float)
    lib.a52_dynrng.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    return lib


def liba52_pcm(lib, data, frames):
    """The interleaved little-endian floats liba52 decodes the listed frames to, in the coded channel order, and the channel count."""
    state = lib.a52_init(0)
    callback = ctypes.CFUNCTYPE(ctypes.c_float, ctypes.c_float, ctypes.c_void_p)(lambda level, user: 1.0)
    channels, planes = 0, []
    for offset, length in frames:
        frame = bytes(data[offset:offset + length])
        flags, rate, bitrate = ctypes.c_int(), ctypes.c_int(), ctypes.c_int()
        if lib.a52_syncinfo(frame, flags, rate, bitrate) != length:
            raise RuntimeError(f"liba52 does not take the frame at {offset}")
        mode = flags.value & 15
        lfe = 1 if flags.value & A52_LFE else 0
        given, level = ctypes.c_int(flags.value & ~0x10 | (flags.value & A52_LFE)), ctypes.c_float(1.0)
        if lib.a52_frame(state, frame, given, level, ctypes.c_float(0.0)) != 0:
            raise RuntimeError(f"liba52 refuses the frame at {offset}")
        lib.a52_dynrng(state, ctypes.cast(callback, ctypes.c_void_p), None)
        channels = LIBA52_FLAGS[mode] + lfe
        for _ in range(6):
            lib.a52_block(state)
            block = lib.a52_samples(state)
            values = (ctypes.c_float * (channels * 256)).from_address(ctypes.addressof(block.contents))
            planes.append([list(values[c * 256:(c + 1) * 256]) for c in range(channels)])
    order = list(range(channels))
    if channels and lfe:
        order = order[1:] + order[:1]
    out = array.array("f")
    for block in planes:
        for n in range(256):
            for c in order:
                out.append(block[c][n])
    return out.tobytes(), channels


def ffmpeg_pcm(ffmpeg, path, channels):
    """FFmpeg's float output in the coded order: its 5.1 and 5.0 layouts put the center and the LFE where the file order does not have them."""
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-drc_scale", "0", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None
    # FFmpeg's channel order for each count of the coded channels (acmod and lfeon); the coded order is L C R Ls Rs LFE.
    order = {1: [0], 2: [0, 1], 3: [0, 2, 1], 4: [0, 1, 2, 3], 5: [0, 2, 1, 3, 4], 6: [0, 2, 1, 4, 5, 3]}.get(channels)
    if order is None:
        return None
    values = array.array("f")
    values.frombytes(result.stdout)
    out = array.array("f")
    for n in range(len(values) // channels):
        for c in order:
            out.append(values[n * channels + c])
    return out.tobytes()


def manifest_frames(manifest):
    return [tuple(f) for f in manifest["frames"]]


def clear_dither(encspect, source, frames, target):
    """A copy of the file with every dither flag cleared and both CRCs of every listed frame written again."""
    report = json.loads(subprocess.run([*shlex.split(encspect), "--json", str(source)], capture_output=True, text=True, check=True).stdout)[0]
    data = bytearray(source.read_bytes())

    def walk(node):
        if node["name"].startswith("IsDithered"):
            position = node["offset"] * 8 + node.get("bitOffset", 0)
            data[position >> 3] &= ~(0x80 >> (position & 7)) & 0xFF
        for child in node.get("children", []):
            walk(child)

    walk(report["root"] if "root" in report else report)
    for offset, length in frames:
        data[offset:offset + length] = m.fix_crc2(m.fix_crc1(bytes(data[offset:offset + length])))
    target.write_bytes(bytes(data))


def pcmdump(command, path, folder):
    subprocess.run([*shlex.split(command), str(folder), str(path)], check=True, capture_output=True)
    manifests = sorted(folder.glob("*.pcm.json"))
    return manifests[0]


def compare(manifest, reference, channels, label, extra=()):
    raw = manifest.parent / f"{label}.f32"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                             "--reference-channels", str(channels), "--json", *extra], capture_output=True, text=True)
    if output.returncode == 2:
        return [f"{label}: {output.stderr.strip()}"], None
    result = json.loads(output.stdout)
    problems = []
    if output.returncode != 0:
        if "--no-sample-bounds" in extra:
            outside = sum(c["bandsOutside"] for c in result["bands"]["channels"])
            compared = sum(c["bandsCompared"] for c in result["bands"]["channels"])
            problems.append(f"{label}: {outside} of {compared} bands outside {result['bands']['toleranceDb']} dB" if compared else f"{label}: no band could be compared")
        else:
            worst = max(result["channels"], key=lambda c: c["max"])
            problems.append(f"{label}: outside the bound (largest {worst['max']:.3g}, RMS {worst['rms']:.3g})")
    return problems, result


def default_files():
    files = []
    for path in sorted(FIXTURES.glob("*.ac3")):
        manifest = FIXTURES / (path.stem + ".expected.json")
        if manifest.exists() and json.loads(manifest.read_text(encoding="utf-8")).get("crosscheck", {}).get("liba52") is True:
            files.append(path)
    return files


# The samples a file is repeated to for the comparison of dithered bands: the energy of a band is compared as a mean over frames, and the estimate of noise needs many.
REPEATED_SAMPLES = 1_000_000

# Fixtures that are no reference for the PCM, with the reason.
EXCLUDED = {
    "derived-ffmpeg-stereo-192k-delta.ac3": "its grouped mantissas hold codes no quantizer has (checks 18 to 20 of A/52 7.10.2), which every decoder reads in a way of its own",
    "derived-ffmpeg-stereo-192k-delta-block0.ac3": "its grouped mantissas hold codes no quantizer has (checks 18 to 20 of A/52 7.10.2), which every decoder reads in a way of its own",
}


def repeated(data, frames, count):
    """The frames of the file, without what lies between them, `count` times over: its bytes and the frames' places."""
    out, places = bytearray(), []
    for _ in range(count):
        for offset, length in frames:
            places.append((len(out), length))
            out += data[offset:offset + length]
    return bytes(out), places


def check(args, lib, path, work):
    if path.name in EXCLUDED:
        return [], [f"  not compared: {EXCLUDED[path.name]}"]
    manifest = json.loads((FIXTURES / (path.stem + ".expected.json")).read_text(encoding="utf-8"))
    frames = manifest_frames(manifest)
    problems, lines = [], []
    stripped = work / (path.stem + ".nodither.ac3")
    clear_dither(args.encspect, path, frames, stripped)
    original = path.read_bytes()
    copies = max(2, REPEATED_SAMPLES // (6 * 256 * len(frames)))
    long_data, long_frames = repeated(original, frames, copies) if path.name.startswith("external-") else (b"", [])
    long_path = work / (path.stem + ".repeated.ac3")
    long_path.write_bytes(long_data)
    variants = [("strict", stripped, frames)]
    if path.name.startswith("external-"):
        # The hand-built files hold no mantissa bits (their SNR offsets are 0): they are silent, and a band of silence cannot be compared.
        variants.append(("dithered", long_path, long_frames))
    for variant, source, listed in variants:
        folder = work / f"{path.stem}.{variant}"
        folder.mkdir()
        dump = pcmdump(args.pcmdump, source, folder)
        streams = json.loads(dump.read_text(encoding="utf-8"))["streams"]
        if len(streams) != 1:
            return [], [f"  not compared: {len(streams)} decoded streams (one stream of one configuration is compared)"]
        data = source.read_bytes()
        reference, channels = liba52_pcm(lib, data, listed)
        extra = [] if variant == "strict" else ["--bands", BANDS, "--no-sample-bounds"]
        found, result = compare(dump, reference, channels, f"liba52-{variant}", extra)
        problems += found
        lines.append(f"  liba52 {variant}: {'ok' if not found else 'OUTSIDE THE BOUND'}{band_summary(result)}")
        ffmpeg = ffmpeg_pcm(args.ffmpeg, source, channels)
        if ffmpeg is None:
            lines.append(f"  ffmpeg {variant}: no output")
            continue
        found, result = compare(dump, ffmpeg, channels, f"ffmpeg-{variant}", extra)
        if path.name in FFMPEG_DIFFERS:
            lines.append(f"  ffmpeg {variant}: {'agrees' if not found else 'differs, as pinned: ' + FFMPEG_DIFFERS[path.name]}")
        else:
            problems += found
            lines.append(f"  ffmpeg {variant}: {'ok' if not found else 'OUTSIDE THE BOUND'}{band_summary(result)}")
    return problems, lines


def band_summary(result):
    if not result or "bands" not in result:
        return ""
    channels = result["bands"]["channels"]
    return f" ({sum(c['bandsCompared'] for c in channels)} bands compared, {sum(c['bandsOutside'] for c in channels)} outside, largest {max(c['largestDifferenceDb'] for c in channels):.2f} dB)"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--encspect", default=DEFAULT_ENCSPECT)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path)
    args = parser.parse_args(argv)
    try:
        lib = load_liba52()
    except OSError as error:
        print(f"liba52 is not installed: {error}", file=sys.stderr)
        return 2
    files = args.files or default_files()
    failures = []
    with tempfile.TemporaryDirectory() as temporary:
        root = args.keep or pathlib.Path(temporary)
        root.mkdir(parents=True, exist_ok=True)
        for path in files:
            work = root / path.stem
            work.mkdir(exist_ok=True)
            problems, lines = check(args, lib, path, work)
            print(f"{path.name}: {'ok' if not problems else 'FAILS'}")
            for line in lines:
                print(line)
            failures += [f"{path.name}: {p}" for p in problems]
    for failure in failures:
        print(failure, file=sys.stderr)
    print(f"{len(files)} files, {len(failures)} problems")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
