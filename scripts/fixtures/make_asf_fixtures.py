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


def fixture(name, description, builder, findings=(), crosscheck=None):
    """findings are those besides the ones every PCM or MPEG audio stream gets (no parser for it here)."""
    FIXTURES.append((name, description, builder, list(findings), crosscheck or {"ffprobe": True}))


def unparsed(streams):
    """The info finding for each audio stream whose coding has no parser: PCM and MPEG audio."""
    names = {0x0001: "PCM", 0x0055: "MPEG audio layer III"}
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
         finding("asf.content-encrypted", "Digital Rights Management"), finding("asf.content-encrypted", "Windows Media Rights Manager"),
         finding("asf.content-encrypted", "network devices"), finding("asf.index-missing", "no Index Object")])


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
        m.packet([m.Payload(1, 6, samples(60))], 200, m.Layout(packet_length=2), declared_length=200),
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
        manifest = {"description": description, "format": "ASF", **model, "findings": unparsed(model["streams"]) + findings, "crosscheck": crosscheck}
        (OUT / f"{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"{count} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
