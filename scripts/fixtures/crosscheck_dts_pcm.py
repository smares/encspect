#!/usr/bin/env python3
"""Compares the PCM that Encspect's DTS core decoder delivers with FFmpeg's, by position (docs/formats/dts.md, "Decoding"; docs/design/lossy-decoders.md, "As built: DTS core").

    crosscheck_dts_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [--work DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, as for crosscheck_musepack_pcm.py. With no FILE the script takes every file of tests/fixtures/dts the decoder decodes: FFmpeg's encoder files
(external-ffmpeg-*.dts) and the hand-built subframes-reconstruct-* files, which use no prediction and no vector quantization (frames with prediction and vector quantization decode with the tables of Annex D.10, which dts_tables.py reads from FFmpeg 6.1.1 because the PDF omits them).

The three real DTS-HD Master Audio files of tests/conformance/dts-hd-samples.json, if downloaded to .cache/dts-hd (check_dts_hd_samples.py does it), are compared too, their first 3 MB, as cores (`-core_only 1`): they use vector quantization.

For each file the analyzer's samples (floats at full scale 1.0, in the core's channel order, the LFE channel last) are compared by position with FFmpeg's decoder's (`ffmpeg -f dts`, 32-bit
float output in WAVE order, which the script puts into the core's order by the names of the speakers) under compare_pcm.py's bounds for a float reference: per channel the RMS of the
difference at most 2^-15 / sqrt(12) of full scale and its largest magnitude at most 2^-14. FFmpeg is a black box here; no source of it is read.

What the bounds rest on and what they hide: the decoder follows the specification's reconstruction in single precision, FFmpeg's decoder its own, so they agree to the rounding of
single precision (about 3e-7 RMS on these files). The specification gives no PCM scale; the decoder's scale was set from this comparison (FrameDecoder.OutputScale and LfeOutputScale), so a
wrong common factor would not show here. FFmpeg's interpolation of the LFE channel for LFF 1 (128 times) differs from Table D.8's filter in some taps (measured with a single-sample
impulse: taps 64, 65, 127, 255 and 511 of 512 differ by up to 15%); the analyzer follows the table, and the LFE channel of the file that uses it passes the bounds because its amplitude is small.

A second reference, libdca 0.0.7 (Ubuntu's libdca-dev; dca_pcm.c, a black box used through its public API, source never read), is independent of FFmpeg and so of the vector tables, which come from FFmpeg:

- dca_pcm.c decodes a raw stream in its own channel configuration (no downmix, level 1, bias 0); its samples are on the analyzer's scale and in its channel order (the LFE channel last).
- The real cores and FFmpeg's encoder files are compared as they are. libdca rounds its quantizer step sizes differently from Table D.2.1 for ABITS 22 to 26 (it uses the nominal 0.00008, 0.00004, ...; the table has the integers 336, 168, 84, 42 and 21 over 2^22, 0.14% more), which a 16-bit source at high resolution shows
  as a gain of 0.99864 on the lower bands (Master Audio 2.0 16bit: replacing the steps of those five ABITS by the nominal ones in a test build gave a difference of 7e-8 RMS and 1.2e-6 at most). So the bounds are those of the FFmpeg comparison plus 1.5e-3 of the channel's
  peak (and of its RMS): that margin is the table's rounding and is no bound for anything else.
- Every one of the 4,096 ADPCM coefficient vectors (D.10.1) and every one of the 1,024 high-frequency vectors (D.10.2) is checked alone: one frame for each, with one coded subband (prediction) or one vector quantized subband, compared frame by frame, with a bound of 1e-5 of the
  frame's peak (the figures reached are 1e-6 or less).
- The hand-built subframes-* files are not compared with libdca, each for the reason the script prints: its default is FILTS 1, the perfect reconstruction bank, which libdca decodes differently from the specification and FFmpeg (a gain of about 0.03 on the same frames with FILTS 0
  agreeing to 1e-5); sum/difference coding (libdca's output differs by a factor of about 2) and ADJ (about 1% in gain) are applied differently too; joint intensity coding is not decoded (the manifests say so).

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
import array
import math
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "dts"
DEFAULT_PCMDUMP = f"dotnet {ROOT / 'tests' / 'Encspect.PcmDump' / 'bin' / 'Release' / 'net10.0' / 'Encspect.PcmDump.dll'}"

# FFmpeg writes the channels of a layout in WAVE order.
WAVE_ORDER = ["front left", "front right", "front center", "LFE", "back left", "back right", "back center"]


def wave_rank(name):
    return WAVE_ORDER.index(name) if name in WAVE_ORDER else len(WAVE_ORDER)


def reorder(reference, layout):
    """FFmpeg's interleaved floats put into the order of layout (the core's), by the speakers' names."""
    import array

    channels = len(layout)
    samples = array.array("f")
    samples.frombytes(reference)
    wave = sorted(range(channels), key=lambda c: (wave_rank(layout[c]), c))
    out = array.array("f", bytes(len(samples) * 4))
    for position, core in enumerate(wave):
        out[core::channels] = samples[position::channels]
    return out.tobytes()


def default_files():
    return sorted(p for p in FIXTURES.glob("*.dts") if p.name.startswith(("external-ffmpeg-", "subframes-reconstruct-", "subframes-")))


def real_files(work):
    """The first 3 MB of each real DTS-HD Master Audio file in the cache, where they are."""
    files = []
    for path in sorted((ROOT / ".cache" / "dts-hd").glob("*.dts")):
        cut = work / ("real-" + path.name.replace(" ", "-"))
        cut.write_bytes(path.read_bytes()[: 3 * 1024 * 1024])
        files.append(cut)
    return files


LIBDCA_GAIN = 1.5e-3
FLOAT_RMS = 2.0 ** -15 / math.sqrt(12.0)
FLOAT_MAX = 2.0 ** -14

HAND_BUILT_SKIP = ("subframes-", "14le-subframes")
SKIP_REASON = "FILTS 1 (libdca's perfect reconstruction bank differs from the specification's), sum/difference coding and ADJ (applied differently), and joint intensity coding (not decoded)"


def build_libdca(work):
    tool = work / "dca_pcm"
    result = subprocess.run(["cc", "-O2", "-o", str(tool), str(HERE / "dca_pcm.c"), "-ldca"], capture_output=True, text=True)
    if result.returncode != 0:
        print("libdca is not available, its comparison is not made:", result.stderr[:200])
        return None
    return tool


def read_floats(data):
    values = array.array("f")
    values.frombytes(data)
    return values


def compare_libdca(path, dump_command, tool, work):
    """The decoder's PCM against libdca's, per channel, in the same order (the analyzer's: the LFE channel last, libdca's too)."""
    dump = work / (path.stem + ".libdca")
    result = subprocess.run(shlex.split(dump_command) + [str(dump), str(path)], capture_output=True)
    info = json.loads((dump / f"0000-{path.name}.pcm.json").read_text())["streams"][0]
    channels = info["channels"]
    mine = read_floats((dump / info["pcm"]).read_bytes())
    out = work / (path.stem + ".libdca.f32")
    run = subprocess.run([str(tool), str(path), str(out)], capture_output=True, text=True)
    if run.returncode != 0 or not out.exists():
        return [f"libdca did not decode it: {run.stderr[:200]}"]
    theirs = read_floats(out.read_bytes())
    count = min(len(mine), len(theirs)) // channels
    problems = []
    if abs(len(mine) - len(theirs)) // channels > 2048:
        problems.append(f"the lengths differ: {len(mine) // channels} and {len(theirs) // channels} samples")
    for c in range(channels):
        a = mine[c::channels][:count]
        b = theirs[c::channels][:count]
        diff = [x - y for x, y in zip(a, b)]
        peak = max((abs(x) for x in a), default=0.0)
        rms_signal = math.sqrt(sum(x * x for x in a) / max(1, count))
        rms = math.sqrt(sum(d * d for d in diff) / max(1, count))
        largest = max((abs(d) for d in diff), default=0.0)
        ok = rms <= FLOAT_RMS + LIBDCA_GAIN * rms_signal and largest <= FLOAT_MAX + LIBDCA_GAIN * peak
        print(f"  channel {c}: RMS {rms:.2e} (bound {FLOAT_RMS + LIBDCA_GAIN * rms_signal:.2e}), largest {largest:.2e} (bound {FLOAT_MAX + LIBDCA_GAIN * peak:.2e}) {'ok' if ok else 'OUTSIDE THE BOUND'}")
        if not ok:
            problems.append(f"channel {c} is outside the bounds")
    return problems


def scan_frames(kind):
    """One frame for each ADPCM coefficient vector (a coded subband with prediction) or each high-frequency vector (a vector quantized subband), mono, FILTS 0 (the vector quantized frames have 4 subsubframes, 1,024 samples, so all 32 elements of a vector count)."""
    sys.path.insert(0, str(HERE))
    import dts_coded as coded
    import dts_model as model

    index = [0]

    def silent(bits, p, nch, subs, vqsub, joinx, bhuff, shuff, ssc, header="core"):
        import random

        rng = random.Random(index[0] + 7)
        bits.put(ssc[0] - 1, 2)
        bits.put(0, 3)
        if kind == "adpcm":
            bits.put(1, 1)
            bits.put(0, 1)
            bits.put(index[0], 12)
            for n in range(vqsub[0]):
                bits.put(4 if n == 0 else 0, 4)
            coded.put_huffman(bits, "A4", 0)
            bits.put(30, 6)
            for sub in range(ssc[0]):
                coded.put_samples(bits, rng, 4, 0, None)
                if sub == ssc[0] - 1:
                    bits.put(0xFFFF, 16)
        else:
            for _ in range(subs[0]):
                bits.put(0, 1)
            for _ in range(vqsub[0]):
                bits.put(0, 4)
            for _ in range(subs[0] - vqsub[0]):
                bits.put(30, 6)
            for _ in range(subs[0] - vqsub[0]):
                bits.put(index[0], 10)
            for sub in range(ssc[0]):
                if sub == ssc[0] - 1:
                    bits.put(0xFFFF, 16)

    original = model.silent_subframes
    model.silent_subframes = silent
    try:
        frames = []
        count = 4096 if kind == "adpcm" else 1024
        for i in range(count):
            index[0] = i
            if kind == "adpcm":
                frames.append(model.core_frame(size=256, rate=29, amode=0, nch=1, subs=[0], vqsub=[31], joinx=[0], thuff=[0], shuff=[5], bhuff=[5], sel=[[0] * 10], nblks=15, hflag=1, filts=0))
            else:
                frames.append(model.core_frame(size=256, rate=29, amode=0, nch=1, subs=[30], vqsub=[30], joinx=[0], thuff=[0], shuff=[5], bhuff=[5], sel=[[0] * 10], nblks=31, hflag=1, filts=0))
        return b"".join(frames), count
    finally:
        model.silent_subframes = original


def scan_libdca(kind, dump_command, tool, work):
    data, count = scan_frames(kind)
    path = work / f"scan-{kind}.dts"
    path.write_bytes(data)
    dump = work / f"scan-{kind}.dump"
    subprocess.run(shlex.split(dump_command) + [str(dump), str(path)], capture_output=True)
    mine = read_floats((dump / f"0000-{path.name}.s0.pcm").read_bytes())
    out = work / f"scan-{kind}.f32"
    subprocess.run([str(tool), str(path), str(out)], capture_output=True)
    theirs = read_floats(out.read_bytes())
    frame = 512 if kind == "adpcm" else 1024
    worst = 0.0
    bad = []
    for i in range(count):
        a = mine[i * frame:(i + 1) * frame]
        b = theirs[i * frame:(i + 1) * frame]
        if len(a) < frame or len(b) < frame:
            bad.append(i)
            continue
        peak = max(abs(x) for x in a)
        largest = max(abs(x - y) for x, y in zip(a, b))
        relative = largest / peak if peak > 0 else (0.0 if largest == 0 else 1.0)
        worst = max(worst, relative)
        if relative > 1e-5:
            bad.append(i)
    print(f"scan of the {'ADPCM coefficient' if kind == 'adpcm' else 'high-frequency'} vectors against libdca: {count} frames, largest difference {worst:.1e} of the frame's peak, {len(bad)} outside 1e-5 {'ok' if not bad else bad[:10]}")
    return bad


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path)
    parser.add_argument("--work", type=pathlib.Path)
    args = parser.parse_args()
    status = 0
    with tempfile.TemporaryDirectory() as temp:
        work = args.work or pathlib.Path(temp)
        work.mkdir(parents=True, exist_ok=True)
        files = args.files or default_files() + real_files(work)
        tool = build_libdca(work)
        for path in files:
            dump = work / path.stem
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"{path.name}: PcmDump failed: {result.stderr.decode(errors='replace')[:300]}")
                status = 2
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            info = json.loads(manifest.read_text())["streams"][0]
            reference = subprocess.run([args.ffmpeg, "-v", "error", "-nostdin", "-f", "dts", "-core_only", "1", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
            if reference.returncode != 0 or not reference.stdout:
                print(f"{path.name}: FFmpeg did not decode it: {reference.stderr.decode(errors='replace')[:300]}")
                status = 2
                continue
            raw = work / f"{path.stem}.ref"
            raw.write_bytes(reorder(reference.stdout, info["channelLayout"]))
            output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                                     "--reference-channels", str(info["channels"])], capture_output=True, text=True)
            print(f"{path.name}: {'agrees' if output.returncode == 0 else 'DIFFERS'}")
            if output.returncode != 0:
                print(output.stdout)
                status = 1
            if tool is not None:
                if path.name.startswith(HAND_BUILT_SKIP):
                    print(f"  libdca skipped: {SKIP_REASON}")
                else:
                    problems = compare_libdca(path, args.pcmdump, tool, work)
                    if problems:
                        print(f"{path.name}: libdca DIFFERS: {problems}")
                        status = 1
            if args.keep:
                args.keep.mkdir(parents=True, exist_ok=True)
                (args.keep / f"{path.stem}.ref.f32").write_bytes(raw.read_bytes())
        if tool is not None and not args.files:
            for kind in ("adpcm", "vq"):
                if scan_libdca(kind, args.pcmdump, tool, work):
                    status = 1
    return status


if __name__ == "__main__":
    sys.exit(main())
