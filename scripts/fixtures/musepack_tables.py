#!/usr/bin/env python3
"""Writes and checks the numeric tables of the Musepack entropy pass and decoder from libmpcdec's BSD sources.

    musepack_tables.py --write   writes MusepackTables.cs (the parser's tables) and MusepackSynthesisTables.cs (the decoder's) from the pinned archives
    musepack_tables.py --check   fails where the committed files differ from what --write would produce

The tables are format data (the Huffman codes of the SV4 to SV8 syntax, the enumerative coding tables of SV8, the requantization coefficients and the synthesis
window) that libmpcdec defines and that have no other public source (docs/development.md, "Where the format tables come from"); each generated table names its source
file and C name. Sources: libmpcdec of musepack_src r495 (huffman.c, mpc_bits_reader.c, requant.c, synth_filter.c) for SV7 and SV8, and libmpcdec 1.2.6 (huffsv46.c,
mpc_decoder.c) for SV4 to SV6, both BSD-3-Clause (THIRD-PARTY-NOTICES.md). Only files under libmpcdec/ are read. The check that a table is right is the comparison
of the decoder's PCM with libmpcdec's (scripts/fixtures/crosscheck_musepack_pcm.py) and the walks' agreement with mpcdec_frames.
"""

import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import musepack_tool  # noqa: E402

ROOT = HERE.parents[1]
PARSER_OUTPUT = ROOT / "src" / "Encspect.Formats.Musepack" / "MusepackTables.cs"
DECODER_OUTPUT = ROOT / "src" / "Encspect.Formats.Musepack" / "Decoding" / "SynthesisTables.cs"
CACHE = ROOT / ".cache" / "musepack-tables"

R495 = "r495"
V126 = "1.2.6"

# (source, file, C name, kind, C# name, class). Kinds: "huff" (entries {code, length, value}, flattened to triples; a second dimension is split into one table each),
# "huff32" (the same with 32-bit codes of libmpcdec 1.2.6), "int8", "uint8", "int" (any integer), "float" (float literals).
HUFFMAN = "libmpcdec/huffman.c"
TABLES = [
    (R495, HUFFMAN, "mpc_table_HuffHdr", "huff", "Sv7Header"),
    (R495, HUFFMAN, "mpc_table_HuffSCFI", "huff", "Sv7Scfi"),
    (R495, HUFFMAN, "mpc_table_HuffDSCF", "huff", "Sv7Dscf"),
    (R495, HUFFMAN, "mpc_table_HuffQ1", "huff", "Sv7Q1"),
    (R495, HUFFMAN, "mpc_table_HuffQ2", "huff", "Sv7Q2"),
    (R495, HUFFMAN, "mpc_table_HuffQ3", "huff", "Sv7Q3"),
    (R495, HUFFMAN, "mpc_table_HuffQ4", "huff", "Sv7Q4"),
    (R495, HUFFMAN, "mpc_table_HuffQ5", "huff", "Sv7Q5"),
    (R495, HUFFMAN, "mpc_table_HuffQ6", "huff", "Sv7Q6"),
    (R495, HUFFMAN, "mpc_table_HuffQ7", "huff", "Sv7Q7"),
    (R495, HUFFMAN, "mpc_huff_SCFI_1", "huff", "Sv8Scfi1"),
    (R495, HUFFMAN, "mpc_sym_SCFI_1", "int8", "Sv8Scfi1Symbols"),
    (R495, HUFFMAN, "mpc_huff_SCFI_2", "huff", "Sv8Scfi2"),
    (R495, HUFFMAN, "mpc_sym_SCFI_2", "int8", "Sv8Scfi2Symbols"),
    (R495, HUFFMAN, "mpc_huff_DSCF_1", "huff", "Sv8Dscf1"),
    (R495, HUFFMAN, "mpc_sym_DSCF_1", "int8", "Sv8Dscf1Symbols"),
    (R495, HUFFMAN, "mpc_huff_DSCF_2", "huff", "Sv8Dscf2"),
    (R495, HUFFMAN, "mpc_sym_DSCF_2", "int8", "Sv8Dscf2Symbols"),
    (R495, HUFFMAN, "mpc_huff_Bands", "huff", "Sv8Bands"),
    (R495, HUFFMAN, "mpc_sym_Bands", "int8", "Sv8BandsSymbols"),
    (R495, HUFFMAN, "mpc_huff_Res_1", "huff", "Sv8Res1"),
    (R495, HUFFMAN, "mpc_sym_Res_1", "int8", "Sv8Res1Symbols"),
    (R495, HUFFMAN, "mpc_huff_Res_2", "huff", "Sv8Res2"),
    (R495, HUFFMAN, "mpc_sym_Res_2", "int8", "Sv8Res2Symbols"),
    (R495, HUFFMAN, "mpc_huff_Q1", "huff", "Sv8Q1"),
    (R495, HUFFMAN, "mpc_sym_Q1", "int8", "Sv8Q1Symbols"),
    (R495, HUFFMAN, "mpc_huff_Q2_1", "huff", "Sv8Q2A"),
    (R495, HUFFMAN, "mpc_sym_Q2_1", "int8", "Sv8Q2ASymbols"),
    (R495, HUFFMAN, "mpc_huff_Q2_2", "huff", "Sv8Q2B"),
    (R495, HUFFMAN, "mpc_sym_Q2_2", "int8", "Sv8Q2BSymbols"),
    (R495, HUFFMAN, "mpc_huff_Q3", "huff", "Sv8Q3"),
    (R495, HUFFMAN, "mpc_sym_Q3", "int8", "Sv8Q3Symbols"),
    (R495, HUFFMAN, "mpc_huff_Q4", "huff", "Sv8Q4"),
    (R495, HUFFMAN, "mpc_sym_Q4", "int8", "Sv8Q4Symbols"),
    (R495, HUFFMAN, "mpc_huff_Q5_1", "huff", "Sv8Q5A"),
    (R495, HUFFMAN, "mpc_sym_Q5_1", "int8", "Sv8Q5ASymbols"),
    (R495, HUFFMAN, "mpc_huff_Q5_2", "huff", "Sv8Q5B"),
    (R495, HUFFMAN, "mpc_sym_Q5_2", "int8", "Sv8Q5BSymbols"),
    (R495, HUFFMAN, "mpc_huff_Q6_1", "huff", "Sv8Q6A"),
    (R495, HUFFMAN, "mpc_sym_Q6_1", "int8", "Sv8Q6ASymbols"),
    (R495, HUFFMAN, "mpc_huff_Q6_2", "huff", "Sv8Q6B"),
    (R495, HUFFMAN, "mpc_sym_Q6_2", "int8", "Sv8Q6BSymbols"),
    (R495, HUFFMAN, "mpc_huff_Q7_1", "huff", "Sv8Q7A"),
    (R495, HUFFMAN, "mpc_sym_Q7_1", "int8", "Sv8Q7ASymbols"),
    (R495, HUFFMAN, "mpc_huff_Q7_2", "huff", "Sv8Q7B"),
    (R495, HUFFMAN, "mpc_sym_Q7_2", "int8", "Sv8Q7BSymbols"),
    (R495, HUFFMAN, "mpc_huff_Q8_1", "huff", "Sv8Q8A"),
    (R495, HUFFMAN, "mpc_sym_Q8_1", "int8", "Sv8Q8ASymbols"),
    (R495, HUFFMAN, "mpc_huff_Q8_2", "huff", "Sv8Q8B"),
    (R495, HUFFMAN, "mpc_sym_Q8_2", "int8", "Sv8Q8BSymbols"),
    (R495, HUFFMAN, "mpc_huff_Q9up", "huff", "Sv8Q9Up"),
    (R495, HUFFMAN, "mpc_sym_Q9up", "int8", "Sv8Q9UpSymbols"),
    (R495, "libmpcdec/mpc_bits_reader.c", "Cnk", "int", "EnumerationCounts"),
    (R495, "libmpcdec/mpc_bits_reader.c", "Cnk_len", "int", "EnumerationLengths"),
    (R495, "libmpcdec/mpc_bits_reader.c", "Cnk_lost", "int", "EnumerationLost"),
    (R495, "libmpcdec/mpc_bits_reader.c", "log2", "int", "LogLengths"),
    (R495, "libmpcdec/mpc_bits_reader.c", "log2_lost", "int", "LogLost"),
    (R495, "libmpcdec/requant.c", "Res_bit", "int", "ResolutionBits"),
    (R495, "libmpcdec/requant.c", "__Dc", "int", "Offsets"),
    (V126, "src/huffsv46.c", "mpc_table_SCFI_Bundle", "huff32", "Sv6ScfiBundle"),
    (V126, "src/huffsv46.c", "mpc_table_DSCF_Entropie", "huff32", "Sv6Dscf"),
    (V126, "src/huffsv46.c", "mpc_table_Region_A", "huff32", "Sv6RegionA"),
    (V126, "src/huffsv46.c", "mpc_table_Region_B", "huff32", "Sv6RegionB"),
    (V126, "src/huffsv46.c", "mpc_table_Region_C", "huff32", "Sv6RegionC"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_1", "huff32", "Sv6Samples1"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_2", "huff32", "Sv6Samples2"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_3", "huff32", "Sv6Samples3"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_4", "huff32", "Sv6Samples4"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_5", "huff32", "Sv6Samples5"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_6", "huff32", "Sv6Samples6"),
    (V126, "src/huffsv46.c", "mpc_table_Entropie_7", "huff32", "Sv6Samples7"),
    (V126, "src/mpc_decoder.c", "Q_res", "int", "Sv6Resolutions"),
]

# The decoder's tables: the requantization coefficients (float literals) and the synthesis window (integers over 65,536).
DECODER_TABLES = [
    (R495, "libmpcdec/requant.c", "__Cc", "float", "Coefficients"),
    (R495, "libmpcdec/synth_filter.c", "Di_opt", "window", "Window"),
]


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def sources():
    """The extracted source trees: {R495: path, V126: path}."""
    CACHE.mkdir(parents=True, exist_ok=True)
    roots = {}
    for key, spec, name in ((R495, musepack_tool.LIBMPC, "musepack_src_r495"), (V126, musepack_tool.LIBMPCDEC_126, "libmpcdec-1.2.6")):
        directory = CACHE / key
        directory.mkdir(parents=True, exist_ok=True)
        if not (directory / name).exists():
            musepack_tool.extract(musepack_tool.fetch(spec, directory), directory)
        roots[key] = directory / name
    return roots


def match_braces(text, start):
    """The text of the brace block whose opening brace is at start, braces included."""
    depth = 0
    for index in range(start, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return text[start:index + 1]
    raise ValueError("unbalanced braces")


def find_array(text, name):
    """(dimensions, initializer text) of the C array called name."""
    match = re.search(r"\b" + re.escape(name) + r"\s*((?:\[[^\]]*\]\s*)+)=\s*\{", text)
    if match is None:
        raise ValueError(f"no array {name}")
    dims = [int(d) if d.strip().isdigit() else d.strip() for d in re.findall(r"\[([^\]]*)\]", match.group(1))]
    return dims, match_braces(text, match.end() - 1)


def numbers(initializer, pattern):
    return re.findall(pattern, initializer)


def parse_number(token):
    token = token.strip().rstrip("uU")
    return int(token, 16) if token.lower().startswith(("0x", "-0x")) else int(token)


def emit_ints(values, per_line):
    lines = []
    for i in range(0, len(values), per_line):
        lines.append("        " + ", ".join(str(v) for v in values[i:i + per_line]) + ",")
    return "\n".join(lines)


def table(kind, text, name):
    """(count, element type, C# initializer lines) of one table, or a list of them for a table with an outer dimension."""
    dims, init = find_array(text, name)
    if kind in ("huff", "huff32"):
        entries = re.findall(r"\{\s*(0x[0-9A-Fa-f]+|\d+)u?\s*,\s*(\d+)\s*,\s*(-?\d+)\s*\}", init)
        flat = []
        for code, length, value in entries:
            flat += [parse_number(code), int(length), int(value)]
        per_table = len(flat)
        count = 1
        if len(dims) == 2:
            count = int(dims[0]) if isinstance(dims[0], int) else 2
            per_table = len(flat) // count
        return [flat[i * per_table:(i + 1) * per_table] for i in range(count)], 3
    if kind == "window":
        values = [parse_number(t) for t in re.findall(r"_\(\s*(-?\d+)\s*\)", init)]
        return [values], 16
    if kind == "float":
        values = re.findall(r"_\(\s*(-?[0-9.]+f)\s*\)", init)
        return [values], 1
    values = [parse_number(t) for t in re.findall(r"-?(?:0x[0-9A-Fa-f]+|\d+)u?", init)]
    # C zero-fills what an initializer leaves out (the last row of Cnk_lost has one value less).
    size = 1
    for dim in dims:
        size *= dim if isinstance(dim, int) else eval(str(dim).replace("MAX_ENUM", "32").replace("/", "//"), {"__builtins__": {}}, {})  # noqa: S307
    values += [0] * (size - len(values)) if len(dims) > 1 else []
    return [values], 1


HEADER = """// Generated by scripts/fixtures/musepack_tables.py from libmpcdec of musepack_src r495 ({r495}) and libmpcdec 1.2.6 ({v126}); do not edit.
// The tables are format data of Musepack's decoders (Huffman codes, enumerative coding tables, requantization coefficients, the synthesis window), copyright (c) 1999-2009
// The Musepack Development Team (the notice of the BSD 3-clause license is in THIRD-PARTY-NOTICES.md). Each table names its source file and C name; the walks and the
// decoder document what each element means.
#pragma warning disable SA1124
"""


def generate(roots):
    from musepack_tool import LIBMPC, LIBMPCDEC_126
    header = HEADER.format(r495=LIBMPC[1], v126=LIBMPCDEC_126[1])
    cache = {}

    def text_of(source, file):
        if (source, file) not in cache:
            cache[(source, file)] = strip_comments((roots[source] / file).read_text(encoding="latin-1"))
        return cache[(source, file)]

    parser = [header, "namespace Encspect.Formats.Musepack;", "", "internal static class MusepackTables", "{"]
    for source, file, cname, kind, csname in TABLES:
        tables, group = table(kind, text_of(source, file), cname)
        origin = f"{'musepack_src r495' if source == R495 else 'libmpcdec 1.2.6'} {file}, {cname}"
        for index, values in enumerate(tables):
            suffix = "" if len(tables) == 1 else "AB"[index]
            if kind == "huff":
                element, description = "int", f"{len(values) // 3} entries of code, length and value"
            elif kind == "huff32":
                element, description = "long", f"{len(values) // 3} entries of code, length and value (32-bit codes)"
            elif kind == "int8":
                element, description = "sbyte", f"{len(values)} symbols"
            else:
                element, description = "int", f"{len(values)} values"
            parser += [f"    // {origin}: {description}.",
                       f"    public static ReadOnlySpan<{element}> {csname}{suffix} => new {element}[]",
                       "    {",
                       emit_ints(values, 12 if kind not in ("huff", "huff32") else 12),
                       "    };",
                       ""]
    parser[-1:] = ["}", ""]

    decoder = [header, "namespace Encspect.Formats.Musepack.Decoding;", "", "internal static class SynthesisTables", "{"]
    for source, file, cname, kind, csname in DECODER_TABLES:
        tables, _ = table(kind, text_of(source, file), cname)
        values = tables[0]
        origin = f"{'musepack_src r495' if source == R495 else 'libmpcdec 1.2.6'} {file}, {cname}"
        if kind == "float":
            decoder += [f"    // {origin}: {len(values)} values.", f"    public static ReadOnlySpan<float> {csname} => new float[]", "    {",
                        "        " + ",\n        ".join(", ".join(values[i:i + 4]) for i in range(0, len(values), 4)) + ",", "    };", ""]
        else:
            decoder += [f"    // {origin}: {len(values)} values, each the numerator of a multiple of 1/65,536 (the synthesis window of ISO/IEC 11172-3 in libmpcdec's layout).",
                        f"    public static ReadOnlySpan<int> {csname}Numerators => new int[]", "    {", emit_ints(values, 16), "    };", ""]
    decoder[-1:] = ["}", ""]
    return "\n".join(parser), "\n".join(decoder)


def main(argv):
    if len(argv) != 2 or argv[1] not in ("--write", "--check"):
        print(__doc__)
        return 2
    parser_text, decoder_text = generate(sources())
    if argv[1] == "--write":
        PARSER_OUTPUT.write_text(parser_text, encoding="utf-8", newline="\n")
        DECODER_OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        DECODER_OUTPUT.write_text(decoder_text, encoding="utf-8", newline="\n")
        return 0
    ok = True
    for path, text in ((PARSER_OUTPUT, parser_text), (DECODER_OUTPUT, decoder_text)):
        if not path.exists() or path.read_text(encoding="utf-8") != text:
            print(f"{path} differs from what {pathlib.Path(__file__).name} --write produces")
            ok = False
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
