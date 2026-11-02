#!/usr/bin/env python3
"""Writes a WMA Professional fixture without the length prefix from a committed one that has it.

    python3 make_wma_pro_no_length_prefix.py <fixtures folder>

The source is external-windows-wma10pro-cbr-stereo-44k-128.wma. Its frames are found by their length prefixes
(docs/design/wma-pro-format.md, "Packets" and "Frame"), each prefix is removed (the skipped bit before the trailer bit ends a
frame, as the format description has it for a stream without the prefix), the trailer bit of every frame is set but the last
frame's, and the frames are joined and cut again into packets of the block alignment, each with its sequence number, its two
bits and the count of bits that finish the frame before. The last packet is padded with zero bits; the decode flags of the codec
data lose the length prefix bit; the media objects keep the headers of the source's first packets, and the counts of the file
are fixed. Writes <fixtures folder>/derived-windows-wma10pro-stereo-44k-128-no-length-prefix.asf and its provenance.json (make_asf_fixtures.py writes the same file with its manifest).
The bytes are deterministic: the source's File Properties object (file ID, creation date) is kept.

Not an encoder's output: the frames are the source's. The streams of a writer without the prefix are in the public files of
tests/conformance/wma-samples.json (Classical_44_16_6_256000_0_20.wma and two more), none committed.
"""

import hashlib
import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import make_wma_pro_variants as v  # noqa: E402

SOURCE = "external-windows-wma10pro-cbr-stereo-44k-128.wma"
NAME = "derived-windows-wma10pro-stereo-44k-128-no-length-prefix"


def rewrite(raw):
    """The bytes of the source written without the length prefix, and the figures of the rewrite (frames, packets)."""
    pro = v.ProFile(raw)
    w = pro.w
    header = 6 + w
    frames = pro.walk()
    packets = [pro.packet_bits(i).s for i in range(len(pro.payloads))]
    stripped = []
    starts = []
    for k, (s_index, s_pos, s_len, e_index, e_pos) in enumerate(frames):
        if s_index == e_index:
            bits = packets[s_index][s_pos:s_pos + s_len]
        else:
            bits = packets[s_index][s_pos:] + packets[e_index][header:e_pos]
        assert len(bits) == s_len, (k, len(bits), s_len)
        starts.append(sum(len(x) for x in stripped))
        stripped.append(bits[w:-1] + ("0" if k == len(frames) - 1 else "1"))
    stream = "".join(stripped)
    size = pro.block_align * 8 - header
    count = -(-len(stream) // size)
    out = bytearray(raw)
    for k in range(count):
        offset, length = pro.payloads[k]
        payload = packets[k][:4 + 2]
        chunk = stream[k * size:(k + 1) * size]
        following = [x for x in starts if x >= k * size]
        nxt = following[0] if following else len(stream)
        payload += format(min(nxt - k * size, size), f"0{w}b") + chunk
        payload += "0" * (length * 8 - len(payload))
        out[offset:offset + length] = int(payload, 2).to_bytes(length, "big")
    packet_start, packet_size, _ = pro.packets[count - 1]
    del out[packet_start + packet_size:]
    pro.data = out
    v.fix_counts(pro, count, len(out))
    codec = bytes.fromhex(next(x for x in v.asf_model.parse(raw)["streams"] if x.get("tag") == 0x162)["codec_data"])
    at = bytes(out).find(codec)
    assert at >= 0 and bytes(out).count(codec) == 1
    assert out[at + 14] & 0x40
    out[at + 14] &= 0xBF
    return bytes(out), {"frames": len(frames), "packets": count}


DESCRIPTION = (SOURCE + " written without the length prefix by make_wma_pro_no_length_prefix.py: the same frames, joined without their prefixes and cut into packets again, "
               "and the length prefix bit of the decode flags cleared. Not an encoder's output and never decoded by Windows; it holds the frames of Media Foundation's transcoder "
               "(derived.source) in the layout of a writer without the prefix, so the frames that start in the last packet, which a decoder reads, are covered.")


def derived(figures):
    return {"from": SOURCE, "tool": "scripts/fixtures/windows/make_wma_pro_no_length_prefix.py", "note": "Not an encoder's output: the source's frames in another layout.", **figures}


def provenance(data, figures, source_provenance):
    """The record next to the fixture: what it is and where it comes from; the source's own record is nested under derived.source."""
    return {
        "description": DESCRIPTION,
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "reproducible": True,
        "note": "Written by the script from the committed source; running it again gives the same bytes. The File Properties object's file ID and creation date are the source's.",
        "derived": {**derived(figures), "source": {k: x for k, x in source_provenance.items() if k not in ("description", "bytes", "sha256", "note", "derived", "reproducible")}},
    }


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    folder = pathlib.Path(sys.argv[1])
    data, figures = rewrite((folder / SOURCE).read_bytes())
    (folder / (NAME + ".asf")).write_bytes(data)
    record = provenance(data, figures, json.loads((folder / (SOURCE[:-4] + ".provenance.json")).read_text(encoding="utf-8")))
    (folder / (NAME + ".provenance.json")).write_text(json.dumps(record, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{NAME}: {figures['frames']} frames in {figures['packets']} packets, {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
