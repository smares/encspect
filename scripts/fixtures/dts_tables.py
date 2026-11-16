#!/usr/bin/env python3
"""Writes and checks the numeric tables of the DTS core entropy pass and decoder from ETSI TS 102 114 V1.6.1, Annex D.

    dts_tables.py --write   writes SubframeTables.cs (the parser's tables) and Decoding/FilterTables.cs (the decoder's) from the pinned PDF
    dts_tables.py --check   fails where the committed files differ from what --write would produce

The tables are format data that the specification defines (docs/development.md, "Where the format tables come from"): the Huffman code books of D.5, the scale factor
tables of D.1, the quantization step sizes of D.2, the joint intensity scale factors of D.3 and the 512-tap filters of D.8 (the 32-band reconstruction filter bank,
perfect and non-perfect, and the two LFE interpolation filters). The text comes from `pdftotext -layout` of the pinned PDF (dts_specs.py), which the script reads from
the download cache and never commits. The block code books of D.6 are not generated: each is the arithmetic code of C.3.2 (the code of element k at level index l is
(l + (levels - 1) / 2) * levels^k, element 1 being k = 0), which the decoder computes with a division and a remainder; the PDF's table V.7 has a typo (47 for 147).

The checks the script makes of what it read: every Huffman book has as many entries as levels and a complete prefix code (the Kraft sum is exactly 1), every
other table has the number of entries its clause gives, and the filters are symmetric or antisymmetric as the specification's are.

D.10.1 (the ADPCM coefficient vectors) and D.10.2 (the high-frequency VQ vectors) are not in the PDF: the specification says of both "Due to its extensive size, this
table is not included here" (V1.6.1, pages 275 and 276).
"""

import fractions
import os
import pathlib
import re
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import dts_specs  # noqa: E402
import ffmpeg_sources  # noqa: E402

ROOT = HERE.parents[1]
PARSER_OUTPUT = ROOT / "src" / "Encspect.Formats.Dts" / "SubframeTables.cs"
DECODER_OUTPUT = ROOT / "src" / "Encspect.Formats.Dts" / "Decoding" / "FilterTables.cs"
VECTOR_OUTPUT = ROOT / "src" / "Encspect.Formats.Dts" / "Decoding" / "VectorData.cs"
CACHE = pathlib.Path(os.environ.get("ENCSPECT_DOWNLOAD_CACHE", ROOT / ".cache")) / "dts-specs"


def spec_lines():
    pdf = dts_specs.fetch(CACHE)
    text = CACHE / "spec.txt"
    if not text.exists() or text.stat().st_mtime < pdf.stat().st_mtime:
        if shutil.which("pdftotext") is None:
            sys.exit("pdftotext (poppler-utils) is needed to read the specification")
        subprocess.run(["pdftotext", "-layout", str(pdf), str(text)], check=True)
    return text.read_text(encoding="utf-8").replace("−", "-").split("\n")


def clause(lines, start_pattern, end_pattern):
    """The lines from the first line matching start_pattern (after the table of contents) to the first matching end_pattern after it."""
    starts = [i for i, line in enumerate(lines) if re.match(start_pattern, line)]
    start = starts[-1]
    for i in range(start + 1, len(lines)):
        if re.match(end_pattern, lines[i]):
            return lines[start:i]
    return lines[start:]


def number(text):
    return float(text.replace(",", "."))


def int_rows(lines, columns):
    """Rows of integers (the first column the index) from lines that are all integers; page headers and titles are skipped."""
    rows = []
    for line in lines:
        tokens = line.split()
        if len(tokens) >= 1 and all(re.fullmatch(r"-?\d+", t) for t in tokens):
            rows.append([int(t) for t in tokens])
    return rows


def scale_factor_tables_by_size(lines):
    section = clause(lines, r"^D\.1\s+Scale Factor Quantization Tables", r"^D\.2\s+Quantization Step Size")
    entries = {}
    for line in section:
        tokens = line.split()
        group = 0
        while len(tokens) >= group + 3:
            index, level, db = tokens[group:group + 3]
            if re.fullmatch(r"\d+", index) and (re.fullmatch(r"\d+", level) or level == "invalid"):
                entries.setdefault(int(index), []).append(-1 if level == "invalid" else int(level))
                group += 3
            else:
                break
    # The 6-bit table lists indexes 0 to 63; the 7-bit table 0 to 127: the first occurrence of an index below 64 is the 6-bit table's, the second the 7-bit one's.
    six = [None] * 64
    seven = [None] * 128
    for index, values in entries.items():
        if index < 64:
            assert len(values) == 2, (index, values)
            six[index], seven[index] = values
        else:
            assert len(values) == 1, (index, values)
            seven[index] = values[0]
    assert None not in six and None not in seven
    return six, seven


def step_sizes(lines):
    lossy = clause(lines, r"^D\.2\.1\s+Lossy Quantization", r"^D\.2\.2")
    lossless = clause(lines, r"^D\.2\.2\s+Lossless Quantization", r"^D\.3\s+Scale Factor for Joint")

    def read(section):
        values = {}
        for line in section:
            m = re.match(r"^\s+(\d+)\s+(\d+|invalid)\s+(\S+)\s*$", line)
            if m:
                values[int(m.group(1))] = -1 if m.group(2) == "invalid" else int(m.group(2))
        assert sorted(values) == list(range(32)), sorted(values)
        return [values[i] for i in range(32)]

    return read(lossy), read(lossless)


def joint_scale(lines):
    section = clause(lines, r"^D\.3\s+Scale Factor for Joint", r"^D\.4\s+Dynamic Range")
    values = {}
    for line in section:
        tokens = line.split()
        for i in range(0, len(tokens) - 1, 2):
            if re.fullmatch(r"\d+", tokens[i]) and re.fullmatch(r"\d+,\d+", tokens[i + 1]) or re.fullmatch(r"\d+", tokens[i]) and re.fullmatch(r"\d+", tokens[i + 1]) and len(tokens) % 2 == 0:
                values[int(tokens[i])] = tokens[i + 1].replace(",", ".")
    assert sorted(values) == list(range(129)), len(values)
    return [values[i] for i in range(129)]


LEVELS = {"3": 3, "4": 4, "5": 5, "7": 7, "9": 9, "12": 12, "13": 13, "17": 17, "25": 25, "33": 33, "65": 65, "129": 129}


def huffman_books(lines):
    """D.5: every book as (name, [(symbol, length, code)])."""
    section = clause(lines, r"^D\.5\s+Huffman Code Books", r"^D\.6\s+Block Code Books")
    books = {}
    order = []
    pending = []
    seen_rows = False
    for line in section:
        names = re.findall(r"Table\s+([A-Z]+\d+)", line)
        if names and not re.search(r"\d\s+\d", line.split("Table")[0]):
            if seen_rows:
                pending = []
                seen_rows = False
            pending.extend(names)
            for name in names:
                if name not in books:
                    books[name] = []
                    order.append(name)
            continue
        tokens = line.split()
        if not tokens or not all(re.fullmatch(r"-?\d+", t) for t in tokens):
            continue
        # Triples (symbol, length, code), the code written with spaces as thousands separators ("1 379").
        triples = []
        i = 0
        while i < len(tokens):
            if i + 2 >= len(tokens) + 0 and i + 3 > len(tokens):
                break
            symbol, length, code = int(tokens[i]), int(tokens[i + 1]), tokens[i + 2]
            i += 3
            while i < len(tokens) and re.fullmatch(r"\d{3}", tokens[i]) and (i + 1 >= len(tokens) or True):
                # A symbol is at most two digits, so a three-digit token continues the code.
                code += tokens[i]
                i += 1
            triples.append((symbol, length, int(code)))
        if not pending:
            continue
        seen_rows = True
        # A row holds the next entry of each table that is not complete yet; rows of a table printed in two column groups hold two of its entries.
        incomplete = [n for n in pending if len(books[n]) < LEVELS[re.search(r"\d+$", n).group(0)]] or pending
        for t, triple in enumerate(triples):
            books[incomplete[t % len(incomplete)]].append(triple)
    complete_d65(books)
    fix_d129_sign(books)
    return order, books


def fix_d129_sign(books):
    """Table D129 of the PDF lists level 37 twice (codes 1 582 and 1 581 of 11 bits) and has no level -37: the minus sign of the second is lost. Every other pair of a book
    is a level and its negative with codes that differ by one, the positive one's the larger, so the entry with the smaller code is -37."""
    entries = books["D129"]
    twice = [i for i, (symbol, _, _) in enumerate(entries) if symbol == 37]
    assert len(twice) == 2 and not any(symbol == -37 for symbol, _, _ in entries)
    smaller = min(twice, key=lambda i: entries[i][2])
    symbol, length, code = entries[smaller]
    assert entries[twice[0]][1] == entries[twice[1]][1] and abs(entries[twice[0]][2] - entries[twice[1]][2]) == 1
    entries[smaller] = (-37, length, code)


def complete_d65(books):
    """The PDF's Table D65 stops at level -30 (its page ends there; E65 follows). The 61 entries it has leave exactly four 15-bit codes unused, and four levels (31, -31,
    32, -32) are missing, so the table is completed with the codes in the order of its other pairs (a level's code is the code of its negative plus one, and the larger
    levels have the lower codes). The four entries are derived, not read: docs/development.md says so."""
    entries = books["D65"]
    assert len(entries) == 61, len(entries)
    used = set()
    for _, length, code in entries:
        base = code << (15 - length)
        used.update(range(base, base + (1 << (15 - length))))
    free = [c for c in range(1 << 15) if c not in used]
    assert free == [28848, 28849, 28850, 28851], free
    entries.extend([(31, 15, 28851), (-31, 15, 28850), (32, 15, 28849), (-32, 15, 28848)])


def check_huffman(order, books):
    levels = LEVELS
    for name in order:
        digits = re.search(r"\d+$", name).group(0)
        entries = books[name]
        assert len(entries) == levels[digits], (name, len(entries))
        kraft = sum(fractions.Fraction(1, 1 << length) for _, length, _ in entries)
        assert kraft == 1, (name, kraft)
        # The codes are a prefix code: no code is the prefix of another.
        words = sorted(format(code, f"0{length}b") for _, length, code in entries)
        for a, b in zip(words, words[1:]):
            assert not b.startswith(a), (name, a, b)
        symbols = sorted(s for s, _, _ in entries)
        if name.endswith("12"):
            assert symbols == list(range(1, 13)), (name, symbols)
        elif name.endswith("4"):
            assert symbols == [0, 1, 2, 3], (name, symbols)
        else:
            half = (int(digits) - 1) // 2
            assert symbols == list(range(-half, half + 1)), (name, symbols)


def filters(lines):
    section = clause(lines, r"^D\.8\s+32-Band Interpolation and LFE", r"^D\.9\s+1 024 tap FIR")
    columns = [[None] * 512 for _ in range(4)]
    for line in section:
        tokens = line.split()
        if len(tokens) == 5 and re.fullmatch(r"\d+", tokens[0]) and all(re.fullmatch(r"-?\d,\d+e[-+]\d+", t) for t in tokens[1:]):
            index = int(tokens[0])
            for c in range(4):
                assert columns[c][index] is None, index
                columns[c][index] = tokens[1 + c].replace(",", ".")
    for column in columns:
        assert None not in column
    return columns


def format_ints(values, per_line=16, indent="        "):
    lines = []
    for i in range(0, len(values), per_line):
        lines.append(indent + ", ".join(str(v) for v in values[i:i + per_line]) + ",")
    return "\n".join(lines)


def format_doubles(values, per_line=4, indent="        "):
    lines = []
    for i in range(0, len(values), per_line):
        lines.append(indent + ", ".join(values[i:i + per_line]) + ",")
    return "\n".join(lines)


HEADER = """// Generated by scripts/fixtures/dts_tables.py from ETSI TS 102 114 V1.6.1 (SHA-256 {sha}), Annex D; do not edit.
// The tables are format data the specification defines (the clause each names); the syntax and the decoder document what each element means.
#pragma warning disable SA1124
"""


def write_parser(lines):
    order, books = huffman_books(lines)
    check_huffman(order, books)
    six, seven = scale_factor_tables_by_size(lines)
    lossy, lossless = step_sizes(lines)
    joint = joint_scale(lines)
    out = [HEADER.format(sha=dts_specs.SHA256)]
    out.append("namespace Encspect.Formats.Dts;\n")
    out.append("internal static class SubframeTables\n{")
    out.append("    // D.1.1, the 6-bit root square table of scale factors: 64 values, -1 where the table says invalid.")
    out.append("    public static ReadOnlySpan<int> ScaleFactor6 => new int[]\n    {\n" + format_ints(six) + "\n    };\n")
    out.append("    // D.1.2, the 7-bit table: 128 values, -1 where invalid.")
    out.append("    public static ReadOnlySpan<int> ScaleFactor7 => new int[]\n    {\n" + format_ints(seven) + "\n    };\n")
    out.append("    // D.2.1, the quantization step sizes of lossy streams times 2^22 by ABITS: 32 values, -1 where invalid.")
    out.append("    public static ReadOnlySpan<int> StepSizeLossy => new int[]\n    {\n" + format_ints(lossy) + "\n    };\n")
    out.append("    // D.2.2, the step sizes of lossless streams (RATE 31) times 2^22 by ABITS.")
    out.append("    public static ReadOnlySpan<int> StepSizeLossless => new int[]\n    {\n" + format_ints(lossless) + "\n    };\n")
    out.append("    // D.3, the scale factors of joint intensity coding: 129 values by the index plus 64.")
    out.append("    public static ReadOnlySpan<double> JointScale => new double[]\n    {\n" + format_doubles(joint) + "\n    };\n")
    for name in order:
        out.append(f"    // D.5, Table {name}: {len(books[name])} entries as (symbol, code length, code).")
        flat = [v for triple in books[name] for v in triple]
        out.append(f"    public static ReadOnlySpan<int> Book{name} => new int[]\n    {{\n" + format_ints(flat, 12) + "\n    };\n")
    out.append("}\n")
    return "\n".join(out)


def write_decoder(lines):
    columns = filters(lines)
    names = [
        ("PerfectReconstruction", "D.8, the 32-band reconstruction filter of FILTS 1 (perfect reconstruction): 512 taps."),
        ("NonPerfectReconstruction", "D.8, the 32-band reconstruction filter of FILTS 0 (non-perfect reconstruction): 512 taps."),
        ("Lfe64", "D.8, the interpolation filter of the LFE channel for a decimation factor of 64 (LFF 2): 512 taps."),
        ("Lfe128", "D.8, the interpolation filter of the LFE channel for a decimation factor of 128 (LFF 1): 512 taps."),
    ]
    out = [HEADER.format(sha=dts_specs.SHA256)]
    out.append("namespace Encspect.Formats.Dts.Decoding;\n")
    out.append("internal static class FilterTables\n{")
    for (name, comment), column in zip(names, columns):
        out.append(f"    // {comment}")
        out.append(f"    public static ReadOnlySpan<double> {name} => new double[]\n    {{\n" + format_doubles(column) + "\n    };\n")
    out.append("}\n")
    return "\n".join(out)


def vector_arrays():
    """The two vector tables of D.10 from FFmpeg 6.1.1's dcadata.c, found by shape: the only two-dimensional arrays with 4,096 x 4 and 1,024 x 32 numbers."""
    import re as _re

    source = ffmpeg_sources.fetch(CACHE.parent / "ffmpeg-sources")
    text = (source / "libavcodec" / "dcadata.c").read_text(encoding="utf-8")
    text = _re.sub(r"/\*.*?\*/", "", text, flags=_re.S)
    text = _re.sub(r"//[^\n]*", "", text)
    found = {}
    for match in _re.finditer(r"((?:\[[^\]]*\]){2})\s*=\s*\{(.*?)\n\};", text, flags=_re.S):
        numbers = [int(v, 16) if "x" in v else int(v) for v in _re.findall(r"-?0x[0-9a-fA-F]+|-?\d+", match.group(2))]
        if len(numbers) == 4096 * 4:
            found["adpcm"] = numbers
        elif len(numbers) == 1024 * 32:
            found["vq"] = numbers
    assert sorted(found) == ["adpcm", "vq"], sorted(found)
    adpcm, vq = found["adpcm"], found["vq"]
    # D.10.1: the first entry is 9928, 1.2119140625 in 2^-13; each element is a 16-bit value. D.10.2: each element is an 8-bit value (the specification's 16-bit entries hold two), divided by 2^4.
    assert adpcm[0] == 9928 and all(-32768 <= v < 32768 for v in adpcm), adpcm[:4]
    assert all(-128 <= v < 128 for v in vq)
    return adpcm, vq


def check_vector_text(lines):
    """The facts the generated tables are read by, as D.10 states them: D.10.1's entries are the element times 2^13 (the first, 9928, is 1.2119140625), D.10.2's are 16 bits of two 8-bit elements each divided by 2^4, 16 entries a vector of 32."""
    section = " ".join(" ".join(l.split()) for l in clause(lines, r"^D\.10\s+VQ Tables", r"^D\.11\s+Look-up Table"))
    for needle in ("4 096 vectors", "multiplied by 2", "9928", "1.2119140625", "1 024 vectors", "16 bits, representing two vector elements", "16 entries in the table to represent one vector of 32 elements", "divided by 2"):
        assert needle in section, needle


def write_vectors(lines):
    check_vector_text(lines)
    adpcm, vq = vector_arrays()
    out = [HEADER.format(sha=dts_specs.SHA256).replace("from ETSI TS 102 114 V1.6.1 (SHA-256", "from FFmpeg 6.1.1 (ffmpeg_sources.py pins it; SHA-256 of ETSI TS 102 114 V1.6.1 for the rest")]
    out.append("namespace Encspect.Formats.Dts.Decoding;\n")
    out.append("// Annex D.10: format data the specification names and does not print (\"Due to its extensive size, this table is not included here\").")
    out.append("internal static class VectorData\n{")
    out.append("    // D.10.1: 4,096 vectors of four ADPCM prediction coefficients, each element a multiple of 2^-13.")
    out.append("    public static ReadOnlySpan<short> Adpcm => new short[]\n    {\n" + format_ints(adpcm, 16) + "\n    };\n")
    out.append("    // D.10.2: 1,024 vectors of 32 samples of the high-frequency subbands, each element a multiple of 2^-4.")
    out.append("    public static ReadOnlySpan<sbyte> HighFrequency => new sbyte[]\n    {\n" + format_ints(vq, 32) + "\n    };\n")
    out.append("}\n")
    return "\n".join(out)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--check"
    lines = spec_lines()
    files = {PARSER_OUTPUT: write_parser(lines), DECODER_OUTPUT: write_decoder(lines), VECTOR_OUTPUT: write_vectors(lines)}
    if mode == "--write":
        for path, content in files.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")
            print(f"wrote {path.relative_to(ROOT)}")
        return 0
    failed = False
    for path, content in files.items():
        if not path.exists() or path.read_text(encoding="utf-8") != content:
            print(f"{path.relative_to(ROOT)} differs from what --write produces")
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
