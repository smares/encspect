#!/usr/bin/env python3
"""Checks the analyzer's low bits unused per span (docs/design/lossywav-detection.md, "The measurement") against the prototype
lossywav_figures.py on the PCM that Encspect's own decoders deliver.

    crosscheck_lossywav_figures.py [--pcmdump CMD] [--encspect CMD] [--keep DIR] [FILE ...]

CMD is the command that runs a built tool, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll"; the defaults
are those paths relative to the repository. With no FILE the script takes the lossyWAV fixtures (FLAC, WavPack and Monkey's Audio) and the
measurement fixtures whose decoded blocks are contiguous. For each file:

- tests/Encspect.PcmDump writes the samples of each decoded stream (interleaved, little-endian, ceil(bits / 8) bytes each) and the runs of
  positions they cover;
- the prototype works out the figures from those samples, in Python and with no code in common with the analyzer;
- encspect --measure=levels --json gives the analyzer's figures from the file;
- every figure must be equal: the distribution, the silent and thin spans, the spans above Low Bits Unused, the changes, the grid and the
  changes on it, and the times with equal counts.

A stream whose blocks are not contiguous (a gap or withheld output) is listed and left out: the prototype has no gaps, and the unit tests
(MeasurementSpanTests) hold the analyzer to a model with them. A stream that is not integer is left out. The script fails if a figure
differs, if a tool fails, or if no stream was compared.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lossywav_figures as lf  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
DEFAULT_ENCSPECT = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")


def default_files():
    files = sorted(FIXTURES.glob("flac/external-lossy*.flac")) + sorted(FIXTURES.glob("wavpack/external-lossywav*.wv"))
    files += sorted(FIXTURES.glob("monkeys-audio/external-lossywav*.ape"))
    files += sorted(FIXTURES.glob("measurement/synthetic-levels-*.flac")) + sorted(FIXTURES.glob("measurement/synthetic-lowbits-*.flac"))
    files += [FIXTURES / "measurement" / n for n in ("lossy-lame-b64.flac", "synthetic-white-44100.flac")]
    return files


def read_samples(path, width, channels):
    data = pathlib.Path(path).read_bytes()
    count = len(data) // (width * channels)
    values = [int.from_bytes(data[i:i + width], "little", signed=True) for i in range(0, count * width * channels, width)]
    return [values[c::channels] for c in range(channels)]


def prototype(entry, pcm_path):
    channels = entry["channels"]
    bits = entry["bitsPerSample"]
    width = (bits + 7) // 8
    return lf.figures_of(lf.pcm_of(read_samples(pcm_path, width, channels), entry["sampleRate"], bits))


def analyzer(command, path):
    result = subprocess.run(shlex.split(command) + ["--measure=levels", "--json", "--hide", "frames,tags,statistics", str(path)], capture_output=True)
    if result.returncode > 1:
        raise RuntimeError(f"{path}: encspect exited with {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
    streams = json.loads(result.stdout)[0]["streams"]
    return [s.get("measurements", {}).get("levels", {}).get("lowBitsPerSpan") for s in streams]


def compare(expected, got):
    """The differences between the prototype's figures and the analyzer's, as strings."""
    problems = []

    def check(what, want, have):
        if want != have:
            problems.append(f"{what}: prototype {want}, analyzer {have}")

    check("spans", expected["spans"], got["spans"])
    check("silent spans", expected["silentSpans"], got["silentSpans"])
    check("thin spans", expected["thinSpans"], got["thinSpans"])
    check("short spans", expected["shortSpans"], got["shortSpans"])
    check("distribution", {int(k): v for k, v in expected["distribution"].items()}, {d["lowBitsUnused"]: d["spans"] for d in got["distribution"]})
    check("spans above", expected["spansAboveLowBitsUnused"], got["spansAboveLowBitsUnused"]["count"])
    check("changes", expected["changes"], got["changes"]["count"])
    check("grid", expected["grid"], got["grid"]["samples"])
    check("moments", expected["changeMoments"], got["changes"]["moments"])
    check("moments on grid", expected["momentsOnGrid"] or 0, got["grid"]["momentsOnGrid"])
    equal = got.get("channelsWithEqualLowBitsUnused")
    check("times", expected["times"], equal["of"] if equal else None)
    check("times with equal counts", expected["timesWithEqualCounts"], equal["count"] if equal else None)
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--encspect", default=DEFAULT_ENCSPECT)
    parser.add_argument("--keep", help="keep the PCM files in this folder")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args()
    files = [pathlib.Path(f) for f in args.files] or default_files()

    compared = failed = 0
    with tempfile.TemporaryDirectory() as scratch:
        dump = pathlib.Path(args.keep or scratch)
        dump.mkdir(parents=True, exist_ok=True)
        # One file at a time, so that a file's manifest is the one named after its number.
        for number, path in enumerate(files):
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {path.name}: PcmDump exited with {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            reports = analyzer(args.encspect, path)
            for entry in streams:
                label = f"{path.name} stream {entry['index']}"
                if entry["format"] != "integer":
                    print(f"skip {label}: not integer")
                    continue
                if len(entry["runs"]) != 1:
                    print(f"skip {label}: {len(entry['runs'])} runs of positions (the prototype has no gaps)")
                    continue
                if entry["runs"][0][0] % 32 != 0:
                    print(f"skip {label}: starts at position {entry['runs'][0][0]}")
                    continue
                got = reports[entry["index"]] if entry["index"] < len(reports) else None
                if got is None:
                    print(f"FAIL {label}: the analyzer gave no figures")
                    failed += 1
                    continue
                expected = prototype(entry, dump / entry["pcm"])
                problems = compare(expected, got)
                compared += 1
                if problems:
                    failed += 1
                    print(f"FAIL {label}: " + "; ".join(problems))
                else:
                    print(f"ok   {label}: {expected['spans']} spans, {expected['changes']} changes, grid {expected['grid']}")
            for old in dump.glob("0000-*"):
                old.unlink()

    print(f"{compared} streams compared, {failed} failed")
    return 0 if compared > 0 and failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
