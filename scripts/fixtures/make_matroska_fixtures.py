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
    inf = master(m.INFO, Crc(value=0x12345678), uint(ID["TimestampScale"], 1_000_000), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "y"))
    return build([ebml_header(), segment(inf, tracks(opus_track()), good, bad)])


@fixture("crc-misplaced", "CRC-32 elements that RFC 8794 section 11.3.1 does not allow: one that is not the first child, and one of two bytes.",
         findings=[("matroska.crc-position", "not the first child"), ("matroska.crc-size", "other than the 4 bytes")], streams=[OPUS])
def _():
    late = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), Crc(), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "y"))
    short = master(m.CLUSTER, E(m.CRC32, b"\x01\x02"), uint(m.TIMESTAMP, 0), *blocks(count=2))
    return build([ebml_header(), segment(late, tracks(opus_track()), short)])


@fixture("elements-unknown-void", "Void elements between and inside others, elements the schema does not list (one with an ID coded longer than needed), "
         "and a deprecated SilentTracks element in a Cluster.",
         findings=[("matroska.unknown-element", "none the Matroska schema lists"), ("matroska.element-id-long", "shortest length"),
                   ("matroska.deprecated-element", "no longer used")], streams=[OPUS], valid=False)
def _():
    inf = master(m.INFO, void(7), uint(ID["TimestampScale"], 1_000_000), raw(0x7FF0, b"\x01\x02\x03"), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "y"))
    cl = cluster(0, void(3), *blocks(count=2), master(0x5854, uint(0x58D7, 1)), raw(0x7FF1, b"zz"), void(2), extra_first=())
    return build([ebml_header(), void(9), segment(void(20), inf, tracks(opus_track(), raw(0x1FFFFFF0, b"\xAA" * 5)), E(0xEC, bytes(3), id_raw=bytes([0x40, 0x6C])), cl)])


@fixture("header-stripping", "A track whose frames have their first byte (the Opus TOC byte) stripped by header stripping (RFC 9559 section 5.1.4.1.31): "
         "the analyzer puts the byte back before it routes the frames.", streams=[OPUS])
def _():
    stripped = [simple_block(1, i * 20, opus_packet(i)[1:], key=True) for i in range(5)]
    enc = content_encodings(content_encoding(order=0, scope=1, compression=3, settings=b"\xF8"))
    return build(standard(cluster(0, *stripped), track=[opus_track(encodings=enc)]))


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


@fixture("encrypted", "A track whose frames are encrypted (ContentEncodingType 1, AES-CTR): the analyzer reports it and does not read the frames.",
         findings=[("matroska.track-encrypted", "is encrypted (AES, AES-CTR)")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, encryption=5, cipher=1, key_id=b"\x01\x02\x03\x04"))
    sealed = [simple_block(1, i * 20, bytes((i * 13 + k) & 0xFF for k in range(41)), key=True) for i in range(4)]
    return build(standard(cluster(0, *sealed), track=[opus_track(encodings=enc)]))


@fixture("compression-bzlib", "A track compressed with bzlib, which the analyzer does not undo.",
         findings=[("matroska.track-encoding-not-read", "compressed with bzlib")], streams=[("Opus", 48000, 1)], valid=False)
def _():
    enc = content_encodings(content_encoding(order=0, scope=1, compression=1))
    return build(standard(cluster(0, *blocks(count=3)), track=[opus_track(encodings=enc)]))


@fixture("encoding-order", "Two ContentEncodings with the same ContentEncodingOrder (RFC 9559 section 5.1.4.1.31.2), and a header-stripped track.",
         findings=[("matroska.encoding-order", "unique")], streams=[OPUS], valid=False)
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
         findings=[("matroska.uid-length", "128 bits")], streams=[OPUS], valid=False)
def _():
    inf = master(m.INFO, raw(ID["SegmentUUID"], bytes(range(16))), uint(ID["TimestampScale"], 1_000_000), flt(ID["Duration"], 60.0), raw(ID["DateUTC"], struct.pack(">q", 788_918_400_000_000_000)),
                 text(ID["Title"], "Segment title"), text(ID["MuxingApp"], "muxer 1.0"), text(ID["WritingApp"], "writer 2.0"), raw(0x3CB923, b"\x01\x02\x03"))
    return build([ebml_header(), segment(inf, tracks(opus_track()), cluster(0, *blocks(count=2)))])


@fixture("timestamp-scale-zero", "A TimestampScale of 0, which RFC 9559 forbids (section 5.1.2.9).", findings=[("matroska.timestamp-scale", "is 0")], streams=[OPUS], valid=False)
def _():
    return build([ebml_header(), segment(master(m.INFO, uint(ID["TimestampScale"], 0), text(ID["MuxingApp"], "x"), text(ID["WritingApp"], "y")), tracks(opus_track()),
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
         findings=[("matroska.track-field", "no TrackNumber"), ("matroska.track-field", "TrackNumber 0"), ("matroska.track-field", "no CodecID"),
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
    inf = master(m.INFO, uint(ID["TimestampScale"], 1_000_000), text(ID["MuxingApp"], "x"), E(ID["WritingApp"], b"w", size=100))
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
                 E(ID["MuxingApp"], b"app\x01"), text(ID["WritingApp"], "y"))
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


# --- Writing the files ---------------------------------------------------------------------------------


def write(name, builder, description, findings, streams, valid, extension, checks):
    data = builder()
    path = OUT / f"{name}.{extension}"
    path.write_bytes(data)
    manifest = {"description": description}
    manifest.update(m.manifest(data))
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
