"""Builds faad2 2.11.1, the version Ubuntu 24.04 ships, with a trace of what its parser reads in every raw data block, so the
analyzer's reading of each access unit can be checked against an independent decoder frame by frame (crosscheck_mp4_fixtures.py).

faad2's own ANALYSIS build no longer compiles, so a few fprintf calls are added to libfaad/syntax.c and sbr_syntax.c at build time,
after the fields they print are read; each line starts with "@@" on standard error:

- "@@AU <bit>": a raw data block starts; "@@EL <id> <bit>": an element starts; "@@END <bit>": the block ends, after its byte alignment;
- "@@CPE <common_window> <ms_mask_present> <ms_used flags>": a channel pair's shared information;
- "@@ICS <window_sequence> <window_shape> <max_sfb> <groups> <global_gain> <pulse> <tns> <gain control> <predictor> <TNS filters>
  <codebooks>": a channel stream, read to the end of its spectral data;
- "@@MS <ms_mask_present> <bands>": faad2's own decision of ms_decode, after the channel pair's two "@@ICS" lines: for each band of each window group, 1
  where it applies mid/side decoding (ms_used set or mask 2, and neither intensity stereo in the second channel nor noise substitution in the first);
- "@@EXT <type>": an extension payload; "@@SBRHDR <start> <stop> <xover>": an SBR header; "@@SBREXT <id>": an SBR extension.

Bit positions count from the start of what the decoder was handed, so only differences between them are compared. The build lives in
.cache/tools (git-ignored); faad2 is under the GPL and is built for checking this project's fixtures, not redistributed.

    python3 scripts/fixtures/faad_trace_tool.py file.m4a
"""

import hashlib
import pathlib
import subprocess
import sys

REPOSITORY = "https://github.com/knik0/faad2"
COMMIT = "216f00e8ddba6f2c64caf481a04f1ddd78b93e78"  # tag 2.11.1
ROOT = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "tools" / "faad2-trace"

PATCHES = {
    "libfaad/syntax.c": [
        ("    hDecoder->has_lfe = 0;\n",
         "    hDecoder->has_lfe = 0;\n    fprintf(stderr, \"@@AU %u\\n\", faad_get_processed_bits(ld));\n"),
        ("        {\n            switch (id_syn_ele) {\n",
         "        {\n            fprintf(stderr, \"@@EL %u %u\\n\", id_syn_ele, faad_get_processed_bits(ld) - 3);\n            switch (id_syn_ele) {\n"),
        ("        faad_byte_align(ld);\n    }\n\n    return;\n}\n\n/* Table 4.4.4 and */",
         "        faad_byte_align(ld);\n    }\n    fprintf(stderr, \"@@END %u\\n\", faad_get_processed_bits(ld));\n\n    return;\n}\n\n/* Table 4.4.4 and */"),
        ("    if (faad_showbits(ld, LEN_SE_ID) == ID_FIL)\n    {\n        faad_flushbits(ld, LEN_SE_ID);\n",
         "    if (faad_showbits(ld, LEN_SE_ID) == ID_FIL)\n    {\n        fprintf(stderr, \"@@EL 6 %u\\n\", faad_get_processed_bits(ld));\n        faad_flushbits(ld, LEN_SE_ID);\n"),
        ("        memcpy(ics2, ics1, sizeof(ic_stream));\n    } else {\n        ics1->ms_mask_present = 0;\n    }\n",
         "        memcpy(ics2, ics1, sizeof(ic_stream));\n    } else {\n        ics1->ms_mask_present = 0;\n    }\n"
         "    {\n        uint8_t g, sfb;\n        fprintf(stderr, \"@@CPE %u %u \", cpe.common_window, ics1->ms_mask_present);\n"
         "        if (ics1->ms_mask_present == 1)\n            for (g = 0; g < ics1->num_window_groups; g++)\n                for (sfb = 0; sfb < ics1->max_sfb; sfb++)\n"
         "                    fprintf(stderr, \"%u\", ics1->ms_used[g][sfb]);\n        fprintf(stderr, \"\\n\");\n    }\n"),
        ("    return 0;\n}\n\n/* Table 4.4.25 */",
         "    {\n        uint8_t g, sfb, w, filters = 0;\n        for (w = 0; w < ics->num_windows; w++)\n            filters += ics->tns_data_present ? ics->tns.n_filt[w] : 0;\n"
         "        fprintf(stderr, \"@@ICS %u %u %u %u %u %u %u %u %u %u \", ics->window_sequence, ics->window_shape, ics->max_sfb, ics->num_window_groups,\n"
         "            ics->global_gain, ics->pulse_data_present, ics->tns_data_present, ics->gain_control_data_present, ics->predictor_data_present, filters);\n"
         "        for (g = 0; g < ics->num_window_groups; g++)\n            for (sfb = 0; sfb < ics->max_sfb; sfb++)\n"
         "                fprintf(stderr, \"%x\", ics->sfb_cb[g][sfb]);\n        fprintf(stderr, \"\\n\");\n    }\n"
         "    return 0;\n}\n\n/* Table 4.4.25 */"),
        ("    uint8_t extension_type = (uint8_t)faad_getbits(ld, 4\n        DEBUGVAR(1,87,\"extension_payload(): extension_type\"));\n",
         "    uint8_t extension_type = (uint8_t)faad_getbits(ld, 4\n        DEBUGVAR(1,87,\"extension_payload(): extension_type\"));\n"
         "    fprintf(stderr, \"@@EXT %u\\n\", extension_type);\n"),
        ("        bs_extension_type = (uint8_t)faad_showbits(ld, 4);\n",
         "        bs_extension_type = (uint8_t)faad_showbits(ld, 4);\n        if (bs_extension_type == EXT_SBR_DATA || bs_extension_type == EXT_SBR_DATA_CRC)\n"
         "            fprintf(stderr, \"@@EXT %u\\n\", bs_extension_type);\n"),
    ],
    "libfaad/ms.c": [
        ("    if (ics->ms_mask_present >= 1)\n    {\n        for (g = 0;",
         "    if (ics->ms_mask_present >= 1)\n    {\n        fprintf(stderr, \"@@MS %u \", ics->ms_mask_present);\n        for (g = 0;"),
        ("                    if ((ics->ms_used[g][sfb] || ics->ms_mask_present == 2) &&\n",
         "                    if (b == 0)\n                        fprintf(stderr, \"%u\", (ics->ms_used[g][sfb] || ics->ms_mask_present == 2) && !is_intensity(icsr, g, sfb) && !is_noise(ics, g, sfb));\n"
         "                    if ((ics->ms_used[g][sfb] || ics->ms_mask_present == 2) &&\n"),
        ("                group++;\n            }\n        }\n    }\n",
         "                group++;\n            }\n        }\n        fprintf(stderr, \"\\n\");\n    }\n"),
    ],
    "libfaad/sbr_syntax.c": [
        ("            sbr->bs_extension_id = (uint8_t)faad_getbits(ld, 2\n                DEBUGVAR(1,227,\"sbr_single_channel_element(): bs_extension_id\"));\n",
         "            sbr->bs_extension_id = (uint8_t)faad_getbits(ld, 2\n                DEBUGVAR(1,227,\"sbr_single_channel_element(): bs_extension_id\"));\n"
         "            fprintf(stderr, \"@@SBREXT %u\\n\", sbr->bs_extension_id);\n"),
        ("    sbr->bs_xover_band = (uint8_t)faad_getbits(ld, 3\n        DEBUGVAR(1,206,\"sbr_header(): bs_xover_band\"));\n",
         "    sbr->bs_xover_band = (uint8_t)faad_getbits(ld, 3\n        DEBUGVAR(1,206,\"sbr_header(): bs_xover_band\"));\n"
         "    fprintf(stderr, \"@@SBRHDR %u %u %u\\n\", sbr->bs_start_freq, sbr->bs_stop_freq, sbr->bs_xover_band);\n"),
    ],
}


def build():
    """Returns the faad binary with the trace, building it once."""
    binary = ROOT / "build" / "faad"
    stamp = ROOT / "build" / "patches.sha256"
    # The binary is rebuilt when the patches or the pinned commit changed, not only when it is missing.
    wanted = hashlib.sha256(repr((COMMIT, sorted(PATCHES.items()))).encode()).hexdigest()
    if binary.exists() and stamp.exists() and stamp.read_text() == wanted:
        return binary
    if not ROOT.exists():
        ROOT.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "-q", REPOSITORY, str(ROOT)], check=True)
    subprocess.run(["git", "-C", str(ROOT), "checkout", "-q", "--force", COMMIT], check=True)
    for name, patches in PATCHES.items():
        path = ROOT / name
        text = path.read_text(encoding="latin-1")
        if "#include <stdio.h>" not in text:
            text = "#include <stdio.h>\n" + text
        for old, new in patches:
            if text.count(old) < 1:
                raise SystemExit(f"faad2's {name} no longer has the code a trace follows: {old[:60]!r}")
            # A pattern patches its first place only, but the fill element that a channel element reads itself, in both of them.
            text = text.replace(old, new, 2 if "@@EL 6" in new else 1)
        path.write_text(text, encoding="latin-1")
    subprocess.run(["cmake", "-S", str(ROOT), "-B", str(ROOT / "build"), "-DCMAKE_BUILD_TYPE=Release"], check=True, capture_output=True)
    subprocess.run(["cmake", "--build", str(ROOT / "build"), "-j8"], check=True, capture_output=True)
    stamp.write_text(wanted)
    return binary


def trace(path):
    """The raw data blocks faad2 reads in a file, in order: per block its length in bits and its records, as lists of fields."""
    binary = build()
    result = subprocess.run([str(binary), "-o", "/dev/null", str(path)], capture_output=True)
    blocks = []
    start = None
    for line in result.stderr.decode("latin-1").splitlines():
        if not line.startswith("@@"):
            continue
        kind, *fields = line[2:].split()
        if kind == "AU":
            start = int(fields[0])
            blocks.append({"records": []})
        elif kind == "END" and blocks:
            blocks[-1]["bits"] = int(fields[0]) - start
        elif blocks:
            if kind == "EL":
                fields[1] = str(int(fields[1]) - start)
            if kind == "ICS" and len(fields) == 10:
                fields.append("")
            blocks[-1]["records"].append([kind] + fields)
    return blocks


if __name__ == "__main__":
    for block in trace(sys.argv[1]):
        print(block.get("bits"), block["records"])
