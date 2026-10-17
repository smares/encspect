#!/usr/bin/env python3
"""Writes the byte-exact Matroska and WebM fixtures in tests/fixtures/matroska.

The files are built by matroska_model.py from EBML elements (RFC 8794) and the Matroska schema (RFC 9559). The audio is Opus
(A_OPUS): an OpusHead in CodecPrivate and packets of one TOC byte and a patterned body, which the Opus parser counts without
complaint and ffmpeg's demuxer accepts, so the container is fully under control: every lacing mode, BlockGroups, unknown
sizes, CRC-32 and Void elements, content encodings (header stripping, zlib, encryption), a video track, cut files and
every kind of damage. Each fixture is a file plus a <name>.expected.json manifest:

- "length" and "format": the file's size and the format name the analyzer gives it ("Matroska" or "WebM").
- "tree": the file's nodes as name, offset and length with their children, worked out from the bytes by
  matroska_model.manifest: the EBML Header, the Segment with its Top-Level Elements and Clusters, a Cluster's elements, a
  BlockGroup's. A block is a leaf; its fields are in "blocks".
- "blocks": every SimpleBlock and Block in file order with its track, timestamp, flags, lacing and the offset and size of each
  frame, and a "problem" where the block cannot be read ("short", "lace-invalid", "lace-single").
- "tracks", "segments", "ebml": the TrackEntries' figures, the Segments' extents and the EBML Header's.
- "streams": the streams the analyzer reports (codec, sample rate, channels), in order.
- "findings": the findings the analyzer must produce, as a code plus a string the message has to contain; no others may appear.
- "crosscheck": what crosscheck_matroska_fixtures.py checks with other tools: "valid" fixtures must pass ffprobe and mkvinfo
  and agree with them on the tracks and every frame's size; others list what the tools must still agree on.

The script uses only the standard library and is deterministic, so CI reruns it and fails when the committed files differ.
"""

import json
import pathlib
import struct
import zlib

import matroska_model as m
from matroska_model import (E, Crc, ID, master, uint, sint, flt, text, raw, void, cluster, segment, simple_block, block_group, ebml_header, info,
                            audio_track, video_track, tracks, file_of, content_encoding, content_encodings, seek_head, cues, tags)

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "matroska"

FIXTURES = {}


def fixture(name, description, findings=(), streams=None, valid=True, extension="mka", checks=None):
    """Registers a builder: it returns the file's bytes. findings: (code, contains) pairs; streams: (codec, rate, channels) triples."""

    def register(builder):
        FIXTURES[name] = (builder, description, list(findings), streams, valid, extension, checks)
        return builder

    return register


# --- Opus in Matroska (A_OPUS: draft-ietf-cellar-codec section 3.4.32) ----------------------------


def opus_head(channels=1, pre_skip=312, rate=48000):
    return b"OpusHead" + struct.pack("<BBHIhB", 1, channels, pre_skip, rate, 0, 0)


def opus_packet(index, channels=1, size=40):
    """One Opus packet: the TOC byte of a CELT fullband 20 ms frame (code 0) and a body that depends on the index."""
    toc = 0xF8 | (4 if channels == 2 else 0)
    return bytes([toc]) + bytes((index * 31 + k * 7) & 0xFF for k in range(size - 1))


def opus_track(number=1, channels=1, **kw):
    kw.setdefault("delay", 6_500_000)
    kw.setdefault("preroll", 80_000_000)
    kw.setdefault("private", opus_head(channels))
    return audio_track(number, "A_OPUS", channels, 48000.0, **kw)


def blocks(track=1, count=5, start=0, step=20, channels=1, size=40, first=0):
    return [simple_block(track, start + i * step, opus_packet(first + i, channels, size + (i % 3)), key=True) for i in range(count)]


def standard(*clusters, doctype="matroska", track=None, extra=(), info_extra=(), seeks=None, scale=1_000_000):
    """EBML Header, then a Segment with Info, Tracks (one Opus track unless `track` gives the entries) and the clusters."""
    entries = track if track is not None else [opus_track()]
    return [ebml_header(doctype), segment(info(scale=scale, duration=100.0, extra=info_extra), tracks(*entries), *extra, *clusters)]


def build(elements):
    return file_of(*elements)


OPUS = ("Opus", 48000, 1)
NOT_ORDERED = [("matroska.encoding-order", "unique"), ("matroska.track-encoding-not-read", "cannot be ordered")]


# --- The fixtures ------------------------------------------------------------------------------------


@fixture("opus-minimal", "One Opus track in a Matroska file: the EBML Header, a Segment with Info, Tracks and one Cluster of five SimpleBlocks.", streams=[OPUS])
def _():
    return build(standard(cluster(0, *blocks(count=5))))


@fixture("opus-stereo-clusters", "A stereo Opus track in three Clusters whose block timestamps are relative to each Cluster's Timestamp.", streams=[("Opus", 48000, 2)])
def _():
    return build(standard(cluster(0, *blocks(count=4, channels=2)), cluster(80, *blocks(count=4, start=0, channels=2, first=4)),
                          cluster(160, *blocks(count=3, start=0, channels=2, first=8)), track=[opus_track(1, 2)]))


@fixture("lacing-modes", "Blocks with every lacing mode (RFC 9559 section 10.3): none, Xiph with a frame of 255 and one of 510 bytes, EBML with sizes that "
         "grow and shrink, and fixed-size.", streams=[OPUS])
def _():
    def frames(sizes, first):
        return [opus_packet(first + i, 1, s) for i, s in enumerate(sizes)]

    items = [
        simple_block(1, 0, frames([40], 0), key=True),
        simple_block(1, 20, frames([255, 40, 510, 7], 1), lacing="xiph", key=True),
        simple_block(1, 100, frames([40, 300, 100, 1200, 20], 5), lacing="ebml", key=True),
        simple_block(1, 200, frames([33, 33, 33], 10), lacing="fixed", key=True),
        simple_block(1, 260, frames([2, 1, 3], 13), lacing="xiph", key=True),
    ]
    return build(standard(cluster(0, *items)))


@fixture("lacing-invalid", "Blocks whose lacing headers do not add up (RFC 9559 section 10.3): Xiph sizes that exceed the block, an EBML size that makes a "
         "frame negative, fixed-size frames that do not divide the block, and a header cut short.",
         findings=[("matroska.lacing-invalid", "does not add up")], streams=[OPUS], valid=False)
def _():
    a = opus_packet(0)
    items = [
        simple_block(1, 0, [a, a, a], lacing="xiph", lace_head=bytes([2, 200, 200])),
        simple_block(1, 20, [a, a, a], lacing="ebml", lace_head=bytes([2, 0x80 | 40, 0x80 | 0])),
        simple_block(1, 40, [a, a, a, a[:-1]], lacing="fixed"),
        simple_block(1, 60, [b""], lacing="xiph", lace_head=bytes([3, 255])),
        simple_block(1, 80, [a], key=True),
    ]
    return build(standard(cluster(0, *items)))


@fixture("lacing-single", "A block that sets a lacing mode but holds one frame, which RFC 9559 forbids (section 10.3).",
         findings=[("matroska.lacing-single", "single frame")], streams=[OPUS])
def _():
    return build(standard(cluster(0, simple_block(1, 0, [opus_packet(0)], lacing="xiph", lace_head=bytes([0])), simple_block(1, 20, [opus_packet(1)]))))


@fixture("lacing-not-allowed", "A track with FlagLacing 0 whose blocks use lacing (RFC 9559 section 5.1.4.1.12).",
         findings=[("matroska.lacing-not-allowed", "FlagLacing is 0")], streams=[OPUS])
def _():
    return build(standard(cluster(0, simple_block(1, 0, [opus_packet(0), opus_packet(1)], lacing="xiph")), track=[opus_track(lacing=False)]))


@fixture("block-groups", "BlockGroups (RFC 9559 section 5.1.3.5) with a Block, BlockDuration, ReferenceBlock, DiscardPadding and BlockAdditions, a laced Block "
         "and SimpleBlocks among them.", streams=[OPUS])
def _():
    items = [
        simple_block(1, 0, opus_packet(0), key=True),
        block_group(1, 20, opus_packet(1), duration=20, references=[-20], discard_padding=1_500_000),
        block_group(1, 40, [opus_packet(2), opus_packet(3, size=60)], lacing="xiph", duration=40, references=[-20, -40],
                    additions=[(1, b"\x01\x02\x03"), (2, b"\xFF")]),
        simple_block(1, 80, opus_packet(4), key=True),
        block_group(1, 100, opus_packet(5), extra=(void(5),)),
    ]
    return build(standard(cluster(0, *items)))


@fixture("block-group-damaged", "BlockGroups without a Block and with two Blocks (RFC 9559 section 5.1.3.5.1).",
         findings=[("matroska.group-no-block", "no Block"), ("matroska.group-multiple-blocks", "more than one Block")], streams=[OPUS], valid=False)
def _():
    items = [
        simple_block(1, 0, opus_packet(0)),
        master(m.BLOCKGROUP, uint(ID["BlockDuration"], 20)),
        master(m.BLOCKGROUP, E(m.BLOCK, m.block_payload(1, 20, [opus_packet(1)], is_block=True)), E(m.BLOCK, m.block_payload(1, 40, [opus_packet(2)], is_block=True))),
    ]
    return build(standard(cluster(0, *items)))


@fixture("short-blocks", "Blocks too short for their header: a SimpleBlock of two bytes, an empty Block and a track number that is no variable-size integer.",
         findings=[("matroska.block-short", "too short")], streams=[OPUS], valid=False)
def _():
    items = [simple_block(1, 0, opus_packet(0)), E(m.SIMPLEBLOCK, b"\x81\x00"), E(m.SIMPLEBLOCK, b""), E(m.SIMPLEBLOCK, b"\x00\x00\x00\x00\x00\x00"),
             block_group(1, 20, opus_packet(1))]
    return build(standard(cluster(0, *items)))


@fixture("unknown-size-live", "A stream written live: the Segment and every Cluster have the unknown size (RFC 8794 section 6.2), each Cluster ending where the "
         "next starts and the last at the end of the file.", findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")],
         streams=[OPUS])
def _():
    clusters = [cluster(i * 100, *blocks(count=5, first=i * 5), unknown=True) for i in range(3)]
    return build(standard(*clusters)[:1] + [segment(info(), tracks(opus_track()), *clusters, unknown=True)])


@fixture("unknown-size-clusters", "A Segment of known size whose Clusters have the unknown size and end at the next Top-Level Element: a Cluster, Cues and Tags.",
         findings=[("matroska.cluster-size-unknown", "unknown")], streams=[OPUS])
def _():
    clusters = [cluster(0, *blocks(count=3), unknown=True), cluster(60, *blocks(count=3, first=3), unknown=True)]
    return build([ebml_header(), segment(info(), tracks(opus_track()), *clusters, tags(("TITLE", "Live")))])


@fixture("crc-valid", "CRC-32 elements (RFC 8794 section 11.3.1) as the first child of the SeekHead, Info, Tracks, Cluster, Cues and Tags, all of which hold.",
         streams=[OPUS])
def _():
    def make(p):
        inf, trk = info(), tracks(opus_track(), crc=True)
        cl = cluster(0, *blocks(count=4), crc=True)
        head = seek_head((m.INFO, p.get("info", 0)), (m.TRACKS, p.get("tracks", 0)), (m.CLUSTER, p.get("c0", 0)), crc=True)
        inf.children.insert(0, Crc())
        seg = segment(head, inf, trk, cl, cues((0, 1, p.get("c0", 0)), crc=True), tags(("TITLE", "x"), crc=True))
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": cl}, seg

    return laid_out(make)


@fixture("crc-damaged", "CRC-32 elements that do not match: one in Info and one in a Cluster, whose data was edited after the CRC was computed.",
         findings=[("matroska.crc-mismatch", "do not match")], streams=[OPUS])
def _():
    good = cluster(0, *blocks(count=3), crc=True)
    bad = cluster(60, *blocks(count=3, first=3), crc=False)
    bad.children.insert(0, Crc(value=0xDEADBEEF))
    inf = master(m.INFO, Crc(value=0x12345678), uint(ID["TimestampScale"], 1_000_000), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "x"))
    return build([ebml_header(), segment(inf, tracks(opus_track()), good, bad)])


@fixture("crc-misplaced", "CRC-32 elements that RFC 8794 section 11.3.1 does not allow: one that is not the first child, and one of two bytes.",
         findings=[("matroska.crc-position", "not the first child"), ("matroska.crc-size", "other than the 4 bytes")], streams=[OPUS])
def _():
    late = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), Crc(), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "x"))
    short = master(m.CLUSTER, E(m.CRC32, b"\x01\x02"), uint(m.TIMESTAMP, 0), *blocks(count=2))
    return build([ebml_header(), segment(late, tracks(opus_track()), short)])


@fixture("elements-unknown-void", "Void elements between and inside others, elements the schema does not list (one with an ID coded longer than needed), "
         "and a deprecated SilentTracks element in a Cluster.",
         findings=[("matroska.unknown-element", "none the Matroska schema lists"), ("matroska.element-id-long", "shortest length"),
                   ("matroska.deprecated-element", "no longer used")], streams=[OPUS], valid=False)
def _():
    inf = master(m.INFO, void(7), uint(ID["TimestampScale"], 1_000_000), raw(0x7FF0, b"\x01\x02\x03"), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "x"))
    cl = cluster(0, void(3), *blocks(count=2), master(0x5854, uint(0x58D7, 1)), raw(0x7FF1, b"zz"), void(2), extra_first=())
    return build([ebml_header(), void(9), segment(void(20), inf, tracks(opus_track(), raw(0x1FFFFFF0, b"\xAA" * 5)), E(0xEC, bytes(3), id_raw=bytes([0x40, 0x6C])), cl)])


@fixture("header-stripping", "A track whose frames have their first byte (the Opus TOC byte) stripped by header stripping (RFC 9559 section 5.1.4.1.31): "
         "the analyzer puts the byte back before it routes the frames.", streams=[OPUS])
def _():
    stripped = [simple_block(1, i * 20, opus_packet(i)[1:], key=True) for i in range(5)]
    enc = content_encodings(content_encoding(order=0, scope=1, compression=3, settings=b"\xF8"))
    return build(standard(cluster(0, *stripped), track=[opus_track(encodings=enc)]))


@fixture("encrypted-cbc", "A track whose frames are encrypted with AES-CBC (ContentEncAESSettings, AESSettingsCipherMode 2): the stream is protected and encrypted, and its frames are not read.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CBC)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=2, key_id=b"\x05\x06"))
    sealed = [simple_block(1, i * 20, bytes((i * 7 + k) & 0xFF for k in range(33)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encrypted-3des", "A track whose frames are encrypted with 3DES (ContentEncAlgo 2, no AES settings): the stream is protected and encrypted, and its frames are not read.",
         findings=[("matroska.track-encrypted", "is encrypted (3DES)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=2, key_id=b"\x09"))
    sealed = [simple_block(1, i * 20, bytes((i * 5 + k) & 0xFF for k in range(29)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("zlib-compressed", "A track whose frames are compressed with zlib (ContentCompAlgo 0).", streams=[OPUS])
def _():
    packed = [simple_block(1, i * 20, zlib.compress(opus_packet(i), 9), key=True) for i in range(4)]
    enc = content_encodings(content_encoding(order=0, scope=1, compression=0))
    return build(standard(cluster(0, *packed), track=[opus_track(encodings=enc)]))


@fixture("private-stripped", "A track whose CodecPrivate is header-stripped (scope 2): the first eight bytes of the OpusHead are put back before it is routed.",
         streams=[OPUS])
def _():
    enc = content_encodings(content_encoding(order=0, scope=2, compression=3, settings=b"OpusHead"))
    return build(standard(cluster(0, *blocks(count=3)), track=[opus_track(private=opus_head()[8:], encodings=enc)]))


@fixture("encrypted", "A track whose frames are encrypted (ContentEncodingType 1, AES-CTR): the analyzer reports it and does not read the frames. The frames have the sizes of the clear encryption-algorithm-default.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01\x02\x03\x04"))
    sealed = [simple_block(1, i * 20, bytes((i * 13 + k) & 0xFF for k in range(40 + i % 3)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encrypted-3des-aes-settings", "A track encrypted with 3DES whose ContentEncryption also holds ContentEncAESSettings, which RFC 9559 forbids unless the algorithm is AES "
         "(Table 25): the scheme is 3DES without a cipher mode.",
         findings=[("matroska.aes-settings-not-aes", "not AES"), ("matroska.track-encrypted", "is encrypted (3DES)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=2, cipher=1, key_id=b"\x09"))
    sealed = [simple_block(1, i * 20, bytes((i * 11 + k) & 0xFF for k in range(31)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encryption-algorithm-none", "A ContentEncoding of type 1 whose ContentEncAlgo is 0, which states that the data are not encrypted (Table 24): the frames are read as they are "
         "and the track is not protected.",
         findings=[("matroska.encryption-none", "not encrypted")], streams=[OPUS], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=0, key_id=b"\x01"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


@fixture("encryption-ignored", "A zlib ContentEncoding (type 0) that also holds a ContentEncryption element, which a reader ignores unless the type is 1 "
         "(section 5.1.4.1.31.8): the compression is undone and the track is not protected.",
         findings=[("matroska.encryption-ignored", "ignores")], streams=[OPUS], valid=False)
def _():
    packed = [simple_block(1, i * 20, zlib.compress(opus_packet(i), 9), key=True) for i in range(4)]
    enc = content_encodings(content_encoding(order=0, scope=1, compression=0, stray_encryption=5))
    return build(standard(cluster(0, *packed), track=[opus_track(encodings=enc)]))


@fixture("encryption-missing", "A ContentEncoding of type 1 without a ContentEncryption element, which RFC 9559 requires then (section 5.1.4.1.31.8): the track is reported "
         "as encrypted by an unknown algorithm.",
         findings=[("matroska.encryption-missing", "without a ContentEncryption"), ("matroska.track-encrypted", "is encrypted, so")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, missing_encryption=True))
    sealed = [simple_block(1, i * 20, bytes((i * 3 + k) & 0xFF for k in range(27)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encrypted-under-bzlib", "A track compressed with bzlib (ContentEncodingOrder 1) over AES-CTR encryption (order 0): the finding names both, and the stream is encrypted.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CTR) and compressed with bzlib")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=1, compression=1), content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x07"))
    sealed = [simple_block(1, i * 20, bytes((i * 9 + k) & 0xFF for k in range(35)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encrypted-private", "A track whose CodecPrivate is encrypted (scope 2, AES-CTR) and whose frames are in the clear: the stream carries the scheme with its samples "
         "not encrypted, and the codec's setup is not available.",
         findings=[("matroska.codec-private-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=2, encryption=5, cipher=1, key_id=b"\x02"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(private=bytes((k * 5 + 1) & 0xFF for k in range(19)), encodings=enc)]))


@fixture("compression-private-bzlib", "A track whose CodecPrivate is compressed with bzlib (scope 2), which the analyzer does not undo, and whose frames are in the clear: "
         "the setup of the codec is unavailable, and nothing is protected.",
         findings=[("matroska.codec-private-not-read", "is compressed with bzlib")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=2, compression=1))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


@fixture("encryption-algorithm-default", "A ContentEncryption without a ContentEncAlgo, whose default is 0 (not encrypted, RFC 9559 section 5.1.4.1.31.9): the frames are read as they are "
         "and the track is not protected.",
         findings=[("matroska.encryption-none", "not encrypted")], streams=[OPUS], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, no_algo=True, key_id=b"\x01"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


@fixture("encrypted-next-encoding", "AES-CTR with scope 4 (the next ContentEncoding) over header stripping: the stripped bytes in ContentCompSettings are ciphertext, so the "
         "frames cannot be put back together and the stream is encrypted.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1"), ("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, encryption=5, cipher=1, key_id=b"\x03"), content_encoding(order=0, scope=1, compression=3, settings=b"\xde\xad"))
    stripped = [simple_block(1, i * 20, opus_packet(i)[1:], key=True) for i in range(4)]
    return build(standard(cluster(0, *stripped), track=[opus_track(encodings=enc)]))


@fixture("encrypted-aac", "An A_AAC track with a clear CodecPrivate (the AudioSpecificConfig) and encrypted frames: the AAC parser describes the stream from the configuration "
         "and the container's timing gives its length.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("AAC", 44100, 2)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01"))
    sealed = [simple_block(1, i * 23, bytes((i * 7 + k) & 0xFF for k in range(50)), key=True) for i in range(40)]
    return build(standard(cluster(0, *sealed), track=[audio_track(codec="A_AAC", channels=2, rate=44100.0, private=b"\x12\x10", encodings=enc)]))


@fixture("encrypted-vorbis", "An A_VORBIS track with its three headers in a clear CodecPrivate and encrypted frames: the Vorbis parser reads the headers, not the frames. The "
         "headers are copied from external-ffmpeg-vorbis, so their vendor string names ffmpeg's muxer while the Segment names this generator.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CTR)"), ("vorbis.writer-claim-differs", "Lavf60.16.100")], streams=[("Vorbis", 44100, 2)], valid=False)
def _():
    manifest = json.loads((OUT / "external-ffmpeg-vorbis.expected.json").read_text(encoding="utf-8"))
    offset, length = manifest["tracks"][0]["private"]
    private = (OUT / "external-ffmpeg-vorbis.mka").read_bytes()[offset:offset + length]
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01"))
    sealed = [simple_block(1, i * 23, bytes((i * 5 + k) & 0xFF for k in range(60)), key=True) for i in range(30)]
    return build(standard(cluster(0, *sealed), track=[audio_track(codec="A_VORBIS", channels=2, rate=44100.0, private=private, encodings=enc)]))


@fixture("vorbis-private-zlib", "An A_VORBIS track whose CodecPrivate (the three headers of external-ffmpeg-vorbis) is zlib-compressed (scope 2), with the frames in the clear: the inflated "
         "headers are handed to the Vorbis parser as they are, including the framing bit of the comment header.",
         findings=[("vorbis.writer-claim-differs", "Lavf60.16.100")], streams=[("Vorbis", 44100, 2)], valid=False)
def _():
    manifest = json.loads((OUT / "external-ffmpeg-vorbis.expected.json").read_text(encoding="utf-8"))
    data = (OUT / "external-ffmpeg-vorbis.mka").read_bytes()
    offset, length = manifest["tracks"][0]["private"]
    blocks_of_source = [b for b in manifest["blocks"] if b["frames"]][:30]
    items = [simple_block(1, i * 23, data[b["frames"][0][0]:b["frames"][0][0] + b["frames"][0][1]], key=True) for i, b in enumerate(blocks_of_source)]
    enc = content_encodings(content_encoding(order=0, scope=2, compression=0))
    return build(standard(cluster(0, *items), track=[audio_track(codec="A_VORBIS", channels=2, rate=44100.0, private=zlib.compress(data[offset:offset + length], 9), encodings=enc)]))


@fixture("vorbis-private-zlib-framing", "The zlib-compressed Vorbis CodecPrivate of vorbis-private-zlib with the framing bit of the identification header cleared: the finding points at the "
         "identification header's Framing Bit, not at the comment header's, although every field of the inflated bytes covers the same range of the file.",
         findings=[("vorbis.framing-missing", "identification header"), ("vorbis.writer-claim-differs", "Lavf60.16.100")], streams=[("Vorbis", 44100, 2)], valid=False)
def _():
    manifest = json.loads((OUT / "external-ffmpeg-vorbis.expected.json").read_text(encoding="utf-8"))
    data = (OUT / "external-ffmpeg-vorbis.mka").read_bytes()
    offset, length = manifest["tracks"][0]["private"]
    private = bytearray(data[offset:offset + length])
    private[3 + private[1] - 1] &= 0xFE
    blocks_of_source = [b for b in manifest["blocks"] if b["frames"]][:30]
    items = [simple_block(1, i * 23, data[b["frames"][0][0]:b["frames"][0][0] + b["frames"][0][1]], key=True) for i, b in enumerate(blocks_of_source)]
    enc = content_encodings(content_encoding(order=0, scope=2, compression=0))
    return build(standard(cluster(0, *items), track=[audio_track(codec="A_VORBIS", channels=2, rate=44100.0, private=zlib.compress(bytes(private), 9), encodings=enc)]))


@fixture("five-byte-id-in-block-group", "An EBML Header that allows IDs of five octets (EBMLMaxIDLength 5), and in a BlockGroup an element with such an ID whose size runs past the BlockGroup: it is an "
         "unknown element, and the finding about its size points at the node of the tree that holds it.",
         findings=[("matroska.header-limits", "EBMLMaxIDLength of 5"), ("matroska.element-overruns-parent", "runs past the end of its parent")],
         streams=[OPUS], valid=False)
def _():
    items = [simple_block(1, 0, opus_packet(0)), block_group(1, 20, opus_packet(1), extra=(E(0x7FF1, b"zz", size=40, id_raw=b"\x08\x12\x34\x56\x78"),))]
    return build([ebml_header(max_id=5), segment(info(), tracks(opus_track()), cluster(0, *items), cluster(100, *blocks(count=4, first=2)))])


@fixture("eight-octet-id", "An EBML Header that allows IDs of eight octets (EBMLMaxIDLength 8), and an element with such an ID (0x0123456789ABCDEF) in a BlockGroup and one directly in a "
         "Cluster: the tree shows the whole ID of each.",
         findings=[("matroska.header-limits", "EBMLMaxIDLength of 8"), ("matroska.unknown-element", "none the Matroska schema lists")], streams=[OPUS], valid=False)
def _():
    long_id = b"\x01\x23\x45\x67\x89\xAB\xCD\xEF"
    items = [simple_block(1, 0, opus_packet(0)), E(0x7FF1, b"zz", id_raw=long_id), block_group(1, 20, opus_packet(1), extra=(E(0x7FF1, b"yy", id_raw=long_id),))]
    return build([ebml_header(max_id=8), segment(info(), tracks(opus_track()), cluster(0, *items))])


@fixture("encrypted-over-algorithm-none", "AES-CTR with scope 4 over an encoding whose ContentEncAlgo is 0: the hidden encoding's claim to be not encrypted cannot be trusted, "
         "so the frames count as encrypted by the AES encryption.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1"), ("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, encryption=5, cipher=1, key_id=b"\x03"), content_encoding(order=0, scope=1, encryption=0))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


@fixture("encrypted-over-3des", "AES-CTR with scope 4 over 3DES with scope 1: the scheme names the outermost encryption, which hides the other's settings.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1"), ("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, encryption=5, cipher=1, key_id=b"\x03"), content_encoding(order=0, scope=1, encryption=2, key_id=b"\x04"))
    sealed = [simple_block(1, i * 20, bytes((i * 3 + k) & 0xFF for k in range(33)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("encrypted-chain", "AES-CTR (scope 4) over 3DES (scope 4) over header stripping: each hides the next. The 3DES encryption is hidden in the chain, so its algorithm cannot be "
         "read, and the stripping it hides counts as encrypted by an unknown algorithm: the scheme has no algorithm.",
         findings=[("matroska.encoding-hidden", "order 1 whose settings are hidden by the ContentEncoding of order 2"), ("matroska.encoding-hidden", "order 0 whose settings are hidden by the ContentEncoding of order 2"), ("matroska.track-encrypted", "is encrypted, so")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=2, scope=4, encryption=5, cipher=1, key_id=b"\x03"), content_encoding(order=1, scope=4, encryption=2, key_id=b"\x04"),
                            content_encoding(order=0, scope=1, compression=3, settings=b"\xde\xad"))
    stripped = [simple_block(1, i * 20, opus_packet(i)[1:], key=True) for i in range(4)]
    return build(standard(cluster(0, *stripped), track=[opus_track(encodings=enc)]))


@fixture("compression-hides-encryption", "Header stripping with scope 4 over an encryption: the stripped bytes are the ContentEncAlgo element (AES), but the encryption's own settings are "
         "unreadable until they are put back, so it counts as encrypted by an unknown algorithm.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1"), ("matroska.track-encrypted", "is encrypted, so")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, compression=3, settings=b"\x47\xE1\x81\x05"), content_encoding(order=0, scope=1, no_algo=True))
    sealed = [simple_block(1, i * 20, bytes((i * 3 + k) & 0xFF for k in range(30)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("compression-hides-compression", "Header stripping with scope 4 over a zlib compression: the zlib encoding's settings are unreadable, so the frames are not read and nothing is protected.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1"), ("matroska.track-encoding-not-read", "settings that another encoding hides")],
         streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, compression=3, settings=b"\x78"), content_encoding(order=0, scope=1, compression=0))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


@fixture("encryption-covers-nothing", "AES-CTR with scope 4 as the only encoding: no next encoding follows, so it hides nothing and the track is not protected.",
         findings=[("matroska.encryption-covers-nothing", "encrypts nothing")], streams=[OPUS], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=4, encryption=5, cipher=1, key_id=b"\x03"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


def aac_with_delay(is_encrypted):
    """Six frames of an ADTS fixture in an A_AAC track with an explicit AudioSpecificConfig and a CodecDelay of 50 ms (which the AAC parser ignores),
    one cluster each in nanoseconds so that the container's timing equals the frames' length; sealed with the same sizes when encrypted."""
    object_type, rate_index, channels, frames = adts_frames("cbr")
    rate = AAC_RATES[rate_index]
    config = struct.pack(">H", (object_type << 11) | (rate_index << 7) | (channels << 3))
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01")) if is_encrypted else None
    clusters = [cluster(round(i * 1024 * 1e9 / rate),
                        simple_block(1, 0, bytes((i * 7 + k) & 0xFF for k in range(len(frame))) if is_encrypted else frame, key=True)) for i, frame in enumerate(frames[:6])]
    return build(standard(*clusters, scale=1, track=[audio_track(codec="A_AAC", channels=channels, rate=float(rate), private=config, delay=50_000_000, encodings=enc)]))


def aac_private(encodings, transform):
    """Six frames of an ADTS fixture, in the clear, in an A_AAC track whose CodecPrivate (the AudioSpecificConfig, passed through transform) is coded by the encodings."""
    object_type, rate_index, channels, frames = adts_frames("cbr")
    rate = AAC_RATES[rate_index]
    config = transform(struct.pack(">H", (object_type << 11) | (rate_index << 7) | (channels << 3)))
    items = [simple_block(1, round(i * 1024 * 1000 / rate), frame, key=True) for i, frame in enumerate(frames[:6])]
    return build(standard(cluster(0, *items), track=[audio_track(codec="A_AAC", channels=channels, rate=float(rate), private=config, encodings=content_encodings(*encodings))]))


@fixture("aac-private-zlib", "An A_AAC track whose CodecPrivate is zlib-compressed (scope 2) and whose frames are in the clear: the CodecPrivate is inflated and handed to the AAC "
         "parser as the configuration.", streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_private([content_encoding(order=0, scope=2, compression=0)], lambda config: zlib.compress(config, 9))


@fixture("aac-private-stripped", "An A_AAC track whose CodecPrivate is header-stripped (scope 2, the first byte of the AudioSpecificConfig) and whose frames are in the clear: the byte "
         "is put back and the configuration handed to the AAC parser.", streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_private([content_encoding(order=0, scope=2, compression=3, settings=b"\x12")], lambda config: config[1:])


@fixture("aac-private-encrypted", "An A_AAC track whose CodecPrivate is encrypted (AES-CTR, scope 2) and whose frames are in the clear: the frames are analyzed, the AAC parser is given no "
         "configuration, and the stream carries the scheme with its samples not encrypted.",
         findings=[("matroska.codec-private-encrypted", "is encrypted (AES-CTR)"), ("aac.no-configuration", "without an AudioSpecificConfig")], streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_private([content_encoding(order=0, scope=2, encryption=5, cipher=1, key_id=b"\x05")], lambda config: bytes((b ^ 0x5A) for b in config))


@fixture("aac-private-bzlib", "An A_AAC track whose CodecPrivate is compressed with bzlib (scope 2), which the analyzer does not undo, and whose frames are in the clear: the frames are "
         "analyzed without a configuration.",
         findings=[("matroska.codec-private-not-read", "compressed with bzlib"), ("aac.no-configuration", "without an AudioSpecificConfig")], streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_private([content_encoding(order=0, scope=2, compression=1)], lambda config: config)


@fixture("ac3-codec-private", "An A_AC3 track with a CodecPrivate, which the codec mapping does not define for it (\"Initialization: none\"): the AC-3 parser is not handed it as a dac3 "
         "configuration, and the frames are analyzed.",
         findings=[("matroska.codec-private-unexpected", "does not define")], streams=[("AC-3", 48000, 6)], valid=False)
def _():
    manifest = json.loads((OUT / "external-ffmpeg-ac3-51.expected.json").read_text(encoding="utf-8"))
    data = (OUT / "external-ffmpeg-ac3-51.mka").read_bytes()
    blocks_of_source = [b for b in manifest["blocks"] if b["frames"]][:4]
    items = [simple_block(1, i * 32, data[b["frames"][0][0]:b["frames"][0][0] + b["frames"][0][1]], key=True) for i, b in enumerate(blocks_of_source)]
    return build(standard(cluster(0, *items), track=[audio_track(codec="A_AC3", channels=6, rate=48000.0, private=b"\x10\x3d\xe0")]))


@fixture("aac-delay", "A clear A_AAC track with a CodecDelay of 50 ms, which the AAC parser ignores: six frames.", streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_with_delay(False)


@fixture("encrypted-aac-delay", "The frames of aac-delay encrypted: the encrypted track counts the samples and the length of the clear one, since only Opus drops the CodecDelay.",
         findings=[("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("AAC", 44100, 2)], valid=False)
def _():
    return aac_with_delay(True)


@fixture("encrypted-in-chain-middle", "Header stripping with scope 4 over AES-CTR with scope 4 over header stripping: the encryption in the middle of the chain is itself hidden, so "
         "its algorithm is unknown, and the frames of the last encoding count as encrypted by it.",
         findings=[("matroska.encoding-hidden", "order 1 whose settings are hidden by the ContentEncoding of order 2"), ("matroska.encoding-hidden", "order 0 whose settings are hidden by the ContentEncoding of order 2"),
                   ("matroska.track-encrypted", "is encrypted, so")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=2, scope=4, compression=3, settings=b"\x47"), content_encoding(order=1, scope=4, encryption=5, cipher=1, key_id=b"\x03"),
                            content_encoding(order=0, scope=1, compression=3, settings=b"\xde\xad"))
    sealed = [simple_block(1, i * 20, bytes((i * 3 + k) & 0xFF for k in range(30)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("hidden-encryption-covers-nothing", "A compression with scope 4 over AES-CTR with scope 4 and no encoding after it: the hidden encryption covers nothing, so the frames are read "
         "and the track is not protected.",
         findings=[("matroska.encoding-hidden", "hidden by the ContentEncoding of order 1")], streams=[OPUS], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=4, compression=3, settings=b"\x47"), content_encoding(order=0, scope=4, encryption=5, cipher=1, key_id=b"\x03"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(encodings=enc)]))


# Encodings that share a ContentEncodingOrder (which RFC 9559 section 5.1.4.1.31.2 forbids): the track cannot be ordered, so nothing of it is read and it is
# encrypted when any encoding is of type 1. Each case is a list of (content_encoding arguments, ContentEncodingType) pairs; the fixtures write them in the order given, and
# check_matroska_encodings.py writes every permutation and expects the same result.
TIE_CASES = {
    "same-order-encryption-and-stripping": [(dict(order=0, scope=1, compression=3, settings=b"\xde\xad"), 0), (dict(order=0, scope=4, encryption=5, cipher=1, key_id=b"\x03"), 1)],
    "same-order-two-compressions": [(dict(order=0, scope=1, compression=3, settings=b"\xF8"), 0), (dict(order=0, scope=1, compression=0), 0)],
    "same-order-aes-modes": [(dict(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x03"), 1), (dict(order=0, scope=1, encryption=5, cipher=2, key_id=b"\x03"), 1)],
    "same-order-over-encryption": [(dict(order=1, scope=4, compression=3, settings=b"\x47"), 0), (dict(order=1, scope=1, compression=0), 0),
                                   (dict(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x03"), 1)],
    "same-order-stray-encryption": [(dict(order=0, scope=1, compression=3, settings=b"\xF8", stray_encryption=0), 0), (dict(order=0, scope=1, compression=3, settings=b"\xF8"), 0)],
}


def tie_file(encodings):
    """A track with the given (content_encoding arguments, type) pairs, in that order, and frames that are the same whatever they are."""
    enc = content_encodings(*[content_encoding(**kw) for kw, _ in encodings])
    sealed = [simple_block(1, i * 20, bytes((i * 3 + k) & 0xFF for k in range(30)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("same-order-encryption-and-stripping", "Header stripping and AES-CTR with scope 4 share ContentEncodingOrder 0 (which RFC 9559 forbids): the encodings cannot be ordered, so none is "
         "trusted, nothing is read, and the track is encrypted by an unknown algorithm since one of them is an encryption.", findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    return tie_file(TIE_CASES["same-order-encryption-and-stripping"])


@fixture("same-order-two-compressions", "Header stripping and zlib share ContentEncodingOrder 0: nothing is read, and with no encryption among them nothing is protected.",
         findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    return tie_file(TIE_CASES["same-order-two-compressions"])


@fixture("same-order-aes-modes", "Two AES encryptions that differ only in the cipher mode (CTR and CBC) share ContentEncodingOrder 0: the scheme names no algorithm, as it could be either.",
         findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    return tie_file(TIE_CASES["same-order-aes-modes"])


@fixture("same-order-over-encryption", "Two compressions share ContentEncodingOrder 1, one with scope 4, over AES-CTR at order 0: whatever the tie hides, the track is encrypted by an unknown algorithm.",
         findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    return tie_file(TIE_CASES["same-order-over-encryption"])


@fixture("same-order-stray-encryption", "Two header strippings share ContentEncodingOrder 0, one with a ContentEncryption element, which a reader ignores under type 0 (section "
         "5.1.4.1.31.8): the encodings cannot be ordered and nothing is read, but with no encoding of type 1 nothing is protected.",
         findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    return tie_file(TIE_CASES["same-order-stray-encryption"])


@fixture("encrypted-private-missing", "AES-CTR with scope 2 (the CodecPrivate) on a track that has none: the encryption covers nothing, so the track is not protected, and the Opus "
         "track lacks the CodecPrivate it needs.",
         findings=[("matroska.encryption-covers-nothing", "encrypts nothing"), ("matroska.codec-private-missing", "no CodecPrivate")], streams=[OPUS], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=2, encryption=5, cipher=1, key_id=b"\x03"))
    return build(standard(cluster(0, *blocks(count=4)), track=[opus_track(private=None, encodings=enc)]))


@fixture("encrypted-empty", "An encrypted track that no block uses: its protection is reported all the same.",
         findings=[("matroska.track-empty", "no block with frames"), ("matroska.no-clusters", "no Cluster")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x04"))
    return build(standard(track=[opus_track(encodings=enc)]))


@fixture("compression-bzlib", "A track compressed with bzlib, which the analyzer does not undo.",
         findings=[("matroska.track-encoding-not-read", "compressed with bzlib")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, compression=1))
    return build(standard(cluster(0, *blocks(count=3)), track=[opus_track(encodings=enc)]))


@fixture("encoding-order", "Two ContentEncodings with the same ContentEncodingOrder (RFC 9559 section 5.1.4.1.31.2): they cannot be ordered, so the header-stripped "
         "frames are not read.", findings=NOT_ORDERED, streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=1, scope=1, compression=3, settings=b"\xF8"), content_encoding(order=1, scope=2, compression=3, settings=b""))
    stripped = [simple_block(1, i * 20, opus_packet(i)[1:], key=True) for i in range(3)]
    return build(standard(cluster(0, *stripped), track=[opus_track(encodings=enc)]))


@fixture("video-and-audio", "A VP8 video track and an Opus track interleaved in the Clusters: the video is read at container level only.",
         findings=[("matroska.track-not-analyzed", "video track")], streams=[("Opus", 48000, 1)], extension="mkv")
def _():
    items = []
    for i in range(4):
        items.append(simple_block(1, i * 40, bytes([0x10 + i]) * 30, key=(i == 0)))
        items.append(simple_block(2, i * 40, opus_packet(i)))
        items.append(simple_block(2, i * 40 + 20, opus_packet(i + 4)))
    return build(standard(cluster(0, *items), track=[video_track(1), opus_track(2)]))


@fixture("two-audio-tracks", "Two audio tracks in one Segment, a stereo Opus track and a mono one, in the same Clusters.",
         streams=[("Opus", 48000, 2), ("Opus", 48000, 1)])
def _():
    items = []
    for i in range(4):
        items.append(simple_block(1, i * 20, opus_packet(i, 2), key=True))
        items.append(simple_block(2, i * 20, opus_packet(i, 1), key=True))
    return build(standard(cluster(0, *items), track=[opus_track(1, 2), opus_track(2, 1, language="deu", name="German")]))


@fixture("webm", "A WebM file: the DocType webm, a VP9 video track and an Opus track.", findings=[("matroska.track-not-analyzed", "video track")],
         streams=[("Opus", 48000, 1)], extension="webm")
def _():
    items = []
    for i in range(3):
        items.append(simple_block(1, i * 40, bytes([0x20 + i]) * 25, key=(i == 0)))
        items.append(simple_block(2, i * 40, opus_packet(i)))
    return build(standard(cluster(0, *items), doctype="webm", track=[video_track(1, "V_VP9"), opus_track(2)]))


@fixture("webm-not-allowed", "A WebM file with a PCM track and a TimestampScale of 500,000 ns, which the WebM Container Guidelines do not allow.",
         findings=[("matroska.webm-codec", "A_PCM/INT/LIT"), ("matroska.webm-timestamp-scale", "500000"), ("matroska.codec-not-analyzed", "PCM")],
         streams=[("PCM (integer, little-endian)", 48000, 2)], extension="webm")
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    items = [simple_block(1, i * 10, bytes(range(16)) * 4, key=True) for i in range(4)]
    return build(standard(cluster(0, *items), doctype="webm", track=[pcm], scale=500_000))


@fixture("tags-chapters-attachments", "Tags with a SimpleTag, nested Chapters, and an attached file, all of which the analyzer lists without reading the attached data.",
         streams=[OPUS])
def _():
    chapters = master(m.CHAPTERS, master(ID["EditionEntry"], master(ID["ChapterAtom"], uint(ID["ChapterUID"], 1), uint(ID["ChapterTimeStart"], 0),
                                                                    master(ID["ChapterDisplay"], text(ID["ChapString"], "One")),
                                                                    master(ID["ChapterAtom"], uint(ID["ChapterUID"], 2), uint(ID["ChapterTimeStart"], 50_000_000)))))
    attachments = master(m.ATTACHMENTS, master(ID["AttachedFile"], text(ID["FileName"], "cover.jpg"), text(ID["FileMediaType"], "image/jpeg"),
                                               raw(ID["FileData"], bytes(range(64))), uint(ID["FileUID"], 7)))
    return build([ebml_header(), segment(info(), tracks(opus_track()), chapters, attachments, tags(("TITLE", "A title"), ("ARTIST", "An artist")),
                                       cluster(0, *blocks(count=3)))])


@fixture("segment-info", "An Info element with a title, a date, a Segment UID and a duration, and a Segment UID of the wrong length.",
         findings=[("matroska.uid-length", "128 bits"), ("matroska.writer-claim-differs", "WritingApp")], streams=[OPUS], valid=False)
def _():
    inf = master(m.INFO, raw(ID["SegmentUUID"], bytes(range(16))), uint(ID["TimestampScale"], 1_000_000), flt(ID["Duration"], 60.0), raw(ID["DateUTC"], struct.pack(">q", 788_918_400_000_000_000)),
                 text(ID["Title"], "Segment title"), text(ID["MuxingApp"], "muxer 1.0"), text(ID["WritingApp"], "writer 2.0"), raw(0x3CB923, b"\x01\x02\x03"))
    return build([ebml_header(), segment(inf, tracks(opus_track()), cluster(0, *blocks(count=2)))])


@fixture("timestamp-scale-zero", "A TimestampScale of 0, which RFC 9559 forbids (section 5.1.2.9).", findings=[("matroska.timestamp-scale", "is 0")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(), segment(master(m.INFO, uint(ID["TimestampScale"], 0), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "x")), tracks(opus_track()),
                                       cluster(0, *blocks(count=2)))])


def laid_out(make):
    """Runs make(positions) until the positions it is given are those its elements have: it returns the file's elements, the named
    elements whose positions it wants (a dict) and the Segment. Positions are from the start of the Segment's data."""
    positions = {}
    for _ in range(4):
        elements, named, seg = make(positions)
        data = file_of(*elements)
        base = seg.offset + seg.header_length
        found = {k: v.offset - base for k, v in named.items()}
        if found == positions:
            return data
        positions = found
    raise SystemExit("the positions do not settle")


@fixture("seek-and-cues", "A SeekHead and Cues whose positions are right: every Seek entry points at the element it names and every cue at a Cluster.", streams=[OPUS])
def _():
    def make(p):
        head = seek_head((m.INFO, p.get("info", 0)), (m.TRACKS, p.get("tracks", 0)), (m.CLUSTER, p.get("c0", 0)), (m.CUES, p.get("cues", 0)))
        inf, trk = info(), tracks(opus_track())
        c0, c1 = cluster(0, *blocks(count=3)), cluster(60, *blocks(count=3, first=3))
        cu = cues((0, 1, p.get("c0", 0)), (60, 1, p.get("c1", 0)))
        seg = segment(head, inf, trk, c0, c1, cu)
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": c0, "c1": c1, "cues": cu}, seg

    return laid_out(make)


@fixture("seek-and-cues-wrong", "A SeekHead and Cues whose positions are wrong: a Seek entry that points at the wrong element and one outside the Segment, and a cue "
         "that points where no Cluster starts.", findings=[("matroska.seek-position", "Seek entr"), ("matroska.cue-position", "no Cluster starts")], streams=[OPUS])
def _():
    def make(p):
        head = seek_head((m.INFO, p.get("info", 0)), (m.TRACKS, p.get("tracks", 0) + 3), (m.CLUSTER, p.get("c0", 0)), (m.CUES, 999_999))
        inf, trk = info(), tracks(opus_track())
        c0, c1 = cluster(0, *blocks(count=3)), cluster(60, *blocks(count=3, first=3))
        cu = cues((0, 1, p.get("c0", 0)), (60, 1, p.get("c1", 0) + 5))
        seg = segment(head, inf, trk, c0, c1, cu)
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": c0, "c1": c1, "cues": cu}, seg

    return laid_out(make)


@fixture("missing-top-level", "A Segment with Clusters but no Info or Tracks, and one with no Cluster.",
         findings=[("matroska.missing-info", "Segment 1 holds no Info element"), ("matroska.missing-info", "Segment 2 holds no Info element"),
                   ("matroska.missing-tracks", "no Tracks element"), ("matroska.no-clusters", "no Cluster"), ("matroska.track-empty", "Track 1"),
                   ("matroska.block-track-unknown", "no TrackEntry describes")], valid=False)
def _():
    first = segment(cluster(0, *blocks(count=2)))
    second = segment(tracks(opus_track()))
    return build([ebml_header(), first, ebml_header(), second])


@fixture("two-segments", "Two Segments one after the other (RFC 9559 section 23.2), each with its own track and Cluster.", streams=[OPUS, ("Opus", 48000, 2)], valid=False)
def _():
    return build([ebml_header(), segment(info(), tracks(opus_track()), cluster(0, *blocks(count=3))),
                  segment(info(), tracks(opus_track(1, 2)), cluster(0, *blocks(count=3, channels=2)))])


@fixture("header-fields", "An EBML Header with an EBMLMaxIDLength of 5, an EBMLReadVersion of 2, a DocTypeReadVersion above its DocTypeVersion and above 4.",
         findings=[("matroska.header-limits", "EBMLMaxIDLength of 5"), ("matroska.header-version", "greater than its DocTypeVersion"),
                   ("matroska.header-version", "newer than the version 4"), ("matroska.header-version", "EBMLReadVersion of 2")], streams=[OPUS], valid=False)
def _():
    head = master(m.EBML, uint(ID["EBMLVersion"], 1), uint(ID["EBMLReadVersion"], 2), uint(ID["EBMLMaxIDLength"], 5), uint(ID["EBMLMaxSizeLength"], 8),
                  text(ID["DocType"], "matroska"), uint(ID["DocTypeVersion"], 5), uint(ID["DocTypeReadVersion"], 6))
    return build([head, segment(info(), tracks(opus_track()), cluster(0, *blocks(count=2)))])


@fixture("header-no-doctype", "An EBML Header with no DocType, which RFC 8794 requires (section 11.2.6).", findings=[("matroska.header-field", "no DocType")], streams=[OPUS], valid=False)
def _():
    head = master(m.EBML, uint(ID["EBMLVersion"], 1), uint(ID["EBMLReadVersion"], 1))
    return build([head, segment(info(), tracks(opus_track()), cluster(0, *blocks(count=2)))])


@fixture("track-fields", "TrackEntries that lack what RFC 9559 requires: no TrackNumber, a TrackNumber of 0, no CodecID, no TrackUID and no TrackType, an audio track with "
         "no Audio element, a repeated TrackNumber, and an audio track of 0 channels.",
         findings=[("matroska.track-field", "has no CodecDelay"), ("matroska.track-field", "has no CodecDelay"), ("matroska.track-field", "no TrackNumber"), ("matroska.track-field", "TrackNumber 0"), ("matroska.track-field", "no CodecID"),
                   ("matroska.track-field", "no TrackUID"), ("matroska.track-field", "no TrackType"), ("matroska.audio-fields", "without an Audio element"),
                   ("matroska.audio-fields", "without an Audio element"), ("matroska.audio-fields", "0 channels"), ("matroska.track-number-duplicate", "first repeated number is 5"),
                   ("matroska.track-empty", "A track without a number"), ("matroska.track-empty", "Track 0"), ("matroska.track-empty", "Track 3"),
                   ("matroska.track-empty", "Track 4"), ("matroska.track-empty", "Track 5"), ("matroska.codec-not-analyzed", "PCM"),
                   ("matroska.track-not-analyzed", "of no stated type")], valid=False)
def _():
    entries = [
        master(ID["TrackEntry"], uint(ID["TrackUID"], 1), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_OPUS")),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 0), uint(ID["TrackUID"], 2), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_FLAC"), master(ID["Audio"], uint(ID["Channels"], 2))),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 3), uint(ID["TrackUID"], 3), uint(ID["TrackType"], 2)),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 4), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_FLAC"), master(ID["Audio"], flt(ID["SamplingFrequency"], 44100.0), uint(ID["Channels"], 0))),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 5), uint(ID["TrackUID"], 5), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_PCM/INT/LIT"),
               master(ID["Audio"], flt(ID["SamplingFrequency"], 8000.0), uint(ID["Channels"], 1))),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 5), uint(ID["TrackUID"], 6), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_PCM/INT/LIT"),
               master(ID["Audio"], flt(ID["SamplingFrequency"], 8000.0), uint(ID["Channels"], 1))),
        master(ID["TrackEntry"], uint(ID["TrackNumber"], 7), uint(ID["TrackUID"], 7), text(ID["CodecID"], "A_OPUS")),
    ]
    return build([ebml_header(), segment(info(), tracks(*entries), cluster(0, simple_block(5, 0, b"\x00" * 8), simple_block(5, 10, b"\x00" * 8)))])


@fixture("codec-private", "Tracks whose CodecPrivate is missing (an Opus track) or invalid (a Vorbis track whose Xiph-laced headers are cut).",
         findings=[("matroska.codec-private-missing", "has no CodecPrivate"), ("matroska.codec-private-invalid", "counts 4 packets"),
                   ("matroska.codec-private-invalid", "three Xiph-laced Vorbis headers")], valid=False)
def _():
    entries = [audio_track(1, "A_OPUS", 1, 48000.0, delay=6_500_000), audio_track(2, "A_VORBIS", 2, 44100.0, private=bytes([3, 30, 40, 1, 2, 3])),
               audio_track(3, "A_VORBIS", 2, 44100.0, private=bytes([2, 30, 40, 1, 2, 3]))]
    items = [simple_block(t, i * 20, b"\x00" * 10) for i in range(2) for t in (1, 2, 3)]
    return build(standard(cluster(0, *items), track=entries))


@fixture("audio-differs", "A track that states a different sampling frequency and channel count than its Opus headers (a mono OpusHead for a stereo track), and an "
         "Opus CodecDelay that is not the pre-skip.",
         findings=[("matroska.channels-differ", "states 2 channels"), ("opus.container-delay-differs", "codec delay of 0.001 s")], streams=[OPUS], valid=False)
def _():
    return build(standard(cluster(0, *blocks(count=3)), track=[audio_track(1, "A_OPUS", 2, 48000.0, private=opus_head(1), delay=1_000_000, preroll=80_000_000)]))


@fixture("webm-aac", "A WebM file with an AAC track and a Matroska-only element, which the WebM Container Guidelines do not allow (the codec is AAC).",
         findings=[("matroska.webm-codec", "A_AAC"), ("aac.no-configuration", "AudioSpecificConfig")], extension="webm", valid=False)
def _():
    aac = audio_track(1, "A_AAC", 2, 44100.0)
    return build(standard(cluster(0, simple_block(1, 0, b"\x21\x00\x49\x90\x02\x19\x00\x23\x80", key=True), simple_block(1, 23, b"\x21\x00\x49\x90\x02\x19\x00\x23\x80", key=True)),
                          doctype="webm", track=[aac]))


# --- Damage --------------------------------------------------------------------------------------------


@fixture("size-past-parent", "Elements whose sizes run past their parents (RFC 8794 section 7.7): a TimestampScale inside Info and a SimpleBlock in a Cluster.",
         findings=[("matroska.element-overruns-parent", "2 elements state a size that runs past the end of their parent")], valid=False, streams=[OPUS])
def _():
    cl = cluster(0, *blocks(count=2))
    cl.children.append(E(m.SIMPLEBLOCK, m.block_payload(1, 40, [opus_packet(2)]), size=500))
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), text(ID["MuxingApp"], "x"), E(ID["WritingApp"], b"x", size=100))
    return build([ebml_header(), segment(inf, tracks(opus_track()), cl, cluster(100, *blocks(count=20, start=0, first=10)))])


@fixture("absurd-sizes", "Elements that state 2^56 - 2 bytes, the largest size EBML can state: a Void element in Info and one in a Cluster.",
         findings=[("matroska.element-truncated", "2 elements state a size that runs past the end of the file")], valid=False, streams=[OPUS])
def _():
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), text(ID["MuxingApp"], "x"), E(m.VOID, b"v", size=(1 << 56) - 2, size_width=8))
    cl = cluster(0, *blocks(count=2))
    cl.children.append(E(m.VOID, b"zzzz", size=(1 << 56) - 2, size_width=8))
    return build([ebml_header(), segment(inf, tracks(opus_track()), cl)])


@fixture("deep-nesting", "Chapters nested 60 levels deep in ChapterAtoms, which no Matroska file does.", findings=[("matroska.too-deep", "levels deep")], valid=False, streams=[OPUS])
def _():
    atom = uint(ID["ChapterUID"], 1)
    for _depth in range(60):
        atom = master(ID["ChapterAtom"], uint(ID["ChapterUID"], 1), atom)
    return build([ebml_header(), segment(info(), tracks(opus_track()), master(m.CHAPTERS, master(ID["EditionEntry"], atom)), cluster(0, *blocks(count=2)))])


@fixture("invalid-headers", "Bytes where an element header should be that are no element: a zero byte in Info, a BlockGroup, a Cluster and the Segment.",
         findings=[("matroska.element-header-invalid", "begins with a byte of zero bits")] * 4, valid=False, streams=[OPUS])
def _():
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), raw(0x4D80, b"x"), E(0x00, b"", id_raw=b"\x00\x00\x00\x00", size=0, size_width=1))
    grp = master(m.BLOCKGROUP, E(m.BLOCK, m.block_payload(1, 40, [opus_packet(2)], is_block=True)), E(0x00, b"", id_raw=b"\x00\x00\x00"))
    cl = cluster(0, *blocks(count=2), grp)
    cl.children.append(E(0x00, b"", id_raw=b"\x00\x00\x00\x00"))
    return build([ebml_header(), segment(inf, tracks(opus_track()), cl, E(0x00, b"", id_raw=b"\x00\x00\x00\x00\x00\x00"))])


@fixture("trailing-data", "Bytes after the Segment that are no element.", findings=[("matroska.element-header-invalid", "from there are not read")], valid=False, streams=[OPUS])
def _():
    return build(standard(cluster(0, *blocks(count=2)))) + b"\x00\x00\x00trailing bytes"


@fixture("unknown-track", "Blocks that name a track no TrackEntry describes.", findings=[("matroska.block-track-unknown", "no TrackEntry describes")], valid=False, streams=[OPUS])
def _():
    return build(standard(cluster(0, simple_block(1, 0, opus_packet(0)), simple_block(9, 20, opus_packet(1)), simple_block(9, 40, opus_packet(2)), simple_block(1, 60, opus_packet(3)))))


@fixture("timestamps-backwards", "An audio track whose block timestamps go back in time.", findings=[("matroska.timestamps-backwards", "go back in time")], valid=False, streams=[OPUS])
def _():
    items = [simple_block(1, 0, opus_packet(0)), simple_block(1, 40, opus_packet(1)), simple_block(1, 20, opus_packet(2)), simple_block(1, 60, opus_packet(3))]
    return build(standard(cluster(100, *items), cluster(50, *blocks(count=1, first=9))))


@fixture("cluster-timestamps", "Clusters whose Timestamp is missing, comes after a block, or repeats.",
         findings=[("matroska.cluster-timestamp-missing", "no Timestamp"), ("matroska.cluster-timestamp-position", "should be the first child")], streams=[OPUS], valid=False)
def _():
    no_timestamp = master(m.CLUSTER, *blocks(count=2))
    late = master(m.CLUSTER, *blocks(count=1, start=60, first=2), uint(m.TIMESTAMP, 40), uint(m.TIMESTAMP, 41), *blocks(count=1, start=40, first=3))
    return build(standard(no_timestamp, late))


@fixture("misplaced-elements", "Elements the schema places elsewhere: a SimpleBlock directly in the Segment, a Cluster in Info and an Info at the root of the document; "
         "and a Tags element of unknown size, where the schema allows none.",
         findings=[("matroska.misplaced-element", "3 elements are placed where the schema does not allow"), ("matroska.unknown-size-not-allowed", "unknown size")],
         valid=False, streams=[OPUS])
def _():
    odd_info = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), master(m.CLUSTER, uint(m.TIMESTAMP, 0)), text(ID["MuxingApp"], "x"))
    live_tags = tags(("TITLE", "x"), crc=False)
    live_tags.size = "unknown"
    return build([ebml_header(), segment(odd_info, tracks(opus_track()), simple_block(1, 0, opus_packet(0)), cluster(0, *blocks(count=2)), live_tags),
                  master(m.INFO, text(ID["MuxingApp"], "x"))])


@fixture("text-and-values", "Values the types do not allow: a UTF-8 title with an invalid sequence, a String with a control character, an unsigned integer of nine "
         "bytes and a float of three.", findings=[("matroska.invalid-text", "characters its type does not allow"), ("matroska.value-size", "2 elements hold data"),
                                                  ("matroska.track-field", "no TrackUID")], valid=False, streams=[OPUS])
def _():
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), E(ID["Title"], b"caf\xE9 \xFF"), E(ID["Duration"], b"\x00\x00\x00"),
                 E(ID["MuxingApp"], b"app\x01"), text(ID["WritingApp"], "app"))
    tr = master(m.TRACKS, master(ID["TrackEntry"], uint(ID["TrackNumber"], 1), E(ID["TrackUID"], bytes(9)), uint(ID["TrackType"], 2), text(ID["CodecID"], "A_OPUS"),
                                 raw(ID["CodecPrivate"], opus_head()), uint(ID["CodecDelay"], 6_500_000), master(ID["Audio"], flt(ID["SamplingFrequency"], 48000.0), uint(ID["Channels"], 1))))
    return build([ebml_header(), segment(inf, tr, cluster(0, *blocks(count=2)))])


@fixture("size-length", "An EBML Header that limits sizes to two octets (EBMLMaxSizeLength 2) in a file whose elements state sizes in four.",
         findings=[("matroska.size-too-long", "EBMLMaxSizeLength")], valid=False, streams=[OPUS])
def _():
    head = ebml_header(max_size=2)
    cl = cluster(0, *blocks(count=2))
    cl.children[0].size_width = 4
    return build([head, segment(info(), tracks(opus_track()), cl)])


# --- Cut files -------------------------------------------------------------------------------------------


@fixture("cut-at-segment", "A file that starts at the Segment's first child: no EBML Header and no Segment header, as a recording cut at its start looks.",
         findings=[("matroska.no-ebml-header", "does not start with an EBML Header"), ("matroska.segment-header-missing", "no Segment element before them")],
         valid=False, streams=[OPUS])
def _():
    elements = standard(cluster(0, *blocks(count=3)), cluster(60, *blocks(count=3, first=3)))
    seg = elements[1]
    return file_of(*seg.children)


@fixture("cut-at-cluster", "A file that starts at a Cluster: Clusters and Cues with no header, Segment or Tracks, so the blocks name tracks no TrackEntry describes.",
         findings=[("matroska.no-ebml-header", "does not start with an EBML Header"), ("matroska.segment-header-missing", "no Segment element before them"),
                   ("matroska.block-track-unknown", "no TrackEntry describes")], valid=False)
def _():
    return file_of(cluster(60, *blocks(count=3, first=3)), cluster(120, *blocks(count=3, first=6)), cues((60, 1, 0)))


@fixture("truncated-cluster", "A file that ends in the middle of its last Cluster's SimpleBlock.",
         findings=[("matroska.element-truncated", "The Segment states"), ("matroska.element-truncated", "2 elements state a size that runs past the end of the file")],
         valid=False, streams=[OPUS])
def _():
    data = file_of(*standard(cluster(0, *blocks(count=3)), cluster(60, *blocks(count=4, first=3))))
    return data[:-25]


@fixture("truncated-header", "A file that ends inside an element header, in the middle of a Cluster.",
         findings=[("matroska.element-truncated", "The Segment states"), ("matroska.element-truncated", "An element states a size"),
                   ("matroska.element-header-invalid", "ends inside its element header")], valid=False, streams=[OPUS])
def _():
    elements = standard(cluster(0, *blocks(count=3)), cluster(60, *blocks(count=2, first=3)))
    data = file_of(*elements)
    last = elements[1].children[-1].children[-1]
    return data[:last.offset + 1]


@fixture("truncated-ebml", "A file that ends inside the EBML Header.", findings=[("matroska.element-truncated", "2 elements state a size that runs past the end of the file")], valid=False)
def _():
    return file_of(ebml_header())[:20]


# --- Legacy AAC IDs, CRC-32 cut by its parent, empty elements, live files with tags, damaged encodings ---------------------


ADTS = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "adts"


def adts_frames(name):
    """(object type, sampling frequency index, channel configuration, raw data blocks) of an ADTS file's frames, headers stripped."""
    data = (ADTS / f"{name}.aac").read_bytes()
    position = 0
    frames = []
    while position + 7 <= len(data) and data[position] == 0xFF and data[position + 1] & 0xF0 == 0xF0:
        length = ((data[position + 3] & 3) << 11) | (data[position + 4] << 3) | (data[position + 5] >> 5)
        header = 7 if data[position + 1] & 1 else 9
        frames.append(data[position + header:position + length])
        position += length
    return (data[2] >> 6) + 1, (data[2] >> 2) & 15, ((data[2] & 1) << 2) | (data[3] >> 6), frames


AAC_RATES = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350]


def legacy_aac(codec, name, output_rate=None, rate_override=None, apps=None):
    """A track with a legacy A_AAC/MPEG2|4/* ID, which has no CodecPrivate (draft-ietf-cellar-codec section 3.4.2 and following)."""
    _, rate_index, channels, frames = adts_frames(name)
    rate = float(rate_override or AAC_RATES[rate_index])
    extra = (flt(ID["OutputSamplingFrequency"], output_rate),) if output_rate else ()
    track = audio_track(1, codec, channels, rate, audio_extra=extra)
    items = [simple_block(1, round(i * 1024 * 1000 / rate), frame, key=True) for i, frame in enumerate(frames)]
    if apps is not None:
        inf = info(duration=100.0, muxing=apps[0], writing=apps[1])
        return build([ebml_header(), segment(inf, tracks(track), cluster(0, *items))]), channels, int(output_rate or rate)
    return build(standard(cluster(0, *items), track=[track])), channels, int(output_rate or rate)


@fixture("aac-legacy-lc", "A track with the legacy ID A_AAC/MPEG4/LC and no CodecPrivate: the AudioSpecificConfig is worked out from the ID and the Audio element "
         "(the object type, the sampling frequency and the channels).", streams=[("AAC", 44100, 2)])
def _():
    return legacy_aac("A_AAC/MPEG4/LC", "stereo")[0]


@fixture("aac-legacy-stray-codec-private", "A track with the legacy ID A_AAC/MPEG4/LC and a CodecPrivate, which the codec mapping does not define for it (\"Initialization: none\"): it would configure "
         "48 kHz mono, but it is not handed over, and the AudioSpecificConfig is worked out from the ID and the Audio element as for aac-legacy-lc.",
         findings=[("matroska.codec-private-unexpected", "does not define")], streams=[("AAC", 44100, 2)], valid=False)
def _():
    _, rate_index, channels, frames = adts_frames("stereo")
    rate = float(AAC_RATES[rate_index])
    stray = struct.pack(">H", (2 << 11) | (3 << 7) | (1 << 3))
    items = [simple_block(1, round(i * 1024 * 1000 / rate), frame, key=True) for i, frame in enumerate(frames)]
    return build(standard(cluster(0, *items), track=[audio_track(1, "A_AAC/MPEG4/LC", channels, rate, private=stray)]))


@fixture("aac-legacy-mpeg2-main", "A track with the legacy ID A_AAC/MPEG2/MAIN, whose frames use the main profile's prediction.", streams=[("AAC", 44100, 1)])
def _():
    return legacy_aac("A_AAC/MPEG2/MAIN", "main-prediction")[0]


@fixture("aac-legacy-ltp", "A track with the legacy ID A_AAC/MPEG4/LTP, whose frames use long term prediction.", streams=[("AAC", 44100, 2)])
def _():
    return legacy_aac("A_AAC/MPEG4/LTP", "ltp")[0]


@fixture("aac-legacy-ssr", "A track with the legacy ID A_AAC/MPEG4/SSR, whose frames carry gain control.", streams=[("AAC", 44100, 1)])
def _():
    return legacy_aac("A_AAC/MPEG4/SSR", "ssr-gain-control")[0]


@fixture("aac-legacy-sbr", "A track with the legacy ID A_AAC/MPEG4/LC/SBR: the sampling frequency is the core's (22050 Hz) and the OutputSamplingFrequency the "
         "SBR output's (44100 Hz), which the worked out AudioSpecificConfig signals explicitly.", streams=[("AAC", 44100, 2)])
def _():
    return legacy_aac("A_AAC/MPEG4/LC/SBR", "sbr-fixvar-resolutions", output_rate=44100.0)[0]


@fixture("aac-legacy-explicit-rate", "A track with the legacy ID A_AAC/MPEG4/LC whose SamplingFrequency (44000 Hz) is not one of the thirteen the AudioSpecificConfig has an "
         "index for: the worked out configuration gives the frequency in full (index 15 and 24 bits), as ISO/IEC 14496-3 section 1.6.3.3 has it.",
         streams=[("AAC", 44000, 2)], valid=False)
def _():
    return legacy_aac("A_AAC/MPEG4/LC", "stereo", rate_override=44000)[0]


@fixture("aac-legacy-sbr-core-rate", "A track with the legacy ID A_AAC/MPEG4/LC/SBR and no OutputSamplingFrequency: the SBR extension of the worked out configuration "
         "gives the core's sampling frequency.", streams=[("AAC", 22050, 2)], valid=False)
def _():
    return legacy_aac("A_AAC/MPEG4/LC/SBR", "sbr-fixvar-resolutions")[0]


@fixture("crc-cut-by-parent", "A Cues element that holds only the header of a CRC-32 element (BF 84) and no value: the parent ends before the CRC-32 element's 4 bytes.",
         findings=[("matroska.crc-size", "holds 0 bytes"), ("matroska.element-overruns-parent", "CRC-32")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(), segment(info(), tracks(opus_track()), E(m.CUES, b"\xBF\x84"), cluster(0, *blocks(count=3)))])


@fixture("crc-cut-at-end", "A file whose last element is a Tags element that holds only the header of a CRC-32 element (BF 84) and ends there.",
         findings=[("matroska.crc-size", "holds 0 bytes"), ("matroska.element-truncated", "CRC-32")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(), segment(info(), tracks(opus_track()), cluster(0, *blocks(count=3)), E(m.TAGS, b"\xBF\x84"))])[:-2] + b"\xBF\x84"


@fixture("cut-at-segment-seeks", "A file cut at the start of its Segment's data whose SeekHead and Cues, written for the whole file, give positions the cut file does not "
         "have: they cannot be resolved, so no finding says they point nowhere.",
         findings=[("matroska.no-ebml-header", "does not start with an EBML Header"), ("matroska.segment-header-missing", "no Segment element before them")],
         streams=[OPUS], valid=False)
def _():
    def make(p):
        head = seek_head((m.INFO, p.get("info", 0)), (m.TRACKS, p.get("tracks", 0)), (m.CLUSTER, p.get("c0", 0)), (m.CUES, p.get("cues", 0)))
        inf, trk = info(), tracks(opus_track())
        c0, c1 = cluster(0, *blocks(count=3)), cluster(60, *blocks(count=3, first=3))
        cu = cues((0, 1, p.get("c0", 0)), (60, 1, p.get("c1", 0)))
        seg = segment(head, inf, trk, c0, c1, cu)
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": c0, "c1": c1, "cues": cu}, seg

    data = laid_out(make)
    # The cut drops the EBML Header and the Segment's header, and the SeekHead too, so the first element is the Info.
    first = data.index(b"\x15\x49\xA9\x66")
    return data[data.index(b"\x15\x49\xA9\x66", first + 4):]


@fixture("segment-crc-truncated", "A Segment with a CRC-32 whose size runs past the end of the file: the file is cut short, so the CRC covers data the file lacks and no "
         "verdict is given.", findings=[("matroska.element-truncated", "The Segment states")], streams=[OPUS], valid=False)
def _():
    seg = segment(Crc(), info(), tracks(opus_track()), cluster(0, *blocks(count=3)), size=1000)
    return build([ebml_header(), seg])


@fixture("live-unknown-element", "A live recording with an element the schema does not list (0x4DDD) in the middle of a Cluster of unknown size, whose last frame ends in "
         "128 bytes shaped like an ID3v1 tag: the unknown element is skipped by its size, as the parse does (RFC 8794 section 6.2), so the recording runs to its end and "
         "the bytes are part of a frame, not a tag.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("matroska.unknown-element", "0x4DDD"),
                   ("tag.near-miss", "ID3v1 signature")], streams=[OPUS], valid=False)
def _():
    clusters = [cluster(i * 100, *blocks(count=3, first=i * 5), E(0x4DDD, b"odd"), *blocks(count=2, start=60, first=i * 5 + 3), unknown=True) for i in range(2)]
    clusters[-1].children.append(simple_block(1, 90, opus_packet(99) + ID3V1, key=True))
    return build(standard(*clusters)[:1] + [segment(info(), tracks(opus_track()), *clusters, unknown=True)])


@fixture("empty-elements", "Elements with no data, which take their default values (RFC 8794 section 6.1): an empty TimestampScale (1,000,000), FlagLacing (1: lacing "
         "allowed), FlagEnabled (1) and Language (\"eng\"), with a laced block that FlagLacing allows.", streams=[OPUS])
def _():
    track = master(ID["TrackEntry"], uint(ID["TrackNumber"], 1), uint(ID["TrackUID"], 0x1001), uint(ID["TrackType"], 2), E(ID["FlagEnabled"], b""), E(ID["FlagLacing"], b""),
                   E(ID["Language"], b""), text(ID["CodecID"], "A_OPUS"), raw(ID["CodecPrivate"], opus_head()), uint(ID["CodecDelay"], 6_500_000),
                   master(ID["Audio"], flt(ID["SamplingFrequency"], 48000.0), uint(ID["Channels"], 1)))
    items = [simple_block(1, 0, [opus_packet(0), opus_packet(1)], lacing="xiph", key=True), simple_block(1, 40, opus_packet(2), key=True)]
    inf = master(m.INFO, E(ID["TimestampScale"], b""), flt(ID["Duration"], 100.0), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "x"))
    return build([ebml_header(), segment(inf, tracks(track), cluster(0, *items))])


@fixture("registry-elements", "Elements the Matroska element registry lists and RFC 9559 does not, which muxers write: an edition's hidden flag and display strings, a "
         "chapter's enabled flag, skip type and tracks, a tag's BlockAddIDValue and a track's Audio emphasis.", streams=[OPUS])
def _():
    chapters = master(m.CHAPTERS, master(ID["EditionEntry"], uint(ID["EditionUID"], 5), uint(ID["EditionFlagHidden"], 0), uint(ID["EditionFlagDefault"], 1),
                                          master(ID["EditionDisplay"], text(ID["EditionString"], "Main"), text(ID["EditionLanguageIETF"], "en")),
                                          master(ID["ChapterAtom"], uint(ID["ChapterUID"], 1), uint(ID["ChapterTimeStart"], 0), uint(ID["ChapterFlagEnabled"], 1),
                                                 uint(ID["ChapterSkipType"], 1), master(ID["ChapterTrack"], uint(ID["ChapterTrackUID"], 0x1001)),
                                                 master(ID["ChapterDisplay"], text(ID["ChapString"], "One")))))
    track = opus_track(audio_extra=(uint(ID["Emphasis"], 0),))
    tg = master(m.TAGS, master(ID["Tag"], master(ID["Targets"], uint(ID["TargetTypeValue"], 50), uint(ID["TagBlockAddIDValue"], 0)),
                               master(ID["SimpleTag"], text(ID["TagName"], "TITLE"), text(ID["TagString"], "x"))))
    return build([ebml_header(), segment(info(), tracks(track), chapters, tg, cluster(0, *blocks(count=3)))])


@fixture("unknown-size-then-segment", "A Segment of unknown size whose last Cluster has the unknown size, followed by a second Segment: each ends where the next Segment "
         "starts (RFC 8794 section 6.2).", findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")],
         streams=[OPUS, ("Opus", 48000, 2)], valid=False)
def _():
    first = segment(info(), tracks(opus_track()), cluster(0, *blocks(count=3), unknown=True), unknown=True)
    second = segment(info(), tracks(opus_track(1, 2)), cluster(0, *blocks(count=3, channels=2)))
    return build([ebml_header(), first, second])


ID3V1 = b"TAG" + b"A live recording".ljust(30, b"\0") + b"A speaker".ljust(30, b"\0") + b"A room".ljust(30, b"\0") + b"2024" + b"No comment".ljust(30, b"\0") + b"\x0c"


def ape_tag(pairs=((b"Artist", b"A speaker"), (b"Title", b"A live recording"))):
    """An APEv2 tag with a header, the items and a footer (APE tag specification 2.000)."""
    def item(key, value, flags=0):
        return struct.pack("<II", len(value), flags) + key + b"\0" + value

    items = b"".join(item(*pair) for pair in pairs)
    size = len(items) + 32
    header = b"APETAGEX" + struct.pack("<IIII", 2000, size, len(pairs), 0xA0000000) + bytes(8)
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, size, len(pairs), 0x80000000) + bytes(8)
    return header + items + footer


def live_file_of(clusters):
    """A live recording (Segment of unknown size) of the given Clusters."""
    return build(standard(*clusters)[:1] + [segment(info(), tracks(opus_track()), *clusters, unknown=True)])


def live_file():
    return live_file_of([cluster(i * 100, *blocks(count=5, first=i * 5), unknown=True) for i in range(3)])


@fixture("live-id3v1", "A live recording (Segment and Clusters of unknown size) with an ID3v1 tag appended: the tag after the last Cluster is the file's, not an element's.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")], streams=[OPUS], valid=False)
def _():
    return live_file(), ID3V1


@fixture("live-ape-id3v1", "A live recording with an APEv2 tag and an ID3v1 tag appended.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")], streams=[OPUS], valid=False)
def _():
    return live_file(), ape_tag() + ID3V1


@fixture("zlib-private-damaged", "A track whose CodecPrivate is zlib-compressed (scope 2) but holds bytes that are no zlib data: the track is reported with a finding, not "
         "analyzed.", findings=[("matroska.codec-private-invalid", "cannot be read back")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=2, compression=0))
    return build(standard(cluster(0, *blocks(count=3)), track=[opus_track(private=b"this is no zlib data at all", encodings=enc)]))


@fixture("zlib-frames-damaged", "A track whose frames are zlib-compressed (scope 1) but of which the second block holds bytes that are no zlib data: the frames up to it are "
         "analyzed and the rest are not.", findings=[("matroska.frame-encoding-invalid", "cannot be read back")], streams=[OPUS], valid=False)
def _():
    packed = [simple_block(1, 0, zlib.compress(opus_packet(0), 9), key=True), simple_block(1, 20, b"\x78\x9c not zlib", key=True),
              simple_block(1, 40, zlib.compress(opus_packet(2), 9), key=True)]
    enc = content_encodings(content_encoding(order=0, scope=1, compression=0))
    return build(standard(cluster(0, *packed), track=[opus_track(encodings=enc)]))


# --- Writers: the Segment's MuxingApp and WritingApp are claims of the writer role ----------------------------------------------


def with_apps(muxing, writing, tracks_=None, items=None):
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), flt(ID["Duration"], 100.0), text(ID["MuxingApp"], muxing), text(ID["WritingApp"], writing))
    return build([ebml_header(), segment(inf, tracks(*(tracks_ or [opus_track()])), cluster(0, *(items or blocks(count=4))))])


@fixture("writer-mkvmerge", "A Segment written by mkvmerge: the MuxingApp names the libraries (\"libebml v1.4.5 + libmatroska v1.7.1\") and the WritingApp the program, which "
         "the Writer node names; the library says no more than the program that uses it, so the two do not conflict.", streams=[OPUS])
def _():
    return with_apps("libebml v1.4.5 + libmatroska v1.7.1", "mkvmerge v82.0 ('I'm The President') 64-bit")


@fixture("writer-lavf", "A Segment written by ffmpeg: both apps hold the muxer's version \"Lavf60.16.100\", which names FFmpeg as the writer, with two pieces of evidence.",
         streams=[OPUS])
def _():
    return with_apps("Lavf60.16.100", "Lavf60.16.100")


@fixture("writer-lavc-id", "A WritingApp that holds a libavcodec encoder ID (\"Lavc60.31.102 aac\") on an AAC track: it is a claim of the encoder role, which reaches the AAC parser "
         "(that names ffmpeg's native encoder only from the stream's own evidence); the MuxingApp's \"Lavf\" is the writer.", streams=[("AAC", 44100, 2)])
def _():
    return legacy_aac("A_AAC/MPEG4/LC", "stereo", apps=("Lavf60.16.100", "Lavc60.31.102 aac"))[0]


@fixture("writer-apps-differ", "A MuxingApp and a WritingApp that name different software, in a Segment with two Opus tracks: one finding for the Segment, not one for each track, "
         "and the WritingApp, the application that wrote the file, names the writer.", findings=[("matroska.writer-claim-differs", "MuxingApp")],
         streams=[OPUS, ("Opus", 48000, 2)], valid=False)
def _():
    return with_apps("muxer 1.0", "writer 2.0", [opus_track(1), opus_track(2, 2)], blocks(1, 2) + blocks(2, 2, channels=2))


@fixture("writer-pcm-lame", "A PCM track, which no codec parser reads, in a Segment whose MuxingApp names one program and whose WritingApp LAME, which writes MPEG audio: the "
         "WritingApp names the writer, the container reports one finding for the two apps and one for LAME writing MPEG audio, not PCM.",
         findings=[("matroska.writer-claim-differs", "MuxingApp"), ("matroska.writer-other-format", "LAME"), ("matroska.codec-not-analyzed", "PCM")],
         streams=[("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    return with_apps("foo 1.0", "LAME 3.100", [pcm], [simple_block(1, i * 10, bytes(range(16)) * 4, key=True) for i in range(4)])


@fixture("writer-two-segments", "Two Segments, one written by ffmpeg and one by mkvmerge: each Segment's apps are the writer claims of its own tracks only.",
         streams=[OPUS, ("Opus", 48000, 2)], valid=False)
def _():
    def seg(muxing, writing, track, channels, first):
        inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), flt(ID["Duration"], 100.0), text(ID["MuxingApp"], muxing), text(ID["WritingApp"], writing))
        return segment(inf, tracks(track), cluster(0, *blocks(count=3, channels=channels, first=first)))

    return build([ebml_header(), seg("Lavf60.16.100", "Lavf60.16.100", opus_track(), 1, 0),
                  seg("libebml v1.4.5 + libmatroska v1.7.1", "mkvmerge v82.0 ('I'm The President') 64-bit", opus_track(1, 2), 2, 3)])


@fixture("writer-pcm-ffmpeg", "A PCM track written by ffmpeg: the container names FFmpeg as the writer of a stream no codec parser reads.",
         findings=[("matroska.codec-not-analyzed", "PCM")], streams=[("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    return with_apps("Lavf60.16.100", "Lavf60.16.100", [pcm], [simple_block(1, i * 10, bytes(range(16)) * 4, key=True) for i in range(4)])


# --- Writers: the file's tags and the Segment's apps --------------------------------------------------------------------------

# What the model cannot know: the writer a file's tag names, which outweighs the Segment's apps (the tags' claims come first, as the router hands
# them to the codec parsers), for the fixtures that append one.
TAG_WRITERS = {}
ENCODER_TAG = ape_tag(((b"Encoder", b"bar 2.0"),))
PCM_BLOCKS = [simple_block(1, i * 10, bytes(range(16)) * 4, key=True) for i in range(4)]


@fixture("writer-tag-and-apps", "A PCM track (no codec parser) and an AAC track (the AAC parser) in a Segment whose apps name \"foo 1.0\", and an APEv2 tag whose Encoder item names "
         "\"bar 2.0\": the tag's claim is weighed first for both tracks, so both name \"bar 2.0\", and the conflict is reported once by the container and once by the AAC parser.",
         findings=[("matroska.writer-claim-differs", "MuxingApp"), ("matroska.writer-claim-differs", "WritingApp"), ("aac.writer-claim-differs", "WritingApp"),
                   ("matroska.codec-not-analyzed", "PCM")],
         streams=[("PCM (integer, little-endian)", 48000, 2), ("AAC", 44100, 2)], valid=False)
def _():
    _, rate_index, channels, frames = adts_frames("stereo")
    aac = audio_track(2, "A_AAC/MPEG4/LC", channels, float(AAC_RATES[rate_index]))
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    items = PCM_BLOCKS + [simple_block(2, round(i * 1024 * 1000 / AAC_RATES[rate_index]), frame, key=True) for i, frame in enumerate(frames)]
    return with_apps("foo 1.0", "foo 1.0", [pcm, aac], items), ENCODER_TAG


TAG_WRITERS["writer-tag-and-apps"] = "bar 2.0"


@fixture("writer-tag-conflicts-two-pcm", "Two PCM tracks in a Segment whose apps name \"foo 1.0\" and an APEv2 Encoder item \"bar 2.0\": the conflict with the tag is one finding for the "
         "Segment, not one for each track.",
         findings=[("matroska.writer-claim-differs", "MuxingApp"), ("matroska.writer-claim-differs", "WritingApp"), ("matroska.codec-not-analyzed", "Track 1"),
                   ("matroska.codec-not-analyzed", "Track 2")],
         streams=[("PCM (integer, little-endian)", 48000, 2), ("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    pcm1, pcm2 = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16), audio_track(2, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    items = PCM_BLOCKS + [simple_block(2, i * 10, bytes(range(16)) * 4, key=True) for i in range(4)]
    return with_apps("foo 1.0", "foo 1.0", [pcm1, pcm2], items), ENCODER_TAG


TAG_WRITERS["writer-tag-conflicts-two-pcm"] = "bar 2.0"


@fixture("writer-tag-without-info", "A Segment with no Info element, so no apps, and an APEv2 Encoder item \"bar 2.0\": the tag still names the writer of the PCM track.",
         findings=[("matroska.missing-info", "no Info"), ("matroska.codec-not-analyzed", "PCM")], streams=[("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, 48000.0, bits=16)
    return build([ebml_header(), segment(tracks(pcm), cluster(0, *PCM_BLOCKS))]), ENCODER_TAG


TAG_WRITERS["writer-tag-without-info"] = "bar 2.0"


# --- Gate 2: TrackTimestampScale, tags after live recordings, truncated seeks, misplaced values, null octets, header limits, sampling frequencies ----------


PCM_RATE = 48000.0
TS_SCALE_TRACK_EXTRA = (flt(0x23314F, 2.0),)


@fixture("track-timestamp-scale", "A PCM track with a TrackTimestampScale of 2.0 and three Clusters at 0, 1000 and 2000 ticks, each with blocks at 0 and 10 Track Ticks: a block's time is "
         "(Cluster Timestamp + block timestamp times TrackTimestampScale) times TimestampScale (RFC 9559 section 11.2), so the Cluster's Timestamp is not scaled.", findings=[("matroska.codec-not-analyzed", "PCM")], streams=[("PCM (integer, little-endian)", 48000, 2)])
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, PCM_RATE, bits=16, extra=TS_SCALE_TRACK_EXTRA)
    clusters = [cluster(t, *[simple_block(1, ts, bytes(range(16)) * 4, key=True) for ts in (0, 10)]) for t in (0, 1000, 2000)]
    return build(standard(*clusters, track=[pcm]))


@fixture("tracks-after-clusters", "A PCM track with a TrackTimestampScale of 2.0 whose Tracks element comes after the Cluster, which a SeekHead before the Cluster references (RFC 9559 "
         "section 6.1 allows it): the blocks' times are worked out with the scale once the Tracks are read, so the blocks at 0 and 1000 Track Ticks are 2 s apart.",
         findings=[("matroska.codec-not-analyzed", "PCM")], streams=[("PCM (integer, little-endian)", 48000, 2)])
def _():
    def make(p):
        head = seek_head((m.INFO, p.get("info", 0)), (m.CLUSTER, p.get("c0", 0)), (m.TRACKS, p.get("tracks", 0)))
        inf = info()
        trk = tracks(audio_track(1, "A_PCM/INT/LIT", 2, PCM_RATE, bits=16, extra=TS_SCALE_TRACK_EXTRA))
        c0 = cluster(0, *[simple_block(1, ts, bytes(range(16)) * 4, key=True) for ts in (0, 1000)])
        seg = segment(head, inf, c0, trk)
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": c0}, seg

    return laid_out(make)


@fixture("encrypted-track-timestamp-scale", "An encrypted Opus track with a TrackTimestampScale of 2.0 in Clusters at 0 and 1000 ticks: its length comes from the container's timing, "
         "which scales the blocks' timestamps and not the Clusters'.", findings=[("matroska.track-encrypted", "is encrypted (AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01\x02"))

    def sealed(first):
        return [simple_block(1, ts, bytes((first + ts + k) & 0xFF for k in range(40)), key=True) for ts in (0, 10, 20)]

    return build(standard(cluster(0, *sealed(0)), cluster(1000, *sealed(50)), track=[opus_track(encodings=enc, extra=TS_SCALE_TRACK_EXTRA)]))


@fixture("live-ape-large", "A live recording with an APEv2 tag of 2,000 bytes of value after it: the tag's header reads as an element ID (0x4150) and a size of 1,364 bytes, which the "
         "tag outgrows, so the walk over the unknown element would swallow it. An unknown element counts as part of the recording only if a known one follows it.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")], streams=[OPUS], valid=False)
def _():
    return live_file(), ape_tag(((b"Title", b"A live recording " * 118),))


@fixture("live-ape-binary", "A live recording with an APEv2 tag whose binary cover item has the bytes of a Void element where the walk over the tag lands after the header's "
         "unknown element (1,368 bytes in) and those of a Cluster child that runs past the data after it: neither vouches for the unknown element before it, so the tag is "
         "not swallowed. Only a Top-Level Element that follows an unknown element makes it part of the recording.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")], streams=[OPUS], valid=False)
def _():
    key = b"Cover Art (Front)"
    value = bytearray((i * 167 + (i >> 3) * 31 + 5) & 0xFF for i in range(4000))
    title = (b"Title", b"A live recording")
    first = 1368 - (32 + 8 + len(title[0]) + 1 + len(title[1]) + 8 + len(key) + 1)
    value[first:first + 6] = b"\xec\x84\x00\x00\x00\x00"
    value[first + 6:first + 9] = b"\xa3\x5f\xff"
    return live_file(), ape_tag((title, (key, bytes(value), 2)))


@fixture("live-ape-footer-only", "A live recording with an APEv2 tag that has no header (as APEv1 tags never do): its first item's length bytes (0xA3 0x01 0x00 0x00) read as a "
         "SimpleBlock whose size is a number far past the data. A known element that runs past the data owns the rest of it, unless an APEv2 footer at the end of the data "
         "places its tag at that element's start (the elements do not vouch for the bytes), so the tag is not swallowed.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown")], streams=[OPUS], valid=False)
def _():
    key = b"Cover"
    value = bytes((i * 29 + 7) & 0xFF for i in range(0x1A3))
    item = struct.pack("<II", len(value), 2) + key + b"\0" + value
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(item) + 32, 1, 0) + bytes(8)
    return live_file(), item + footer


@fixture("live-truncated-unknown-element", "A live recording cut short in the middle of its last SimpleBlock, with an element the schema does not list (0x4ABC) in its last Cluster, "
         "whose last 128 bytes are shaped like an ID3v1 tag: a known element that runs past the data owns the rest of the data, so the bytes are part of the block, not a tag.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("matroska.unknown-element", "0x4ABC"),
                   ("matroska.element-truncated", "cut short"), ("tag.near-miss", "ID3v1 signature")], streams=[OPUS], valid=False)
def _():
    last = simple_block(1, 100, opus_packet(9) + bytes(40) + ID3V1 + bytes(60), key=True).render()
    recording = [cluster(0, *blocks(count=3), E(0x4ABC, b"\x11" * 20), *blocks(count=2, start=60, first=3), unknown=True)]
    return live_file_of(recording) + last[:len(last) - 60]


@fixture("live-unknown-tag-signature", "A live recording with an element the schema does not list whose bytes begin like an ID3v1 tag (\"TAG\", ID 0x5441) in the middle of its first "
         "Cluster, a second Cluster after it, and a header-less APEv2 tag whose size is 300 bytes too large, so that it reaches back into the first Cluster: the Cluster after the "
         "unknown element vouches for the recording before it, so both Clusters are read and the damaged tag is not accepted.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("matroska.unknown-element", "0x5441"),
                   ("matroska.element-header-invalid", "No element can be read"), ("tag.near-miss", "APE signature")], streams=[OPUS], valid=False)
def _():
    recording = live_file_of([cluster(0, *blocks(count=3), E(0x5441, bytes(0x700)), *blocks(count=2, start=60, first=3), unknown=True), cluster(1000, *blocks(count=5, first=5), unknown=True)])
    start = recording.index(b"\x54\x41\x47\x00")
    item = struct.pack("<II", 5, 0) + b"Title\0" + b"Hello"
    extra = (len(recording) - start) - 10
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(item) + 32 + extra, 1, 0) + bytes(8)
    return recording + item + footer


def false_footer(target):
    """A live recording whose last block's payload ends in an APEv2 footer (one item's worth of size, flags 0) whose tag size reaches back to the
    start of the last block or of the Segment's first child (Info): the items it would hold are the recording's elements."""
    last = simple_block(1, 100, opus_packet(9) + bytes(40) + bytes(32), key=True)
    clusters = [cluster(0, *blocks(count=3), last, unknown=True)]
    seg = segment(info(), tracks(opus_track()), *clusters, unknown=True)
    data = bytearray(build([ebml_header(), seg]))
    start = last.offset if target == "block" else seg.children[0].offset
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(data) - 32 - start + 32, 1, 0) + bytes(8)
    data[-32:] = footer
    return bytes(data)


@fixture("live-false-ape-footer-block", "A live recording whose last block's payload ends in bytes that read as an APEv2 footer whose tag size reaches back to the start of that "
         "block: the tag would be the block, but the elements from there run through known elements to the end of the data and the \"items\" are no items, so the "
         "recording keeps its bytes and the candidate is a near miss.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("tag.near-miss", "APE signature")], streams=[OPUS], valid=False)
def _():
    return false_footer("block")


@fixture("live-false-ape-footer-info", "As live-false-ape-footer-block with a tag size that reaches back to the Segment's first child (Info): a Top-Level Element starts there, "
         "and the elements from there run through to the end of the data, so the Segment keeps its Info, Tracks and Cluster.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("tag.near-miss", "APE signature")], streams=[OPUS], valid=False)
def _():
    return false_footer("info")


def false_items(variant):
    """A live recording of PCM blocks whose bytes from the first Cluster's Timestamp (written in two octets, E7 82 00 00: the value size 33,511 of a
    first item) read as APEv2 items that parse: the next SimpleBlock's ID, size and track are the item's flags, the block's timecode 0x4141 its key "AA"
    ended by the block's flags 0x00, and the rest its value up to a footer in the last 32 bytes of the file, whose tag size reaches back to the
    Timestamp. The elements run to the end of the data all the same: through a second Cluster of unknown size ("two-clusters"), in one Cluster
    ("one-cluster"), or to an unknown element that runs to the end ("unknown-tail")."""
    pcm = audio_track(1, "A_PCM/INT/LIT", 1, 8000.0, bits=16)

    def layout(n):
        first = [uint(m.TIMESTAMP, 0, 2), simple_block(1, 0x4141, [bytes(200)])] + [simple_block(1, 0x4141 + 20 * i, [bytes(200)], key=True) for i in range(1, 5)]
        last = simple_block(1, 2000, [bytes(n)], key=True)
        if variant == "two-clusters":
            clusters = [master(m.CLUSTER, *first, size="unknown"), cluster(1000, *[simple_block(1, 20 * i, [bytes(200)], key=True) for i in range(5)], last, unknown=True)]
        elif variant == "one-cluster":
            clusters = [master(m.CLUSTER, *first, *[simple_block(1, 0x4141 + 100 + 20 * i, [bytes(200)], key=True) for i in range(5)], last, size="unknown")]
        else:
            clusters = [master(m.CLUSTER, *first, size="unknown"), E(0x4321, bytes(n))]
        seg = segment(info(), tracks(pcm), *clusters, unknown=True)
        return build([ebml_header(), seg]), first[0].offset

    n = 0
    for _ in range(4):
        data, start = layout(n)
        wanted = start + 11 + 33511 + 32
        if len(data) == wanted:
            break
        n += wanted - len(data)
    data = bytearray(data)
    data[-32:] = b"APETAGEX" + struct.pack("<IIII", 2000, len(data) - start, 1, 0) + bytes(8)
    return bytes(data)


FALSE_ITEMS = [("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("matroska.codec-not-analyzed", "PCM"), ("tag.near-miss", "APE signature")]
FALSE_ITEMS_BLOCKS = FALSE_ITEMS + [("matroska.timestamps-backwards", "go back in time")]
PCM_STREAM = [("PCM (integer, little-endian)", 8000, 1)]


@fixture("live-false-ape-items-two-clusters", "A live recording of two Clusters of unknown size whose first Cluster's bytes from its Timestamp read as APEv2 items that parse, up to a footer "
         "in the last block that places a tag there: the elements run through both Clusters to the end of the data, so the recording keeps its bytes and the footer is a near miss.",
         findings=FALSE_ITEMS_BLOCKS, streams=PCM_STREAM, valid=False)
def _():
    return false_items("two-clusters")


@fixture("live-false-ape-items-one-cluster", "As live-false-ape-items-two-clusters with the blocks after the first in one Cluster of unknown size.",
         findings=FALSE_ITEMS_BLOCKS, streams=PCM_STREAM, valid=False)
def _():
    return false_items("one-cluster")


@fixture("live-false-ape-items-unknown-tail", "As live-false-ape-items-two-clusters with an element the schema does not list (0x4321) after the first Cluster, which runs to the end of the "
         "data and holds the footer: the unknown element ends exactly at the end of the data, so it is the recording's.",
         findings=FALSE_ITEMS + [("matroska.unknown-element", "0x4321")], streams=PCM_STREAM, valid=False)
def _():
    return false_items("unknown-tail")


@fixture("live-ape-footer-damaged-key", "The headerless APEv2 tag of live-ape-footer-only with the first byte of its key changed to 0x01: the elements do not vouch for the bytes "
         "(the first item's length reads as an element that runs past the data), so the tag wins, and the APE parser reports its damage instead of the tag being dropped.",
         findings=[("matroska.segment-size-unknown", "unknown"), ("matroska.cluster-size-unknown", "unknown"), ("ape.items-invalid", "invalid key")], streams=[OPUS], valid=False)
def _():
    key = b"\x01over"
    value = bytes((i * 29 + 7) & 0xFF for i in range(0x1A3))
    item = struct.pack("<II", len(value), 2) + key + b"\0" + value
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(item) + 32, 1, 0) + bytes(8)
    return live_file(), item + footer


@fixture("truncated-seek-cues", "A file cut at the end, right after the last Cluster, whose SeekHead names the Cues past the cut: the position lies inside the Segment's declared "
         "size, so the truncation is the finding and the Seek entry is not.", findings=[("matroska.element-truncated", "The Segment states")], streams=[OPUS], valid=False)
def _():
    def make(p):
        head = seek_head((m.INFO, p.get("info", 0)), (m.TRACKS, p.get("tracks", 0)), (m.CLUSTER, p.get("c0", 0)), (m.CUES, p.get("cues", 0)))
        inf, trk = info(), tracks(opus_track())
        c0, c1 = cluster(0, *blocks(count=3)), cluster(60, *blocks(count=3, first=3))
        cu = cues((0, 1, p.get("c0", 0)), (60, 1, p.get("c1", 0)))
        seg = segment(head, inf, trk, c0, c1, cu)
        return [ebml_header(), seg], {"info": inf, "tracks": trk, "c0": c0, "c1": c1, "cues": cu}, seg

    elements = []

    def capture(p):
        result = make(p)
        elements[:] = result[0]
        return result

    data = laid_out(capture)
    cues_element = elements[1].children[-1]
    return data[:cues_element.offset]


@fixture("misplaced-values", "A TimestampScale of 5,000,000 inside Tracks and a WritingApp of \"LAME 3.100\" inside a Tag, both where the schema does not place them, in a file whose Info "
         "states 1,000,000 and the mkvmerge apps: the misplaced values are shown and reported but not used, so the PCM track's four blocks, 40 ms apart, last 0.16 s at the Info's scale and its writer is mkvmerge.",
         findings=[("matroska.misplaced-element", "2 elements are placed where the schema does not allow"), ("matroska.codec-not-analyzed", "PCM")],
         streams=[("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    pcm = audio_track(1, "A_PCM/INT/LIT", 2, PCM_RATE, bits=16)
    stray_scale = uint(ID["TimestampScale"], 5_000_000)
    stray_tag = master(ID["Tag"], master(ID["Targets"], uint(ID["TargetTypeValue"], 50)), text(ID["WritingApp"], "LAME 3.100"))
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), flt(ID["Duration"], 100.0), text(ID["MuxingApp"], "libebml v1.4.5 + libmatroska v1.7.1"),
                 text(ID["WritingApp"], "mkvmerge v82.0 ('I'm The President') 64-bit"))
    return build([ebml_header(), segment(inf, tracks(pcm, stray_scale), cluster(0, *[simple_block(1, i * 40, bytes(range(16)) * 4, key=True) for i in range(4)]),
                                         master(m.TAGS, stray_tag))])


@fixture("string-null", "Strings with a null octet and data after it (RFC 8794 section 13): a CodecID of \"A_OPUS\\0\\0xy\", a DocType of \"matroska\\0xy\" and a Title of \"ab\\0cd\" end at the "
         "first null octet, so the track is routed as Opus, the file is Matroska and none is invalid text.", streams=[OPUS])
def _():
    trk = opus_track()
    codec = next(c for c in trk.children if c.id == ID["CodecID"])
    codec.data = b"A_OPUS\x00\x00xy"
    elements = standard(cluster(0, *blocks(count=4)), track=[trk], info_extra=(E(ID["Title"], b"ab\x00cd"),))
    return build([ebml_header("matroska\x00xy"), elements[1]])


@fixture("header-max-id-length-2", "An EBML Header with an EBMLMaxIDLength of 2: the Segment and the Top-Level Elements have IDs of four octets whatever it says (RFC 9559 section 4.4), "
         "so the file is read in full and only the header's value is reported.", findings=[("matroska.header-limits", "EBMLMaxIDLength of 2")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(max_id=2), standard(cluster(0, *blocks(count=4)))[1]])


@fixture("header-limits-large", "An EBML Header with an EBMLMaxIDLength of 200 and an EBMLMaxSizeLength of 16, which RFC 9559 forbids (section 4.3): the finding states the values as "
         "the header gives them.", findings=[("matroska.header-limits", "EBMLMaxIDLength of 200 and an EBMLMaxSizeLength of 16")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(max_id=200, max_size=16), standard(cluster(0, *blocks(count=4)))[1]])


@fixture("sampling-frequency-invalid", "Four PCM tracks whose SamplingFrequency or OutputSamplingFrequency is not above 0 (RFC 9559 sections 5.1.4.1.29.1 and 5.1.4.1.29.2): NaN, "
         "-48000 and infinity for the first three, and a NaN output sampling frequency beside a valid sampling frequency for the fourth. Each is a finding. The streams of "
         "the first three have no sampling rate, and the fourth uses its sampling frequency.",
         findings=[("matroska.audio-fields", "NaN Hz, which"), ("matroska.audio-fields", "-48000 Hz"), ("matroska.audio-fields", "Infinity Hz"), ("matroska.audio-fields", "output sampling frequency of NaN Hz"),
                   ("matroska.codec-not-analyzed", "Track 1"), ("matroska.codec-not-analyzed", "Track 2"), ("matroska.codec-not-analyzed", "Track 3"), ("matroska.codec-not-analyzed", "Track 4")],
         streams=[("PCM (integer, little-endian)", 0, 2)] * 3 + [("PCM (integer, little-endian)", 48000, 2)], valid=False)
def _():
    rates = [float("nan"), -48000.0, float("inf"), 48000.0]
    entries = [audio_track(i + 1, "A_PCM/INT/LIT", 2, rate, bits=16, audio_extra=(flt(ID["OutputSamplingFrequency"], float("nan")),) if i == 3 else ()) for i, rate in enumerate(rates)]
    items = [simple_block(i + 1, t * 10, bytes(range(16)) * 4, key=True) for i in range(4) for t in range(3)]
    return build(standard(cluster(0, *items), track=entries))


@fixture("aac-legacy-sbr-invalid-output", "A track with the legacy ID A_AAC/MPEG4/LC/SBR whose OutputSamplingFrequency is NaN: it is not used, so no AudioSpecificConfig is worked out "
         "from the ID and the AAC parser gets no configuration.",
         findings=[("matroska.audio-fields", "output sampling frequency of NaN Hz"), ("aac.no-configuration", "without an AudioSpecificConfig")], streams=[("AAC", 22050, 2)], valid=False)
def _():
    _, rate_index, channels, frames = adts_frames("sbr-fixvar-resolutions")
    rate = float(AAC_RATES[rate_index])
    track = audio_track(1, "A_AAC/MPEG4/LC/SBR", channels, rate, audio_extra=(flt(ID["OutputSamplingFrequency"], float("nan")),))
    items = [simple_block(1, round(i * 1024 * 1000 / rate), frame, key=True) for i, frame in enumerate(frames)]
    return build(standard(cluster(0, *items), track=[track]))


@fixture("writer-lavf-under-application", "A Segment whose MuxingApp is ffmpeg's muxer version (\"Lavf60.16.100\") and whose WritingApp names another application, as libavformat writes "
         "the encoding_tool metadata (matroskaenc.c): the muxer is the library under the application, as libebml and libmatroska are under mkvmerge, so the two do not conflict.",
         streams=[OPUS])
def _():
    return with_apps("Lavf60.16.100", "HandBrake 1.7.2 2024011600")


# --- Writing the files ---------------------------------------------------------------------------------


def write(name, builder, description, findings, streams, valid, extension, checks):
    data = builder()
    tail = b""
    if isinstance(data, tuple):
        # Bytes after the Segment's elements, such as a tag: the manifest is the file without them, which the host gives the tag parsers.
        data, tail = data
    path = OUT / f"{name}.{extension}"
    path.write_bytes(data + tail)
    manifest = {"description": description}
    manifest.update(m.manifest(data))
    if name in TAG_WRITERS:
        manifest["writers"] = [{"name": TAG_WRITERS[name], "confidence": "Low"} for _ in manifest["writers"] or [None]]
    manifest["streams"] = [{"codec": c, "sample_rate": r, "channels": n} for c, r, n in streams] if streams is not None else None
    manifest["findings"] = [{"code": code, "contains": contains} for code, contains in findings]
    manifest["crosscheck"] = checks or {"valid": valid}
    (OUT / f"{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, spec in FIXTURES.items():
        write(name, *spec[:1], *spec[1:])
    print(f"Wrote {len(FIXTURES)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
