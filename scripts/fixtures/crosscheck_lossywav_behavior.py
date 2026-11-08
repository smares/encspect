#!/usr/bin/env python3
"""Runs the negative set of the lossyWAV claim from behavior (docs/design/lossywav-detection.md, "From behavior" and "False positives") again, and
holds the analyzer's counts of wasted bits to flac's.

    crosscheck_lossywav_behavior.py [--encspect CMD] [--keep DIR]

Needs flac. Takes every FLAC fixture in tests/fixtures/flac and tests/fixtures/measurement that `flac -d` decodes and that is not lossyWAV's output
(the files named *lossyflac* and *lossywav*), codes it again with `flac -5 -b 512` (the frames of a lossyFLAC file) at its own sample rate, and again
at 44.1 kHz where the rate is not one of lossyWAV's tested rates (so that the rule is applied to every file), and runs the analyzer on both. Each file must show:

- no Processor node and no processor in the stream: the claim from behavior does not name lossyWAV;
- at most 3 changes of wasted bits (the Changes entry of Wasted Bits);
- the changes the analyzer counts equal those that `flac -a` lists for the same file (the wasted bits of each subframe, in frame order).

The same check runs on the lossyWAV fixtures of the FLAC folder, where the claim must follow the importer's recomputation (flacUnits.claim of the
provenance), and the counts must equal flac's.
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
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_ENCSPECT = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")
# Files that zero bits per block on another grid, as a tool other than lossyWAV might (make_measurement_fixtures.py). Coded in frames of 512, a count per block
# of 4,096 samples changes only on frame boundaries, which lie on lossyWAV's grid of 512, so the rule cannot tell them apart: a limit the design states,
# listed here and not counted as a problem.
KNOWN_LIMITS = ("synthetic-lowbits-other-",)
TESTED = {22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000}


def walk(node):
    yield node
    for child in node.get("children", []):
        yield from walk(child)


def analyze(encspect, path):
    result = subprocess.run([*shlex.split(encspect), "--json", "--hide", "frames,measurements", str(path)], capture_output=True, text=True)
    if result.returncode > 1:
        raise SystemExit(f"{path}: encspect failed: {result.stderr[-300:]}")
    report = json.loads(result.stdout)[0]
    nodes = list(walk(report["root"]))
    wasted = next((n for n in nodes if n["name"] == "Wasted Bits"), None)
    changes = next((c["value"] for c in wasted["children"] if c["name"] == "Changes"), 0) if wasted else 0
    streams = report.get("streams", [])
    return {"processor": [s.get("processor") for s in streams], "node": any(n["name"] == "Processor" for n in nodes), "changes": changes,
            "evidence": [n["value"] for n in nodes if n["name"] == "Evidence" and "changes of the wasted bits" in str(n.get("value"))]}


def flac_changes(path):
    """The changes of wasted bits between consecutive frames of each channel, from `flac -a`."""
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "a.ana"
        subprocess.run(["flac", "-a", "-s", "-f", "-o", str(out), str(path)], capture_output=True, check=True)
        last, changes, units = {}, 0, 0
        with out.open() as lines:
            for line in lines:
                if line.startswith("frame="):
                    channel = 0
                elif line.startswith("\tsubframe=") and "wasted_bits=" in line:
                    wasted = int(line.split("wasted_bits=")[1].split()[0])
                    units += 1
                    if channel in last and last[channel] != wasted:
                        changes += 1
                    last[channel] = wasted
                    channel += 1
        return changes, units


def recode(source, work, rate):
    wav = work / "in.wav"
    if subprocess.run(["flac", "-d", "-s", "-f", "-o", str(wav), str(source)], capture_output=True).returncode != 0:
        return None
    if rate is not None:
        # The WAV header's rate (extensible or not, it is at offset 24 of the fmt chunk body, which follows RIFF, WAVE and "fmt " + size).
        data = bytearray(wav.read_bytes())
        at = data.index(b"fmt ") + 8
        data[at + 4:at + 8] = rate.to_bytes(4, "little")
        wav.write_bytes(data)
    out = work / "out.flac"
    result = subprocess.run(["flac", "-s", "-f", "-5", "-b", "512", "--lax", "-o", str(out), str(wav)], capture_output=True)
    return out if result.returncode == 0 and out.exists() else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--encspect", default=DEFAULT_ENCSPECT)
    parser.add_argument("--keep")
    arguments = parser.parse_args()
    problems, limits, files, runs, most = [], [], 0, 0, 0
    sources = sorted(p for folder in ("flac", "measurement") for p in (FIXTURES / folder).glob("*.flac") if "lossyflac" not in p.name and "lossywav" not in p.name)
    for source in sources:
        with tempfile.TemporaryDirectory() as directory:
            work = pathlib.Path(directory)
            for variant in (None, 44100):
                if variant is not None and rate_of(source) in TESTED:
                    continue
                coded = recode(source, work, variant)
                if coded is None:
                    break
                files += variant is None
                runs += 1
                found = analyze(arguments.encspect, coded)
                expected, _ = flac_changes(coded)
                label = f"{source.name} at {variant or 'its own rate'}"
                if source.name.startswith(KNOWN_LIMITS):
                    limits.append(f"{label}: {'claimed' if found['evidence'] else 'not claimed'}, {found['changes']} changes of wasted bits")
                    if found["changes"] != expected:
                        problems.append(f"{label}: {found['changes']} changes, but flac lists {expected}")
                    continue
                if found["node"] or any(found["processor"]) or found["evidence"]:
                    problems.append(f"{label}: claimed as {found['processor']}")
                if found["changes"] > 3:
                    problems.append(f"{label}: {found['changes']} changes of wasted bits")
                if found["changes"] != expected:
                    problems.append(f"{label}: {found['changes']} changes, but flac lists {expected}")
                most = max(most, found["changes"])
                if arguments.keep:
                    pathlib.Path(arguments.keep).mkdir(parents=True, exist_ok=True)
                    (pathlib.Path(arguments.keep) / f"{source.stem}-{variant or 'own'}.flac").write_bytes(coded.read_bytes())
    for provenance in sorted((FIXTURES / "flac").glob("external-lossyflac-*.provenance.json")):
        record = json.loads(provenance.read_text())
        path = provenance.parent / record["file"]
        found = analyze(arguments.encspect, path)
        expected, _ = flac_changes(path)
        claimed = bool(found["evidence"])
        if claimed != record["flacUnits"]["claim"]:
            problems.append(f"{path.name}: the analyzer's claim from behavior is {claimed}, the importer's {record['flacUnits']['claim']}")
        if found["changes"] != expected:
            problems.append(f"{path.name}: {found['changes']} changes, but flac lists {expected}")
    print(f"{files} files, {runs} codings, at most {most} changes of wasted bits outside the known limits; {len(problems)} problems")
    for limit in limits:
        print("  known limit:", limit)
    for problem in problems:
        print(" ", problem)
    sys.exit(1 if problems else 0)


def rate_of(path):
    data = path.read_bytes()
    return int.from_bytes(data[18:21], "big") >> 4


if __name__ == "__main__":
    main()
