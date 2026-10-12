#!/usr/bin/env python3
"""Writes the byte-exact Vorbis and Opus fixtures in tests/fixtures/ogg.

Pages come from ogg_model.py, packets from vorbis_opus_model.py: Vorbis streams use a minimal setup
header (or a few richer ones) whose audio packets code silence, so libvorbis decodes them; Opus
packets carry frames of zero bytes in every TOC configuration and frame packing, which libopus
decodes. The manifests have the parts make_ogg_fixtures.py describes; a Vorbis stream's entry adds
"vorbis" with the identification header's fields, the comment, the setup header's summary, every
audio packet with its block ("L1" a long block of mode 1, "S0" a short one, or why it codes none) and
the statistics; an Opus stream's adds "opus" with OpusHead's fields, the comment and the data after
it, every audio packet with each stream's TOC (configuration, stereo flag, code, frames) and the
statistics. The expectations are worked out by vorbis_opus_model.py from the specifications.

Standard library only and deterministic, so CI reruns it and fails when the committed files differ.
"""

import struct

import make_ogg_fixtures as hand
import ogg_model as m
import vorbis_opus_model as v

VENDOR = b"Encspect fixture"


# --- Expectations ---------------------------------------------------------------------------------


def positions_of(page_ends, first_audio):
    out = []
    for r, ends, clean in page_ends:
        if ends and r["granule"] != -1 and ends[-1] >= first_audio:
            out.append((ends[-1] - first_audio, r["granule"], bool(r["flags"] & m.EOS) and clean))
    return out


def expect_vorbis(entry, data, packets, page_ends, s):
    ident = packets[0]["data"]
    fields = v.vorbis_identification_fields(ident)
    channels = ident[11] if len(ident) >= 30 else 0
    vorbis = {"header": fields}
    first_audio = 3
    setup = None
    if len(packets) > 1 and packets[1]["data"][:7] == b"\x03vorbis":
        vorbis["comment"] = m.read_comment(packets[1]["data"][7:])
    if len(packets) > 2 and packets[2]["data"][:7] == b"\x05vorbis":
        setup = v.read_setup(packets[2]["data"], channels)
        vorbis["setup"] = {"value": setup["value"], "groups": setup["groups"]}
    audio = packets[first_audio:]
    gaps = [i - first_audio for i, p in enumerate(packets) if p["gap"] and i >= first_audio]
    result = v.vorbis_expectations(ident, setup, [(p["offset"], p["data"], p["ranges"]) for p in audio], positions_of(page_ends, first_audio), gaps)
    vorbis.update({"packets": result["packets"], "blocks": result["blocks"], "statistics": result["statistics"]})
    if s.get("encoder"):
        vorbis["encoder"] = s["encoder"]
    entry["vorbis"] = vorbis
    entry["model_findings"] = sorted(set(result["findings"] + (setup["problems"] if setup else [])))
    info = result["stream_info"]
    if info and channels:
        info["channel_mode"] = v.VORBIS_LAYOUTS[channels][0] if channels in v.VORBIS_LAYOUTS else f"{channels} channels"
    return entry, info


def expect_opus(entry, data, packets, page_ends, s):
    head = packets[0]["data"]
    opus = {"header": v.opus_head_fields(head)}
    first_audio = 1
    if len(packets) > 1 and packets[1]["data"][:8] == b"OpusTags":
        first_audio = 2
        tags = packets[1]["data"]
    if len(packets) > 1 and packets[1]["data"][:8] == b"OpusTags" and len(packets[1]["data"]) > 8:
        comment = m.read_comment(tags[8:])
        opus["comment"] = comment
        end = 8 + comment_end(tags[8:])
        if end < len(tags):
            opus["extra"] = f"{m.plural(len(tags) - end, 'byte', 'bytes')}, {'binary data to keep' if tags[end] & 1 else 'padding, which may be discarded'}"
    audio = packets[first_audio:]
    gaps = [i - first_audio for i, p in enumerate(packets) if p["gap"] and i >= first_audio]
    result = v.opus_expectations(head, [(p["offset"], p["data"], p["ranges"]) for p in audio], positions_of(page_ends, first_audio), gaps)
    opus.update({"packets": result["packets"], "tocs": result["tocs"], "statistics": result["statistics"]})
    if s.get("encoder"):
        opus["encoder"] = s["encoder"]
    entry["opus"] = opus
    entry["model_findings"] = sorted(set(result["findings"]))
    return entry, result["stream_info"]


def comment_end(data):
    """Where a Vorbis comment that fits its data ends."""
    length = struct.unpack_from("<I", data, 0)[0]
    position = 4 + length
    count = struct.unpack_from("<I", data, position)[0]
    position += 4
    for _ in range(count):
        position += 4 + struct.unpack_from("<I", data, position)[0]
    return position


hand.CODECS["Vorbis"] = expect_vorbis
hand.CODECS["Opus"] = expect_opus


class Fixture(hand.Fixture):
    def write(self):
        super().write()
        # Every finding the models predict must be listed, with the text its message holds.
        import json
        path = hand.OUT / (self.name + ".expected.json")
        manifest = json.loads(path.read_text(encoding="utf-8"))
        listed = {f["code"] for f in self.findings}
        for stream in manifest["streams"]:
            missing = set(stream.pop("model_findings", [])) - listed - {"vorbis.header-extra-data"}
            if missing:
                raise SystemExit(f"{self.name}: the model predicts {sorted(missing)}, which the fixture does not list")
        path.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


# --- Vorbis streams -------------------------------------------------------------------------------

LONG, SHORT = 11, 8


def returned(blocks, b0=1 << SHORT, b1=1 << LONG):
    """The samples each audio packet returns by the overlap rule (section 4.3.8), from its block
    flags (None for a packet that codes no block)."""
    out, previous = [], None
    for flag in blocks:
        if flag is None:
            out.append(0)
            continue
        size = b1 if flag else b0
        out.append(0 if previous is None else previous // 4 + size // 4)
        previous = size
    return out


def vorbis_audio_packets(blocks, channels, mode_count=2, sizes=None):
    """Audio packets of the minimal codec for a list of block flags (1 long, 0 short), with window
    flags that match the neighbors, padded with zero bytes to the given sizes."""
    out = []
    for i, flag in enumerate(blocks):
        previous = blocks[i - 1] if i > 0 else 1
        following = blocks[i + 1] if i + 1 < len(blocks) else 1
        packet = v.vorbis_audio(flag, mode_count, flag == 1, previous, following, channels)
        if sizes:
            packet += bytes(max(0, sizes[i % len(sizes)] - len(packet)))
        out.append(packet)
    return out


def vorbis_stream(channels, rate, blocks, comments=(), sizes=None, end_trim=0, start_offset=0, coupling=None, bitrates=(0, 0, 0), setup=None,
                  ident=None, audio=None, vendor=VENDOR):
    """The packets and granule positions of a Vorbis stream: the headers at 0, then each audio
    packet's returned samples plus the start offset; end_trim less on the last."""
    packets = [ident or v.vorbis_identification(channels, rate, (SHORT, LONG), bitrates),
               v.vorbis_comment_header(vendor, list(comments)),
               setup or v.vorbis_setup(channels, coupling=coupling)]
    audio = audio if audio is not None else vorbis_audio_packets(blocks, channels, sizes=sizes)
    packets += audio
    total, granules = start_offset, [0, 0, 0]
    for r in returned(blocks):
        total += r
        granules.append(total)
    granules[-1] -= end_trim
    return packets, granules


class Cuts:
    """Pages by packets rather than segments: each entry is how many packets a page completes,
    turned into segment counts once the packets are known."""

    def __init__(self, *groups):
        self.groups = list(groups)

    def segments(self, packets):
        out, index = [], 0
        for count in self.groups:
            if index >= len(packets):
                break
            out.append(sum(len(m.lacing_of(len(p))) for p in packets[index:index + count]))
            index += count
        return out


def vorbis_cuts(audio_count, per_page=16):
    """libvorbis' layout: the identification header alone, the comment and setup on the second
    page, then the audio."""
    return Cuts(1, 2, *[per_page] * (audio_count // per_page + 1))


def pages_for(serial, packets, granules, cuts):
    return m.pages_for(serial, packets, granules, cuts.segments(packets) if isinstance(cuts, Cuts) else cuts)


def single(name, packets, granules, cuts, serial=0x0B0B0001, extension=".ogg", **stream):
    f = Fixture(name, extension)
    if stream.get("codec") == "Vorbis":
        f.crosscheck = {"valid": True, "skip": {}}
    pages = pages_for(serial, packets, granules, cuts)
    for p in pages:
        f.w.write(p)
    f.stream(serial, pages, **stream)
    return f


def switching(count, attacks=(10, 30)):
    """Long blocks with short ones around each attack, as an encoder switches at transients."""
    blocks = [1] * count
    for a in attacks:
        for i in range(a, min(a + 4, count)):
            blocks[i] = 0
    return blocks


def vorbis_mono():
    blocks = switching(48)
    packets, granules = vorbis_stream(1, 44100, blocks, comments=[b"TITLE=Minimal Vorbis"], end_trim=300)
    f = single("vorbis-minimal-mono", packets, granules, vorbis_cuts(len(blocks)), codec="Vorbis")
    f.write()


def vorbis_stereo():
    # Two channels coupled by one square polar step, all three bitrate hints equal, and packets of
    # varying sizes, two of which span pages.
    blocks = switching(40, attacks=(5, 20))
    sizes = [30, 45, 61, 700, 52, 38, 1200, 44]
    packets, granules = vorbis_stream(2, 48000, blocks, comments=[b"ARTIST=Encspect"], sizes=sizes, coupling=[(0, 1)], bitrates=(128000, 128000, 128000),
                                      end_trim=1000)
    f = single("vorbis-stereo-coupled", packets, granules, [1, 2, 3, 2, 5, 9, 9, 9, 9], serial=0x0B0B0002, codec="Vorbis")
    f.write()


def vorbis_surround():
    # Six channels, which section 4.3.9 orders as 5.1, with two coupling steps.
    blocks = [1] * 20
    packets, granules = vorbis_stream(6, 48000, blocks, coupling=[(0, 2), (3, 4)], bitrates=(0, 256000, 0), end_trim=50)
    f = single("vorbis-surround-51", packets, granules, vorbis_cuts(len(blocks)), serial=0x0B0B0006, codec="Vorbis")
    f.write()


def vorbis_start_offset():
    # A stream cut from a longer one: the first audio page, which ends after the second audio
    # packet as section A.2 asks, has a granule position 44100 samples beyond what the packets
    # return.
    blocks = [1] * 30
    packets, granules = vorbis_stream(2, 44100, blocks, start_offset=44100, end_trim=100)
    f = single("vorbis-start-offset", packets, granules, [1, 2, 2, 10, 20], serial=0x0B0B0007, codec="Vorbis")
    f.finding("vorbis.start-offset", "starts the stream at a later time")
    f.write()


def vorbis_start_trim():
    # Samples discarded at the start: the first audio page's granule position is 500 below the
    # samples the first two packets return, within the 1024 they return.
    blocks = [1] * 30
    packets, granules = vorbis_stream(2, 44100, blocks, start_offset=-500)
    f = single("vorbis-start-trimming", packets, granules, [1, 2, 2, 10, 20], serial=0x0B0B0008, codec="Vorbis")
    f.write()


def vorbis_granule_errors():
    # A first audio page that discards more than the first two packets return and does not end
    # after the second packet, a granule position in the middle that does not match the packets,
    # and a last one beyond what the packets return.
    blocks = [1] * 30
    packets, granules = vorbis_stream(1, 44100, blocks, start_offset=-3000)
    granules[3 + 15] += 77
    granules[-1] += 2000
    f = single("vorbis-granule-errors", packets, granules, [1, 2, 5, 11, 20], serial=0x0B0B0009, codec="Vorbis")
    f.finding("vorbis.start-page-layout", "ends after audio packet 5")
    f.finding("vorbis.start-trimming-large", "discards 3000 samples at the start, more than the 1024")
    f.finding("vorbis.granule-inconsistent", "not match the samples the packets before")
    f.finding("vorbis.granule-beyond-packets", "samples beyond what the packets before it return")
    f.write()


def vorbis_packet_errors():
    # Three modes, so the mode takes two bits and 3 is undefined; window flags that contradict the
    # neighbors; a packet with the type bit set; and an empty packet.
    modes = ((0, 0), (1, 0), (1, 0))
    setup = v.vorbis_setup(1, modes=modes)
    blocks = [1] * 12
    audio = [v.vorbis_audio(1, 3, True, 1, 1) for _ in blocks]
    audio[3] = v.vorbis_audio(2, 3, True, 0, 1)                   # previous flag says short
    audio[6] = v.vorbis_audio(1, 3, True, 1, 0)                   # next flag says short
    w = v.BitWriter()
    w.put(0, 1)
    w.put(3, 2)
    audio.insert(8, w.bytes())                                     # mode 3 is undefined
    audio.insert(9, b"\x01 not audio")
    audio.insert(10, b"")
    flags = [1] * 8 + [None, None, None] + [1] * 4
    packets = [v.vorbis_identification(1, 22050, (SHORT, LONG)), v.vorbis_comment_header(VENDOR, []), setup] + audio
    total, granules = 0, [0, 0, 0]
    for r in returned(flags):
        total += r
        granules.append(total)
    f = single("vorbis-packet-errors", packets, granules, vorbis_cuts(len(audio)), serial=0x0B0B000A, codec="Vorbis")
    f.crosscheck["skip"]["oggdec"] = ("libvorbisfile's handling of the packets it skips is not modeled: oggdec writes 3072 samples fewer, "
                                      "a long block's worth for each of the three packets that code no block")
    f.finding("vorbis.window-flag-mismatch", "2 window flags do not match the size of the neighboring block")
    f.finding("vorbis.mode-invalid", "names a mode the setup header does not define")
    f.finding("vorbis.not-audio", "has the packet type bit set")
    f.finding("vorbis.empty-packet", "1 audio packet is empty")
    f.write()


def vorbis_ident_invalid():
    # Link 1: version 1, block sizes 32 and 2048, no framing bit; link 2: no channels and a sample
    # rate of 0. Decoders refuse both; their audio packets are only counted.
    f = Fixture("vorbis-ident-invalid", ".ogg")
    links = [
        (0x0B0B0010, v.vorbis_identification(1, 44100, (5, LONG), version=1, framing=0), 1),
        (0x0B0B0011, v.vorbis_identification(0, 0, (SHORT, LONG)), 0),
    ]
    for link, (serial, ident, channels) in enumerate(links, 1):
        packets, granules = vorbis_stream(channels, 44100, [1] * 8, ident=ident)
        pages = pages_for(serial, packets, [0] * len(packets), vorbis_cuts(8))
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=link, codec="Vorbis")
    f.finding("vorbis.version", "gives the Vorbis version 1")
    f.finding("vorbis.blocksize-invalid", "gives the block sizes 32 and 2048")
    f.finding("vorbis.framing-missing", "identification header's framing bit is 0")
    f.finding("vorbis.channels-invalid", "gives 0 channels")
    f.finding("vorbis.rate-invalid", "sample rate of 0 Hz")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "ogginfo, ffprobe and oggdec": "they refuse the headers, as the findings say"}}
    f.write()


def vorbis_header_layout():
    # The identification header shares its page with the comment header, the setup header's page
    # goes on with audio, and the comment header has no framing bit but trailing bytes in its
    # place.
    blocks = [1] * 20
    packets, granules = vorbis_stream(1, 44100, blocks)
    packets[1] = v.vorbis_comment_header(VENDOR, [b"TITLE=Layout"], framing=False) + b"\x00XY"
    f = single("vorbis-header-layout", packets, granules, [2, 3, 20], serial=0x0B0B0012, codec="Vorbis")
    f.finding("vorbis.page-layout", "identification header shares its page")
    f.finding("vorbis.framing-missing", "comment header's framing bit is 0")
    f.finding("vorbis.comment-trailing-data", "holds 2 bytes after its framing bit")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "ogginfo, ffprobe and oggdec": "libvorbis refuses the comment header without its framing bit, as the finding says"}}
    f.write()


def vorbis_setup_truncated():
    # Three links whose setup headers end inside the codeword lengths, the floors and the modes.
    full = v.vorbis_setup(1)
    cuts = [17, 32, len(full) - 4]
    f = Fixture("vorbis-setup-truncated", ".ogg")
    for link, cut in enumerate(cuts, 1):
        serial = 0x0B0B0020 + link
        packets, _ = vorbis_stream(1, 44100, [1] * 6, setup=full[:cut])
        pages = pages_for(serial, packets, [0] * len(packets), vorbis_cuts(6))
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=link, codec="Vorbis")
    f.finding("vorbis.setup-truncated", "ends inside the codeword lengths")
    f.finding("vorbis.setup-truncated", "ends inside the floor type field")
    f.finding("vorbis.setup-truncated", "ends inside the transform type field")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "ogginfo, ffprobe and oggdec": "libvorbis refuses a setup header cut short, as the findings say"}}
    f.write()


def book_sets(*books):
    def write(w):
        w.put(len(books) - 1, 8)
        for b in books:
            b(w)
    return write


def minimal_book(w):
    v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))


def vorbis_setup_invalid():
    # One link per setup header that breaks the specification or what libvorbis accepts.
    variants = [
        ("bad sync", v.vorbis_setup(1, books=book_sets(lambda w: v.codebook(w, 1, [1, 1], sync=0x424242)))),
        ("overpopulated", v.vorbis_setup(1, books=book_sets(lambda w: v.codebook(w, 1, [1, 1, 1], lookup=(1, 0.0, 1.0, 2, 0, [0, 1, 2]))))),
        ("underpopulated", v.vorbis_setup(1, books=book_sets(lambda w: v.codebook(w, 1, [1, 2], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))))),
        ("too large", v.vorbis_setup(1, books=book_sets(minimal_book, lambda w: v.codebook(w, 32768, [10] * 1024)))),
        ("floor type", v.vorbis_setup(1, floors=lambda w: (w.put(0, 6), w.put(2, 16)))),
        ("mode window", v.vorbis_setup(1, modes=((0, 0), (1, 1)))),
        ("framing", v.vorbis_setup(1, framing=0)),
    ]
    f = Fixture("vorbis-setup-invalid", ".ogg")
    for link, (_, setup) in enumerate(variants, 1):
        serial = 0x0B0B0030 + link
        packets, granules = vorbis_stream(1, 44100, [1] * 6, setup=setup)
        pages = pages_for(serial, packets, granules, vorbis_cuts(6))
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=link, codec="Vorbis")
    f.finding("vorbis.setup-invalid", "does not start with the sync pattern 0x564342 but 0x424242")
    f.finding("vorbis.codebook-overpopulated", "ask for more codewords than a prefix code of those lengths holds")
    f.finding("vorbis.codebook-underpopulated", "leave codewords unused")
    f.finding("vorbis.codebook-too-large", "has 32768 dimensions and 1024 entries")
    f.finding("vorbis.setup-invalid", "Floor 0 has the type 2")
    f.finding("vorbis.setup-invalid", "Mode 1 uses mapping 1, but the setup header has 1 mapping")
    f.finding("vorbis.framing-missing", "setup header's framing bit is 0")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "ogginfo, ffprobe and oggdec": "libvorbis refuses each of these setup headers, as the findings say; oggdec confirms it for each link"}}
    f.write()


def vorbis_setup_rich():
    # A setup header that takes the other paths: an ordered codebook, a sparse one, one with a
    # lookup of type 2, a floor of type 0, a residue of type 2 and a mapping of two submaps with
    # a channel mux. The audio packets code no floor, which decodes to silence.
    def books(w):
        w.put(3, 8)
        v.codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))                    # 0: classbook
        v.codebook(w, 2, [2, 2, 3, 3, 3, 3], ordered=True, lookup=(2, -1.0, 0.5, 3, 0, list(range(8)) + [1, 2, 3, 4]))  # 1
        v.codebook(w, 1, [1, 0, 2, 0, 2], lookup=(1, 0.0, 1.0, 3, 1, [0, 1, 2, 3, 4]))   # 2: sparse
        v.codebook(w, 4, [2] * 4, lookup=(1, -1.0, 1.0, 1, 0, [1]))                      # 3: lookup1_values(4, 4) = 1

    def floors(w):
        w.put(1, 6)
        w.put(0, 16)          # floor 0 of type 0
        w.put(16, 8)
        w.put(44100 & 0xFFFF, 16)
        w.put(256, 16)
        w.put(6, 6)
        w.put(140, 8)
        w.put(1, 4)
        w.put(1, 8)
        w.put(2, 8)
        w.put(1, 16)          # floor 1 of type 1 with one partition of a class with a subclass bit
        w.put(1, 5)
        w.put(0, 4)
        w.put(1, 3)           # class dimensions 2
        w.put(1, 2)           # one subclass bit
        w.put(0, 8)           # masterbook 0
        w.put(0, 8)           # subclass book none
        w.put(3, 8)           # subclass book 2
        w.put(1, 2)           # multiplier 2
        w.put(7, 4)           # range bits
        w.put(30, 7)
        w.put(90, 7)

    def residues(w):
        w.put(0, 6)
        w.put(2, 16)
        w.put(0, 24)
        w.put(256, 24)
        w.put(15, 24)
        w.put(1, 6)           # two classifications
        w.put(0, 8)           # classbook 0
        w.put(1, 3)
        w.put(0, 1)
        w.put(3, 3)
        w.put(1, 1)
        w.put(1, 5)           # cascade 0x0B
        w.put(1, 8)
        w.put(1, 8)
        w.put(2, 8)
        w.put(3, 8)

    def mappings(w):
        w.put(0, 6)
        w.put(0, 16)
        w.put(1, 1)
        w.put(1, 4)           # two submaps
        w.put(1, 1)
        w.put(0, 8)
        w.put(0, 1)
        w.put(1, 1)
        w.put(0, 2)
        w.put(0, 4)
        w.put(1, 4)           # channel 0 in submap 0, channel 1 in submap 1
        for floor in (0, 1):
            w.put(0, 8)
            w.put(floor, 8)
            w.put(0, 8)

    setup = v.vorbis_setup(2, books=books, floors=floors, residues=residues, mappings=mappings)
    blocks = switching(24, attacks=(8,))
    audio = []
    for i, flag in enumerate(blocks):
        w = v.BitWriter()
        w.put(0, 1)
        w.put(flag, 1)
        if flag:
            w.put(blocks[i - 1] if i else 1, 1)
            w.put(blocks[i + 1] if i + 1 < len(blocks) else 1, 1)
        w.put(0, 6)           # floor 0: amplitude 0, no curve
        w.put(0, 1)           # floor 1: not used
        audio.append(w.bytes() + bytes(3))
    packets, granules = vorbis_stream(2, 44100, blocks, setup=setup, audio=audio, end_trim=10)
    f = single("vorbis-setup-rich", packets, granules, vorbis_cuts(len(blocks)), serial=0x0B0B0040, codec="Vorbis")
    f.crosscheck["skip"]["ffprobe"] = ("ffmpeg's native Vorbis decoder supports codebooks with lookup type 1 only and refuses codebook 1's "
                                       "type 2 (\"Codebook lookup type not supported\"), which libvorbis decodes; oggdec's samples are compared")
    f.write()


# --- Opus streams ---------------------------------------------------------------------------------


def opus_stream(head, audio, pre_skip=None, tags=None, end_trim=0, start_offset=0):
    """The packets and granule positions of an Opus stream: the headers at 0, then each packet's
    samples at 48 kHz plus the start offset; end_trim less on the last."""
    packets = [head, tags if tags is not None else v.opus_tags(VENDOR, [b"TITLE=Opus fixture"])] + audio
    total, granules = start_offset, [0, 0]
    for packet in audio:
        total += v.declared_samples(packet)
        granules.append(total)
    granules[-1] -= end_trim
    return packets, granules


def opus_cuts(count, per_page=50):
    return Cuts(1, 1, *[per_page] * (count // per_page + 1))


def opus_toc_configs():
    # Every configuration with code 0, stereo and mono alternating; then codes 1 and 2; then code 3
    # in CBR and VBR, with padding, and with frames of no data; one packet whose frames span pages.
    audio = []
    for config in range(32):
        audio.append(v.opus_packet(config, config % 2, [3 + config]))
    for config in (1, 9, 13, 17, 29):
        audio.append(v.opus_packet(config, 1, [20, 20]))
        audio.append(v.opus_packet(config, 0, [10, 30]))
        audio.append(v.opus_packet(config, 0, [300, 2]))
    audio.append(v.opus_packet(16, 0, [8] * 48))                 # 48 frames of 2.5 ms: 120 ms
    audio.append(v.opus_packet(19, 1, [40] * 6, code=3, vbr=False))
    audio.append(v.opus_packet(19, 1, [40, 0, 0, 12], vbr=True))
    audio.append(v.opus_packet(23, 0, [20, 20], code=3, vbr=False, padding=10))
    audio.append(v.opus_packet(27, 1, [5, 6, 7], vbr=True, padding=600))
    audio.append(v.opus_packet(31, 1, [0]))
    audio.append(v.opus_packet(31, 1, [1000]))
    packets, granules = opus_stream(v.opus_head(1, pre_skip=312, rate=44100), audio, end_trim=500)
    f = single("opus-toc-configs", packets, granules, [1, 1, 20, 20, 3, 3, 10], serial=0x0F0F0001, extension=".opus", codec="Opus")
    f.write()


def opus_cbr():
    # Every packet the same size, code 3 CBR with padding, as a CBR encoder writes them.
    audio = [v.opus_packet(31, 1, [140], code=3, vbr=False, padding=8) for _ in range(60)]
    packets, granules = opus_stream(v.opus_head(2, pre_skip=312, rate=48000, gain=-384), audio, end_trim=200)
    f = single("opus-stereo-cbr", packets, granules, opus_cuts(len(audio)), serial=0x0F0F0002, extension=".opus", codec="Opus")
    f.write()


def multistream_packet(parts):
    """An Ogg packet of several streams' Opus packets: each but the last self-delimited."""
    out = b""
    for i, (config, stereo, frames, kwargs) in enumerate(parts):
        out += v.opus_packet(config, stereo, frames, self_delimited=i < len(parts) - 1, **kwargs)
    return out


def opus_multistream():
    # 5.1 in mapping family 1: four streams, two of them coupled, in every frame packing, each
    # stream but the last self-delimited.
    head = v.opus_head(6, family=1, streams=4, coupled=2, mapping=[0, 4, 1, 2, 3, 5])
    audio = []
    for i in range(30):
        kinds = [
            (31, 1, [60], {}),
            (30, 1, [30, 30], {}),
            (30, 1, [25, 35], {}),
            (31, 0, [10] if i % 2 else [12], {}),
        ]
        if i % 5 == 4:
            kinds = [(30, 1, [20, 20], {"code": 3, "vbr": False, "padding": 3}), (30, 1, [5, 9], {"code": 3, "vbr": True}),
                     (30, 0, [7, 7], {}), (30, 0, [4, 6], {})]
        audio.append(multistream_packet(kinds))
    packets, granules = opus_stream(head, audio, end_trim=100)
    f = single("opus-multistream-51", packets, granules, opus_cuts(len(audio), 12), serial=0x0F0F0003, extension=".opus", codec="Opus")
    f.write()


def opus_family255():
    head = v.opus_head(3, family=255, streams=3, coupled=0, mapping=[0, 1, 255])
    audio = [multistream_packet([(29, 0, [20], {}), (29, 0, [21], {}), (29, 0, [22], {})]) for _ in range(20)]
    packets, granules = opus_stream(head, audio)
    f = single("opus-family-255", packets, granules, opus_cuts(len(audio)), serial=0x0F0F0004, extension=".opus", codec="Opus")
    f.crosscheck = {"valid": True, "skip": {"opusdec": "libopusfile 0.12, which opusdec reads with, refuses channel mapping family 255 (\"Failed to open\")"}}
    f.write()


def opus_ambisonics2():
    # Second-order ambisonics with non-diegetic stereo in family 2 (RFC 8486): 11 channels from
    # ten streams, the first coupled for the stereo pair, the ACN channels from the mono streams.
    head = v.opus_head(11, family=2, streams=10, coupled=1, mapping=list(range(2, 11)) + [0, 1])
    audio = [multistream_packet([(31, 1, [30], {})] + [(31, 0, [10 + s], {}) for s in range(9)]) for _ in range(15)]
    packets, granules = opus_stream(head, audio)
    f = single("opus-ambisonics-family-2", packets, granules, opus_cuts(len(audio)), serial=0x0F0F0005, extension=".opus", codec="Opus")
    f.crosscheck = {"valid": True, "skip": {"opusdec": "libopusfile 0.12, which opusdec reads with, refuses channel mapping family 2 (\"Failed to open\")"}}
    f.write()


def opus_ambisonics3():
    # First-order ambisonics in family 3 (RFC 8486): two coupled streams, four decoded channels,
    # and a 4 by 4 demixing matrix of Q15 coefficients (identity), stored column by column.
    matrix = b"".join(struct.pack("<h", 32767 if r == c else 0) for c in range(4) for r in range(4))
    head = v.opus_head(4, family=3, streams=2, coupled=2, mapping=list(matrix))
    audio = [multistream_packet([(31, 1, [40], {}), (31, 1, [41], {})]) for _ in range(15)]
    packets, granules = opus_stream(head, audio)
    f = single("opus-ambisonics-family-3", packets, granules, opus_cuts(len(audio)), serial=0x0F0F0006, extension=".opus", codec="Opus")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "opusinfo, opusdec and ffprobe": "Ubuntu's libopus 1.4 is built without the projection decoder family 3 needs, so they refuse the stream; the packets oggz lists are compared"}}
    f.write()


def opus_head_invalid():
    # One link per identification header that breaks RFC 7845 or RFC 8486.
    heads = [
        (v.opus_head(2, version=16), [("opus.version", "gives the version 16")]),
        (v.opus_head(2, version=2), [("opus.version-minor", "gives the version 2")]),
        (v.opus_head(0, family=0), [("opus.channels-invalid", "gives 0 output channels")]),
        (v.opus_head(3, family=0), [("opus.channels-invalid", "family 0 allows one or two channels, but the header gives 3")]),
        (v.opus_head(9, family=1, streams=9, coupled=0, mapping=list(range(9))), [("opus.channels-invalid", "family 1 allows one to eight channels, but the header gives 9")]),
        (v.opus_head(2, family=1, streams=1, coupled=2, mapping=[0, 1]), [("opus.coupled-count-invalid", "gives 2 coupled streams of 1")]),
        (v.opus_head(1, family=255, streams=0, coupled=0, mapping=[255]), [("opus.stream-count-invalid", "gives 0 streams")]),
        (v.opus_head(2, family=1, streams=1, coupled=1, mapping=[0, 7]), [("opus.mapping-invalid", "channel 1, names 7")]),
        (v.opus_head(5, family=2, streams=5, coupled=0, mapping=list(range(5))), [("opus.channels-invalid", "family 2 allows (1 + n)² + 2j channels")]),
        (v.opus_head(2, family=7, streams=1, coupled=1, mapping=[0, 1]), [("opus.mapping-family-reserved", "Channel mapping family 7 is reserved")]),
        (v.opus_head(2, family=1, streams=1, coupled=1, mapping=[0]), [("opus.header-short", "fewer than the 23 its channel mapping needs")]),
        (b"OpusHead\x01\x02\x38\x01", [("opus.header-short", "holds 12 bytes, too few for its fields")]),
        (v.opus_head(1, extra=b"future"), [("opus.header-extra-data", "holds 6 bytes after its fields")]),
    ]
    f = Fixture("opus-head-invalid", ".opus")
    for link, (head, findings) in enumerate(heads, 1):
        serial = 0x0F0F0100 + link
        streams = max(1, head[19]) if len(head) >= 21 and head[18] != 0 else 1
        audio = [multistream_packet([(28, 0, [20], {})] * streams) for _ in range(5)]
        packets, granules = opus_stream(head, audio)
        pages = pages_for(serial, packets, granules, opus_cuts(len(audio)))
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=link, codec="Opus")
        for code, text in findings:
            f.finding(code, text)
    f.crosscheck = {"valid": False, "oggz_validate": ["File contains no Ogg packets"], "not_compared": {
        "opusinfo, opusdec and ffprobe": "they refuse most of these headers, as the findings say, and read a chain's later links as one stream",
        "oggz validate's other checks": "oggz 1.1.1 does not know Opus, and gives up on the file at the link whose identification header is cut short"}}
    f.write()


def opus_granules():
    # One link per granule position case of RFC 7845, section 4: a stream that starts later; a
    # first audio page smaller than its samples; end trimming before the last page; trimming more
    # than the last packet; a pre-skip larger than the stream; a last position beyond the packets;
    # header pages with granule positions; an identification header that shares its page and a
    # comment header that does not end its page.
    f = Fixture("opus-granule-edges", ".opus")
    audio = [v.opus_packet(31, 0, [30]) for _ in range(20)]

    def link(number, packets, granules, cuts, findings):
        serial = 0x0F0F0200 + number
        pages = pages_for(serial, packets, granules, cuts)
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=number, codec="Opus")
        for code, text in findings:
            f.finding(code, text)

    head = v.opus_head(1, pre_skip=312)
    packets, granules = opus_stream(head, audio, start_offset=48000, end_trim=100)
    link(1, packets, granules, opus_cuts(20, 10), [("opus.start-offset", "48000 samples beyond what the packets before it hold")])
    packets, granules = opus_stream(head, audio, start_offset=-960)
    link(2, packets, granules, opus_cuts(20, 10), [("opus.start-negative", "960 samples smaller")])
    packets, granules = opus_stream(head, audio)
    granules[2 + 9] -= 50
    link(3, packets, granules, opus_cuts(20, 5), [("opus.end-trimming-early", "before the last page")])
    packets, granules = opus_stream(head, audio, end_trim=2000)
    link(4, packets, granules, opus_cuts(20, 10), [("opus.end-trimming-large", "trims 2000 samples from the end, more than the 960")])
    packets, granules = opus_stream(v.opus_head(1, pre_skip=30000), audio)
    link(5, packets, granules, opus_cuts(20, 10), [("opus.pre-skip-beyond", "pre-skip of 30000 samples")])
    packets, granules = opus_stream(head, audio, end_trim=-500)
    link(6, packets, granules, opus_cuts(20, 10), [("opus.granule-beyond-packets", "500 samples beyond")])
    packets, granules = opus_stream(head, audio)
    granules[0], granules[1] = 5, 7
    link(7, packets, granules, opus_cuts(20, 10), [("opus.header-granule", "identification header has the granule position 5")])
    packets, granules = opus_stream(head, audio)
    link(8, packets, granules, [2, 1, 10, 10], [("opus.page-layout", "The identification header does not end its page")])
    packets, granules = opus_stream(head, audio)
    link(9, packets, granules, [1, 2, 10, 10], [("opus.page-layout", "The comment header does not end its page")])
    f.crosscheck = {"valid": False, "oggz_validate": ["Terminal header page has non-zero granulepos"], "not_compared": {
        "opusinfo, opusdec and ffprobe": "they read a chain's later links as one stream or stop at the first, and the granule position cases are what the findings report"}}
    f.write()


def opus_packets_corrupt():
    # Link 1: packets that break each of R1 to R7 of RFC 6716, section 3.4, between valid ones.
    # Link 2: multistream packets whose self-delimited framing runs past the packet, and whose
    # streams code different durations.
    f = Fixture("opus-packets-corrupt", ".opus")
    good = v.opus_packet(31, 0, [20])
    bad = [
        b"",                                                     # R1
        bytes([31 << 3]) + bytes(1300),                          # R2: a code 0 frame of 1300 bytes
        bytes([(31 << 3) | 1]) + bytes(21),                      # R3: code 1 with an odd rest
        bytes([(31 << 3) | 2, 252, 200]) + bytes(10),            # R4: code 2 whose first length exceeds the packet
        bytes([(31 << 3) | 3, 0]) + bytes(10),                   # R5: code 3 without frames
        bytes([(31 << 3) | 3, 7]) + bytes(10),                   # R5: seven frames of 20 ms
        bytes([(31 << 3) | 3, 3]) + bytes(10),                   # R6: CBR, 10 bytes in three frames
        bytes([(31 << 3) | 3, 0x40 | 2, 255, 255]),              # R6: padding length runs past the packet
        bytes([(31 << 3) | 3, 0x80 | 3, 100, 100]) + bytes(50),  # R7: VBR lengths beyond the packet
    ]
    audio = []
    for packet in bad:
        audio += [good, packet]
    audio.append(good)
    packets, granules = opus_stream(v.opus_head(1), audio)
    serial = 0x0F0F0301
    pages = pages_for(serial, packets, granules, opus_cuts(len(audio)))
    for p in pages:
        f.w.write(p)
    f.stream(serial, pages, link=1, codec="Opus")

    head = v.opus_head(3, family=1, streams=2, coupled=1, mapping=[0, 2, 1])
    good = multistream_packet([(31, 1, [30], {}), (31, 0, [20], {})])
    overrun = bytes([31 << 3 | 4, 200]) + bytes(20)
    durations = multistream_packet([(31, 1, [30], {}), (30, 0, [20], {})])
    audio = [good, good, overrun, good, durations, good]
    packets, granules = opus_stream(head, audio)
    serial = 0x0F0F0302
    pages = pages_for(serial, packets, granules, opus_cuts(len(audio)))
    for p in pages:
        f.w.write(p)
    f.stream(serial, pages, link=2, codec="Opus")
    f.finding("opus.packet-r1", "the packet is empty")
    f.finding("opus.packet-r2", "its frame of 1300 bytes is longer than the 1275 bytes")
    f.finding("opus.packet-r3", "do not split into two frames of equal size")
    f.finding("opus.packet-r4", "the first frame's length is missing or larger")
    f.finding("opus.packet-r5", "2 packets break rule R5")
    f.finding("opus.packet-r6", "2 packets break rule R6")
    f.finding("opus.packet-r7", "take more bytes than the packet holds")
    f.finding("opus.self-delimiting", "self-delimited framing (RFC 6716, appendix B) does not fit")
    f.finding("opus.stream-durations-differ", "stream 1 codes 480 samples where stream 0 codes 960")
    f.crosscheck = {"valid": False, "oggz_validate": "ok", "packets": True, "not_compared": {
        "opusinfo, opusdec and ffprobe": "they read a chain's later links as one stream, and libopus refuses the corrupt packets, as the findings say"}}
    f.write()


def opus_tags_variants():
    # Link 1: binary data after the comments, which RFC 7845 asks tools to keep; link 2: a comment
    # header of "OpusTags" alone; link 3: no comment header at all.
    f = Fixture("opus-tags-variants", ".opus")
    audio = [v.opus_packet(28, 1, [25]) for _ in range(10)]
    variants = [
        (v.opus_tags(VENDOR, [b"ARTIST=Binary"], extra=b"\x01binary data"), [], 2),
        (b"OpusTags", [("opus.tags-empty", "ends after \"OpusTags\"")], 2),
    ]
    for link, (tags, findings, _) in enumerate(variants, 1):
        serial = 0x0F0F0400 + link
        packets, granules = opus_stream(v.opus_head(2), audio, tags=tags)
        pages = pages_for(serial, packets, granules, opus_cuts(len(audio)))
        for p in pages:
            f.w.write(p)
        f.stream(serial, pages, link=link, codec="Opus")
        for code, text in findings:
            f.finding(code, text)
    serial = 0x0F0F0403
    packets = [v.opus_head(2)] + audio
    total, granules = 0, [0]
    for packet in audio:
        total += v.declared_samples(packet)
        granules.append(total)
    pages = pages_for(serial, packets, granules, [1, 10])
    for p in pages:
        f.w.write(p)
    f.stream(serial, pages, link=3, codec="Opus")
    f.finding("opus.tags-missing", "second packet is not the comment header")
    f.crosscheck = {"valid": False, "oggz_validate": ["Terminal header page contains non-header packet"], "not_compared": {
        "opusinfo, opusdec and ffprobe": "they read a chain's later links as one stream, and refuse the links without a comment, as the findings say"}}
    f.write()


FIXTURES = [
    vorbis_mono, vorbis_stereo, vorbis_surround, vorbis_start_offset, vorbis_start_trim, vorbis_granule_errors, vorbis_packet_errors,
    vorbis_ident_invalid, vorbis_header_layout, vorbis_setup_truncated, vorbis_setup_invalid, vorbis_setup_rich,
    opus_toc_configs, opus_cbr, opus_multistream, opus_family255, opus_ambisonics2, opus_ambisonics3, opus_head_invalid, opus_granules,
    opus_packets_corrupt, opus_tags_variants,
]


def main():
    hand.OUT.mkdir(parents=True, exist_ok=True)
    for build in FIXTURES:
        build()
    print(f"Wrote {len(FIXTURES)} fixtures to {hand.OUT}")


if __name__ == "__main__":
    main()
