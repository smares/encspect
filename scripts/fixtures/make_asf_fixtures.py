#!/usr/bin/env python3
"""Writes the hand-built ASF fixtures into tests/fixtures/asf, byte by byte from asf_model.py: every
header object, packets of every layout (single and multiple payloads, error correction data, every width
of the length fields, replicated data with extension bytes, compressed payloads, fragmented media
objects, padding), indexes, metadata, and damaged files. Their audio is PCM, which ffmpeg reads and
decodes, so ffprobe can confirm the packets, streams and timestamps.

Each fixture has a manifest (expected.json) with the figures the model reads from the file (the objects
with their offsets and sizes, the streams, the packets and payloads, the media objects) and the findings
the analyzer must report, exactly. Run with no arguments to rewrite them; CI does and fails if they
differ from the committed files.
"""

import json
import pathlib
import struct
import zlib
import sys

import asf_model as m

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "asf"

FILE_ID = bytes(range(1, 17))
SIZE = 256


def pcm(number=1, rate=8000, channels=1, bits=16):
    align = channels * bits // 8
    return m.stream_properties(number, m.wave_format(1, channels, rate, rate * align, align, bits))


def samples(count, seed=0):
    return bytes((seed + 3 * i) & 0xFF for i in range(count))


def objects_in(streams, count, size, packet_size, layout=None, start_number=1, step_ms=25, extension=None):
    """Single-fragment payloads, as many to a packet as fit."""
    layout = layout or m.Layout()
    packets = []
    batch = []
    send = 0
    for i in range(count):
        batch.append(m.Payload(streams[i % len(streams)], start_number + i // len(streams), samples(size, i), time=i * step_ms, extension=(extension or {}).get(streams[i % len(streams)], b"")))
        try:
            m.packet(batch, packet_size, layout)
        except ValueError:
            batch.pop()
            packets.append(m.packet(batch, packet_size, layout, send_time=send))
            send += step_ms
            batch = [m.Payload(streams[i % len(streams)], start_number + i // len(streams), samples(size, i), time=i * step_ms, extension=(extension or {}).get(streams[i % len(streams)], b""))]
    if batch:
        packets.append(m.packet(batch, packet_size, layout, send_time=send))
    return packets


def simple(packets, children=None, packet_size=SIZE, **kw):
    return m.asf_file([pcm()] if children is None else children, packets, packet_size, file_id=FILE_ID, play=1_000_000, preroll=0, **kw)


def tiny_png():
    """A 1 by 1 pixel PNG, so that decoders open the picture."""
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 0, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"\x00\x80")) + chunk(b"IEND", b"")


def finding(code, contains):
    return {"code": code, "contains": contains}


FIXTURES = []


PROTECTION = {}


def fixture(name, description, builder, findings=(), crosscheck=None, protection=None):
    """findings are those besides the ones every PCM or MPEG audio stream gets (no parser for it here). protection lists, for each stream the
    analyzer reports, the scheme, whether it is encrypted and the DRM systems the file names (none for a file with no encryption object)."""
    FIXTURES.append((name, description, builder, list(findings), crosscheck or {"ffprobe": True}))
    if protection is not None:
        PROTECTION[name] = protection


def unparsed(streams):
    """The info finding for each audio stream whose coding has no parser: PCM and MPEG audio."""
    names = {0x0001: "PCM", 0x0055: "MPEG Layer III"}
    return [finding("asf.codec-not-analyzed", names[s["tag"]]) for s in streams if s.get("tag") in names and not s["encrypted"]]


# ---- valid files ----

fixture("minimal", "A mono PCM stream in single-payload packets without error correction data (the smallest ASF file).",
        lambda: simple([m.packet([m.Payload(1, i + 1, samples(200, i), time=i * 25)], SIZE, m.Layout(multiple=False, ecc=False), send_time=i * 25) for i in range(3)]),
        [])

fixture("multiple-payloads", "Packets of multiple payloads with error correction data, the layout Windows Media writes.",
        lambda: simple(objects_in([1], 9, 100, 512), packet_size=512),
        [])

fixture("two-streams", "Two audio streams with extended stream properties, names, languages, a bitrate record and a mutual exclusion, interleaved in the packets.",
        lambda: m.asf_file(
            [pcm(1, 8000, 1), pcm(2, 16000, 2),
             m.stream_bitrate_properties([(1, 16000), (2, 64000)]),
             m.mutual_exclusion(m.ADVANCED_MUTUAL_EXCLUSION, m.MUTEX_LANGUAGE, [1, 2]),
             m.language_list(["en-us", "de-de"])],
            objects_in([1, 2], 12, 100, 512), 512, file_id=FILE_ID, play=2_000_000,
            extension=m.extended_stream_properties(1, 0, 2000, 128000, 3000, 0, names=[(0, "English")], language_index=0)
            + m.extended_stream_properties(2, 0, 2000, 256000, 3000, 0, names=[(1, "Deutsch"), (0, "German")], language_index=1)),
        [])


def fragmented():
    big = samples(700, 7)
    other = samples(300, 9)
    layout = m.Layout()
    packets = [
        m.packet([m.Payload(1, 1, big[:300], 0, 700, 0), m.Payload(2, 1, other[:150], 0, 300, 0)], 512, layout),
        m.packet([m.Payload(1, 1, big[300:600], 300, 700, 0), m.Payload(2, 1, other[150:], 150, 300, 0)], 512, layout),
        m.packet([m.Payload(1, 1, big[600:], 600, 700, 0), m.Payload(1, 2, samples(20), 0, 20, 90)], 512, layout),
    ]
    return m.asf_file([pcm(1), pcm(2)], packets, 512, file_id=FILE_ID, play=1_000_000)


fixture("fragmented-objects", "Media objects of 700 and 300 bytes split over several packets, interleaved with one another and with a whole object.", fragmented,
        [])


def compressed():
    subs = [samples(40, i) for i in range(5)]
    multiple = m.packet([m.Payload(1, 10, subpayloads=subs, time=500, delta=20, compressed=True, key=False),
                         m.Payload(1, 20, samples(80), 0, 80, 700)], 512, m.Layout())
    single = m.packet([m.Payload(1, 30, subpayloads=subs[:3], time=900, delta=10, compressed=True, key=False)], 512, m.Layout(multiple=False))
    return simple([multiple, single], packet_size=512)


fixture("compressed-payloads", "A compressed payload of five sub-payloads in a packet of multiple payloads, and one of three alone in a packet.", compressed,
        [])


def widths(layout, packet_size=300, count=7):
    def build():
        return simple(objects_in([1], count, 60, packet_size, layout), packet_size=packet_size)
    return build


fixture("widths-large", "Every length field at its widest: the packet length, sequence and padding length, the replicated data length, offset, media object number and payload length as double words.",
        widths(m.Layout(packet_length=4, sequence=4, padding=4, replicated=4, offset=4, number=4, payload_length=4)))
fixture("widths-small", "Every length field at a narrow width: no sequence, a packet length of a byte is too small for 300, so two bytes; padding, replicated length, offset and number as bytes and words.",
        widths(m.Layout(packet_length=2, sequence=1, padding=2, replicated=1, offset=2, number=2, payload_length=1), 200))
fixture("widths-none", "The fields that may be left out are: no padding length, no replicated data, no offset and no media object number; every payload is a whole object.",
        lambda: simple([m.packet([m.Payload(1, 0, samples(50, i), replicated=b"") for _ in range(2)], 256, m.Layout(padding=0, replicated=0, offset=0, number=0)) for i in range(3)]),
        [], {"ffprobe": False, "reason": "ffmpeg rejects a payload with no replicated data and no offset field (\"packet fragment position invalid\")."})
fixture("single-payload-widths", "Single-payload packets with a packet length, a sequence and a padding length of a word and a replicated data of 14 bytes (the object size, the time and six bytes of extension data).",
        lambda: simple([m.packet([m.Payload(1, i + 1, samples(100, i), time=i * 10, extension=b"\x01\x02\x03\x04\x05\x06")], 256, m.Layout(multiple=False, packet_length=2, sequence=2, padding=2),
                                 send_time=i * 10, sequence=i) for i in range(3)]))
fixture("padding-zero", "Packets of multiple payloads that state a padding length of 0 although they are padded, which the specification allows (section 5.2.2).",
        lambda: simple([m.packet([m.Payload(1, i + 1, samples(60, i), time=i * 10)], 256, m.Layout(), padding_field=0) for i in range(3)]))
fixture("ecc-bytes", "Error correction data of 2 bytes whose type and cycle are set, and an error correction object in the header.",
        lambda: m.asf_file([pcm(), m.error_correction(m.NO_ERROR_CORRECTION)], [m.packet([m.Payload(1, i + 1, samples(60, i))], 256, m.Layout(), ecc_data=bytes([0x11, i])) for i in range(3)],
                           256, file_id=FILE_ID, play=500_000))
fixture("broadcast", "A file in the process of being created: the broadcast flag set, the Data Object of size 0, and the counts and durations invalid.",
        lambda: m.asf_file([pcm()], objects_in([1], 4, 60, 256), 256, file_id=FILE_ID, flags=1, data_size=0, play=0, total_packets=0),
        [finding("asf.broadcast", "broadcast flag")])


def metadata_file():
    tag = b"ID3\x03\x00\x00\x00\x00\x00\x12" + b"TIT2" + struct.pack(">I", 8) + b"\x00\x00" + b"\x00Title\x00\x00"
    image = tiny_png()
    picture = struct.pack("<BI", 3, len(image)) + m.utf16("image/png") + m.utf16("Cover") + image
    return m.asf_file(
        [pcm(),
         m.content_description("Title \u00e9\u4e2d", "Artist", "(c) 2026", "Comment", "Rating"),
         m.extended_content_description([
             ("WM/AlbumTitle", 0, "Album"), ("WM/TrackNumber", 3, 7), ("WM/EncodingTime", 4, 132_000_000_000_000_000), ("WM/Year", 0, "2026"),
             ("IsVBR", 2, 1), ("Duration", 4, 20_000_000), ("WM/Track", 5, 3), ("WM/Picture", 1, picture), ("ID3", 1, tag), ("WM/Blob", 1, b"\x00\x01\x02"),
             ("WMFSDKVersion", 0, "12.0.19041.1"), ("WM/EncodingSettings", 0, "Lavf60.16.100"), ("WM/ToolName", 0, "Tool")]),
         m.metadata([(0, "WM/Genre", 0, "Genre"), (1, "AspectRatioX", 3, 16), (1, "IsDefault", 2, 1), (0, "Big", 4, 5), (0, "Flag", 5, 2)]),
         m.metadata_library([(0, "WM/Lyrics", 0, "La la", 0), (1, "WM/Ref", 6, m.AUDIO_MEDIA, 1), (0, "WM/Lyrics", 0, "Lalala", 1), (0, "Raw", 1, b"\xAA\xBB", 0)]),
         m.content_branding(2, b"\xff\xd8\xff\xe0JPEG", b"http://example.invalid/banner\x00", b"http://example.invalid/c\x00"),
         m.language_list(["en-us", "fr-fr"])],
        objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000)


fixture("metadata", "Every metadata object: content description, extended content description with each value type, an attached picture and an ID3v2 tag in byte arrays, a metadata object, a metadata library with languages and a GUID, the content branding object and a language list.",
        metadata_file,
        [])


def header_file():
    return m.asf_file(
        [pcm(1), pcm(2),
         m.codec_list([(2, "Windows Media Audio V8", "128 kbps, 44 kHz, stereo", b"\x61\x01"), (1, "Video", "", b""), (0xFFFF, "Other", "x", b"")]),
         m.script_command(["URL", "FILENAME"], [(1000, 0, "http://example.invalid/"), (2000, 1, "next.asf")]),
         m.marker("Markers", [(0, 0, 0, "Start"), (400, 5_000_000, 500, "Chorus")]),
         m.mutual_exclusion(m.BITRATE_MUTUAL_EXCLUSION, m.MUTEX_BITRATE, [1, 2]),
         m.mutual_exclusion(m.ADVANCED_MUTUAL_EXCLUSION, m.MUTEX_LANGUAGE, [1, 2]),
         m.group_mutual_exclusion(m.MUTEX_LANGUAGE, [[1], [2]]),
         m.stream_prioritization([(1, 1), (2, 0)]),
         m.bandwidth_sharing(m.SHARING_PARTIAL, 128000, 8000, [1, 2]),
         m.stream_bitrate_properties([(1, 16000), (2, 16000)]),
         m.padding(37),
         m.content_encryption(), m.obj(m.EXTENDED_CONTENT_ENCRYPTION, struct.pack("<I", 5) + b"12345"),
         m.obj(m.DIGITAL_SIGNATURE, struct.pack("<II", 2, 4) + b"sign"),
         m.obj("12345678-1234-1234-1234-123456789ABC", b"custom object data")],
        objects_in([1, 2], 4, 60, 256), 256, file_id=FILE_ID, play=500_000,
        extension=m.obj(m.COMPATIBILITY, bytes([2, 1]))
        + m.obj(m.ADVANCED_CONTENT_ENCRYPTION, struct.pack("<H", 1) + m.guid("7A079BB6-DAA4-4E12-A5CA-91D38DC11A8D") + struct.pack("<IH", 1, 1) + struct.pack("<HH", 1, 2) + struct.pack("<H", 1)
                 + struct.pack("<I", 3) + b"abc")
        + m.index_parameters(1000, [(1, 3)]) + m.padding(5))


fixture("header-objects", "Every other header object: a codec list, a script command, a marker, mutual exclusions, a stream prioritization, a bandwidth sharing, bitrate records, padding, two encryption objects, a signature, an object of no known GUID, and in the header extension a compatibility object, an advanced content encryption object and index parameters.",
        header_file,
        [finding("asf.unknown-object", "12345678-1234-1234-1234-123456789ABC"),
         finding("asf.content-encrypted", "Digital Rights Manager"), finding("asf.content-encrypted", "Windows Media Rights Manager"),
         finding("asf.content-encrypted", "network devices"), finding("asf.index-missing", "no Index Object")],
        protection=[{"scheme": "Microsoft Digital Rights Manager version 1", "is_encrypted": True, "systems": ["Windows Media DRM for network devices"]},
                    {"scheme": "Microsoft Digital Rights Manager version 1", "is_encrypted": True, "systems": []}])


def extended_file():
    hidden = m.stream_properties(3, m.wave_format(1, 1, 8000, 16000, 2, 16))
    return m.asf_file(
        [pcm(1), m.language_list(["en-us"])],
        objects_in([1, 3], 6, 80, 512, m.Layout(), extension={1: b"\x01\x00" + bytes(14) + b"\x00\x00"}), 512, file_id=FILE_ID, play=500_000,
        extension=m.extended_stream_properties(
            1, 0, 500, 128000, 3000, 0, 128000, 3000, 0, 4096, 0x5, 0, 0, [(0, "Main")],
            [(m.EXTENSION_SAMPLE_DURATION, 2, b""), (m.EXTENSION_TIMECODE, 14, b"info"), (m.guid_text(b"\x01" * 16), 0xFFFF, b"")])
        + m.extended_stream_properties(3, 0, 500, 64000, 1000, 0, stream=hidden))


fixture("extended-stream-properties", "Extended stream properties with a name, three payload extension systems (two fixed, one variable size) and flags, and a stream whose Stream Properties Object is inside its extended one.",
        extended_file,
        [])


def index_file():
    packets = objects_in([1], 8, 150, 256)
    return m.asf_file(
        [pcm()] + [m.index_parameters(1000, [(1, 1)])], packets, 256, file_id=FILE_ID, play=2_000_000,
        trailing=m.index_object(1000, [(1, 1)], [([0], [(0,), (256,), (512,), (0xFFFFFFFF,)]), ([512], [(0,), (256,)])])
        + m.simple_index([(0, 1), (1, 1), (2, 1)]))


fixture("indexes", "An Index Parameters Object, an Index Object of two blocks with an invalid offset and a Simple Index Object of three entries.", index_file,
        [])


def all_indexes_file():
    packets = objects_in([1], 8, 150, 256)
    parameters = m.index_parameters(1000, [(1, 1), (1, 3)])
    return m.asf_file(
        [pcm()],
        packets, 256, file_id=FILE_ID, play=2_000_000, extension=parameters + m.index_parameters(2, [(1, 2), (1, 0xFF)], m.MEDIA_OBJECT_INDEX_PARAMETERS)
        + m.index_parameters(0, [(1, 2)], m.TIMECODE_INDEX_PARAMETERS),
        trailing=m.index_object(1000, [(1, 1), (1, 3)], [([0, 0], [(0, 0), (256, 256)])])
        + m.index_object(2, [(1, 2), (1, 0xFF)], [([0, 0], [(0, 0), (512, 1)])], m.MEDIA_OBJECT_INDEX)
        + m.obj(m.TIMECODE_INDEX, struct.pack("<IHI", 1, 1, 1) + struct.pack("<HH", 1, 2) + struct.pack("<IHQ", 2, 0, 0) + struct.pack("<II", 0x01020304, 0) + struct.pack("<II", 0x01020305, 256))
        + m.simple_index([(0, 2), (2, 1)]))


fixture("index-kinds", "An Index Object with two index types for one stream, a Media Object Index Object, a Timecode Index Object and a Simple Index Object, with their parameter objects.",
        all_indexes_file, [])


def spread_file():
    ec = m.spread_audio(2, 256, 64, b"\0")
    return m.asf_file(
        [m.stream_properties(1, m.wave_format(0x0161, 2, 44100, 16000, 743, 16, bytes(10)), ec_type=m.AUDIO_SPREAD, ec_data=ec)],
        objects_in([1], 3, 100, 512), 512, file_id=FILE_ID, play=500_000)


fixture("spread-audio", "Windows Media Audio whose error correction spreads the audio over two packets, which scrambles its media objects; the analyzer does not descramble them.",
        spread_file, [finding("asf.audio-spread", "2 packets")], {"ffprobe": False, "reason": "ffmpeg descrambles the media objects and decodes them as Windows Media Audio, which these are not."})


def mismatched_indexes_file():
    parameters = (m.index_parameters(2, [(1, 2)], m.MEDIA_OBJECT_INDEX_PARAMETERS) + m.index_parameters(0, [(1, 1)], m.TIMECODE_INDEX_PARAMETERS))
    return m.asf_file(
        [pcm()], objects_in([1], 8, 150, 256), 256, file_id=FILE_ID, play=2_000_000, extension=parameters,
        trailing=m.index_object(2, [(1, 3)], [([0], [(0,), (512,)])], m.MEDIA_OBJECT_INDEX)
        + m.obj(m.TIMECODE_INDEX, struct.pack("<IHI", 1, 1, 1) + struct.pack("<HH", 1, 2) + struct.pack("<IHQ", 2, 0, 0) + struct.pack("<II", 0x01020304, 0) + struct.pack("<II", 0x01020305, 256))
        + m.simple_index([(0, 2), (2, 1)]))


fixture("index-specifiers-differ", "A Media Object Index Object whose index specifier (stream 1, nearest past cleanpoint) differs from that of its Media Object Index Parameters Object (nearest past media object), and a Timecode Index Object that differs from its Timecode Index Parameters Object, which lists index type 1, a value section 4.11 says is not valid.",
        mismatched_indexes_file,
        [finding("asf.index-specifiers", "Media Object Index Object's index specifiers differ"), finding("asf.index-specifiers", "Timecode Index Object's index specifiers differ"),
         finding("asf.index-specifiers", "index type 1")])


def missing_parameters_file():
    return m.asf_file(
        [pcm()], objects_in([1], 8, 150, 256), 256, file_id=FILE_ID, play=2_000_000,
        trailing=m.index_object(2, [(1, 2)], [([0], [(0,), (512,)])], m.MEDIA_OBJECT_INDEX)
        + m.obj(m.TIMECODE_INDEX, struct.pack("<IHI", 1, 1, 1) + struct.pack("<HH", 1, 2) + struct.pack("<IHQ", 2, 0, 0) + struct.pack("<II", 0x01020304, 0) + struct.pack("<II", 0x01020305, 256))
        + m.simple_index([(0, 2), (2, 1)]))


fixture("index-parameters-missing-kinds", "A Media Object Index Object and a Timecode Index Object with no parameter object in the header, which sections 4.10 and 4.11 require.",
        missing_parameters_file,
        [finding("asf.index-parameters-missing", "Media Object Index Object but no Media Object Index Parameters Object"), finding("asf.index-parameters-missing", "Timecode Index Object but no Timecode Index Parameters Object")])


def content_description_file():
    # A title of 5 bytes, which is no whole UTF-16 character, and an author of 200 bytes where the object holds fewer; a descriptor count of 3 for 1.
    description = m.obj(m.CONTENT_DESCRIPTION, struct.pack("<5H", 5, 200, 0, 0, 0) + "Tit".encode("utf-16-le")[:5])
    one = m.extended_content_description([("WM/AlbumTitle", 0, "A")])
    counted = one[:24] + struct.pack("<H", 3) + one[26:]
    return m.asf_file([pcm(), description, counted], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000)


fixture("content-description-findings", "A Content Description Object whose title length is odd and whose author length is more than the object holds, and an Extended Content Description Object that counts three descriptors for one.",
        content_description_file,
        [finding("asf.string-odd-length", "title"), finding("asf.object-truncated", "author"), finding("asf.descriptor-count", "counts 3 descriptors, but its data holds 1")])


def short_length_field_file():
    # Packets of 256 bytes whose length field says 200, with a padding length of 0: the 56 bytes between are padding, as FFmpeg reads them.
    layout = m.Layout(packet_length=2, multiple=False)
    packets = []
    for i in range(3):
        data_length = 200 - (1 + 2 + 2 + 2 + 6) - (1 + 1 + 4 + 1 + 8)
        packets.append(m.packet([m.Payload(1, i + 1, b"\x10" * data_length, 0, data_length, i * 25)], 256, layout, declared_length=200, padding_field=0))
    return m.asf_file([pcm()], packets, 256, file_id=FILE_ID, play=1_000_000)


fixture("length-field-short", "Packets of 256 bytes, the size every packet has, whose packet length field says 200 and whose padding length is 0: the bytes between the payload and the next packet are padding, in the packet and in the statistics.",
        short_length_field_file,
        [finding("asf.packet-length", "3 packets")])


fixture("length-field-short-cut", "The file length-field-short cut 5 bytes into its third packet, inside the packet header: the missing packet has no payload and no padding to count, only the two whole packets' 56 bytes each.",
        lambda: short_length_field_file()[:-256 + 5],
        [finding("asf.packet-truncated", "ends before its header"), finding("asf.packet-length", "2 packets"), finding("asf.object-truncated", "Data Object"), finding("asf.file-size", "but the file holds 843")],
        {"ffprobe": False, "reason": "the file is cut inside a packet on purpose."})


fixture("length-field-short-cut-fields", "The same file cut 8 bytes into its third packet, inside the packet header's fields: the Packet Length field that fits is read, the fields that do not are left out.",
        lambda: short_length_field_file()[:-256 + 8],
        [finding("asf.packet-truncated", "ends before its header"), finding("asf.packet-length", "2 packets"), finding("asf.object-truncated", "Data Object"), finding("asf.file-size", "but the file holds 846")],
        {"ffprobe": False, "reason": "the file is cut inside a packet on purpose."})

def zero_length_field_file():
    layout = m.Layout(packet_length=2, multiple=False)
    packets = [m.packet([m.Payload(1, i + 1, b"\x10" * 100, 0, 100, i * 25)], 256, layout, declared_length=0) for i in range(3)]
    return m.asf_file([pcm()], packets, 256, file_id=FILE_ID, play=1_000_000)


fixture("length-field-zero", "Packets of 256 bytes, the size every packet has, that carry a packet length field of 0, which states a packet of no bytes (FFmpeg rejects it): the packets are read at the file's size.",
        zero_length_field_file,
        [finding("asf.packet-length", "3 packets")], {"ffprobe": False, "reason": "ffmpeg rejects a packet length of 0 (asfdec_f.c: invalid packet_length)."})


def unaligned_offsets_file(variable):
    packets = objects_in([1], 8, 150, 256)
    override = (lambda size, n: m.file_properties(FILE_ID, size, 0, n, 2_000_000, 0, 0, 2, 128, 256, 0)) if variable else None
    return m.asf_file(
        [pcm()] + [m.index_parameters(1000, [(1, 1)])], packets, 256, file_id=FILE_ID, play=2_000_000, properties_override=override,
        trailing=m.index_object(1000, [(1, 1)], [([0], [(0,), (100,), (512,)])]) + m.simple_index([(0, 1), (1, 1), (2, 1)]))


fixture("index-offset-unaligned", "An Index Object with an entry whose offset (100) is not the start of a packet of the 256 bytes every packet has.",
        lambda: unaligned_offsets_file(False),
        [finding("asf.index-offset", "do not point to the start of a packet")])
fixture("index-offset-variable-packets", "The same index in a file whose minimum packet size is 128 and maximum 256: the size of a packet is not known from the header, so the offset is not checked against packet starts.",
        lambda: unaligned_offsets_file(True),
        [finding("asf.packet-size", "minimum (128)")])


def stamp_file(attributes):
    codec = struct.pack("<IHI", 17 * 2048, 0x000F, 0)
    stream = m.stream_properties(1, m.wave_format(0x0161, 2, 44100, 16000, 100, 16, codec))
    packets = [m.packet([m.Payload(1, i + 1, b"\x11" * 100, 0, 100, i * 25)], 512, m.Layout(), send_time=i * 25) for i in range(2)]
    return m.asf_file([stream, m.extended_content_description(attributes)], packets, 512, file_id=FILE_ID, play=1_000_000)


WMA_NOISE = [finding("wma.frame-invalid", "coefficients run past the end of the block")]
fixture("writer-stamp-last", "A Windows Media Audio stream whose Extended Content Description Object lists WMFSDKVersion and then, as its last attribute, WM/EncodingSettings with an ffmpeg muxer version, as ffmpeg's ASF muxer leaves a remux: the stamp names the writer.",
        lambda: stamp_file([("WMFSDKVersion", 0, "12.0.26100.1"), ("WM/EncodingSettings", 0, "Lavf60.16.100")]),
        WMA_NOISE + [finding("wma.writer-claim-differs", "WMFSDKVersion")], {"ffprobe": False, "reason": "the media objects are not Windows Media Audio superframes."})
fixture("writer-stamp-not-last", "The same attributes with another attribute (WM/ToolName) after the ffmpeg muxer version, which other software added: the stamp keeps its place in file order, behind WMFSDKVersion.",
        lambda: stamp_file([("WMFSDKVersion", 0, "12.0.26100.1"), ("WM/EncodingSettings", 0, "Lavf60.16.100"), ("WM/ToolName", 0, "Tagger 1.0")]),
        WMA_NOISE + [finding("wma.writer-claim-differs", "Lavf60.16.100")] , {"ffprobe": False, "reason": "the media objects are not Windows Media Audio superframes."})

def fragments_in_two_streams_file():
    codec = struct.pack("<IHI", 17 * 2048, 0x000F, 0)

    def stream(number):
        return m.stream_properties(number, m.wave_format(0x0161, 2, 44100, 16000, 100, 16, codec))

    layout = m.Layout()
    packets = [
        m.packet([m.Payload(1, 1, b"\x11" * 100, 0, 100, 0), m.Payload(2, 1, b"\x22" * 100, 0, 100, 0)], 512, layout),
        # Stream 1: a fragment at offset 50 of object 7, which never began. Stream 2: two fragments of object 9, which never began.
        m.packet([m.Payload(1, 7, b"\x11" * 50, 50, 100, 10), m.Payload(2, 9, b"\x22" * 40, 30, 100, 10)], 512, layout),
        m.packet([m.Payload(2, 9, b"\x22" * 40, 60, 100, 20), m.Payload(1, 2, b"\x11" * 100, 0, 100, 30)], 512, layout),
    ]
    return m.asf_file([stream(1), stream(2)], packets, 512, file_id=FILE_ID, play=10_000_000)


fixture("fragments-in-two-streams", "Two Windows Media Audio streams, each with fragments of a media object that never began (one in stream 1, two in stream 2): the findings count them per stream and name their own stream.",
        fragments_in_two_streams_file,
        [finding("asf.fragment-order", "A fragment of a media object of stream 1 does not"), finding("asf.fragment-order", "2 fragments of media objects of stream 2"),
         finding("wma.frame-invalid", "coefficients run past the end of the block"), finding("wma.bit-offset", "bit offset past its end")],
        {"ffprobe": False, "reason": "the media objects are not Windows Media Audio superframes."})


DERIVED_FROM = "external-ffmpeg-wmav2-mono-44k-48k.wma"


def tool_name_file():
    """The committed ffmpeg wmav2 file DERIVED_FROM with a WM/ToolName attribute added: its stream and codec list, packets and File Properties
    figures (file ID, creation time, play duration, send duration, preroll, flags, packet sizes, maximum bit rate) are the source's, and the
    Header Extension Object is as empty as the source's."""
    source = (OUT / DERIVED_FROM).read_bytes()
    header_end = struct.unpack_from("<Q", source, 16)[0]
    objects = m.read_objects(source, 30, header_end)
    properties = next(source[at:at + size] for name, at, size in objects if name == m.FILE_PROPERTIES)
    file_id, _, creation, _, play, send, preroll, flags, minimum, maximum, bitrate = struct.unpack_from("<16sQQQQQQIIII", properties, 24)
    assert minimum == maximum
    extension = next(source[at:at + size] for name, at, size in objects if name == m.HEADER_EXTENSION)
    assert len(extension) == 46, "the source's Header Extension Object has children"
    children = [source[at:at + size] for name, at, size in objects if name in (m.STREAM_PROPERTIES, m.CODEC_LIST)]
    body = source[header_end + 50:header_end + struct.unpack_from("<Q", source, header_end + 16)[0]]
    packets = [body[k:k + maximum] for k in range(0, len(body), maximum)]
    return m.asf_file(children + [m.extended_content_description([("WM/ToolName", 0, "Lavc60.31.102 wmav2")])], packets, maximum,
                      file_id=file_id, play=play, send=send, preroll=preroll, flags=flags, creation=creation, max_bitrate=bitrate)


fixture("derived-ffmpeg-wmav2-tool-name-lavc", "The ffmpeg wmav2 file external-ffmpeg-wmav2-mono-44k-48k.wma, whose superframes are padded with 'N' bytes, with a WM/ToolName attribute added that holds ffmpeg's libavcodec encoder ID: "
        "the ID counts as encoder evidence only where the stream shows ffmpeg, as it does here, so it is listed under the encoder the stream names and the attribute names no writer.",
        tool_name_file, [])


def two_codecs():
    return m.asf_file(
        [m.stream_properties(1, m.wave_format(0x0055, 2, 44100, 16000, 1, 0)),
         m.stream_properties(2, b"\x01\x02\x03", stream_type=m.VIDEO_MEDIA),
         m.stream_properties(3, struct.pack("<H", 1), stream_type=m.COMMAND_MEDIA)],
        objects_in([1], 3, 100, 512), 512, file_id=FILE_ID, play=500_000)


fixture("other-streams", "An MPEG audio stream, a video stream and a command stream, none of which is analyzed.",
        two_codecs,
        [finding("asf.stream-not-analyzed", "video"), finding("asf.stream-not-analyzed", "command"),
         finding("asf.stream-without-payloads", "Stream 2"), finding("asf.stream-without-payloads", "Stream 3")],
        {"ffprobe": False, "reason": "the MPEG audio payloads are not MPEG audio frames."})

fixture("extensible-format", "A WAVEFORMATEXTENSIBLE for 6-channel PCM: valid bits, channel mask and sub-format.",
        lambda: m.asf_file([m.stream_properties(1, m.wave_format_extensible(1, 6, 48000, 576000, 12, 16, 16, 0x3F))], objects_in([1], 3, 120, 512), 512, file_id=FILE_ID, play=500_000),
        [finding("asf.codec-not-analyzed", "PCM")])

# ---- protected files ----

DRM_SYSTEM = "7A079BB6-DAA4-4E12-A5CA-91D38DC11A8D"
UNKNOWN_SYSTEM = "01234567-89AB-CDEF-0123-456789ABCDEF"
NETWORK_DEVICES = "Windows Media DRM for network devices"
WMA2 = m.wave_format(0x0161, 2, 44100, 16000, 743, 16, bytes(10))
ENCRYPTED_FFPROBE = {"ffprobe": True}


def wma(number=1, encrypted=False):
    return m.stream_properties(number, WMA2, encrypted=encrypted)


def advanced(records):
    """An Advanced Content Encryption Object (4.13): records of (system ID, version, [(id type, id bytes)], data)."""
    body = struct.pack("<H", len(records))
    for system, version, objects, data in records:
        body += m.guid(system) + struct.pack("<IH", version, len(objects))
        for kind, identifier in objects:
            body += struct.pack("<HH", kind, len(identifier)) + identifier
        body += struct.pack("<I", len(data)) + data
    return m.obj(m.ADVANCED_CONTENT_ENCRYPTION, body)


def stream_number(number):
    return struct.pack("<H", number)


def protected(children, extension=b"", count=6):
    return lambda: m.asf_file(children, objects_in([1], count, 100, 512), 512, file_id=FILE_ID, play=1_000_000, extension=extension)


ENCRYPTED = finding("asf.codec-not-analyzed", "is encrypted")
DRM1_FINDING = finding("asf.content-encrypted", "Digital Rights Manager")
EXTENDED_FINDING = finding("asf.content-encrypted", "Windows Media Rights Manager")
ADVANCED_FINDING = finding("asf.content-encrypted", "network devices")


def scheme(name, systems=()):
    return [{"scheme": name, "is_encrypted": True, "systems": list(systems)}]


fixture("protected-drm1", "Windows Media Audio whose Stream Properties Object sets the Encrypted Content Flag, with a Content Encryption Object (Microsoft Digital Rights Manager version 1): "
        "a secret, the protection type DRM, a key ID and a license URL. The payloads are random bytes, so the stream is described by its header alone.",
        protected([wma(1, encrypted=True), m.content_encryption()]), [DRM1_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE, scheme("Microsoft Digital Rights Manager version 1"))
def wma_codec(tag, extra, channels=2, rate=44100, align=743):
    return m.stream_properties(1, m.wave_format(tag, channels, rate, rate // 8, align, 16, extra), encrypted=True)


# Professional and Lossless: the sample size, the channel mask, eight unknown bytes, the decode flags and two unknown bytes (18 bytes, as FFmpeg's wmaprodec.c and wmalosslessdec.c read them).
PRO_DATA = struct.pack("<HIQHH", 16, 3, 0, 0x00E0, 0)


def voice_data():
    import voice_model
    return voice_model.Config(8000, 300).codec_data()


for tag, name, extra, codec in [(0x0162, "pro", PRO_DATA, "Windows Media Audio Professional"), (0x0163, "lossless", PRO_DATA, "Windows Media Audio Lossless")]:
    fixture(f"protected-wma-{name}", f"{codec}, in a stream whose Encrypted Content Flag is set and a Content Encryption Object: the stream is described by its WAVEFORMATEX and its {len(extra)} bytes of codec-specific data, "
            "and the encrypted media objects are not read.", protected([wma_codec(tag, extra), m.content_encryption()]), [DRM1_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE,
            scheme("Microsoft Digital Rights Manager version 1"))
fixture("protected-wma-voice", "Windows Media Audio Voice, mono, in a stream whose Encrypted Content Flag is set and a Content Encryption Object: the stream is described by its WAVEFORMATEX and the 46 bytes of "
        "codec-specific data, and the encrypted media objects are not read.",
        protected([wma_codec(0x000A, voice_data(), channels=1, rate=8000, align=300), m.content_encryption()]), [DRM1_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE,
        scheme("Microsoft Digital Rights Manager version 1"))
fixture("protected-extended", "Windows Media Audio with an Extended Content Encryption Object (Windows Media Rights Manager 7) and no Encrypted Content Flag: the object covers the whole file, "
        "so the stream is protected.",
        protected([wma(1), m.obj(m.EXTENDED_CONTENT_ENCRYPTION, struct.pack("<I", 5) + b"12345")]), [EXTENDED_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE, scheme("Windows Media Rights Manager 7"))
fixture("protected-flag-only", "Windows Media Audio whose Stream Properties Object sets the Encrypted Content Flag and a file with no encryption object: the stream is encrypted, with no scheme named.",
        protected([wma(1, encrypted=True)]), [ENCRYPTED], ENCRYPTED_FFPROBE, scheme("Protection scheme not stated (Encrypted Content Flag)"))
def advanced_partial():
    records = [(DRM_SYSTEM, 1, [(1, stream_number(2))], b"abc"), (UNKNOWN_SYSTEM, 3, [(1, stream_number(7))], b"")]
    return m.asf_file([pcm(1), wma(2)], objects_in([1, 2], 6, 100, 512), 512, file_id=FILE_ID, play=1_000_000, extension=advanced(records))


fixture("protected-advanced", "An Advanced Content Encryption Object in the Header Extension Object with a record of the one system ID the specification lists (Windows Media DRM for network devices) "
        "for stream 2, the Windows Media Audio stream, and one of a system ID it does not list for stream 7, which the file does not have. A PCM stream 1 stays clear.",
        advanced_partial, [ADVANCED_FINDING, ENCRYPTED, finding("asf.encrypted-object-unknown-stream", "stream 7")], ENCRYPTED_FFPROBE,
        [{"scheme": "Windows Media DRM for network devices", "is_encrypted": True, "systems": [NETWORK_DEVICES]}])
fixture("protected-advanced-whole-file", "An Advanced Content Encryption Object whose record names stream 0, the whole file, with a system ID the specification does not list: every stream is protected, "
        "and the system is named by its ID.",
        protected([wma(1)], extension=advanced([(UNKNOWN_SYSTEM, 2, [(1, stream_number(0))], b"data")])), [ADVANCED_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE,
        [{"scheme": "Windows Media DRM for network devices", "is_encrypted": True, "systems": [UNKNOWN_SYSTEM.lower()]}])
fixture("protected-all-objects", "Windows Media Audio covered by all three encryption objects and a Digital Signature Object: the first object in file order names the scheme, and the systems are those of the Advanced Content Encryption Object.",
        protected([wma(1, encrypted=True), m.content_encryption(), m.obj(m.EXTENDED_CONTENT_ENCRYPTION, struct.pack("<I", 2) + b"ab"), m.obj(m.DIGITAL_SIGNATURE, struct.pack("<II", 2, 4) + b"sign")],
                  extension=advanced([(DRM_SYSTEM, 1, [(1, stream_number(1))], b"")])), [DRM1_FINDING, EXTENDED_FINDING, ADVANCED_FINDING, ENCRYPTED], ENCRYPTED_FFPROBE,
        [{"scheme": "Microsoft Digital Rights Manager version 1", "is_encrypted": True, "systems": [NETWORK_DEVICES]}])
fixture("signature-only", "A Digital Signature Object and no encryption object: the signature protects nothing, so the file is not protected.",
        protected([pcm(1), m.obj(m.DIGITAL_SIGNATURE, struct.pack("<II", 2, 4) + b"sign")]), [], None, [])
fixture("protection-wrong-fields", "A Content Encryption Object whose protection type is XYZ and a Digital Signature Object of signature type 7, where sections 3.14 and 3.16 give DRM and 2.",
        protected([wma(1), m.content_encryption(protection=b"XYZ\0"), m.obj(m.DIGITAL_SIGNATURE, struct.pack("<II", 7, 4) + b"sign")]),
        [DRM1_FINDING, ENCRYPTED, finding("asf.protection-type", "XYZ"), finding("asf.signature-type", "7")], ENCRYPTED_FFPROBE, scheme("Microsoft Digital Rights Manager version 1"))
fixture("protection-object-ids", "An Advanced Content Encryption Object with an Encrypted Object Record of the ID type 2, which section 4.13 does not define, and one of type 1 whose ID is 4 bytes long, not 2 (the section says it should be 2): neither can be read as a stream number, so the record is taken to cover every stream, and the stream, whose Encrypted Content Flag is clear, is protected.",
        protected([wma(1)], extension=advanced([(DRM_SYSTEM, 1, [(2, b"abcd"), (1, b"\x01\x00\x00\x00")], b"")])),
        [ADVANCED_FINDING, ENCRYPTED, finding("asf.encrypted-object-id-type", "0x0002"), finding("asf.encrypted-object-id-length", "4 bytes")], ENCRYPTED_FFPROBE,
        [{"scheme": "Windows Media DRM for network devices", "is_encrypted": True, "systems": [NETWORK_DEVICES]}])
fixture("protection-truncated", "A Content Encryption Object whose secret data length is 5000 bytes, in an object of 40.", 
        protected([wma(1, encrypted=True), m.obj(m.CONTENT_ENCRYPTION, struct.pack("<I", 5000) + b"secret" + struct.pack("<I", 4) + b"DRM\0")]),
        [DRM1_FINDING, ENCRYPTED, finding("asf.object-truncated", "Content Encryption Object")], ENCRYPTED_FFPROBE, scheme("Microsoft Digital Rights Manager version 1"))
fixture("protected-mpeg-audio", "MPEG Layer III, which has no parser here, in a file with a Content Encryption Object: the stream is still reported, as protected and encrypted.",
        protected([m.stream_properties(1, m.wave_format(0x0055, 2, 44100, 16000, 1, 0)), m.content_encryption()]), [DRM1_FINDING],
        {"ffprobe": False, "reason": "the MPEG audio payloads are not MPEG audio frames."},
        scheme("Microsoft Digital Rights Manager version 1"))
fixture("protected-video-only", "A video stream whose Encrypted Content Flag is set, with a Content Encryption Object, and no audio: the stream is reported as protected and encrypted.",
        protected([m.stream_properties(1, b"\x01\x02\x03", stream_type=m.VIDEO_MEDIA, encrypted=True), m.content_encryption()]),
        [DRM1_FINDING, finding("asf.stream-not-analyzed", "video")], ENCRYPTED_FFPROBE, scheme("Microsoft Digital Rights Manager version 1"))
fixture("protected-audio-and-video", "An encrypted Windows Media Audio stream and an encrypted video stream, with a Content Encryption Object: the summary takes its codec and rate from the audio stream, and its protection from both.",
        protected([wma(1, encrypted=True), m.stream_properties(2, b"\x01\x02\x03", stream_type=m.VIDEO_MEDIA, encrypted=True), m.content_encryption()]),
        [DRM1_FINDING, ENCRYPTED, finding("asf.stream-not-analyzed", "video"), finding("asf.stream-without-payloads", "Stream 2")], ENCRYPTED_FFPROBE,
        scheme("Microsoft Digital Rights Manager version 1") * 2)
fixture("protected-advanced-no-records", "An Advanced Content Encryption Object with no content encryption record: it protects no stream, and a PCM stream stays clear.",
        protected([pcm(1)], extension=advanced([])), [finding("asf.no-content-encryption-record", "no content encryption record")], None, [])

fixture("protected-advanced-no-object-records", "An Advanced Content Encryption Object whose only record lists no encrypted object records: it names no stream, so it protects none and a PCM stream stays clear.",
        protected([pcm(1)], extension=advanced([(DRM_SYSTEM, 1, [], b"")])), [finding("asf.no-content-encryption-record", "name no stream")])
fixture("protected-advanced-unknown-stream-only", "An Advanced Content Encryption Object whose only record names stream 5, which the file does not have: it protects no stream of the file.",
        protected([pcm(1)], extension=advanced([(DRM_SYSTEM, 1, [(1, stream_number(5))], b"")])),
        [finding("asf.no-content-encryption-record", "streams that the file does not have"), finding("asf.encrypted-object-unknown-stream", "stream 5")])


def advanced_raw(body, streams=1):
    return lambda: m.asf_file([pcm(1), wma(2)], objects_in([1, 2], 6, 100, 512), 512, file_id=FILE_ID, play=1_000_000, extension=m.obj(m.ADVANCED_CONTENT_ENCRYPTION, body))


RECORD_HEAD = struct.pack("<H", 1) + m.guid(DRM_SYSTEM) + struct.pack("<I", 1)
UNREAD = finding("asf.content-encrypted", "could not be read in full")
NETWORK = [{"scheme": "Windows Media DRM for network devices", "is_encrypted": True, "systems": [NETWORK_DEVICES]}]
fixture("protected-advanced-truncated", "An Advanced Content Encryption Object cut inside its only record, which counts three encrypted object records and holds one, for stream 2: the record as far as it was read still protects the stream.",
        advanced_raw(RECORD_HEAD + struct.pack("<H", 3) + struct.pack("<HH", 1, 2) + stream_number(2)),
        [UNREAD, ENCRYPTED, finding("asf.object-truncated", "Advanced Content Encryption Object")], ENCRYPTED_FFPROBE, NETWORK * 2)
NO_SYSTEM = [{"scheme": "Windows Media DRM for network devices", "is_encrypted": True, "systems": []}]
for cut, why, body, protection in [
    ("in-guid", "inside the system ID of the only record", RECORD_HEAD[:10], NO_SYSTEM),
    ("after-system", "after the system ID and version of the only record, before its object count", RECORD_HEAD, NETWORK),
    ("after-count", "after the object count of the only record, which counts two encrypted object records and holds none", RECORD_HEAD + struct.pack("<H", 2), NETWORK),
    ("in-object-record", "inside the only encrypted object record", RECORD_HEAD + struct.pack("<H", 1) + struct.pack("<H", 1), NETWORK),
    ("empty", "before the count of records", b"", NO_SYSTEM),
]:
    fixture(f"protected-advanced-cut-{cut}", f"An Advanced Content Encryption Object cut {why}: the records were not read in full, so every stream is taken to be protected.",
            advanced_raw(body), [UNREAD, ENCRYPTED, finding("asf.object-truncated", "Advanced Content Encryption Object")] + ([finding("asf.object-size", "at least 26")] if not body else []),
            ENCRYPTED_FFPROBE, protection * 2)
ONE_RECORD = m.guid(DRM_SYSTEM) + struct.pack("<IH", 1, 1) + struct.pack("<HH", 1, 2) + stream_number(2) + struct.pack("<I", 0)
fixture("protected-advanced-count-past-object", "An Advanced Content Encryption Object that counts two records and holds one, which names stream 2: the second record is missing, so every stream is taken to be protected.",
        advanced_raw(struct.pack("<H", 2) + ONE_RECORD), [UNREAD, ENCRYPTED, finding("asf.object-truncated", "Advanced Content Encryption Object")], ENCRYPTED_FFPROBE, NO_SYSTEM + NETWORK)
fixture("protected-advanced-1001-records", "An Advanced Content Encryption Object of 1001 records, each naming stream 2: the first 1000 are read and the rest are not, so every stream is taken to be protected.",
        advanced_raw(struct.pack("<H", 1001) + ONE_RECORD * 1001), [UNREAD, ENCRYPTED, finding("asf.too-many-entries", "content encryption records")], ENCRYPTED_FFPROBE, NO_SYSTEM + NETWORK)
fixture("protected-advanced-data-size", "An Advanced Content Encryption Object whose record states 100 bytes of data and holds 3, for stream 2.",
        advanced_raw(RECORD_HEAD + struct.pack("<H", 1) + struct.pack("<HH", 1, 2) + stream_number(2) + struct.pack("<I", 100) + b"abc"),
        [ADVANCED_FINDING, ENCRYPTED, finding("asf.encryption-data-size", "100 bytes of data")], ENCRYPTED_FFPROBE, NETWORK)


def advanced_many():
    objects = [(1, stream_number(2))] * 1001
    return m.asf_file([pcm(1), wma(2)], objects_in([1, 2], 6, 100, 512), 512, file_id=FILE_ID, play=1_000_000, extension=advanced([(DRM_SYSTEM, 1, objects, b"")]))


fixture("protected-advanced-many-records", "An Advanced Content Encryption Object with one record of 1001 encrypted object records, all for stream 2: the first 1000 are read and the rest of the object is not, so every stream is taken to be protected.", advanced_many,
        [UNREAD, ENCRYPTED, finding("asf.too-many-entries", "encrypted object records")], ENCRYPTED_FFPROBE,
        NETWORK * 2)

# ---- damaged files ----


def cut(build, length):
    return lambda: build()[:length]


def base():
    return simple(objects_in([1], 6, 100, 256))


fixture("truncated-header", "A file cut in the middle of the header.", cut(base, 130),
        [finding("asf.object-truncated", "ends"), finding("asf.object-truncated", "too few"), finding("asf.header-object-count", "counts"), finding("asf.missing-data", "no Data Object"),
         finding("asf.no-streams", "no Stream Properties"), finding("asf.missing-header-extension", "no Header Extension"),
         finding("asf.missing-file-properties", "no File Properties"), finding("asf.object-size", "at least 104"), finding("asf.object-overruns", "4 more")],
        {"ffprobe": False, "reason": "the header is cut."})
fixture("truncated-data", "A file cut in the middle of the third packet.", lambda: base()[:-600],
        [finding("asf.object-truncated", "ends"), finding("asf.file-size", "1094 bytes"), finding("asf.packet-count", "counts 3 data packets"), finding("asf.packet-count", "Data Object counts 3"),
         finding("asf.packet-truncated", "ends before"), finding("asf.payload-overrun", "extends past")],
        {"ffprobe": False, "reason": "the file is cut inside a payload, which ffmpeg returns as a short packet."})
fixture("trailing-garbage", "Bytes after the last object that are not an object.", lambda: base() + b"\xde\xad\xbe\xef" * 5, [finding("asf.trailing-data", "20 bytes"), finding("asf.file-size", "1094 bytes")])


def header_child_size(size, delta=0):
    def build():
        return m.asf_file([pcm(), m.obj(m.PADDING, b"\0" * 10)[:16] + struct.pack("<Q", size) + b"\0" * 10], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000)
    return build


fixture("object-size-small", "A header object that states a size of 10 bytes, less than its GUID and size.", header_child_size(10),
        [finding("asf.object-size", "less than the 24"), finding("asf.missing-header-extension", "no Header Extension"), finding("asf.header-object-count", "counts 4")], {"ffprobe": False, "reason": "ffmpeg rejects the object."})
fixture("object-size-large", "A header object that states more bytes than the header has left.", header_child_size(5000),
        [finding("asf.object-overruns", "5000"), finding("asf.missing-header-extension", "no Header Extension"), finding("asf.header-object-count", "counts 4")], {"ffprobe": False, "reason": "ffmpeg rejects the object."})
fixture("missing-file-properties", "A header without a File Properties Object.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, properties_override=m.padding(80)),
        [finding("asf.missing-file-properties", "no File Properties"), finding("asf.no-packet-size", "no packet size")], {"ffprobe": False, "reason": "ffmpeg needs the packet size from the File Properties Object."})
fixture("no-streams", "A header without a Stream Properties Object.",
        lambda: m.asf_file([m.padding(10)], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID),
        [finding("asf.no-streams", "no Stream Properties"), finding("asf.payload-unknown-stream", "stream 1")], {"ffprobe": False, "reason": "there is no stream."})
fixture("header-count-wrong", "A header whose object count is 9 for 3 objects.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, header_count=9),
        [finding("asf.header-object-count", "9 objects")])


def reserved_file():
    data = bytearray(base())
    data[28] = 7
    data[29] = 9
    return bytes(data)


fixture("header-reserved", "A Header Object whose reserved bytes are 7 and 9 instead of 1 and 2.", reserved_file,
        [finding("asf.header-reserved1", "7"), finding("asf.header-reserved2", "9")], {"ffprobe": False, "reason": "ffmpeg fails on the second reserved byte."})
fixture("file-size-wrong", "A file whose File Properties Object gives a size of 1000 bytes.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000, properties_override=lambda size, n: m.file_properties(FILE_ID, 1000, 0, n, 500_000, 0, 0, 2, 256, 256, 0)),
        [finding("asf.file-size", "1000 bytes")])
fixture("packet-count-wrong", "A File Properties Object and a Data Object that count 5 and 7 packets for 3.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000, total_packets=7,
                           properties_override=m.file_properties(FILE_ID, 0, 0, 5, 500_000, 0, 0, 2, 256, 256, 0), fix_file_size=False),
        [finding("asf.packet-count", "counts 5"), finding("asf.packet-count", "counts 7"), finding("asf.file-size", "0 bytes")])
fixture("packet-sizes-differ", "A File Properties Object with a minimum packet size of 128 and a maximum of 256.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000, properties_override=lambda size, n: m.file_properties(FILE_ID, size, 0, n, 500_000, 0, 0, 2, 128, 256, 0)),
        [finding("asf.packet-size", "minimum (128)")])
fixture("file-id-differs", "A Data Object with another file ID than the File Properties Object.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000,
                           properties_override=lambda size, n: m.file_properties(bytes(16), size, 0, n, 500_000, 0, 0, 2, 256, 256, 0)),
        [finding("asf.file-id", "differs")])
fixture("data-size-zero", "A Data Object that states a size of 0 in a file that is not a broadcast.",
        lambda: m.asf_file([pcm()], objects_in([1], 3, 60, 256), 256, file_id=FILE_ID, play=500_000, data_size=0),
        [finding("asf.data-size-zero", "size of 0")])


def packets_with_issues():
    layout = m.Layout()

    def pk(**kw):
        return m.packet([m.Payload(1, kw.pop("number", 1), samples(60), time=0)], 256, layout, **kw)

    packets = [
        pk(),
        pk(padding_field=999, number=2),
        m.packet([m.Payload(1, 3, samples(60))], 256, m.Layout(), payload_flags=0x80),
        m.packet([m.Payload(1, 4, samples(60), replicated=b"abc")], 256, m.Layout()),
        m.packet([m.Payload(0, 5, samples(60), stream_byte=0)], 256, m.Layout()),
        m.packet([m.Payload(1, 6, samples(60))], 256, m.Layout(packet_length=2), declared_length=200, padding_field=0),
        m.packet([m.Payload(1, 7, samples(60))], 256, m.Layout(), ecc_flags=0xA2),
        m.packet([m.Payload(1, 8, samples(60))], 256, m.Layout(), padding_field=3),
        pk(number=9),
    ]
    return m.asf_file([pcm()], packets, 256, file_id=FILE_ID, play=500_000)


fixture("packet-issues", "Packets with a padding length past the packet, no payloads, replicated data of 3 bytes, a payload of stream 0, a packet length that differs from the packet size, an error correction length type that is reserved and a wrong padding length.",
        packets_with_issues,
        [finding("asf.padding-length", "more padding"), finding("asf.no-payloads", "no payloads"), finding("asf.replicated-length", "2 to 7"), finding("asf.payload-stream-zero", "stream 0"),
         finding("asf.packet-length", "differs from the packet size"), finding("asf.ecc-length-type", "reserves"), finding("asf.padding-mismatch", "padding length"),
         finding("asf.stream-number-type", "stream number length type"), finding("asf.payload-unknown-stream", "stream 0")],
        {"ffprobe": False, "reason": "the packets are damaged on purpose."})


def overrun_file():
    layout = m.Layout()
    good = m.packet([m.Payload(1, 1, samples(60))], 256, layout)
    bad = bytearray(m.packet([m.Payload(1, 2, samples(60)), m.Payload(1, 3, samples(60))], 256, layout))
    # The first payload's length word: make it larger than the packet.
    assert bad[29:31] == struct.pack("<H", 60)
    bad[29:31] = struct.pack("<H", 600)
    return simple([good, bytes(bad), good], packet_size=256)


fixture("payload-overrun", "A payload whose length is 600 bytes in a packet of 256.", overrun_file,
        [finding("asf.payload-overrun", "extends past")],
        {"ffprobe": False, "reason": "the packet is damaged on purpose."})


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, description, builder, findings, crosscheck in FIXTURES:
        data = builder()
        (OUT / f"{name}.asf").write_bytes(data)
        model = m.parse(data)
        derived = {"derived": {"from": DERIVED_FROM, "note": "Not an encoder's output: the source file with one attribute added to its header."}} if name.startswith("derived-") else {}
        manifest = {"description": description, "format": "ASF", **model, **derived, "findings": unparsed(model["streams"]) + findings, "crosscheck": crosscheck}
        if name in PROTECTION:
            manifest["protection"] = PROTECTION[name]
        (OUT / f"{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"{count} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
