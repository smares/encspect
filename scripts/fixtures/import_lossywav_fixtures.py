#!/usr/bin/env python3
"""Takes the files of the lossywav-fixtures workflow's artifact into tests/fixtures.

    python3 scripts/fixtures/import_lossywav_fixtures.py path/to/extracted/artifact [--force]

The artifact (`lossywav-fixtures-to-commit`, run 42469204989) holds manifest.json and the files under fixtures/tests/fixtures. lossyWAV 1.4.2
(POSIX port) wrote them; its output carries the time of the run in the text of its fact chunk, so the files are committed once, byte for byte,
and checked by content, never regenerated. This script never runs lossyWAV.

For each file of the sets below it checks the SHA-256 and size against the artifact's manifest, copies it, and writes
`<name>.provenance.json` (where it came from, the settings, the coding, the SHA-256 and the figures the workflow recorded) and
`<name>.expected.json` from the format's model (wav_model.py, flac_model.py, wavpack_model.py, monkeys_audio_model.py), which reads the committed
bytes apart from the analyzer, with the findings the analyzer must raise.

The manifest's `arguments` leave out `--stdout` for the stdout file (it is a second run of the same input), so the importer adds it, and its
`lossyWavCheck` is the answer for the `.lossy.wav` of the same run, not for the committed file. The importer therefore records that answer as
`lossyWavCheckOfLossyFile` and derives the committed file's own from its bytes by lossyWAV's rule (a fact chunk before the data whose text starts
with lossyWAV; `--check` looks at nothing else): exit 255 and "already processed" if there is one, exit 0 and "not marked" if not. A file already there is left alone unless it has the same bytes or --force is given.

The sets are the WAV files (PR 0 of docs/design/lossywav-detection.md) and the FLAC, WavPack and Monkey's Audio files (PR 1, which reads their
marker). For a FLAC file the importer also reads the wasted bits of every subframe from the committed bytes and holds them to the workflow's
`flacUnits` (the distribution, the changes and how many fall on a frame boundary): that recomputation is the reference for the article's statements.
"""

import hashlib
import json
import pathlib
import shutil
import sys

import flac_model
import monkeys_audio_model
import wav_model as m
import wavpack_model

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


# Path under tests/fixtures -> (coding, the file the PCM of a Monkey's Audio file is taken from).
CODED = {
    "monkeys-audio/external-lossywav.ape": "wav/lossywav-standard.wav",
    "flac/external-lossyflac-b512.flac": None,
    "flac/external-lossyflac-b512-foreign.flac": None,
    "flac/external-lossyflac-b4096.flac": None,
    "flac/external-lossyflac-synthetic-b512.flac": None,
    "flac/external-lossyflac-synthetic-b4096.flac": None,
    "flac/external-lossyflac-96k-b1024.flac": None,
    "flac/external-lossyflac-96k-b512.flac": None,
    "flac/external-lossyflac-6ch.flac": None,
    "wavpack/external-lossywav.wv": None,
    "wavpack/external-lossywav-merge-blocks.wv": None,
    "wavpack/external-lossywav-synthetic-merge-blocks.wv": None,
    "flac/external-lossyflac-floor-22k.flac": None,
    "flac/external-lossyflac-floor-32k.flac": None,
    "flac/external-lossyflac-floor-44k.flac": None,
    "flac/external-lossyflac-floor-88k.flac": None,
    "flac/external-lossyflac-floor-176k.flac": None,
    "flac/external-lossyflac-floor-192k.flac": None,
    "flac/external-lossyflac-floor-44k-16bit.flac": None,
    "flac/external-lossyflac-floor-44k-16bit-quiet.flac": None,
}

# The commands that coded the lossyWAV output (make_lossywav_fixtures.py, CODERS), with the file names left out.
CODING_ARGUMENTS = {
    "flac-b512-foreign": ["flac", "-s", "-f", "-5", "-b", "512", "--keep-foreign-metadata"],
    "flac-b512": ["flac", "-s", "-f", "-5", "-b", "512"],
    "flac-b4096": ["flac", "-s", "-f", "-5"],
    "wavpack": ["wavpack", "-q", "-y"],
    "wavpack-b512-merge": ["wavpack", "-q", "-y", "--blocksize=512", "--merge-blocks"],
    "mac-c2000": ["mac", "-c2000"],
}

TOOLS = {
    "flac": "flac 1.5.0 (the flac package of the CI runner, ubuntu-26.04)",
    "wavpack": "wavpack 5.9.0 (the wavpack package of the CI runner, ubuntu-26.04)",
    "mac": "mac, the console tool of the Monkey's Audio SDK 13.44, built from MAC_1344_SDK.zip",
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


def coding_of(record, data):
    """The command that coded the file, the flac block size of a grid coding taken from the committed STREAMINFO."""
    kind = record["kind"]
    if kind == "flac-bgrid":
        block = int.from_bytes(data[8:10], "big")
        return ["flac", "-s", "-f", "-5", "-b", str(block)]
    return CODING_ARGUMENTS[kind]


def marker_text(data):
    """The text of lossyWAV's fact chunk if the committed file holds one before its audio, found in the first bytes of the file the coder kept
    the WAV header in (the foreign metadata blocks of FLAC, the wrapper of WavPack, the header data of Monkey's Audio)."""
    start = data.find(b"fact")
    while 0 <= start < 4096:
        if data[start + 8:start + 17] == b"lossyWAV ":
            size = int.from_bytes(data[start + 4:start + 8], "little")
            return data[start + 8:start + 8 + size].rstrip(b"\r\n\0").decode("latin-1")
        start = data.find(b"fact", start + 1)
    return None


def own_check_coded(data):
    return {"exit": 255, "answer": "already processed", "basis": "derived from the file's bytes by lossyWAV's rule"} if marker_text(data) is not None else {"exit": 0, "answer": "not marked", "basis": "derived from the file's bytes by lossyWAV's rule"}


def flac_units(data):
    """The wasted bits of every subframe, read from the committed bytes by flac_model.py: the units, their distribution, the changes between the
    frames of a channel and how many of them fall on a multiple of the frame length (the grid, when the frames are the lossyWAV block)."""
    stream = flac_model.Stream(data, 0, len(data))
    frames = [f for f in stream.frames if "subframes" in f]
    channels = max(len(f["subframes"]) for f in frames)
    distribution = {}
    changes = 0
    previous = [None] * channels
    for f in frames:
        for index, sub in enumerate(f["subframes"]):
            distribution[sub["wasted"]] = distribution.get(sub["wasted"], 0) + 1
            if previous[index] is not None and previous[index] != sub["wasted"]:
                changes += 1
            previous[index] = sub["wasted"]
    return {"subframes": sum(len(f["subframes"]) for f in frames), "distribution": dict(sorted(distribution.items())), "changes": changes}


def import_coded(artifact, records):
    for relative, pcm_from in CODED.items():
        record = records[f"tests/fixtures/{relative}"]
        data = (artifact / "fixtures" / record["path"]).read_bytes()
        if sha256(data) != record["sha256"] or len(data) != record["size"]:
            raise SystemExit(f"{relative}: the file does not match the artifact's manifest")
        target = OUT / relative
        if target.exists() and target.read_bytes() != data and "--force" not in sys.argv:
            raise SystemExit(f"{relative}: already committed with other bytes; --force replaces it")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(artifact / "fixtures" / record["path"], target)
        stem = target.parent / target.name[:-len(target.suffix)]
        coding = coding_of(record, data)
        check = own_check_coded(data)
        # lossyWAV's --check answered for the WAV the file was coded from; the committed file keeps the marker only where its coder kept the chunk.
        provenance = {
            "file": target.name,
            "sha256": record["sha256"],
            "size": record["size"],
            "tool": SOURCE,
            "workflow": RUN,
            "input": record["input"],
            "inputSha256": record["inputSha256"],
            "arguments": record["arguments"],
            "kind": record["kind"],
            "coding": coding,
            "codingTool": TOOLS[coding[0]],
            "lossyWavCheck": check,
            "lossyWavCheckOfLossyFile": record["lossyWavCheck"],
            "marker": marker_text(data),
            "figures": record["figures"],
            "passesGate": record["passesGate"],
        }
        for key in ("lengthsTried", "flacUnits"):
            if key in record:
                provenance[key] = record[key]
        encoder = {"tool": TOOLS[coding[0]], "arguments": coding[1:], "input": {"kind": "lossyWAV 1.4.2 output", "source": record["input"], "arguments": record["arguments"]}}
        if relative.endswith(".flac"):
            units = flac_units(data)
            recorded = record["flacUnits"]
            units = {**units, "distribution": {str(k): v for k, v in units["distribution"].items()}}
            for key in ("subframes", "distribution", "changes"):
                if units[key] != recorded[key]:
                    raise SystemExit(f"{relative}: the wasted bits of the committed bytes ({key}: {units[key]}) differ from the workflow's ({recorded[key]})")
            provenance["flacUnitsFromCommittedBytes"] = units
            expected = flac_model.manifest(data, 0, len(data), {"regions": [{"name": "FLAC", "offset": 0, "length": len(data)}], "encoder": encoder, "crosscheck": {"flac_test": True}})
        elif relative.endswith(".wv"):
            expected = wavpack_model.manifest(data, 0, len(data), {"regions": [{"name": "WavPack", "offset": 0, "length": len(data)}], "encoder": {**encoder, "file": "main file"}, "findings": []})
            expected["crosscheck"] = {"valid": True, "decode": {"opens": True, "samples": expected["stream_infos"][0]["samples"], "errors": 0}}
        else:
            wav = (OUT / pcm_from).read_bytes()
            chunk = next(c for c in m.read(wav)["chunks"] if c["id"] == "data")
            pcm = wav[chunk["offset"] + 8:chunk["offset"] + 8 + chunk["length"]]
            a = monkeys_audio_model.parse(data)
            samples = len(pcm) // (a.channels * a.bits // 8)
            regions = [{"name": "Monkey's Audio", "offset": a.junk, "length": a.data_end - a.junk}]
            decode = {"status": "decoded", "checked": len(a.frames), "failed": 0, "samples": samples, "findings": []}
            expected = monkeys_audio_model.manifest(data, a, regions, encoder, [], {"md5": hashlib.md5(pcm).hexdigest()}, decode, stored_crcs=len(a.frames))
            expected["crosscheck"] = {"mac_verify": True, "samples": samples}
            provenance["pcmFrom"] = pcm_from
        pathlib.Path(f"{stem}.provenance.json").write_text(json.dumps(provenance, indent=1) + "\n", encoding="utf-8")
        pathlib.Path(f"{stem}.expected.json").write_text(json.dumps(expected, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{relative}: {record['size']} bytes, marker {'yes' if provenance['marker'] else 'no'}")


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
    import_coded(artifact, records)


if __name__ == "__main__":
    main()
