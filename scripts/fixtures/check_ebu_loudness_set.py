#!/usr/bin/env python3
"""Runs the analyzer on the EBU loudness test set (the minimum-requirements signals of EBU Tech 3341 and Tech 3342) and holds the loudness, the loudness
range, the maxima of the momentary and short-term loudness and the true peak to the tolerances of the two documents' tables.

The set is the EBU's download (https://tech.ebu.ch/loudness), version 5 of 30 March 2016: 70 WAV files at 48 kHz and a readme. The readme allows the material
to be used "only for technical testing purposes", so it is fetched at a pinned SHA-256 and never committed. The EBU's site serves the zip to a browser and
answers scripted requests with 403, so its only address here is the private repository's copy, which needs ENCSPECT_PRIVATE_TOKEN. The expected values are those of the documents' Table 1
(read from tech.ebu.ch on 2026-10-28); LoudnessEbuTests synthesizes every case that can be synthesized, so these files add the two authentic programmes
(Tech 3341 cases 7 and 8, Tech 3342 cases 5 and 6) and the EBU's own rendering of the rest.

    python3 scripts/fixtures/check_ebu_loudness_set.py [--analyzer PATH] [--directory EXTRACTED]

--analyzer is the CLI (default src/Encspect.Cli/bin/Release/net10.0/encspect); --directory uses a set that is already extracted instead of downloading it.

Without a pin (SHA256 empty) nothing is checked: the script prints a GitHub ::warning:: saying so and exits with 0, and `--is-pinned` prints that warning
and exits with 1 for the workflow, which then builds nothing and skips the check.
"""

import json
import pathlib
import re
import subprocess
import sys
import tempfile
import zipfile

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
URLS = [downloads.private("loudness/ebu-loudness-test-setv05.zip")]
SHA256 = "9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062"

# (file name pattern, figure, expected value, tolerance up, tolerance down). Figures: I, LRA, M, S (maxima) and TP. Patterns are matched against the lower-case
# names of the files in version 5 of the set, which end in .wav (the true-peak files and most of case 13 in .wav.wav).
CASES = [
    (r"seq-3341-1-16bit", "I", -23.0, 0.1, 0.1), (r"seq-3341-1-16bit", "M", -23.0, 0.1, 0.1), (r"seq-3341-1-16bit", "S", -23.0, 0.1, 0.1),
    (r"seq-3341-2-16bit", "I", -33.0, 0.1, 0.1), (r"seq-3341-2-16bit", "M", -33.0, 0.1, 0.1), (r"seq-3341-2-16bit", "S", -33.0, 0.1, 0.1),
    (r"seq-3341-3-16bit", "I", -23.0, 0.1, 0.1), (r"seq-3341-4-16bit", "I", -23.0, 0.1, 0.1), (r"seq-3341-5-16bit", "I", -23.0, 0.1, 0.1),
    (r"seq-3341-6-6channels-waveex-16bit", "I", -23.0, 0.1, 0.1),
    (r"seq-3341-7_seq-3342-5-24bit", "I", -23.0, 0.1, 0.1), (r"seq-3341-7_seq-3342-5-24bit", "LRA", 5.0, 1.0, 1.0),
    (r"seq-3341-2011-8_seq-3342-6-24bit", "I", -23.0, 0.1, 0.1), (r"seq-3341-2011-8_seq-3342-6-24bit", "LRA", 15.0, 1.0, 1.0),
    (r"seq-3341-9-24bit", "S", -23.0, 0.1, 0.1),
    (r"seq-3341-10-\d+-24bit", "S", -23.0, 0.1, 0.1),
    (r"seq-3341-11-24bit", "S", -19.0, 0.1, 0.1),
    (r"seq-3341-12-24bit", "M", -23.0, 0.1, 0.1),
    (r"seq-3341-13-\d+-24bit", "M", -23.0, 0.1, 0.1),
    (r"seq-3341-14-24bit", "M", -19.0, 0.1, 0.1),
    (r"seq-3341-15-24bit", "TP", -6.0, 0.2, 0.4), (r"seq-3341-16-24bit", "TP", -6.0, 0.2, 0.4), (r"seq-3341-17-24bit", "TP", -6.0, 0.2, 0.4),
    (r"seq-3341-18-24bit", "TP", -6.0, 0.2, 0.4), (r"seq-3341-19-24bit", "TP", 3.0, 0.2, 0.4),
    (r"seq-3341-2[0-3]-24bit", "TP", 0.0, 0.2, 0.4),
    (r"seq-3342-1-16bit", "LRA", 10.0, 1.0, 1.0), (r"seq-3342-2-16bit", "LRA", 5.0, 1.0, 1.0), (r"seq-3342-3-16bit", "LRA", 20.0, 1.0, 1.0),
    (r"seq-3342-4-16bit", "LRA", 15.0, 1.0, 1.0),
]
MINIMUM_FIGURES = 20
NOT_PINNED = (f"::warning title=EBU loudness test set not pinned::The EBU loudness test set is not pinned (SHA256 is empty in {pathlib.Path(__file__).name}); nothing was checked. "
              f"Add the zip to the private repository and set SHA256 to its digest.")


def measure(analyzer, path):
    result = subprocess.run([str(analyzer), "--json", "--measure=loudness,true-peak", "--hide", "frames,tags,statistics,side-info", str(path)], capture_output=True)
    document = json.loads(result.stdout)
    m = document[0]["streams"][0].get("measurements", {})
    loud = m.get("loudness", {})
    return {
        "I": loud.get("integratedLoudness", {}).get("value"), "LRA": loud.get("loudnessRange", {}).get("value"),
        "M": loud.get("maximumMomentaryLoudness", {}).get("value"), "S": loud.get("maximumShortTermLoudness", {}).get("value"),
        "TP": m.get("truePeak", {}).get("maximumTruePeakLevel", {}).get("value"),
    }


def main():
    arguments = sys.argv[1:]
    analyzer = ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect"
    directory = None
    while arguments:
        flag = arguments.pop(0)
        if flag == "--is-pinned":
            if not SHA256:
                print(NOT_PINNED)
            sys.exit(0 if SHA256 else 1)
        if flag == "--analyzer":
            analyzer = pathlib.Path(arguments.pop(0))
        elif flag == "--directory":
            directory = pathlib.Path(arguments.pop(0))
        else:
            sys.exit(f"unknown argument {flag}")
    with tempfile.TemporaryDirectory() as work:
        if directory is None:
            if not SHA256:
                print(NOT_PINNED)
                return
            archive = pathlib.Path(work) / "set.zip"
            downloads.fetch(URLS, SHA256, archive)
            directory = pathlib.Path(work) / "set"
            with zipfile.ZipFile(archive) as zipped:
                zipped.extractall(directory)
        files = sorted(p for p in directory.rglob("*") if p.suffix.lower() == ".wav" or p.name.lower().endswith(".wav.wav"))
        problems, compared = [], 0
        for path in files:
            name = path.name.lower()
            for pattern, figure, expected, up, down in CASES:
                if re.search(pattern, name) is None:
                    continue
                value = measure(analyzer, path)[figure]
                compared += 1
                if value is None or not expected - down - 1e-9 <= value <= expected + up + 1e-9:
                    problems.append(f"{path.name}: {figure} reads {value}, expected {expected} +{up}/-{down}")
        print(f"{len(files)} files in the set, {compared} figures compared")
        if compared < MINIMUM_FIGURES:
            problems.append(f"only {compared} figures were compared; the set's file names may differ from the patterns")
        for problem in problems:
            print("FAIL", problem)
        sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
