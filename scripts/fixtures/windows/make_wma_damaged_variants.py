#!/usr/bin/env python3
"""Writes WMA fixtures with a cut media object and with damage from committed Windows files (docs/design/samples-definition.md, "Lost samples").

    python3 make_wma_damaged_variants.py <fixtures folder>

Four files, each a committed Windows file with bytes of its Data Object changed, nothing else (the File Properties object, the counts and
the stated sizes stay the source's, so the findings that follow are the container's):

- derived-windows-wma92-stereo-44k-128-object-cut.asf: external-windows-wma92-cbr-stereo-44k-128.wma cut 2,500 bytes into its last media object.
- derived-windows-wma92lossless-stereo-44k-16-object-cut.asf: external-windows-wma92lossless-stereo-44k-16.wma cut 6,000 bytes into its last media object.
- derived-windows-wma92-stereo-44k-128-damaged.asf: the same standard file with 300 bytes of its fifth media object overwritten with 0xFF from byte 1,000 on.
- derived-windows-wma92lossless-stereo-44k-16-damaged.asf: the same Lossless file with the sequence number of its seventh media object changed, so that a
  packet seems to be lost.

Not encoders' output and never decoded by Windows. The bytes are deterministic.
"""

import hashlib
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
import asf_model  # noqa: E402

STANDARD = "external-windows-wma92-cbr-stereo-44k-128.wma"
LOSSLESS = "external-windows-wma92lossless-stereo-44k-16.wma"


def objects(raw):
    return asf_model.parse(raw)["media_objects"]


def cut_standard(raw):
    start = objects(raw)[-1][4][0][0]
    return raw[:start + 2500]


def cut_lossless(raw):
    start = objects(raw)[-1][4][0][0]
    return raw[:start + 6000]


def damage_standard(raw):
    start = objects(raw)[4][4][0][0]
    out = bytearray(raw)
    out[start + 1000:start + 1300] = b"\xff" * 300
    return bytes(out)


def damage_lossless(raw):
    start = objects(raw)[6][4][0][0]
    out = bytearray(raw)
    out[start] = (out[start] & 0x0F) | (((out[start] >> 4) + 5) & 15) << 4
    return bytes(out)


VARIANTS = [
    ("derived-windows-wma92-stereo-44k-128-object-cut", STANDARD, cut_standard,
     "external-windows-wma92-cbr-stereo-44k-128.wma cut 2,500 bytes into its last media object: the container hands the cut object over, flagged cut."),
    ("derived-windows-wma92lossless-stereo-44k-16-object-cut", LOSSLESS, cut_lossless,
     "external-windows-wma92lossless-stereo-44k-16.wma cut 6,000 bytes into its last media object: the container hands the cut object over, flagged cut."),
    ("derived-windows-wma92-stereo-44k-128-damaged", STANDARD, damage_standard,
     "external-windows-wma92-cbr-stereo-44k-128.wma with 300 bytes of its fifth media object overwritten with 0xFF from byte 1,000 on."),
    ("derived-windows-wma92lossless-stereo-44k-16-damaged", LOSSLESS, damage_lossless,
     "external-windows-wma92lossless-stereo-44k-16.wma with the sequence number of its seventh media object changed, so that a packet seems to be lost."),
]


def provenance(name, source, description, data, source_provenance):
    return {
        "description": description + " Not an encoder's output and never decoded by Windows.",
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "reproducible": True,
        "note": "Written by the script from the committed source; running it again gives the same bytes.",
        "derived": {
            "from": source,
            "tool": "scripts/fixtures/windows/make_wma_damaged_variants.py",
            "note": "Not an encoder's output: the source's bytes with a cut or damage added.",
            "source": {k: x for k, x in source_provenance.items() if k not in ("description", "bytes", "sha256", "note", "derived", "reproducible")},
        },
    }


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    folder = pathlib.Path(sys.argv[1])
    for name, source, build, description in VARIANTS:
        data = build((folder / source).read_bytes())
        (folder / (name + ".asf")).write_bytes(data)
        source_record = json.loads((folder / (source[:-4] + ".provenance.json")).read_text(encoding="utf-8"))
        record = provenance(name, source, description, data, source_record)
        (folder / (name + ".provenance.json")).write_text(json.dumps(record, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{name}: {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
