#!/usr/bin/env python3
"""Takes the files of the optimfrog-fixtures workflow's artifact into tests/fixtures/optimfrog.

    python3 scripts/fixtures/import_optimfrog_fixtures.py path/to/extracted/artifact [--force]

The artifact (`optimfrog-fixtures`, run 42672985679) holds manifest.json and the files under fixtures/. OptimFROG 5.100's `ofr`, `off` and `ofs` wrote them
(make_external_optimfrog_fixtures.py, run in CI only); every file was encoded twice and the two files are equal byte for byte, so the files are committed
as they are and a later run is compared byte for byte. This script never runs an encoder.

For each file it checks the SHA-256 and size against the artifact's manifest, copies it, and writes `<name>.provenance.json` (where it came from, the
arguments, the input, the runs' exit statuses, the decoding checks) and, for the files the analyzer reads (`.ofr` and `.ofs`), `<name>.expected.json` from
optimfrog_model.py, which reads the committed bytes by the specification apart from the analyzer. The correction files (`.ofc`) are copied with their
provenance only: their detection is the lead's decision (docs/design/tak-optimfrog-headers.md, "Pending on the fixtures").
"""

import hashlib
import json
import pathlib
import shutil
import sys

import optimfrog_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "optimfrog"
RUN = {"run": 42672985679, "commit": "feat/optimfrog", "artifact": "optimfrog-fixtures"}
DOWNLOAD_PAGE = "http://losslessaudio.org/Downloads.php"
SOURCE = f"OptimFROG 5.100 for Linux x64 (SDK 1.600), {DOWNLOAD_PAGE}"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    force = "--force" in sys.argv
    if len(arguments) != 1:
        raise SystemExit(__doc__)
    artifact = pathlib.Path(arguments[0])
    manifest = json.loads((artifact / "manifest.json").read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    written = 0
    for case in manifest["cases"]:
        if case.get("status") == "no output":
            continue
        entries = [(case["fixture"], case["sha256"], case["size"], case.get("model"))]
        for companion in case.get("companions", {}).values():
            entries.append((companion["fixture"], companion["sha256"], companion["size"], companion.get("model")))
        for name, digest, size, _ in entries:
            source = artifact / "fixtures" / name
            data = source.read_bytes()
            if sha256(data) != digest or len(data) != size:
                raise SystemExit(f"{name} differs from the manifest")
            target = OUT / name
            if target.exists() and target.read_bytes() != data and not force:
                raise SystemExit(f"{name} exists with other bytes; use --force")
            shutil.copy(source, target)
            stem = target.with_suffix("")
            provenance = {"source": SOURCE, "run": RUN, "case": case["name"], "tool": case["tool"], "arguments": case["arguments"], "input": case["input"],
                          "sha256": digest, "size": size, "reproducible": all(case["reproducible"].values()),
                          "runs": {k: {"command": v["command"], "exit": v["exit"]} for k, v in case["runs"].items()},
                          "decoded": case.get("decoded", {})}
            target.with_name(name + ".provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            if target.suffix in (".ofr", ".ofs"):
                extra = {"encoder": {"tool": SOURCE, "arguments": case["arguments"], "case": case["name"], "tool_program": case["tool"], "input": case["input"]}}
                stem.with_suffix(".expected.json").write_text(json.dumps(m.manifest(data, extra), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            written += 1
    print(f"Imported {written} files into {OUT}")


if __name__ == "__main__":
    main()
