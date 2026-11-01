#!/usr/bin/env python3
"""Compares the records of the WMA Professional variants (windows-wma-pro-variants.yml) with two predictions.

    python3 check_wma_pro_variants.py <artifact folder> [more artifact folders]

The folder holds manifest.json (from make_wma_pro_variants.py) and records/<file>.json (from the probe). For each
variant it prints the samples per channel the decoder DMO and the Source Reader gave, and:

  analyzer   the analyzer's figure: frames * F less the first frame's start trim and the last frame's end trim
             (the first frame's trim of F is what the analyzer subtracts for the frame it does not output)
  rule       frames * F + (F - largest last subframe over the channels of the last frame) / 2, less the same trims:
             the rule that fits the records of run 41252306359 (docs/design/wma-pro-format.md, "What Microsoft's
             decoder outputs")

and the differences decoder - analyzer and decoder - rule. Nothing here is a verdict about the decoder.
"""

import json
import pathlib
import sys


def figure(record, path):
    run = record.get(path) or {}
    if run.get("crashed") or run.get("stalled"):
        return None
    return run.get("totalSamples")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    rows = []
    for folder in map(pathlib.Path, sys.argv[1:]):
        manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))["files"]
        for name, info in sorted(manifest.items()):
            record_path = folder / "records" / (name + ".json")
            if not record_path.exists() or "frames" not in info:
                continue
            record = json.loads(record_path.read_text(encoding="utf-8"))
            f = info["frameSize"]
            start, end = info["startTrim"], info["endTrim"]
            if end is None:
                continue
            sizes = info["lastSizes"]
            analyzer = max(0, info["frames"] * f - start - end)
            rule = info["frames"] * f + (f - max(sizes)) // 2 - start - end if sizes else None
            dmo, reader = figure(record, "direct"), figure(record, "sourceReader")
            rows.append((name, info, dmo, reader, analyzer, rule))
    width = max((len(r[0]) for r in rows), default=10)
    print(f"{'file':{width}}  frames start  end  last-sizes       dmo   reader  analyzer  rule   dmo-analyzer dmo-rule reader-rule")
    matched = 0
    counted = 0
    for name, info, dmo, reader, analyzer, rule in rows:
        d_a = None if dmo is None else dmo - analyzer
        d_r = None if dmo is None or rule is None else dmo - rule
        r_r = None if reader is None or rule is None else reader - rule
        if d_r is not None:
            counted += 1
            matched += d_r == 0
        print(f"{name:{width}}  {info['frames']:>5} {info['startTrim']:>5} {info['endTrim']:>4}  {str(info['lastSizes']):<14} {dmo!s:>7} {reader!s:>7} {analyzer:>9} {rule!s:>7} {d_a!s:>10} {d_r!s:>8} {r_r!s:>10}")
    print(f"\ndecoder = rule in {matched} of {counted} variants with both figures")
    return 0


if __name__ == "__main__":
    sys.exit(main())
