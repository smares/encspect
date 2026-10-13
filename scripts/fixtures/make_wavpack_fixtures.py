#!/usr/bin/env python3
"""Writes the hand-built WavPack fixtures in tests/fixtures/wavpack (built-*.wv) and their manifests.

Each is made from an encoder-written fixture of make_external_wavpack_fixtures.py by changing bytes: a
block cut short, bytes between blocks, a changed header field, a sub-block inserted, removed or made
to overrun its block, a changed flag, a tag after the blocks. Where a block changes, its checksum
sub-block is worked out again (unless the fixture is about the checksum), so the damage is the only
one. The standard library only: CI regenerates them from the committed encoder-written fixtures and
fails if the files differ.

The manifest of each is worked out by wavpack_model.py from the bytes, plus the findings the
analyzer must report and whether libwavpack's tools read the file as valid (crosscheck).
"""

import json
import pathlib
import struct

import wavpack_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "wavpack"


def base(name):
    return (DIRECTORY / f"external-{name}.wv").read_bytes()


def junk(length, seed=7):
    """Bytes of 0x80 to 0xFE: no signature of a block or a tag, and no ASCII."""
    out = bytearray()
    state = seed
    for _ in range(length):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(0x80 + (state >> 16) % 0x7F)
    return bytes(out)


def blocks(data):
    """The raw blocks of a file made of blocks only."""
    return [data[e["offset"]:e["offset"] + e["length"]] for e in m.walk(data, 0, len(data)) if e["kind"] == "block"]


def subs(block):
    """The sub-blocks of a block as raw bytes, header included."""
    found, left = m.sub_blocks(block)
    assert left == 0
    return [block[p:p + hl + size] for i, p, hl, size, length, clipped in found], [i for i, *_ in found]


def where(ids, function):
    """The place of the sub-block with this function among the raw ID bytes."""
    return [i & 0x3F for i in ids].index(function)


def sub(function, data, large=False):
    """A sub-block with this ID byte function and data, padded to an even length."""
    odd = len(data) % 2
    padded = data + b"\0" * odd
    ident = function | (0x40 if odd else 0) | (0x80 if large or len(padded) > 510 else 0)
    words = len(padded) // 2
    head = bytes([ident, words & 0xFF]) if ident < 0x80 else bytes([ident, words & 0xFF, words >> 8 & 0xFF, words >> 16 & 0xFF])
    return head + padded


def join(block, parts, checksum=True):
    """The block with these sub-blocks, its size field set and, with checksum, the checksum
    sub-block (already among the parts, in its last place) worked out again."""
    out = bytearray(block[:m.HEADER])
    body = b"".join(parts)
    struct.pack_into("<I", out, 4, m.HEADER + len(body) - 8)
    result = bytes(out) + body
    if checksum:
        found, _ = m.sub_blocks(result)
        i, p, hl, size, length, clipped = next(f for f in found if f[0] & 0x3F == 0x2F)
        total = m.checksum(result, p + hl - 2)
        stored = struct.pack("<I", total) if size == 4 else struct.pack("<H", (total ^ (total >> 16)) & 0xFFFF)
        result = result[:p + hl] + stored + result[p + hl + size:]
    return result


def patch(block, **fields):
    """The block with header fields changed, its checksum worked out again."""
    out = bytearray(block)
    layout = {"version": ("<H", 8), "index_high": ("<B", 10), "total_high": ("<B", 11), "total": ("<I", 12), "index": ("<I", 16), "samples": ("<I", 20),
              "flags": ("<I", 24)}
    for key, value in fields.items():
        fmt, at = layout[key]
        struct.pack_into(fmt, out, at, value)
    return join(bytes(out), subs(bytes(out))[0])


def flags(block):
    return struct.unpack_from("<I", block, 24)[0]


def checksum_start(block):
    found, _ = m.sub_blocks(block)
    return next(p for i, p, hl, size, length, clipped in found if i & 0x3F == 0x2F)


def with_flags(block, mask, value=None):
    """Flags with bits cleared (value None) or set."""
    f = flags(block)
    return patch(block, flags=(f & ~mask) if value is None else (f | mask))


def without(block, function):
    parts, ids = subs(block)
    return join(block, [p for p, i in zip(parts, ids) if i & 0x3F != function])


def insert_before_checksum(block, *new):
    parts, ids = subs(block)
    return join(block, parts[:-1] + list(new) + parts[-1:])


def id3v1():
    return b"TAG" + b"Hand-built title".ljust(30, b"\0") + b"Hand-built artist".ljust(30, b"\0") + b"Album".ljust(30, b"\0") + b"2026" + b"Comment".ljust(28, b"\0") + b"\0\x01\x11"


def id3v2():
    frame = b"TIT2" + (6).to_bytes(4, "big") + b"\0\0" + b"\0Title"
    size = len(frame)
    return b"ID3\x03\0\0" + bytes([size >> 21 & 0x7F, size >> 14 & 0x7F, size >> 7 & 0x7F, size & 0x7F]) + frame


# name: (description, builder, findings, valid). A builder takes no arguments and returns the file's bytes.
# valid: whether wvunpack and ffprobe read the file as they would an undamaged one (None: not applicable).
def A():
    return blocks(base("lossless-16-stereo"))


def build():
    b = {}
    a = A()
    aa = b"".join

    b["truncated-last-block"] = ("The last block cut 1,000 bytes before its end.", aa(a)[:-1000],
                                 [("wavpack.block-truncated", "states")], False)
    b["truncated-header"] = ("The file cut ten bytes into the third block's header.", aa(a[:2]) + a[2][:10],
                             [("wavpack.block-truncated", "inside the header"), ("wavpack.total-samples-mismatch", "holds")], False)
    bad = bytearray(a[1])
    bad[200] ^= 0x10
    b["bad-checksum"] = ("One byte of the second block's audio changed, so its checksum no longer fits.", aa([a[0], bytes(bad), a[2]]),
                         [("wavpack.block-checksum-mismatch", "not the one worked out")], False)
    parts, ids = subs(a[1])
    dummy = sub(0, bytes(len(parts[-1]) - 2))
    b["checksum-missing"] = ("The second block says it ends with a checksum, but its checksum sub-block is a dummy.", aa([a[0], join(a[1], parts[:-1] + [dummy], checksum=False), a[2]]),
                             [("wavpack.block-checksum-missing", "holds none")], False)
    b["garbage-between-blocks"] = ("37 bytes that are no block between the first and the second block.", aa([a[0], junk(37), a[1], a[2]]),
                                   [("wavpack.data-skipped", "37 bytes")], True)
    b["garbage-prefix"] = ("100 bytes that are no block before the first block.", junk(100) + aa(a), [("wavpack.data-skipped", "100 bytes")], True)
    b["trailing-garbage"] = ("50 bytes that are no block after the last block.", aa(a) + junk(50), [("wavpack.data-skipped", "50 bytes")], True)
    b["bad-signature"] = ("The signature of the second block changed to \"wvpx\".", aa([a[0], a[1][:3] + b"x" + a[1][4:], a[2]]),
                          [("wavpack.data-skipped", f"{len(a[1])} bytes"), ("wavpack.block-index-gap", "samples are missing"), ("wavpack.total-samples-mismatch", "holds")], False)
    b["missing-first-block"] = ("The first block left out, as in a part cut from a longer stream.", aa(a[1:]),
                                [("wavpack.first-block-index", "begins at sample 11025")], True)
    b["block-index-gap"] = ("The second block left out.", aa([a[0], a[2]]),
                            [("wavpack.block-index-gap", "samples are missing"), ("wavpack.total-samples-mismatch", "holds")], False)
    b["block-index-overlap"] = ("The second block twice.", aa([a[0], a[1], a[1], a[2]]),
                                [("wavpack.block-index-overlap", "samples overlap"), ("wavpack.total-samples-mismatch", "holds")], False)
    big = bytearray(a[0])
    struct.pack_into("<I", big, 4, struct.unpack_from("<I", big, 4)[0] + 100)
    b["block-size-too-large"] = ("The first block states 100 bytes more than it has.", aa([bytes(big), a[1], a[2]]),
                                 [("wavpack.block-size-mismatch", "another block begins"), ("wavpack.block-checksum-mismatch", "not the one worked out")], False)
    small = bytearray(a[0])
    struct.pack_into("<I", small, 4, struct.unpack_from("<I", small, 4)[0] - 50)
    b["block-size-too-small"] = ("The first block states 50 bytes less than it has.", aa([bytes(small), a[1], a[2]]),
                                 [("wavpack.subblock-overrun", "are left in the block"), ("wavpack.block-checksum-missing", "holds none"), ("wavpack.data-skipped", "50 bytes")], False)
    trail = a[0] + b"\x55"
    trail = bytearray(trail)
    struct.pack_into("<I", trail, 4, len(trail) + 1 - 8)
    b["block-trailing-byte"] = ("The first block has a byte after its last sub-block, and states two.", aa([bytes(trail), a[1], a[2]]),
                                [("wavpack.block-size-mismatch", "another block begins"), ("wavpack.block-trailing-byte", "holds no sub-block"),
                                 ("wavpack.block-checksum-mismatch", "not the one worked out")], False)
    over = bytearray(a[1])
    found, _ = m.sub_blocks(a[1])
    stream = next(p for i, p, hl, size, length, clipped in found if i & 0x3F == 0x0A)
    over[stream + 1:stream + 4] = b"\xFF\xFF\x7F"
    b["subblock-overrun"] = ("The bitstream of the second block states 16,777,214 bytes.", aa([a[0], bytes(over), a[2]]),
                             [("wavpack.subblock-overrun", "states 16777214 bytes"), ("wavpack.block-checksum-missing", "holds none")], False)
    b["unknown-subblocks"] = ("A sub-block that is not optional and one that is, both unknown, in two blocks.",
                              aa([insert_before_checksum(a[0], sub(0x1E, b"\1\2\3\4")), insert_before_checksum(a[1], sub(0x3E, b"\5\6\7\x08")), a[2]]),
                              [("wavpack.subblock-unknown", "function 0x1E"), ("wavpack.optional-subblock-unknown", "function 0x3E")], False)
    b["version-unsupported"] = ("Stream version 0x411 in all three blocks.", aa([patch(x, version=0x411) for x in a]),
                                [("wavpack.version-unsupported", "0x411")], False)
    b["reserved-flag"] = ("Bit 27, which is reserved, set in the first block.", aa([with_flags(a[0], 0x08000000, True), a[1], a[2]]),
                          [("wavpack.reserved-flag", "reserved flag")], True)
    mono = blocks(base("lossless-16-mono"))
    b["flags-invalid"] = ("The mono flag and the false stereo flag both set in the first block of a mono file.", aa([with_flags(mono[0], 0x40000000, True), *mono[1:]]),
                          [("wavpack.flags-invalid", "both set")], False)
    b["set-without-final"] = ("The second block without the final block flag.", aa([a[0], with_flags(a[1], 0x1000), a[2]]),
                              [("wavpack.block-set-broken", "ends without a final block")], False)
    b["set-without-initial"] = ("The second block without the initial block flag.", aa([a[0], with_flags(a[1], 0x800), a[2]]),
                                [("wavpack.block-set-broken", "without the initial block flag")], False)
    terms = bytearray(a[0])
    found, _ = m.sub_blocks(a[0])
    at = next(p + hl for i, p, hl, size, length, clipped in found if i & 0x3F == 2)
    terms[at] = 5
    b["decorr-terms-invalid"] = ("A decorrelation pass with the term 0 in the first block.", aa([join(bytes(terms), subs(bytes(terms))[0]), a[1], a[2]]),
                                 [("wavpack.decorr-terms-invalid", "not all valid")], False)
    b["audio-without-bitstream"] = ("The bitstream sub-block left out of the third block.", aa([a[0], a[1], without(a[2], 0x0A)]),
                                    [("wavpack.audio-without-bitstream", "holds no bitstream")], False)
    b["total-samples-mismatch"] = ("The first block says the file holds 30,000 samples.", aa([patch(a[0], total=30000), a[1], a[2]]),
                                   [("wavpack.total-samples-mismatch", "30000")], False)
    b["total-samples-unknown"] = ("The first block says the length is unknown.", aa([patch(a[0], total=0xFFFFFFFF), a[1], a[2]]),
                                  [("wavpack.total-samples-unknown", "-1")], True)
    b["total-samples-40-bit"] = ("The first block's total samples use the upper byte: 0x1 and 0x10.", aa([patch(a[0], total=0x10, total_high=1), a[1], a[2]]),
                                 [("wavpack.total-samples-mismatch", "4294967311")], False)
    b["block-index-40-bit"] = ("The block indexes use the upper byte: 0x1 in every block.", aa([patch(x, index_high=1, total=0xFFFFFFFF if i == 0 else struct.unpack_from("<I", x, 12)[0]) for i, x in enumerate(a)]),
                               [("wavpack.first-block-index", "begins at sample 4294967296")], True)
    dsd = blocks(base("dsd-64"))
    dsd_parts, dsd_ids = subs(dsd[0])
    at = where(dsd_ids, 0x0E)
    dsd_bad = bytearray(dsd_parts[at])
    dsd_bad[5] = 2
    dsd_parts[at] = bytes(dsd_bad)
    b["dsd-invalid"] = ("The DSD block of the first block with a mode of 2.", aa([join(dsd[0], dsd_parts), *dsd[1:]]),
                        [("wavpack.dsd-invalid", "mode is 2")], False)
    surround = blocks(base("lossless-51-16"))
    for label, count, mask, text, finding in (("zero", 0, 0x3F, "Channel info with no channels.", ("wavpack.channel-info-invalid", "refuses")),
                                              ("four", 4, 0x33, "Channel info for four channels in a file of six.", ("wavpack.channel-count-mismatch", "says 4 channels"))):
        parts, ids = subs(surround[0])
        parts[where(ids, 0x0D)] = sub(0x0D, bytes([count, mask]))
        b[f"channel-info-{label}"] = (text, aa([join(surround[0], parts), *surround[1:]]), [finding], False)
    digest = bytes(range(16))
    meta = join(patch(a[0], samples=0, index=0, flags=0x34BC1E59), [sub(0x26, digest), sub(0x2F, bytes(4))])
    b["no-audio"] = ("A single block with no samples, which holds an MD5 digest.", meta, [("wavpack.no-audio", "None of the 1 blocks counts a sample")], None)
    rate = blocks(base("rate-12345"))
    b["sample-rate-missing"] = ("The sample rate sub-block left out of a file of 12,345 Hz.", aa([without(rate[0], 0x27)]),
                                [("wavpack.sample-rate-missing", "44100 Hz")], True)
    pair = blocks(base("lossless-4ch-two-unassigned"))
    parts, ids = subs(pair[0])
    parts.insert(where(ids, 0x0D) + 1, sub(0x2B, b"\xFF\xFF"))
    b["channel-identities"] = ("Identities of the two channels the channel mask names none for.", aa([join(pair[0], parts), *pair[1:]]), [], True)
    b["dummy-and-large-subblocks"] = ("A dummy sub-block and a large, odd-sized one in the first block.",
                                      aa([insert_before_checksum(a[0], sub(0, bytes(6)), sub(0x22, b"LIST" + (689).to_bytes(4, "little") + bytes(689), large=True)), a[1], a[2]]),
                                      [], True)
    wide = blocks(base("lossless-32-stereo"))
    b["extra-bitstream-missing"] = ("The extra bitstreams of a 32-bit integer file left out.", aa([without(x, 0x0C) for x in wide]), [], False)
    b["id3v1"] = ("An ID3v1 tag after the blocks.", aa(a) + id3v1(), [], True)
    ape = base("apev2-tag")
    b["ape-and-id3v1"] = ("An ID3v1 tag after the APEv2 tag.", ape + id3v1(), [("ape.items-unsorted", "is smaller than the item before it")], True)
    tail = bytearray(aa(a))
    tail[-128:-125] = b"TAG"
    tail[-125:-95] = b"Inside the last block".ljust(30, b"\0")
    last = bytes(tail[len(aa(a[:2])):])
    b["tag-inside-block"] = ("Bytes that look like an ID3v1 tag 128 bytes before the end, inside the last block's audio.", aa(a[:2]) + join(last, subs(last)[0]),
                              [("tag.near-miss", "ID3v1 signature")], False)
    b["leading-id3v2"] = ("An ID3v2 tag before the blocks.", id3v2() + aa(a), [], True)
    return b


def regions(data, name):
    """The top-level nodes."""
    if name in ("built-id3v1",):
        return [("WavPack", 0, len(data) - 128), ("ID3v1", len(data) - 128, 128)]
    if name == "built-ape-and-id3v1":
        tag = data.rindex(b"APETAGEX", 0, len(data) - 160)
        return [("WavPack", 0, tag), ("APE", tag, len(data) - 128 - tag), ("ID3v1", len(data) - 128, 128)]
    if name == "built-leading-id3v2":
        size = len(id3v2())
        return [("ID3v2", 0, size), ("WavPack", size, len(data) - size)]
    return [("WavPack", 0, len(data))]


def main():
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, (description, data, findings, valid) in build().items():
        full = f"built-{name}"
        (DIRECTORY / f"{full}.wv").write_bytes(data)
        top = regions(data, full)
        start = top[1][1] if full == "built-leading-id3v2" else 0
        end = top[0][1] + top[0][2] if full != "built-leading-id3v2" else len(data)
        result = m.manifest(data, start, end, {"regions": [{"name": n, "offset": o, "length": l} for n, o, l in top], "description": description,
                                               "findings": [{"code": c, "contains": t} for c, t in findings], "crosscheck": {"valid": valid}})
        (DIRECTORY / f"{full}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"Wrote {count} hand-built fixtures to {DIRECTORY}")


if __name__ == "__main__":
    main()
