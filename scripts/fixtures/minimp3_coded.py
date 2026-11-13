"""Builds minimp3_coded.c against a patched copy of the pinned minimp3 header (see that file) and runs it."""

import json
import pathlib
import subprocess
import tempfile

SOURCE = pathlib.Path(__file__).resolve().parent / "minimp3_coded.c"
HEADERS = pathlib.Path(__file__).resolve().parents[2] / "third_party" / "minimp3"

DECLARATION = ("typedef struct { int end, block_type, n_long_sfb; const uint8_t *sfbtab; } coded_t;\n"
               "static int g_coded_n;\nstatic coded_t g_coded[4];\n"
               "static int g_isb_n, g_isb_i, g_isb_off, g_isb_w;\nstatic int g_isb[2];\n")
HUFFMAN = "static void L3_huffman(float *dst, bs_t *bs, const L3_gr_info_t *gr_info, const float *scf, int layer3gr_limit)\n{\n"
SECOND_RELOAD = "        DEQ_COUNT1(1);\n        RELOAD_SCALEFACTOR;\n"
QUAD_LOOP = "    for (np = 1 - big_val_cnt;; dst += 4)\n    {\n"
END = "    bs->pos = layer3gr_limit;\n}\n"


STEREO_PROCESS_DECLARATION = "    unsigned i, max_pos = HDR_TEST_MPEG1(hdr) ? 7 : 64;\n"
INTENSITY_BAND = "            L3_intensity_stereo_band(left, sfb[i], kl*s, kr*s);\n"
STEREO_PROCESS_CALL = "    L3_stereo_process(left, ist_pos, gr->sfbtab, hdr, max_band, gr[1].scalefac_compress & 1);\n"
RECORD_BAND = ("            if (g_isb_i < 0) { g_isb_i = (int)i; g_isb_off = (int)(left - left0); g_isb_w = sfb[i]; }\n")
RECORD_GRANULE = ("    g_isb_i = -1;\n" + STEREO_PROCESS_CALL +
                  "    if (g_isb_n < 2) g_isb[g_isb_n++] = g_isb_i < 0 ? -1 : g_isb_i < gr->n_long_sfb ? g_isb_off : g_isb_off - ((g_isb_i - gr->n_long_sfb) % 3) * g_isb_w;\n")


def patched_header(directory):
    text = (HEADERS / "minimp3.h").read_text()
    start = text.index(HUFFMAN)
    end = text.index(END, start)
    body = text[start + len(HUFFMAN):end]
    # The second reload of a quadruple stops after its first pair: two lines of the quadruple were decoded.
    assert body.count(SECOND_RELOAD) == 1
    # The analyzer, like libmad, mpg123 and FFmpeg, decodes no quadruple that does not fit in the 576 lines (minimp3 decodes the two lines of one that starts at 574).
    assert body.count(QUAD_LOOP) == 1
    body = body.replace(QUAD_LOOP, QUAD_LOOP + "        if (dst - dst0 > 572) break;\n")
    body = body.replace(SECOND_RELOAD, "        DEQ_COUNT1(1);\n        if (!--np) { np = *sfb++/2; if (!np) { dst += 2; break; } one = *scf++; }\n")
    recorded = ("    g_coded[g_coded_n].end = (int)(dst - dst0); g_coded[g_coded_n].block_type = gr_info->block_type;\n"
                "    g_coded[g_coded_n].n_long_sfb = gr_info->n_long_sfb; g_coded[g_coded_n].sfbtab = gr_info->sfbtab; g_coded_n++;\n")
    text = (text[:start] + DECLARATION + HUFFMAN + "    float *dst0 = dst;\n" + body + recorded + END + text[end + len(END):])
    # The first band of each granule that takes the intensity branch of L3_stereo_process, as the offset in the order the data come in of the first band
    # of its triple of windows (a short band's three windows, one line each, start at the 576-line scale's 3 * the window's line), -1 where none does.
    assert text.count(STEREO_PROCESS_DECLARATION) == 1 and text.count(INTENSITY_BAND) == 1 and text.count(STEREO_PROCESS_CALL) == 1
    text = text.replace(STEREO_PROCESS_DECLARATION, STEREO_PROCESS_DECLARATION + "    float *left0 = left;\n")
    text = text.replace(INTENSITY_BAND, RECORD_BAND + INTENSITY_BAND)
    text = text.replace(STEREO_PROCESS_CALL, RECORD_GRANULE)
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
