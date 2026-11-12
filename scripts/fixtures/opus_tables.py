#!/usr/bin/env python3
"""Writes and checks the numeric tables of the Opus decoder (src/Encspect.Formats.Opus/OpusTables.cs) from libopus 1.5.2's source.

    opus_tables.py --write   writes OpusTables.cs from the pinned release (opus_tool.py)
    opus_tables.py --check   reads the pinned release again and fails where OpusTables.cs differs from what --write would produce

The tables are format data (codebooks, probability models, band edges, windows and transform twiddles) that RFC 6716's decoder and libopus define and that have no
other public source (docs/development.md, "Where the format tables come from"); the generated file names the source file and C name of each. Their license is
libopus's (BSD-3-Clause, THIRD-PARTY-NOTICES.md). The range coder state of every packet of the test vectors is the check that a table is right.
Exit status 0 where the committed file is what the source gives.
"""

import pathlib
import re
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import opus_tool  # noqa: E402

OUTPUT = HERE.parents[1] / "src" / "Encspect.Formats.Opus" / "OpusTables.cs"

C_TYPES = {"uint8": "byte", "int8": "sbyte", "int16": "short", "int32": "int", "uint32": "uint", "float": "float"}

# (class, C++ name, file in the release, C type of the elements, C# name)
SILK = "silk/"
CELT = "celt/"
TABLES = [
    ("SilkTables", "silk_gain_iCDF", SILK + "tables_gain.c", "uint8", "GainICdf"),
    ("SilkTables", "silk_delta_gain_iCDF", SILK + "tables_gain.c", "uint8", "DeltaGainICdf"),
    ("SilkTables", "silk_LTP_per_index_iCDF", SILK + "tables_LTP.c", "uint8", "LtpPerIndexICdf"),
    ("SilkTables", "silk_LTP_gain_iCDF_0", SILK + "tables_LTP.c", "uint8", "LtpGainICdf0"),
    ("SilkTables", "silk_LTP_gain_iCDF_1", SILK + "tables_LTP.c", "uint8", "LtpGainICdf1"),
    ("SilkTables", "silk_LTP_gain_iCDF_2", SILK + "tables_LTP.c", "uint8", "LtpGainICdf2"),
    ("SilkTables", "silk_LTP_gain_vq_0", SILK + "tables_LTP.c", "int8", "LtpVq0Q7"),
    ("SilkTables", "silk_LTP_gain_vq_1", SILK + "tables_LTP.c", "int8", "LtpVq1Q7"),
    ("SilkTables", "silk_LTP_gain_vq_2", SILK + "tables_LTP.c", "int8", "LtpVq2Q7"),
    ("SilkTables", "silk_NLSF_CB1_NB_MB_Q8", SILK + "tables_NLSF_CB_NB_MB.c", "uint8", "NlsfCb1NbMbQ8"),
    ("SilkTables", "silk_NLSF_CB1_Wght_Q9", SILK + "tables_NLSF_CB_NB_MB.c", "int16", "NlsfCb1NbMbWghtQ9"),
    ("SilkTables", "silk_NLSF_CB1_iCDF_NB_MB", SILK + "tables_NLSF_CB_NB_MB.c", "uint8", "NlsfCb1NbMbICdf"),
    ("SilkTables", "silk_NLSF_CB2_SELECT_NB_MB", SILK + "tables_NLSF_CB_NB_MB.c", "uint8", "NlsfCb2SelectNbMb"),
    ("SilkTables", "silk_NLSF_CB2_iCDF_NB_MB", SILK + "tables_NLSF_CB_NB_MB.c", "uint8", "NlsfCb2ICdfNbMb"),
    ("SilkTables", "silk_NLSF_PRED_NB_MB_Q8", SILK + "tables_NLSF_CB_NB_MB.c", "uint8", "NlsfPredNbMbQ8"),
    ("SilkTables", "silk_NLSF_DELTA_MIN_NB_MB_Q15", SILK + "tables_NLSF_CB_NB_MB.c", "int16", "NlsfDeltaMinNbMbQ15"),
    ("SilkTables", "silk_NLSF_CB1_WB_Q8", SILK + "tables_NLSF_CB_WB.c", "uint8", "NlsfCb1WbQ8"),
    ("SilkTables", "silk_NLSF_CB1_WB_Wght_Q9", SILK + "tables_NLSF_CB_WB.c", "int16", "NlsfCb1WbWghtQ9"),
    ("SilkTables", "silk_NLSF_CB1_iCDF_WB", SILK + "tables_NLSF_CB_WB.c", "uint8", "NlsfCb1WbICdf"),
    ("SilkTables", "silk_NLSF_CB2_SELECT_WB", SILK + "tables_NLSF_CB_WB.c", "uint8", "NlsfCb2SelectWb"),
    ("SilkTables", "silk_NLSF_CB2_iCDF_WB", SILK + "tables_NLSF_CB_WB.c", "uint8", "NlsfCb2ICdfWb"),
    ("SilkTables", "silk_NLSF_PRED_WB_Q8", SILK + "tables_NLSF_CB_WB.c", "uint8", "NlsfPredWbQ8"),
    ("SilkTables", "silk_NLSF_DELTA_MIN_WB_Q15", SILK + "tables_NLSF_CB_WB.c", "int16", "NlsfDeltaMinWbQ15"),
    ("SilkTables", "silk_stereo_pred_quant_Q13", SILK + "tables_other.c", "int16", "StereoPredQuantQ13"),
    ("SilkTables", "silk_stereo_pred_joint_iCDF", SILK + "tables_other.c", "uint8", "StereoPredJointICdf"),
    ("SilkTables", "silk_stereo_only_code_mid_iCDF", SILK + "tables_other.c", "uint8", "StereoOnlyCodeMidICdf"),
    ("SilkTables", "silk_LBRR_flags_2_iCDF", SILK + "tables_other.c", "uint8", "LbrrFlags2ICdf"),
    ("SilkTables", "silk_LBRR_flags_3_iCDF", SILK + "tables_other.c", "uint8", "LbrrFlags3ICdf"),
    ("SilkTables", "silk_lsb_iCDF", SILK + "tables_other.c", "uint8", "LsbICdf"),
    ("SilkTables", "silk_LTPscale_iCDF", SILK + "tables_other.c", "uint8", "LtpScaleICdf"),
    ("SilkTables", "silk_type_offset_VAD_iCDF", SILK + "tables_other.c", "uint8", "TypeOffsetVadICdf"),
    ("SilkTables", "silk_type_offset_no_VAD_iCDF", SILK + "tables_other.c", "uint8", "TypeOffsetNoVadICdf"),
    ("SilkTables", "silk_NLSF_interpolation_factor_iCDF", SILK + "tables_other.c", "uint8", "NlsfInterpolationFactorICdf"),
    ("SilkTables", "silk_Quantization_Offsets_Q10", SILK + "tables_other.c", "int16", "QuantizationOffsetsQ10"),
    ("SilkTables", "silk_LTPScales_table_Q14", SILK + "tables_other.c", "int16", "LtpScalesQ14"),
    ("SilkTables", "silk_uniform3_iCDF", SILK + "tables_other.c", "uint8", "Uniform3ICdf"),
    ("SilkTables", "silk_uniform4_iCDF", SILK + "tables_other.c", "uint8", "Uniform4ICdf"),
    ("SilkTables", "silk_uniform5_iCDF", SILK + "tables_other.c", "uint8", "Uniform5ICdf"),
    ("SilkTables", "silk_uniform6_iCDF", SILK + "tables_other.c", "uint8", "Uniform6ICdf"),
    ("SilkTables", "silk_uniform8_iCDF", SILK + "tables_other.c", "uint8", "Uniform8ICdf"),
    ("SilkTables", "silk_NLSF_EXT_iCDF", SILK + "tables_other.c", "uint8", "NlsfExtICdf"),
    ("SilkTables", "silk_pitch_lag_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchLagICdf"),
    ("SilkTables", "silk_pitch_delta_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchDeltaICdf"),
    ("SilkTables", "silk_pitch_contour_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchContourICdf"),
    ("SilkTables", "silk_pitch_contour_NB_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchContourNbICdf"),
    ("SilkTables", "silk_pitch_contour_10_ms_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchContour10MsICdf"),
    ("SilkTables", "silk_pitch_contour_10_ms_NB_iCDF", SILK + "tables_pitch_lag.c", "uint8", "PitchContour10MsNbICdf"),
    ("SilkTables", "silk_pulses_per_block_iCDF", SILK + "tables_pulses_per_block.c", "uint8", "PulsesPerBlockICdf"),
    ("SilkTables", "silk_rate_levels_iCDF", SILK + "tables_pulses_per_block.c", "uint8", "RateLevelsICdf"),
    ("SilkTables", "silk_shell_code_table0", SILK + "tables_pulses_per_block.c", "uint8", "ShellCodeTable0"),
    ("SilkTables", "silk_shell_code_table1", SILK + "tables_pulses_per_block.c", "uint8", "ShellCodeTable1"),
    ("SilkTables", "silk_shell_code_table2", SILK + "tables_pulses_per_block.c", "uint8", "ShellCodeTable2"),
    ("SilkTables", "silk_shell_code_table3", SILK + "tables_pulses_per_block.c", "uint8", "ShellCodeTable3"),
    ("SilkTables", "silk_shell_code_table_offsets", SILK + "tables_pulses_per_block.c", "uint8", "ShellCodeTableOffsets"),
    ("SilkTables", "silk_sign_iCDF", SILK + "tables_pulses_per_block.c", "uint8", "SignICdf"),
    ("SilkTables", "silk_LSFCosTab_FIX_Q12", SILK + "table_LSF_cos.c", "int16", "LsfCosTabQ12"),
    ("SilkTables", "silk_CB_lags_stage2", SILK + "pitch_est_tables.c", "int8", "CbLagsStage2"),
    ("SilkTables", "silk_CB_lags_stage2_10_ms", SILK + "pitch_est_tables.c", "int8", "CbLagsStage2TenMs"),
    ("SilkTables", "silk_CB_lags_stage3", SILK + "pitch_est_tables.c", "int8", "CbLagsStage3"),
    ("SilkTables", "silk_CB_lags_stage3_10_ms", SILK + "pitch_est_tables.c", "int8", "CbLagsStage3TenMs"),
    ("SilkTables", "silk_resampler_frac_FIR_12", SILK + "resampler_rom.c", "int16", "ResamplerFracFir12"),
    ("CeltTables", "e_prob_model", CELT + "quant_bands.c", "uint8", "EProbModel"),
    ("CeltTables", "eMeans", CELT + "quant_bands.c", "float", "EMeans"),
    ("CeltTables", "tf_select_table", CELT + "celt.c", "int8", "TfSelect"),
    ("CeltTables", "eband5ms", CELT + "modes.c", "int16", "EBand5Ms"),
    ("CeltTables", "band_allocation", CELT + "modes.c", "uint8", "BandAllocation"),
    ("CeltTables", "window120", CELT + "static_modes_float.h", "float", "Window120"),
    ("CeltTables", "logN400", CELT + "static_modes_float.h", "int16", "LogN400"),
    ("CeltTables", "cache_index50", CELT + "static_modes_float.h", "int16", "CacheIndex50"),
    ("CeltTables", "cache_bits50", CELT + "static_modes_float.h", "uint8", "CacheBits50"),
    ("CeltTables", "cache_caps50", CELT + "static_modes_float.h", "uint8", "CacheCaps50"),
    ("CeltTables", "fft_twiddles48000_960", CELT + "static_modes_float.h", "float", "FftTwiddles"),
    ("CeltTables", "fft_bitrev480", CELT + "static_modes_float.h", "int16", "FftBitrev480"),
    ("CeltTables", "fft_bitrev240", CELT + "static_modes_float.h", "int16", "FftBitrev240"),
    ("CeltTables", "fft_bitrev120", CELT + "static_modes_float.h", "int16", "FftBitrev120"),
    ("CeltTables", "fft_bitrev60", CELT + "static_modes_float.h", "int16", "FftBitrev60"),
    ("CeltTables", "mdct_twiddles960", CELT + "static_modes_float.h", "float", "MdctTwiddles"),
    ("CeltTables", "CELT_PVQ_U_DATA", CELT + "cwrs.c", "uint32", "PvqUData"),
]

HEADER = """// Generated by scripts/fixtures/opus_tables.py from libopus 1.5.2 (opus-1.5.2.tar.gz, SHA-256 65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1);
// do not edit. The tables are format data of RFC 6716's decoder (probability models, codebooks, band edges, windows, transform twiddles). Copyright (c) 2001-2023
// Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry, CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo, Microsoft Corporation,
// Amazon.com, Inc.; BSD-3-Clause, the notice in THIRD-PARTY-NOTICES.md. Every element's meaning is documented where the decoder uses the table.
#pragma warning disable SA1124
namespace Encspect.Formats.Opus;
"""


# The constants of silk/define.h that the tables name.
DEFINES = {"OFFSET_VL_Q10": 32, "OFFSET_VH_Q10": 100, "OFFSET_UVL_Q10": 100, "OFFSET_UVH_Q10": 240}


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def preprocess(text):
    """Evaluates the conditions of cwrs.c: CUSTOM_MODES is not defined, SMALL_FOOTPRINT is not defined."""
    out, stack = [], []
    for line in text.split("\n"):
        stripped = line.strip()
        if stripped.startswith("#if"):
            value = True
            if "defined(CUSTOM_MODES)" in stripped or "defined (CUSTOM_MODES)" in stripped:
                value = False
            elif stripped.startswith("#ifndef") or "!defined" in stripped:
                value = True
            elif stripped.startswith("#ifdef") or "defined" in stripped or stripped.startswith("#if "):
                value = False if re.search(r"CUSTOM_MODES|FIXED_POINT|HAVE_|ENABLE_|OPUS_ARM|SMALL_FOOTPRINT|MIPS", stripped) and "!" not in stripped else True
            stack.append([value, value, True])
            continue
        if stripped.startswith("#else"):
            top = stack[-1]
            top[0] = not top[1]
            continue
        if stripped.startswith("#endif"):
            stack.pop()
            continue
        if stripped.startswith("#"):
            continue
        if all(s[0] for s in stack):
            out.append(line)
    return "\n".join(out)


def balanced(text, start):
    """The text of the braces that open at text[start]."""
    depth, index = 0, start
    while True:
        c = text[index]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return text[start + 1:index]
        index += 1


def tokens(body):
    body = body.replace("{", " ").replace("}", " ")
    return [t.strip() for t in body.split(",") if t.strip()]


def find_table(text, name):
    for match in re.finditer(r"\b" + re.escape(name) + r"\s*(\[[^=;{]*\]\s*)+=\s*", text):
        brace = text.index("{", match.end() - 1)
        if text[match.end():brace].strip() == "":
            return tokens(balanced(text, brace))
    raise KeyError(name)


def literal(token, kind):
    token = token.strip()
    if kind == "float":
        value = token.rstrip("fF")
        float(value)
        return value + "f"
    # An integer expression of literals, as tables write -7651 or 39083 - 65536.
    expression = re.sub(r"(?<=[0-9a-fA-FxX])[uUlL]+\b", "", token)
    value = eval(expression, {"__builtins__": {}}, DEFINES)  # noqa: S307
    return str(value) + ("U" if kind == "uint32" else "")


def generate(root):
    cache = {}
    classes = {}
    for cls, cname, path, kind, csname in TABLES:
        if path not in cache:
            text = strip_comments((root / path).read_text(encoding="utf-8", errors="replace"))
            cache[path] = preprocess(text) if path.endswith("cwrs.c") else text
        try:
            items = find_table(cache[path], cname)
        except KeyError:
            raise SystemExit(f"{path}: no table {cname}")
        if cname.startswith("fft_twiddles"):
            items = [t for t in items]
        values = [literal(t, kind) for t in items]
        classes.setdefault(cls, []).append((cname, path, kind, csname, values))
    lines = [HEADER]
    for cls, tables in classes.items():
        lines.append(f"internal static class {cls}")
        lines.append("{")
        for cname, path, kind, csname, values in tables:
            lines.append(f"    // {path}, {cname}: {len(values)} values.")
            lines.append(f"    public static ReadOnlySpan<{C_TYPES[kind]}> {csname} => new {C_TYPES[kind]}[]")
            lines.append("    {")
            for i in range(0, len(values), 16):
                lines.append("        " + ", ".join(values[i:i + 16]) + ",")
            lines.append("    };")
            lines.append("")
        lines.pop()
        lines.append("}")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def main(argv):
    if argv[1:] not in (["--write"], ["--check"]):
        print(__doc__)
        return 2
    with tempfile.TemporaryDirectory() as directory:
        root = opus_tool.extract_libopus(directory)
        text = generate(root)
    if argv[1] == "--write":
        OUTPUT.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {OUTPUT}")
        return 0
    if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != text:
        print("OpusTables.cs differs from the tables of libopus 1.5.2; run opus_tables.py --write")
        return 1
    print(f"OpusTables.cs equals the tables of libopus 1.5.2 ({len(TABLES)} tables)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
