"""A model of the ASF container (Advanced Systems Format Specification, revision 01.20.03) for the
fixtures: builders that write objects, packets and payloads byte by byte, and a reader that works the
expected figures out of a file, apart from the analyzer. Standard library only.

The reader is the model of what the analyzer must report: top-level objects, the objects of the header
and of the Header Extension Object, the streams and their WAVEFORMATEX, the packets and payloads with
every field, and the media objects the payloads put together, which a codec parser is handed.
"""

import struct
import uuid

# ---- GUIDs (section 10) ----

HEADER = "75B22630-668E-11CF-A6D9-00AA0062CE6C"
# Section 4.12; the text prints the Header Object's GUID for it in section 10.3, a misprint (MediaInfoLib and the
# Windows Media Format SDK's files use this one).
COMPATIBILITY = "26F18B5D-4584-47EC-9F5F-0E651F0452C9"
DATA = "75B22636-668E-11CF-A6D9-00AA0062CE6C"
SIMPLE_INDEX = "33000890-E5B1-11CF-89F4-00A0C90349CB"
INDEX = "D6E229D3-35DA-11D1-9034-00A0C90349BE"
MEDIA_OBJECT_INDEX = "FEB103F8-12AD-4C64-840F-2A1D2F7AD48C"
TIMECODE_INDEX = "3CB73FD0-0C4A-4803-953D-EDF7B6228F0C"
FILE_PROPERTIES = "8CABDCA1-A947-11CF-8EE4-00C00C205365"
STREAM_PROPERTIES = "B7DC0791-A9B7-11CF-8EE6-00C00C205365"
HEADER_EXTENSION = "5FBF03B5-A92E-11CF-8EE3-00C00C205365"
CODEC_LIST = "86D15240-311D-11D0-A3A4-00A0C90348F6"
SCRIPT_COMMAND = "1EFB1A30-0B62-11D0-A39B-00A0C90348F6"
MARKER = "F487CD01-A951-11CF-8EE6-00C00C205365"
BITRATE_MUTUAL_EXCLUSION = "D6E229DC-35DA-11D1-9034-00A0C90349BE"
ERROR_CORRECTION = "75B22635-668E-11CF-A6D9-00AA0062CE6C"
CONTENT_DESCRIPTION = "75B22633-668E-11CF-A6D9-00AA0062CE6C"
EXTENDED_CONTENT_DESCRIPTION = "D2D0A440-E307-11D2-97F0-00A0C95EA850"
CONTENT_BRANDING = "2211B3FA-BD23-11D2-B4B7-00A0C955FC6E"
STREAM_BITRATE_PROPERTIES = "7BF875CE-468D-11D1-8D82-006097C9A2B2"
CONTENT_ENCRYPTION = "2211B3FB-BD23-11D2-B4B7-00A0C955FC6E"
EXTENDED_CONTENT_ENCRYPTION = "298AE614-2622-4C17-B935-DAE07EE9289C"
DIGITAL_SIGNATURE = "2211B3FC-BD23-11D2-B4B7-00A0C955FC6E"
PADDING = "1806D474-CADF-4509-A4BA-9AABCB96AAE8"
EXTENDED_STREAM_PROPERTIES = "14E6A5CB-C672-4332-8399-A96952065B5A"
ADVANCED_MUTUAL_EXCLUSION = "A08649CF-4775-4670-8A16-6E35357566CD"
GROUP_MUTUAL_EXCLUSION = "D1465A40-5A79-4338-B71B-E36B8FD6C249"
STREAM_PRIORITIZATION = "D4FED15B-88D3-454F-81F0-ED5C45999E24"
BANDWIDTH_SHARING = "A69609E6-517B-11D2-B6AF-00C04FD908E9"
LANGUAGE_LIST = "7C4346A9-EFE0-4BFC-B229-393EDE415C85"
METADATA = "C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA"
METADATA_LIBRARY = "44231C94-9498-49D1-A141-1D134E457054"
INDEX_PARAMETERS = "D6E229DF-35DA-11D1-9034-00A0C90349BE"
MEDIA_OBJECT_INDEX_PARAMETERS = "6B203BAD-3F11-48E4-ACA8-D7613DE2CFA7"
TIMECODE_INDEX_PARAMETERS = "F55E496D-9797-4B5D-8C8B-604DFE9BFB24"
ADVANCED_CONTENT_ENCRYPTION = "43058533-6981-49E6-9B74-AD12CB86D58C"
RESERVED_1 = "ABD3D211-A9BA-11CF-8EE6-00C00C205365"
RESERVED_2 = "86D15241-311D-11D0-A3A4-00A0C90348F6"
RESERVED_3 = "4B1ACBE3-100B-11D0-A39B-00A0C90348F6"
RESERVED_4 = "4CFEDB20-75F6-11CF-9C0F-00A0C90349CB"
AUDIO_MEDIA = "F8699E40-5B4D-11CF-A8FD-00805F5C442B"
VIDEO_MEDIA = "BC19EFC0-5B4D-11CF-A8FD-00805F5C442B"
COMMAND_MEDIA = "59DACFC0-59E6-11D0-A3AC-00A0C90348F6"
NO_ERROR_CORRECTION = "20FB5700-5B55-11CF-A8FD-00805F5C442B"
AUDIO_SPREAD = "BFC3CD50-618F-11CF-8BB2-00AA00B4E220"
MUTEX_LANGUAGE = "D6E22A00-35DA-11D1-9034-00A0C90349BE"
MUTEX_BITRATE = "D6E22A01-35DA-11D1-9034-00A0C90349BE"
SHARING_EXCLUSIVE = "AF6060AA-5197-11D2-B6AF-00C04FD908E9"
SHARING_PARTIAL = "AF6060AB-5197-11D2-B6AF-00C04FD908E9"
EXTENSION_TIMECODE = "399595EC-8667-4E2D-8FDB-98814CE76C1E"
EXTENSION_SAMPLE_DURATION = "C6BD9450-867F-4907-83A3-C77921B733AD"
EXTENSION_PIXEL_ASPECT_RATIO = "1B1EE554-F9EA-4BC8-821A-376B74E4C4B8"

NAMES = {
    HEADER: "Header Object", DATA: "Data Object", SIMPLE_INDEX: "Simple Index Object", INDEX: "Index Object",
    MEDIA_OBJECT_INDEX: "Media Object Index Object", TIMECODE_INDEX: "Timecode Index Object",
    FILE_PROPERTIES: "File Properties Object", STREAM_PROPERTIES: "Stream Properties Object",
    HEADER_EXTENSION: "Header Extension Object", CODEC_LIST: "Codec List Object", SCRIPT_COMMAND: "Script Command Object",
    MARKER: "Marker Object", BITRATE_MUTUAL_EXCLUSION: "Bitrate Mutual Exclusion Object", ERROR_CORRECTION: "Error Correction Object",
    CONTENT_DESCRIPTION: "Content Description Object", EXTENDED_CONTENT_DESCRIPTION: "Extended Content Description Object",
    CONTENT_BRANDING: "Content Branding Object", STREAM_BITRATE_PROPERTIES: "Stream Bitrate Properties Object",
    CONTENT_ENCRYPTION: "Content Encryption Object", EXTENDED_CONTENT_ENCRYPTION: "Extended Content Encryption Object",
    DIGITAL_SIGNATURE: "Digital Signature Object", PADDING: "Padding Object",
    EXTENDED_STREAM_PROPERTIES: "Extended Stream Properties Object", ADVANCED_MUTUAL_EXCLUSION: "Advanced Mutual Exclusion Object",
    GROUP_MUTUAL_EXCLUSION: "Group Mutual Exclusion Object", STREAM_PRIORITIZATION: "Stream Prioritization Object",
    BANDWIDTH_SHARING: "Bandwidth Sharing Object", LANGUAGE_LIST: "Language List Object", METADATA: "Metadata Object",
    METADATA_LIBRARY: "Metadata Library Object", INDEX_PARAMETERS: "Index Parameters Object",
    MEDIA_OBJECT_INDEX_PARAMETERS: "Media Object Index Parameters Object", TIMECODE_INDEX_PARAMETERS: "Timecode Index Parameters Object",
    ADVANCED_CONTENT_ENCRYPTION: "Advanced Content Encryption Object",
}


def guid(text):
    """The 16 bytes of a GUID as Windows lays it out (the first three fields little-endian)."""
    return uuid.UUID(text).bytes_le


def guid_text(data):
    return str(uuid.UUID(bytes_le=bytes(data))).upper()


def obj(guid_text_, payload):
    """An object: GUID, a size of 24 bytes plus the data, the data (section 2.1)."""
    return guid(guid_text_) + struct.pack("<Q", 24 + len(payload)) + payload


def utf16(text, terminate=True):
    return (text + ("\0" if terminate else "")).encode("utf-16-le")


# ---- header objects ----

def file_properties(file_id=b"\0" * 16, file_size=0, creation=0, packets=0, play=0, send=0, preroll=0, flags=2, min_packet=0, max_packet=0, max_bitrate=0):
    return obj(FILE_PROPERTIES, file_id + struct.pack("<QQQQQQIIII", file_size, creation, packets, play, send, preroll, flags, min_packet, max_packet, max_bitrate))


def wave_format(tag, channels, rate, avg_bytes, align, bits, extra=b"", size=None):
    """A WAVEFORMATEX with its codec-specific data (section 9.1); size overrides cbSize."""
    return struct.pack("<HHIIHHH", tag, channels, rate, avg_bytes, align, bits, len(extra) if size is None else size) + extra


def wave_format_extensible(tag, channels, rate, avg_bytes, align, bits, valid_bits, mask, extra=b""):
    sub = struct.pack("<I", tag) + guid("00000000-0000-0010-8000-00AA00389B71")[4:]
    return wave_format(0xFFFE, channels, rate, avg_bytes, align, bits, struct.pack("<HI", valid_bits, mask) + sub + extra)


def spread_audio(span=1, virtual_packet=0, virtual_chunk=0, silence=b"\0"):
    return struct.pack("<BHHH", span, virtual_packet, virtual_chunk, len(silence)) + silence


def stream_properties(number, type_data, stream_type=AUDIO_MEDIA, ec_type=NO_ERROR_CORRECTION, ec_data=b"", time_offset=0, encrypted=False, reserved=0):
    flags = number | (0x8000 if encrypted else 0)
    return obj(STREAM_PROPERTIES, guid(stream_type) + guid(ec_type) + struct.pack("<QIIHI", time_offset, len(type_data), len(ec_data), flags, reserved) + type_data + ec_data)


def header_extension(children=b"", reserved_1=RESERVED_1, reserved_2=6, size=None):
    return obj(HEADER_EXTENSION, guid(reserved_1) + struct.pack("<HI", reserved_2, len(children) if size is None else size) + children)


def codec_list(entries):
    """entries: (type, name, description, information)"""
    body = b"".join(struct.pack("<HH", t, len(n) + 1) + utf16(n) + struct.pack("<H", len(d) + 1 if d else 0) + (utf16(d) if d else b"") + struct.pack("<H", len(i)) + i
                    for t, n, d, i in entries)
    return obj(CODEC_LIST, guid(RESERVED_2) + struct.pack("<I", len(entries)) + body)


def content_description(title="", author="", copyright_="", description="", rating=""):
    texts = [utf16(t) if t else b"" for t in (title, author, copyright_, description, rating)]
    return obj(CONTENT_DESCRIPTION, struct.pack("<5H", *[len(t) for t in texts]) + b"".join(texts))


def descriptor_value(kind, value):
    if kind == 0:
        return utf16(value)
    if kind == 1:
        return value
    if kind == 2:
        return struct.pack("<I", value)
    if kind == 3:
        return struct.pack("<I", value)
    if kind == 4:
        return struct.pack("<Q", value)
    if kind == 5:
        return struct.pack("<H", value)
    raise ValueError(kind)


def extended_content_description(items):
    """items: (name, type, value); a boolean is 32 bits here."""
    body = b""
    for name, kind, value in items:
        data = descriptor_value(kind, value)
        body += struct.pack("<H", len(utf16(name))) + utf16(name) + struct.pack("<HH", kind, len(data)) + data
    return obj(EXTENDED_CONTENT_DESCRIPTION, struct.pack("<H", len(items)) + body)


def metadata_records(items, library):
    """items: (stream, name, type, value[, language]); a boolean is 16 bits in these objects, and the library has the GUID type 6."""
    body = b""
    for item in items:
        stream, name, kind, value = item[:4]
        language = item[4] if len(item) > 4 else 0
        if kind == 2:
            data = struct.pack("<H", value)
        elif kind == 6:
            data = guid(value)
        else:
            data = descriptor_value(kind, value)
        body += struct.pack("<HHHHI", language if library else 0, stream, len(utf16(name)), kind, len(data)) + utf16(name) + data
    return struct.pack("<H", len(items)) + body


def metadata(items):
    return obj(METADATA, metadata_records(items, False))


def metadata_library(items):
    return obj(METADATA_LIBRARY, metadata_records(items, True))


def content_branding(image_type=0, image=b"", banner_url=b"", copyright_url=b""):
    return obj(CONTENT_BRANDING, struct.pack("<II", image_type, len(image)) + image + struct.pack("<I", len(banner_url)) + banner_url + struct.pack("<I", len(copyright_url)) + copyright_url)


def stream_bitrate_properties(records):
    return obj(STREAM_BITRATE_PROPERTIES, struct.pack("<H", len(records)) + b"".join(struct.pack("<HI", n, b) for n, b in records))


def padding(size):
    return obj(PADDING, b"\0" * size)


def language_list(languages):
    return obj(LANGUAGE_LIST, struct.pack("<H", len(languages)) + b"".join(struct.pack("<B", len(utf16(l))) + utf16(l) for l in languages))


def extended_stream_properties(number, start=0, end=0, bitrate=0, buffer=0, fullness=0, alt_bitrate=0, alt_buffer=0, alt_fullness=0, max_object=0, flags=0,
                               language_index=0, time_per_frame=0, names=(), extensions=(), stream=b""):
    """names: (language index, text); extensions: (GUID text, data size, info bytes)."""
    body = struct.pack("<QQIIIIIIII", start, end, bitrate, buffer, fullness, alt_bitrate, alt_buffer, alt_fullness, max_object, flags)
    body += struct.pack("<HHQHH", number, language_index, time_per_frame, len(names), len(extensions))
    for language, text in names:
        body += struct.pack("<HH", language, len(utf16(text))) + utf16(text)
    for system, size, info in extensions:
        body += guid(system) + struct.pack("<HI", size, len(info)) + info
    return obj(EXTENDED_STREAM_PROPERTIES, body + stream)


def mutual_exclusion(guid_text_, kind, streams):
    return obj(guid_text_, guid(kind) + struct.pack("<H", len(streams)) + b"".join(struct.pack("<H", s) for s in streams))


def group_mutual_exclusion(kind, records):
    return obj(GROUP_MUTUAL_EXCLUSION, guid(kind) + struct.pack("<H", len(records)) + b"".join(struct.pack("<H", len(r)) + b"".join(struct.pack("<H", s) for s in r) for r in records))


def stream_prioritization(records):
    return obj(STREAM_PRIORITIZATION, struct.pack("<H", len(records)) + b"".join(struct.pack("<HH", n, flags) for n, flags in records))


def bandwidth_sharing(kind, bitrate, buffer, streams):
    return obj(BANDWIDTH_SHARING, guid(kind) + struct.pack("<IIH", bitrate, buffer, len(streams)) + b"".join(struct.pack("<H", s) for s in streams))


def index_parameters(interval, specifiers, guid_text_=INDEX_PARAMETERS):
    return obj(guid_text_, struct.pack("<IH", interval, len(specifiers)) + b"".join(struct.pack("<HH", s, t) for s, t in specifiers))


def script_command(types, commands):
    body = guid(RESERVED_3) + struct.pack("<HH", len(commands), len(types)) + b"".join(struct.pack("<H", len(utf16(t))//2) + utf16(t) for t in types)
    for time, type_index, name in commands:
        body += struct.pack("<IHH", time, type_index, len(utf16(name)) // 2) + utf16(name)
    return obj(SCRIPT_COMMAND, body)


def marker(name, markers):
    body = guid(RESERVED_4) + struct.pack("<IHH", len(markers), 0, len(utf16(name))) + utf16(name)
    for offset, time, send, description in markers:
        text = utf16(description)
        body += struct.pack("<QQHIII", offset, time, 12 + len(text), send, 0, len(text) // 2) + text
    return obj(MARKER, body)


def error_correction(kind, data=b""):
    return obj(ERROR_CORRECTION, guid(kind) + struct.pack("<I", len(data)) + data)


def content_encryption(secret=b"secret", protection=b"DRM\0", key_id=b"key\0", url=b"http://example.invalid/license\0"):
    return obj(CONTENT_ENCRYPTION, struct.pack("<I", len(secret)) + secret + struct.pack("<I", len(protection)) + protection + struct.pack("<I", len(key_id)) + key_id +
               struct.pack("<I", len(url)) + url)


# ---- packets ----

def fixed(value, width):
    return struct.pack({1: "<B", 2: "<H", 4: "<I"}[width], value) if width else b""


WIDTH_TYPE = {0: 0, 1: 1, 2: 2, 4: 3}


class Payload:
    """One payload of a packet. kind 'plain' has replicated data (object size, presentation time and extension bytes);
    'compressed' has a presentation time, a time delta and sub-payloads."""

    def __init__(self, stream, number, data=b"", offset=0, size=None, time=0, key=True, extension=b"", compressed=False, delta=0, subpayloads=(), replicated=None, stream_byte=None):
        self.stream = stream
        self.number = number
        self.data = data
        self.offset = offset
        self.size = len(data) if size is None else size
        self.time = time
        self.key = key
        self.extension = extension
        self.compressed = compressed
        self.delta = delta
        self.subpayloads = subpayloads
        self.replicated = replicated
        self.stream_byte = stream_byte


class Layout:
    """The widths of the fields of a packet (sections 5.2.2 and 5.2.3), in bytes: 0, 1, 2 or 4."""

    def __init__(self, packet_length=0, sequence=0, padding=2, replicated=1, offset=4, number=1, payload_length=2, ecc=True, multiple=True):
        self.packet_length = packet_length
        self.sequence = sequence
        self.padding = padding
        self.replicated = replicated
        self.offset = offset
        self.number = number
        self.payload_length = payload_length
        self.ecc = ecc
        self.multiple = multiple


def payload_bytes(p, layout, with_length):
    if p.compressed:
        data = b"".join(struct.pack("<B", len(s)) + s for s in p.subpayloads)
        replicated = struct.pack("<B", p.delta)
        header = struct.pack("<B", p.stream | (0x80 if p.key else 0)) + fixed(p.number, layout.number) + fixed(p.time, layout.offset) + fixed(1, layout.replicated) + replicated
    else:
        data = p.data
        replicated = p.replicated if p.replicated is not None else (struct.pack("<II", p.size, p.time) + p.extension if layout.replicated else b"")
        stream_byte = p.stream_byte if p.stream_byte is not None else p.stream | (0x80 if p.key else 0)
        header = struct.pack("<B", stream_byte) + fixed(p.number, layout.number) + fixed(p.offset, layout.offset) + fixed(len(replicated), layout.replicated) + replicated
    if with_length:
        header += fixed(len(data), layout.payload_length)
    return header + data


def packet(payloads, size, layout=None, send_time=0, duration=0, sequence=0, ecc_data=b"\0\0", padding_field=None, declared_length=None, ecc_flags=None, payload_flags=None,
           raw_tail=None):
    """A packet of `size` bytes. padding_field overrides the padding length written (None: the real padding); declared_length writes the packet length field."""
    layout = layout or Layout()
    multiple = layout.multiple
    body = b""
    if multiple:
        flags = len(payloads) | (WIDTH_TYPE[layout.payload_length] << 6) if payload_flags is None else payload_flags
        body += struct.pack("<B", flags)
    body += b"".join(payload_bytes(p, layout, multiple) for p in payloads)
    ecc = b""
    if layout.ecc:
        ecc_byte = (len(ecc_data) | 0x80) if ecc_flags is None else ecc_flags
        ecc = struct.pack("<B", ecc_byte) + ecc_data
    length_flags = (1 if multiple else 0) | (WIDTH_TYPE[layout.sequence] << 1) | (WIDTH_TYPE[layout.padding] << 3) | (WIDTH_TYPE[layout.packet_length] << 5) | (0 if layout.ecc else 0)
    property_flags = WIDTH_TYPE[layout.replicated] | (WIDTH_TYPE[layout.offset] << 2) | (WIDTH_TYPE[layout.number] << 4) | (1 << 6)
    head_length = len(ecc) + 2 + layout.packet_length + layout.sequence + layout.padding + 6
    pad = size - head_length - len(body)
    if pad < 0:
        raise ValueError(f"The payloads take {-pad} bytes more than the packet of {size} bytes holds.")
    written_padding = pad if padding_field is None else padding_field
    head = ecc + struct.pack("<BB", length_flags, property_flags)
    head += fixed(size if declared_length is None else declared_length, layout.packet_length)
    head += fixed(sequence, layout.sequence)
    head += fixed(written_padding, layout.padding)
    head += struct.pack("<IH", send_time, duration)
    out = head + body + (raw_tail if raw_tail is not None else b"\0" * pad)
    return out[:size] if raw_tail is None else out


def data_object(file_id, packets, total=None, reserved=0x0101, size=None):
    body = guid_bytes(file_id) + struct.pack("<QH", len(packets) if total is None else total, reserved) + b"".join(packets)
    return (guid(DATA) + struct.pack("<Q", 24 + len(body) if size is None else size) + body)


def guid_bytes(value):
    return value if isinstance(value, (bytes, bytearray)) else guid(value)


def simple_index(entries, interval=10_000_000, max_packets=None, file_id=b"\0" * 16):
    maximum = max((c for _, c in entries), default=0) if max_packets is None else max_packets
    return obj(SIMPLE_INDEX, file_id + struct.pack("<QII", interval, maximum, len(entries)) + b"".join(struct.pack("<IH", p, c) for p, c in entries))


def index_object(interval, specifiers, blocks, guid_text_=INDEX):
    """blocks: (positions, entries) where entries are tuples of one offset per specifier."""
    body = struct.pack("<IHI", interval, len(specifiers), len(blocks)) + b"".join(struct.pack("<HH", s, t) for s, t in specifiers)
    for positions, entries in blocks:
        body += struct.pack("<I", len(entries)) + b"".join(struct.pack("<Q", p) for p in positions)
        body += b"".join(b"".join(struct.pack("<I", o) for o in entry) for entry in entries)
    return obj(guid_text_, body)


def asf_file(header_children, packets, packet_size, file_id=b"\0" * 16, play=0, send=0, preroll=0, flags=2, trailing=b"", max_bitrate=0, creation=0,
             header_count=None, extension=b"", properties_override=None, data_size=None, total_packets=None, fix_file_size=True, data_extra=b""):
    """Header Object with File Properties, the header children and a Header Extension Object, then the Data Object and the trailing objects.
    Children are placed after the File Properties Object; extension holds the objects of the Header Extension Object."""
    children = list(header_children)

    def build(file_size):
        properties = properties_override(file_size, len(packets)) if callable(properties_override) else properties_override if properties_override is not None else file_properties(file_id, file_size, creation, len(packets), play, send, preroll, flags,
                                                                                                  packet_size, packet_size, max_bitrate)
        parts = [properties] + children + [header_extension(extension)]
        count = len(parts) if header_count is None else header_count
        body = b"".join(parts)
        header = guid(HEADER) + struct.pack("<QIBB", 30 + len(body), count, 1, 2) + body
        data = data_object(file_id, packets, total_packets, size=data_size)
        return header + data + data_extra + trailing

    data = build(0)
    return build(len(data)) if fix_file_size else data


# ---- reading ----

def read_objects(data, start, end):
    """The objects between start and end: (guid text, offset, size) while the sizes fit."""
    result = []
    position = start
    while position + 24 <= end:
        size = struct.unpack_from("<Q", data, position + 16)[0]
        name = guid_text(data[position:position + 16])
        if size == 0 and name == DATA:
            size = end - position
        if size < 24:
            break
        result.append((name, position, min(size, end - position)))
        position += size
    return result


def parse(data):
    """The figures a file's structure gives: objects, streams, packets, payloads and media objects."""
    result = {"length": len(data), "objects": [], "header_objects": [], "extension_objects": [], "streams": [], "packets": [], "payloads": [], "media_objects": []}
    top = read_objects(data, 0, len(data))
    for g, offset, size in top:
        result["objects"].append([NAMES.get(g, g), offset, size])
    header = next(((o, s) for g, o, s in top if g == HEADER), None)
    packet_size = 0
    file = {}
    if header:
        for g, offset, size in read_objects(data, header[0] + 30, header[0] + header[1]):
            result["header_objects"].append([NAMES.get(g, g), offset, size])
            if g == FILE_PROPERTIES and size >= 104:
                (file_size, creation, packets, play, send, preroll, flags, minimum, maximum, bitrate) = struct.unpack_from("<QQQQQQIIII", data, offset + 40)
                file = {"file_size": file_size, "packets": packets, "play_duration": play, "send_duration": send, "preroll": preroll, "flags": flags, "min_packet": minimum,
                        "max_packet": maximum, "max_bitrate": bitrate}
                packet_size = maximum or minimum
            elif g == STREAM_PROPERTIES and size >= 78:
                result["streams"].append(stream_of(data, offset, size))
            elif g == HEADER_EXTENSION and size >= 46:
                inner = struct.unpack_from("<I", data, offset + 24 + 16 + 2)[0]
                for eg, eo, es in read_objects(data, offset + 46, min(offset + 46 + inner, offset + size)):
                    result["extension_objects"].append(["Compatibility Object" if eg == COMPATIBILITY else NAMES.get(eg, eg), eo, es])
                    if eg == EXTENDED_STREAM_PROPERTIES and es >= 24 + 64:
                        embedded = embedded_stream(data, eo, es)
                        if embedded:
                            result["streams"].append(embedded)
    result["file"] = file
    streams = {s["number"]: s for s in result["streams"]}
    data_object_ = next(((o, s) for g, o, s in top if g == DATA), None)
    if data_object_ and data_object_[1] >= 50:
        packet_size = packet_size or data_object_[1] - 50
        try:
            read_packets(data, data_object_[0] + 50, data_object_[0] + data_object_[1], packet_size, result)
        except (struct.error, IndexError):
            pass
    return result


def embedded_stream(data, offset, size):
    """The Stream Properties Object that ends an Extended Stream Properties Object, as stream_of reads it, or None."""
    names, extensions = struct.unpack_from("<HH", data, offset + 24 + 60)
    at = offset + 24 + 64
    for _ in range(names):
        at += 4 + struct.unpack_from("<H", data, at + 2)[0]
    for _ in range(extensions):
        at += 16 + 2 + 4 + struct.unpack_from("<I", data, at + 18)[0]
    if offset + size - at >= 78 and guid_text(data[at:at + 16]) == STREAM_PROPERTIES:
        entry = stream_of(data, at, offset + size - at)
        entry["embedded"] = True
        return entry
    return None


def stream_of(data, offset, size):
    stream_type = guid_text(data[offset + 24:offset + 40])
    ec_type = guid_text(data[offset + 40:offset + 56])
    time_offset, type_length, ec_length, flags = struct.unpack_from("<QIIH", data, offset + 56)
    start = offset + 24 + 54
    entry = {"number": flags & 0x7F, "type": NAMES.get(stream_type, stream_type), "error_correction": ec_type, "encrypted": bool(flags & 0x8000), "type_length": type_length}
    if stream_type == AUDIO_MEDIA and type_length >= 16:
        tag, channels, rate, avg, align, bits = struct.unpack_from("<HHIIHH", data, start)
        entry.update({"tag": tag, "channels": channels, "rate": rate, "avg_bytes": avg, "block_align": align, "bits": bits})
        if type_length >= 18:
            extra = struct.unpack_from("<H", data, start + 16)[0]
            entry["codec_data"] = data[start + 18:start + 18 + min(extra, type_length - 18)].hex()
    return entry


def width(kind):
    return {0: 0, 1: 1, 2: 2, 3: 4}[kind]


def read_value(data, position, w):
    return {0: 0, 1: lambda: data[position], 2: lambda: struct.unpack_from("<H", data, position)[0], 4: lambda: struct.unpack_from("<I", data, position)[0]}[w]() if w else 0


def read_packets(data, start, end, packet_size, result):
    """Packets of packet_size bytes (or the packet length field), with every payload, and the media objects."""
    position = start
    pending = {}
    index = 0
    while position + 2 <= end:
        chunk = data[position:min(position + packet_size, end)]
        at = 0
        ecc_length = 0
        if chunk[0] & 0x80:
            ecc_length = chunk[0] & 0x0F if not chunk[0] & 0x60 else 0
            at = 1 + ecc_length
        if at + 2 > len(chunk):
            result["packets"].append([position, len(chunk), 0])
            break
        length_flags, property_flags = chunk[at], chunk[at + 1]
        at += 2
        w_length, w_sequence, w_padding = width((length_flags >> 5) & 3), width((length_flags >> 1) & 3), width((length_flags >> 3) & 3)
        if at + w_length + w_sequence + w_padding + 6 > len(chunk):
            # The data ends inside the packet's header: a packet node with no payloads.
            result["packets"].append([position, len(chunk), 0])
            break
        length = read_value(chunk, at, w_length)
        at += w_length + w_sequence
        padding_ = read_value(chunk, at + 0, w_padding)
        at += w_padding
        send_time, duration = struct.unpack_from("<IH", chunk, at)
        at += 6
        size = length or packet_size
        body_end = min(size, len(chunk)) - (padding_ if len(chunk) >= size and padding_ <= size - at else 0)
        multiple = length_flags & 1
        count = 1
        w_payload = 0
        if multiple:
            count = chunk[at] & 0x3F
            w_payload = width(chunk[at] >> 6)
            at += 1
        w_replicated, w_offset, w_number = width(property_flags & 3), width((property_flags >> 2) & 3), width((property_flags >> 4) & 3)
        payloads = 0
        for _ in range(count):
            if at >= body_end:
                break
            stream_byte = chunk[at]
            at += 1
            number = read_value(chunk, at, w_number)
            at += w_number
            offset = read_value(chunk, at, w_offset)
            at += w_offset
            replicated = read_value(chunk, at, w_replicated)
            at += w_replicated
            object_size = presentation = 0
            delta = 0
            if replicated == 1:
                delta = chunk[at]
            elif replicated >= 8:
                object_size, presentation = struct.unpack_from("<II", chunk, at)
            at += replicated
            data_length = read_value(chunk, at, w_payload) if multiple else body_end - at
            at += w_payload if multiple else 0
            data_length = max(0, min(data_length, body_end - at))
            payload_at = position + at
            result["payloads"].append([index, stream_byte & 0x7F, number, offset, replicated, data_length, replicated == 1, bool(stream_byte & 0x80)])
            payloads += 1
            if replicated == 1:
                inner = chunk[at:at + data_length]
                sub = 0
                cursor = 0
                while cursor < len(inner):
                    n = inner[cursor]
                    result["media_objects"].append([stream_byte & 0x7F, number + sub, n, offset + delta * sub, [[payload_at + cursor + 1, n]]])
                    cursor += 1 + n
                    sub += 1
            else:
                key = (stream_byte & 0x7F)
                if offset == 0:
                    pending[key] = [number, object_size if replicated >= 8 else data_length, presentation, 0, []]
                current = pending.get(key)
                if current and current[0] == number and offset == current[3]:
                    take = min(data_length, current[1] - current[3])
                    current[3] += take
                    current[4].append([payload_at, take])
                    if current[3] == current[1]:
                        result["media_objects"].append([key, number, current[1], current[2], current[4]])
                        del pending[key]
            at += data_length
        # A file that gives one packet size as minimum and maximum has packets of that size (section 8.2.13); a shorter
        # length field leaves padding (FFmpeg's asfdec_f.c reads it so).
        fixed = packet_size > 0 and result["file"].get("min_packet") == result["file"].get("max_packet")
        step = packet_size if fixed else size
        result["packets"].append([position, min(step, end - position), payloads])
        position += step
        index += 1
    return result
