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


def id3v2_seek(distance):
    """An ID3v2.4 tag whose SEEK frame says the next tag starts distance bytes after this one's end."""
    seek = b"SEEK" + (4).to_bytes(4, "big") + b"\0\0" + distance.to_bytes(4, "big")
    size = len(seek)
    return b"ID3\x04\0\0" + bytes([size >> 21 & 0x7F, size >> 14 & 0x7F, size >> 7 & 0x7F, size & 0x7F]) + seek


def id3v2_title():
    frame = b"TIT2" + (6).to_bytes(4, "big") + b"\0\0" + b"\0Title"
    size = len(frame)
    return b"ID3\x04\0\0" + bytes([size >> 21 & 0x7F, size >> 14 & 0x7F, size >> 7 & 0x7F, size & 0x7F]) + frame


def ape_tag(*items):
    """An APEv2 tag with a header and a footer, its text items (key, value) sorted ascending by size."""
    body = b"".join(struct.pack("<II", len(value), 0) + key.encode() + b"\0" + value.encode() for key, value in sorted(items, key=lambda kv: len(kv[0]) + len(kv[1])))
    size = len(body) + 32
    block = lambda flags: b"APETAGEX" + struct.pack("<IIII", 2000, size, len(items), flags) + bytes(8)
    return block(0xA0000000) + body + block(0x80000000)


def dsd_with(block, data):
    """The first block of a DSD file with its DSD sub-block replaced by one of this data after the rate
    power byte (which keeps its value)."""
    parts, ids = subs(block)
    at = where(ids, 0x0E)
    raw = parts[at]
    power = raw[4 if raw[0] & 0x80 else 2]
    parts[at] = sub(0x0E, bytes([power]) + data)
    return join(block, parts)


def dsd_rate_overflow(block):
    """A DSD block of rate index 15, a sample rate sub-block of 2,147,483,647 Hz and a rate power of 31."""
    block = patch(block, flags=flags(block) | (0xF << 23))
    parts, ids = subs(block)
    at = where(ids, 0x0E)
    raw = parts[at]
    hl = 4 if raw[0] & 0x80 else 2
    parts[at] = raw[:hl] + bytes([31]) + raw[hl + 1:]
    parts.insert(at, sub(0x27, b"\xff\xff\xff\x7f"))
    return join(block, parts)


# What libwavpack's decoder does with each file: WavpackOpenFileInputEx64 as wvunpack opens it (block checksums verified), then
# WavpackUnpackSamples to the end. crosscheck_wavpack_fixtures.py runs both and fails on any difference. None: libwavpack refuses to
# open the file, because the init or metadata code of a block rejects it (a decorrelation term of 0, weights without terms, a DSD block of
# a mode or size it cannot read, a layout it cannot take, an unknown sub-block that is not optional, a stream version it does not
# decode). Else (samples unpacked, errors counted): an error is a block it skips, or a checksum or CRC that fails; a cut file decodes
# up to the cut.
DECODES = {
    "ape-and-id3v1": (24000, 0),
    "ape-encoder-claim": (24000, 0),
    "ape-encoder-wavpack": (24000, 0),
    "audio-without-bitstream": (24000, 2),
    "bad-checksum": (24000, 1),
    "bad-signature": (24000, 1),
    "block-index-40-bit": (24000, 0),
    "block-index-gap": (24000, 1),
    "block-index-overlap": (24000, 1),
    "block-size-too-large": (6488, 1),
    "block-size-too-small": (12975, 1),
    "block-trailing-byte": (6488, 1),
    "channel-identities": (12000, 0),
    "channel-info-four": (24000, 2),
    "channel-info-ignored": (24000, 0),
    "channel-info-no-count": (24000, 0),
    "channel-info-zero": None,
    "channel-info-after-first-block": (24000, 0),
    "channel-info-after-bad-block": (12975, 1),
    "channels-change": (24000, 2),
    "checksum-missing": (24000, 1),
    "decorr-terms-invalid": None,
    "dsd-fast-history-only": None,
    "dsd-high-rate-step": None,
    "dsd-high-short": None,
    "dsd-invalid": None,
    "dsd-rate-overflow": (65536, 0),
    "dsd-stored-mode": (65536, 1),
    "dsd-stored-size": None,
    "dummy-and-large-subblocks": (24000, 0),
    "extra-bitstream-missing": (24000, 0),
    "flags-invalid": None,
    "garbage-between-blocks": (24000, 0),
    "garbage-prefix": (24000, 0),
    "id3v1": (24000, 0),
    "leading-id3v2": (24000, 0),
    "missing-first-block": (12975, 0),
    "no-audio": None,
    "reorder-too-long": None,
    "reserved-flag": (24000, 0),
    "sample-rate-missing": (12000, 0),
    "sample-rate-zero-custom": (12000, 0),
    "sample-rate-zero": (24000, 0),
    "seek-tag-between-blocks": (24000, 0),
    "set-without-final-limit": (1000, 23),
    "set-without-final": (24000, 0),
    "set-without-initial": (24000, 1),
    "shift-too-large": (24000, 0),
    "subblock-overrun": (24000, 1),
    "subblock-sizes-mixed": None,
    "tag-inside-block": (24000, 1),
    "total-samples-40-bit": (24000, 0),
    "total-samples-mismatch": (24000, 0),
    "total-samples-unknown": (24000, 0),
    "trailing-garbage": (24000, 0),
    "truncated-header": (17512, 0),
    "truncated-last-block": (17512, 0),
    "unknown-subblocks": None,
    "version-unsupported": None,
    "weights-without-terms": (24000, 2),
}


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
    # The other sets' channel infos, which say 6 channels, are ignored once the first one gives channels (libwavpack keeps the first).
    for label, count, mask, text, found in (("zero", 0, 0x3F, "Channel info with no channels.",
                                             [("wavpack.channel-info-invalid", "refuses"), ("wavpack.channel-info-ignored", "gives 6 channels and a mask of 0x3F, but the flags of the first audio block that verifies gave 2 channels")]),
                                            ("four", 4, 0x33, "Channel info for four channels in a file of six.",
                                             [("wavpack.channel-count-mismatch", "says 4 channels"), ("wavpack.channel-info-ignored", "gives 6 channels and a mask of 0x3F, but an earlier one gave 4 channels")])):
        parts, ids = subs(surround[0])
        parts[where(ids, 0x0D)] = sub(0x0D, bytes([count, mask]))
        b[f"channel-info-{label}"] = (text, aa([join(surround[0], parts), *surround[1:]]), found, False)
    b["dsd-rate-overflow"] = ("A DSD file of 2,147,483,647 Hz with a rate power of 31, whose bit rate overflows 32 bits.", aa([dsd_rate_overflow(x) for x in dsd]),
                              [("wavpack.sample-rate-implausible", "more than the 2147483647 Hz")], False)
    samples = struct.unpack_from("<I", dsd[0], 20)[0]
    channels = 1 if flags(dsd[0]) & (m.MONO | m.FALSE_STEREO) else 2
    b["dsd-stored-mode"] = ("The first DSD block in the stored mode with exactly the bytes it counts.", aa([dsd_with(dsd[0], b"\0" + bytes(samples * channels)), *dsd[1:]]),
                            [], False)
    b["dsd-stored-size"] = ("The first DSD block in the stored mode with 37 bytes of audio, not the bytes it counts.", aa([dsd_with(dsd[0], b"\0" + bytes(37)), *dsd[1:]]),
                            [("wavpack.dsd-invalid", "its stored mode holds 37 bytes")], False)
    b["dsd-fast-history-only"] = ("The first DSD block in the fast mode with data that ends right after the history bits.", aa([dsd_with(dsd[0], b"\x01\x03"), *dsd[1:]]),
                                  [("wavpack.dsd-invalid", "ends right after the history bits")], False)
    digest = bytes(range(16))
    meta = join(patch(a[0], samples=0, index=0, flags=0x34BC1E59), [sub(0x26, digest), sub(0x2F, bytes(4))])
    b["no-audio"] = ("A single block with no samples, which holds an MD5 digest.", meta, [("wavpack.no-audio", "None of the 1 block counts a sample")], None)
    rate = blocks(base("rate-12345"))
    b["sample-rate-missing"] = ("The sample rate sub-block left out of a file of 12,345 Hz.", aa([without(rate[0], 0x27)]),
                                [("wavpack.sample-rate-missing", "44100 Hz")], True)
    stereo_parts, stereo_ids = subs(a[0])
    stereo_parts.insert(0, sub(0x0D, b"\0"))
    b["channel-info-no-count"] = ("A channel info of one byte, a count of 0 and no mask, in the first block: libwavpack takes the channels from the flags.",
                                  aa([join(a[0], stereo_parts), a[1], a[2]]), [], True)
    zero_rate = join(a[0], [sub(0x27, bytes(3))] + subs(a[0])[0])
    b["sample-rate-zero"] = ("A sample rate sub-block of 0 Hz in a file whose flags give 44100 Hz: libwavpack ignores it.", aa([zero_rate, a[1], a[2]]),
                             [("wavpack.sample-rate-zero", "gives 0 Hz")], True)
    parts, ids = subs(rate[0])
    parts[where(ids, 0x27)] = sub(0x27, bytes(3))
    b["sample-rate-zero-custom"] = ("The sample rate sub-block of a file of 12,345 Hz set to 0, so the flags' index 15 leaves libwavpack the 44100 Hz it takes then.",
                                    aa([join(rate[0], parts)]),
                                    [("wavpack.sample-rate-zero", "gives 0 Hz"), ("wavpack.sample-rate-missing", "44100 Hz")], True)
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
    b["ape-encoder-claim"] = ("An APEv2 tag after the blocks whose \"encoder\" item names LAME, spelled in lower case: the writer, which the blocks' encoder does not contradict, but LAME writes MPEG audio, not WavPack.", aa(a) + ape_tag(("encoder", "LAME 3.100")),
                              [("wavpack.writer-other-format", "which writes MPEG audio, not WavPack")], True)
    b["ape-encoder-wavpack"] = ("An APEv2 tag after the blocks whose Encoder item names WavPack.", aa(a) + ape_tag(("Encoder", "WavPack 5.6.0")), [], True)
    small = blocks(base("lossless-16-stereo-small-blocks"))
    b["set-without-final-limit"] = ("24 stereo blocks of which only the first is initial and none is final, each 8 of them with one first sample: libwavpack ends a set at its 8 streams.",
                                    aa([patch(x, flags=(flags(x) & ~0x1800) | (0x800 if i == 0 else 0), index=i // 8 * 8000) for i, x in enumerate(small)]),
                                    [("wavpack.block-set-broken", "holds 8 blocks without a final block"), ("wavpack.block-set-broken", "follows a set that libwavpack ends at its stream limit"),
                                     ("wavpack.block-index-gap", "samples are missing"),
                                     ("wavpack.total-samples-mismatch", "hold 3000")], False)
    b["leading-id3v2"] = ("An ID3v2 tag before the blocks.", id3v2() + aa(a), [], True)

    # Cases libwavpack's decoder tells apart from what the block reader checks: they open or not, and count errors, as DECODES says.
    b["channels-change"] = ("The third block of a file of small stereo blocks with the mono flag set: its set holds 1 channel, the first set 2.",
                            aa([*small[:2], with_flags(small[2], 0x4, True), *small[3:]]),
                            [("wavpack.format-changes", "holds 1 channel, but the first holds 2 channels"), ("wavpack.subblock-size", "decorrelation weights sub-block"),
                             ("wavpack.subblock-size", "entropy variables sub-block")], False)
    dsd_high = blocks(base("dsd-64-high"))
    b["dsd-high-short"] = ("The DSD block of the first block of a high mode file cut to the mode and 8 bytes: the mode needs 20 for a stereo block.",
                           aa([dsd_with(dsd_high[0], b"\3" + bytes(8)), *dsd_high[1:]]),
                           [("wavpack.dsd-invalid", "its high mode needs at least 20 bytes after the mode in a stereo block, but 8 bytes follow")], False)
    dsd_parts, dsd_ids = subs(dsd_high[0])
    at = where(dsd_ids, 0x0E)
    raw = bytearray(dsd_parts[at])
    raw[(4 if raw[0] & 0x80 else 2) + 3] = 19
    dsd_parts[at] = bytes(raw)
    b["dsd-high-rate-step"] = ("The rate step of the high mode in the DSD block of the first block set to 19, not the 20 libwavpack takes.",
                               aa([join(dsd_high[0], dsd_parts), *dsd_high[1:]]),
                               [("wavpack.dsd-invalid", "its high mode has a rate step of 19, not 20")], False)
    b["weights-without-terms"] = ("The decorrelation terms left out of the second block, which keeps its weights and samples.", aa([a[0], without(a[1], 0x02), a[2]]),
                                  [("wavpack.subblock-size", "decorrelation weights sub-block"), ("wavpack.subblock-size", "decorrelation samples sub-block")], False)
    parts, ids = subs(a[0])
    parts[where(ids, 0x2A)] = sub(0x2A, bytes([0, 0, 0x93, 2, 1, 0, 1]))
    b["reorder-too-long"] = ("The new config block of the first block names a layout of 2 channels and 3 bytes of channel reordering.", aa([join(a[0], parts), *a[1:]]),
                             [("wavpack.subblock-size", "new config block sub-block")], False)
    later = next(i for i, x in enumerate(surround) if i > 0 and flags(x) & 0x800)
    b["channel-info-ignored"] = ("A channel info of 2 channels and a mask of 8 speakers in the initial block of the second set, after the first block's: libwavpack keeps the first.",
                                 aa([*surround[:later], insert_before_checksum(surround[later], sub(0x0D, bytes([2, 0xFF, 0x00]))), *surround[later + 1:]]),
                                 [("wavpack.channel-info-ignored", "gives 2 channels and a mask of 0xFF")], True)
    b["channel-info-after-first-block"] = ("Channel infos in the second and third block of a stereo file whose first block has none (6 channels with a 5.1 mask, then 2 channels with 8 speakers): libwavpack settled on the flags' stereo by then and takes neither.",
                                           aa([a[0], insert_before_checksum(a[1], sub(0x0D, bytes([6, 0x3F]))), insert_before_checksum(a[2], sub(0x0D, bytes([2, 0xFF, 0x00])))]),
                                           [("wavpack.channel-info-ignored", "gives 6 channels and a mask of 0x3F, but the flags of the first audio block that verifies gave 2 channels and a mask of 0x3")], False)
    first_bad = bytearray(a[0])
    first_bad[200] ^= 0x10
    b["channel-info-after-bad-block"] = ("The first block's audio changed so its checksum fails, and a channel info of 6 channels in the second: libwavpack skips the first block, so the second is the first audio block that verifies and its channel info settles the channels.",
                                         aa([bytes(first_bad), insert_before_checksum(a[1], sub(0x0D, bytes([6, 0x3F]))), a[2]]),
                                         [("wavpack.block-checksum-mismatch", "not the one worked out"), ("wavpack.channel-count-mismatch", "says 6 channels")], False)
    eight = blocks(base("lossless-8-stereo"))
    b["shift-too-large"] = ("A left shift of 31 bits in every block of a file of 8-bit samples.", aa([patch(x, flags=(flags(x) & ~(0x1F << 13)) | (31 << 13)) for x in eight]),
                            [("wavpack.flags-invalid", "left shift of the block at 0x0 is 31 bits, not less than the 8 bits")], False)
    mixed = blocks(base("lossless-16-stereo"))
    mixed[0] = join(mixed[0], [sub(0x05, bytes(8)) if i & 0x3F == 0x05 else p for p, i in zip(*subs(mixed[0]))])
    mixed[1] = insert_before_checksum(mixed[1], sub(0x26, bytes(10)), sub(0x26, bytes(12)))
    b["subblock-sizes-mixed"] = ("Entropy variables of 8 bytes in the first block, and two MD5 sub-blocks of 10 and 12 bytes in the second.", aa(mixed),
                                 [("wavpack.subblock-size", "entropy variables sub-block"), ("wavpack.subblock-size", "MD5 sub-block")], False)
    seek = id3v2_seek(100 + len(a[0]))
    b["seek-tag-between-blocks"] = ("Stray bytes and a block after an ID3v2.4 tag whose SEEK frame points at a second ID3v2 tag, then the other blocks: the stream declares no end, so it is read up to the second tag and the bytes after it are not read.",
                                    seek + junk(100) + a[0] + id3v2_title() + aa(a[1:]), [("wavpack.data-after-tag", "follow an ID3v2 tag"), ("wavpack.data-skipped", "100 bytes"), ("wavpack.total-samples-mismatch", "holds")], False)
    return b


def regions(data, name):
    """The top-level nodes."""
    if name in ("built-id3v1",):
        return [("WavPack", 0, len(data) - 128), ("ID3v1", len(data) - 128, 128)]
    if name in ("built-ape-encoder-claim", "built-ape-encoder-wavpack"):
        tag = data.rindex(b"APETAGEX", 0, len(data) - 32)
        return [("WavPack", 0, tag), ("APE", tag, len(data) - tag)]
    if name == "built-ape-and-id3v1":
        tag = data.rindex(b"APETAGEX", 0, len(data) - 160)
        return [("WavPack", 0, tag), ("APE", tag, len(data) - 128 - tag), ("ID3v1", len(data) - 128, 128)]
    if name == "built-leading-id3v2":
        size = len(id3v2())
        return [("ID3v2", 0, size), ("WavPack", size, len(data) - size)]
    if name == "built-seek-tag-between-blocks":
        first = len(id3v2_seek(0)) + 100 + len(blocks(base("lossless-16-stereo"))[0])
        tag = len(id3v2_title())
        return [("ID3v2", 0, len(id3v2_seek(0))), ("WavPack", len(id3v2_seek(0)), first - len(id3v2_seek(0))),
                ("ID3v2", first, tag), ("Data", first + tag, len(data) - first - tag)]
    return [("WavPack", 0, len(data))]


def decode(name):
    """The expectation of the decoder pass for the fixture: it does not open, or opens and unpacks this many samples with this many errors."""
    result = DECODES[name]
    return {"opens": False} if result is None else {"opens": True, "samples": result[0], "errors": result[1]}


def main():
    DIRECTORY.mkdir(parents=True, exist_ok=True)
    count = 0
    built = build()
    assert set(built) == set(DECODES), sorted(set(built) ^ set(DECODES))
    for name, (description, data, findings, valid) in built.items():
        full = f"built-{name}"
        (DIRECTORY / f"{full}.wv").write_bytes(data)
        top = regions(data, full)
        start = top[1][1] if full in ("built-leading-id3v2", "built-seek-tag-between-blocks") else 0
        end = top[0][1] + top[0][2] if full != "built-leading-id3v2" else len(data)
        if full == "built-seek-tag-between-blocks":
            end = top[1][1] + top[1][2]
        result = m.manifest(data, start, end, {"regions": [{"name": n, "offset": o, "length": l} for n, o, l in top], "description": description,
                                               "findings": [{"code": c, "contains": t} for c, t in findings], "crosscheck": {"valid": valid, "decode": decode(name)}})
        (DIRECTORY / f"{full}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"Wrote {count} hand-built fixtures to {DIRECTORY}")


if __name__ == "__main__":
    main()
