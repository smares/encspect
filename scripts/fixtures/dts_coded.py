"""Subframes with content for the hand-built DTS core frames (ETSI TS 102 114 V1.6.1, 5.5 and 5.6), written from the specification's pseudo-code by a writer apart from the
analyzer's walk (SubframeWalker.cs): bit allocation, transient modes, scale factors, joint intensity scale factors, vector quantization addresses, the LFE channel and subband
samples in every coding (plain, Huffman and block code), from a seeded generator, so the fixtures that use them regenerate without a difference. dts_model.py calls it where a
frame asks for coded content; without it the subframes are silent.

The Huffman code books are those of SubframeTables.cs, which scripts/fixtures/dts_tables.py generates from Annex D.5 and which is committed, so this script needs no download. What it
returns is what the analyzer's figures must equal: the highest subband that carries audio and which subframes have a transient.
"""

import pathlib
import random
import re

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "src" / "Encspect.Formats.Dts" / "SubframeTables.cs"

_BOOKS = None


def books():
    """Every Huffman book of D.5 as {name: {symbol: (length, code)}}, read from the generated table file."""
    global _BOOKS
    if _BOOKS is None:
        text = SOURCE.read_text(encoding="utf-8")
        _BOOKS = {}
        for name, body in re.findall(r"Book(\w+) => new int\[\]\s*\{(.*?)\};", text, re.S):
            numbers = [int(x) for x in re.findall(r"-?\d+", body)]
            _BOOKS[name] = {numbers[i]: (numbers[i + 1], numbers[i + 2]) for i in range(0, len(numbers), 3)}
    return _BOOKS


def put_huffman(bits, name, symbol):
    length, code = books()[name][symbol]
    bits.put(code, length)


# The Huffman books of each quantizer ABITS 1 to 10 by SEL (Table 5-26), and the levels and code widths of the block codes of ABITS 1 to 7.
QUANTIZER_BOOKS = [["A3"], ["A5", "B5", "C5"], ["A7", "B7", "C7"], ["A9", "B9", "C9"], ["A13", "B13", "C13"], ["A17", "B17", "C17", "D17", "E17", "F17", "G17"],
                   ["A25", "B25", "C25", "D25", "E25", "F25", "G25"], ["A33", "B33", "C33", "D33", "E33", "F33", "G33"], ["A65", "B65", "C65", "D65", "E65", "F65", "G65"],
                   ["A129", "B129", "C129", "D129", "E129", "F129", "G129"]]
LEVELS = [3, 5, 7, 9, 13, 17, 25]
BLOCK_BITS = [7, 10, 12, 13, 15, 17, 19]
SCALE_BOOKS = ["SA129", "SB129", "SC129", "SD129", "SE129"]
TRANSIENT_BOOKS = ["A4", "B4", "C4", "D4"]
ABITS_BOOKS = ["A12", "B12", "C12", "D12", "E12"]


def value_range(abits, sel):
    """The quantization indexes an ABITS and SEL can code: (kind, low, high)."""
    if abits >= 11:
        width = abits - 3
        return "plain", -(1 << (width - 1)), (1 << (width - 1)) - 1
    books_of = QUANTIZER_BOOKS[abits - 1]
    if sel < len(books_of):
        symbols = list(books()[books_of[sel]])
        return "huffman", min(symbols), max(symbols)
    if abits <= 7:
        half = (LEVELS[abits - 1] - 1) // 2
        return "block", -half, half
    width = abits - 3
    return "plain", -(1 << (width - 1)), (1 << (width - 1)) - 1


def put_samples(bits, rng, abits, sel, books_of_ch):
    kind, low, high = value_range(abits, sel)
    values = [min(high, max(low, round(rng.gauss(0, max(1, (high - low) / 6))))) for _ in range(8)]
    if kind == "huffman":
        for v in values:
            put_huffman(bits, QUANTIZER_BOOKS[abits - 1][sel], v)
    elif kind == "block":
        levels = LEVELS[abits - 1]
        half = (levels - 1) // 2
        for block in range(2):
            code = 0
            for k in range(4):
                code += (values[block * 4 + k] + half) * levels ** k
            bits.put(code, BLOCK_BITS[abits - 1])
    else:
        width = abits - 3
        for v in values:
            bits.put(v & ((1 << width) - 1), width)


class Facts:
    def __init__(self):
        self.subbands = 0
        self.transient_mask = []


def write_subframes(bits, p, nch, subs, vqsub, joinx, bhuff, shuff, thuff, sel, ssc_list, seed=1):
    """The subframes of a core frame with content. p carries cpf, dynf, aspf and lff; the arrays are per channel. Returns the facts the walk must find."""
    rng = random.Random(seed)
    facts = Facts()
    for sf, count in enumerate(ssc_list):
        bits.put(count - 1, 2)
        bits.put(0, 3)
        pmode = [[rng.randrange(2) if p.get("adpcm") else 0 for _ in range(subs[ch])] for ch in range(nch)]
        for ch in range(nch):
            for m in pmode[ch]:
                bits.put(m, 1)
        for ch in range(nch):
            for m in pmode[ch]:
                if m:
                    bits.put(rng.randrange(4096), 12)
        abits = []
        for ch in range(nch):
            row = []
            for n in range(vqsub[ch]):
                low = 1 if bhuff[ch] < 5 else 0
                high = 12 if bhuff[ch] < 5 else 15 if bhuff[ch] == 5 else 26
                value = rng.choice([low, low, low + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, high]) if p.get("rich", True) else low
                value = min(value, high, p.get("abits_max", 99))
                row.append(value)
                if bhuff[ch] < 5:
                    put_huffman(bits, ABITS_BOOKS[bhuff[ch]], value)
                else:
                    bits.put(value, 4 if bhuff[ch] == 5 else 5)
            abits.append(row)
        tmode = [[0] * 32 for _ in range(nch)]
        if count > 1:
            for ch in range(nch):
                for n in range(vqsub[ch]):
                    if abits[ch][n] > 0:
                        tmode[ch][n] = rng.choice([0, 0, 0, 0, 1, 2, 3]) % count if p.get("transients", True) else 0
                        put_huffman(bits, TRANSIENT_BOOKS[thuff[ch]], tmode[ch][n])
        for ch in range(nch):
            total = 0

            def scale():
                nonlocal total
                low, high = p.get("scale_range", (0, 62))
                index = rng.randrange(low, high + 1)
                if shuff[ch] < 5:
                    put_huffman(bits, SCALE_BOOKS[shuff[ch]], index - total)
                    total = index
                else:
                    bits.put(index, 6 if shuff[ch] == 5 else 7)

            for n in range(vqsub[ch]):
                if abits[ch][n] > 0:
                    scale()
                    if tmode[ch][n] > 0:
                        scale()
            for n in range(vqsub[ch], subs[ch]):
                scale()
        join_shuff = [rng.randrange(5) if joinx[ch] else 0 for ch in range(nch)]
        for ch in range(nch):
            if joinx[ch]:
                bits.put(join_shuff[ch], 3)
        for ch in range(nch):
            if joinx[ch]:
                for n in range(subs[ch], subs[joinx[ch] - 1]):
                    put_huffman(bits, SCALE_BOOKS[join_shuff[ch]], rng.randrange(-64, 65))
        if p.get("dynf"):
            bits.put(rng.randrange(256), 8)
        if p.get("cpf"):
            bits.put(0, 16)
        for ch in range(nch):
            for n in range(vqsub[ch], subs[ch]):
                bits.put(rng.randrange(1024), 10)
        if p.get("lff"):
            for _ in range(2 * p["lff"] * count):
                bits.put(rng.randrange(256), 8)
            low, high = p.get("scale_range", (0, 124))
            bits.put(rng.randrange(low, high + 1), 8)
        for ssf in range(count):
            for ch in range(nch):
                for n in range(vqsub[ch]):
                    if abits[ch][n] > 0:
                        a = abits[ch][n]
                        put_samples(bits, rng, a, sel[ch][a - 1] if a <= 10 else 0, None)
            if ssf == count - 1 or p.get("aspf"):
                bits.put(0xFFFF, 16)
        highest = 0
        for ch in range(nch):
            for n in range(vqsub[ch]):
                if abits[ch][n] > 0:
                    highest = max(highest, n + 1)
            highest = max(highest, subs[ch])
            if joinx[ch]:
                highest = max(highest, subs[joinx[ch] - 1])
        facts.subbands = max(facts.subbands, highest)
        facts.transient_mask.append(any(t > 0 for row in tmode for t in row))
    return facts
