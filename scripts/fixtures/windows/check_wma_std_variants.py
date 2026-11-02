#!/usr/bin/env python3
"""Compares the records of standard Windows Media Audio files (windows-wma-std.yml) with the rule for Microsoft's output.

    python3 check_wma_std_variants.py <analyzer> [--fixtures <folder of the committed files>] <artifact folder>...

Each artifact folder holds records/<file>.json (from the probe of windows-wma-decode.yml) and, for variants, manifest.json (from
make_wma_std_variants.py or make_wma_std_probes.py), and the files themselves where the artifact carries them (probes/, encodes/).
With --fixtures, files of that folder with a record in an artifact folder are read too. The analyzer is run on every file, and the
table gives the samples per channel the decoder DMO and the Source Reader gave next to the analyzer's "Samples" and, where it has
them, its "Coded Samples" (the valid frames times the frame length; ffmpeg decodes one frame less):

  dmo, reader  what Microsoft's decoder gave through the DMO and through the Source Reader
  analyzer     the analyzer's "Samples": the rule fitted to the decoder, (K - 2) * F + (F - min(B, L)) / 2 for K the frames whose bits are
               complete (the frame the last superframe announces and holds whole included), B the last block of frame K and L its
               Next Block Size, for version 2 streams with a bit reservoir and variable block lengths; the valid frames less the first,
               times F, where the rule does not apply
  coded        the analyzer's "Coded Samples"

Kinds of probe whose construction perturbs the decoder (la, sh, tf and hf shift bits and raise the bit offset; cnt changes the frame
count) are listed apart: the analyzer is not expected to follow them. Nothing here is a verdict about the decoder.
"""

import json
import pathlib
import re
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
    inner = []
    walk(streams[0], inner)
    stats = next((n for n in inner if n["name"] == "Statistics" and n.get("children")), None)
    if stats is None:
        return None
    values = {c["name"]: c["value"] for c in stats["children"]}
    return {"analyzer": int(values["Samples"]), "coded": values.get("Coded Samples"), "invalid": int(values.get("Invalid Frames", 0))}


PERTURBING = ("la", "sh", "tf", "hf", "cnt", "from")


def perturbing(info):
    kind = (info or {}).get("kind", "")
    return kind.startswith(PERTURBING) and not kind.startswith(("mz", "mo"))


def locate(folder, name, fixtures):
    for where in (folder, folder / "probes", folder / "encodes", fixtures):
        if where is not None and (where / name).exists():
            return where / name
    return None


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
    rows, apart, unread = [], [], []
    for folder in folders:
        manifest_path = folder / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))["files"] if manifest_path.exists() else {}
        for record_path in sorted((folder / "records").glob("*.json")):
            name = record_path.name[:-5]
            record = json.loads(record_path.read_text(encoding="utf-8"))
            path = locate(folder, name, fixtures)
            got = read(analyzer, path) if path else None
            if got is None:
                unread.append(name)
                continue
            info = manifest.get(name)
            dmo, reader = figure(record, "direct"), figure(record, "sourceReader")
            row = (name, (info or {}).get("kind", ""), dmo, reader, got["analyzer"], got["coded"])
            (apart if perturbing(info) or got["invalid"] else rows).append(row)
    width = max((len(r[0]) for r in rows), default=10)
    print(f"{'file':{width}} {'kind':>8}      dmo   reader analyzer    coded  dmo-analyzer reader-analyzer")
    matched = reader_matched = 0
    for name, kind, dmo, reader, analyzed, coded in rows:
        d = None if dmo is None else dmo - analyzed
        r = None if reader is None else reader - analyzed
        matched += d == 0
        reader_matched += r == 0
        print(f"{name:{width}} {kind:>8} {dmo!s:>8} {reader!s:>8} {analyzed:>8} {coded!s:>8} {d!s:>13} {r!s:>15}")
    print(f"\ndecoder DMO = analyzer in {matched} of {len(rows)} files; Source Reader = analyzer in {reader_matched} of {len(rows)}")
    if apart:
        by_kind = {}
        for name, kind, dmo, reader, analyzed, coded in apart:
            equal = by_kind.setdefault(re.sub(r"-?\d+$", "", kind) or "invalid", [0, 0])
            equal[0] += dmo == analyzed
            equal[1] += 1
        print("\nnot compared (kinds that perturb the decoder, or an invalid frame), with the files where the DMO equals the analyzer:")
        for kind, (equal, total) in sorted(by_kind.items()):
            print(f"  {kind}: {equal} of {total}")
    if unread:
        print(f"\n{len(unread)} records have no file the analyzer could read, and are not compared: {', '.join(unread[:10])}{' ...' if len(unread) > 10 else ''}")
    return 0 if matched == len(rows) and not unread else 1


if __name__ == "__main__":
    sys.exit(main())
