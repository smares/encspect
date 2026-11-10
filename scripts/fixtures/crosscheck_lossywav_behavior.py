#!/usr/bin/env python3
"""Runs the negative set of the lossyWAV claim from behavior (docs/design/lossywav-detection.md, "From behavior" and "False positives") again, and
holds the analyzer's counts of wasted bits to flac's.

    crosscheck_lossywav_behavior.py [--encspect CMD] [--keep DIR]

Needs flac. Takes every FLAC fixture in tests/fixtures/flac and tests/fixtures/measurement that `flac -d` decodes and that is not lossyWAV's output
(the files named *lossyflac* and *lossywav*), codes it again with `flac -5` in frames of its rate's lossyWAV block (the coding that can show the pattern) and in frames of 512, at its own sample rate, and again at 44.1 kHz where the rate is not one of lossyWAV's tested rates (so that the rule is applied to every file), and runs the analyzer on each. Each file must show:

- no Processor node and no processor in the stream: the claim from behavior does not name lossyWAV;
- at most 3 changes of wasted bits (the Changes entry of Wasted Bits), except in the simulated tools of synthetic-lowbits-other-*, which have theirs by construction;
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
BLOCKS = {22050: 256, 32000: 256, 44100: 512, 48000: 512, 88200: 1024, 96000: 1024, 176400: 2048, 192000: 2048}
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
    statistics = next((n for n in nodes if n["name"] == "Statistics"), {"children": []})
    wasted = next((n for n in statistics["children"] if n["name"] == "Wasted Bits"), None)
    changes = next((c["value"] for c in wasted["children"] if c["name"] == "Changes"), 0) if wasted else 0
    streams = report.get("streams", [])
    return {"processor": [s.get("processor") for s in streams], "node": any(n["name"] == "Processor" for n in nodes), "changes": changes,
            "evidence": [n["value"] for n in nodes if n["name"] == "Evidence" and "changes of the wasted bits" in str(n.get("value"))]}


def flac_changes(path):
    """The changes of wasted bits between consecutive frames of each channel, from `flac -a`. Only a source channel coded as itself is compared (left in
    left/right and left/side, right in left/right and side/right): a mid or side subframe states the coding's own count."""
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "a.ana"
        subprocess.run(["flac", "-a", "-s", "-f", "-o", str(out), str(path)], capture_output=True, check=True)
        last, changes, units = {}, 0, 0
        with out.open() as lines:
            for line in lines:
                if line.startswith("frame="):
                    channel = 0
                    assignment = line.split("channel_assignment=")[1].split()[0].upper() if "channel_assignment=" in line else "INDEPENDENT"
                elif line.startswith("\tsubframe=") and "wasted_bits=" in line:
                    # A constant subframe of 0 is digital silence: it has no count and leaves the channel's previous one.
                    if "type=CONSTANT" in line and "value=0" in line.split("type=CONSTANT")[1].split():
                        channel += 1
                        continue
                    wasted = int(line.split("wasted_bits=")[1].split()[0])
                    is_source = not (assignment.startswith("MID") or (assignment.startswith("LEFT_SIDE") and channel == 1) or (assignment.startswith("RIGHT_SIDE") and channel == 0))
                    if is_source:
                        units += 1
                        if channel in last and last[channel] != wasted:
                            changes += 1
                        last[channel] = wasted
                    channel += 1
        return changes, units


def recode(source, work, rate, block):
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
    result = subprocess.run(["flac", "-s", "-f", "-5", "-b", str(block), "--lax", "-o", str(out), str(wav)], capture_output=True)
    return out if result.returncode == 0 and out.exists() else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--encspect", default=DEFAULT_ENCSPECT)
    parser.add_argument("--keep")
    arguments = parser.parse_args()
    problems, files, runs, most = [], 0, 0, 0
    skipped = 0
    sources = sorted(p for folder in ("flac", "measurement") for p in (FIXTURES / folder).glob("*.flac") if "lossyflac" not in p.name and "lossywav" not in p.name)
    for source in sources:
        with tempfile.TemporaryDirectory() as directory:
            work = pathlib.Path(directory)
            own = rate_of(source)
            before = runs
            # Each file in frames of its rate's lossyWAV block (the coding that can show the pattern) and in frames of 512, at its own rate and, where that
            # is not one lossyWAV was tested at, labeled 44.1 kHz so that the rule is applied to every file.
            for variant in (None,) if own in TESTED else (None, 44100):
                effective = variant or own
                for block in sorted({BLOCKS.get(effective, 512), 512}):
                    coded = recode(source, work, variant, block)
                    if coded is None:
                        skipped += 1
                        print(f"  skipped: {source.name} at {variant or 'its own rate'} in frames of {block} (flac could not decode or code it again)")
                        continue
                    runs += 1
                    found = analyze(arguments.encspect, coded)
                    expected, _ = flac_changes(coded)
                    label = f"{source.name} at {variant or 'its own rate'} in frames of {block}"
                    if found["node"] or any(found["processor"]) or found["evidence"]:
                        problems.append(f"{label}: claimed as {found['processor']}")
                    # A simulated tool that zeroes bits per block has its changes by construction; it must still not be named.
                    if found["changes"] > 3 and not source.name.startswith("synthetic-lowbits-other-"):
                        problems.append(f"{label}: {found['changes']} changes of wasted bits")
                    if found["changes"] != expected:
                        problems.append(f"{label}: {found['changes']} changes, but flac lists {expected}")
                    most = max(most, found["changes"])
                    if arguments.keep:
                        pathlib.Path(arguments.keep).mkdir(parents=True, exist_ok=True)
                        (pathlib.Path(arguments.keep) / f"{source.stem}-{variant or 'own'}-{block}.flac").write_bytes(coded.read_bytes())
            files += runs > before
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
    print(f"{files} files, {runs} codings ({skipped} skipped), at most {most} changes of wasted bits ; {len(problems)} problems")
    for problem in problems:
        print(" ", problem)
    sys.exit(1 if problems else 0)


def rate_of(path):
    data = path.read_bytes()
    at = data.find(b"fLaC")
    if at < 0:
        return 0
    return int.from_bytes(data[at + 18:at + 21], "big") >> 4


if __name__ == "__main__":
    main()
