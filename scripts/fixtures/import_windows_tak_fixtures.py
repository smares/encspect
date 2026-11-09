#!/usr/bin/env python3
"""Takes the files of the tak-fixtures workflow's artifact into tests/fixtures/tak.

    python3 scripts/fixtures/import_windows_tak_fixtures.py path/to/extracted/artifact --run=RUN_ID --commit=SHA [--force]
    python3 scripts/fixtures/import_windows_tak_fixtures.py --summary path/to/extracted/artifact

The artifact (`tak-fixtures` of the run --run names, made at the commit --commit names) holds tak/*.tak, pcm/*.wav, encodes.json and
environment.json. Takc.exe of TAK 2.3.3 wrote the files on a windows-2025 runner (make_windows_tak_fixtures.ps1; the archive is
pinned in tak_tool.py and never committed); every case was encoded twice and `reproducible` says whether the two files are equal byte for
byte. This script never runs an encoder.

For each case that wrote a file and whose decoded audio equals the input, it copies the file to `external-<case>.tak` and writes
`<name>.tak.provenance.json` and `<name>.expected.json` (tak_model.py's reading of the committed bytes): the request (arguments, how the input reached Takc), the input's signal and SHA-256, the output's size and
SHA-256, whether it is reproducible, Takc's `-fi` output, the decoding check, what the SDK library reports (the stream info with the profile that `-fi`
does not print, the encoder info, the wave data sizes and the MD5), the runner and the tools. Cases that wrote the same bytes are committed
once (a preset before an option, then the shortest name) and the others are listed as `identicalCases`. The settings Takc was asked for
are compared with what the SDK reads from the file (preset, evaluation, sample format); a difference stops the import, because it is something
to decide before the file is committed. Files over 600 KB are refused unless --allow-large is given.
"""

import hashlib
import json
import pathlib
import re
import shutil
import sys

import tak_model
import tak_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "tak"
RUN = {"run": 0, "commit": "", "artifact": "tak-fixtures"}
SOURCE = f"TAK {tak_tool.VERSION} (Takc.exe), {tak_tool.AUTHOR_PAGE}"
LARGE = 600 * 1024


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def canonical_key(case):
    """Which of the cases that wrote the same bytes is committed: a preset before an option, then the shortest name."""
    name = case["name"]
    return (0 if name.startswith("preset-") else 1, len(name), name)


def preset_name(level, evaluation):
    return f"-p{level}" + {0: "", 1: "e", 2: "m"}.get(evaluation, f"?{evaluation}")


def disagreements(case, signals):
    """What the SDK reads from the file where it differs from what the case asked for."""
    sdk = case.get("sdk") or {}
    problems = []
    if "error" in sdk or "codec" not in sdk:
        return [f"the SDK library did not read the file: {sdk.get('error', 'no stream info')}"]
    signal = signals.get(case["input"]) or signals.get(case["name"])
    if signal:
        for key, expected in (("sampleRate", signal["rate"]), ("channels", signal["channels"]), ("bitsPerSample", signal["bits"])):
            if sdk.get(key) != expected:
                problems.append(f"{key} {sdk.get(key)} for {expected} asked")
        if sdk.get("sampleCount") != signal["frames"] and not case.get("lossywav"):
            problems.append(f"sampleCount {sdk.get('sampleCount')} for {signal['frames']} asked")
    match = [a for a in case["arguments"] if re.fullmatch(r"-p[0-4][em]?", a)]
    if match and "preset" in sdk:
        asked = match[0]
        read = preset_name(sdk["preset"], sdk.get("evaluation"))
        if asked != read:
            problems.append(f"preset {read} for {asked} asked")
    return problems


def summary(artifact):
    records = json.loads((artifact / "encodes.json").read_text(encoding="utf-8"))["cases"]
    for case in records:
        sdk = case.get("sdk") or {}
        fields = " ".join(f"{k}={sdk.get(k)}" for k in ("codec", "profile", "preset", "evaluation", "encoderVersion", "frameSizeType",
                                                         "frameSizeInSamples", "sampleCount", "sampleRate", "bitsPerSample", "channels",
                                                         "waveHeaderSize", "waveFooterSize") if k in sdk)
        print(f"{case['status']:6} {case['name']:22} {case.get('size', '-'):>7} reproducible={case.get('reproducible')} {fields}"
              + (f" ERROR {case.get('error')}" if case.get("error") else ""))


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    force = "--force" in sys.argv
    allow_large = "--allow-large" in sys.argv
    for option in (a for a in sys.argv[1:] if a.startswith("--run=") or a.startswith("--commit=")):
        key, value = option[2:].split("=", 1)
        RUN[key] = int(value) if key == "run" else value
    if len(arguments) != 1:
        raise SystemExit(__doc__)
    artifact = pathlib.Path(arguments[0])
    if "--summary" in sys.argv:
        summary(artifact)
        return
    encodes = json.loads((artifact / "encodes.json").read_text(encoding="utf-8"))
    environment = json.loads((artifact / "environment.json").read_text(encoding="utf-8"))
    plan = json.loads((artifact / "plan.json").read_text(encoding="utf-8"))
    signals = plan["signals"]
    cases = [c for c in encodes["cases"] if c["status"] == "ok"]
    OUT.mkdir(parents=True, exist_ok=True)
    for stale in [*OUT.glob("external-*")]:
        stale.unlink()
    groups = {}
    for case in cases:
        groups.setdefault(case["sha256"], []).append(case)
    written = 0
    for case in sorted(cases, key=lambda c: c["name"]):
        same = sorted(groups[case["sha256"]], key=canonical_key)
        if same[0] is not case:
            continue
        problems = disagreements(case, signals)
        if problems:
            raise SystemExit(f"{case['name']}: " + "; ".join(problems))
        source = artifact / "tak" / case["output"]
        data = source.read_bytes()
        if sha256(data) != case["sha256"] or len(data) != case["size"]:
            raise SystemExit(f"{case['output']} differs from encodes.json")
        if len(data) > LARGE and not allow_large:
            raise SystemExit(f"{case['output']} is {len(data)} bytes; use --allow-large")
        name = f"external-{case['name']}.tak"
        target = OUT / name
        if target.exists() and target.read_bytes() != data and not force:
            raise SystemExit(f"{name} exists with other bytes; use --force")
        shutil.copy(source, target)
        identical = [{"case": c["name"], "arguments": c["arguments"], "mode": c["mode"], "input": c["input"]} for c in same[1:]]
        provenance = {
            "source": SOURCE,
            "run": RUN,
            "case": case["name"],
            "tool": "Takc.exe",
            "arguments": case["arguments"],
            "mode": case["mode"],
            "input": {"name": case["input"], "sha256": case["inputSha256"], "size": case["inputSize"], "signal": signals.get(case["input"]),
                      "lossywav": case.get("lossywav")},
            "sha256": case["sha256"],
            "size": case["size"],
            "reproducible": case["reproducible"],
            "encode": case["encode"],
            "info": case["info"],
            "decode": case["decode"],
            "md5Test": case.get("md5Test"),
            "sdk": case["sdk"],
            "environment": {k: environment.get(k) for k in ("windowsCaption", "windowsVersion", "windowsBuild", "runnerImageOS", "runnerImageVersion",
                                                             "takVersion", "takcSha256", "librarySha256", "libraryFileVersion", "lossywavSha256")},
            "identicalCases": identical,
        }
        target.with_name(name + ".provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        extra = {"encoder": {"tool": SOURCE, "arguments": case["arguments"], "case": case["name"], "mode": case["mode"], "input": case["input"]["name"] if isinstance(case["input"], dict) else case["input"]}}
        target.with_name(name[:-len(".tak")] + ".expected.json").write_text(json.dumps(tak_model.manifest(data, extra), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        written += 1
    skipped = [c["name"] for c in encodes["cases"] if c["status"] != "ok"]
    print(f"Imported {written} files into {OUT}" + (f"; cases without a file: {', '.join(skipped)}" if skipped else ""))


if __name__ == "__main__":
    main()
