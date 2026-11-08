#!/usr/bin/env python3
"""Takes the files of the lossywav-fixtures workflow's artifact into tests/fixtures.

    python3 scripts/fixtures/import_lossywav_fixtures.py path/to/extracted/artifact [--force]

The artifact (`lossywav-fixtures-to-commit`, run 42469204989) holds manifest.json and the files under fixtures/tests/fixtures. lossyWAV 1.4.2
(POSIX port) wrote them; its output carries the time of the run in the text of its fact chunk, so the files are committed once, byte for byte,
and checked by content, never regenerated. This script never runs lossyWAV.

For each file of the sets below it checks the SHA-256 and size against the artifact's manifest, copies it, and writes
`<name>.provenance.json` (where it came from, the settings, and the SHA-256) and, for a WAV file, `<name>.expected.json` from wav_model.py,
with the findings the analyzer must raise.

The manifest's `arguments` leave out `--stdout` for the stdout file (it is a second run of the same input), so the importer adds it, and its
`lossyWavCheck` is the answer for the `.lossy.wav` of the same run, not for the committed file. The importer therefore records that answer as
`lossyWavCheckOfLossyFile` and derives the committed file's own from its bytes by lossyWAV's rule (a fact chunk before the data whose text starts
with lossyWAV; `--check` looks at nothing else): exit 255 and "already processed" if there is one, exit 0 and "not marked" if not. A file already there is left alone unless it has the same bytes or --force is given.

The sets that are imported so far are the WAV files (PR 0 of docs/design/lossywav-detection.md); the FLAC, WavPack and Monkey's Audio files
join with the change that reads their marker.
"""

import hashlib
import json
import pathlib
import shutil
import sys

import wav_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures"
RUN = {"run": 42469204989, "commit": "55ba334", "artifact": "lossywav-fixtures-to-commit (13613675366)"}
SOURCE = "lossyWAV 1.4.2, POSIX port https://github.com/MoSal/lossywav-for-posix.git c3a22be0e1d8d516e3aa1761b6822ec68c06f920"

# Path under tests/fixtures -> the findings the analyzer must raise for it.
IMPORTED = {
    "wav/lossywav-standard.wav": [],
    "wav/lossywav-standard.lwcdf.wav": [],
    "wav/lossywav-standard-chunks.wav": [],
    # --stdout leaves the fact chunk out but its RIFF size still counts it.
    "wav/lossywav-stdout.wav": ["wav.riff-size-past-chunks"],
}


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def own_check(data):
    """lossyWAV's --check answer for the file, by its rule (the design's probes), never by running it."""
    for chunk in m.read(data)["chunks"]:
        if chunk["id"] == "data":
            break
        if chunk["id"] == "fact" and data[chunk["offset"] + 8:chunk["offset"] + 8 + chunk["length"]].startswith(b"lossyWAV"):
            return {"exit": 255, "answer": "already processed", "basis": "derived from the file's bytes by lossyWAV's rule"}
    return {"exit": 0, "answer": "not marked", "basis": "derived from the file's bytes by lossyWAV's rule"}


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(arguments) != 1:
        raise SystemExit(__doc__)
    force = "--force" in sys.argv
    artifact = pathlib.Path(arguments[0])
    manifest = json.loads((artifact / "manifest.json").read_text(encoding="utf-8"))
    records = {record["path"]: record for record in manifest["fixtures"]}
    for relative, findings in IMPORTED.items():
        record = records[f"tests/fixtures/{relative}"]
        data = (artifact / "fixtures" / record["path"]).read_bytes()
        if sha256(data) != record["sha256"] or len(data) != record["size"]:
            raise SystemExit(f"{relative}: the file does not match the artifact's manifest")
        target = OUT / relative
        if target.exists() and target.read_bytes() != data and not force:
            raise SystemExit(f"{relative}: already committed with other bytes; --force replaces it")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(artifact / "fixtures" / record["path"], target)
        run_arguments = [*record["arguments"], "--stdout"] if record["kind"] == "stdout" else record["arguments"]
        stem = target.parent / target.name[:-len(target.suffix)]
        provenance = {
            "file": target.name,
            "sha256": record["sha256"],
            "size": record["size"],
            "tool": SOURCE,
            "workflow": RUN,
            "input": record["input"],
            "inputSha256": record["inputSha256"],
            "arguments": run_arguments,
            "kind": record["kind"],
            "lossyWavCheck": own_check(data),
            "lossyWavCheckOfLossyFile": record["lossyWavCheck"],
        }
        pathlib.Path(f"{stem}.provenance.json").write_text(json.dumps(provenance, indent=1) + "\n", encoding="utf-8")
        expected = m.manifest(data)
        expected["findings"] = [{"code": code} for code in findings]
        expected["tool"] = "lossyWAV 1.4.2 (POSIX port)"
        expected["arguments"] = run_arguments
        pathlib.Path(f"{stem}.expected.json").write_text(json.dumps(expected, indent=1) + "\n", encoding="utf-8")
        print(f"{relative}: {record['size']} bytes")


if __name__ == "__main__":
    main()
