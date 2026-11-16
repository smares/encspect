#!/usr/bin/env python3
"""Prints how many frames of 20 ms libspeex 1.2.1's fixed-point decoder needs, started cold at a packet, to be within a threshold of the same decoder that went on
(docs/design/lossy-decoders.md, "As built: Speex": the span the analyzer's decoder withholds after a gap).

    speex_convergence.py [--threshold LSB] [--work DIR]

For the speech fixtures of each mode (tests/fixtures/ogg) and every packet, speex_convergence.c (built by speex_tool.py) decodes the stream from that packet with a fresh
decoder and reports the frame after which no sample differs from the continuous decode by more than the threshold (16 least significant bits by default). The median, 90th
and 99th percentile and the largest are printed for narrowband, wideband and ultra-wideband.
"""

import argparse
import pathlib
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import speex_tool  # noqa: E402

FIXTURES = HERE.parents[1] / "tests" / "fixtures" / "ogg"
MODES = {
    "narrowband": ["external-speexenc-nb", "external-speexenc-nb-q2", "external-speexenc-nb-cbr-bitrate", "external-speexenc-nb-vbr-dtx", "external-ffmpeg-libspeex-nb-vbr"],
    "wideband": ["external-speexenc-wb", "external-speexenc-wb-q4", "external-speexenc-wb-vbr", "external-speexenc-wb-stereo", "external-speexenc-wb-vad-dtx", "external-ffmpeg-libspeex-wb"],
    "ultra-wideband": ["external-speexenc-uwb", "external-speexenc-uwb-vbr-q10", "external-ffmpeg-libspeex-uwb-abr"],
}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--threshold", type=int, default=16)
    parser.add_argument("--work", type=pathlib.Path)
    args = parser.parse_args(argv)
    work = args.work or pathlib.Path(tempfile.mkdtemp(prefix="speex-convergence-"))
    binary = speex_tool.build_pcm(work, "speex_convergence")
    for mode, names in MODES.items():
        values = []
        for name in names:
            out = subprocess.run([str(binary), str(FIXTURES / (name + ".spx")), str(args.threshold)], capture_output=True, text=True, check=True).stdout
            values += [int(line.split()[1]) for line in out.splitlines() if line.startswith("K ")]
        values.sort()
        n = len(values)
        print(f"{mode}: {n} cuts, median {values[n // 2]}, 90th {values[int(n * .9)]}, 99th {values[int(n * .99)]}, largest {values[-1]} frames")
    return 0


if __name__ == "__main__":
    sys.exit(main())
