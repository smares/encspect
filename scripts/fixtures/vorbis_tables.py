#!/usr/bin/env python3
"""Checks the table of floor 1 amplitudes of the Vorbis decoder (src/Encspect.Formats.Vorbis/Decoding/FloorTable.cs) against the Vorbis I specification, section 10.1,
"floor1_inverse_dB_table": the 256 values, read from the specification's text (pinned by SHA-256 in vorbis_tool.py), equal the committed ones as single-precision floats.

    vorbis_tables.py --check

The table is format data of the specification, not code of an implementation (docs/development.md, "Where the format tables come from"). Exit status 0 where they agree.
"""

import html
import pathlib
import re
import struct
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import vorbis_tool  # noqa: E402

TABLE = HERE.parents[1] / "src" / "Encspect.Formats.Vorbis" / "Decoding" / "FloorTable.cs"


def as_float(text):
    return struct.unpack("f", struct.pack("f", float(text)))[0]


def specification_values(path):
    text = path.read_text(encoding="utf-8", errors="replace")
    text = html.unescape(re.sub(r"<[^>]+>", "", re.sub(r"<(script|style).*?</\\1>", "", text, flags=re.S)))
    at = [m.start() for m in re.finditer("floor1_inverse_dB_table", text)][-1]
    body = text[at:at + 12000]
    # The listing numbers its lines (a line number before each row); the numbers have no decimal point, the values do.
    values = re.findall(r"(?<![0-9.])[0-9]\.[0-9]+(?:e-[0-9]+)?", re.sub(r"(?m)^\d+ +", "", body))
    return [as_float(v) for v in values[:256]]


def committed_values():
    text = TABLE.read_text(encoding="utf-8")
    return [as_float(v) for v in re.findall(r"([0-9]\.?[0-9]*(?:e-[0-9]+)?)f", text[text.index("Amplitude"):])]


def main(argv):
    if argv[1:] != ["--check"]:
        print(__doc__)
        return 2
    with tempfile.TemporaryDirectory() as directory:
        expected = specification_values(vorbis_tool.fetch_spec(directory))
    actual = committed_values()
    if len(expected) != 256 or actual != expected[:len(actual)] or len(actual) != 256:
        # The specification prints the last value as 1.0000000, which the pattern may split; compare the first 255 and the last by value.
        if not (len(actual) == 256 and actual[:255] == expected[:255] and actual[255] == 1.0):
            print(f"FloorTable.cs differs from the specification's table: {len(actual)} values, {len(expected)} read")
            return 1
    print("floor1_inverse_dB_table: 256 values equal the specification's")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
