#!/usr/bin/env python3
"""Compares the records of standard Windows Media Audio files (windows-wma-std.yml) with the rule for Microsoft's output.

    python3 check_wma_std_variants.py <analyzer> [--fixtures <folder of the committed files>] <artifact folder>...

Each artifact folder holds records/<file>.json (from the probe of windows-wma-decode.yml) and, for variants, manifest.json (from
make_wma_std_variants.py). With --fixtures, files of that folder with a record in an artifact folder are read too. The analyzer is
run on every file; for each one that has no damage the table gives the samples per channel the decoder DMO and the Source Reader
gave, and:

  coded    the frames' count: (valid frames - 1) * F, the count the analyzer gave before it followed the decoder
  rule     coded + (F - L) / 2, where F is the frame length and L the block size that the last block of the last frame announces
           for the block after it (its "Next Block Size"; F for a stream without variable block lengths)
  analyzer the analyzer's "Samples" in this build

Files with an invalid frame, or with fewer than two valid frames, are listed apart. Nothing here is a verdict about the decoder.
"""

import json
import pathlib
import subprocess
import sys

WMA_NAMES = ("Windows Media Audio 1", "Windows Media Audio 2")


def walk(node, out):
    out.append(node)
    for child in node.get("children") or []:
        walk(child, out)


def figure(record, path):
    run = record.get(path) or {}
    if run.get("crashed") or run.get("stalled"):
        return None
    return run.get("totalSamples")


def read(analyzer, path):
    result = subprocess.run([analyzer, "--json", str(path)], capture_output=True, text=True)
    try:
        report = json.loads(result.stdout)[0]
    except (ValueError, IndexError):
        return None
    nodes = []
    walk(report["root"], nodes)
    streams = [n for n in nodes if n["name"] in WMA_NAMES]
    if len(streams) != 1:
        return None
    stream = streams[0]
    inner = []
    walk(stream, inner)
    stats = next((n for n in inner if n["name"] == "Statistics" and n.get("children")), None)
    if stats is None:
        return None
    values = {c["name"]: c["value"] for c in stats["children"]}
    frames_node = next((n for n in inner if n["name"] == "Frames" and n.get("children")), None)
    frames = [c for c in frames_node["children"] if c["name"] == "Frame"] if frames_node else []
    f = int(values["Frame Length"])
    valid = int(str(stream["value"]).split()[0])
    nexts = []
    if frames:
        blocks = []
        walk(frames[-1], blocks)
        sizes = [b["value"] for b in blocks if b["name"] == "Next Block Size"]
        nexts = [int(sizes[-1])] if sizes else [f]
    return {"valid": valid, "invalid": int(values.get("Invalid Frames", 0)), "frameLength": f, "next": nexts[0] if nexts else f,
            "analyzer": int(values["Samples"])}


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        print(__doc__)
        return 2
    analyzer = args.pop(0)
    fixtures = None
    if args and args[0] == "--fixtures":
        fixtures = pathlib.Path(args[1])
        args = args[2:]
    folders = [pathlib.Path(a) for a in args]
    rows, apart = [], []
    for folder in folders:
        manifest_path = folder / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))["files"] if manifest_path.exists() else {}
        for record_path in sorted((folder / "records").glob("*.json")):
            name = record_path.name[:-5]
            record = json.loads(record_path.read_text(encoding="utf-8"))
            path = folder / name if (folder / name).exists() else (fixtures / name if fixtures and (fixtures / name).exists() else None)
            got = read(analyzer, path) if path else None
            info = manifest.get(name)
            if got is None and info and "next" in info:
                # The variants are made in the job and are not in the artifact: what the maker found stands in for the analyzer's reading.
                got = {"valid": info["frames"], "invalid": 0, "frameLength": info["frameLength"], "next": info["next"], "analyzer": None}
            elif got is not None and info and "next" in info and (got["valid"], got["next"]) != (info["frames"], info["next"]):
                print(f"{name}: the analyzer reads {got['valid']} frames and next size {got['next']}, the maker found {info['frames']} and {info['next']}", file=sys.stderr)
            if got is None:
                continue
            dmo, reader = figure(record, "direct"), figure(record, "sourceReader")
            if got["invalid"] or got["valid"] < 2:
                apart.append((name, got, dmo, reader))
                continue
            f = got["frameLength"]
            coded = (got["valid"] - 1) * f
            rows.append((name, got, dmo, reader, coded, coded + (f - got["next"]) // 2))
    width = max((len(r[0]) for r in rows), default=10)
    print(f"{'file':{width}} frames    F   next      dmo   reader    coded     rule analyzer  dmo-coded dmo-rule reader-rule")
    matched = counted = reader_matched = reader_counted = 0
    for name, got, dmo, reader, coded, rule in rows:
        d_c = None if dmo is None else dmo - coded
        d_r = None if dmo is None else dmo - rule
        r_r = None if reader is None else reader - rule
        if dmo is not None:
            counted += 1
            matched += d_r == 0
        if reader is not None:
            reader_counted += 1
            reader_matched += r_r == 0
        print(f"{name:{width}} {got['valid']:>6} {got['frameLength']:>4} {got['next']:>6} {dmo!s:>8} {reader!s:>8} {coded:>8} {rule:>8} {got['analyzer']!s:>8} {d_c!s:>10} {d_r!s:>8} {r_r!s:>11}")
    print(f"\ndecoder DMO = rule in {matched} of {counted} files; Source Reader = rule in {reader_matched} of {reader_counted}")
    if apart:
        print("\nnot compared (an invalid frame, or fewer than two valid frames):")
        for name, got, dmo, reader in apart:
            print(f"  {name}: valid {got['valid']}, invalid {got['invalid']}, analyzer {got['analyzer']}, dmo {dmo}, reader {reader}")
    return 0 if matched == counted else 1


if __name__ == "__main__":
    sys.exit(main())
