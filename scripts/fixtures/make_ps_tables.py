#!/usr/bin/env python3
"""Writes src/Encspect.Formats.Aac/Decoding/PsTableData.cs from the parametric stereo tables of libxaac
(github.com/ittiam-systems/libxaac at 2fbadd5, Apache License 2.0): ixheaacd_aac_dec_ps_tables in decoder/ixheaacd_sbr_rom.c, in the order
of ia_ps_tables_struct (decoder/ixheaacd_sbr_rom.h).

    python3 -I scripts/fixtures/make_ps_tables.py [--check] <libxaac checkout>

With --check the file is compared with what the tables give and nothing is written.

The tables are format data (filter prototypes, modulation tables, band groupings, phase factors); the file lists them as float or int arrays.

libxaac has no Huffman codebooks for the inter-channel and overall phase differences (it does not parse them), so those four, huff_ipd_df, huff_ipd_dt, huff_opd_df and huff_opd_dt
(ISO/IEC 14496-3 Tables 8.B.20 and 8.B.21), are transcribed below as codewords, format data of the standard. Whichever the mode, the script checks that each is a complete prefix code
of the eight indices and that src/Encspect.Formats.Aac/PsHuffman.cs holds exactly the binary trees these codewords make (`--print-trees` writes them)."""
import re
import sys
from pathlib import Path

NAMES = ["qmf_fract_delay_phase_factor_im", "qmf_fract_delay_phase_factor_re", "frac_delay_phase_fac_qmf_sub_im_20", "frac_delay_phase_fac_qmf_sub_re_20",
         "frac_delay_phase_fac_qmf_sub_im_34", "frac_delay_phase_fac_qmf_sub_re_34", "qmf_ser_fract_delay_phase_factor_im", "qmf_ser_fract_delay_phase_factor_re",
         "frac_delay_phase_fac_ser_qmf_sub_im_20", "frac_delay_phase_fac_ser_qmf_sub_re_20", "frac_delay_phase_fac_ser_qmf_sub_im_34", "frac_delay_phase_fac_ser_qmf_sub_re_34",
         "scale_factors_flt", "scale_factors_fine_flt", "alphas", "all_pass_link_decay_ser", "p8_13_20", "p2_13_20", "p12_13_34", "p8_13_34", "p4_13_34",
         "cos_mod_2channel", "cos_sin_mod_4channel", "cos_sin_mod_8channel", "cos_sin_mod_12channel", "qmf_delay_idx_tbl", "group_borders_20_tbl", "group_borders_34_tbl",
         "bin_group_map_20", "bin_group_map_34", "quantized_iids", "quantized_iids_fine", "quantized_rhos", "ipd_bins_tbl", "band_res_hyb20", "band_res_hyb34"]
# The fields of the struct before the first of NAMES (all WORD16 tables and the dummy).
LEADING = 25
MASK = 0x1000
DEFINES = {"NEGATE_IPD_MASK": MASK, "NUM_LOW_RES_IPD_BINS": 5, "NUM_MID_RES_IPD_BINS": 11, "NUM_HI_RES_IPD_BINS": 17,
           "NO_HYBRID_CHANNELS_2": 2, "NO_HYBRID_CHANNELS_4": 4, "NO_HYBRID_CHANNELS_8": 8, "NO_HYBRID_CHANNELS_12": 12}


# The codewords of the indices 0 to 7 of Tables 8.B.20 and 8.B.21, and the name of the tree of PsHuffman.cs for each.
PHASE_CODEWORDS = {
    "IpdFrequencyTree": ["1", "000", "0110", "0100", "0010", "0011", "0101", "0111"],
    "IpdTimeTree": ["1", "010", "0010", "00011", "00010", "0000", "0011", "011"],
    "OpdFrequencyTree": ["1", "001", "0110", "0100", "01111", "01110", "0101", "000"],
    "OpdTimeTree": ["1", "010", "0001", "00111", "00110", "0000", "0010", "011"],
}


def phase_tree(words):
    """The tree of PsHuffman.cs for a codebook: a node is (child after 0) << 8 | (child after 1) & 0xFF, a child the index of a node or, for a leaf, the symbol less 64."""
    assert sum(2.0 ** -len(w) for w in words) == 1.0, "not complete"
    nodes = [[None, None]]
    for symbol, word in enumerate(words):
        at = 0
        for i, bit in enumerate(word):
            bit = int(bit)
            if i == len(word) - 1:
                assert nodes[at][bit] is None, "not a prefix code"
                nodes[at][bit] = symbol - 64
            else:
                if nodes[at][bit] is None:
                    nodes.append([None, None])
                    nodes[at][bit] = len(nodes) - 1
                assert nodes[at][bit] >= 0, "not a prefix code"
                at = nodes[at][bit]
    return [((zero & 0xFF) << 8) | (one & 0xFF) for zero, one in nodes]


def check_phase_codebooks(print_trees=False):
    text = (Path(__file__).resolve().parents[2] / "src/Encspect.Formats.Aac/PsHuffman.cs").read_text(encoding="utf-8")
    for name, words in PHASE_CODEWORDS.items():
        tree = phase_tree(words)
        if print_trees:
            print(f"    private static readonly ushort[] {name} = [{', '.join(f'0x{n:04X}' for n in tree)}];")
            continue
        match = re.search(rf"ushort\[\] {name} = \[([^\]]*)\];", text)
        if match is None or [int(n, 16) for n in re.findall(r"0x[0-9A-Fa-f]{4}", match.group(1))] != tree:
            sys.exit(f"PsHuffman.cs: {name} is not the tree of the codewords of Tables 8.B.20 and 8.B.21")
    if not print_trees:
        print("PsHuffman.cs holds the trees of the phase difference codebooks")


def tokens(text):
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.findall(r"[{}]|[^{},\s][^{},]*", text)


def parse(toks, at=0):
    out = []
    while at < len(toks):
        t = toks[at]
        if t == "{":
            child, at = parse(toks, at + 1)
            out.append(child)
            continue
        if t == "}":
            return out, at + 1
        out.append(t.strip())
        at += 1
    return out, at


def value(text):
    for name, v in DEFINES.items():
        text = text.replace(name, str(v))
    text = re.sub(r"(\d(?:\.\d*)?(?:[eE][-+]?\d+)?)[fF]\b", r"\1", text)
    return eval(text, {"__builtins__": {}})


def flatten(node):
    if isinstance(node, list):
        for child in node:
            yield from flatten(child)
    else:
        yield value(node)


def dims(node):
    if not isinstance(node, list):
        return []
    return [len(node)] + (dims(node[0]) if node and isinstance(node[0], list) else [])


def main():
    if "--print-trees" in sys.argv:
        check_phase_codebooks(print_trees=True)
        return
    check_phase_codebooks()
    check = "--check" in sys.argv
    root = Path([a for a in sys.argv[1:] if a != "--check"][0])
    source = (root / "decoder/ixheaacd_sbr_rom.c").read_text()
    body = source[source.index("const ia_ps_tables_struct ixheaacd_aac_dec_ps_tables = {"):]
    body = body[body.index("{"):]
    tree, _ = parse(tokens(body))
    fields = [f for f in tree[0] if f != ""] if tree and isinstance(tree[0], list) else tree
    fields = [f for f in fields if f != ""]
    # The first field is a nested group whose closing brace is followed by a stray comma: the list holds one entry for each field.
    tables = fields[LEADING:]
    if len(tables) != len(NAMES):
        sys.exit(f"{len(tables)} tables where {len(NAMES)} were expected")
    lines = [
        "// Generated by scripts/fixtures/make_ps_tables.py: format data of the parametric stereo decoder (ISO/IEC 14496-3 8.6.4): hybrid filter prototypes and modulation, the fractional delays and",
        "// all-pass phase factors, band groupings, the dequantization of IID and ICC.",
        "// Taken from libxaac (github.com/ittiam-systems/libxaac at 2fbadd5, Apache License 2.0, Copyright (C) The Android Open Source Project; the notice is in THIRD-PARTY-NOTICES.md):",
        "// decoder/ixheaacd_sbr_rom.c, ixheaacd_aac_dec_ps_tables.",
        "namespace Encspect.Formats.Aac.Decoding;",
        "",
        "internal static class PsTableData",
        "{",
    ]
    for name, table in zip(NAMES, tables):
        values = list(flatten(table))
        ints = all(float(v).is_integer() for v in values) and not name.startswith(("p", "cos", "scale", "alphas", "frac", "qmf_fract", "qmf_ser", "all", "quantized_rhos"))
        ctype, suffix = ("int", "") if ints else ("float", "f")
        camel = "".join(part.capitalize() for part in name.split("_"))
        lines.append(f"    // {name}, {'x'.join(str(d) for d in dims(table))}")
        lines.append(f"    public static readonly {ctype}[] {camel} =")
        lines.append("    [")
        for i in range(0, len(values), 6):
            chunk = values[i:i + 6]
            lines.append("        " + ", ".join((str(int(v)) if ints else repr(float(v)) + suffix) for v in chunk) + ",")
        lines.append("    ];")
        lines.append("")
    lines.pop()
    lines.append("}")
    out = Path(__file__).resolve().parents[2] / "src/Encspect.Formats.Aac/Decoding/PsTableData.cs"
    text = "\n".join(lines) + "\n"
    if check:
        if out.read_text(encoding="utf-8") != text:
            sys.exit(f"{out} differs from what libxaac's tables give")
        print(f"{out.name} equals libxaac's tables")
        return
    out.write_text(text, encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
