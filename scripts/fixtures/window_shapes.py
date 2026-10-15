#!/usr/bin/env python3
"""Tabulates the AAC window shapes (sine or KBD) per file and per window sequence, from the
Encspect CLI's JSON, for one question: does an encoder use sine windows only?

    python3 window_shapes.py <file or directory>... [--cli PATH] [--csv] [--min-streams N] [--summary]

- The CLI runs once per argument with --ndjson. Without --cli, the first of $ENCSPECT, an
  `encspect` on PATH and `dotnet run --project src/Encspect.Cli` is used.
- The JSON has the shares of both shapes per file (Statistics, Window Shapes) but not per
  window sequence, so the counts come from the "ICS Info" nodes of the raw data blocks, whose
  value reads "<sequence>, <shape>, max_sfb ...". An ICS Info directly under a channel pair
  element is a common window and stands for both channel streams; one inside a channel stream
  stands for that one. The total is checked against the Statistics node: a file where they differ
  is marked "!" and its counts are those of the nodes.
- Files with no AAC channel streams are left out. A file whose frame nodes are not in the JSON
  is shown with the shares of the Statistics node only (columns of the sequences stay empty).
- "KBD" columns read "KBD streams of all streams of that sequence", with the percentage.
- --summary adds one line per argument: the totals of its files.
- --by-prefix adds one line per file name prefix (the part before the first "-" or "."; "apple-intel" counts as
  "apple"; the "passthru" copies of other encoders' packets are a prefix of their own), which groups the committed fixtures by encoder.
- With more than one argument, the file names carry their argument's name.
"""

import argparse
import collections
import json
import os
import pathlib
import shutil
import subprocess
import sys

SEQUENCES = ["only long", "long start", "eight short", "long stop"]
LABELS = ["long", "start", "short", "stop"]
SHAPES = ["sine", "kbd"]


def command(cli):
    if cli:
        return [cli]
    if os.environ.get("ENCSPECT"):
        return [os.environ["ENCSPECT"]]
    if shutil.which("encspect"):
        return ["encspect"]
    project = pathlib.Path(__file__).resolve().parents[2] / "src" / "Encspect.Cli"
    return ["dotnet", "run", "-c", "Release", "--project", str(project), "--"]


def percent(part, whole):
    return f"{100.0 * part / whole:.1f}" if whole else "-"


def count(root):
    """Returns ({(sequence, shape): channel streams}, streams in the Statistics node or None, whether ICS Info nodes exist)."""
    counts = collections.Counter()
    stat = {}
    ics = False
    stack = [(root, "")]
    while stack:
        node, parent = stack.pop()
        name = node.get("name")
        if name == "ICS Info" and isinstance(node.get("value"), str):
            ics = True
            parts = [p.strip().lower() for p in node["value"].split(",")]
            if len(parts) >= 2 and parts[0] in SEQUENCES and parts[1] in SHAPES:
                counts[(SEQUENCES.index(parts[0]), SHAPES.index(parts[1]))] += 2 if parent == "CPE" else 1
        elif name == "Window Shapes" and parent == "Statistics":
            for child in node.get("children") or []:
                value = child.get("value")
                if isinstance(value, str) and child.get("name") in ("Sine", "KBD"):
                    stat[child["name"].lower()] = stat.get(child["name"].lower(), 0) + int(value.split()[0].replace(",", ""))
        for child in node.get("children") or []:
            stack.append((child, name))
    return counts, (sum(stat.values()) if stat else None), stat, ics


def rows_for(report):
    counts, statistics_total, stat, ics = count(report["root"])
    total = sum(counts.values())
    if not ics and not stat:
        return None
    row = {"file": report.get("source", "?")}
    if ics:
        row["streams"] = total
        row["sine"] = sum(v for (s, h), v in counts.items() if h == 0)
        row["kbd"] = sum(v for (s, h), v in counts.items() if h == 1)
        row["mismatch"] = statistics_total is not None and statistics_total != total
        for i in range(4):
            row[LABELS[i]] = (counts[(i, 0)] + counts[(i, 1)], counts[(i, 1)])
    else:
        row["streams"] = statistics_total
        row["sine"] = stat.get("sine", 0)
        row["kbd"] = stat.get("kbd", 0)
        row["mismatch"] = False
        for i in range(4):
            row[LABELS[i]] = None
    return row


def analyze(argument, cli):
    process = subprocess.run(command(cli) + ["--ndjson", "--", argument], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    if process.returncode == 3:
        print(f"window_shapes: could not read {argument}: {process.stderr.strip()}", file=sys.stderr)
    rows = []
    for line in process.stdout.splitlines():
        if line.strip():
            row = rows_for(json.loads(line))
            if row and row["streams"]:
                rows.append(row)
    return rows


def cell(entry):
    if entry is None:
        return "?"
    streams, kbd = entry
    return f"{kbd}/{streams} ({percent(kbd, streams)}%)" if streams else "-"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--cli")
    parser.add_argument("--csv", action="store_true", help="comma-separated output instead of the aligned table")
    parser.add_argument("--summary", action="store_true", help="a totals line per argument")
    parser.add_argument("--by-prefix", action="store_true", help="a totals line per file name prefix")
    parser.add_argument("--min-streams", type=int, default=1, help="leave out files with fewer channel streams")
    args = parser.parse_args()

    header = ["file", "streams", "sine %", "KBD %"] + [f"KBD in {label}" for label in LABELS]
    table = []
    totals = []
    groups = collections.OrderedDict()
    for argument in args.inputs:
        rows = [r for r in analyze(argument, args.cli) if r["streams"] >= args.min_streams]
        rows.sort(key=lambda r: r["file"])
        sums = {"name": argument, "streams": 0, "sine": 0, "kbd": 0, **{l: [0, 0] for l in LABELS}}
        for r in rows:
            base = os.path.basename(r["file"]) if os.path.isdir(argument) else r["file"]
            if len(args.inputs) > 1 and os.path.isdir(argument):
                base = os.path.basename(argument.rstrip("/\\")) + "/" + base
            key = os.path.basename(r["file"]).replace("apple-intel-", "apple-").replace(".", "-").split("-")[0]
            if "passthru" in base:
                key = "passthru"  # another encoder's packets in Apple's writer
            group = groups.setdefault(key, {"files": 0, "streams": 0, "sine": 0, "kbd": 0, **{l: [0, 0] for l in LABELS}})
            group["files"] += 1
            for field in ("streams", "sine", "kbd"):
                group[field] += r[field]
            for l in LABELS:
                if r[l]:
                    group[l][0] += r[l][0]
                    group[l][1] += r[l][1]
            table.append([base + (" !" if r["mismatch"] else ""), str(r["streams"]), percent(r["sine"], r["streams"]), percent(r["kbd"], r["streams"])] + [cell(r[l]) for l in LABELS])
            sums["streams"] += r["streams"]
            sums["sine"] += r["sine"]
            sums["kbd"] += r["kbd"]
            for l in LABELS:
                if r[l]:
                    sums[l][0] += r[l][0]
                    sums[l][1] += r[l][1]
        totals.append((sums, len(rows)))
    if args.summary:
        for sums, n in totals:
            table.append([f"TOTAL {sums['name']} ({n} files)", str(sums["streams"]), percent(sums["sine"], sums["streams"]), percent(sums["kbd"], sums["streams"])] + [cell(tuple(sums[l])) for l in LABELS])
    if args.by_prefix:
        for key, g in sorted(groups.items()):
            table.append([f"PREFIX {key} ({g['files']} files)", str(g["streams"]), percent(g["sine"], g["streams"]), percent(g["kbd"], g["streams"])] + [cell(tuple(g[l])) for l in LABELS])
    if args.csv:
        print(",".join(header))
        for r in table:
            print(",".join('"' + c.replace('"', '""') + '"' for c in r))
    else:
        widths = [max(len(x) for x in col) for col in zip(header, *table)]
        for r in [header] + table:
            print("  ".join(c.ljust(w) if i == 0 else c.rjust(w) for i, (c, w) in enumerate(zip(r, widths))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
