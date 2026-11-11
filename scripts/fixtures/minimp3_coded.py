"""Builds minimp3_coded.c against a patched copy of the pinned minimp3 header (see that file) and runs it."""

import json
import pathlib
import subprocess
import tempfile

SOURCE = pathlib.Path(__file__).resolve().parent / "minimp3_coded.c"
HEADERS = pathlib.Path(__file__).resolve().parents[2] / "third_party" / "minimp3"

DECLARATION = ("typedef struct { int end, block_type, n_long_sfb; const uint8_t *sfbtab; } coded_t;\n"
               "static int g_coded_n;\nstatic coded_t g_coded[4];\n")
HUFFMAN = "static void L3_huffman(float *dst, bs_t *bs, const L3_gr_info_t *gr_info, const float *scf, int layer3gr_limit)\n{\n"
SECOND_RELOAD = "        DEQ_COUNT1(1);\n        RELOAD_SCALEFACTOR;\n"
END = "    bs->pos = layer3gr_limit;\n}\n"


def patched_header(directory):
    text = (HEADERS / "minimp3.h").read_text()
    start = text.index(HUFFMAN)
    end = text.index(END, start)
    body = text[start + len(HUFFMAN):end]
    # The second reload of a quadruple stops after its first pair: two lines of the quadruple were decoded.
    assert body.count(SECOND_RELOAD) == 1
    body = body.replace(SECOND_RELOAD, "        DEQ_COUNT1(1);\n        if (!--np) { np = *sfb++/2; if (!np) { dst += 2; break; } one = *scf++; }\n")
    recorded = ("    g_coded[g_coded_n].end = (int)(dst - dst0); g_coded[g_coded_n].block_type = gr_info->block_type;\n"
                "    g_coded[g_coded_n].n_long_sfb = gr_info->n_long_sfb; g_coded[g_coded_n].sfbtab = gr_info->sfbtab; g_coded_n++;\n")
    text = (text[:start] + DECLARATION + HUFFMAN + "    float *dst0 = dst;\n" + body + recorded + END + text[end + len(END):])
    (directory / "minimp3.h").write_text(text)


def build():
    directory = pathlib.Path(tempfile.mkdtemp(prefix="minimp3-coded-"))
    patched_header(directory)
    binary = directory / "minimp3_coded"
    subprocess.run(["cc", "-O1", "-I", str(directory), "-o", str(binary), str(SOURCE), "-lm"], check=True)
    return binary


def run(binary, path):
    result = subprocess.run([str(binary), str(path)], capture_output=True, text=True, check=True)
    return [json.loads(line) for line in result.stdout.splitlines()]
