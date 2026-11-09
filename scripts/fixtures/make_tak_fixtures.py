#!/usr/bin/env python3
"""Writes the hand-built TAK fixtures in tests/fixtures/tak (built-*.tak) from the committed external-preset-p2.tak, which Takc.exe 2.3.3 wrote
(docs/design/tak-optimfrog-headers.md, "Hand-built"). The objects and frames are the encoder's; only the bytes around them are written here: a failing CRC-24, a cut
file, an object that runs past the data, a missing End object, an unknown object, a sync-like object, a stream info that is not first, a missing last frame info, bytes after
the last frame and an ID3v2 tag before the stream ID. The manifests come from tak_model.py, which reads takStream.pas and not the analyzer.

    python3 scripts/fixtures/make_tak_fixtures.py
"""

import json
import pathlib
import struct

import tak_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "tak"
SOURCE = OUT / "external-preset-p2.tak"
SCRIPT = "scripts/fixtures/make_tak_fixtures.py"


def objects_of(base):
    """The objects of the source as (id, bytes) and the offset where the audio starts."""
    entries, audio = m.walk(base)
    return [(o["id"], base[o["offset"]:o["end"]]) for o in entries], audio


def build(parts, audio_bytes):
    return b"tBaK" + b"".join(parts) + audio_bytes


def with_crc_flipped(data):
    """The object with its CRC-24 (the last 3 bytes) changed."""
    return data[:-3] + bytes(b ^ 0x5A for b in data[-3:])


def id3v2():
    frame = b"TIT2" + struct.pack(">I", 6) + bytes(2) + b"\x00Song\x00"
    size = len(frame)
    return b"ID3\x03\x00\x00" + bytes([(size >> 21) & 127, (size >> 14) & 127, (size >> 7) & 127, size & 127]) + frame


def header(object_id, size, reserved=0):
    return struct.pack("<I", object_id | (reserved << 7) | (size << 8))


def cases():
    base = SOURCE.read_bytes()
    objects, audio = objects_of(base)
    audio_bytes = base[audio:]
    by_id = {i: b for i, b in objects}
    order = [i for i, _ in objects]
    stream_info, last_frame, encoder_info, wave_data, end = by_id[1], by_id[7], by_id[4], by_id[3], by_id[0]

    def make(replacements=None, drop=(), extra_before_end=b"", cut=None, tail=b"", sequence=None, keep_end=True, prefix=b""):
        parts = []
        for i in (sequence or order):
            if i in drop or (i == 0 and not keep_end):
                continue
            body = (replacements or {}).get(i, by_id[i])
            parts.append(extra_before_end + body if i == 0 else body)
        data = prefix + build(parts, audio_bytes) + tail
        return data if cut is None else data[:cut]

    wave_at = 4 + len(stream_info) + len(last_frame) + len(encoder_info)
    sync_like = header(127, 160, reserved=1) + bytes(160)
    unknown = header(20, 10) + bytes(10)
    return {
        "built-crc-stream-info": (make({1: with_crc_flipped(stream_info)}), "the CRC-24 of the stream info changed"),
        "built-crc-encoder-info": (make({4: with_crc_flipped(encoder_info)}), "the CRC-24 of the encoder info changed"),
        "built-crc-last-frame-info": (make({7: with_crc_flipped(last_frame)}), "the CRC-24 of the last frame info changed, so there is no declared end"),
        "built-cut-metadata": (make(cut=wave_at + 30), "the data ends 30 bytes into the wave data object"),
        "built-cut-audio": (make(cut=len(base) - 1000), "the last 1000 bytes of the audio are missing"),
        "built-object-past-data": (make({3: header(3, 1_000_000) + wave_data[4:]}), "the wave data object states 1,000,000 bytes"),
        "built-end-missing": (make(keep_end=False), "the End object is removed, so the first frame follows the last object"),
        "built-sync-like-object": (make(extra_before_end=sync_like), "an object with ID 127 and the reserved bit set before the End object: its bytes begin FF A0 but are no frame header"),
        "built-unknown-object": (make(extra_before_end=unknown), "an object with ID 20 before the End object"),
        "built-stream-info-not-first": (make(sequence=[3, 1, 7, 4, 0]), "the wave data object comes before the stream info"),
        "built-no-last-frame-info": (make(drop=(7,)), "the last frame info is removed, so there is no declared end"),
        "built-data-after-frames": (make(tail=b"these bytes are no tag at all"), "29 bytes after the last frame that are no tag"),
        "built-id3v2-before": (make(prefix=id3v2()), "an ID3v2.3 tag before the stream ID"),
    }


def main():
    for name, (data, what) in cases().items():
        (OUT / f"{name}.tak").write_bytes(data)
        extra = {"built": {"from": SOURCE.name, "script": SCRIPT, "change": what}}
        (OUT / f"{name}.expected.json").write_text(json.dumps(m.manifest(data, extra), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        (OUT / f"{name}.tak.provenance.json").write_text(json.dumps({"source": f"{SOURCE.name} changed by {SCRIPT}", "change": what, "size": len(data)}, indent=1) + "\n", encoding="utf-8")
    print(f"Wrote {len(cases())} files into {OUT}")


if __name__ == "__main__":
    main()
