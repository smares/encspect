#!/usr/bin/env python3
"""Compares the PCM that Encspect's Speex decoder delivers with libspeex 1.2.1's fixed-point decoder, by position
(docs/formats/speex.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "Speex decoding").

    crosscheck_speex_pcm.py [--pcmdump CMD] [--keep DIR] [--work DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every Speex file of tests/fixtures/ogg.

For each file:

- PcmDump writes the samples of each decoded stream (16-bit integers, interleaved) with the runs of positions they cover, untrimmed and at the coded stage.
- speex_pcm (speex_tool.py builds it from libspeex 1.2.1 with FIXED_POINT: speexdec's decoding with its perceptual enhancer on and its in-band stereo handler, without its
  trimming) writes what the decoder returns for each frame it decodes, and a line for each audio packet with the frames it decoded and what stopped it. The packets give
  the positions: a packet counts the samples of the frames it holds, as the analyzer's parser does, or all the frames the header gives where it is empty or corrupt.
- Every position both have must hold the same sample: the decoder follows libspeex operation by operation, so the figures are exact on every file libspeex decodes.
- Positions only the analyzer has (the frames it withholds after a gap) and positions only libspeex has (the frames before a corruption, which the analyzer loses with the
  whole packet) are listed and must be pinned in EXPECTED.

Exit status 0 where every file agrees, 1 where one differs, 2 for a usage error.
"""

import argparse
import array
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import speex_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "ogg"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# The positions [first, end) that one side has and the other does not, by file and stream index: {"only-analyzer": [...], "only-reference": [...]}. Each is a decision of the
# analyzer's, not of the decoder:
# - After a gap the analyzer starts its decoder over and withholds the next 20 frames (docs/design/lossy-decoders.md, "As built: Speex"); libspeex goes on from the state it has.
# - A packet the analyzer's parser calls corrupt is lost whole, including the frames before the fault, which libspeex decodes.
# - libogg, which speexdec reads with, drops a page whose checksum fails or that follows garbage, and a page that ends the file short, which the analyzer's Ogg parser keeps.
# - speex_pcm starts one decoder for a serial number, so a chain whose second link repeats it is decoded as the first link's audio.
EXPECTED = {
    "bad-crc.spx#0": {"only-analyzer": [[1600, 4800]]},
    "garbage-between-pages.spx#0": {"only-analyzer": [[1600, 4800]]},
    "chained-same-serial.ogg#0": {"only-reference": [[3360, 6720]]},
    "continuation-errors.spx#0": {"only-reference": [[800, 3200]]},
    "sequence-gap.spx#0": {"only-reference": [[1600, 4800]]},
    "frames-corrupt.spx#0": {"only-reference": [[640, 1120], [1280, 1760], [2080, 3040]]},
    "speex-wideband-layer-skip.spx#0": {"only-reference": [[800, 960]]},
    "truncated-page-empty-packet.spx#0": {"only-analyzer": [[0, 640]]},
    "truncated-page.spx#0": {"only-analyzer": [[6400, 6720]]},
}


def reference_runs(prefix, index, header):
    """The runs [position, samples, offset in the samples libspeex wrote] and the samples per frame and channels of stream index of speex_pcm's output."""
    channels, frame_size, per_packet = header["channels"], header["frame-size"], header["frames-per-packet"]
    runs, position, written = [], 0, 0
    for line in pathlib.Path(f"{prefix}.s{index}.packets").read_text().splitlines():
        decoded, code, length = (int(x) for x in line.split())
        if decoded:
            runs.append([position, decoded * frame_size, written])
            written += decoded * frame_size
        # An empty or corrupt packet counts all its frames, as the parser does; any other packet the frames it holds.
        position += per_packet * frame_size if length == 0 or code in (-2, -3) else decoded * frame_size
    return runs


def merge(runs):
    merged = []
    for position, count, offset in runs:
        if merged and merged[-1][0] + merged[-1][1] == position and merged[-1][2] + merged[-1][1] == offset:
            merged[-1][1] += count
        else:
            merged.append([position, count, offset])
    return merged


def compare(analyzer, reference, a, b, channels):
    """(differences, largest difference, positions only one side has) of the two sets of runs over the samples a (analyzer) and b (reference)."""
    differences, largest = 0, 0
    only_analyzer, only_reference = [], []
    covered = []
    for ap, ac, ao in analyzer:
        position = ap
        end = ap + ac
        for rp, rc, ro in reference:
            lo, hi = max(ap, rp), min(end, rp + rc)
            if lo >= hi:
                continue
            if lo > position:
                only_analyzer.append([position, lo])
            position = hi
            x = a[(ao + lo - ap) * channels:(ao + hi - ap) * channels]
            y = b[(ro + lo - rp) * channels:(ro + hi - rp) * channels]
            if x != y:
                for i in range(len(x)):
                    if x[i] != y[i]:
                        differences += 1
                        largest = max(largest, abs(x[i] - y[i]))
            covered.append([lo, hi])
        if position < end:
            only_analyzer.append([position, end])
    for rp, rc, ro in reference:
        position = rp
        for lo, hi in sorted(c for c in covered if c[0] < rp + rc and c[1] > rp):
            if lo > position:
                only_reference.append([position, lo])
            position = max(position, hi)
        if position < rp + rc:
            only_reference.append([position, rp + rc])
    return differences, largest, only_analyzer, only_reference


def parse_header(line):
    words = line.split()
    return {words[i]: int(words[i + 1]) for i in range(0, len(words), 2) if words[i] != "stream" or True}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    parser.add_argument("--work", type=pathlib.Path, help="build libspeex in this folder")
    args = parser.parse_args(argv)
    files = args.files or sorted(p for p in FIXTURES.iterdir() if p.suffix in (".spx", ".ogg"))
    work = args.work or pathlib.Path(tempfile.mkdtemp(prefix="speex-"))
    work.mkdir(parents=True, exist_ok=True)
    binary = speex_tool.build_pcm(work / "libspeex")
    failed = compared = 0
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        for number, path in enumerate(files):
            name = path.name
            prefix = base / f"ref{number}"
            result = subprocess.run([str(binary), str(path), str(prefix)], capture_output=True, text=True)
            headers = [parse_header(line) for line in result.stdout.splitlines() if line.startswith("stream ")]
            if not headers:
                continue
            dump = base / f"dump{number}"
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = [s for s in json.loads(manifest.read_text())["streams"] if s["codec"] == "speex"] if manifest.exists() else []
            problems, notes = [], []
            for index, header in enumerate(headers):
                label = f"{name} stream {index}"
                stream = streams[index] if index < len(streams) else None
                reference = merge(reference_runs(prefix, index, header))
                if stream is None:
                    if reference:
                        problems.append(f"{label}: libspeex decodes samples, the analyzer decodes none")
                    continue
                if stream["channels"] != header["channels"]:
                    problems.append(f"{label}: the analyzer decoded {stream['channels']} channels, libspeex {header['channels']}")
                    continue
                channels = header["channels"]
                a = array.array("h")
                a.frombytes((dump / stream["pcm"]).read_bytes())
                b = array.array("h")
                b.frombytes(pathlib.Path(f"{prefix}.s{index}.pcm").read_bytes())
                runs, position = [], 0
                for first, count in stream["runs"]:
                    runs.append([first, count, position])
                    position += count
                differences, largest, only_analyzer, only_reference = compare(merge(runs), reference, a, b, channels)
                compared += 1
                if differences:
                    problems.append(f"{label}: {differences} samples differ, by up to {largest}")
                pinned = EXPECTED.get(f"{name}#{index}", {})
                if only_analyzer != pinned.get("only-analyzer", []):
                    problems.append(f"{label}: positions only the analyzer has: {only_analyzer}")
                if only_reference != pinned.get("only-reference", []):
                    problems.append(f"{label}: positions only libspeex has: {only_reference}")
            if problems:
                failed += 1
                print("FAIL " + "; ".join(problems))
            else:
                print(f"ok   {name}" + (f" ({'; '.join(notes)})" if notes else ""))
    print(f"{compared} streams compared with libspeex, {failed} files failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
