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

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
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
            if args.keep:
                args.keep.mkdir(parents=True, exist_ok=True)
                (args.keep / f"{path.stem}.ref.f32").write_bytes(raw.read_bytes())
    return status


if __name__ == "__main__":
    sys.exit(main())
