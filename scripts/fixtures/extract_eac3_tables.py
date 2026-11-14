#!/usr/bin/env python3
"""Extracts the specification data of E-AC-3's decoder from the text of ETSI TS 102 366 V1.4.1 (the file `ac3_specs.py` downloads) into
src/Encspect.Formats.Ac3/Decoding/AhtTables.cs: the vectors of the adaptive hybrid transform's vector quantizer (Tables E.3.1 to E.3.7, 956 vectors of six 16-bit values), the constants
that remap large mantissas (Table E.2.6) and the attenuation of spectral extension's notch filter (Table E.2.12). ATSC A/52:2018 prints the vectors in its Tables E4.1 to E4.7 identically;
--check compares both with the extraction.

    python3 scripts/fixtures/extract_eac3_tables.py SPEC.pdf [--atsc A52-2018.pdf] [--check]

Needs pdftotext (poppler). Without --check the file is written.
"""

import argparse
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
TARGET = ROOT / "src" / "Encspect.Formats.Ac3" / "Decoding" / "AhtTables.cs"


def text_of(pdf):
    return subprocess.run(["pdftotext", "-layout", str(pdf), "-"], capture_output=True, text=True, check=True).stdout.split("\n")


def vectors(lines, marker):
    tables, current = {}, None
    for line in lines:
        header = re.search(marker, line)
        if header:
            current = int(header.group(1))
            tables[current] = {}
            continue
        row = re.match(r"\s*(\d+)\s+((?:0x[0-9a-fA-F]{4}\s*){6})\s*$", line)
        if row and current is not None:
            tables[current][int(row.group(1))] = [int(v, 16) for v in row.group(2).split()]
    for hebap, size in zip(range(1, 8), [4, 8, 16, 32, 128, 256, 512]):
        assert sorted(tables[hebap]) == list(range(size)), f"hebap {hebap} has {len(tables[hebap])} vectors, expected {size}"
    return {h: [tables[h][i] for i in range(len(tables[h]))] for h in range(1, 8)}


def signed(value):
    return value - 65536 if value >= 0x8000 else value


def build(etsi):
    lines = text_of(etsi)
    start = next(i for i, l in enumerate(lines) if "E.3         AHT vector quantization tables" in l or l.strip().startswith("E.3         AHT vector"))
    tables = vectors(lines[start:], r"Table E\.3\.\d: VQ table for hebap (\d)")
    attenuation = {}
    at = next(i for i, l in enumerate(lines) if "Table E.2.12: Spectral extension attenuation table" in l)
    for line in lines[at:at + 60]:
        m = re.match(r"\s*(\d+)\s+(\d,\d+)\s+(\d,\d+)\s+(\d,\d+)\s*$", line)
        if m:
            attenuation[int(m.group(1))] = [m.group(i).replace(",", ".") for i in (2, 3, 4)]
    assert sorted(attenuation) == list(range(32))
    remap, current = {}, None
    at = next(i for i, l in enumerate(lines) if "Table E.2.6: Large mantissa" in l)
    for line in lines[at:at + 80]:
        m = re.match(r"\s*(\d+)\s+x\s*[≥<]\s*0\s+(.*)$", line)
        if m:
            current = int(m.group(1))
            remap.setdefault(current, {})["pos" if "≥" in line else "neg"] = m.group(2).split()
            continue
        m = re.match(r"\s*x\s*[≥<]\s*0\s+(.*)$", line)
        if m and current is not None:
            remap[current]["pos" if "≥" in line else "neg"] = m.group(1).split()
    assert sorted(remap) == list(range(8, 20))

    def constant(token):
        return 0 if token == "N/A" else signed(int(token, 16))

    out = [
        "namespace Encspect.Formats.Ac3.Decoding;",
        "",
        "// Format data of ETSI TS 102 366 V1.4.1 Annex E, extracted from the specification's text by scripts/fixtures/extract_eac3_tables.py (which checks the committed file against a fresh extraction):",
        "// Tables E.3.1 to E.3.7, the vectors of the adaptive hybrid transform's vector quantizer, Table E.2.6, the remapping constants of its gain adaptive quantizer, and Table E.2.12, the attenuation of spectral extension's notch filter.",
        "internal static class AhtTables",
        "{",
        "    // The vectors of hebap 1 to 7 (Tables E.3.1 to E.3.7: 4, 8, 16, 32, 128, 256 and 512 vectors), as signed 16-bit values, six to a vector.",
        "    public static readonly short[][] Vectors =",
        "    [",
        "        [],",
    ]
    for hebap in range(1, 8):
        out.append("        [")
        for row in tables[hebap]:
            out.append("            " + ", ".join(str(signed(v)) for v in row) + ",")
        out.append("        ],")
    out += ["    ];", "",
            "    // Table E.2.12: the attenuation of the notch filter by spxattencod (rows) and distance from its center (columns 0 to 2).",
            "    public static readonly float[][] Attenuation =", "    ["]
    out += [f"        [{', '.join(a + 'f' for a in attenuation[i])}]," for i in range(32)]
    out += ["    ];", "",
            "    // Table E.2.6, the constants of the remapping y = x + a x + b of the large mantissas, as 16-bit fractions (divide by 32768), by hebap 8 to 19 (index hebap - 8): a for the gain 1, then for the gain 2 a, b where x is 0 or",
            "    // more, b where it is negative, then the same three for the gain 4 (zero where the table has no entry).",
            "    public static readonly short[][] Remap =", "    ["]
    for hebap in range(8, 20):
        p, n = remap[hebap]["pos"], remap[hebap]["neg"]
        out.append("        [" + ", ".join(str(constant(t)) for t in (p[0], p[2], p[3], n[3], p[4], p[5], n[5])) + "],")
    out += ["    ];", "}", ""]
    return "\n".join(out), tables


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("etsi", type=pathlib.Path)
    parser.add_argument("--atsc", type=pathlib.Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    code, tables = build(args.etsi)
    if args.atsc:
        other = vectors(text_of(args.atsc), r"Table E4\.\d+\s+VQ Table for hebap (\d)")
        if other != tables:
            print("ATSC A/52:2018's vectors differ from ETSI's", file=sys.stderr)
            return 1
    if args.check:
        if TARGET.read_text(encoding="utf-8") != code:
            print(f"{TARGET} differs from the extraction", file=sys.stderr)
            return 1
        print("AhtTables.cs equals the extraction")
        return 0
    TARGET.write_text(code, encoding="utf-8")
    print(f"wrote {TARGET}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
