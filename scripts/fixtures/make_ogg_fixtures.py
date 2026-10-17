#!/usr/bin/env python3
"""Writes the byte-exact Ogg and Speex fixtures in tests/fixtures/ogg.

Pages, Speex headers, Vorbis comments and Speex frames are built by ogg_model.py: frames carry real
submode bits and zero coded data, which libspeex decodes without complaint, so the page layout,
lacing, granule positions and frame modes are fully under control. Each fixture is a file plus a
<name>.expected.json manifest:

- "length" and "regions": the file's top-level nodes (tags and one "Ogg" region per stretch of data
  between tags) as name, offset and length.
- "pages": every page and gap of the Ogg data in file order, with each page's serial number,
  sequence number, granule position, header type flags and whether its CRC holds.
- "streams": each logical stream as the analyzer shows it: serial number, codec, chain link, page
  and packet counts, whether it ends with an end-of-stream page, and for Speex the header fields,
  the comment, every audio packet's offset and length (with its file ranges where it spans pages),
  every frame's submodes, the statistics and the encoder (ogg_model.speex_expectations), and the
  samples speexdec outputs (ogg_model.speexdec_samples).
- "stream_infos": the figures each stream reports to the summary and the CSV, with its playback group (the chain link).
- "findings": the findings the analyzer must produce, as a code plus a string the message has to
  contain; no others may appear.
- "crosscheck": what crosscheck_ogg_fixtures.py checks with other tools: "valid" fixtures must pass
  oggz validate and match oggz dump's packets, ffprobe's packets and duration, ogginfo's header
  fields and speexdec's decoded samples and per-frame bitrates; others list the tools that must
  still agree, and the oggz validate errors they must produce.

The script uses only the standard library and is deterministic, so CI reruns it and fails when the
committed files differ.
"""

import json
import pathlib
import struct

import ogg_model as m

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "ogg"

VENDOR = b"Encoded with Speex 1.2.1"

# Expectations of other codecs by name (make_vorbis_opus_fixtures.py fills it): a function of the
# stream's entry, the file's bytes, its packets, its pages with the packets that end on them, and the
# stream's spec, which returns the entry and the stream's figures for the summary.
CODECS = {}
RATES = [8000, 16000, 32000]


# --- Streams ------------------------------------------------------------------------------------


def speex_packets(mode, audio, nframes=1, channels=1, comments=(), vendor=VENDOR, end_samples=None, extra_headers=(), **header):
    """The packets of a Speex stream and their granule positions as speexenc writes them: 0 on the
    headers, then each audio packet's samples less the encoder's lookahead, and end_samples on the
    last (the input's length, which decoders trim to)."""
    rate = header.pop("rate", RATES[mode])
    head = m.speex_header(rate, mode, channels=channels, frames_per_packet=nframes, extra_headers=len(extra_headers), **header)
    packets = [head, m.vorbis_comment(vendor, list(comments)), *extra_headers] + [m.speex_packet(frames) for frames in audio]
    per_packet = max(1, nframes) * (160 << mode)
    granules = [0] * (2 + len(extra_headers)) + [(i + 1) * per_packet - m.ENCODER_LOOKAHEAD[mode] for i in range(len(audio))]
    if end_samples is not None:
        granules[-1] = end_samples
    return packets, granules


def default_cuts(count_audio, per_page=20, extra=0):
    """speexenc's layout: the header and the comment on pages of their own, then the audio."""
    return [1, 1] + [1] * extra + [per_page] * (count_audio // per_page + 1)


def cbr(count, nb, wb=None, uwb=None):
    return [[(nb, wb, uwb)] for _ in range(count)]


class Fixture:
    def __init__(self, name, extension=".spx"):
        self.name, self.extension = name, extension
        self.w = m.Writer()
        self.streams = []
        self.findings = []
        self.crosscheck = {"valid": True}
        self.regions_before = []
        self.regions_after = []
        self.ogg_start = 0

    def stream(self, serial, pages, link=1, codec="Speex", analyzed=True, lost=(), extra_headers=0, encoder=None, writer=None, window=None):
        # window: the file offsets (from, to) the stream's pages lie in, where a serial number is reused in another link.
        self.streams.append({"serial": serial, "pages": pages, "link": link, "codec": codec, "analyzed": analyzed, "lost": set(lost),
                             "extra_headers": extra_headers, "encoder": encoder, "writer": writer, "window": window})

    def finding(self, code, contains):
        self.findings.append({"code": code, "contains": contains})

    def write(self):
        data = bytes(self.w.data)
        ogg_end = len(data) - sum(length for _, length in self.regions_after)
        regions = [{"name": n, "offset": o, "length": l} for n, o, l in self.regions_before]
        regions.append({"name": "Ogg", "offset": self.ogg_start, "length": ogg_end - self.ogg_start})
        position = ogg_end
        for name, length in self.regions_after:
            regions.append({"name": name, "offset": position, "length": length})
            position += length

        streams, infos = [], []
        for s in self.streams:
            entry, info = expect_stream(data, self.w.records, s)
            streams.append(entry)
            if info:
                infos.append(info)
        manifest = {
            "length": len(data), "regions": regions,
            "pages": [{k: v for k, v in r.items()} for r in self.w.records],
            "streams": streams, "stream_infos": infos, "findings": self.findings, "crosscheck": self.crosscheck,
        }
        (OUT / (self.name + self.extension)).write_bytes(data)
        (OUT / (self.name + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def expect_stream(data, records, s):
    """A stream's expectations from the pages written for it, put together as the analyzer does:
    pages in s["lost"] (by sequence number) are dropped with the packet they cut, as are pages
    after a sequence gap."""
    serial = s["serial"]
    low, high = s.get("window") or (0, float("inf"))
    pages = [r for r in records if r.get("name") == "Page" and r["serial"] == serial and not r.get("foreign") and low <= r["offset"] < high]
    packets, page_ends, pending, last_sequence, gap = [], [], None, None, False
    for r in pages:
        offset = r["offset"]
        dropped = r["sequence"] in s["lost"] or not r["crc_ok"] or r.get("truncated")
        if last_sequence is not None and r["sequence"] != (last_sequence + 1) & 0xFFFFFFFF:
            pending, gap = None, True
        last_sequence = r["sequence"]
        if dropped:
            pending, gap = None, True
            page_ends.append((r, [], True))
            continue
        count = data[offset + 26]
        lacing = data[offset + 27:offset + 27 + count]
        at = offset + 27 + count
        ends = []
        index = 0
        if r["flags"] & m.CONTINUED and pending is None and count:
            while index < count:
                value = lacing[index]
                index += 1
                at += value
                if value < 255:
                    break
            gap = True
        elif not r["flags"] & m.CONTINUED and pending is not None:
            pending, gap = None, True
        while index < count:
            value = lacing[index]
            index += 1
            if pending is None:
                pending = (bytearray(), [])
            pending[0].extend(data[at:at + value])
            if value:
                pending[1].append((at, value))
            at += value
            if value < 255:
                buffer, ranges = pending
                merged = []
                for o, l in ranges:
                    if merged and merged[-1][0] + merged[-1][1] == o:
                        merged[-1] = (merged[-1][0], merged[-1][1] + l)
                    else:
                        merged.append((o, l))
                packets.append({"offset": merged[0][0] if merged else at, "data": bytes(buffer), "ranges": merged, "gap": gap})
                ends.append(len(packets) - 1)
                pending, gap = None, False
        page_ends.append((r, ends, pending is None))

    # A damaged page's end-of-stream flag never reaches a decoder.
    entry = {
        "serial": f"0x{serial:08X}", "codec": s["codec"], "link": s["link"], "pages": len(pages), "packets": len(packets),
        "has_eos": any(r["flags"] & m.EOS and r["crc_ok"] and not r.get("truncated") for r in pages),
    }
    entry, info = expect_codec(entry, data, packets, page_ends, s)
    # The streams of one link play at the same time and form a playback group, which the summary counts once.
    if info:
        info["playback_group"] = s["link"]
    return entry, info


def expect_codec(entry, data, packets, page_ends, s):
    if s["codec"] in CODECS and s["analyzed"] and packets:
        return CODECS[s["codec"]](entry, data, packets, page_ends, s)
    if s["codec"] != "Speex" or not s["analyzed"] or not packets:
        return entry, None

    header = packets[0]["data"]
    first_audio = 2 + s["extra_headers"]
    audio = packets[first_audio:]
    positions = []
    for r, ends, clean in page_ends:
        if ends and r["granule"] != -1 and ends[-1] >= first_audio:
            is_end = bool(r["flags"] & m.EOS) and clean
            positions.append((ends[-1] - first_audio, r["granule"], is_end))
    gaps = [i - first_audio for i, p in enumerate(packets) if p["gap"] and i >= first_audio]
    speex = m.speex_expectations(header, [(p["offset"], p["data"], p["ranges"]) for p in audio], positions, gaps)
    entry["speex"] = {"header": m.speex_header_fields(header), **{k: v for k, v in speex.items() if k != "stream_info"}}
    if len(packets) > 1:
        entry["speex"]["comment"] = m.read_comment(packets[1]["data"])
    if len(header) >= 80 and 0 <= struct.unpack_from("<i", header, 40)[0] <= 2:
        frames = [None] * first_audio + [len(f) for f in speex["frames"]]
        entry["speex"]["speexdec_samples"] = m.speexdec_samples(
            [(r["granule"], len(ends), bool(r["flags"] & m.EOS)) for r, ends, _ in page_ends if ends], frames, header)
    return entry, speex["stream_info"]


# --- Fixtures ----------------------------------------------------------------------------------


def single(name, mode, audio, end_samples=None, per_page=20, **kwargs):
    f = Fixture(name)
    packets, granules = speex_packets(mode, audio, end_samples=end_samples, **kwargs)
    pages = m.pages_for(0x5EED0000 + mode, packets, granules, default_cuts(len(audio), per_page))
    for p in pages:
        f.w.write(p)
    f.stream(0x5EED0000 + mode, pages)
    return f


def narrowband():
    # 1 s of narrowband: submode 5 (15 kbps) as speexenc --quality 8 writes it, and a last granule
    # position 60 samples before the end of the last packet, which decoders trim.
    f = single("speex-narrowband", 0, cbr(50, 5), end_samples=50 * 160 - 40 - 60)
    f.write()


def wideband():
    # Every narrowband submode under every wideband submode, and frames without a wideband layer,
    # as VBR encodes switch between them.
    audio = [[(nb, wb)] for nb in range(9) for wb in (None, 0, 1, 2, 3, 4)]
    f = single("speex-wideband", 1, audio, vbr=1, per_page=17)
    f.write()


def ultra_wideband():
    # A frame without a wideband layer has no ultra-wideband one either: the decoder would take the
    # ultra-wideband layer's marker for the wideband one's.
    audio = [[(nb, wb, uwb)] for nb in (2, 4, 6) for wb in (None, 1, 3) for uwb in (None, 0, 1) if wb is not None or uwb is None]
    f = single("speex-ultra-wideband", 2, audio * 2, end_samples=42 * 640 - 349 - 500)
    f.write()


def frames_and_stereo():
    # Three frames per packet, stereo: each frame starts with the in-band stereo parameters
    # (request 9, 8 bits), as speex_encode_stereo writes them, and one frame carries a second
    # in-band request (ID 2, a mode request of 4 bits).
    frame = (4, 2, None, (9,))
    audio = [[frame, frame, frame] for _ in range(20)]
    audio[5] = [frame, (4, 2, None, (9, 2)), frame]
    f = single("speex-stereo-nframes", 1, audio, nframes=3, channels=2, per_page=8)
    f.write()


def multiplexed():
    # Two Speex streams in one link: both beginning-of-stream pages first, then their pages
    # interleaved in time order, as a muxer of two voices writes them.
    f = Fixture("multiplexed", ".ogg")
    a_packets, a_granules = speex_packets(0, cbr(30, 3))
    b_packets, b_granules = speex_packets(1, cbr(30, 5, 2), comments=[b"TITLE=Second voice"])
    a = m.pages_for(0x0000A001, a_packets, a_granules, [1, 1, 10, 10, 10])
    b = m.pages_for(0x0000B002, b_packets, b_granules, [1, 1, 10, 10, 10])
    for p in [a[0], b[0], a[1], b[1], b[2], a[2], b[3], a[3], b[4], a[4]]:
        f.w.write(p)
    f.stream(0x0000A001, a)
    f.stream(0x0000B002, b)
    f.write()


def chained():
    # Two links: a narrowband stream ends, then a wideband one begins, as two files joined end to
    # end; the second reuses no serial number.
    f = Fixture("chained", ".ogg")
    a_packets, a_granules = speex_packets(0, cbr(25, 4), end_samples=25 * 160 - 40 - 10)
    b_packets, b_granules = speex_packets(1, cbr(25, 6, 3), vendor=b"Lavf60.16.100", comments=[b"encoder=Lavc60.31.102 libspeex"])
    for p in m.pages_for(0x11111111, a_packets, a_granules, default_cuts(25)) + m.pages_for(0x22222222, b_packets, b_granules, default_cuts(25)):
        f.w.write(p)
    f.stream(0x11111111, None, link=1)
    f.stream(0x22222222, None, link=2)
    f.write()


def packet_spanning_pages():
    # A comment of 1,310 bytes spread over three pages, and audio packets of five frames, 348 bytes
    # in two segments, which page boundaries split inside their fourth frame, so its fields
    # straddle a page header.
    f = Fixture("packet-spanning-pages")
    comments = [b"COMMENT=" + bytes(range(65, 91)) * 48, b"TITLE=Spanning"]
    packets, granules = speex_packets(1, [[(6, 3)] * 5] * 6, nframes=5, comments=comments)
    # The header alone; the comment's six segments two to a page; then the audio's twelve segments
    # three, four and five to a page.
    pages = m.pages_for(0x0DD0BEEF, packets, granules, [1, 2, 2, 2, 3, 4, 5])
    for p in pages:
        f.w.write(p)
    f.stream(0x0DD0BEEF, pages)
    f.write()


def lacing_255():
    # Packets of exactly 255 and 510 bytes end with a lacing value of 0; one 255-byte packet's 0
    # falls on the next page, which continues it with nothing but the terminating 0; a packet of
    # 0 bytes; and frames padded to 255 bytes with user in-band data.
    f = Fixture("lacing-255", ".ogg")
    head = m.speex_header(8000, 0)
    comment = m.vorbis_comment(VENDOR, [])

    def padded(size):
        # The frame and its terminator, then bytes of 0xFF, which read as another terminator.
        packet = m.speex_packet([(5,)])
        return packet + b"\xff" * (size - len(packet))

    audio = [padded(255), padded(510), padded(255), m.speex_packet([(5,)]), padded(255), m.speex_packet([(5,)])]
    packets = [head, comment] + audio
    granules = [0, 0] + [(i + 1) * 160 - 40 for i in range(len(audio))]
    # Pages: header; comment; [255+0, 255+255+0, 255] ; [0 (continues), 38, 255+0, 38].
    pages = m.pages_for(0x00255255, packets, granules, [1, 1, 6, 5])
    for p in pages:
        f.w.write(p)
    f.stream(0x00255255, pages)
    f.write()


def comment_edge_cases():
    # Valid but unusual comments: an empty value, an '=' inside the value, a field name in lower
    # case and one repeated, multibyte UTF-8 in the vendor and a value, an empty vendor string is
    # in the next fixture; and an ENCODER field.
    f = Fixture("comment-edge-cases")
    comments = [b"ARTIST=", b"TITLE=a=b=c", b"title=lower case", b"ARTIST=Second", "ALBUM=Ünïcödé ✓ 日本".encode(), b"ENCODER=Custom tool 2"]
    packets, granules = speex_packets(0, cbr(10, 3), comments=comments, vendor="Encoded with Speex 1.2.1 ✓".encode())
    pages = m.pages_for(0x00C0FFEE, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    f.stream(0x00C0FFEE, pages)
    f.write()


def writer_claims_differ():
    # Three ENCODER fields that name different software, the last LAME, which writes MPEG audio: the writer is the first, and the second is a finding of its own role.
    f = Fixture("writer-claims-differ")
    packets, granules = speex_packets(0, cbr(10, 3), comments=[b"ENCODER=Foo Muxer 1.0", b"ENCODER=Bar Tool 2.0", b"ENCODER=LAME 3.100"])
    pages = m.pages_for(0x00C0FFEF, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    f.stream(0x00C0FFEF, pages)
    f.finding("speex.writer-claim-differs", "ENCODER field names \"Bar Tool 2.0\"")
    f.finding("speex.writer-other-format", "which writes MPEG audio, not Speex")
    f.write()


def encoder_other_format():
    # A libavcodec encoder ID of another codec in the ENCODER field never becomes the encoder: speexenc's vendor string names it (speexenc 1.2.1,
    # high confidence), the ID stays as evidence under it, and a finding says it is not Speex's.
    f = Fixture("encoder-other-format")
    packets, granules = speex_packets(0, cbr(10, 3), comments=[b"ENCODER=Lavc60.31.102 libopus"])
    pages = m.pages_for(0x00C0FFF0, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    f.stream(0x00C0FFF0, pages)
    f.finding("speex.encoder-other-format", "ENCODER field names \"Lavc60.31.102 libopus\"")
    f.write()


def encoder_other_format_no_vendor():
    # The same ID with a vendor string that names no encoder: only the header's version names one (libspeex 1.2.1, low confidence), and the ID stays evidence under it.
    f = Fixture("encoder-other-format-no-vendor")
    packets, granules = speex_packets(0, cbr(10, 3), comments=[b"ENCODER=Lavc60.31.102 libopus"], vendor=b"Foo Tool")
    pages = m.pages_for(0x00C0FFF1, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    f.stream(0x00C0FFF1, pages)
    f.finding("speex.encoder-other-format", "ENCODER field names \"Lavc60.31.102 libopus\"")
    f.write()


def comment_empty():
    f = Fixture("comment-empty")
    packets, granules = speex_packets(0, cbr(5, 1), vendor=b"")
    pages = m.pages_for(0x0000E000, packets, granules, default_cuts(5))
    for p in pages:
        f.w.write(p)
    f.stream(0x0000E000, pages)
    f.write()


def unknown_codec():
    # A stream whose first packet names no Ogg mapping, multiplexed with a Speex stream, and a
    # Theora identification header that is recognized but not analyzed, whose beginning-of-stream
    # page comes first, as RFC 3533's grouping wants for video.
    f = Fixture("unknown-codec", ".ogg")
    speex, granules = speex_packets(0, cbr(10, 2))
    unknown = m.pages_for(0x0BADC0DE, [b"XYZcodec header", b"payload one", b"payload two"], [0, 0, 0], [1, 2])
    theora = m.pages_for(0x00F0F0F0, [b"\x80theora" + bytes(35), b"\x81theora" + bytes(8), b"\x82theora"], [0, 0, 0], [1, 2])
    s = m.pages_for(0x5EED0000, speex, granules, default_cuts(10))
    for p in [theora[0], s[0], unknown[0], s[1], unknown[1], theora[1], s[2]]:
        f.w.write(p)
    f.stream(0x00F0F0F0, theora, codec="Theora", analyzed=False)
    f.stream(0x5EED0000, s)
    f.stream(0x0BADC0DE, unknown, codec="Unknown")
    f.finding("ogg.codec-unknown", "0x0BADC0DE starts with bytes no Ogg mapping is known for (58595A636F646563)")
    f.finding("ogg.codec-not-analyzed", "0x00F0F0F0 holds Theora")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "not_compared": {
        "oggz info and dump": "oggz 1.1.3 miscounts every stream of a file that holds a stream whose codec it does not know (it lists 2 of the Speex stream's 12 packets here)",
        "ogginfo, ffprobe and speexdec": "they read only the Speex stream, whose figures the valid fixtures compare, and say nothing about the others"}}
    f.write()


def tags_around():
    # An ID3v2 tag before the Ogg data and an APE tag and an ID3v1 tag after it, as taggers that do
    # not know Ogg write them.
    f = Fixture("tags-around")
    id3 = b"ID3\x03\x00\x00" + bytes([0, 0, 0, 20]) + b"TIT2" + struct.pack(">I", 6) + b"\x00\x00" + b"\x00Title" + bytes(4)
    f.w.raw(id3)
    f.regions_before.append(("ID3v2", 0, len(id3)))
    f.ogg_start = len(id3)
    packets, granules = speex_packets(0, cbr(10, 3))
    pages = m.pages_for(0x7A657A65, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    item = struct.pack("<II", 5, 0) + b"Title\x00Hello"
    footer = b"APETAGEX" + struct.pack("<IIII", 2000, len(item) + 32, 1, 0) + bytes(8)
    ape = item + footer
    tag = b"TAG" + b"Title".ljust(30, b"\0") + b"Artist".ljust(30, b"\0") + b"Album".ljust(30, b"\0") + b"2026" + bytes(30) + b"\x0c"
    f.w.raw(ape + tag)
    f.regions_after += [("APE", len(ape)), ("ID3v1", 128)]
    f.stream(0x7A657A65, pages)
    f.crosscheck = {"valid": True}
    f.write()


def tag_signature_in_last_page():
    # The last page's data ends with "TAG" 128 bytes before the end of the file, where an ID3v1 tag
    # would start: the page's own length says it runs to the end, so it is no tag.
    f = Fixture("tag-signature-in-last-page")
    # The last packet is a frame, its terminator, a byte of 0xFF that reads as another terminator,
    # and 128 bytes that start with "TAG".
    audio = cbr(10, 5)
    packets, granules = speex_packets(0, audio)
    packets[-1] += b"\xff" + b"TAG" + b"Not a tag".ljust(125, b"\0")
    pages = m.pages_for(0x0000F001, packets, granules, default_cuts(10, per_page=40))
    for p in pages:
        f.w.write(p)
    f.stream(0x0000F001, pages)
    f.finding("tag.near-miss", "rejected: part of the Ogg data")
    f.write()


# --- Corrupt -----------------------------------------------------------------------------------


def bad_crc():
    f = Fixture("bad-crc")
    packets, granules = speex_packets(0, cbr(30, 4))
    pages = m.pages_for(0x00000BAD, packets, granules, [1, 1, 10, 10, 10])
    pages[3].crc = 0x12345678
    for p in pages:
        f.w.write(p)
    f.stream(0x00000BAD, pages)
    f.finding("ogg.crc-mismatch", "1 page of the logical stream 0x00000BAD fails the CRC check")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "oggz validate's verdict": "libogg drops a page that fails its CRC before oggz sees it, so validate passes; oggz dump lacks the page's packets, as the analyzer's packets do, which is compared",
        "speexdec": "speexdec stops its packet loop at the hole libogg reports after the lost page (ogg_stream_packetout returns -1) and reads the next page's packets a page late, which the model of its trimming does not follow",
        "ogginfo and ffprobe": "they do not report a page that fails its CRC"}}
    f.write()


def sequence_gap():
    # A page of ten packets is missing, so the granule positions after it count samples the
    # stream no longer holds.
    f = Fixture("sequence-gap")
    packets, granules = speex_packets(0, cbr(40, 4))
    pages = m.pages_for(0x00006A90, packets, granules, [1, 1, 10, 10, 10, 10])
    for p in pages[:3] + pages[4:]:
        f.w.write(p)
    f.stream(0x00006A90, pages[:3] + pages[4:])
    f.finding("ogg.sequence-gap", "jumps from 2 to 4")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "oggz validate's verdict": "oggz validate does not check page sequence numbers; oggz dump lists the packets of the pages that are there, which is compared",
        "speexdec": "speexdec stops its packet loop at the hole libogg reports for the gap and reads the next page's packets a page late, which the model of its trimming does not follow",
        "ogginfo and ffprobe": "they do not report a gap in the page sequence"}}
    f.write()


def truncated_page():
    f = Fixture("truncated-page")
    packets, granules = speex_packets(1, cbr(30, 5, 2))
    pages = m.pages_for(0x0000C07A, packets, granules, [1, 1, 10, 10, 10])
    for p in pages[:-1]:
        f.w.write(p)
    f.w.write(pages[-1], cut=27 + len(pages[-1].lacing) + 100)
    f.stream(0x0000C07A, pages)
    f.finding("ogg.page-truncated", "is cut short after 137 bytes")
    f.finding("ogg.eos-missing", "")
    f.crosscheck = {"valid": False, "oggz_validate": ["missing *** eos"], "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they do not report a page cut short; libogg never returns it, so oggz dump lacks its packets, as the analyzer's packets do"}}
    f.write()


def missing_eos():
    f = Fixture("missing-eos")
    packets, granules = speex_packets(0, cbr(20, 3))
    pages = m.pages_for(0x0000E0F0, packets, granules, default_cuts(20), eos=False)
    for p in pages:
        f.w.write(p)
    f.stream(0x0000E0F0, pages)
    f.finding("ogg.eos-missing", "the file was probably cut short")
    f.crosscheck = {"valid": False, "oggz_validate": ["missing *** eos"], "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they do not report a missing end-of-stream page"}}
    f.write()


def garbage_between_pages():
    f = Fixture("garbage-between-pages")
    packets, granules = speex_packets(0, cbr(30, 4))
    pages = m.pages_for(0x00006A6A, packets, granules, [1, 1, 10, 10, 10])
    for i, p in enumerate(pages):
        f.w.write(p)
        if i == 2:
            # Junk that holds a capture pattern and a version byte but no valid page.
            f.w.junk(b"junk\x00OggS\x00\x02" + bytes(30) + b"more junk")
    f.stream(0x00006A6A, pages)
    f.finding("ogg.junk", "50 bytes at 0x")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "speexdec": True, "not_compared": {
        "oggz validate's verdict": "oggz validate, like libogg, skips bytes between pages without a word",
        "ogginfo and ffprobe": "they skip the bytes between pages without a word"}}
    f.write()


def oggs_in_mp3():
    # MPEG-1 Layer III frames (stereo, 128 kbps, 44.1 kHz, zero side information) whose data holds
    # "OggS" with a page header, a page whose CRC does not hold and, in the second frame, "OggS"
    # with a segment table that points past the file: none is a page, so the file is MPEG audio.
    frame = bytearray(b"\xff\xfb\x90\x00" + bytes(413))
    fake, _ = m.page(1, 0, 0, m.BOS, [10], b"0123456789", crc=0xDEADBEEF)
    frame[100:100 + len(fake)] = fake
    frames = [bytearray(frame) for _ in range(6)]
    frames[1][40:50] = b"OggS\x00\x02" + bytes(4)
    data = b"".join(frames)
    (OUT / "oggs-in-mp3.mp3").write_bytes(data)
    # Six frames of 1152 samples, 417 bytes each.
    info = {"codec": "MPEG-1 Layer III", "sample_rate": 44100, "channels": 2, "channel_layout": ["front left", "front right"], "samples": 6912,
            "duration": 6912 / 44100,
            "average_bitrate": 6 * 417 * 8 * 44100 / 6912 / 1000, "is_vbr": False}
    manifest = {"length": len(data), "format": "MPEG Audio", "regions": [{"name": "MPEG Audio", "offset": 0, "length": len(data)}],
                "pages": [], "streams": [], "stream_infos": [info], "findings": [], "crosscheck": {"valid": False, "mpeg_frames": 6}}
    (OUT / "oggs-in-mp3.expected.json").write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")


def huge_lacing():
    # A page of 255 segments of 255 bytes, the largest RFC 3533 allows, whose packet never ends
    # and whose data the file cuts short; before it, a whole one that continues on the next page
    # with the end-of-stream flag, which ends inside the packet.
    f = Fixture("huge-lacing")
    packets, granules = speex_packets(0, cbr(5, 3))
    pages = m.pages_for(0x0000FFFF, packets, granules, default_cuts(5), eos=False)
    for p in pages:
        f.w.write(p)
    big = m.PageSpec(0x0000FFFF, pages[-1].sequence + 1, -1, 0, [255] * 255, bytes(255 * 255), [])
    f.w.write(big)
    end = m.PageSpec(0x0000FFFF, big.sequence + 1, -1, m.CONTINUED | m.EOS, [255] * 255, bytes(255 * 255), [])
    f.w.write(end, cut=27 + 255 + 1000)
    f.stream(0x0000FFFF, pages)
    f.finding("ogg.page-truncated", "is cut short after 1282 bytes of 65307 bytes")
    f.finding("ogg.eos-missing", "sets the flag but is damaged")
    f.crosscheck = {"valid": False, "oggz_validate": ["missing *** eos"], "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they do not report a page cut short; the packet the largest page starts never ends, so no tool lists it"}}
    f.write()


def continuation_errors():
    # A packet left unfinished at the end of a page whose next page does not set the continued
    # flag, and a page that sets the flag with no packet unfinished.
    f = Fixture("continuation-errors")
    packets, granules = speex_packets(0, cbr(20, 3))
    pages = m.pages_for(0x0000C0C0, packets, granules, default_cuts(20, per_page=5))
    # Page 3 ends inside a 300-byte packet; page 4 starts fresh without the flag.
    pages[2].lacing += [255]
    pages[2].body += bytes(255)
    pages[4].flags |= m.CONTINUED
    pages[4].lacing = [10] + pages[4].lacing
    pages[4].body = bytes(10) + pages[4].body
    for p in pages:
        f.w.write(p)
    f.stream(0x0000C0C0, pages)
    f.finding("ogg.continuation-missing", "without the continued-packet flag")
    f.finding("ogg.continuation-orphaned", "sets the continued-packet flag, but no packet was left unfinished")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "not_compared": {
        "oggz validate's verdict": "oggz validate does not check the continued-packet flag",
        "oggz info and dump, and speexdec": "libogg joins the unfinished packet to the next page's first one instead of dropping it (oggz dump lists a packet of 275 bytes), so its packets differ from the analyzer's, which follow the flags as RFC 3533 defines them; the orphaned bytes it skips, as the analyzer does",
        "ogginfo and ffprobe": "they read the packets as libogg puts them together"}}
    f.write()


def granule_errors():
    # Granule positions that go back, one on a page where no packet ends, and -1 on a page that
    # completes packets.
    f = Fixture("granule-errors")
    packets, granules = speex_packets(0, cbr(25, 3))
    pages = m.pages_for(0x00006E6E, packets, granules, [1, 1, 5, 5, 5, 5, 5])
    # After page 5, a page holds only the start of a 300-byte packet but has a granule position;
    # the next page continues it. The packet is a frame, then a terminator and filler bytes.
    pages[4].granule = pages[3].granule - 100
    pages[5].granule = -1
    long_packet = m.speex_packet([(3,)]) + b"\x7f" + b"\xff" * 279
    spare = m.PageSpec(0x00006E6E, pages[5].sequence + 1, pages[6].granule - 1, 0, [255], long_packet[:255], [])
    for p in pages[6:]:
        p.sequence += 1
    pages[6].flags |= m.CONTINUED
    pages[6].lacing = [45] + pages[6].lacing
    pages[6].body = long_packet[255:] + pages[6].body
    for p in pages[:6] + [spare] + pages[6:]:
        f.w.write(p)
    f.stream(0x00006E6E, pages[:6] + [spare] + pages[6:])
    f.finding("ogg.granule-backwards", "goes back once")
    f.finding("ogg.granule-missing", "has the granule position -1")
    f.finding("ogg.granule-without-packet", "completes no packet but has a granule position")
    f.finding("speex.granule-inconsistent", "not match the samples of the packets")
    f.crosscheck = {"valid": False, "oggz_validate": ["out of order", "must be -1"], "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they do not check granule positions"}}
    f.write()


def bos_missing():
    # A file cut at a page boundary after its header pages: the stream starts without its
    # beginning-of-stream page, so its codec is unknown.
    f = Fixture("bos-missing")
    packets, granules = speex_packets(0, cbr(20, 3))
    pages = m.pages_for(0x0000B0B0, packets, granules, default_cuts(20, per_page=10))
    for p in pages[2:]:
        f.w.write(p)
    f.stream(0x0000B0B0, pages[2:], codec="Unknown")
    f.finding("ogg.bos-missing", "without a beginning-of-stream page")
    f.finding("ogg.codec-unknown", "starts with bytes no Ogg mapping is known for")
    f.crosscheck = {"valid": False, "oggz_validate": ["missing *** bos"], "not_compared": {
        "oggz info and dump": "oggz cannot name the stream's codec without its first page and lists none of its packets",
        "ogginfo, ffprobe and speexdec": "no decoder reads a Speex stream without its header"}}
    f.write()


def comment_invalid():
    # Comments that break the format: no '=', bytes that are not UTF-8, a field name with a
    # character outside 0x20 to 0x7D, and a last comment whose length runs past the packet.
    f = Fixture("comment-invalid")
    comment = m.vorbis_comment(VENDOR, [b"NOSEPARATOR", b"TITLE=\xff\xfe broken", b"F\x01ELD=value"])
    comment += struct.pack("<I", 1000) + b"ARTIST=cut"
    comment = comment[:24 + 4] + struct.pack("<I", 4) + comment[32:]
    head = m.speex_header(8000, 0)
    audio = [m.speex_packet([(3,)]) for _ in range(5)]
    packets = [head, comment] + audio
    granules = [0, 0] + [(i + 1) * 160 - 40 for i in range(5)]
    pages = m.pages_for(0x0000BADC, packets, granules, default_cuts(5))
    for p in pages:
        f.w.write(p)
    f.stream(0x0000BADC, pages)
    f.finding("vorbis-comment.no-separator", "\"NOSEPARATOR\" has no '='")
    f.finding("vorbis-comment.not-utf8", "is not valid UTF-8")
    f.finding("vorbis-comment.invalid-field-name", "is not made of ASCII 0x20 to 0x7D")
    f.finding("vorbis-comment.length-overrun", "Comment 4 of 4 declares 1000 bytes")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they stop reading the comment at the one that runs past the packet, and report nothing else of it"}}
    f.write()


def header_invalid():
    # A header that breaks speex_header.h but still names a mode decoders know: size 76, three
    # channels, zero frames per packet, version ID 2, reserved fields set and a bit-stream version
    # speexdec refuses; and an extra header announced and present.
    f = Fixture("header-invalid")
    extra = [b"EXTRA HEADER"]
    packets, granules = speex_packets(0, cbr(5, 3), channels=3, nframes=0, version_id=2, header_size=76, bitstream=3,
                                      reserved=(1, 0), frame_size=200, extra_headers=extra)
    pages = m.pages_for(0x00000EAD, packets, granules, default_cuts(5, extra=1))
    for p in pages:
        f.w.write(p)
    f.stream(0x00000EAD, pages, extra_headers=1)
    f.finding("speex.header-size", "gives its size as 76 bytes")
    f.finding("speex.frame-size", "gives a frame size of 200 samples")
    f.finding("speex.bitstream-version", "bit-stream version is 3")
    f.finding("speex.channels-invalid", "gives 3 channels")
    f.finding("speex.frames-per-packet", "gives 0 frames per packet")
    f.finding("speex.version-id", "version ID is 2")
    f.finding("speex.reserved", "A reserved header field is not 0")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "speexdec, ogginfo and ffprobe": "speexdec refuses the bit-stream version, as the finding says, and the others read the header as it stands"}}
    f.write()


def header_bad_mode():
    f = Fixture("header-bad-mode")
    head = m.speex_header(8000, 0)
    head = head[:40] + struct.pack("<i", 7) + head[44:]
    short_head = head
    packets = [short_head, m.vorbis_comment(VENDOR, [])] + [m.speex_packet([(3,)]) for _ in range(4)]
    granules = [0, 0] + [(i + 1) * 160 - 40 for i in range(4)]
    pages = m.pages_for(0x00000B0D, packets, granules, default_cuts(4))
    for p in pages:
        f.w.write(p)
    f.stream(0x00000B0D, pages)
    f.finding("speex.mode-invalid", "mode 7 is none of")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "speexdec, ogginfo and ffprobe": "decoders reject the mode, as the finding says"}}
    f.write()


def header_short():
    f = Fixture("header-short")
    head = b"Speex   1.2.1" + bytes(40)
    packets = [head, m.vorbis_comment(VENDOR, [])] + [m.speex_packet([(3,)]) for _ in range(3)]
    pages = m.pages_for(0x00000005, packets, [0, 0, 120, 280, 440], default_cuts(3))
    for p in pages:
        f.w.write(p)
    f.stream(0x00000005, pages)
    f.finding("speex.header-short", "holds 53 bytes, fewer than the header's 80")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "speexdec, ogginfo and ffprobe": "decoders reject the short header, as the finding says"}}
    f.write()


def frames_corrupt():
    # Audio packets with an undefined narrowband submode (10), a frame that runs past the packet,
    # one frame too many, one frame too few, an empty packet, and a wideband layer in a narrowband
    # stream, which narrowband decoders skip.
    f = Fixture("frames-corrupt")
    head = m.speex_header(8000, 0, frames_per_packet=2)
    w = m.BitWriter()
    w.put(0, 1)
    w.put(10, 4)
    w.put(0, 11)
    invalid = w.bytes()
    overrun = m.speex_packet([(3,), (3,)])[:25]
    extra = m.speex_packet([(3,), (3,), (3,)])
    fewer = m.speex_packet([(3,)])
    w = m.BitWriter()
    w.put(1, 1)
    w.put(1, 3)
    w.put(0, 32)
    m.speex_frame(w, 3)
    m.speex_frame(w, 3)
    w.terminate()
    stray = w.bytes()
    good = m.speex_packet([(3,), (3,)])
    audio = [good, invalid, good, overrun, extra, fewer, b"", stray, good, good]
    packets = [head, m.vorbis_comment(VENDOR, [])] + audio
    granules = [0, 0] + [(i + 1) * 320 - 40 for i in range(len(audio))]
    pages = m.pages_for(0x0000FA11, packets, granules, default_cuts(len(audio)))
    for p in pages:
        f.w.write(p)
    f.stream(0x0000FA11, pages)
    f.finding("speex.frame-invalid", "narrowband submode 10 is not defined")
    f.finding("speex.frame-overrun", "the frame runs past the end of the packet")
    f.finding("speex.extra-frames", "more frames than the 2 per packet")
    f.finding("speex.missing-frames", "fewer frames than the 2 per packet")
    f.finding("speex.empty-packet", "1 audio packet is empty")
    f.finding("speex.wideband-layer-in-narrowband", "carries wideband layers")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "they do not read frames; speexdec's frame bitrates and samples are compared"}}
    f.write()


def deep_nesting():
    # Containers inside containers that the analyzer must not follow: a comment whose value is a
    # whole Ogg file, an unknown stream whose packets are Ogg pages, and 200 chained links of one
    # page each, so the report stays wide, never deep.
    f = Fixture("deep-nesting", ".ogg")
    inner_packets, inner_granules = speex_packets(0, cbr(2, 1))
    inner_pages = m.pages_for(0x01, inner_packets, inner_granules, [1, 1, 2])
    nested = b"".join(m.page(p.serial, p.sequence, p.granule, p.flags, p.lacing, p.body)[0] for p in inner_pages)
    packets, granules = speex_packets(0, cbr(3, 1), comments=[b"EMBEDDED=" + nested])
    s = m.pages_for(0x0000D00D, packets, granules, [1, 1, 3])
    pagey = m.pages_for(0x0000D11D, [b"NESTOGG!" + nested, nested, nested], [0, 0, 0], [2])
    for p in [s[0], pagey[0], s[1], pagey[1], s[2]]:
        f.w.write(p)
    f.stream(0x0000D00D, s)
    f.stream(0x0000D11D, pagey, codec="Unknown")
    f.finding("ogg.codec-unknown", "0x0000D11D starts with bytes")
    f.finding("vorbis-comment.not-utf8", "A comment is not valid UTF-8")
    for link in range(200):
        serial = 0x10000 + link
        p = m.PageSpec(serial, 0, 0, m.BOS | m.EOS, [8], b"LINK" + struct.pack("<I", link), [0])
        f.w.write(p)
        f.stream(serial, [p], link=2 + link, codec="Unknown")
        f.finding("ogg.codec-unknown", f"0x{serial:08X} starts with bytes")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "not_compared": {
        "oggz info and dump": "oggz 1.1.3 miscounts every stream of a file that holds a stream whose codec it does not know (it lists 1 of this file's packets)",
        "ogginfo, ffprobe and speexdec": "the links hold no codec they decode, and the Speex stream's figures are what the valid fixtures compare"}}
    f.write()


def eos_inside_packet():
    f = Fixture("eos-inside-packet")
    packets, granules = speex_packets(0, cbr(10, 3))
    pages = m.pages_for(0x0000E1E1, packets, granules, default_cuts(10))
    pages[-1].lacing += [255]
    pages[-1].body += bytes(255)
    for p in pages:
        f.w.write(p)
    f.stream(0x0000E1E1, pages)
    f.finding("ogg.packet-unfinished", "ends inside a packet, which is never completed")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "speexdec": True, "not_compared": {
        "oggz validate's verdict": "oggz validate does not check packets left unfinished at the end of a stream",
        "ogginfo and ffprobe": "they do not report a packet left unfinished"}}
    f.write()


def chained_same_serial():
    # Two files joined end to end that both use the serial number 0x33333333: RFC 3533, section 4 asks for a serial number that is
    # unique within the scope of the physical bitstream, also across the links of a chain.
    f = Fixture("chained-same-serial", ".ogg")
    a_packets, a_granules = speex_packets(0, cbr(20, 4))
    b_packets, b_granules = speex_packets(0, cbr(20, 5))
    first = m.pages_for(0x33333333, a_packets, a_granules, default_cuts(20))
    for p in first:
        f.w.write(p)
    boundary = len(f.w.data)
    second = m.pages_for(0x33333333, b_packets, b_granules, default_cuts(20))
    for p in second:
        f.w.write(p)
    f.stream(0x33333333, first, link=1, window=(0, boundary))
    f.stream(0x33333333, second, link=2, window=(boundary, len(f.w.data)))
    f.finding("ogg.serial-reused", "starts a logical stream in a later link with the serial number 0x33333333 of an earlier one")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "not_compared": {
        "oggz info and dump": "oggz counts the pages and packets of both links under the one serial number (44 packets in 6 pages here)",
        "ogginfo, ffprobe and speexdec": "they read the second link's pages under the serial number of the first, or stop at the end of the first link"}}
    f.write()


def ignored_bits():
    # Bits after the frames the header gives per packet, which decoders never read (speexdec decodes exactly the header's frames
    # per packet): an undefined narrowband submode, a frame that runs past the packet and a wideband layer of submode 0 that
    # libspeex 1.2.1 cannot skip. None of them is a finding.
    f = Fixture("speex-ignored-bits")
    head = m.speex_header(8000, 0)
    good = m.speex_packet([(3,)])

    def after(frame_nb, tail, size=None):
        w = m.BitWriter()
        m.speex_frame(w, frame_nb)
        for bit in tail:
            w.put(int(bit), 1)
        return w.bytes()

    audio = [good, after(1, "01001"), good, after(3, "00101"), good, after(3, "1000"), after(1, "01111"), good, good]
    packets = [head, m.vorbis_comment(VENDOR, [])] + audio
    granules = [0, 0] + [(i + 1) * 160 - 40 for i in range(len(audio))]
    pages = m.pages_for(0x00BEEF01, packets, granules, default_cuts(len(audio)))
    for p in pages:
        f.w.write(p)
    f.stream(0x00BEEF01, pages)
    f.write()


def comment_many():
    # More than 1,024 comments, so the analyzer builds the comment nodes in blocks when they are read: valid ones, and some with no
    # '=', an invalid field name and bytes that are not UTF-8, which are findings counted once.
    f = Fixture("comment-many")
    comments = []
    for i in range(1100):
        comments.append(b"NOSEPARATOR" if i % 100 == 50 else b"bad\x01name=x" if i % 250 == 7 else b"TITLE=\xff" if i == 900 else f"TITLE=Song {i}".encode())
    packets, granules = speex_packets(0, cbr(10, 3), comments=comments)
    pages = m.pages_for(0x00C0FFEA, packets, granules, default_cuts(10))
    for p in pages:
        f.w.write(p)
    f.stream(0x00C0FFEA, pages)
    f.finding("vorbis-comment.no-separator", "11 comments have no '=' between a field name and a value")
    f.finding("vorbis-comment.invalid-field-name", "5 field names are empty or not made of ASCII 0x20 to 0x7D")
    f.finding("vorbis-comment.not-utf8", "A comment is not valid UTF-8")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "speexdec": True, "not_compared": {
        "ogginfo and ffprobe": "ogginfo stops listing the comments at the first with an invalid field name, and ffprobe starts the stream at -200 samples where the pages give -40 (observed with ffprobe 8.0.1; not explained)"}}
    f.write()


def wideband_layer_skip():
    # A narrowband stream with wideband layers before its frames, as a wideband stream relabeled narrowband has: a layer of
    # submode 2 is skipped, and one of submode 0 makes libspeex 1.2.1 (nb_decode, wb_skip_table) read it again until
    # "More than two wideband layers found. The stream is corrupted."
    f = Fixture("speex-wideband-layer-skip")
    head = m.speex_header(8000, 0)

    def layered(submode):
        w = m.BitWriter()
        w.put(1, 1)
        w.put(submode, 3)
        w.put(0, m.WB_BITS[submode] - 4)
        m.speex_frame(w, 3)
        w.terminate()
        return w.bytes()

    good = m.speex_packet([(3,)])
    audio = [good, layered(2), good, good, layered(0), good]
    packets = [head, m.vorbis_comment(VENDOR, [])] + audio
    granules = [0, 0] + [(i + 1) * 160 - 40 for i in range(len(audio))]
    pages = m.pages_for(0x00BEEF02, packets, granules, default_cuts(len(audio)))
    for p in pages:
        f.w.write(p)
    f.stream(0x00BEEF02, pages)
    f.finding("speex.frame-invalid", "a skipped wideband layer has submode 0, which libspeex 1.2.1 cannot skip")
    f.finding("speex.wideband-layer-in-narrowband", "1 audio packet of this narrowband stream carries wideband layers")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True,
                    "speexdec_error": "More than two wideband layers found. The stream is corrupted.",
                    "not_compared": {"speexdec's samples and bitrates": "speexdec drops the rest of the corrupt packet and goes on, which the model of its output does not follow",
                                     "ogginfo and ffprobe": "they do not read frames"}}
    f.write()


FIXTURES = [
    narrowband, wideband, ultra_wideband, frames_and_stereo, multiplexed, chained, packet_spanning_pages, lacing_255,
    comment_edge_cases, writer_claims_differ, encoder_other_format, encoder_other_format_no_vendor, comment_empty, unknown_codec, tags_around, tag_signature_in_last_page,
    bad_crc, sequence_gap, truncated_page, missing_eos, garbage_between_pages, oggs_in_mp3, huge_lacing, continuation_errors,
    granule_errors, bos_missing, comment_invalid, header_invalid, header_bad_mode, header_short, frames_corrupt, deep_nesting,
    eos_inside_packet, chained_same_serial, ignored_bits, comment_many, wideband_layer_skip,
]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for build in FIXTURES:
        build()
    print(f"Wrote {len(FIXTURES)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
