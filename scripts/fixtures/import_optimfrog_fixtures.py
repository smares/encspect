#!/usr/bin/env python3
"""Takes the files of the optimfrog-fixtures workflow's artifact into tests/fixtures/optimfrog.

    python3 scripts/fixtures/import_optimfrog_fixtures.py path/to/extracted/artifact --run=RUN_ID --commit=SHA [--force]

The artifact (`optimfrog-fixtures` of the run --run names, made at the commit --commit names) holds manifest.json and the files under fixtures/. OptimFROG 5.100's `ofr`, `off` and `ofs` wrote them
(make_external_optimfrog_fixtures.py, run in CI only); every file was encoded twice and the two files are equal byte for byte, so the files are committed
as they are; a later run is compared by hand (its manifest's `sha256` for each case against the provenance files). Cases that wrote the same bytes are committed once,
the others listed as `identicalCases` in that file's provenance. This script never runs an encoder.

For each file it checks the SHA-256 and size against the artifact's manifest, copies it, and writes `<name>.provenance.json` (where it came from, the
arguments, the input, the runs' exit statuses, the decoding checks) and, for the files the analyzer reads (`.ofr` and `.ofs`), `<name>.expected.json` from
optimfrog_model.py, which reads the committed bytes by the specification apart from the analyzer. The correction files (`.ofc`) get one too: a run of CORR blocks
whose CRC-32s are compared (the lead's decision of 2026-11-09).
"""

import hashlib
import json
import pathlib
import shutil
import sys

import optimfrog_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "optimfrog"
RUN = {"run": 0, "commit": "", "artifact": "optimfrog-fixtures"}
DOWNLOAD_PAGE = "http://losslessaudio.org/Downloads.php"
SOURCE = f"OptimFROG 5.100 for Linux x64 (SDK 1.600), {DOWNLOAD_PAGE}"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def canonical_key(case):
    """Which of the cases that wrote the same bytes is committed: a preset before a mode, a case without a correction file before one with, then the shortest name."""
    name = case["name"]
    return (0 if name.startswith("preset-") else 1, 1 if "correction" in name else 0, len(name), name)


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    force = "--force" in sys.argv
    options = [a for a in sys.argv[1:] if a.startswith("--run=") or a.startswith("--commit=")]
    for option in options:
        key, value = option[2:].split("=", 1)
        RUN[key] = int(value) if key == "run" else value
    if len(arguments) != 1:
        raise SystemExit(__doc__)
    artifact = pathlib.Path(arguments[0])
    manifest = json.loads((artifact / "manifest.json").read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    for stale in OUT.glob("external-*"):
        stale.unlink()
    groups = {}
    for case in manifest["cases"]:
        if case.get("status") != "no output":
            groups.setdefault(case["sha256"], []).append(case)
    written = 0
    for case in sorted(manifest["cases"], key=lambda c: c["name"]):
        if case.get("status") == "no output":
            continue
        same = sorted(groups[case["sha256"]], key=canonical_key)
        if same[0] is not case:
            continue
        identical = [{"case": c["name"], "arguments": c["arguments"], "tool": c["tool"], "input": c["input"]} for c in same[1:]]
        entries = [(case["fixture"], case["sha256"], case["size"], case.get("model"))]
        for companion in case.get("companions", {}).values():
            entries.append((companion["fixture"], companion["sha256"], companion["size"], companion.get("model")))
        for name, digest, size, _ in entries:
            source_name = name
            source = artifact / "fixtures" / name
            data = source.read_bytes()
            if sha256(data) != digest or len(data) != size:
                raise SystemExit(f"{name} differs from the manifest")
            if name.endswith(".ofc"):
                # Beside the .ofs of the same case, whose manifest and golden take the stem.
                name = name[:-4] + "-data.ofc"
            target = OUT / name
            if target.exists() and target.read_bytes() != data and not force:
                raise SystemExit(f"{name} exists with other bytes; use --force")
            shutil.copy(source, target)
            stem = target.with_suffix("")
            provenance = {"source": SOURCE, "run": RUN, "case": case["name"], "tool": case["tool"], "arguments": case["arguments"], "input": case["input"],
                          "sha256": digest, "size": size, "reproducible": all(case["reproducible"].values()),
                          "runs": {k: {"command": v["command"], "exit": v["exit"]} for k, v in case["runs"].items()},
                          "decoded": case.get("decoded", {}), "identicalCases": identical}
            target.with_name(name + ".provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            if target.suffix in (".ofr", ".ofs", ".ofc"):
                extra = {"encoder": {"tool": SOURCE, "arguments": case["arguments"], "case": case["name"], "tool_program": case["tool"], "input": case["input"]}}
                stem.with_suffix(".expected.json").write_text(json.dumps(m.manifest(data, extra), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            written += 1
    print(f"Imported {written} files into {OUT}")


if __name__ == "__main__":
    main()
