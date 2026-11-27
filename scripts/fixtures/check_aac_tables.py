#!/usr/bin/env python3
"""Checks the AAC tables the analyzer and the bitstream model read with (ISO/IEC 14496-3 Annex 4.A and Tables 4.129 to 4.147).

Always, with the standard library only:

- src/Encspect.Formats.Aac/HuffmanData.cs holds exactly the codewords, lengths and band offsets of aac_tables.py, so the C# parser and the
  Python model that writes the hand-built fixtures read the same tables;
- every codebook is a complete prefix code: no codeword is a prefix of another, and the Kraft sum of the lengths is exactly 1, as for
  every Huffman code of the specification;
- a few entries the specification's tables give, such as the one-bit codeword 0 for a scalefactor difference of 0.

With --spec FILE (the text of ISO/IEC 14496-3:2009, a private copy that is neither committed nor quoted; the default is
.cache/spec/14496-3.txt where that exists), the tables are compared with the specification's own:

- the scalefactor codebook (Table 4.A.1), the eleven spectral codebooks (Tables 4.A.2 to 4.A.12) and the ten SBR envelope and noise
  floor codebooks (Tables 4.A.79 to 4.A.88): every index, length and codeword;
- the scalefactor band offsets and counts for 1024 and 128 lines, and for 960 and 120 (the values in brackets), of Tables 4.129 to 4.141;
- the start frequency offsets of SBR (4.6.18.3.2.1).

PRED_SFB_MAX per sampling frequency is not in that text (it gives only the constant, in 4.5.2.3 and the syntax of the prediction data), so
it is compared with nothing here.

    python3 scripts/fixtures/check_aac_tables.py [--spec FILE]
"""

import pathlib
import re
import sys

import aac_tables as T

ROOT = pathlib.Path(__file__).resolve().parents[2]
CS = ROOT / "src" / "Encspect.Formats.Aac" / "HuffmanData.cs"

SBR_NAMES = list(T.SBR_HUFFMAN)


def cs_arrays(text):
    arrays = {}
    for match in re.finditer(r"public static readonly \w+\[\]\[?\]? (\w+) =\s*\[(.*?)\];", text, re.S):
        body = match.group(2)
        if "[" in body:
            arrays[match.group(1)] = [[int(v, 0) for v in re.findall(r"-?0x[0-9A-F]+|-?\d+", row)] for row in re.findall(r"\[([^\]]*)\]", body)]
        else:
            arrays[match.group(1)] = [int(v, 0) for v in re.findall(r"-?0x[0-9A-F]+|-?\d+", body)]
    return arrays


def pascal(name):
    return "".join(p.capitalize() for p in name.replace("1_5dB", "15").replace("3_0dB", "30").split("_"))


def all_tables():
    """(name, codes, lengths, values) for every codebook; values give what each index decodes to."""
    out = []
    for n, (codes, bits) in enumerate(T.SPECTRAL, 1):
        out.append((f"spectral codebook {n}", codes, bits, [spectral_values(n, i) for i in range(len(codes))]))
    out.append(("scalefactor codebook", T.SCALEFACTOR[0], T.SCALEFACTOR[1], [(i - 60,) for i in range(121)]))
    for name in SBR_NAMES:
        codes, bits = T.SBR_HUFFMAN[name]
        lav = (len(codes) - 1) // 2
        out.append((name, codes, bits, [(i - lav,) for i in range(len(codes))]))
    return out


def spectral_values(n, index):
    """What index stands for in codebook n (ISO/IEC 14496-3 4.6.3.3): signed or unsigned quadruples or pairs."""
    if n <= 4:
        signed = n <= 2
        digits = [(index // 27) % 3, (index // 9) % 3, (index // 3) % 3, index % 3]
        return tuple(d - 1 if signed else d for d in digits)
    if n <= 6:
        return ((index // 9) - 4, (index % 9) - 4)
    modulo = {7: 8, 8: 8, 9: 13, 10: 13, 11: 17}[n]
    return (index // modulo, index % modulo)


def check_codes(problems):
    for name, codes, bits, _ in all_tables():
        total = sum(2 ** (max(bits) - b) for b in bits)
        if total != 2 ** max(bits):
            problems.append(f"{name}: the Kraft sum is {total / 2 ** max(bits)}, not 1")
        words = sorted((format(c, f"0{b}b"), i) for i, (c, b) in enumerate(zip(codes, bits)))
        for (a, i), (b, j) in zip(words, words[1:]):
            if b.startswith(a):
                problems.append(f"{name}: codeword {i} ({a}) is a prefix of codeword {j} ({b})")
        for i, (c, b) in enumerate(zip(codes, bits)):
            if c >= 1 << b:
                problems.append(f"{name}: codeword {i} does not fit its {b} bits")


# Entries as the specification's tables print them: (table, index, length, codeword).
SPEC_ENTRIES = [
    ("scalefactor", 60, 1, 0x0),      # Table 4.A.1: difference 0
    ("scalefactor", 59, 3, 0x4),      # difference -1
    ("scalefactor", 61, 4, 0xA),      # difference +1
    ("spectral 1", 40, 1, 0x0),       # Table 4.A.2: (0, 0, 0, 0)
    ("spectral 2", 40, 3, 0x0),       # Table 4.A.3: (0, 0, 0, 0)
    ("spectral 3", 0, 1, 0x0),        # Table 4.A.4: (0, 0, 0, 0)
    ("spectral 11", 0, 4, 0x0),       # Table 4.A.12: (0, 0)
    ("spectral 11", 288, 5, 0x4),     # (16, 16), the escape pair
]


def check_spec_entries(problems):
    for table, index, length, code in SPEC_ENTRIES:
        codes, bits = T.SCALEFACTOR if table == "scalefactor" else T.SPECTRAL[int(table.split()[1]) - 1]
        if (bits[index], codes[index]) != (length, code):
            problems.append(f"{table} index {index}: {bits[index]} bits, 0x{codes[index]:X}; the specification gives {length} bits, 0x{code:X}")


def check_cs(problems):
    arrays = cs_arrays(CS.read_text(encoding="utf-8"))
    expected = {}
    for n, (codes, bits) in enumerate(T.SPECTRAL, 1):
        expected[f"Codes{n}"] = codes
        expected[f"Bits{n}"] = bits
    expected["ScalefactorCodes"], expected["ScalefactorBits"] = T.SCALEFACTOR
    for name in SBR_NAMES:
        expected[pascal(name) + "Codes"], expected[pascal(name) + "Bits"] = T.SBR_HUFFMAN[name]
    expected["SbrOffset"] = T.SBR_OFFSET
    expected["Long"], expected["Short"], expected["Long960"], expected["Short120"] = T.SWB_LONG, T.SWB_SHORT, T.SWB_LONG_960, T.SWB_SHORT_120
    expected["NumLong"], expected["NumShort"], expected["NumLong960"], expected["NumShort120"] = T.NUM_SWB_LONG, T.NUM_SWB_SHORT, T.NUM_SWB_LONG_960, T.NUM_SWB_SHORT_120
    expected["PredictionMax"] = T.PRED_SFB_MAX
    for name, values in expected.items():
        if arrays.get(name) != values:
            problems.append(f"HuffmanData.cs: {name} differs from aac_tables.py")


SPEC = ROOT / ".cache" / "spec" / "14496-3.txt"

# The rows of the specification's tables: the sampling frequency indexes each table serves, by long (1024 lines, 960 in brackets)
# and short (128 lines, 120 in brackets) windows. Indexes 11 and 12 (8 and 7.35 kHz) share the 8 kHz tables.
LONG_TABLES = {0: "4.140", 1: "4.140", 2: "4.138", 3: "4.129", 4: "4.129", 5: "4.131", 6: "4.136", 7: "4.136", 8: "4.134", 9: "4.134", 10: "4.134", 11: "4.132", 12: "4.132"}
SHORT_TABLES = {0: "4.141", 1: "4.141", 2: "4.139", 3: "4.130", 4: "4.130", 5: "4.130", 6: "4.137", 7: "4.137", 8: "4.135", 9: "4.135", 10: "4.135", 11: "4.133", 12: "4.133"}
SBR_TABLES = dict(zip(SBR_NAMES, ["4.A.79", "4.A.80", "4.A.81", "4.A.82", "4.A.83", "4.A.84", "4.A.85", "4.A.86", "4.A.87", "4.A.88"]))


def spec_section(lines, start, end):
    first = next(i for i, line in enumerate(lines) if re.search(start, line))
    last = next(i for i in range(first + 1, len(lines)) if re.search(end, lines[i]))
    return lines[first:last]


def compare_rows(problems, name, rows, codes, bits):
    if sorted(rows) != list(range(len(codes))):
        problems.append(f"{name}: the specification lists {len(rows)} entries, the table {len(codes)}")
        return
    for index, (length, code) in rows.items():
        if (bits[index], codes[index]) != (length, code):
            problems.append(f"{name} index {index}: {bits[index]} bits, 0x{codes[index]:X}; the specification gives {length} bits, 0x{code:X}")


def spec_codebook(lines, number, hexadecimal):
    """Rows of 'index length codeword', two to a line, of Table 4.A.<number>; the SBR tables print length and codeword as 0x values."""
    word = r"(0x[0-9A-Fa-f]+)" if hexadecimal else r"([0-9a-fA-F]+)"
    pattern = re.compile(r"(\d+)\s+" + (word if hexadecimal else r"(\d+)") + r"\s+" + word)
    section = spec_section(lines, rf"Table 4\.A\.{number} [–-] ", r"Table 4\.A\.\d+ [–-] |^\s*4\.A\.\d")
    rows = {}
    for line in section[1:]:
        text = line.strip()
        if re.fullmatch(r"(?:\d+\s+\S+\s+\S+\s*){1,2}", text):
            for match in re.finditer(r"(\d+)\s+(\S+)\s+(\S+)", text):
                index, length, code = match.groups()
                rows[int(index)] = (int(length, 0) if hexadecimal else int(length), int(code, 16))
    return rows


def spec_offsets(lines, number):
    """The scalefactor band offsets of a Table 4.1xx, two 'swb offset' pairs to a line: swb -> (offset, offset in brackets or None).
    The last line ends with the window length alone, as '1024 (960)'."""
    section = spec_section(lines, rf"Table {re.escape(number)} [–-] scalefactor bands", r"Table 4\.\d+ [–-] ")
    token = r"\d+(?:\s*\((?:\d+|-)\))?"
    offsets = {}
    end = None
    for line in section[1:]:
        text = line.strip()
        if not re.fullmatch(rf"(?:{token}\s+){{0,3}}{token}", text):
            continue
        tokens = re.findall(token, text)
        if len(tokens) == 1 and "(" not in tokens[0]:
            continue
        if len(tokens) % 2:
            end = tokens.pop()
        for k in range(0, len(tokens), 2):
            offsets[int(tokens[k])] = tokens[k + 1]
    if end is not None:
        offsets[max(offsets) + 1] = end
    parsed = {}
    for swb, value in offsets.items():
        match = re.fullmatch(r"(\d+)(?:\s*\((\d+|-)\))?", value)
        parsed[swb] = (int(match.group(1)), int(match.group(2)) if match.group(2) and match.group(2) != "-" else (0 if match.group(2) else None))
    return parsed


def check_specification(path, problems):
    lines = path.read_text(encoding="utf-8", errors="replace").split("\n")
    compare_rows(problems, "scalefactor codebook (Table 4.A.1)", spec_codebook(lines, 1, False), *T.SCALEFACTOR)
    for n, (codes, bits) in enumerate(T.SPECTRAL, 1):
        compare_rows(problems, f"spectral codebook {n} (Table 4.A.{n + 1})", spec_codebook(lines, n + 1, False), codes, bits)
    for name, table in SBR_TABLES.items():
        codes, bits = T.SBR_HUFFMAN[name]
        rows = spec_codebook(lines, table.split(".")[-1], True)
        compare_rows(problems, f"{name} (Table {table})", rows, codes, bits)
    for index in range(13):
        for table, offsets, counts, offsets960, counts960 in ((LONG_TABLES[index], T.SWB_LONG[index], T.NUM_SWB_LONG[index], T.SWB_LONG_960[index], T.NUM_SWB_LONG_960[index]),
                                                           (SHORT_TABLES[index], T.SWB_SHORT[index], T.NUM_SWB_SHORT[index], T.SWB_SHORT_120[index], T.NUM_SWB_SHORT_120[index])):
            rows = spec_offsets(lines, table)
            if [rows.get(i, (None,))[0] for i in range(len(offsets))] != list(offsets) or len(offsets) - 1 != counts:
                problems.append(f"sampling frequency index {index}: offsets differ from Table {table}")
            # A value in brackets replaces the offset for 960 (or 120) lines, and "(-)" leaves the offset out.
            expected = [rows[i][1] if rows[i][1] else rows[i][0] for i in range(len(offsets)) if rows[i][1] != 0]
            if expected != list(offsets960) or len(offsets960) - 1 != counts960:
                problems.append(f"sampling frequency index {index}: offsets for 960 or 120 lines differ from Table {table}")
    start = next(i for i, line in enumerate(lines) if "k0 = startMin" in line)
    block = " ".join(lines[start:start + 14])
    found = [[int(v) for v in re.findall(r"-?\d+", row)] for row in re.findall(r"\[([^\]]*)\]", block)]
    found = [row for row in found if len(row) == 16]
    if found != T.SBR_OFFSET:
        problems.append(f"SBR_OFFSET differs from 4.6.18.3.2.1: the specification gives {found}")


def main(arguments):
    problems = []
    check_codes(problems)
    check_spec_entries(problems)
    check_cs(problems)
    checked = "the C# copy, completeness and prefix freedom, and entries the specification prints"
    spec = pathlib.Path(arguments[arguments.index("--spec") + 1]) if "--spec" in arguments else SPEC
    if "--spec" in arguments or spec.exists():
        check_specification(spec, problems)
        checked += "; every codebook, the band offsets and the SBR start offsets against ISO/IEC 14496-3:2009"
    for problem in problems:
        print(problem)
    print(f"Checked {checked}: {len(problems)} problems.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
