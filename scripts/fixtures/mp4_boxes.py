"""Builds ISO base media (MP4) files box by box, and AAC access units bit by bit, for the fixture scripts.

A Box knows its type, header options and either raw payload or child boxes; write() lays the file out and
records every box's path of types, offset and size, which the manifests list. Sample tables that point into
mdat are built in two passes: the first finds where mdat's payload lands, the second fills in the offsets.

The AAC access units are raw_data_blocks (ISO/IEC 14496-3 4.4.2.1) that decoders play as silence: channel
elements with max_sfb 0, so no section, scalefactor or spectral data, or with every band in ZERO_HCB
sections, which makes a single channel element longer than the 192 bits the ADTS CRC covers. A fill element
(EXT_FILL, 4.5.2.10) of any size varies their lengths.
"""

import struct


class Bits:
    """Writes bit fields most significant bit first."""

    def __init__(self):
        self.value = 0
        self.count = 0

    def put(self, value, width):
        assert 0 <= value < (1 << width), (value, width)
        self.value = (self.value << width) | value
        self.count += width
        return self

    def align(self):
        if self.count % 8:
            self.put(0, 8 - self.count % 8)
        return self

    def bytes(self):
        padded = self.count + (-self.count % 8)
        return (self.value << (padded - self.count)).to_bytes(padded // 8, "big")


# --- AAC -----------------------------------------------------------------------------------------

SAMPLE_RATES = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350]


def rate_index(rate):
    return SAMPLE_RATES.index(rate)


def ics_info(bits, max_sfb=0):
    # ics_reserved_bit, window_sequence ONLY_LONG_SEQUENCE, window_shape, max_sfb, predictor_data_present.
    bits.put(0, 1).put(0, 2).put(0, 1).put(max_sfb, 6).put(0, 1)


def ics_body(bits, gain=100, zero_bands=0):
    """global_gain, the section data, then no pulse, TNS or gain control data."""
    bits.put(gain, 8)
    # One section of ZERO_HCB per band: sect_cb 0 (4 bits), sect_len 1 (5 bits for long windows).
    for _ in range(zero_bands):
        bits.put(0, 4).put(1, 5)
    bits.put(0, 1).put(0, 1).put(0, 1)


def sce(bits, tag=0, element=0, zero_bands=0):
    bits.put(element, 3).put(tag, 4)
    # individual_channel_stream without a common window: global_gain before ics_info.
    bits.put(100, 8)
    ics_info(bits, zero_bands)
    for _ in range(zero_bands):
        bits.put(0, 4).put(1, 5)
    bits.put(0, 1).put(0, 1).put(0, 1)


def cpe(bits, tag=0):
    bits.put(1, 3).put(tag, 4).put(1, 1)
    ics_info(bits)
    bits.put(0, 2)
    ics_body(bits)
    ics_body(bits)


def fill(bits, count):
    """A fill element of count payload bytes: extension_type EXT_FILL, fill_nibble, then 0xA5 bytes."""
    bits.put(6, 3)
    if count < 15:
        bits.put(count, 4)
    else:
        bits.put(15, 4).put(count - 14, 8)
    if count:
        bits.put(0, 4).put(0, 4)
        for _ in range(count - 1):
            bits.put(0xA5, 8)


def raw_data_block(layout, padding=0, zero_bands=0):
    """layout: "mono", "stereo", "5.1" (SCE, CPE, CPE, LFE) or "7.1" (SCE, CPE, CPE, CPE, LFE). padding: fill bytes after the channels."""
    bits = Bits()
    if layout == "mono":
        sce(bits, zero_bands=zero_bands)
    elif layout == "stereo":
        cpe(bits)
    elif layout == "5.1":
        sce(bits)
        cpe(bits, 0)
        cpe(bits, 1)
        sce(bits, element=3)
    elif layout == "7.1":
        sce(bits)
        cpe(bits, 0)
        cpe(bits, 1)
        cpe(bits, 2)
        sce(bits, element=3)
    if padding:
        fill(bits, padding)
    bits.put(7, 3)
    return bits.align().bytes()


def units(layout, count, step=37, spread=40, zero_bands=0):
    """count access units whose fill elements vary their lengths."""
    return [raw_data_block(layout, (i * step) % spread, zero_bands) for i in range(count)]


def object_type(bits, aot):
    if aot >= 32:
        bits.put(31, 5).put(aot - 32, 6)
    else:
        bits.put(aot, 5)


def frequency(bits, rate):
    if rate in SAMPLE_RATES:
        bits.put(rate_index(rate), 4)
    else:
        bits.put(15, 4).put(rate, 24)


def asc(aot=2, rate=44100, channels=2, sbr=None, ps=None, signaling=None, extension_rate=None, pce=None):
    """An AudioSpecificConfig (1.6.2.1): signaling None, "hierarchical" or "backward"; sbr and ps the flags
    the backward-compatible extension carries."""
    bits = Bits()
    if signaling == "hierarchical":
        object_type(bits, 29 if ps else 5)
        frequency(bits, rate)
        bits.put(channels, 4)
        frequency(bits, extension_rate)
        object_type(bits, aot)
    else:
        object_type(bits, aot)
        frequency(bits, rate)
        bits.put(channels, 4)
    # GASpecificConfig: frameLengthFlag, dependsOnCoreCoder, extensionFlag.
    bits.put(0, 1).put(0, 1).put(0, 1)
    if channels == 0:
        pce(bits)
    if signaling == "backward":
        bits.put(0x2B7, 11)
        object_type(bits, 5)
        bits.put(1 if sbr else 0, 1)
        if sbr:
            frequency(bits, extension_rate)
            if ps is not None:
                bits.put(0x548, 11).put(1 if ps else 0, 1)
    return bits.align().bytes()


def pce_5_1(bits):
    """program_config_element (4.4.1.1): center SCE and left/right CPE at the front, a CPE behind, an LFE."""
    start = bits.count
    bits.put(0, 4).put(1, 2).put(rate_index(48000), 4)
    bits.put(2, 4).put(0, 4).put(1, 4).put(1, 2).put(0, 3).put(0, 4)
    bits.put(0, 1).put(0, 1).put(0, 1)
    bits.put(0, 1).put(0, 4)  # front: SCE 0
    bits.put(1, 1).put(0, 4)  # front: CPE 0
    bits.put(1, 1).put(1, 4)  # back: CPE 1
    bits.put(0, 4)  # LFE 0
    # byte_alignment() counts from the start of the AudioSpecificConfig, which is byte-aligned.
    bits.align()
    bits.put(0, 8)
    return start


# --- Boxes ---------------------------------------------------------------------------------------


class Box:
    def __init__(self, kind, payload=b"", children=(), full=None, large=False, size=None, uuid=None, after=b""):
        self.kind = kind if isinstance(kind, bytes) else kind.encode("latin-1")
        self.payload = payload
        self.children = list(children)
        self.full = full
        self.large = large
        self.size = size
        self.uuid = uuid
        self.after = after

    def body(self, offset, path, out):
        header = 16 if self.large else 8
        if self.uuid:
            header += 16
        prefix = b""
        if self.full is not None:
            version, flags = self.full
            prefix = bytes([version]) + flags.to_bytes(3, "big")
        data = prefix + self.payload
        position = offset + header + len(data)
        record = len(out)
        out.append(None)
        for child in self.children:
            chunk = child.write(position, path + [self.kind.decode("latin-1")], out)
            data += chunk
            position += len(chunk)
        data += self.after
        size = header + len(data) if self.size is None else self.size
        out[record] = ("/".join(path + [self.kind.decode("latin-1")]), offset, header + len(data))
        if self.large:
            head = struct.pack(">I4sQ", 1, self.kind, size)
        else:
            head = struct.pack(">I4s", size, self.kind)
        if self.uuid:
            head += self.uuid
        return head + data

    def write(self, offset, path, out):
        return self.body(offset, path, out)


def write(boxes, offset=0):
    """The file's bytes and every box as (path, offset, size)."""
    out = []
    data = b""
    for box in boxes:
        data += box.write(offset + len(data), [], out)
    return data, out


def box(kind, payload=b"", *children, **options):
    return Box(kind, payload, children, **options)


def fullbox(kind, version, flags, payload=b"", *children, **options):
    return Box(kind, payload, children, full=(version, flags), **options)


def u8(v):
    return struct.pack(">B", v)


def u16(v):
    return struct.pack(">H", v)


def u32(v):
    return struct.pack(">I", v & 0xFFFFFFFF)


def u64(v):
    return struct.pack(">Q", v)


def ftyp(major=b"M4A ", minor=0, brands=(b"M4A ", b"mp42", b"isom")):
    return box("ftyp", major + u32(minor) + b"".join(brands))


def mvhd(timescale, duration, version=0, next_track=2):
    times = (u64(0) + u64(0) + u32(timescale) + u64(duration)) if version else (u32(0) + u32(0) + u32(timescale) + u32(duration))
    matrix = u32(0x10000) + u32(0) + u32(0) + u32(0) + u32(0x10000) + u32(0) + u32(0) + u32(0) + u32(0x40000000)
    return fullbox("mvhd", version, 0, times + u32(0x10000) + u16(0x100) + bytes(10) + matrix + bytes(24) + u32(next_track))


def tkhd(track, duration, flags=3):
    matrix = u32(0x10000) + u32(0) + u32(0) + u32(0) + u32(0x10000) + u32(0) + u32(0) + u32(0) + u32(0x40000000)
    return fullbox("tkhd", 0, flags, u32(0) + u32(0) + u32(track) + u32(0) + u32(duration) + bytes(8) + u16(0) + u16(1) + u16(0x100) + u16(0) + matrix + u32(0) + u32(0))


def mdhd(timescale, duration, language=b"und"):
    packed = ((language[0] - 0x60) << 10) | ((language[1] - 0x60) << 5) | (language[2] - 0x60)
    return fullbox("mdhd", 0, 0, u32(0) + u32(0) + u32(timescale) + u32(duration) + u16(packed) + u16(0))


def hdlr(handler, name=b"SoundHandler", component=b"\0\0\0\0", pascal=False):
    text = (bytes([len(name)]) + name) if pascal else name + b"\0"
    return fullbox("hdlr", 0, 0, component + handler + bytes(12) + text)


def esds(object_type_indication, config, average=0, maximum=0, buffer=0):
    def descriptor(tag, body):
        return bytes([tag, 0x80, 0x80, 0x80, len(body)]) + body

    dsi = descriptor(5, config) if config is not None else b""
    decoder = descriptor(4, bytes([object_type_indication, 0x15]) + buffer.to_bytes(3, "big") + u32(maximum) + u32(average) + dsi)
    es = descriptor(3, u16(1) + u8(0) + decoder + descriptor(6, b"\x02"))
    return fullbox("esds", 0, 0, es)


def mp4a(channels, rate, *children, version=0, extra=b"", format=b"mp4a"):
    rate_field = u32(rate << 16) if rate < 65536 else u32(0)
    fields = bytes(6) + u16(1) + u16(version) + u16(0) + u32(0) + u16(channels) + u16(16) + u16(0) + u16(0) + rate_field + extra
    return box(format, fields, *children)


def chan(tag, bitmap=0, descriptions=()):
    """QuickTime's channel layout atom: Core Audio's AudioChannelLayout, big-endian (a layout tag, a channel
    bitmap and AudioChannelDescriptions of label, flags and three 32-bit float coordinates)."""
    body = u32(tag) + u32(bitmap) + u32(len(descriptions))
    for label, flags, coordinates in descriptions:
        body += u32(label) + u32(flags) + b"".join(struct.pack(">f", c) for c in coordinates)
    return fullbox("chan", 0, 0, body)


def chnl(defined=0, positions=(), omitted=0, objects=None):
    """ISO/IEC 14496-12's channel layout box, version 0: channels as a defined layout with its omitted
    channels, or a speaker position per channel (126 followed by azimuth and elevation), and an object count
    where objects is set."""
    structure = 1 | (2 if objects is not None else 0)
    body = u8(structure) + u8(defined)
    if defined:
        body += u64(omitted)
    else:
        for position in positions:
            if isinstance(position, tuple):
                body += u8(126) + struct.pack(">hb", *position)
            else:
                body += u8(position)
    if objects is not None:
        body += u8(objects)
    return fullbox("chnl", 0, 0, body)


def stsd(*entries, version=0):
    return fullbox("stsd", version, 0, u32(len(entries)), *entries)


def stts(entries):
    return fullbox("stts", 0, 0, u32(len(entries)) + b"".join(u32(c) + u32(d) for c, d in entries))


def stsc(entries):
    return fullbox("stsc", 0, 0, u32(len(entries)) + b"".join(u32(f) + u32(s) + u32(d) for f, s, d in entries))


def stsz(sizes, uniform=0, count=None):
    count = len(sizes) if count is None else count
    return fullbox("stsz", 0, 0, u32(uniform) + u32(count) + (b"" if uniform else b"".join(u32(s) for s in sizes)))


def stco(offsets, wide=False):
    if wide:
        return fullbox("co64", 0, 0, u32(len(offsets)) + b"".join(u64(o) for o in offsets))
    return fullbox("stco", 0, 0, u32(len(offsets)) + b"".join(u32(o) for o in offsets))


def elst(edits, version=0):
    if version:
        body = b"".join(u64(d) + struct.pack(">q", t) + u32(0x10000) for d, t in edits)
    else:
        body = b"".join(u32(d) + struct.pack(">i", t) + u32(0x10000) for d, t in edits)
    return box("edts", b"", fullbox("elst", version, 0, u32(len(edits)) + body))


def sgpd_roll(distance, grouping=b"roll"):
    return fullbox("sgpd", 1, 0, grouping + u32(2) + u32(1) + struct.pack(">h", distance))


def sbgp(count, grouping=b"roll"):
    return fullbox("sbgp", 0, 0, grouping + u32(1) + u32(count) + u32(1))


def dinf():
    return box("dinf", b"", fullbox("dref", 0, 0, u32(1), fullbox("url ", 0, 1)))


def data_atom(kind, value, locale=0):
    return box("data", u32(kind) + u32(locale) + value)


def item(kind, *atoms):
    return box(kind, b"", *atoms)


def text_item(kind, text):
    return item(kind, data_atom(1, text.encode("utf-8")))


def freeform(name, value, kind=1, mean=b"com.apple.iTunes"):
    return box("----", b"", fullbox("mean", 0, 0, mean), fullbox("name", 0, 0, name.encode("ascii")), data_atom(kind, value))


def meta_hdlr(handler=b"mdir"):
    # Apple writes "appl" into the first reserved word of the metadata handler.
    return fullbox("hdlr", 0, 0, u32(0) + handler + b"appl" + bytes(8) + b"\0")


def ilst_meta(*items, handler=b"mdir"):
    return fullbox("meta", 0, 0, b"", meta_hdlr(handler), box("ilst", b"", *items))


def udta(*children):
    return box("udta", b"", *children)


class Track:
    """An audio track whose samples are laid out in chunks of chunk samples."""

    def __init__(self, track_id, samples, rate, channels, sample_entry, timescale=None, chunk=10, extra=(), edits=None, mdhd_duration=None, stts_entries=None,
                 stsz_box=None, stsc_box=None, durations=None):
        self.track_id = track_id
        self.samples = samples
        self.rate = rate
        self.channels = channels
        self.sample_entry = sample_entry
        self.timescale = timescale or rate
        self.chunk = chunk
        self.extra = list(extra)
        self.edits = edits
        self.mdhd_duration = mdhd_duration
        self.stts_entries = stts_entries
        self.stsz_box = stsz_box
        self.stsc_box = stsc_box
        self.durations = durations or [1024] * len(samples)

    def chunks(self):
        return [self.samples[i:i + self.chunk] for i in range(0, len(self.samples), self.chunk)]

    def total(self):
        return sum(self.durations)

    def trak(self, chunk_offsets, movie_timescale, wide=False, stco_box=None):
        duration = self.mdhd_duration if self.mdhd_duration is not None else self.total()
        presented = duration * movie_timescale // self.timescale
        if self.edits:
            presented = sum(d for d, _ in self.edits)
        runs = []
        for index, chunk in enumerate(self.chunks()):
            if not runs or runs[-1][1] != len(chunk):
                runs.append((index + 1, len(chunk), 1))
        entries = self.stts_entries
        if entries is None:
            entries = []
            for d in self.durations:
                if entries and entries[-1][1] == d:
                    entries[-1] = (entries[-1][0] + 1, d)
                else:
                    entries.append((1, d))
        children = [tkhd(self.track_id, presented)]
        if self.edits:
            children.append(elst(self.edits))
        table = [
            stsd(self.sample_entry),
            stts(entries),
            self.stsc_box or stsc(runs),
            self.stsz_box or stsz([len(s) for s in self.samples]),
            stco_box or stco(chunk_offsets, wide),
        ] + self.extra
        children.append(box("mdia", b"", mdhd(self.timescale, duration), hdlr(b"soun"), box("minf", b"", fullbox("smhd", 0, 0, bytes(4)), dinf(), box("stbl", b"", *table))))
        return box("trak", b"", *children)


def interleave(tracks):
    """The mdat payload and each track's chunk offsets relative to it, the chunks of the tracks in turn."""
    payload = b""
    offsets = [[] for _ in tracks]
    chunk_lists = [t.chunks() for t in tracks]
    for index in range(max(len(c) for c in chunk_lists)):
        for number, chunks in enumerate(chunk_lists):
            if index < len(chunks):
                offsets[number].append(len(payload))
                payload += b"".join(chunks[index])
    return payload, offsets


def movie(tracks, before=(), after=(), movie_timescale=1000, moov_first=True, wide=False, large_mdat=False, moov_extra=(), mdat_extra=b"", ftyp_box=None,
          stco_boxes=None, trailer=b""):
    """A file of ftyp, moov and mdat (in either order), with the tracks' chunks interleaved in mdat."""
    payload, relative = interleave(tracks)
    payload += mdat_extra

    def build(base):
        traks = [t.trak([base + o for o in offs], movie_timescale, wide, None if stco_boxes is None else stco_boxes[i]) for i, (t, offs) in enumerate(zip(tracks, relative))]
        durations = [t.edits and sum(d for d, _ in t.edits) or (t.mdhd_duration if t.mdhd_duration is not None else t.total()) * movie_timescale // t.timescale for t in tracks]
        moov = box("moov", b"", mvhd(movie_timescale, max(durations) if durations else 0, next_track=len(tracks) + 1), *traks, *moov_extra)
        mdat = box("mdat", payload, large=large_mdat)
        head = [ftyp_box or ftyp()] + list(before)
        return head + ([moov, mdat] if moov_first else [mdat, moov]) + list(after)

    data, boxes = write(build(0))
    mdat_start = next(offset for path, offset, size in boxes if path == "mdat") + (16 if large_mdat else 8)
    data, boxes = write(build(mdat_start))
    return data + trailer, boxes, [[mdat_start + o for o in offs] for offs in relative]


# --- Protection: sinf, Common Encryption, FairPlay and Audible ----------------------------------------


def frma(original):
    return box("frma", original)


def schm(scheme, version=0x10000, uri=None):
    """ISO/IEC 14496-12 8.12.5: the scheme type, its version and, with flag 1, a URI."""
    return fullbox("schm", 0, 1 if uri else 0, scheme + u32(version) + (uri + b"\0" if uri else b""))


def tenc(is_protected=1, iv_size=8, kid=bytes(range(0x10, 0x20)), version=0, crypt=0, skip=0, constant_iv=None):
    """ISO/IEC 23001-7's track encryption box: a reserved byte, version 0's second reserved byte or version 1's
    crypt and skip blocks, then the default IsProtected, per-sample IV size and KID, and a constant IV where
    protected samples carry none of their own (GPAC's tenc_box_read and FFmpeg's mov_read_tenc read the same)."""
    body = u8(0) + (u8(0) if version == 0 else u8((crypt << 4) | skip)) + u8(is_protected) + u8(iv_size) + kid
    if constant_iv is not None:
        body += u8(len(constant_iv)) + constant_iv
    return fullbox("tenc", version, 0, body)


def schi(*children):
    return box("schi", b"", *children)


def sinf(*children):
    return box("sinf", b"", *children)


def pssh(system_id, data=b"", kids=None):
    """ISO/IEC 23001-7's protection system specific header: version 1 lists the key IDs the data covers."""
    body = bytes.fromhex(system_id.replace("-", ""))
    if kids is not None:
        body += u32(len(kids)) + b"".join(kids)
    return fullbox("pssh", 0 if kids is None else 1, 0, body + u32(len(data)) + data)


def senc(count, entries=b"", subsamples=False):
    return fullbox("senc", 0, 2 if subsamples else 0, u32(count) + entries)


def saiz(default_size, count, sizes=b"", aux_type=None):
    head = (aux_type + u32(0)) if aux_type else b""
    return fullbox("saiz", 0, 1 if aux_type else 0, head + u8(default_size) + u32(count) + sizes)


def saio(offsets, version=0, aux_type=None):
    head = (aux_type + u32(0)) if aux_type else b""
    pack = u64 if version else u32
    return fullbox("saio", version, 1 if aux_type else 0, head + u32(len(offsets)) + b"".join(pack(o) for o in offsets))


def seig_entry(is_protected, iv_size, kid, crypt=0, skip=0, constant_iv=None):
    body = u8(0) + u8((crypt << 4) | skip) + u8(is_protected) + u8(iv_size) + kid
    if constant_iv is not None:
        body += u8(len(constant_iv)) + constant_iv
    return body


def sgpd_seig(entries):
    """A sample group description of grouping type seig (version 1, each entry with its length)."""
    return fullbox("sgpd", 1, 0, b"seig" + u32(0) + u32(len(entries)) + b"".join(u32(len(e)) + e for e in entries))


def sbgp_seig(runs):
    return fullbox("sbgp", 0, 0, b"seig" + u32(len(runs)) + b"".join(u32(c) + u32(i) for c, i in runs))
