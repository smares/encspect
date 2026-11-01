#!/usr/bin/env python3
"""Compares the records of the WMA Professional variants (windows-wma-pro-variants.yml) with two predictions.

    python3 check_wma_pro_variants.py [--analyzer path/to/encspect --public <folder of the public files>] <artifact folder>...

The folder holds manifest.json (from make_wma_pro_variants.py) and records/<file>.json (from the probe). For each
variant it prints the samples per channel the decoder DMO and the Source Reader gave, and:

  analyzer   the analyzer's figure: frames * F less the first frame's start trim and the last frame's end trim
  rule       what the records of the stereo and 5.1 fixtures fit (docs/design/wma-pro-format.md, "What Microsoft's
             decoder outputs"). With N frames, the frame size F and the largest size, over the channels, of the last
             subframe of the last frame (L_last), of the first subframe of the first frame (L_first) and of the last
             subframe of the frame before the last (L_prev):
               end of the output E = N * F + (F - L_last) / 2
               start cut = the start trim where the first frame states one, else F / 2 + L_first / 2
               end cut = the end trim, at most the output of the last frame, E - ((N - 1) * F + (F - L_prev) / 2)
               rule = E - start cut - end cut

With --analyzer and --public, the public files in that folder are read through the analyzer's report (frames, trims and
subframe sizes) instead of a manifest, for the artifact folders' records of files that are not variants.
Nothing here is a verdict about the decoder.
"""

import json
import pathlib
import re
import subprocess
import sys


def figure(record, path):
    run = record.get(path) or {}
    if run.get("crashed") or run.get("stalled"):
        return None
    return run.get("totalSamples")


def rule_of(n, f, start, start_stated, end, first, last, prev):
    e = n * f + (f - max(last)) // 2
    start_cut = start if start_stated else f // 2 + max(first) // 2
    if n >= 2 and prev:
        chunk = e - ((n - 1) * f + (f - max(prev)) // 2)
        end_cut = min(end, chunk)
    else:
        end_cut = end
    return e - start_cut - end_cut


def find(node, name, out):
    if node["name"] == name:
        out.append(node)
    for child in node.get("children") or []:
        find(child, name, out)


def sizes_of(frame, f):
    tile = next(c for c in frame["children"] if c["name"] == "Tile Header")
    children = tile.get("children")
    if not children:
        return [f], [f]
    lists = [[int(x) for x in c["value"].split(",")] for c in children]
    return [x[0] for x in lists], [x[-1] for x in lists]


def public_rows(analyzer, folder, folders):
    rows = []
    for path in sorted(folder.glob("*.wma")):
        record_path = next((d / "records" / (path.name + ".json") for d in folders if (d / "records" / (path.name + ".json")).exists()), None)
        if record_path is None:
            continue
        record = json.loads(record_path.read_text(encoding="utf-8"))
        result = subprocess.run([analyzer, "--json", str(path)], capture_output=True, text=True)
        try:
            report = json.loads(result.stdout)[0]
        except (ValueError, IndexError):
            continue
        stats = []
        find(report["root"], "Statistics", stats)
        if not stats:
            continue
        stats = [x for x in stats if any(c["name"] == "Frame Length" for c in x.get("children") or [])]
        if not stats:
            continue
        stat = {c["name"]: c["value"] for c in stats[0]["children"]}
        frames_nodes = [x for x in (lambda out: (find(report["root"], "Frames", out), out)[1])([]) if x.get("children")]
        if not frames_nodes:
            continue
        frames = [c for c in frames_nodes[0]["children"] if c["name"] == "Frame"]
        f = int(stat["Frame Length"])
        valid = [x for x in frames]
        trim_first = next((c["value"] for c in valid[0]["children"] if c["name"] == "Trim"), "none")
        trim_last = next((c["value"] for c in valid[-1]["children"] if c["name"] == "Trim"), "none")
        m = re.match(r"(\d+) samples at the start, (\d+) at the end", trim_first)
        start, stated = (int(m[1]), True) if m and int(m[1]) > 0 else (0, False)
        m = re.match(r"(\d+) samples at the start, (\d+) at the end", trim_last)
        end = int(m[2]) if m else 0
        first, _ = sizes_of(valid[0], f)
        _, last = sizes_of(valid[-1], f)
        prev = sizes_of(valid[-2], f)[1] if len(valid) > 1 else None
        n = len(valid)
        info = {"frames": n, "startTrim": start, "endTrim": end, "lastSizes": last, "frameSize": f, "startStated": stated,
                "firstSizes": first, "prevLastSizes": prev, "channels": stat.get("Channels")}
        rows.append((path.name, info, figure(record, "direct"), figure(record, "sourceReader"), int(stat["Samples"]),
                     rule_of(n, f, start, stated, end, first, last, prev)))
    return rows


def main():
    args = sys.argv[1:]
    analyzer = public = None
    while args and args[0].startswith("--"):
        if args[0] == "--analyzer":
            analyzer = args[1]
        elif args[0] == "--public":
            public = pathlib.Path(args[1])
        else:
            print(__doc__)
            return 2
        args = args[2:]
    if not args:
        print(__doc__)
        return 2
    folders = list(map(pathlib.Path, args))
    rows = []
    for folder in folders:
        manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))["files"]
        for name, info in sorted(manifest.items()):
            record_path = folder / "records" / (name + ".json")
            if not record_path.exists() or "frames" not in info or info["endTrim"] is None:
                continue
            record = json.loads(record_path.read_text(encoding="utf-8"))
            f = info["frameSize"]
            start, end = info["startTrim"], info["endTrim"]
            analyzer_figure = max(0, info["frames"] * f - start - end)
            rule = rule_of(info["frames"], f, start, info["startStated"], end, info["firstSizes"], info["lastSizes"], info["prevLastSizes"])
            rows.append((name, info, figure(record, "direct"), figure(record, "sourceReader"), analyzer_figure, rule))
    if analyzer and public:
        rows += public_rows(analyzer, public, folders)
    width = max((len(r[0]) for r in rows), default=10)
    print(f"{'file':{width}}  frames start stated  end  last-sizes            dmo   reader  analyzer     rule  dmo-analyzer dmo-rule reader-rule")
    matched = counted = 0
    for name, info, dmo, reader, analyzer_figure, rule in rows:
        d_a = None if dmo is None else dmo - analyzer_figure
        d_r = None if dmo is None else dmo - rule
        r_r = None if reader is None else reader - rule
        if d_r is not None:
            counted += 1
            matched += d_r == 0
        print(f"{name:{width}}  {info['frames']:>5} {info['startTrim']:>5} {str(info['startStated'])[0]:>6} {info['endTrim']:>4}  {str(info['lastSizes'])[:20]:<20} {dmo!s:>8} {reader!s:>8} {analyzer_figure:>9} {rule:>8} {d_a!s:>10} {d_r!s:>8} {r_r!s:>10}")
    print(f"\ndecoder = rule in {matched} of {counted} files with a decoder figure")
    return 0


if __name__ == "__main__":
    sys.exit(main())
