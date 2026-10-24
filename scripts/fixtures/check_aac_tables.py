#!/usr/bin/env python3
"""Checks the AAC tables the analyzer and the bitstream model read with (ISO/IEC 14496-3 Annex 4.A and Tables 4.129 to 4.147).

Always, with the standard library only:

- src/Encspect.Formats.Aac/HuffmanData.cs holds exactly the codewords, lengths and band offsets of aac_tables.py, so the C# parser and the
  Python model that writes the hand-built fixtures read the same tables;
- every codebook is a complete prefix code: no codeword is a prefix of another, and the Kraft sum of the lengths is exactly 1, as for
  every Huffman code of the specification;
- a few entries the specification's tables give, such as the one-bit codeword 0 for a scalefactor difference of 0.

With --sources (or when .cache/tools holds them), against independent implementations at pinned commits:

- every codeword of the spectral and scalefactor codebooks decodes, through faad2 2.11.1's own decoding tables (libfaad/codebook/hcb_*.h,
  its two-step and binary-search methods, simulated here), to the values its index stands for and with its length; and every SBR codeword
  through faad2's SBR trees (libfaad/sbr_huff.c);
- the scalefactor band offsets and counts for 1024, 128, 960 and 120 lines match fdk-aac 2.0.2's sfbOffsetTables (libAACdec/src/aac_rom.cpp),
  and PRED_SFB_MAX for AAC Main matches faad2's pred_sfb_max (libfaad/common.c);
- every spectral, scalefactor and SBR codebook (codewords and lengths), the band counts, PRED_SFB_MAX and the SBR offsets match FFmpeg 6.1.1's
  (libavcodec/aactab.c, aacsbrdata.h), the release scripts/fixtures/ffmpeg_sources.py pins by SHA-256. FFmpeg is one cross-check next to
  faad2, not the source of the tables: the specification is.

faad2 is under the GPL, FFmpeg's files under the LGPL and fdk-aac under its own license; their sources are read here, at check time, and
nothing of them is copied.

    python3 scripts/fixtures/check_aac_tables.py [--sources]
"""

import pathlib
import re
import subprocess
import sys

import aac_tables as T
import ffmpeg_sources

ROOT = pathlib.Path(__file__).resolve().parents[2]
CS = ROOT / "src" / "Encspect.Formats.Aac" / "HuffmanData.cs"
TOOLS = ROOT / ".cache" / "tools"
FAAD = ("https://github.com/knik0/faad2", "216f00e8ddba6f2c64caf481a04f1ddd78b93e78", "faad2-2.11.1")  # tag 2.11.1
FDK = ("https://github.com/mstorsjo/fdk-aac", "801f67f671929311e0c9952c5f92d6e147c7b003", "fdk-aac-2.0.2")  # tag v2.0.2

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


def clone(spec):
    repository, commit, directory = spec
    path = TOOLS / directory
    if not path.exists():
        TOOLS.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "-q", repository, str(path)], check=True)
        subprocess.run(["git", "-C", str(path), "checkout", "-q", commit], check=True)
    return path


def c_rows(text, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*\[[^\]]*\](?:\[[^\]]*\])?\s*=\s*\{(.*?)\n\};", text, re.S)
    if not match:
        raise SystemExit(f"no {name}")
    body = re.sub(r"/\*.*?\*/", "", match.group(1), flags=re.S)
    return [[int(v) for v in re.findall(r"-?\d+", row)] for row in re.findall(r"\{([^{}]*)\}", body)]


def binary_rows(text, name):
    """A binary-search table of faad2: rows of { is_leaf, { data... } }."""
    match = re.search(r"\b" + re.escape(name) + r"\s*\[[^\]]*\]\s*=\s*\{(.*?)\n\};", text, re.S)
    body = re.sub(r"/\*.*?\*/", "", match.group(1), flags=re.S)
    return [[int(leaf)] + [int(v) for v in re.findall(r"-?\d+", data)] for leaf, data in re.findall(r"\{\s*(\d+)\s*,\s*\{([^}]*)\}\s*\}", body)]


def faad_spectral(faad, problems):
    """Decodes every codeword with faad2's tables, as libfaad/huffman.c does."""
    book = faad / "libfaad" / "codebook"
    first_bits = [0, 5, 5, 0, 5, 0, 5, 0, 5, 0, 6, 5]
    for n, (codes, bits) in enumerate(T.SPECTRAL, 1):
        text = (book / f"hcb_{n}.h").read_text(encoding="latin-1")
        for index, (code, length) in enumerate(zip(codes, bits)):
            word = format(code, f"0{length}b") + "0" * 20
            if first_bits[n]:
                first = c_rows(text, f"hcb{n}_1")
                second = c_rows(text, f"hcb{n}_2")
                n1 = first_bits[n]
                offset, extra = first[int(word[:n1], 2)]
                if extra:
                    offset += int(word[n1:n1 + extra], 2)
                entry = second[offset]
                decoded_length, values = entry[0], tuple(entry[1:])
            else:
                tree = binary_rows(text, f"hcb{n}")
                offset = 0
                used = 0
                while not tree[offset][0]:
                    offset += tree[offset][1 + int(word[used])]
                    used += 1
                decoded_length, values = used, tuple(tree[offset][1:])
            values = values[:4 if n <= 4 else 2]
            if decoded_length != length or values != spectral_values(n, index):
                problems.append(f"spectral codebook {n} index {index}: faad2 decodes {decoded_length} bits to {values}, expected {length} bits, "
                                f"{spectral_values(n, index)}")
    tree = c_rows((book / "hcb_sf.h").read_text(encoding="latin-1"), "hcb_sf")
    for index, (code, length) in enumerate(zip(*T.SCALEFACTOR)):
        word = format(code, f"0{length}b")
        offset = 0
        used = 0
        while tree[offset][1]:
            offset += tree[offset][int(word[used])]
            used += 1
        if (used, tree[offset][0]) != (length, index):
            problems.append(f"scalefactor index {index}: faad2 decodes {used} bits to {tree[offset][0]}")


def faad_sbr(faad, problems):
    text = (faad / "libfaad" / "sbr_huff.c").read_text(encoding="latin-1")
    for name in SBR_NAMES:
        codes, bits = T.SBR_HUFFMAN[name]
        lav = (len(codes) - 1) // 2
        tree = c_rows(text, name)
        for index, (code, length) in enumerate(zip(codes, bits)):
            word = format(code, f"0{length}b")
            node = 0
            used = 0
            while node >= 0:
                node = tree[node][int(word[used])]
                used += 1
            # sbr_huff_dec returns the leaf plus 64; faad2's balance codebooks hold their values doubled, the step coupled channels use.
            value = (index - lav) * (2 if "_bal_" in name else 1)
            if (used, node + 64) != (length, value):
                problems.append(f"{name} index {index}: faad2 decodes {used} bits to {node + 64}, expected {length} bits, {value}")


def fdk_bands(fdk, problems):
    text = (fdk / "libAACdec" / "src" / "aac_rom.cpp").read_text(encoding="latin-1")

    def table(name):
        match = re.search(r"\b" + name + r"\s*\[\d*\]\s*=\s*\{(.*?)\};", text, re.S)
        return [int(v) for v in re.findall(r"\d+", re.sub(r"/\*.*?\*/", "", match.group(1)))]

    rows = re.search(r"sfbOffsetTables\[5\]\[16\]\s*=\s*\{(.*?)\n\};", text, re.S).group(1)
    groups = re.findall(r"\{\s*(\{sfb_.*?)\}\s*(?:,|$)", rows, re.S)
    entries = re.findall(r"\{(sfb_\w+),\s*(sfb_\w+),\s*(\d+),\s*(\d+)\}", rows)
    by_length = {}
    for long_name, short_name, long_count, short_count in entries:
        by_length.setdefault(long_name.split("_")[2], []).append((long_name, short_name, int(long_count), int(short_count)))
    for label, ours_long, ours_short, num_long, num_short in [("1024", T.SWB_LONG, T.SWB_SHORT, T.NUM_SWB_LONG, T.NUM_SWB_SHORT),
                                                              ("960", T.SWB_LONG_960, T.SWB_SHORT_120, T.NUM_SWB_LONG_960, T.NUM_SWB_SHORT_120)]:
        theirs = by_length[label][:13]
        for index, (long_name, short_name, long_count, short_count) in enumerate(theirs):
            if (num_long[index], num_short[index]) != (long_count, short_count):
                problems.append(f"{label} lines, sampling frequency index {index}: {num_long[index]} and {num_short[index]} bands, fdk-aac {long_count} and {short_count}")
            if ours_long[index][:long_count + 1] != table(long_name)[:long_count + 1]:
                problems.append(f"{label} lines, sampling frequency index {index}: long band offsets differ from fdk-aac's {long_name}")
            if ours_short[index][:short_count + 1] != table(short_name)[:short_count + 1]:
                problems.append(f"{label} lines, sampling frequency index {index}: short band offsets differ from fdk-aac's {short_name}")
    _ = groups


def ffmpeg_tables(ffmpeg, problems):
    """Codewords, lengths, band counts and offsets as FFmpeg 6.1.1 prints them."""
    aactab = (ffmpeg / "libavcodec" / "aactab.c").read_text(encoding="latin-1")
    sbr = (ffmpeg / "libavcodec" / "aacsbrdata.h").read_text(encoding="latin-1")

    def array(text, name):
        match = re.search(r"\b" + re.escape(name) + r"\s*\[[^\]]*\](?:\[[^\]]*\])?\s*=\s*\{(.*?)\};", text, re.S)
        if not match:
            raise SystemExit(f"no {name} in FFmpeg's sources")
        body = re.sub(r"//[^\n]*", "", re.sub(r"/\*.*?\*/", "", match.group(1), flags=re.S))
        return [int(v, 0) for v in re.findall(r"-?0[xX][0-9a-fA-F]+|-?\d+", body)]

    def compare(label, ours, theirs):
        if list(ours) != theirs:
            problems.append(f"{label} differs from FFmpeg 6.1.1's")

    for n, (codes, bits) in enumerate(T.SPECTRAL, 1):
        compare(f"spectral codebook {n} codewords", codes, array(aactab, f"codes{n}"))
        compare(f"spectral codebook {n} lengths", bits, array(aactab, f"bits{n}"))
    compare("scalefactor codewords", T.SCALEFACTOR[0], array(aactab, "ff_aac_scalefactor_code"))
    compare("scalefactor lengths", T.SCALEFACTOR[1], array(aactab, "ff_aac_scalefactor_bits"))
    for name in SBR_NAMES:
        compare(f"{name} codewords", T.SBR_HUFFMAN[name][0], array(sbr, name + "_codes"))
        compare(f"{name} lengths", T.SBR_HUFFMAN[name][1], array(sbr, name + "_bits"))
    compare("long band counts", T.NUM_SWB_LONG, array(aactab, "ff_aac_num_swb_1024"))
    compare("long band counts for 960 lines", T.NUM_SWB_LONG_960, array(aactab, "ff_aac_num_swb_960"))
    compare("short band counts", T.NUM_SWB_SHORT, array(aactab, "ff_aac_num_swb_128"))
    compare("short band counts for 120 lines", T.NUM_SWB_SHORT_120, array(aactab, "ff_aac_num_swb_120"))
    compare("PRED_SFB_MAX", T.PRED_SFB_MAX, array(aactab, "ff_aac_pred_sfb_max"))
    compare("SBR offsets", [v for row in T.SBR_OFFSET for v in row], array(sbr, "sbr_offset"))


def faad_prediction(faad, problems):
    text = (faad / "libfaad" / "common.c").read_text(encoding="latin-1")
    match = re.search(r"pred_sfb_max\[\]\s*=\s*\{(.*?)\};", text, re.S)
    theirs = [int(v) for v in re.findall(r"\d+", re.sub(r"/\*.*?\*/", "", match.group(1)))]
    if T.PRED_SFB_MAX[:len(theirs)] != theirs:
        problems.append(f"PRED_SFB_MAX {T.PRED_SFB_MAX} differs from faad2's {theirs}")


def main(arguments):
    problems = []
    check_codes(problems)
    check_spec_entries(problems)
    check_cs(problems)
    checked = "the C# copy, completeness and prefix freedom, and entries the specification prints"
    if "--sources" in arguments or ((TOOLS / FAAD[2]).exists() and (TOOLS / FDK[2]).exists()):
        faad = clone(FAAD)
        fdk = clone(FDK)
        faad_spectral(faad, problems)
        faad_sbr(faad, problems)
        faad_prediction(faad, problems)
        fdk_bands(fdk, problems)
        ffmpeg_tables(ffmpeg_sources.fetch(TOOLS / "ffmpeg-sources"), problems)
        checked += ("; every codeword through faad2 2.11.1's decoding tables, the band tables against fdk-aac 2.0.2, "
                    "the codebooks, band counts and SBR offsets against FFmpeg 6.1.1")
    for problem in problems:
        print(problem)
    print(f"Checked {checked}: {len(problems)} problems.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
