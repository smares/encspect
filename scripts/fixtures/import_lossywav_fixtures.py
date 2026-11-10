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
marker). For a FLAC file the importer decodes the committed bytes with flac_model.py and recomputes the figures of the workflow's `figures` (the
spans of 32 samples: the distribution of their counts of zero low bits, the changes, the grid, the changes on it, and the times the channels agree), and the
wasted bits of every subframe against `flacUnits`; a difference stops the import. That recomputation is the reference for the article's statements. The
`figures` of a WavPack or Monkey's Audio file are the workflow's record: the file is checked to decode to the samples of the FLAC file made from the same
input (same input and lossyWAV arguments), whose figures are recomputed.
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


# Path under tests/fixtures -> the WAV file the PCM of a Monkey's Audio file is taken from (None for the others).
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

# (input SHA-256, lossyWAV arguments) -> MD5 of the decoded samples of the FLAC file made from that output, whose figures are recomputed.
FIGURES = {}


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
    """The text of lossyWAV's fact chunk if the committed file holds one before its audio, by lossyWAV's rule, found by walking the chunks the coder
    kept: the "riff" APPLICATION blocks of FLAC's foreign metadata, or the chunks after the WAVE form type in the wrapper of WavPack and the header
    data of Monkey's Audio. A data chunk ahead of the text puts it after the audio."""
    chunks = []
    if data[:4] == b"fLaC":
        position = 4
        while position + 4 <= len(data):
            header = data[position]
            size = int.from_bytes(data[position + 1:position + 4], "big")
            body = data[position + 4:position + 4 + size]
            if header & 0x7F == 2 and body[:4] == b"riff":
                chunks.append((body[4:8], int.from_bytes(body[8:12], "little"), body[12:]))
            position += 4 + size
            if header & 0x80:
                break
    else:
        position = data.find(b"WAVE", 0, 4096) + 4
        while 4 <= position < min(len(data), 4096) and position + 8 <= len(data):
            size = int.from_bytes(data[position + 4:position + 8], "little")
            chunks.append((data[position:position + 4], size, data[position + 8:position + 8 + size]))
            position += 8 + size + (size & 1)
    for chunk_id, size, body in chunks:
        if chunk_id == b"data":
            return None
        if chunk_id == b"fact" and body.startswith(b"lossyWAV "):
            return body[:size].rstrip(b"\r\n\0").decode("latin-1")
    return None


def own_check_coded(data):
    return {"exit": 255, "answer": "already processed", "basis": "derived from the file's bytes by lossyWAV's rule"} if marker_text(data) is not None else {"exit": 0, "answer": "not marked", "basis": "derived from the file's bytes by lossyWAV's rule"}


def span_figures(stream):
    """The figures of docs/design/lossywav-detection.md, "The measurement", over the decoded samples of a FLAC stream (flac_model.py): spans of 32
    samples per channel; the count is the zero low bits all samples share, the magnitude the bits beside the sign; a silent span has no count; a
    thin span has a magnitude fewer than 4 bits above its count; a short span (the last) is counted in the distribution only; the changes are of the
    spans that are none of these, against the channel's last such span; the grid is the largest power of two from 256 to 65,536 samples with 95% of at least 20
    changes within one span of one phase."""
    channels = None
    for f in stream.frames:
        if "channels" not in f:
            continue
        if channels is None:
            channels = [[] for _ in f["channels"]]
        for series, ch in zip(channels, f["channels"]):
            series.extend(ch)
    width = stream.info["bits"]
    spans = silent = thin = 0
    distribution = {}
    changes = []
    counts = []
    for series in channels:
        last = None
        row = []
        for start in range(0, len(series), 32):
            block = series[start:start + 32]
            spans += 1
            ored = 0
            magnitude = 0
            for v in block:
                ored |= v
                magnitude = max(magnitude, v.bit_length() if v >= 0 else (-v - 1).bit_length())
            if ored == 0:
                silent += 1
                row.append(None)
                continue
            count = min((ored & -ored).bit_length() - 1, width)
            distribution[count] = distribution.get(count, 0) + 1
            row.append(count)
            if len(block) < 32:
                continue
            if magnitude - count < 4:
                thin += 1
                continue
            if last is not None and last != count:
                changes.append(start)
            last = count
        counts.append(row)
    times = equal = 0
    for column in zip(*counts):
        if None in column:
            continue
        times += 1
        equal += len(set(column)) == 1
    grid = on_grid = None
    if len(changes) >= 20:
        for g in (65536 >> k for k in range(9)):
            best = max(sum(min((c - p) % g, (p - c) % g) < 32 for c in changes) for p in {c % g for c in changes})
            if best * 20 >= len(changes) * 19:
                grid, on_grid = g, best
                break
    low = min(distribution) if distribution else 0
    return {"spans": spans, "silentSpans": silent, "thinSpans": thin, "changes": len(changes), "grid": grid, "changesOnGrid": on_grid,
            "timesWithEqualCounts": equal, "times": times, "lowBitsUnused": low, "distribution": {str(k): v for k, v in sorted(distribution.items())}}


def flac_units(data):
    """The wasted bits of every subframe, read from the committed bytes by flac_model.py: the units, their distribution and the changes between the
    frames of a channel."""
    stream = flac_model.Stream(data, 0, len(data))
    return units_of(stream)


TESTED_GRIDS = {22050: 256, 32000: 256, 44100: 512, 48000: 512, 88200: 1024, 96000: 1024, 176400: 2048, 192000: 2048}


def units_of(stream):
    """The distribution of the subframes' wasted bits, and the behavior rule's figures (docs/design/lossywav-detection.md, "FLAC and mid/side"): only a
    source channel coded as itself is compared with the same channel's previous subframe (left in left/right and left/side, right in left/right and
    side/right; every channel of a frame of one or three or more channels), a mid or side subframe is in the distribution only, and a constant subframe
    of 0 is digital silence with no count. `changes` is the older count of every subframe against the one at the same index, which the workflow recorded."""
    import math
    frames = [f for f in stream.frames if "subframes" in f]
    channels = max(len(f["subframes"]) for f in frames)
    distribution, compared = {}, {}
    compared_changes = every_change = silent = 0
    previous, last = [None] * channels, [None] * channels
    moments, first, divisor = set(), None, 0
    block = max(f["header"]["block_size"] for f in frames)
    for f in frames:
        code, start = f["header"]["channel_code"], f["header"]["number"] * (1 if f["header"]["variable"] else block)
        for index, sub in enumerate(f["subframes"]):
            if sub["type"] == "constant" and sub.get("constant") == 0:
                silent += 1
                continue
            wasted = sub["wasted"]
            distribution[wasted] = distribution.get(wasted, 0) + 1
            if previous[index] is not None and previous[index] != wasted:
                every_change += 1
            previous[index] = wasted
            is_source = not (code == 10 or (code == 8 and index == 1) or (code == 9 and index == 0))
            if not is_source:
                continue
            compared[wasted] = compared.get(wasted, 0) + 1
            if last[index] is not None and last[index] != wasted:
                compared_changes += 1
                moments.add(start)
                first = start if first is None else first
                divisor = math.gcd(divisor, abs(start - first))
            last[index] = wasted
    rate = frames[0]["header"]["sample_rate"] or stream.info["sample_rate"]
    above = sum(n for c, n in compared.items() if c > min(compared)) if compared else 0
    total = sum(compared.values())
    claim = TESTED_GRIDS.get(rate) is not None and len(moments) >= 20 and divisor == TESTED_GRIDS[rate] and above * 2 > total
    return {"subframes": sum(distribution.values()) + silent, "distribution": dict(sorted(distribution.items())), "changes": every_change,
            "silentSubframes": silent, "comparedChanges": compared_changes, "moments": len(moments), "gridOfChanges": divisor, "comparedSubframes": total, "comparedAboveSmallest": above, "claim": claim}


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
            # The workflow recorded the older count (`changes`: every subframe against the one at the same index); the figures of the design's rule are the
            # importer's own, from the committed bytes.
            for key in ("subframes", "distribution", "changes"):
                mine = units[key]
                if mine != recorded[key]:
                    raise SystemExit(f"{relative}: the wasted bits of the committed bytes ({key}: {mine}) differ from the workflow's ({recorded[key]})")
            provenance["flacUnitsFromCommittedBytes"] = units
            stream = flac_model.Stream(data, 0, len(data))
            figures = span_figures(stream)
            for key, value in figures.items():
                if value != record["figures"].get(key):
                    raise SystemExit(f"{relative}: the {key} of the committed bytes ({value}) differs from the workflow's ({record['figures'].get(key)})")
            provenance["figuresFromCommittedBytes"] = figures
            FIGURES[(record["inputSha256"], tuple(record["arguments"]))] = expected_md5 = flac_model.decode_md5(stream)["decoded_pcm_md5"]
            expected = flac_model.manifest(data, 0, len(data), {"regions": [{"name": "FLAC", "offset": 0, "length": len(data)}], "encoder": encoder, "crosscheck": {"flac_test": True}})
        elif relative.endswith(".wv"):
            expected = wavpack_model.manifest(data, 0, len(data), {"regions": [{"name": "WavPack", "offset": 0, "length": len(data)}], "encoder": {**encoder, "file": "main file"}, "findings": []})
            expected["crosscheck"] = {"valid": True, "decode": {"opens": True, "samples": expected["stream_infos"][0]["samples"], "errors": 0}}
            same = FIGURES.get((record["inputSha256"], tuple(record["arguments"])))
            if same is None or expected.get("decoded_pcm_md5") != same:
                raise SystemExit(f"{relative}: does not decode to the samples of the FLAC file made from the same lossyWAV output ({same} against {expected.get('decoded_pcm_md5')})")
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
